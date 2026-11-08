using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class ResourceBudgetSmoke
{
    sealed class EmptyMemory : IMemoryStore
    {
        public Task<string> RememberAsync(MemoryItem item, CancellationToken ct) { return Task.FromResult(""); }
        public Task<List<MemoryItem>> RetrieveAsync(string query, int topK, CancellationToken ct)
        {
            return Task.FromResult(new List<MemoryItem>());
        }
        public Task<List<MemoryItem>> LoadAllAsync(CancellationToken ct)
        {
            return Task.FromResult(new List<MemoryItem>());
        }
    }

    sealed class FlakyModel : IBrainModel
    {
        public int Calls;
        readonly int _failures;
        readonly Exception _error;

        public FlakyModel(int failures, Exception error)
        {
            _failures = failures;
            _error = error;
        }

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (Calls <= _failures) throw _error;
            return Task.FromResult("ok");
        }
    }

    sealed class OneToolModel : IToolCallingModel
    {
        public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history,
            CancellationToken ct)
        {
            foreach (BrainTurnMessage message in history)
                if (message.Role == "tool")
                    return Task.FromResult(new BrainTurn { Text = message.Content, Done = true });
            return Task.FromResult(new BrainTurn
            {
                Calls = new List<BrainToolCall>
                {
                    new BrainToolCall
                    {
                        Id = "budget-call",
                        Tool = "read_file",
                        Tier = PermissionTier.ReadOnly,
                        Args = new Dictionary<string, object> { ["path"] = "a.txt" }
                    }
                }
            });
        }
    }

    sealed class FlakyToolModel : IToolCallingModel
    {
        public int Calls;

        public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1) throw new IOException("temporary tool-model outage");
            return Task.FromResult(new BrainTurn { Text = "tool-model-recovered", Done = true });
        }
    }

    sealed class CountingBody : IActionExecutor
    {
        public int Calls;
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ActionResult
            {
                RequestId = request.RequestId,
                Ok = true,
                Output = "ok",
                AuditId = "budget-audit-" + Calls
            });
        }
    }

    sealed class GateModel : IBrainModel
    {
        public readonly TaskCompletionSource<bool> Started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Release =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            Started.TrySetResult(true);
            using (ct.Register(delegate { Release.TrySetCanceled(ct); }))
                return await Release.Task.ConfigureAwait(false);
        }
    }

    public static bool Run()
    {
        var budget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);

        string reason;
        bool firstTask = budget.TryEnterTask(out reason);
        bool secondTaskBlocked = !budget.TryEnterTask(out reason);
        budget.ExitTask();
        bool taskRecovered = budget.TryEnterTask(out reason);
        budget.ExitTask();
        bool concurrencyOk = firstTask && secondTaskBlocked && taskRecovered;

        var hostBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        var gateModel = new GateModel();
        using (var host = new BrainHost(new EmptyMemory(), gateModel, new CountingBody()) { Budget = hostBudget })
        {
            Task<string> firstHostTask = host.AskAsync("first", CancellationToken.None);
            gateModel.Started.Task.GetAwaiter().GetResult();
            string secondHostAnswer = host.AskAsync("second", CancellationToken.None).GetAwaiter().GetResult();
            gateModel.Release.TrySetResult("first-complete");
            string firstHostAnswer = firstHostTask.GetAwaiter().GetResult();
            concurrencyOk = concurrencyOk && firstHostAnswer == "first-complete" &&
                secondHostAnswer.Contains("资源预算已满");
        }

        bool pendingOk = budget.TryQueuePending(7, out reason) &&
            !budget.TryQueuePending(8, out reason);

        var toolBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        int acceptedCalls = 0;
        for (int i = 0; i < 120; i++)
            if (toolBudget.TryConsumeToolCall(out reason)) acceptedCalls++;
        bool toolLimitOk = acceptedCalls == 120 && !toolBudget.TryConsumeToolCall(out reason);

        var hourlyBody = new CountingBody();
        var hourlyBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        for (int i = 0; i < 121; i++)
        {
            var runner = new ToolAgentRunner(new EmptyMemory(), new OneToolModel(), hourlyBody)
            {
                Budget = hourlyBudget
            };
            runner.RunAsync("tool " + i, CancellationToken.None).GetAwaiter().GetResult();
        }
        bool sharedToolLimitOk = hourlyBody.Calls == 120;

        var toolRetryBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        var flakyToolModel = new FlakyToolModel();
        string toolRetryAnswer = new ToolAgentRunner(new EmptyMemory(), flakyToolModel, new CountingBody())
        {
            Budget = toolRetryBudget
        }.RunAsync("retry tool model", CancellationToken.None).GetAwaiter().GetResult();
        bool toolRetryOk = toolRetryAnswer == "tool-model-recovered" && flakyToolModel.Calls == 2;

        var retryBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        int retryEvents = 0;
        retryBudget.RetryObserved += delegate { retryEvents++; };
        var retryModel = new FlakyModel(1, new IOException("temporary"));
        var retryRunner = new AgentRunner(new EmptyMemory(), retryModel) { Budget = retryBudget };
        string retryAnswer = retryRunner.RunAsync("retry", CancellationToken.None).GetAwaiter().GetResult();
        ResourceBudgetSnapshot retrySnapshot = retryBudget.Snapshot();
        bool retryOk = retryAnswer == "ok" && retryModel.Calls == 2 &&
            retrySnapshot.RetriesLastHour == 1 && retryEvents == 2;

        var permanentBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        var permanentModel = new FlakyModel(2, new TimeoutException("timeout"));
        bool bounded = false;
        try
        {
            new AgentRunner(new EmptyMemory(), permanentModel) { Budget = permanentBudget }
                .RunAsync("fail twice", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (TimeoutException) { bounded = permanentModel.Calls == 2; }

        var invalidBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        var invalidModel = new FlakyModel(1, new InvalidOperationException("bad json"));
        int invalidCalls = 0;
        try
        {
            new AgentRunner(new EmptyMemory(), invalidModel) { Budget = invalidBudget }
                .RunAsync("invalid", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { invalidCalls = invalidModel.Calls; }
        bool invalidOk = invalidCalls == 1;

        var cancelBudget = new ResourceBudget(1, 8, 120, 2, 1, 3, 1024, 2048);
        var cancelModel = new FlakyModel(2, new IOException("temporary"));
        int cancelCalls = 0;
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try
            {
                new AgentRunner(new EmptyMemory(), cancelModel) { Budget = cancelBudget }
                    .RunAsync("cancelled", cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelCalls = cancelModel.Calls; }
        }
        bool cancelOk = cancelCalls == 0;

        budget.UpdateStorage(1024, 2048);
        bool storageAtLimit = budget.CanRunLowPriorityPulse;
        budget.UpdateStorage(1025, 2048);
        bool memoryBlocked = !budget.CanRunLowPriorityPulse;
        bool highPriorityAllowed = budget.CanRunPulse(true);
        budget.UpdateStorage(1024, 2049);
        bool logBlocked = !budget.CanRunLowPriorityPulse;
        budget.UpdateStorage(512, 512);
        bool storageRecovered = budget.CanRunLowPriorityPulse;

        ResourceBudgetSnapshot snapshot = budget.Snapshot();
        bool limitsExposed = snapshot.MaxConcurrentTasks == 1 && snapshot.MaxPendingTasks == 8 &&
            snapshot.MaxToolCallsPerHour == 120 && snapshot.MaxModelAttempts == 2 &&
            snapshot.MaxWebViewFailures == 3;

        bool ok = concurrencyOk && pendingOk && toolLimitOk && sharedToolLimitOk && toolRetryOk && retryOk &&
            bounded && invalidOk && cancelOk && storageAtLimit && memoryBlocked && highPriorityAllowed && logBlocked &&
            storageRecovered && limitsExposed;
        Console.WriteLine("RESOURCE BUDGET: " + (ok ? "PASS" : "FAIL") +
            " tool_calls=" + hourlyBody.Calls + "/120");
        return ok;
    }
}
