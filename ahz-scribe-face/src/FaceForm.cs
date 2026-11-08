using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.WinForms;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    public sealed class StateLabel : Control
    {
        public StateLabel()
        {
            Dock = DockStyle.Top;
            Height = 42;
            BackColor = Theme.BgSecondary;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            string text = Text ?? "";
            int nl = text.IndexOf('\n');
            string title = nl >= 0 ? text.Substring(0, nl) : text;
            string value = nl >= 0 ? text.Substring(nl + 1) : "";
            using (var tf = new Font("Microsoft YaHei UI", 8.5f))
            using (var vf = new Font("Microsoft YaHei UI", 10.5f))
            {
                TextRenderer.DrawText(e.Graphics, title, tf, new Rectangle(10, 2, Width - 20, 16), Theme.TextSecondary, TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(e.Graphics, value, vf, new Rectangle(10, 19, Width - 20, 20), Theme.TextPrimary, TextFormatFlags.EndEllipsis);
            }
            using (var pen = new Pen(Theme.BorderSubtle, 1)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
    [System.Runtime.InteropServices.ComVisible(true)]
    public sealed class PulseRow
    {
        public string time { get; set; }
        public string triggerType { get; set; }
        public string action { get; set; }
        public string reason { get; set; }
        public string raw { get; set; }
        public string thought { get; set; }
        public string stateRestore { get; set; }
        public string stateWriteback { get; set; }
        public string auditId { get; set; }
        public string pulseId { get; set; }
        public string ts { get; set; }
        public string confidence { get; set; }
        public string toolCalls { get; set; }
        public string memories { get; set; }
        public string appliedRules { get; set; }
    }

    [System.Runtime.InteropServices.ComVisible(true)]
    public sealed class FaceUiHost
    {
        readonly string _dataDir;
        readonly Action<string> _openAudit;
        public FaceUiHost(string dataDir, Action<string> openAudit) { _dataDir = dataDir; _openAudit = openAudit; }

        public void OpenAudit(string auditId) { try { if (_openAudit != null) _openAudit(auditId); } catch { } }

        public string QueryMemories(string filter)
        {
            var rows = new List<object>();
            using (var store = new MemoryStore(Path.Combine(_dataDir, "memory.jsonl")))
            {
                var items = store.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                foreach (var item in items.OrderByDescending(x =>
                {
                    DateTimeOffset parsed;
                    return DateTimeOffset.TryParse(x.Ts, out parsed) ? parsed : DateTimeOffset.MinValue;
                }).Take(200))
                {
                    string text = (item.Key ?? "") + " " + (item.Value ?? "") + " " + (item.Summary ?? "");
                    if (!string.IsNullOrEmpty(filter) && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string tag = !string.IsNullOrEmpty(item.Tag) ? item.Tag : MemoryTag(item);
                    rows.Add(new
                    {
                        kind = tag,
                        key = item.Key,
                        value = item.Value,
                        status = item.Status,
                        audit = item.SourceAuditId,
                        raw = item.Ts + " [#" + tag + "] " + item.Value
                    });
                }
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }

        static string MemoryTag(MemoryItem item)
        {
            string kind = (item.Kind ?? "").ToLowerInvariant();
            if (kind == "episodic" || kind == "episode") return "epi";
            if (kind == "intent") return "intent";
            if (kind == "drive") return "drive";
            if (kind == "emotion" || item.Status == "disputed") return "emotion";
            return "pref";
        }
        public string QueryFallbackAudit(string filter)
        {
            string path = FaceAudit.FallbackPath();
            var rows = new List<object>();
            if (!File.Exists(path)) return "[]";
            foreach (string line in File.ReadLines(path).Reverse().Take(100).Reverse())
            {
                if (!string.IsNullOrEmpty(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(line))
                    {
                        var r = doc.RootElement;
                        rows.Add(new
                        {
                            ts = r.TryGetProperty("ts", out var ts) ? ts.GetString() : "",
                            span = r.TryGetProperty("span", out var sp) ? sp.GetString() : "",
                            tool = r.TryGetProperty("tool", out var tl) ? tl.GetString() : "",
                            decision = r.TryGetProperty("decision", out var dc) ? dc.GetString() : "",
                            result = r.TryGetProperty("result", out var rs) ? rs.GetString() : "",
                            auditId = r.TryGetProperty("audit_id", out var ai) ? ai.GetString() : "",
                            fallback = true,
                            raw = line
                        });
                    }
                }
                catch { }
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
        public string QueryAudit(string filter)
        {
            string path = Environment.GetEnvironmentVariable("AHZ_AUDIT_PATH");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AhzScribe", "audit", "audit.jsonl");
            var rows = new List<object>();
            if (!File.Exists(path)) return "[]";
            foreach (string line in File.ReadLines(path).Reverse().Take(200).Reverse())
            {
                if (!string.IsNullOrEmpty(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(line))
                    {
                        var r = doc.RootElement;
                        rows.Add(new
                        {
                            ts = r.TryGetProperty("ts", out var ts) ? ts.GetString() : "",
                            span = r.TryGetProperty("span", out var sp) ? sp.GetString() : "",
                            tool = r.TryGetProperty("tool", out var tl) ? tl.GetString() : "",
                            decision = r.TryGetProperty("decision", out var dc) ? dc.GetString() : "",
                            result = r.TryGetProperty("result", out var rs) ? rs.GetString() : "",
                            auditId = r.TryGetProperty("audit_id", out var ai) ? ai.GetString() : "",
                            fallback = r.TryGetProperty("fallback", out var fb) && fb.ValueKind == System.Text.Json.JsonValueKind.True,
                            raw = line
                        });
                    }
                }
                catch { }
            }
            string fbp = FaceAudit.FallbackPath();
            if (File.Exists(fbp))
            {
                foreach (string line in File.ReadLines(fbp).Reverse().Take(100).Reverse())
                {
                    if (!string.IsNullOrEmpty(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        using (var doc = System.Text.Json.JsonDocument.Parse(line))
                        {
                            var r = doc.RootElement;
                            rows.Add(new { ts = r.TryGetProperty("ts", out var ts) ? ts.GetString() : "", span = r.TryGetProperty("span", out var sp) ? sp.GetString() : "", tool = r.TryGetProperty("tool", out var tl) ? tl.GetString() : "", decision = r.TryGetProperty("decision", out var dc) ? dc.GetString() : "", result = r.TryGetProperty("result", out var rs) ? rs.GetString() : "", auditId = r.TryGetProperty("audit_id", out var ai) ? ai.GetString() : "", fallback = true, raw = line });
                        }
                    }
                    catch { }
                }
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
        public string QueryPulses(string filter)
        {
            string jsonPath = Path.Combine(_dataDir, "pulse.jsonl");
            if (File.Exists(jsonPath))
            {
                var jr = new List<PulseRow>();
                foreach (string line in File.ReadLines(jsonPath).Reverse().Take(200).Reverse())
                {
                    if (!string.IsNullOrEmpty(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        using (var doc = System.Text.Json.JsonDocument.Parse(line))
                        {
                            var r = doc.RootElement;
                            string ts = r.TryGetProperty("ts", out var ts0) ? ts0.GetString() : "";
                            jr.Add(new PulseRow
                            {
                                time = ts.Length >= 19 ? ts.Substring(11, 8) : (r.TryGetProperty("time", out var t) ? t.GetString() : ""),
                                ts = ts,
                                triggerType = r.TryGetProperty("trigger_type", out var tt) ? tt.GetString() : (r.TryGetProperty("triggerType", out var tt2) ? tt2.GetString() : ""),
                                action = r.TryGetProperty("action", out var a) ? a.GetString() : "",
                                reason = r.TryGetProperty("reason", out var re) ? re.GetString() : "",
                                thought = r.TryGetProperty("thought", out var th) ? th.GetString() : "",
                                stateRestore = r.TryGetProperty("state_restore", out var sr) ? sr.GetRawText() : (r.TryGetProperty("stateRestore", out var sr2) ? sr2.GetString() : ""),
                                stateWriteback = r.TryGetProperty("state_writeback", out var sw) ? sw.GetRawText() : (r.TryGetProperty("stateWriteback", out var sw2) ? sw2.GetString() : ""),
                                auditId = r.TryGetProperty("audit_id", out var ai) ? ai.GetString() : (r.TryGetProperty("auditId", out var ai2) ? ai2.GetString() : ""),
                                pulseId = r.TryGetProperty("pulse_id", out var pi) ? pi.GetString() : "",
                                confidence = r.TryGetProperty("confidence", out var cf) && cf.ValueKind != System.Text.Json.JsonValueKind.Null ? cf.ToString() : "",
                                appliedRules = r.TryGetProperty("applied_rules", out var ar) ? ar.GetRawText() : "[]",
                                toolCalls = r.TryGetProperty("tool_calls", out var tc) ? tc.GetRawText() : "[]",
                                memories = r.TryGetProperty("memories_injected", out var mi) ? mi.GetRawText() : "[]",
                                raw = line
                            });
                        }
                    }
                    catch { }
                }
                if (jr.Count > 0) return System.Text.Json.JsonSerializer.Serialize(jr);
            }
            string p = Path.Combine(_dataDir, "pulse.log");
            var rows = new List<PulseRow>();
            if (!File.Exists(p)) return "[]";
            foreach (string line in File.ReadLines(p).Reverse().Take(200).Reverse())
            {
                if (!string.IsNullOrEmpty(filter) && line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var row = new PulseRow { raw = line };
                if (line.Length >= 19) row.time = line.Substring(11, 8);
                string rest = line.Length > 19 ? line.Substring(19).Trim() : line;
                string source = rest; string[] parts = new string[0];
                int slash = rest.IndexOf(" / ", StringComparison.Ordinal);
                if (slash >= 0)
                {
                    source = rest.Substring(0, slash).Trim();
                    parts = rest.Substring(slash + 3).Split(new[] { " / " }, StringSplitOptions.None);
                }
                row.triggerType = source;
                if (source == "proactive_message")
                {
                    row.action = parts.Length > 0 ? parts[0] : "";
                    row.triggerType = parts.Length > 2 ? parts[2] : "user_message";
                    row.reason = parts.Length > 2 ? parts[2] : "";
                }
                else if (source == "pulse_model")
                {
                    row.action = parts.Length > 2 ? parts[2] : "none";
                    row.triggerType = "timer";
                    row.reason = parts.Length > 2 ? parts[2] : "";
                }
                else
                {
                    row.action = parts.Length > 1 ? parts[1] : "";
                    row.reason = parts.Length > 2 ? parts[2] : "";
                }
                rows.Add(row);
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
    }
    public sealed class FaceForm : Form
    {
        public bool ResurrectionDetected { get; private set; }
        readonly RichTextBox _output;
        Color _trayColor;
        readonly Label _lastResponse;
        readonly TextBox _trace;
        readonly TextBox _traceFilter;
        readonly StringBuilder _traceRaw;
        readonly TextBox _memoryView;
        readonly TextBox _statusView;
        readonly TextBox _auditView;
        readonly TextBox _pulseView;
        readonly WebView2 _web;
        readonly Panel _htmlHost;
        Panel _webErrorPanel; Label _webErrorLabel; ListBox _webSnapshot; Button _webRetry; int _webFailures; bool _webPermanent; DateTime _lastWebBusRefresh;
        string _auditFocus;
        readonly StateLabel _stateFocus, _stateIntents, _stateBody, _statePulse, _stateMood;
        bool _recovering;
        Panel _topBar; Button _topStop;
        Panel _stopBanner; Label _stopBannerText; Button _stopButton;
        RowStyle _stopRow, _hintRow;
        double _timeJumpDays;
        bool _mixedDeviceMemory;
        bool _unreadChat; const int HotKeyId = 0xA11; const int HotKeyId2 = 0xA12;
        Panel _hintPanel;
        Icon _trayIcon;
        readonly System.Windows.Forms.Timer _breathTimer;
        bool _breathing;
        bool _busySend;
        DateTime _lastBodySample;
        readonly Queue<string> _pendingSends = new Queue<string>();
        CancellationTokenSource _turnCts;
        readonly TextBox _taskView;
        ProgressBar _taskProgress;
        readonly Label _topDot, _topIdle, _topFore, _topDisk, _topClock;
        readonly NotifyIcon _tray;
        ToolStripMenuItem _trayStatus, _trayPulse;
        readonly System.Windows.Forms.Timer _clockTimer;
        readonly UIStateBus _uiBus = new UIStateBus();
        bool _reallyExit;
        readonly TextBox _memoryFilter;
        readonly TextBox _setBase, _setModel, _setWorkspace, _setPipe, _setBodyData;
        readonly CheckBox _setNoProxy;
        readonly NumericUpDown _setProactiveMax, _setQuietStart, _setQuietEnd, _setPauseHours, _setPulseSeconds, _setMemoryLimit;
        readonly CheckBox _setReadOnly;
        Label _selfModelLabel;
        readonly TabControl _tabs;
        readonly Label _status;
        readonly Panel _side;
        readonly Button[] _nav;
        readonly TextBox _input;
        readonly Button _send;
        readonly BrainHost _host;
        readonly HealthManager _health;
        readonly ResourceBudget _budget;
        readonly FacePulseBody _pulseBody;
        readonly BodySensor _sensor;
        System.Threading.Timer _bodyTimer;
        readonly PulseScheduler _pulse;
        readonly PulseQueue _pulseQueue;
        readonly PulseArbiter _arbiter;
        readonly System.Threading.Timer _pulseTimer;
        readonly string _modelName;
        readonly string _sessionPath;
        readonly string _dataDir;
        readonly string _soulId;
        readonly FaceSettings _settings;
        readonly MemoryStore _memory;
        readonly AutobiographyStore _autobiography;
        readonly AttentionStore _attention;
        readonly GoalStore _goals;
        readonly ExperienceStore _experience;
        readonly IdentityManifestStore _identityStore;
        IdentityManifestLoadResult _identityResult;
        IdentityManifest _identityManifest;
        MigrationDiffReport _migrationReport;
        SelfModelSnapshot _selfModel;

        public FaceForm()
        {
            Text = "Ahz Scribe · face v0";
            Width = 920;
            Height = 640;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 10);
            ClientSize = new Size(1060, 680);
            MinimumSize = new Size(1024, 680);
            BackColor = Theme.BgPrimary;
            ForeColor = Theme.TextPrimary;

            string faceData = Environment.GetEnvironmentVariable("AHZ_FACE_DATA");
            _dataDir = string.IsNullOrWhiteSpace(faceData) ? Path.Combine(AppContext.BaseDirectory, "_face-data") : faceData;
            _sessionPath = Path.Combine(_dataDir, "sessions.jsonl");
            Directory.CreateDirectory(_dataDir);
            string schemaError = FaceSettings.ValidateSchema(_dataDir);
            if (schemaError.Length > 0)
            {
                MessageBox.Show("配置文件版本不匹配，拒绝启动：\\n" + schemaError, "Ahz Scribe", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }
            _settings = FaceSettings.Load(_dataDir);
            SoulBackup.RecoverInterruptedImport(_dataDir, _settings.Workspace);
            _soulId = EnsureSoulId(_dataDir);
            _identityStore = new IdentityManifestStore(_dataDir);
            if (_settings.WindowWidth >= 800 && _settings.WindowHeight >= 500)
            {
                ClientSize = new Size(_settings.WindowWidth, _settings.WindowHeight);
                if (_settings.WindowLeft != int.MinValue) Location = new Point(_settings.WindowLeft, _settings.WindowTop);
            }
            if (!string.IsNullOrEmpty(_settings.BodyPipe)) Environment.SetEnvironmentVariable("AHZ_BODY_PIPE", _settings.BodyPipe);
            if (!string.IsNullOrEmpty(_settings.BaseUrl)) Environment.SetEnvironmentVariable("AHZ_MODEL_BASE", _settings.BaseUrl);
            if (!string.IsNullOrEmpty(_settings.Model)) Environment.SetEnvironmentVariable("AHZ_MODEL_NAME", _settings.Model);
            if (_settings.ModelNoProxy) Environment.SetEnvironmentVariable("AHZ_MODEL_NOPROXY", "1");
            _memory = new MemoryStore(Path.Combine(_dataDir, "memory.jsonl"));
            _autobiography = new AutobiographyStore(_dataDir);
            _attention = new AttentionStore(_dataDir);
            _goals = new GoalStore(_dataDir,
                PolicyEnv.EnvInt("AHZ_GOAL_DAILY_MAX", 3),
                PolicyEnv.EnvInt("AHZ_GOAL_ACTIVE_MAX", 3),
                PolicyEnv.EnvInt("AHZ_GOAL_VALID_DAYS", 7),
                PolicyEnv.EnvInt("AHZ_GOAL_NO_RESPONSE_PAUSE", 2));
            _experience = new ExperienceStore(Path.Combine(_dataDir, "experience"));
            Environment.SetEnvironmentVariable("AHZ_PROACTIVE_MAX", _settings.ProactiveDailyMax.ToString());
            Environment.SetEnvironmentVariable("AHZ_QUIET_START", _settings.QuietStartHour.ToString());
            Environment.SetEnvironmentVariable("AHZ_QUIET_END", _settings.QuietEndHour.ToString());
            Environment.SetEnvironmentVariable("AHZ_UNANSWERED_PAUSE_HOURS", _settings.UnansweredPauseHours.ToString());
            _health = new HealthManager("model", "body", "memory", "audit", "webview", "identity");
            _health.Changed += OnHealthChanged;
            _budget = new ResourceBudget();
            _budget.RetryObserved += OnRetryObserved;
            _host = new BrainHost(_memory, CreateModel(out _modelName));
            _host.Experience = _experience;
            _host.Health = _health;
            _host.Budget = _budget;
            LoadOrCreateIdentity();
            RefreshAutobiographyContext();
            _host.Attention = _attention;
            RefreshAttentionContext();
            _host.Goals = _goals;
            RefreshGoalContext();
            _host.Trace = delegate(string line) { AppendTrace(line); };
            _host.SelfModelChanged += delegate(SelfModelSnapshot snapshot)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(new Action<SelfModelSnapshot>(ApplySelfModelSnapshot), snapshot);
                    else ApplySelfModelSnapshot(snapshot);
                }
                catch { }
            };
            _uiBus.Changed += ApplyUiSnapshot;
            _uiBus.Changed += ApplyUiAux;
            _uiBus.Changed += delegate(UiSnapshot s) { try { if (_tabs == null || _htmlHost == null || !_htmlHost.Visible) return; if ((DateTime.Now - _lastWebBusRefresh).TotalSeconds < 3) return; _lastWebBusRefresh = DateTime.Now; if (_tabs.SelectedIndex == 1) BeginInvoke(new Action(RenderPulse)); else if (_tabs.SelectedIndex == 2) BeginInvoke(new Action(RenderMemoryHtml)); else if (_tabs.SelectedIndex == 3) BeginInvoke(new Action(RenderAuditHtml)); } catch { } };
            FaceAudit.Appended += delegate { try { if (_tabs != null && _tabs.SelectedIndex == 3 && _htmlHost != null && _htmlHost.Visible) BeginInvoke(new Action(RenderAuditHtml)); } catch { } };
            _pulseBody = new FacePulseBody();
            _pulse = new PulseScheduler(_pulseBody, _memory, new FacePulseAudit(AppendTrace, Path.Combine(_dataDir, "pulse.log")),
                new FacePulseSink(OnPulse), new ModelPulseDecider(_memory, _host.Model, _experience,
                    _health, _budget, _goals));
            _pulseQueue = new PulseQueue();
            _arbiter = new PulseArbiter(_pulse, _pulseQueue, delegate { return FaceSession.LoadState(_sessionPath); });
            _pulseTimer = new System.Threading.Timer(delegate { PulseTick(); }, null, _settings.PulseSeconds * 1000, _settings.PulseSeconds * 1000);
            _sensor = new BodySensor(_settings.Workspace);
            _bodyTimer = new System.Threading.Timer(delegate { SampleBody(); }, null, 0, 30000);
            FormClosed += delegate { _pulse.Dispose(); _bodyTimer.Dispose(); _pulseTimer.Dispose(); if (_breathTimer != null) _breathTimer.Stop(); };
            _host.Policy = new SelfIterationPolicy();
            _host.Propose = delegate(BrainToolCall call, CancellationToken ct)
            {
                string title = call.Args != null && call.Args.ContainsKey("title") ? Convert.ToString(call.Args["title"]) : "自我改造申请";
                string change = call.Args != null && call.Args.ContainsKey("change") ? Convert.ToString(call.Args["change"]) : "";
                bool approved = false;
                using (var dlg = new ApprovalForm(title, change))
                    approved = dlg.ShowDialog(this) == DialogResult.Yes;
                if (approved)
                {
                    string root = string.IsNullOrEmpty(_settings.Workspace) ? (Environment.GetEnvironmentVariable("AHZ_WORKSPACE") ?? "") : _settings.Workspace;
                    if (!string.IsNullOrEmpty(root)) _host.Policy.Begin(root);
                }
                return Task.FromResult(approved);
            };

            _output = new RichTextBox();
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = RichTextBoxScrollBars.None;
            _output.Dock = DockStyle.Fill;
            _output.BackColor = Theme.BgPrimary;
            _output.ForeColor = Theme.TextPrimary;
            _output.BorderStyle = BorderStyle.None;
            _output.Font = new Font("Microsoft YaHei UI", 10f);
            _output.HideSelection = false;

            _trace = new TextBox();
            _trace.Multiline = true;
            _trace.ReadOnly = true;
            _trace.ScrollBars = ScrollBars.Vertical;
            _trace.Dock = DockStyle.Fill;
            _trace.BackColor = Theme.BgPrimary;
            _trace.ForeColor = Theme.TextSecondary;
            _trace.BorderStyle = BorderStyle.None;
            _trace.Font = new Font("Consolas", 9f);

            _traceRaw = new StringBuilder();
            _traceFilter = new TextBox();
            _traceFilter.Dock = DockStyle.Top;
            _traceFilter.BackColor = Theme.BgSecondary;
            _traceFilter.ForeColor = Theme.TextPrimary;
            _traceFilter.BorderStyle = BorderStyle.FixedSingle;
            _traceFilter.PlaceholderText = "过滤轨迹...";
            _traceFilter.TextChanged += delegate { RefreshTrace(); };

            _memoryView = new TextBox();
            _memoryView.Multiline = true;
            _memoryView.ReadOnly = true;
            _memoryView.ScrollBars = ScrollBars.Vertical;
            _memoryView.Dock = DockStyle.Fill;
            _memoryView.BackColor = Theme.BgPrimary;
            _memoryView.ForeColor = Theme.TextSecondary;
            _memoryView.BorderStyle = BorderStyle.None;
            _memoryView.Font = new Font("Consolas", 9f);

            _statusView = new TextBox();
            _statusView.Multiline = true;
            _statusView.ReadOnly = true;
            _statusView.ScrollBars = ScrollBars.Vertical;
            _statusView.Dock = DockStyle.Fill;
            _statusView.BackColor = Theme.BgPrimary;
            _statusView.ForeColor = Theme.TextPrimary;
            _statusView.BorderStyle = BorderStyle.None;
            _statusView.Font = new Font("Consolas", 9f);

            _auditView = new TextBox();
            _auditView.Multiline = true;
            _auditView.ReadOnly = true;
            _auditView.ScrollBars = ScrollBars.Vertical;
            _auditView.Dock = DockStyle.Fill;
            _auditView.BackColor = Theme.BgPrimary;
            _auditView.ForeColor = Theme.TextPrimary;
            _auditView.BorderStyle = BorderStyle.None;
            _auditView.Font = new Font("Consolas", 8.5f);

            _pulseView = new TextBox();
            _pulseView.Multiline = true;
            _pulseView.ReadOnly = true;
            _pulseView.ScrollBars = ScrollBars.Vertical;
            _pulseView.Dock = DockStyle.Fill;
            _pulseView.BackColor = Theme.BgPrimary;
            _pulseView.ForeColor = Theme.TextPrimary;
            _pulseView.BorderStyle = BorderStyle.None;
            _pulseView.Font = new Font("Consolas", 8.5f);
            _web = new WebView2();
            _web.Dock = DockStyle.Fill;
            _web.BackColor = Theme.BgPrimary;
            _web.DefaultBackgroundColor = Theme.BgPrimary;
            _htmlHost = new Panel();
            _htmlHost.Dock = DockStyle.Fill;
            _htmlHost.Visible = false;
            _htmlHost.BackColor = Theme.BgPrimary;
            _htmlHost.Controls.Add(_web);
            _webErrorPanel = new Panel();
            _webErrorPanel.Dock = DockStyle.Fill;
            _webErrorPanel.BackColor = Theme.BgPrimary;
            _webErrorPanel.Visible = false;
            _webErrorLabel = new Label();
            _webErrorLabel.Dock = DockStyle.Top;
            _webErrorLabel.Height = 64;
            _webErrorLabel.ForeColor = Theme.StateWarning;
            _webErrorLabel.Font = new Font("Microsoft YaHei UI", 10f);
            _webErrorLabel.Padding = new Padding(24);
            _webErrorLabel.Text = "HTML 面板不可用";
            _webSnapshot = new ListBox();
            _webSnapshot.Dock = DockStyle.Fill;
            _webSnapshot.BackColor = Theme.BgPrimary;
            _webSnapshot.ForeColor = Theme.TextSecondary;
            _webSnapshot.BorderStyle = BorderStyle.None;
            _webSnapshot.Font = new Font("Consolas", 9f);
            _webSnapshot.Visible = false;
            _webRetry = new Button();
            _webRetry.Text = "重试";
            _webRetry.Dock = DockStyle.Bottom;
            _webRetry.Height = 36;
            _webRetry.Enabled = false;
            _webRetry.FlatStyle = FlatStyle.Flat;
            _webRetry.FlatAppearance.BorderSize = 0;
            _webRetry.BackColor = Theme.BgTertiary;
            _webRetry.ForeColor = Theme.TextPrimary;
            _webRetry.Click += delegate { if (_webPermanent) return; _webSnapshot.Visible = false; _webErrorLabel.Visible = true; _webErrorPanel.Visible = false; _htmlHost.Visible = true; try { RenderPulse(); } catch { } };
            _webErrorPanel.Controls.Add(_webSnapshot);
            _webErrorPanel.Controls.Add(_webErrorLabel);
            _webErrorPanel.Controls.Add(_webRetry);
            _web.CoreWebView2InitializationCompleted += delegate(object s, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), "init success=" + e.IsSuccess + " " + e.InitializationException + "\n"); } catch { }
                if (!e.IsSuccess)
                {
                    ShowWebFailure("初始化失败：" + e.InitializationException);
                }
                else
                {
                    _webFailures = 0;
                    _webPermanent = false;
                    _webRetry.Enabled = true;
                    _webRetry.Text = "重试";
                    _health.ReportComponent("webview", true, "");
                    _web.CoreWebView2.ProcessFailed += delegate(object ps, Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs pe)
                    {
                        try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), "process_failed=" + pe.ProcessFailedKind + "\n"); } catch { }
                        ShowWebFailure("进程异常：" + pe.ProcessFailedKind);
                    };
                }
            };
            _web.NavigationCompleted += delegate(object s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) { try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), "nav success=" + e.IsSuccess + " " + e.WebErrorStatus + "\n"); } catch { } };
            _taskView = new TextBox();
            _taskView.Multiline = true;
            _taskView.ReadOnly = true;
            _taskView.Dock = DockStyle.Fill;
            _taskView.BackColor = Theme.BgPrimary;
            _taskView.ForeColor = Theme.TextPrimary;
            _taskView.BorderStyle = BorderStyle.None;
            _taskProgress = new ProgressBar();
            _taskProgress.Dock = DockStyle.Top;
            _taskProgress.Height = 6;
            _taskProgress.Style = ProgressBarStyle.Marquee;
            _taskProgress.MarqueeAnimationSpeed = 40;
            _taskProgress.Visible = false;
            _taskView.Font = new Font("Consolas", 9f);

            _memoryFilter = new TextBox();
            _memoryFilter.Dock = DockStyle.Top;
            _memoryFilter.BackColor = Theme.BgSecondary;
            _memoryFilter.ForeColor = Theme.TextPrimary;
            _memoryFilter.BorderStyle = BorderStyle.FixedSingle;
            _memoryFilter.PlaceholderText = "搜索记忆...";
            _memoryFilter.TextChanged += delegate { RenderMemory(); };

            _setBase = MakeSettingBox(_settings.BaseUrl);
            _setModel = MakeSettingBox(_settings.Model);
            _setWorkspace = MakeSettingBox(_settings.Workspace);
            _setPipe = MakeSettingBox(_settings.BodyPipe);
            _setBodyData = MakeSettingBox(_settings.BodyData);
            _setBodyData.PlaceholderText = "body 数据目录（读审计用）";
            _setNoProxy = new CheckBox();
            _setNoProxy.Text = "模型直连（不使用系统代理）";
            _setNoProxy.ForeColor = Theme.TextPrimary;
            _setNoProxy.Checked = _settings.ModelNoProxy;
            _setWorkspace.PlaceholderText = "工作区路径；留空则不能自我迭代";
            _setProactiveMax = MakeNumber(0, 20, _settings.ProactiveDailyMax);
            _setQuietStart = MakeNumber(0, 23, _settings.QuietStartHour);
            _setQuietEnd = MakeNumber(0, 23, _settings.QuietEndHour);
            _setPauseHours = MakeNumber(1, 168, _settings.UnansweredPauseHours);
            _setPulseSeconds = MakeNumber(10, 600, _settings.PulseSeconds);
            _setMemoryLimit = MakeNumber(1, 50, _settings.MemorySearchLimit);
            _setReadOnly = new CheckBox(); _setReadOnly.Text = "只读模式：禁止 WorkspaceWrite / Full 工具"; _setReadOnly.ForeColor = Theme.TextPrimary; _setReadOnly.Checked = _settings.ReadOnlyMode;

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Font = new Font("Microsoft YaHei UI", 9f);
            _tabs.Appearance = TabAppearance.FlatButtons;
            _tabs.ItemSize = new Size(0, 1);
            _tabs.SizeMode = TabSizeMode.Fixed;
            var chatPage = new TabPage("对话");
            chatPage.Padding = new Padding(0, 24, 0, 0);
            chatPage.BackColor = Theme.BgPrimary;
            _lastResponse = new Label();
            _lastResponse.Dock = DockStyle.Top;
            _lastResponse.Height = 22;
            _lastResponse.TextAlign = ContentAlignment.MiddleRight;
            _lastResponse.Padding = new Padding(0, 0, 10, 0);
            _lastResponse.ForeColor = Theme.TextTertiary;
            _lastResponse.Font = new Font("Microsoft YaHei UI", 8.5f);
            _lastResponse.Text = "最后响应：—";
            chatPage.Controls.Add(_output);
            chatPage.Controls.Add(_lastResponse);
            var tracePage = new TabPage("轨迹");
            tracePage.BackColor = Theme.BgPrimary;
            var tracePanel = new Panel();
            tracePanel.Dock = DockStyle.Fill;
            tracePanel.Controls.Add(_trace);
            tracePanel.Controls.Add(_traceFilter);
            tracePage.Controls.Add(tracePanel);
            var pulsePage = new TabPage("脉冲");
            pulsePage.BackColor = Theme.BgPrimary;
            var pulsePanel = new Panel();
            pulsePanel.Dock = DockStyle.Fill;
            var pulseRefresh = new Button();
            pulseRefresh.Text = "刷新";
            pulseRefresh.Dock = DockStyle.Top;
            pulseRefresh.Height = 28;
            pulseRefresh.Click += delegate { RenderPulse(); };
            pulsePanel.Controls.Add(pulseRefresh);
            _pulseView.Visible = false;
            pulsePanel.Controls.Add(_pulseView);
            pulsePage.Controls.Add(pulsePanel);
            var memoryPage = new TabPage("记忆");
            memoryPage.BackColor = Theme.BgPrimary;
            var memoryPanel = new Panel();
            memoryPanel.Dock = DockStyle.Fill;
            memoryPanel.Controls.Add(_memoryView);
            memoryPanel.Controls.Add(_memoryFilter);
            memoryPage.Controls.Add(memoryPanel);
            var statusPage = new TabPage("状态");
            statusPage.BackColor = Theme.BgPrimary;
            statusPage.Controls.Add(_statusView);
            var auditPage = new TabPage("审计");
            auditPage.BackColor = Theme.BgPrimary;
            var auditPanel = new Panel();
            auditPanel.Dock = DockStyle.Fill;
            var auditRefresh = new Button();
            auditRefresh.Text = "刷新";
            auditRefresh.Dock = DockStyle.Top;
            auditRefresh.Height = 28;
            auditRefresh.Click += delegate { RenderAudit(); };
            auditPanel.Controls.Add(_auditView);
            auditPanel.Controls.Add(auditRefresh);
            auditPage.Controls.Add(auditPanel);
            var settingsPage = new TabPage("设置");
            settingsPage.BackColor = Theme.BgPrimary;
            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Padding = new Padding(18);
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowCount = 7;
            for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            table.Controls.Add(new Label { Text = "模型 Base URL", ForeColor = Theme.TextPrimary, AutoSize = true }, 0, 0);
            table.Controls.Add(_setBase, 1, 0);
            table.Controls.Add(new Label { Text = "模型名", ForeColor = Theme.TextPrimary, AutoSize = true }, 0, 1);
            table.Controls.Add(_setModel, 1, 1);
            table.Controls.Add(new Label { Text = "工作区", ForeColor = Theme.TextPrimary, AutoSize = true }, 0, 2);
            table.Controls.Add(_setWorkspace, 1, 2);
            table.Controls.Add(new Label { Text = "body 管道", ForeColor = Theme.TextPrimary, AutoSize = true }, 0, 3);
            table.Controls.Add(_setPipe, 1, 3);
            var save = new Button();
            save.Text = "保存（重启 face 生效）";
            save.Width = 220;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.BackColor = Theme.StateInfo;
            save.ForeColor = Theme.TextPrimary;
            save.Click += delegate { SaveSettings(); };
            table.Controls.Add(_setNoProxy, 1, 4);
            table.Controls.Add(new Label { Text = "body 数据目录", ForeColor = Theme.TextPrimary, AutoSize = true }, 0, 5);
            table.Controls.Add(_setBodyData, 1, 5);
            table.Controls.Add(save, 1, 6);
            var settingsFlow = new FlowLayoutPanel();
            settingsFlow.Dock = DockStyle.Fill;
            settingsFlow.FlowDirection = FlowDirection.TopDown;
            settingsFlow.WrapContents = false;
            settingsFlow.AutoScroll = true;
            settingsFlow.Padding = new Padding(14);
            settingsFlow.BackColor = Theme.BgPrimary;

            var llmGroup = MakeGroup("LLM");
            llmGroup.Controls.Add(MakeSettingRow("Base URL", _setBase));
            llmGroup.Controls.Add(MakeSettingRow("模型名", _setModel));
            llmGroup.Controls.Add(_setNoProxy);

            var bodyGroup = MakeGroup("身体");
            bodyGroup.Controls.Add(MakeSettingRow("工作区", _setWorkspace));
            bodyGroup.Controls.Add(MakeSettingRow("body 管道", _setPipe));
            bodyGroup.Controls.Add(MakeSettingRow("body 数据目录", _setBodyData));

            var safetyGroup = MakeGroup("安全");
            safetyGroup.Height = 80;
            safetyGroup.Controls.Add(MakeSettingRow("保存设置", save));

            settingsFlow.Controls.Add(llmGroup);
            settingsFlow.Controls.Add(bodyGroup);
            settingsFlow.Controls.Add(safetyGroup);
            settingsFlow.Controls.Add(MakeSelfModelGroup());
            settingsFlow.Controls.Add(MakePermissionGroup());
            settingsFlow.Controls.Add(MakeMemoryGroup());
            var pulseGroup = MakeGroup("脉冲");
            pulseGroup.Height = 80;
            pulseGroup.Controls.Add(MakeSettingRow("周期（秒）", _setPulseSeconds));
            settingsFlow.Controls.Add(pulseGroup);
            var proactiveGroup = MakeGroup("主动性");
            proactiveGroup.Height = 180;
            proactiveGroup.Controls.Add(MakeSettingRow("每日上限", _setProactiveMax));
            proactiveGroup.Controls.Add(MakeSettingRow("静默开始", _setQuietStart));
            proactiveGroup.Controls.Add(MakeSettingRow("静默结束", _setQuietEnd));
            proactiveGroup.Controls.Add(MakeSettingRow("未回复暂停小时", _setPauseHours));
            settingsFlow.Controls.Add(proactiveGroup);
            settingsFlow.Controls.Add(MakeSoulGroup());
            settingsFlow.Controls.Add(MakePersonaGroup());
            settingsPage.Controls.Add(settingsFlow);
            var taskPage = new TabPage("任务");
            taskPage.BackColor = Theme.BgPrimary;
            taskPage.Controls.Add(_taskProgress);
            taskPage.Controls.Add(_taskView);
            _tabs.TabPages.Add(chatPage);
            _tabs.TabPages.Add(pulsePage);
            _tabs.TabPages.Add(memoryPage);
            _tabs.TabPages.Add(auditPage);
            _tabs.TabPages.Add(taskPage);
            _tabs.TabPages.Add(settingsPage);
            FixTabPages();
            _side = new Panel();
            _side.Dock = DockStyle.Left;
            _side.Width = 260;
            _side.AutoScroll = true;
            _side.BackColor = Theme.BgBase;
            var statePanel = new Panel();
            statePanel.Dock = DockStyle.Top;
            statePanel.Height = 240;
            statePanel.BackColor = Theme.BgSecondary;
            statePanel.Padding = new Padding(10);
            _stateFocus = MakeStateLabel();
            _stateIntents = MakeStateLabel();
            _stateBody = MakeStateLabel();
            _statePulse = MakeStateLabel();
            _stateMood = MakeStateLabel();
            statePanel.Controls.Add(_stateMood);
            statePanel.Controls.Add(_statePulse);
            statePanel.Controls.Add(_stateBody);
            statePanel.Controls.Add(_stateIntents);
            statePanel.Controls.Add(_stateFocus);
            var brand = new Label();
            brand.Text = "AHZ\nSCRIBE";
            brand.Dock = DockStyle.Top;
            brand.Height = 72;
            brand.Padding = new Padding(16, 14, 0, 0);
            brand.Font = new Font("Consolas", 13f, FontStyle.Bold);
            brand.ForeColor = Theme.StateInfo;
            _side.Controls.Add(brand);
            var tabBar = new Panel();
            tabBar.Dock = DockStyle.Top;
            tabBar.Height = 36;
            tabBar.BackColor = Theme.BgTertiary;
            tabBar.Paint += delegate(object s, PaintEventArgs e) { using (var pen = new Pen(Theme.BorderSubtle, 1)) e.Graphics.DrawLine(pen, 0, tabBar.Height - 1, tabBar.Width, tabBar.Height - 1); };
            string[] navNames = { "对话", "脉冲", "记忆", "审计", "任务", "设置" };
            _nav = new Button[navNames.Length];
            for (int i = navNames.Length - 1; i >= 0; i--)
            {
                int index = i;
                var b = new Button();
                b.Text = navNames[i];
                b.Dock = DockStyle.Left;
                b.Width = 86;
                b.Height = 36;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Theme.BgBase;
                b.ForeColor = Theme.TextSecondary;
                b.TextAlign = ContentAlignment.MiddleCenter;
                b.Padding = new Padding(0);
                b.Click += delegate { _tabs.SelectedIndex = index; UpdateNav(); };
                b.Paint += delegate(object s, PaintEventArgs e) { if (_tabs != null && index == _tabs.SelectedIndex) { using (var pen = new Pen(Theme.StateInfo, 2)) e.Graphics.DrawLine(pen, 0, b.Height - 2, b.Width, b.Height - 2); } };
                tabBar.Controls.Add(b);
                _nav[i] = b;
            }
            var search = new Button();
            search.Text = "搜索对话";
            search.Dock = DockStyle.Bottom;
            search.Height = 46;
            search.FlatStyle = FlatStyle.Flat;
            search.FlatAppearance.BorderSize = 0;
            search.BackColor = Theme.BgBase;
            search.ForeColor = Theme.TextSecondary;
            search.TextAlign = ContentAlignment.MiddleLeft;
            search.Padding = new Padding(18, 0, 0, 0);
            search.Click += delegate
            {
                using (var f = new HistorySearchForm(_sessionPath))
                    if (f.ShowDialog(this) == DialogResult.OK && f.SelectedMessage != null) JumpTo(f.SelectedMessage);
            };
            _side.Controls.Add(search);
            _side.Controls.Add(statePanel);
            _side.Controls.Add(brand);
            _tabs.SelectedIndexChanged += delegate { if (_tabs.SelectedIndex == 0) _unreadChat = false; UpdateNav(); FixTabPages(); ShowHtmlTab(); };
            UpdateNav();
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 56;
            bottom.BackColor = Theme.BgPrimary;
            bottom.Paint += delegate(object s, PaintEventArgs e) { using (var pen = new Pen(Theme.BorderSubtle, 1)) e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };

            _input = new TextBox();
            _input.Dock = DockStyle.Fill;
            _input.Multiline = true;
            _input.PlaceholderText = "输入问题；/tool 前缀让模型使用工具";
            _input.ScrollBars = ScrollBars.None;
            _input.Font = new Font("Microsoft YaHei UI", 10f);
            _input.BackColor = Theme.BgSecondary;
            _input.ForeColor = Theme.TextPrimary;
            _input.BorderStyle = BorderStyle.None;

            _send = new Button();
            _send.Text = "↑ 发送";
            _send.Dock = DockStyle.Right;
            _send.Width = 68;
            _send.FlatStyle = FlatStyle.Flat;
            _send.FlatAppearance.BorderSize = 0;
            _send.BackColor = Theme.StateInfo;
            _send.ForeColor = Theme.TextPrimary;
            _send.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _send.Click += delegate { SendAsync(); };
            _input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendAsync(); }
            };

            _status = new Label();
            _status.Dock = DockStyle.Top;
            _status.Height = 18;
            _status.BackColor = Theme.BgPrimary;
            _status.ForeColor = Theme.TextSecondary;
            _status.Font = new Font("Consolas", 8.5f);
            _status.Padding = new Padding(10, 3, 4, 0);
            _status.Text = "episode -   memory 0   body " + (Environment.GetEnvironmentVariable("AHZ_BODY_PIPE") ?? "ahz-scribe-body");
            bottom.Controls.Add(_status);
            var pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(10, 20, 8, 4);
            pad.Controls.Add(_send);
            pad.Controls.Add(_input);
            bottom.Controls.Add(pad);
            var stopBtn = new Button();
            _stopButton = stopBtn;
            stopBtn.Text = "■ 急停";
            stopBtn.Dock = DockStyle.Right;
            stopBtn.Width = 56;
            stopBtn.FlatStyle = FlatStyle.Flat;
            stopBtn.FlatAppearance.BorderSize = 0;
            stopBtn.BackColor = Theme.StateDanger;
            stopBtn.ForeColor = Theme.DangerButtonFg;
            stopBtn.Click += delegate { ToggleEmergencyStop(); };
            var auditBtn = new Button();
            auditBtn.Text = "审计";
            auditBtn.Dock = DockStyle.Right;
            auditBtn.Width = 48;
            auditBtn.FlatStyle = FlatStyle.Flat;
            auditBtn.FlatAppearance.BorderSize = 0;
            auditBtn.BackColor = Theme.BgPrimary;
            auditBtn.ForeColor = Theme.TextTertiary;
            auditBtn.Click += delegate { _tabs.SelectedIndex = 3; UpdateNav(); };
            _hintPanel = new Panel();
            _hintPanel.Dock = DockStyle.Fill;
            _hintPanel.Height = 30;
            _hintPanel.BackColor = Theme.BgTertiary;
            _hintPanel.Visible = false;
            var hint = new Label(); hint.Text = "关闭窗口后，AhzScribe 会缩到托盘继续运行。托盘图标在任务栏右下角。"; hint.Dock = DockStyle.Fill; hint.ForeColor = Theme.TextPrimary; hint.TextAlign = ContentAlignment.MiddleLeft; hint.Padding = new Padding(10, 0, 0, 0); hint.Font = new Font("Microsoft YaHei UI", 9f);
            var hintClose = new Button(); hintClose.Text = "知道了"; hintClose.Dock = DockStyle.Right; hintClose.Width = 84; hintClose.FlatStyle = FlatStyle.Flat; hintClose.FlatAppearance.BorderSize = 0; hintClose.BackColor = Theme.BgTertiary; hintClose.ForeColor = Theme.TextPrimary; hintClose.Click += delegate { _hintPanel.Visible = false; if (_hintRow != null) _hintRow.Height = 0; };
            _hintPanel.Controls.Add(hint); _hintPanel.Controls.Add(hintClose);
            var pauseBtn = new Button();
            pauseBtn.Text = "⏸";
            pauseBtn.Dock = DockStyle.Right;
            pauseBtn.Width = 48;
            pauseBtn.FlatStyle = FlatStyle.Flat;
            pauseBtn.FlatAppearance.BorderSize = 0;
            pauseBtn.BackColor = Theme.BgTertiary;
            pauseBtn.ForeColor = Theme.TextPrimary;
            var pauseMenu = new ContextMenuStrip();
            pauseMenu.Items.Add("暂停 2 小时", null, delegate { PauseProactive(DateTime.Now.AddHours(2)); });
            pauseMenu.Items.Add("暂停到明天", null, delegate { PauseProactive(DateTime.Today.AddDays(1).AddHours(8)); });
            pauseMenu.Items.Add("取消暂停", null, delegate { PauseProactive(DateTime.MinValue); });
            pauseBtn.Click += delegate { pauseMenu.Show(pauseBtn, new Point(0, -pauseMenu.Height)); };
            bottom.Controls.Add(pauseBtn);
            bottom.Controls.Add(auditBtn);
            bottom.Controls.Add(stopBtn);


            var topStop = new Button();
            _topStop = topStop;
            topStop.Text = "■";
            topStop.Dock = DockStyle.None;
            topStop.Width = 38;
            topStop.FlatStyle = FlatStyle.Flat;
            topStop.FlatAppearance.BorderSize = 0;
            topStop.BackColor = Theme.StateDanger;
            topStop.ForeColor = Theme.DangerButtonFg;
            topStop.Click += delegate { ToggleEmergencyStop(); };
            var top = new Panel();
            _topBar = top;
            top.Dock = DockStyle.Top;
            top.Height = 32;
            top.BackColor = Theme.BgPrimary;
            top.Paint += delegate(object s, PaintEventArgs e) { using (var pen = new Pen(Theme.BorderSubtle, 1)) e.Graphics.DrawLine(pen, 0, top.Height - 1, top.Width, top.Height - 1); };
            _topDot = new Label(); _topDot.Text = "●"; _topDot.AutoSize = false; _topDot.Width = 22; _topDot.Dock = DockStyle.Left; _topDot.TextAlign = ContentAlignment.MiddleCenter; _topDot.ForeColor = Theme.StateInfo; _topDot.Font = new Font("Segoe UI", 10f);
            _topIdle = new Label(); _topIdle.Dock = DockStyle.Left; _topIdle.Width = 140; _topIdle.TextAlign = ContentAlignment.MiddleLeft; _topIdle.ForeColor = Theme.TextPrimary; _topIdle.Font = new Font("Microsoft YaHei UI", 9f);
            _topFore = new Label(); _topFore.Dock = DockStyle.Left; _topFore.Width = 300; _topFore.TextAlign = ContentAlignment.MiddleLeft; _topFore.ForeColor = Theme.TextPrimary; _topFore.Font = new Font("Microsoft YaHei UI", 9f);
            _topDisk = new Label(); _topDisk.Dock = DockStyle.None; _topDisk.Width = 150; _topDisk.TextAlign = ContentAlignment.MiddleRight; _topDisk.ForeColor = Theme.TextPrimary; _topDisk.Font = new Font("Consolas", 9f);
            _topClock = new Label(); _topClock.Dock = DockStyle.None; _topClock.Width = 90; _topClock.TextAlign = ContentAlignment.MiddleRight; _topClock.ForeColor = Theme.TextPrimary; _topClock.Font = new Font("Consolas", 9f);
            _topDisk.Height = 32; _topClock.Height = 32; topStop.Height = 32;
            top.Controls.Add(_topDisk);
            top.Controls.Add(_topClock);
            top.Controls.Add(topStop);
            top.Controls.Add(_topFore); top.Controls.Add(_topIdle); top.Controls.Add(_topDot);
            top.Resize += delegate { PositionTopRight(); };

            _stopBanner = new Panel();
            _stopBanner.Dock = DockStyle.Fill;
            _stopBanner.BackColor = Theme.BgDisputed;
            _stopBanner.Visible = false;
            _stopBannerText = new Label();
            _stopBannerText.Dock = DockStyle.Fill;
            _stopBannerText.Text = "已急停：脉冲和当前任务已停止";
            _stopBannerText.TextAlign = ContentAlignment.MiddleLeft;
            _stopBannerText.Padding = new Padding(16, 0, 0, 0);
            _stopBannerText.ForeColor = Theme.StateWarning;
            _stopBannerText.Font = new Font("Microsoft YaHei UI", 9f);
            _stopBanner.Controls.Add(_stopBannerText);

            var main = new Panel();
            main.Dock = DockStyle.Fill;
            var right = new Panel();
            right.Dock = DockStyle.Fill;
            right.Controls.Add(_htmlHost);
            right.Controls.Add(_tabs);
            right.Controls.Add(_webErrorPanel);
            right.Controls.Add(tabBar);
            right.Padding = new Padding(0, 42, 0, 0);
            tabBar.BringToFront();
            _tabs.Resize += delegate { FixTabPages(); };
            main.Controls.Add(right);
            main.Controls.Add(_side);
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            _stopRow = new RowStyle(SizeType.Absolute, 0);
            _hintRow = new RowStyle(SizeType.Absolute, 0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(_stopRow);
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(_hintRow);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(_stopBanner, 0, 1);
            root.Controls.Add(main, 0, 2);
            root.Controls.Add(_hintPanel, 0, 3);
            root.Controls.Add(bottom, 0, 4);
            Controls.Add(root);
            _clockTimer = new System.Windows.Forms.Timer();
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += delegate
            {
                _topClock.Text = DateTime.Now.ToString("HH:mm:ss");
                if (_lastBodySample != default(DateTime) && (DateTime.Now - _lastBodySample).TotalSeconds > 90) RestartBodySensor();
            };
            _clockTimer.Start();
            _breathTimer = new System.Windows.Forms.Timer();
            _breathTimer.Interval = 100;
            _breathTimer.Tick += delegate
            {
                double phase = 0.5 + 0.5 * Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * Math.PI / 1.25);
                Color c = Color.FromArgb((int)(Theme.StateInfoDim.R + (Theme.StateInfo.R - Theme.StateInfoDim.R) * phase), (int)(Theme.StateInfoDim.G + (Theme.StateInfo.G - Theme.StateInfoDim.G) * phase), (int)(Theme.StateInfoDim.B + (Theme.StateInfo.B - Theme.StateInfoDim.B) * phase));
                if (_topDot != null && !_recovering && _health.Snapshot().State == HealthState.Healthy)
                    _topDot.ForeColor = c;
            };

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "Ahz Scribe";
            _tray.Visible = true;
            SetTrayColor(Theme.StateNeutral);
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开 Ahz Scribe", null, delegate { ShowWithFade(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            _trayStatus = new ToolStripMenuItem("当前状态：—"); _trayStatus.Enabled = false; trayMenu.Items.Add(_trayStatus);
            _trayPulse = new ToolStripMenuItem("最近脉冲：—"); _trayPulse.Enabled = false; trayMenu.Items.Add(_trayPulse);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("查看审计", null, delegate { Show(); _tabs.SelectedIndex = 3; UpdateNav(); });
            var trayPause = new ToolStripMenuItem("暂停主动");
            trayPause.DropDownItems.Add("2 小时", null, delegate { PauseProactive(DateTime.Now.AddHours(2)); });
            trayPause.DropDownItems.Add("直到明天", null, delegate { PauseProactive(DateTime.Today.AddDays(1).AddHours(8)); });
            trayPause.DropDownItems.Add("取消", null, delegate { PauseProactive(DateTime.MinValue); });
            trayMenu.Items.Add(trayPause);
            trayMenu.Items.Add("急停", null, delegate { EmergencyStop(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { if (MessageBox.Show("确定退出 AhzScribe？后台脉冲将停止", "Ahz Scribe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { _reallyExit = true; _tray.Visible = false; Close(); } });
            _tray.ContextMenuStrip = trayMenu;
            _tray.DoubleClick += delegate { ShowWithFade(); };
            _tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWithFade(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                try { if (WindowState == FormWindowState.Normal) { _settings.WindowLeft = Left; _settings.WindowTop = Top; _settings.WindowWidth = Width; _settings.WindowHeight = Height; _settings.Save(_dataDir); } } catch { }
                if (!_reallyExit)
                {
                    e.Cancel = true;
                    string confirm = Path.Combine(_dataDir, "first_close_confirm_shown");
                    if (!File.Exists(confirm))
                    {
                        DialogResult r = MessageBox.Show(this, "关闭后将缩到托盘，后台脉冲继续运行。是否继续？", "Ahz Scribe", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (r != DialogResult.Yes) return;
                        try { File.WriteAllText(confirm, DateTime.Now.ToString("o"), new UTF8Encoding(false)); } catch { }
                    }
                    Hide();
                }
            };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            top.BringToFront();
            bottom.BringToFront();

            Append("face v0 · model=" + _modelName);
            Append("输入问题；/tool 前缀让模型使用工具。危险操作会弹确认框。");
            string firstRun = Path.Combine(_dataDir, "first_run_tray_hint_shown");
            if (!File.Exists(firstRun))
            {
                _hintPanel.Visible = true;
                if (_hintRow != null) _hintRow.Height = 30;
                try { File.WriteAllText(firstRun, DateTime.Now.ToString("o"), new UTF8Encoding(false)); } catch { }
            }
            MmyMemory.EnsureMigrated(_dataDir);
            List<FaceMessage> history = FaceSession.Load(_sessionPath, 200);
            if (history.Count == 0) Append("这是你的连续时间线。没有 /new，也没有会话列表；直接说话就行。");
            foreach (FaceMessage old in history)
            {
                if (old.Kind == "tool") AppendTrace(old.Text);
                else AppendHistory(old);
            }
            RememberOldEpisodes(history);
            RenderMemory();
            UpdateStatus();
            FaceState state = FaceSession.LoadState(_sessionPath);
            if (state != null) UpdateTitle(state.CurrentFocus);
            UpdateStatus();
            CheckMixedDeviceMemory();
            MigrationResult mr = state == null
                ? new MigrationResult { Kind = MigrationKind.NormalRestart }
                : MigrationGuard.Check(_dataDir, _sessionPath, state, _modelName,
                    _identityResult == null ? "" : _identityResult.PreviousModel);
            _migrationReport = MigrationDiffService.Record(_dataDir, _sessionPath,
                _identityManifest, mr, DateTimeOffset.Now);
            if (mr.IsResurrection) StartResurrection(mr);
            InitializeHealthComponents();
            RunStartupChecks();
            RefreshSelfModel();
            ActivationServer.Start(ShowWithFade);
            KeyboardHook.Install(delegate { try { BeginInvoke(new Action(ShowWithFade)); } catch { } });
            SyncEmergencyUi();
        }

        static string EnsureSoulId(string dataDir)
        {
            string path = Path.Combine(dataDir, "soul_id");
            try
            {
                string pendingSoul = SoulBackup.AdoptPendingSoulId(dataDir);
                if (!string.IsNullOrWhiteSpace(pendingSoul)) return pendingSoul;
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(existing)) return existing;
                }
                string created = "soul_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(path, created, new UTF8Encoding(false));
                return created;
            }
            catch { return "soul_" + Guid.NewGuid().ToString("N"); }
        }

        void LoadOrCreateIdentity()
        {
            PermissionTier ceiling = _settings.ReadOnlyMode ? PermissionTier.ReadOnly : PermissionTier.Full;
            string profile = _settings.ReadOnlyMode ? "read_only" : "interactive";
            _identityResult = _identityStore.LoadOrCreate(
                _soulId, MmyMemory.DeviceId(), _modelName, profile, ceiling);
            _identityManifest = _identityResult == null ? null : _identityResult.Manifest;
            _host.IdentitySummary = _identityResult != null && _identityResult.Verification != null &&
                _identityResult.Verification.Valid
                ? IdentityManifestStore.DescribeForModel(_identityManifest)
                : "";
            if (_identityResult == null) return;
            PermissionTier identityCeiling;
            if (_identityResult.Verification != null && _identityResult.Verification.Valid &&
                _identityManifest != null &&
                Enum.TryParse<PermissionTier>(_identityManifest.PermissionCeiling, true, out identityCeiling) &&
                identityCeiling == PermissionTier.ReadOnly)
            {
                _settings.ReadOnlyMode = true;
                if (_identityResult.CeilingClamped)
                {
                    FaceAudit.Append(_dataDir, "identity", "permission_ceiling", "blocked",
                        "requested higher permission than " + _identityManifest.PermissionCeiling,
                        "identity invariant permission ceiling enforced");
                }
            }
            if (_identityResult.Created)
            {
                FaceAudit.Append(_dataDir, "identity", "manifest", "allowed",
                    _identityManifest.SoulId, "identity manifest created");
            }
            if (_identityResult.EnvironmentChanged)
            {
                bool modelChanged = !string.Equals(_identityResult.PreviousModel,
                    _identityManifest.ActiveModel, StringComparison.Ordinal);
                bool deviceChanged = !string.Equals(_identityResult.PreviousDeviceId,
                    _identityManifest.CurrentDeviceId, StringComparison.OrdinalIgnoreCase);
                if (modelChanged)
                {
                    FaceAudit.Append(_dataDir, "identity", "model_switch", "allowed",
                        (_identityResult.PreviousModel ?? "") + " -> " +
                        (_identityManifest.ActiveModel ?? ""),
                        "model is mutable; identity invariants preserved");
                }
                if (deviceChanged)
                {
                    FaceAudit.Append(_dataDir, "identity", "device_switch", "allowed",
                        (_identityResult.PreviousDeviceId ?? "") + " -> " +
                        (_identityManifest.CurrentDeviceId ?? ""),
                        "device is mutable source; identity invariants preserved");
                }
            }
            if (_identityResult.Verification == null || !_identityResult.Verification.Valid)
            {
                string reason = _identityResult.Verification == null ? "identity verification missing" :
                    string.Join("; ", _identityResult.Verification.Errors);
                FaceAudit.Append(_dataDir, "identity", "verify", "blocked", reason,
                    "identity invariant verification failed");
            }
        }

        async void RefreshSelfModel()
        {
            try
            {
                PermissionTier maximum = _settings.ReadOnlyMode ? PermissionTier.ReadOnly : PermissionTier.Full;
                string profile = _settings.ReadOnlyMode ? "read_only" : "interactive";
                await _host.RefreshSelfModelAsync(
                    _soulId, MmyMemory.DeviceId(), profile, maximum, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Append("自我模型加载失败：" + ex.Message);
            }
        }

        void RefreshAutobiographyContext()
        {
            try
            {
                List<MemoryItem> memories = _memory.LoadAllAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                FaceState state = FaceSession.LoadState(_sessionPath);
                _autobiography.BuildRecent(memories,
                    state == null ? "" : state.CurrentFocus,
                    DateTimeOffset.Now, 7);
                if (_host != null)
                    _host.AutobiographySummary = _autobiography.BuildContext(8);
            }
            catch (Exception ex)
            {
                AppendTrace("[自传] 刷新失败：" + ex.Message);
            }
        }

        void RefreshAttentionContext()
        {
            try
            {
                List<MemoryItem> memories = _memory.LoadAllAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                _attention.RefreshFromMemories(memories, DateTimeOffset.Now);
                if (_host != null)
                {
                    _host.Attention = _attention;
                    _host.AttentionSummary = _attention.BuildContext(6);
                }
            }
            catch (Exception ex)
            {
                AppendTrace("[关注] 刷新失败：" + ex.Message);
            }
        }

        void RefreshGoalContext()
        {
            try
            {
                if (_host != null)
                {
                    _host.Goals = _goals;
                    _host.GoalSummary = _goals.BuildContext(6);
                }
            }
            catch (Exception ex)
            {
                AppendTrace("[目标] 刷新失败：" + ex.Message);
            }
        }

        void InitializeHealthComponents()
        {
            bool memoryHealthy = true;
            string memoryReason = "";
            try { _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                memoryHealthy = false;
                memoryReason = ex.GetType().Name + ": " + ex.Message;
            }
            _health.ReportComponent("memory", memoryHealthy, memoryReason);

            string auditReason;
            bool auditHealthy = FaceAudit.Probe(_dataDir, out auditReason);
            _health.ReportComponent("audit", auditHealthy, auditReason);
            _health.ReportComponent("model", true, "");
            bool identityHealthy = _identityResult != null && _identityResult.Verification != null &&
                _identityResult.Verification.Valid;
            string identityReason = identityHealthy ? "" :
                (_identityResult == null || _identityResult.Verification == null
                    ? "identity manifest unavailable"
                    : string.Join("; ", _identityResult.Verification.Errors));
            _health.ReportComponent("identity", identityHealthy, identityReason);
            _health.SetStopped(File.Exists(StopPath()), "EMERGENCY_STOP is active");
            RefreshStorageBudget();
            ApplyHealthSnapshot(_health.Snapshot());
        }

        async Task RecheckHealthComponentsAsync()
        {
            bool memoryHealthy = true;
            string memoryReason = "";
            try { _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                memoryHealthy = false;
                memoryReason = ex.GetType().Name + ": " + ex.Message;
            }
            _health.ReportComponent("memory", memoryHealthy, memoryReason);

            string auditReason;
            bool auditHealthy = FaceAudit.Probe(_dataDir, out auditReason);
            _health.ReportComponent("audit", auditHealthy, auditReason);
            LoadOrCreateIdentity();
            bool identityHealthy = _identityResult != null && _identityResult.Verification != null &&
                _identityResult.Verification.Valid;
            string identityReason = identityHealthy ? "" :
                (_identityResult == null || _identityResult.Verification == null
                    ? "identity manifest unavailable"
                    : string.Join("; ", _identityResult.Verification.Errors));
            _health.ReportComponent("identity", identityHealthy, identityReason);
            try
            {
                PermissionTier maximum = _settings.ReadOnlyMode ? PermissionTier.ReadOnly : PermissionTier.Full;
                string profile = _settings.ReadOnlyMode ? "read_only" : "interactive";
                await _host.RefreshSelfModelAsync(
                    _soulId, MmyMemory.DeviceId(), profile, maximum, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _health.ReportComponent("body", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        void OnHealthChanged(HealthSnapshot snapshot, HealthTransition transition)
        {
            try
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action<HealthSnapshot, HealthTransition>(ApplyHealthTransition), snapshot, transition);
                    return;
                }
                ApplyHealthTransition(snapshot, transition);
            }
            catch { }
        }

        void OnRetryObserved(string subject, string phase)
        {
            try
            {
                string detail = subject + " retry " + phase;
                FaceAudit.Append(_dataDir, "resource_budget", subject, phase, phase, detail);
                AppendTrace("[预算] " + detail);
            }
            catch { }
        }

        void ApplyHealthTransition(HealthSnapshot snapshot, HealthTransition transition)
        {
            ApplyHealthSnapshot(snapshot);
            if (transition == null) return;
            string text = transition.From + " -> " + transition.To + " / " + transition.Component +
                " / " + transition.Reason;
            FaceAudit.Append(_dataDir, "health", transition.Component,
                transition.To == HealthState.Healthy ? "allowed" : "blocked",
                transition.To.ToString(), text);
            AppendTrace("[健康] " + text);
        }

        void ApplyHealthSnapshot(HealthSnapshot snapshot)
        {
            if (snapshot == null) return;
            try
            {
                File.WriteAllText(Path.Combine(_dataDir, "health.json"),
                    System.Text.Json.JsonSerializer.Serialize(snapshot,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }

            string firstProblem = snapshot.ActiveProblems == null || snapshot.ActiveProblems.Count == 0
                ? "组件正常"
                : snapshot.ActiveProblems[0];
            if (_stateBody != null)
                _stateBody.Text = "系统 " + snapshot.State + "\n" + firstProblem;
            Color stateColor = snapshot.State == HealthState.Healthy ? Theme.StateInfo :
                (snapshot.State == HealthState.Stopped ? Theme.StateDanger : Theme.StateWarning);
            if (_topDot != null) _topDot.ForeColor = stateColor;
            if (_tray != null) _tray.Text = "Ahz Scribe · " + snapshot.State;
        }

        void RefreshStorageBudget()
        {
            long memoryBytes = 0;
            memoryBytes += FileSize(Path.Combine(_dataDir, "memory.jsonl"));
            memoryBytes += FileSize(Path.Combine(_dataDir, "semantic.jsonl"));
            memoryBytes += FileSize(Path.Combine(_dataDir, "episodic.jsonl"));
            memoryBytes += FileSize(Path.Combine(_dataDir, "disputed.jsonl"));
            memoryBytes += FileSize(Path.Combine(_dataDir, "memory-lifecycle.jsonl"));
            memoryBytes += FileSize(Path.Combine(_dataDir, "memory-usage.json"));
            try
            {
                foreach (string file in Directory.GetFiles(Path.Combine(_dataDir, "memory"), "*.mmy"))
                    memoryBytes += FileSize(file);
            }
            catch { }

            long logBytes = 0;
            logBytes += FileSize(Path.Combine(_dataDir, "pulse.jsonl"));
            logBytes += FileSize(Path.Combine(_dataDir, "sessions.jsonl"));
            logBytes += FileSize(Path.Combine(_dataDir, "audit.jsonl"));
            try
            {
                foreach (string file in Directory.GetFiles(_dataDir, "*.log"))
                    logBytes += FileSize(file);
            }
            catch { }

            _budget.UpdateStorage(memoryBytes, logBytes);
            try
            {
                File.WriteAllText(Path.Combine(_dataDir, "resource-budget.json"),
                    JsonSerializer.Serialize(_budget.Snapshot(),
                        new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static long FileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        static string MiB(long bytes)
        {
            return (bytes / 1048576.0).ToString("F1") + " MiB";
        }

        void ApplySelfModelSnapshot(SelfModelSnapshot snapshot)
        {
            if (snapshot == null) return;
            _selfModel = snapshot;
            try
            {
                File.WriteAllText(
                    Path.Combine(_dataDir, "self_model.json"),
                    System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
            int available = snapshot.Capabilities == null ? 0 : snapshot.Capabilities.Count(x => x.Available);
            int total = snapshot.Capabilities == null ? 0 : snapshot.Capabilities.Count;
            if (_selfModelLabel != null)
                _selfModelLabel.Text = "soul " + snapshot.SoulId + "\n权限 " + snapshot.PermissionProfile +
                    " · 能力 " + available + "/" + total +
                    (snapshot.OpenConstraints == null || snapshot.OpenConstraints.Count == 0 ? "" : "\n约束 " + string.Join("；", snapshot.OpenConstraints));
            Append("自我模型已更新：" + available + "/" + total + " 项能力可用");
        }

        const int WM_HOTKEY = 0x0312;
        const int MOD_ALT = 0x0001;
        const int MOD_CONTROL = 0x0002;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                bool a = RegisterHotKey(Handle, HotKeyId, MOD_CONTROL | MOD_ALT, (int)Keys.Space);
                bool b = RegisterHotKey(Handle, HotKeyId2, MOD_CONTROL | MOD_ALT | 0x0004, (int)Keys.Space);
                try { File.AppendAllText(Path.Combine(_dataDir, "hotkey.log"), "ctrl_alt_space=" + a + " ctrl_alt_shift_space=" + b + "\n"); } catch { }
            }
            catch { }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { UnregisterHotKey(Handle, HotKeyId); UnregisterHotKey(Handle, HotKeyId2); } catch { }
            base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (m.WParam.ToInt32() == HotKeyId || m.WParam.ToInt32() == HotKeyId2)) { ShowWithFade(); return; }
            base.WndProc(ref m);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (Width > 1600) Width = 1600;
                if (Height > 1040) Height = 1040;
                int maxH = (int)(wa.Height * 96.0 / DeviceDpi) - 140;
                int maxW = (int)(wa.Width * 96.0 / DeviceDpi) - 80;
                if (ClientSize.Height > maxH && maxH > 400) ClientSize = new Size(ClientSize.Width, maxH);
                if (ClientSize.Width > maxW && maxW > 700) ClientSize = new Size(maxW, ClientSize.Height);
            }
            catch { }
        }
        IBrainModel CreateModel(out string name)
        {
            string key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            string baseUrl = Environment.GetEnvironmentVariable("AHZ_MODEL_BASE") ?? "https://api.deepseek.com";
            string model = Environment.GetEnvironmentVariable("AHZ_MODEL_NAME") ?? "deepseek-v4-pro";
            name = model;
            if (string.IsNullOrEmpty(key)) return new EchoModel();
            return new OpenAiToolModel(new ModelOptions { BaseUrl = baseUrl, Model = model, ApiKey = key, MaxTokens = 4096 });
        }

        void Append(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), text); return; }
            _output.AppendText(text + Environment.NewLine + Environment.NewLine);
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        }

        async void RememberOldEpisodes(List<FaceMessage> history)
        {
            try
            {
                await Task.Run(async delegate
                {
                    FaceState st = FaceSession.LoadState(_sessionPath);
                    string current = st == null ? "" : st.CurrentEpisode;
                    var known = new HashSet<string>(_memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult()
                        .Select(x => x.Key ?? ""), StringComparer.OrdinalIgnoreCase);
                    bool realModel = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"));
                    foreach (var g in history.Where(x => !string.IsNullOrEmpty(x.Episode) && x.Episode != current)
                        .GroupBy(x => x.Episode))
                    {
                        if (known.Contains("episode:" + g.Key)) continue;
                        var recent = g.Skip(Math.Max(0, g.Count() - 8)).ToList();
                        string fallback = string.Join("\n", recent.Select(x => (x.Role ?? "") + ": " + Cut(x.Text, 200)));
                        string summary = fallback;
                        if (realModel)
                        {
                            try
                            {
                                string prompt = "请把下面这段旧的对话压缩成 1-3 句 episodic memory，只写事实、承诺和偏好，不要寒暄：\n" + fallback;
                                string generated = await _host.AskAsync(prompt, CancellationToken.None).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(generated)) summary = generated.Trim();
                            }
                            catch { }
                        }
                        string sourceAudit = FaceAudit.Append(_dataDir, "memory", "extract", "allowed", "episode " + g.Key, "episode summary");
                        _memory.RememberAsync(new MemoryItem
                        {
                            Kind = "episodic",
                            Key = "episode:" + g.Key,
                            Value = summary,
                            Summary = "episode " + g.Key,
                            SourceAuditId = sourceAudit,
                            Status = "active"
                        }, CancellationToken.None).GetAwaiter().GetResult();
                        try
                        {
                            var extractor = new MemoryExtractor(_host.Model) { Budget = _budget };
                            List<MemoryItem> extracted = await extractor.ExtractAsync(fallback, sourceAudit, CancellationToken.None).ConfigureAwait(false);
                            foreach (MemoryItem m in extracted)
                                _memory.RememberAsync(m, CancellationToken.None).GetAwaiter().GetResult();
                        }
                        catch { }
                    }
                }).ConfigureAwait(false);
            }
            catch { }
        }

        static string StopPath()
        {
            string f = Environment.GetEnvironmentVariable("AHZ_STOP_FILE");
            if (!string.IsNullOrEmpty(f)) return f;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AhzScribe", "state", "EMERGENCY_STOP");
        }
        static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
        void AppendHistory(FaceMessage m)
        {
            string role = m.Role ?? "";
            if (role == "工具") { _output.SelectionColor = Theme.TextTertiary; _output.SelectionBackColor = Theme.BgPrimary; _output.AppendText((m.Text ?? "") + Environment.NewLine); return; }
            AppendBubble(role, m.Text ?? "", m.Kind ?? "chat", false);
        }
        void AppendBubble(string role, string text, string kind, bool persist)
        {
            bool user = role == "你";
            _output.SelectionAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _output.SelectionBackColor = user ? Theme.BgElevated : Theme.BgSecondary;
            _output.SelectionColor = kind == "error" ? Theme.StateDanger : Theme.TextPrimary;
            _output.SelectionFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            string label = user ? "你" : (role == "工具" ? "工具" : "Ahz");
            _output.AppendText("  " + label + "  " + text + "  " + Environment.NewLine + Environment.NewLine);
            _output.SelectionBackColor = Theme.BgPrimary;
            _output.SelectionAlignment = HorizontalAlignment.Left;
        }
        void NoteResponse()
        {
            if (_lastResponse != null) { _lastResponse.Text = "最后响应：" + DateTime.Now.ToString("HH:mm:ss"); _lastResponse.ForeColor = Theme.TextTertiary; }
        }
        void AppendChat(string role, string text, string kind)
        {
            FaceSession.Append(_sessionPath, new FaceMessage { Role = role, Text = text, Kind = kind });
            AppendBubble(role, text ?? "", kind, true);
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
            if (role == "Ahz") NoteResponse();
        }
        void AppendTrace(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendTrace), text); return; }
            FaceSession.Append(_sessionPath, new FaceMessage { Role = "工具", Text = text, Kind = "tool" });
            _traceRaw.AppendLine("[工具] " + text);
            RefreshTrace();
        }
        void RefreshTrace()
        {
            string filter = _traceFilter == null ? "" : _traceFilter.Text.Trim();
            string raw = _traceRaw.ToString();
            if (filter.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (string line in raw.Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                    if (line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) sb.AppendLine(line);
                raw = sb.ToString();
            }
            _trace.Text = raw;
            _trace.SelectionStart = _trace.TextLength;
            _trace.ScrollToCaret();
        }
        void RenderMemory()
        {
            try
            {
                List<MemoryItem> all = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                var sb = new System.Text.StringBuilder();
                string filter = _memoryFilter == null ? "" : _memoryFilter.Text.Trim();
                if (filter.Length > 0)
                    all = all.Where(x => ((x.Key ?? "") + " " + (x.Value ?? "") + " " + (x.Kind ?? "")).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (MemoryItem m in all.Skip(Math.Max(0, all.Count - 30)))
                    sb.AppendLine("[" + (m.Kind ?? "") + "] " + (m.Key ?? "") + " = " + Cut(m.Value, 200));
                _memoryView.Text = sb.ToString();
            }
            catch { }
        }
        TextBox MakeSettingBox(string value)
        {
            var b = new TextBox();
            b.Text = value ?? "";
            b.Dock = DockStyle.Fill;
            b.BackColor = Theme.BgSecondary;
            b.ForeColor = Theme.TextPrimary;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.Font = new Font("Consolas", 9.5f);
            b.Margin = new Padding(0, 4, 0, 4);
            return b;
        }
        bool AllowProactive()
        {
            FaceState st = FaceSession.LoadState(_sessionPath);
            if (st == null) return true;
            DateTime now = DateTime.Now;
            if (now.Hour < 8 || now.Hour >= 23) return false;
            DateTime paused;
            if (DateTime.TryParse(st.ProactivePausedUntil, out paused) && paused > now) return false;
            string today = now.ToString("yyyy-MM-dd");
            if (st.LastProactiveDate != today) { st.LastProactiveDate = today; st.ProactiveCount = 0; }
            if (st.ProactiveCount >= 2) return false;
            st.ProactiveCount++;
            st.LastProactiveAt = now.ToString("o");
            st.UnansweredProactive++;
            if (st.UnansweredProactive >= 2) st.ProactivePausedUntil = now.AddHours(24).ToString("o");
            FaceSession.SaveState(_sessionPath, st);
            return true;
        }
        void RunDrive()
        {
            try
            {
                FaceState st = FaceSession.LoadState(_sessionPath);
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (st != null && st.LastDriveDate == today) { AppendTrace("[drive] already today"); return; }
                List<MemoryItem> all = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                DriveGoal g = DriveEngine.Pick(all);
                GoalState goal = null;
                try
                {
                    if (g != null)
                    {
                        List<AutobiographyEvidence> evidence = GoalEvidence(g.EvidenceIds, all);
                        if (evidence.Count == 0)
                        {
                            AppendTrace("[drive] no source evidence");
                            return;
                        }
                        goal = _goals.Propose(g.Text, g.Reason, g.SourceKind, evidence,
                            g.Score, DateTimeOffset.Now);
                    }
                    else
                    {
                        AttentionState attention = _attention.Dominant(DateTimeOffset.Now);
                        if (attention == null || attention.Sources == null ||
                            attention.Sources.Count == 0) { AppendTrace("[drive] no goal"); return; }
                        var evidence = new List<AutobiographyEvidence>();
                        for (int i = 0; i < attention.EvidenceIds.Count; i++)
                        {
                            SourceRef source = attention.Sources.Count == 0
                                ? null
                                : attention.Sources[Math.Min(i, attention.Sources.Count - 1)];
                            evidence.Add(new AutobiographyEvidence
                            {
                                EvidenceId = attention.EvidenceIds[i],
                                Source = source
                            });
                        }
                        goal = _goals.Propose("持续关注：" + attention.Subject,
                            "长期关注成为稳定目标来源", GoalSourceKind.AttentionChange,
                            evidence, Math.Min(0.9, 0.5 + attention.Score / 20.0),
                            DateTimeOffset.Now);
                    }
                    if (goal.Status == GoalStatus.Candidate)
                        goal = _goals.Promote(goal.GoalId, DateTimeOffset.Now);
                }
                catch (InvalidDataException ex)
                {
                    AppendTrace("[drive] none: " + ex.Message);
                    FaceAudit.Append(_dataDir, "goal", "propose", "blocked",
                        g == null ? "attention" : g.Type, ex.Message);
                    return;
                }
                FaceState now = FaceSession.LoadState(_sessionPath);
                if (now != null) { now.LastDriveDate = today; FaceSession.SaveState(_sessionPath, now); }
                FaceAudit.Append(_dataDir, "goal", "propose", "allowed",
                    goal.GoalId, goal.SourceKind + ": " + goal.Reason);
                RefreshGoalContext();
                AppendTrace("[goal] " + goal.SourceKind + " score=" +
                    goal.Score.ToString("F2") + " " + goal.Title);
            }
            catch { }
        }

        static List<AutobiographyEvidence> GoalEvidence(IEnumerable<string> ids,
            List<MemoryItem> memories)
        {
            var result = new List<AutobiographyEvidence>();
            foreach (string id in ids ?? new List<string>())
            {
                MemoryItem memory = memories.FirstOrDefault(x => x != null &&
                    string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (memory == null) continue;
                SourceRef source = memory.Provenance != null &&
                    memory.Provenance.Sources != null
                    ? memory.Provenance.Sources.FirstOrDefault(x => x != null)
                    : null;
                if (source == null)
                    source = new SourceRef
                    {
                        SourceId = memory.SourceAuditId,
                        Kind = SourceKind.Memory,
                        DeviceId = memory.OriginDevice ?? "",
                        Ts = memory.Ts ?? "",
                        Summary = memory.Summary ?? memory.Value ?? ""
                    };
                result.Add(new AutobiographyEvidence
                {
                    EvidenceId = "memory:" + memory.Id,
                    Source = source
                });
            }
            return result;
        }
        void PulseTick()
        {
            try
            {
                if (File.Exists(StopPath())) return;
                FaceState st = FaceSession.LoadState(_sessionPath);
                if (st != null && !string.IsNullOrEmpty(st.ActiveTask)) return;
                DateTime lastCheck = DateTime.MinValue;
                if (st != null && !string.IsNullOrEmpty(st.LastIntentCheck)) DateTime.TryParse(st.LastIntentCheck, out lastCheck);
                DateTime now = DateTime.Now;
                bool checkDue = (now - lastCheck).TotalMinutes >= 10;
                bool hasOpen = st != null && st.OpenIntents != null && st.OpenIntents.Any(x => x.Status == "open" || x.Status == "monitoring");
                bool hasDue = hasOpen && st.OpenIntents.Any(x =>
                {
                    if (x.Status != "open" && x.Status != "monitoring") return false;
                    DateTime dueAt;
                    return string.IsNullOrEmpty(x.DueAt) || (DateTime.TryParse(x.DueAt, out dueAt) && dueAt <= now);
                });
                bool due = checkDue && hasDue;
                RefreshStorageBudget();
                if (!_budget.CanRunPulse(due))
                {
                    FaceAudit.Append(_dataDir, "pulse", "scheduled", "blocked",
                        "low_priority_budget", "storage or task budget exhausted");
                    return;
                }
                if (checkDue && st != null) { st.LastIntentCheck = now.ToString("o"); FaceSession.SaveState(_sessionPath, st); }
                bool idle = _sensor != null && _sensor.Snapshot().IdleSeconds >= 1800;
                string type = due ? "intent_due" : (idle && hasOpen ? "body_event" : "timer");
                FaceSession.SetPulseTrigger(_sessionPath, type, "scheduled");
                PulseDecision d = _arbiter.EnqueueAsync(new PulseTrigger { Type = type, Payload = "scheduled" }, CancellationToken.None).GetAwaiter().GetResult();
                WritePulseDetail(type, d, st);
            }
            catch { }
        }
        void WritePulseDetail(string type, PulseDecision d, FaceState st)
        {
            try
            {
                if (d == null) return;
                var restore = new Dictionary<string, object>
                {
                    ["idle_seconds"] = _sensor == null ? 0 : _sensor.Snapshot().IdleSeconds,
                    ["current_focus"] = st == null ? "" : st.CurrentFocus,
                    ["open_intents"] = st == null || st.OpenIntents == null ? new List<object>() : st.OpenIntents.Where(x => x.Status == "open" || x.Status == "monitoring").Select(x => (object)new { id = x.Id, condition = x.Text, status = x.Status }).ToList(),
                    ["active_task"] = st == null ? "" : st.ActiveTask
                };
                var writeback = new Dictionary<string, object>
                {
                    ["last_pulse_result"] = d.Action ?? "none",
                    ["mood"] = st == null ? "平静" : (string.IsNullOrEmpty(st.ActiveTask) ? "平静" : "专注")
                };
                string action = string.IsNullOrEmpty(d.Action) ? "none" : d.Action;
                string summary = d.Text == null ? "" : d.Text;
                if (summary.Length > 60) summary = summary.Substring(0, 60);
                var rec = new Dictionary<string, object>
                {
                    ["pulse_id"] = "pulse_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"),
                    ["ts"] = DateTimeOffset.Now.ToString("o"),
                    ["trigger_type"] = type,
                    ["action"] = action,
                    ["summary_line"] = summary.Length == 0 ? action : summary,
                    ["audit_id"] = d.AuditId ?? "",
                    ["state_restore"] = restore,
                    ["thought"] = d.Thought ?? "",
                    ["reason"] = d.Reason ?? "",
                    ["confidence"] = d.Confidence,
                    ["applied_rules"] = d.AppliedRules ?? new List<string>(),
                    ["tool_calls"] = new List<object>(),
                    ["state_writeback"] = writeback,
                    ["memories_injected"] = d.MemoriesInjected ?? new List<string>()
                };                File.AppendAllText(Path.Combine(_dataDir, "pulse.jsonl"), JsonSerializer.Serialize(rec) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
        public string SensorStaleTest()
        {
            _lastBodySample = DateTime.Now.AddSeconds(-100);
            RestartBodySensor();
            bool ok = (DateTime.Now - _lastBodySample).TotalSeconds < 5;
            return "sensor_stale_restart=" + ok + " last=" + ((int)(DateTime.Now - _lastBodySample).TotalSeconds) + "s";
        }

        public string PreferenceCorrectionUiTest()
        {
            const string feedback = "以后回复不要用 emoji";
            _input.Text = feedback;
            SendAsync();
            bool ruleActive = _experience.RequiresNoEmoji();
            bool experienceWritten = _experience.LoadExperiences().Any(x =>
                x != null && string.Equals(x.UserFeedback, feedback, StringComparison.Ordinal) &&
                string.Equals(x.Outcome, "user_corrected", StringComparison.OrdinalIgnoreCase));
            bool confirmationVisible = _output.Text.Contains("以后最终回答不使用 emoji 或表情符号");
            bool auditWritten = false;
            try
            {
                string auditPath = FaceAudit.PathFor(_dataDir);
                auditWritten = File.Exists(auditPath) &&
                    File.ReadAllText(auditPath, Encoding.UTF8).Contains("user corrected final response style");
            }
            catch { }
            _input.Text = "/experience-reset";
            SendAsync();
            bool ruleReset = !_experience.RequiresNoEmoji();
            bool resetVisible = _output.Text.Contains("已停用意图证据、工具失败和回答偏好适应规则");
            bool pass = ruleActive && experienceWritten && confirmationVisible && auditWritten &&
                ruleReset && resetVisible;
            return "preference_ui=" + (pass ? "PASS" : "FAIL") +
                " rule=" + ruleActive +
                " experience=" + experienceWritten +
                " confirmation=" + confirmationVisible +
                " audit=" + auditWritten +
                " reset=" + ruleReset +
                " reset_confirmation=" + resetVisible;
        }

        public string HealthUiTest()
        {
            _health.ReportComponent("model", false, "injected model outage");
            HealthSnapshot degraded = _health.Snapshot();
            bool stateChanged = degraded.State == HealthState.Degraded;
            bool visible = _stateBody != null && _stateBody.Text.Contains("Degraded");
            bool stateFile = false;
            try
            {
                string path = Path.Combine(_dataDir, "health.json");
                stateFile = File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains("Degraded");
            }
            catch { }
            _input.Text = "/health";
            SendAsync();
            bool command = _output.Text.Contains("health = Degraded") &&
                _output.Text.Contains("model = unhealthy: injected model outage");
            bool audit = false;
            try
            {
                string path = FaceAudit.PathFor(_dataDir);
                audit = File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains("\"span\":\"health\"");
            }
            catch { }
            _health.ReportComponent("model", true, "");
            bool pass = stateChanged && visible && stateFile && command && audit;
            return "health_ui=" + (pass ? "PASS" : "FAIL") +
                " degraded=" + stateChanged +
                " visible=" + visible +
                " command=" + command +
                " audit=" + audit +
                " state_file=" + stateFile;
        }

        public async Task<string> HealthFaultUiTest()
        {
            string pipe = "ahz-m3a-missing-" + Guid.NewGuid().ToString("N");
            bool bodyFailureSeen = false;
            using (var client = new BodyClient(pipe))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try { await client.GetCapabilitiesAsync(timeout.Token).ConfigureAwait(false); }
                catch { bodyFailureSeen = true; }
            }
            _health.ReportComponent("body", !bodyFailureSeen,
                bodyFailureSeen ? "injected missing body pipe" : "");
            HealthSnapshot bodyDown = _health.Snapshot();
            bool bodyDegraded = bodyDown.State == HealthState.Degraded && !_health.CanUseToolCalls;

            _health.ReportComponent("body", true, "");
            _health.ReportComponent("webview", false, "injected renderer crash");
            HealthSnapshot webviewDown = _health.Snapshot();
            bool webviewDegraded = webviewDown.State == HealthState.Degraded &&
                _health.AllowsTool("read_file", PermissionTier.ReadOnly);
            _health.ReportComponent("webview", true, "");

            bool recovered = _health.Snapshot().State == HealthState.Healthy;
            bool pass = bodyFailureSeen && bodyDegraded && webviewDegraded && recovered;
            return "health_fault=" + (pass ? "PASS" : "FAIL") +
                " body_pipe=" + bodyFailureSeen +
                " body_degraded=" + bodyDegraded +
                " webview_degraded=" + webviewDegraded +
                " recovered=" + recovered;
        }

        public string BudgetUiTest()
        {
            bool queueBlocked = false;
            try
            {
                _busySend = true;
                for (int i = 0; i < _budget.MaxPendingTasks; i++)
                    _pendingSends.Enqueue("queued-" + i);
                _budget.SetPendingCount(_pendingSends.Count);
                _input.Text = "overflow";
                SendAsync();
                queueBlocked = _pendingSends.Count == _budget.MaxPendingTasks &&
                    _output.Text.Contains("待发送队列已满");
            }
            finally
            {
                _busySend = false;
                _pendingSends.Clear();
                _budget.SetPendingCount(0);
            }

            RefreshStorageBudget();
            _input.Text = "/budget";
            SendAsync();
            bool command = _output.Text.Contains("model_attempts = " + _budget.MaxModelAttempts) &&
                _output.Text.Contains("tool_calls_1h = ");
            bool stateFile = false;
            try
            {
                string path = Path.Combine(_dataDir, "resource-budget.json");
                stateFile = File.Exists(path) &&
                    File.ReadAllText(path, Encoding.UTF8).Contains("\"MaxModelAttempts\"");
            }
            catch { }
            bool webViewCapped = false;
            try
            {
                _webFailures = 0;
                _webPermanent = false;
                for (int i = 0; i < _budget.MaxWebViewFailures; i++)
                    ShowWebFailure("budget test");
                webViewCapped = _webPermanent && !_webRetry.Enabled;
            }
            finally
            {
                _webFailures = 0;
                _webPermanent = false;
                if (_webRetry != null) _webRetry.Enabled = true;
                _health.ReportComponent("webview", true, "");
            }
            bool pass = queueBlocked && command && stateFile && webViewCapped;
            return "budget_ui=" + (pass ? "PASS" : "FAIL") +
                " queue=" + queueBlocked +
                " command=" + command +
                " state_file=" + stateFile +
                " webview_cap=" + webViewCapped;
        }

        public string IdentityUiTest()
        {
            bool manifestFile = File.Exists(_identityStore.ManifestPath);
            bool digestFile = File.Exists(_identityStore.DigestPath);
            bool valid = _identityResult != null && _identityResult.Verification != null &&
                _identityResult.Verification.Valid;
            int invariants = _identityManifest == null || _identityManifest.Invariants == null
                ? 0
                : _identityManifest.Invariants.Count;
            _input.Text = "/identity";
            SendAsync();
            bool command = _output.Text.Contains("soul = " + _soulId) &&
                _output.Text.Contains("safety.authority = program_controls_tools_and_audit");

            string currentDevice = MigrationGuard.DeviceId();
            string dormancyDir = Path.Combine(_dataDir, "identity-dormancy-" + Guid.NewGuid().ToString("N"));
            string deviceDir = Path.Combine(_dataDir, "identity-device-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dormancyDir);
            Directory.CreateDirectory(deviceDir);
            string dormancySessions = Path.Combine(dormancyDir, "sessions.jsonl");
            var dormancyState = new FaceState
            {
                DeviceId = currentDevice,
                LastPulseAt = DateTime.Now.AddDays(-2).ToString("o"),
                UpdatedAt = DateTime.Now.AddDays(-2).ToString("o"),
                SchemaVersion = 1
            };
            FaceSession.SaveState(dormancySessions, dormancyState);
            MigrationResult dormancy = MigrationGuard.Check(dormancyDir, dormancySessions,
                FaceSession.LoadState(dormancySessions));
            string dormancyPulse = Path.Combine(dormancyDir, "pulse.jsonl");

            string deviceSessions = Path.Combine(deviceDir, "sessions.jsonl");
            var deviceState = new FaceState
            {
                DeviceId = "device-before-migration",
                LastPulseAt = DateTime.Now.ToString("o"),
                UpdatedAt = DateTime.Now.ToString("o"),
                SchemaVersion = 1
            };
            FaceSession.SaveState(deviceSessions, deviceState);
            MigrationResult device = MigrationGuard.Check(deviceDir, deviceSessions,
                FaceSession.LoadState(deviceSessions));

            bool dormancySemantics = dormancy.TimeJumped && dormancy.LongDormancy &&
                !dormancy.IsResurrection && File.Exists(dormancyPulse) &&
                File.ReadAllText(dormancyPulse, Encoding.UTF8).Contains("system_dormancy") &&
                !File.ReadAllText(dormancyPulse, Encoding.UTF8).Contains("system_resurrection");
            bool deviceSemantics = device.DeviceChanged && device.IsResurrection;
            bool pass = manifestFile && digestFile && valid && invariants >= 6 && command &&
                dormancySemantics && deviceSemantics;
            return "identity_ui=" + (pass ? "PASS" : "FAIL") +
                " manifest=" + manifestFile +
                " digest=" + digestFile +
                " valid=" + valid +
                " invariants=" + invariants +
                " command=" + command +
                " dormancy=" + dormancySemantics +
                " device_change=" + deviceSemantics;
        }

        public string MigrationDiffUiTest()
        {
            bool reportFile = File.Exists(MigrationDiffService.ReportPath(_dataDir));
            bool historyFile = File.Exists(MigrationDiffService.HistoryPath(_dataDir));
            bool baselineFile = File.Exists(MigrationDiffService.BaselinePath(_dataDir));
            MigrationObservation started = MigrationObservationStore.Start(_dataDir,
                _migrationReport, DateTimeOffset.Now, 1);
            _input.Text = "/migration";
            SendAsync();
            bool query = _output.Text.Contains("migration = " + _migrationReport.Kind) &&
                _output.Text.Contains("observation = Active") &&
                _output.Text.Contains(started.ObservationId);
            _input.Text = "/migration cancel ui test";
            SendAsync();
            MigrationObservation cancelled = MigrationObservationStore.Current(_dataDir,
                DateTimeOffset.Now);
            bool cancel = cancelled.Status == MigrationObservationStatus.Cancelled &&
                _output.Text.Contains("迁移观察期已取消");
            bool pass = reportFile && historyFile && baselineFile && query && cancel;
            return "migration_diff_ui=" + (pass ? "PASS" : "FAIL") +
                " report=" + reportFile +
                " history=" + historyFile +
                " baseline=" + baselineFile +
                " query=" + query +
                " cancel=" + cancel;
        }

        public string AutobiographyUiTest()
        {
            string audit = FaceAudit.Append(_dataDir, "memory", "autobiography_ui",
                "allowed", "project:m6a", "isolated autobiography UI test");
            _memory.RememberAsync(new MemoryItem
            {
                Kind = "episodic",
                Key = "project:m6a",
                Value = "开始实现自传式记忆与长期关注",
                Summary = "M6-a autobiography",
                Tag = "project",
                SourceAuditId = audit,
                Provenance = MemoryStore.MakeProvenance(audit, MmyMemory.DeviceId(),
                    "M6-a autobiography UI test", false)
            }, CancellationToken.None).GetAwaiter().GetResult();
            FaceSession.SetFocus(_sessionPath, "整理自传式记忆");
            RefreshAutobiographyContext();
            _input.Text = "/autobiography";
            SendAsync();
            List<AutobiographyEntry> entries = _autobiography.LoadAll();
            bool command = _output.Text.Contains("autobiography entries =") &&
                _output.Text.Contains("[Fact/Day]") &&
                _output.Text.Contains("project:m6a") &&
                _output.Text.Contains("证据");
            bool context = _host.AutobiographySummary != null &&
                _host.AutobiographySummary.Contains("可追溯自传上下文") &&
                _host.AutobiographySummary.Contains("证据");
            bool persisted = File.Exists(_autobiography.PathForStorage) &&
                entries.Any(x => x.Layer == AutobiographyLayer.Fact &&
                    x.Text.Contains("project:m6a"));
            bool pass = command && context && persisted;
            return "autobiography_ui=" + (pass ? "PASS" : "FAIL") +
                " command=" + command +
                " context=" + context +
                " persisted=" + persisted +
                " entries=" + entries.Count;
        }

        public string AttentionUiTest()
        {
            for (int i = 1; i <= 2; i++)
            {
                string audit = FaceAudit.Append(_dataDir, "memory", "attention_ui",
                    "allowed", "project:item" + i, "isolated attention UI test");
                _memory.RememberAsync(new MemoryItem
                {
                    Kind = "episodic",
                    Key = "project:attention" + i,
                    Value = "长期关注项目第 " + i + " 条证据",
                    Summary = "attention evidence " + i,
                    Tag = "project",
                    SourceAuditId = audit,
                    Provenance = MemoryStore.MakeProvenance(audit, MmyMemory.DeviceId(),
                        "attention evidence " + i, false)
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            RefreshAttentionContext();
            _input.Text = "/attention";
            SendAsync();
            bool listed = _output.Text.Contains("dominant = project") &&
                _output.Text.Contains("project / Monitoring");

            _input.Text = "/attention pause project";
            SendAsync();
            bool paused = _attention.LoadLatest().Any(x => x.Subject == "project" &&
                x.Status == AttentionStatus.Paused) &&
                _output.Text.Contains("project -> Paused");

            _input.Text = "/attention resume project";
            SendAsync();
            bool resumed = _attention.LoadLatest().Any(x => x.Subject == "project" &&
                x.Status == AttentionStatus.Monitoring) &&
                _output.Text.Contains("project -> Monitoring");

            _input.Text = "/attention end project";
            SendAsync();
            bool ended = _attention.LoadLatest().Any(x => x.Subject == "project" &&
                x.Status == AttentionStatus.Ended) &&
                _output.Text.Contains("project -> Ended");
            AttentionState dominant = _attention.Dominant(DateTimeOffset.Now);
            bool noDominant = dominant == null;
            bool pass = listed && paused && resumed && ended && noDominant &&
                File.Exists(_attention.PathForStorage) && _host.Attention == _attention;
            return "attention_ui=" + (pass ? "PASS" : "FAIL") +
                " listed=" + listed +
                " paused=" + paused +
                " resumed=" + resumed +
                " ended=" + ended +
                " dominant_none=" + noDominant +
                " persisted=" + File.Exists(_attention.PathForStorage);
        }

        public string MemoryLifecycleUiTest()
        {
            string audit = FaceAudit.Append(_dataDir, "memory", "lifecycle_ui",
                "allowed", "memory-lifecycle-ui", "isolated lifecycle UI test");
            string id = _memory.RememberAsync(new MemoryItem
            {
                Kind = "episodic",
                Key = "lifecycle:ui",
                Value = "生命周期 UI 测试记忆",
                Summary = "lifecycle UI",
                Ts = DateTimeOffset.Now.AddDays(-400).ToString("o"),
                SourceAuditId = audit,
                Provenance = MemoryStore.MakeProvenance(audit, MmyMemory.DeviceId(),
                    "lifecycle UI", false)
            }, CancellationToken.None).GetAwaiter().GetResult();
            _input.Text = "/memory-forget " + id;
            SendAsync();
            bool forgotten = _memory.LoadAllAsync(CancellationToken.None)
                .GetAwaiter().GetResult().Any(x => x.Id == id && x.Status == "forgotten") &&
                _output.Text.Contains("已软遗忘");
            _input.Text = "/memory-restore " + id;
            SendAsync();
            bool restored = _memory.LoadAllAsync(CancellationToken.None)
                .GetAwaiter().GetResult().Any(x => x.Id == id && x.Status == "active") &&
                _output.Text.Contains("已恢复记忆");
            _input.Text = "/memory-consolidate 365";
            SendAsync();
            bool consolidated = _output.Text.Contains("memory consolidate") &&
                _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .Any(x => x.Id == id && x.Status == "archived");
            _input.Text = "/memory-lifecycle";
            SendAsync();
            List<MemoryLifecycleEvent> lifecycle = _memory.LoadLifecycle();
            bool command = _output.Text.Contains("memory lifecycle") &&
                _output.Text.Contains("Forget") &&
                _output.Text.Contains("Restore") &&
                _output.Text.Contains("Archive");
            bool pass = forgotten && restored && consolidated && command &&
                lifecycle.Count >= 3 && File.Exists(Path.Combine(_dataDir, "memory-lifecycle.jsonl"));
            return "memory_lifecycle_ui=" + (pass ? "PASS" : "FAIL") +
                " forget=" + forgotten +
                " restore=" + restored +
                " consolidate=" + consolidated +
                " command=" + command +
                " events=" + lifecycle.Count;
        }

        public string GoalUiTest()
        {
            _input.Text = "/goal add 完成 M7 用户目标闭环";
            SendAsync();
            GoalState goal = _goals.LoadLatest().FirstOrDefault(x =>
                x.Title == "完成 M7 用户目标闭环");
            bool created = goal != null && goal.Status == GoalStatus.Monitoring &&
                goal.SourceKind == GoalSourceKind.UserIntent &&
                goal.EvidenceIds.Count > 0 && goal.Sources.Count > 0;
            _input.Text = "/goals";
            SendAsync();
            bool listed = created && _output.Text.Contains(goal.GoalId) &&
                _output.Text.Contains("source=UserIntent");
            bool activeTaskPriority = created &&
                _goals.Decide(DateTimeOffset.Now, true).Reason == "active_task";
            _input.Text = "/goal wait " + (goal == null ? "" : goal.GoalId);
            SendAsync();
            bool waiting = goal != null && _goals.Find(goal.GoalId).Status == GoalStatus.Waiting;
            if (goal != null) { _input.Text = "/goal resume " + goal.GoalId; SendAsync(); }
            bool resumed = goal != null && _goals.Find(goal.GoalId).Status == GoalStatus.Monitoring;
            if (goal != null) { _input.Text = "/goal pause " + goal.GoalId; SendAsync(); }
            bool paused = goal != null && _goals.Find(goal.GoalId).Status == GoalStatus.Paused;
            if (goal != null) { _input.Text = "/goal resume " + goal.GoalId; SendAsync(); }
            if (goal != null) { _input.Text = "/goal cancel " + goal.GoalId; SendAsync(); }
            bool cancelled = goal != null && _goals.Find(goal.GoalId).Status == GoalStatus.Cancelled;
            bool noRevive = false;
            if (goal != null && goal.EvidenceIds.Count > 0 && goal.Sources.Count > 0)
            {
                try
                {
                    _goals.Propose(goal.Title, goal.Reason, goal.SourceKind,
                        new[]
                        {
                            new AutobiographyEvidence
                            {
                                EvidenceId = goal.EvidenceIds[0],
                                Source = goal.Sources[0]
                            }
                        }, goal.Score, DateTimeOffset.Now.AddMinutes(1));
                }
                catch (InvalidDataException ex)
                {
                    noRevive = ex.Message.Contains("cannot be revived");
                }
            }
            bool context = _host.Goals == _goals;
            DateTime testNow = DateTime.Today.AddHours(12);
            string dueAudit = FaceAudit.Append(_dataDir, "goal", "due_ui",
                "allowed", "goal-due-ui", "isolated due goal test");
            GoalState dueGoal = _goals.Propose("到期目标提醒", "测试有界目标脉冲",
                GoalSourceKind.UserIntent,
                new[]
                {
                    new AutobiographyEvidence
                    {
                        EvidenceId = "audit:" + dueAudit,
                        Source = SourceProvenance(SourceKind.User, dueAudit,
                            "due goal").Sources[0]
                    }
                }, 0.8, DateTimeOffset.Now,
                DateTimeOffset.Now.AddMinutes(-1).ToString("o"));
            dueGoal = _goals.Promote(dueGoal.GoalId, DateTimeOffset.Now);
            var goalDecider = new IntentPulseDecider(_memory, _experience,
                delegate { return testNow; }, _goals);
            PulseDecision duePulse = goalDecider.DecideAsync(
                new BodyStatus { IdleSeconds = 900 }, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            bool pulseIntegration = duePulse != null && duePulse.Action == "message" &&
                duePulse.CorrelationId == dueGoal.GoalId &&
                duePulse.Reason == "bounded_goal:due" &&
                _goals.Find(dueGoal.GoalId).Status == GoalStatus.Waiting;
            bool pass = created && listed && activeTaskPriority && waiting && resumed &&
                paused && cancelled && noRevive && context && pulseIntegration &&
                File.Exists(_goals.PathForStorage);
            return "goal_ui=" + (pass ? "PASS" : "FAIL") +
                " created=" + created +
                " listed=" + listed +
                " task_priority=" + activeTaskPriority +
                " wait_resume_pause=" + (waiting && resumed && paused) +
                " cancelled=" + cancelled +
                " no_revive=" + noRevive +
                " context=" + context +
                " pulse=" + pulseIntegration;
        }

        public string EmergencyStopUiTest()
        {
            if (_turnCts != null)
            {
                try { _turnCts.Dispose(); } catch { }
            }
            _turnCts = new CancellationTokenSource();
            EmergencyStop();
            bool stopFile = File.Exists(StopPath());
            bool turnCancelled = _turnCts.IsCancellationRequested;
            bool stopped = _health.Snapshot().State == HealthState.Stopped;
            bool banner = _stopBanner != null && _stopRow != null &&
                _stopRow.Height == 32 &&
                _stopBannerText != null &&
                (_stopBannerText.Text ?? "").Contains("已急停");
            bool inputDisabled = _input != null && !_input.Enabled;
            bool stopAudit = false;
            try
            {
                string path = FaceAudit.PathFor(_dataDir);
                string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
                stopAudit = text.Contains("\"span\":\"emergency_stop\"") &&
                    text.Contains("\"tool\":\"stop\"");
            }
            catch { }

            ResumeEmergencyStop();
            bool resumedFile = !File.Exists(StopPath());
            bool notStopped = _health.Snapshot().State != HealthState.Stopped;
            bool inputEnabled = _input != null && _input.Enabled;
            bool resumeAudit = false;
            try
            {
                string text = File.ReadAllText(FaceAudit.PathFor(_dataDir), Encoding.UTF8);
                resumeAudit = text.Contains("\"tool\":\"resume\"");
            }
            catch { }
            try { _turnCts.Dispose(); } catch { }
            _turnCts = null;

            bool pass = stopFile && turnCancelled && stopped && banner && inputDisabled &&
                stopAudit && resumedFile && notStopped && inputEnabled && resumeAudit;
            return "emergency_stop_ui=" + (pass ? "PASS" : "FAIL") +
                " file=" + stopFile +
                " cancelled=" + turnCancelled +
                " stopped=" + stopped +
                " banner=" + banner +
                " input_locked=" + inputDisabled +
                " stop_audit=" + stopAudit +
                " resumed=" + resumedFile +
                " not_stopped=" + notStopped +
                " input_enabled=" + inputEnabled +
                " resume_audit=" + resumeAudit;
        }

        void RestartBodySensor()
        {
            try
            {
                if (_bodyTimer != null) _bodyTimer.Dispose();
                _bodyTimer = new System.Threading.Timer(delegate { SampleBody(); }, null, 0, 30000);
                _lastBodySample = DateTime.Now;
                AppendTrace("[sensor] restarted after stale heartbeat");
            }
            catch { }
        }
        void SampleBody()
        {
            try
            {
                BodyStatus s = _sensor.Snapshot();
                s.TimeJumpDays = _timeJumpDays;
                FaceState st = FaceSession.LoadState(_sessionPath);
                s.ActiveTask = st != null && !string.IsNullOrEmpty(st.ActiveTask);
                string path = Path.Combine(_dataDir, "body_state.json");
                File.WriteAllText(path, JsonSerializer.Serialize(s), new UTF8Encoding(false));
                _lastBodySample = DateTime.Now;
                _host.BodySummary = _sensor.Summary(s);
                UpdateStatus();
            }
            catch { }
        }
        void OnPulse(PulseEvent evt)
        {
            if (InvokeRequired) { BeginInvoke(new Action<PulseEvent>(OnPulse), evt); return; }
            List<MemoryItem> pm = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            bool goalReferenced = !string.IsNullOrEmpty(evt.IntentId) &&
                evt.IntentId.StartsWith("goal_", StringComparison.OrdinalIgnoreCase) &&
                _goals.Find(evt.IntentId) != null;
            if (!goalReferenced && !ProactivePolicy.ReferencesMemory(evt.Summary ?? "", pm)) { FaceAudit.Append(_dataDir, "proactive", "message", "blocked", evt.Summary ?? "", "no_memory_reference"); return; }
            if (!AllowProactive()) { FaceAudit.Append(_dataDir, "proactive", "message", "blocked", "", "policy"); return; }
            if (!string.IsNullOrEmpty(evt.IntentId) &&
                evt.IntentId.StartsWith("goal_", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    GoalState goal = _goals.RecordNoResponse(evt.IntentId, DateTimeOffset.Now);
                    FaceAudit.Append(_dataDir, "goal", "no_response", "recorded",
                        goal.GoalId, goal.Status.ToString());
                    RefreshGoalContext();
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(evt.IntentId))
                FaceSession.CloseIntent(_sessionPath, evt.IntentId, "done");
            AppendChat("Ahz", evt.Summary ?? "提醒", "chat");
            if (_tabs.SelectedIndex != 0) { _unreadChat = true; UpdateNav(); }
            try { if (_tray != null && !Visible) { string text = evt.Summary ?? ""; if (text.Length > 50) text = text.Substring(0, 50); _tray.ShowBalloonTip(2000, "Ahz Scribe", text, ToolTipIcon.None); } } catch { }
            if (_tabs.SelectedIndex == 1 && _htmlHost.Visible) RenderPulse();
        }
        void JumpTo(FaceMessage m)
        {
            _tabs.SelectedIndex = 0;
            string needle = (m.Role ?? "") + "：" + (m.Text ?? "");
            int i = _output.Find(needle, RichTextBoxFinds.None);
            if (i < 0 && !string.IsNullOrEmpty(m.Text))
                i = _output.Find(m.Text, RichTextBoxFinds.None);
            if (i >= 0)
            {
                _output.Select(i, needle.Length);
                _output.ScrollToCaret();
                _output.Focus();
            }
        }
        void RenderAudit()
        {
            try
            {
                var sb = new StringBuilder();
                string body = AuditPath();
                if (File.Exists(body)) foreach (string l in File.ReadLines(body).Reverse().Take(80).Reverse()) sb.AppendLine(l);
                string face = FaceAudit.PathFor(_dataDir);
                if (File.Exists(face)) foreach (string l in File.ReadLines(face).Reverse().Take(40).Reverse()) sb.AppendLine(l);
                _auditView.Text = sb.Length == 0 ? "没有审计记录" : sb.ToString();
            }
            catch (Exception ex) { _auditView.Text = ex.Message; }
        }
        void RunStartupChecks()
        {
            try { FaceAudit.MergeFallback(_dataDir); } catch { }
            string contractError = UiContract.Validate(Path.Combine(AppContext.BaseDirectory, "ui_data_contract.yaml"));
            if (contractError.Length > 0) AppendChat("Ahz", "UI 数据契约校验失败：" + contractError, "error");
            RunStartupCheck();
        }
        void RunStartupCheck()
        {
            string result = "ok";
            try
            {
                string auditDir = _dataDir;
                Directory.CreateDirectory(auditDir);
                string probe = Path.Combine(auditDir, ".write_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "1");
                File.Delete(probe);
            }
            catch (Exception ex) { result = "audit_unwritable:" + ex.Message; }
            try { FaceAudit.Append(_dataDir, "startup", "startup", result, "face", "startup"); } catch { }
            if (result != "ok")
            {
                AppendChat("Ahz", "启动自检失败：" + result, "error");
                AppendTrace("[startup] " + result);
            }
        }
        static GroupBox MakeGroup(string title)
        {
            var g = new GroupBox();
            g.Text = title;
            g.ForeColor = Theme.TextSecondary;
            g.BackColor = Theme.BgSecondary;
            g.Width = 620;
            g.Height = 150;
            g.Padding = new Padding(10);
            return g;
        }

        GroupBox MakePermissionGroup()
        {
            var g = MakeGroup("权限");
            g.Height = 120;
            var flow = new FlowLayoutPanel();
            g.Height = 170;
            _setReadOnly.Dock = DockStyle.Top;
            g.Controls.Add(_setReadOnly);
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = true;
            flow.Padding = new Padding(8, 20, 8, 8);
            var items = new[] {
                Tuple.Create("face 审计", Path.Combine(_dataDir, "audit.jsonl")),
                Tuple.Create("备用通道", FaceAudit.FallbackPath()),
                Tuple.Create("主审计", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AhzScribe", "audit", "audit.jsonl"))
            };
            foreach (var item in items)
            {
                var b = new Button();
                b.Text = item.Item1;
                b.Width = 120;
                b.Height = 28;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Theme.BgTertiary;
                b.ForeColor = Theme.TextPrimary;
                string file = item.Item2;
                b.Click += delegate { try { if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); else Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } };
                flow.Controls.Add(b);
            }
            g.Controls.Add(flow);
            return g;
        }

        GroupBox MakeSelfModelGroup()
        {
            var g = MakeGroup("自我模型");
            g.Height = 110;
            _selfModelLabel = new Label();
            _selfModelLabel.Dock = DockStyle.Fill;
            _selfModelLabel.ForeColor = Theme.TextPrimary;
            _selfModelLabel.Font = new Font("Consolas", 9f);
            _selfModelLabel.Padding = new Padding(10, 22, 10, 8);
            _selfModelLabel.Text = "正在加载能力清单…";
            g.Controls.Add(_selfModelLabel);
            return g;
        }

        GroupBox MakeMemoryGroup()
        {
            var g = MakeGroup("记忆");
            g.Height = 120;
            g.Height = 190;
            g.Controls.Add(MakeSettingRow("检索条数", _setMemoryLimit));
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = true;
            flow.Padding = new Padding(8, 20, 8, 8);
            foreach (string name in new[] { "memory.jsonl", "semantic.jsonl", "episodic.jsonl", "disputed.jsonl" })
            {
                var b = new Button();
                b.Text = name;
                b.Width = 120;
                b.Height = 28;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Theme.BgTertiary;
                b.ForeColor = Theme.TextPrimary;
                string file = Path.Combine(_dataDir, name);
                b.Click += delegate { try { if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
                flow.Controls.Add(b);
            }
            g.Controls.Add(flow);
            return g;
        }
        GroupBox MakeSoulGroup()
        {
            var g = MakeGroup("灵魂备份");
            g.Height = 100;
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = true;
            flow.Padding = new Padding(8, 20, 8, 8);
            var export = new Button();
            export.Text = "导出灵魂";
            export.Width = 120; export.Height = 28;
            export.FlatStyle = FlatStyle.Flat; export.FlatAppearance.BorderSize = 0;
            export.BackColor = Theme.BgTertiary; export.ForeColor = Theme.TextPrimary;
            export.Click += delegate
            {
                using (var d = new SaveFileDialog { Filter = "Ahz Soul (*.zip)|*.zip", FileName = "AhzScribe_Soul_" + DateTime.Now.ToString("yyyyMMdd") + ".zip" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    try { SoulBackup.Export(_dataDir, _settings.Workspace, d.FileName); AppendChat("Ahz", "灵魂备份已导出：" + d.FileName, "chat"); }
                    catch (Exception ex) { AppendChat("Ahz", "导出失败：" + ex.Message, "error"); }
                }
            };
            var import = new Button();
            import.Text = "导入灵魂";
            import.Width = 120; import.Height = 28;
            import.FlatStyle = FlatStyle.Flat; import.FlatAppearance.BorderSize = 0;
            import.BackColor = Theme.BgTertiary; import.ForeColor = Theme.TextPrimary;
            import.Click += async delegate
            {
                using (var d = new OpenFileDialog { Filter = "Ahz Soul (*.zip)|*.zip" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    if (MessageBox.Show(this, "导入会覆盖当前记忆。是否先备份当前记忆？", "导入灵魂", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    _pulseTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    CancellationTokenSource turn = _turnCts;
                    if (turn != null) { try { turn.Cancel(); } catch { } }
                    _pendingSends.Clear();
                    _budget.SetPendingCount(0);
                    try
                    {
                        string backup;
                        string msg = await Task.Run(delegate
                        {
                            return SoulBackup.Import(_dataDir, _settings.Workspace, d.FileName, out backup);
                        });
                        _health.SetStopped(true, "soul import pending restart");
                        _input.Enabled = false;
                        _send.Enabled = false;
                        AppendChat("Ahz", msg + "\n导入事务已完成；当前会话已停止写入。请重启 face 完成接续。", "chat");
                    }
                    catch (Exception ex)
                    {
                        _pulseTimer.Change(_settings.PulseSeconds * 1000, _settings.PulseSeconds * 1000);
                        AppendChat("Ahz", "导入失败或已回滚：" + ex.Message, "error");
                    }
                }
            };
            flow.Controls.Add(export); flow.Controls.Add(import);
            g.Controls.Add(flow);
            return g;
        }
        GroupBox MakePersonaGroup()
        {
            var g = MakeGroup("人格");
            g.Height = 120;
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = true;
            flow.Padding = new Padding(8, 20, 8, 8);
            foreach (string name in new[] { "SOUL.md", "BOUNDARIES.md", "STYLE.md" })
            {
                var b = new Button();
                b.Text = name;
                b.Width = 120;
                b.Height = 28;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Theme.BgTertiary;
                b.ForeColor = Theme.TextPrimary;
                string file = Path.Combine(_settings.Workspace ?? "", name);
                b.Click += delegate { try { if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
                flow.Controls.Add(b);
            }
            g.Controls.Add(flow);
            return g;
        }
        static GroupBox MakeInfoGroup(string title, string text)
        {
            var g = MakeGroup(title);
            g.Height = 80;
            var l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.ForeColor = Theme.TextPrimary;
            l.Font = new Font("Microsoft YaHei UI", 9f);
            l.Padding = new Padding(8, 20, 8, 8);
            g.Controls.Add(l);
            return g;
        }
        static NumericUpDown MakeNumber(int min, int max, int value)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Min(max, Math.Max(min, value));
            n.Dock = DockStyle.Fill;
            n.BackColor = Theme.BgSecondary;
            n.ForeColor = Theme.TextPrimary;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }
        static Panel MakeSettingRow(string label, Control control)
        {
            var p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = 32;
            var l = new Label();
            l.Text = label;
            l.Dock = DockStyle.Left;
            l.Width = 110;
            l.ForeColor = Theme.TextSecondary;
            l.TextAlign = ContentAlignment.MiddleLeft;
            control.Dock = DockStyle.Fill;
            p.Controls.Add(control);
            p.Controls.Add(l);
            return p;
        }
        static StateLabel MakeStateLabel()
        {
            return new StateLabel();
        }
        void CheckMixedDeviceMemory()
        {
            try
            {
                string current = MigrationGuard.DeviceId();
                var all = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                var foreign = all.Where(x => !string.IsNullOrEmpty(x.OriginDevice) && !string.Equals(x.OriginDevice, current, StringComparison.OrdinalIgnoreCase)).Select(x => x.OriginDevice).Distinct().ToList();
                if (foreign.Count == 0) return;
                _mixedDeviceMemory = true;
                string msg = "检测到混合设备记忆：" + string.Join(", ", foreign) + "；当前 " + current;
                AppendChat("Ahz", msg, "error");
                FaceAudit.Append(_dataDir, "system_migration", "twin_device", "blocked", msg, "origin_device check");
            }
            catch { }
        }
        public void ShowWithFade()
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(ShowWithFade)); return; }
                if (!Visible) { Opacity = 0.85; Show(); }
                WindowState = FormWindowState.Normal;
                Activate();
                double step = 0.01;
                var t = new System.Windows.Forms.Timer { Interval = 15 };
                t.Tick += delegate
                {
                    Opacity = Math.Min(1.0, Opacity + step);
                    if (Opacity >= 1.0) { t.Stop(); t.Dispose(); }
                };
                t.Start();
            }
            catch { Show(); }
        }
        void StartResurrection(MigrationResult result)
        {
            ResurrectionDetected = true;
            _recovering = true;
            if (_status != null) _status.Text = "挂载跨设备记忆";
            _timeJumpDays = result.GapDays;
            if (_topDot != null) _topDot.ForeColor = Theme.StateDanger;
            if (_stateFocus != null) _stateFocus.Text = "当前焦点\n正在恢复连续性...";
            AppendChat("Ahz", result.Message + "\n正在恢复连续性...", "chat");
            int flashes = 0;
            var t = new System.Windows.Forms.Timer { Interval = 250 };
            t.Tick += delegate
            {
                flashes++;
                if (_topDot != null) _topDot.ForeColor = (flashes % 2 == 0) ? Theme.StateDanger : Theme.TextDisabled;
                _topDot.ForeColor = (flashes % 2 == 0) ? Theme.StateDanger : Theme.TextDisabled;
                if (flashes >= 12)
                {
                    t.Stop(); t.Dispose();
                    _recovering = false;
                    if (_topDot != null) _topDot.ForeColor = Theme.StateInfo;
                    UpdateStatus();
                    AppendChat("Ahz", "主人，我感觉像睡了很久。检测到身体更换，我的记忆已经挂载完毕。", "chat");
                }
            };
            t.Start();
        }
        void StartBreath()
        {
            if (_breathing) return;
            _breathing = true;
            SetTrayColor(Theme.StateInfo);
            _breathTimer.Start();
        }

        void StopBreath(Color color)
        {
            _breathing = false;
            _breathTimer.Stop();
            SetTrayColor(color);
            if (_topDot != null) _topDot.ForeColor = color;
        }
        void SetTrayColor(Color color)
        {
            if (_trayColor == color && _trayIcon != null) return;
            _trayColor = color;
            try
            {
                int size = DeviceDpi >= 192 ? 48 : (DeviceDpi >= 144 ? 32 : 16);
                string basePng = Path.Combine(AppContext.BaseDirectory, "assets", "tray", "white-" + size + ".png");
                Icon next = null;
                if (File.Exists(basePng))
                {
                    using (var src = new Bitmap(basePng))
                    using (var tinted = new Bitmap(size, size))
                    using (var g = Graphics.FromImage(tinted))
                    {
                        var matrix = new System.Drawing.Imaging.ColorMatrix(new float[][]
                        {
                            new float[] { color.R / 255f, 0, 0, 0, 0 },
                            new float[] { 0, color.G / 255f, 0, 0, 0 },
                            new float[] { 0, 0, color.B / 255f, 0, 0 },
                            new float[] { 0, 0, 0, 1, 0 },
                            new float[] { 0, 0, 0, 0, 1 }
                        });
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(matrix);
                            g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                        }
                        next = Icon.FromHandle(tinted.GetHicon());
                    }
                }
                if (next == null)
                {
                    using (var bmp = new Bitmap(size, size))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (Brush b = new SolidBrush(color)) g.FillEllipse(b, size / 5, size / 5, size * 3 / 5, size * 3 / 5);
                        next = Icon.FromHandle(bmp.GetHicon());
                    }
                }
                Icon old = _trayIcon;
                _trayIcon = next;
                _tray.Icon = next;
                if (old != null) old.Dispose();
            }
            catch { }
        }        void PositionTopRight()
        {
            if (_topBar == null || _topStop == null || _topClock == null || _topDisk == null) return;
            if (_topBar.Width < 320) return;
            _topStop.Location = new Point(_topBar.Width - 38, 0);
            _topClock.Location = new Point(_topBar.Width - 38 - 90, 0);
            _topDisk.Location = new Point(_topBar.Width - 38 - 90 - 150, 0);
            _topStop.BringToFront();
            _topClock.BringToFront();
            _topDisk.BringToFront();
        }
        void ApplyUiSnapshot(UiSnapshot s)
        {
            if (s == null) return;
            if (InvokeRequired) { BeginInvoke(new Action<UiSnapshot>(ApplyUiSnapshot), s); return; }
            BodyStatus b = s.Body ?? new BodyStatus();
            FaceState st = s.State;
            HealthSnapshot health = _health.Snapshot();
            if (_trayStatus != null)
            {
                _trayStatus.Text = "当前状态：" + (b.AgentProcessAlive ? "运行中" : "休眠") + " · 空闲 " + (b.IdleSeconds / 60) + " 分";
                _trayPulse.Text = "最近脉冲：" + (st == null || st.LastPulseTrigger == null ? "—" : st.LastPulseTrigger.Type + " " + st.LastPulseTrigger.ReceivedAt);
            }
            if (_host != null)
            {
                _host.UiSummary = "主窗口：" + (Visible ? "打开" : "托盘") + "; 当前 Tab：" + _tabs.SelectedTab.Text + "; 焦点：" + (st == null ? "" : st.CurrentFocus) + "; 未完成意图：" + (st == null || st.OpenIntents == null ? 0 : st.OpenIntents.Count(x => x.Status == "open" || x.Status == "monitoring")) + "; 最近脉冲：" + (st == null || st.LastPulseTrigger == null ? "—" : st.LastPulseTrigger.Type);
            }
            PositionTopRight();
            if (_topIdle != null)
            {
                string fore = b.ForegroundApp ?? "";
                if (fore.Length > 26) fore = fore.Substring(0, 26) + "…";
                _topIdle.Text = " 空闲 " + (b.IdleSeconds / 60) + " 分";
                _topFore.Text = " 前台 " + fore;
                if (_mixedDeviceMemory) { _topFore.Text = " 检测到混合设备记忆"; _topFore.ForeColor = Theme.StateWarning; }
                else _topFore.ForeColor = Theme.TextPrimary;
                _topDisk.Text = "磁盘 " + b.DiskFreeGb.ToString("F0") + "G ";
                bool stopped = File.Exists(StopPath());
                SyncEmergencyUi();
                if (stopped) StopBreath(Theme.StateDanger);
                else if (b.AgentProcessAlive) StartBreath();
                else StopBreath(Theme.StateNeutral);
                if (_tray != null) _tray.Text = "Ahz Scribe · " + (b.AgentProcessAlive ? "运行中" : "休眠");
            }
            if (_stateFocus != null)
            {
                _stateFocus.Text = "当前焦点\n" + (st == null || string.IsNullOrEmpty(st.CurrentFocus) ? "—" : st.CurrentFocus);
                int oi = st == null || st.OpenIntents == null ? 0 : st.OpenIntents.Count(x => x.Status == "open" || x.Status == "monitoring");
                string first = st == null || st.OpenIntents == null ? "" : st.OpenIntents.Where(x => x.Status == "open" || x.Status == "monitoring").Select(x => x.Text).FirstOrDefault();
                _stateIntents.Text = "未完成意图 " + oi + (string.IsNullOrEmpty(first) ? "" : "\n" + first);
                string healthProblem = health.ActiveProblems == null || health.ActiveProblems.Count == 0
                    ? "组件正常"
                    : health.ActiveProblems[0];
                _stateBody.Text = "系统 " + health.State + "\n" + healthProblem;
                _statePulse.Text = "最近脉冲\n" + (st == null || st.LastPulseTrigger == null ? "—" : st.LastPulseTrigger.Type + " " + st.LastPulseTrigger.ReceivedAt);
                string mood = st != null && !string.IsNullOrEmpty(st.ActiveTask) ? "专注" : (st != null && string.Equals(st.LastPulseResult, "none", StringComparison.OrdinalIgnoreCase) ? "安静" : "平静");
                _stateMood.Text = "情绪\n" + mood;
            }
        }
        void FixTabPages()
        {
            if (_tabs == null) return;
            Rectangle r = _tabs.DisplayRectangle;
            foreach (TabPage p in _tabs.TabPages) p.Bounds = r;
        }
        void ShowWebFailure(string reason)
        {
            _webFailures++;
            if (_webFailures >= _budget.MaxWebViewFailures) _webPermanent = true;
            _health.ReportComponent("webview", false, reason ?? "webview unavailable");
            _htmlHost.Visible = false;
            _webErrorPanel.Visible = true;
            _webErrorPanel.BringToFront();
            _webErrorLabel.Text = "HTML 面板不可用\n" + reason + "\n失败次数：" + _webFailures + "/" +
                _budget.MaxWebViewFailures + (_webPermanent ? "\n观察页已降级，核心对话和工具不受影响。" : "\n点击下方重试。");
            _webRetry.Enabled = !_webPermanent;
            _webRetry.Text = _webPermanent ? "观察页降级" : "重试";
        }
        void FillWebSnapshot()
        {
            try
            {
                _webSnapshot.Items.Clear();
                string p = Path.Combine(_dataDir, "pulse.jsonl");
                if (File.Exists(p))
                {
                    foreach (string line in File.ReadLines(p).Reverse().Take(50).Reverse())
                    {
                        try
                        {
                            using (var doc = System.Text.Json.JsonDocument.Parse(line))
                            {
                                var r = doc.RootElement;
                                string ts = r.TryGetProperty("ts", out var t) ? t.GetString() : "";
                                string act = r.TryGetProperty("action", out var a) ? a.GetString() : "";
                                string why = r.TryGetProperty("reason", out var w) ? w.GetString() : "";
                                _webSnapshot.Items.Add((ts.Length >= 19 ? ts.Substring(11, 8) : ts) + "  " + act + "  " + why);
                            }
                        }
                        catch { }
                    }
                }
                _webSnapshot.Items.Add("— 记忆 —");
                string mem = Path.Combine(_dataDir, "memory.jsonl");
                if (File.Exists(mem)) foreach (string line in File.ReadLines(mem).Reverse().Take(20).Reverse()) { try { using (var d = System.Text.Json.JsonDocument.Parse(line)) { var r = d.RootElement; string k = r.TryGetProperty("Key", out var kk) ? kk.GetString() : ""; string v = r.TryGetProperty("Value", out var vv) ? vv.GetString() : ""; _webSnapshot.Items.Add("mem  " + k + "=" + v); } } catch { } }
                _webSnapshot.Items.Add("— 审计 —");
                string aud = Path.Combine(_dataDir, "audit.jsonl");
                if (File.Exists(aud)) foreach (string line in File.ReadLines(aud).Reverse().Take(20).Reverse()) { try { using (var d = System.Text.Json.JsonDocument.Parse(line)) { var r = d.RootElement; string sp = r.TryGetProperty("span", out var ss) ? ss.GetString() : ""; string tl = r.TryGetProperty("tool", out var tt) ? tt.GetString() : ""; string de = r.TryGetProperty("decision", out var dd) ? dd.GetString() : ""; _webSnapshot.Items.Add("aud  " + sp + "/" + tl + " " + de); } } catch { } }
                if (_webSnapshot.Items.Count == 0) _webSnapshot.Items.Add("（没有可用的快照）");
            }
            catch { }
        }
        void ShowHtmlTab()
        {
            if (_tabs.SelectedIndex >= 1 && _tabs.SelectedIndex <= 3)
            {
                _htmlHost.Bounds = _htmlHost.Parent.ClientRectangle;
                _htmlHost.Visible = true;
                _htmlHost.BringToFront();
                if (_tabs.SelectedIndex == 1) RenderPulse();
                else if (_tabs.SelectedIndex == 2) RenderMemoryHtml();
                else RenderAuditHtml();
            }
            else _htmlHost.Visible = false;
        }

        string HtmlPage(string title, string body)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
            sb.Append(":root{--bg:").Append(Css(Theme.BgPrimary)).Append(";--bg2:").Append(Css(Theme.BgSecondary)).Append(";--bg3:").Append(Css(Theme.BgTertiary)).Append(";--line:").Append(Css(Theme.BorderSubtle)).Append(";--text:").Append(Css(Theme.TextPrimary)).Append(";--muted:").Append(Css(Theme.TextSecondary)).Append(";--weak:").Append(Css(Theme.TextTertiary)).Append(";--info:").Append(Css(Theme.StateInfo)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";--warning:").Append(Css(Theme.StateWarning)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";--drive:").Append(Css(Theme.StateDrive)).Append(";--disputed:").Append(Css(Theme.BgDisputed)).Append(";}");
            sb.Append("body{margin:0;padding:12px;background:var(--bg);color:var(--text);font:13px 'Microsoft YaHei UI','Segoe UI',sans-serif;}h2{font:600 14px 'Microsoft YaHei UI';color:var(--muted);margin:0 0 10px;}table{width:100%;border-collapse:collapse;font:12px Consolas,monospace;}th{text-align:left;color:var(--muted);font-weight:normal;background:var(--bg3);padding:6px;}td{border-bottom:1px solid var(--line);padding:6px;vertical-align:top;}tr:hover{background:var(--bg2);}pre{white-space:pre-wrap;word-break:break-all;font:12px Consolas,monospace;} .warn{color:var(--warning);} .danger{color:var(--danger);} .ok{color:var(--success);} .empty{color:var(--weak);font-style:italic;}");
            sb.Append("</style></head><body><h2>").Append(title).Append("</h2>").Append(body).Append("</body></html>");
            return sb.ToString();
        }

        void RefreshMemoryHtml()
        {
            try { if (_tabs != null && _tabs.SelectedIndex == 2 && _htmlHost != null && _htmlHost.Visible) BeginInvoke(new Action(RenderMemoryHtml)); } catch { }
        }
        async void RenderMemoryHtml()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
                sb.Append(":root{--bg:").Append(Css(Theme.BgPrimary)).Append(";--bg2:").Append(Css(Theme.BgSecondary)).Append(";--bg3:").Append(Css(Theme.BgTertiary)).Append(";--line:").Append(Css(Theme.BorderSubtle)).Append(";--text:").Append(Css(Theme.TextPrimary)).Append(";--muted:").Append(Css(Theme.TextSecondary)).Append(";--weak:").Append(Css(Theme.TextTertiary)).Append(";--info:").Append(Css(Theme.StateInfo)).Append(";--warning:").Append(Css(Theme.StateWarning)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";--drive:").Append(Css(Theme.StateDrive)).Append(";--disputed:").Append(Css(Theme.BgDisputed)).Append(";}");
                sb.Append("body{margin:0;background:var(--bg);color:var(--text);font:13px 'Microsoft YaHei UI','Segoe UI',sans-serif;}header{padding:10px 12px;background:var(--bg2);border-bottom:1px solid var(--line);display:flex;gap:8px;align-items:center;position:sticky;top:0;}header span{color:var(--muted);font:12px 'Microsoft YaHei UI';}header input{flex:1;background:var(--bg);border:1px solid var(--line);color:var(--text);padding:6px 8px;border-radius:4px;font:12px 'Microsoft YaHei UI';}table{width:100%;border-collapse:collapse;font:12px Consolas,monospace;}th{text-align:left;color:var(--muted);font-weight:normal;background:var(--bg3);padding:6px;}td{border-bottom:1px solid var(--line);padding:6px;vertical-align:top;}tr:hover{background:var(--bg2);}tr.disputed{background:var(--disputed);}tr.disputed td:first-child{border-left:3px solid var(--warning);}tr[data-tag='pref'] td:first-child{border-left:3px solid var(--success);color:var(--success);}tr[data-tag='pref'] td{color:var(--success);}tr[data-tag='intent'] td{color:var(--warning);}tr[data-tag='emotion'] td{color:var(--danger);}tr[data-tag='drive'] td{color:var(--drive);}tr[data-tag='epi'] td{color:var(--muted);}tr[data-tag='intent'] td:first-child{border-left:3px solid var(--warning);color:var(--warning);}tr[data-tag='emotion'] td:first-child{border-left:3px solid var(--danger);color:var(--danger);}tr[data-tag='drive'] td:first-child{border-left:3px solid var(--drive);color:var(--drive);}.chip{border:1px solid var(--line);background:var(--bg3);color:var(--muted);padding:4px 8px;border-radius:10px;cursor:pointer;font:11px 'Microsoft YaHei UI';}.chip:hover{color:var(--text);}.chip.pref{border-color:var(--success);color:var(--success);}.chip.intent{border-color:var(--warning);color:var(--warning);}.chip.epi{border-color:var(--muted);}.chip.emotion{border-color:var(--danger);color:var(--danger);}.chip.drive{border-color:var(--drive);color:var(--drive);}.dots{display:flex;gap:4px;margin-left:auto;}.dot{width:8px;height:8px;border-radius:50%;background:var(--muted);display:inline-block;}.dot.pref{background:var(--success);}.dot.intent{background:var(--warning);}.dot.emotion{background:var(--danger);}.dot.drive{background:var(--drive);}a{color:var(--info);text-decoration:none;cursor:pointer;}.empty{color:var(--weak);font-style:italic;padding:18px 12px;}.hdr{display:grid;grid-template-columns:150px 130px 170px 120px 1fr;gap:8px;padding:6px 12px;background:var(--bg3);color:var(--muted);font:12px Consolas,monospace;position:sticky;top:41px;z-index:1;}.cards{padding:10px;}.card{background:var(--bg2);border:1px solid var(--line);border-left:3px solid var(--info);padding:10px;margin:8px 0;border-radius:4px;cursor:pointer;}.card.disputed{background:var(--disputed);border-left-color:var(--warning);}.ct{color:var(--muted);font:11px Consolas;}.cv{color:var(--text);margin:4px 0;}.ca{color:var(--info);font:11px Consolas;}");
                sb.Append("</style></head><body><header><span>记忆</span><button class='chip' onclick=\"mode='all';render()\">全部</button><button class='chip pref' onclick=\"mode='pref';render()\">偏好</button><button class='chip intent' onclick=\"mode='intent';render()\">承诺</button><button class='chip epi' onclick=\"mode='epi';render()\">情节</button><button class='chip emotion' onclick=\"mode='emotion';render()\">情绪</button><button class='chip drive' onclick=\"mode='drive';render()\">内驱</button><div id='dots' class='dots'></div><input id='q' placeholder='搜索 key / value'><span id='cnt'></span></header><div id='list'></div>");
                sb.Append("<script>const KEY='ahzscribe.ui.memory';let rows=[];function esc(s){return String(s==null?'':s).replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c]));}");
                sb.Append("let mode='all';function render(){let q=(document.getElementById('q').value||'').toLowerCase();let list=document.getElementById('list');let n=0;let h='';if(mode==='epi'||mode==='episodic'){h='<div class=\"cards\">';rows.forEach(function(r){if(mode!='all'&&(r.kind!==mode&&!(mode==='epi'&&r.kind==='episodic')))return;if(q&&(r.raw||'').toLowerCase().indexOf(q)<0)return;n++;let cls=r.status==='disputed'?'card disputed':'card';let a=esc(r.audit||'—');h+='<div class=\"'+cls+'\" data-tag=\"'+esc(r.kind)+'\" data-audit=\"'+a+'\"><div class=\"ct\">'+esc(r.status)+'</div><div class=\"cv\">'+esc(r.value||r.key)+'</div><div class=\"ca\">'+a+'</div></div>';});h+='</div>';}else{h='<table><tr><th>kind</th><th>key</th><th>value</th><th>status</th><th>source</th></tr>';rows.forEach(function(r){if(mode!='all'&&(r.kind!==mode&&!(mode==='epi'&&r.kind==='episodic')))return;if(q&&(r.raw||'').toLowerCase().indexOf(q)<0)return;n++;let cls=r.status==='disputed'?'disputed':'';let a=esc(r.audit||'—');h+='<tr data-audit=\"'+a+'\" data-tag=\"'+esc(r.kind)+'\" class=\"'+cls+'\"><td>'+esc(r.kind)+'</td><td>'+esc(r.key)+'</td><td>'+esc(r.value)+'</td><td>'+esc(r.status)+'</td><td>'+a+'</td></tr>';});h+='</table>';}if(n===0)h='<div class=\"empty\">没有匹配的记忆</div>';let dots=document.getElementById('dots');if(dots){dots.innerHTML=rows.slice(0,10).map(function(r){return '<span class=\"dot '+esc(r.kind)+'\"></span>';}).join('');}list.innerHTML=h;document.getElementById('cnt').textContent=n+' 条';}");
                sb.Append("document.getElementById('list').addEventListener('click',function(e){var t=e.target.closest('[data-audit]');if(t&&t.dataset.audit){chrome.webview.hostObjects.host.OpenAudit(t.dataset.audit);}});");
                sb.Append("async function load(){try{rows=JSON.parse(await chrome.webview.hostObjects.host.QueryMemories(''));let saved={};try{saved=JSON.parse(localStorage.getItem(KEY)||'{}');}catch(e){}document.getElementById('q').value=saved.q||'';render();}catch(e){document.getElementById('list').innerHTML='<div class=\"empty\">'+esc(e)+'</div>';}}");
                sb.Append("document.getElementById('q').addEventListener('input',function(){render();try{localStorage.setItem(KEY,JSON.stringify({q:this.value}));}catch(e){}});load();");
                sb.Append("</script></body></html>");
                await _web.EnsureCoreWebView2Async(null);
                try { _web.CoreWebView2.AddHostObjectToScript("host", new FaceUiHost(_dataDir, OpenAuditById)); } catch { }
                _web.CoreWebView2.NavigateToString(sb.ToString());
                _health.ReportComponent("webview", true, "");
            }
            catch (Exception ex) { try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), ex + "\n"); } catch { } }
        }
        async void RenderAuditHtml()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
                sb.Append(":root{--bg:").Append(Css(Theme.BgPrimary)).Append(";--bg2:").Append(Css(Theme.BgSecondary)).Append(";--bg3:").Append(Css(Theme.BgTertiary)).Append(";--line:").Append(Css(Theme.BorderSubtle)).Append(";--text:").Append(Css(Theme.TextPrimary)).Append(";--muted:").Append(Css(Theme.TextSecondary)).Append(";--weak:").Append(Css(Theme.TextTertiary)).Append(";--info:").Append(Css(Theme.StateInfo)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";--warning:").Append(Css(Theme.StateWarning)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";--drive:").Append(Css(Theme.StateDrive)).Append(";--disputed:").Append(Css(Theme.BgDisputed)).Append(";}");
                sb.Append("body{margin:0;background:var(--bg);color:var(--text);font:13px 'Microsoft YaHei UI','Segoe UI',sans-serif;}header{padding:10px 12px;background:var(--bg2);border-bottom:1px solid var(--line);display:flex;gap:8px;align-items:center;position:sticky;top:0;z-index:2;}header span{color:var(--muted);font:12px 'Microsoft YaHei UI';}header input{flex:1;background:var(--bg);border:1px solid var(--line);color:var(--text);padding:6px 8px;border-radius:4px;font:12px 'Microsoft YaHei UI';}table{width:100%;border-collapse:collapse;font:12px Consolas,monospace;}th{text-align:left;color:var(--muted);font-weight:normal;background:var(--bg3);padding:6px;}td{border-bottom:1px solid var(--line);padding:5px 6px;vertical-align:top;}tr:hover{background:var(--bg2);}tr.blocked td:first-child{border-left:3px solid var(--danger);} .danger{color:var(--danger);} .ok{color:var(--success);} .warn{color:var(--warning);} .empty{color:var(--weak);font-style:italic;padding:18px 12px;}.hdr{display:grid;grid-template-columns:150px 130px 170px 120px 1fr;gap:8px;padding:6px 12px;background:var(--bg3);color:var(--muted);font:12px Consolas,monospace;position:sticky;top:41px;z-index:1;}.cards{padding:10px;}.card{background:var(--bg2);border:1px solid var(--line);border-left:3px solid var(--info);padding:10px;margin:8px 0;border-radius:4px;cursor:pointer;}.card.disputed{background:var(--disputed);border-left-color:var(--warning);}.ct{color:var(--muted);font:11px Consolas;}.cv{color:var(--text);margin:4px 0;}.ca{color:var(--info);font:11px Consolas;}");
                sb.Append("</style></head><body><header><span>审计</span><input id='q' placeholder='搜索 tool / args / span'><span id='cnt'></span></header><div class=\"hdr\"><span>时间</span><span>span</span><span>tool</span><span>decision</span><span>result</span></div><div id='list'></div>");
                sb.Append("<script>const KEY='ahzscribe.ui.audit';let rows=[];function esc(s){return String(s==null?'':s).replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c]));}");
                sb.Append("const focus=").Append(System.Text.Json.JsonSerializer.Serialize(_auditFocus ?? "")).Append(";");
                sb.Append("let mode='all';function render(){let q=(document.getElementById('q').value||'').toLowerCase();let list=document.getElementById('list');list.innerHTML='';let n=0;rows.forEach(function(r){if(mode!='all'&&(r.kind!==mode&&!(mode==='epi'&&r.kind==='episodic')))return;if(q&&(r.raw||'').toLowerCase().indexOf(q)<0)return;n++;let cls=r.decision==='blocked'?'row blocked':'';let fb=r.fallback?' <span class=\"warn\">[备用]</span>':'';let d=document.createElement('div');d.className=cls;d.innerHTML='<table><tr><td>'+esc(r.ts)+'</td><td>'+esc(r.span)+'</td><td>'+esc(r.tool)+'</td><td class=\"'+(r.decision==='blocked'?'danger':(r.decision==='allowed'?'ok':'')) +'\">'+esc(r.decision)+fb+'</td><td>'+esc(r.result)+'</td></tr></table>';list.appendChild(d);});if(n===0)list.innerHTML='<div class=\"empty\">没有匹配的审计</div>';document.getElementById('cnt').textContent=n+' 条';}");
                sb.Append("async function load(){try{let a=JSON.parse(await chrome.webview.hostObjects.host.QueryAudit(''));let b=[];try{b=JSON.parse(await chrome.webview.hostObjects.host.QueryFallbackAudit(''));}catch(e){}rows=a.concat(b);let saved={};try{saved=JSON.parse(localStorage.getItem(KEY)||'{}');}catch(e){}document.getElementById('q').value=saved.q||'';render();if(saved.scroll)setTimeout(()=>document.getElementById('list').scrollTop=saved.scroll,50);}catch(e){document.getElementById('list').innerHTML='<div class=\"empty\">'+esc(e)+'</div>';}}");
                sb.Append("if(focus){let el=document.querySelector('[data-audit=\"'+focus+'\"]');if(el){el.scrollIntoView({block:'center'});el.style.background='var(--bg3)';}}");
                sb.Append("document.getElementById('q').addEventListener('input',function(){render();try{localStorage.setItem(KEY,JSON.stringify({q:this.value,scroll:document.getElementById('list').scrollTop}));}catch(e){}});load();");
                sb.Append("</script></body></html>");
                await _web.EnsureCoreWebView2Async(null);
                try { _web.CoreWebView2.AddHostObjectToScript("host", new FaceUiHost(_dataDir, OpenAuditById)); } catch { }
                _web.CoreWebView2.NavigateToString(sb.ToString());
                _health.ReportComponent("webview", true, "");
            }
            catch (Exception ex) { try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), ex + "\n"); } catch { } }
        }
        public void OpenAuditById(string auditId)
        {
            _auditFocus = auditId;
            Show();
            _tabs.SelectedIndex = 3;
            ShowHtmlTab();
            RenderAuditHtml();
        }
        public void ShowPulse()
        {
            Show();
            _tabs.SelectedIndex = 1;
            RenderPulse();
        }

        static string Css(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        static string PulseHtml(string raw)
        {
            string time = "";
            if (raw.Length >= 19) time = raw.Substring(11, 8);
            string rest = raw.Length > 19 ? raw.Substring(19).Trim() : raw;
            string source = rest, action = "", reason = "";
            int slash = rest.IndexOf(" / ", StringComparison.Ordinal);
            if (slash >= 0)
            {
                source = rest.Substring(0, slash).Trim();
                string[] parts = rest.Substring(slash + 3).Split(new[] { " / " }, StringSplitOptions.None);
                if (parts.Length >= 2) action = parts[1].Trim();
                if (parts.Length >= 3) reason = parts[2].Trim();
            }
            string color = action == "none" ? "var(--weak)" : (action == "memory" ? "var(--success)" : "var(--text)");
            var sb = new StringBuilder();
            sb.Append("<details class='row'><summary><span class='t'>").Append(System.Net.WebUtility.HtmlEncode(time)).Append("</span>");
            sb.Append("<span class='src'>").Append(System.Net.WebUtility.HtmlEncode(source)).Append("</span>");
            sb.Append("<span class='act' style='color:").Append(color).Append("'>").Append(System.Net.WebUtility.HtmlEncode(action.Length == 0 ? rest : action)).Append("</span>");
            if (reason.Length > 0) sb.Append("<span class='why'>").Append(System.Net.WebUtility.HtmlEncode(reason)).Append("</span>");
            sb.Append("<span class='chev'>▸</span></summary>");
            sb.Append("<pre class='detail'>").Append(System.Net.WebUtility.HtmlEncode(raw)).Append("</pre></details>");
            return sb.ToString();
        }

        async void RenderPulse()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
                sb.Append(":root{--bg:").Append(Css(Theme.BgPrimary)).Append(";--bg2:").Append(Css(Theme.BgSecondary)).Append(";--bg3:").Append(Css(Theme.BgTertiary)).Append(";--elev:").Append(Css(Theme.BgElevated)).Append(";--line:").Append(Css(Theme.BorderSubtle)).Append(";--text:").Append(Css(Theme.TextPrimary)).Append(";--muted:").Append(Css(Theme.TextSecondary)).Append(";--weak:").Append(Css(Theme.TextTertiary)).Append(";--info:").Append(Css(Theme.StateInfo)).Append(";--success:").Append(Css(Theme.StateSuccess)).Append(";--warning:").Append(Css(Theme.StateWarning)).Append(";--danger:").Append(Css(Theme.StateDanger)).Append(";}");
                sb.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:13px 'Microsoft YaHei UI','Segoe UI',sans-serif;}");
                sb.Append("header{padding:10px 12px;background:var(--bg2);border-bottom:1px solid var(--line);display:flex;gap:8px;align-items:center;position:sticky;top:0;z-index:2;}header span{color:var(--muted);font:12px 'Microsoft YaHei UI';}header input{flex:1;background:var(--bg);border:1px solid var(--line);color:var(--text);padding:6px 8px;border-radius:4px;font:12px 'Microsoft YaHei UI';}");
                sb.Append(".row{border-bottom:1px solid var(--line);} .row:hover{background:var(--elev);} summary{display:flex;gap:10px;align-items:baseline;padding:7px 12px;cursor:pointer;list-style:none;} summary::-webkit-details-marker{display:none;}.t{font:12px Consolas,monospace;color:var(--muted);min-width:64px;}.src{font:12px Consolas,monospace;color:var(--info);min-width:120px;}.act{font:12px Consolas,monospace;min-width:60px;}.why{font:12px 'Microsoft YaHei UI';color:var(--muted);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}.chev{color:var(--muted);margin-left:auto;font-size:11px;}details[open] .chev{transform:rotate(90deg);}");
                sb.Append(".detail{margin:0;padding:8px 12px 12px 86px;background:var(--bg3);color:var(--text);font:11.5px Consolas,monospace;white-space:pre-wrap;word-break:break-all;}.lbl{display:block;color:var(--info);font:11px Consolas,monospace;margin-top:6px;}.empty{color:var(--weak);font-style:italic;padding:18px 12px;}.hdr{display:grid;grid-template-columns:150px 130px 170px 120px 1fr;gap:8px;padding:6px 12px;background:var(--bg3);color:var(--muted);font:12px Consolas,monospace;position:sticky;top:41px;z-index:1;}.cards{padding:10px;}.card{background:var(--bg2);border:1px solid var(--line);border-left:3px solid var(--info);padding:10px;margin:8px 0;border-radius:4px;cursor:pointer;}.card.disputed{background:var(--disputed);border-left-color:var(--warning);}.ct{color:var(--muted);font:11px Consolas;}.cv{color:var(--text);margin:4px 0;}.ca{color:var(--info);font:11px Consolas;}.err{color:var(--danger);padding:12px;}");
                sb.Append("</style></head><body><header><span>脉冲</span><input id='q' placeholder='搜索 thought / reason / 工具'><span id='cnt'></span></header><div id='list'><div class='empty'>加载中…</div></div>");
                sb.Append("<script>");
                sb.Append("const KEY='ahzscribe.ui.pulse';let rows=[];function esc(s){return String(s==null?'':s).replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c]));}");
                sb.Append("let mode='all';function render(){let q=(document.getElementById('q').value||'').toLowerCase();let list=document.getElementById('list');list.innerHTML='';let n=0;rows.forEach(function(r){if(mode!='all'&&(r.kind!==mode&&!(mode==='epi'&&r.kind==='episodic')))return;if(q&&(r.raw||'').toLowerCase().indexOf(q)<0)return;n++;let color=r.action==='none'?'var(--weak)':(r.action==='memory'?'var(--success)':'var(--text)');let d=document.createElement('details');d.className='row';d.innerHTML='<summary><span class=\"t\">'+esc(r.time)+'</span><span class=\"src\">'+esc(r.triggerType)+'</span><span class=\"act\" style=\"color:'+color+'\">'+esc(r.action)+'</span><span class=\"why\">'+esc(r.reason)+'</span><span class=\"chev\">▸</span></summary><pre class=\"detail\"><span class=\"lbl\">state_restore</span>'+esc(r.stateRestore||r.raw)+'\\n<span class=\"lbl\">thought</span>'+esc(r.thought||'（未记录）')+'\\n<span class=\"lbl\">action</span>'+esc(r.action)+' / '+esc(r.reason)+'\\n<span class=\"lbl\">state_writeback</span>'+esc(r.stateWriteback||'（未记录）')+'\\n<span class=\"lbl\">confidence</span>'+esc(r.confidence||'—')+'\\n<span class=\"lbl\">applied_rules</span>'+esc(r.appliedRules||'[]')+'\\n<span class=\"lbl\">pulse_id</span>'+esc(r.pulseId||'—')+'\\n<span class=\"lbl\">tool_calls</span>'+esc(r.toolCalls||'[]')+'\\n<span class=\"lbl\">memories</span>'+esc(r.memories||'[]')+'</pre>';list.appendChild(d);});if(n===0)list.innerHTML='<div class=\"empty\">没有匹配的脉冲</div>';document.getElementById('cnt').textContent=n+' 条';}");
                sb.Append("async function load(){try{rows=JSON.parse(await chrome.webview.hostObjects.host.QueryPulses(''));let saved={};try{saved=JSON.parse(localStorage.getItem(KEY)||'{}');}catch(e){}document.getElementById('q').value=saved.q||'';render();if(saved.scroll)setTimeout(()=>document.getElementById('list').scrollTop=saved.scroll,50);}catch(e){document.getElementById('list').innerHTML='<div class=\"err\">'+esc(e)+'</div>';}}");
                sb.Append("document.getElementById('q').addEventListener('input',function(){render();try{localStorage.setItem(KEY,JSON.stringify({q:this.value,scroll:document.getElementById('list').scrollTop}));}catch(e){}});document.getElementById('list').addEventListener('scroll',function(){try{localStorage.setItem(KEY,JSON.stringify({q:document.getElementById('q').value,scroll:this.scrollTop}));}catch(e){}},{passive:true});load();");
                sb.Append("</script></body></html>");
                await _web.EnsureCoreWebView2Async(null);
                try { _web.CoreWebView2.AddHostObjectToScript("host", new FaceUiHost(_dataDir, OpenAuditById)); } catch { }
                _web.CoreWebView2.NavigateToString(sb.ToString());
                _health.ReportComponent("webview", true, "");
            }
            catch (Exception ex) { try { File.AppendAllText(Path.Combine(_dataDir, "webview.log"), ex.ToString() + "\n"); } catch { } }
        }
        void PauseProactive(DateTime until)
        {
            try
            {
                FaceState st = FaceSession.LoadState(_sessionPath);
                if (st == null) return;
                st.ProactivePausedUntil = until == DateTime.MinValue ? "" : until.ToString("o");
                FaceSession.SaveState(_sessionPath, st);
                AppendChat("Ahz", until == DateTime.MinValue ? "已取消主动暂停" : "主动消息暂停到 " + until.ToString("MM-dd HH:mm"), "chat");
            }
            catch (Exception ex) { AppendChat("Ahz", "暂停失败：" + ex.Message, "error"); }
        }
        void ToggleEmergencyStop()
        {
            if (!File.Exists(StopPath()))
            {
                EmergencyStop();
                return;
            }
            if (MessageBox.Show(this, "恢复后脉冲和工具调用会重新启用。确定恢复？", "Ahz Scribe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                ResumeEmergencyStop();
        }

        void EmergencyStop()
        {
            try
            {
                string file = StopPath();
                string dir = Path.GetDirectoryName(file);
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, DateTime.Now.ToString("o"), new UTF8Encoding(false));
                _health.SetStopped(true, "user emergency stop");
                _pulseTimer.Change(Timeout.Infinite, Timeout.Infinite);
                CancellationTokenSource turn = _turnCts;
                if (turn != null) { try { turn.Cancel(); } catch { } }
                _pendingSends.Clear();
                _budget.SetPendingCount(0);
                FaceAudit.Append(_dataDir, "emergency_stop", "stop", "blocked", "current turn cancelled", "user emergency stop");
                AppendChat("Ahz", "已急停：脉冲和当前任务已停止。", "error");
                try { if (_tray != null) _tray.ShowBalloonTip(2000, "Ahz Scribe", "已急停，所有脉冲和当前任务已停止", ToolTipIcon.Warning); } catch { }
                SyncEmergencyUi();
            }
            catch (Exception ex) { AppendChat("Ahz", "急停失败：" + ex.Message, "error"); }
        }

        void ResumeEmergencyStop()
        {
            try
            {
                try { File.Delete(StopPath()); } catch { }
                _health.SetStopped(false, "user resumed after confirmation");
                _pulseTimer.Change(_settings.PulseSeconds * 1000, _settings.PulseSeconds * 1000);
                FaceAudit.Append(_dataDir, "emergency_stop", "resume", "allowed", "", "user resumed after confirmation");
                AppendChat("Ahz", "已恢复脉冲和任务执行。", "chat");
                SyncEmergencyUi();
                UpdateStatus();
            }
            catch (Exception ex) { AppendChat("Ahz", "恢复失败：" + ex.Message, "error"); }
        }

        void SyncEmergencyUi()
        {
            bool stopped = File.Exists(StopPath());
            if (_stopBanner != null)
            {
                _stopBanner.Visible = stopped;
                if (_stopRow != null) _stopRow.Height = stopped ? 32 : 0;
            }
            if (_stopButton != null)
            {
                _stopButton.Text = stopped ? "恢复" : "■ 急停";
                _stopButton.BackColor = stopped ? Theme.StateWarning : Theme.StateDanger;
                _stopButton.ForeColor = stopped ? Theme.TextPrimary : Theme.DangerButtonFg;
            }
            if (_topStop != null)
            {
                _topStop.Text = stopped ? "↻" : "■";
                _topStop.BackColor = stopped ? Theme.StateWarning : Theme.StateDanger;
                _topStop.ForeColor = stopped ? Theme.TextPrimary : Theme.DangerButtonFg;
            }
            if (_input != null) _input.Enabled = !stopped;
            if (_send != null) _send.Enabled = !stopped && !_busySend;
            if (stopped && _topDot != null) _topDot.ForeColor = Theme.StateDanger;
        }
        void UpdateStatus()
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(UpdateStatus)); return; }
                FaceState st = FaceSession.LoadState(_sessionPath);
                BodyStatus b = _sensor == null ? new BodyStatus() : _sensor.Snapshot();
                _uiBus.Publish(new UiSnapshot { State = st, Body = b, Memories = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult() });
            }
            catch { }
        }

        void ApplyUiAux(UiSnapshot s)
        {
            if (s == null) return;
            if (InvokeRequired) { BeginInvoke(new Action<UiSnapshot>(ApplyUiAux), s); return; }
            FaceState st = s.State;
            BodyStatus b = s.Body ?? new BodyStatus();
            int mem = _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult().Count;
            var sb = new StringBuilder();
            sb.AppendLine("episode     " + (st == null ? "-" : st.CurrentEpisode));
            sb.AppendLine("messages    " + (st == null ? 0 : st.MessageCount));
            sb.AppendLine("focus       " + (st == null ? "" : st.CurrentFocus));
            sb.AppendLine("active_task " + (st == null ? "" : st.ActiveTask));
            sb.AppendLine("intents     " + (st == null || st.OpenIntents == null ? 0 : st.OpenIntents.Count));
            sb.AppendLine("memory      " + mem);
            sb.AppendLine("pulse       " + (st == null || st.LastPulseTrigger == null ? "-" : st.LastPulseTrigger.Type + " / " + st.LastPulseTrigger.ReceivedAt));
            sb.AppendLine("health      " + _health.Snapshot().State);
            sb.AppendLine();
            sb.AppendLine(_sensor == null ? "" : _sensor.Summary(b));
            _statusView.Text = sb.ToString();
            if (_taskView != null)
            {
                var ts = new StringBuilder();
                ts.AppendLine("active_task  " + (st == null ? "" : st.ActiveTask));
                ts.AppendLine("started_at   " + (st == null ? "" : st.ActiveTaskStartedAt));
                bool hasTask = st != null && !string.IsNullOrEmpty(st.ActiveTask);
                ts.AppendLine();
                ts.AppendLine("任务步骤");
                if (hasTask)
                {
                    ts.AppendLine("  [完成] 1. 接收任务");
                    ts.AppendLine("  [进行] 2. 执行中");
                    ts.AppendLine("  [等待] 3. 写回结果");
                }
                else ts.AppendLine("  （当前没有进行中的任务）");
                ts.AppendLine();
                ts.AppendLine("未完成意图");
                if (st != null && st.OpenIntents != null)
                    foreach (OpenIntent it in st.OpenIntents) ts.AppendLine(it.Status + "  " + it.CreatedAt + "  " + it.Text);
                _taskView.Text = ts.ToString();
                if (_taskProgress != null) _taskProgress.Visible = st != null && !string.IsNullOrEmpty(st.ActiveTask);
                if (_recovering && _stateFocus != null) _stateFocus.Text = "当前焦点\\n正在恢复连续性...";
            }
        }
        void UpdateNav()
        {
            if (_nav == null) return;
            for (int i = 0; i < _nav.Length; i++)
            {
                _nav[i].Text = (i == 0 && _unreadChat) ? "对话 ●" : (i == 0 ? "对话" : _nav[i].Text.Replace(" ●", ""));
                _nav[i].BackColor = i == _tabs.SelectedIndex ? Theme.BgElevated : Theme.BgBase;
                _nav[i].ForeColor = i == _tabs.SelectedIndex ? Theme.TextPrimary : Theme.TextSecondary;
            }
        }
        int ClearOpenIntents()
        {
            FaceState st = FaceSession.LoadState(_sessionPath);
            int n = 0;
            if (st != null && st.OpenIntents != null)
            {
                foreach (OpenIntent it in st.OpenIntents)
                {
                    if (it.Status == "open" || it.Status == "monitoring") { it.Status = "cancelled"; n++; }
                }
                FaceSession.SaveState(_sessionPath, st);
            }
            UpdateStatus();
            return n;
        }

        void RememberAuto(string kind, string key, string value, string reason)
        {
            string audit = FaceAudit.Append(_dataDir, "memory", "remember", "allowed", key + "=" + value, reason);
            _memory.RememberAsync(new MemoryItem
            {
                Kind = kind,
                Key = key,
                Value = value,
                Summary = key + "=" + value,
                SourceAuditId = audit,
                Provenance = SourceProvenance(SourceKind.System, audit, key + "=" + value)
            }, CancellationToken.None).GetAwaiter().GetResult();
            RenderMemory();
            RefreshMemoryHtml();
        }

        bool TryNaturalIntent(string text)
        {
            string t = text.Trim();
            bool clearWord = t.IndexOf("清空", StringComparison.Ordinal) >= 0 || t.IndexOf("清除", StringComparison.Ordinal) >= 0 || t.IndexOf("取消", StringComparison.Ordinal) >= 0;
            bool taskWord = t.IndexOf("任务", StringComparison.Ordinal) >= 0 || t.IndexOf("意图", StringComparison.Ordinal) >= 0;
            if (clearWord && taskWord)
            {
                int n = ClearOpenIntents();
                AppendChat("Ahz", n == 0 ? "当前没有待取消的任务或意图。" : "已取消 " + n + " 条任务/意图。", "chat");
                return true;
            }
            var m = System.Text.RegularExpressions.Regex.Match(t, @"简称\s*([A-Za-z0-9_\-]+)");
            if (m.Success)
            {
                string name = m.Groups[1].Value;
                RememberAuto("identity", "nickname", name, "natural language: 简称");
                AppendChat("Ahz", "已记住：简称 " + name + "。", "chat");
                return true;
            }
            if (t.StartsWith("以后叫我", StringComparison.Ordinal) || t.StartsWith("叫我", StringComparison.Ordinal))
            {
                string name = t.Replace("以后叫我", "").Replace("叫我", "").Trim();
                if (name.Length > 0)
                {
                    RememberAuto("identity", "nickname", name, "natural language: 叫我");
                    AppendChat("Ahz", "已记住：以后叫你 " + name + "。", "chat");
                    return true;
                }
            }
            if (t.StartsWith("我叫", StringComparison.Ordinal))
            {
                string name = t.Substring(2).Trim();
                if (name.Length > 0 && name != "谁")
                {
                    RememberAuto("identity", "user_name", name, "natural language: 我叫");
                    AppendChat("Ahz", "已记住：你叫 " + name + "。", "chat");
                    return true;
                }
            }
            if (t.StartsWith("记住", StringComparison.Ordinal) || t.StartsWith("请记住", StringComparison.Ordinal))
            {
                string body = t.StartsWith("请记住", StringComparison.Ordinal) ? t.Substring(3).Trim() : t.Substring(2).Trim();
                int eq = body.IndexOf('=');
                string key; string value;
                if (eq > 0) { key = body.Substring(0, eq).Trim(); value = body.Substring(eq + 1).Trim(); }
                else
                {
                    string[] parts = body.Split(new[] { '是', '：', ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return false;
                    key = parts[0].Trim(); value = parts[1].Trim();
                }
                if (key.Length == 0 || value.Length == 0) return false;
                RememberAuto("semantic", key, value, "natural language: 记住");
                AppendChat("Ahz", "已记住：" + key + " = " + value, "chat");
                return true;
            }
            return false;
        }
        void ExtractTurnMemory(string user, string answer)
        {
            try
            {
                string source = FaceAudit.Append(_dataDir, "memory", "extract", "allowed", "turn", "turn extraction");
                var ex = new MemoryExtractor(_host.Model) { Budget = _budget };
                List<MemoryItem> items = ex.ExtractAsync(user + "\nAhz: " + answer, source, CancellationToken.None).GetAwaiter().GetResult();
                foreach (MemoryItem m in items) _memory.RememberAsync(m, CancellationToken.None).GetAwaiter().GetResult();
                if (items.Count > 0) { try { BeginInvoke(new Action(delegate { RenderMemory(); RefreshMemoryHtml(); })); } catch { } }
            }
            catch { }
        }

        void ExtractCurrent()
        {
            try
            {
                List<FaceMessage> msgs = FaceSession.Load(_sessionPath, 5000)
                    .Where(x => x.Kind != "tool").ToList();
                if (msgs.Count > 300) msgs = msgs.Skip(msgs.Count - 300).ToList();
                var sb = new StringBuilder();
                foreach (FaceMessage m in msgs)
                    sb.AppendLine((m.Role ?? "") + ": " + (m.Text ?? ""));
                string source = FaceAudit.Append(_dataDir, "memory", "extract", "allowed", "manual /extract", "manual extraction");
                string debugPath = Path.Combine(_dataDir, "extract-debug.log");
                File.WriteAllText(Path.Combine(_dataDir, "extract-transcript.txt"), sb.ToString(), new UTF8Encoding(false));
                var ex = new MemoryExtractor(_host.Model, s => { try { File.AppendAllText(debugPath, s + Environment.NewLine); } catch { } })
                {
                    Budget = _budget
                };
                List<MemoryItem> items = ex.ExtractAsync(sb.ToString(), source, CancellationToken.None).GetAwaiter().GetResult();
                foreach (MemoryItem m in items)
                    _memory.RememberAsync(m, CancellationToken.None).GetAwaiter().GetResult();
                RenderMemory();
                RefreshMemoryHtml();
                AppendChat("Ahz", "提取完成：" + items.Count + " 条，source=" + source, "chat");
            }
            catch (Exception ex) { AppendChat("Ahz", "提取失败：" + ex.Message, "chat"); }
        }
        string AuditPath()
        {
            string data = !string.IsNullOrEmpty(_settings.BodyData) ? _settings.BodyData : Environment.GetEnvironmentVariable("AHZ_BODY_DATA");
            if (!string.IsNullOrEmpty(data)) return Path.Combine(data, "audit.jsonl");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AhzScribe", "body", "audit.jsonl");
        }

        void HandleAudit(string args)
        {
            var allLines = new List<string>();
            string path = AuditPath();
            if (File.Exists(path)) allLines.AddRange(File.ReadAllLines(path, Encoding.UTF8));
            string facePath = FaceAudit.PathFor(_dataDir);
            if (File.Exists(facePath)) allLines.AddRange(File.ReadAllLines(facePath, Encoding.UTF8));
            if (allLines.Count == 0) { AppendChat("Ahz", "没有审计记录：" + path, "chat"); return; }
            string[] lines = allLines.ToArray();
            string[] parts = (args ?? "").Split(new[] { ' ' }, 2);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            if (cmd == "tail")
            {
                int n = 20;
                if (parts.Length > 1) int.TryParse(parts[1], out n);
                if (n < 1) n = 20;
                AppendChat("Ahz", string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n))), "chat");
                return;
            }
            if (cmd == "show")
            {
                string id = parts.Length > 1 ? parts[1].Trim() : "";
                if (id.Length == 0) { AppendChat("Ahz", "用法：/audit show <audit_id>", "chat"); return; }
                string hit = lines.FirstOrDefault(x => x.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null && hit.IndexOf("rolled_back_by", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string link = lines.FirstOrDefault(x => x.IndexOf("audit_link", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        x.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (link != null) hit += "\nrolled_back_by: audit_link present -> " + link;
                }
                AppendChat("Ahz", hit ?? "没有找到 audit_id：" + id, "chat");
                return;
            }
            if (cmd == "export")
            {
                string outPath = Path.Combine(_dataDir, "audit-export.jsonl");
                File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
                AppendChat("Ahz", "已导出 " + lines.Length + " 条到 " + outPath, "chat");
                return;
            }
            AppendChat("Ahz", "用法：/audit tail [n] | /audit show <id> | /audit export", "chat");
        }
        void SaveSettings()
        {
            _settings.BaseUrl = _setBase.Text.Trim();
            _settings.Model = _setModel.Text.Trim();
            _settings.Workspace = _setWorkspace.Text.Trim();
            _settings.BodyPipe = _setPipe.Text.Trim();
            _settings.BodyData = _setBodyData.Text.Trim();
            _settings.ModelNoProxy = _setNoProxy.Checked;
            _settings.ReadOnlyMode = _setReadOnly.Checked;
            _settings.MemorySearchLimit = (int)_setMemoryLimit.Value;
            _settings.ProactiveDailyMax = (int)_setProactiveMax.Value;
            _settings.QuietStartHour = (int)_setQuietStart.Value;
            _settings.QuietEndHour = (int)_setQuietEnd.Value;
            _settings.UnansweredPauseHours = (int)_setPauseHours.Value;
            _settings.PulseSeconds = (int)_setPulseSeconds.Value;
            if (_setNoProxy.Checked) Environment.SetEnvironmentVariable("AHZ_MODEL_NOPROXY", "1");
            else Environment.SetEnvironmentVariable("AHZ_MODEL_NOPROXY", "");
            _settings.Save(_dataDir);
            RefreshSelfModel();
            AppendChat("Ahz", "设置已保存，重启 face 后生效" + (string.IsNullOrEmpty(_settings.Workspace) ? "；工作区为空，propose_upgrade 会被拒绝" : ""), "chat");
        }
        void UpdateTitle(string focus)
        {
            Text = "Ahz Scribe · face v0";
            if (!string.IsNullOrWhiteSpace(focus))
                Text += " · " + Cut(focus, 36);
        }
        async void SendAsync()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0) return;
            _input.Clear();
            _pulseBody.LastUserAt = DateTime.UtcNow;
            FaceState ps = FaceSession.LoadState(_sessionPath);
            if (ps != null) { ps.UnansweredProactive = 0; ps.ProactivePausedUntil = ""; FaceSession.SaveState(_sessionPath, ps); }
            FaceSession.SetFocus(_sessionPath, text);
            UpdateTitle(text);
            AppendChat("你", text, "chat");
            if (_experience.IsNoEmojiPreferenceCorrection(text))
            {
                string preferenceAudit = FaceAudit.Append(_dataDir, "experience", "user_correction", "accepted",
                    Cut(text, 160), "user corrected final response style");
                if (_experience.RecordNoEmojiPreferenceCorrection(text, preferenceAudit, MmyMemory.DeviceId()))
                {
                    AppendChat("Ahz", "我记下了：以后最终回答不使用 emoji 或表情符号。", "chat");
                    return;
                }
            }
            if (_experience.IsReminderCorrection(text))
            {
                string correctionAudit = FaceAudit.Append(_dataDir, "experience", "user_correction", "accepted",
                    Cut(text, 160), "user corrected reminder behavior");
                if (_experience.RecordReminderCorrection(text, correctionAudit, MmyMemory.DeviceId()))
                {
                    AppendChat("Ahz", "我记下了：以后到期提醒会先核对证据，不再直接发送。", "chat");
                    return;
                }
            }
            if (text == "/stop")
            {
                EmergencyStop();
                return;
            }
            if (text == "/resume")
            {
                ResumeEmergencyStop();
                return;
            }
            if (File.Exists(StopPath()))
            {
                AppendChat("Ahz", "EMERGENCY_STOP 生效中。发 /resume 恢复。", "chat");
                return;
            }
            if (!text.StartsWith("/", StringComparison.Ordinal))
            {
                try { _goals.RecordUserResponse(DateTimeOffset.Now); }
                catch { }
            }
            if (text.StartsWith("/intent ", StringComparison.Ordinal))
            {
                string body = text.Substring(8).Trim();
                if (body.Length == 0) { AppendChat("Ahz", "用法：/intent [30m] <要提醒的事>", "chat"); return; }
                string dueAt = "";
                Match mins = Regex.Match(body, @"^(\d+)\s*(分钟|分|m|min)\s*(.*)$", RegexOptions.IgnoreCase);
                if (mins.Success)
                {
                    int delay = Math.Max(1, int.Parse(mins.Groups[1].Value));
                    dueAt = DateTime.Now.AddMinutes(delay).ToString("o");
                    body = mins.Groups[3].Value.Length == 0 ? mins.Value : mins.Groups[3].Value;
                }
                OpenIntent oi = FaceSession.AddIntent(_sessionPath, body, dueAt);
                _memory.RememberAsync(new MemoryItem { Kind = "intent", Key = oi.Id, Value = body, Summary = "intent", DueAt = dueAt }, CancellationToken.None).GetAwaiter().GetResult();
                AppendChat("Ahz", "已记下意图：" + body + (dueAt.Length == 0 ? "" : "（到期 " + dueAt + "）") + "\n下次脉冲会检查（安静时段 23:00–08:00 不提醒）", "chat");
                RenderMemory();
                RefreshMemoryHtml();
                return;
            }
            if (text == "/intents")
            {
                FaceState state = FaceSession.LoadState(_sessionPath);
                List<OpenIntent> open = state == null || state.OpenIntents == null
                    ? new List<OpenIntent>()
                    : state.OpenIntents.Where(x => x.Status == "open" || x.Status == "monitoring").ToList();
                AppendChat("Ahz", open.Count == 0 ? "没有未完成意图" :
                    string.Join("\n", open.Select(x => x.Id + "  " + x.Text)), "chat");
                return;
            }
            if (text.StartsWith("/intent-evidence", StringComparison.OrdinalIgnoreCase))
            {
                string arg = text.Length > 16 ? text.Substring(16).Trim() : "";
                FaceState state = FaceSession.LoadState(_sessionPath);
                List<OpenIntent> open = state == null || state.OpenIntents == null
                    ? new List<OpenIntent>()
                    : state.OpenIntents.Where(x => x.Status == "open" || x.Status == "monitoring").ToList();
                OpenIntent intent = string.IsNullOrEmpty(arg)
                    ? open.FirstOrDefault()
                    : open.FirstOrDefault(x => string.Equals(x.Id, arg, StringComparison.OrdinalIgnoreCase)) ??
                      open.FirstOrDefault(x => (x.Text ?? "").IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0);
                if (intent == null)
                {
                    AppendChat("Ahz", "没有匹配的未完成意图；先用 /intents 查看。", "chat");
                    return;
                }
                string evidenceAudit = FaceAudit.Append(_dataDir, "experience", "intent_evidence", "accepted",
                    Cut(intent.Text ?? "", 160), "user confirmed evidence for intent");
                _memory.RememberAsync(new MemoryItem
                {
                    Kind = "intent_evidence",
                    Key = intent.Id,
                    Value = "verified",
                    Summary = "evidence for " + (intent.Text ?? ""),
                    SourceAuditId = evidenceAudit,
                    Provenance = SourceProvenance(SourceKind.User, evidenceAudit, "intent evidence: " + (intent.Text ?? ""))
                }, CancellationToken.None).GetAwaiter().GetResult();
                AppendChat("Ahz", "已记录意图证据（24 小时有效）：" + intent.Text, "chat");
                RenderMemory();
                RefreshMemoryHtml();
                return;
            }
            if (text == "/pulse")
            {
                FaceSession.SetPulseTrigger(_sessionPath, "timer", "manual /pulse");
                DateTime saved = _pulseBody.LastUserAt;
                _pulseBody.LastUserAt = DateTime.UtcNow.AddSeconds(-701);
                try
                {
                    PulseDecision d = _arbiter.EnqueueAsync(new PulseTrigger { Type = "timer", Payload = "manual /pulse" }, CancellationToken.None).GetAwaiter().GetResult();
                    WritePulseDetail("timer", d, FaceSession.LoadState(_sessionPath));
                    AppendChat("Ahz", "pulse: " + (d == null ? "null" : d.Action + " / " + (d.Reason ?? "")), "chat");
                }
                finally { _pulseBody.LastUserAt = saved; }
                return;
            }
            if (text.StartsWith("/pause", StringComparison.OrdinalIgnoreCase))
            {
                string arg = text.Length > 6 ? text.Substring(6).Trim() : "";
                if (arg == "off" || arg == "取消") PauseProactive(DateTime.MinValue);
                else if (arg == "明天" || arg == "tomorrow") PauseProactive(DateTime.Today.AddDays(1).AddHours(8));
                else PauseProactive(DateTime.Now.AddHours(2));
                return;
            }
            if (text == "/budget")
            {
                RefreshStorageBudget();
                ResourceBudgetSnapshot budget = _budget.Snapshot();
                AppendChat("Ahz",
                    "budget = " + (budget.ActiveLimits.Count == 0 ? "ok" : string.Join(", ", budget.ActiveLimits)) +
                    "\ntasks = " + budget.ActiveTasks + "/" + budget.MaxConcurrentTasks +
                    "\npending_limit = " + budget.MaxPendingTasks +
                    "\ntool_calls_1h = " + budget.ToolCallsLastHour + "/" + budget.MaxToolCallsPerHour +
                    "\nmodel_attempts = " + budget.MaxModelAttempts +
                    "\nretries_1h = " + budget.RetriesLastHour +
                    "\nwebview_failures = " + budget.MaxWebViewFailures +
                    "\nmemory = " + MiB(budget.MemoryBytes) + "/" + MiB(budget.MemoryLimitBytes) +
                    "\nlogs = " + MiB(budget.LogBytes) + "/" + MiB(budget.LogLimitBytes), "chat");
                return;
            }
            if (text == "/identity")
            {
                LoadOrCreateIdentity();
                IdentityManifest identity = _identityManifest;
                bool valid = _identityResult != null && _identityResult.Verification != null &&
                    _identityResult.Verification.Valid;
                var lines = new List<string>();
                if (identity != null)
                {
                    lines.Add("identity = " + (valid ? "valid" : "invalid"));
                    lines.Add("soul = " + identity.SoulId);
                    lines.Add("lineage = " + identity.LineageId);
                    lines.Add("created = " + identity.CreatedAt);
                    lines.Add("device = " + identity.CurrentDeviceId + " (mutable)");
                    lines.Add("model = " + identity.ActiveModel + " (mutable)");
                    lines.Add("permission = " + identity.PermissionProfile + " / ceiling " + identity.PermissionCeiling);
                    foreach (IdentityInvariant invariant in identity.Invariants ?? new List<IdentityInvariant>())
                        lines.Add("- " + invariant.Key + " = " + invariant.Value);
                }
                if (_identityResult != null && _identityResult.Verification != null &&
                    _identityResult.Verification.Errors.Count > 0)
                    lines.Add("errors = " + string.Join("; ", _identityResult.Verification.Errors));
                AppendChat("Ahz", lines.Count == 0 ? "身份清单不可用" : string.Join("\n", lines), "chat");
                return;
            }
            if (text == "/goals" || text.StartsWith("/goal ", StringComparison.OrdinalIgnoreCase))
            {
                if (text == "/goals")
                {
                    List<GoalState> all = _goals.LoadLatest();
                    string detail = all.Count == 0 ? "没有目标" :
                        string.Join("\n", all.Select(x =>
                            x.GoalId + " [" + x.Status + "] " + x.Title +
                            "\n  source=" + x.SourceKind + " score=" +
                            x.Score.ToString("F2") + " evidence=" + x.EvidenceIds.Count +
                            " reason=" + x.LastTransition));
                    AppendChat("Ahz", "goals = " + all.Count +
                        " / active_limit=" + _goals.ActiveLimit +
                        " / daily_limit=" + _goals.DailyLimit + "\n" + detail, "chat");
                    return;
                }
                string arg = text.Length > 6 ? text.Substring(6).Trim() : "";
                if (arg.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                {
                    string title = arg.Substring(4).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        AppendChat("Ahz", "用法：/goal add <目标>", "chat");
                        return;
                    }
                    string audit = FaceAudit.Append(_dataDir, "goal", "user_add",
                        "allowed", title, "explicit user goal");
                    try
                    {
                        GoalState goal = _goals.Propose(title, "用户明确创建目标",
                            GoalSourceKind.UserIntent,
                            new[]
                            {
                                new AutobiographyEvidence
                                {
                                    EvidenceId = "audit:" + audit,
                                    Source = SourceProvenance(SourceKind.User, audit, title).Sources[0]
                                }
                            }, 0.9, DateTimeOffset.Now);
                        goal = _goals.Promote(goal.GoalId, DateTimeOffset.Now);
                        RefreshGoalContext();
                        AppendChat("Ahz", "已创建目标 " + goal.GoalId + "：" + goal.Title, "chat");
                    }
                    catch (Exception ex)
                    {
                        AppendChat("Ahz", "目标创建被拒绝：" + ex.Message, "error");
                    }
                    return;
                }
                int split = arg.IndexOf(' ');
                string action = split < 0 ? arg.ToLowerInvariant() :
                    arg.Substring(0, split).ToLowerInvariant();
                string goalId = split < 0 ? "" : arg.Substring(split + 1).Trim();
                if (!string.Equals(action, "drive", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(goalId))
                {
                    AppendChat("Ahz", "用法：/goal pause|resume|wait|done|cancel <goal_id>", "chat");
                    return;
                }
                if (string.Equals(action, "drive", StringComparison.OrdinalIgnoreCase))
                {
                    RunDrive();
                    return;
                }
                string operationAudit = FaceAudit.Append(_dataDir, "goal", action,
                    "allowed", goalId, "user goal command");
                try
                {
                    GoalState changed = action == "pause"
                        ? _goals.Pause(goalId, "user paused goal", DateTimeOffset.Now)
                        : action == "resume"
                            ? _goals.Resume(goalId, DateTimeOffset.Now)
                            : action == "wait"
                                ? _goals.Wait(goalId, "user set goal waiting", DateTimeOffset.Now)
                                : action == "done"
                                    ? _goals.Complete(goalId, DateTimeOffset.Now)
                                    : _goals.Cancel(goalId,
                                        "user cancelled goal; " + operationAudit,
                                        DateTimeOffset.Now);
                    RefreshGoalContext();
                    AppendChat("Ahz", "目标 " + changed.GoalId + " -> " + changed.Status +
                        " / " + changed.LastTransition, "chat");
                }
                catch (Exception ex)
                {
                    AppendChat("Ahz", "目标操作失败：" + ex.Message, "error");
                }
                return;
            }
            if (text == "/autobiography")
            {
                RefreshAutobiographyContext();
                List<AutobiographyEntry> entries = _autobiography.LoadAll()
                    .Where(x => x != null && x.Status == "active")
                    .OrderByDescending(x => x.CreatedAt, StringComparer.Ordinal)
                    .Take(12).ToList();
                string detail = entries.Count == 0 ? "还没有可追溯自传条目" :
                    string.Join("\n", entries.Select(x =>
                        "[" + x.Layer + "/" + x.Period + "] " + x.Text +
                        "\n  证据 " + x.Evidence.Count + " 条： " +
                        string.Join(", ", x.Evidence.Take(4).Select(e => e.EvidenceId)) +
                        (x.Evidence.Count > 4 ? " ..." : "")));
                AppendChat("Ahz", "autobiography entries = " +
                    _autobiography.LoadAll().Count + "\n" + detail, "chat");
                return;
            }
            if (text == "/attention" ||
                text.StartsWith("/attention ", StringComparison.OrdinalIgnoreCase))
            {
                RefreshAttentionContext();
                string arg = text.Length > 10 ? text.Substring(10).Trim() : "";
                if (arg.StartsWith("pause ", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("resume ", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("end ", StringComparison.OrdinalIgnoreCase))
                {
                    int split = arg.IndexOf(' ');
                    string action = arg.Substring(0, split).ToLowerInvariant();
                    string subject = arg.Substring(split + 1).Trim();
                    try
                    {
                        AttentionState changed = action == "pause"
                            ? _attention.Pause(subject, "user paused via /attention",
                                DateTimeOffset.Now)
                            : action == "resume"
                                ? _attention.Resume(subject, "user resumed via /attention",
                                    DateTimeOffset.Now)
                                : _attention.End(subject, "user ended via /attention",
                                    DateTimeOffset.Now);
                        FaceAudit.Append(_dataDir, "attention", action, "allowed",
                            changed.Subject, "user attention command");
                        RefreshAttentionContext();
                        AppendChat("Ahz", "关注 " + changed.Subject + " -> " +
                            changed.Status, "chat");
                    }
                    catch (Exception ex)
                    {
                        AppendChat("Ahz", "关注状态修改失败：" + ex.Message, "error");
                    }
                    return;
                }
                List<AttentionState> states = _attention.LoadLatest();
                AttentionState dominant = _attention.Dominant(DateTimeOffset.Now);
                string detail = states.Count == 0 ? "没有长期关注对象" :
                    string.Join("\n", states.Select(x =>
                        "- " + x.Subject + " / " + x.Status +
                        " / score " + x.Score.ToString("F2") +
                        " / evidence " + x.EvidenceIds.Count +
                        " / expires " + x.ExpiresAt));
                AppendChat("Ahz",
                    "dominant = " + (dominant == null ? "none" : dominant.Subject) +
                    "\n" + detail, "chat");
                return;
            }
            if (text == "/migration" ||
                text.StartsWith("/migration ", StringComparison.OrdinalIgnoreCase))
            {
                MigrationObservation observation = MigrationObservationStore.Current(
                    _dataDir, DateTimeOffset.Now);
                string arg = text.Length > 10 ? text.Substring(10).Trim() : "";
                if (arg.StartsWith("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = arg.Length > 6 ? arg.Substring(6).Trim() : "";
                    bool cancelled = MigrationObservationStore.Cancel(_dataDir,
                        observation.ObservationId,
                        string.IsNullOrWhiteSpace(reason) ? "user /migration cancel" : reason,
                        DateTimeOffset.Now);
                    AppendChat("Ahz", cancelled
                        ? "迁移观察期已取消：" + observation.ObservationId
                        : "没有可取消的活动观察期。", "chat");
                    return;
                }
                MigrationDiffReport report = _migrationReport ??
                    MigrationDiffService.ReadReport(_dataDir);
                if (report == null)
                {
                    AppendChat("Ahz", "还没有迁移差异报告", "chat");
                    return;
                }
                AppendChat("Ahz",
                    "migration = " + report.Kind +
                    "\nidentity_preserved = " + report.IdentityPreserved +
                    "\ndevice_changed = " + report.DeviceChanged +
                    "\nmodel_changed = " + report.ModelChanged +
                    "\npermission_changed = " + report.PermissionChanged +
                    "\nintent_changes = " + report.IntentChanges.Count +
                    "\nmemory_delta = " + report.MemoryCountDelta +
                    "\ncapabilities_added = " + report.CapabilitiesAdded.Count +
                    "\ncapabilities_removed = " + report.CapabilitiesRemoved.Count +
                    "\ntimeline_gap_hours = " +
                        (report.TimelineGapSeconds / 3600.0).ToString("F1") +
                    "\nobservation = " + observation.Status +
                    (string.IsNullOrEmpty(observation.ObservationId) ? "" :
                        "\nobservation_id = " + observation.ObservationId) +
                    (string.IsNullOrEmpty(observation.ExpiresAt) ? "" :
                        "\nobservation_expires = " + observation.ExpiresAt) +
                    "\nreport = " + MigrationDiffService.ReportPath(_dataDir),
                    "chat");
                return;
            }
            if (text == "/health" || text.StartsWith("/health ", StringComparison.OrdinalIgnoreCase))
            {
                string arg = text.Length > 7 ? text.Substring(7).Trim() : "";
                if (string.Equals(arg, "retry", StringComparison.OrdinalIgnoreCase))
                    await RecheckHealthComponentsAsync();
                HealthSnapshot health = _health.Snapshot();
                var rows = new List<string>();
                foreach (KeyValuePair<string, string> item in health.Components)
                    rows.Add(item.Key + " = " + item.Value);
                AppendChat("Ahz", "health = " + health.State + "\n" + string.Join("\n", rows) +
                    (string.IsNullOrEmpty(health.LastTransition) ? "" : "\nlast = " + health.LastTransition), "chat");
                return;
            }
            if (text == "/selfcheck")
            {
                var sb = new StringBuilder();
                await RecheckHealthComponentsAsync();
                HealthSnapshot health = _health.Snapshot();
                sb.AppendLine("health   " + health.State);
                foreach (KeyValuePair<string, string> item in health.Components)
                    sb.AppendLine(("         " + item.Key).PadRight(9) + item.Value);
                sb.AppendLine("body     " + (Environment.GetEnvironmentVariable("AHZ_BODY_PIPE") ?? "ahz-scribe-body"));
                int availableCaps = _selfModel == null || _selfModel.Capabilities == null ? 0 : _selfModel.Capabilities.Count(x => x.Available);
                int totalCaps = _selfModel == null || _selfModel.Capabilities == null ? 0 : _selfModel.Capabilities.Count;
                sb.AppendLine("self     " + (_selfModel == null ? "未加载" : "soul=" + _selfModel.SoulId + " caps=" + availableCaps + "/" + totalCaps));
                sb.AppendLine("source   " + (_selfModel == null ? "未加载" : "device=" + _selfModel.CurrentDeviceId + " model=" + _selfModel.ActiveModel));
                sb.AppendLine("identity " + (_identityManifest == null ? "未加载" :
                    "soul=" + _identityManifest.SoulId + " lineage=" + _identityManifest.LineageId +
                    " valid=" + (_identityResult != null && _identityResult.Verification != null &&
                        _identityResult.Verification.Valid)));
                try
                {
                    sb.AppendLine("memory   " + _memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult().Count + " 条");
                }
                catch (Exception ex) { sb.AppendLine("memory   FAIL " + ex.Message); }
                sb.AppendLine("autobiography " + _autobiography.LoadAll().Count + " 条 / context " +
                    (string.IsNullOrEmpty(_host.AutobiographySummary) ? "empty" : "ready"));
                AttentionState dominantAttention = _attention.Dominant(DateTimeOffset.Now);
                sb.AppendLine("attention " + _attention.LoadLatest().Count + " 个 / dominant " +
                    (dominantAttention == null ? "none" : dominantAttention.Subject));
                sb.AppendLine("goals     " + _goals.LoadLatest().Count(x =>
                    x.Status == GoalStatus.Candidate || x.Status == GoalStatus.Monitoring ||
                    x.Status == GoalStatus.Due || x.Status == GoalStatus.Paused ||
                    x.Status == GoalStatus.Waiting) + " active / " +
                    _goals.LoadLatest().Count + " total");
                sb.AppendLine("webview  " + (_web != null && _web.CoreWebView2 != null ? "initialized" : "not initialized"));
                sb.AppendLine("config   " + (_settings != null && _settings.SchemaVersion == 1 ? "OK" : "schema mismatch"));
                sb.AppendLine("sensor   " + (_bodyTimer != null ? "OK" : "missing") + " last=" + (_lastBodySample == default(DateTime) ? "never" : ((int)(DateTime.Now - _lastBodySample).TotalSeconds + "s ago")));
                sb.AppendLine("fallback " + FaceAudit.FallbackPath() + " exists=" + File.Exists(FaceAudit.FallbackPath()));
                sb.AppendLine("state    " + (_sessionPath ?? "-"));
                AppendChat("Ahz", sb.ToString(), "chat");
                return;
            }            if (text == "/proactive-reset")
            {
                FaceState st = FaceSession.LoadState(_sessionPath);
                if (st != null)
                {
                    st.ProactiveCount = 0; st.UnansweredProactive = 0; st.ProactivePausedUntil = "";
                    FaceSession.SaveState(_sessionPath, st);
                }
                AppendChat("Ahz", "主动消息限流已重置", "chat");
                return;
            }
            if (text == "/experience")
            {
                List<AdaptationRule> active = _experience.GetActiveRules();
                string detail = active.Count == 0 ? "没有生效中的适应规则" :
                    string.Join("\n", active.Select(x => "- " + x.Name + " [" + x.Scope + "] 到期 " +
                        (string.IsNullOrEmpty(x.ExpiresAt) ? "未设置" : x.ExpiresAt)));
                AppendChat("Ahz", "适应规则 " + _experience.LoadRules().Count +
                    " 条，决策轨迹 " + _experience.LoadTraces().Count + " 条\n" + detail, "chat");
                return;
            }
            if (text == "/experience-reset")
            {
                _experience.ResetIntentEvidenceRule("user /experience-reset");
                _experience.ResetToolRules();
                _experience.ResetResponsePreferenceRules();
                AppendChat("Ahz", "已停用意图证据、工具失败和回答偏好适应规则；后续行为应回到基线。", "chat");
                return;
            }
            if (text == "/mmy-merge")
            {
                int n = MmyMemory.MergeDuplicates(_dataDir);
                AppendChat("Ahz", "MMY 合并完成：" + n + " 条冲突已标记", "chat");
                return;
            }
            if (text == "/mmy-archive")
            {
                int n = MmyMemory.Archive(_dataDir, 365);
                AppendChat("Ahz", "MMY 归档完成：" + n + " 条旧情节", "chat");
                return;
            }
            if (text == "/mmy-migrate")
            {
                int n = MmyMemory.MigrateFromJsonl(_dataDir);
                AppendChat("Ahz", "MMY 迁移完成：" + n + " 条", "chat");
                return;
            }
            if (text == "/intent-clear" || text == "/clear-intents")
            {
                FaceState st = FaceSession.LoadState(_sessionPath);
                if (st != null && st.OpenIntents != null)
                {
                    foreach (OpenIntent it in st.OpenIntents) if (it.Status == "open" || it.Status == "monitoring") it.Status = "cancelled";
                    FaceSession.SaveState(_sessionPath, st);
                }
                AppendChat("Ahz", "已取消当前 open intents", "chat");
                return;
            }
            if (text == "/drive")
            {
                RunDrive();
                return;
            }
            if (text == "/extract")
            {
                ExtractCurrent();
                return;
            }
            if (text.StartsWith("/audit", StringComparison.Ordinal))
            {
                HandleAudit(text.Substring(6).Trim());
                return;
            }
            if (text == "/help")
            {
                AppendChat("Ahz", "命令：\n/ask <问题>  对话\n/tool <任务>  让模型调用工具\n/remember <kind> <key> <value>  写入记忆\n/memory <查询>  检索记忆\n/memory-forget <id>  软遗忘（可恢复）\n/memory-restore <id>  恢复记忆\n/memory-archive <id>  归档记忆\n/memory-consolidate [天数]  巩固、降级和归档\n/memory-lifecycle  查看记忆生命周期\n/episode  当前时间线状态\n/intent <事>  记一个提醒意图\n/intents  列出未完成意图\n/intent-evidence [id或文本]  记录意图证据\n/pulse  立即执行一次脉冲\n/extract  提取最近对话记忆\n/drive  立即跑一次内驱\n/goals  查看有界目标\n/goal add <目标>  创建用户目标\n/goal pause|resume|wait|done|cancel <id>  管理目标\n/intent-clear  取消所有 open intents\n/proactive-reset  重置主动消息限流\n/experience  查看经历适应规则\n/experience-reset  停用经历适应规则\n/autobiography  查看可追溯自传摘要\n/attention [pause|resume|end 主题]  查看或修改长期关注\n/identity  查看稳定身份和不变量\n/migration [cancel [原因]]  查看或取消迁移观察期\n/health [retry]  查看或重新检查健康状态\n/budget  查看资源预算\n/pause [明天|off]  暂停主动消息\n/selfcheck  运行时自检\n/audit tail [n]  查看审计\n/audit show <id>  查看一条\n/audit export  导出到 _face-data\n/stop  全局急停\n/resume  恢复\n/help  本帮助", "chat");
                return;
            }
            if (text == "/episode")
            {
                FaceState st = FaceSession.LoadState(_sessionPath);
                AppendChat("Ahz", st == null ? "还没有时间线状态" : "episode " + st.CurrentEpisode + "\nmessage_count " + st.MessageCount + "\nfocus " + (st.CurrentFocus ?? ""), "chat");
                return;
            }
            if (text.StartsWith("/remember ", StringComparison.Ordinal))
            {
                string[] p = text.Split(new[] { ' ' }, 4);
                if (p.Length < 4) { AppendChat("Ahz", "用法：/remember <kind> <key> <value>", "chat"); return; }
                string sa = FaceAudit.Append(_dataDir, "memory", "remember", "allowed", p[2] + "=" + p[3], "user /remember");
                _memory.RememberAsync(new MemoryItem
                {
                    Kind = p[1],
                    Key = p[2],
                    Value = p[3],
                    Summary = p[2] + "=" + p[3],
                    SourceAuditId = sa,
                    Provenance = SourceProvenance(SourceKind.User, sa, p[2] + "=" + p[3])
                }, CancellationToken.None).GetAwaiter().GetResult();
                RefreshAttentionContext();
                RefreshAutobiographyContext();
                AppendChat("Ahz", "已记住 " + p[2] + " = " + p[3], "chat");
                RenderMemory();
                RefreshMemoryHtml();
                if (_tabs.SelectedIndex == 2) RenderMemoryHtml();
                return;
            }
            if (text.StartsWith("/memory-forget ", StringComparison.Ordinal) ||
                text.StartsWith("/memory-restore ", StringComparison.Ordinal) ||
                text.StartsWith("/memory-archive ", StringComparison.Ordinal))
            {
                int split = text.IndexOf(' ');
                string action = text.Substring(0, split);
                string memoryId = text.Substring(split + 1).Trim();
                if (string.IsNullOrWhiteSpace(memoryId))
                {
                    AppendChat("Ahz", "缺少 memory id", "error");
                    return;
                }
                string audit = FaceAudit.Append(_dataDir, "memory", action.TrimStart('/'),
                    "allowed", memoryId, "user memory lifecycle command");
                try
                {
                    MemoryLifecycleEvent changed = action == "/memory-forget"
                        ? _memory.ForgetAsync(memoryId, "user requested forget", audit,
                            CancellationToken.None).GetAwaiter().GetResult()
                        : action == "/memory-restore"
                            ? _memory.RestoreAsync(memoryId, "user requested restore", audit,
                                CancellationToken.None).GetAwaiter().GetResult()
                            : _memory.ArchiveAsync(memoryId, "user requested archive", audit,
                                CancellationToken.None).GetAwaiter().GetResult();
                    RefreshAutobiographyContext();
                    RefreshAttentionContext();
                    RenderMemory();
                    AppendChat("Ahz", action == "/memory-forget"
                        ? "已软遗忘 " + memoryId + "，原记录仍可恢复。"
                        : action == "/memory-restore"
                            ? "已恢复记忆 " + memoryId + " -> " + changed.NewStatus
                            : "已归档记忆 " + memoryId + "，原记录保留。", "chat");
                }
                catch (Exception ex)
                {
                    AppendChat("Ahz", "记忆生命周期操作失败：" + ex.Message, "error");
                }
                return;
            }
            if (text.StartsWith("/memory-consolidate", StringComparison.OrdinalIgnoreCase))
            {
                string raw = text.Length > 19 ? text.Substring(19).Trim() : "";
                int days;
                if (!int.TryParse(raw, out days) || days < 1) days = 365;
                string audit = FaceAudit.Append(_dataDir, "memory", "consolidate",
                    "allowed", "days=" + days, "user memory consolidation command");
                try
                {
                    MemoryConsolidationResult result = _memory.ConsolidateAsync(
                        DateTimeOffset.Now, days, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    RefreshAutobiographyContext();
                    RefreshAttentionContext();
                    RenderMemory();
                    AppendChat("Ahz", "memory consolidate archived=" + result.Archived +
                        " downgraded=" + result.Downgraded +
                        " reinforced=" + result.Reinforced +
                        " conflicts=" + result.ConflictsPreserved +
                        " audit=" + audit, "chat");
                }
                catch (Exception ex)
                {
                    AppendChat("Ahz", "记忆巩固失败：" + ex.Message, "error");
                }
                return;
            }
            if (text == "/memory-lifecycle")
            {
                List<MemoryLifecycleEvent> events = _memory.LoadLifecycle();
                string detail = events.Count == 0 ? "没有生命周期记录" :
                    string.Join("\n", events.Skip(Math.Max(0, events.Count - 20)).Select(x =>
                        x.Action + " " + x.MemoryId + " " + x.PreviousStatus +
                        " -> " + x.NewStatus + " / " + x.Reason));
                AppendChat("Ahz", "memory lifecycle events = " + events.Count +
                    "\n" + detail, "chat");
                return;
            }
            if (text.StartsWith("/memory ", StringComparison.Ordinal))
            {
                string query = text.Substring(8);
                int limit = Math.Max(1, _settings.MemorySearchLimit);
                List<MemoryItem> hits = _memory.RetrieveAsync(query, Math.Max(12, limit),
                    CancellationToken.None).GetAwaiter().GetResult();
                hits = _attention.InfluenceRetrieval(query, hits, limit);
                AppendChat("Ahz", hits.Count == 0 ? "没有命中记忆" : string.Join("\n", hits.Select(x => "[" + x.Kind + "] " + x.Key + " = " + x.Value)), "chat");
                return;
            }
            if (!text.StartsWith("/", StringComparison.Ordinal) && TryNaturalIntent(text)) return;
            if (_busySend)
            {
                string queueReason;
                if (!_budget.TryQueuePending(_pendingSends.Count, out queueReason))
                {
                    AppendChat("Ahz", "待发送队列已满，本条未排队。\n" + queueReason, "error");
                    return;
                }
                _pendingSends.Enqueue(text);
                _budget.SetPendingCount(_pendingSends.Count);
                _status.Text = _pendingSends.Count + " 条待发送";
                return;
            }
            _busySend = true;
            var turnCts = new CancellationTokenSource();
            _turnCts = turnCts;
            bool taskStarted = false;
            _send.Enabled = false;
            try
            {
                FaceSession.BeginTask(_sessionPath, text);
                if (_lastResponse != null) { _lastResponse.Text = "最后响应：等待中…"; _lastResponse.ForeColor = Theme.StateWarning; }
                taskStarted = true;
                _host.Approval = delegate(BrainToolCall call, CancellationToken ct)
                {
                    if (_settings.ReadOnlyMode && call.Tier != PermissionTier.ReadOnly) { AppendChat("Ahz", "只读模式已拒绝 " + call.Tool + "（" + call.Tier + "）", "error"); return Task.FromResult(false); }
                    try { if (_tray != null && !Visible) _tray.ShowBalloonTip(2000, "待确认", "请求 " + (call.Tool ?? "") + "：" + Cut(DescribeArgs(call), 40), ToolTipIcon.Warning); } catch { }
                    using (var dlg = new ApprovalForm("工具审批：" + (call.Tool ?? ""), DescribeArgs(call)))
                        return Task.FromResult(dlg.ShowDialog(this) == DialogResult.Yes);
                };
                string answer = text.StartsWith("/tool ", StringComparison.Ordinal)
                    ? await _host.RunToolAsync(text.Substring(6), turnCts.Token)
                    : await _host.AskAsync(text, turnCts.Token);
                AppendChat("Ahz", answer ?? "", "chat");
                if (!string.IsNullOrWhiteSpace(answer)) _ = Task.Run(delegate { ExtractTurnMemory(text, answer); });
            UpdateStatus();
            }
            catch (OperationCanceledException)
            {
                if (!File.Exists(StopPath())) AppendChat("Ahz", "本轮执行已取消。", "error");
            }
            catch (Exception ex) { Append("错误：" + ex.Message); if (_lastResponse != null) { _lastResponse.Text = "最后响应：错误 " + DateTime.Now.ToString("HH:mm:ss"); _lastResponse.ForeColor = Theme.StateDanger; } }
            finally
            {
                if (taskStarted) FaceSession.EndTask(_sessionPath);
                if (ReferenceEquals(_turnCts, turnCts))
                {
                    _turnCts = null;
                    turnCts.Dispose();
                }
                bool stopped = File.Exists(StopPath());
                _send.Enabled = !stopped;
                _input.Enabled = !stopped;
                _busySend = false;
                if (!stopped && _pendingSends.Count > 0)
                {
                    _input.Text = _pendingSends.Dequeue();
                    _budget.SetPendingCount(_pendingSends.Count);
                    _status.Text = _pendingSends.Count + " 条待发送";
                    BeginInvoke(new Action(SendAsync));
                }
                else
                {
                    _status.Text = stopped ? "已急停" : "episode -   memory 0   body " + (Environment.GetEnvironmentVariable("AHZ_BODY_PIPE") ?? "ahz-scribe-body");
                    if (!stopped) _input.Focus();
                }
            }
        }

        static string DescribeArgs(BrainToolCall call)
        {
            if (call.Args == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in call.Args) sb.AppendLine(kv.Key + " = " + kv.Value);
            return sb.ToString();
        }

        static Provenance SourceProvenance(SourceKind kind, string auditId, string summary)
        {
            return new Provenance
            {
                Confidence = 1.0,
                DirectlyObserved = true,
                Inferred = false,
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = kind.ToString().ToLowerInvariant(),
                        Kind = kind,
                        DeviceId = MmyMemory.DeviceId(),
                        AuditId = auditId ?? "",
                        Ts = DateTimeOffset.UtcNow.ToString("o"),
                        Summary = summary ?? ""
                    }
                }
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_turnCts != null) { try { _turnCts.Cancel(); } catch { } _turnCts.Dispose(); _turnCts = null; }
            _host.Dispose();
            base.OnFormClosed(e);
        }
    }
}
