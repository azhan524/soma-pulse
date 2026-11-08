using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class MemoryLifecycleSmoke
{
    public static bool Run()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "ahz-memory-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            string path = Path.Combine(dir, "memory.jsonl");
            string oldId;
            string recentId;
            string relationshipId;
            string inferredId;
            using (var store = new MemoryStore(path))
            {
                oldId = store.RememberAsync(Item("old-low-value",
                    "old-low-value", "一条低价值旧情节", now.AddDays(-400),
                    SourceKind.User, "audit-old"), System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                recentId = store.RememberAsync(Item("recent-task",
                    "recent-task", "近期正在执行的任务", now.AddMinutes(-10),
                    SourceKind.Tool, "audit-recent"), System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                relationshipId = store.RememberAsync(Item("relationship:user",
                    "核心关系", "与用户长期共同经历的关系", now.AddDays(-400),
                    SourceKind.User, "audit-relationship"), System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                inferredId = store.RememberAsync(new MemoryItem
                {
                    Kind = "semantic",
                    Key = "inference:topic",
                    Value = "模型推测但没有直接证据",
                    Summary = "inferred",
                    Ts = now.AddHours(-1).ToString("o"),
                    Provenance = new Provenance
                    {
                        Inferred = true,
                        DirectlyObserved = false,
                        Confidence = 0.6,
                        Sources = new List<SourceRef>
                        {
                            new SourceRef
                            {
                                SourceId = "model-only",
                                Kind = SourceKind.Model,
                                Ts = now.AddHours(-1).ToString("o"),
                                Summary = "inferred"
                            }
                        }
                    }
                }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                store.RememberAsync(Item("conflict:date", "十一月",
                    "deadline A", now.AddDays(-2), SourceKind.User, "audit-conflict-a"),
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                store.RememberAsync(Item("conflict:date", "十二月",
                    "deadline B", now.AddDays(-1), SourceKind.Tool, "audit-conflict-b"),
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();

                for (int i = 0; i < 3; i++)
                    store.RetrieveAsync("recent-task", 5,
                        System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                MemoryConsolidationResult result = store.ConsolidateAsync(now, 365,
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                List<MemoryItem> items = store.LoadAllAsync(
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                MemoryItem oldItem = items.Single(x => x.Id == oldId);
                MemoryItem recentItem = items.Single(x => x.Id == recentId);
                MemoryItem relationship = items.Single(x => x.Id == relationshipId);
                MemoryItem inferred = items.Single(x => x.Id == inferredId);
                bool archived = oldItem.Status == "archived";
                bool importantKept = relationship.Status == "active";
                bool inferredDowngraded = inferred.Status == "downgraded";
                bool conflictPreserved = result.ConflictsPreserved >= 1 &&
                    items.Where(x => x.Key == "conflict:date").Count() == 2 &&
                    items.Any(x => x.Key == "conflict:date" && x.Status == "disputed" &&
                        x.Provenance.Sources.Any(s => s.Kind == SourceKind.Tool));
                bool reinforced = result.Reinforced >= 1 &&
                    store.LoadLifecycle().Any(x => x.MemoryId == recentId &&
                        x.Action == MemoryLifecycleAction.Reinforce);

                store.ForgetAsync(recentId, "test forget", "audit-forget",
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                List<MemoryItem> afterForget = store.LoadAllAsync(
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                bool softDelete = afterForget.Any(x => x.Id == recentId &&
                    x.Status == "forgotten");
                bool hidden = store.RetrieveAsync("recent-task", 5,
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult()
                    .All(x => x.Id != recentId);
                store.RestoreAsync(recentId, "test restore", "audit-restore",
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                List<MemoryItem> afterRestore = store.LoadAllAsync(
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                bool restored = afterRestore.Any(x => x.Id == recentId &&
                    x.Status == "active") &&
                    store.RetrieveAsync("recent-task", 5,
                        System.Threading.CancellationToken.None).GetAwaiter().GetResult()
                        .Any(x => x.Id == recentId);

                string audit = File.Exists(Path.Combine(dir, "audit.jsonl"))
                    ? File.ReadAllText(Path.Combine(dir, "audit.jsonl"), Encoding.UTF8)
                    : "";
                bool auditOk = audit.Contains("memory_forget") &&
                    audit.Contains("memory_restore") &&
                    audit.Contains("memory_consolidate");
                bool noPhysicalDelete = afterRestore.Count == items.Count;
                var restarted = new MemoryStore(path);
                List<MemoryItem> afterRestart = restarted.LoadAllAsync(
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                bool lifecyclePersisted = restarted.LoadLifecycle().Count >= 4 &&
                    afterRestart.Any(x => x.Id == oldId && x.Status == "archived") &&
                    afterRestart.Any(x => x.Id == inferredId && x.Status == "downgraded");
                restarted.Dispose();

                bool ok = archived && importantKept && inferredDowngraded &&
                    conflictPreserved && reinforced && softDelete && hidden && restored &&
                    auditOk && noPhysicalDelete && lifecyclePersisted;
                Console.WriteLine("MEMORY LIFECYCLE: " + (ok ? "PASS" : "FAIL") +
                    " archived=" + archived +
                    " important_kept=" + importantKept +
                    " downgraded=" + inferredDowngraded +
                    " conflict_preserved=" + conflictPreserved +
                    " reinforced=" + reinforced +
                    " forget_restore=" + (softDelete && hidden && restored) +
                    " audit=" + auditOk +
                    " restart=" + lifecyclePersisted);
                return ok;
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static MemoryItem Item(string key, string value, string summary, DateTimeOffset ts,
        SourceKind kind, string auditId)
    {
        return new MemoryItem
        {
            Kind = "episodic",
            Key = key,
            Value = value,
            Summary = summary,
            Ts = ts.ToString("o"),
            Status = "active",
            OriginDevice = "device-test",
            SourceAuditId = auditId,
            Provenance = new Provenance
            {
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = auditId,
                        Kind = kind,
                        DeviceId = "device-test",
                        AuditId = auditId,
                        Ts = ts.ToString("o"),
                        Summary = summary
                    }
                },
                Confidence = 1.0,
                DirectlyObserved = true
            }
        };
    }
}
