using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
            using var auditScope = IsSecurityAuditMethod(method) ? ScheduledTaskAuditPolicyScope.EnableSuccess() : null;
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var client = Execute(method, operation, taskPath, definitionPath, actionArguments);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var after = ScheduledTaskClient.Snapshot(taskPath);
            var securityEventId = SecurityEventId(operation);
            var diagnostic = UsesSecurityEventDiagnostic(method)
                ? CollectEventLogDiagnostic(taskPath, securityEventId, occurredAtUtc)
                : new DiagnosticResult(null, null, null, null);
            auditScope?.Restore();
            var succeeded = Verify(method, operation, marker, actionArguments, before, after)
                && (!IsSecurityAuditMethod(method) || diagnostic.SecurityEventFound == true)
                && (auditScope is null || auditScope.RestoreSucceeded);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Method = method, Operation = operation, Succeeded = succeeded, OccurredAtUtc = occurredAtUtc,
                CompletedAtUtc = completedAtUtc,
                TaskPath = taskPath, ExpectedMarker = marker, Before = before, After = after,
                ClientProcessId = client?.ProcessId, ClientExecutable = client?.Executable,
                ClientCommandLine = client?.CommandLine, ClientStartedAtUtc = client?.StartedAtUtc,
                ClientEndedAtUtc = client?.EndedAtUtc, ClientExitCode = client?.ExitCode,
                ClientStandardOutput = client?.StandardOutput, ClientStandardError = client?.StandardError,
                SecurityEventId = UsesSecurityEventDiagnostic(method) ? securityEventId : null,
                SecurityEventFound = diagnostic.SecurityEventFound,
                SecurityEventOccurredAtUtc = diagnostic.SecurityEventOccurredAtUtc,
                SecurityEventRecordId = diagnostic.SecurityEventRecordId,
                SecurityEvent4698Found = operation == "create" ? diagnostic.SecurityEventFound : null,
                AuditSubcategoryId = auditScope is null ? null : ScheduledTaskAuditPolicyScope.OtherObjectAccessEvents.ToString("B").ToUpperInvariant(),
                AuditPolicyBefore = auditScope?.Before,
                AuditPolicyActive = auditScope?.Active,
                AuditSuccessEnabled = auditScope?.SuccessEnabled,
                AuditPolicyRestored = auditScope?.Restored,
                AuditPolicyChanged = auditScope?.Changed,
                AuditPolicyRestoreSucceeded = auditScope?.RestoreSucceeded,
                AuditPolicyOutput = diagnostic.AuditPolicyOutput,
                SecurityEventQueryOutput = diagnostic.SecurityEventQueryOutput,
                DiagnosticError = diagnostic.Error,
                HResult = 0, Error = succeeded ? null : IsSecurityAuditMethod(method) && diagnostic.SecurityEventFound != true
                    ? $"计划任务行为已执行，但 Security 日志中未找到本轮任务路径对应的 {securityEventId} 事件。"
                    : "计划任务操作后的状态或审核策略恢复结果未满足预期。",
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
            return operation switch
            {
                "create" => RunSchtasksCreate(taskPath, Path.Combine(Environment.SystemDirectory, "cmd.exe"), actionArguments),
                "modify" => RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    ["/Change", "/TN", taskPath, "/ENABLE"], 15_000, requireSuccess: true),
                "delete" => RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    ["/Delete", "/TN", taskPath, "/F"], 15_000, requireSuccess: true),
                _ => throw new ArgumentException($"schtasks.exe 子测试不支持的计划任务操作：{operation}"),
            };
        }
        if (IsSecurityAuditMethod(method))
        {
            switch (operation)
            {
                case "create":
                case "modify":
                    return RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                        ["/Create", "/TN", taskPath, "/XML", definitionPath, "/F"],
                        15_000, requireSuccess: true);
                case "delete":
                    return RunCommand(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                        ["/Delete", "/TN", taskPath, "/F"], 15_000, requireSuccess: true);
                default:
                    throw new ArgumentException($"Windows 安全审计子测试不支持的计划任务操作：{operation}");
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
        "create" => !before.Exists && after.Exists
            && after.Enabled == (method == "schtasks_cli" || IsSecurityAuditMethod(method))
            && (method == "schtasks_cli" || IsSecurityAuditMethod(method)
                ? string.Equals(after.ActionArguments, actionArguments, StringComparison.OrdinalIgnoreCase)
                : after.Marker == marker)
            && (!IsSecurityAuditMethod(method) || after.Marker == marker)
            && (method != "schtasks_cli" && !IsSecurityAuditMethod(method)
                || after.Triggers?.Contains("TimeTrigger", StringComparer.Ordinal) == true),
        "modify" => before.Exists && after.Exists && before.XmlSha256 != after.XmlSha256
            && (method == "schtasks_cli"
                ? before.Enabled == false && after.Enabled == true
                    && string.Equals(after.ActionArguments, before.ActionArguments, StringComparison.OrdinalIgnoreCase)
                : IsSecurityAuditMethod(method)
                ? before.Enabled == false && after.Enabled == true
                    && after.Marker == marker
                    && string.Equals(after.ActionArguments, actionArguments, StringComparison.OrdinalIgnoreCase)
                    && after.Triggers?.Contains("TimeTrigger", StringComparer.Ordinal) == true
                : before.Marker != after.Marker && after.Marker == marker),
        "delete" => before.Exists && !after.Exists,
        _ => false,
    };

    private static bool IsSecurityAuditMethod(string method) =>
        method is "security_audit_create" or "security_audit_update" or "security_audit_delete";

    private static bool UsesSecurityEventDiagnostic(string method) =>
        method == "schtasks_cli" || IsSecurityAuditMethod(method);

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

    private static DiagnosticResult CollectEventLogDiagnostic(string taskPath, int securityEventId, DateTimeOffset operationStartedAtUtc)
    {
        var errors = new List<string>();
        string? auditOutput = null;
        string? securityOutput = null;
        bool? found = null;
        DateTimeOffset? eventOccurredAtUtc = null;
        long? eventRecordId = null;
        try
        {
            var audit = RunCommand(Path.Combine(Environment.SystemDirectory, "auditpol.exe"),
                ["/get", "/subcategory:{0CCE9227-69AE-11D9-BED3-505054503030}", "/r"],
                10_000, requireSuccess: false);
            auditOutput = JoinOutput(audit);
            if (audit.ExitCode != 0) errors.Add($"auditpol 退出码 {audit.ExitCode}");
        }
        catch (Exception exception) { errors.Add($"auditpol：{exception.Message}"); }
        for (var attempt = 0; attempt < 20 && found != true; attempt++)
        {
            try
            {
                if (attempt > 0) Thread.Sleep(100);
                var query = $"*[System[(EventID={securityEventId}) and TimeCreated[timediff(@SystemTime) <= 20000]]]";
                var security = RunCommand(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                    ["qe", "Security", $"/q:{query}", "/f:xml", "/c:80", "/rd:true"], 10_000, requireSuccess: false);
                securityOutput = JoinOutput(security);
                if (security.ExitCode != 0)
                {
                    errors.Add($"wevtutil Security 查询退出码 {security.ExitCode}");
                    break;
                }
                var evidence = FindSecurityEvent(securityOutput, taskPath, securityEventId, operationStartedAtUtc);
                found = evidence is not null;
                if (evidence is not null)
                {
                    eventOccurredAtUtc = evidence.OccurredAtUtc;
                    eventRecordId = evidence.RecordId;
                    securityOutput = evidence.RawXml;
                }
            }
            catch (Exception exception)
            {
                errors.Add($"wevtutil：{exception.Message}");
                break;
            }
        }
        return new DiagnosticResult(found, auditOutput, securityOutput, errors.Count == 0 ? null : string.Join(" | ", errors),
            eventOccurredAtUtc, eventRecordId);
    }

    private static SecurityEventEvidence? FindSecurityEvent(string output, string taskPath, int eventId,
        DateTimeOffset operationStartedAtUtc)
    {
        foreach (Match match in Regex.Matches(output, @"<Event\b[\s\S]*?</Event>", RegexOptions.CultureInvariant))
        {
            XDocument document;
            try { document = XDocument.Parse(match.Value, LoadOptions.PreserveWhitespace); }
            catch { continue; }
            var eventIdText = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "EventID")?.Value;
            if (!int.TryParse(eventIdText, out var parsedEventId) || parsedEventId != eventId) continue;
            var taskName = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Data" && string.Equals((string?)element.Attribute("Name"), "TaskName", StringComparison.Ordinal))?.Value;
            if (!string.Equals(taskName, taskPath, StringComparison.OrdinalIgnoreCase)
                && !match.Value.Contains(taskPath, StringComparison.OrdinalIgnoreCase)) continue;
            var systemTime = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTimeOffset.TryParse(systemTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var occurredAtUtc)) continue;
            occurredAtUtc = occurredAtUtc.ToUniversalTime();
            if (occurredAtUtc < operationStartedAtUtc.AddSeconds(-2)) continue;
            var recordText = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "EventRecordID")?.Value;
            _ = long.TryParse(recordText, out var recordId);
            return new SecurityEventEvidence(occurredAtUtc, recordId == 0 ? null : recordId, match.Value);
        }
        return null;
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
        string? SecurityEventQueryOutput, string? Error, DateTimeOffset? SecurityEventOccurredAtUtc = null,
        long? SecurityEventRecordId = null);

    private sealed record SecurityEventEvidence(DateTimeOffset OccurredAtUtc, long? RecordId, string RawXml);
}
