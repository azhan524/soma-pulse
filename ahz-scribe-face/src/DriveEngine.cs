using System;
using System.Collections.Generic;
using System.Linq;
using AhzScribe.Contracts;

namespace AhzScribe.Face
{
    public sealed class DriveGoal
    {
        public string Type { get; set; }
        public string Key { get; set; }
        public string Text { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; }
        public GoalSourceKind SourceKind { get; set; }
        public List<string> EvidenceIds { get; set; }

        public DriveGoal()
        {
            EvidenceIds = new List<string>();
        }
    }

    /// <summary>
    /// S4 drive engine. Pure local rules; no model, no tools.
    /// It proposes at most one goal, and only once per day from the caller.
    /// </summary>
    public static class DriveEngine
    {
        public static DriveGoal Pick(List<MemoryItem> memory)
        {
            if (memory == null || memory.Count == 0) return null;
            var goals = new List<DriveGoal>();

            MemoryItem disputed = memory.FirstOrDefault(x => string.Equals(x.Status, "disputed", StringComparison.OrdinalIgnoreCase));
            if (disputed != null)
                goals.Add(new DriveGoal
                {
                    Type = "contradiction",
                    Key = disputed.Key ?? "",
                    Text = "解决记忆矛盾：" + (disputed.Key ?? "") + " = " + (disputed.Value ?? ""),
                    Score = 0.4 * 1.0 + 0.3 * 0.8 + 0.2 * 0.8 + 0.1 * 0.7,
                    Reason = "disputed memory",
                    SourceKind = GoalSourceKind.MemoryConflict,
                    EvidenceIds = new List<string> { disputed.Id ?? "" }
                });

            var semantic = memory.Where(x => string.Equals(x.Kind, "semantic", StringComparison.OrdinalIgnoreCase)).ToList();
            var singles = semantic.GroupBy(x => x.Key ?? "").Where(g => g.Count() == 1).Take(3);
            foreach (var g in singles)
            {
                MemoryItem m = g.First();
                bool gap = (m.Value ?? "").Contains("和") || (m.Value ?? "").Contains("与");
                goals.Add(new DriveGoal
                {
                    Type = gap ? "evidence_gap" : "crystallization",
                    Key = g.Key,
                    Text = (gap ? "补全关系：" : "补全记忆：") + g.Key + " = " + (m.Value ?? ""),
                    Score = 0.4 * (gap ? 0.7 : 0.5) + 0.3 * 0.4 + 0.2 * 0.5 + 0.1 * 0.8,
                    Reason = gap ? "A/B relation hinted" : "single memory",
                    SourceKind = GoalSourceKind.EvidenceGap,
                    EvidenceIds = new List<string> { m.Id ?? "" }
                });
            }

            var dangling = memory.Where(x => string.Equals(x.Kind, "semantic", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Key ?? "").FirstOrDefault(g => g.Count() == 1);
            if (dangling != null)
                goals.Add(new DriveGoal
                {
                    Type = "dangling_edge",
                    Key = dangling.Key,
                    Text = "为记忆建立关联：" + dangling.Key,
                    Score = 0.4 * 0.4 + 0.3 * 0.3 + 0.2 * 0.5 + 0.1 * 0.9,
                    Reason = "no association",
                    SourceKind = GoalSourceKind.EvidenceGap,
                    EvidenceIds = new List<string> { dangling.First().Id ?? "" }
                });

            return goals.OrderByDescending(x => x.Score).FirstOrDefault();
        }
    }
}
