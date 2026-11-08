using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    public sealed class ExperienceStore
    {
        public const string NoEmojiRuleName = "response_no_emoji";
        readonly string _experiencePath;
        readonly string _rulePath;
        readonly string _tracePath;
        readonly object _gate = new object();
        readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public ExperienceStore(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir is required");
            Directory.CreateDirectory(dataDir);
            _experiencePath = Path.Combine(dataDir, "experiences.jsonl");
            _rulePath = Path.Combine(dataDir, "adaptation-rules.jsonl");
            _tracePath = Path.Combine(dataDir, "decision-traces.jsonl");
        }

        public bool IsReminderCorrection(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.ToLowerInvariant();
            bool mentionsReminder = value.Contains("提醒") || value.Contains("remind");
            bool asksEvidence = value.Contains("先确认") || value.Contains("先核对") ||
                value.Contains("先查") || value.Contains("确认后") || value.Contains("核对后") ||
                value.Contains("别直接") || value.Contains("不要直接") ||
                value.Contains("check first") || value.Contains("verify first") || value.Contains("without checking");
            bool corrects = value.Contains("错") || value.Contains("不对") || value.Contains("以后") ||
                value.Contains("下次") || value.Contains("应该") || value.Contains("should") ||
                value.Contains("don't") || value.Contains("do not");
            return mentionsReminder && asksEvidence && corrects;
        }

        public bool IsNoEmojiPreferenceCorrection(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.ToLowerInvariant();
            bool mentionsEmoji = value.Contains("emoji") || value.Contains("表情符号") ||
                value.Contains("表情") || value.Contains("颜文字");
            bool prohibits = value.Contains("不要") || value.Contains("别") ||
                value.Contains("禁止") || value.Contains("不许") || value.Contains("不想看") ||
                value.Contains("don't") || value.Contains("do not") || value.Contains("without");
            bool future = value.Contains("以后") || value.Contains("下次") ||
                value.Contains("今后") || value.Contains("回答") || value.Contains("回复") ||
                value.Contains("reply") || value.Contains("answer");
            return mentionsEmoji && prohibits && future;
        }

        public bool RecordNoEmojiPreferenceCorrection(string feedback, string auditId, string deviceId = "")
        {
            if (!IsNoEmojiPreferenceCorrection(feedback)) return false;
            string now = DateTimeOffset.UtcNow.ToString("o");
            string experienceId = "exp_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 6);
            var sources = new List<SourceRef>
            {
                new SourceRef
                {
                    SourceId = "user_preference_correction",
                    Kind = SourceKind.User,
                    DeviceId = deviceId ?? "",
                    AuditId = auditId ?? "",
                    Ts = now,
                    Summary = feedback
                },
                new SourceRef
                {
                    SourceId = "preference_correction_audit",
                    Kind = SourceKind.Audit,
                    DeviceId = deviceId ?? "",
                    AuditId = auditId ?? "",
                    Ts = now,
                    Summary = "user corrected final response style"
                }
            };
            var experience = new ExperienceRecord
            {
                ExperienceId = experienceId,
                Situation = "final assistant response style",
                ActionTaken = "used emoji",
                Outcome = "user_corrected",
                UserFeedback = feedback,
                Sources = sources,
                AppliedPolicies = new List<string> { "response_style" },
                Confidence = 1.0,
                Ts = now,
                Status = "active"
            };
            var rule = new AdaptationRule
            {
                RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Name = NoEmojiRuleName,
                Scope = "response_style",
                Action = "strip_emoji",
                Subject = "final_answer",
                EvidenceIds = new List<string> { experienceId },
                ObservationCount = 1,
                CreatedAt = now,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToString("o"),
                Enabled = true,
                Source = auditId ?? ""
            };
            lock (_gate)
            {
                Append(_experiencePath, experience);
                Append(_rulePath, rule);
            }
            return true;
        }

        public bool RequiresNoEmoji()
        {
            lock (_gate) return LatestActiveRule("response_style", "strip_emoji", NoEmojiRuleName) != null;
        }

        public string PreferenceContext()
        {
            return RequiresNoEmoji()
                ? "已确认的用户偏好：最终回答不要使用 emoji 或表情符号。此偏好来自用户明确纠正。"
                : "";
        }

        public void ResetResponsePreferenceRules()
        {
            string now = DateTimeOffset.UtcNow.ToString("o");
            List<AdaptationRule> active;
            lock (_gate)
            {
                active = Read<AdaptationRule>(_rulePath)
                    .Where(x => x != null &&
                        string.Equals(x.Scope, "response_style", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Action, "strip_emoji", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Name + "|" + x.Subject, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.OrderBy(y => y.CreatedAt, StringComparer.OrdinalIgnoreCase).Last())
                    .Where(IsActive)
                    .ToList();
            }
            foreach (AdaptationRule rule in active)
            {
                var disabled = new AdaptationRule
                {
                    RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Name = rule.Name,
                    Scope = rule.Scope,
                    Action = rule.Action,
                    Subject = rule.Subject,
                    TriggerKey = rule.TriggerKey,
                    EvidenceIds = new List<string>(),
                    ObservationCount = rule.ObservationCount,
                    CreatedAt = now,
                    ExpiresAt = now,
                    Enabled = false,
                    Source = "user_reset"
                };
                lock (_gate) Append(_rulePath, disabled);
            }
        }

        public List<AdaptationRule> GetActiveRules()
        {
            lock (_gate)
            {
                return Read<AdaptationRule>(_rulePath)
                    .Where(x => x != null)
                    .GroupBy(RuleIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.OrderBy(y => y.CreatedAt, StringComparer.OrdinalIgnoreCase).Last())
                    .Where(IsActive)
                    .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public bool RecordReminderCorrection(string feedback, string auditId, string deviceId = "")
        {
            if (!IsReminderCorrection(feedback)) return false;
            string now = DateTimeOffset.UtcNow.ToString("o");
            string experienceId = "exp_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var sources = new List<SourceRef>
            {
                new SourceRef
                {
                    SourceId = "user_correction",
                    Kind = SourceKind.User,
                    DeviceId = deviceId ?? "",
                    AuditId = auditId ?? "",
                    Ts = now,
                    Summary = feedback
                },
                new SourceRef
                {
                    SourceId = "correction_audit",
                    Kind = SourceKind.Audit,
                    DeviceId = deviceId ?? "",
                    AuditId = auditId ?? "",
                    Ts = now,
                    Summary = "user feedback audit"
                }
            };
            var experience = new ExperienceRecord
            {
                ExperienceId = experienceId,
                Situation = "intent_due reminder",
                ActionTaken = "message",
                Outcome = "user_corrected",
                UserFeedback = feedback,
                Sources = sources,
                AppliedPolicies = new List<string> { "intent_reminder" },
                Confidence = 1.0,
                Ts = now,
                Status = "active"
            };
            var rule = new AdaptationRule
            {
                RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Name = "intent_requires_evidence",
                Scope = "intent_due",
                Action = "require_evidence",
                EvidenceIds = new List<string> { experienceId },
                ObservationCount = 1,
                CreatedAt = now,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToString("o"),
                Enabled = true,
                Source = auditId ?? ""
            };
            lock (_gate)
            {
                Append(_experiencePath, experience);
                Append(_rulePath, rule);
            }
            return true;
        }

        public bool RequiresIntentEvidence()
        {
            lock (_gate)
                return LatestActiveRule("intent_due", "require_evidence", "intent_requires_evidence") != null;
        }

        public void ResetIntentEvidenceRule(string reason)
        {
            string now = DateTimeOffset.UtcNow.ToString("o");
            var rule = new AdaptationRule
            {
                RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Name = "intent_requires_evidence",
                Scope = "intent_due",
                Action = "require_evidence",
                EvidenceIds = new List<string>(),
                ObservationCount = 0,
                CreatedAt = now,
                ExpiresAt = now,
                Enabled = false,
                Source = reason ?? "user_reset"
            };
            lock (_gate) Append(_rulePath, rule);
        }

        public void RecordDecision(DecisionTrace trace)
        {
            if (trace == null) return;
            lock (_gate) Append(_tracePath, trace);
        }

        public void RecordToolFailure(string tool, Dictionary<string, object> args, string error,
            string auditId, string deviceId = "")
        {
            if (string.IsNullOrWhiteSpace(tool)) return;
            string key = TriggerKey(tool, args);
            string now = DateTimeOffset.UtcNow.ToString("o");
            string experienceId = "exp_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var experience = new ExperienceRecord
            {
                ExperienceId = experienceId,
                Situation = "tool failure: " + tool,
                ActionTaken = tool,
                Outcome = "error: " + (error ?? ""),
                UserFeedback = "",
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = tool,
                        Kind = SourceKind.Tool,
                        DeviceId = deviceId ?? "",
                        AuditId = auditId ?? "",
                        Ts = now,
                        Summary = error ?? ""
                    }
                },
                AppliedPolicies = new List<string> { "tool_failure" },
                Confidence = 1.0,
                Ts = now,
                Status = "active"
            };
            var rule = new AdaptationRule
            {
                RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Name = "tool_identical_retry_blocked",
                Scope = "tool_call",
                Action = "block_identical_retry",
                Subject = tool,
                TriggerKey = key,
                EvidenceIds = new List<string> { experienceId },
                ObservationCount = 1,
                CreatedAt = now,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("o"),
                Enabled = true,
                Source = auditId ?? ""
            };
            lock (_gate)
            {
                Append(_experiencePath, experience);
                Append(_rulePath, rule);
            }
        }

        public string ToolRetryBlockReason(string tool, Dictionary<string, object> args)
        {
            if (string.IsNullOrWhiteSpace(tool)) return "";
            string key = TriggerKey(tool, args);
            lock (_gate)
            {
                AdaptationRule rule = Read<AdaptationRule>(_rulePath)
                    .Where(x => x != null && string.Equals(x.Scope, "tool_call", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Action, "block_identical_retry", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Subject, tool, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.TriggerKey, key, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.CreatedAt, StringComparer.OrdinalIgnoreCase)
                    .LastOrDefault();
                if (rule == null || !rule.Enabled) return "";
                DateTimeOffset expires;
                if (DateTimeOffset.TryParse(rule.ExpiresAt, out expires) && expires < DateTimeOffset.UtcNow) return "";
                return "identical " + tool + " call previously failed; change arguments or wait for rule reset";
            }
        }

        public void ResetToolRules()
        {
            string now = DateTimeOffset.UtcNow.ToString("o");
            List<AdaptationRule> rules;
            lock (_gate) rules = Read<AdaptationRule>(_rulePath);
            foreach (AdaptationRule active in rules.Where(x => x != null && x.Enabled &&
                string.Equals(x.Scope, "tool_call", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Action, "block_identical_retry", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Subject + "|" + x.TriggerKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last()))
            {
                var disabled = new AdaptationRule
                {
                    RuleId = "rule_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Name = active.Name,
                    Scope = active.Scope,
                    Action = active.Action,
                    Subject = active.Subject,
                    TriggerKey = active.TriggerKey,
                    EvidenceIds = new List<string>(),
                    ObservationCount = active.ObservationCount,
                    CreatedAt = now,
                    ExpiresAt = now,
                    Enabled = false,
                    Source = "user_reset"
                };
                lock (_gate) Append(_rulePath, disabled);
            }
        }

        public List<ExperienceRecord> LoadExperiences()
        {
            lock (_gate) return Read<ExperienceRecord>(_experiencePath);
        }

        public List<AdaptationRule> LoadRules()
        {
            lock (_gate) return Read<AdaptationRule>(_rulePath);
        }

        public List<DecisionTrace> LoadTraces()
        {
            lock (_gate) return Read<DecisionTrace>(_tracePath);
        }

        AdaptationRule LatestActiveRule(string scope, string action, string name)
        {
            AdaptationRule rule = Read<AdaptationRule>(_rulePath)
                .Where(x => x != null &&
                    string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Action, action, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(name) || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.CreatedAt, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();
            return IsActive(rule) ? rule : null;
        }

        static string RuleIdentity(AdaptationRule rule)
        {
            return (rule.Name ?? "") + "|" + (rule.Scope ?? "") + "|" + (rule.Action ?? "") +
                "|" + (rule.Subject ?? "") + "|" + (rule.TriggerKey ?? "");
        }

        static bool IsActive(AdaptationRule rule)
        {
            if (rule == null || !rule.Enabled) return false;
            DateTimeOffset expires;
            return !DateTimeOffset.TryParse(rule.ExpiresAt, out expires) || expires >= DateTimeOffset.UtcNow;
        }

        void Append<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, JsonSerializer.Serialize(value, _json) + Environment.NewLine, new UTF8Encoding(false));
        }

        static string TriggerKey(string tool, Dictionary<string, object> args)
        {
            string normalized = "";
            if (args != null)
                normalized = string.Join("&", args.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => x.Key + "=" + Convert.ToString(x.Value)));
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes((tool ?? "") + "|" + normalized))).ToLowerInvariant();
        }

        List<T> Read<T>(string path)
        {
            var result = new List<T>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    T value = JsonSerializer.Deserialize<T>(line, _json);
                    if (value != null) result.Add(value);
                }
                catch { }
            }
            return result;
        }
    }
}
