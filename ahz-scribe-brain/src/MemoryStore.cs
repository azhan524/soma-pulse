using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Brain-side memory store. It never touches the workspace; it only owns its own JSONL files.
    /// S2: semantic / episodic / disputed are separate append-only files; the legacy
    /// memory.jsonl remains readable for migration.
    /// </summary>
    public sealed class MemoryStore : IMemoryStore, IDisposable
    {
        static readonly Regex Word = new Regex(@"[\u4e00-\u9fff]{2,}|[A-Za-z0-9_]{2,}", RegexOptions.Compiled);
        readonly string _path;
        readonly string _semantic;
        readonly string _episodic;
        readonly string _disputed;
        readonly string _lifecycle;
        readonly string _usage;
        readonly HashSet<string> _mmyCorruptionsSeen = new HashSet<string>(StringComparer.Ordinal);
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public MemoryStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
            _path = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(_path);
            _semantic = Path.Combine(dir, "semantic.jsonl");
            _episodic = Path.Combine(dir, "episodic.jsonl");
            _disputed = Path.Combine(dir, "disputed.jsonl");
            _lifecycle = Path.Combine(dir, "memory-lifecycle.jsonl");
            _usage = Path.Combine(dir, "memory-usage.json");
        }

        public async Task<string> RememberAsync(MemoryItem item, CancellationToken ct)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrEmpty(item.Id)) item.Id = "mem_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            if (string.IsNullOrEmpty(item.Ts)) item.Ts = DateTimeOffset.UtcNow.ToString("o");
            if (string.IsNullOrEmpty(item.Status)) item.Status = "active";
            if (string.IsNullOrEmpty(item.OriginDevice)) item.OriginDevice = MmyMemory.DeviceId();
            EnsureProvenance(item);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                List<MemoryItem> all = ReadAllUnsafe();
                MemoryItem sameKey = all.FirstOrDefault(m => m.Status == "active" && !string.IsNullOrEmpty(item.Key) && m.Key == item.Key);
                if (sameKey != null)
                {
                    if (sameKey.Value == item.Value)
                    {
                        DateTimeOffset oldTs;
                        if (DateTimeOffset.TryParse(sameKey.Ts, out oldTs) &&
                            (DateTimeOffset.UtcNow - oldTs).TotalHours < 24 &&
                            HasSource(sameKey, item))
                            return sameKey.Id;
                    }
                    else
                    {
                        item.Status = "disputed";
                        item.DisputedWith = sameKey.Id;
                    }
                }

                string path = PathFor(item);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string line = JsonSerializer.Serialize(item, _json);
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    await w.WriteLineAsync(line).ConfigureAwait(false);
                MmyMemory.AppendItem(Path.GetDirectoryName(_path), item);
                return item.Id;
            }
            finally { _gate.Release(); }

            string PathFor(MemoryItem m)
            {
                string k = (m.Kind ?? "").ToLowerInvariant();
                if (m.Status == "disputed" || k == "disputed") return _disputed;
                if (k == "semantic" || k == "fact" || k == "preference") return _semantic;
                if (k == "episodic" || k == "episode") return _episodic;
                return _path;
            }
        }

        public async Task<List<MemoryItem>> LoadAllAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return ReadAllUnsafe(); }
            finally { _gate.Release(); }
        }

        List<MemoryItem> ReadAllUnsafe()
        {
            var list = new List<MemoryItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bySignature = new Dictionary<string, MemoryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in new[] { _path, _semantic, _episodic, _disputed })
            {
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        MemoryItem m = JsonSerializer.Deserialize<MemoryItem>(line, _json);
                        if (m == null) continue;
                        EnsureProvenance(m);
                        if (!string.IsNullOrEmpty(m.Id) && !seenIds.Add(m.Id)) continue;
                        MemoryItem existing;
                        if (bySignature.TryGetValue(Signature(m.Key, m.Value), out existing))
                        {
                            MergeProvenance(existing, m);
                            continue;
                        }
                        bySignature[Signature(m.Key, m.Value)] = m;
                        list.Add(m);
                    }
                    catch { }
                }
            }
            string memDir = Path.GetDirectoryName(_path);
            foreach (string path in new[] { MmyMemory.SemanticPath(memDir), MmyMemory.EpisodicPath(memDir), Path.Combine(MmyMemory.MemoryDir(memDir), "archive.mmy") })
            {
                MmyParseResult parsed = MmyMemory.ParseDetailed(path);
                if (parsed.CorruptedBlocks.Count > 0 || !string.IsNullOrEmpty(parsed.ReadError))
                    QuarantineCorrupted(memDir, path, parsed);
                foreach (var e in parsed.Entries)
                {
                    string key = e.Meta.ContainsKey("key") ? e.Meta["key"] : "";
                    string value = e.Content ?? "";
                    string id = e.Meta.ContainsKey("id") ? e.Meta["id"] : "";
                    if (!string.IsNullOrEmpty(id) && !seenIds.Add(id)) continue;
                    SourceKind sourceKind;
                    if (!Enum.TryParse<SourceKind>(e.Meta.ContainsKey("source_kind") ? e.Meta["source_kind"] : "", true, out sourceKind))
                        sourceKind = e.Meta.ContainsKey("audit") && !string.IsNullOrEmpty(e.Meta["audit"]) ? SourceKind.Audit : SourceKind.System;
                    double confidence;
                    if (!double.TryParse(e.Meta.ContainsKey("confidence") ? e.Meta["confidence"] : "", out confidence))
                        confidence = 1.0;
                    var parsedItem = new MemoryItem
                    {
                        Id = string.IsNullOrEmpty(id) ? "mmy_" + Guid.NewGuid().ToString("N") : id,
                        Kind = e.Tag == "epi" ? "episodic" : "semantic",
                        Tag = e.Tag,
                        Key = key,
                        Value = value,
                        Summary = value,
                        Status = e.Meta.ContainsKey("status") ? e.Meta["status"] : "active",
                        SourceAuditId = e.Meta.ContainsKey("audit") ? e.Meta["audit"] : "",
                        OriginDevice = e.Meta.ContainsKey("origin") ? e.Meta["origin"] : (e.Meta.ContainsKey("origin_device") ? e.Meta["origin_device"] : ""),
                        Ts = e.Timestamp.ToUniversalTime().ToString("o"),
                        Provenance = new Provenance
                        {
                            Confidence = confidence,
                            DirectlyObserved = !string.Equals(e.Meta.ContainsKey("inferred") ? e.Meta["inferred"] : "", "true", StringComparison.OrdinalIgnoreCase),
                            Inferred = string.Equals(e.Meta.ContainsKey("inferred") ? e.Meta["inferred"] : "", "true", StringComparison.OrdinalIgnoreCase),
                            Sources = new List<SourceRef>
                            {
                                new SourceRef
                                {
                                    SourceId = e.Meta.ContainsKey("source_id") ? e.Meta["source_id"] : (e.Meta.ContainsKey("audit") ? e.Meta["audit"] : "system"),
                                    Kind = sourceKind,
                                    DeviceId = e.Meta.ContainsKey("origin") ? e.Meta["origin"] : "",
                                    AuditId = e.Meta.ContainsKey("audit") ? e.Meta["audit"] : "",
                                    Ts = e.Timestamp.ToUniversalTime().ToString("o"),
                                    Summary = value
                                }
                            }
                        }
                    };
                    MemoryItem existing;
                    if (bySignature.TryGetValue(Signature(key, value), out existing))
                    {
                        MergeProvenance(existing, parsedItem);
                        continue;
                    }
                    bySignature[Signature(key, value)] = parsedItem;
                    list.Add(parsedItem);
                }
            }
            ApplyLifecycleUnsafe(list);
            return list;
        }

        void QuarantineCorrupted(string dataDir, string path, MmyParseResult parsed)
        {
            try
            {
                string marker = path + "|" + (parsed.ReadError ?? "") + "|" +
                    string.Join("\n", parsed.CorruptedBlocks);
                string key;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    key = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
                if (!_mmyCorruptionsSeen.Add(key)) return;

                string memoryDir = MmyMemory.MemoryDir(dataDir);
                Directory.CreateDirectory(memoryDir);
                string quarantine = Path.Combine(memoryDir, "quarantine.jsonl");
                string record = JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    source = path,
                    read_error = parsed.ReadError ?? "",
                    corrupted_blocks = parsed.CorruptedBlocks,
                    action = "skipped_bad_blocks"
                });
                File.AppendAllText(quarantine, record + Environment.NewLine, new UTF8Encoding(false));
                string audit = JsonSerializer.Serialize(new
                {
                    audit_id = "mmy_quarantine_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"),
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    span = "memory",
                    tool = "mmy_parse",
                    decision = "quarantined",
                    result = parsed.CorruptedBlocks.Count + " corrupted blocks",
                    reason = "valid memory blocks preserved",
                    source = "brain",
                    fallback = false
                });
                File.AppendAllText(Path.Combine(dataDir, "audit.jsonl"), audit + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static string Signature(string key, string value)
        {
            return ((key ?? "").Trim() + "|" + (value ?? "").Trim()).ToLowerInvariant();
        }

        static void EnsureProvenance(MemoryItem item)
        {
            if (item == null) return;
            if (item.Provenance == null)
                item.Provenance = MakeProvenance(item.SourceAuditId, item.OriginDevice, item.Summary ?? item.Value, false);
            if (item.Provenance.Sources == null) item.Provenance.Sources = new List<SourceRef>();
        }

        static bool HasSource(MemoryItem existing, MemoryItem incoming)
        {
            if (existing == null || incoming == null) return false;
            EnsureProvenance(existing);
            EnsureProvenance(incoming);
            foreach (SourceRef candidate in incoming.Provenance.Sources)
            {
                if (candidate == null) continue;
                bool found = existing.Provenance.Sources.Any(source =>
                    source != null &&
                    string.Equals(source.SourceId ?? "", candidate.SourceId ?? "",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.AuditId ?? "", candidate.AuditId ?? "",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.DeviceId ?? "", candidate.DeviceId ?? "",
                        StringComparison.OrdinalIgnoreCase));
                if (!found) return false;
            }
            return true;
        }

        static void MergeProvenance(MemoryItem target, MemoryItem incoming)
        {
            if (target == null || incoming == null) return;
            EnsureProvenance(target);
            EnsureProvenance(incoming);
            foreach (SourceRef candidate in incoming.Provenance.Sources)
            {
                if (candidate == null) continue;
                bool found = target.Provenance.Sources.Any(source =>
                    source != null &&
                    string.Equals(source.SourceId ?? "", candidate.SourceId ?? "",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.AuditId ?? "", candidate.AuditId ?? "",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.DeviceId ?? "", candidate.DeviceId ?? "",
                        StringComparison.OrdinalIgnoreCase));
                if (!found) target.Provenance.Sources.Add(candidate);
            }
            if (string.IsNullOrWhiteSpace(target.OriginDevice) &&
                !string.IsNullOrWhiteSpace(incoming.OriginDevice))
                target.OriginDevice = incoming.OriginDevice;
            if (incoming.Provenance.Confidence.HasValue &&
                (!target.Provenance.Confidence.HasValue ||
                 incoming.Provenance.Confidence.Value < target.Provenance.Confidence.Value))
                target.Provenance.Confidence = incoming.Provenance.Confidence;
            target.Provenance.DirectlyObserved =
                target.Provenance.DirectlyObserved || incoming.Provenance.DirectlyObserved;
            target.Provenance.Inferred =
                target.Provenance.Inferred && incoming.Provenance.Inferred;
        }

        public static Provenance MakeProvenance(string auditId, string deviceId, string summary, bool inferred)
        {
            var source = new SourceRef
            {
                SourceId = string.IsNullOrEmpty(auditId) ? "system" : auditId,
                Kind = string.IsNullOrEmpty(auditId) ? SourceKind.System : SourceKind.Audit,
                DeviceId = deviceId ?? "",
                AuditId = auditId ?? "",
                Ts = DateTimeOffset.UtcNow.ToString("o"),
                Summary = summary ?? ""
            };
            return new Provenance
            {
                Sources = new List<SourceRef> { source },
                Confidence = inferred ? 0.6 : 1.0,
                DirectlyObserved = !inferred,
                Inferred = inferred
            };
        }

        public Task<MemoryLifecycleEvent> ArchiveAsync(string memoryId, string reason,
            string auditId, CancellationToken ct)
        {
            return SetLifecycleAsync(memoryId, MemoryLifecycleAction.Archive, reason, auditId, ct);
        }

        public Task<MemoryLifecycleEvent> ForgetAsync(string memoryId, string reason,
            string auditId, CancellationToken ct)
        {
            return SetLifecycleAsync(memoryId, MemoryLifecycleAction.Forget, reason, auditId, ct);
        }

        public Task<MemoryLifecycleEvent> RestoreAsync(string memoryId, string reason,
            string auditId, CancellationToken ct)
        {
            return SetLifecycleAsync(memoryId, MemoryLifecycleAction.Restore, reason, auditId, ct);
        }

        async Task<MemoryLifecycleEvent> SetLifecycleAsync(string memoryId,
            MemoryLifecycleAction action, string reason, string auditId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(memoryId))
                throw new InvalidDataException("memory id is required");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                List<MemoryItem> all = ReadAllUnsafe();
                MemoryItem item = all.FirstOrDefault(x => string.Equals(x.Id, memoryId,
                    StringComparison.OrdinalIgnoreCase));
                if (item == null) throw new InvalidDataException("memory not found: " + memoryId);
                List<MemoryLifecycleEvent> history = ReadLifecycleUnsafe();
                MemoryLifecycleEvent latest = history.LastOrDefault(x =>
                    string.Equals(x.MemoryId, memoryId, StringComparison.OrdinalIgnoreCase));
                string previousStatus = item.Status ?? "active";
                string newStatus;
                if (action == MemoryLifecycleAction.Restore)
                {
                    MemoryLifecycleEvent original = history.LastOrDefault(x =>
                        string.Equals(x.MemoryId, memoryId, StringComparison.OrdinalIgnoreCase) &&
                        (x.Action == MemoryLifecycleAction.Archive ||
                         x.Action == MemoryLifecycleAction.Forget ||
                         x.Action == MemoryLifecycleAction.Downgrade));
                    newStatus = original == null || string.IsNullOrWhiteSpace(original.PreviousStatus)
                        ? "active"
                        : original.PreviousStatus;
                }
                else
                {
                    newStatus = action == MemoryLifecycleAction.Archive ? "archived" :
                        action == MemoryLifecycleAction.Forget ? "forgotten" :
                        action == MemoryLifecycleAction.Downgrade ? "downgraded" :
                        previousStatus;
                }
                var record = new MemoryLifecycleEvent
                {
                    EventId = "memlife_" + Guid.NewGuid().ToString("N"),
                    MemoryId = memoryId,
                    Action = action,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    Reason = reason ?? "",
                    SourceAuditId = auditId ?? "",
                    Ts = DateTimeOffset.UtcNow.ToString("o")
                };
                record.Sources.Add(new SourceRef
                {
                    SourceId = "memory_lifecycle",
                    Kind = string.IsNullOrWhiteSpace(auditId) ? SourceKind.System : SourceKind.Audit,
                    AuditId = auditId ?? "",
                    Ts = record.Ts,
                    Summary = action + " " + memoryId + ": " + (reason ?? "")
                });
                AppendLifecycleUnsafe(record);
                AppendMemoryAuditUnsafe("memory_" + action.ToString().ToLowerInvariant(),
                    "allowed", memoryId, reason, auditId);
                return record;
            }
            finally { _gate.Release(); }
        }

        public async Task<MemoryConsolidationResult> ConsolidateAsync(
            DateTimeOffset now, int archiveOlderThanDays, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = new MemoryConsolidationResult
                {
                    RunId = "consolidate_" + now.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff"),
                    Ts = now.ToString("o")
                };
                List<MemoryItem> all = ReadAllUnsafe();
                List<MemoryLifecycleEvent> history = ReadLifecycleUnsafe();
                HashSet<string> reinforced = new HashSet<string>(history
                    .Where(x => x.Action == MemoryLifecycleAction.Reinforce)
                    .Select(x => x.MemoryId), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, MemoryUsageRecord> usage = ReadUsageUnsafe();
                foreach (MemoryItem item in all)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                    if (item.Status == "forgotten" || item.Status == "deleted")
                    {
                        result.ForgottenSkipped++;
                        continue;
                    }
                    if (item.Status == "disputed")
                    {
                        result.ConflictsPreserved++;
                        continue;
                    }
                    MemoryUsageRecord count;
                    usage.TryGetValue(item.Id, out count);
                    if (count != null && count.Count >= 3 && reinforced.Add(item.Id))
                    {
                        AppendLifecycleUnsafe(NewLifecycle(item, MemoryLifecycleAction.Reinforce,
                            item.Status, item.Status, "retrieved at least three times", ""));
                        result.Reinforced++;
                    }
                    DateTimeOffset ts;
                    int ageDays = DateTimeOffset.TryParse(item.Ts, out ts)
                        ? (int)Math.Max(0, (now - ts).TotalDays)
                        : 0;
                    if ((item.Kind == "episodic" || item.Kind == "episode") &&
                        ageDays >= Math.Max(1, archiveOlderThanDays) &&
                        item.Status == "active" && !IsImportant(item))
                    {
                        AppendLifecycleUnsafe(NewLifecycle(item, MemoryLifecycleAction.Archive,
                            "active", "archived", "old low-value episodic memory", ""));
                        result.Archived++;
                        continue;
                    }
                    bool noDirectEvidence = item.Provenance != null &&
                        item.Provenance.Inferred &&
                        (item.Provenance.Sources == null ||
                         !item.Provenance.Sources.Any(x => x != null &&
                            x.Kind != SourceKind.Model));
                    if (item.Status == "active" && noDirectEvidence)
                    {
                        AppendLifecycleUnsafe(NewLifecycle(item, MemoryLifecycleAction.Downgrade,
                            "active", "downgraded",
                            "inferred memory has no non-model evidence", ""));
                        result.Downgraded++;
                    }
                }
                AppendMemoryAuditUnsafe("memory_consolidate", "allowed", result.RunId,
                    "archived=" + result.Archived + " downgraded=" + result.Downgraded +
                    " reinforced=" + result.Reinforced +
                    " conflicts_preserved=" + result.ConflictsPreserved,
                    "");
                return result;
            }
            finally { _gate.Release(); }
        }

        public List<MemoryLifecycleEvent> LoadLifecycle()
        {
            _gate.Wait();
            try
            {
                return ReadLifecycleUnsafe()
                    .OrderBy(x => x.Ts, StringComparer.Ordinal)
                    .ToList();
            }
            finally { _gate.Release(); }
        }

        public async Task<List<MemoryItem>> RetrieveAsync(string query, int topK, CancellationToken ct)
        {
            List<MemoryItem> all = await LoadAllAsync(ct).ConfigureAwait(false);
            if (topK <= 0) topK = 5;
            HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTokens(words, query);
            Dictionary<string, MemoryUsageRecord> usage = ReadUsageUnsafe();

            var scored = new List<KeyValuePair<MemoryItem, double>>();
            foreach (MemoryItem m in all)
            {
                if (m == null || m.Status == "deleted" || m.Status == "forgotten") continue;
                string text = (m.Key ?? "") + " " + (m.Value ?? "") + " " + (m.Summary ?? "");
                var mt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddTokens(mt, text);
                int score = 0;
                foreach (string w in mt) if (words.Contains(w)) score++;
                if (score == 0) continue;
                double days = 30;
                DateTimeOffset ts;
                if (DateTimeOffset.TryParse(m.Ts, out ts)) days = Math.Max(0, (DateTimeOffset.UtcNow - ts).TotalDays);
                double decay = 1.0 / (1.0 + days / 30.0);
                double importance = IsImportant(m) ? 3.0 : 0.0;
                double status = m.Status == "archived" ? -12.0 :
                    (m.Status == "downgraded" ? -3.0 : 0.0);
                double inference = m.Provenance != null && m.Provenance.Inferred ? -2.0 : 0.0;
                MemoryUsageRecord used;
                usage.TryGetValue(m.Id ?? "", out used);
                double frequency = Math.Log(1.0 + (used == null ? 0 : used.Count), 2.0);
                scored.Add(new KeyValuePair<MemoryItem, double>(m,
                    score * 10.0 + decay + importance + status + inference + frequency));
            }
            List<MemoryItem> selected = scored.OrderByDescending(x => x.Value)
                .Take(topK).Select(x => x.Key).ToList();
            await RecordUsageAsync(selected, ct).ConfigureAwait(false);
            return selected;
        }

        static void AddTokens(HashSet<string> set, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Word.Matches(text))
            {
                string s = m.Value;
                set.Add(s);
                if (s.Length >= 4)
                    for (int i = 0; i + 2 <= s.Length; i++) set.Add(s.Substring(i, 2));
            }
        }

        void ApplyLifecycleUnsafe(List<MemoryItem> items)
        {
            if (items == null || items.Count == 0) return;
            List<MemoryLifecycleEvent> lifecycle = ReadLifecycleUnsafe();
            Dictionary<string, MemoryLifecycleEvent> latest = lifecycle
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MemoryId))
                .GroupBy(x => x.MemoryId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key,
                    x => x.OrderBy(y => y.Ts, StringComparer.Ordinal).Last(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (MemoryItem item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                MemoryLifecycleEvent record;
                if (latest.TryGetValue(item.Id, out record))
                    item.Status = string.IsNullOrWhiteSpace(record.NewStatus)
                        ? item.Status
                        : record.NewStatus;
            }
        }

        List<MemoryLifecycleEvent> ReadLifecycleUnsafe()
        {
            var result = new List<MemoryLifecycleEvent>();
            if (!File.Exists(_lifecycle)) return result;
            foreach (string line in File.ReadLines(_lifecycle, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    MemoryLifecycleEvent record = JsonSerializer.Deserialize<MemoryLifecycleEvent>(
                        line, _json);
                    if (record != null) result.Add(record);
                }
                catch { }
            }
            return result;
        }

        static MemoryLifecycleEvent NewLifecycle(MemoryItem item, MemoryLifecycleAction action,
            string previousStatus, string newStatus, string reason, string auditId)
        {
            return new MemoryLifecycleEvent
            {
                EventId = "memlife_" + Guid.NewGuid().ToString("N"),
                MemoryId = item == null ? "" : (item.Id ?? ""),
                Action = action,
                PreviousStatus = previousStatus ?? "",
                NewStatus = newStatus ?? "",
                Reason = reason ?? "",
                SourceAuditId = auditId ?? "",
                Ts = DateTimeOffset.UtcNow.ToString("o"),
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = "memory_consolidation",
                        Kind = SourceKind.System,
                        AuditId = auditId ?? "",
                        Ts = DateTimeOffset.UtcNow.ToString("o"),
                        Summary = reason ?? ""
                    }
                }
            };
        }

        void AppendLifecycleUnsafe(MemoryLifecycleEvent record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lifecycle));
            File.AppendAllText(_lifecycle, JsonSerializer.Serialize(record, _json) +
                Environment.NewLine, new UTF8Encoding(false));
        }

        void AppendMemoryAuditUnsafe(string tool, string decision, string result,
            string reason, string linkedAuditId)
        {
            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    audit_id = "memory_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                        Guid.NewGuid().ToString("N").Substring(0, 6),
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    span = "memory",
                    tool,
                    decision,
                    result,
                    reason,
                    linked_audit_id = linkedAuditId ?? "",
                    source = "brain",
                    fallback = false
                });
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(_path), "audit.jsonl"),
                    line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        async Task RecordUsageAsync(List<MemoryItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0) return;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Dictionary<string, MemoryUsageRecord> usage = ReadUsageUnsafe();
                string now = DateTimeOffset.UtcNow.ToString("o");
                foreach (MemoryItem item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                    MemoryUsageRecord record;
                    if (!usage.TryGetValue(item.Id, out record) || record == null)
                        record = new MemoryUsageRecord();
                    record.Count++;
                    record.LastAt = now;
                    usage[item.Id] = record;
                }
                string temp = _usage + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(usage,
                    new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                if (File.Exists(_usage))
                {
                    try { File.Replace(temp, _usage, null); }
                    catch { File.Copy(temp, _usage, true); File.Delete(temp); }
                }
                else File.Move(temp, _usage);
            }
            catch { }
            finally { _gate.Release(); }
        }

        Dictionary<string, MemoryUsageRecord> ReadUsageUnsafe()
        {
            try
            {
                return File.Exists(_usage)
                    ? JsonSerializer.Deserialize<Dictionary<string, MemoryUsageRecord>>(
                        File.ReadAllText(_usage, Encoding.UTF8), _json) ??
                        new Dictionary<string, MemoryUsageRecord>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, MemoryUsageRecord>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, MemoryUsageRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        static bool IsImportant(MemoryItem item)
        {
            if (item == null) return false;
            string kind = (item.Kind ?? "").ToLowerInvariant();
            if (kind == "semantic" || kind == "fact" || kind == "preference" ||
                kind == "identity" || kind == "intent")
                return true;
            string text = ((item.Key ?? "") + " " + (item.Value ?? "") + " " +
                (item.Summary ?? "") + " " + (item.Tag ?? "")).ToLowerInvariant();
            return text.Contains("relationship") || text.Contains("关系") ||
                text.Contains("承诺") || text.Contains("promise") ||
                text.Contains("identity") || text.Contains("身份") ||
                text.Contains("boundary") || text.Contains("边界") ||
                text.Contains("安全") || text.Contains("权限");
        }

        sealed class MemoryUsageRecord
        {
            public int Count { get; set; }
            public string LastAt { get; set; }
        }

        public void Dispose() { _gate.Dispose(); }
    }
}
