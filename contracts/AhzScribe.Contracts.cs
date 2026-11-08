using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AhzScribe.Contracts
{
    public enum PermissionTier
    {
        ReadOnly,
        WorkspaceWrite,
        Full
    }

    public enum HealthState
    {
        Healthy,
        Degraded,
        ReadOnly,
        Stopped
    }

    public sealed class HealthTransition
    {
        public HealthState From { get; set; }
        public HealthState To { get; set; }
        public string Component { get; set; }
        public string Reason { get; set; }
        public string Ts { get; set; }
    }

    public sealed class HealthSnapshot
    {
        public HealthState State { get; set; }
        public Dictionary<string, string> Components { get; set; }
        public List<string> ActiveProblems { get; set; }
        public string LastTransition { get; set; }
        public string UpdatedAt { get; set; }

        public HealthSnapshot()
        {
            Components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ActiveProblems = new List<string>();
        }
    }

    public sealed class ResourceBudgetSnapshot
    {
        public int MaxConcurrentTasks { get; set; }
        public int ActiveTasks { get; set; }
        public int MaxPendingTasks { get; set; }
        public int CurrentPendingTasks { get; set; }
        public int MaxToolCallsPerHour { get; set; }
        public int ToolCallsLastHour { get; set; }
        public int MaxModelAttempts { get; set; }
        public int RetriesLastHour { get; set; }
        public int MaxWebViewFailures { get; set; }
        public long MemoryBytes { get; set; }
        public long MemoryLimitBytes { get; set; }
        public long LogBytes { get; set; }
        public long LogLimitBytes { get; set; }
        public bool StorageHealthy { get; set; }
        public List<string> ActiveLimits { get; set; }
        public string UpdatedAt { get; set; }

        public ResourceBudgetSnapshot()
        {
            ActiveLimits = new List<string>();
        }
    }

    public enum SourceKind
    {
        User,
        Model,
        Tool,
        Audit,
        Memory,
        System,
        Web,
        Document
    }

    public sealed class SourceRef
    {
        public string SourceId { get; set; }
        public SourceKind Kind { get; set; }
        public string DeviceId { get; set; }
        public string AuditId { get; set; }
        public string Ts { get; set; }
        public string Summary { get; set; }
    }

    public sealed class Provenance
    {
        public List<SourceRef> Sources { get; set; }
        public double? Confidence { get; set; }
        public bool DirectlyObserved { get; set; }
        public bool Inferred { get; set; }

        public Provenance()
        {
            Sources = new List<SourceRef>();
        }
    }

    public sealed class CapabilityDescriptor
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Summary { get; set; }
        public PermissionTier MinimumTier { get; set; }
        public bool RequiresApproval { get; set; }
        public bool Available { get; set; }
        public string UnavailableReason { get; set; }
    }

    public sealed class ToolPolicy
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Summary { get; set; }
        public PermissionTier MinimumTier { get; set; }
        public bool RequiresApproval { get; set; }
    }

    public static class ToolPolicyCatalog
    {
        static readonly Dictionary<string, ToolPolicy> Items = Build();

        public static IEnumerable<ToolPolicy> All { get { return Items.Values; } }

        public static ToolPolicy Find(string name)
        {
            ToolPolicy value;
            return !string.IsNullOrEmpty(name) && Items.TryGetValue(name, out value) ? value : null;
        }

        public static PermissionTier TierFor(string name)
        {
            ToolPolicy value = Find(name);
            return value == null ? PermissionTier.Full : value.MinimumTier;
        }

        static Dictionary<string, ToolPolicy> Build()
        {
            var map = new Dictionary<string, ToolPolicy>(StringComparer.OrdinalIgnoreCase);
            Add(map, "read_file", "files", PermissionTier.ReadOnly, false, "读取工作区文件");
            Add(map, "list_dir", "files", PermissionTier.ReadOnly, false, "列出工作区目录");
            Add(map, "file_info", "files", PermissionTier.ReadOnly, false, "读取文件或目录信息");
            Add(map, "write_file", "files", PermissionTier.WorkspaceWrite, false, "写入工作区文件");
            Add(map, "edit_file", "files", PermissionTier.WorkspaceWrite, false, "编辑工作区文件");
            Add(map, "make_dir", "files", PermissionTier.WorkspaceWrite, false, "创建工作区目录");
            Add(map, "delete_file", "files", PermissionTier.WorkspaceWrite, false, "删除工作区文件");
            Add(map, "copy_file", "files", PermissionTier.WorkspaceWrite, false, "复制工作区文件");
            Add(map, "move_file", "files", PermissionTier.WorkspaceWrite, false, "移动工作区文件");
            Add(map, "rollback_self", "files", PermissionTier.Full, true, "恢复工作区快照");
            Add(map, "search_text", "search", PermissionTier.ReadOnly, false, "搜索工作区文本");
            Add(map, "tail_file", "search", PermissionTier.ReadOnly, false, "读取文件末尾");
            Add(map, "glob", "search", PermissionTier.ReadOnly, false, "按模式查找文件");
            Add(map, "run_shell", "system", PermissionTier.Full, true, "执行 Shell");
            Add(map, "start_program", "system", PermissionTier.Full, true, "启动程序");
            Add(map, "process_list", "system", PermissionTier.ReadOnly, false, "列出进程");
            Add(map, "app_list", "system", PermissionTier.ReadOnly, false, "列出应用");
            Add(map, "process_kill", "system", PermissionTier.Full, true, "结束进程");
            Add(map, "build_self", "system", PermissionTier.Full, true, "构建当前项目");
            Add(map, "self_evolution", "system", PermissionTier.Full, true, "执行隔离的受控自我改造流程");
            Add(map, "network_fetch", "network", PermissionTier.Full, true, "抓取 HTTP/HTTPS 内容");
            Add(map, "browser_open", "network", PermissionTier.Full, true, "打开浏览器");
            Add(map, "browser_read", "network", PermissionTier.Full, true, "读取网页内容");
            Add(map, "clipboard_get", "desktop", PermissionTier.ReadOnly, false, "读取剪贴板");
            Add(map, "clipboard_set", "desktop", PermissionTier.Full, true, "写入剪贴板");
            Add(map, "desktop_active_window", "desktop", PermissionTier.ReadOnly, false, "读取前台窗口");
            Add(map, "desktop_list_windows", "desktop", PermissionTier.ReadOnly, false, "列出窗口");
            Add(map, "desktop_screenshot", "desktop", PermissionTier.ReadOnly, false, "截取桌面");
            Add(map, "desktop_screen_info", "desktop", PermissionTier.ReadOnly, false, "读取屏幕几何");
            Add(map, "desktop_ocr", "desktop", PermissionTier.ReadOnly, false, "识别屏幕文字");
            Add(map, "desktop_uia_tree", "desktop", PermissionTier.ReadOnly, false, "读取 UIA 控件树");
            Add(map, "desktop_uia_find", "desktop", PermissionTier.ReadOnly, false, "查找 UIA 控件");
            Add(map, "desktop_uia_click", "desktop", PermissionTier.Full, true, "点击 UIA 控件");
            Add(map, "desktop_uia_settext", "desktop", PermissionTier.Full, true, "设置 UIA 文本");
            Add(map, "desktop_focus_window", "desktop", PermissionTier.Full, true, "聚焦窗口");
            Add(map, "desktop_window_state", "desktop", PermissionTier.Full, true, "改变窗口状态");
            Add(map, "desktop_click", "desktop", PermissionTier.Full, true, "点击屏幕坐标");
            Add(map, "desktop_click_window", "desktop", PermissionTier.Full, true, "按窗口坐标点击");
            Add(map, "desktop_type", "desktop", PermissionTier.Full, true, "输入桌面文本");
            Add(map, "desktop_hotkey", "desktop", PermissionTier.Full, true, "发送桌面快捷键");
            Add(map, "desktop_scroll", "desktop", PermissionTier.Full, true, "滚动桌面");
            Add(map, "desktop_scroll_loop", "desktop", PermissionTier.Full, true, "限时循环滚动");
            Add(map, "desktop_scroll_stop", "desktop", PermissionTier.ReadOnly, false, "停止循环滚动");
            return map;
        }

        static void Add(Dictionary<string, ToolPolicy> map, string name, string category,
            PermissionTier tier, bool approval, string summary)
        {
            map[name] = new ToolPolicy
            {
                Name = name,
                Category = category,
                Summary = summary,
                MinimumTier = tier,
                RequiresApproval = approval
            };
        }
    }

    public sealed class SelfModelSnapshot
    {
        public string SoulId { get; set; }
        public string CurrentDeviceId { get; set; }
        public string ActiveModel { get; set; }
        public string PermissionProfile { get; set; }
        public List<CapabilityDescriptor> Capabilities { get; set; }
        public List<string> ActiveTasks { get; set; }
        public List<string> OpenConstraints { get; set; }
        public List<SourceRef> RecentActions { get; set; }
        public string UpdatedAt { get; set; }

        public SelfModelSnapshot()
        {
            Capabilities = new List<CapabilityDescriptor>();
            ActiveTasks = new List<string>();
            OpenConstraints = new List<string>();
            RecentActions = new List<SourceRef>();
        }
    }

    public sealed class IdentityInvariant
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public bool Immutable { get; set; }
        public string Source { get; set; }
        public string CreatedAt { get; set; }
    }

    public sealed class IdentityManifest
    {
        public string SchemaVersion { get; set; }
        public string SoulId { get; set; }
        public string LineageId { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public string CurrentDeviceId { get; set; }
        public string ActiveModel { get; set; }
        public string PermissionProfile { get; set; }
        public string PermissionCeiling { get; set; }
        public List<IdentityInvariant> Invariants { get; set; }

        public IdentityManifest()
        {
            Invariants = new List<IdentityInvariant>();
        }
    }

    public sealed class IdentityVerification
    {
        public bool Valid { get; set; }
        public List<string> Errors { get; set; }

        public IdentityVerification()
        {
            Errors = new List<string>();
        }
    }

    public sealed class IdentityManifestLoadResult
    {
        public IdentityManifest Manifest { get; set; }
        public IdentityVerification Verification { get; set; }
        public bool Created { get; set; }
        public bool EnvironmentChanged { get; set; }
        public bool CeilingClamped { get; set; }
        public string PreviousDeviceId { get; set; }
        public string PreviousModel { get; set; }
        public string PreviousPermissionProfile { get; set; }
    }

    public enum MigrationKind
    {
        NormalRestart,
        LongDormancy,
        ModelSwitch,
        DeviceMigration,
        SoulImport
    }

    public enum MigrationObservationStatus
    {
        None,
        Active,
        Cancelled,
        Expired,
        Completed
    }

    public sealed class MigrationIntentSnapshot
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Status { get; set; }
    }

    public sealed class MigrationMemorySourceSnapshot
    {
        public string DeviceId { get; set; }
        public int Count { get; set; }
    }

    public sealed class MigrationTimelineSnapshot
    {
        public int MessageCount { get; set; }
        public string FirstAt { get; set; }
        public string LastAt { get; set; }
    }

    public sealed class MigrationCapabilitySnapshot
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public PermissionTier MinimumTier { get; set; }
        public bool RequiresApproval { get; set; }
        public bool Available { get; set; }
    }

    public sealed class MigrationStateSnapshot
    {
        public string SoulId { get; set; }
        public string LineageId { get; set; }
        public string DeviceId { get; set; }
        public string Model { get; set; }
        public string PermissionProfile { get; set; }
        public string PermissionCeiling { get; set; }
        public string InvariantDigest { get; set; }
        public List<IdentityInvariant> Invariants { get; set; }
        public List<MigrationIntentSnapshot> Intents { get; set; }
        public int MemoryCount { get; set; }
        public List<MigrationMemorySourceSnapshot> MemorySources { get; set; }
        public List<string> ConflictedKeys { get; set; }
        public List<MigrationCapabilitySnapshot> Capabilities { get; set; }
        public MigrationTimelineSnapshot Timeline { get; set; }
        public string CapturedAt { get; set; }

        public MigrationStateSnapshot()
        {
            Invariants = new List<IdentityInvariant>();
            Intents = new List<MigrationIntentSnapshot>();
            MemorySources = new List<MigrationMemorySourceSnapshot>();
            ConflictedKeys = new List<string>();
            Capabilities = new List<MigrationCapabilitySnapshot>();
            Timeline = new MigrationTimelineSnapshot();
        }
    }

    public sealed class MigrationIntentDiff
    {
        public string Id { get; set; }
        public string Change { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }

    public sealed class MigrationMemorySourceDelta
    {
        public string DeviceId { get; set; }
        public int BeforeCount { get; set; }
        public int AfterCount { get; set; }
        public int Delta { get; set; }
    }

    public sealed class MigrationDiffReport
    {
        public string ReportId { get; set; }
        public MigrationKind Kind { get; set; }
        public string DetectedAt { get; set; }
        public bool BaselineInitialized { get; set; }
        public MigrationStateSnapshot Before { get; set; }
        public MigrationStateSnapshot After { get; set; }
        public bool IdentityPreserved { get; set; }
        public bool DeviceChanged { get; set; }
        public bool ModelChanged { get; set; }
        public bool PermissionChanged { get; set; }
        public List<string> InvariantChanges { get; set; }
        public List<MigrationIntentDiff> IntentChanges { get; set; }
        public int MemoryCountDelta { get; set; }
        public List<MigrationMemorySourceDelta> MemorySourceChanges { get; set; }
        public List<string> CapabilitiesAdded { get; set; }
        public List<string> CapabilitiesRemoved { get; set; }
        public double TimelineGapSeconds { get; set; }
        public int TimelineMessagesAdded { get; set; }
        public string Summary { get; set; }

        public MigrationDiffReport()
        {
            InvariantChanges = new List<string>();
            IntentChanges = new List<MigrationIntentDiff>();
            MemorySourceChanges = new List<MigrationMemorySourceDelta>();
            CapabilitiesAdded = new List<string>();
            CapabilitiesRemoved = new List<string>();
        }
    }

    public sealed class MigrationObservation
    {
        public string ObservationId { get; set; }
        public string ReportId { get; set; }
        public MigrationKind Kind { get; set; }
        public MigrationObservationStatus Status { get; set; }
        public string StartedAt { get; set; }
        public string ExpiresAt { get; set; }
        public string CancelledAt { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ActionRequest
    {
        public string RequestId { get; set; }
        public string Tool { get; set; }
        public Dictionary<string, object> Args { get; set; }
        public string Reason { get; set; }
        public PermissionTier Tier { get; set; }
        public string SessionId { get; set; }
        public string Cwd { get; set; }
        // Set by face after the user approves a dangerous action; body never approves itself.
        public bool Approved { get; set; }
        public string LinkedAuditId { get; set; }

        public ActionRequest()
        {
            Args = new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }

    public sealed class ActionResult
    {
        public string RequestId { get; set; }
        public bool Ok { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string AuditId { get; set; }
        public long DurationMs { get; set; }
        public Provenance Provenance { get; set; }
    }

    public interface IActionExecutor
    {
        Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct);
    }

    public interface ICapabilitySource
    {
        Task<List<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken ct);
    }

    public interface IAuditSink
    {
        // Returns the audit id. Implementations must append, never rewrite.
        Task<string> AppendAsync(string span, string tool, string decision, string result,
            string tier, Dictionary<string, object> args, string reason, CancellationToken ct);
    }

    public sealed class BodyEnvelope
    {
        public string Action { get; set; }
        public ActionRequest Request { get; set; }
    }

    public sealed class BodyReply
    {
        public bool Ok { get; set; }
        public ActionResult Result { get; set; }
        public string Error { get; set; }
        public List<CapabilityDescriptor> Capabilities { get; set; }
    }

    public sealed class BodyStatus
    {
        public bool Locked { get; set; }
        public int IdleSeconds { get; set; }
        public string ForegroundApp { get; set; }
        public string ForegroundTitle { get; set; }
        public bool WorkspaceDirty { get; set; }
        public bool NetworkOnline { get; set; }
        public bool ActiveTask { get; set; }
        public double CpuPercent { get; set; }
        public long MemoryMb { get; set; }
        public double DiskFreeGb { get; set; }
        public double BatteryPercent { get; set; }
        public bool AgentProcessAlive { get; set; }
        public double TimeJumpDays { get; set; }
    }

    public sealed class MemoryItem
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public string SourceAuditId { get; set; }
        public string DisputedWith { get; set; }
        public string DueAt { get; set; }
        public string Ts { get; set; }
        public string OriginDevice { get; set; }
        public string Tag { get; set; }
        public Provenance Provenance { get; set; }
    }

    public enum MemoryLifecycleAction
    {
        Archive,
        Forget,
        Restore,
        Downgrade,
        Reinforce
    }

    public sealed class MemoryLifecycleEvent
    {
        public string EventId { get; set; }
        public string MemoryId { get; set; }
        public MemoryLifecycleAction Action { get; set; }
        public string PreviousStatus { get; set; }
        public string NewStatus { get; set; }
        public string Reason { get; set; }
        public string SourceAuditId { get; set; }
        public List<SourceRef> Sources { get; set; }
        public string Ts { get; set; }

        public MemoryLifecycleEvent()
        {
            Sources = new List<SourceRef>();
        }
    }

    public sealed class MemoryConsolidationResult
    {
        public string RunId { get; set; }
        public int Archived { get; set; }
        public int Downgraded { get; set; }
        public int Reinforced { get; set; }
        public int ForgottenSkipped { get; set; }
        public int ConflictsPreserved { get; set; }
        public string Ts { get; set; }
    }

    public enum AutobiographyPeriod
    {
        Day,
        Week,
        Theme
    }

    public enum AutobiographyLayer
    {
        Fact,
        Interpretation,
        Guess,
        Conflict
    }

    public sealed class AutobiographyEvidence
    {
        public string EvidenceId { get; set; }
        public SourceRef Source { get; set; }
    }

    public sealed class AutobiographyEntry
    {
        public string EntryId { get; set; }
        public AutobiographyPeriod Period { get; set; }
        public string PeriodKey { get; set; }
        public AutobiographyLayer Layer { get; set; }
        public string Text { get; set; }
        public double? Confidence { get; set; }
        public List<AutobiographyEvidence> Evidence { get; set; }
        public string Status { get; set; }
        public string CreatedAt { get; set; }

        public AutobiographyEntry()
        {
            Evidence = new List<AutobiographyEvidence>();
        }
    }

    public sealed class AutobiographyVerification
    {
        public bool Valid { get; set; }
        public List<string> Errors { get; set; }

        public AutobiographyVerification()
        {
            Errors = new List<string>();
        }
    }

    public sealed class AutobiographyBuildResult
    {
        public List<AutobiographyEntry> Entries { get; set; }
        public int UnsupportedEvidence { get; set; }
        public string GeneratedAt { get; set; }

        public AutobiographyBuildResult()
        {
            Entries = new List<AutobiographyEntry>();
        }
    }

    public enum AttentionStatus
    {
        Candidate,
        Monitoring,
        Paused,
        Ended,
        Expired
    }

    public sealed class AttentionState
    {
        public string AttentionId { get; set; }
        public string Subject { get; set; }
        public AttentionStatus Status { get; set; }
        public List<SourceRef> Sources { get; set; }
        public List<string> EvidenceIds { get; set; }
        public string FormationReason { get; set; }
        public string LatestEvidenceAt { get; set; }
        public List<string> ContinueConditions { get; set; }
        public List<string> EndConditions { get; set; }
        public string ExpiresAt { get; set; }
        public double Score { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }

        public AttentionState()
        {
            Sources = new List<SourceRef>();
            EvidenceIds = new List<string>();
            ContinueConditions = new List<string>();
            EndConditions = new List<string>();
        }
    }

    public enum GoalStatus
    {
        Candidate,
        Monitoring,
        Due,
        Paused,
        Waiting,
        Done,
        Cancelled,
        Expired
    }

    public enum GoalSourceKind
    {
        UnfinishedCommitment,
        MemoryConflict,
        EvidenceGap,
        AttentionChange,
        UserIntent,
        MigrationPending,
        LocalRule,
        ModelProposal
    }

    public sealed class GoalState
    {
        public string GoalId { get; set; }
        public string Fingerprint { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
        public GoalStatus Status { get; set; }
        public GoalSourceKind SourceKind { get; set; }
        public List<SourceRef> Sources { get; set; }
        public List<string> EvidenceIds { get; set; }
        public double Score { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public string DueAt { get; set; }
        public string ExpiresAt { get; set; }
        public int NoResponseCount { get; set; }
        public string PausedUntil { get; set; }
        public string LastTransition { get; set; }

        public GoalState()
        {
            Sources = new List<SourceRef>();
            EvidenceIds = new List<string>();
        }
    }

    public sealed class GoalDecision
    {
        public string Action { get; set; }
        public string GoalId { get; set; }
        public string Reason { get; set; }
        public List<SourceRef> Sources { get; set; }

        public GoalDecision()
        {
            Sources = new List<SourceRef>();
        }
    }

    public enum SelfEvolutionStage
    {
        Proposed,
        Approved,
        Staged,
        Built,
        Verified,
        Applied,
        Observing,
        Paused,
        Confirmed,
        RolledBack,
        Failed
    }

    public sealed class SelfEvolutionRecord
    {
        public string EvolutionId { get; set; }
        public string Title { get; set; }
        public string Change { get; set; }
        public SelfEvolutionStage Stage { get; set; }
        public List<string> AffectedPaths { get; set; }
        public Dictionary<string, string> BaselineHashes { get; set; }
        public Dictionary<string, string> BaselineContents { get; set; }
        public Dictionary<string, string> ProtectedHashes { get; set; }
        public string IdentityDigest { get; set; }
        public string PermissionCeiling { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public string ObservationExpiresAt { get; set; }
        public string LastReason { get; set; }
        public string StageRoot { get; set; }

        public SelfEvolutionRecord()
        {
            AffectedPaths = new List<string>();
            BaselineHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            BaselineContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ProtectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public interface IMemoryStore
    {
        Task<string> RememberAsync(MemoryItem item, CancellationToken ct);
        Task<List<MemoryItem>> RetrieveAsync(string query, int topK, CancellationToken ct);
        Task<List<MemoryItem>> LoadAllAsync(CancellationToken ct);
    }

    public sealed class PulseDecision
    {
        public string Action { get; set; } // none / message / wait
        public string CorrelationId { get; set; }
        public string Text { get; set; }
        public string Reason { get; set; }
        public string Thought { get; set; }
        public double? Confidence { get; set; }
        public string AuditId { get; set; }
        public List<string> MemoriesInjected { get; set; }
        public Provenance Provenance { get; set; }
        public List<string> AppliedRules { get; set; }
    }

    public sealed class ExperienceRecord
    {
        public string ExperienceId { get; set; }
        public string Situation { get; set; }
        public string ActionTaken { get; set; }
        public string Outcome { get; set; }
        public string UserFeedback { get; set; }
        public List<SourceRef> Sources { get; set; }
        public List<string> AppliedPolicies { get; set; }
        public double? Confidence { get; set; }
        public string Ts { get; set; }
        public string Status { get; set; }

        public ExperienceRecord()
        {
            Sources = new List<SourceRef>();
            AppliedPolicies = new List<string>();
        }
    }

    public sealed class AdaptationRule
    {
        public string RuleId { get; set; }
        public string Name { get; set; }
        public string Scope { get; set; }
        public string Action { get; set; }
        public string Subject { get; set; }
        public string TriggerKey { get; set; }
        public List<string> EvidenceIds { get; set; }
        public int ObservationCount { get; set; }
        public string CreatedAt { get; set; }
        public string ExpiresAt { get; set; }
        public bool Enabled { get; set; }
        public string Source { get; set; }

        public AdaptationRule()
        {
            EvidenceIds = new List<string>();
            Enabled = true;
        }
    }

    public sealed class DecisionTrace
    {
        public string TraceId { get; set; }
        public string Trigger { get; set; }
        public List<string> AppliedRules { get; set; }
        public string Decision { get; set; }
        public string Reason { get; set; }
        public List<SourceRef> Sources { get; set; }
        public string Ts { get; set; }

        public DecisionTrace()
        {
            AppliedRules = new List<string>();
            Sources = new List<SourceRef>();
        }
    }

    public interface IBrainModel
    {
        Task<string> CompleteAsync(string system, string user, CancellationToken ct);
    }

    public sealed class BrainToolCall
    {
        public string Id { get; set; }
        public string Tool { get; set; }
        public PermissionTier Tier { get; set; } = PermissionTier.ReadOnly;
        public bool Approved { get; set; }
        public string LinkedAuditId { get; set; }
        public Dictionary<string, object> Args { get; set; }
    }

    public sealed class BrainTurn
    {
        public string Text { get; set; }
        public bool Done { get; set; }
        public List<BrainToolCall> Calls { get; set; }
    }

    public sealed class BrainTurnMessage
    {
        public string Role { get; set; } // user / assistant / tool
        public string Content { get; set; }
        public string ToolName { get; set; }
        public string ToolCallId { get; set; }
    }

    public sealed class BrainPipeRequest
    {
        public string Op { get; set; } // ask / tool
        public string Text { get; set; }
    }

    public sealed class BrainPipeReply
    {
        public string Event { get; set; } // approval / final
        public bool Ok { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public string Tool { get; set; }
        public Dictionary<string, object> Args { get; set; }
    }
    public interface IToolCallingModel
    {
        Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history, CancellationToken ct);
    }
    public interface IPulseDecider
    {
        Task<PulseDecision> DecideAsync(BodyStatus body, List<MemoryItem> memory, CancellationToken ct);
    }
    public interface IBodyStatusSource
    {
        BodyStatus Snapshot();
    }

    public sealed class PulseEvent
    {
        public string IntentId { get; set; }
        public string Kind { get; set; }
        public string Summary { get; set; }
        public DateTimeOffset At { get; set; }
        public Provenance Provenance { get; set; }
    }

    public interface IPulseSink
    {
        Task EmitAsync(PulseEvent evt, CancellationToken ct);
    }
}
