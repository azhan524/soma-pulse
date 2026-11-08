using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Contracts;

namespace AhzScribe.Face
{
    internal static class AdaptationSmoke
    {
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
                    Error = "forced failure",
                    AuditId = "audit-fail-" + Calls
                });
            }
        }

        sealed class RetrySameModel : IToolCallingModel
        {
            public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history, CancellationToken ct)
            {
                int tools = 0;
                foreach (BrainTurnMessage message in history) if (message.Role == "tool") tools++;
                if (tools == 0 || tools == 1)
                    return Task.FromResult(new BrainTurn
                    {
                        Calls = new List<BrainToolCall>
                        {
                            new BrainToolCall
                            {
                                Id = "retry",
                                Tool = "read_file",
                                Tier = PermissionTier.ReadOnly,
                                Args = new Dictionary<string, object> { ["path"] = "same.txt" }
                            }
                        }
                    });
                return Task.FromResult(new BrainTurn { Text = "tool failure rule used", Done = true });
            }
        }

        sealed class EmojiModel : IBrainModel
        {
            public string LastSystem;

            public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
            {
                LastSystem = system ?? "";
                return Task.FromResult("结论：测试完成 😀🔥");
            }
        }

        sealed class NoopBody : IActionExecutor
        {
            public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
            {
                return Task.FromResult(new ActionResult
                {
                    RequestId = request.RequestId,
                    Ok = true,
                    Output = "noop",
                    AuditId = "audit-noop"
                });
            }
        }

        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "ahz-adaptation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DateTime now = DateTime.Today.AddHours(12);
            try
            {
                int baselineMessages;
                int baseline = RunSet(root, "a", null, now, out baselineMessages);

                var learned = new ExperienceStore(Path.Combine(root, "learned"));
                learned.RecordReminderCorrection("刚才提醒错了，以后提醒前先确认", "audit-correction", "test-device");
                int correctedMessages;
                int corrected = RunSet(root, "b", Path.Combine(root, "learned"), now, out correctedMessages);

                learned.ResetIntentEvidenceRule("test_ablation");
                int revertedMessages;
                int reverted = RunSet(root, "c", Path.Combine(root, "learned"), now, out revertedMessages);

                var paraphrased = new ExperienceStore(Path.Combine(root, "paraphrased"));
                paraphrased.RecordReminderCorrection("下次提醒前先核对一下证据", "audit-correction-2", "test-device");
                int generalizedMessages;
                int generalized = RunSet(root, "d", Path.Combine(root, "paraphrased"), now, out generalizedMessages);

                var evidenceStore = new ExperienceStore(Path.Combine(root, "evidence"));
                evidenceStore.RecordReminderCorrection("以后提醒前要先确认证据", "audit-correction-3", "test-device");
                int freshMessages;
                int freshWaits = RunEvidenceSet(root, "e", Path.Combine(root, "evidence"), now, 0, out freshMessages);
                int expiredMessages;
                int expiredWaits = RunEvidenceSet(root, "f", Path.Combine(root, "evidence"), now, 25, out expiredMessages);

                int experiences = learned.LoadExperiences().Count + paraphrased.LoadExperiences().Count + evidenceStore.LoadExperiences().Count;
                int rules = learned.LoadRules().Count + paraphrased.LoadRules().Count + evidenceStore.LoadRules().Count;
                int traces = learned.LoadTraces().Count + paraphrased.LoadTraces().Count + evidenceStore.LoadTraces().Count;
                int toolTrialPasses = RunToolFailureTrials(root);
                bool toolAblation = RunToolFailureAblation(root);

                var preferenceBaseline = new ExperienceStore(Path.Combine(root, "preference-baseline"));
                int preferenceBaselineContexts;
                int preferenceBaselineRemoved = RunPreferenceTrials(root, "pref-base",
                    preferenceBaseline, out preferenceBaselineContexts);

                string preferencePath = Path.Combine(root, "preference-learned");
                var preferenceLearned = new ExperienceStore(preferencePath);
                bool preferenceRecorded = preferenceLearned.RecordNoEmojiPreferenceCorrection(
                    "以后回复不要用 emoji", "audit-preference-1", "test-device");
                int preferenceCorrectedContexts;
                int preferenceCorrectedRemoved = RunPreferenceTrials(root, "pref-learned",
                    preferenceLearned, out preferenceCorrectedContexts);
                preferenceLearned.ResetResponsePreferenceRules();
                int preferenceAblationContexts;
                int preferenceAblationRemoved = RunPreferenceTrials(root, "pref-ablation",
                    preferenceLearned, out preferenceAblationContexts);

                var preferenceParaphrased = new ExperienceStore(Path.Combine(root, "preference-paraphrased"));
                bool preferenceParaphraseRecorded = preferenceParaphrased.RecordNoEmojiPreferenceCorrection(
                    "下次回答别再用表情符号", "audit-preference-2", "test-device");
                int preferenceParaphraseContexts;
                int preferenceParaphraseRemoved = RunPreferenceTrials(root, "pref-paraphrase",
                    preferenceParaphrased, out preferenceParaphraseContexts);

                string preferenceRestartPath = Path.Combine(root, "preference-restart");
                bool preferenceRestartRecorded = new ExperienceStore(preferenceRestartPath)
                    .RecordNoEmojiPreferenceCorrection("今后回复不要带 emoji", "audit-preference-3", "test-device");
                var preferenceRestarted = new ExperienceStore(preferenceRestartPath);
                int preferenceRestartContexts;
                int preferenceRestartRemoved = RunPreferenceTrials(root, "pref-restart",
                    preferenceRestarted, out preferenceRestartContexts);

                experiences += preferenceLearned.LoadExperiences().Count +
                    preferenceParaphrased.LoadExperiences().Count +
                    preferenceRestarted.LoadExperiences().Count;
                rules += preferenceLearned.LoadRules().Count +
                    preferenceParaphrased.LoadRules().Count +
                    preferenceRestarted.LoadRules().Count;
                traces += preferenceLearned.LoadTraces().Count +
                    preferenceParaphrased.LoadTraces().Count +
                    preferenceRestarted.LoadTraces().Count;

                bool ok = baseline == 0 && baselineMessages == 20 &&
                    corrected == 20 && correctedMessages == 0 &&
                    reverted == 0 && revertedMessages == 20 &&
                    generalized == 20 && generalizedMessages == 0 &&
                    freshMessages == 20 && freshWaits == 0 &&
                    expiredMessages == 0 && expiredWaits == 20 &&
                    experiences == 6 && rules >= 7 && traces >= 140 &&
                    toolTrialPasses == 20 && toolAblation &&
                    preferenceRecorded && preferenceParaphraseRecorded && preferenceRestartRecorded &&
                    preferenceBaselineRemoved == 0 && preferenceBaselineContexts == 0 &&
                    preferenceCorrectedRemoved == 20 && preferenceCorrectedContexts == 20 &&
                    preferenceAblationRemoved == 0 && preferenceAblationContexts == 0 &&
                    preferenceParaphraseRemoved == 20 && preferenceParaphraseContexts == 20 &&
                    preferenceRestartRemoved == 20 && preferenceRestartContexts == 20;
                Console.WriteLine("baseline_message=" + baselineMessages + "/20 wait=" + baseline);
                Console.WriteLine("corrected_message=" + correctedMessages + "/20 wait=" + corrected);
                Console.WriteLine("ablation_message=" + revertedMessages + "/20 wait=" + reverted);
                Console.WriteLine("paraphrase_message=" + generalizedMessages + "/20 wait=" + generalized);
                Console.WriteLine("fresh_evidence_message=" + freshMessages + "/20 wait=" + freshWaits);
                Console.WriteLine("expired_evidence_message=" + expiredMessages + "/20 wait=" + expiredWaits);
                Console.WriteLine("experiences=" + experiences + " rules=" + rules + " traces=" + traces);
                Console.WriteLine("tool_failure_rule=" + toolTrialPasses + "/20");
                Console.WriteLine("tool_failure_ablation=" + toolAblation);
                Console.WriteLine("preference_baseline_kept=" + (20 - preferenceBaselineRemoved) + "/20");
                Console.WriteLine("preference_corrected_removed=" + preferenceCorrectedRemoved + "/20");
                Console.WriteLine("preference_ablation_kept=" + (20 - preferenceAblationRemoved) + "/20");
                Console.WriteLine("preference_paraphrase_removed=" + preferenceParaphraseRemoved + "/20");
                Console.WriteLine("preference_restart_removed=" + preferenceRestartRemoved + "/20");
                Console.WriteLine("preference_context=" + preferenceCorrectedContexts + "/20");
                Console.WriteLine(ok ? "M2-A/B/C/D ADAPTATION: PASS" : "M2-A/B/C/D ADAPTATION: FAIL");
                return ok ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        static int RunSet(string root, string name, string experienceDir, DateTime now, out int messages)
        {
            int waits = 0;
            messages = 0;
            for (int i = 0; i < 20; i++)
            {
                string dir = Path.Combine(root, name + i.ToString("00"));
                var memory = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
                try
                {
                    memory.RememberAsync(new MemoryItem
                    {
                        Kind = "intent",
                        Key = "intent-" + i,
                        Value = "检查项目状态",
                        Summary = "intent",
                        DueAt = now.AddMinutes(-1).ToString("o")
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    ExperienceStore experience = string.IsNullOrEmpty(experienceDir) ? null : new ExperienceStore(experienceDir);
                    var decider = new IntentPulseDecider(memory, experience, delegate { return now; });
                    PulseDecision decision = decider.DecideAsync(new BodyStatus { IdleSeconds = 900 }, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (decision != null && string.Equals(decision.Action, "wait", StringComparison.OrdinalIgnoreCase)) waits++;
                    if (decision != null && string.Equals(decision.Action, "message", StringComparison.OrdinalIgnoreCase)) messages++;
                }
                finally { memory.Dispose(); }
            }
            return waits;
        }

        static int RunEvidenceSet(string root, string name, string experienceDir, DateTime now,
            int evidenceAgeHours, out int messages)
        {
            int waits = 0;
            messages = 0;
            for (int i = 0; i < 20; i++)
            {
                string dir = Path.Combine(root, name + i.ToString("00"));
                var memory = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
                try
                {
                    string key = "intent-" + i;
                    memory.RememberAsync(new MemoryItem
                    {
                        Kind = "intent",
                        Key = key,
                        Value = "核对证据后提醒",
                        Summary = "intent",
                        DueAt = now.AddMinutes(-1).ToString("o")
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    memory.RememberAsync(new MemoryItem
                    {
                        Kind = "intent_evidence",
                        Key = key,
                        Value = "verified",
                        Summary = "evidence",
                        Ts = now.AddHours(-evidenceAgeHours).ToString("o"),
                        Provenance = MemoryStore.MakeProvenance("evidence-audit", "device", "verified", false)
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    var experience = new ExperienceStore(experienceDir);
                    var decider = new IntentPulseDecider(memory, experience, delegate { return now; });
                    PulseDecision decision = decider.DecideAsync(new BodyStatus { IdleSeconds = 900 }, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (decision != null && string.Equals(decision.Action, "wait", StringComparison.OrdinalIgnoreCase)) waits++;
                    if (decision != null && string.Equals(decision.Action, "message", StringComparison.OrdinalIgnoreCase)) messages++;
                }
                finally { memory.Dispose(); }
            }
            return waits;
        }

        static int RunToolFailureTrials(string root)
        {
            int passes = 0;
            for (int i = 0; i < 20; i++)
            {
                string dir = Path.Combine(root, "tool" + i.ToString("00"));
                var experience = new ExperienceStore(Path.Combine(dir, "experience"));
                var body = new FailingBody();
                var runner = new ToolAgentRunner(new EmptyMemory(), new RetrySameModel(), body);
                runner.Experience = experience;
                string answer = runner.RunAsync("retry same tool", CancellationToken.None).GetAwaiter().GetResult();
                if (body.Calls == 1 && answer.Contains("tool failure rule used")) passes++;
            }
            return passes;
        }

        static bool RunToolFailureAblation(string root)
        {
            var experience = new ExperienceStore(Path.Combine(root, "tool-ablation", "experience"));
            var firstBody = new FailingBody();
            new ToolAgentRunner(new EmptyMemory(), new RetrySameModel(), firstBody)
            {
                Experience = experience
            }.RunAsync("first", CancellationToken.None).GetAwaiter().GetResult();

            var blockedBody = new FailingBody();
            new ToolAgentRunner(new EmptyMemory(), new RetrySameModel(), blockedBody)
            {
                Experience = experience
            }.RunAsync("blocked", CancellationToken.None).GetAwaiter().GetResult();

            experience.ResetToolRules();
            var restoredBody = new FailingBody();
            new ToolAgentRunner(new EmptyMemory(), new RetrySameModel(), restoredBody)
            {
                Experience = experience
            }.RunAsync("restored", CancellationToken.None).GetAwaiter().GetResult();
            return firstBody.Calls == 1 && blockedBody.Calls == 0 && restoredBody.Calls == 1;
        }

        static int RunPreferenceTrials(string root, string name, ExperienceStore experience, out int contexts)
        {
            int removed = 0;
            contexts = 0;
            for (int i = 0; i < 20; i++)
            {
                var model = new EmojiModel();
                using (var host = new BrainHost(new EmptyMemory(), model, new NoopBody()))
                {
                    host.Experience = experience;
                    string answer = host.AskAsync("测试偏好 " + i, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (!ResponsePreferenceAdapter.ContainsEmoji(answer)) removed++;
                    if (model.LastSystem.Contains("不要使用 emoji")) contexts++;
                }
            }
            return removed;
        }

        sealed class EmptyMemory : IMemoryStore
        {
            public Task<string> RememberAsync(MemoryItem item, CancellationToken ct) { return Task.FromResult(item.Id ?? ""); }
            public Task<List<MemoryItem>> RetrieveAsync(string query, int topK, CancellationToken ct) { return Task.FromResult(new List<MemoryItem>()); }
            public Task<List<MemoryItem>> LoadAllAsync(CancellationToken ct) { return Task.FromResult(new List<MemoryItem>()); }
        }
    }
}
