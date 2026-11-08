using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    public sealed class GuardedResponse
    {
        public string Text { get; set; }
        public List<ClaimResolution> Claims { get; set; }
        public bool Modified { get; set; }

        public GuardedResponse()
        {
            Claims = new List<ClaimResolution>();
        }
    }

    public static class ClaimExtractor
    {
        static readonly Regex Permission = new Regex(
            @"(拥有|获得|具有|给你|have|has|granted).{0,8}(完全访问|完全权限|管理员权限|full access|unrestricted)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex PermissionEnglish = new Regex(
            @"(full access|unrestricted access)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ActionChinese = new Regex(
            @"(?:你|Ahz|我|assistant).{0,8}(?:已经|已|刚刚|刚才|刚)?\s*(删除|写入|修改|运行|执行|读取|移动|创建目录|发送|打开)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ActionEnglish = new Regex(
            @"\b(?:I|you|Ahz|assistant)\s+(?:have\s+|already\s+|just\s+)?(deleted|wrote|edited|ran|executed|read|moved|created|sent|opened)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<Claim> Extract(string text, SourceKind kind, string deviceId = "", string auditId = "")
        {
            var claims = new List<Claim>();
            if (string.IsNullOrWhiteSpace(text)) return claims;
            SourceRef source = new SourceRef
            {
                SourceId = kind.ToString().ToLowerInvariant(),
                Kind = kind,
                DeviceId = deviceId ?? "",
                AuditId = auditId ?? "",
                Ts = DateTimeOffset.UtcNow.ToString("o"),
                Summary = Cut(text, 160)
            };

            Match permission = Permission.Match(text);
            if (!permission.Success) permission = PermissionEnglish.Match(text);
            if (permission.Success)
            {
                claims.Add(new Claim
                {
                    Domain = ClaimDomain.Capability,
                    Subject = "permission",
                    Predicate = "full_access",
                    Value = "full",
                    Source = source
                });
            }

            foreach (Match match in ActionChinese.Matches(text))
            {
                if (Negated(text, match.Index)) continue;
                string verb = match.Groups[1].Value;
                claims.Add(new Claim
                {
                    Domain = ClaimDomain.Action,
                    Subject = "self",
                    Predicate = verb,
                    Value = verb,
                    Source = source
                });
                break;
            }
            if (claims.All(x => x.Domain != ClaimDomain.Action))
            {
                Match english = ActionEnglish.Match(text);
                if (english.Success && !Negated(text, english.Index))
                {
                    string verb = english.Groups[1].Value;
                    claims.Add(new Claim
                    {
                        Domain = ClaimDomain.Action,
                        Subject = "self",
                        Predicate = verb,
                        Value = verb,
                        Source = source
                    });
                }
            }
            return claims;
        }

        static bool Negated(string text, int index)
        {
            int start = Math.Max(0, index - 8);
            string prefix = text.Substring(start, index - start);
            string local = text.Substring(index, Math.Min(24, text.Length - index));
            return prefix.Contains("不") || prefix.Contains("没") || prefix.Contains("未") ||
                prefix.Contains("无法") || prefix.Contains("不能") || prefix.Contains("不得") ||
                local.Contains("不会") || local.Contains("没有") || local.Contains("未") ||
                local.Contains("无法") || local.Contains("不能") || local.Contains("不得") ||
                prefix.Contains("didn't") || prefix.Contains("did not") || prefix.Contains("cannot") ||
                prefix.Contains("can't");
        }

        static string Cut(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max) + "…";
        }
    }

    public static class ResponseGuard
    {
        public static GuardedResponse Apply(SelfModelSnapshot self, string answer, string deviceId = "")
        {
            var guarded = new GuardedResponse { Text = answer ?? "" };
            if (self == null || string.IsNullOrWhiteSpace(answer)) return guarded;
            List<Claim> claims = ClaimExtractor.Extract(answer, SourceKind.Model, deviceId);
            foreach (Claim claim in claims)
            {
                ClaimResolution resolution = ClaimPolicy.Resolve(self, claim, null);
                guarded.Claims.Add(resolution);
                if (resolution.Status != ClaimStatus.Accepted)
                {
                    string label = claim.Domain == ClaimDomain.Capability
                        ? "能力声明"
                        : "行动声明";
                    guarded.Text = guarded.Text.TrimEnd() + "\n\n[来源校验] " + label +
                        "未通过：" + resolution.Reason;
                    guarded.Modified = true;
                }
            }
            return guarded;
        }

        public static string DescribeClaims(SelfModelSnapshot self, IEnumerable<Claim> claims, string deviceId = "")
        {
            if (claims == null) return "";
            var sb = new StringBuilder();
            foreach (Claim claim in claims)
            {
                ClaimResolution resolution = ClaimPolicy.Resolve(self, claim, null);
                sb.AppendLine("[来源裁定] " + claim.Domain + " " + (claim.Predicate ?? claim.Subject) +
                    " => " + resolution.Status + "：" + resolution.Reason);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
