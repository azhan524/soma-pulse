using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// S2 structured extraction: 0-3 memories, low temperature is a model concern.
    /// Empty array is a valid result.
    /// </summary>
    public sealed class MemoryExtractor
    {
        readonly IBrainModel _model;
        readonly Action<string> _log;
        public ResourceBudget Budget { get; set; }

        public MemoryExtractor(IBrainModel model, Action<string> log = null) { _model = model; _log = log; }

        public async Task<List<MemoryItem>> ExtractAsync(string transcript, string sourceAuditId, CancellationToken ct)
        {
            var result = new List<MemoryItem>();
            if (string.IsNullOrWhiteSpace(transcript)) return result;
            string system = "你只做长期记忆提取。从对话中提取 0-3 条真正值得以后记住的内容；空数组 [] 是合法且常见的输出。优先保留：用户姓名、长期偏好、长期项目、明确承诺、重要个人事实。必须为每条选择标签：#pref（偏好/事实）、#intent（承诺）、#epi（经历）、#emotion（有证据的情绪）、#drive（内省）。输出 JSON 数组，每条 {\"tag\":\"pref|intent|epi|emotion|drive\",\"key\":\"短键\",\"value\":\"内容\",\"summary\":\"一句摘要\"}。寒暄、临时状态、模型推测不要写入。";
            try
            {
                string raw = await CompleteWithRetryAsync(system, transcript, ct).ConfigureAwait(false);
                try { _log?.Invoke(raw ?? ""); } catch { }
                int a = raw.IndexOf('[');
                int b = raw.LastIndexOf(']');
                if (a < 0 || b <= a) return result;
                using (JsonDocument doc = JsonDocument.Parse(raw.Substring(a, b - a + 1)))
                {
                    int n = 0;
                    foreach (JsonElement e in doc.RootElement.EnumerateArray())
                    {
                        if (n >= 3) break;
                        string tag = e.TryGetProperty("tag", out var tg) ? (tg.GetString() ?? "pref") : (e.TryGetProperty("kind", out var k0) ? (k0.GetString() ?? "pref") : "pref");
                        string kind = tag == "epi" || tag == "episodic" ? "episodic" : (tag == "intent" ? "intent" : "semantic");
                        string key = e.TryGetProperty("key", out var kk) ? (kk.GetString() ?? "") : "";
                        string value = e.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
                        string summary = e.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
                        result.Add(new MemoryItem
                        {
                            Kind = kind,
                            Tag = tag,
                            Key = key.Length > 120 ? key.Substring(0, 120) : key,
                            Value = value.Length > 1000 ? value.Substring(0, 1000) : value,
                            Summary = summary,
                            SourceAuditId = sourceAuditId,
                            Status = "active",
                            Provenance = ModelProvenance(sourceAuditId, value)
                        });
                        n++;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            if (result.Count == 0) result.AddRange(Heuristic(transcript, sourceAuditId));
            return result;
        }

        async Task<string> CompleteWithRetryAsync(string system, string transcript, CancellationToken ct)
        {
            int maxAttempts = Budget == null ? 1 : Budget.MaxModelAttempts;
            bool retried = false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    string answer = await _model.CompleteAsync(system, transcript, ct).ConfigureAwait(false);
                    if (retried) Budget.RecordRetryRecovered("memory_extractor");
                    return answer;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (Budget == null || attempt >= maxAttempts || ct.IsCancellationRequested ||
                        !IsTransient(ex))
                        throw;
                    Budget.RecordRetry("memory_extractor");
                    retried = true;
                    await Task.Delay(Budget.RetryDelay(attempt), ct).ConfigureAwait(false);
                }
            }
        }

        static bool IsTransient(Exception ex)
        {
            return ex is TimeoutException || ex is TaskCanceledException ||
                ex is System.IO.IOException || ex is System.Net.Http.HttpRequestException;
        }

        static List<MemoryItem> Heuristic(string transcript, string sourceAuditId)
        {
            var list = new List<MemoryItem>();
            if (string.IsNullOrEmpty(transcript)) return list;
            string[] patterns = { "我叫", "我喜欢", "我不喜欢", "我正在做", "我承诺", "我的猫叫" };
            foreach (string rawLine in transcript.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("你:", StringComparison.Ordinal) && !line.StartsWith("你：", StringComparison.Ordinal)) continue;
                foreach (string p in patterns)
                {
                    int i = line.IndexOf(p, StringComparison.Ordinal);
                    if (i < 0) continue;
                    string value = line.Substring(i).Trim();
                    if (value.Length > 300) value = value.Substring(0, 300);
                    list.Add(new MemoryItem
                    {
                        Kind = p == "我承诺" ? "episodic" : "semantic",
                        Key = p,
                        Value = value,
                        Summary = value,
                        SourceAuditId = sourceAuditId,
                        Status = "active",
                        Provenance = ModelProvenance(sourceAuditId, value)
                    });
                    break;
                }
                if (list.Count >= 3) break;
            }
            return list;
        }

        static Provenance ModelProvenance(string auditId, string summary)
        {
            return new Provenance
            {
                Confidence = 0.6,
                DirectlyObserved = false,
                Inferred = true,
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = "memory_extractor",
                        Kind = SourceKind.Model,
                        AuditId = auditId ?? "",
                        Ts = DateTimeOffset.UtcNow.ToString("o"),
                        Summary = summary ?? ""
                    }
                }
            };
        }
    }
}
