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
    string? EnvironmentId = null,
    int InterCapabilityDelaySeconds = 3,
    Action<RunProgressUpdate>? ProgressCallback = null,
    int InterSubtestDelayMilliseconds = SubtestTiming.DefaultDelayMilliseconds);

public sealed record RunResult(string RunId, string RunDirectory, string DatabasePath, string LocalExportPath, string Status);

public sealed record RunProgressUpdate(
    DateTimeOffset TimestampUtc,
    string Kind,
    string Level,
    string Message,
    int Progress,
    int TotalCapabilities,
    string? CapabilityId = null,
    string? CapabilityName = null,
    int? CapabilityIndex = null,
    string? CapabilityStatus = null,
    int? WaitRemainingSeconds = null,
    bool Important = false,
    JsonObject? LocalEvidence = null);

public sealed class RunnerService
{
    private static readonly HashSet<string> TerminalStatuses = ["LOCAL_PASS", "SAMPLE_ERROR", "CLEANUP_ERROR", "SKIPPED", "ABORTED"];

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ManifestPaths.Count == 0) throw new ArgumentException("至少需要一个能力清单。");
        if (request.InterCapabilityDelaySeconds is < 0 or > 300) throw new ArgumentOutOfRangeException(nameof(request), "能力间等待时间必须在 0..300 秒内。");
        if (request.InterSubtestDelayMilliseconds is < 0 or > SubtestTiming.MaximumDelayMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(request), $"子测试间等待时间必须在 0..{SubtestTiming.MaximumDelayMilliseconds} 毫秒内。");
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
                Report(request, new RunProgressUpdate(started, "run_started", "info", $"测试轮次已创建，共 {packages.Length} 项能力，将严格串行执行。", 3, packages.Length, Important: true));
                for (var index = 0; index < packages.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var package = packages[index];
                    var capabilityName = package.Manifest.DisplayNameZh ?? package.Manifest.DisplayName ?? package.Manifest.CapabilityId;
                    var startProgress = 5 + (int)Math.Floor(index * 90d / packages.Length);
                    Report(request, new RunProgressUpdate(
                        DateTimeOffset.UtcNow,
                        "capability_started",
                        "info",
                        $"开始第 {index + 1}/{packages.Length} 项：{capabilityName}。",
                        startProgress,
                        packages.Length,
                        package.Manifest.CapabilityId,
                        capabilityName,
                        index + 1,
                        "running",
                        Important: true));
                    var caseRunId = Ids.NewUuid7();
                    var nonce = Ids.NewNonce();
                    var parameters = CapabilityCatalog.BuildParameters(package.Manifest, request.ParametersJson);
                    database.AddCapability(runId, caseRunId, index + 1, nonce, package, parameters);
                    database.AddFact(new LocalFactObservation
                    {
                        CaseRunId = caseRunId,
                        Key = "execution.inter_subtest_delay_ms",
                        Value = JsonValue.Create(request.InterSubtestDelayMilliseconds),
                        ObservedAtUtc = DateTimeOffset.UtcNow,
                        Source = "runner_configuration",
                        Confidence = "high",
                    });
                    var caseDirectory = Path.Combine(workRoot, caseRunId);
                    Directory.CreateDirectory(caseDirectory);
                    var parameterPath = Path.Combine(caseDirectory, "parameters.json");
                    await File.WriteAllTextAsync(parameterPath, parameters.GetRawText(), new UTF8Encoding(false), cancellationToken);

                    if (Precheck(package.Manifest) is { } failure)
                    {
                        SkipCapability(database, caseRunId, failure.Code, failure.Message);
                        ReportCapabilityCompleted(request, package, index, packages.Length, "SKIPPED", failure.Message, database.ReadCapabilityEvidence(caseRunId));
                        await WaitBeforeNextAsync(request, package, index, packages.Length, cancellationToken);
                        continue;
                    }
                    if (package.Manifest.RiskLevel is "L2" or "L3" && !request.AllowHighRisk)
                    {
                        SkipCapability(database, caseRunId, "RISK_APPROVAL_REQUIRED", "L2/L3 能力需要 --allow-high-risk。");
                        ReportCapabilityCompleted(request, package, index, packages.Length, "SKIPPED", "未确认高风险执行，能力已跳过。", database.ReadCapabilityEvidence(caseRunId));
                        await WaitBeforeNextAsync(request, package, index, packages.Length, cancellationToken);
                        continue;
                    }

                    var capabilityStatus = await ExecuteControllerAsync(database, package, runId, caseRunId, nonce, databasePath, caseDirectory, parameterPath, request, index, packages.Length, cancellationToken);
                    if (capabilityStatus is not ("LOCAL_PASS" or "SKIPPED")) runStatus = "COMPLETED_WITH_ERRORS";
                    ReportCapabilityCompleted(request, package, index, packages.Length, capabilityStatus, CapabilityStatusMessage(capabilityStatus), database.ReadCapabilityEvidence(caseRunId));
                    if (capabilityStatus == "CLEANUP_ERROR") break;
                    await WaitBeforeNextAsync(request, package, index, packages.Length, cancellationToken);
                }
                database.Seal(runStatus, DateTimeOffset.UtcNow);
                Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "run_completed", runStatus == "COMPLETED" ? "info" : "warning", runStatus == "COMPLETED" ? "全部能力执行完成，本地数据库已封存。" : "测试轮次已结束，但有能力未通过本地自验证。", 97, packages.Length, Important: true));
            }
            catch (OperationCanceledException)
            {
                TryAbort(database, "RUN_CANCELLED", "用户取消了测试轮次。");
                Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "run_cancelled", "warning", "用户取消了测试轮次，正在保留已产生的证据。", 100, packages.Length, Important: true));
                throw;
            }
            catch (Exception exception)
            {
                TryAbort(database, "RUN_ABORTED", exception.Message);
                Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "run_failed", "error", $"测试轮次异常结束：{exception.Message}", 100, packages.Length, Important: true));
                throw;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
        ExportService.Export(databasePath, exportPath);
        Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "export_completed", "info", "本地运行结果 JSON 已生成，可以进入离线比较。", 100, packages.Length, Important: true));
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
        RunRequest request,
        int capabilityIndex,
        int totalCapabilities,
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
        AddArgument(startInfo, "inter-subtest-delay-ms", request.InterSubtestDelayMilliseconds.ToString());

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Controller 启动返回空进程。");
            var capabilityName = package.Manifest.DisplayNameZh ?? package.Manifest.DisplayName ?? package.Manifest.CapabilityId;
            Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "controller_started", "info", $"控制程序已启动（PID {process.Id}）。", 8 + (int)Math.Floor(capabilityIndex * 90d / totalCapabilities), totalCapabilities, package.Manifest.CapabilityId, capabilityName, capabilityIndex + 1));
            var stdoutTask = CaptureOutputAsync(process.StandardOutput, line =>
            {
                var display = ControllerOutputFormatter.FormatLine(line);
                Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, display.Kind, display.Level, display.Message, 8 + (int)Math.Floor(capabilityIndex * 90d / totalCapabilities), totalCapabilities, package.Manifest.CapabilityId, capabilityName, capabilityIndex + 1, display.Status, Important: display.Level != "info"));
            });
            var stderrTask = CaptureOutputAsync(process.StandardError, line => Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "controller_stderr", "warning", line, 8 + (int)Math.Floor(capabilityIndex * 90d / totalCapabilities), totalCapabilities, package.Manifest.CapabilityId, capabilityName, capabilityIndex + 1, Important: true)));
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
            if (!string.IsNullOrWhiteSpace(stdout)) database.AddLog(caseRunId, "info", "controller.stdout", stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) database.AddLog(caseRunId, "warning", "controller.stderr", stderr);

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

    private static async Task<string> CaptureOutputAsync(StreamReader reader, Action<string> onLine)
    {
        var builder = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            builder.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line)) onLine(line);
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static void ReportCapabilityCompleted(RunRequest request, CapabilityPackage package, int index, int total, string status, string message, JsonObject localEvidence)
    {
        var name = package.Manifest.DisplayNameZh ?? package.Manifest.DisplayName ?? package.Manifest.CapabilityId;
        var level = status is "LOCAL_PASS" or "SKIPPED" ? "info" : "warning";
        var progress = 5 + (int)Math.Floor((index + 1) * 90d / total);
        Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "capability_completed", level, $"{name}：{message}", progress, total, package.Manifest.CapabilityId, name, index + 1, status, Important: true, LocalEvidence: localEvidence));
    }

    private static async Task WaitBeforeNextAsync(RunRequest request, CapabilityPackage current, int index, int total, CancellationToken cancellationToken)
    {
        if (index >= total - 1 || request.InterCapabilityDelaySeconds == 0) return;
        var name = current.Manifest.DisplayNameZh ?? current.Manifest.DisplayName ?? current.Manifest.CapabilityId;
        var progress = 5 + (int)Math.Floor((index + 1) * 90d / total);
        for (var remaining = request.InterCapabilityDelaySeconds; remaining > 0; remaining--)
        {
            Report(request, new RunProgressUpdate(DateTimeOffset.UtcNow, "waiting_next", "info", $"{remaining} 秒后开始下一项能力。", progress, total, current.Manifest.CapabilityId, name, index + 1, "waiting", remaining));
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string CapabilityStatusMessage(string status) => status switch
    {
        "LOCAL_PASS" => "本地行为和清理均已验证通过。",
        "SKIPPED" => "因前置条件不满足而跳过。",
        "CLEANUP_ERROR" => "行为已发生，但清理失败，后续能力已停止。",
        "ABORTED" => "执行被中止。",
        _ => "本地行为未通过自验证。",
    };

    private static void Report(RunRequest request, RunProgressUpdate update)
    {
        try
        {
            request.ProgressCallback?.Invoke(update);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"进度回调失败：{exception.Message}");
        }
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add($"--{name}");
        info.ArgumentList.Add(value);
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
