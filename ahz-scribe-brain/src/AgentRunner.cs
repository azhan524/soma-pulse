using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Brain-side task runner v0: retrieve memory, call the model, return text.
    /// It deliberately has no tool loop and no body access yet.
    /// </summary>
    public sealed class AgentRunner
    {
        readonly IMemoryStore _memory;
        readonly IBrainModel _model;
        public string BodySummary { get; set; }
        public string UiSummary { get; set; }
        public string SelfModelSummary { get; set; }
        public string ClaimContext { get; set; }
        public string PreferenceContext { get; set; }
        public string IdentityContext { get; set; }
        public string AutobiographyContext { get; set; }
        public AttentionStore Attention { get; set; }
        public GoalStore Goals { get; set; }
        public ResourceBudget Budget { get; set; }

        public AgentRunner(IMemoryStore memory, IBrainModel model)
        {
            _memory = memory; _model = model;
        }

        public async Task<string> RunAsync(string userText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userText)) return "";
            List<MemoryItem> hits = await _memory.RetrieveAsync(userText, 12, ct).ConfigureAwait(false);
            if (Attention != null)
                hits = Attention.InfluenceRetrieval(userText, hits, 6);
            string memory = "";
            if (hits != null && hits.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("相关记忆：");
                foreach (MemoryItem m in hits)
                    sb.AppendLine("- " + (m.Kind ?? "") + " / " + (m.Key ?? "") + " = " + (m.Value ?? ""));
                memory = sb.ToString();
            }
            string system = "你是 Ahz Scribe。只根据用户输入和给出的记忆回答；记忆不足就说明不足。禁止声称执行了任何没有出现在工具结果或记忆里的动作。你不能直接清除任务、写记忆或改变状态；用户要清任务时应提示发送 /intent-clear，要长期记住信息时应提示发送 /remember <kind> <key> <value>。";
            if (!string.IsNullOrEmpty(BodySummary)) system += "\\n" + (BodySummary.Length > 300 ? BodySummary.Substring(0, 300) : BodySummary);
            if (!string.IsNullOrEmpty(UiSummary)) system += "\\n当前 UI 状态：\\n" + (UiSummary.Length > 600 ? UiSummary.Substring(0, 600) : UiSummary);
            if (!string.IsNullOrEmpty(SelfModelSummary)) system += "\\n" + SelfModelSummary;
            if (!string.IsNullOrEmpty(ClaimContext)) system += "\\n" + ClaimContext;
            if (!string.IsNullOrEmpty(PreferenceContext)) system += "\n" + PreferenceContext;
            if (!string.IsNullOrEmpty(IdentityContext)) system += "\n" + IdentityContext;
            if (!string.IsNullOrEmpty(AutobiographyContext)) system += "\n" + AutobiographyContext;
            string attentionContext = Attention == null ? "" : Attention.BuildContext();
            if (!string.IsNullOrEmpty(attentionContext)) system += "\n" + attentionContext;
            string goalContext = Goals == null ? "" : Goals.BuildContext();
            if (!string.IsNullOrEmpty(goalContext)) system += "\n" + goalContext;
            int maxAttempts = Budget == null ? 1 : Budget.MaxModelAttempts;
            bool retried = false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    string answer = await _model.CompleteAsync(system, memory + "\n用户：" + userText, ct)
                        .ConfigureAwait(false);
                    if (retried) Budget.RecordRetryRecovered("model");
                    return answer;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (Budget == null || attempt >= maxAttempts || ct.IsCancellationRequested ||
                        !IsTransient(ex))
                        throw;
                    Budget.RecordRetry("model");
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
    }
}
