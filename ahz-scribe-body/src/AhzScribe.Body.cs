using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using FlaUI.Core;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using FlaUI.UIA3;
using FlaUI.Core.AutomationElements;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Body
{
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int needed);
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--self-test")
                return SelfTest.Run();
            if (args != null && args.Length > 0 && args[0] == "--ipc-test")
                return IpcSelfTest.Run();
            if (args != null && args.Length > 0 && args[0] == "--token-test")
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                try
                {
                    int isApp = 0; int len = 0;
                    GetTokenInformation(id.Token, 29, IntPtr.Zero, 0, out len);
                    IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                    if (GetTokenInformation(id.Token, 29, buf, 4, out len)) isApp = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                    Console.WriteLine("appcontainer=" + isApp);
                }
                catch (Exception ex) { Console.WriteLine("tokenerr=" + ex.Message); }
                Console.WriteLine("user=" + id.Name + " owner=" + id.Owner);
                foreach (var g in id.Groups)
                    if (g.Value != null && g.Value.StartsWith("S-1-16-")) Console.WriteLine("integrity=" + g.Value);
                try
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "ahz-scribe-audit", System.IO.Pipes.PipeDirection.InOut);
                    pipe.Connect(2000);
                    Console.WriteLine("PIPE: OK");
                    pipe.Dispose();
                }
                catch (Exception ex) { Console.WriteLine("PIPE: " + ex.GetType().Name + " " + ex.Message); }
                return 0;
            }
            if (args != null && args.Length > 0 && args[0] == "--broker-test")
            {
                string data = Environment.GetEnvironmentVariable("AHZ_BODY_DATA") ?? Path.Combine(Environment.CurrentDirectory, "_body-data");
                Directory.CreateDirectory(data);
                var sink = new BrokerAuditSink(new JsonlAuditSink(Path.Combine(data, "broker-test.jsonl")));
                string id = sink.AppendAsync("broker_test", "test", "allowed", "ok", "read-only",
                    new Dictionary<string, object>(), "broker test", CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("BROKER AUDIT: " + (string.IsNullOrEmpty(id) ? "FAIL" : "PASS " + id));
                return string.IsNullOrEmpty(id) ? 1 : 0;
            }

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                Console.WriteLine("ahz-scribe-body: pipe=" + BodyServer.PipeName);
                BodyServer.RunAsync(cts.Token).GetAwaiter().GetResult();
            }
            return 0;
        }
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string Dump(object o) { return JsonSerializer.Serialize(o, o.GetType(), Options); }
        public static T Parse<T>(string s) { return JsonSerializer.Deserialize<T>(s, Options); }
    }

    internal sealed class BodyEnvelope
    {
        public string Action { get; set; }
        public ActionRequest Request { get; set; }
    }

    internal sealed class BodyReply
    {
        public bool Ok { get; set; }
        public ActionResult Result { get; set; }
        public string Error { get; set; }
        public List<CapabilityDescriptor> Capabilities { get; set; }
    }

    internal static class BodyCapabilityCatalog
    {
        static readonly List<CapabilityDescriptor> Items = Build();

        public static List<CapabilityDescriptor> Snapshot()
        {
            return Items.Select(Clone).ToList();
        }

        static CapabilityDescriptor Clone(CapabilityDescriptor value)
        {
            return new CapabilityDescriptor
            {
                Name = value.Name,
                Category = value.Category,
                Summary = value.Summary,
                MinimumTier = value.MinimumTier,
                RequiresApproval = value.RequiresApproval,
                Available = value.Available,
                UnavailableReason = value.UnavailableReason
            };
        }

        static List<CapabilityDescriptor> Build()
        {
            return ToolPolicyCatalog.All
                .OrderBy(x => x.Category)
                .ThenBy(x => x.Name)
                .Select(x => new CapabilityDescriptor
                {
                    Name = x.Name,
                    Category = x.Category,
                    Summary = x.Summary,
                    MinimumTier = x.MinimumTier,
                    RequiresApproval = x.RequiresApproval,
                    Available = true
                })
                .ToList();
        }
    }

    internal sealed class BodyExecutor : IActionExecutor
    {
        readonly string _root;
        readonly IAuditSink _audit;
        readonly SafeSelfEvolution _selfEvolution;

        public BodyExecutor(string root, IAuditSink audit)
        {
            _root = Path.GetFullPath(root);
            _audit = audit;
            _selfEvolution = new SafeSelfEvolution(_root);
        }

        public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var result = new ActionResult { RequestId = request == null ? "" : request.RequestId };
            string auditId = "";
            try
            {
                if (request == null) throw new ArgumentException("request is required");
                if (string.IsNullOrEmpty(request.Tool)) throw new ArgumentException("tool is required");
                if (request.Args == null) request.Args = new Dictionary<string, object>();
                if (StopRequested())
                {
                    result.Ok = false;
                    result.Error = "EMERGENCY_STOP";
                    result.DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                    return result;
                }

                if (request.Tool == "rollback_self")
                    await _audit.AppendAsync("snapshot", "rollback_self", "selected", "", request.Tier.ToString(),
                        request.Args, request.Reason, ct).ConfigureAwait(false);
                await _audit.AppendAsync("tier_decision", request.Tool, request.Tier.ToString(), "", request.Tier.ToString(),
                    request.Args, request.Reason, ct).ConfigureAwait(false);
                if (request.Approved)
                    await _audit.AppendAsync("approval", request.Tool, "approved", "", request.Tier.ToString(),
                        request.Args, "user approved", ct).ConfigureAwait(false);
                auditId = await _audit.AppendAsync(
                    "body_action", request.Tool, "pending", "",
                    request.Tier.ToString(), request.Args, request.Reason, ct).ConfigureAwait(false);
                result.AuditId = auditId;

                switch (request.Tool)
                {
                    case "read_file": result.Output = ReadFile(request, ct); break;
                    case "list_dir": result.Output = ListDir(request, ct); break;
                    case "write_file": result.Output = WriteFile(request, ct); break;
                    case "edit_file": result.Output = EditFile(request); break;
                    case "rollback_self": result.Output = RollbackSelf(request); break;
                    case "file_info": result.Output = FileInfo(request, ct); break;
                    case "make_dir": result.Output = MakeDir(request, ct); break;
                    case "delete_file": result.Output = DeleteFile(request, ct); break;
                    case "copy_file": result.Output = CopyFile(request, ct); break;
                    case "move_file": result.Output = MoveFile(request, ct); break;
                    case "run_shell": result.Output = RunShell(request, ct); break;
                    case "start_program": result.Output = StartProgram(request, ct); break;
                    case "search_text": result.Output = SearchText(request, ct); break;
                    case "tail_file": result.Output = TailFile(request); break;
                    case "glob": result.Output = Glob(request); break;
                    case "network_fetch": result.Output = NetworkFetch(request, ct); break;
                    case "browser_open": result.Output = BrowserOpen(request); break;
                    case "desktop_uia_tree": result.Output = DesktopUiaTree(); break;
                    case "desktop_uia_find": result.Output = DesktopUiaFind(request); break;
                    case "desktop_uia_click": result.Output = DesktopUiaClick(request); break;
                    case "desktop_uia_settext": result.Output = DesktopUiaSetText(request); break;
                    case "browser_read": result.Output = NetworkFetch(request, ct); break;
                    case "desktop_list_windows": result.Output = DesktopWindows(); break;
                    case "process_list": result.Output = ProcessList(); break;
                    case "app_list": result.Output = AppList(); break;
                    case "process_kill": result.Output = ProcessKill(request); break;
                    case "build_self": result.Output = BuildSelf(request); break;
                    case "self_evolution": result.Output = RunSelfEvolution(request); break;
                    case "clipboard_get": result.Output = ClipboardGet(); break;
                    case "clipboard_set": result.Output = ClipboardSet(request); break;
                    case "desktop_active_window": result.Output = DesktopActiveWindow(); break;
                    case "desktop_click": result.Output = DesktopClick(request); break;
                    case "desktop_type": result.Output = DesktopType(request); break;
                    case "desktop_hotkey": result.Output = DesktopHotkey(request); break;
                    case "desktop_scroll": result.Output = DesktopScroll(request); break;
                    case "desktop_scroll_loop": result.Output = DesktopScrollLoop(request); break;
                    case "desktop_scroll_stop": result.Output = DesktopScrollStop(); break;
                    case "desktop_screenshot": result.Output = DesktopScreenshot(); break;
                    case "desktop_screen_info": Rectangle vs = SystemInformation.VirtualScreen; result.Output = vs.Left + "," + vs.Top + "," + vs.Width + "," + vs.Height; break;
                    case "desktop_ocr": result.Output = DesktopOcr(request); break;
                    case "desktop_focus_window": result.Output = DesktopFocus(request); break;
                    case "desktop_window_state": result.Output = DesktopWindowState(request); break;
                    case "desktop_click_window": result.Output = DesktopClickWindow(request); break;
                    default: throw new InvalidOperationException("tool not implemented by body v0: " + request.Tool);
                }
                result.Ok = true;
                string resultAuditId = await _audit.AppendAsync(
                    "body_result", request.Tool, "allowed", "ok",
                    request.Tier.ToString(), request.Args, request.Reason, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(resultAuditId)) result.AuditId = resultAuditId;
                if (request.Tool == "rollback_self" && !string.IsNullOrEmpty(request.LinkedAuditId))
                {
                    try
                    {
                        await _audit.AppendAsync("audit_link", "rolled_back_by", "linked", result.AuditId, "read-only",
                            new Dictionary<string, object>
                            {
                                ["original_audit_id"] = request.LinkedAuditId,
                                ["rollback_audit_id"] = result.AuditId
                            }, "rollback reverse link", ct).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
                try
                {
                    string blockedAuditId = await _audit.AppendAsync("blocked", request.Tool, "blocked", ex.Message, request.Tier.ToString(),
                        request.Args, "body exception", ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(blockedAuditId)) result.AuditId = blockedAuditId;
                }
                catch { }
            }
            result.Provenance = new Provenance
            {
                Confidence = result.Ok ? 1.0 : 0.5,
                DirectlyObserved = result.Ok,
                Inferred = false,
                Sources = new List<SourceRef>
                {
                    new SourceRef
                    {
                        SourceId = request == null ? "" : (request.RequestId ?? ""),
                        Kind = SourceKind.Tool,
                        DeviceId = Environment.MachineName,
                        AuditId = result.AuditId,
                        Ts = DateTimeOffset.UtcNow.ToString("o"),
                        Summary = request == null ? (result.Error ?? "") : (request.Tool + ": " + (result.Ok ? "ok" : result.Error))
                    }
                }
            };
            result.DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            return result;
        }

        static bool StopRequested()
        {
            string f = Environment.GetEnvironmentVariable("AHZ_STOP_FILE");
            if (string.IsNullOrEmpty(f))
                f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AhzScribe", "state", "EMERGENCY_STOP");
            return File.Exists(f);
        }
        string FullPath(ActionRequest r, string key, bool mustExist)
        {
            string rel = r.Args != null && r.Args.ContainsKey(key) ? Convert.ToString(r.Args[key]) : "";
            if (string.IsNullOrEmpty(rel)) throw new ArgumentException(key + " is required");
            string p = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(_root, rel));
            if (!IsUnder(_root, p)) throw new UnauthorizedAccessException("path escapes workspace: " + p);
            if (mustExist && !File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("not found: " + p);
            return p;
        }

        static bool IsUnder(string root, string path)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(path);
            return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        string ReadFile(ActionRequest r, CancellationToken ct)
        {
            string p = FullPath(r, "path", true);
            if (Directory.Exists(p)) throw new IOException("is a directory: " + p);
            return File.ReadAllText(p);
        }

        string ListDir(ActionRequest r, CancellationToken ct)
        {
            string p = FullPath(r, "path", true);
            var lines = new List<string>();
            foreach (string d in Directory.EnumerateDirectories(p).OrderBy(x => x))
                lines.Add("[dir] " + Path.GetFileName(d));
            foreach (string f in Directory.EnumerateFiles(p).OrderBy(x => x))
                lines.Add("[file] " + Path.GetFileName(f) + " (" + new FileInfo(f).Length + ")");
            return string.Join("\n", lines);
        }

        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

        string DesktopClick(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_click requires Full tier and Approved=true");
            int x = Convert.ToInt32(r.Args.ContainsKey("x") ? r.Args["x"] : 0);
            int y = Convert.ToInt32(r.Args.ContainsKey("y") ? r.Args["y"] : 0);
            if (r.Args.ContainsKey("relative") && Convert.ToBoolean(r.Args["relative"])) { x += SystemInformation.VirtualScreen.Left; y += SystemInformation.VirtualScreen.Top; }
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            return "clicked " + x + "," + y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;

        string DesktopScroll(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_scroll requires Full tier and Approved=true");
            int delta = 0;
            if (r.Args.ContainsKey("delta")) int.TryParse(Convert.ToString(r.Args["delta"]), out delta);
            if (delta == 0) delta = -120;
            int times = 1;
            if (r.Args.ContainsKey("times")) int.TryParse(Convert.ToString(r.Args["times"]), out times);
            if (times < 1) times = 1;
            if (times > 20) times = 20;
            for (int i = 0; i < times; i++)
            {
                mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                Thread.Sleep(120);
            }
            return "scrolled " + times + " x " + delta;
        }
        static CancellationTokenSource _scrollCts;
        static readonly object ScrollGate = new object();

        string DesktopScrollLoop(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_scroll_loop requires Full tier and Approved=true");
            int minutes = 5;
            if (r.Args.ContainsKey("minutes")) int.TryParse(Convert.ToString(r.Args["minutes"]), out minutes);
            if (minutes < 1) minutes = 1;
            if (minutes > 30) minutes = 30;
            int seconds = 8;
            if (r.Args.ContainsKey("interval_seconds")) int.TryParse(Convert.ToString(r.Args["interval_seconds"]), out seconds);
            if (seconds < 3) seconds = 3;
            if (seconds > 60) seconds = 60;
            int delta = -120;
            if (r.Args.ContainsKey("delta")) int.TryParse(Convert.ToString(r.Args["delta"]), out delta);
            if (delta == 0) delta = -120;
            lock (ScrollGate)
            {
                if (_scrollCts != null) { try { _scrollCts.Cancel(); } catch { } }
                _scrollCts = new CancellationTokenSource();
                CancellationToken token = _scrollCts.Token;
                int total = minutes;
                int step = seconds;
                int wheel = delta;
                Thread t = new Thread(delegate()
                {
                    DateTime end = DateTime.Now.AddMinutes(total);
                    while (DateTime.Now < end && !token.IsCancellationRequested)
                    {
                        mouse_event(0x0800, 0, 0, unchecked((uint)wheel), UIntPtr.Zero);
                        if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(step))) break;
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            return "scroll_loop started " + minutes + "m interval=" + seconds + "s";
        }

        string DesktopScrollStop()
        {
            lock (ScrollGate)
            {
                if (_scrollCts == null) return "scroll_loop not running";
                try { _scrollCts.Cancel(); } catch { }
                _scrollCts = null;
                return "scroll_loop stopped";
            }
        }
        string DesktopHotkey(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_hotkey requires Full tier and Approved=true");
            string keys = r.Args.ContainsKey("keys") ? Convert.ToString(r.Args["keys"]) : "";
            if (string.IsNullOrWhiteSpace(keys)) throw new ArgumentException("keys is required");
            SendKeys.SendWait(keys);
            return "sent " + keys;
        }
        string DesktopType(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_type requires Full tier and Approved=true");
            string text = r.Args.ContainsKey("text") ? Convert.ToString(r.Args["text"]) : "";
            foreach (char ch in text)
            {
                INPUT down = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } };
                INPUT up = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
                SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
                SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
            }
            return "typed " + text.Length + " chars";
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        string DesktopOcr(ActionRequest r)
        {
            string path = r.Args.ContainsKey("path") ? Convert.ToString(r.Args["path"]) : DesktopScreenshot();
            if (!Path.IsPathRooted(path)) path = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using (IRandomAccessStream stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult())
            {
                BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                SoftwareBitmap bitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
                OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null) throw new InvalidOperationException("OCR engine unavailable");
                OcrResult result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                return result.Text ?? "";
            }
        }
        string DesktopScreenshot()
        {
            Rectangle b = SystemInformation.VirtualScreen;
            string dir = Path.Combine(_root, "_screenshots");
            Directory.CreateDirectory(dir);
            string name = "screen_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
            string path = Path.Combine(dir, name);
            using (var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                bmp.Save(path, ImageFormat.Png);
            }
            return "_screenshots/" + name;
        }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        IntPtr FindWindowByTitle(string title)
        {
            IntPtr found = IntPtr.Zero;
            string want = (title ?? "").Trim();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                string t = sb.ToString();
                if (t.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        string DesktopWindowState(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_window_state requires Full tier and Approved=true");
            string title = r.Args.ContainsKey("title") ? Convert.ToString(r.Args["title"]) : "";
            string action = r.Args.ContainsKey("action") ? Convert.ToString(r.Args["action"]).ToLowerInvariant() : "restore";
            IntPtr h = FindWindowByTitle(title);
            if (h == IntPtr.Zero) return "window not found: " + title;
            switch (action)
            {
                case "minimize": ShowWindow(h, 6); break;
                case "maximize": ShowWindow(h, 3); break;
                case "restore": ShowWindow(h, 9); break;
                case "close": PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); break;
                default: return "unknown action: " + action;
            }
            return action + " " + title;
        }
        string DesktopFocus(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_focus_window requires Full tier and Approved=true");
            string title = r.Args.ContainsKey("title") ? Convert.ToString(r.Args["title"]) : "";
            IntPtr h = FindWindowByTitle(title);
            if (h == IntPtr.Zero) return "window not found: " + title;
            ShowWindow(h, 9);
            SetForegroundWindow(h);
            return "focused: " + title;
        }

        string DesktopClickWindow(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("desktop_click_window requires Full tier and Approved=true");
            string title = r.Args.ContainsKey("title") ? Convert.ToString(r.Args["title"]) : "";
            IntPtr h = FindWindowByTitle(title);
            if (h == IntPtr.Zero) return "window not found: " + title;
            ShowWindow(h, 9);
            SetForegroundWindow(h);
            RECT rect;
            if (!GetWindowRect(h, out rect)) return "GetWindowRect failed";
            int x = Convert.ToInt32(r.Args.ContainsKey("x") ? r.Args["x"] : 0);
            int y = Convert.ToInt32(r.Args.ContainsKey("y") ? r.Args["y"] : 0);
            if (r.Args.ContainsKey("relative") && Convert.ToBoolean(r.Args["relative"])) { x += SystemInformation.VirtualScreen.Left; y += SystemInformation.VirtualScreen.Top; }
            SetCursorPos(rect.Left + x, rect.Top + y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            return "clicked window " + title + " at " + x + "," + y;
        }
        string DesktopWindows()
        {
            var lines = new List<string>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                lines.Add(pid + " | " + title);
                return true;
            }, IntPtr.Zero);
            return string.Join("\n", lines.Take(100).ToArray());
        }

        string ClipboardGet()
        {
            return RunSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "");
        }

        string ClipboardSet(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("clipboard_set requires Full tier and Approved=true");
            string text = r.Args.ContainsKey("text") ? Convert.ToString(r.Args["text"]) : "";
            if (text.Length > 100000) text = text.Substring(0, 100000);
            RunSta(() => { Clipboard.SetText(text ?? ""); return "ok"; });
            return "clipboard set";
        }

        static string RunSta(Func<string> f)
        {
            string result = null;
            Exception error = null;
            var t = new Thread(delegate()
            {
                try { result = f(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw error;
            return result;
        }
        string BuildSelf(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("build_self requires Full tier and Approved=true");
            string script = Path.Combine(_root, "build.cmd");
            if (!File.Exists(script)) throw new FileNotFoundException("build.cmd not found in workspace: " + _root);
            var psi = new ProcessStartInfo("cmd.exe", "/c build.cmd") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } return "build timeout"; }
                string all = (stdout + "\n" + stderr).Trim();
                if (all.Length > 4000) all = all.Substring(all.Length - 4000);
                return "exit=" + p.ExitCode + "\n" + all;
            }
        }
        string RunSelfEvolution(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("self_evolution requires Full tier and Approved=true");
            string action = r.Args.ContainsKey("action")
                ? Convert.ToString(r.Args["action"])
                : "";
            return _selfEvolution.Execute(action, r.Args, true);
        }
        string ProcessKill(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("process_kill requires Full tier and Approved=true");
            int pid = Convert.ToInt32(r.Args.ContainsKey("pid") ? r.Args["pid"] : 0);
            if (pid <= 0) throw new ArgumentException("pid is required");
            if (pid == Process.GetCurrentProcess().Id) throw new InvalidOperationException("refusing to kill body itself");
            using (Process p = Process.GetProcessById(pid))
            {
                p.Kill();
                return "killed " + pid + " " + p.ProcessName;
            }
        }
        string AppList()
        {
            var lines = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                    lines.Add(p.Id + " | " + p.ProcessName + " | " + p.MainWindowTitle);
                }
                catch { }
                if (lines.Count >= 120) break;
            }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", lines.ToArray());
        }
        string ProcessList()
        {
            var lines = new List<string>();
            foreach (Process p in Process.GetProcesses().OrderByDescending(x => { try { return x.WorkingSet64; } catch { return 0; } }).Take(100))
            {
                try { lines.Add(p.Id + " | " + p.ProcessName + " | " + (p.WorkingSet64 / 1024 / 1024) + " MB"); }
                catch { }
            }
            if (lines.Count == 0) lines.Add("uia children=0");
            return string.Join("\n", lines.ToArray());
        }
        string DesktopActiveWindow()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            uint pid; GetWindowThreadProcessId(h, out pid);
            return pid + " | " + sb.ToString().Trim();
        }
        string SearchText(ActionRequest r, CancellationToken ct)
        {
            string pattern = r.Args.ContainsKey("pattern") ? Convert.ToString(r.Args["pattern"]) : "";
            string path = r.Args.ContainsKey("path") ? Convert.ToString(r.Args["path"]) : ".";
            if (string.IsNullOrEmpty(pattern)) throw new ArgumentException("pattern is required");
            string dir = FullPath(r, "path", true);
            var re = new Regex(pattern, RegexOptions.IgnoreCase);
            var sb = new StringBuilder();
            int n = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (n >= 200) break;
                try
                {
                    int line = 0;
                    foreach (string s in File.ReadLines(f))
                    {
                        line++;
                        if (re.IsMatch(s))
                        {
                            sb.AppendLine(Path.GetRelativePath(_root, f) + ":" + line + ": " + s.Trim());
                            if (++n >= 200) break;
                        }
                    }
                }
                catch { }
            }
            return sb.ToString().TrimEnd();
        }

        string TailFile(ActionRequest r)
        {
            string path = FullPath(r, "path", true);
            int n = r.Args.ContainsKey("lines") ? Convert.ToInt32(r.Args["lines"]) : 50;
            if (n < 1) n = 1; if (n > 1000) n = 1000;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            int start = Math.Max(0, lines.Length - n);
            return string.Join("\n", lines.Skip(start).ToArray());
        }

        string Glob(ActionRequest r)
        {
            string pattern = r.Args.ContainsKey("pattern") ? Convert.ToString(r.Args["pattern"]) : "*";
            string path = r.Args.ContainsKey("path") ? Convert.ToString(r.Args["path"]) : ".";
            string dir = FullPath(r, "path", true);
            var list = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(_root, x)).OrderBy(x => x).Take(500);
            return string.Join("\n", list.ToArray());
        }

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string DesktopUiaTree()
        {
            string output = "";
            string error = "";
            Thread t = new Thread(delegate()
            {
                try
                {
                    var lines = new List<string>();
                    using (var automation = new UIA3Automation())
                    {
                        AutomationElement desktop = automation.GetDesktop();
                        var children = desktop.FindAllChildren();
                        lines.Add("children=" + children.Length);
                        foreach (AutomationElement e in children)
                        {
                            try
                            {
                                string name = ""; string id = ""; string type = "?"; string cls = ""; int pid = 0;
                                try { name = e.Name ?? ""; } catch { }
                                try { id = e.AutomationId ?? ""; } catch { }
                                try { cls = e.ClassName ?? ""; } catch { }
                                try { type = e.ControlType.ToString(); } catch { }
                                try { pid = e.Properties.ProcessId.Value; } catch { }
                                lines.Add(pid + " | " + type + " | " + cls + " | " + id + " | " + name);
                                if (lines.Count >= 80) break;
                            }
                            catch (Exception ex) { lines.Add("err " + ex.GetType().Name); }
                        }
                    }
                    output = string.Join("\n", lines.ToArray());
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(8000);
            return error.Length > 0 ? error : (output.Length > 0 ? output : "uia empty");
        }
        static string UiaRun(Func<string> action)
        {
            string output = "";
            string error = "";
            Thread t = new Thread(delegate()
            {
                try { output = action(); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(10000);
            return error.Length > 0 ? "ERROR " + error : (output.Length > 0 ? output : "no match");
        }

        AutomationElement UiaFindOne(string name, string id)
        {
            using (var automation = new UIA3Automation())
            {
                AutomationElement desktop = automation.GetDesktop();
                foreach (AutomationElement e in desktop.FindAllDescendants())
                {
                    try
                    {
                        if (name.Length > 0 && !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (id.Length > 0 && !string.Equals(e.AutomationId, id, StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Length == 0 && id.Length == 0) continue;
                        return e;
                    }
                    catch { }
                }
            }
            return null;
        }

        string DesktopUiaFind(ActionRequest r)
        {
            string name = r.Args.ContainsKey("name") ? Convert.ToString(r.Args["name"]) : "";
            string id = r.Args.ContainsKey("automation_id") ? Convert.ToString(r.Args["automation_id"]) : "";
            return UiaRun(delegate()
            {
                var lines = new List<string>();
                using (var automation = new UIA3Automation())
                {
                    foreach (AutomationElement e in automation.GetDesktop().FindAllDescendants())
                    {
                        try
                        {
                            string n = e.Name ?? "";
                            string aid = e.AutomationId ?? "";
                            if (name.Length > 0 && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (id.Length > 0 && aid.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            int pid = 0; try { pid = e.Properties.ProcessId.Value; } catch { }
                            string type = "?"; try { type = e.ControlType.ToString(); } catch { }
                            lines.Add(pid + " | " + type + " | " + aid + " | " + n);
                            if (lines.Count >= 40) break;
                        }
                        catch { }
                    }
                }
                return lines.Count == 0 ? "no match" : string.Join("\n", lines.ToArray());
            });
        }

        string DesktopUiaClick(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved) throw new UnauthorizedAccessException("desktop_uia_click requires Full and Approved");
            string name = r.Args.ContainsKey("name") ? Convert.ToString(r.Args["name"]) : "";
            string id = r.Args.ContainsKey("automation_id") ? Convert.ToString(r.Args["automation_id"]) : "";
            return UiaRun(delegate()
            {
                AutomationElement e = UiaFindOne(name, id);
                if (e == null) return "no match";
                e.Click();
                return "clicked " + e.Name;
            });
        }

        string DesktopUiaSetText(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved) throw new UnauthorizedAccessException("desktop_uia_settext requires Full and Approved");
            string name = r.Args.ContainsKey("name") ? Convert.ToString(r.Args["name"]) : "";
            string id = r.Args.ContainsKey("automation_id") ? Convert.ToString(r.Args["automation_id"]) : "";
            string text = r.Args.ContainsKey("text") ? Convert.ToString(r.Args["text"]) : "";
            return UiaRun(delegate()
            {
                AutomationElement e = UiaFindOne(name, id);
                if (e == null) return "no match";
                e.AsTextBox().Text = text;
                return "set " + e.Name;
            });
        }
        string BrowserOpen(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("browser_open requires Full tier and Approved=true");
            string url = r.Args.ContainsKey("url") ? Convert.ToString(r.Args["url"]) : "";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("url must be http/https");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return "opened " + url;
        }
        string NetworkFetch(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("network_fetch requires Full tier and Approved=true");
            string url = r.Args.ContainsKey("url") ? Convert.ToString(r.Args["url"]) : "";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("url must be http/https");
            string text = Http.GetStringAsync(uri).GetAwaiter().GetResult();
            if (text.Length > 20000) text = text.Substring(0, 20000) + "\n[truncated]";
            return text;
        }
        string RunShell(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("run_shell requires Full tier and Approved=true");
            string command = r.Args.ContainsKey("command") ? Convert.ToString(r.Args["command"]) : "";
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required");
            var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                Task<string> so = p.StandardOutput.ReadToEndAsync();
                Task<string> se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } throw new TimeoutException("shell timed out"); }
                string output = (so.Result + se.Result).Trim();
                if (output.Length > 20000) output = output.Substring(0, 20000) + "\n[truncated]";
                return "exit=" + p.ExitCode + "\n" + output;
            }
        }

        string StartProgram(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("start_program requires Full tier and Approved=true");
            string path = r.Args.ContainsKey("path") ? Convert.ToString(r.Args["path"]) : "";
            string args = r.Args.ContainsKey("args") ? Convert.ToString(r.Args["args"]) : "";
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
            var psi = new ProcessStartInfo(path, args) { WorkingDirectory = _root, UseShellExecute = false };
            var p = Process.Start(psi);
            return "started pid=" + (p == null ? 0 : p.Id);
        }

        string FileInfo(ActionRequest r, CancellationToken ct)
        {
            string p = FullPath(r, "path", true);
            FileInfo fi = new FileInfo(p);
            if (fi.Exists) return "file " + fi.Length + " bytes, modified " + fi.LastWriteTimeUtc.ToString("o");
            DirectoryInfo di = new DirectoryInfo(p);
            return "dir, modified " + di.LastWriteTimeUtc.ToString("o");
        }

        string MakeDir(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier == PermissionTier.ReadOnly) throw new UnauthorizedAccessException("read-only tier cannot make directories");
            string p = FullPath(r, "path", false);
            Directory.CreateDirectory(p);
            return "created " + p;
        }

        string DeleteFile(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier == PermissionTier.ReadOnly) throw new UnauthorizedAccessException("read-only tier cannot delete files");
            string p = FullPath(r, "path", true);
            if (Directory.Exists(p)) throw new IOException("delete_file only deletes files: " + p);
            File.Delete(p);
            return "deleted " + p;
        }

        string CopyFile(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier == PermissionTier.ReadOnly) throw new UnauthorizedAccessException("read-only tier cannot copy files");
            string from = FullPath(r, "from", true);
            string to = FullPath(r, "to", false);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
            return "copied " + from + " -> " + to;
        }

        string MoveFile(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier == PermissionTier.ReadOnly) throw new UnauthorizedAccessException("read-only tier cannot move files");
            string from = FullPath(r, "from", true);
            string to = FullPath(r, "to", false);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Move(from, to);
            return "moved " + from + " -> " + to;
        }

        string RollbackSelf(ActionRequest r)
        {
            if (r.Tier != PermissionTier.Full || !r.Approved)
                throw new UnauthorizedAccessException("rollback_self requires Full tier and Approved=true");
            string snapDir = Path.Combine(_root, ".snapshots");
            if (!Directory.Exists(snapDir)) throw new DirectoryNotFoundException("no .snapshots directory");
            string want = r.Args != null && r.Args.ContainsKey("snapshot_id") ? Convert.ToString(r.Args["snapshot_id"]) : "";
            string[] files = Directory.GetFiles(snapDir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            string hit = null;
            foreach (string f in files)
                if (string.IsNullOrEmpty(want) || Path.GetFileNameWithoutExtension(f).StartsWith(want, StringComparison.OrdinalIgnoreCase))
                { hit = f; break; }
            if (hit == null) throw new FileNotFoundException("snapshot not found: " + want);
            int ok = 0, fail = 0;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(hit, Encoding.UTF8)))
            {
                JsonElement arr;
                if (!doc.RootElement.TryGetProperty("files", out arr) || arr.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("snapshot has no files");
                foreach (JsonElement item in arr.EnumerateArray())
                {
                    try
                    {
                        string rel = item.TryGetProperty("rel", out var rv) ? rv.GetString() : "";
                        if (string.IsNullOrEmpty(rel)) { fail++; continue; }
                        string target = Path.GetFullPath(Path.Combine(_root, rel));
                        if (!IsUnder(_root, target)) { fail++; continue; }
                        bool existed = item.TryGetProperty("existed", out var ev) && ev.ValueKind == JsonValueKind.True;
                        string old = item.TryGetProperty("old", out var ov) && ov.ValueKind == JsonValueKind.String ? ov.GetString() : "";
                        if (existed)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllText(target, old, new UTF8Encoding(false));
                        }
                        else if (File.Exists(target)) File.Delete(target);
                        ok++;
                    }
                    catch { fail++; }
                }
            }
            return "restored " + ok + " file(s), failed " + fail + ", snapshot " + Path.GetFileNameWithoutExtension(hit);
        }
        string EditFile(ActionRequest r)
        {
            string path = FullPath(r, "path", true);
            string oldS = r.Args.ContainsKey("old_string") ? Convert.ToString(r.Args["old_string"]) : "";
            string newS = r.Args.ContainsKey("new_string") ? Convert.ToString(r.Args["new_string"]) : "";
            if (string.IsNullOrEmpty(oldS)) throw new ArgumentException("old_string is required");
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (!text.Contains(oldS)) throw new ArgumentException("old_string not found");
            File.WriteAllText(path, text.Replace(oldS, newS), new UTF8Encoding(false));
            return "replaced in " + Path.GetRelativePath(_root, path);
        }
        string WriteFile(ActionRequest r, CancellationToken ct)
        {
            if (r.Tier == PermissionTier.ReadOnly)
                throw new UnauthorizedAccessException("read-only tier cannot write");
            string p = FullPath(r, "path", false);
            string text = r.Args.ContainsKey("text") ? Convert.ToString(r.Args["text"]) : "";
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text, new UTF8Encoding(false));
            return "written " + text.Length + " chars to " + p;
        }
    }

    internal sealed class JsonlAuditSink : IAuditSink
    {
        readonly string _path;
        readonly object _lock = new object();

        public JsonlAuditSink(string path) { _path = path; }

        public Task<string> AppendAsync(string span, string tool, string decision, string result,
            string tier, Dictionary<string, object> args, string reason, CancellationToken ct)
        {
            string id = "body_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var rec = new Dictionary<string, object>
            {
                ["audit_id"] = id,
                ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                ["span"] = span,
                ["tool"] = tool,
                ["decision"] = decision,
                ["result"] = result,
                ["tier"] = tier,
                ["reason"] = reason,
                ["args"] = args,
                ["linked_audit_id"] = args != null && args.ContainsKey("linked_audit_id") ? Convert.ToString(args["linked_audit_id"]) : ""
            };
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.AppendAllText(_path, Json.Dump(rec) + "\n", new UTF8Encoding(false));
            }
            return Task.FromResult(id);
        }
    }

    public sealed class BrokerReply
    {
        public string status { get; set; }
        public string audit_id { get; set; }
        public string message { get; set; }
    }

    internal sealed class BrokerAuditSink : IAuditSink
    {
        readonly IAuditSink _fallback;
        public BrokerAuditSink(IAuditSink fallback) { _fallback = fallback; }

        public async Task<string> AppendAsync(string span, string tool, string decision, string result,
            string tier, Dictionary<string, object> args, string reason, CancellationToken ct)
        {
            string id = "body_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                var doc = new Dictionary<string, object>
                {
                    ["audit_id"] = id,
                    ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["session_id"] = "body",
                    ["span"] = span,
                    ["tool"] = tool,
                    ["args_hash"] = "",
                    ["args_summary"] = reason ?? "",
                    ["payload_ref"] = "",
                    ["tier"] = tier ?? "",
                    ["decision"] = decision ?? "",
                    ["approval"] = new Dictionary<string, object> { ["required"] = false, ["result"] = null },
                    ["result"] = result ?? "",
                    ["duration_ms"] = 0,
                    ["snapshot_id"] = "",
                    ["linked_audit_id"] = "",
                    ["rolled_back_by"] = null
                };
                var req = new Dictionary<string, object> { ["op"] = "append", ["doc"] = doc, ["payload"] = "" };
                using (var pipe = new NamedPipeClientStream(".", "ahz-scribe-audit", PipeDirection.InOut))
                {
                    pipe.Connect(3000);
                    using (var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var r = new StreamReader(pipe, Encoding.UTF8))
                    {
                        w.WriteLine(Json.Dump(req));
                        string line = r.ReadLine();
                        var reply = string.IsNullOrEmpty(line) ? null : Json.Parse<BrokerReply>(line);
                        if (reply != null && !string.IsNullOrEmpty(reply.audit_id))
                            return reply.audit_id;
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory("_body-data");
                    File.AppendAllText(Path.Combine("_body-data", "broker-error.log"), ex.ToString() + "\n");
                }
                catch { }
            }
            return await _fallback.AppendAsync(span, tool, decision, result, tier, args, reason, ct).ConfigureAwait(false);
        }
    }
    internal static class BodyServer
    {
        public static readonly string PipeName = Environment.GetEnvironmentVariable("AHZ_BODY_PIPE") ?? "ahz-scribe-body";

        public static async Task RunAsync(CancellationToken ct)
        {
            string bodyData = Environment.GetEnvironmentVariable("AHZ_BODY_DATA");
            string root = string.IsNullOrWhiteSpace(bodyData) ? Path.Combine(Directory.GetCurrentDirectory(), "_body-data") : bodyData;
            Directory.CreateDirectory(root);
            var audit = new BrokerAuditSink(new JsonlAuditSink(Path.Combine(root, "audit.jsonl")));
            var executor = new BodyExecutor(Environment.CurrentDirectory, audit);

            while (!ct.IsCancellationRequested)
            {
                using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        BodyReply reply = new BodyReply();
                        try
                        {
                            var env = Json.Parse<BodyEnvelope>(line);
                            if (env == null)
                                throw new InvalidOperationException("bad envelope");
                            if (env.Action == "capabilities")
                            {
                                reply.Capabilities = BodyCapabilityCatalog.Snapshot();
                                reply.Ok = true;
                            }
                            else
                            {
                                if (env.Action != "execute" || env.Request == null)
                                    throw new InvalidOperationException("bad envelope");
                                reply.Result = await executor.ExecuteAsync(env.Request, ct).ConfigureAwait(false);
                                reply.Ok = reply.Result.Ok;
                            }
                        }
                        catch (Exception ex)
                        {
                            reply.Ok = false;
                            reply.Error = ex.Message;
                        }
                        await writer.WriteLineAsync(Json.Dump(reply)).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "_body-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello", new UTF8Encoding(false));
            var auditPath = Path.Combine(root, "audit.jsonl");
            var exec = new BodyExecutor(root, new JsonlAuditSink(auditPath));

            var read = exec.ExecuteAsync(new ActionRequest
            {
                RequestId = "r1", Tool = "read_file", Tier = PermissionTier.ReadOnly,
                Args = new Dictionary<string, object> { ["path"] = "a.txt" }
            }, CancellationToken.None).GetAwaiter().GetResult();

            var list = exec.ExecuteAsync(new ActionRequest
            {
                RequestId = "r2", Tool = "list_dir", Tier = PermissionTier.ReadOnly,
                Args = new Dictionary<string, object> { ["path"] = "." }
            }, CancellationToken.None).GetAwaiter().GetResult();

            var write = exec.ExecuteAsync(new ActionRequest
            {
                RequestId = "r3", Tool = "write_file", Tier = PermissionTier.WorkspaceWrite,
                Args = new Dictionary<string, object> { ["path"] = "b.txt", ["text"] = "ok" }
            }, CancellationToken.None).GetAwaiter().GetResult();

            var glob = exec.ExecuteAsync(new ActionRequest { RequestId = "r6", Tool = "glob", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object> { ["pattern"] = "*.txt", ["path"] = "." } }, CancellationToken.None).GetAwaiter().GetResult();
            var tail = exec.ExecuteAsync(new ActionRequest { RequestId = "r7", Tool = "tail_file", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object> { ["path"] = "a.txt", ["lines"] = 5 } }, CancellationToken.None).GetAwaiter().GetResult();
            var search = exec.ExecuteAsync(new ActionRequest { RequestId = "r8", Tool = "search_text", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object> { ["pattern"] = "hello", ["path"] = "." } }, CancellationToken.None).GetAwaiter().GetResult();

            var shot = exec.ExecuteAsync(new ActionRequest { RequestId = "r10", Tool = "desktop_screenshot", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object>() }, CancellationToken.None).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.Combine(root, ".snapshots"));
            File.WriteAllText(Path.Combine(root, ".snapshots", "snap-test.json"), "{\"files\":[{\"rel\":\"a.txt\",\"old\":\"hello\",\"existed\":true}]}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "a.txt"), "broken", new UTF8Encoding(false));
            var rollback = exec.ExecuteAsync(new ActionRequest { RequestId = "r11", Tool = "rollback_self", Tier = PermissionTier.Full, Approved = true, Args = new Dictionary<string, object>() }, CancellationToken.None).GetAwaiter().GetResult();
            bool selfEvolutionOk = SafeSelfEvolutionSelfTest.Run();
            var windows = exec.ExecuteAsync(new ActionRequest { RequestId = "r9", Tool = "desktop_list_windows", Tier = PermissionTier.ReadOnly, Args = new Dictionary<string, object>() }, CancellationToken.None).GetAwaiter().GetResult();
            var shell = exec.ExecuteAsync(new ActionRequest
            {
                RequestId = "r4", Tool = "run_shell", Tier = PermissionTier.Full, Approved = true,
                Args = new Dictionary<string, object> { ["command"] = "echo shell-ok" }
            }, CancellationToken.None).GetAwaiter().GetResult();
            var denied = exec.ExecuteAsync(new ActionRequest
            {
                RequestId = "r5", Tool = "run_shell", Tier = PermissionTier.Full, Approved = false,
                Args = new Dictionary<string, object> { ["command"] = "echo bad" }
            }, CancellationToken.None).GetAwaiter().GetResult();

            List<CapabilityDescriptor> caps = BodyCapabilityCatalog.Snapshot();
            bool capabilityOk = caps.Count >= 30 && caps.All(x => !string.IsNullOrEmpty(x.Name)) &&
                caps.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == caps.Count;
            bool provenanceOk = read.Provenance != null && read.Provenance.Sources != null &&
                read.Provenance.Sources.Count == 1 && read.Provenance.Sources[0].Kind == SourceKind.Tool &&
                read.Provenance.Sources[0].AuditId == read.AuditId;
            bool ok = capabilityOk && provenanceOk && read.Ok && read.Output == "hello" && list.Ok && list.Output.Contains("a.txt")
                      && write.Ok && File.ReadAllText(Path.Combine(root, "b.txt")) == "ok"
                      && File.Exists(auditPath) && shell.Ok && shell.Output.Contains("shell-ok")
                      && glob.Ok && glob.Output.Contains("a.txt") && tail.Ok && tail.Output.Contains("hello")
                      && search.Ok && search.Output.Contains("hello") && rollback.Ok && File.ReadAllText(Path.Combine(root, "a.txt")) == "hello" && windows.Ok && shot.Ok && File.Exists(Path.Combine(root, shot.Output))
                      && !denied.Ok && selfEvolutionOk;
            Console.WriteLine("CAPABILITIES: " + caps.Count + " approval=" + caps.Count(x => x.RequiresApproval));
            Console.WriteLine("PROVENANCE: " + (provenanceOk ? "PASS" : "FAIL"));
            Console.WriteLine(ok ? "BODY SELF-TEST: PASS" : "BODY SELF-TEST: FAIL");
            if (!ok)
            {
                Console.WriteLine("read=" + read.Ok + " " + read.Error);
                Console.WriteLine("list=" + list.Ok + " " + list.Error);
                Console.WriteLine("write=" + write.Ok + " " + write.Error);
            }
            try { Directory.Delete(root, true); } catch { }
            return ok ? 0 : 1;
        }
    }

    internal static class IpcSelfTest
    {
        public static int Run()
        {
            string file = Path.Combine(Directory.GetCurrentDirectory(), "_body-ipc.txt");
            File.WriteAllText(file, "ipc-hello", new UTF8Encoding(false));
            bool ok = false;
            string error = "";
            using (var cts = new CancellationTokenSource())
            {
                Task server = Task.Run(() => BodyServer.RunAsync(cts.Token));
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", BodyServer.PipeName, PipeDirection.InOut);
                    pipe.Connect(5000);
                    using (var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var r = new StreamReader(pipe, Encoding.UTF8))
                    {
                        string req = "{\"Action\":\"execute\",\"Request\":{\"RequestId\":\"ipc1\",\"Tool\":\"read_file\",\"Tier\":\"ReadOnly\",\"Args\":{\"path\":\"_body-ipc.txt\"}}}";
                        w.WriteLine(req);
                        string resp = r.ReadLine();
                        ok = resp != null && resp.Contains("ipc-hello");
                        if (!ok) error = resp == null ? "null response" : resp;
                    }
                    using (var capPipe = new NamedPipeClientStream(".", BodyServer.PipeName, PipeDirection.InOut))
                    {
                        capPipe.Connect(5000);
                        using (var cw = new StreamWriter(capPipe, new UTF8Encoding(false)) { AutoFlush = true })
                        using (var cr = new StreamReader(capPipe, Encoding.UTF8))
                        {
                            cw.WriteLine("{\"Action\":\"capabilities\"}");
                            string caps = cr.ReadLine();
                            ok = ok && caps != null && caps.Contains("read_file") && caps.Contains("run_shell");
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    try { if (pipe != null) pipe.Dispose(); } catch { }
                    cts.Cancel();
                    try { server.Wait(2000); } catch { }
                    try { File.Delete(file); } catch { }
                }
            }
            Console.WriteLine(ok ? "BODY IPC: PASS" : "BODY IPC: FAIL " + error);
            return ok ? 0 : 1;
        }
    }

}
