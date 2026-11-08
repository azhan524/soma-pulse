using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    internal static class FaultMatrixSmoke
    {
        sealed class EmptyMemory : IMemoryStore
        {
            public Task<string> RememberAsync(MemoryItem item, CancellationToken ct) { return Task.FromResult(""); }
            public Task<List<MemoryItem>> RetrieveAsync(string query, int topK, CancellationToken ct)
            {
                return Task.FromResult(new List<MemoryItem>());
            }
            public Task<List<MemoryItem>> LoadAllAsync(CancellationToken ct)
            {
                return Task.FromResult(new List<MemoryItem>());
            }
        }

        sealed class FlakyTextModel : IBrainModel
        {
            readonly Exception _error;
            int _failures;

            public FlakyTextModel(Exception error, int failures = 1)
            {
                _error = error;
                _failures = failures;
            }

            public int Calls { get; private set; }

            public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                Calls++;
                if (Calls <= _failures) throw _error;
                return Task.FromResult("recovered");
            }
        }

        sealed class LoopToolModel : IToolCallingModel
        {
            public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history,
                CancellationToken ct)
            {
                int failures = 0;
                foreach (BrainTurnMessage message in history)
                    if (message.Role == "tool" && message.Content.StartsWith("ERROR:", StringComparison.Ordinal))
                        failures++;
                return Task.FromResult(new BrainTurn
                {
                    Calls = new List<BrainToolCall>
                    {
                        new BrainToolCall
                        {
                            Id = "fault-" + failures,
                            Tool = "read_file",
                            Tier = PermissionTier.ReadOnly,
                            Args = new Dictionary<string, object> { ["path"] = "missing-" + failures + ".txt" }
                        }
                    }
                });
            }
        }

        sealed class FailingBody : IActionExecutor
        {
            public int Calls;
            public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(new ActionResult
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    Error = "forced tool failure",
                    AuditId = "fault-tool-" + Calls
                });
            }
        }

        sealed class NoopBody : IActionExecutor
        {
            public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
            {
                return Task.FromResult(new ActionResult { RequestId = request.RequestId, Ok = true, Output = "ok" });
            }
        }

        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "ahz-m3c-matrix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var results = new Dictionary<string, bool>();
            try
            {
                results["network_disconnect"] = RunModelFault(root, "network_disconnect",
                    new HttpRequestException("simulated network disconnect"));
                results["model_timeout"] = RunModelFault(root, "model_timeout",
                    new TimeoutException("simulated model timeout"));
                results["model_invalid_json"] = RunInvalidJson(root);
                results["webview_crash"] = RunWebViewFault(root);
                results["body_pipe_stop"] = RunBodyPipeFault(root);
                results["audit_broker_unavailable"] = RunAuditFault(root);
                results["mmy_corrupt_block"] = RunMmyCorrupt(root);
                results["continuous_tool_failures"] = RunToolFailureLoop(root);

                int passed = 0;
                foreach (KeyValuePair<string, bool> item in results)
                {
                    Console.WriteLine(item.Key + "=" + (item.Value ? "PASS" : "FAIL"));
                    if (item.Value) passed++;
                }
                Console.WriteLine("FAULT MATRIX: " + (passed == results.Count ? "PASS" : "FAIL") +
                    " cases=" + passed + "/" + results.Count);
                return passed == results.Count ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        static bool RunModelFault(string root, string name, Exception error)
        {
            string dir = CaseDir(root, name);
            Record(dir, name, "started", error.GetType().Name);
            var health = HealthyManager();
            var budget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
            var model = new FlakyTextModel(error);
            bool ok;
            using (var host = new BrainHost(new EmptyMemory(), model, new NoopBody())
            {
                Health = health,
                Budget = budget
            })
            {
                string answer = host.AskAsync("fault", CancellationToken.None).GetAwaiter().GetResult();
                ok = answer == "recovered" && model.Calls == 2 && budget.Snapshot().RetriesLastHour == 1 &&
                    health.Snapshot().State == HealthState.Healthy;
            }
            if (ok) Record(dir, name, "recovered", "bounded retry succeeded");
            return ok && AuditPair(dir, name);
        }

        static bool RunInvalidJson(string root)
        {
            const string name = "model_invalid_json";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "invalid JSON");
            bool parseBlocked = false;
            try { OpenAiToolModel.ParseResponse("{\"choices\":"); }
            catch (JsonException) { parseBlocked = true; }

            var health = HealthyManager();
            var budget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
            var model = new FlakyTextModel(new InvalidOperationException("invalid JSON"), 1);
            bool noRetry = false;
            using (var host = new BrainHost(new EmptyMemory(), model, new NoopBody())
            {
                Health = health,
                Budget = budget
            })
            {
                try { host.AskAsync("invalid", CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { noRetry = model.Calls == 1; }
            }
            bool degraded = health.Snapshot().State == HealthState.Degraded;
            health.ReportComponent("model", true, "");
            bool recovered = health.Snapshot().State == HealthState.Healthy;
            bool ok = parseBlocked && noRetry && degraded && recovered;
            if (ok) Record(dir, name, "recovered", "invalid response rejected without retry");
            return ok && AuditPair(dir, name);
        }

        static bool RunWebViewFault(string root)
        {
            const string name = "webview_crash";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "renderer crash");
            var health = HealthyManager();
            health.ReportComponent("webview", false, "renderer crashed");
            bool degraded = health.Snapshot().State == HealthState.Degraded &&
                health.AllowsTool("read_file", PermissionTier.ReadOnly);
            health.ReportComponent("webview", true, "");
            bool recovered = health.Snapshot().State == HealthState.Healthy;
            bool ok = degraded && recovered;
            if (ok) Record(dir, name, "recovered", "observation page restored");
            return ok && AuditPair(dir, name);
        }

        static bool RunAuditFault(string root)
        {
            const string name = "audit_broker_unavailable";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "primary audit unavailable");
            string auditPath = FaceAudit.PathFor(dir);
            bool probeBefore;
            string ignored;
            probeBefore = FaceAudit.Probe(dir, out ignored);
            string error;
            bool blocked;
            using (new FileStream(auditPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                blocked = !FaceAudit.Probe(dir, out error);
            }
            var health = HealthyManager();
            health.ReportComponent("audit", false, error);
            bool readOnly = health.Snapshot().State == HealthState.ReadOnly &&
                health.AllowsTool("read_file", PermissionTier.ReadOnly) &&
                !health.AllowsTool("write_file", PermissionTier.WorkspaceWrite);
            bool recoveredProbe = FaceAudit.Probe(dir, out ignored);
            health.ReportComponent("audit", true, "");
            bool recovered = recoveredProbe && health.Snapshot().State == HealthState.Healthy;
            bool ok = probeBefore && blocked && readOnly && recovered;
            if (ok) Record(dir, name, "recovered", "audit writable again");
            return ok && AuditPair(dir, name);
        }

        static bool RunMmyCorrupt(string root)
        {
            const string name = "mmy_corrupt_block";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "one malformed block");
            string path = MmyMemory.EpisodicPath(dir);
            MmyMemory.Append(path, new MmyEntry
            {
                Timestamp = DateTime.Now.AddMinutes(-2),
                Tag = "epi",
                Content = "first-good"
            });
            MmyMemory.Append(path, new MmyEntry
            {
                Timestamp = DateTime.Now.AddMinutes(-1),
                Tag = "epi",
                Content = "second-good"
            });
            File.AppendAllText(path, "---\nmalformed block without header\n\n", new UTF8Encoding(false));
            MmyParseResult parsed = MmyMemory.ParseDetailed(path);
            bool goodPreserved = parsed.Entries.Count == 2 &&
                parsed.Entries.Exists(x => x.Content == "first-good") &&
                parsed.Entries.Exists(x => x.Content == "second-good");
            bool badQuarantined = parsed.CorruptedBlocks.Count == 1 &&
                !parsed.Entries.Exists(x => (x.Content ?? "").Contains("malformed block"));
            List<MemoryItem> loaded;
            using (var store = new MemoryStore(Path.Combine(dir, "memory.jsonl")))
                loaded = store.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            bool loadedGood = loaded.Count == 2 &&
                loaded.Exists(x => x.Value == "first-good") &&
                loaded.Exists(x => x.Value == "second-good");
            bool quarantineFile = File.Exists(Path.Combine(MmyMemory.MemoryDir(dir), "quarantine.jsonl"));
            bool auditRecorded = false;
            try
            {
                auditRecorded = File.ReadAllText(FaceAudit.PathFor(dir), Encoding.UTF8)
                    .Contains("\"tool\":\"mmy_parse\"");
            }
            catch { }
            bool ok = goodPreserved && badQuarantined && loadedGood && quarantineFile && auditRecorded;
            if (ok) Record(dir, name, "recovered", "bad block skipped; valid blocks preserved");
            return ok && AuditPair(dir, name);
        }

        static bool RunToolFailureLoop(string root)
        {
            const string name = "continuous_tool_failures";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "repeated tool failures");
            var body = new FailingBody();
            var health = HealthyManager();
            var budget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
            var runner = new ToolAgentRunner(new EmptyMemory(), new LoopToolModel(), body, 4)
            {
                Health = health,
                Budget = budget
            };
            string answer = runner.RunAsync("keep failing", CancellationToken.None).GetAwaiter().GetResult();
            bool bounded = body.Calls == 4 && answer.Contains("工具循环超过 4 步") &&
                budget.Snapshot().ToolCallsLastHour == 4;
            if (bounded) Record(dir, name, "recovered", "tool loop stopped at step limit");
            return bounded && AuditPair(dir, name);
        }

        static bool RunBodyPipeFault(string root)
        {
            const string name = "body_pipe_stop";
            string dir = CaseDir(root, name);
            Record(dir, name, "started", "stop body process and restart");
            string repo = FindRepo();
            if (string.IsNullOrEmpty(repo)) return false;
            string bodyExe = Path.Combine(repo, "ahz-scribe-body", "src", "bin", "Release",
                "net10.0-windows", "AhzScribe.Body.exe");
            if (!File.Exists(bodyExe)) return false;

            string pipe = "ahz-m3c-body-" + Guid.NewGuid().ToString("N");
            string bodyData = Path.Combine(dir, "body-data");
            Directory.CreateDirectory(bodyData);
            Process body = null;
            Process restarted = null;
            try
            {
                body = StartBody(bodyExe, repo, pipe, bodyData);
                bool started = WaitCapabilities(pipe, 12, 250);
                var health = HealthyManager();
                bool healthy = started && health.Snapshot().State == HealthState.Healthy;

                body.Kill(true);
                body.WaitForExit(5000);
                bool stopped = !WaitCapabilities(pipe, 1, 0);
                health.ReportComponent("body", false, "body process stopped");
                bool degraded = health.Snapshot().State == HealthState.Degraded && !health.CanUseToolCalls;

                restarted = StartBody(bodyExe, repo, pipe, bodyData);
                bool recoveredProcess = WaitCapabilities(pipe, 12, 250);
                health.ReportComponent("body", true, "");
                bool recovered = recoveredProcess && health.Snapshot().State == HealthState.Healthy;
                bool ok = healthy && stopped && degraded && recovered;
                if (ok) Record(dir, name, "recovered", "body process restarted");
                return ok && AuditPair(dir, name);
            }
            finally
            {
                try { if (body != null && !body.HasExited) body.Kill(true); } catch { }
                try { if (restarted != null && !restarted.HasExited) restarted.Kill(true); } catch { }
                try { body?.Dispose(); } catch { }
                try { restarted?.Dispose(); } catch { }
            }
        }

        static Process StartBody(string exe, string repo, string pipe, string dataDir)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["AHZ_BODY_PIPE"] = pipe;
            psi.Environment["AHZ_BODY_DATA"] = dataDir;
            string dotnetRoot = Path.Combine(repo, "runtime", "dotnet");
            if (Directory.Exists(dotnetRoot)) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            Process process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("body process did not start");
            return process;
        }

        static bool WaitCapabilities(string pipe, int attempts, int delayMs)
        {
            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                try
                {
                    using (var client = new BodyClient(pipe))
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        List<CapabilityDescriptor> capabilities =
                            client.GetCapabilitiesAsync(cts.Token).GetAwaiter().GetResult();
                        if (capabilities != null && capabilities.Count > 0) return true;
                    }
                }
                catch { }
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
            return false;
        }

        static string FindRepo()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "ahz-scribe-body", "src"))) return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent == null ? "" : parent.FullName;
            }
            return "";
        }

        static HealthManager HealthyManager()
        {
            var health = new HealthManager("model", "body", "memory", "audit", "webview");
            health.ReportComponent("model", true);
            health.ReportComponent("body", true);
            health.ReportComponent("memory", true);
            health.ReportComponent("audit", true);
            health.ReportComponent("webview", true);
            return health;
        }

        static string CaseDir(string root, string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Record(string dir, string name, string phase, string detail)
        {
            FaceAudit.Append(dir, "fault_injection", name, phase, phase, detail);
        }

        static bool AuditPair(string dir, string name)
        {
            try
            {
                string path = FaceAudit.PathFor(dir);
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path, Encoding.UTF8);
                return text.Contains("\"tool\":\"" + name + "\"") &&
                    text.Contains("\"decision\":\"started\"") &&
                    text.Contains("\"decision\":\"recovered\"");
            }
            catch { return false; }
        }
    }
}
