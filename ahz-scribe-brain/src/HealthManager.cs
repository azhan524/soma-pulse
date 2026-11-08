using System;
using System.Collections.Generic;
using System.Linq;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Aggregates component health and turns it into explicit execution permissions.
    /// Health is evidence for behavior, not a decorative status string.
    /// </summary>
    public sealed class HealthManager
    {
        sealed class ComponentReport
        {
            public bool? Healthy { get; set; }
            public string Reason { get; set; }
        }

        readonly object _gate = new object();
        readonly Dictionary<string, ComponentReport> _components =
            new Dictionary<string, ComponentReport>(StringComparer.OrdinalIgnoreCase);
        HealthState _state = HealthState.Healthy;
        string _lastTransition = "";
        string _lastTransitionTs = "";
        bool _stopped;

        public event Action<HealthSnapshot, HealthTransition> Changed;

        public HealthManager(params string[] components)
        {
            string[] names = components == null || components.Length == 0
                ? new[] { "model", "body", "memory", "audit", "webview" }
                : components;
            foreach (string name in names.Where(x => !string.IsNullOrWhiteSpace(x)))
                _components[name] = new ComponentReport();
        }

        public bool CanChat
        {
            get { lock (_gate) return !_stopped; }
        }

        public bool CanUseToolCalls
        {
            get { lock (_gate) return CanUseBodyLocked(); }
        }

        public bool CanWrite
        {
            get { lock (_gate) return CanWriteLocked(); }
        }

        public HealthSnapshot Snapshot()
        {
            lock (_gate) return BuildSnapshotLocked();
        }

        public void ReportComponent(string component, bool healthy, string reason = "")
        {
            if (string.IsNullOrWhiteSpace(component)) return;
            HealthSnapshot snapshot;
            HealthTransition transition;
            lock (_gate)
            {
                ComponentReport report;
                if (!_components.TryGetValue(component, out report))
                {
                    report = new ComponentReport();
                    _components[component] = report;
                }
                bool? oldHealthy = report.Healthy;
                string oldReason = report.Reason ?? "";
                string nextReason = healthy ? "" : (reason ?? "");
                if (oldHealthy == healthy && string.Equals(oldReason, nextReason, StringComparison.Ordinal))
                    return;

                report.Healthy = healthy;
                report.Reason = nextReason;
                transition = ApplyStateLocked(component, healthy ? "recovered" : nextReason);
                snapshot = BuildSnapshotLocked();
            }
            Action<HealthSnapshot, HealthTransition> handler = Changed;
            if (handler != null) handler(snapshot, transition);
        }

        public void SetStopped(bool stopped, string reason = "")
        {
            HealthSnapshot snapshot;
            HealthTransition transition;
            lock (_gate)
            {
                if (_stopped == stopped) return;
                _stopped = stopped;
                transition = ApplyStateLocked("system", stopped ? reason : "resumed");
                snapshot = BuildSnapshotLocked();
            }
            Action<HealthSnapshot, HealthTransition> handler = Changed;
            if (handler != null) handler(snapshot, transition);
        }

        public bool AllowsTool(string tool, PermissionTier tier)
        {
            lock (_gate)
            {
                if (!CanUseBodyLocked()) return false;
                PermissionTier effective = EffectiveTier(tool, tier);
                return effective == PermissionTier.ReadOnly || CanWriteLocked();
            }
        }

        public string ToolBlockReason(string tool, PermissionTier tier)
        {
            lock (_gate)
            {
                if (_stopped) return "health: system is stopped";
                string bodyReason = ComponentProblemLocked("body");
                if (bodyReason != null) return "health: body unavailable" + Suffix(bodyReason);
                if (!IsHealthyLocked("body")) return "health: body has not been verified";
                string modelReason = ComponentProblemLocked("model");
                if (modelReason != null) return "health: model unavailable" + Suffix(modelReason);
                if (!IsHealthyLocked("model")) return "health: model has not been verified";
                PermissionTier effective = EffectiveTier(tool, tier);
                if (effective != PermissionTier.ReadOnly && !CanWriteLocked())
                {
                    string auditReason = ComponentProblemLocked("audit");
                    string memoryReason = ComponentProblemLocked("memory");
                    return "health: write actions require healthy audit and memory" +
                        Suffix(auditReason ?? memoryReason ?? "current state is " + _state);
                }
                return "";
            }
        }

        bool CanUseBodyLocked()
        {
            return !_stopped && IsHealthyLocked("body") && IsHealthyLocked("model");
        }

        bool CanWriteLocked()
        {
            return !_stopped && IsHealthyLocked("body") && IsHealthyLocked("model") &&
                IsHealthyLocked("audit") && IsHealthyLocked("memory") &&
                _state != HealthState.ReadOnly;
        }

        bool IsHealthyLocked(string component)
        {
            ComponentReport report;
            return _components.TryGetValue(component, out report) && report.Healthy == true;
        }

        string ComponentProblemLocked(string component)
        {
            ComponentReport report;
            if (!_components.TryGetValue(component, out report) || report.Healthy != false) return null;
            return string.IsNullOrWhiteSpace(report.Reason) ? component + " unavailable" : report.Reason;
        }

        HealthTransition ApplyStateLocked(string component, string reason)
        {
            HealthState next = ComputeStateLocked();
            HealthState previous = _state;
            _state = next;
            string normalized = string.IsNullOrWhiteSpace(reason) ? "state changed" : reason;
            _lastTransition = previous + " -> " + next + ": " + component + " " + normalized;
            _lastTransitionTs = DateTimeOffset.UtcNow.ToString("o");
            return new HealthTransition
            {
                From = previous,
                To = next,
                Component = component,
                Reason = normalized,
                Ts = _lastTransitionTs
            };
        }

        HealthState ComputeStateLocked()
        {
            if (_stopped) return HealthState.Stopped;
            if (!IsKnownLocked("audit") || !IsKnownLocked("memory") ||
                HasProblemLocked("audit") || HasProblemLocked("memory") ||
                HasProblemLocked("identity"))
                return HealthState.ReadOnly;
            if (!IsKnownLocked("body") || !IsKnownLocked("model") ||
                HasProblemLocked("body") || HasProblemLocked("model") || HasProblemLocked("webview"))
                return HealthState.Degraded;
            return HealthState.Healthy;
        }

        bool IsKnownLocked(string component)
        {
            ComponentReport report;
            return _components.TryGetValue(component, out report) && report.Healthy != null;
        }

        bool HasProblemLocked(string component)
        {
            ComponentReport report;
            return _components.TryGetValue(component, out report) && report.Healthy == false;
        }

        HealthSnapshot BuildSnapshotLocked()
        {
            var snapshot = new HealthSnapshot
            {
                State = _state,
                LastTransition = _lastTransition,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            foreach (KeyValuePair<string, ComponentReport> item in _components)
            {
                string value = item.Value.Healthy == null ? "unknown" :
                    (item.Value.Healthy == true ? "healthy" : "unhealthy: " + (item.Value.Reason ?? ""));
                snapshot.Components[item.Key] = value;
                if (item.Value.Healthy == false)
                    snapshot.ActiveProblems.Add(item.Key + ": " + (item.Value.Reason ?? "unavailable"));
            }
            return snapshot;
        }

        static PermissionTier EffectiveTier(string tool, PermissionTier reported)
        {
            ToolPolicy policy = ToolPolicyCatalog.Find(tool);
            return policy == null || policy.MinimumTier <= reported ? reported : policy.MinimumTier;
        }

        static string Suffix(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason;
        }
    }
}
