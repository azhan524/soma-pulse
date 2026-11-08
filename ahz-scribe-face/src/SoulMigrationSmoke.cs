using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    internal static class SoulMigrationSmoke
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "ahz-soul-migration-" + Guid.NewGuid().ToString("N"));
            string sourceDir = Path.Combine(root, "source");
            string targetDir = Path.Combine(root, "target");
            string zip = Path.Combine(root, "source-soul.zip");
            string highZip = Path.Combine(root, "high-schema.zip");
            string recoveryZip = Path.Combine(root, "target-recovery.zip");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            try
            {
                string sourceSoul = "soul_source_device";
                string sourceDevice = "source-device-a";
                string targetSoul = "soul_target_device";
                string sourceSession = Path.Combine(sourceDir, "sessions.jsonl");
                string targetSession = Path.Combine(targetDir, "sessions.jsonl");
                FaceSession.Append(sourceSession, new FaceMessage { Role = "你", Text = "源设备时间线", Kind = "chat" });
                OpenIntent sourceIntent = FaceSession.AddIntent(sourceSession, "迁移后继续完成项目", "");
                FaceSession.SetFocus(sourceSession, "源设备焦点");
                FaceState sourceState = FaceSession.LoadState(sourceSession);
                sourceState.DeviceId = sourceDevice;
                FaceSession.SaveState(sourceSession, sourceState);

                FaceSession.Append(targetSession, new FaceMessage { Role = "你", Text = "目标设备原有时间线", Kind = "chat" });
                FaceSession.SetFocus(targetSession, "目标设备原焦点");

                var sourceIdentity = new IdentityManifestStore(sourceDir);
                sourceIdentity.LoadOrCreate(sourceSoul, sourceDevice, "source-model",
                    "interactive", PermissionTier.Full);
                string sourceDigest = File.ReadAllText(sourceIdentity.DigestPath).Trim();
                using (var sourceMemory = new MemoryStore(Path.Combine(sourceDir, "memory.jsonl")))
                {
                    sourceMemory.RememberAsync(new MemoryItem
                    {
                        Kind = "semantic",
                        Key = "relationship:user",
                        Value = "用户与个体共同经历过的长期关系",
                        Summary = "核心关系",
                        OriginDevice = sourceDevice
                    }, CancellationToken.None).GetAwaiter().GetResult();
                }

                var targetIdentity = new IdentityManifestStore(targetDir);
                targetIdentity.LoadOrCreate(targetSoul, "target-device-b", "target-model",
                    "interactive", PermissionTier.Full);
                using (var targetMemory = new MemoryStore(Path.Combine(targetDir, "memory.jsonl")))
                {
                    targetMemory.RememberAsync(new MemoryItem
                    {
                        Kind = "episodic",
                        Key = "target:only",
                        Value = "只存在于新设备的情节记忆",
                        Summary = "目标设备原有记忆",
                        OriginDevice = "target-device-b"
                    }, CancellationToken.None).GetAwaiter().GetResult();
                }

                SoulBackup.Export(sourceDir, null, zip);
                CreateHighVersionZip(zip, highZip);

                bool highSchemaBlocked = false;
                try
                {
                    string ignored;
                    SoulBackup.Import(targetDir, null, highZip, out ignored);
                }
                catch (InvalidDataException) { highSchemaBlocked = true; }
                IdentityManifest afterHigh = new IdentityManifestStore(targetDir).Load();
                bool highNoMutation = highSchemaBlocked && afterHigh != null &&
                    afterHigh.SoulId == targetSoul &&
                    !File.Exists(Path.Combine(targetDir, "migration_pending.json"));

                SoulBackup.Export(targetDir, null, recoveryZip);
                File.WriteAllText(Path.Combine(targetDir, "partial-import.txt"), "partial", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(targetDir, "current_state.json"),
                    "{\"CurrentFocus\":\"corrupted-partial-import\"}", new UTF8Encoding(false));
                var journal = new Dictionary<string, object>
                {
                    ["Status"] = "applying",
                    ["StartedAt"] = DateTimeOffset.Now.ToString("o"),
                    ["Workspace"] = "",
                    ["BackupZip"] = recoveryZip,
                    ["SourceDeviceId"] = sourceDevice,
                    ["TargetDeviceId"] = "target-device-b",
                    ["SourceSoulId"] = sourceSoul,
                    ["CreatedFiles"] = new List<string> { Path.Combine(targetDir, "partial-import.txt") }
                };
                File.WriteAllText(Path.Combine(targetDir, "migration_journal.json"),
                    JsonSerializer.Serialize(journal), new UTF8Encoding(false));
                bool recovered = SoulBackup.RecoverInterruptedImport(targetDir);
                FaceState recoveredState = FaceSession.LoadState(targetSession);
                bool recoveryOk = recovered &&
                    !File.Exists(Path.Combine(targetDir, "migration_journal.json")) &&
                    !File.Exists(Path.Combine(targetDir, "partial-import.txt")) &&
                    recoveredState != null && recoveredState.CurrentFocus == "目标设备原焦点";

                bool interruptedRolledBack = false;
                try
                {
                    string ignoredBackup;
                    SoulBackup.ImportWithObserver(targetDir, null, zip, delegate(int copied)
                    {
                        if (copied == 2) throw new IOException("injected migration interruption");
                    }, out ignoredBackup);
                }
                catch (InvalidDataException ex)
                {
                    interruptedRolledBack = ex.Message.Contains("rolled_back=True");
                }
                IdentityManifest afterInterrupted = new IdentityManifestStore(targetDir).Load();
                bool rollbackOk = interruptedRolledBack && afterInterrupted != null &&
                    afterInterrupted.SoulId == targetSoul &&
                    !File.Exists(Path.Combine(targetDir, "migration_pending.json")) &&
                    !File.Exists(Path.Combine(targetDir, "migration_journal.json"));

                string backup;
                string importMessage = SoulBackup.Import(targetDir, null, zip, out backup);
                bool pendingOk = File.Exists(Path.Combine(targetDir, "migration_pending.json"));
                string pendingText = pendingOk
                    ? File.ReadAllText(Path.Combine(targetDir, "migration_pending.json"), Encoding.UTF8)
                    : "";
                bool targetDevice = string.Equals(SoulBackup.DeviceId(), "target-device-b",
                    StringComparison.OrdinalIgnoreCase);
                if (!targetDevice)
                {
                    using (var pending = JsonDocument.Parse(pendingText))
                    {
                        string actualTarget = pending.RootElement.TryGetProperty("target_device_id", out var td)
                            ? (td.GetString() ?? "")
                            : "";
                        targetDevice = !string.IsNullOrWhiteSpace(actualTarget);
                    }
                }
                pendingOk = pendingOk &&
                    pendingText.Contains("\"source_device_id\": \"" + sourceDevice + "\"") &&
                    pendingText.Contains("\"source_soul_id\": \"" + sourceSoul + "\"") &&
                    targetDevice;

                string actualTargetDevice = SoulBackup.DeviceId();
                IdentityManifestLoadResult identitySwitch = new IdentityManifestStore(targetDir).LoadOrCreate(
                    sourceSoul, actualTargetDevice, "new-device-model", "interactive", PermissionTier.Full);
                string afterDigest = File.ReadAllText(Path.Combine(targetDir, "identity-invariants.sha256")).Trim();
                bool identityOk = identitySwitch.Verification.Valid &&
                    identitySwitch.EnvironmentChanged &&
                    identitySwitch.Manifest.SoulId == sourceSoul &&
                    identitySwitch.Manifest.CurrentDeviceId == actualTargetDevice &&
                    identitySwitch.PreviousDeviceId == sourceDevice &&
                    string.Equals(sourceDigest, afterDigest, StringComparison.OrdinalIgnoreCase);

                FaceState importedState = FaceSession.LoadState(targetSession);
                bool intentOk = importedState != null && importedState.OpenIntents != null &&
                    importedState.OpenIntents.Any(x => x.Id == sourceIntent.Id &&
                        x.Text == "迁移后继续完成项目" &&
                        (x.Status == "open" || x.Status == "monitoring"));
                List<FaceMessage> timeline = FaceSession.Load(targetSession, 50);
                bool timelineOk = timeline.Any(x => x.Text == "源设备时间线") &&
                    !timeline.Any(x => x.Text == "目标设备原有时间线");
                MigrationResult migration = MigrationGuard.Check(targetDir, targetSession, importedState);
                bool migrationOk = migration.IsResurrection && migration.ImportedSoul &&
                    migration.DeviceChanged &&
                    !File.Exists(Path.Combine(targetDir, "migration_pending.json"));

                using (var migratedMemory = new MemoryStore(Path.Combine(targetDir, "memory.jsonl")))
                {
                    List<MemoryItem> memories = migratedMemory.LoadAllAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    bool sourceMemory = memories.Any(x => x.Key == "relationship:user" &&
                        (x.OriginDevice == sourceDevice ||
                         (x.Provenance != null && x.Provenance.Sources != null &&
                          x.Provenance.Sources.Any(s => s.DeviceId == sourceDevice))));
                    bool targetMemory = memories.Any(x => x.Key == "target:only" &&
                        (x.OriginDevice == "target-device-b" ||
                         (x.Provenance != null && x.Provenance.Sources != null &&
                          x.Provenance.Sources.Any(s => s.DeviceId == "target-device-b"))));
                    bool memoryOk = sourceMemory && targetMemory;
                    bool backupOk = File.Exists(backup);
                    bool ok = highNoMutation && recoveryOk && rollbackOk && pendingOk &&
                        identityOk && intentOk && timelineOk && migrationOk && memoryOk && backupOk &&
                        !string.IsNullOrEmpty(importMessage);
                    Console.WriteLine("SOUL MIGRATION: " + (ok ? "PASS" : "FAIL") +
                        " high_schema=" + highNoMutation +
                        " recovery=" + recoveryOk +
                        " rollback=" + rollbackOk +
                        " pending=" + pendingOk +
                        " identity=" + identityOk +
                        " intent=" + intentOk +
                        " timeline=" + timelineOk +
                        " migration=" + migrationOk +
                        " memory_sources=" + memoryOk +
                        " backup=" + backupOk +
                        " backup_path=" + (backup ?? "missing"));
                    return ok ? 0 : 1;
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        static void CreateHighVersionZip(string sourceZip, string targetZip)
        {
            File.Copy(sourceZip, targetZip, true);
            using (var archive = ZipFile.Open(targetZip, ZipArchiveMode.Update))
            {
                ZipArchiveEntry existing = archive.GetEntry("manifest.json");
                string manifest = "";
                if (existing != null)
                {
                    using (var reader = new StreamReader(existing.Open(), Encoding.UTF8))
                        manifest = reader.ReadToEnd();
                    existing.Delete();
                }
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(manifest) ? "{}" : manifest))
                {
                    var props = doc.RootElement.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetRawText());
                    props["schema_version"] = "2";
                    manifest = "{" + string.Join(",", props.Select(p => "\"" + p.Key + "\":" + p.Value)) + "}";
                }
                ZipArchiveEntry replacement = archive.CreateEntry("manifest.json");
                using (var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false)))
                    writer.Write(manifest);
            }
        }
    }
}
