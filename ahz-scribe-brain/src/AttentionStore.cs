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
    /// Append-only long-term attention state. Attention changes retrieval and model
    /// context, but never grants permissions or creates facts.
    /// </summary>
    public sealed class AttentionStore
    {
        const int DefaultValidityDays = 14;
        const int RecentDays = 30;
        readonly string _path;
        readonly object _gate = new object();
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public AttentionStore(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "attention.jsonl");
        }

        public string PathForStorage { get { return _path; } }

        public AttentionState FormOrRefresh(string subject, string reason,
            IEnumerable<AutobiographyEvidence> evidence, DateTimeOffset now,
            int validityDays = DefaultValidityDays)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new InvalidDataException("attention subject is required");
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException("attention formation reason is required");
            List<AutobiographyEvidence> normalized = NormalizeEvidence(evidence);
            if (normalized.Count == 0)
                throw new InvalidDataException("attention requires evidence");
            if (!normalized.Any(x => x.Source != null &&
                x.Source.Kind != SourceKind.Model))
                throw new InvalidDataException("model-only evidence cannot form long-term attention");

            lock (_gate)
            {
                List<AttentionState> states = LoadLatestUnsafe();
                AttentionState state = states.FirstOrDefault(x =>
                    string.Equals(x.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase));
                if (state == null)
                {
                    state = new AttentionState
                    {
                        AttentionId = "attention_" + Hash(subject.Trim().ToLowerInvariant()).Substring(0, 16),
                        Subject = subject.Trim(),
                        Status = AttentionStatus.Monitoring,
                        FormationReason = reason.Trim(),
                        CreatedAt = now.ToString("o")
                    };
                    state.ContinueConditions.Add("最近 30 天持续出现非模型证据");
                    state.EndConditions.Add("用户明确结束");
                    state.EndConditions.Add("超过有效期没有新证据");
                }
                MergeEvidence(state, normalized);
                state.FormationReason = reason.Trim();
                state.LatestEvidenceAt = LatestEvidenceAt(state.Sources, now);
                state.ExpiresAt = now.AddDays(Math.Max(1, validityDays)).ToString("o");
                state.Score = Score(state, now);
                state.UpdatedAt = now.ToString("o");
                Append(state);
                return state;
            }
        }

        public List<AttentionState> RefreshFromMemories(IEnumerable<MemoryItem> memories,
            DateTimeOffset now)
        {
            lock (_gate)
            {
                var groups = (memories ?? new List<MemoryItem>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => new { Memory = x, Subject = SubjectOf(x), Evidence = EvidenceFor(x) })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Subject))
                    .GroupBy(x => x.Subject, StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    var evidence = group.Select(x => x.Evidence).ToList();
                    int nonModel = evidence.Count(x => x.Source != null &&
                        x.Source.Kind != SourceKind.Model);
                    if (nonModel < 2) continue;
                    FormOrRefresh(group.Key,
                        group.Count() + " 条可追溯记忆重复指向该主题",
                        evidence, now);
                }
                ExpireInternal(now);
                return ActiveStatesUnsafe(now);
            }
        }

        public List<AttentionState> Expire(DateTimeOffset now)
        {
            lock (_gate)
            {
                ExpireInternal(now);
                return ActiveStatesUnsafe(now);
            }
        }

        public AttentionState Pause(string subjectOrId, string reason, DateTimeOffset now)
        {
            return ChangeStatus(subjectOrId, AttentionStatus.Paused, reason, now);
        }

        public AttentionState Resume(string subjectOrId, string reason, DateTimeOffset now)
        {
            return ChangeStatus(subjectOrId, AttentionStatus.Monitoring, reason, now);
        }

        public AttentionState End(string subjectOrId, string reason, DateTimeOffset now)
        {
            return ChangeStatus(subjectOrId, AttentionStatus.Ended, reason, now);
        }

        AttentionState ChangeStatus(string subjectOrId, AttentionStatus status,
            string reason, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(subjectOrId))
                throw new InvalidDataException("attention subject or id is required");
            lock (_gate)
            {
                AttentionState state = LoadLatestUnsafe().FirstOrDefault(x =>
                    string.Equals(x.AttentionId, subjectOrId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Subject, subjectOrId, StringComparison.OrdinalIgnoreCase));
                if (state == null) throw new InvalidDataException("attention not found: " + subjectOrId);
                if (status == AttentionStatus.Monitoring &&
                    (state.Status == AttentionStatus.Ended || state.Status == AttentionStatus.Expired))
                    throw new InvalidDataException("ended attention cannot resume; create new evidence");
                state.Status = status;
                state.FormationReason = string.IsNullOrWhiteSpace(reason)
                    ? state.FormationReason
                    : reason.Trim();
                state.UpdatedAt = now.ToString("o");
                Append(state);
                return state;
            }
        }

        public AttentionState Dominant(DateTimeOffset now)
        {
            lock (_gate)
            {
                ExpireInternal(now);
                return ActiveStatesUnsafe(now)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.LatestEvidenceAt, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
        }

        public List<AttentionState> LoadLatest()
        {
            lock (_gate) return LoadLatestUnsafe()
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Subject, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<MemoryItem> InfluenceRetrieval(string query, IEnumerable<MemoryItem> hits,
            int topK)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            List<AttentionState> active = Expire(now);
            List<string> subjects = active.Select(x => x.Subject).Where(x =>
                !string.IsNullOrWhiteSpace(x)).ToList();
            var scored = new List<KeyValuePair<MemoryItem, double>>();
            int index = 0;
            foreach (MemoryItem item in hits ?? new List<MemoryItem>())
            {
                if (item == null) continue;
                string text = ((item.Key ?? "") + " " + (item.Value ?? "") + " " +
                    (item.Summary ?? "")).ToLowerInvariant();
                double score = 100.0 - index++;
                foreach (string subject in subjects)
                    if (text.Contains(subject.ToLowerInvariant())) score += 25.0;
                scored.Add(new KeyValuePair<MemoryItem, double>(item, score));
            }
            return scored.OrderByDescending(x => x.Value).Take(Math.Max(1, topK))
                .Select(x => x.Key).ToList();
        }

        public bool IsRelated(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.ToLowerInvariant();
            return Expire(DateTimeOffset.Now).Any(x =>
                value.Contains((x.Subject ?? "").ToLowerInvariant()));
        }

        public string BuildContext(int max = 6)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            List<AttentionState> states = Expire(now);
            if (states.Count == 0) return "";
            AttentionState dominant = states.OrderByDescending(x => x.Score).FirstOrDefault();
            var sb = new StringBuilder();
            sb.AppendLine("长期关注上下文（关注只影响检索排序，不增加事实或权限）：");
            if (dominant != null)
                sb.AppendLine("- 当前主导关注：" + dominant.Subject + "，证据 " +
                    dominant.EvidenceIds.Count + " 条，分数 " + dominant.Score.ToString("F2",
                        CultureInfo.InvariantCulture));
            foreach (AttentionState state in states.OrderByDescending(x => x.Score).Take(max))
                sb.AppendLine("- " + state.Subject + " / " + state.Status + " / " +
                    (state.FormationReason ?? "") + " / 到期 " + (state.ExpiresAt ?? ""));
            return sb.ToString();
        }

        void ExpireInternal(DateTimeOffset now)
        {
            foreach (AttentionState state in LoadLatestUnsafe())
            {
                if (state.Status != AttentionStatus.Monitoring &&
                    state.Status != AttentionStatus.Candidate) continue;
                DateTimeOffset expires;
                if (!DateTimeOffset.TryParse(state.ExpiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out expires) || now < expires) continue;
                state.Status = AttentionStatus.Expired;
                state.FormationReason = "attention validity elapsed without new evidence";
                state.UpdatedAt = now.ToString("o");
                Append(state);
            }
        }

        List<AttentionState> ActiveStatesUnsafe(DateTimeOffset now)
        {
            return LoadLatestUnsafe()
                .Where(x => x.Status == AttentionStatus.Candidate ||
                    x.Status == AttentionStatus.Monitoring)
                .OrderByDescending(x => x.Score).ToList();
        }

        void Append(AttentionState state)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(state, _json) +
                Environment.NewLine, new UTF8Encoding(false));
        }

        List<AttentionState> LoadLatestUnsafe()
        {
            var all = new List<AttentionState>();
            if (!File.Exists(_path)) return all;
            foreach (string line in File.ReadLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    AttentionState state = JsonSerializer.Deserialize<AttentionState>(line, _json);
                    if (state != null) all.Add(state);
                }
                catch { }
            }
            return all.GroupBy(x => x.Subject ?? x.AttentionId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderBy(y => y.UpdatedAt, StringComparer.Ordinal).Last())
                .ToList();
        }

        static List<AutobiographyEvidence> NormalizeEvidence(
            IEnumerable<AutobiographyEvidence> evidence)
        {
            var result = new List<AutobiographyEvidence>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutobiographyEvidence item in evidence ?? new List<AutobiographyEvidence>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.EvidenceId) ||
                    item.Source == null) continue;
                if (seen.Add(item.EvidenceId))
                    result.Add(new AutobiographyEvidence
                    {
                        EvidenceId = item.EvidenceId,
                        Source = item.Source
                    });
            }
            return result;
        }

        static void MergeEvidence(AttentionState state, List<AutobiographyEvidence> evidence)
        {
            var ids = new HashSet<string>(state.EvidenceIds ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (AutobiographyEvidence item in evidence)
                if (ids.Add(item.EvidenceId)) state.EvidenceIds.Add(item.EvidenceId);
            foreach (SourceRef source in evidence.Select(x => x.Source))
            {
                bool exists = state.Sources.Any(x => x != null &&
                    string.Equals(x.SourceId ?? "", source.SourceId ?? "",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.AuditId ?? "", source.AuditId ?? "",
                        StringComparison.OrdinalIgnoreCase));
                if (!exists) state.Sources.Add(source);
            }
        }

        static string LatestEvidenceAt(List<SourceRef> sources, DateTimeOffset fallback)
        {
            DateTimeOffset latest = fallback;
            foreach (SourceRef source in sources ?? new List<SourceRef>())
            {
                DateTimeOffset ts;
                if (source != null && DateTimeOffset.TryParse(source.Ts,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                    ts > latest) latest = ts;
            }
            return latest.ToString("o");
        }

        static double Score(AttentionState state, DateTimeOffset now)
        {
            DateTimeOffset latest;
            if (!DateTimeOffset.TryParse(state.LatestEvidenceAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out latest)) latest = now;
            double recency = Math.Max(0, 30.0 - (now - latest).TotalDays) / 10.0;
            int sourceCount = (state.Sources ?? new List<SourceRef>())
                .Where(x => x != null).Select(x => (x.SourceId ?? x.AuditId ?? ""))
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return Math.Round((state.EvidenceIds ?? new List<string>()).Count +
                sourceCount * 1.5 + recency, 4);
        }

        static AutobiographyEvidence EvidenceFor(MemoryItem memory)
        {
            SourceRef source = memory.Provenance != null && memory.Provenance.Sources != null
                ? memory.Provenance.Sources.FirstOrDefault(x => x != null)
                : null;
            if (source == null)
                source = new SourceRef
                {
                    SourceId = memory.SourceAuditId,
                    Kind = SourceKind.Memory,
                    DeviceId = memory.OriginDevice ?? "",
                    Ts = memory.Ts ?? "",
                    Summary = memory.Summary ?? memory.Value ?? ""
                };
            return new AutobiographyEvidence
            {
                EvidenceId = "memory:" + memory.Id,
                Source = source
            };
        }

        static string SubjectOf(MemoryItem memory)
        {
            if (memory == null || memory.Status == "deleted") return "";
            string tag = (memory.Tag ?? "").Trim();
            if (!string.IsNullOrEmpty(tag) &&
                !tag.Equals("episode", StringComparison.OrdinalIgnoreCase) &&
                !tag.Equals("intent", StringComparison.OrdinalIgnoreCase) &&
                !tag.Equals("evidence", StringComparison.OrdinalIgnoreCase))
                return tag;
            string key = memory.Key ?? "";
            int colon = key.IndexOf(':');
            if (colon > 0) return key.Substring(0, colon);
            return "";
        }

        static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .ToLowerInvariant();
        }
    }
}
