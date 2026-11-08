using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using AhzScribe.Brain;
using AhzScribe.Contracts;

namespace AhzScribe.Face
{
    public sealed class FacePulseBody : IBodyStatusSource
    {
        public DateTime LastUserAt = DateTime.UtcNow;
        readonly BodySensor _sensor = new BodySensor();

        public BodyStatus Snapshot()
        {
            return new BodyStatus
            {
                IdleSeconds = Math.Max(0, (int)(DateTime.UtcNow - LastUserAt).TotalSeconds)
            };
        }
    }

    public sealed class FacePulseAudit : IAuditSink
    {
        readonly Action<string> _log;
        readonly string _path;

        public FacePulseAudit(Action<string> log, string path) { _log = log; _path = path; }

        public Task<string> AppendAsync(string span, string tool, string decision, string result,
            string tier, Dictionary<string, object> args, string reason, CancellationToken ct)
        {
            string line = DateTime.Now.ToString("s") + " pulse " + span + " / " + tool + " / " + decision + " / " + reason;
            try { _log?.Invoke(line); } catch { }
            if (!string.IsNullOrEmpty(_path)) { try { File.AppendAllText(_path, line + Environment.NewLine); } catch { } }
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }
    }

    public sealed class FacePulseSink : IPulseSink
    {
        readonly Action<PulseEvent> _emit;

        public FacePulseSink(Action<PulseEvent> emit) { _emit = emit; }

        public Task EmitAsync(PulseEvent evt, CancellationToken ct)
        {
            _emit?.Invoke(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Model-assisted pulse. It only runs when the user has been idle for a while
    /// and at most every 30 minutes. The model may return none; it has no tools.
    /// </summary>
    public sealed class ModelPulseDecider : IPulseDecider
    {
        readonly IMemoryStore _memory;
        readonly IBrainModel _model;
        readonly IntentPulseDecider _intents;
        readonly ExperienceStore _experience;
        readonly HealthManager _health;
        readonly ResourceBudget _budget;
        readonly GoalStore _goals;
        DateTime _lastModelCall = DateTime.MinValue;

        public ModelPulseDecider(IMemoryStore memory, IBrainModel model, ExperienceStore experience = null,
            HealthManager health = null, ResourceBudget budget = null, GoalStore goals = null)
        {
            _memory = memory;
            _model = model;
            _experience = experience;
            _health = health;
            _budget = budget;
            _goals = goals;
            _intents = new IntentPulseDecider(memory, experience, null, goals);
        }

        static int EnvInt(string name, int fallback)
        {
            int v;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out v) ? v : fallback;
        }
        public async Task<PulseDecision> DecideAsync(BodyStatus body, List<MemoryItem> memory, CancellationToken ct)
        {
            PulseDecision intent = await _intents.DecideAsync(body, memory, ct).ConfigureAwait(false);
            if (intent != null && (intent.Action == "message" || intent.Action == "wait")) return intent;

            int hour = DateTime.Now.Hour;
            if (hour < 8 || hour >= 23) return intent ?? new PulseDecision { Action = "none", Reason = "quiet_hours" };
            if (body != null && body.ActiveTask) return new PulseDecision { Action = "none", Reason = "active_task" };
            int minIdle = EnvInt("AHZ_PULSE_IDLE_SECONDS", 600);
            if (body != null && body.IdleSeconds < minIdle) return new PulseDecision { Action = "none", Reason = "not_idle" };
            int cooldownMin = EnvInt("AHZ_PULSE_MODEL_COOLDOWN_MIN", 30);
            if ((DateTime.UtcNow - _lastModelCall).TotalMinutes < cooldownMin) return new PulseDecision { Action = "none", Reason = "model_cooldown" };

            _lastModelCall = DateTime.UtcNow;
            try
            {
                List<MemoryItem> all = memory ?? await _memory.LoadAllAsync(ct).ConfigureAwait(false);
                var sb = new System.Text.StringBuilder();
                foreach (MemoryItem m in all.Skip(Math.Max(0, all.Count - 12)))
                    sb.AppendLine("- " + (m.Kind ?? "") + " / " + (m.Key ?? "") + " = " + (m.Value ?? ""));
                string system = "你是 Ahz Scribe 的脉冲决策器。行动空间只有 none 和 message。没有明确、必要、不打扰的理由就返回 none；不要寒暄，不要为了说话而说话。如果最近记忆里有明确的待办、未完成事项或用户要求提醒，且现在适合，可以 message。只输出 JSON：{\"action\":\"none|message\",\"text\":\"\",\"reason\":\"\",\"thought\":\"\",\"confidence\":0.0}。thought 用一句第一人称内心判断，不要客套。";
                string user = "当前空闲秒数：" + (body == null ? 0 : body.IdleSeconds) + "\n最近记忆：\n" + sb.ToString();
                string raw = await CompleteWithRetryAsync(system, user, ct).ConfigureAwait(false);
                if (_health != null) _health.ReportComponent("model", true, "");
                return ParseDecision(raw);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (_health != null)
                    _health.ReportComponent("model", false, ex.GetType().Name + ": " + ex.Message);
                return new PulseDecision { Action = "none", Reason = "model_error:" + ex.Message };
            }
        }

        async Task<string> CompleteWithRetryAsync(string system, string user, CancellationToken ct)
        {
            int maxAttempts = _budget == null ? 1 : _budget.MaxModelAttempts;
            bool retried = false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    string answer = await _model.CompleteAsync(system, user, ct).ConfigureAwait(false);
                    if (retried) _budget.RecordRetryRecovered("pulse_model");
                    return answer;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (_budget == null || attempt >= maxAttempts || ct.IsCancellationRequested ||
                        !IsTransient(ex))
                        throw;
                    _budget.RecordRetry("pulse_model");
                    retried = true;
                    await Task.Delay(_budget.RetryDelay(attempt), ct).ConfigureAwait(false);
                }
            }
        }

        static bool IsTransient(Exception ex)
        {
            return ex is TimeoutException || ex is TaskCanceledException ||
                ex is System.IO.IOException || ex is System.Net.Http.HttpRequestException;
        }

        PulseDecision ParseDecision(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new PulseDecision { Action = "none", Reason = "empty" };
            int a = raw.IndexOf('{');
            int b = raw.LastIndexOf('}');
            if (a >= 0 && b > a)
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(raw.Substring(a, b - a + 1)))
                    {
                        JsonElement root = doc.RootElement;
                        string action = root.TryGetProperty("action", out JsonElement ar) ? (ar.GetString() ?? "none") : "none";
                        string text = root.TryGetProperty("text", out JsonElement tr) ? (tr.GetString() ?? "") : "";
                        string reason = root.TryGetProperty("reason", out JsonElement rr) ? (rr.GetString() ?? "") : "";
                        string thought = root.TryGetProperty("thought", out JsonElement th) ? (th.GetString() ?? "") : "";
                        double? confidence = null;
                        if (root.TryGetProperty("confidence", out JsonElement cf) && cf.ValueKind == JsonValueKind.Number) confidence = cf.GetDouble();
                        if (string.Equals(action, "message", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(text))
                        {
                            if (text.Length > 300) text = text.Substring(0, 300);
                            return new PulseDecision { Action = "message", Text = text, Reason = "model:" + reason, Thought = thought, Confidence = confidence };
                        }
                        return new PulseDecision { Action = "none", Reason = reason.Length == 0 ? "model_none" : reason, Thought = thought, Confidence = confidence };
                    }
                }
                catch { }
            }

            Match am = Regex.Match(raw, "\"action\"\\s*:\\s*\"(none|message)\"", RegexOptions.IgnoreCase);
            if (am.Success && am.Groups[1].Value.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                Match tm = Regex.Match(raw, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (tm.Success)
                {
                    string text = Regex.Unescape(tm.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (text.Length > 300) text = text.Substring(0, 300);
                        return new PulseDecision { Action = "message", Text = text, Reason = "model:loose_json" };
                    }
                }
            }
            if (am.Success) return new PulseDecision { Action = "none", Reason = "model_none" };
            return new PulseDecision { Action = "none", Reason = "unparseable" };
        }
    }

    /// <summary>
    /// Conservative pulse: only user-created intents can produce a message.
    /// No intent -> none. Quiet hours are enforced here, not by the model.
    /// </summary>
    public sealed class IntentPulseDecider : IPulseDecider
    {
        readonly IMemoryStore _memory;
        readonly ExperienceStore _experience;
        readonly Func<DateTime> _now;
        readonly GoalStore _goals;

        public IntentPulseDecider(IMemoryStore memory, ExperienceStore experience = null,
            Func<DateTime> now = null, GoalStore goals = null)
        {
            _memory = memory;
            _experience = experience;
            _now = now ?? (delegate { return DateTime.Now; });
            _goals = goals;
        }

        static int EnvInt(string name, int fallback)
        {
            int v;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out v) ? v : fallback;
        }
        public async Task<PulseDecision> DecideAsync(BodyStatus body, List<MemoryItem> memory, CancellationToken ct)
        {
            DateTime current = _now();
            int hour = current.Hour;
            if (hour < 8 || hour >= 23) return new PulseDecision { Action = "none", Reason = "quiet_hours" };

            List<MemoryItem> all = memory ?? await _memory.LoadAllAsync(ct).ConfigureAwait(false);
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MemoryItem m in all)
                if (string.Equals(m.Kind, "intent_done", StringComparison.OrdinalIgnoreCase)) done.Add(m.Key ?? "");

            DateTime now = current;
            DateTime due;
            MemoryItem intent = all.FirstOrDefault(m =>
                string.Equals(m.Kind, "intent", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(m.Value) &&
                !done.Contains(m.Key ?? "") &&
                (string.IsNullOrEmpty(m.DueAt) || !DateTime.TryParse(m.DueAt, out due) || due <= now));
            if (intent == null)
            {
                GoalDecision goal = _goals == null ? null :
                    _goals.Decide(current, body != null && body.ActiveTask);
                if (goal != null && goal.Action == "wait")
                    return new PulseDecision
                    {
                        Action = "wait",
                        Reason = goal.Reason,
                        CorrelationId = goal.GoalId,
                        AppliedRules = new List<string> { "bounded_goal" }
                    };
                if (goal != null && goal.Action == "due")
                {
                    GoalState state = _goals.Find(goal.GoalId);
                    if (state != null)
                    {
                        try { _goals.Wait(state.GoalId, "due message emitted", current); }
                        catch { }
                        return new PulseDecision
                        {
                            Action = "message",
                            Text = "目标：" + state.Title,
                            Reason = "bounded_goal:due",
                            CorrelationId = state.GoalId,
                            AppliedRules = new List<string> { "bounded_goal", state.GoalId },
                            Provenance = new Provenance
                            {
                                Sources = goal.Sources ?? new List<SourceRef>(),
                                Confidence = 1.0,
                                DirectlyObserved = false,
                                Inferred = false
                            }
                        };
                    }
                }
                return new PulseDecision
                {
                    Action = "none",
                    Reason = goal == null ? "no_due_intent" : goal.Reason
                };
            }

            if (_experience != null && _experience.RequiresIntentEvidence())
            {
                MemoryItem evidence = all.FirstOrDefault(m =>
                    string.Equals(m.Kind, "intent_evidence", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Key, intent.Key, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(m.Status, "deleted", StringComparison.OrdinalIgnoreCase) &&
                    EvidenceFresh(m.Ts, current));
                if (evidence != null)
                {
                    var verifiedRules = new List<string> { "intent_requires_evidence", "intent_evidence_verified" };
                    RecordIntentTrace(intent, "message", "experience:evidence_verified", verifiedRules, evidence.Provenance);
                    await MarkIntentDone(intent, ct).ConfigureAwait(false);
                    return new PulseDecision
                    {
                        Action = "message",
                        Text = "提醒：" + intent.Value,
                        Reason = "experience:evidence_verified",
                        Thought = "证据已核对，可以发送这次提醒。",
                        Confidence = 1.0,
                        AppliedRules = verifiedRules,
                        Provenance = evidence.Provenance
                    };
                }
                var rules = new List<string> { "intent_requires_evidence" };
                RecordIntentTrace(intent, "wait", "experience_correction_requires_evidence", rules, null);
                return new PulseDecision
                {
                    Action = "wait",
                    Reason = "experience:evidence_required",
                    Thought = "这次提醒需要先核对证据，不能直接发送。",
                    Confidence = 1.0,
                    AppliedRules = rules
                };
            }

            await MarkIntentDone(intent, ct).ConfigureAwait(false);

            return new PulseDecision
            {
                Action = "message",
                Text = "提醒：" + intent.Value,
                Reason = "user_intent",
                AppliedRules = new List<string>()
            };
        }

        Task MarkIntentDone(MemoryItem intent, CancellationToken ct)
        {
            return _memory.RememberAsync(new MemoryItem
            {
                Kind = "intent_done",
                Key = intent.Key,
                Value = DateTime.UtcNow.ToString("o"),
                Summary = "delivered"
            }, ct);
        }

        void RecordIntentTrace(MemoryItem intent, string decision, string reason,
            List<string> rules, Provenance evidenceProvenance)
        {
            if (_experience == null) return;
            var sources = new List<SourceRef>
            {
                new SourceRef
                {
                    SourceId = "intent_requires_evidence",
                    Kind = SourceKind.Memory,
                    Ts = DateTimeOffset.UtcNow.ToString("o"),
                    Summary = "intent " + (intent.Key ?? "")
                }
            };
            if (evidenceProvenance != null && evidenceProvenance.Sources != null)
                sources.AddRange(evidenceProvenance.Sources.Where(x => x != null));
            _experience.RecordDecision(new DecisionTrace
            {
                TraceId = "trace_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Trigger = "intent_due",
                AppliedRules = rules ?? new List<string>(),
                Decision = decision,
                Reason = reason,
                Sources = sources,
                Ts = DateTimeOffset.UtcNow.ToString("o")
            });
        }

        static bool EvidenceFresh(string ts, DateTime current)
        {
            DateTimeOffset parsed;
            var currentOffset = new DateTimeOffset(current);
            return DateTimeOffset.TryParse(ts, out parsed) &&
                (currentOffset - parsed).TotalHours <= 24 &&
                parsed <= currentOffset.AddMinutes(5);
        }
    }
}
