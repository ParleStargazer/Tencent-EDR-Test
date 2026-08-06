using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdrTest;

public sealed record RunRequest(
    IReadOnlyList<string> ManifestPaths,
    string RunsDirectory,
    string? ParametersJson,
    bool AllowHighRisk,
    string? SuiteId = null,
    string? EnvironmentId = null);

public sealed record RunResult(string RunId, string RunDirectory, string DatabasePath, string LocalExportPath, string Status);

public sealed class RunnerService
{
    private static readonly HashSet<string> TerminalStatuses = ["LOCAL_PASS", "SAMPLE_ERROR", "CLEANUP_ERROR", "SKIPPED", "ABORTED"];

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ManifestPaths.Count == 0) throw new ArgumentException("至少需要一个能力清单。");
        var packages = request.ManifestPaths.Select(CapabilityCatalog.Load).ToArray();
        if (packages.Select(x => x.Manifest.CapabilityId).Distinct(StringComparer.Ordinal).Count() != packages.Length)
        {
            throw new InvalidDataException("同一轮不能重复选择相同 capability_id。");
        }

        var runId = Ids.NewUuid7();
        var started = DateTimeOffset.UtcNow;
        var runDirectory = Path.Combine(Path.GetFullPath(request.RunsDirectory), started.ToLocalTime().ToString("yyyyMMdd"), runId);
        var databasePath = Path.Combine(runDirectory, $"{runId}.db");
        var workRoot = Path.Combine(runDirectory, "work");
        var exportPath = Path.Combine(runDirectory, "export", "local-run.json");
        Directory.CreateDirectory(workRoot);

        var runStatus = "COMPLETED";
        using (var database = RunDatabase.Create(databasePath, new RunSeed(runId, request.SuiteId, request.EnvironmentId, started)))
        {
            try
            {
                database.AddLog(null, "info", "run", "测试轮次已创建。", properties: new JsonObject { ["run_id"] = runId });
                for (var index = 0; index < packages.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var package = packages[index];
                    var caseRunId = Ids.NewUuid7();
                    var nonce = Ids.NewNonce();
                    var parameters = CapabilityCatalog.BuildParameters(package.Manifest, request.ParametersJson);
                    database.AddCapability(runId, caseRunId, index + 1, nonce, package, parameters);
                    var caseDirectory = Path.Combine(workRoot, caseRunId);
                    Directory.CreateDirectory(caseDirectory);
                    var parameterPath = Path.Combine(caseDirectory, "parameters.json");
                    await File.WriteAllTextAsync(parameterPath, parameters.GetRawText(), new UTF8Encoding(false), cancellationToken);

                    if (Precheck(package.Manifest) is { } failure)
                    {
                        SkipCapability(database, caseRunId, failure.Code, failure.Message);
                        continue;
                    }
                    if (package.Manifest.RiskLevel is "L2" or "L3" && !request.AllowHighRisk)
                    {
                        SkipCapability(database, caseRunId, "RISK_APPROVAL_REQUIRED", "L2/L3 能力需要 --allow-high-risk。");
                        continue;
                    }

                    var capabilityStatus = await ExecuteControllerAsync(database, package, runId, caseRunId, nonce, databasePath, caseDirectory, parameterPath, cancellationToken);
                    if (capabilityStatus is not ("LOCAL_PASS" or "SKIPPED")) runStatus = "COMPLETED_WITH_ERRORS";
                    if (capabilityStatus == "CLEANUP_ERROR") break;
                }
                database.Seal(runStatus, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                TryAbort(database, "RUN_CANCELLED", "用户取消了测试轮次。");
                throw;
            }
            catch (Exception exception)
            {
                TryAbort(database, "RUN_ABORTED", exception.Message);
                throw;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
        ExportService.Export(databasePath, exportPath);
        return new RunResult(runId, runDirectory, databasePath, exportPath, runStatus);
    }

    private static async Task<string> ExecuteControllerAsync(
        RunDatabase database,
        CapabilityPackage package,
        string runId,
        string caseRunId,
        string nonce,
        string databasePath,
        string workDirectory,
        string parameterPath,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        database.StartCapability(caseRunId, startedAt, ["controller"]);
        var executable = package.ResolveProgram(package.Manifest.Controller.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in package.Manifest.Controller.Arguments) startInfo.ArgumentList.Add(argument);
        AddArgument(startInfo, "run-id", runId);
        AddArgument(startInfo, "case-run-id", caseRunId);
        AddArgument(startInfo, "nonce", nonce);
        AddArgument(startInfo, "run-db", Path.GetFullPath(databasePath));
        AddArgument(startInfo, "work-dir", Path.GetFullPath(workDirectory));
        AddArgument(startInfo, "manifest", package.ManifestPath);
        AddArgument(startInfo, "package-dir", package.PackageDirectory);
        AddArgument(startInfo, "parameters", Path.GetFullPath(parameterPath));
        AddArgument(startInfo, "timeout-ms", checked(package.Manifest.Timeouts.ExecuteSeconds * 1000).ToString());

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Controller 启动返回空进程。");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var timeout = TimeSpan.FromSeconds(package.Manifest.Timeouts.ExecuteSeconds + package.Manifest.Timeouts.CleanupSeconds);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                database.CompleteCapability(caseRunId, "ABORTED", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "RUN_CANCELLED", "用户取消了测试轮次。");
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                database.CompleteCapability(caseRunId, "SAMPLE_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "CONTROLLER_TIMEOUT", $"Controller 超过 {timeout.TotalSeconds:0} 秒。");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stdout)) database.AddLog(caseRunId, "info", "controller.stdout", Truncate(stdout));
            if (!string.IsNullOrWhiteSpace(stderr)) database.AddLog(caseRunId, "warning", "controller.stderr", Truncate(stderr));

            var currentStatus = database.GetCapabilityStatus(caseRunId);
            if (!TerminalStatuses.Contains(currentStatus))
            {
                var status = process.ExitCode switch
                {
                    0 => "LOCAL_PASS",
                    10 => "SKIPPED",
                    30 => "CLEANUP_ERROR",
                    _ => "SAMPLE_ERROR",
                };
                database.CompleteCapability(caseRunId, status, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, process.ExitCode == 0 ? null : $"CONTROLLER_EXIT_{process.ExitCode}", process.ExitCode == 0 ? null : "Controller 未写入终态，Runner 根据退出码封存。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            database.CompleteCapability(caseRunId, "SAMPLE_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "CONTROLLER_START_FAILED", exception.Message);
            database.AddLog(caseRunId, "error", "controller", exception.Message, "CONTROLLER_START_FAILED");
        }

        return database.GetCapabilityStatus(caseRunId);
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add($"--{name}");
        info.ArgumentList.Add(value);
    }

    private static string Truncate(string value)
    {
        const int maximum = 16_384;
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum] + "…[truncated]";
    }

    private static void TryAbort(RunDatabase database, string code, string message)
    {
        try
        {
            database.AddLog(null, "error", "run", message, code);
            database.Seal("ABORTED", DateTimeOffset.UtcNow);
        }
        catch
        {
            // 保留原始异常；未封存数据库仍可由 inspect 诊断。
        }
    }

    private static (string Code, string Message)? Precheck(CapabilityManifest manifest)
    {
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
        {
            "x86" => "x86",
            "arm64" => "arm64",
            _ => "x64",
        };
        if (!manifest.Platform.Architectures.Contains(architecture, StringComparer.OrdinalIgnoreCase))
        {
            return ("ARCHITECTURE_MISMATCH", $"能力不支持当前架构 {architecture}。");
        }
        if (manifest.Platform.MinimumVersion is { } minimumText
            && Version.TryParse(minimumText, out var minimum)
            && Environment.OSVersion.Version < minimum)
        {
            return ("OS_VERSION_TOO_LOW", $"能力要求 Windows {minimum} 或更高版本。");
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (manifest.RequiredPrivilege == "system" && !identity.IsSystem)
            {
                return ("SYSTEM_REQUIRED", "能力要求 SYSTEM 身份。");
            }
            if (manifest.RequiredPrivilege == "administrator"
                && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return ("ADMINISTRATOR_REQUIRED", "能力要求管理员权限。");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ("PRIVILEGE_CHECK_FAILED", $"无法检查当前权限：{exception.Message}");
        }
        return null;
    }

    private static void SkipCapability(RunDatabase database, string caseRunId, string code, string message)
    {
        var time = DateTimeOffset.UtcNow;
        database.StartCapability(caseRunId, time, ["runner"]);
        database.CompleteCapability(caseRunId, "SKIPPED", time, 0, code, message);
        database.AddLog(caseRunId, "warning", "precheck", message, code);
    }
}
