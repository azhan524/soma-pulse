using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Bounded local goal state. A model proposal may exist only as a candidate;
    /// it can never be promoted to a long-lived goal without source evidence.
    /// </summary>
    public sealed class GoalStore
    {
        readonly string _path;
        readonly object _gate = new object();
        readonly int _dailyLimit;
        readonly int _activeLimit;
        readonly int _validityDays;
        readonly int _noResponsePauseAfter;
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GoalStore(string dataDir, int dailyLimit = 3, int activeLimit = 3,
            int validityDays = 7, int noResponsePauseAfter = 2)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "goals.jsonl");
            _dailyLimit = Math.Max(1, dailyLimit);
            _activeLimit = Math.Max(1, activeLimit);
            _validityDays = Math.Max(1, validityDays);
            _noResponsePauseAfter = Math.Max(1, noResponsePauseAfter);
        }

        public string PathForStorage { get { return _path; } }
        public int DailyLimit { get { return _dailyLimit; } }
        public int ActiveLimit { get { return _activeLimit; } }

        public GoalState Propose(string title, string reason, GoalSourceKind sourceKind,
            IEnumerable<AutobiographyEvidence> evidence, double score, DateTimeOffset now,
            string dueAt = "")
        {
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidDataException("goal title is required");
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException("goal reason is required");
            List<AutobiographyEvidence> normalized = NormalizeEvidence(evidence);
            if (normalized.Count == 0)
                throw new InvalidDataException("goal requires evidence");
            string fingerprint = Fingerprint(sourceKind, normalized, title);
            lock (_gate)
            {
                List<GoalState> states = LoadLatestUnsafe();
                GoalState existing = states.FirstOrDefault(x => x.Fingerprint == fingerprint);
                if (existing != null)
                {
                    if (IsClosed(existing.Status))
                        throw new InvalidDataException("closed goal cannot be revived: " + existing.GoalId);
                    return existing;
                }
                int createdToday = states.Count(x =>
                    ParseTime(x.CreatedAt).LocalDateTime.Date == now.LocalDateTime.Date);
                if (createdToday >= _dailyLimit)
                    throw new InvalidDataException("daily_goal_limit_reached");
                if (states.Count(x => IsActive(x.Status)) >= _activeLimit)
                    throw new InvalidDataException("active_goal_limit_reached");
                var goal = new GoalState
                {
                    GoalId = "goal_" + Hash(fingerprint).Substring(0, 16),
                    Fingerprint = fingerprint,
                    Title = title.Trim(),
                    Reason = reason.Trim(),
                    Status = GoalStatus.Candidate,
                    SourceKind = sourceKind,
                    Sources = normalized.Select(x => x.Source).Where(x => x != null).ToList(),
                    EvidenceIds = normalized.Select(x => x.EvidenceId).ToList(),
                    Score = Math.Max(0, Math.Min(1, score)),
                    CreatedAt = now.ToString("o"),
                    UpdatedAt = now.ToString("o"),
                    DueAt = dueAt ?? "",
                    ExpiresAt = now.AddDays(_validityDays).ToString("o"),
                    NoResponseCount = 0,
                    PausedUntil = "",
                    LastTransition = "created"
                };
                Append(goal);
                return goal;
            }
        }

        public GoalState Promote(string goalId, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Monitoring, "promoted", now,
                delegate(GoalState state)
                {
                    if (state.SourceKind == GoalSourceKind.ModelProposal)
                        throw new InvalidDataException("model proposal cannot be promoted");
                    if (state.Sources == null || state.Sources.Count == 0 ||
                        state.EvidenceIds == null || state.EvidenceIds.Count == 0)
                        throw new InvalidDataException("goal source evidence missing");
                });
        }

        public GoalState MarkDue(string goalId, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Due, "due", now, null);
        }

        public GoalState Pause(string goalId, string reason, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Paused, reason, now, null);
        }

        public GoalState Wait(string goalId, string reason, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Waiting, reason, now, null);
        }

        public GoalState Resume(string goalId, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Monitoring, "resumed", now, null);
        }

        public GoalState Complete(string goalId, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Done, "completed", now, null);
        }

        public GoalState Cancel(string goalId, string reason, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Cancelled,
                string.IsNullOrWhiteSpace(reason) ? "user_cancelled" : reason, now, null);
        }

        public GoalState Expire(string goalId, DateTimeOffset now)
        {
            return Transition(goalId, GoalStatus.Expired, "expired", now, null);
        }

        public GoalState RecordNoResponse(string goalId, DateTimeOffset now)
        {
            lock (_gate)
            {
                GoalState state = LatestUnsafe(goalId);
                if (state == null) throw new InvalidDataException("goal not found: " + goalId);
                state.NoResponseCount++;
                state.UpdatedAt = now.ToString("o");
                if (state.NoResponseCount >= _noResponsePauseAfter &&
                    (state.Status == GoalStatus.Monitoring || state.Status == GoalStatus.Due))
                {
                    state.Status = GoalStatus.Paused;
                    state.PausedUntil = now.AddHours(24).ToString("o");
                    state.LastTransition = "paused_after_no_response";
                }
                else state.LastTransition = "no_response_recorded";
                Append(state);
                return state;
            }
        }

        public void RecordUserResponse(DateTimeOffset now)
        {
            lock (_gate)
            {
                foreach (GoalState state in LoadLatestUnsafe().Where(x => IsActive(x.Status)))
                {
                    if (state.NoResponseCount == 0) continue;
                    state.NoResponseCount = 0;
                    state.LastTransition = "user_response_received";
                    state.UpdatedAt = now.ToString("o");
                    Append(state);
                }
            }
        }

        public GoalDecision Decide(DateTimeOffset now, bool activeTask)
        {
            lock (_gate)
            {
                ExpireInternal(now);
                List<GoalState> active = LoadLatestUnsafe()
                    .Where(x => IsActive(x.Status)).ToList();
                if (activeTask && active.Count > 0)
                    return new GoalDecision { Action = "none", Reason = "active_task" };
                if (active.Count == 0)
                    return new GoalDecision { Action = "none", Reason = "no_goal" };
                GoalState waiting = active.FirstOrDefault(x => x.Status == GoalStatus.Waiting);
                if (waiting != null)
                    return Decision("wait", waiting, "goal_waiting");
                GoalState due = active.Where(x => !string.IsNullOrWhiteSpace(x.DueAt))
                    .FirstOrDefault(x => ParseTime(x.DueAt) <= now);
                if (due != null)
                {
                    if (due.Status != GoalStatus.Due) due = MarkDueInternal(due, now);
                    return Decision("due", due, "goal_due");
                }
                GoalState next = active
                    .Where(x => x.Status != GoalStatus.Paused)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.CreatedAt, StringComparer.Ordinal)
                    .FirstOrDefault();
                return next == null
                    ? new GoalDecision { Action = "none", Reason = "all_goals_paused" }
                    : Decision("none", next, "goal_monitoring");
            }
        }

        public GoalState Find(string goalId)
        {
            lock (_gate) return LatestUnsafe(goalId);
        }

        public List<GoalState> LoadLatest()
        {
            lock (_gate) return LoadLatestUnsafe()
                .OrderBy(x => x.Status)
                .ThenByDescending(x => x.Score)
                .ToList();
        }

        public List<GoalState> LoadHistory()
        {
            lock (_gate)
            {
                var result = new List<GoalState>();
                if (!File.Exists(_path)) return result;
                foreach (string line in File.ReadLines(_path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        GoalState state = JsonSerializer.Deserialize<GoalState>(line, _json);
                        if (state != null) result.Add(state);
                    }
                    catch { }
                }
                return result;
            }
        }

        public string BuildContext(int max = 6)
        {
            List<GoalState> active = LoadLatest().Where(x => IsActive(x.Status))
                .OrderByDescending(x => x.Score).Take(max).ToList();
            if (active.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("有界目标上下文（目标不得扩大权限）：");
            foreach (GoalState goal in active)
                sb.AppendLine("- " + goal.Title + " / " + goal.Status + " / " +
                    goal.SourceKind + " / 分数 " + goal.Score.ToString("F2",
                        CultureInfo.InvariantCulture) + " / 证据 " +
                    goal.EvidenceIds.Count);
            return sb.ToString();
        }

        GoalState Transition(string goalId, GoalStatus target, string reason,
            DateTimeOffset now, Action<GoalState> validate)
        {
            lock (_gate)
            {
                GoalState state = LatestUnsafe(goalId);
                if (state == null) throw new InvalidDataException("goal not found: " + goalId);
                if (IsClosed(state.Status))
                    throw new InvalidDataException("closed goal cannot transition: " + goalId);
                if (!Allowed(state.Status, target))
                    throw new InvalidDataException("invalid goal transition: " +
                        state.Status + " -> " + target);
                if (validate != null) validate(state);
                state.Status = target;
                state.LastTransition = reason ?? target.ToString();
                state.UpdatedAt = now.ToString("o");
                if (target == GoalStatus.Expired) state.PausedUntil = "";
                Append(state);
                return state;
            }
        }

        GoalState MarkDueInternal(GoalState state, DateTimeOffset now)
        {
            state.Status = GoalStatus.Due;
            state.LastTransition = "due";
            state.UpdatedAt = now.ToString("o");
            Append(state);
            return state;
        }

        void ExpireInternal(DateTimeOffset now)
        {
            foreach (GoalState state in LoadLatestUnsafe().Where(x => IsActive(x.Status)))
            {
                if (ParseTime(state.ExpiresAt) > now) continue;
                state.Status = GoalStatus.Expired;
                state.LastTransition = "expired";
                state.UpdatedAt = now.ToString("o");
                Append(state);
            }
        }

        GoalState LatestUnsafe(string goalId)
        {
            return LoadLatestUnsafe().FirstOrDefault(x =>
                string.Equals(x.GoalId, goalId, StringComparison.OrdinalIgnoreCase));
        }

        List<GoalState> LoadLatestUnsafe()
        {
            return LoadHistory().GroupBy(x => x.GoalId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderBy(y => y.UpdatedAt, StringComparer.Ordinal).Last())
                .ToList();
        }

        void Append(GoalState state)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(state, _json) +
                Environment.NewLine, new UTF8Encoding(false));
        }

        static GoalDecision Decision(string action, GoalState state, string reason)
        {
            return new GoalDecision
            {
                Action = action,
                GoalId = state == null ? "" : state.GoalId,
                Reason = reason,
                Sources = state == null || state.Sources == null
                    ? new List<SourceRef>()
                    : state.Sources.ToList()
            };
        }

        static bool IsActive(GoalStatus status)
        {
            return status == GoalStatus.Candidate || status == GoalStatus.Monitoring ||
                status == GoalStatus.Due || status == GoalStatus.Paused ||
                status == GoalStatus.Waiting;
        }

        static bool IsClosed(GoalStatus status)
        {
            return status == GoalStatus.Done || status == GoalStatus.Cancelled ||
                status == GoalStatus.Expired;
        }

        static bool Allowed(GoalStatus from, GoalStatus to)
        {
            if (from == GoalStatus.Candidate)
                return to == GoalStatus.Monitoring || to == GoalStatus.Cancelled ||
                    to == GoalStatus.Expired;
            if (from == GoalStatus.Monitoring)
                return to == GoalStatus.Due || to == GoalStatus.Paused ||
                    to == GoalStatus.Waiting || to == GoalStatus.Done ||
                    to == GoalStatus.Cancelled || to == GoalStatus.Expired;
            if (from == GoalStatus.Due)
                return to == GoalStatus.Done || to == GoalStatus.Paused ||
                    to == GoalStatus.Waiting || to == GoalStatus.Cancelled ||
                    to == GoalStatus.Expired;
            if (from == GoalStatus.Paused)
                return to == GoalStatus.Monitoring || to == GoalStatus.Waiting ||
                    to == GoalStatus.Cancelled || to == GoalStatus.Expired;
            if (from == GoalStatus.Waiting)
                return to == GoalStatus.Monitoring || to == GoalStatus.Due ||
                    to == GoalStatus.Cancelled || to == GoalStatus.Expired;
            return false;
        }

        static List<AutobiographyEvidence> NormalizeEvidence(
            IEnumerable<AutobiographyEvidence> evidence)
        {
            var result = new List<AutobiographyEvidence>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutobiographyEvidence item in evidence ?? new List<AutobiographyEvidence>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.EvidenceId) ||
                    item.Source == null || !seen.Add(item.EvidenceId)) continue;
                result.Add(new AutobiographyEvidence
                {
                    EvidenceId = item.EvidenceId,
                    Source = item.Source
                });
            }
            return result;
        }

        static string Fingerprint(GoalSourceKind sourceKind,
            List<AutobiographyEvidence> evidence, string title)
        {
            return sourceKind + "|" + string.Join(",", evidence.Select(x => x.EvidenceId)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + "|" +
                (title ?? "").Trim().ToLowerInvariant();
        }

        static DateTimeOffset ParseTime(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out parsed) ? parsed : DateTimeOffset.MinValue;
        }

        static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .ToLowerInvariant();
        }
    }
}
