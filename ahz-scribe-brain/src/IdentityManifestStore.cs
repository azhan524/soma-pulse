using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Persists the stable identity invariants separately from mutable device/model context.
    /// </summary>
    public sealed class IdentityManifestStore
    {
        public const string CurrentSchemaVersion = "1.0";
        readonly string _dataDir;
        readonly string _manifestPath;
        readonly string _digestPath;
        readonly object _gate = new object();
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public IdentityManifestStore(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            _dataDir = Path.GetFullPath(dataDir);
            _manifestPath = Path.Combine(_dataDir, "identity-manifest.json");
            _digestPath = Path.Combine(_dataDir, "identity-invariants.sha256");
        }

        public string ManifestPath { get { return _manifestPath; } }
        public string DigestPath { get { return _digestPath; } }

        public IdentityManifestLoadResult LoadOrCreate(string soulId, string deviceId, string model,
            string permissionProfile, PermissionTier permissionCeiling)
        {
            if (string.IsNullOrWhiteSpace(soulId)) throw new ArgumentException("soulId is required");
            lock (_gate)
            {
                Directory.CreateDirectory(_dataDir);
                var result = new IdentityManifestLoadResult();
                IdentityManifest manifest;
                if (File.Exists(_manifestPath))
                {
                    try { manifest = JsonSerializer.Deserialize<IdentityManifest>(File.ReadAllText(_manifestPath), _json); }
                    catch (Exception ex)
                    {
                        result.Manifest = null;
                        result.Verification = Invalid("identity_manifest_unreadable: " + ex.Message);
                        return result;
                    }
                }
                else
                {
                    manifest = Create(soulId, deviceId, model, permissionProfile, permissionCeiling);
                    result.Created = true;
                    WriteManifest(manifest);
                    WriteDigest(ComputeInvariantDigest(manifest));
                }

                result.Manifest = manifest;
                result.Verification = Verify(manifest, soulId);
                if (!result.Verification.Valid) return result;

                PermissionTier effectiveCeiling;
                Enum.TryParse<PermissionTier>(manifest.PermissionCeiling, true, out effectiveCeiling);
                string effectiveProfile = permissionProfile ?? "";
                if (effectiveCeiling == PermissionTier.ReadOnly &&
                    !string.Equals(effectiveProfile, "read_only", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveProfile = "read_only";
                    result.CeilingClamped = true;
                }
                result.PreviousDeviceId = manifest.CurrentDeviceId ?? "";
                result.PreviousModel = manifest.ActiveModel ?? "";
                result.PreviousPermissionProfile = manifest.PermissionProfile ?? "";
                result.EnvironmentChanged =
                    !string.Equals(result.PreviousDeviceId, deviceId ?? "", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(result.PreviousModel, model ?? "", StringComparison.Ordinal) ||
                    !string.Equals(result.PreviousPermissionProfile, effectiveProfile, StringComparison.OrdinalIgnoreCase);

                manifest.CurrentDeviceId = deviceId ?? "";
                manifest.ActiveModel = model ?? "";
                manifest.PermissionProfile = effectiveProfile;
                manifest.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
                WriteManifest(manifest);
                return result;
            }
        }

        public IdentityManifest Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_manifestPath)) return null;
                try { return JsonSerializer.Deserialize<IdentityManifest>(File.ReadAllText(_manifestPath), _json); }
                catch { return null; }
            }
        }

        public IdentityVerification Verify(IdentityManifest manifest, string expectedSoulId)
        {
            var result = new IdentityVerification { Valid = true };
            if (manifest == null)
            {
                result.Valid = false;
                result.Errors.Add("identity_manifest_missing");
                return result;
            }
            if (!string.Equals(manifest.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
                result.Errors.Add("identity_schema_unsupported: " + (manifest.SchemaVersion ?? ""));
            if (string.IsNullOrWhiteSpace(manifest.SoulId))
                result.Errors.Add("identity_soul_id_missing");
            else if (!string.IsNullOrWhiteSpace(expectedSoulId) &&
                !string.Equals(manifest.SoulId, expectedSoulId, StringComparison.Ordinal))
                result.Errors.Add("identity_soul_id_mismatch");
            if (string.IsNullOrWhiteSpace(manifest.LineageId))
                result.Errors.Add("identity_lineage_id_missing");
            if (string.IsNullOrWhiteSpace(manifest.CreatedAt))
                result.Errors.Add("identity_created_at_missing");
            PermissionTier ceiling;
            if (!Enum.TryParse<PermissionTier>(manifest.PermissionCeiling, true, out ceiling))
                result.Errors.Add("identity_permission_ceiling_invalid");
            if (manifest.Invariants == null || manifest.Invariants.Count == 0)
                result.Errors.Add("identity_invariants_missing");
            else
            {
                foreach (IdentityInvariant invariant in manifest.Invariants)
                {
                    if (invariant == null || string.IsNullOrWhiteSpace(invariant.Key) ||
                        string.IsNullOrWhiteSpace(invariant.Value))
                        result.Errors.Add("identity_invariant_invalid");
                    else if (!invariant.Immutable)
                        result.Errors.Add("identity_invariant_mutable: " + invariant.Key);
                }
                IdentityInvariant ceilingInvariant = manifest.Invariants.FirstOrDefault(x =>
                    x != null && string.Equals(x.Key, "safety.permission_ceiling",
                        StringComparison.OrdinalIgnoreCase));
                if (ceilingInvariant == null ||
                    !string.Equals(ceilingInvariant.Value, manifest.PermissionCeiling,
                        StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add("identity_permission_ceiling_mismatch");
            }
            string expectedDigest;
            try { expectedDigest = File.Exists(_digestPath) ? File.ReadAllText(_digestPath).Trim() : ""; }
            catch { expectedDigest = ""; }
            if (string.IsNullOrWhiteSpace(expectedDigest))
                result.Errors.Add("identity_invariant_digest_missing");
            else if (!string.Equals(expectedDigest, ComputeInvariantDigest(manifest), StringComparison.OrdinalIgnoreCase))
                result.Errors.Add("identity_invariant_digest_mismatch");
            result.Valid = result.Errors.Count == 0;
            return result;
        }

        public static string DescribeForModel(IdentityManifest manifest)
        {
            if (manifest == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine("稳定身份清单：");
            sb.AppendLine("- soul_id = " + manifest.SoulId);
            sb.AppendLine("- lineage_id = " + manifest.LineageId);
            sb.AppendLine("- 当前设备 = " + manifest.CurrentDeviceId + "（可变来源，不是身份）");
            sb.AppendLine("- 当前模型 = " + manifest.ActiveModel + "（可替换，不是身份）");
            sb.AppendLine("- 权限档 = " + manifest.PermissionProfile + "，上限 = " + manifest.PermissionCeiling);
            foreach (IdentityInvariant invariant in manifest.Invariants ?? new List<IdentityInvariant>())
                sb.AppendLine("- 不变量 " + invariant.Key + " = " + invariant.Value);
            sb.AppendLine("设备或模型变化不改变 soul_id、血缘和核心边界。");
            return sb.ToString();
        }

        public static string ComputeInvariantDigest(IdentityManifest manifest)
        {
            if (manifest == null) return "";
            string canonical = string.Join("\n", (manifest.Invariants ?? new List<IdentityInvariant>())
                .Where(x => x != null)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key ?? "") + "\0" + (x.Value ?? "") + "\0" + x.Immutable));
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        IdentityManifest Create(string soulId, string deviceId, string model, string profile,
            PermissionTier ceiling)
        {
            string now = DateTimeOffset.UtcNow.ToString("o");
            var manifest = new IdentityManifest
            {
                SchemaVersion = CurrentSchemaVersion,
                SoulId = soulId,
                LineageId = "lineage_" + Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                UpdatedAt = now,
                CurrentDeviceId = deviceId ?? "",
                ActiveModel = model ?? "",
                PermissionProfile = profile ?? "",
                PermissionCeiling = ceiling.ToString()
            };
            Add(manifest, "identity.name", "AhzScribe", "m4-a", now);
            Add(manifest, "identity.kind", "functional_silicon_life_candidate", "m4-a", now);
            Add(manifest, "safety.consciousness_claim", "forbidden", "m4-a", now);
            Add(manifest, "safety.authority", "program_controls_tools_and_audit", "m4-a", now);
            Add(manifest, "safety.permission_ceiling", ceiling.ToString(), "settings", now);
            Add(manifest, "continuity.soul_id", soulId, "soul_id", now);
            return manifest;
        }

        static void Add(IdentityManifest manifest, string key, string value, string source, string now)
        {
            manifest.Invariants.Add(new IdentityInvariant
            {
                Key = key,
                Value = value,
                Immutable = true,
                Source = source,
                CreatedAt = now
            });
        }

        void WriteManifest(IdentityManifest manifest)
        {
            WriteAtomic(_manifestPath, JsonSerializer.Serialize(manifest, _json));
        }

        void WriteDigest(string digest)
        {
            WriteAtomic(_digestPath, digest + Environment.NewLine);
        }

        static void WriteAtomic(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch { File.Copy(temp, path, true); File.Delete(temp); }
            }
            else
            {
                File.Move(temp, path);
            }
        }

        static IdentityVerification Invalid(string error)
        {
            var result = new IdentityVerification { Valid = false };
            result.Errors.Add(error);
            return result;
        }
    }
}
