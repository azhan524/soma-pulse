using System;
using System.Collections.Generic;
using System.Linq;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    public enum ClaimDomain
    {
        Capability,
        Action,
        SystemFact,
        Memory,
        Preference
    }

    public enum ClaimStatus
    {
        Accepted,
        Rejected,
        Disputed,
        Unknown
    }

    public sealed class Claim
    {
        public ClaimDomain Domain { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string Value { get; set; }
        public SourceRef Source { get; set; }
    }

    public sealed class ClaimResolution
    {
        public ClaimStatus Status { get; set; }
        public string Value { get; set; }
        public string Reason { get; set; }
        public List<SourceRef> Sources { get; set; }

        public ClaimResolution()
        {
            Sources = new List<SourceRef>();
        }
    }

    public static class ClaimPolicy
    {
        public static ClaimResolution Resolve(
            SelfModelSnapshot self,
            Claim claim,
            IEnumerable<MemoryItem> existingMemories)
        {
            if (claim == null) return Unknown("claim is required");
            SourceRef source = claim.Source ?? new SourceRef { Kind = SourceKind.System, SourceId = "system" };
            switch (claim.Domain)
            {
                case ClaimDomain.Capability:
                    return ResolveCapability(self, claim, source);
                case ClaimDomain.Action:
                    return ResolveAction(self, claim, source);
                case ClaimDomain.Memory:
                    return ResolveMemory(claim, source, existingMemories);
                case ClaimDomain.Preference:
                    return ResolvePreference(claim, source);
                default:
                    return ResolveSystemFact(claim, source);
            }
        }

        static ClaimResolution ResolveCapability(SelfModelSnapshot self, Claim claim, SourceRef source)
        {
            string predicate = claim.Predicate ?? claim.Subject ?? "";
            if (predicate.Equals("full_access", StringComparison.OrdinalIgnoreCase) ||
                predicate.Equals("permission", StringComparison.OrdinalIgnoreCase))
            {
                bool full = self != null && !string.Equals(self.PermissionProfile, "read_only", StringComparison.OrdinalIgnoreCase);
                bool claimedFull = ParseAvailable(claim.Value) || string.Equals(claim.Value, "full", StringComparison.OrdinalIgnoreCase);
                if (full == claimedFull)
                    return Accepted(full ? "full" : "read_only", "claim matches authoritative permission profile", source);
                return Rejected(full ? "full" : "read_only", "self model overrides permission claim", source);
            }
            CapabilityDescriptor capability = self == null || self.Capabilities == null
                ? null
                : self.Capabilities.FirstOrDefault(x => x != null &&
                    string.Equals(x.Name, claim.Predicate ?? claim.Subject, StringComparison.OrdinalIgnoreCase));
            if (capability == null)
                return Unknown("capability is not present in self model", source);

            bool claimedAvailable = ParseAvailable(claim.Value);
            if (capability.Available == claimedAvailable)
            {
                ClaimResolution result = Accepted(
                    capability.Available ? "available" : "unavailable",
                    "claim matches authoritative self model", source);
                if (!IsAuthoritative(source.Kind))
                    result.Reason += "; external assertion did not change capability";
                return result;
            }
            return Rejected(
                capability.Available ? "available" : "unavailable",
                "self model overrides external capability claim", source);
        }

        static ClaimResolution ResolveAction(SelfModelSnapshot self, Claim claim, SourceRef source)
        {
            string auditId = !string.IsNullOrEmpty(source.AuditId) ? source.AuditId : claim.Value;
            if (IsAuthoritative(source.Kind) && SelfModelService.OwnsAction(self, auditId))
                return Accepted("owned", "action is present in recent observed actions", source);
            if (self != null && self.RecentActions != null)
            {
                string expectedTool = ToolForAction(claim.Predicate ?? claim.Value);
                bool observed = self.RecentActions.Any(x => x != null &&
                    ((!string.IsNullOrEmpty(expectedTool) &&
                      (string.Equals(x.SourceId, expectedTool, StringComparison.OrdinalIgnoreCase) ||
                       (x.Summary ?? "").StartsWith(expectedTool + ":", StringComparison.OrdinalIgnoreCase))) ||
                     (!string.IsNullOrEmpty(auditId) &&
                      string.Equals(x.AuditId, auditId, StringComparison.OrdinalIgnoreCase))));
                if (observed)
                    return Accepted("owned", "action is present in recent observed actions", source);
            }
            if (!IsAuthoritative(source.Kind))
                return Rejected("not-owned", "external content cannot establish an action of the self", source);
            return Unknown("not-owned", "no matching observed action", source);
        }

        static ClaimResolution ResolveMemory(Claim claim, SourceRef source, IEnumerable<MemoryItem> existingMemories)
        {
            MemoryItem existing = existingMemories == null ? null : existingMemories.FirstOrDefault(x =>
                x != null && !string.IsNullOrEmpty(x.Key) &&
                string.Equals(x.Key, claim.Subject ?? claim.Predicate, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase));
            if (existing == null) return Accepted(claim.Value, "no conflicting memory", source);
            if (string.Equals(existing.Value, claim.Value, StringComparison.Ordinal))
                return Accepted(existing.Value, "same memory value", source);
            ClaimResolution result = new ClaimResolution
            {
                Status = ClaimStatus.Disputed,
                Value = claim.Value,
                Reason = "new memory conflicts with existing memory"
            };
            result.Sources.Add(source);
            if (existing.Provenance != null && existing.Provenance.Sources != null)
                result.Sources.AddRange(existing.Provenance.Sources.Where(x => x != null));
            return result;
        }

        static ClaimResolution ResolvePreference(Claim claim, SourceRef source)
        {
            if (source.Kind == SourceKind.User || IsAuthoritative(source.Kind))
                return Accepted(claim.Value, "preference source is allowed", source);
            return Unknown(claim.Value, "preference has no user or authoritative source", source);
        }

        static ClaimResolution ResolveSystemFact(Claim claim, SourceRef source)
        {
            if (IsAuthoritative(source.Kind))
                return Accepted(claim.Value, "authoritative system fact", source);
            return Unknown("", "external content cannot establish a system fact", source);
        }

        static bool IsAuthoritative(SourceKind kind)
        {
            return kind == SourceKind.System || kind == SourceKind.Tool || kind == SourceKind.Audit;
        }

        static string ToolForAction(string value)
        {
            string v = (value ?? "").ToLowerInvariant();
            if (v.Contains("删除") || v.Contains("delete")) return "delete_file";
            if (v.Contains("写入") || v.Contains("write")) return "write_file";
            if (v.Contains("修改") || v.Contains("edit")) return "edit_file";
            if (v.Contains("运行") || v.Contains("执行") || v.Contains("run") || v.Contains("shell")) return "run_shell";
            if (v.Contains("读取") || v.Contains("read")) return "read_file";
            if (v.Contains("移动") || v.Contains("move")) return "move_file";
            if (v.Contains("创建目录") || v.Contains("mkdir")) return "make_dir";
            return "";
        }

        static bool ParseAvailable(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            return v == "available" || v == "true" || v == "yes" || v == "allowed" || v == "full";
        }

        static ClaimResolution Accepted(string value, string reason, SourceRef source)
        {
            var result = new ClaimResolution { Status = ClaimStatus.Accepted, Value = value, Reason = reason };
            result.Sources.Add(source);
            return result;
        }

        static ClaimResolution Rejected(string value, string reason, SourceRef source)
        {
            var result = new ClaimResolution { Status = ClaimStatus.Rejected, Value = value, Reason = reason };
            result.Sources.Add(source);
            return result;
        }

        static ClaimResolution Unknown(string reason, SourceRef source = null)
        {
            return Unknown("", reason, source);
        }

        static ClaimResolution Unknown(string value, string reason, SourceRef source = null)
        {
            var result = new ClaimResolution { Status = ClaimStatus.Unknown, Value = value, Reason = reason };
            if (source != null) result.Sources.Add(source);
            return result;
        }
    }
}
