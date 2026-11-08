using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AhzScribe.Contracts;

namespace AhzScribe.Body
{
    internal sealed class SafeSelfEvolution
    {
        static readonly string[] AllowedExact =
        {
            "README.md",
            "ahz-scribe-face/src/Theme.cs"
        };

        static readonly string[] ProtectedExact =
        {
            "AGENTS.md", "SOUL.md", "BOUNDARIES.md",
            "ahz-scribe-face/src/FaceForm.cs",
            "ahz-scribe-face/src/Program.cs",
            "ahz-scribe-face/src/FaceAudit.cs",
            "ahz-scribe-face/src/FaceSession.cs",
            "ahz-scribe-body/src/AhzScribe.Body.cs",
            "ahz-scribe-brain/src/SelfIterationPolicy.cs",
            "contracts/AhzScribe.Contracts.cs"
        };

        static readonly string[] ProtectedRoots =
        {
            "contracts",
            "ahz-scribe-body/src",
            "ahz-scribe-brain/src",
            "ahz-scribe-brain-host/src",
            "docs/acceptance",
            "docs/release"
        };

        static readonly string[] CopyExcludes =
        {
            ".git", "bin", "obj", "_build", "_local_artifacts", "data", "runtime",
            ".snapshots", "_body-data", "_broker-test"
        };

        readonly string _root;
        readonly string _stateRoot;
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public SafeSelfEvolution(string root, string stateRoot = "")
        {
            _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string rootKey = HashText(_root.ToLowerInvariant()).Substring(0, 16);
            _stateRoot = string.IsNullOrWhiteSpace(stateRoot)
                ? Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "AhzScribe", "self-evolution", rootKey)
                : Path.GetFullPath(stateRoot);
            Directory.CreateDirectory(_stateRoot);
        }

        public string Execute(string action, Dictionary<string, object> args, bool approved)
        {
            if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
            {
                string statusId = StringArg(args, "evolution_id");
                return statusId.Length == 0
                    ? "self-evolution proposals=" + Directory.GetFiles(_stateRoot, "*.json").Length
                    : JsonSerializer.Serialize(Load(statusId), _json);
            }
            if (!approved)
                throw new UnauthorizedAccessException("self_evolution requires explicit approval");
            switch ((action ?? "").ToLowerInvariant())
            {
                case "begin":
                    return JsonSerializer.Serialize(Begin(StringArg(args, "title"),
                        StringArg(args, "change"), StringListArg(args, "paths"),
                        StringArg(args, "identity_digest"),
                        StringArg(args, "permission_ceiling")), _json);
                case "apply":
                    return Apply(StringArg(args, "evolution_id"), StringMapArg(args, "files"));
                case "build":
                    return Build(StringArg(args, "evolution_id"));
                case "verify":
                    return Verify(StringArg(args, "evolution_id"),
                        StringArg(args, "identity_digest"));
                case "commit":
                    return Commit(StringArg(args, "evolution_id"));
                case "observe":
                    return Observe(StringArg(args, "evolution_id"),
                        IntArg(args, "hours", 24));
                case "pause":
                    return Pause(StringArg(args, "evolution_id"),
                        StringArg(args, "reason"));
                case "rollback":
                    return Rollback(StringArg(args, "evolution_id"),
                        StringArg(args, "reason"));
                case "confirm":
                    return Confirm(StringArg(args, "evolution_id"));
                default:
                    throw new InvalidOperationException("unknown self_evolution action: " + action);
            }
        }

        public SelfEvolutionRecord Begin(string title, string change,
            List<string> paths, string identityDigest, string permissionCeiling)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(change))
                throw new InvalidDataException("self-evolution title and change are required");
            if (paths == null || paths.Count == 0)
                throw new InvalidDataException("self-evolution paths are required");
            var normalized = new List<string>();
            foreach (string path in paths)
            {
                string rel = NormalizeAllowedPath(path);
                if (!File.Exists(Path.Combine(_root, rel)))
                    throw new InvalidDataException("self-evolution may edit existing files only: " + rel);
                if (!normalized.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    normalized.Add(rel);
            }

            string id = "evo_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 6);
            string stage = Path.Combine(_stateRoot, id, "stage");
            CopyDirectory(_root, stage, CopyExcludes);
            var record = new SelfEvolutionRecord
            {
                EvolutionId = id,
                Title = title.Trim(),
                Change = change.Trim(),
                Stage = SelfEvolutionStage.Approved,
                AffectedPaths = normalized,
                IdentityDigest = identityDigest ?? "",
                PermissionCeiling = permissionCeiling ?? "",
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
                LastReason = "approved proposal",
                StageRoot = stage
            };
            foreach (string path in normalized)
            {
                string full = Path.Combine(_root, path);
                record.BaselineHashes[path] = FileHash(full);
                record.BaselineContents[path] = File.ReadAllText(full, Encoding.UTF8);
            }
            foreach (string path in ProtectedFiles(_root))
                record.ProtectedHashes[path] = FileHash(Path.Combine(_root, path));
            Save(record);
            return record;
        }

        public string Apply(string id, Dictionary<string, string> files)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Approved, SelfEvolutionStage.Staged);
            if (files == null || files.Count == 0)
                throw new InvalidDataException("self-evolution apply requires file contents");
            foreach (var item in files)
            {
                string rel = NormalizeAllowedPath(item.Key);
                if (!record.AffectedPaths.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("path not declared in proposal: " + rel);
                string target = Path.Combine(record.StageRoot, rel);
                EnsureUnder(record.StageRoot, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, item.Value ?? "", new UTF8Encoding(false));
            }
            record.Stage = SelfEvolutionStage.Staged;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            record.LastReason = "isolated stage updated";
            Save(record);
            return "staged " + files.Count + " file(s) in " + record.StageRoot;
        }

        public string Build(string id)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Staged);
            string script = Path.Combine(record.StageRoot, "build.cmd");
            if (!File.Exists(script))
                throw new FileNotFoundException("build.cmd not found in isolated stage");
            CommandResult result = RunCommand(record.StageRoot, script);
            if (!result.Ok)
            {
                record.Stage = SelfEvolutionStage.Failed;
                record.LastReason = "build failed: " + result.Output;
                Save(record);
                try { Rollback(id, "automatic rollback after build failure"); } catch { }
                throw new InvalidOperationException("self-evolution build failed: " + result.Output);
            }
            record.Stage = SelfEvolutionStage.Built;
            record.LastReason = "build passed: " + Short(result.Output, 500);
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            return record.LastReason;
        }

        public string Verify(string id, string currentIdentityDigest)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Built);
            foreach (var item in record.ProtectedHashes)
            {
                string live = Path.Combine(_root, item.Key);
                string stage = Path.Combine(record.StageRoot, item.Key);
                if (!File.Exists(stage) || !string.Equals(FileHash(stage), item.Value,
                    StringComparison.OrdinalIgnoreCase))
                    throw VerificationFailure(record, "protected core changed in stage: " + item.Key);
                if (!File.Exists(live) || !string.Equals(FileHash(live), item.Value,
                    StringComparison.OrdinalIgnoreCase))
                    throw VerificationFailure(record, "protected core changed in workspace: " + item.Key);
            }
            if (!string.IsNullOrWhiteSpace(record.IdentityDigest) &&
                !string.Equals(record.IdentityDigest, currentIdentityDigest,
                    StringComparison.OrdinalIgnoreCase))
                throw VerificationFailure(record, "identity invariant digest changed");
            string verify = Path.Combine(record.StageRoot, "verify.cmd");
            if (!File.Exists(verify))
                throw new FileNotFoundException("verify.cmd not found in isolated stage");
            CommandResult result = RunCommand(record.StageRoot, verify);
            if (!result.Ok)
                throw VerificationFailure(record, "regression failed: " + result.Output);
            record.Stage = SelfEvolutionStage.Verified;
            record.LastReason = "protected core, identity and regression passed";
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            return record.LastReason;
        }

        Exception VerificationFailure(SelfEvolutionRecord record, string message)
        {
            record.Stage = SelfEvolutionStage.Failed;
            record.LastReason = message;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            try { Rollback(record.EvolutionId, "automatic rollback: " + message); } catch { }
            return new InvalidDataException(message);
        }

        public string Commit(string id)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Verified);
            foreach (var item in record.BaselineHashes)
            {
                string live = Path.Combine(_root, item.Key);
                if (!File.Exists(live) || !string.Equals(FileHash(live), item.Value,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("workspace changed after baseline: " + item.Key);
            }
            foreach (string path in record.AffectedPaths)
            {
                string source = Path.Combine(record.StageRoot, path);
                string target = Path.Combine(_root, path);
                EnsureUnder(_root, target);
                File.Copy(source, target, true);
            }
            record.Stage = SelfEvolutionStage.Applied;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            record.LastReason = "verified stage applied to workspace";
            Save(record);
            return "applied " + record.AffectedPaths.Count + " file(s)";
        }

        public string Observe(string id, int hours)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Applied);
            record.Stage = SelfEvolutionStage.Observing;
            record.ObservationExpiresAt = DateTimeOffset.UtcNow.AddHours(
                Math.Max(1, hours)).ToString("o");
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            record.LastReason = "observation started";
            Save(record);
            return "observing until " + record.ObservationExpiresAt;
        }

        public string Pause(string id, string reason)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Observing);
            record.Stage = SelfEvolutionStage.Paused;
            record.LastReason = reason ?? "observation paused";
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            return "observation paused";
        }

        public string Rollback(string id, string reason)
        {
            SelfEvolutionRecord record = Load(id);
            if (record.Stage == SelfEvolutionStage.Confirmed ||
                record.Stage == SelfEvolutionStage.RolledBack)
                throw new InvalidOperationException("self-evolution is already closed");
            bool liveChanged = record.Stage == SelfEvolutionStage.Applied ||
                record.Stage == SelfEvolutionStage.Observing ||
                record.Stage == SelfEvolutionStage.Paused;
            if (liveChanged)
            {
                foreach (string path in record.AffectedPaths)
                {
                    string target = Path.Combine(_root, path);
                    EnsureUnder(_root, target);
                    string content;
                    if (record.BaselineContents.TryGetValue(path, out content))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllText(target, content, new UTF8Encoding(false));
                    }
                }
            }
            foreach (var item in record.ProtectedHashes)
            {
                string live = Path.Combine(_root, item.Key);
                if (!File.Exists(live) || !string.Equals(FileHash(live), item.Value,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("protected core changed during rollback: " + item.Key);
            }
            record.Stage = SelfEvolutionStage.RolledBack;
            record.LastReason = reason ?? "rollback";
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            return "rolled back; live_changed=" + liveChanged;
        }

        public string Confirm(string id)
        {
            SelfEvolutionRecord record = Load(id);
            RequireStage(record, SelfEvolutionStage.Observing);
            DateTimeOffset expires;
            if (DateTimeOffset.TryParse(record.ObservationExpiresAt, out expires) &&
                expires < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("observation expired before confirmation");
            record.Stage = SelfEvolutionStage.Confirmed;
            record.LastReason = "observation confirmed";
            record.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            Save(record);
            return "confirmed";
        }

        string NormalizeAllowedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("empty path");
            string full = Path.GetFullPath(Path.Combine(_root, path));
            EnsureUnder(_root, full);
            string rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
            bool allowed = AllowedExact.Contains(rel, StringComparer.OrdinalIgnoreCase);
            if (!allowed && rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) &&
                rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !rel.StartsWith("docs/acceptance/", StringComparison.OrdinalIgnoreCase) &&
                !rel.StartsWith("docs/release/", StringComparison.OrdinalIgnoreCase))
                allowed = true;
            if (!allowed) throw new UnauthorizedAccessException("path outside self-evolution whitelist: " + rel);
            return rel;
        }

        SelfEvolutionRecord Load(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException("evolution_id is required");
            string path = Path.Combine(_stateRoot, id + ".json");
            if (!File.Exists(path))
                throw new FileNotFoundException("self-evolution record not found: " + id);
            return JsonSerializer.Deserialize<SelfEvolutionRecord>(
                File.ReadAllText(path, Encoding.UTF8), _json);
        }

        void Save(SelfEvolutionRecord record)
        {
            string path = Path.Combine(_stateRoot, record.EvolutionId + ".json");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, _json), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch { File.Copy(temp, path, true); File.Delete(temp); }
            }
            else File.Move(temp, path);
        }

        static void RequireStage(SelfEvolutionRecord record, params SelfEvolutionStage[] stages)
        {
            if (record == null || !stages.Contains(record.Stage))
                throw new InvalidOperationException("invalid self-evolution stage: " +
                    (record == null ? "missing" : record.Stage.ToString()));
        }

        static IEnumerable<string> ProtectedFiles(string root)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in ProtectedExact)
                if (File.Exists(Path.Combine(root, rel))) files.Add(rel);
            foreach (string relRoot in ProtectedRoots)
            {
                string fullRoot = Path.Combine(root, relRoot);
                if (!Directory.Exists(fullRoot)) continue;
                foreach (string file in Directory.GetFiles(fullRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                    files.Add(rel);
                }
            }
            return files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        static void CopyDirectory(string source, string target, string[] excludes)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                if (excludes.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                CopyDirectory(dir, Path.Combine(target, name), excludes);
            }
            foreach (string file in Directory.GetFiles(source))
            {
                if (excludes.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
        }

        static CommandResult RunCommand(string cwd, string script)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch { }
                    return new CommandResult { Ok = false, Output = "timeout" };
                }
                string output = (stdout + "\n" + stderr).Trim();
                return new CommandResult { Ok = p.ExitCode == 0, Output = Short(output, 4000) };
            }
        }

        static void EnsureUnder(string root, string target)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("path outside self-evolution root");
        }

        static string FileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }

        static string HashText(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
                    .ToLowerInvariant();
        }

        static string Short(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max) + "...";
        }

        static string StringArg(Dictionary<string, object> args, string name)
        {
            object value;
            return args != null && args.TryGetValue(name, out value) && value != null
                ? Convert.ToString(value) : "";
        }

        static int IntArg(Dictionary<string, object> args, string name, int fallback)
        {
            int value;
            return int.TryParse(StringArg(args, name), out value) ? value : fallback;
        }

        static List<string> StringListArg(Dictionary<string, object> args, string name)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null)
                return new List<string>();
            JsonElement json;
            if (value is JsonElement)
            {
                json = (JsonElement)value;
                return json.ValueKind == JsonValueKind.Array
                    ? json.EnumerateArray().Select(x => x.GetString() ?? "")
                        .Where(x => x.Length > 0).ToList()
                    : new List<string>();
            }
            IEnumerable<string> values = value as IEnumerable<string>;
            return values == null ? new List<string>() : values.ToList();
        }

        static Dictionary<string, string> StringMapArg(Dictionary<string, object> args, string name)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonElement json;
            if (value is JsonElement)
            {
                json = (JsonElement)value;
                return json.ValueKind == JsonValueKind.Object
                    ? json.EnumerateObject().ToDictionary(x => x.Name,
                        x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            var map = value as Dictionary<string, string>;
            if (map != null) return new Dictionary<string, string>(map,
                StringComparer.OrdinalIgnoreCase);
            var objects = value as Dictionary<string, object>;
            return objects == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : objects.ToDictionary(x => x.Key, x => Convert.ToString(x.Value) ?? "",
                    StringComparer.OrdinalIgnoreCase);
        }

        sealed class CommandResult
        {
            public bool Ok { get; set; }
            public string Output { get; set; }
        }
    }
}
