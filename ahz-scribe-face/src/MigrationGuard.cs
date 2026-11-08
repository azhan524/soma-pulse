using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    public sealed class MigrationResult
    {
        public bool IsResurrection { get; set; }
        public bool DeviceChanged { get; set; }
        public bool ModelChanged { get; set; }
        public bool TimeJumped { get; set; }
        public bool LongDormancy { get; set; }
        public bool ImportedSoul { get; set; }
        public double GapDays { get; set; }
        public string Message { get; set; }
        public MigrationKind Kind { get; set; }
        public string PreviousModel { get; set; }
        public string CurrentModel { get; set; }
    }

    // Private migration guard. It never changes the public release.
    public static class MigrationGuard
    {
        static readonly Regex PathLike = new Regex(@"[A-Za-z]:\\[^\s""']+", RegexOptions.Compiled);

        public static string DeviceId()
        {
            return SoulBackup.DeviceId();
        }

        public static MigrationResult Check(string dataDir, string sessionPath, FaceState state)
        {
            return Check(dataDir, sessionPath, state, "", "");
        }

        public static MigrationResult Check(string dataDir, string sessionPath, FaceState state,
            string currentModel, string previousModel)
        {
            var result = new MigrationResult();
            if (state == null) return result;
            string current = DeviceId();
            bool pending = File.Exists(Path.Combine(dataDir, "migration_pending.json"));
            bool deviceChanged = !string.IsNullOrEmpty(state.DeviceId) && !string.Equals(state.DeviceId, current, StringComparison.OrdinalIgnoreCase);
            double gapDays = 0;
            DateTime last;
            if (TryParse(state.LastPulseAt, out last)) gapDays = Math.Max(0, (DateTime.Now - last).TotalDays);
            else if (TryParse(state.UpdatedAt, out last)) gapDays = Math.Max(0, (DateTime.Now - last).TotalDays);
            bool timeJumped = gapDays > 1.0;
            bool modelChanged = !string.IsNullOrWhiteSpace(currentModel) &&
                !string.IsNullOrWhiteSpace(previousModel) &&
                !string.Equals(currentModel, previousModel, StringComparison.Ordinal);
            bool changed = pending || deviceChanged || modelChanged || timeJumped;

            int attention = 0;
            if (state.OpenIntents != null)
            {
                foreach (OpenIntent it in state.OpenIntents)
                {
                    string text = it == null ? "" : (it.Text ?? "");
                    var m = PathLike.Match(text);
                    if (!m.Success) continue;
                    string path = m.Value.TrimEnd('.', ',', ';', ':', '，', '。');
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        it.Status = "needs_attention";
                        attention++;
                    }
                }
            }

            state.DeviceId = current;
            state.LastPulseAt = DateTime.Now.ToString("o");
            if (state.SchemaVersion <= 0) state.SchemaVersion = 1;
            FaceSession.SaveState(sessionPath, state);

            string pendingPath = Path.Combine(dataDir, "migration_pending.json");
            if (File.Exists(pendingPath)) { try { File.Delete(pendingPath); } catch { } }

            if (!changed) return result;

            bool resurrection = pending || deviceChanged;
            result.IsResurrection = resurrection;
            result.DeviceChanged = deviceChanged;
            result.ModelChanged = modelChanged;
            result.TimeJumped = timeJumped;
            result.LongDormancy = timeJumped && !resurrection;
            result.ImportedSoul = pending;
            result.GapDays = gapDays;
            result.PreviousModel = previousModel ?? "";
            result.CurrentModel = currentModel ?? "";
            result.Kind = pending ? MigrationKind.SoulImport :
                deviceChanged ? MigrationKind.DeviceMigration :
                modelChanged ? MigrationKind.ModelSwitch :
                timeJumped ? MigrationKind.LongDormancy :
                MigrationKind.NormalRestart;
            if (!changed)
            {
                result.Message = "普通重启；身份、设备、模型和时间线保持连续。";
                return result;
            }
            if (resurrection)
            {
                result.Message = "检测到连续性事件：" +
                    (pending ? "灵魂备份导入；" : "") +
                    (deviceChanged ? "设备变更；" : "") +
                    (modelChanged ? "模型切换 " + previousModel + " -> " + currentModel + "；" : "") +
                    (timeJumped ? "时间跳跃 " + gapDays.ToString("F1") + " 天；" : "") +
                    (attention > 0 ? attention + " 条意图路径失效；" : "");
            }
            else if (modelChanged)
            {
                result.Message = "检测到模型切换 " + previousModel + " -> " + currentModel +
                    "；身份、记忆和能力边界继续有效。";
            }
            else
            {
                result.Message = "检测到长时间休眠 " + gapDays.ToString("F1") +
                    " 天；身份和设备未更换。";
            }
            FaceAudit.Append(dataDir, "system_migration",
                resurrection ? "resurrection" : (modelChanged ? "model_switch" : "dormancy"),
                "allowed", result.Message, "startup migration guard");
            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    pulse_id = "pulse_migration_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    ts = DateTimeOffset.Now.ToString("o"),
                    trigger_type = resurrection ? "system_resurrection" :
                        (modelChanged ? "system_model_switch" : "system_dormancy"),
                    action = resurrection ? "system" : "none",
                    summary_line = result.Message,
                    audit_id = "",
                    state_restore = new { focus = state.CurrentFocus, device_id = current },
                    thought = "",
                    reason = result.Message,
                    confidence = (double?)null,
                    tool_calls = new object[0],
                    state_writeback = new
                    {
                        continuity = resurrection ? "resurrected" :
                            (modelChanged ? "model_switched" : "dormant_resume")
                    },
                    memories_injected = new string[0]
                });
                File.AppendAllText(Path.Combine(dataDir, "pulse.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            return result;
        }

        static bool TryParse(string value, out DateTime time)
        {
            time = default(DateTime);
            if (string.IsNullOrWhiteSpace(value)) return false;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                time = dto.LocalDateTime;
                return true;
            }
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time);
        }
    }
}
