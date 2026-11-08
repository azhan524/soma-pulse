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
    /// Append-only autobiography. Every layer must point to raw evidence; narrative
    /// entries are never allowed to become evidence for later narrative entries.
    /// </summary>
    public sealed class AutobiographyStore
    {
        readonly string _path;
        readonly object _gate = new object();
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public AutobiographyStore(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "autobiography.jsonl");
        }

        public string PathForStorage { get { return _path; } }

        public AutobiographyEntry Add(AutobiographyPeriod period, string periodKey,
            AutobiographyLayer layer, string text, double? confidence,
            IEnumerable<AutobiographyEvidence> evidence)
        {
            if (string.IsNullOrWhiteSpace(periodKey))
                throw new InvalidDataException("autobiography period key is required");
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("autobiography text is required");
            List<AutobiographyEvidence> normalized = NormalizeEvidence(evidence);
            if (normalized.Count == 0)
                throw new InvalidDataException("autobiography entry requires evidence");
            if (normalized.Any(x => string.IsNullOrWhiteSpace(x.EvidenceId)))
                throw new InvalidDataException("autobiography evidence id is required");
            if (normalized.Any(x => x.Source == null))
                throw new InvalidDataException("autobiography evidence source is required");
            if (normalized.Any(x => (x.EvidenceId ?? "").StartsWith("narrative:",
                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("narrative entries cannot support other narrative entries");
            if (layer == AutobiographyLayer.Fact &&
                !normalized.Any(x => x.Source.Kind != SourceKind.Model))
                throw new InvalidDataException("fact layer requires non-model evidence");

            string cleanText = text.Trim();
            var entry = new AutobiographyEntry
            {
                EntryId = ComputeId(period, periodKey, layer, cleanText, normalized),
                Period = period,
                PeriodKey = periodKey.Trim(),
                Layer = layer,
                Text = cleanText,
                Confidence = confidence,
                Evidence = normalized,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            lock (_gate)
            {
                AutobiographyEntry existing = ReadAllUnsafe()
                    .FirstOrDefault(x => x != null && x.EntryId == entry.EntryId);
                if (existing != null) return existing;
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, _json) +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            return entry;
        }

        public AutobiographyBuildResult BuildRecent(IEnumerable<MemoryItem> memories,
            string currentFocus, DateTimeOffset now, int days = 7)
        {
            if (days < 1) days = 1;
            var result = new AutobiographyBuildResult { GeneratedAt = now.ToString("o") };
            DateTimeOffset from = now.AddDays(-days);
            var eligible = new List<KeyValuePair<MemoryItem, AutobiographyEvidence>>();
            foreach (MemoryItem memory in memories ?? new List<MemoryItem>())
            {
                if (memory == null) { result.UnsupportedEvidence++; continue; }
                if (string.IsNullOrWhiteSpace(memory.Id))
                {
                    result.UnsupportedEvidence++;
                    continue;
                }
                DateTimeOffset ts;
                if (!DateTimeOffset.TryParse(memory.Ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out ts) || ts < from || ts > now.AddDays(1))
                    continue;
                eligible.Add(new KeyValuePair<MemoryItem, AutobiographyEvidence>(
                    memory, EvidenceFor(memory)));
            }
            if (eligible.Count == 0) return result;

            foreach (var dayGroup in eligible.GroupBy(x =>
                x.Key.TimestampUtc().ToLocalTime().Date).OrderBy(x => x.Key))
            {
                string dayKey = dayGroup.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                List<AutobiographyEvidence> evidence = dayGroup.Select(x => x.Value).ToList();
                List<MemoryItem> factMemories = dayGroup
                    .Where(x => x.Value.Source != null &&
                        x.Value.Source.Kind != SourceKind.Model)
                    .Select(x => x.Key).ToList();
                if (factMemories.Count > 0)
                    result.Entries.Add(Add(AutobiographyPeriod.Day, dayKey,
                        AutobiographyLayer.Fact, BuildFactText(factMemories), 1.0,
                        dayGroup.Where(x => factMemories.Contains(x.Key))
                            .Select(x => x.Value)));

                string themes = string.Join("、", dayGroup.Select(x => x.Key)
                    .Select(x => ThemeOf(x)).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
                if (!string.IsNullOrWhiteSpace(themes))
                    result.Entries.Add(Add(AutobiographyPeriod.Day, dayKey,
                        AutobiographyLayer.Interpretation,
                        "从当天 " + dayGroup.Count() + " 条可追溯记忆看，关注主题集中在：" + themes +
                        "。这是依据原始记忆作出的解释，不是新增事实。",
                        0.7, evidence));

                foreach (var conflict in dayGroup
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key.Key))
                    .GroupBy(x => x.Key.Key, StringComparer.Ordinal)
                    .Where(x => x.Select(y => y.Key.Value).Distinct(StringComparer.Ordinal).Count() > 1))
                {
                    string values = string.Join("；", conflict.Select(x =>
                        (x.Key.Value ?? "") + " [" + x.Value.EvidenceId + "]"));
                    result.Entries.Add(Add(AutobiographyPeriod.Day, dayKey,
                        AutobiographyLayer.Conflict,
                        "冲突主题 " + conflict.Key + " 保留不同说法：" + values +
                        "。未静默选择其中一方。",
                        0.5, conflict.Select(x => x.Value)));
                }
            }

            if (!string.IsNullOrWhiteSpace(currentFocus))
            {
                string dayKey = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var focusEvidence = new AutobiographyEvidence
                {
                    EvidenceId = "state:focus:" + dayKey,
                    Source = new SourceRef
                    {
                        SourceId = "current_state.current_focus",
                        Kind = SourceKind.System,
                        Ts = now.ToString("o"),
                        Summary = currentFocus
                    }
                };
                result.Entries.Add(Add(AutobiographyPeriod.Day, dayKey,
                    AutobiographyLayer.Guess,
                    "当前焦点可能是“" + Short(currentFocus, 120) +
                    "”，但这只是状态线索，不据此新增事实或改写身份。",
                    0.35, new[] { focusEvidence }));
            }

            List<AutobiographyEvidence> allEvidence = eligible.Select(x => x.Value).ToList();
            List<AutobiographyEvidence> factEvidence = eligible
                .Where(x => x.Value.Source != null && x.Value.Source.Kind != SourceKind.Model)
                .Select(x => x.Value).ToList();
            DateTime localNow = now.ToLocalTime().DateTime;
            string weekKey = ISOWeek.GetYear(localNow).ToString(CultureInfo.InvariantCulture) +
                "-W" + ISOWeek.GetWeekOfYear(localNow).ToString("00", CultureInfo.InvariantCulture);
            if (factEvidence.Count > 0)
                result.Entries.Add(Add(AutobiographyPeriod.Week, weekKey,
                    AutobiographyLayer.Fact,
                    "最近 " + days + " 天共有 " + factEvidence.Count + " 条可作事实层的可追溯记录。",
                    1.0, factEvidence));
            result.Entries.Add(Add(AutobiographyPeriod.Week, weekKey,
                AutobiographyLayer.Interpretation,
                "最近一段时间的主要关注主题：" +
                string.Join("、", eligible.Select(x => ThemeOf(x.Key))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(6)),
                0.65, allEvidence));

            foreach (var themeGroup in eligible
                .Where(x => !string.IsNullOrWhiteSpace(ThemeOf(x.Key)))
                .GroupBy(x => ThemeOf(x.Key), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<AutobiographyEvidence> themeEvidence = themeGroup.Select(x => x.Value).ToList();
                bool hasNonModel = themeGroup.Any(x => x.Value.Source != null &&
                    x.Value.Source.Kind != SourceKind.Model);
                result.Entries.Add(Add(AutobiographyPeriod.Theme, themeGroup.Key,
                    hasNonModel ? AutobiographyLayer.Fact : AutobiographyLayer.Interpretation,
                    "主题 " + themeGroup.Key + " 有 " + themeGroup.Count() +
                    " 条可追溯记忆" + (hasNonModel ? "。" : "，均来自模型推断。"),
                    hasNonModel ? 1.0 : 0.5, themeEvidence));
            }
            return result;
        }

        public string BuildContext(int maxEntries = 8)
        {
            List<AutobiographyEntry> entries = LoadAll()
                .Where(x => x != null && x.Status == "active")
                .OrderByDescending(x => x.CreatedAt, StringComparer.Ordinal)
                .Take(Math.Max(1, maxEntries)).ToList();
            if (entries.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("可追溯自传上下文（只能作为经历摘要，不能借此增加事实或权限）：");
            foreach (AutobiographyEntry entry in entries)
                sb.AppendLine("- [" + entry.Layer + "/" + entry.Period + "] " +
                    Short(entry.Text, 220) + "（证据 " + entry.Evidence.Count + " 条）");
            return sb.ToString();
        }

        public AutobiographyVerification Verify(AutobiographyEntry entry,
            IEnumerable<string> knownEvidenceIds = null)
        {
            var result = new AutobiographyVerification { Valid = true };
            if (entry == null)
            {
                result.Valid = false;
                result.Errors.Add("entry_missing");
                return result;
            }
            if (string.IsNullOrWhiteSpace(entry.EntryId))
                result.Errors.Add("entry_id_missing");
            if (string.IsNullOrWhiteSpace(entry.Text))
                result.Errors.Add("text_missing");
            if (entry.Evidence == null || entry.Evidence.Count == 0)
                result.Errors.Add("evidence_missing");
            else
            {
                HashSet<string> known = knownEvidenceIds == null
                    ? null
                    : new HashSet<string>(knownEvidenceIds.Where(x => x != null),
                        StringComparer.OrdinalIgnoreCase);
                foreach (AutobiographyEvidence evidence in entry.Evidence)
                {
                    if (evidence == null || string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
                        evidence.Source == null)
                    {
                        result.Errors.Add("evidence_invalid");
                        continue;
                    }
                    if (evidence.EvidenceId.StartsWith("narrative:",
                        StringComparison.OrdinalIgnoreCase))
                        result.Errors.Add("narrative_circular_evidence: " + evidence.EvidenceId);
                    if (known != null && !known.Contains(evidence.EvidenceId))
                        result.Errors.Add("evidence_unknown: " + evidence.EvidenceId);
                    if (string.IsNullOrWhiteSpace(evidence.Source.SourceId) &&
                        string.IsNullOrWhiteSpace(evidence.Source.AuditId))
                        result.Errors.Add("evidence_source_invalid: " + evidence.EvidenceId);
                }
            }
            if (entry.Layer == AutobiographyLayer.Conflict &&
                (entry.Evidence == null ||
                 entry.Evidence.Select(x => x == null ? "" : x.EvidenceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2))
                result.Errors.Add("conflict_requires_two_evidence_sources");
            result.Valid = result.Errors.Count == 0;
            return result;
        }

        public List<AutobiographyEntry> LoadAll()
        {
            lock (_gate) return ReadAllUnsafe();
        }

        List<AutobiographyEntry> ReadAllUnsafe()
        {
            var result = new List<AutobiographyEntry>();
            if (!File.Exists(_path)) return result;
            foreach (string line in File.ReadLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    AutobiographyEntry entry = JsonSerializer.Deserialize<AutobiographyEntry>(line, _json);
                    if (entry != null) result.Add(entry);
                }
                catch { }
            }
            return result;
        }

        static List<AutobiographyEvidence> NormalizeEvidence(
            IEnumerable<AutobiographyEvidence> evidence)
        {
            var result = new List<AutobiographyEvidence>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutobiographyEvidence item in evidence ?? new List<AutobiographyEvidence>())
            {
                if (item == null) continue;
                string id = (item.EvidenceId ?? "").Trim();
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                result.Add(new AutobiographyEvidence { EvidenceId = id, Source = item.Source });
            }
            return result;
        }

        static AutobiographyEvidence EvidenceFor(MemoryItem memory)
        {
            SourceRef source = memory.Provenance != null && memory.Provenance.Sources != null
                ? memory.Provenance.Sources.FirstOrDefault(x => x != null)
                : null;
            if (source == null)
            {
                source = new SourceRef
                {
                    SourceId = memory.SourceAuditId,
                    Kind = SourceKind.Memory,
                    DeviceId = memory.OriginDevice ?? "",
                    Ts = memory.Ts ?? "",
                    Summary = memory.Summary ?? memory.Value ?? ""
                };
            }
            return new AutobiographyEvidence
            {
                EvidenceId = "memory:" + memory.Id,
                Source = source
            };
        }

        static string BuildFactText(IEnumerable<MemoryItem> memories)
        {
            string text = string.Join("；", memories.Select(x =>
                (x.Kind ?? "memory") + "/" + (x.Key ?? "") + "=" + (x.Value ?? "")));
            return Short("可追溯记录：" + text, 800);
        }

        static string ThemeOf(MemoryItem memory)
        {
            if (!string.IsNullOrWhiteSpace(memory.Tag)) return memory.Tag.Trim();
            string key = memory.Key ?? "";
            int colon = key.IndexOf(':');
            if (colon > 0) return key.Substring(0, colon);
            return memory.Kind ?? "";
        }

        static string ComputeId(AutobiographyPeriod period, string periodKey,
            AutobiographyLayer layer, string text, List<AutobiographyEvidence> evidence)
        {
            string canonical = period + "|" + periodKey + "|" + layer + "|" + text + "|" +
                string.Join(",", evidence.Select(x => x.EvidenceId)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            using (SHA256 sha = SHA256.Create())
            {
                string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .ToLowerInvariant();
                return "autobiography_" + hash.Substring(0, 20);
            }
        }

        static string Short(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max) + "...";
        }
    }

    internal static class AutobiographyMemoryTime
    {
        public static DateTimeOffset TimestampUtc(this MemoryItem item)
        {
            DateTimeOffset parsed;
            if (item != null && DateTimeOffset.TryParse(item.Ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out parsed))
                return parsed.ToUniversalTime();
            return DateTimeOffset.UtcNow;
        }
    }
}
