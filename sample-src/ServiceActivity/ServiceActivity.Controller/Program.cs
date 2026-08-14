using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace ServiceActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.service.create"] = "create",
        ["win.service.modify"] = "modify",
        ["win.service.delete"] = "delete",
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        Process? actorProcess = null;
        string? serviceName = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
                throw new InvalidDataException($"ServiceActivity Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);
            var tag = BuildTag(invocation.Nonce);
            serviceName = $"EdrTestSvc_{tag}_{operation}";
            var beforeDisplayName = $"EDRTEST|{invocation.Nonce}|SERVICE|BEFORE";
            var expectedDisplayName = $"EDRTEST|{invocation.Nonce}|SERVICE|{operation.ToUpperInvariant()}";
            var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var beforeBinaryPath = $"\"{command}\" /d /c exit 0";
            var expectedBinaryPath = $"\"{command}\" /d /c rem EDRTEST_{tag}_{operation.ToUpperInvariant()}";
            Prepare(operation, serviceName, beforeDisplayName, beforeBinaryPath);
            var setupDelayMs = parameters["setup_delay_ms"]?.GetValue<int>() ?? 750;
            if (setupDelayMs > 0 && operation != "create") Thread.Sleep(setupDelayMs);

            var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var resultPath = Path.Combine(invocation.WorkDir, "service-actor-result.json");
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var actorArguments = new List<string>
            {
                "--operation", operation, "--service-name", serviceName, "--display-name", expectedDisplayName,
                "--binary-path", expectedBinaryPath, "--result", resultPath,
                "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            actorProcess = Start(actorPath, actorArguments, invocation.WorkDir);
            var result = WaitAndRead(resultPath, invocation.TimeoutMs, actorProcess);
            if (!actorProcess.WaitForExit(invocation.TimeoutMs))
            {
                actorProcess.Kill(entireProcessTree: true);
                throw new TimeoutException($"等待服务活动 Actor 退出超时：PID {actorProcess.Id}");
            }

            var actor = ObserveActor(invocation, actorProcess, actorPath, actorArguments, result);
            database.AddProgram(actor);
            var independentlyObserved = ServiceClient.Snapshot(serviceName);
            var succeeded = result.Succeeded && Verify(operation, expectedDisplayName, expectedBinaryPath, result, independentlyObserved);
            var artifact = CreateEvidenceArtifact(invocation, resultPath, operation, serviceName);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, result, actor, artifact.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, result, actor, localEvent.LocalEventId, succeeded);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);

            var cleanup = Cleanup(invocation, serviceName, actorProcess);
            database.AddCleanup(cleanup);
            actorProcess.Dispose();
            actorProcess = null;
            serviceName = null;
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "SERVICE_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = succeeded ? null : result.Error ?? "Controller 独立查询未确认预期服务状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "SERVICE_OUTCOME_MISMATCH", error);
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
                    var cleanup = serviceName is null
                        ? EmptyCleanup(invocation)
                        : Cleanup(invocation, serviceName, actorProcess);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "SERVICE_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally
        {
            actorProcess?.Dispose();
            database?.Dispose();
        }
    }

    private static void Prepare(string operation, string serviceName, string displayName, string binaryPath)
    {
        RemoveExactService(serviceName);
        if (ServiceClient.Snapshot(serviceName).Exists)
            throw new IOException($"无法清除冲突的本轮服务：{serviceName}");
        if (operation == "create") return;
        ServiceClient.Create(serviceName, displayName, binaryPath,
            operation == "modify" ? ServiceClient.DemandStart : ServiceClient.DisabledStart);
        var seeded = ServiceClient.Snapshot(serviceName);
        var expectedStart = operation == "modify" ? ServiceClient.DemandStart : ServiceClient.DisabledStart;
        if (!seeded.Exists || seeded.DisplayName != displayName || seeded.StartType != expectedStart
            || !EquivalentPath(seeded.BinaryPath, binaryPath) || seeded.State != "stopped")
            throw new IOException("Controller 未能独立确认服务预置状态。");
    }

    private static bool Verify(string operation, string displayName, string binaryPath,
        BehaviorResult result, ServiceSnapshot current) => operation switch
    {
        "create" => !result.Before.Exists && result.After.Exists && current.Exists
            && current.DisplayName == displayName && EquivalentPath(current.BinaryPath, binaryPath)
            && current.StartType == ServiceClient.DisabledStart && current.State == "stopped",
        "modify" => result.Before.Exists && result.Before.StartType == ServiceClient.DemandStart
            && result.After.Exists && current.Exists && result.Before.DisplayName != result.After.DisplayName
            && current.DisplayName == displayName && EquivalentPath(current.BinaryPath, binaryPath)
            && current.StartType == ServiceClient.DisabledStart && current.State == "stopped",
        "delete" => result.Before.Exists && !result.After.Exists && !current.Exists,
        _ => false,
    };

    private static ProgramObservation ObserveActor(ControllerInvocation invocation, Process process, string executable,
        IReadOnlyList<string> arguments, BehaviorResult result)
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
            CaseRunId = invocation.CaseRunId, Role = "actor", InstanceName = result.NativeApi, InstanceIndex = 0,
            ExecutablePath = executable, Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable),
            Md5 = Hashing.FileMd5(executable), Pid = process.Id, ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(process), Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            { "x86" => "x86", "arm64" => "arm64", _ => "x64" },
            CommandLine = FormatCommandLine(executable, arguments), WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt, EndedAtUtc = endedAt, ExitCode = exitCode,
            StartupAttempted = true, StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "ServiceActivity.Controller", ["native_api"] = result.NativeApi,
                ["service_was_never_started"] = true, ["controlled_service_prefix"] = "EdrTestSvc_",
            },
        };
    }

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, string operation, Stopwatch stopwatch,
        BehaviorResult result, ProgramObservation actor, string artifactId) => new()
    {
        CaseRunId = invocation.CaseRunId, Sequence = 1, EventType = "service", EventAction = operation,
        Nonce = invocation.Nonce, OccurredAtUtc = result.OccurredAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds, Source = "service_activity_controller",
        CollectionMethod = "native_scm_api_plus_independent_query_service_config", Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "service", ["operation"] = operation, ["actor"] = ProcessReference(actor),
            ["service_name"] = result.ServiceName, ["before"] = ServiceState(result.Before),
            ["after"] = ServiceState(result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true, ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.Win32Error, ["message"] = result.Error,
            },
        },
        EvidenceRefs = [artifactId],
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, string operation,
        BehaviorResult result, ProgramObservation actor, string eventId, bool succeeded)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"service.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["service.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["service.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            ["service.name"] = JsonValue.Create(result.ServiceName), ["service.display_name"] = JsonValue.Create(result.ExpectedDisplayName),
            ["service.binary_path"] = JsonValue.Create(result.ExpectedBinaryPath), ["service.native_api"] = JsonValue.Create(result.NativeApi),
            ["service.actor_pid"] = JsonValue.Create(actor.Pid), ["service.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["service.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["service.before.exists"] = JsonValue.Create(result.Before.Exists), ["service.before.display_name"] = JsonValue.Create(result.Before.DisplayName),
            ["service.before.binary_path"] = JsonValue.Create(result.Before.BinaryPath), ["service.before.start_type"] = JsonValue.Create(result.Before.StartType),
            ["service.before.account"] = JsonValue.Create(result.Before.Account), ["service.before.service_type"] = JsonValue.Create(result.Before.ServiceType),
            ["service.before.state"] = JsonValue.Create(result.Before.State),
            ["service.after.exists"] = JsonValue.Create(result.After.Exists), ["service.after.display_name"] = JsonValue.Create(result.After.DisplayName),
            ["service.after.binary_path"] = JsonValue.Create(result.After.BinaryPath), ["service.after.start_type"] = JsonValue.Create(result.After.StartType),
            ["service.after.account"] = JsonValue.Create(result.After.Account), ["service.after.service_type"] = JsonValue.Create(result.After.ServiceType),
            ["service.after.state"] = JsonValue.Create(result.After.State),
            ["service.system_event_id"] = JsonValue.Create(result.SystemEventId), ["service.system_event_found"] = JsonValue.Create(result.SystemEventFound),
            ["service.system_event_query_output"] = JsonValue.Create(result.SystemEventQueryOutput),
            ["service.diagnostic_error"] = JsonValue.Create(result.DiagnosticError),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) =>
        database.AddFact(new LocalFactObservation
        {
            CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value,
            ObservedAtUtc = DateTimeOffset.UtcNow, Source = "service_activity_controller", Confidence = "high",
        });

    private static ArtifactObservation CreateEvidenceArtifact(ControllerInvocation invocation, string resultPath,
        string operation, string serviceName)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId, Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, resultPath).Replace('\\', '/'), MediaType = "application/json",
            Sha256 = Hashing.FileSha256(resultPath), SizeBytes = new FileInfo(resultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(resultPath), Sensitive = false,
            Metadata = new JsonObject
            {
                ["operation"] = operation, ["service_name"] = serviceName,
                ["service_was_never_started"] = true,
            },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, string serviceName, Process? actor)
    {
        var started = DateTimeOffset.UtcNow;
        var before = SafeSnapshot(serviceName);
        var errors = new List<string>();
        if (actor is not null) Stop(actor, errors);
        try { RemoveExactService(serviceName); }
        catch (Exception exception) { errors.Add($"清理本轮服务失败：{exception.Message}"); }
        var after = SafeSnapshot(serviceName);
        var actorAlive = actor is not null && IsAlive(actor);
        var succeeded = errors.Count == 0 && !actorAlive && !after.Exists;
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId, Sequence = 1, Action = "delete_exact_test_service",
            Status = succeeded ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["service_name"] = serviceName, ["service_exists"] = before.Exists },
            After = new JsonObject { ["service_exists"] = after.Exists, ["actor_alive"] = actorAlive },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId, Sequence = 1, Action = "no_service_allocated", Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow, EndedAtUtc = DateTimeOffset.UtcNow,
    };

    private static void RemoveExactService(string serviceName)
    {
        ServiceClient.ValidateServiceName(serviceName);
        ServiceClient.Delete(serviceName, ignoreMissing: true);
        ServiceClient.WaitUntilMissing(serviceName);
    }

    private static ServiceSnapshot SafeSnapshot(string serviceName)
    {
        try { return ServiceClient.Snapshot(serviceName); }
        catch { return ServiceClient.MissingSnapshot(); }
    }

    private static JsonObject ServiceState(ServiceSnapshot value) => new()
    {
        ["exists"] = value.Exists, ["display_name"] = value.DisplayName, ["binary_path"] = value.BinaryPath,
        ["start_type"] = value.StartType, ["account"] = value.Account, ["service_type"] = value.ServiceType,
        ["state"] = value.State,
    };

    private static JsonObject ProcessReference(ProgramObservation program) => new()
    {
        ["program_instance_id"] = program.ProgramInstanceId, ["pid"] = program.Pid, ["parent_pid"] = program.ParentPid,
        ["started_at_utc"] = Values.Utc(program.StartedAtUtc), ["executable"] = program.ExecutablePath,
        ["command_line"] = program.CommandLine,
    };

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"启动服务活动 Actor 失败：{executable}");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"Actor 写入结果前已退出，退出码 {process.ExitCode}。");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待服务活动结果超时：{path}");
            Thread.Sleep(10);
        }
        return ProtocolJson.Read<BehaviorResult>(path);
    }

    private static void Stop(Process process, ICollection<string> errors)
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
        if (tag.Length < 8) throw new InvalidDataException("本轮 nonce 不能生成安全的服务测试名称。");
        return tag;
    }

    private static bool EquivalentPath(string? left, string? right) => string.Equals(
        left?.Trim().Replace('/', '\\'), right?.Trim().Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    private static bool IsAlive(Process process) { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch (InvalidOperationException) { return null; } }
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"' : value;
    private static void WriteStatus(string status, string capabilityId, string operation, string? error) => Console.WriteLine(new JsonObject
    {
        ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = capabilityId,
        ["operation"] = operation, ["methods"] = 1, ["error"] = error,
    }.ToJsonString(JsonDefaults.Options));
}
