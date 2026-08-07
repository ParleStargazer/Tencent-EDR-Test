using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdrTest;

namespace ProcessActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.process.create"] = "create",
        ["win.process.terminate"] = "terminate",
        ["win.process.access"] = "access",
        ["win.process.image_load"] = "image_load",
        ["win.process.remote_thread"] = "remote_thread_create",
        ["win.process.tampering"] = "tamper",
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        var stopwatch = Stopwatch.StartNew();
        ExecutionState? state = null;
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
            {
                throw new InvalidDataException($"ProcessActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            }

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            state = Execute(invocation, package, operation, parameters);
            var actor = CreateProgram(invocation, "actor", state.ActorPath, state.ActorProcess, Environment.ProcessId, state.ActorArguments, null);
            var targetParentPid = operation == "create" ? state.ActorProcess.Id : Environment.ProcessId;
            var target = CreateProgram(invocation, "target", state.TargetPath, state.TargetProcess, targetParentPid, state.TargetArguments, state.TargetSnapshot);
            database.AddProgram(actor);
            database.AddProgram(target);

            var independentlyObserved = VerifyOutcome(operation, state);
            var localSucceeded = state.Result.Succeeded && independentlyObserved;
            var evidence = CreateEvidenceArtifact(invocation, state.ResultPath);
            if (evidence is not null) database.AddArtifact(evidence);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state.Result, actor, target, evidence?.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, state.Result, localEvent.LocalEventId, actor, target, localSucceeded);

            var cleanup = Cleanup(invocation, state);
            database.AddCleanup(cleanup);
            if (!string.Equals(cleanup.Status, "succeeded", StringComparison.Ordinal))
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "PROCESS_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, state.Result.Error);
                return 30;
            }

            var status = localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var errorCode = localSucceeded ? null : independentlyObserved ? "BEHAVIOR_OPERATION_FAILED" : "INDEPENDENT_OBSERVATION_FAILED";
            var errorMessage = localSucceeded ? null : state.Result.Error ?? "行为 API 返回成功，但 Controller 的独立观察未确认预期状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, errorCode, errorMessage);
            WriteStatus(status, package.Manifest.CapabilityId, operation, errorMessage);
            return localSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                var cleanup = state is null ? EmptyCleanup(invocation, exception.Message) : Cleanup(invocation, state);
                try
                {
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "PROCESS_ACTIVITY_CONTROLLER_ERROR", exception.Message);
                }
                catch (Exception databaseException)
                {
                    Console.Error.WriteLine(databaseException);
                }
                return cleanup.Status == "succeeded" ? 20 : 30;
            }
            return 20;
        }
        finally
        {
            database?.Dispose();
            state?.Dispose();
        }
    }

    private static ExecutionState Execute(
        ControllerInvocation invocation,
        CapabilityPackage package,
        string operation,
        JsonObject parameters)
    {
        var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
        var targetDefinition = package.Manifest.Participants.Single(participant => participant.Role == "target");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var targetPath = package.ResolveProgram(targetDefinition.Executable);
        var resultPath = Path.Combine(invocation.WorkDir, "behavior-result.json");
        var targetResultPath = Path.Combine(invocation.WorkDir, "target-result.json");
        var readyPath = Path.Combine(invocation.WorkDir, "target-ready.json");
        var goPath = Path.Combine(invocation.WorkDir, "image-load.go");
        var state = new ExecutionState(actorPath, targetPath, resultPath);

        try
        {
            if (operation == "create")
            {
                var lifetime = ParameterInt(parameters, "target_lifetime_ms", 5_000);
                state.ActorArguments =
                [
                    "--role", "actor", "--operation", operation, "--target", targetPath,
                    "--target-ready", readyPath, "--target-lifetime-ms", lifetime.ToString(),
                    "--nonce", invocation.Nonce, "--result", resultPath,
                ];
                state.ActorProcess = Start(actorPath, state.ActorArguments, invocation.WorkDir);
                state.Result = WaitAndRead<BehaviorResult>(resultPath, invocation.TimeoutMs);
                state.TargetSnapshot = state.Result.Target ?? throw new InvalidDataException("进程创建结果缺少 Target 快照。");
                state.TargetProcess = Process.GetProcessById(state.TargetSnapshot.Pid);
                state.TargetArguments = ExtractArguments(state.TargetSnapshot.CommandLine);
                return state;
            }

            var targetLifetime = Math.Min(invocation.TimeoutMs + 5_000, 120_000);
            state.TargetArguments = operation == "image_load"
                ?
                [
                    "--role", "target", "--operation", "image_load", "--ready", readyPath,
                    "--go", goPath, "--result", targetResultPath,
                    "--library", ParameterString(parameters, "library_name", "winhttp.dll"),
                    "--hold-ms", ParameterInt(parameters, "post_load_hold_ms", 2_000).ToString(),
                    "--wait-ms", invocation.TimeoutMs.ToString(), "--nonce", invocation.Nonce,
                ]
                :
                [
                    "--role", "target", "--operation", "idle", "--ready", readyPath,
                    "--lifetime-ms", targetLifetime.ToString(), "--nonce", invocation.Nonce,
                ];
            state.TargetProcess = Start(targetPath, state.TargetArguments, invocation.WorkDir);
            state.TargetSnapshot = WaitAndRead<ProcessSnapshot>(readyPath, Math.Min(invocation.TimeoutMs, 10_000));
            if (operation is "image_load" or "remote_thread_create")
            {
                var libraryPath = ResolveSystemLibrary(ParameterString(parameters, "library_name", "winhttp.dll"));
                state.ImageWasLoadedBefore = ModuleLoaded(state.TargetProcess, libraryPath);
            }

            var actorArguments = new List<string>
            {
                "--role", "actor", "--operation", operation, "--target-pid", state.TargetProcess.Id.ToString(),
                "--nonce", invocation.Nonce, "--result", resultPath,
            };
            switch (operation)
            {
                case "terminate":
                    Add(actorArguments, "exit-code", ParameterInt(parameters, "requested_exit_code", 197).ToString());
                    break;
                case "access":
                    Add(actorArguments, "access-mask", ParameterUInt(parameters, "requested_access_mask", 4096).ToString());
                    break;
                case "image_load":
                    Add(actorArguments, "go", goPath);
                    break;
                case "remote_thread_create":
                    Add(actorArguments, "library", ParameterString(parameters, "library_name", "winhttp.dll"));
                    break;
                case "tamper":
                    Add(actorArguments, "payload-size", ParameterInt(parameters, "payload_size", 64).ToString());
                    break;
            }

            state.ActorArguments = [.. actorArguments];
            state.ActorProcess = Start(actorPath, state.ActorArguments, invocation.WorkDir);
            var actorResult = WaitAndRead<BehaviorResult>(resultPath, invocation.TimeoutMs);
            state.Result = operation == "image_load"
                ? WaitAndRead<BehaviorResult>(targetResultPath, invocation.TimeoutMs)
                : actorResult;
            if (operation == "image_load") state.ResultPath = targetResultPath;
            return state;
        }
        catch
        {
            var errors = new List<string>();
            Stop(state.ActorProcess, errors);
            Stop(state.TargetProcess, errors);
            state.Dispose();
            throw;
        }
    }

    private static ProgramObservation CreateProgram(
        ControllerInvocation invocation,
        string role,
        string executable,
        Process process,
        int parentPid,
        IReadOnlyList<string> arguments,
        ProcessSnapshot? snapshot)
    {
        DateTimeOffset startedAt;
        try { startedAt = process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { startedAt = snapshot?.StartedAtUtc ?? DateTimeOffset.UtcNow; }

        DateTimeOffset? endedAt = null;
        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                endedAt = process.ExitTime.ToUniversalTime();
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出且句柄不可查询时，保留协议快照。
        }

        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = role,
            ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable),
            Sha1 = Hashing.FileSha1(executable),
            Md5 = Hashing.FileMd5(executable),
            Pid = process.Id,
            ParentPid = parentPid,
            SessionId = TrySessionId(process),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x86" => "x86",
                "arm64" => "arm64",
                _ => "x64",
            },
            CommandLine = snapshot?.CommandLine ?? FormatCommandLine(executable, arguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "ProcessActivity.Controller",
                ["nonce_in_command_line"] = true,
            },
        };
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        string operation,
        Stopwatch stopwatch,
        BehaviorResult result,
        ProgramObservation actor,
        ProgramObservation target,
        string? evidenceArtifactId)
    {
        var data = new JsonObject
        {
            ["kind"] = "process",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["target"] = ProcessReference(target),
            ["result"] = new JsonObject
            {
                ["attempted"] = result.Attempted,
                ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.Win32Error,
                ["message"] = result.Error,
            },
        };

        switch (operation)
        {
            case "create":
                data["creation"] = new JsonObject
                {
                    ["creation_flags"] = 0,
                    ["inherit_handles"] = false,
                    ["initial_thread_id"] = result.InitialThreadId,
                };
                break;
            case "terminate":
                data["termination"] = new JsonObject
                {
                    ["method"] = "TerminateProcess",
                    ["requested_exit_code"] = result.RequestedExitCode,
                    ["observed_exit_code"] = result.ObservedExitCode,
                    ["observed_exit_at_utc"] = result.ObservedExitAtUtc is { } ended ? Values.Utc(ended) : null,
                };
                break;
            case "access":
                data["access"] = new JsonObject
                {
                    ["operation_name"] = result.AccessOperationName,
                    ["requested_access_mask"] = result.RequestedAccessMask is { } requested ? (long)requested : null,
                    ["granted_access_mask"] = result.GrantedAccessMask is { } granted ? (long)granted : null,
                    ["handle_obtained"] = result.HandleObtained,
                };
                break;
            case "image_load":
                data["image"] = new JsonObject
                {
                    ["path"] = result.ImagePath,
                    ["base_address"] = result.ImageBaseAddress,
                    ["size_bytes"] = result.ImageSizeBytes,
                    ["hashes"] = result.ImageSha256 is null ? null : new JsonObject { ["sha256"] = result.ImageSha256 },
                    ["before_loaded"] = result.BeforeLoaded,
                    ["after_loaded"] = result.AfterLoaded,
                };
                break;
            case "remote_thread_create":
                data["thread"] = new JsonObject
                {
                    ["thread_id"] = result.ThreadId,
                    ["start_address"] = result.StartAddress,
                    ["parameter_address"] = result.ParameterAddress,
                    ["creation_flags"] = result.CreationFlags is { } flags ? (long)flags : null,
                };
                break;
            case "tamper":
                data["tamper"] = new JsonObject
                {
                    ["technique"] = result.TamperTechnique,
                    ["target_address"] = result.TargetAddress,
                    ["size_bytes"] = result.SizeBytes,
                    ["before_sha256"] = result.BeforeSha256,
                    ["after_sha256"] = result.AfterSha256,
                    ["memory_released"] = result.MemoryReleased,
                };
                break;
        }

        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            EventType = "process",
            EventAction = operation,
            Nonce = invocation.Nonce,
            OccurredAtUtc = result.OccurredAtUtc,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "process_activity_controller",
            CollectionMethod = CollectionMethod(operation),
            Confidence = "high",
            ActorProgramId = actor.ProgramInstanceId,
            TargetProgramId = target.ProgramInstanceId,
            Data = data,
            EvidenceRefs = evidenceArtifactId is null ? [] : [evidenceArtifactId],
        };
    }

    private static void AddFacts(
        RunDatabase database,
        ControllerInvocation invocation,
        string operation,
        BehaviorResult result,
        string eventId,
        ProgramObservation actor,
        ProgramObservation target,
        bool succeeded)
    {
        var source = "process_activity_controller";
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"process.{FactOperation(operation)}_succeeded"] = JsonValue.Create(succeeded),
            ["process.target_pid"] = JsonValue.Create(target.Pid),
            ["process.actor_pid"] = JsonValue.Create(actor.Pid),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        if (operation == "create")
        {
            values["process.child_pid"] = JsonValue.Create(target.Pid);
            values["process.parent_pid"] = JsonValue.Create(actor.Pid);
        }
        if (operation == "terminate") values["process.exit_code"] = JsonValue.Create(result.ObservedExitCode);
        if (operation == "access") values["process.access_mask"] = JsonValue.Create(result.RequestedAccessMask is { } mask ? (long)mask : 0);
        if (operation is "image_load" or "remote_thread_create") values["process.image_path"] = JsonValue.Create(result.ImagePath);
        if (operation == "image_load") values["process.image_sha256"] = JsonValue.Create(result.ImageSha256);
        if (operation == "remote_thread_create") values["process.thread_id"] = JsonValue.Create(result.ThreadId);
        if (operation == "tamper")
        {
            values["process.before_sha256"] = JsonValue.Create(result.BeforeSha256);
            values["process.after_sha256"] = JsonValue.Create(result.AfterSha256);
            values["process.memory_released"] = JsonValue.Create(result.MemoryReleased);
        }

        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = eventId,
                Key = key,
                Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = source,
                Confidence = "high",
            });
        }
    }

    private static bool VerifyOutcome(string operation, ExecutionState state)
    {
        try
        {
            return operation switch
            {
                "create" => !state.TargetProcess.HasExited && state.TargetProcess.Id == state.TargetSnapshot?.Pid,
                "terminate" => state.TargetProcess.HasExited && state.Result.ObservedExitCode == state.TargetProcess.ExitCode,
                "access" => !state.TargetProcess.HasExited && state.Result.HandleObtained == true,
                "image_load" => !state.TargetProcess.HasExited && !state.ImageWasLoadedBefore
                    && state.Result.BeforeLoaded == false && state.Result.AfterLoaded == true
                    && ModuleLoaded(state.TargetProcess, state.Result.ImagePath),
                "remote_thread_create" => !state.TargetProcess.HasExited && !state.ImageWasLoadedBefore
                    && ModuleLoaded(state.TargetProcess, state.Result.ImagePath),
                "tamper" => !state.TargetProcess.HasExited && state.Result.BeforeSha256 is not null && state.Result.AfterSha256 is not null
                    && !string.Equals(state.Result.BeforeSha256, state.Result.AfterSha256, StringComparison.Ordinal)
                    && state.Result.MemoryReleased == true,
                _ => false,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"独立观察失败：{exception.Message}");
            return false;
        }
    }

    private static bool ModuleLoaded(Process process, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath)) return false;
        process.Refresh();
        return process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(Path.GetFullPath(module.FileName), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSystemLibrary(string name)
    {
        if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("library_name 只能是系统 DLL 文件名或绝对路径。");
        }
        var path = Path.Combine(Environment.SystemDirectory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到系统 DLL。", path);
        return path;
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var started = DateTimeOffset.UtcNow;
        var beforeActor = IsAlive(state.ActorProcess);
        var beforeTarget = IsAlive(state.TargetProcess);
        var errors = new List<string>();
        Stop(state.ActorProcess, errors);
        Stop(state.TargetProcess, errors);
        var afterActor = IsAlive(state.ActorProcess);
        var afterTarget = IsAlive(state.TargetProcess);
        var succeeded = errors.Count == 0 && !afterActor && !afterTarget;
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_controlled_process_tree",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = started,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["actor_alive"] = beforeActor, ["target_alive"] = beforeTarget },
            After = new JsonObject { ["actor_alive"] = afterActor, ["target_alive"] = afterTarget },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation, string error) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Action = "stop_controlled_process_tree",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Before = new JsonObject { ["actor_alive"] = false, ["target_alive"] = false },
        After = new JsonObject { ["actor_alive"] = false, ["target_alive"] = false },
        ErrorMessage = null,
    };

    private static ArtifactObservation? CreateEvidenceArtifact(ControllerInvocation invocation, string resultPath)
    {
        if (!File.Exists(resultPath)) return null;
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        var relative = Path.GetRelativePath(runDirectory, resultPath).Replace('\\', '/');
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "behavior_protocol",
            RelativePath = relative,
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(resultPath),
            SizeBytes = new FileInfo(resultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(resultPath),
            Sensitive = false,
            Metadata = new JsonObject { ["operation"] = JsonNode.Parse(File.ReadAllText(resultPath))?["operation"]?.GetValue<string>() },
        };
    }

    private static JsonObject ProcessReference(ProgramObservation program) => new()
    {
        ["program_instance_id"] = program.ProgramInstanceId,
        ["pid"] = program.Pid,
        ["parent_pid"] = program.ParentPid,
        ["started_at_utc"] = Values.Utc(program.StartedAtUtc),
        ["executable"] = program.ExecutablePath,
        ["command_line"] = program.CommandLine,
    };

    private static Process Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"启动行为程序失败：{executable}");
    }

    private static T WaitAndRead<T>(string path, int timeoutMs) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待行为结果超时：{path}");
            Thread.Sleep(25);
        }
        return ProtocolJson.Read<T>(path);
    }

    private static void Stop(Process? process, ICollection<string> errors)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5_000)) errors.Add($"PID {process.Id} 在 5 秒内未退出。");
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }
        catch (Exception exception)
        {
            errors.Add($"停止 PID {process.Id} 失败：{exception.Message}");
        }
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static int? TrySessionId(Process process)
    {
        try { return process.SessionId; }
        catch (InvalidOperationException) { return null; }
    }

    private static int ParameterInt(JsonObject parameters, string name, int fallback) =>
        parameters[name]?.GetValue<int>() ?? fallback;

    private static uint ParameterUInt(JsonObject parameters, string name, uint fallback)
    {
        var value = parameters[name]?.GetValue<long>();
        return value is null ? fallback : checked((uint)value.Value);
    }

    private static string ParameterString(JsonObject parameters, string name, string fallback) =>
        parameters[name]?.GetValue<string>() ?? fallback;

    private static void Add(ICollection<string> arguments, string name, string value)
    {
        arguments.Add("--" + name);
        arguments.Add(value);
    }

    private static IReadOnlyList<string> ExtractArguments(string commandLine) => ["--captured-command-line", commandLine];

    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
        : value;

    private static string FactOperation(string operation) => operation switch
    {
        "remote_thread_create" => "remote_thread_create",
        _ => operation,
    };

    private static string CollectionMethod(string operation) => operation switch
    {
        "create" => "process_handle_and_ready_protocol",
        "terminate" => "process_exit_handle_query",
        "access" => "open_process_and_liveness_query",
        "image_load" => "target_module_enumeration",
        "remote_thread_create" => "thread_handle_and_target_module_enumeration",
        "tamper" => "remote_memory_readback_hash",
        _ => "controller_observation",
    };

    private static void WriteStatus(string status, string capabilityId, string operation, string? error) =>
        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0",
            ["status"] = status,
            ["capability_id"] = capabilityId,
            ["operation"] = operation,
            ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));

    private sealed class ExecutionState : IDisposable
    {
        public ExecutionState(string actorPath, string targetPath, string resultPath)
        {
            ActorPath = actorPath;
            TargetPath = targetPath;
            ResultPath = resultPath;
        }

        public string ActorPath { get; }
        public string TargetPath { get; }
        public string ResultPath { get; set; }
        public Process ActorProcess { get; set; } = null!;
        public Process TargetProcess { get; set; } = null!;
        public IReadOnlyList<string> ActorArguments { get; set; } = [];
        public IReadOnlyList<string> TargetArguments { get; set; } = [];
        public ProcessSnapshot? TargetSnapshot { get; set; }
        public BehaviorResult Result { get; set; } = null!;
        public bool ImageWasLoadedBefore { get; set; }

        public void Dispose()
        {
            ActorProcess?.Dispose();
            TargetProcess?.Dispose();
        }
    }
}
