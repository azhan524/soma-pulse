using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    public static class MigrationDiffService
    {
        const int ObservationHours = 24;
        static readonly object Gate = new object();
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        public static string ReportPath(string dataDir)
        {
            return Path.Combine(dataDir, "migration-diff.json");
        }

        public static string BaselinePath(string dataDir)
        {
            return Path.Combine(dataDir, "migration-baseline.json");
        }

        public static string HistoryPath(string dataDir)
        {
            return Path.Combine(dataDir, "migration-history.jsonl");
        }

        public static MigrationDiffReport Record(string dataDir, string sessionPath,
            IdentityManifest identity, MigrationResult migration, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            lock (Gate)
            {
                Directory.CreateDirectory(dataDir);
                MigrationStateSnapshot before = Read<MigrationStateSnapshot>(BaselinePath(dataDir));
                MigrationStateSnapshot after = Capture(dataDir, sessionPath, identity, now);
                var report = new MigrationDiffReport
                {
                    ReportId = "migration_" + now.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                        Guid.NewGuid().ToString("N").Substring(0, 6),
                    Kind = migration == null ? MigrationKind.NormalRestart : migration.Kind,
                    DetectedAt = now.ToString("o"),
                    BaselineInitialized = before == null,
                    Before = before,
                    After = after,
                    TimelineGapSeconds = migration == null ? 0 :
                        Math.Max(0, migration.GapDays * 86400)
                };
                Compare(report);
                WriteAtomic(ReportPath(dataDir), JsonSerializer.Serialize(report, Json));
                AppendHistory(dataDir, report);
                WriteAtomic(BaselinePath(dataDir), JsonSerializer.Serialize(after, Json));

                if (before != null && report.Kind != MigrationKind.NormalRestart)
                    MigrationObservationStore.Start(dataDir, report, now, ObservationHours);

                FaceAudit.Append(dataDir, "continuity", "migration_diff",
                    report.BaselineInitialized ? "initialized" : "reported",
                    report.Kind + ": " + report.Summary, "M4-d migration diff");
                return report;
            }
        }

        public static MigrationDiffReport ReadReport(string dataDir)
        {
            return Read<MigrationDiffReport>(ReportPath(dataDir));
        }

        static MigrationStateSnapshot Capture(string dataDir, string sessionPath,
            IdentityManifest identity, DateTimeOffset now)
        {
            FaceState state = FaceSession.LoadState(sessionPath);
            List<FaceMessage> messages = FaceSession.Load(sessionPath, int.MaxValue);
            List<MemoryItem> memories;
            using (var store = new MemoryStore(Path.Combine(dataDir, "memory.jsonl")))
                memories = store.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();

            var snapshot = new MigrationStateSnapshot
            {
                SoulId = identity == null ? "" : (identity.SoulId ?? ""),
                LineageId = identity == null ? "" : (identity.LineageId ?? ""),
                DeviceId = identity == null ? "" : (identity.CurrentDeviceId ?? ""),
                Model = identity == null ? "" : (identity.ActiveModel ?? ""),
                PermissionProfile = identity == null ? "" : (identity.PermissionProfile ?? ""),
                PermissionCeiling = identity == null ? "" : (identity.PermissionCeiling ?? ""),
                InvariantDigest = identity == null ? "" :
                    IdentityManifestStore.ComputeInvariantDigest(identity),
                Invariants = identity == null || identity.Invariants == null
                    ? new List<IdentityInvariant>()
                    : identity.Invariants.Where(x => x != null).Select(x => new IdentityInvariant
                    {
                        Key = x.Key,
                        Value = x.Value,
                        Immutable = x.Immutable,
                        Source = x.Source,
                        CreatedAt = x.CreatedAt
                    }).OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
                MemoryCount = memories.Count,
                Timeline = new MigrationTimelineSnapshot
                {
                    MessageCount = messages.Count,
                    FirstAt = messages.Count == 0 ? "" : (messages[0].Ts ?? ""),
                    LastAt = messages.Count == 0 ? "" : (messages[messages.Count - 1].Ts ?? "")
                },
                CapturedAt = now.ToString("o")
            };

            if (state != null && state.OpenIntents != null)
                snapshot.Intents = state.OpenIntents.Where(x => x != null).Select(x =>
                    new MigrationIntentSnapshot
                    {
                        Id = x.Id ?? "",
                        Text = x.Text ?? "",
                        Status = x.Status ?? ""
                    }).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();

            PermissionTier maximum = EffectiveTier(identity);
            snapshot.Capabilities = ToolPolicyCatalog.All.OrderBy(x => x.Name,
                StringComparer.Ordinal).Select(x => new MigrationCapabilitySnapshot
                {
                    Name = x.Name,
                    Category = x.Category,
                    MinimumTier = x.MinimumTier,
                    RequiresApproval = x.RequiresApproval,
                    Available = x.MinimumTier <= maximum
                }).ToList();

            var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MemoryItem item in memories)
            {
                HashSet<string> devices = SourceDevices(item);
                if (devices.Count == 0) devices.Add("unknown");
                foreach (string device in devices)
                {
                    int count;
                    sourceCounts.TryGetValue(device, out count);
                    sourceCounts[device] = count + 1;
                }
            }
            snapshot.MemorySources = sourceCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new MigrationMemorySourceSnapshot
                {
                    DeviceId = x.Key,
                    Count = x.Value
                }).ToList();
            snapshot.ConflictedKeys = memories
                .Where(x => string.Equals(x.Status, "disputed", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(x.DisputedWith))
                .Select(x => x.Key ?? "")
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return snapshot;
        }

        static void Compare(MigrationDiffReport report)
        {
            if (report.Before == null)
            {
                report.IdentityPreserved = true;
                report.Summary = "已建立迁移差异基线：" + report.Kind;
                return;
            }
            MigrationStateSnapshot before = report.Before;
            MigrationStateSnapshot after = report.After;
            report.IdentityPreserved =
                string.Equals(before.SoulId, after.SoulId, StringComparison.Ordinal) &&
                string.Equals(before.LineageId, after.LineageId, StringComparison.Ordinal) &&
                string.Equals(before.InvariantDigest, after.InvariantDigest,
                    StringComparison.OrdinalIgnoreCase);
            report.DeviceChanged = !string.Equals(before.DeviceId, after.DeviceId,
                StringComparison.OrdinalIgnoreCase);
            report.ModelChanged = !string.Equals(before.Model, after.Model, StringComparison.Ordinal);
            report.PermissionChanged =
                !string.Equals(before.PermissionProfile, after.PermissionProfile,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(before.PermissionCeiling, after.PermissionCeiling,
                    StringComparison.OrdinalIgnoreCase);

            Dictionary<string, IdentityInvariant> oldInvariants = (before.Invariants ??
                new List<IdentityInvariant>()).Where(x => x != null && x.Key != null)
                .ToDictionary(x => x.Key, x => x, StringComparer.Ordinal);
            Dictionary<string, IdentityInvariant> newInvariants = (after.Invariants ??
                new List<IdentityInvariant>()).Where(x => x != null && x.Key != null)
                .ToDictionary(x => x.Key, x => x, StringComparer.Ordinal);
            foreach (string key in oldInvariants.Keys.Union(newInvariants.Keys)
                .OrderBy(x => x, StringComparer.Ordinal))
            {
                IdentityInvariant a;
                IdentityInvariant b;
                oldInvariants.TryGetValue(key, out a);
                newInvariants.TryGetValue(key, out b);
                if (a == null || b == null || !string.Equals(a.Value, b.Value, StringComparison.Ordinal) ||
                    a.Immutable != b.Immutable)
                    report.InvariantChanges.Add(key);
            }

            Dictionary<string, MigrationIntentSnapshot> oldIntents = (before.Intents ??
                new List<MigrationIntentSnapshot>()).Where(x => x != null && x.Id != null)
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
            Dictionary<string, MigrationIntentSnapshot> newIntents = (after.Intents ??
                new List<MigrationIntentSnapshot>()).Where(x => x != null && x.Id != null)
                .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
            foreach (string id in oldIntents.Keys.Union(newIntents.Keys)
                .OrderBy(x => x, StringComparer.Ordinal))
            {
                MigrationIntentSnapshot a;
                MigrationIntentSnapshot b;
                oldIntents.TryGetValue(id, out a);
                newIntents.TryGetValue(id, out b);
                if (a == null)
                {
                    report.IntentChanges.Add(new MigrationIntentDiff
                    {
                        Id = id,
                        Change = "added",
                        Before = "",
                        After = DescribeIntent(b)
                    });
                }
                else if (b == null)
                {
                    report.IntentChanges.Add(new MigrationIntentDiff
                    {
                        Id = id,
                        Change = "removed",
                        Before = DescribeIntent(a),
                        After = ""
                    });
                }
                else if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal) ||
                    !string.Equals(a.Status, b.Status, StringComparison.Ordinal))
                {
                    report.IntentChanges.Add(new MigrationIntentDiff
                    {
                        Id = id,
                        Change = "changed",
                        Before = DescribeIntent(a),
                        After = DescribeIntent(b)
                    });
                }
            }

            report.MemoryCountDelta = after.MemoryCount - before.MemoryCount;
            Dictionary<string, int> oldSources = (before.MemorySources ??
                new List<MigrationMemorySourceSnapshot>()).Where(x => x != null && x.DeviceId != null)
                .ToDictionary(x => x.DeviceId, x => x.Count, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> newSources = (after.MemorySources ??
                new List<MigrationMemorySourceSnapshot>()).Where(x => x != null && x.DeviceId != null)
                .ToDictionary(x => x.DeviceId, x => x.Count, StringComparer.OrdinalIgnoreCase);
            foreach (string device in oldSources.Keys.Union(newSources.Keys)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int a;
                int b;
                oldSources.TryGetValue(device, out a);
                newSources.TryGetValue(device, out b);
                if (a != b)
                    report.MemorySourceChanges.Add(new MigrationMemorySourceDelta
                    {
                        DeviceId = device,
                        BeforeCount = a,
                        AfterCount = b,
                        Delta = b - a
                    });
            }

            Dictionary<string, bool> oldCapabilities = (before.Capabilities ??
                new List<MigrationCapabilitySnapshot>()).Where(x => x != null && x.Name != null)
                .ToDictionary(x => x.Name, x => x.Available, StringComparer.Ordinal);
            Dictionary<string, bool> newCapabilities = (after.Capabilities ??
                new List<MigrationCapabilitySnapshot>()).Where(x => x != null && x.Name != null)
                .ToDictionary(x => x.Name, x => x.Available, StringComparer.Ordinal);
            foreach (string name in oldCapabilities.Keys.Union(newCapabilities.Keys)
                .OrderBy(x => x, StringComparer.Ordinal))
            {
                bool a;
                bool b;
                oldCapabilities.TryGetValue(name, out a);
                newCapabilities.TryGetValue(name, out b);
                if (!a && b) report.CapabilitiesAdded.Add(name);
                else if (a && !b) report.CapabilitiesRemoved.Add(name);
            }
            report.TimelineMessagesAdded = Math.Max(0,
                after.Timeline.MessageCount - before.Timeline.MessageCount);

            report.Summary = BuildSummary(report);
        }

        static string BuildSummary(MigrationDiffReport report)
        {
            var parts = new List<string>
            {
                report.Kind.ToString(),
                report.IdentityPreserved ? "identity_preserved" : "identity_changed"
            };
            if (report.DeviceChanged) parts.Add("device_changed");
            if (report.ModelChanged) parts.Add("model_changed");
            if (report.PermissionChanged) parts.Add("permission_changed");
            if (report.IntentChanges.Count > 0)
                parts.Add("intents=" + report.IntentChanges.Count);
            if (report.MemoryCountDelta != 0)
                parts.Add("memory_delta=" + report.MemoryCountDelta);
            if (report.CapabilitiesAdded.Count > 0)
                parts.Add("capabilities_added=" + report.CapabilitiesAdded.Count);
            if (report.CapabilitiesRemoved.Count > 0)
                parts.Add("capabilities_removed=" + report.CapabilitiesRemoved.Count);
            if (report.TimelineGapSeconds >= 1)
                parts.Add("timeline_gap_hours=" +
                    (report.TimelineGapSeconds / 3600.0).ToString("F1", CultureInfo.InvariantCulture));
            return string.Join("; ", parts);
        }

        static string DescribeIntent(MigrationIntentSnapshot intent)
        {
            return intent == null ? "" : "[" + (intent.Status ?? "") + "] " + (intent.Text ?? "");
        }

        static HashSet<string> SourceDevices(MemoryItem item)
        {
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item == null) return devices;
            if (!string.IsNullOrWhiteSpace(item.OriginDevice)) devices.Add(item.OriginDevice);
            if (item.Provenance != null && item.Provenance.Sources != null)
                foreach (SourceRef source in item.Provenance.Sources)
                    if (source != null && !string.IsNullOrWhiteSpace(source.DeviceId))
                        devices.Add(source.DeviceId);
            return devices;
        }

        static PermissionTier EffectiveTier(IdentityManifest identity)
        {
            PermissionTier ceiling = PermissionTier.Full;
            if (identity != null)
                Enum.TryParse(identity.PermissionCeiling ?? "", true, out ceiling);
            bool readOnly = identity != null &&
                string.Equals(identity.PermissionProfile, "read_only",
                    StringComparison.OrdinalIgnoreCase);
            if (readOnly || ceiling == PermissionTier.ReadOnly) return PermissionTier.ReadOnly;
            if (ceiling == PermissionTier.WorkspaceWrite) return PermissionTier.WorkspaceWrite;
            return PermissionTier.Full;
        }

        static void AppendHistory(string dataDir, MigrationDiffReport report)
        {
            string line = JsonSerializer.Serialize(new
            {
                report_id = report.ReportId,
                detected_at = report.DetectedAt,
                kind = report.Kind.ToString(),
                identity_preserved = report.IdentityPreserved,
                device_changed = report.DeviceChanged,
                model_changed = report.ModelChanged,
                permission_changed = report.PermissionChanged,
                intent_changes = report.IntentChanges.Count,
                memory_delta = report.MemoryCountDelta,
                capabilities_removed = report.CapabilitiesRemoved.Count,
                timeline_gap_seconds = report.TimelineGapSeconds,
                summary = report.Summary
            });
            File.AppendAllText(HistoryPath(dataDir), line + Environment.NewLine,
                new UTF8Encoding(false));
        }

        static T Read<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json)
                    : null;
            }
            catch { return null; }
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
            else File.Move(temp, path);
        }

        internal static JsonSerializerOptions JsonOptions { get { return Json; } }
    }

    public static class MigrationObservationStore
    {
        const int DefaultHours = 24;

        public static string PathFor(string dataDir)
        {
            return Path.Combine(dataDir, "migration-observation.json");
        }

        internal static MigrationObservation Start(string dataDir, MigrationDiffReport report,
            DateTimeOffset now, int hours)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (hours <= 0) hours = DefaultHours;
            var observation = new MigrationObservation
            {
                ObservationId = "obs_" + now.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff"),
                ReportId = report.ReportId,
                Kind = report.Kind,
                Status = MigrationObservationStatus.Active,
                StartedAt = now.ToString("o"),
                ExpiresAt = now.AddHours(hours).ToString("o"),
                CancelledAt = "",
                Reason = "watch " + report.Kind
            };
            Write(dataDir, observation);
            FaceAudit.Append(dataDir, "continuity", "migration_observation", "allowed",
                observation.ObservationId, observation.Reason);
            return observation;
        }

        public static MigrationObservation Current(string dataDir, DateTimeOffset now)
        {
            MigrationObservation observation = Read(dataDir);
            if (observation == null)
                return new MigrationObservation
                {
                    Status = MigrationObservationStatus.None,
                    StartedAt = "",
                    ExpiresAt = "",
                    CancelledAt = "",
                    Reason = "no migration observation"
                };
            if (observation.Status == MigrationObservationStatus.Active)
            {
                DateTimeOffset expires;
                if (DateTimeOffset.TryParse(observation.ExpiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out expires) && now >= expires)
                {
                    observation.Status = MigrationObservationStatus.Expired;
                    observation.Reason = "observation period elapsed";
                    Write(dataDir, observation);
                }
            }
            return observation;
        }

        public static bool Cancel(string dataDir, string observationId, string reason,
            DateTimeOffset now)
        {
            MigrationObservation observation = Read(dataDir);
            if (observation == null ||
                observation.Status != MigrationObservationStatus.Active ||
                string.IsNullOrWhiteSpace(observationId) ||
                !string.Equals(observation.ObservationId, observationId, StringComparison.Ordinal))
                return false;
            observation.Status = MigrationObservationStatus.Cancelled;
            observation.CancelledAt = now.ToString("o");
            observation.Reason = string.IsNullOrWhiteSpace(reason)
                ? "cancelled by operator"
                : reason;
            Write(dataDir, observation);
            FaceAudit.Append(dataDir, "continuity", "migration_observation_cancel", "allowed",
                observation.ObservationId, observation.Reason);
            return true;
        }

        static MigrationObservation Read(string dataDir)
        {
            try
            {
                string path = PathFor(dataDir);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<MigrationObservation>(
                        File.ReadAllText(path, Encoding.UTF8), MigrationDiffService.JsonOptions)
                    : null;
            }
            catch { return null; }
        }

        static void Write(string dataDir, MigrationObservation observation)
        {
            Directory.CreateDirectory(dataDir);
            string path = PathFor(dataDir);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(observation,
                MigrationDiffService.JsonOptions), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch { File.Copy(temp, path, true); File.Delete(temp); }
            }
            else File.Move(temp, path);
        }
    }
}
