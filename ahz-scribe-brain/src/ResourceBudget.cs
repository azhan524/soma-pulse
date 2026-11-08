using System;
using System.Collections.Generic;
using AhzScribe.Contracts;

namespace AhzScribe.Brain
{
    /// <summary>
    /// Process-wide execution budget shared by face and brain.
    /// Counters are bounded in memory and storage thresholds are reported by face.
    /// </summary>
    public sealed class ResourceBudget
    {
        readonly object _gate = new object();
        readonly Queue<DateTimeOffset> _toolCalls = new Queue<DateTimeOffset>();
        readonly Queue<DateTimeOffset> _retries = new Queue<DateTimeOffset>();
        int _activeTasks;
        int _pendingTasks;
        long _memoryBytes;
        long _logBytes;

        public event Action<string, string> RetryObserved;

        public int MaxConcurrentTasks { get; private set; }
        public int MaxPendingTasks { get; private set; }
        public int MaxToolCallsPerHour { get; private set; }
        public int MaxModelAttempts { get; private set; }
        public int RetryBaseDelayMs { get; private set; }
        public int MaxWebViewFailures { get; private set; }
        public long MemoryLimitBytes { get; private set; }
        public long LogLimitBytes { get; private set; }

        public ResourceBudget(int maxConcurrentTasks = 1, int maxPendingTasks = 8,
            int maxToolCallsPerHour = 120, int maxModelAttempts = 2, int retryBaseDelayMs = 250,
            int maxWebViewFailures = 3, long memoryLimitBytes = 64L * 1024 * 1024,
            long logLimitBytes = 32L * 1024 * 1024)
        {
            MaxConcurrentTasks = Positive(maxConcurrentTasks, 1);
            MaxPendingTasks = Positive(maxPendingTasks, 8);
            MaxToolCallsPerHour = Positive(maxToolCallsPerHour, 120);
            MaxModelAttempts = Math.Max(1, maxModelAttempts);
            RetryBaseDelayMs = Math.Max(0, retryBaseDelayMs);
            MaxWebViewFailures = Positive(maxWebViewFailures, 3);
            MemoryLimitBytes = Math.Max(1, memoryLimitBytes);
            LogLimitBytes = Math.Max(1, logLimitBytes);
        }

        public bool TryEnterTask(out string reason)
        {
            lock (_gate)
            {
                if (_activeTasks >= MaxConcurrentTasks)
                {
                    reason = "max concurrent tasks reached (" + _activeTasks + "/" + MaxConcurrentTasks + ")";
                    return false;
                }
                _activeTasks++;
                reason = "";
                return true;
            }
        }

        public void ExitTask()
        {
            lock (_gate)
                if (_activeTasks > 0) _activeTasks--;
        }

        public bool TryQueuePending(int currentPending, out string reason)
        {
            if (currentPending < MaxPendingTasks)
            {
                reason = "";
                return true;
            }
            reason = "pending task queue full (" + currentPending + "/" + MaxPendingTasks + ")";
            return false;
        }

        public void SetPendingCount(int pending)
        {
            lock (_gate) _pendingTasks = Math.Max(0, pending);
        }

        public bool TryConsumeToolCall(out string reason)
        {
            lock (_gate)
            {
                Prune(_toolCalls, DateTimeOffset.UtcNow.AddHours(-1));
                if (_toolCalls.Count >= MaxToolCallsPerHour)
                {
                    reason = "hourly tool-call budget exhausted (" + _toolCalls.Count + "/" +
                        MaxToolCallsPerHour + ")";
                    return false;
                }
                _toolCalls.Enqueue(DateTimeOffset.UtcNow);
                reason = "";
                return true;
            }
        }

        public void RecordRetry(string subject = "model")
        {
            lock (_gate)
            {
                Prune(_retries, DateTimeOffset.UtcNow.AddHours(-1));
                _retries.Enqueue(DateTimeOffset.UtcNow);
            }
            RaiseRetry(subject, "started");
        }

        public void RecordRetryRecovered(string subject = "model")
        {
            RaiseRetry(subject, "recovered");
        }

        public TimeSpan RetryDelay(int retryOrdinal)
        {
            int shift = Math.Max(0, Math.Min(5, retryOrdinal - 1));
            return TimeSpan.FromMilliseconds(RetryBaseDelayMs * (1 << shift));
        }

        public void UpdateStorage(long memoryBytes, long logBytes)
        {
            lock (_gate)
            {
                _memoryBytes = Math.Max(0, memoryBytes);
                _logBytes = Math.Max(0, logBytes);
            }
        }

        public bool CanRunLowPriorityPulse
        {
            get
            {
                lock (_gate)
                    return _activeTasks == 0 && _memoryBytes <= MemoryLimitBytes &&
                        _logBytes <= LogLimitBytes;
            }
        }

        public bool CanRunPulse(bool highPriority)
        {
            return highPriority || CanRunLowPriorityPulse;
        }

        public ResourceBudgetSnapshot Snapshot()
        {
            lock (_gate)
            {
                Prune(_toolCalls, DateTimeOffset.UtcNow.AddHours(-1));
                Prune(_retries, DateTimeOffset.UtcNow.AddHours(-1));
                var snapshot = new ResourceBudgetSnapshot
                {
                    MaxConcurrentTasks = MaxConcurrentTasks,
                    ActiveTasks = _activeTasks,
                    MaxPendingTasks = MaxPendingTasks,
                    CurrentPendingTasks = _pendingTasks,
                    MaxToolCallsPerHour = MaxToolCallsPerHour,
                    ToolCallsLastHour = _toolCalls.Count,
                    MaxModelAttempts = MaxModelAttempts,
                    RetriesLastHour = _retries.Count,
                    MaxWebViewFailures = MaxWebViewFailures,
                    MemoryBytes = _memoryBytes,
                    MemoryLimitBytes = MemoryLimitBytes,
                    LogBytes = _logBytes,
                    LogLimitBytes = LogLimitBytes,
                    StorageHealthy = _memoryBytes <= MemoryLimitBytes && _logBytes <= LogLimitBytes,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
                };
                if (_activeTasks >= MaxConcurrentTasks)
                    snapshot.ActiveLimits.Add("concurrent_tasks");
                if (_toolCalls.Count >= MaxToolCallsPerHour)
                    snapshot.ActiveLimits.Add("hourly_tool_calls");
                if (_memoryBytes > MemoryLimitBytes)
                    snapshot.ActiveLimits.Add("memory_storage");
                if (_logBytes > LogLimitBytes)
                    snapshot.ActiveLimits.Add("log_storage");
                return snapshot;
            }
        }

        static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
        }

        static int Positive(int value, int fallback)
        {
            return value > 0 ? value : fallback;
        }

        void RaiseRetry(string subject, string phase)
        {
            Action<string, string> handler = RetryObserved;
            if (handler == null) return;
            try { handler(subject ?? "model", phase ?? ""); } catch { }
        }
    }
}
