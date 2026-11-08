using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class AttentionSmoke
{
    public static bool Run()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "ahz-attention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var memories = new List<MemoryItem>
            {
                Memory("m-p1", "project", "project:m5", "M5-b 最终收尾",
                    SourceKind.User, "audit-p1", now.AddHours(-4)),
                Memory("m-p2", "project", "project:m6", "自传式记忆",
                    SourceKind.Tool, "audit-p2", now.AddHours(-3)),
                Memory("m-a1", "archive", "archive:test", "整理发布证据",
                    SourceKind.User, "audit-a1", now.AddHours(-2)),
                Memory("m-a2", "archive", "archive:build", "构建发布候选",
                    SourceKind.Tool, "audit-a2", now.AddHours(-1)),
                Memory("m-a3", "archive", "archive:check", "复核发布闸门",
                    SourceKind.Audit, "audit-a3", now.AddMinutes(-30)),
                Memory("m-model", "hypothesis", "hypothesis:next", "模型猜测",
                    SourceKind.Model, "audit-model", now.AddMinutes(-20))
            };
            var store = new AttentionStore(dir);
            List<AttentionState> active = store.RefreshFromMemories(memories, now);
            AttentionState project = active.FirstOrDefault(x => x.Subject == "project");
            AttentionState archive = active.FirstOrDefault(x => x.Subject == "archive");
            bool formed = project != null && project.Status == AttentionStatus.Monitoring &&
                project.EvidenceIds.Count == 2 && project.Sources.Count == 2 &&
                !string.IsNullOrEmpty(project.FormationReason) &&
                !string.IsNullOrEmpty(project.LatestEvidenceAt) &&
                project.ContinueConditions.Count > 0 && project.EndConditions.Count > 0 &&
                !string.IsNullOrEmpty(project.ExpiresAt);
            bool modelOnlySkipped = active.All(x => x.Subject != "hypothesis");
            AttentionState dominant = store.Dominant(now);
            bool dominantArchive = dominant != null && dominant.Subject == "archive";

            var hits = new List<MemoryItem>
            {
                Memory("m-u", "other", "other:none", "普通状态", SourceKind.User,
                    "audit-u", now),
                Memory("m-x", "project", "project:m5", "普通项目状态", SourceKind.User,
                    "audit-x", now),
                Memory("m-y", "archive", "archive:test", "发布状态", SourceKind.User,
                    "audit-y", now)
            };
            List<MemoryItem> reranked = store.InfluenceRetrieval("状态", hits, 3);
            bool retrievalInfluenced = reranked.Count == 3 &&
                (reranked[0].Key == "archive:test" || reranked[0].Key == "project:m5");

            AttentionState paused = store.Pause("archive", "user pause", now);
            AttentionState afterPause = store.Dominant(now);
            bool pauseOk = paused.Status == AttentionStatus.Paused &&
                afterPause != null && afterPause.Subject == "project";
            AttentionState resumed = store.Resume("archive", "user resume", now);
            AttentionState afterResume = store.Dominant(now);
            bool resumeOk = resumed.Status == AttentionStatus.Monitoring &&
                afterResume != null && afterResume.Subject == "archive";
            AttentionState ended = store.End("archive", "user ended", now);
            store.RefreshFromMemories(memories, now);
            AttentionState endedAfterRefresh = store.LoadLatest()
                .First(x => x.Subject == "archive");
            bool endOk = ended.Status == AttentionStatus.Ended &&
                endedAfterRefresh.Status == AttentionStatus.Ended;

            var restarted = new AttentionStore(dir);
            bool restartOk = restarted.LoadLatest().Count == 2 &&
                restarted.LoadLatest().Any(x => x.Subject == "project" &&
                    x.EvidenceIds.Contains("memory:m-p1"));

            string context = restarted.BuildContext();
            var modelA = new AttentionCaptureModel();
            var runnerA = new AgentRunner(new StaticAttentionMemory(memories), modelA)
            {
                Attention = restarted
            };
            runnerA.RunAsync("我最近在关注什么", System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            var modelB = new AttentionCaptureModel();
            var runnerB = new AgentRunner(new StaticAttentionMemory(memories), modelB)
            {
                Attention = restarted
            };
            runnerB.RunAsync("换模型后我最近在关注什么", System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            bool contextOk = context.Contains("长期关注上下文") &&
                context.Contains("当前主导关注") &&
                modelA.SystemText.Contains("长期关注上下文") &&
                modelB.SystemText.Contains("长期关注上下文");

            bool modelOnlyRejected = false;
            try
            {
                restarted.FormOrRefresh("model-only", "bad", new[]
                {
                    new AutobiographyEvidence
                    {
                        EvidenceId = "memory:m-model",
                        Source = new SourceRef
                        {
                            SourceId = "model",
                            Kind = SourceKind.Model,
                            Summary = "guess"
                        }
                    }
                }, now);
            }
            catch (InvalidDataException) { modelOnlyRejected = true; }

            bool noEvidenceRejected = false;
            try
            {
                restarted.FormOrRefresh("empty", "bad", new AutobiographyEvidence[0], now);
            }
            catch (InvalidDataException) { noEvidenceRejected = true; }

            List<AttentionState> afterExpiry = restarted.Expire(now.AddDays(20));
            bool expired = !afterExpiry.Any(x => x.Subject == "project");
            bool endedStill = restarted.LoadLatest()
                .Any(x => x.Subject == "archive" && x.Status == AttentionStatus.Ended);

            bool ok = formed && modelOnlySkipped && dominantArchive &&
                retrievalInfluenced && pauseOk && resumeOk && endOk && restartOk &&
                contextOk && modelOnlyRejected && noEvidenceRejected && expired &&
                endedStill;
            Console.WriteLine("ATTENTION: " + (ok ? "PASS" : "FAIL") +
                " formed=" + formed +
                " dominant_transfer=" + dominantArchive +
                " retrieval=" + retrievalInfluenced +
                " pause_resume_end=" + (pauseOk && resumeOk && endOk) +
                " restart=" + restartOk +
                " model_guard=" + (modelOnlyRejected && noEvidenceRejected) +
                " expiry=" + expired);
            return ok;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static MemoryItem Memory(string id, string tag, string key, string value,
        SourceKind kind, string auditId, DateTimeOffset ts)
    {
        return new MemoryItem
        {
            Id = id,
            Kind = "episodic",
            Tag = tag,
            Key = key,
            Value = value,
            Summary = value,
            Status = "active",
            Ts = ts.ToString("o"),
            OriginDevice = "device-test",
            Provenance = new Provenance
            {
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = "source-" + id,
                        Kind = kind,
                        DeviceId = "device-test",
                        AuditId = auditId,
                        Ts = ts.ToString("o"),
                        Summary = value
                    }
                }
            }
        };
    }

    sealed class StaticAttentionMemory : IMemoryStore
    {
        readonly List<MemoryItem> _items;
        public StaticAttentionMemory(List<MemoryItem> items) { _items = items; }

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

    sealed class AttentionCaptureModel : IBrainModel
    {
        public string SystemText = "";

        public System.Threading.Tasks.Task<string> CompleteAsync(string system, string user,
            System.Threading.CancellationToken ct)
        {
            SystemText = system ?? "";
            return System.Threading.Tasks.Task.FromResult("attention context");
        }
    }
}
