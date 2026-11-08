using System;
using System.IO;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Brain-side self-iteration gate. Write tools stay closed until propose_upgrade is approved.
    /// </summary>
    public sealed class SelfIterationPolicy
    {
        static readonly string[] Blocked =
        {
            "AGENTS.md", "SOUL.md", "BOUNDARIES.md", "config.json", "sessions.json",
            "tests/acceptance.md", "devtools/bench", "devtools/harden-audit.ps1",
            "devtools/install-audit-broker.ps1", "devtools/uninstall-audit-broker.ps1",
            "audit", "state", ".snapshots"
        };

        public string Root { get; private set; }
        public bool Active { get; private set; }

        public void Begin(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root is required");
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Active = true;
        }

        public void Reset() { Active = false; Root = null; }

        public bool Check(string path, out string why)
        {
            why = "";
            if (!Active || string.IsNullOrEmpty(Root)) { why = "propose_upgrade_not_approved"; return false; }
            if (string.IsNullOrEmpty(path)) { why = "empty_path"; return false; }
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
            string prefix = Root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { why = "outside_workspace"; return false; }
            string rel = full.Substring(prefix.Length).Replace('\\', '/');
            foreach (string b in Blocked)
                if (string.Equals(rel, b, StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase))
                { why = "protected:" + b; return false; }
            why = "direct_workspace_write_blocked; use self_evolution pipeline";
            return false;
        }
    }
}
