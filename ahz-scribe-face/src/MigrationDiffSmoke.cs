using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    internal static class MigrationDiffSmoke
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "ahz-m4d-migration-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                bool normalOk = RunNormalRestart(Path.Combine(root, "normal"));
                bool modelOk = RunModelSwitch(Path.Combine(root, "model"));
                bool dormancyOk = RunDormancy(Path.Combine(root, "dormancy"));
                bool deviceOk = RunDeviceMigration(Path.Combine(root, "device"));
                bool capabilitiesOk = RunCapabilityChange(Path.Combine(root, "capabilities"));
                bool soulOk = RunSoulImport(Path.Combine(root, "soul"));
                bool conflictsOk = RunSourceConflicts(Path.Combine(root, "conflicts"));
                bool ok = normalOk && modelOk && dormancyOk && deviceOk && capabilitiesOk &&
                    soulOk && conflictsOk;
                Console.WriteLine("MIGRATION DIFF: " + (ok ? "PASS" : "FAIL") +
                    " normal=" + normalOk +
                    " model=" + modelOk +
                    " dormancy=" + dormancyOk +
                    " device=" + deviceOk +
                    " capabilities=" + capabilitiesOk +
                    " soul=" + soulOk +
                    " conflicts=" + conflictsOk);
                return ok ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        static bool RunNormalRestart(string dir)
        {
            Directory.CreateDirectory(dir);
            string sessions = SeedState(dir, "normal intent");
            string device = SoulBackup.DeviceId();
            IdentityManifest identity = SeedIdentity(dir, "soul-normal", device, "model-normal",
                "interactive");
            MigrationResult migration = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-normal", "model-normal");
            MigrationDiffReport report = MigrationDiffService.Record(dir, sessions, identity,
                migration, DateTimeOffset.Now);
            return migration.Kind == MigrationKind.NormalRestart &&
                report.Kind == MigrationKind.NormalRestart &&
                report.BaselineInitialized &&
                report.IdentityPreserved &&
                File.Exists(MigrationDiffService.ReportPath(dir)) &&
                File.Exists(MigrationDiffService.BaselinePath(dir)) &&
                MigrationObservationStore.Current(dir, DateTimeOffset.Now).Status ==
                    MigrationObservationStatus.None;
        }

        static bool RunModelSwitch(string dir)
        {
            Directory.CreateDirectory(dir);
            string sessions = SeedState(dir, "old intent");
            string device = SoulBackup.DeviceId();
            IdentityManifest before = SeedIdentity(dir, "soul-model", device, "model-a",
                "interactive");
            MigrationResult normal = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-a", "model-a");
            MigrationDiffReport baseline = MigrationDiffService.Record(dir, sessions, before,
                normal, DateTimeOffset.Now);
            string baselineDigest = baseline.After.InvariantDigest;

            FaceState state = FaceSession.LoadState(sessions);
            state.OpenIntents[0].Status = "done";
            FaceSession.SaveState(sessions, state);
            OpenIntent added = FaceSession.AddIntent(sessions, "new intent after switch", "");
            IdentityManifest after = SeedIdentity(dir, "soul-model", device, "model-b",
                "interactive");
            MigrationResult migration = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-b", "model-a");
            MigrationDiffReport report = MigrationDiffService.Record(dir, sessions, after,
                migration, DateTimeOffset.Now);
            MigrationObservation observation =
                MigrationObservationStore.Current(dir, DateTimeOffset.Now);
            bool wrongCancelBlocked = !MigrationObservationStore.Cancel(dir, "wrong-id",
                "wrong observation", DateTimeOffset.Now);
            bool cancelled = MigrationObservationStore.Cancel(dir,
                observation.ObservationId, "operator cancelled", DateTimeOffset.Now);
            MigrationObservation final =
                MigrationObservationStore.Current(dir, DateTimeOffset.Now);
            string reportJson = File.ReadAllText(MigrationDiffService.ReportPath(dir));
            return migration.Kind == MigrationKind.ModelSwitch &&
                migration.ModelChanged &&
                !migration.IsResurrection &&
                report.ModelChanged &&
                report.IdentityPreserved &&
                report.Before.Model == "model-a" &&
                report.After.Model == "model-b" &&
                string.Equals(baselineDigest, report.After.InvariantDigest,
                    StringComparison.OrdinalIgnoreCase) &&
                report.IntentChanges.Any(x => x.Id == added.Id &&
                    x.Change == "added") &&
                report.IntentChanges.Any(x => x.Change == "changed") &&
                observation.Status == MigrationObservationStatus.Active &&
                !string.IsNullOrEmpty(observation.ExpiresAt) &&
                wrongCancelBlocked &&
                cancelled &&
                final.Status == MigrationObservationStatus.Cancelled &&
                reportJson.Contains("\"kind\": \"model_switch\"");
        }

        static bool RunDormancy(string dir)
        {
            Directory.CreateDirectory(dir);
            string sessions = SeedState(dir, "sleep intent");
            string device = SoulBackup.DeviceId();
            IdentityManifest identity = SeedIdentity(dir, "soul-dormancy", device,
                "model-dormancy", "interactive");
            MigrationResult normal = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-dormancy", "model-dormancy");
            MigrationDiffService.Record(dir, sessions, identity, normal, DateTimeOffset.Now);

            FaceState state = FaceSession.LoadState(sessions);
            state.LastPulseAt = DateTime.Now.AddDays(-3).ToString("o");
            FaceSession.SaveState(sessions, state);
            MigrationResult migration = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-dormancy", "model-dormancy");
            MigrationDiffReport report = MigrationDiffService.Record(dir, sessions, identity,
                migration, DateTimeOffset.Now);
            return migration.Kind == MigrationKind.LongDormancy &&
                migration.LongDormancy &&
                !migration.IsResurrection &&
                report.TimelineGapSeconds >= 2.5 * 86400 &&
                report.Kind == MigrationKind.LongDormancy;
        }

        static bool RunDeviceMigration(string dir)
        {
            Directory.CreateDirectory(dir);
            string sessions = SeedState(dir, "device intent");
            string oldDevice = "device-old";
            IdentityManifest identity = SeedIdentity(dir, "soul-device", oldDevice,
                "model-device", "interactive");
            FaceState state = FaceSession.LoadState(sessions);
            state.DeviceId = oldDevice;
            FaceSession.SaveState(sessions, state);
            MigrationResult normal = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-device", "model-device");
            MigrationDiffService.Record(dir, sessions, identity, normal, DateTimeOffset.Now);

            IdentityManifest moved = SeedIdentity(dir, "soul-device", "device-new",
                "model-device", "interactive");
            state = FaceSession.LoadState(sessions);
            state.DeviceId = oldDevice;
            FaceSession.SaveState(sessions, state);
            MigrationResult migration = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-device", "model-device");
            MigrationDiffReport report = MigrationDiffService.Record(dir, sessions, moved,
                migration, DateTimeOffset.Now);
            return migration.Kind == MigrationKind.DeviceMigration &&
                migration.DeviceChanged &&
                migration.IsResurrection &&
                report.DeviceChanged &&
                report.Before.DeviceId == oldDevice &&
                report.After.DeviceId == "device-new";
        }

        static bool RunCapabilityChange(string dir)
        {
            Directory.CreateDirectory(dir);
            string sessions = SeedState(dir, "capability intent");
            string device = SoulBackup.DeviceId();
            IdentityManifest full = SeedIdentity(dir, "soul-capabilities", device,
                "model-capabilities", "interactive");
            MigrationResult normal = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-capabilities", "model-capabilities");
            MigrationDiffService.Record(dir, sessions, full, normal, DateTimeOffset.Now);

            IdentityManifest limited = SeedIdentity(dir, "soul-capabilities", device,
                "model-capabilities", "read_only");
            MigrationResult migration = MigrationGuard.Check(dir, sessions,
                FaceSession.LoadState(sessions), "model-capabilities", "model-capabilities");
            MigrationDiffReport report = MigrationDiffService.Record(dir, sessions, limited,
                migration, DateTimeOffset.Now);
            return report.PermissionChanged &&
                report.CapabilitiesRemoved.Contains("write_file") &&
                report.After.Capabilities.Any(x => x.Name == "write_file" && !x.Available);
        }

        static bool RunSoulImport(string dir)
        {
            string sourceDir = Path.Combine(dir, "source");
            string targetDir = Path.Combine(dir, "target");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            string sourceSessions = SeedState(sourceDir, "source intent");
            string targetSessions = SeedState(targetDir, "target intent");
            string sourceDevice = "source-device";
            string targetDevice = SoulBackup.DeviceId();
            IdentityManifest sourceIdentity = SeedIdentity(sourceDir, "soul-source",
                sourceDevice, "source-model", "interactive");
            using (var sourceMemory = new MemoryStore(Path.Combine(sourceDir, "memory.jsonl")))
            {
                sourceMemory.RememberAsync(new MemoryItem
                {
                    Kind = "semantic",
                    Key = "relationship:imported",
                    Value = "imported relationship",
                    Summary = "imported",
                    OriginDevice = sourceDevice
                }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            }
            IdentityManifest targetBefore = SeedIdentity(targetDir, "soul-target",
                targetDevice, "target-model", "interactive");
            MigrationResult targetNormal = MigrationGuard.Check(targetDir, targetSessions,
                FaceSession.LoadState(targetSessions), "target-model", "target-model");
            MigrationDiffService.Record(targetDir, targetSessions, targetBefore,
                targetNormal, DateTimeOffset.Now);

            string zip = Path.Combine(dir, "source.zip");
            string backup;
            SoulBackup.Export(sourceDir, null, zip);
            SoulBackup.Import(targetDir, null, zip, out backup);
            string adoptedSoul = SoulBackup.AdoptPendingSoulId(targetDir);
            IdentityManifest imported = SeedIdentity(targetDir, adoptedSoul, targetDevice,
                "source-model", "interactive");
            MigrationResult migration = MigrationGuard.Check(targetDir, targetSessions,
                FaceSession.LoadState(targetSessions), "source-model", "target-model");
            MigrationDiffReport report = MigrationDiffService.Record(targetDir, targetSessions,
                imported, migration, DateTimeOffset.Now);
            return adoptedSoul == sourceIdentity.SoulId &&
                File.Exists(Path.Combine(targetDir, "soul_id")) &&
                migration.Kind == MigrationKind.SoulImport &&
                migration.ImportedSoul &&
                report.Kind == MigrationKind.SoulImport &&
                report.ModelChanged &&
                report.MemorySourceChanges.Any(x => x.DeviceId == sourceDevice &&
                    x.AfterCount > 0) &&
                !string.IsNullOrEmpty(backup) &&
                File.Exists(backup);
        }

        static bool RunSourceConflicts(string dir)
        {
            Directory.CreateDirectory(dir);
            var store = new MemoryStore(Path.Combine(dir, "memory.jsonl"));
            store.RememberAsync(new MemoryItem
            {
                Kind = "semantic",
                Key = "device:role",
                Value = "old-device-value",
                Summary = "old",
                OriginDevice = "device-old",
                SourceAuditId = "audit-old"
            }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            store.RememberAsync(new MemoryItem
            {
                Kind = "semantic",
                Key = "device:role",
                Value = "new-device-value",
                Summary = "new",
                OriginDevice = "device-new",
                SourceAuditId = "audit-new"
            }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            store.RememberAsync(new MemoryItem
            {
                Kind = "semantic",
                Key = "identity:display_name",
                Value = "AhzScribe",
                Summary = "name old",
                OriginDevice = "device-old",
                SourceAuditId = "audit-name-old"
            }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            store.RememberAsync(new MemoryItem
            {
                Kind = "semantic",
                Key = "identity:display_name",
                Value = "AhzScribe",
                Summary = "name new",
                OriginDevice = "device-new",
                SourceAuditId = "audit-name-new"
            }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            List<MemoryItem> all = store.LoadAllAsync(System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            List<MemoryItem> conflicts = all.Where(x => x.Key == "device:role").ToList();
            MemoryItem sameValue = all.Single(x => x.Key == "identity:display_name");
            bool sourceIds = sameValue.Provenance != null &&
                sameValue.Provenance.Sources != null &&
                sameValue.Provenance.Sources.Any(x => x.DeviceId == "device-old") &&
                sameValue.Provenance.Sources.Any(x => x.DeviceId == "device-new");
            bool ok = conflicts.Count == 2 &&
                conflicts.Any(x => x.Value == "old-device-value" &&
                    x.OriginDevice == "device-old" && x.Status == "active") &&
                conflicts.Any(x => x.Value == "new-device-value" &&
                    x.OriginDevice == "device-new" && x.Status == "disputed") &&
                sourceIds;
            store.Dispose();
            return ok;
        }

        static string SeedState(string dir, string intentText)
        {
            string sessions = Path.Combine(dir, "sessions.jsonl");
            FaceSession.Append(sessions, new FaceMessage
            {
                Role = "你",
                Text = "timeline " + intentText,
                Kind = "chat"
            });
            FaceSession.SetFocus(sessions, intentText);
            FaceSession.AddIntent(sessions, intentText, "");
            FaceState state = FaceSession.LoadState(sessions);
            state.DeviceId = SoulBackup.DeviceId();
            state.LastPulseAt = DateTime.Now.ToString("o");
            FaceSession.SaveState(sessions, state);
            return sessions;
        }

        static IdentityManifest SeedIdentity(string dir, string soul, string device,
            string model, string profile)
        {
            IdentityManifestStore store = new IdentityManifestStore(dir);
            IdentityManifestLoadResult result = store.LoadOrCreate(soul, device, model,
                profile, PermissionTier.Full);
            if (result == null || result.Verification == null || !result.Verification.Valid)
                throw new InvalidDataException("identity seed failed: " +
                    (result == null || result.Verification == null ? "missing" :
                        string.Join("; ", result.Verification.Errors)));
            return result.Manifest;
        }
    }
}
