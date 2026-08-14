using System.ComponentModel;
using System.Diagnostics;

namespace ServiceActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var operation = "unknown";
        var serviceName = "EdrTestSvc_unallocated";
        var displayName = "EDRTEST|unallocated";
        var binaryPath = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c exit 0";
        var before = ServiceClient.MissingSnapshot();
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation");
            serviceName = options.Require("service-name");
            displayName = options.Require("display-name");
            binaryPath = options.Require("binary-path");
            resultPath = Path.GetFullPath(options.Require("result"));
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            ServiceClient.ValidateServiceName(serviceName);
            ServiceClient.ValidateDisplayName(displayName);
            ServiceClient.ValidateTestBinaryPath(binaryPath);

            before = ServiceClient.Snapshot(serviceName);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            Execute(operation, serviceName, displayName, binaryPath);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var after = ServiceClient.Snapshot(serviceName);
            var systemEventId = SystemEventId(operation);
            var diagnostic = systemEventId is null
                ? new DiagnosticResult(null, null, null)
                : CollectSystemEventDiagnostic(serviceName, systemEventId.Value);
            var succeeded = Verify(operation, displayName, binaryPath, before, after);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Operation = operation, Succeeded = succeeded, OccurredAtUtc = occurredAtUtc,
                CompletedAtUtc = completedAtUtc, ServiceName = serviceName, ExpectedDisplayName = displayName,
                ExpectedBinaryPath = binaryPath, NativeApi = NativeApi(operation), Before = before, After = after,
                SystemEventId = systemEventId, SystemEventFound = diagnostic.Found,
                SystemEventQueryOutput = diagnostic.Output, DiagnosticError = diagnostic.Error,
                Win32Error = 0, Error = succeeded ? null : "服务操作后的 SCM 状态未满足预期。",
            });
            if (holdMs > 0) Thread.Sleep(holdMs);
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                var after = SafeSnapshot(serviceName);
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = operation, Succeeded = false, OccurredAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow, ServiceName = serviceName,
                    ExpectedDisplayName = displayName, ExpectedBinaryPath = binaryPath,
                    NativeApi = SafeNativeApi(operation), Before = before, After = after,
                    Win32Error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static void Execute(string operation, string serviceName, string displayName, string binaryPath)
    {
        switch (operation)
        {
            case "create":
                ServiceClient.Create(serviceName, displayName, binaryPath, ServiceClient.DisabledStart);
                break;
            case "modify":
                ServiceClient.Modify(serviceName, displayName, binaryPath, ServiceClient.DisabledStart);
                break;
            case "delete":
                ServiceClient.Delete(serviceName);
                ServiceClient.WaitUntilMissing(serviceName);
                break;
            default:
                throw new ArgumentException($"不支持的服务操作：{operation}");
        }
    }

    private static bool Verify(string operation, string displayName, string binaryPath,
        ServiceSnapshot before, ServiceSnapshot after) => operation switch
    {
        "create" => !before.Exists && after.Exists && after.DisplayName == displayName
            && EquivalentPath(after.BinaryPath, binaryPath) && after.StartType == ServiceClient.DisabledStart
            && after.Account?.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) == true
            && after.ServiceType == "win32_own_process" && after.State == "stopped",
        "modify" => before.Exists && after.Exists && before.StartType == ServiceClient.DemandStart
            && after.StartType == ServiceClient.DisabledStart && before.DisplayName != after.DisplayName
            && after.DisplayName == displayName && !EquivalentPath(before.BinaryPath, after.BinaryPath)
            && EquivalentPath(after.BinaryPath, binaryPath) && after.State == "stopped",
        "delete" => before.Exists && !after.Exists,
        _ => false,
    };

    private static int? SystemEventId(string operation) => operation switch
    {
        "create" => 7045,
        "modify" => 7040,
        "delete" => null,
        _ => throw new ArgumentException($"不支持的服务操作：{operation}"),
    };

    private static string NativeApi(string operation) => operation switch
    {
        "create" => "CreateServiceW", "modify" => "ChangeServiceConfigW", "delete" => "DeleteService",
        _ => throw new ArgumentException($"不支持的服务操作：{operation}"),
    };

    private static string SafeNativeApi(string operation) => operation switch
    {
        "create" => "CreateServiceW", "modify" => "ChangeServiceConfigW", "delete" => "DeleteService", _ => "unknown",
    };

    private static DiagnosticResult CollectSystemEventDiagnostic(string serviceName, int eventId)
    {
        try
        {
            Thread.Sleep(300);
            var query = $"*[System[Provider[@Name='Service Control Manager'] and (EventID={eventId}) and TimeCreated[timediff(@SystemTime) <= 15000]]]";
            var result = RunCommand(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                ["qe", "System", $"/q:{query}", "/f:xml", "/c:30", "/rd:true"], 10_000);
            var output = string.Join(Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            return new DiagnosticResult(result.ExitCode == 0 && output.Contains(serviceName, StringComparison.OrdinalIgnoreCase),
                output, result.ExitCode == 0 ? null : $"wevtutil System 查询退出码 {result.ExitCode}");
        }
        catch (Exception exception) { return new DiagnosticResult(null, null, exception.Message); }
    }

    private static CommandResult RunCommand(string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动系统程序：{executable}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待系统程序退出超时：{executable}");
        }
        Task.WaitAll(output, error);
        return new CommandResult(process.ExitCode, output.Result, error.Result);
    }

    private static bool EquivalentPath(string? left, string? right) => string.Equals(
        left?.Trim().Replace('/', '\\'), right?.Trim().Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    private static ServiceSnapshot SafeSnapshot(string serviceName)
    {
        try { return ServiceClient.Snapshot(serviceName); }
        catch { return ServiceClient.MissingSnapshot(); }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record DiagnosticResult(bool? Found, string? Output, string? Error);
}
