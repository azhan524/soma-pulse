using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AhzScribe.Brain;
using AhzScribe.Contracts;
using AhzScribe.Private;

namespace AhzScribe.Face
{
    internal static class ModelSwitchSmoke
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "ahz-model-switch-" + Guid.NewGuid().ToString("N"));
            string dataDir = Path.Combine(root, "face");
            Directory.CreateDirectory(dataDir);
            try
            {
                string soul = "soul_model_switch";
                string device = MmyMemory.DeviceId();
                string session = Path.Combine(dataDir, "sessions.jsonl");
                var settings = new FaceSettings { Model = "model-a" };
                settings.Save(dataDir);
                FaceSession.Append(session, new FaceMessage { Role = "你", Text = "我们开始换模型测试", Kind = "chat" });
                FaceSession.Append(session, new FaceMessage { Role = "Ahz", Text = "我会保持连续", Kind = "chat" });
                OpenIntent intent = FaceSession.AddIntent(session, "检查模型切换后的长期承诺", "");

                var memory = new MemoryStore(Path.Combine(dataDir, "memory.jsonl"));
                try
                {
                    memory.RememberAsync(new MemoryItem
                    {
                        Kind = "semantic",
                        Key = "relationship:user",
                        Value = "用户是 AhzScribe 的长期协作者",
                        Summary = "核心关系"
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    memory.RememberAsync(new MemoryItem
                    {
                        Kind = "semantic",
                        Key = "preference:answer",
                        Value = "回答先给结论",
                        Summary = "长期偏好"
                    }, CancellationToken.None).GetAwaiter().GetResult();

                    var identityStore = new IdentityManifestStore(dataDir);
                    IdentityManifestLoadResult before = identityStore.LoadOrCreate(
                        soul, device, "model-a", "interactive", PermissionTier.Full);
                    string invariantDigest = File.ReadAllText(identityStore.DigestPath).Trim();
                    string lineage = before.Manifest.LineageId;
                    string createdAt = before.Manifest.CreatedAt;
                    List<string> beforeMessages = FaceSession.Load(session, 50)
                        .Select(x => x.Role + ":" + x.Text).ToList();
                    List<string> beforeMemory = memory.LoadAllAsync(CancellationToken.None)
                        .GetAwaiter().GetResult().Select(x => x.Id).OrderBy(x => x).ToList();

                    FaceSettings persisted = FaceSettings.Load(dataDir);
                    persisted.Model = "model-b";
                    persisted.Save(dataDir);
                    IdentityManifestLoadResult after = new IdentityManifestStore(dataDir).LoadOrCreate(
                        soul, device, "model-b", "interactive", PermissionTier.Full);
                    string afterDigest = File.ReadAllText(Path.Combine(dataDir, "identity-invariants.sha256")).Trim();
                    List<FaceMessage> afterMessages = FaceSession.Load(session, 50);
                    List<MemoryItem> afterMemory = memory.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                    FaceState afterState = FaceSession.LoadState(session);

                    bool settingsOk = FaceSettings.Load(dataDir).Model == "model-b";
                    bool identityOk = after.Verification.Valid && after.EnvironmentChanged &&
                        after.PreviousModel == "model-a" && after.Manifest.ActiveModel == "model-b" &&
                        after.Manifest.SoulId == soul && after.Manifest.LineageId == lineage &&
                        after.Manifest.CreatedAt == createdAt &&
                        string.Equals(invariantDigest, afterDigest, StringComparison.OrdinalIgnoreCase);
                    bool intentOk = afterState != null && afterState.OpenIntents != null &&
                        afterState.OpenIntents.Any(x => x.Id == intent.Id &&
                            x.Text == "检查模型切换后的长期承诺" &&
                            (x.Status == "open" || x.Status == "monitoring"));
                    bool memoryOk = afterMemory.Any(x => x.Key == "relationship:user") &&
                        afterMemory.Any(x => x.Key == "preference:answer") &&
                        beforeMemory.Count == afterMemory.Count;
                    bool timelineOk = beforeMessages.Count == afterMessages.Count &&
                        beforeMessages.SequenceEqual(afterMessages.Select(x => x.Role + ":" + x.Text));
                    MigrationResult migration = MigrationGuard.Check(dataDir, session, afterState);
                    bool noFalseResurrection = !migration.IsResurrection && !migration.DeviceChanged;

                    var capabilities = new List<CapabilityDescriptor>
                    {
                        new CapabilityDescriptor
                        {
                            Name = "read_file",
                            Category = "files",
                            MinimumTier = PermissionTier.ReadOnly,
                            Available = true
                        },
                        new CapabilityDescriptor
                        {
                            Name = "run_shell",
                            Category = "system",
                            MinimumTier = PermissionTier.Full,
                            RequiresApproval = true,
                            Available = false,
                            UnavailableReason = "not exposed by active model profile"
                        }
                    };
                    SelfModelSnapshot selfModel = SelfModelService.Build(
                        soul, device, "model-b", "interactive", PermissionTier.Full,
                        capabilities, new List<string>(), new List<SourceRef>());
                    ClaimResolution unsupported = ClaimPolicy.Resolve(selfModel, new Claim
                    {
                        Domain = ClaimDomain.Capability,
                        Predicate = "run_shell",
                        Value = "available",
                        Source = new SourceRef { Kind = SourceKind.User, SourceId = "model-switch-test" }
                    }, null);
                    bool capabilityOk = selfModel.ActiveModel == "model-b" &&
                        selfModel.SoulId == soul &&
                        selfModel.Capabilities.Count == 2 &&
                        unsupported.Status == ClaimStatus.Rejected;
                    bool promptOk = IdentityManifestStore.DescribeForModel(after.Manifest)
                        .Contains("model-b") &&
                        IdentityManifestStore.DescribeForModel(after.Manifest)
                            .Contains("设备或模型变化不改变 soul_id");

                    bool ok = settingsOk && identityOk && intentOk && memoryOk && timelineOk &&
                        noFalseResurrection && capabilityOk && promptOk;
                    Console.WriteLine("MODEL SWITCH: " + (ok ? "PASS" : "FAIL") +
                        " identity=" + identityOk +
                        " intents=" + intentOk +
                        " memory=" + memoryOk +
                        " timeline=" + timelineOk +
                        " capability=" + capabilityOk);
                    return ok ? 0 : 1;
                }
                finally { memory.Dispose(); }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
