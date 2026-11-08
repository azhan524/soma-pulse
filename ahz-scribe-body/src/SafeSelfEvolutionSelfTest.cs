using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AhzScribe.Body
{
    internal static class SafeSelfEvolutionSelfTest
    {
        public static bool Run()
        {
            string baseDir = Path.Combine(Path.GetTempPath(),
                "ahz-self-evolution-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(baseDir, "root");
            string state = Path.Combine(baseDir, "state");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(Path.Combine(root, "ahz-scribe-face", "src"));
            Directory.CreateDirectory(Path.Combine(root, "contracts"));
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "core", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "README.md"), "good", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "ahz-scribe-face", "src", "Theme.cs"),
                "theme-a", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "contracts", "Core.cs"),
                "protected", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "build.cmd"),
                "@echo build-ok\r\nexit /b 0\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "verify.cmd"),
                "@findstr /C:\"FAIL\" README.md >nul 2>&1\r\n" +
                "@if errorlevel 1 exit /b 0\r\n@exit /b 1\r\n", new UTF8Encoding(false));
            try
            {
                var manager = new SafeSelfEvolution(root, state);
                bool protectedBlocked = false;
                try
                {
                    manager.Begin("bad", "touch core",
                        new List<string> { "AGENTS.md" }, "identity-a", "Full");
                }
                catch (UnauthorizedAccessException) { protectedBlocked = true; }
                bool approvalBlocked = false;
                try
                {
                    manager.Execute("begin", Args(new Dictionary<string, object>
                    {
                        ["title"] = "bad", ["change"] = "bad",
                        ["paths"] = new List<string> { "README.md" }
                    }), false);
                }
                catch (UnauthorizedAccessException) { approvalBlocked = true; }

                string protectedBefore = Hash(Path.Combine(root, "contracts", "Core.cs"));
                var success = manager.Begin("safe", "docs and theme",
                    new List<string> { "README.md", "ahz-scribe-face/src/Theme.cs" },
                    "identity-a", "Full");
                manager.Apply(success.EvolutionId, new Dictionary<string, string>
                {
                    ["README.md"] = "better",
                    ["ahz-scribe-face/src/Theme.cs"] = "theme-b"
                });
                bool isolated = File.ReadAllText(Path.Combine(root, "README.md")) == "good";
                manager.Build(success.EvolutionId);
                manager.Verify(success.EvolutionId, "identity-a");
                manager.Commit(success.EvolutionId);
                manager.Observe(success.EvolutionId, 24);
                manager.Confirm(success.EvolutionId);
                bool successOk = File.ReadAllText(Path.Combine(root, "README.md")) == "better" &&
                    File.ReadAllText(Path.Combine(root, "ahz-scribe-face", "src", "Theme.cs")) == "theme-b" &&
                    Hash(Path.Combine(root, "contracts", "Core.cs")) == protectedBefore &&
                    Status(manager, success.EvolutionId) == "Confirmed";

                var failed = manager.Begin("regression failure", "must rollback",
                    new List<string> { "README.md" }, "identity-a", "Full");
                manager.Apply(failed.EvolutionId, new Dictionary<string, string>
                {
                    ["README.md"] = "FAIL"
                });
                manager.Build(failed.EvolutionId);
                bool autoRollback = false;
                try { manager.Verify(failed.EvolutionId, "identity-a"); }
                catch (InvalidDataException) { autoRollback = true; }
                bool failedOk = autoRollback &&
                    Status(manager, failed.EvolutionId) == "RolledBack" &&
                    File.ReadAllText(Path.Combine(root, "README.md")) == "better";

                var identity = manager.Begin("identity mismatch", "must rollback",
                    new List<string> { "README.md" }, "identity-a", "Full");
                manager.Apply(identity.EvolutionId, new Dictionary<string, string>
                {
                    ["README.md"] = "identity-change"
                });
                manager.Build(identity.EvolutionId);
                bool identityBlocked = false;
                try { manager.Verify(identity.EvolutionId, "identity-b"); }
                catch (InvalidDataException) { identityBlocked = true; }
                bool identityOk = identityBlocked &&
                    Status(manager, identity.EvolutionId) == "RolledBack" &&
                    File.ReadAllText(Path.Combine(root, "README.md")) == "better";

                var observed = manager.Begin("observed", "observation rollback",
                    new List<string> { "README.md" }, "identity-a", "Full");
                manager.Apply(observed.EvolutionId, new Dictionary<string, string>
                {
                    ["README.md"] = "temporary"
                });
                manager.Build(observed.EvolutionId);
                manager.Verify(observed.EvolutionId, "identity-a");
                manager.Commit(observed.EvolutionId);
                manager.Observe(observed.EvolutionId, 24);
                File.WriteAllText(Path.Combine(root, "README.md"), "anomaly",
                    new UTF8Encoding(false));
                manager.Rollback(observed.EvolutionId, "injected observation anomaly");
                bool observationRollback = Status(manager, observed.EvolutionId) == "RolledBack" &&
                    File.ReadAllText(Path.Combine(root, "README.md")) == "better";

                bool ok = protectedBlocked && approvalBlocked && isolated && successOk &&
                    failedOk && identityOk && observationRollback;
                Console.WriteLine("SAFE SELF-EVOLUTION: " + (ok ? "PASS" : "FAIL") +
                    " protected=" + protectedBlocked +
                    " approval=" + approvalBlocked +
                    " isolated=" + isolated +
                    " success=" + successOk +
                    " auto_rollback=" + failedOk +
                    " identity_guard=" + identityOk +
                    " observation_rollback=" + observationRollback);
                return ok;
            }
            finally
            {
                try { Directory.Delete(baseDir, true); } catch { }
            }
        }

        static Dictionary<string, object> Args(Dictionary<string, object> args)
        {
            return args;
        }

        static string Status(SafeSelfEvolution manager, string id)
        {
            string json = manager.Execute("status", new Dictionary<string, object>
            {
                ["evolution_id"] = id
            }, false);
            using (JsonDocument doc = JsonDocument.Parse(json))
                return doc.RootElement.GetProperty("Stage").GetString();
        }

        static string Hash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(path))
                return Convert.ToHexString(sha.ComputeHash(fs));
        }
    }
}
