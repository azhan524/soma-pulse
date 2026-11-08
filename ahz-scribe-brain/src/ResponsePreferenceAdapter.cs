using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Deterministic enforcement for response-style preferences that the user explicitly corrected.
    /// The persisted rule explains the change; this adapter makes the change observable in the answer.
    /// </summary>
    public static class ResponsePreferenceAdapter
    {
        static readonly Regex Emoji = new Regex(
            @"\uD83C[\uDDE6-\uDDFF\uDF00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDEFF]|[\u200D\u2600-\u27BF\uFE0F]+",
            RegexOptions.Compiled);

        static readonly Regex SpacesBeforeNewline = new Regex(@"[ \t]+\r?\n", RegexOptions.Compiled);
        static readonly Regex ExtraSpaces = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        static readonly Regex SpaceBeforePunctuation = new Regex(@"\s+([。！？，,.!?])", RegexOptions.Compiled);

        public static bool ContainsEmoji(string text)
        {
            return !string.IsNullOrEmpty(text) && Emoji.IsMatch(text);
        }

        public static string Apply(string text, ExperienceStore experience)
        {
            if (string.IsNullOrEmpty(text) || experience == null || !experience.RequiresNoEmoji())
                return text;

            string cleaned = Emoji.Replace(text, "");
            cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
            cleaned = SpacesBeforeNewline.Replace(cleaned, "\n");
            cleaned = ExtraSpaces.Replace(cleaned, " ").Trim();
            if (cleaned.Length == 0) cleaned = "已按你的偏好省略表情内容。";

            experience.RecordDecision(new DecisionTrace
            {
                TraceId = "trace_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Trigger = "final_answer",
                AppliedRules = new List<string> { ExperienceStore.NoEmojiRuleName },
                Decision = "sanitize_emoji",
                Reason = "user_preference:no_emoji",
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = ExperienceStore.NoEmojiRuleName,
                        Kind = SourceKind.User,
                        Ts = DateTimeOffset.UtcNow.ToString("o"),
                        Summary = "persisted explicit response-style preference"
                    }
                },
                Ts = DateTimeOffset.UtcNow.ToString("o")
            });
            return cleaned;
        }
    }
}
