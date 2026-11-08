using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AhzScribe.Face
{
    public sealed class FaceMessage
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public string Kind { get; set; } // chat / tool
        public string Ts { get; set; }
        public string Episode { get; set; }
    }

    public sealed class OpenIntent
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Status { get; set; } // open / done / cancelled
        public string CreatedAt { get; set; }
        public string DueAt { get; set; }
        public string Source { get; set; }
    }

    public sealed class PulseTrigger
    {
        public string Type { get; set; } // user_message / timer / intent_due / body_event / drive
        public string Payload { get; set; }
        public string ReceivedAt { get; set; }
    }

    public sealed class FaceState
    {
        public string CurrentEpisode { get; set; }
        public int MessageCount { get; set; }
        public string LastUserAt { get; set; }
        public string LastUserContactAt { get; set; }
        public string CurrentFocus { get; set; }
        public string LastPulseResult { get; set; }
        public string UpdatedAt { get; set; }
        public string ActiveTask { get; set; }
        public string ActiveTaskStartedAt { get; set; }
        public List<OpenIntent> OpenIntents { get; set; }
        public PulseTrigger LastPulseTrigger { get; set; }
        public string LastDriveDate { get; set; }
        public string LastIntentCheck { get; set; }
        public string LastProactiveDate { get; set; }
        public int ProactiveCount { get; set; }
        public string LastProactiveAt { get; set; }
        public int UnansweredProactive { get; set; }
        public string ProactivePausedUntil { get; set; }
        public string DeviceId { get; set; }
        public string LastPulseAt { get; set; }
        public int SchemaVersion { get; set; } = 1;
    }

    public static class FaceSession
    {
        const int EpisodeLimit = 40;
        static readonly object Gate = new object();
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static string StatePath(string sessionPath)
        {
            return Path.Combine(Path.GetDirectoryName(sessionPath), "current_state.json");
        }

        public static FaceState LoadState(string sessionPath)
        {
            try
            {
                string f = StatePath(sessionPath);
                if (!File.Exists(f)) return null;
                return JsonSerializer.Deserialize<FaceState>(File.ReadAllText(f, Encoding.UTF8), Json);
            }
            catch { return null; }
        }

        public static void SetFocus(string sessionPath, string text)
        {
            try
            {
                lock (Gate)
                {
                    FaceState st = LoadState(sessionPath) ?? new FaceState { CurrentEpisode = NewEpisodeId() };
                    st.CurrentFocus = string.IsNullOrEmpty(text) ? "" : (text.Length > 200 ? text.Substring(0, 200) : text);
                    SaveState(sessionPath, st);
                }
            }
            catch { }
        }
        public static void SaveState(string sessionPath, FaceState st)
        {
            try
            {
                st.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath(sessionPath)));
                File.WriteAllText(StatePath(sessionPath), JsonSerializer.Serialize(st, Json), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void BeginTask(string sessionPath, string text)
        {
            try
            {
                lock (Gate)
                {
                    FaceState st = LoadState(sessionPath) ?? new FaceState { CurrentEpisode = NewEpisodeId(), OpenIntents = new List<OpenIntent>() };
                    st.ActiveTask = text ?? "";
                    st.ActiveTaskStartedAt = DateTime.Now.ToString("o");
                    SaveState(sessionPath, st);
                }
            }
            catch { }
        }

        public static void EndTask(string sessionPath)
        {
            try
            {
                lock (Gate)
                {
                    FaceState st = LoadState(sessionPath);
                    if (st == null) return;
                    st.ActiveTask = "";
                    st.ActiveTaskStartedAt = "";
                    SaveState(sessionPath, st);
                }
            }
            catch { }
        }

        public static OpenIntent AddIntent(string sessionPath, string text, string dueAt, string source = "user")
        {
            lock (Gate)
            {
                FaceState st = LoadState(sessionPath) ?? new FaceState { CurrentEpisode = NewEpisodeId(), OpenIntents = new List<OpenIntent>() };
                if (st.OpenIntents == null) st.OpenIntents = new List<OpenIntent>();
                var it = new OpenIntent
                {
                    Id = "intent:" + Guid.NewGuid().ToString("N"),
                    Text = text ?? "",
                    Status = "open",
                    CreatedAt = DateTime.Now.ToString("o"),
                    DueAt = dueAt ?? "",
                    Source = source
                };
                st.OpenIntents.Add(it);
                SaveState(sessionPath, st);
                return it;
            }
        }

        public static void CloseIntent(string sessionPath, string id, string status)
        {
            lock (Gate)
            {
                FaceState st = LoadState(sessionPath);
                if (st == null || st.OpenIntents == null) return;
                foreach (OpenIntent it in st.OpenIntents)
                    if (it.Id == id) it.Status = string.IsNullOrEmpty(status) ? "done" : status;
                SaveState(sessionPath, st);
            }
        }

        public static void SetPulseTrigger(string sessionPath, string type, string payload)
        {
            lock (Gate)
            {
                FaceState st = LoadState(sessionPath) ?? new FaceState { CurrentEpisode = NewEpisodeId(), OpenIntents = new List<OpenIntent>() };
                st.LastPulseTrigger = new PulseTrigger { Type = type, Payload = payload ?? "", ReceivedAt = DateTime.Now.ToString("o") };
                SaveState(sessionPath, st);
            }
        }
        public static List<FaceMessage> Load(string path, int max)
        {
            var list = new List<FaceMessage>();
            try
            {
                if (!File.Exists(path)) return list;
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        FaceMessage m = JsonSerializer.Deserialize<FaceMessage>(line, Json);
                        if (m != null) list.Add(m);
                    }
                    catch { }
                }
            }
            catch { }
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }

        public static void Append(string path, FaceMessage message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    FaceState st = LoadState(path);
                    if (st == null || string.IsNullOrEmpty(st.CurrentEpisode))
                    {
                        st = new FaceState { CurrentEpisode = NewEpisodeId(), MessageCount = 0 };
                    }
                    if (st.MessageCount >= EpisodeLimit)
                    {
                        st.CurrentEpisode = NewEpisodeId();
                        st.MessageCount = 0;
                    }
                    message.Episode = st.CurrentEpisode;
                    message.Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    File.AppendAllText(path, JsonSerializer.Serialize(message, Json) + Environment.NewLine, new UTF8Encoding(false));
                    st.MessageCount++;
                    if (message.Role == "你") { st.LastUserAt = message.Ts; st.LastUserContactAt = message.Ts; }
                    SaveState(path, st);
                }
            }
            catch { }
        }

        static string NewEpisodeId()
        {
            return "ep-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }
}
