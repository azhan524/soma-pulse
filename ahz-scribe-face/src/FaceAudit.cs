using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AhzScribe.Face
{
    public static class FaceAudit
    {
        static readonly object Gate = new object();
        public static event Action Appended;
        public static string LastError = "";

        public static string PathFor(string dataDir)
        {
            return Path.Combine(dataDir, "audit.jsonl");
        }

        public static string FallbackPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ahz", "audit_fallback", "audit_fallback.jsonl");
        }

        public static string Append(string dataDir, string span, string tool, string decision, string result, string reason)
        {
            lock (Gate)
            {
                string id = "face_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var rec = new
                {
                    audit_id = id,
                    ts = DateTimeOffset.UtcNow.ToString("o"),
                    span = span,
                    tool = tool,
                    decision = decision,
                    result = result,
                    reason = reason,
                    source = "face",
                    fallback = false
                };
                try
                {
                    Directory.CreateDirectory(dataDir);
                    File.AppendAllText(PathFor(dataDir), JsonSerializer.Serialize(rec) + Environment.NewLine, new UTF8Encoding(false));
                    RaiseAppended();
                    return id;
                }
                catch (Exception primaryEx)
                {
                    try
                    {
                        string fb = FallbackPath();
                        Directory.CreateDirectory(Path.GetDirectoryName(fb));
                        var frec = new
                        {
                            audit_id = id,
                            ts = DateTimeOffset.UtcNow.ToString("o"),
                            span = span,
                            tool = tool,
                            decision = decision,
                            result = result,
                            reason = reason,
                            source = "face",
                            fallback = true
                        };
                        File.AppendAllText(fb, JsonSerializer.Serialize(frec) + Environment.NewLine, new UTF8Encoding(false));
                        RaiseAppended();
                        return id;
                    }
                    catch (Exception fbEx) { LastError = primaryEx.GetType().Name + ":" + primaryEx.Message + " / " + fbEx.GetType().Name + ":" + fbEx.Message; return ""; }
                }
            }
        }

        public static bool Probe(string dataDir, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(dataDir);
                using (new FileStream(PathFor(dataDir), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                {
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static void RaiseAppended()
        {
            Action handler = Appended;
            if (handler == null) return;
            try { handler(); } catch { }
        }

        public static void MergeFallback(string dataDir)
        {
            lock (Gate)
            {
                try
                {
                    string fb = FallbackPath();
                    if (!File.Exists(fb)) return;
                    string[] lines = File.ReadAllLines(fb);
                    if (lines.Length == 0) { File.Delete(fb); return; }
                    using (var w = new StreamWriter(PathFor(dataDir), true, new UTF8Encoding(false)))
                        foreach (string l in lines) w.WriteLine(l);
                    File.Delete(fb);
                }
                catch { }
            }
        }
    }
}
