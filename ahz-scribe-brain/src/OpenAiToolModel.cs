using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    public sealed class ModelOptions
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 1024;
    }

    /// <summary>
    /// OpenAI-compatible chat completions client. It returns tool calls as BrainTurn.Calls.
    /// It does not execute anything; ToolAgentRunner sends those calls to body.
    /// </summary>
    public sealed class OpenAiToolModel : IToolCallingModel, IBrainModel, IDisposable
    {
        readonly ModelOptions _o;
        readonly HttpClient _http;

        public OpenAiToolModel(ModelOptions options, HttpClient http = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("BaseUrl is required");
            _o = options;
            if (http != null) _http = http;
            else if (Environment.GetEnvironmentVariable("AHZ_MODEL_NOPROXY") == "1")
                _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(120) };
            else
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public string ModelName { get { return _o.Model; } }

        public async Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history, CancellationToken ct)
        {
            var messages = new List<object>();
            messages.Add(new { role = "system", content = system });
            if (history != null)
            {
                foreach (BrainTurnMessage m in history)
                {
                    if (m == null) continue;
                    if (m.Role == "tool")
                    {
                        messages.Add(new
                        {
                            role = "user",
                            content = "工具 " + (m.ToolName ?? "") + " 结果：\n" + (m.Content ?? "")
                        });
                    }
                    else
                    {
                        messages.Add(new { role = m.Role ?? "user", content = m.Content ?? "" });
                    }
                }
            }

            var request = new
            {
                model = _o.Model,
                messages = messages,
                tools = ToolDefs(),
                tool_choice = "auto",
                temperature = _o.Temperature,
                max_tokens = _o.MaxTokens
            };

            string url = _o.BaseUrl.TrimEnd('/');
            if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url += "/chat/completions";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_o.ApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
                using (HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException("model HTTP " + (int)resp.StatusCode + ": " + body);
                    return ParseResponse(body);
                }
            }
        }

        public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            var messages = new List<object>
            {
                new { role = "system", content = system },
                new { role = "user", content = user ?? "" }
            };
            var request = new
            {
                model = _o.Model,
                messages = messages,
                temperature = _o.Temperature,
                max_tokens = _o.MaxTokens
            };
            string url = _o.BaseUrl.TrimEnd('/');
            if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url += "/chat/completions";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_o.ApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
                using (HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException("model HTTP " + (int)resp.StatusCode + ": " + body);
                    BrainTurn turn = ParseResponse(body);
                    return turn == null ? "" : (turn.Text ?? "");
                }
            }
        }

        public static BrainTurn ParseResponse(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                JsonElement choices;
                if (!root.TryGetProperty("choices", out choices) || choices.GetArrayLength() == 0)
                    throw new InvalidOperationException("model response has no choices");
                JsonElement choice = choices[0];
                JsonElement msg = choice.GetProperty("message");
                string text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
                var turn = new BrainTurn { Text = text ?? "", Calls = new List<BrainToolCall>() };
                string finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : "";
                if (msg.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tc in calls.EnumerateArray())
                    {
                        JsonElement fn = tc.GetProperty("function");
                        string name = fn.GetProperty("name").GetString();
                        string args = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "{}";
                        var call = new BrainToolCall
                        {
                            Id = tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Tool = name,
                            Tier = TierFor(name),
                            Approved = false,
                            Args = ParseArgs(args)
                        };
                        turn.Calls.Add(call);
                    }
                }
                turn.Done = turn.Calls.Count == 0 || finish == "stop";
                return turn;
            }
        }

        static Dictionary<string, object> ParseArgs(string json)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    result[p.Name] = Element(p.Value);
            }
            return result;
        }

        static object Element(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.TryGetInt64(out long l) ? (object)l : e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement x in e.EnumerateArray()) list.Add(Element(x));
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (JsonProperty p in e.EnumerateObject()) dict[p.Name] = Element(p.Value);
                    return dict;
                default: return null;
            }
        }

        static PermissionTier TierFor(string tool)
        {
            return ToolPolicyCatalog.TierFor(tool);
        }

        static object[] ToolDefs()
        {
            return new object[]
            {
                Fn("desktop_list_windows", "列出可见窗口标题", new { }),
                Fn("process_list", "列出占用内存最高的进程", new { }),
                Fn("process_kill", "按 PID 结束进程（必须已批准）", new { pid = "PID" }),
                Fn("build_self", "在工作区执行 build.cmd（必须已批准）", new { }),
                Fn("self_evolution", "受控自我改造流水线（必须已批准）",
                    new { action = "begin|apply|build|verify|commit|observe|pause|rollback|confirm|status", evolution_id = "", title = "", change = "", paths = new string[0], files = new Dictionary<string, string>(), identity_digest = "", permission_ceiling = "", hours = 24, reason = "" }),
                Fn("desktop_active_window", "读取前台窗口标题", new { }),
                Fn("desktop_click", "点击屏幕坐标（必须已批准）", new { x = "x", y = "y" }),
                Fn("desktop_type", "输入文本（必须已批准）", new { text = "文本" }),
                Fn("desktop_hotkey", "发送快捷键，如 ^s / %{F4}（必须已批准）", new { keys = "快捷键" }),
                Fn("desktop_scroll", "鼠标滚轮滚动（必须已批准）", new { delta = -120, times = 1 }),
                Fn("desktop_scroll_loop", "限时循环滚动（必须已批准）", new { minutes = 10, interval_seconds = 8, delta = -120 }),
                Fn("desktop_scroll_stop", "停止限时循环滚动", new { }),
                Fn("desktop_screenshot", "截取主屏到 _screenshots", new { }),
                Fn("desktop_uia_tree", "用 FlaUI 列出顶层 UIA 控件", new { }),
                Fn("desktop_uia_find", "按名称/automation_id 查找 UIA 控件", new { name = "名称", automation_id = "" }),
                Fn("desktop_uia_click", "点击 UIA 控件（必须已批准）", new { name = "名称", automation_id = "" }),
                Fn("desktop_uia_settext", "给 UIA 文本框设值（必须已批准）", new { name = "名称", automation_id = "", text = "文本" }),
                Fn("browser_open", "用默认浏览器打开 URL（必须已批准）", new { url = "https://..." }),
                Fn("browser_read", "读取 URL 的 HTML 文本", new { url = "https://..." }),
                Fn("clipboard_get", "读取剪贴板文本", new { }),
                Fn("clipboard_set", "写入剪贴板文本（必须已批准）", new { text = "文本" }),
                Fn("desktop_focus_window", "按标题聚焦窗口（必须已批准）", new { title = "标题" }),
                Fn("desktop_window_state", "最小化/最大化/恢复/关闭窗口（必须已批准）", new { title = "标题", action = "minimize|maximize|restore|close" }),
                Fn("desktop_click_window", "按窗口相对坐标点击（必须已批准）", new { title = "标题", x = "x", y = "y" }),
                Fn("propose_upgrade", "提交自我改造申请，等待用户批准", new { title = "标题", change = "改什么" }),
                Fn("read_file", "读取工作区文件", new { path = "相对路径" }),
                Fn("list_dir", "列出目录", new { path = "相对路径，默认 ." }),
                Fn("write_file", "写入工作区文件", new { path = "相对路径", content = "内容" }),
                Fn("edit_file", "替换文件中的一段文本", new { path = "相对路径", old_string = "原文", new_string = "新文" }),
                Fn("rollback_self", "从 .snapshots 回滚最近或指定快照", new { snapshot_id = "可选" }),
                Fn("search_text", "按正则搜索工作区文本", new { pattern = "正则", path = "相对路径" }),
                Fn("tail_file", "读取文件末尾", new { path = "相对路径", lines = "行数" }),
                Fn("glob", "按通配符列文件", new { pattern = "*.cs", path = "相对路径" }),
                Fn("network_fetch", "抓取 http/https（必须已批准）", new { url = "https://..." }),
                Fn("run_shell", "执行 shell（必须已批准）", new { command = "命令" }),
                Fn("start_program", "启动程序（必须已批准）", new { path = "程序路径", args = "参数" })
            };
        }

        static object Fn(string name, string desc, object schema)
        {
            var props = new Dictionary<string, object>();
            foreach (var p in schema.GetType().GetProperties())
                props[p.Name] = new { type = "string", description = p.Name };
            return new
            {
                type = "function",
                function = new
                {
                    name = name,
                    description = desc,
                    parameters = new { type = "object", properties = props, required = new string[0] }
                }
            };
        }

        public void Dispose() { _http.Dispose(); }
    }
}
