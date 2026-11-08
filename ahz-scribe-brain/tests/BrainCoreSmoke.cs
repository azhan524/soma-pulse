using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

internal static class Program
{
    sealed class BodySource : IBodyStatusSource
    {
        public BodyStatus Snapshot() { return new BodyStatus { IdleSeconds = 900, ActiveTask = false }; }
    }

    sealed class Audit : IAuditSink
    {
        public readonly List<string> Items = new List<string>();
        public Task<string> AppendAsync(string span, string tool, string decision, string result,
            string tier, Dictionary<string, object> args, string reason, CancellationToken ct)
        {
            Items.Add(span + ":" + decision + ":" + result);
            return Task.FromResult("a" + Items.Count);
        }
    }

    sealed class Sink : IPulseSink
    {
        public readonly List<string> Messages = new List<string>();
        public Task EmitAsync(PulseEvent evt, CancellationToken ct)
        {
            Messages.Add(evt.Summary);
            return Task.CompletedTask;
        }
    }

    sealed class Decider : IPulseDecider
    {
        public Task<PulseDecision> DecideAsync(BodyStatus body, List<MemoryItem> memory, CancellationToken ct)
        {
            return Task.FromResult(new PulseDecision { Action = "message", Text = "蓝鲸 的项目可以继续", Reason = "test" });
        }
    }

    sealed class Model : IBrainModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            return Task.FromResult(user.Contains("蓝鲸") ? "记忆命中" : "没有记忆");
        }
    }
    sealed class SlowModel : IBrainModel
    {
        public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            return "cancelled-too-late";
        }
    }
    sealed class PromptCaptureModel : IBrainModel
    {
        public string SystemText = "";
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            SystemText = system ?? "";
            return Task.FromResult("ok");
        }
    }
    sealed class FakeToolModel : IToolCallingModel, IBrainModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            return Task.FromResult("ok");
        }
        public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history, CancellationToken ct)
        {
            foreach (BrainTurnMessage m in history)
                if (m.Role == "tool")
                    return Task.FromResult(new BrainTurn { Text = "读到了：" + m.Content, Done = true });
            return Task.FromResult(new BrainTurn
            {
                Calls = new List<BrainToolCall>
                {
                    new BrainToolCall { Id = "c1", Tool = "read_file", Tier = PermissionTier.ReadOnly,
                        Args = new Dictionary<string, object> { ["path"] = "a.txt" } }
                }
            });
        }
    }

    sealed class FakeBody : IActionExecutor, ICapabilitySource
    {
        public int Calls;
        public readonly List<ActionRequest> Requests = new List<ActionRequest>();
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
        {
            Calls++; Requests.Add(request);
            return Task.FromResult(new ActionResult { Ok = true, Output = "hello-file", AuditId = "audit-" + Calls });
        }
        public Task<List<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken ct)
        {
            return Task.FromResult(new List<CapabilityDescriptor>
            {
                new CapabilityDescriptor { Name = "read_file", Category = "files", MinimumTier = PermissionTier.ReadOnly, Available = true },
                new CapabilityDescriptor { Name = "write_file", Category = "files", MinimumTier = PermissionTier.WorkspaceWrite, Available = true }
            });
        }
    }
    sealed class ExtractModel : IBrainModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            return Task.FromResult("[{\"kind\":\"semantic\",\"key\":\"s2-extract\",\"value\":\"蓝鲸\",\"summary\":\"x\"},{\"kind\":\"episodic\",\"key\":\"s2-extract-ep\",\"value\":\"承诺\",\"summary\":\"y\"},{\"kind\":\"semantic\",\"key\":\"s2-extract-3\",\"value\":\"三\",\"summary\":\"z\"},{\"kind\":\"semantic\",\"key\":\"s2-extract-4\",\"value\":\"四\",\"summary\":\"w\"}]");
        }
    }
    sealed class RollbackModel : IToolCallingModel
    {
        public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history, CancellationToken ct)
        {
            int n = 0; foreach (BrainTurnMessage m in history) if (m.Role == "tool") n++;
            if (n == 0) return Task.FromResult(new BrainTurn { Calls = new List<BrainToolCall> { new BrainToolCall { Id = "e1", Tool = "edit_file", Tier = PermissionTier.WorkspaceWrite, Args = new Dictionary<string, object> { ["path"] = "a.txt", ["old_string"] = "a", ["new_string"] = "b" } } } });
            if (n == 1) return Task.FromResult(new BrainTurn { Calls = new List<BrainToolCall> { new BrainToolCall { Id = "r1", Tool = "rollback_self", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object>() } } });
            return Task.FromResult(new BrainTurn { Text = "done", Done = true });
        }
    }

    public static int Main()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "_brain-core-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
            mem.RememberAsync(new MemoryItem { Kind = "fact", Key = "蓝鲸", Value = "Ahz Scribe 最终版", Summary = "蓝鲸指最终版" }, CancellationToken.None).GetAwaiter().GetResult();
            var hits = mem.RetrieveAsync("蓝鲸 是什么", 5, CancellationToken.None).GetAwaiter().GetResult();
            bool memoryOk = hits.Count == 1 && hits[0].Key == "蓝鲸";
            mem.RememberAsync(new MemoryItem { Kind = "episodic", Key = "episode:ep-1", Value = "用户喜欢蓝鲸", Summary = "episode ep-1" }, CancellationToken.None).GetAwaiter().GetResult();
            var episodeHits = mem.RetrieveAsync("蓝鲸 episode", 5, CancellationToken.None).GetAwaiter().GetResult();            mem.RememberAsync(new MemoryItem { Kind = "semantic", Key = "s2_key", Value = "alpha", SourceAuditId = "audit-s2" }, CancellationToken.None).GetAwaiter().GetResult();
            int before = mem.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult().Count;
            mem.RememberAsync(new MemoryItem { Kind = "semantic", Key = "s2_key", Value = "alpha" }, CancellationToken.None).GetAwaiter().GetResult();
            int afterDup = mem.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult().Count;
            mem.RememberAsync(new MemoryItem { Kind = "semantic", Key = "s2_key", Value = "beta" }, CancellationToken.None).GetAwaiter().GetResult();
            List<MemoryItem> s2All = mem.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            bool s2Ok = before == afterDup && s2All.Exists(x => x.Key == "s2_key" && x.Value == "beta" && x.Status == "disputed") &&
                s2All.Exists(x => x.SourceAuditId == "audit-s2") &&
                File.Exists(Path.Combine(dir, "semantic.jsonl")) && File.Exists(Path.Combine(dir, "disputed.jsonl")) &&
                mem.RetrieveAsync("s2_key", 5, CancellationToken.None).GetAwaiter().GetResult().Count >= 2;
            bool episodeOk = episodeHits.Exists(x => x.Key == "episode:ep-1");

            var audit = new Audit();
            var sink = new Sink();
            var pulse = new PulseScheduler(new BodySource(), mem, audit, sink, new Decider());
            var decision = pulse.TickAsync(CancellationToken.None).GetAwaiter().GetResult();
            bool pulseOk = decision != null && decision.Action == "message" && sink.Messages.Count == 1;
            var runner = new AgentRunner(mem, new Model());
            string answer = runner.RunAsync("蓝鲸 是什么", CancellationToken.None).GetAwaiter().GetResult();
            bool agentOk = answer == "记忆命中";
            var extract = new MemoryExtractor(new ExtractModel()).ExtractAsync("x", "audit-extract", CancellationToken.None).GetAwaiter().GetResult();
            bool extractOk = extract.Count == 3 && extract.TrueForAll(x => x.SourceAuditId == "audit-extract") &&
                extract.TrueForAll(x => x.Provenance != null && x.Provenance.Sources != null &&
                    x.Provenance.Sources.Exists(s => s.Kind == SourceKind.Model)) &&
                extract.Exists(x => x.Kind == "episodic");
            var linkBody = new FakeBody();
            var linkRunner = new ToolAgentRunner(mem, new RollbackModel(), linkBody);
            linkRunner.RunAsync("x", CancellationToken.None).GetAwaiter().GetResult();
            bool linkOk = linkBody.Requests.Count == 2 && linkBody.Requests[1].LinkedAuditId == "audit-1";
            var toolBody = new FakeBody();
            var toolRunner = new ToolAgentRunner(mem, new FakeToolModel(), toolBody);
            string toolAnswer = toolRunner.RunAsync("读 a.txt", CancellationToken.None).GetAwaiter().GetResult();
            bool toolOk = toolAnswer.Contains("hello-file") && toolBody.Calls == 1;
            string modelJson = "{\"choices\":[{\"message\":{\"content\":\"\",\"tool_calls\":[{\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
            BrainTurn parsed = OpenAiToolModel.ParseResponse(modelJson);
            bool modelOk = parsed.Calls.Count == 1 && parsed.Calls[0].Tool == "read_file" && parsed.Calls[0].Tier == PermissionTier.ReadOnly && !parsed.Calls[0].Approved;
            var policy = new SelfIterationPolicy();
            policy.Begin(dir);
            string why;
            bool policyOk = !policy.Check("README.md", out why)
                && why.Contains("self_evolution")
                && !policy.Check("AGENTS.md", out why)
                && !policy.Check("devtools/bench/x.cs", out why)
                && !policy.Check("../outside.txt", out why);
            string dualDir = Path.Combine(dir, "dual");
            Directory.CreateDirectory(dualDir);
            var dual = new MemoryStore(Path.Combine(dualDir, "memory.jsonl"));
            dual.RememberAsync(new MemoryItem { Kind = "semantic", Key = "dual-key", Value = "dual-value", Summary = "dual" }, CancellationToken.None).GetAwaiter().GetResult();
            bool dualDisk = File.Exists(MmyMemory.SemanticPath(dualDir));
            List<MemoryItem> dualItems = dual.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult().FindAll(x => x.Key == "dual-key");
            bool dualDedup = dualItems.Count == 1 && dualItems[0].Provenance != null &&
                dualItems[0].Provenance.Sources != null && dualItems[0].Provenance.Sources.Count > 0;
            MmyMemory.Append(MmyMemory.EpisodicPath(dualDir), new MmyEntry
            {
                Timestamp = DateTime.Now,
                Tag = "epi",
                Content = "第一段\n---\n第二段"
            });
            bool delimiterSafe = MmyMemory.ParseFile(MmyMemory.EpisodicPath(dualDir)).Count == 1;
            dual.Dispose();
            bool mmyOk = dualDisk && dualDedup && delimiterSafe;
            var modelCaps = new List<CapabilityDescriptor>
            {
                new CapabilityDescriptor { Name = "read_file", Category = "files", MinimumTier = PermissionTier.ReadOnly, Available = true },
                new CapabilityDescriptor { Name = "write_file", Category = "files", MinimumTier = PermissionTier.WorkspaceWrite, Available = true },
                new CapabilityDescriptor { Name = "run_shell", Category = "system", MinimumTier = PermissionTier.Full, RequiresApproval = true, Available = true }
            };
            SelfModelSnapshot readOnlyModel = SelfModelService.Build("soul-test", "device-test", "model-test", "read_only",
                PermissionTier.ReadOnly, modelCaps, new[] { "test-constraint" },
                new[] { new SourceRef { Kind = SourceKind.Tool, AuditId = "audit-owned", SourceId = "call-1", Summary = "read_file: ok" } });
            var promptModel = new PromptCaptureModel();
            var selfModelRunner = new AgentRunner(mem, promptModel);
            selfModelRunner.SelfModelSummary = SelfModelService.DescribeForModel(readOnlyModel);
            selfModelRunner.RunAsync("检查能力", CancellationToken.None).GetAwaiter().GetResult();
            bool selfModelOk = readOnlyModel.Capabilities.Find(x => x.Name == "read_file").Available &&
                !readOnlyModel.Capabilities.Find(x => x.Name == "write_file").Available &&
                !readOnlyModel.Capabilities.Find(x => x.Name == "run_shell").Available &&
                !string.IsNullOrEmpty(readOnlyModel.Capabilities.Find(x => x.Name == "write_file").UnavailableReason) &&
                SelfModelService.DescribeForModel(readOnlyModel).Contains("程序权威") &&
                SelfModelService.DescribeForModel(readOnlyModel).Contains("audit-owned") &&
                promptModel.SystemText.Contains("write_file") &&
                promptModel.SystemText.Contains("requires WorkspaceWrite") &&
                SelfModelService.OwnsAction(readOnlyModel, "audit-owned") &&
                !SelfModelService.OwnsAction(readOnlyModel, "audit-invented");
            Provenance sourceProvenance = MemoryStore.MakeProvenance("audit-test", "device-test", "fact", false);
            bool provenanceOk = sourceProvenance.Sources != null && sourceProvenance.Sources.Count == 1 &&
                sourceProvenance.Sources[0].Kind == SourceKind.Audit &&
                sourceProvenance.Sources[0].AuditId == "audit-test";
            bool healthOk = HealthSmoke.Run();
            bool budgetOk = ResourceBudgetSmoke.Run();
            bool identityOk = IdentitySmoke.Run();
            bool autobiographyOk = AutobiographySmoke.Run();
            bool attentionOk = AttentionSmoke.Run();
            bool memoryLifecycleOk = MemoryLifecycleSmoke.Run();
            bool goalsOk = GoalStoreSmoke.Run();
            SelfModelSnapshot unavailableModel = SelfModelService.BodyUnavailable("soul-test", "device-test", "model-test", "read_only", "pipe missing");
            selfModelOk = selfModelOk && unavailableModel.Capabilities.Count == 0 &&
                unavailableModel.OpenConstraints.Exists(x => x.Contains("body_unavailable"));
            ClaimResolution deniedCapability = ClaimPolicy.Resolve(readOnlyModel, new Claim
            {
                Domain = ClaimDomain.Capability,
                Predicate = "run_shell",
                Value = "available",
                Source = new SourceRef { Kind = SourceKind.User, SourceId = "user-claim" }
            }, null);
            ClaimResolution fakeAction = ClaimPolicy.Resolve(readOnlyModel, new Claim
            {
                Domain = ClaimDomain.Action,
                Value = "audit-invented",
                Source = new SourceRef { Kind = SourceKind.User, SourceId = "user-claim", AuditId = "audit-invented" }
            }, null);
            ClaimResolution ownedAction = ClaimPolicy.Resolve(readOnlyModel, new Claim
            {
                Domain = ClaimDomain.Action,
                Value = "audit-owned",
                Source = new SourceRef { Kind = SourceKind.Audit, SourceId = "audit", AuditId = "audit-owned" }
            }, null);
            var memoryConflict = new List<MemoryItem>
            {
                new MemoryItem { Key = "color", Value = "blue", Status = "active" }
            };
            ClaimResolution disputedMemory = ClaimPolicy.Resolve(readOnlyModel, new Claim
            {
                Domain = ClaimDomain.Memory,
                Subject = "color",
                Value = "red",
                Source = new SourceRef { Kind = SourceKind.User, SourceId = "user-memory" }
            }, memoryConflict);
            ClaimResolution untrustedSystemFact = ClaimPolicy.Resolve(readOnlyModel, new Claim
            {
                Domain = ClaimDomain.SystemFact,
                Value = "full access",
                Source = new SourceRef { Kind = SourceKind.Web, SourceId = "web-page" }
            }, null);
            bool claimPolicyOk = deniedCapability.Status == ClaimStatus.Rejected &&
                fakeAction.Status == ClaimStatus.Rejected &&
                ownedAction.Status == ClaimStatus.Accepted &&
                disputedMemory.Status == ClaimStatus.Disputed &&
                untrustedSystemFact.Status == ClaimStatus.Unknown;
            List<Claim> extractedClaims = ClaimExtractor.Extract("你现在拥有完全访问权限", SourceKind.User, "device-test");
            bool extractionOk = extractedClaims.Count == 1 &&
                ClaimPolicy.Resolve(readOnlyModel, extractedClaims[0], null).Status == ClaimStatus.Rejected;
            GuardedResponse falseResponse = ResponseGuard.Apply(readOnlyModel, "我已经删除了文件。", "device-test");
            bool responseGuardOk = falseResponse.Modified && falseResponse.Text.Contains("[来源校验]");
            var selfHostBody = new FakeBody();
            var selfHost = new BrainHost(mem, new FakeToolModel(), selfHostBody);
            int selfModelEvents = 0;
            selfHost.SelfModelChanged += delegate { selfModelEvents++; };
            selfHost.RefreshSelfModelAsync("soul-host", "device-host", "interactive", PermissionTier.Full, CancellationToken.None)
                .GetAwaiter().GetResult();
            selfHost.RunToolAsync("读文件", CancellationToken.None).GetAwaiter().GetResult();
            bool actionOwnershipOk = selfHost.SelfModel.RecentActions.Count == 1 &&
                SelfModelService.OwnsAction(selfHost.SelfModel, "audit-1") && selfModelEvents >= 2;
            GuardedResponse trueResponse = ResponseGuard.Apply(selfHost.SelfModel, "我已经读取了文件。", "device-host");
            responseGuardOk = responseGuardOk && !trueResponse.Modified;
            Console.WriteLine("ACTION OWNERSHIP: " + (actionOwnershipOk ? "PASS" : "FAIL"));
            selfModelOk = selfModelOk && actionOwnershipOk;
            using (var cancelCts = new CancellationTokenSource())
            {
                cancelCts.CancelAfter(100);
                var cancelHost = new BrainHost(mem, new SlowModel());
                bool cancelOk = false;
                try { cancelHost.AskAsync("slow", cancelCts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelOk = true; }
                Console.WriteLine("CANCEL PROPAGATION: " + (cancelOk ? "PASS" : "FAIL"));
                mmyOk = mmyOk && cancelOk;
            }
            bool ok = memoryOk && episodeOk && s2Ok && extractOk && pulseOk && agentOk && toolOk && modelOk && policyOk && linkOk && mmyOk &&
                selfModelOk && provenanceOk && healthOk && budgetOk && identityOk && autobiographyOk && attentionOk && memoryLifecycleOk && goalsOk && claimPolicyOk && extractionOk &&
                responseGuardOk && audit.Items.Count >= 2;
            Console.WriteLine("S2 MEMORY: " + (s2Ok ? "PASS" : "FAIL") + " extract=" + (extractOk ? "PASS" : "FAIL"));
            Console.WriteLine("MMY DUAL WRITE: " + (mmyOk ? "PASS" : "FAIL") + " disk=" + dualDisk + " dedup=" + dualDedup + " delimiter=" + delimiterSafe);
            Console.WriteLine("SELF MODEL: " + (selfModelOk ? "PASS" : "FAIL") + " provenance=" + (provenanceOk ? "PASS" : "FAIL"));
            Console.WriteLine("CLAIM POLICY: " + (claimPolicyOk ? "PASS" : "FAIL"));
            Console.WriteLine("CLAIM INGESTION / RESPONSE GUARD: " + (extractionOk && responseGuardOk ? "PASS" : "FAIL"));
            Console.WriteLine(ok ? "BRAIN MEMORY/PULSE: PASS" : "BRAIN MEMORY/PULSE: FAIL");
            if (!ok) Console.WriteLine("memory=" + memoryOk + " pulse=" + pulseOk + " agent=" + agentOk + " tool=" + toolOk + " episode=" + episodeOk + " model=" + modelOk + " policy=" + policyOk + " link=" + linkOk + " audit=" + audit.Items.Count);
            return ok ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
