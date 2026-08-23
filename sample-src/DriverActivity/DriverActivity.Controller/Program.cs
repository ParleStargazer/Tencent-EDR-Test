using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EdrTest;

namespace DriverActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["win.driver.load"] = "load",
            ["win.driver.modify"] = "modify",
            ["win.driver.unload"] = "unload",
        };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        var actorProcesses = new List<Process>();
        string? serviceName = null;
        string? imagePath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
                throw new InvalidDataException($"DriverActivity Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var packageDirectory = Path.GetDirectoryName(Path.GetFullPath(invocation.ManifestPath))
                ?? throw new InvalidDataException("无法定位能力包目录。");
            var sourceDriver = Path.Combine(packageDirectory, "EdrTestDriver.sys");
            var metadataPath = Path.Combine(packageDirectory, "driver-package.json");
            var environment = EvaluateEnvironment(operation, sourceDriver, metadataPath);
            AddEnvironmentFacts(database, invocation, environment);
            if (!environment.Ready)
            {
                database.AddCleanup(EmptyCleanup(invocation, "environment_not_ready"));
                database.CompleteCapability(invocation.CaseRunId, "SKIPPED", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "ENVIRONMENT_NOT_READY", environment.Reason);
                WriteStatus("SKIPPED", package.Manifest.CapabilityId, operation, environment.Reason);
                return 10;
            }

            var tag = BuildTag(invocation.Nonce);
            serviceName = $"EdrTestDrv_{tag}_{operation}";
            imagePath = Path.Combine(invocation.WorkDir, $"EdrTestDriver_{tag}_{operation}.sys");
            EnsureServiceAbsent(serviceName);
            File.Copy(sourceDriver, imagePath, overwrite: false);
            var copiedHash = Hashing.FileSha256(imagePath);
            if (!string.Equals(copiedHash, environment.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("驱动工作副本 SHA256 与已验证包不一致。");

            var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            BehaviorResult result;
            ProgramObservation actor;
            string resultPath;
            BehaviorResult? setupResult = null;
            ProgramObservation? setupActor = null;

            if (operation == "unload")
            {
                var setup = ExecuteActor(invocation, actorPath, "setup_load", serviceName, imagePath,
                    marker: null, holdMs: 0, instanceIndex: 0, actorProcesses);
                setupResult = setup.Result;
                setupActor = setup.Observation;
                database.AddProgram(setupActor);
                if (!setupResult.Succeeded || !DriverClient.Snapshot(serviceName, imagePath).Loaded)
                    throw new InvalidOperationException(setupResult.Error ?? "卸载测试的预置加载未成功。");
                var isolationMs = parameters["load_isolation_ms"]?.GetValue<int>() ?? 2_200;
                Thread.Sleep(Math.Max(2_000, isolationMs));
                var unload = ExecuteActor(invocation, actorPath, "unload", serviceName, imagePath,
                    marker: null, holdMs, instanceIndex: 1, actorProcesses);
                result = unload.Result;
                actor = unload.Observation;
                resultPath = unload.ResultPath;
            }
            else
            {
                var marker = operation == "modify" ? $"EDRTEST_DRIVER_MODIFY|{invocation.Nonce}" : null;
                var execution = ExecuteActor(invocation, actorPath, operation, serviceName, imagePath,
                    marker, holdMs, instanceIndex: 0, actorProcesses);
                result = execution.Result;
                actor = execution.Observation;
                resultPath = execution.ResultPath;
            }

            database.AddProgram(actor);
            var current = DriverClient.Snapshot(serviceName, imagePath);
            var succeeded = result.Succeeded && Verify(operation, result, current);
            if (setupResult is not null && !setupResult.Succeeded) succeeded = false;

            var artifact = CreateEvidenceArtifact(invocation, resultPath, operation, serviceName);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, result, actor, artifact.ArtifactId,
                environment);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, result, actor, localEvent.LocalEventId,
                succeeded, setupResult, setupActor, parameters);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);

            var cleanup = Cleanup(invocation, serviceName, imagePath, actorProcesses);
            database.AddCleanup(cleanup);
            serviceName = null;
            imagePath = null;
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "DRIVER_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = succeeded ? null : result.Error ?? "Controller 独立查询未确认预期驱动状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "DRIVER_OUTCOME_MISMATCH", error);
            WriteStatus(status, package.Manifest.CapabilityId, operation, error);
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanup = serviceName is null || imagePath is null
                        ? EmptyCleanup(invocation, "no_driver_allocated")
                        : Cleanup(invocation, serviceName, imagePath, actorProcesses);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "DRIVER_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine(cleanupException);
                }
            }
            return 20;
        }
        finally
        {
            foreach (var process in actorProcesses) process.Dispose();
            database?.Dispose();
        }
    }

    private static EnvironmentCheck EvaluateEnvironment(string operation, string sourceDriver, string metadataPath)
    {
        if (!OperatingSystem.IsWindows()) return EnvironmentCheck.NotReady("仅支持 Windows。", sourceDriver);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return EnvironmentCheck.NotReady("驱动样本仅支持 x64 进程。", sourceDriver);
        if (!DriverClient.IsAdministrator())
            return EnvironmentCheck.NotReady("驱动能力需要管理员权限。", sourceDriver);
        if (!File.Exists(sourceDriver))
            return EnvironmentCheck.NotReady("能力包缺少 EdrTestDriver.sys。", sourceDriver);
        if (!File.Exists(metadataPath))
            return EnvironmentCheck.NotReady("能力包缺少 driver-package.json。", sourceDriver);

        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
            ?? throw new InvalidDataException("driver-package.json 不是 JSON 对象。");
        var expectedHash = metadata["sha256"]?.GetValue<string>();
        var actualHash = Hashing.FileSha256(sourceDriver);
        if (string.IsNullOrWhiteSpace(expectedHash)
            || !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            return EnvironmentCheck.NotReady("驱动文件与 package 元数据 SHA256 不一致。", sourceDriver, actualHash);

        var signatureValid = metadata["signature_valid"]?.GetValue<bool>() ?? false;
        var signer = metadata["signer"]?.GetValue<string>();
        var thumbprint = metadata["certificate_thumbprint"]?.GetValue<string>();
        var requiresTestSigning = metadata["requires_test_signing"]?.GetValue<bool>() ?? true;
        if (operation is "load" or "unload")
        {
            if (!signatureValid || string.IsNullOrWhiteSpace(thumbprint))
                return EnvironmentCheck.NotReady("驱动未完成可验证的代码签名。", sourceDriver, actualHash, signer,
                    signatureValid, thumbprint, requiresTestSigning);
            if (!CertificateTrusted(thumbprint))
                return EnvironmentCheck.NotReady("驱动测试证书尚未同时导入 LocalMachine\\Root 与 TrustedPublisher。",
                    sourceDriver, actualHash, signer, signatureValid, thumbprint, requiresTestSigning);
            if (requiresTestSigning && !TestSigningEnabled())
                return EnvironmentCheck.NotReady("当前启动项未启用 testsigning；请运行初始化脚本并重启。",
                    sourceDriver, actualHash, signer, signatureValid, thumbprint, requiresTestSigning);
        }
        return new EnvironmentCheck(true, null, sourceDriver, actualHash, signer, signatureValid,
            thumbprint, requiresTestSigning);
    }

    private static bool CertificateTrusted(string thumbprint) =>
        StoreContains(StoreName.Root, thumbprint) && StoreContains(StoreName.TrustedPublisher, thumbprint);

    private static bool StoreContains(StoreName name, string thumbprint)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
    }

    private static bool TestSigningEnabled()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/enum");
        info.ArgumentList.Add("{current}");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 bcdedit.exe。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("bcdedit 环境检查超时。");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"bcdedit 环境检查失败：{error}");
        return System.Text.RegularExpressions.Regex.IsMatch(output,
            @"(?im)^\s*testsigning\s+(Yes|On|是|开启)\s*$");
    }

    private static ActorExecution ExecuteActor(ControllerInvocation invocation, string actorPath, string operation,
        string serviceName, string imagePath, string? marker, int holdMs, int instanceIndex,
        ICollection<Process> processes)
    {
        var resultPath = Path.Combine(invocation.WorkDir, $"driver-actor-{instanceIndex}-{operation}.json");
        var arguments = new List<string>
        {
            "--operation", operation,
            "--service-name", serviceName,
            "--image-path", imagePath,
            "--allowed-root", invocation.WorkDir,
            "--result", resultPath,
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (marker is not null)
        {
            arguments.Add("--marker");
            arguments.Add(marker);
        }
        var process = Start(actorPath, arguments, invocation.WorkDir);
        processes.Add(process);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs, process);
        if (!process.WaitForExit(invocation.TimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待驱动活动 Actor 退出超时：PID {process.Id}");
        }
        return new ActorExecution(result, ObserveActor(invocation, process, actorPath, arguments, result,
            instanceIndex), resultPath);
    }

    private static ProgramObservation ObserveActor(ControllerInvocation invocation, Process process,
        string executable, IReadOnlyList<string> arguments, BehaviorResult result, int instanceIndex)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { startedAt = result.OccurredAtUtc; }
        try { endedAt = process.ExitTime.ToUniversalTime(); exitCode = process.ExitCode; }
        catch (InvalidOperationException) { endedAt = null; exitCode = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = result.NativeApi,
            InstanceIndex = instanceIndex,
            ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable),
            Sha1 = Hashing.FileSha1(executable),
            Md5 = Hashing.FileMd5(executable),
            Pid = process.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(process),
            Architecture = "x64",
            CommandLine = FormatCommandLine(executable, arguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "DriverActivity.Controller",
                ["native_api"] = result.NativeApi,
                ["controlled_service_prefix"] = "EdrTestDrv_",
                ["image_path_locked_to_work_dir"] = true,
            },
        };
    }

    private static bool Verify(string operation, BehaviorResult result, DriverSnapshot current) => operation switch
    {
        "load" => !result.Before.Loaded && result.After.Loaded && current.Loaded
            && result.After.ServiceState == "running" && !string.IsNullOrWhiteSpace(result.After.BaseAddress),
        "modify" => !result.Before.Loaded && !result.After.Loaded && !current.Loaded
            && result.FileBefore?.Exists == true && result.FileAfter?.Exists == true
            && result.FileAfter.SizeBytes > result.FileBefore.SizeBytes
            && result.FileBefore.Sha256 != result.FileAfter.Sha256,
        "unload" => result.Before.Loaded && !result.After.Loaded && !current.Loaded
            && result.After.ServiceState == "stopped",
        _ => false,
    };

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, string operation,
        Stopwatch stopwatch, BehaviorResult result, ProgramObservation actor, string artifactId,
        EnvironmentCheck environment) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        EventType = "driver",
        EventAction = operation,
        Nonce = invocation.Nonce,
        OccurredAtUtc = result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "driver_activity_controller",
        CollectionMethod = "native_scm_api_plus_kernel_module_enumeration_and_hashing",
        Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "driver",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["driver_name"] = result.DriverName,
            ["before"] = DriverState(result.Before, environment),
            ["after"] = DriverState(result.After, environment),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.Win32Error,
                ["message"] = result.Error,
            },
        },
        EvidenceRefs = [artifactId],
    };

    private static void AddEnvironmentFacts(RunDatabase database, ControllerInvocation invocation,
        EnvironmentCheck environment)
    {
        AddFact(database, invocation, "driver.environment.ready", JsonValue.Create(environment.Ready), null);
        AddFact(database, invocation, "driver.environment.reason", JsonValue.Create(environment.Reason), null);
        AddFact(database, invocation, "driver.package.sha256", JsonValue.Create(environment.Sha256), null);
        AddFact(database, invocation, "driver.package.signer", JsonValue.Create(environment.Signer), null);
        AddFact(database, invocation, "driver.package.signature_valid", JsonValue.Create(environment.SignatureValid), null);
        AddFact(database, invocation, "driver.package.certificate_thumbprint",
            JsonValue.Create(environment.CertificateThumbprint), null);
        AddFact(database, invocation, "driver.package.requires_test_signing",
            JsonValue.Create(environment.RequiresTestSigning), null);
    }

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, string operation,
        BehaviorResult result, ProgramObservation actor, string eventId, bool succeeded,
        BehaviorResult? setupResult, ProgramObservation? setupActor, JsonObject parameters)
    {
        var beforeFile = result.FileBefore;
        var afterFile = result.FileAfter;
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"driver.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["driver.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["driver.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            ["driver.name"] = JsonValue.Create(result.DriverName),
            ["driver.service_name"] = JsonValue.Create(result.ServiceName),
            ["driver.image_path"] = JsonValue.Create(result.ImagePath),
            ["driver.native_api"] = JsonValue.Create(result.NativeApi),
            ["driver.actor_pid"] = JsonValue.Create(actor.Pid),
            ["driver.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["driver.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["driver.before.loaded"] = JsonValue.Create(result.Before.Loaded),
            ["driver.before.service_exists"] = JsonValue.Create(result.Before.ServiceExists),
            ["driver.before.service_state"] = JsonValue.Create(result.Before.ServiceState),
            ["driver.before.base_address"] = JsonValue.Create(result.Before.BaseAddress),
            ["driver.before.size_bytes"] = JsonValue.Create(beforeFile?.SizeBytes ?? result.Before.SizeBytes),
            ["driver.before.module_size_bytes"] = JsonValue.Create(result.Before.ModuleSizeBytes),
            ["driver.before.hashes.md5"] = JsonValue.Create(beforeFile?.Md5 ?? result.Before.Md5),
            ["driver.before.hashes.sha256"] = JsonValue.Create(beforeFile?.Sha256 ?? result.Before.Sha256),
            ["driver.before.last_write_time_utc"] = JsonValue.Create(beforeFile?.LastWriteTimeUtc is null
                ? null : Values.Utc(beforeFile.LastWriteTimeUtc.Value)),
            ["driver.after.loaded"] = JsonValue.Create(result.After.Loaded),
            ["driver.after.service_exists"] = JsonValue.Create(result.After.ServiceExists),
            ["driver.after.service_state"] = JsonValue.Create(result.After.ServiceState),
            ["driver.after.base_address"] = JsonValue.Create(result.After.BaseAddress),
            ["driver.after.size_bytes"] = JsonValue.Create(afterFile?.SizeBytes ?? result.After.SizeBytes),
            ["driver.after.module_size_bytes"] = JsonValue.Create(result.After.ModuleSizeBytes),
            ["driver.after.hashes.md5"] = JsonValue.Create(afterFile?.Md5 ?? result.After.Md5),
            ["driver.after.hashes.sha256"] = JsonValue.Create(afterFile?.Sha256 ?? result.After.Sha256),
            ["driver.after.last_write_time_utc"] = JsonValue.Create(afterFile?.LastWriteTimeUtc is null
                ? null : Values.Utc(afterFile.LastWriteTimeUtc.Value)),
            ["driver.modification.marker"] = JsonValue.Create(result.Marker),
            ["driver.setup_load_succeeded"] = JsonValue.Create(setupResult?.Succeeded),
            ["driver.setup_load_completed_at_utc"] = JsonValue.Create(setupResult is null
                ? null : Values.Utc(setupResult.CompletedAtUtc)),
            ["driver.setup_actor_pid"] = JsonValue.Create(setupActor?.Pid),
            ["driver.load_isolation_ms"] = JsonValue.Create(parameters["load_isolation_ms"]?.GetValue<int>()),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key,
        JsonNode? value, string? eventId) => database.AddFact(new LocalFactObservation
    {
        CaseRunId = invocation.CaseRunId,
        LocalEventId = eventId,
        Key = key,
        Value = value,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Source = "driver_activity_controller",
        Confidence = "high",
    });

    private static ArtifactObservation CreateEvidenceArtifact(ControllerInvocation invocation, string resultPath,
        string operation, string serviceName)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, resultPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(resultPath),
            SizeBytes = new FileInfo(resultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(resultPath),
            Sensitive = false,
            Metadata = new JsonObject
            {
                ["operation"] = operation,
                ["service_name"] = serviceName,
                ["driver_image"] = "work-directory-copy",
            },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, string serviceName,
        string imagePath, IEnumerable<Process> actors)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        foreach (var actor in actors) StopProcess(actor, errors);
        var before = SafeSnapshot(serviceName, imagePath);
        try
        {
            if (before.Loaded) DriverClient.Stop(serviceName, ignoreInactive: true);
        }
        catch (Exception exception) { errors.Add($"卸载本轮驱动失败：{exception.Message}"); }
        try
        {
            DriverClient.Delete(serviceName, ignoreMissing: true);
            DriverClient.WaitForServiceMissing(serviceName);
        }
        catch (Exception exception) { errors.Add($"删除本轮驱动服务失败：{exception.Message}"); }
        try
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
        catch (Exception exception) { errors.Add($"删除驱动工作副本失败：{exception.Message}"); }
        var after = SafeSnapshot(serviceName, imagePath);
        var succeeded = errors.Count == 0 && !after.Loaded && !after.ServiceExists && !File.Exists(imagePath);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = 1,
            Action = "stop_delete_exact_driver_service_and_work_copy",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = started,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject
            {
                ["service_name"] = serviceName,
                ["loaded"] = before.Loaded,
                ["service_exists"] = before.ServiceExists,
                ["image_path"] = imagePath,
            },
            After = new JsonObject
            {
                ["loaded"] = after.Loaded,
                ["service_exists"] = after.ServiceExists,
                ["image_exists"] = File.Exists(imagePath),
            },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation, string action) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        Action = action,
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
    };

    private static void EnsureServiceAbsent(string serviceName)
    {
        DriverClient.Stop(serviceName, ignoreInactive: true);
        DriverClient.Delete(serviceName, ignoreMissing: true);
        DriverClient.WaitForServiceMissing(serviceName);
    }

    private static DriverSnapshot SafeSnapshot(string serviceName, string imagePath)
    {
        try { return DriverClient.Snapshot(serviceName, imagePath); }
        catch
        {
            return new DriverSnapshot
            {
                Loaded = false,
                ServiceExists = false,
                ServiceName = serviceName,
                ImagePath = imagePath,
            };
        }
    }

    private static JsonObject DriverState(DriverSnapshot value, EnvironmentCheck environment) => new()
    {
        ["loaded"] = value.Loaded,
        ["service_name"] = value.ServiceName,
        ["image_path"] = value.ImagePath,
        ["base_address"] = value.BaseAddress,
        ["size_bytes"] = value.SizeBytes,
        ["module_size_bytes"] = value.ModuleSizeBytes,
        ["hashes"] = new JsonObject { ["md5"] = value.Md5, ["sha256"] = value.Sha256 },
        ["signer"] = environment.Signer,
        ["signature_valid"] = environment.SignatureValid,
    };

    private static JsonObject ProcessReference(ProgramObservation program) => new()
    {
        ["program_instance_id"] = program.ProgramInstanceId,
        ["pid"] = program.Pid,
        ["parent_pid"] = program.ParentPid,
        ["started_at_utc"] = Values.Utc(program.StartedAtUtc),
        ["executable"] = program.ExecutablePath,
        ["command_line"] = program.CommandLine,
    };

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"启动驱动活动 Actor 失败：{executable}");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Actor 写入结果前已退出，退出码 {process.ExitCode}。");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"等待驱动活动结果超时：{path}");
            Thread.Sleep(10);
        }
        return ProtocolJson.Read<BehaviorResult>(path);
    }

    private static void StopProcess(Process process, ICollection<string> errors)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5_000)) errors.Add($"PID {process.Id} 在 5 秒内未退出。");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception exception) { errors.Add($"停止 PID {process.Id} 失败：{exception.Message}"); }
    }

    private static string BuildTag(string nonce)
    {
        var tag = new string(nonce.Where(char.IsLetterOrDigit).Take(16).ToArray()).ToLowerInvariant();
        if (tag.Length < 8) throw new InvalidDataException("本轮 nonce 不能生成安全的驱动测试名称。");
        return tag;
    }

    private static int? TrySessionId(Process process)
    {
        try { return process.SessionId; }
        catch (InvalidOperationException) { return null; }
    }

    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
        : value;

    private static void WriteStatus(string status, string capabilityId, string operation, string? error) =>
        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0",
            ["status"] = status,
            ["capability_id"] = capabilityId,
            ["operation"] = operation,
            ["methods"] = 1,
            ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));

    private sealed record ActorExecution(BehaviorResult Result, ProgramObservation Observation, string ResultPath);

    private sealed record EnvironmentCheck(
        bool Ready,
        string? Reason,
        string SourceDriver,
        string? Sha256,
        string? Signer,
        bool? SignatureValid,
        string? CertificateThumbprint,
        bool? RequiresTestSigning)
    {
        public static EnvironmentCheck NotReady(string reason, string sourceDriver, string? sha256 = null,
            string? signer = null, bool? signatureValid = null, string? certificateThumbprint = null,
            bool? requiresTestSigning = null) =>
            new(false, reason, sourceDriver, sha256, signer, signatureValid, certificateThumbprint,
                requiresTestSigning);
    }
}
