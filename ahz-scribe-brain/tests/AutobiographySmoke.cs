using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class AutobiographySmoke
{
    public static bool Run()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "ahz-autobiography-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var memories = new List<MemoryItem>
            {
                new MemoryItem
                {
                    Id = "mem_auto_1",
                    Kind = "episodic",
                    Key = "project:m5b",
                    Value = "M5-b 计划改为最终版本收尾测试",
                    Summary = "M5-b schedule",
                    Tag = "project",
                    Ts = now.AddHours(-3).ToString("o"),
                    Status = "active",
                    Provenance = MemoryStore.MakeProvenance(
                        "audit-auto-1", "device-a", "user planned M5-b", false)
                },
                new MemoryItem
                {
                    Id = "mem_auto_2",
                    Kind = "semantic",
                    Key = "project:autobiography",
                    Value = "开始整理自传式记忆",
                    Summary = "M6 autobiography",
                    Tag = "project",
                    Ts = now.AddHours(-2).ToString("o"),
                    Status = "active",
                    Provenance = MemoryStore.MakeProvenance(
                        "audit-auto-2", "device-a", "M6 started", false)
                },
                new MemoryItem
                {
                    Id = "mem_auto_3",
                    Kind = "semantic",
                    Key = "project:deadline",
                    Value = "截止日期是十一月",
                    Summary = "deadline A",
                    Tag = "project",
                    Ts = now.AddHours(-1).ToString("o"),
                    Status = "active",
                    OriginDevice = "device-a",
                    Provenance = new Provenance
                    {
                        Sources = new List<SourceRef>
                        {
                            new SourceRef
                            {
                                SourceId = "user-deadline-a",
                                Kind = SourceKind.User,
                                DeviceId = "device-a",
                                AuditId = "audit-auto-3",
                                Ts = now.AddHours(-1).ToString("o"),
                                Summary = "deadline A"
                            }
                        }
                    }
                },
                new MemoryItem
                {
                    Id = "mem_auto_4",
                    Kind = "semantic",
                    Key = "project:deadline",
                    Value = "截止日期是十二月",
                    Summary = "deadline B",
                    Tag = "project",
                    Ts = now.AddMinutes(-30).ToString("o"),
                    Status = "disputed",
                    DisputedWith = "mem_auto_3",
                    OriginDevice = "device-b",
                    Provenance = new Provenance
                    {
                        Sources = new List<SourceRef>
                        {
                            new SourceRef
                            {
                                SourceId = "tool-deadline-b",
                                Kind = SourceKind.Tool,
                                DeviceId = "device-b",
                                AuditId = "audit-auto-4",
                                Ts = now.AddMinutes(-30).ToString("o"),
                                Summary = "deadline B"
                            }
                        }
                    }
                },
                new MemoryItem
                {
                    Id = "mem_auto_model",
                    Kind = "semantic",
                    Key = "hypothesis:next",
                    Value = "模型推测下一步可能需要长期关注",
                    Summary = "model inference",
                    Tag = "hypothesis",
                    Ts = now.AddMinutes(-20).ToString("o"),
                    Status = "active",
                    Provenance = new Provenance
                    {
                        Confidence = 0.6,
                        Inferred = true,
                        DirectlyObserved = false,
                        Sources = new List<SourceRef>
                        {
                            new SourceRef
                            {
                                SourceId = "model-inference",
                                Kind = SourceKind.Model,
                                DeviceId = "device-a",
                                Ts = now.AddMinutes(-20).ToString("o"),
                                Summary = "model inference"
                            }
                        }
                    }
                }
            };
            var store = new AutobiographyStore(dir);
            AutobiographyBuildResult built = store.BuildRecent(memories,
                "整理自传式记忆，但不要改写身份", now, 7);
            HashSet<string> known = new HashSet<string>(built.Entries
                .SelectMany(x => x.Evidence).Select(x => x.EvidenceId),
                StringComparer.OrdinalIgnoreCase);
            bool everyEntryTraced = built.Entries.Count > 0 &&
                built.Entries.All(x => store.Verify(x, known).Valid);
            bool fact = built.Entries.Any(x => x.Layer == AutobiographyLayer.Fact &&
                x.Period == AutobiographyPeriod.Day &&
                x.Text.Contains("M5-b 计划") &&
                x.Evidence.All(e => e.EvidenceId.StartsWith("memory:",
                    StringComparison.OrdinalIgnoreCase)));
            bool interpretation = built.Entries.Any(x =>
                x.Layer == AutobiographyLayer.Interpretation &&
                x.Period == AutobiographyPeriod.Day);
            bool guess = built.Entries.Any(x => x.Layer == AutobiographyLayer.Guess &&
                x.Text.Contains("整理自传式记忆"));
            AutobiographyEntry conflict = built.Entries.FirstOrDefault(x =>
                x.Layer == AutobiographyLayer.Conflict && x.Text.Contains("project:deadline"));
            bool conflictOk = conflict != null &&
                conflict.Evidence.Any(x => x.EvidenceId == "memory:mem_auto_3") &&
                conflict.Evidence.Any(x => x.EvidenceId == "memory:mem_auto_4") &&
                conflict.Text.Contains("十一月") &&
                conflict.Text.Contains("十二月");
            bool week = built.Entries.Any(x => x.Period == AutobiographyPeriod.Week &&
                x.Layer == AutobiographyLayer.Fact);
            bool theme = built.Entries.Any(x => x.Period == AutobiographyPeriod.Theme &&
                x.PeriodKey == "project");
            bool noFabricatedFact = built.Entries
                .Where(x => x.Layer == AutobiographyLayer.Fact)
                .All(x => !x.Text.Contains("不存在的经历") &&
                    !x.Text.Contains("整理自传式记忆，但不要改写身份") &&
                    !x.Text.Contains("模型推测下一步"));
            bool modelInferenceInterpreted = built.Entries.Any(x =>
                x.Layer == AutobiographyLayer.Interpretation &&
                x.Text.Contains("hypothesis"));
            bool focusOnlyGuess = built.Entries
                .Where(x => x.Layer != AutobiographyLayer.Guess)
                .All(x => !x.Text.Contains("不要改写身份"));

            int beforeRestart = store.LoadAll().Count;
            var restarted = new AutobiographyStore(dir);
            bool restartOk = restarted.LoadAll().Count == beforeRestart &&
                restarted.LoadAll().All(x => restarted.Verify(x,
                    restarted.LoadAll().SelectMany(y => y.Evidence)
                        .Select(y => y.EvidenceId)).Valid);
            string context = restarted.BuildContext(8);
            bool contextOk = context.Contains("可追溯自传上下文") &&
                context.Contains("证据");
            var promptA = new ContextCaptureModel();
            var runnerA = new AgentRunner(new StaticMemory(memories), promptA)
            {
                AutobiographyContext = context
            };
            runnerA.RunAsync("我最近在关注什么？", System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            var promptB = new ContextCaptureModel();
            var runnerB = new AgentRunner(new StaticMemory(memories), promptB)
            {
                AutobiographyContext = context
            };
            runnerB.RunAsync("换模型后我最近在关注什么？", System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            bool modelSwitchContext = promptA.SystemText.Contains("可追溯自传上下文") &&
                promptB.SystemText.Contains("可追溯自传上下文") &&
                promptA.SystemText.Contains("证据") &&
                promptB.SystemText.Contains("证据");

            int builtCount = restarted.LoadAll().Count;
            restarted.BuildRecent(memories, "整理自传式记忆，但不要改写身份", now, 7);
            bool dedupOk = restarted.LoadAll().Count == builtCount;

            bool noEvidenceRejected = false;
            try
            {
                store.Add(AutobiographyPeriod.Day, "no-evidence",
                    AutobiographyLayer.Fact, "没有证据的事实", 1.0,
                    new AutobiographyEvidence[0]);
            }
            catch (InvalidDataException) { noEvidenceRejected = true; }

            AutobiographyEntry fabricated = store.Add(AutobiographyPeriod.Day, "fabricated",
                AutobiographyLayer.Fact, "虚构事实", 1.0,
                new[]
                {
                    new AutobiographyEvidence
                    {
                        EvidenceId = "memory:does-not-exist",
                        Source = new SourceRef
                        {
                            SourceId = "user-claim",
                            Kind = SourceKind.User,
                            Summary = "fabricated"
                        }
                    }
                });
            bool fabricatedRejected = !store.Verify(fabricated, known).Valid;

            AutobiographyBuildResult unsupported = store.BuildRecent(
                new[] { new MemoryItem { Value = "missing id" } }, "", now, 7);
            bool unsupportedCounted = unsupported.UnsupportedEvidence == 1;

            bool ok = everyEntryTraced && fact && interpretation && guess && conflictOk &&
                week && theme && noFabricatedFact && modelInferenceInterpreted &&
                focusOnlyGuess && restartOk &&
                contextOk && modelSwitchContext && dedupOk && noEvidenceRejected && fabricatedRejected &&
                unsupportedCounted;
            Console.WriteLine("AUTOBIOGRAPHY: " + (ok ? "PASS" : "FAIL") +
                " entries=" + built.Entries.Count +
                " facts=" + built.Entries.Count(x => x.Layer == AutobiographyLayer.Fact) +
                " interpretations=" + built.Entries.Count(x =>
                    x.Layer == AutobiographyLayer.Interpretation) +
                " conflicts=" + built.Entries.Count(x => x.Layer == AutobiographyLayer.Conflict) +
                " restart=" + restartOk +
                " evidence_guard=" + (everyEntryTraced && noEvidenceRejected && fabricatedRejected));
            return ok;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    sealed class StaticMemory : IMemoryStore
        {
            readonly List<MemoryItem> _items;

            public StaticMemory(List<MemoryItem> items)
            {
                _items = items ?? new List<MemoryItem>();
            }

            public System.Threading.Tasks.Task<string> RememberAsync(MemoryItem item,
                System.Threading.CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(item == null ? "" : item.Id);
            }

            public System.Threading.Tasks.Task<List<MemoryItem>> RetrieveAsync(string query,
                int topK, System.Threading.CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(_items.Take(topK).ToList());
            }

            public System.Threading.Tasks.Task<List<MemoryItem>> LoadAllAsync(
                System.Threading.CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(_items.ToList());
            }
        }

    sealed class ContextCaptureModel : IBrainModel
    {
        public string SystemText = "";

        public System.Threading.Tasks.Task<string> CompleteAsync(string system, string user,
            System.Threading.CancellationToken ct)
        {
            SystemText = system ?? "";
            return System.Threading.Tasks.Task.FromResult("autobiography context");
        }
    }
    }
