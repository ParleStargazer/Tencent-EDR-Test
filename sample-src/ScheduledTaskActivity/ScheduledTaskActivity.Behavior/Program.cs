namespace ScheduledTaskActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var operation = "unknown";
        var taskPath = "\\EdrTest_unallocated";
        var marker = "unallocated";
        TaskSnapshot before = ScheduledTaskClient.MissingSnapshot();
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation");
            taskPath = options.Require("task-path");
            marker = options.Require("marker");
            resultPath = Path.GetFullPath(options.Require("result"));
            var definitionPath = Path.GetFullPath(options.Require("definition"));
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            before = ScheduledTaskClient.Snapshot(taskPath);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            Execute(operation, taskPath, definitionPath);
            var after = ScheduledTaskClient.Snapshot(taskPath);
            var succeeded = Verify(operation, marker, before, after);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Operation = operation, Succeeded = succeeded, OccurredAtUtc = occurredAtUtc,
                TaskPath = taskPath, ExpectedMarker = marker, Before = before, After = after,
                HResult = 0, Error = succeeded ? null : "计划任务操作后的状态未满足预期。",
            });
            if (holdMs > 0) Thread.Sleep(holdMs);
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                var after = SafeSnapshot(taskPath);
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = operation, Succeeded = false, OccurredAtUtc = DateTimeOffset.UtcNow,
                    TaskPath = taskPath, ExpectedMarker = marker, Before = before, After = after,
                    HResult = exception.HResult, Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static void Execute(string operation, string taskPath, string definitionPath)
    {
        switch (operation)
        {
            case "create":
                ScheduledTaskClient.Register(taskPath, File.ReadAllText(definitionPath), update: false);
                break;
            case "modify":
                ScheduledTaskClient.Register(taskPath, File.ReadAllText(definitionPath), update: true);
                break;
            case "delete":
                ScheduledTaskClient.Delete(taskPath);
                break;
            default:
                throw new ArgumentException($"不支持的计划任务操作：{operation}");
        }
    }

    private static bool Verify(string operation, string marker, TaskSnapshot before, TaskSnapshot after) => operation switch
    {
        "create" => !before.Exists && after.Exists && after.Enabled == false && after.Marker == marker,
        "modify" => before.Exists && after.Exists && before.XmlSha256 != after.XmlSha256
            && before.Marker != after.Marker && after.Marker == marker && after.Enabled == false,
        "delete" => before.Exists && !after.Exists,
        _ => false,
    };

    private static TaskSnapshot SafeSnapshot(string taskPath)
    {
        try { return ScheduledTaskClient.Snapshot(taskPath); }
        catch { return ScheduledTaskClient.MissingSnapshot(); }
    }
}
