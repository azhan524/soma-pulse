using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using AhzScribe.Brain;
using AhzScribe.Contracts;

namespace AhzScribe.Private
{
    // Private soul backup / migration module. Not part of the public release.
    public static class SoulBackup
    {
        public const int SchemaVersion = 1;
        sealed class MigrationJournal
        {
            public string Status { get; set; }
            public string StartedAt { get; set; }
            public string Workspace { get; set; }
            public string BackupZip { get; set; }
            public string SourceDeviceId { get; set; }
            public string TargetDeviceId { get; set; }
            public string SourceSoulId { get; set; }
            public List<string> CreatedFiles { get; set; }

            public MigrationJournal()
            {
                CreatedFiles = new List<string>();
            }
        }

        static readonly string[] CoreFiles =
        {
            "sessions.jsonl", "memory.jsonl", "semantic.jsonl", "episodic.jsonl", "disputed.jsonl",
            "current_state.json", "settings.json", "body_state.json",
            "identity-manifest.json", "identity-invariants.sha256", "autobiography.jsonl",
            "attention.jsonl", "memory-lifecycle.jsonl", "memory-usage.json"
            , "goals.jsonl"
        };

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

        public static string Export(string dataDir, string workspace, string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath)) throw new ArgumentException("zipPath required");
            string temp = Path.Combine(Path.GetTempPath(), "ahz-soul-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string sourceDevice = DeviceId();
                IdentityManifest identity = new IdentityManifestStore(dataDir).Load();
                if (identity != null && !string.IsNullOrWhiteSpace(identity.CurrentDeviceId))
                    sourceDevice = identity.CurrentDeviceId;
                var manifest = new Dictionary<string, object>
                {
                    ["schema_version"] = SchemaVersion,
                    ["mmy_format"] = MmyMemory.FormatVersion,
                    ["device_id"] = sourceDevice,
                    ["soul_id"] = identity == null ? "" : (identity.SoulId ?? ""),
                    ["created_at"] = DateTimeOffset.Now.ToString("o"),
                    ["files"] = new List<string>()
                };
                var files = (List<string>)manifest["files"];
                foreach (string name in CoreFiles)
                {
                    string src = Path.Combine(dataDir, name);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(temp, name), true);
                    files.Add(name);
                }
                if (!string.IsNullOrWhiteSpace(workspace))
                {
                    foreach (string name in new[] { "SOUL.md", "STYLE.md", "BOUNDARIES.md" })
                    {
                        string src = Path.Combine(workspace, name);
                        if (File.Exists(src)) { File.Copy(src, Path.Combine(temp, name), true); files.Add(name); }
                    }
                }
                string mem = MmyMemory.MemoryDir(dataDir);
                if (Directory.Exists(mem))
                {
                    foreach (string f in Directory.GetFiles(mem, "*.mmy"))
                    {
                        string rel = Path.Combine("memory", Path.GetFileName(f));
                        Directory.CreateDirectory(Path.Combine(temp, "memory"));
                        File.Copy(f, Path.Combine(temp, rel), true);
                        files.Add(rel);
                    }
                }
                File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(temp, zipPath, CompressionLevel.Optimal, false);
                return zipPath;
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        public static string Import(string dataDir, string workspace, string zipPath, out string backupPath)
        {
            return ImportWithObserver(dataDir, workspace, zipPath, null, out backupPath);
        }

        internal static string ImportWithObserver(string dataDir, string workspace, string zipPath,
            Action<int> afterCopy, out string backupPath)
        {
            backupPath = null;
            if (!File.Exists(zipPath)) throw new FileNotFoundException(zipPath);
            Directory.CreateDirectory(dataDir);
            string temp = Path.Combine(Path.GetTempPath(), "ahz-soul-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                ExtractZipSafe(zipPath, temp);
                string manifestPath = Path.Combine(temp, "manifest.json");
                if (!File.Exists(manifestPath)) throw new InvalidDataException("manifest.json missing");
                int schema;
                string sourceDevice;
                string sourceSoul;
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    JsonElement root = doc.RootElement;
                    schema = root.TryGetProperty("schema_version", out var sv) ? sv.GetInt32() : 0;
                    sourceDevice = root.TryGetProperty("device_id", out var dv) && dv.ValueKind == JsonValueKind.String
                        ? (dv.GetString() ?? "")
                        : "";
                    sourceSoul = root.TryGetProperty("soul_id", out var sid) && sid.ValueKind == JsonValueKind.String
                        ? (sid.GetString() ?? "")
                        : "";
                }
                if (schema <= 0 || schema > SchemaVersion)
                    throw new InvalidDataException("unsupported soul schema");

                string tempIdentityPath = Path.Combine(temp, "identity-manifest.json");
                string tempDigestPath = Path.Combine(temp, "identity-invariants.sha256");
                if (File.Exists(tempIdentityPath) || File.Exists(tempDigestPath))
                {
                    if (!File.Exists(tempIdentityPath) || !File.Exists(tempDigestPath))
                        throw new InvalidDataException("incomplete identity manifest");
                    IdentityManifest imported = new IdentityManifestStore(temp).Load();
                    if (string.IsNullOrWhiteSpace(sourceSoul) && imported != null) sourceSoul = imported.SoulId;
                    IdentityVerification verification = new IdentityManifestStore(temp).Verify(imported, sourceSoul);
                    if (!verification.Valid)
                        throw new InvalidDataException("identity invariant verification failed: " +
                            string.Join("; ", verification.Errors));
                    if (string.IsNullOrWhiteSpace(sourceDevice) && imported != null)
                        sourceDevice = imported.CurrentDeviceId;
                }

                backupPath = Path.Combine(dataDir, "soul_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                Export(dataDir, workspace, backupPath);

                var journal = new MigrationJournal
                {
                    Status = "applying",
                    StartedAt = DateTimeOffset.Now.ToString("o"),
                    Workspace = workspace ?? "",
                    BackupZip = backupPath,
                    SourceDeviceId = sourceDevice ?? "",
                    TargetDeviceId = DeviceId(),
                    SourceSoulId = sourceSoul ?? ""
                };
                string journalPath = Path.Combine(dataDir, "migration_journal.json");
                WriteJournal(journalPath, journal);
                int copyCount = 0;
                try
                {
                    foreach (string name in CoreFiles)
                    {
                        string src = Path.Combine(temp, name);
                        if (!File.Exists(src)) continue;
                        CopyWithJournal(src, Path.Combine(dataDir, name), journal, journalPath);
                        copyCount++;
                        if (afterCopy != null) afterCopy(copyCount);
                    }
                    string mem = Path.Combine(temp, "memory");
                    if (Directory.Exists(mem))
                    {
                        Directory.CreateDirectory(MmyMemory.MemoryDir(dataDir));
                        foreach (string src in Directory.GetFiles(mem, "*.mmy"))
                        {
                            CopyWithJournal(src, Path.Combine(MmyMemory.MemoryDir(dataDir),
                                Path.GetFileName(src)), journal, journalPath);
                            copyCount++;
                            if (afterCopy != null) afterCopy(copyCount);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(workspace))
                    {
                        foreach (string name in new[] { "SOUL.md", "STYLE.md", "BOUNDARIES.md" })
                        {
                            string src = Path.Combine(temp, name);
                            if (!File.Exists(src)) continue;
                            CopyWithJournal(src, Path.Combine(workspace, name), journal, journalPath);
                            copyCount++;
                            if (afterCopy != null) afterCopy(copyCount);
                        }
                    }

                    var pending = new
                    {
                        imported_at = DateTimeOffset.Now.ToString("o"),
                        source_device_id = sourceDevice ?? "",
                        target_device_id = DeviceId(),
                        source_soul_id = sourceSoul ?? "",
                        old_device_id = sourceDevice ?? "",
                        zip = Path.GetFileName(zipPath),
                        backup_zip = Path.GetFileName(backupPath),
                        schema_version = schema,
                        status = "pending_restart"
                    };
                    File.WriteAllText(Path.Combine(dataDir, "migration_pending.json"),
                        JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                    try { File.Delete(journalPath); } catch { }
                }
                catch (Exception importEx)
                {
                    string rollbackError;
                    bool rolledBack = RestoreFromJournal(dataDir, workspace, journal, out rollbackError);
                    if (rolledBack) { try { File.Delete(journalPath); } catch { } }
                    throw new InvalidDataException("soul import interrupted; rolled_back=" + rolledBack +
                        (rolledBack ? "" : "; recovery_error=" + rollbackError) +
                        "; cause=" + importEx.Message, importEx);
                }

                AppendAudit(dataDir, "soul_import", "allowed", Path.GetFileName(zipPath),
                    "soul backup imported transactionally");
                return "已导入灵魂备份；原记忆已备份到 " + backupPath;
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        static void ExtractZipSafe(string zipPath, string targetRoot)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
                    string relative = Path.GetRelativePath(targetRoot, target);
                    if (Path.IsPathRooted(relative) ||
                        string.Equals(relative, "..", StringComparison.Ordinal) ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("unsafe zip path");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        static void WriteJournal(string path, MigrationJournal journal)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(journal,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        static void CopyWithJournal(string source, string target, MigrationJournal journal, string journalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target))
            {
                journal.CreatedFiles.Add(target);
                WriteJournal(journalPath, journal);
            }
            File.Copy(source, target, true);
        }

        static bool RestoreFromJournal(string dataDir, string workspace, MigrationJournal journal,
            out string error)
        {
            error = "";
            if (journal == null || string.IsNullOrWhiteSpace(journal.BackupZip) ||
                !File.Exists(journal.BackupZip))
            {
                error = "rollback backup missing";
                return false;
            }
            string temp = Path.Combine(Path.GetTempPath(), "ahz-soul-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                ExtractZipSafe(journal.BackupZip, temp);
                foreach (string name in CoreFiles)
                {
                    string src = Path.Combine(temp, name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dataDir, name), true);
                }
                string mem = Path.Combine(temp, "memory");
                if (Directory.Exists(mem))
                {
                    Directory.CreateDirectory(MmyMemory.MemoryDir(dataDir));
                    foreach (string src in Directory.GetFiles(mem, "*.mmy"))
                        File.Copy(src, Path.Combine(MmyMemory.MemoryDir(dataDir), Path.GetFileName(src)), true);
                }
                string targetWorkspace = string.IsNullOrWhiteSpace(workspace) ? (journal.Workspace ?? "") : workspace;
                if (!string.IsNullOrWhiteSpace(targetWorkspace))
                {
                    foreach (string name in new[] { "SOUL.md", "STYLE.md", "BOUNDARIES.md" })
                    {
                        string src = Path.Combine(temp, name);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(targetWorkspace, name), true);
                    }
                }
                foreach (string created in journal.CreatedFiles ?? new List<string>())
                {
                    string full = Path.GetFullPath(created);
                    if (!IsUnder(full, dataDir) && !IsUnder(full, targetWorkspace)) continue;
                    try { if (File.Exists(full)) File.Delete(full); } catch { }
                }
                try { File.Delete(Path.Combine(dataDir, "migration_pending.json")); } catch { }
                AppendAudit(dataDir, "soul_import_rollback", "allowed", journal.BackupZip,
                    "interrupted soul import restored from backup");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        public static bool RecoverInterruptedImport(string dataDir, string workspace = "")
        {
            string journalPath = Path.Combine(dataDir, "migration_journal.json");
            if (!File.Exists(journalPath)) return false;
            try
            {
                MigrationJournal journal = JsonSerializer.Deserialize<MigrationJournal>(
                    File.ReadAllText(journalPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                string error;
                bool restored = RestoreFromJournal(dataDir, workspace, journal, out error);
                if (restored) { try { File.Delete(journalPath); } catch { } }
                else AppendAudit(dataDir, "soul_import_recovery", "blocked", error,
                    "interrupted soul import could not be restored");
                return restored;
            }
            catch (Exception ex)
            {
                AppendAudit(dataDir, "soul_import_recovery", "blocked", ex.GetType().Name + ": " + ex.Message,
                    "migration journal unreadable");
                return false;
            }
        }

        static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        static void AppendAudit(string dataDir, string tool, string decision, string result, string reason)
        {
            try
            {
                File.AppendAllText(Path.Combine(dataDir, "audit.jsonl"), JsonSerializer.Serialize(new
                {
                    audit_id = tool + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"),
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    span = "system",
                    tool,
                    decision,
                    result,
                    reason,
                    source = "private",
                    fallback = false
                }) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public static string SelfTest()
        {
            string a = Path.Combine(Path.GetTempPath(), "ahz-soul-a-" + Guid.NewGuid().ToString("N"));
            string b = Path.Combine(Path.GetTempPath(), "ahz-soul-b-" + Guid.NewGuid().ToString("N"));
            string zip = Path.Combine(Path.GetTempPath(), "ahz-soul-" + Guid.NewGuid().ToString("N") + ".zip");
            string badZip = Path.Combine(Path.GetTempPath(), "ahz-soul-bad-" + Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            try
            {
                File.WriteAllText(Path.Combine(a, "sessions.jsonl"), "{\"Role\":\"你\",\"Text\":\"test\"}\n");
                File.WriteAllText(Path.Combine(a, "memory.jsonl"), "{\"Id\":\"m1\",\"Kind\":\"semantic\",\"Key\":\"k\",\"Value\":\"v\"}\n");
                File.WriteAllText(Path.Combine(a, "current_state.json"), "{\"CurrentFocus\":\"test\"}");
                File.WriteAllText(Path.Combine(a, "autobiography.jsonl"),
                    "{\"EntryId\":\"autobiography_test\",\"Layer\":\"Fact\",\"Text\":\"backup test\",\"Evidence\":[]}\n");
                File.WriteAllText(Path.Combine(a, "attention.jsonl"),
                    "{\"AttentionId\":\"attention_test\",\"Subject\":\"backup\",\"Status\":\"Monitoring\",\"EvidenceIds\":[],\"Sources\":[]}\n");
                File.WriteAllText(Path.Combine(a, "memory-lifecycle.jsonl"),
                    "{\"EventId\":\"lifecycle_test\",\"MemoryId\":\"m1\",\"Action\":\"Archive\",\"PreviousStatus\":\"active\",\"NewStatus\":\"archived\"}\n");
                File.WriteAllText(Path.Combine(a, "memory-usage.json"), "{\"m1\":{\"Count\":1}}");
                File.WriteAllText(Path.Combine(a, "goals.jsonl"),
                    "{\"GoalId\":\"goal_test\",\"Title\":\"backup\",\"Status\":\"Candidate\",\"Sources\":[],\"EvidenceIds\":[]}\n");
                var identity = new IdentityManifestStore(a);
                identity.LoadOrCreate("soul_backup_test", "device-a", "model-a", "interactive",
                    PermissionTier.Full);
                Directory.CreateDirectory(Path.Combine(a, "memory"));
                File.WriteAllText(Path.Combine(a, "memory", "semantic.mmy"), "# MMY_FORMAT: 1.0\n\n---\n[2026-10-27 10:00:00] [#pref] [key:k]\nv\n");
                Export(a, null, zip);
                string backup;
                Import(b, null, zip, out backup);
                using (var bad = ZipFile.Open(badZip, ZipArchiveMode.Create))
                {
                    var entry = bad.CreateEntry("../escape.txt");
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write("bad");
                }
                bool unsafeBlocked = false;
                try
                {
                    string ignoredBackup;
                    Import(b, null, badZip, out ignoredBackup);
                }
                catch (InvalidDataException) { unsafeBlocked = true; }
                bool ok = File.Exists(Path.Combine(b, "sessions.jsonl")) &&
                          File.Exists(Path.Combine(b, "memory", "semantic.mmy")) &&
                          File.Exists(Path.Combine(b, "identity-manifest.json")) &&
                          File.Exists(Path.Combine(b, "identity-invariants.sha256")) &&
                          File.Exists(Path.Combine(b, "autobiography.jsonl")) &&
                          File.Exists(Path.Combine(b, "attention.jsonl")) &&
                          File.Exists(Path.Combine(b, "memory-lifecycle.jsonl")) &&
                          File.Exists(Path.Combine(b, "memory-usage.json")) &&
                          File.Exists(Path.Combine(b, "goals.jsonl")) &&
                          File.Exists(Path.Combine(b, "migration_pending.json")) &&
                          unsafeBlocked;
                return "soul_roundtrip=" + ok +
                    " autobiography=" + File.Exists(Path.Combine(b, "autobiography.jsonl")) +
                    " attention=" + File.Exists(Path.Combine(b, "attention.jsonl")) +
                    " lifecycle=" + File.Exists(Path.Combine(b, "memory-lifecycle.jsonl")) +
                    " usage=" + File.Exists(Path.Combine(b, "memory-usage.json")) +
                    " goals=" + File.Exists(Path.Combine(b, "goals.jsonl")) +
                    " backup=" + File.Exists(backup) +
                    " pending=" + File.Exists(Path.Combine(b, "migration_pending.json")) +
                    " unsafe_zip_blocked=" + unsafeBlocked;
            }
            finally
            {
                try { Directory.Delete(a, true); } catch { }
                try { Directory.Delete(b, true); } catch { }
                try { File.Delete(zip); } catch { }
                try { File.Delete(badZip); } catch { }
            }
        }
        public static int MigrateToCurrent(string dataDir, int fromVersion)
        {
            if (fromVersion >= SchemaVersion) return 0;
            int changed = 0;
            foreach (string name in new[] { "memory.jsonl", "semantic.jsonl", "episodic.jsonl", "disputed.jsonl" })
            {
                string path = Path.Combine(dataDir, name);
                if (!File.Exists(path)) continue;
                var lines = new List<string>();
                foreach (string line in File.ReadLines(path))
                {
                    string current = line;
                    try
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(current) as System.Text.Json.Nodes.JsonObject;
                        if (node != null && !node.ContainsKey("OriginDevice"))
                        {
                            node["OriginDevice"] = DeviceId();
                            current = node.ToJsonString();
                            changed++;
                        }
                    }
                    catch { }
                    lines.Add(current);
                }
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            return changed;
        }

        public static string AdoptPendingSoulId(string dataDir)
        {
            string pendingPath = Path.Combine(dataDir, "migration_pending.json");
            if (!File.Exists(pendingPath)) return "";
            try
            {
                string soul = "";
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(pendingPath)))
                {
                    if (doc.RootElement.TryGetProperty("source_soul_id", out JsonElement value) &&
                        value.ValueKind == JsonValueKind.String)
                        soul = value.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(soul)) return "";
                File.WriteAllText(Path.Combine(dataDir, "soul_id"), soul,
                    new UTF8Encoding(false));
                AppendAudit(dataDir, "soul_identity_adopt", "allowed", soul,
                    "pending soul import carries the migrating identity");
                return soul;
            }
            catch (Exception ex)
            {
                AppendAudit(dataDir, "soul_identity_adopt", "blocked",
                    ex.GetType().Name + ": " + ex.Message,
                    "pending soul identity could not be read");
                return "";
            }
        }

        public static string ConsumePending(string dataDir)
        {
            string path = Path.Combine(dataDir, "migration_pending.json");
            if (!File.Exists(path)) return "";
            try
            {
                string text = File.ReadAllText(path);
                File.Delete(path);
                using (var doc = JsonDocument.Parse(text))
                {
                    string imported = doc.RootElement.TryGetProperty("imported_at", out var t) ? t.GetString() : "";
                    return "检测到灵魂导入（" + imported + "），状态已接续。";
                }
            }
            catch { return "检测到灵魂导入，状态已接续。"; }
        }
    }
}
