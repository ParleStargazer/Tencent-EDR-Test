using System.Diagnostics;

namespace ScheduledTaskActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var method = "unknown";
        var operation = "unknown";
        var taskPath = "\\EdrTest_unallocated";
        var marker = "unallocated";
        TaskSnapshot before = ScheduledTaskClient.MissingSnapshot();
        try
        {
            var options = ArgumentReader.Parse(args);
            method = options.Require("method");
            operation = options.Require("operation");
            taskPath = options.Require("task-path");
            marker = options.Require("marker");
            var actionArguments = options.Require("action-arguments");
            resultPath = Path.GetFullPath(options.Require("result"));
            var definitionPath = Path.GetFullPath(options.Require("definition"));
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            before = ScheduledTaskClient.Snapshot(taskPath);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var client = Execute(method, operation, taskPath, definitionPath, actionArguments);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var after = ScheduledTaskClient.Snapshot(taskPath);
            var securityEventId = SecurityEventId(operation);
            var diagnostic = method == "schtasks_cli"
                ? CollectEventLogDiagnostic(taskPath, securityEventId)
                : new DiagnosticResult(null, null, null, null);
            var succeeded = Verify(method, operation, marker, actionArguments, before, after);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Method = method, Operation = operation, Succeeded = succeeded, OccurredAtUtc = occurredAtUtc,
                CompletedAtUtc = completedAtUtc,
                TaskPath = taskPath, ExpectedMarker = marker, Before = before, After = after,
                ClientProcessId = client?.ProcessId, ClientExecutable = client?.Executable,
                ClientCommandLine = client?.CommandLine, ClientStartedAtUtc = client?.StartedAtUtc,
                ClientEndedAtUtc = client?.EndedAtUtc, ClientExitCode = client?.ExitCode,
                ClientStandardOutput = client?.StandardOutput, ClientStandardError = client?.StandardError,
                SecurityEventId = method == "schtasks_cli" ? securityEventId : null,
                SecurityEventFound = diagnostic.SecurityEventFound,
                SecurityEvent4698Found = operation == "create" ? diagnostic.SecurityEventFound : null,
                AuditPolicyOutput = diagnostic.AuditPolicyOutput,
                SecurityEventQueryOutput = diagnostic.SecurityEventQueryOutput,
                DiagnosticError = diagnostic.Error,
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
                    Method = method, Operation = operation, Succeeded = false, OccurredAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    TaskPath = taskPath, ExpectedMarker = marker, Before = before, After = after,
                    HResult = exception.HResult, Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static CommandResult? Execute(string method, string operation, string taskPath, string definitionPath,
        string actionArguments)
    {
        if (method == "schtasks_cli")
        {
            var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            switch (operation)
            {
                case "create":
                    return RunSchtasksCreate(taskPath, command, actionArguments);
                case "modify":
                    return RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                        ["/Change", "/TN", taskPath, "/ENABLE"],
                        15_000, requireSuccess: true);
                case "delete":
                    return RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                        ["/Delete", "/TN", taskPath, "/F"], 15_000, requireSuccess: true);
                default:
                    throw new ArgumentException($"schtasks_cli 不支持的计划任务操作：{operation}");
            }
        }
        if (method != "task_scheduler_com") throw new ArgumentException($"不支持的计划任务测试方法：{method}");
        switch (operation)
        {
            case "create":
                ScheduledTaskClient.Register(taskPath, File.ReadAllText(definitionPath), update: false);
                return null;
            case "modify":
                ScheduledTaskClient.Register(taskPath, File.ReadAllText(definitionPath), update: true);
                return null;
            case "delete":
                ScheduledTaskClient.Delete(taskPath);
                return null;
            default:
                throw new ArgumentException($"不支持的计划任务操作：{operation}");
        }
    }

    private static bool Verify(string method, string operation, string marker, string actionArguments,
        TaskSnapshot before, TaskSnapshot after) => operation switch
    {
        "create" => !before.Exists && after.Exists && after.Enabled == (method == "schtasks_cli")
            && (method == "schtasks_cli"
                ? string.Equals(after.ActionArguments, actionArguments, StringComparison.OrdinalIgnoreCase)
                : after.Marker == marker)
            && (method != "schtasks_cli" || after.Triggers?.Contains("TimeTrigger", StringComparer.Ordinal) == true),
        "modify" => before.Exists && after.Exists && before.XmlSha256 != after.XmlSha256
            && (method == "schtasks_cli"
                ? before.Enabled == false && after.Enabled == true
                    && string.Equals(after.ActionArguments, before.ActionArguments, StringComparison.OrdinalIgnoreCase)
                : before.Marker != after.Marker && after.Marker == marker),
        "delete" => before.Exists && !after.Exists,
        _ => false,
    };

    private static CommandResult RunSchtasksCreate(string taskPath, string command, string actionArguments)
    {
        var future = DateTime.Now.AddYears(1).AddMinutes(1);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var dateCandidates = new[]
        {
            future.ToString(culture.DateTimeFormat.ShortDatePattern, culture),
            future.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture),
            future.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture),
            future.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture),
        }.Distinct(StringComparer.Ordinal).ToArray();
        CommandResult? last = null;
        foreach (var startDate in dateCandidates)
        {
            last = RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                ["/Create", "/TN", taskPath, "/SC", "ONCE", "/SD", startDate,
                    "/ST", future.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    "/TR", $"{command} {actionArguments}", "/RL", "LIMITED", "/F"],
                15_000, requireSuccess: false);
            if (last.ExitCode == 0) return last;
        }
        throw new InvalidOperationException($"schtasks.exe 无法使用本机或兼容日期格式创建任务：{JoinOutput(last!)}");
    }

    private static int SecurityEventId(string operation) => operation switch
    {
        "create" => 4698,
        "modify" => 4702,
        "delete" => 4699,
        _ => throw new ArgumentException($"没有为计划任务操作 {operation} 定义安全事件 ID。"),
    };

    private static DiagnosticResult CollectEventLogDiagnostic(string taskPath, int securityEventId)
    {
        var errors = new List<string>();
        string? auditOutput = null;
        string? securityOutput = null;
        bool? found = null;
        try
        {
            var audit = RunCommand(Path.Combine(Environment.SystemDirectory, "auditpol.exe"),
                ["/get", "/subcategory:{0CCE9227-69AE-11D9-BED3-505054503030}", "/r"],
                10_000, requireSuccess: false);
            auditOutput = JoinOutput(audit);
            if (audit.ExitCode != 0) errors.Add($"auditpol 退出码 {audit.ExitCode}");
        }
        catch (Exception exception) { errors.Add($"auditpol：{exception.Message}"); }
        try
        {
            Thread.Sleep(250);
            var query = $"*[System[(EventID={securityEventId}) and TimeCreated[timediff(@SystemTime) <= 15000]]]";
            var security = RunCommand(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                ["qe", "Security", $"/q:{query}", "/f:xml", "/c:30", "/rd:true"], 10_000, requireSuccess: false);
            securityOutput = JoinOutput(security);
            found = security.ExitCode == 0 && securityOutput.Contains(taskPath, StringComparison.OrdinalIgnoreCase);
            if (security.ExitCode != 0) errors.Add($"wevtutil Security 查询退出码 {security.ExitCode}");
        }
        catch (Exception exception) { errors.Add($"wevtutil：{exception.Message}"); }
        return new DiagnosticResult(found, auditOutput, securityOutput, errors.Count == 0 ? null : string.Join(" | ", errors));
    }

    private static CommandResult RunCommand(string executable, IReadOnlyList<string> arguments, int timeoutMs, bool requireSuccess)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动系统程序：{executable}");
        var started = SafeStartTime(process);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待系统程序退出超时：{executable}");
        }
        Task.WaitAll(outputTask, errorTask);
        var result = new CommandResult(process.Id, executable, FormatCommandLine(executable, arguments), started,
            SafeExitTime(process), process.ExitCode, outputTask.Result, errorTask.Result);
        if (requireSuccess && result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} 退出码 {result.ExitCode}：{JoinOutput(result)}");
        return result;
    }

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { return DateTimeOffset.UtcNow; }
    }

    private static DateTimeOffset SafeExitTime(Process process)
    {
        try { return process.ExitTime.ToUniversalTime(); }
        catch (InvalidOperationException) { return DateTimeOffset.UtcNow; }
    }

    private static string JoinOutput(CommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value));

    private static TaskSnapshot SafeSnapshot(string taskPath)
    {
        try { return ScheduledTaskClient.Snapshot(taskPath); }
        catch { return ScheduledTaskClient.MissingSnapshot(); }
    }

    private sealed record CommandResult(int ProcessId, string Executable, string CommandLine,
        DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, int ExitCode, string StandardOutput, string StandardError);

    private sealed record DiagnosticResult(bool? SecurityEventFound, string? AuditPolicyOutput,
        string? SecurityEventQueryOutput, string? Error);
}
