using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AhzScribe.Contracts;

namespace AhzScribe.Private
{
    public sealed class MmyEntry
    {
        public DateTime Timestamp { get; set; }
        public string Tag { get; set; }
        public Dictionary<string, string> Meta { get; set; }
        public string Content { get; set; }
        public MmyEntry() { Meta = new Dictionary<string, string>(); }
    }

    public sealed class MmyParseResult
    {
        public List<MmyEntry> Entries { get; set; }
        public List<string> CorruptedBlocks { get; set; }
        public string ReadError { get; set; }

        public MmyParseResult()
        {
            Entries = new List<MmyEntry>();
            CorruptedBlocks = new List<string>();
            ReadError = "";
        }
    }

    // AhzScribe private memory format. The public repository never receives this file.
    public static class MmyMemory
    {
        public const string FormatVersion = "1.0";
        static readonly Regex Header = new Regex(
            @"^\[(?<ts>[^\]]+)\]\s*\[#(?<tag>[^\]]+)\]\s*(?<meta>(?:\[[^\]]+\]\s*)*)",
            RegexOptions.Compiled);
        static readonly Regex Meta = new Regex(@"\[(?<k>[^:\]]+):(?<v>[^\]]*)\]", RegexOptions.Compiled);
        static readonly object WriteGate = new object();

        public static string MemoryDir(string dataDir) { return Path.Combine(dataDir, "memory"); }

        public static string SemanticPath(string dataDir) { return Path.Combine(MemoryDir(dataDir), "semantic.mmy"); }
        public static string EpisodicPath(string dataDir) { return Path.Combine(MemoryDir(dataDir), "episodic.mmy"); }

        public static List<MmyEntry> ParseFile(string path)
        {
            return ParseDetailed(path).Entries;
        }

        public static MmyParseResult ParseDetailed(string path)
        {
            var result = new MmyParseResult();
            if (!File.Exists(path)) return result;
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex)
            {
                result.ReadError = ex.GetType().Name + ": " + ex.Message;
                return result;
            }

            var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"(?m)^---\s*$");
            foreach (string raw in blocks)
            {
                string block = raw.Trim();
                if (block.Length == 0) continue;
                if (block.StartsWith("# MMY_FORMAT", StringComparison.Ordinal)) continue;
                var e = ParseBlock(block);
                if (e != null) result.Entries.Add(e);
                else result.CorruptedBlocks.Add(block);
            }
            return result;
        }

        static MmyEntry ParseBlock(string block)
        {
            var firstLineEnd = block.IndexOf('\n');
            string first = firstLineEnd >= 0 ? block.Substring(0, firstLineEnd) : block;
            string body = firstLineEnd >= 0 ? block.Substring(firstLineEnd + 1).Trim() : "";
            var m = Header.Match(first);
            var entry = new MmyEntry();
            if (!m.Success)
                return null;
            DateTime ts;
            if (!DateTime.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts))
                ts = DateTime.Now;
            entry.Timestamp = ts;
            entry.Tag = m.Groups["tag"].Value.Trim().ToLowerInvariant();
            foreach (Match mm in Meta.Matches(m.Groups["meta"].Value))
                entry.Meta[mm.Groups["k"].Value.Trim()] = mm.Groups["v"].Value.Trim();
            entry.Content = UnescapeContent(body);
            return entry;
        }

        public static void Append(string path, MmyEntry entry)
        {
            if (entry == null) return;
            try
            {
                lock (WriteGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                    var sb = new StringBuilder();
                    if (isNew)
                    {
                        sb.AppendLine("# MMY_FORMAT: " + FormatVersion);
                        sb.AppendLine("# DEVICE_ORIGIN: " + DeviceId());
                        sb.AppendLine("# LAST_UPDATED: " + DateTimeOffset.Now.ToString("o"));
                        sb.AppendLine();
                    }
                    sb.AppendLine("---");
                    var meta = string.Join(" ", entry.Meta.Select(kv => "[" + kv.Key + ":" + kv.Value + "]"));
                    sb.AppendLine("[" + entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + "] [#" + entry.Tag + "] " + meta);
                    sb.AppendLine(EscapeContent(entry.Content).Trim());
                    sb.AppendLine();
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static string DeviceId()
        {
            try
            {
                string raw = Environment.MachineName + "|" + Environment.OSVersion.VersionString + "|" + Environment.ProcessorCount;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Substring(0, 16).ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        public static void AppendItem(string dataDir, MemoryItem item)
        {
            if (item == null) return;
            string tag = MapTag((item.Kind ?? "").ToLowerInvariant(), item.Status ?? "");
            string path = tag == "epi" || tag == "emotion" ? EpisodicPath(dataDir) : SemanticPath(dataDir);
            DateTimeOffset parsed;
            var e = new MmyEntry
            {
                Timestamp = DateTimeOffset.TryParse(item.Ts, out parsed) ? parsed.LocalDateTime : DateTime.Now,
                Tag = tag,
                Content = !string.IsNullOrEmpty(item.Value) ? item.Value : (item.Summary ?? "")
            };
            if (!string.IsNullOrEmpty(item.Key)) e.Meta["key"] = item.Key;
            if (!string.IsNullOrEmpty(item.Id)) e.Meta["id"] = item.Id;
            if (!string.IsNullOrEmpty(item.Status)) e.Meta["status"] = item.Status;
            if (!string.IsNullOrEmpty(item.SourceAuditId)) e.Meta["audit"] = item.SourceAuditId;
            e.Meta["origin"] = MmyMemory.DeviceId();
            if (item.Provenance != null && item.Provenance.Sources != null && item.Provenance.Sources.Count > 0)
            {
                SourceRef source = item.Provenance.Sources[0];
                if (!string.IsNullOrEmpty(source.SourceId)) e.Meta["source_id"] = source.SourceId;
                e.Meta["source_kind"] = source.Kind.ToString();
                e.Meta["inferred"] = item.Provenance.Inferred ? "true" : "false";
                if (item.Provenance.Confidence.HasValue)
                    e.Meta["confidence"] = item.Provenance.Confidence.Value.ToString(CultureInfo.InvariantCulture);
            }
            Append(path, e);
        }
        public static int MergeDuplicates(string dataDir)
        {
            int merged = 0;
            try
            {
                var all = new List<MmyEntry>();
                foreach (string file in new[] { SemanticPath(dataDir), EpisodicPath(dataDir), Path.Combine(MemoryDir(dataDir), "archive.mmy") })
                    all.AddRange(ParseFile(file));
                var byKey = all.Where(x => x.Meta.ContainsKey("key")).GroupBy(x => x.Meta["key"]);
                foreach (var group in byKey)
                {
                    var values = group.Select(x => x.Content ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (values.Count <= 1) continue;
                    foreach (var e in group) { e.Meta["conflict"] = "true"; merged++; }
                }
                var sem = all.Where(x => x.Tag != "epi").ToList();
                var epi = all.Where(x => x.Tag == "epi").ToList();
                Directory.CreateDirectory(MemoryDir(dataDir));
                File.WriteAllText(SemanticPath(dataDir), HeaderText() + RenderBlocks(sem), new UTF8Encoding(false));
                File.WriteAllText(EpisodicPath(dataDir), HeaderText() + RenderBlocks(epi), new UTF8Encoding(false));
                if (merged > 0) AppendAuditMarker(dataDir, merged);
            }
            catch { }
            return merged;
        }
        public static int Archive(string dataDir, int olderThanDays)
        {
            int moved = 0;
            try
            {
                var keep = new List<MmyEntry>();
                var archive = new List<MmyEntry>();
                DateTime cutoff = DateTime.Now.AddDays(-Math.Max(0, olderThanDays));
                foreach (string file in new[] { SemanticPath(dataDir), EpisodicPath(dataDir) })
                {
                    foreach (var e in ParseFile(file))
                    {
                        if (e.Tag == "epi" && e.Timestamp < cutoff) archive.Add(e);
                        else keep.Add(e);
                    }
                }
                if (archive.Count == 0) return 0;
                string arch = Path.Combine(MemoryDir(dataDir), "archive.mmy");
                var existing = ParseFile(arch);
                existing.AddRange(archive);
                File.WriteAllText(arch, HeaderText() + RenderBlocks(existing), new UTF8Encoding(false));
                var sem = keep.Where(x => x.Tag != "epi").ToList();
                var epi = keep.Where(x => x.Tag == "epi").ToList();
                if (File.Exists(SemanticPath(dataDir))) File.WriteAllText(SemanticPath(dataDir), HeaderText() + RenderBlocks(sem), new UTF8Encoding(false));
                if (File.Exists(EpisodicPath(dataDir))) File.WriteAllText(EpisodicPath(dataDir), HeaderText() + RenderBlocks(epi), new UTF8Encoding(false));
                moved = archive.Count;
            }
            catch { }
            return moved;
        }
        public static void EnsureMigrated(string dataDir)
        {
            try
            {
                string sem = SemanticPath(dataDir);
                string epi = EpisodicPath(dataDir);
                if (File.Exists(sem) || File.Exists(epi)) return;
                MigrateFromJsonl(dataDir);
            }
            catch { }
        }

        public static int MigrateFromJsonl(string dataDir)
        {
            int count = 0;
            try
            {
                string sem = SemanticPath(dataDir);
                string epi = EpisodicPath(dataDir);
                var semantic = new List<MmyEntry>();
                var episodic = new List<MmyEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string file in new[] { "semantic.jsonl", "episodic.jsonl", "disputed.jsonl", "memory.jsonl" })
                {
                    string path = Path.Combine(dataDir, file);
                    if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadLines(path))
                    {
                        try
                        {
                            using (var doc = JsonDocument.Parse(line))
                            {
                                var r = doc.RootElement;
                                string kind = Get(r, "Kind").ToLowerInvariant();
                                string key = Get(r, "Key");
                                string value = Get(r, "Value");
                                string summary = Get(r, "Summary");
                                string id = Get(r, "Id");
                                string status = Get(r, "Status");
                                if (!string.IsNullOrEmpty(id) && !seen.Add(id)) continue;
                                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(summary)) continue;

                                string tag = MapTag(kind, status);
                                var e = new MmyEntry
                                {
                                    Timestamp = ParseTime(Get(r, "Ts")),
                                    Tag = tag,
                                    Content = !string.IsNullOrEmpty(value) ? value : summary
                                };
                                if (!string.IsNullOrEmpty(key)) e.Meta["key"] = key;
                                if (!string.IsNullOrEmpty(id)) e.Meta["id"] = id;
                                if (!string.IsNullOrEmpty(status)) e.Meta["status"] = status;
                                string audit = Get(r, "SourceAuditId");
                                if (!string.IsNullOrEmpty(audit)) e.Meta["audit"] = audit;
                                if (tag == "epi") episodic.Add(e); else semantic.Add(e);
                                count++;
                            }
                        }
                        catch { }
                    }
                }

                if (semantic.Count > 0)
                {
                    Directory.CreateDirectory(MemoryDir(dataDir));
                    File.WriteAllText(sem, HeaderText() + RenderBlocks(semantic), new UTF8Encoding(false));
                }
                if (episodic.Count > 0)
                {
                    Directory.CreateDirectory(MemoryDir(dataDir));
                    File.WriteAllText(epi, HeaderText() + RenderBlocks(episodic), new UTF8Encoding(false));
                }
                if (count > 0) AppendAuditMarker(dataDir, count);
            }
            catch { }
            return count;
        }

        static string HeaderText()
        {
            return "# MMY_FORMAT: " + FormatVersion + "\n" +
                   "# DEVICE_ORIGIN: " + DeviceId() + "\n" +
                   "# LAST_UPDATED: " + DateTimeOffset.Now.ToString("o") + "\n\n";
        }

        static string RenderBlocks(List<MmyEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries.OrderBy(x => x.Timestamp))
            {
                sb.AppendLine("---");
                string meta = string.Join(" ", e.Meta.Select(kv => "[" + kv.Key + ":" + kv.Value + "]"));
                sb.AppendLine("[" + e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + "] [#" + e.Tag + "] " + meta);
                sb.AppendLine(EscapeContent(e.Content).Trim());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string EscapeContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"^---\s*$")) lines[i] = "\\---";
            return string.Join("\n", lines);
        }

        static string UnescapeContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"^\\---\s*$")) lines[i] = "---";
            return string.Join("\n", lines);
        }

        static void AppendAuditMarker(string dataDir, int count)
        {
            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    audit_id = "mmy_migrate_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"),
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    span = "memory",
                    tool = "mmy_migrate",
                    decision = "allowed",
                    result = count + " entries",
                    reason = "jsonl to mmy migration",
                    source = "private",
                    fallback = false
                });
                File.AppendAllText(Path.Combine(dataDir, "audit.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        static string MapTag(string kind, string status)
        {
            if (status == "disputed") return "emotion";
            if (kind == "semantic" || kind == "fact" || kind == "preference" || kind == "identity") return "pref";
            if (kind == "intent") return "intent";
            if (kind == "drive") return "drive";
            if (kind == "system") return "system";
            return "epi";
        }

        static string Get(JsonElement r, string name)
        {
            JsonElement v;
            return r.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        }

        static DateTime ParseTime(string s)
        {
            DateTimeOffset d;
            if (DateTimeOffset.TryParse(s, out d)) return d.LocalDateTime;
            return DateTime.Now;
        }
    }
}
