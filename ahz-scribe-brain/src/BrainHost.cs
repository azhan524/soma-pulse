using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Brain host v0. It owns the body client and the tool loop; face only talks to this class.
    /// </summary>
    public sealed class BrainHost : IDisposable
    {
        readonly IMemoryStore _memory;
        readonly IBrainModel _model;
        readonly IActionExecutor _body;
        readonly bool _ownsBody;
        readonly AgentRunner _textRunner;

        public Func<BrainToolCall, CancellationToken, Task<bool>> Approval { get; set; }
        public Action<string> Trace { get; set; }
        public event Action<SelfModelSnapshot> SelfModelChanged;
        public IBrainModel Model { get { return _model; } }
        public string BodySummary { get; set; }
        public string UiSummary { get; set; }
        public SelfModelSnapshot SelfModel { get; private set; }
        public string SelfModelSummary { get; private set; }
        public string ClaimContext { get; private set; }
        public string IdentitySummary { get; set; }
        public string AutobiographySummary { get; set; }
        public AttentionStore Attention { get; set; }
        public string AttentionSummary { get; set; }
        public GoalStore Goals { get; set; }
        public string GoalSummary { get; set; }
        public ExperienceStore Experience { get; set; }
        public HealthManager Health { get; set; }
        public ResourceBudget Budget { get; set; }
        public SelfIterationPolicy Policy { get; set; }
        public Func<BrainToolCall, CancellationToken, Task<bool>> Propose { get; set; }

        public BrainHost(IMemoryStore memory, IBrainModel model, IActionExecutor body = null)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            if (body != null) { _body = body; _ownsBody = false; }
            else { _body = new BodyClient(); _ownsBody = true; }
            _textRunner = new AgentRunner(_memory, _model);
        }

        public async Task<string> AskAsync(string text, CancellationToken ct)
        {
            EvaluateUserClaims(text);
            if (Health != null && !Health.CanChat)
                return "系统已停止，当前不能继续对话。请先恢复急停状态。";
            string budgetReason = "";
            bool budgetEntered = Budget != null && Budget.TryEnterTask(out budgetReason);
            if (Budget != null && !budgetEntered)
                return "资源预算已满，当前不能开始新任务。\n" + budgetReason;
            try
            {
                _textRunner.BodySummary = BodySummary;
                _textRunner.UiSummary = UiSummary;
                _textRunner.SelfModelSummary = SelfModelSummary;
                _textRunner.ClaimContext = ClaimContext;
                _textRunner.PreferenceContext = Experience == null ? "" : Experience.PreferenceContext();
                _textRunner.IdentityContext = IdentitySummary;
                _textRunner.AutobiographyContext = AutobiographySummary;
                _textRunner.Attention = Attention;
                _textRunner.Goals = Goals;
                _textRunner.Budget = Budget;
                try
                {
                    string answer = await _textRunner.RunAsync(text, ct).ConfigureAwait(false);
                    ReportModelHealth(true, "");
                    return ApplyResponsePolicies(answer);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ReportModelHealth(false, ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
            finally { if (budgetEntered) Budget.ExitTask(); }
        }

        public async Task<string> RunToolAsync(string text, CancellationToken ct)
        {
            EvaluateUserClaims(text);
            if (Health != null && !Health.CanUseToolCalls)
            {
                string reason = Health.ToolBlockReason("read_file", PermissionTier.ReadOnly);
                return "系统当前不可执行工具。\n" + (string.IsNullOrEmpty(reason) ? "健康状态未就绪。" : reason);
            }
            string budgetReason = "";
            bool budgetEntered = Budget != null && Budget.TryEnterTask(out budgetReason);
            if (Budget != null && !budgetEntered)
                return "资源预算已满，当前不能开始新任务。\n" + budgetReason;
            IToolCallingModel tm = _model as IToolCallingModel;
            try
            {
                if (tm == null) throw new InvalidOperationException("current model does not support tool calls");
                var runner = new ToolAgentRunner(_memory, tm, _body);
                runner.Approval = Approval;
                runner.Trace = Trace;
                runner.SelfModelSummary = SelfModelSummary;
                runner.ClaimContext = ClaimContext;
                runner.PreferenceContext = Experience == null ? "" : Experience.PreferenceContext();
                runner.IdentityContext = IdentitySummary;
                runner.AutobiographyContext = AutobiographySummary;
                runner.Attention = Attention;
                runner.Goals = Goals;
                runner.Experience = Experience;
                runner.Health = Health;
                runner.Budget = Budget;
                runner.ResultObserved = ObserveActionResult;
                runner.Policy = Policy;
                runner.Propose = Propose;
                try
                {
                    string answer = await runner.RunAsync(text, ct).ConfigureAwait(false);
                    ReportModelHealth(true, "");
                    return ApplyResponsePolicies(answer);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ReportModelHealth(false, ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
            finally { if (budgetEntered) Budget.ExitTask(); }
        }

        string ApplyResponsePolicies(string answer)
        {
            string guarded = ResponseGuard.Apply(SelfModel, answer, Environment.MachineName).Text;
            return ResponsePreferenceAdapter.Apply(guarded, Experience);
        }

        void EvaluateUserClaims(string text)
        {
            if (SelfModel == null)
            {
                ClaimContext = "";
                return;
            }
            List<Claim> claims = ClaimExtractor.Extract(text, SourceKind.User, Environment.MachineName);
            ClaimContext = ResponseGuard.DescribeClaims(SelfModel, claims, Environment.MachineName);
        }

        public async Task<SelfModelSnapshot> RefreshSelfModelAsync(
            string soulId,
            string deviceId,
            string permissionProfile,
            PermissionTier maximumTier,
            CancellationToken ct)
        {
            try
            {
                ICapabilitySource source = _body as ICapabilitySource;
                List<CapabilityDescriptor> capabilities;
                if (source == null)
                {
                    capabilities = new List<CapabilityDescriptor>();
                    ReportBodyHealth(false, "body capability source unavailable");
                }
                else
                {
                    capabilities = await source.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                    ReportBodyHealth(true, "");
                }
                var constraints = new List<string>();
                if (source == null) constraints.Add("body_capability_source_unavailable");
                List<SourceRef> recentActions = SelfModel == null || SelfModel.RecentActions == null
                    ? new List<SourceRef>()
                    : SelfModel.RecentActions.ToList();
                SelfModel = SelfModelService.Build(soulId, deviceId, _model is OpenAiToolModel
                    ? ((OpenAiToolModel)_model).ModelName
                    : _model.GetType().Name,
                    permissionProfile, maximumTier, capabilities, constraints, recentActions);
            }
            catch (Exception ex)
            {
                ReportBodyHealth(false, ex.GetType().Name + ": " + ex.Message);
                SelfModel = SelfModelService.BodyUnavailable(
                    soulId, deviceId, _model.GetType().Name, permissionProfile, ex.Message);
            }
            SelfModelSummary = SelfModelService.DescribeForModel(SelfModel);
            PublishSelfModel();
            return SelfModel;
        }

        void ReportModelHealth(bool healthy, string reason)
        {
            if (Health != null) Health.ReportComponent("model", healthy, reason);
        }

        void ReportBodyHealth(bool healthy, string reason)
        {
            if (Health != null) Health.ReportComponent("body", healthy, reason);
        }

        void ObserveActionResult(BrainToolCall call, ActionResult result)
        {
            if (SelfModel == null || call == null) return;
            SourceRef action = null;
            if (result != null && result.Provenance != null && result.Provenance.Sources != null)
                action = result.Provenance.Sources.FirstOrDefault(x => x != null && x.Kind == SourceKind.Tool);
            if (action == null)
            {
                action = new SourceRef
                {
                    SourceId = call.Id ?? "",
                    Kind = SourceKind.Tool,
                    AuditId = result == null ? "" : (result.AuditId ?? ""),
                    Ts = DateTimeOffset.UtcNow.ToString("o"),
                    Summary = call.Tool + ": " + (result == null ? "no result" : (result.Ok ? "ok" : result.Error))
                };
            }
            SelfModel.RecentActions.Add(action);
            if (SelfModel.RecentActions.Count > 50)
                SelfModel.RecentActions.RemoveRange(0, SelfModel.RecentActions.Count - 50);
            SelfModelSummary = SelfModelService.DescribeForModel(SelfModel);
            PublishSelfModel();
        }

        void PublishSelfModel()
        {
            Action<SelfModelSnapshot> handler = SelfModelChanged;
            if (handler == null) return;
            try { handler(SelfModel); } catch { }
        }

        public void Dispose()
        {
            if (_ownsBody)
            {
                IDisposable d = _body as IDisposable;
                if (d != null) d.Dispose();
            }
        }
    }
}
