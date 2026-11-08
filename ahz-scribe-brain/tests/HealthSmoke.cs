using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AhzScribe.Brain;
using AhzScribe.Contracts;

internal static class HealthSmoke
{
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
                AuditId = "health-audit-" + Calls
            });
        }
    }

    sealed class ToolModel : IToolCallingModel
    {
        readonly string _tool;
        readonly PermissionTier _tier;

        public ToolModel(string tool, PermissionTier tier)
        {
            _tool = tool;
            _tier = tier;
        }

        public Task<BrainTurn> NextAsync(string system, IReadOnlyList<BrainTurnMessage> history,
            CancellationToken ct)
        {
            foreach (BrainTurnMessage message in history)
                if (message.Role == "tool")
                    return Task.FromResult(new BrainTurn { Text = "tool result: " + message.Content, Done = true });
            return Task.FromResult(new BrainTurn
            {
                Calls = new List<BrainToolCall>
                {
                    new BrainToolCall
                    {
                        Id = "health-call",
                        Tool = _tool,
                        Tier = _tier,
                        Args = new Dictionary<string, object> { ["path"] = "health.txt" }
                    }
                }
            });
        }
    }

    sealed class FailingModel : IBrainModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            throw new IOException("forced model outage");
        }
    }

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

    public static bool Run()
    {
        var health = new HealthManager("model", "body", "memory", "audit", "webview");
        int transitions = 0;
        health.Changed += delegate(HealthSnapshot snapshot, HealthTransition transition)
        {
            if (transition != null) transitions++;
        };
        health.ReportComponent("model", true);
        health.ReportComponent("body", true);
        health.ReportComponent("memory", true);
        health.ReportComponent("audit", true);
        health.ReportComponent("webview", true);
        bool healthy = health.Snapshot().State == HealthState.Healthy &&
            health.AllowsTool("write_file", PermissionTier.WorkspaceWrite);

        health.ReportComponent("body", false, "pipe disconnected");
        var blockedBody = new CountingBody();
        var blockedRunner = new ToolAgentRunner(new EmptyMemory(),
            new ToolModel("read_file", PermissionTier.ReadOnly), blockedBody) { Health = health };
        blockedRunner.RunAsync("blocked", CancellationToken.None).GetAwaiter().GetResult();
        bool bodyBlocked = health.Snapshot().State == HealthState.Degraded &&
            !health.CanUseToolCalls && blockedBody.Calls == 0;

        health.ReportComponent("body", true, "");
        bool bodyRecovered = health.Snapshot().State == HealthState.Healthy;

        health.ReportComponent("webview", false, "renderer crashed");
        var webBody = new CountingBody();
        var webRunner = new ToolAgentRunner(new EmptyMemory(),
            new ToolModel("read_file", PermissionTier.ReadOnly), webBody) { Health = health };
        webRunner.RunAsync("webview degraded", CancellationToken.None).GetAwaiter().GetResult();
        bool webviewDegraded = health.Snapshot().State == HealthState.Degraded && webBody.Calls == 1;
        health.ReportComponent("webview", true, "");

        health.ReportComponent("audit", false, "audit file is read-only");
        var readBody = new CountingBody();
        var readRunner = new ToolAgentRunner(new EmptyMemory(),
            new ToolModel("read_file", PermissionTier.ReadOnly), readBody) { Health = health };
        readRunner.RunAsync("read allowed", CancellationToken.None).GetAwaiter().GetResult();
        var writeBody = new CountingBody();
        var writeRunner = new ToolAgentRunner(new EmptyMemory(),
            new ToolModel("write_file", PermissionTier.WorkspaceWrite), writeBody) { Health = health };
        writeRunner.RunAsync("write blocked", CancellationToken.None).GetAwaiter().GetResult();
        bool auditReadOnly = health.Snapshot().State == HealthState.ReadOnly &&
            readBody.Calls == 1 && writeBody.Calls == 0 && !health.CanWrite;

        health.ReportComponent("audit", true, "");
        var restoredBody = new CountingBody();
        var restoredRunner = new ToolAgentRunner(new EmptyMemory(),
            new ToolModel("write_file", PermissionTier.WorkspaceWrite), restoredBody) { Health = health };
        restoredRunner.RunAsync("write restored", CancellationToken.None).GetAwaiter().GetResult();
        bool writeRecovered = health.Snapshot().State == HealthState.Healthy && restoredBody.Calls == 1;

        health.ReportComponent("memory", false, "memory parse failed");
        bool memoryReadOnly = health.Snapshot().State == HealthState.ReadOnly;
        health.ReportComponent("memory", true, "");

        var hostHealth = new HealthManager("model", "body", "memory", "audit", "webview");
        hostHealth.ReportComponent("body", true);
        hostHealth.ReportComponent("memory", true);
        hostHealth.ReportComponent("audit", true);
        hostHealth.ReportComponent("webview", true);
        bool modelFailed = false;
        bool chatBlocked = false;
        using (var host = new BrainHost(new EmptyMemory(), new FailingModel(), new CountingBody()))
        {
            host.Health = hostHealth;
            try { host.AskAsync("fail", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { modelFailed = true; }
            hostHealth.SetStopped(true, "test stopped guard");
            string stoppedReply = host.AskAsync("must not call model", CancellationToken.None)
                .GetAwaiter().GetResult();
            chatBlocked = stoppedReply.Contains("系统已停止");
            hostHealth.SetStopped(false, "test stopped guard clear");
        }
        bool modelDegraded = modelFailed && hostHealth.Snapshot().State == HealthState.Degraded;
        hostHealth.ReportComponent("model", true, "");
        bool modelRecovered = hostHealth.Snapshot().State == HealthState.Healthy;

        health.SetStopped(true, "test emergency stop");
        bool stopped = health.Snapshot().State == HealthState.Stopped && !health.CanChat;
        health.SetStopped(false, "test resume");
        bool resumed = health.Snapshot().State == HealthState.Healthy;
        bool transitionsOk = transitions >= 10 && !string.IsNullOrEmpty(health.Snapshot().LastTransition);

        bool ok = healthy && bodyBlocked && bodyRecovered && webviewDegraded && auditReadOnly &&
            writeRecovered && memoryReadOnly && modelDegraded && modelRecovered && chatBlocked &&
            stopped && resumed &&
            transitionsOk;
        Console.WriteLine("HEALTH STATE: " + (ok ? "PASS" : "FAIL") +
            " transitions=" + transitions);
        return ok;
    }
}
