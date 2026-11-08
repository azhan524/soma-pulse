using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Face;
using AhzScribe.Contracts;

internal sealed class EchoModel : IBrainModel
{
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        return Task.FromResult("[face v0] " + user);
    }
}

internal sealed class ConsoleAudit : IAuditSink
{
    public Task<string> AppendAsync(string span, string tool, string decision, string result,
        string tier, Dictionary<string, object> args, string reason, CancellationToken ct)
    {
        Console.WriteLine("[audit] " + span + " " + decision + " " + result);
        return Task.FromResult("face-" + DateTime.UtcNow.Ticks);
    }
}

internal sealed class ConsoleSink : IPulseSink
{
    public Task EmitAsync(PulseEvent evt, CancellationToken ct)
    {
        Console.WriteLine("[pulse] " + evt.Summary);
        return Task.CompletedTask;
    }
}

internal sealed class NoDecider : IPulseDecider
{
    public Task<PulseDecision> DecideAsync(BodyStatus body, List<MemoryItem> memory, CancellationToken ct)
    {
        return Task.FromResult(new PulseDecision { Action = "none", Reason = "face_v0" });
    }
}

internal sealed class FaceBody : IBodyStatusSource
{
    public BodyStatus Snapshot() { return new BodyStatus { IdleSeconds = 0, ActiveTask = false }; }
}

internal sealed class CaptureModel : IBrainModel
{
    public string User = "";
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        User = user ?? "";
        return Task.FromResult("ok");
    }
}
internal static class Program
{
    static string Env(string name, string fallback)
    {
        string v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    static void RunUi(bool forceShow)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        var f = new AhzScribe.Face.FaceForm();
        bool show = forceShow || f.ResurrectionDetected;
        if (show)
        {
            f.Opacity = 0.85;
            f.Shown += delegate
            {
                var t = new System.Windows.Forms.Timer { Interval = 15 };
                t.Tick += delegate { f.Opacity = Math.Min(1.0, f.Opacity + 0.01); if (f.Opacity >= 1.0) { t.Stop(); t.Dispose(); } };
                t.Start();
            };
            System.Windows.Forms.Application.Run(f);
        }
        else
        {
            f.ShowInTaskbar = false;
            f.WindowState = System.Windows.Forms.FormWindowState.Minimized;
            f.Show();
            f.Hide();
            System.Windows.Forms.Application.Run();
        }
    }
    static IBrainModel CreateModel(out string name)
    {
        string key = Env("DEEPSEEK_API_KEY", Env("AHZ_MODEL_KEY", ""));
        string baseUrl = Env("AHZ_MODEL_BASE", "https://api.deepseek.com");
        string model = Env("AHZ_MODEL_NAME", "deepseek-flash(1)");
        name = model;
        if (string.IsNullOrEmpty(key)) return new EchoModel();
        return new OpenAiToolModel(new ModelOptions { BaseUrl = baseUrl, Model = model, ApiKey = key });
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args != null && Array.IndexOf(args, "--memory-real") >= 0)
        {
            string key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("MEMORY REAL: SKIP (no key)"); return 0; }
            string dir = Path.Combine(AppContext.BaseDirectory, "_memory-real-" + Guid.NewGuid().ToString("N"));
            string sp = Path.Combine(dir, "sessions.jsonl");
            try
            {
                var store = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
                store.RememberAsync(new MemoryItem { Kind = "episodic", Key = "episode:test", Value = "用户喜欢蓝鲸，并且正在做 Ahz Scribe", Summary = "episode test" }, CancellationToken.None).GetAwaiter().GetResult();
                using (var realModel = new OpenAiToolModel(new ModelOptions
                {
                    BaseUrl = Environment.GetEnvironmentVariable("AHZ_MODEL_BASE") ?? "https://api.deepseek.com",
                    Model = Environment.GetEnvironmentVariable("AHZ_MODEL_NAME") ?? "deepseek-v4-pro",
                    ApiKey = key
                }))
                {
                    var runner = new AgentRunner(store, realModel);
                    string answer = runner.RunAsync("根据记忆，我喜欢什么？", CancellationToken.None).GetAwaiter().GetResult();
                    bool ok = answer != null && answer.Contains("蓝鲸");
                    Console.WriteLine(ok ? "MEMORY REAL: PASS" : "MEMORY REAL: FAIL " + answer);
                    return ok ? 0 : 1;
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        if (args != null && Array.IndexOf(args, "--proactive-test") >= 0)
        {
            DateTime testNow = DateTime.Today.AddHours(12);
            var st = new FaceState { ProactiveCount = 2, LastProactiveDate = testNow.ToString("yyyy-MM-dd") };
            bool cap = !ProactiveGate.Allow(st, testNow);
            var paused = new FaceState { ProactivePausedUntil = testNow.AddHours(1).ToString("o") };
            bool pause = !ProactiveGate.Allow(paused, testNow);
            var normal = new FaceState { ProactiveCount = 0, LastProactiveDate = testNow.ToString("yyyy-MM-dd") };
            bool ok = cap && pause && ProactiveGate.Allow(normal, testNow);
            var recovered = new FaceState { ProactiveCount = 0, UnansweredProactive = 0, LastProactiveDate = testNow.ToString("yyyy-MM-dd"), ProactivePausedUntil = testNow.AddMinutes(-1).ToString("o") };
            bool recovery = ProactiveGate.Allow(recovered, testNow);
            ok = ok && recovery;
            Console.WriteLine("cap=" + cap + " paused=" + pause + " recovery=" + recovery);
            Console.WriteLine(ok ? "PROACTIVE GATE: PASS" : "PROACTIVE GATE: FAIL");
            return ok ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--body-test") >= 0)
        {
            var sensor = new BodySensor(Environment.CurrentDirectory);
            BodyStatus s = sensor.Snapshot();
            bool ok = s != null && s.MemoryMb > 0 && s.DiskFreeGb >= 0 && s.AgentProcessAlive;
            Console.WriteLine("idle=" + s.IdleSeconds + " mem=" + s.MemoryMb + " disk=" + s.DiskFreeGb + " net=" + s.NetworkOnline);
            Console.WriteLine(ok ? "S3 BODY: PASS" : "S3 BODY: FAIL");
            return ok ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--policy-test") >= 0)
        {
            var mem = new List<MemoryItem> { new MemoryItem { Kind = "semantic", Key = "pref", Value = "用户喜欢蓝鲸" } };
            bool yes = ProactivePolicy.ReferencesMemory("提醒：蓝鲸这件事", mem);
            bool no = ProactivePolicy.ReferencesMemory("你好呀", mem);
            Console.WriteLine("policy yes=" + yes + " no=" + no);
            Console.WriteLine(yes && !no ? "PROACTIVE POLICY: PASS" : "PROACTIVE POLICY: FAIL");
            return yes && !no ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--drive-test") >= 0)
        {
            var mem = new List<MemoryItem>
            {
                new MemoryItem { Kind = "semantic", Key = "a", Value = "x", Status = "disputed" },
                new MemoryItem { Kind = "semantic", Key = "b", Value = "y", Status = "active" }
            };
            DriveGoal g = DriveEngine.Pick(mem);
            bool ok = g != null && g.Type == "contradiction" && g.Score > 0;
            Console.WriteLine("drive=" + (g == null ? "null" : g.Type + " " + g.Score.ToString("F2")));
            Console.WriteLine(ok ? "DRIVE ENGINE: PASS" : "DRIVE ENGINE: FAIL");
            return ok ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--pulse-test") >= 0)
        {
            var q = new PulseQueue();
            q.Enqueue(new PulseTrigger { Type = "timer" });
            q.Enqueue(new PulseTrigger { Type = "drive" });
            q.Enqueue(new PulseTrigger { Type = "intent_due" });
            q.Enqueue(new PulseTrigger { Type = "body_event" });
            q.Enqueue(new PulseTrigger { Type = "user_message" });
            string order = "";
            PulseTrigger t;
            while ((t = q.Dequeue()) != null) order += t.Type + ",";
            bool orderOk = order == "user_message,intent_due,body_event,drive,timer,";
            var body = new FaceBody();
            var mem = new MemoryStore(Path.Combine(Path.GetTempPath(), "ahz-pulse-" + Guid.NewGuid().ToString("N"), "memory.jsonl"));
            var sched = new PulseScheduler(body, mem, new ConsoleAudit(), new ConsoleSink(), new NoDecider());
            var arb = new PulseArbiter(sched, new PulseQueue(), delegate { return new FaceState { ActiveTask = "running" }; });
            var d = arb.EnqueueAsync(new PulseTrigger { Type = "timer" }, CancellationToken.None).GetAwaiter().GetResult();
            bool deferOk = d != null && d.Reason == "active_task_deferred";
            Console.WriteLine("queue_order=" + order + " deferred=" + deferOk);
            Console.WriteLine(orderOk && deferOk ? "PULSE QUEUE: PASS" : "PULSE QUEUE: FAIL");
            sched.Dispose(); mem.Dispose();
            return orderOk && deferOk ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--state-test") >= 0)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "_state-test-" + Guid.NewGuid().ToString("N"));
            string sp = Path.Combine(dir, "sessions.jsonl");
            try
            {
                FaceSession.Append(sp, new FaceMessage { Role = "你", Text = "状态测试", Kind = "chat" });
                FaceSession.SetFocus(sp, "状态测试");
                FaceSession.BeginTask(sp, "running");
                OpenIntent it = FaceSession.AddIntent(sp, "提醒测试", "");
                FaceSession.SetPulseTrigger(sp, "timer", "state-test");
                FaceState a = FaceSession.LoadState(sp);
                bool persisted = a != null && a.CurrentFocus == "状态测试" && a.ActiveTask == "running" &&
                    a.OpenIntents != null && a.OpenIntents.Count > 0 && a.LastPulseTrigger != null &&
                    File.Exists(Path.Combine(dir, "current_state.json"));
                FaceSession.EndTask(sp);
                FaceState b = FaceSession.LoadState(sp);
                bool cleared = b != null && string.IsNullOrEmpty(b.ActiveTask);
                Console.WriteLine("state_file=" + File.Exists(Path.Combine(dir, "current_state.json")) +
                    " focus=" + (a == null ? "null" : a.CurrentFocus) +
                    " active=" + (a == null ? "null" : a.ActiveTask) +
                    " intents=" + (a == null || a.OpenIntents == null ? -1 : a.OpenIntents.Count));
                Console.WriteLine(persisted && cleared ? "S0 STATE: PASS" : "S0 STATE: FAIL");
                return persisted && cleared ? 0 : 1;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        if (args != null && Array.IndexOf(args, "--session-test") >= 0)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "_session-test-" + Guid.NewGuid().ToString("N"));
            string sp = Path.Combine(dir, "sessions.jsonl");
            try
            {
                for (int i = 0; i < 41; i++)
                    FaceSession.Append(sp, new FaceMessage { Role = "你", Text = "m" + i, Kind = "chat" });
                List<FaceMessage> msgs = FaceSession.Load(sp, 100);
                FaceState st = FaceSession.LoadState(sp);
                int eps = 0; string last = "";
                foreach (FaceMessage m in msgs) if (m.Episode != last) { eps++; last = m.Episode; }
                var mstore = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
                mstore.RememberAsync(new MemoryItem { Kind = "episodic", Key = "episode:" + msgs[0].Episode, Value = "用户问过 m0", Summary = "episode test" }, CancellationToken.None).GetAwaiter().GetResult();
                var capture = new CaptureModel();
                var runner = new AgentRunner(mstore, capture);
                runner.RunAsync("m0", CancellationToken.None).GetAwaiter().GetResult();
                bool memOk = capture.User.Contains("用户问过 m0");
                bool ok = msgs.Count == 41 && eps == 2 && st != null && st.MessageCount == 1 && memOk;
                Console.WriteLine("msgs=" + msgs.Count + " eps=" + eps + " state=" + (st == null ? "null" : st.MessageCount.ToString()) + " memory=" + memOk);
                Console.WriteLine(ok ? "FACE SESSION EPISODE: PASS" : "FACE SESSION EPISODE: FAIL");
                return ok ? 0 : 1;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        if (args != null && Array.IndexOf(args, "--soul-test") >= 0)
        {
            Console.WriteLine(AhzScribe.Private.SoulBackup.SelfTest());
            return 0;
        }
        if (args != null && Array.IndexOf(args, "--adaptation-test") >= 0)
        {
            return AhzScribe.Face.AdaptationSmoke.Run();
        }
        if (args != null && Array.IndexOf(args, "--fault-matrix-test") >= 0)
        {
            return AhzScribe.Face.FaultMatrixSmoke.Run();
        }
        if (args != null && Array.IndexOf(args, "--model-switch-test") >= 0)
        {
            return AhzScribe.Face.ModelSwitchSmoke.Run();
        }
        if (args != null && Array.IndexOf(args, "--soul-migration-test") >= 0)
        {
            return AhzScribe.Face.SoulMigrationSmoke.Run();
        }
        if (args != null && Array.IndexOf(args, "--migration-diff-test") >= 0)
        {
            return AhzScribe.Face.MigrationDiffSmoke.Run();
        }
        if (args != null && Array.IndexOf(args, "--preference-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m2d-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.PreferenceCorrectionUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("preference_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--health-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m3a-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m3a-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.HealthUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("health_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--health-fault-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m3a-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m3a-fault-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            string auditPath = AhzScribe.Face.FaceAudit.PathFor(dir);
            File.WriteAllText(auditPath, "");
            File.SetAttributes(auditPath, FileAttributes.ReadOnly);
            string auditError;
            bool auditBlocked = !AhzScribe.Face.FaceAudit.Probe(dir, out auditError);
            File.SetAttributes(auditPath, FileAttributes.Normal);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.HealthFaultUiTest().GetAwaiter().GetResult();
                Console.WriteLine(result);
                Console.WriteLine("health_audit_probe=" + (auditBlocked ? "PASS" : "FAIL") +
                    " error=" + auditError);
                bool ok = result.IndexOf("health_fault=PASS", StringComparison.Ordinal) >= 0 && auditBlocked;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--budget-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m3b-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m3b-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.BudgetUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("budget_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--identity-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m4a-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m4a-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "identity-test-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.IdentityUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("identity_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--model-switch-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m4b-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m4b-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            AhzScribe.Face.FaceForm first = null;
            AhzScribe.Face.FaceForm second = null;
            try
            {
                Environment.SetEnvironmentVariable("AHZ_MODEL_NAME", "model-a");
                Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "model-switch-a-" + Guid.NewGuid().ToString("N"));
                first = new AhzScribe.Face.FaceForm();
                string soulPath = Path.Combine(dir, "soul_id");
                string soul = File.Exists(soulPath) ? File.ReadAllText(soulPath, Encoding.UTF8).Trim() : "";
                first.Dispose();
                first = null;

                Environment.SetEnvironmentVariable("AHZ_MODEL_NAME", "model-b");
                Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "model-switch-b-" + Guid.NewGuid().ToString("N"));
                second = new AhzScribe.Face.FaceForm();
                string manifestPath = Path.Combine(dir, "identity-manifest.json");
                IdentityManifest manifest = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = System.Text.Json.JsonSerializer.Deserialize<IdentityManifest>(
                            File.ReadAllText(manifestPath, Encoding.UTF8));
                    }
                    catch { }
                }
                string auditPath = Path.Combine(dir, "audit.jsonl");
                string audit = File.Exists(auditPath) ? File.ReadAllText(auditPath, Encoding.UTF8) : "";
                bool digest = File.Exists(Path.Combine(dir, "identity-invariants.sha256"));
                bool auditTool = audit.Contains("\"tool\":\"model_switch\"");
                bool auditTransition = audit.Contains("model-a -> model-b") ||
                    audit.Contains("model-a -\\u003E model-b");
                bool ok = manifest != null && manifest.SoulId == soul &&
                    manifest.ActiveModel == "model-b" && digest &&
                    auditTool && auditTransition;
                Console.WriteLine("model_switch_ui=" + (ok ? "PASS" : "FAIL") +
                    " soul=" + (manifest != null && manifest.SoulId == soul) +
                    " model=" + (manifest == null ? "missing" : manifest.ActiveModel) +
                    " digest=" + digest +
                    " audit=" + auditTool +
                    " transition=" + auditTransition);
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                try { if (first != null) first.Dispose(); } catch { }
                try { if (second != null) second.Dispose(); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--migration-diff-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m4d-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m4d-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "migration-diff-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.MigrationDiffUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("migration_diff_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--autobiography-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m6a-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m6a-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "m6a-autobiography-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.AutobiographyUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("autobiography_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--attention-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m6b-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m6b-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "m6b-attention-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.AttentionUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("attention_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--memory-lifecycle-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m6c-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m6c-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "m6c-lifecycle-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.MemoryLifecycleUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("memory_lifecycle_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--goal-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m7a-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m7a-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "m7a-goal-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.GoalUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("goal_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--emergency-stop-ui-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-m5-stop-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AHZ_FACE_DATA", dir);
            Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", "ahz-m5-stop-missing-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_INSTANCE_ID", "m5-stop-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("AHZ_STOP_FILE", Path.Combine(dir, "EMERGENCY_STOP"));
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "");
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            try
            {
                string result = f.EmergencyStopUiTest();
                Console.WriteLine(result);
                bool ok = result.IndexOf("emergency_stop_ui=PASS", StringComparison.Ordinal) >= 0;
                System.Windows.Forms.Application.Exit();
                return ok ? 0 : 1;
            }
            finally
            {
                f.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        if (args != null && Array.IndexOf(args, "--schema-test") >= 0)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ahz-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SchemaVersion\":2}");
            string err = AhzScribe.Face.FaceSettings.ValidateSchema(dir);
            Console.WriteLine("schema_error=" + err);
            try { Directory.Delete(dir, true); } catch { }
            return err.StartsWith("settings_schema_mismatch") ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--sensor-stale-test") >= 0)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            string result = f.SensorStaleTest();
            Console.WriteLine(result);
            bool ok = result.Contains("True");
            System.Windows.Forms.Application.Exit();
            return ok ? 0 : 1;
        }
        if (args != null && Array.IndexOf(args, "--ui-pulse") >= 0)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var f = new AhzScribe.Face.FaceForm();
            f.Show();
            f.ShowPulse();
            System.Windows.Forms.Application.Run(f);
            return 0;
        }
        if (args != null && Array.IndexOf(args, "--audit-fallback-test") >= 0)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "_face-data");
            Directory.CreateDirectory(dir);
            string audit = Path.Combine(dir, "audit.jsonl");
            string backup = null;
            try
            {
                if (File.Exists(audit)) backup = File.ReadAllText(audit);
                File.WriteAllText(audit, backup ?? "");
                File.SetAttributes(audit, FileAttributes.ReadOnly);
                string id = FaceAudit.Append(dir, "test", "fallback", "forced", "audit read-only", "audit-fallback-test");
                string fb = FaceAudit.FallbackPath();
                bool fbExists = File.Exists(fb);
                Console.WriteLine("audit_id=" + id);
                Console.WriteLine("error=" + FaceAudit.LastError);
                Console.WriteLine("fallback_path=" + fb);
                Console.WriteLine("fallback_exists=" + fbExists);
                if (fbExists) Console.WriteLine("fallback_last=" + File.ReadLines(fb).Last());
                return fbExists && !string.IsNullOrEmpty(id) ? 0 : 1;
            }
            finally
            {
                try { File.SetAttributes(audit, FileAttributes.Normal); } catch { }
                try { if (backup != null) File.WriteAllText(audit, backup); } catch { }
            }
        }
        bool uiMode = args == null || args.Length == 0 || Array.IndexOf(args, "--ui") >= 0 || Array.IndexOf(args, "--silent") >= 0;
        if (uiMode && !AhzScribe.Face.SingleInstance.TryAcquire())
        {
            if (!AhzScribe.Face.ActivationServer.Activate())
                AhzScribe.Face.SingleInstance.ActivateExisting();
            return 0;
        }
        if (args != null && (Array.IndexOf(args, "--ui") >= 0 || Array.IndexOf(args, "--silent") >= 0))
        {
            RunUi(Array.IndexOf(args, "--ui") >= 0);
            return 0;
        }
        if (args == null || args.Length == 0)
        {
            RunUi(false);
            return 0;
        }
        if (args != null && Array.IndexOf(args, "--console") >= 0)
        {
            // console fallback below
        }

        string root = Path.Combine(AppContext.BaseDirectory, "_face-data");
        Directory.CreateDirectory(root);
        var memory = new MemoryStore(Path.Combine(root, "memory.jsonl"));
        IBrainModel model = CreateModel(out string modelName);
        var host = new BrainHost(memory, model);
        host.Trace = delegate(string line) { Console.WriteLine("[trace] " + line); };
        var pulse = new PulseScheduler(new FaceBody(), memory, new ConsoleAudit(), new ConsoleSink(), new NoDecider());

        string brainPipe = Env("AHZ_BRAIN_PIPE", "");
        BrainPipeClient pipeClient = string.IsNullOrEmpty(brainPipe) ? null : new BrainPipeClient(brainPipe);
        Console.WriteLine("Ahz Scribe face v0 · model=" + modelName + (pipeClient == null ? "" : " · brain=" + brainPipe));
        Console.WriteLine("命令：/remember <kind> <key> <value> | /memory <query> | /ask <text> | /tool <text> | /pulse | /quit");
        while (true)
        {
            Console.Write("> ");
            string line = Console.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            try
            {
                if (line == "/quit" || line == "/exit") break;
                if (line.StartsWith("/remember ", StringComparison.Ordinal))
                {
                    string[] p = line.Split(new[] { ' ' }, 4);
                    if (p.Length < 4) { Console.WriteLine("用法：/remember <kind> <key> <value>"); continue; }
                    string id = memory.RememberAsync(new MemoryItem { Kind = p[1], Key = p[2], Value = p[3], Summary = p[2] + "=" + p[3] }, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("已记住 " + id);
                    continue;
                }
                if (line.StartsWith("/memory ", StringComparison.Ordinal))
                {
                    var hits = memory.RetrieveAsync(line.Substring(8), 8, CancellationToken.None).GetAwaiter().GetResult();
                    foreach (MemoryItem m in hits) Console.WriteLine("- " + m.Kind + " / " + m.Key + " = " + m.Value);
                    continue;
                }
                if (line.StartsWith("/ask ", StringComparison.Ordinal))
                {
                    Console.WriteLine((pipeClient == null ? host.AskAsync(line.Substring(5), CancellationToken.None) : pipeClient.AskAsync(line.Substring(5), CancellationToken.None)).GetAwaiter().GetResult());
                    continue;
                }
                if (line.StartsWith("/tool ", StringComparison.Ordinal))
                {
                    if (pipeClient == null && !(model is IToolCallingModel)) { Console.WriteLine("当前模型不支持工具调用。"); continue; }
                    host.Approval = delegate(BrainToolCall call, CancellationToken ct)
                    {
                        Console.Write("批准 " + call.Tool + " ? [y/N] ");
                        string a = Console.ReadLine();
                        return Task.FromResult(a != null && a.Trim().Equals("y", StringComparison.OrdinalIgnoreCase));
                    };
                    Console.WriteLine((pipeClient == null ? host.RunToolAsync(line.Substring(6), CancellationToken.None) : pipeClient.RunToolAsync(line.Substring(6), host.Approval, CancellationToken.None)).GetAwaiter().GetResult());
                    continue;
                }
                if (line == "/pulse")
                {
                    pulse.TickAsync(CancellationToken.None).GetAwaiter().GetResult();
                    continue;
                }
                Console.WriteLine((pipeClient == null ? host.AskAsync(line, CancellationToken.None) : pipeClient.AskAsync(line, CancellationToken.None)).GetAwaiter().GetResult());
            }
            catch (Exception ex) { Console.WriteLine("错误：" + ex.Message); }
        }
        pulse.Dispose();
        return 0;
    }
}
