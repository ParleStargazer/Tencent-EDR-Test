namespace WmiActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        WmiPlan? plan = null;
        WmiSnapshot? before = null;
        WmiSnapshot? after = null;
        DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow;
        string? filterPath = null;
        string? consumerPath = null;
        string? bindingPath = null;
        var gateObserved = false;
        try
        {
            var options = ArgumentReader.Parse(args);
            var capabilityId = options.Require("capability-id");
            var nonce = options.Require("nonce");
            var workDir = Path.GetFullPath(options.Require("work-dir"));
            var readyPath = Path.GetFullPath(options.Require("ready"));
            var gatePath = Path.GetFullPath(options.Require("gate"));
            resultPath = Path.GetFullPath(options.Require("result"));
            var timeoutMs = options.GetInt("timeout-ms", 60_000, 1_000, 180_000);
            var holdMs = options.GetInt("hold-ms", 1_000, 0, 30_000);
            Directory.CreateDirectory(workDir);
            plan = WmiPlans.Create(capabilityId, nonce, workDir);

            before = WmiRepository.CaptureTarget(plan);
            if (before.Exists) throw new InvalidOperationException($"创建前已存在本轮目标 WMI 对象：{before.ObjectPath}");
            var created = WmiRepository.Create(plan);
            occurredAtUtc = created.OccurredAtUtc;
            filterPath = created.FilterPath;
            consumerPath = created.ConsumerPath;
            bindingPath = created.BindingPath;
            after = WmiRepository.CaptureTarget(plan);
            if (!WmiRepository.MatchesPlan(plan, after)) throw new InvalidDataException("Actor 创建后的 WMI 对象与计划不一致。");

            ProtocolJson.WriteAtomic(readyPath, new WmiReady
            {
                CapabilityId = plan.CapabilityId,
                Operation = plan.Operation,
                ActorProcessId = Environment.ProcessId,
                OccurredAtUtc = occurredAtUtc,
                Before = before,
                After = after,
                FilterPath = filterPath,
                ConsumerPath = consumerPath,
                BindingPath = bindingPath,
            });

            var gate = WaitForGate(gatePath, timeoutMs);
            if (!string.Equals(gate.CapabilityId, plan.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(gate.Operation, plan.Operation, StringComparison.Ordinal))
                throw new InvalidDataException("Controller 验证闸门与当前 WMI 能力不一致。");
            gateObserved = true;
            var actorVerified = WmiRepository.MatchesPlan(plan, WmiRepository.CaptureTarget(plan));
            if (!actorVerified) throw new InvalidDataException("Controller 放行后 Actor 无法重新查询到目标 WMI 对象。");
            if (holdMs > 0) Thread.Sleep(holdMs);

            var cleanup = WmiRepository.Cleanup(plan);
            var final = WmiRepository.CaptureTarget(plan);
            var succeeded = gateObserved && actorVerified && cleanup.Succeeded && !final.Exists;
            ProtocolJson.WriteAtomic(resultPath, new WmiBehaviorResult
            {
                CapabilityId = plan.CapabilityId,
                Operation = plan.Operation,
                ActorProcessId = Environment.ProcessId,
                OccurredAtUtc = occurredAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ControllerGateObserved = gateObserved,
                ActorVerificationSucceeded = actorVerified,
                CleanupSucceeded = cleanup.Succeeded,
                CleanupOrder = cleanup.Order,
                Before = before,
                After = after,
                Final = final,
                FilterPath = filterPath,
                ConsumerPath = consumerPath,
                BindingPath = bindingPath,
                Succeeded = succeeded,
                HResult = null,
                Error = cleanup.Errors.Count == 0 ? null : string.Join(" | ", cleanup.Errors),
            });
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            WmiCleanupResult? cleanup = null;
            if (plan is not null)
            {
                try { cleanup = WmiRepository.Cleanup(plan); }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            if (resultPath is not null && plan is not null)
            {
                WmiSnapshot final;
                try { final = WmiRepository.CaptureTarget(plan); }
                catch { final = new WmiSnapshot { Exists = true, ObjectClass = plan.ObjectClass }; }
                ProtocolJson.WriteAtomic(resultPath, new WmiBehaviorResult
                {
                    CapabilityId = plan.CapabilityId,
                    Operation = plan.Operation,
                    ActorProcessId = Environment.ProcessId,
                    OccurredAtUtc = occurredAtUtc,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ControllerGateObserved = gateObserved,
                    ActorVerificationSucceeded = false,
                    CleanupSucceeded = cleanup?.Succeeded == true,
                    CleanupOrder = cleanup?.Order ?? [],
                    Before = before ?? new WmiSnapshot { Exists = false, ObjectClass = plan.ObjectClass },
                    After = after ?? new WmiSnapshot { Exists = false, ObjectClass = plan.ObjectClass },
                    Final = final,
                    FilterPath = filterPath,
                    ConsumerPath = consumerPath,
                    BindingPath = bindingPath,
                    Succeeded = false,
                    HResult = exception.HResult,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static WmiVerificationGate WaitForGate(string path, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待 Controller WMI 独立验证超时。");
            Thread.Sleep(5);
        }
        return ProtocolJson.Read<WmiVerificationGate>(path);
    }
}
