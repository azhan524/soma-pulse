using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class GoalStoreSmoke
{
    public static bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ahz-goals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var store = new GoalStore(Path.Combine(root, "main"), 3, 2, 7, 2);
            GoalState userGoal = store.Propose("完成 M7 目标闸门", "用户明确要求",
                GoalSourceKind.UserIntent, new[] { Evidence("audit-goal-1",
                    SourceKind.User, now) }, 0.9, now, now.AddMinutes(-1).ToString("o"));
            bool created = userGoal.Status == GoalStatus.Candidate &&
                userGoal.Sources.Count == 1 && userGoal.EvidenceIds.Count == 1;
            userGoal = store.Promote(userGoal.GoalId, now);
            bool promoted = userGoal.Status == GoalStatus.Monitoring &&
                userGoal.LastTransition == "promoted";

            GoalState modelGoal = store.Propose("模型随口提议", "没有用户证据",
                GoalSourceKind.ModelProposal, new[] { Evidence("model-goal",
                    SourceKind.Model, now) }, 0.4, now);
            bool modelCandidate = modelGoal.Status == GoalStatus.Candidate;
            bool modelPromoteBlocked = false;
            try { store.Promote(modelGoal.GoalId, now); }
            catch (InvalidDataException) { modelPromoteBlocked = true; }

            bool activeLimit = false;
            try
            {
                store.Propose("第三个目标", "应被上限拒绝", GoalSourceKind.LocalRule,
                    new[] { Evidence("audit-goal-3", SourceKind.System, now) }, 0.5, now);
            }
            catch (InvalidDataException ex)
            {
                activeLimit = ex.Message.Contains("active_goal_limit");
            }
            bool noEvidenceBlocked = false;
            try
            {
                store.Propose("无证据目标", "bad", GoalSourceKind.UserIntent,
                    new AutobiographyEvidence[0], 0.5, now);
            }
            catch (InvalidDataException) { noEvidenceBlocked = true; }

            GoalDecision activeTask = store.Decide(now, true);
            bool activeTaskWins = activeTask.Action == "none" &&
                activeTask.Reason == "active_task";
            GoalDecision dueDecision = store.Decide(now, false);
            bool due = dueDecision.Action == "due" &&
                dueDecision.GoalId == userGoal.GoalId;
            store.RecordNoResponse(userGoal.GoalId, now.AddMinutes(1));
            GoalState paused = store.RecordNoResponse(userGoal.GoalId, now.AddMinutes(2));
            bool noResponsePause = paused.Status == GoalStatus.Paused &&
                paused.NoResponseCount == 2;
            store.Resume(userGoal.GoalId, now.AddMinutes(3));
            GoalState waiting = store.Wait(userGoal.GoalId, "等待用户提供证据",
                now.AddMinutes(4));
            GoalDecision waitDecision = store.Decide(now.AddMinutes(4), false);
            bool waitingFirstClass = waiting.Status == GoalStatus.Waiting &&
                waitDecision.Action == "wait" &&
                waitDecision.Reason == "goal_waiting";
            store.Resume(userGoal.GoalId, now.AddMinutes(5));
            GoalState cancelled = store.Cancel(userGoal.GoalId, "用户关闭目标",
                now.AddMinutes(6));
            bool userClose = cancelled.Status == GoalStatus.Cancelled &&
                cancelled.LastTransition == "用户关闭目标";
            bool noRevive = false;
            try
            {
                store.Propose("完成 M7 目标闸门", "用户明确要求",
                    GoalSourceKind.UserIntent, new[] { Evidence("audit-goal-1",
                        SourceKind.User, now) }, 0.9, now.AddMinutes(7));
            }
            catch (InvalidDataException ex)
            {
                noRevive = ex.Message.Contains("cannot be revived");
            }
            store.Cancel(modelGoal.GoalId, "取消模型候选", now.AddMinutes(7));
            GoalDecision none = store.Decide(now.AddMinutes(8), false);
            bool noneFirstClass = none.Action == "none" && none.Reason == "no_goal";

            var restarted = new GoalStore(Path.Combine(root, "main"), 3, 2, 7, 2);
            bool restartOk = restarted.LoadLatest().Count == 2 &&
                restarted.LoadLatest().Any(x => x.GoalId == userGoal.GoalId &&
                    x.Status == GoalStatus.Cancelled);

            var daily = new GoalStore(Path.Combine(root, "daily"), 1, 3, 7, 2);
            daily.Propose("每日一", "first", GoalSourceKind.UserIntent,
                new[] { Evidence("daily-1", SourceKind.User, now) }, 0.5, now);
            bool dailyLimit = false;
            try
            {
                daily.Propose("每日二", "second", GoalSourceKind.UserIntent,
                    new[] { Evidence("daily-2", SourceKind.User, now) }, 0.5, now);
            }
            catch (InvalidDataException ex)
            {
                dailyLimit = ex.Message.Contains("daily_goal_limit");
            }

            var expiryStore = new GoalStore(Path.Combine(root, "expiry"), 3, 3, 1, 2);
            GoalState expiring = expiryStore.Propose("会过期目标", "test",
                GoalSourceKind.LocalRule, new[] { Evidence("expiry", SourceKind.System, now) },
                0.5, now);
            expiryStore.Promote(expiring.GoalId, now);
            GoalDecision expiredDecision = expiryStore.Decide(now.AddDays(2), false);
            GoalState expired = expiryStore.Find(expiring.GoalId);
            bool expiryOk = expired.Status == GoalStatus.Expired &&
                expiredDecision.Action == "none";

            bool ok = created && promoted && modelCandidate && modelPromoteBlocked &&
                activeLimit && noEvidenceBlocked && activeTaskWins && due &&
                noResponsePause && waitingFirstClass && userClose && noRevive &&
                noneFirstClass && restartOk && dailyLimit && expiryOk;
            Console.WriteLine("BOUNDED GOALS: " + (ok ? "PASS" : "FAIL") +
                " source=" + (created && noEvidenceBlocked) +
                " model_guard=" + (modelCandidate && modelPromoteBlocked) +
                " limits=" + (activeLimit && dailyLimit) +
                " task_priority=" + activeTaskWins +
                " due_wait_none=" + (due && waitingFirstClass && noneFirstClass) +
                " no_response_pause=" + noResponsePause +
                " close_no_revive=" + (userClose && noRevive) +
                " expiry=" + expiryOk);
            return ok;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static AutobiographyEvidence Evidence(string id, SourceKind kind, DateTimeOffset ts)
    {
        return new AutobiographyEvidence
        {
            EvidenceId = id,
            Source = new SourceRef
            {
                SourceId = id,
                Kind = kind,
                AuditId = id,
                Ts = ts.ToString("o"),
                Summary = id
            }
        };
    }
}
