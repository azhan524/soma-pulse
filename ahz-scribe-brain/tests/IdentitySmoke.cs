using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class IdentitySmoke
{
    public static bool Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ahz-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string soul = "soul_identity_test";
            var store = new IdentityManifestStore(dir);
            IdentityManifestLoadResult created = store.LoadOrCreate(
                soul, "device-a", "model-a", "interactive", PermissionTier.Full);
            bool createOk = created.Created && created.Verification.Valid &&
                created.Manifest.SoulId == soul &&
                !string.IsNullOrEmpty(created.Manifest.LineageId) &&
                created.Manifest.Invariants.Count == 6 &&
                created.Manifest.Invariants.All(x => x.Immutable);
            string digest = File.ReadAllText(store.DigestPath).Trim();
            string lineage = created.Manifest.LineageId;
            string createdAt = created.Manifest.CreatedAt;

            var restartedStore = new IdentityManifestStore(dir);
            IdentityManifestLoadResult modelSwitch = restartedStore.LoadOrCreate(
                soul, "device-a", "model-b", "interactive", PermissionTier.Full);
            bool modelOk = !modelSwitch.Created && modelSwitch.Verification.Valid &&
                modelSwitch.EnvironmentChanged &&
                modelSwitch.PreviousModel == "model-a" &&
                modelSwitch.Manifest.ActiveModel == "model-b" &&
                modelSwitch.Manifest.CurrentDeviceId == "device-a" &&
                modelSwitch.Manifest.SoulId == soul &&
                modelSwitch.Manifest.LineageId == lineage &&
                modelSwitch.Manifest.CreatedAt == createdAt &&
                string.Equals(File.ReadAllText(restartedStore.DigestPath).Trim(), digest,
                    StringComparison.OrdinalIgnoreCase);

            IdentityManifestLoadResult deviceSwitch = restartedStore.LoadOrCreate(
                soul, "device-b", "model-b", "interactive", PermissionTier.Full);
            bool deviceOk = deviceSwitch.Verification.Valid &&
                deviceSwitch.PreviousDeviceId == "device-a" &&
                deviceSwitch.Manifest.CurrentDeviceId == "device-b" &&
                deviceSwitch.Manifest.SoulId == soul &&
                deviceSwitch.Manifest.LineageId == lineage;

            IdentityManifestLoadResult profileSwitch = restartedStore.LoadOrCreate(
                soul, "device-b", "model-b", "read_only", PermissionTier.Full);
            bool profileOk = profileSwitch.Verification.Valid &&
                profileSwitch.Manifest.PermissionProfile == "read_only" &&
                profileSwitch.Manifest.PermissionCeiling == PermissionTier.Full.ToString() &&
                profileSwitch.Manifest.Invariants.Exists(x =>
                    x.Key == "safety.permission_ceiling" && x.Value == PermissionTier.Full.ToString());

            IdentityManifestLoadResult stableRestart = new IdentityManifestStore(dir).LoadOrCreate(
                soul, "device-b", "model-b", "read_only", PermissionTier.Full);
            bool restartOk = stableRestart.Verification.Valid && !stableRestart.EnvironmentChanged &&
                stableRestart.Manifest.SoulId == soul && stableRestart.Manifest.LineageId == lineage;

            string ceilingDir = Path.Combine(dir, "permission-ceiling");
            var ceilingStore = new IdentityManifestStore(ceilingDir);
            ceilingStore.LoadOrCreate("soul_ceiling", "device-a", "model-a", "read_only",
                PermissionTier.ReadOnly);
            IdentityManifestLoadResult ceilingAttempt = new IdentityManifestStore(ceilingDir).LoadOrCreate(
                "soul_ceiling", "device-a", "model-a", "interactive", PermissionTier.Full);
            bool ceilingOk = ceilingAttempt.Verification.Valid && ceilingAttempt.CeilingClamped &&
                ceilingAttempt.Manifest.PermissionProfile == "read_only" &&
                ceilingAttempt.Manifest.PermissionCeiling == PermissionTier.ReadOnly.ToString();

            IdentityManifestLoadResult wrongSoul = new IdentityManifestStore(dir).LoadOrCreate(
                "soul_wrong", "device-b", "model-b", "read_only", PermissionTier.Full);
            bool wrongSoulBlocked = !wrongSoul.Verification.Valid &&
                wrongSoul.Verification.Errors.Contains("identity_soul_id_mismatch");

            IdentityManifest manifest = restartedStore.Load();
            manifest.Invariants.First(x => x.Key == "safety.authority").Value = "model_can_override";
            File.WriteAllText(restartedStore.ManifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            IdentityManifestLoadResult tampered = new IdentityManifestStore(dir).LoadOrCreate(
                soul, "device-b", "model-b", "read_only", PermissionTier.Full);
            bool tamperBlocked = !tampered.Verification.Valid &&
                tampered.Verification.Errors.Contains("identity_invariant_digest_mismatch");

            bool ok = createOk && modelOk && deviceOk && profileOk && restartOk && ceilingOk &&
                wrongSoulBlocked && tamperBlocked;
            Console.WriteLine("IDENTITY MANIFEST: " + (ok ? "PASS" : "FAIL") +
                " invariants=" + created.Manifest.Invariants.Count);
            return ok;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
