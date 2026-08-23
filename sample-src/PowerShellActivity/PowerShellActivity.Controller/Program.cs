using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace PowerShellActivity;

internal static class Program
{
    private const string CapabilityId = "win.powershell.script_block";

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        var states = new List<ExecutionState>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!string.Equals(package.Manifest.CapabilityId, CapabilityId, StringComparison.Ordinal))
                throw new InvalidDataException($"PowerShellActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var localSucceeded = true;
            string? firstError = null;
            foreach (var (method, instanceIndex) in PowerShellScriptPlans.Methods.Select((value, index) => (value, index)))
            {
                var state = Execute(invocation, package, parameters, method, instanceIndex);
                states.Add(state);
                var actor = CreateActorProgram(invocation, state);
                var target = CreateTargetProgram(invocation, state, actor.Pid);
                database.AddProgram(actor);
                database.AddProgram(target);
                var verified = Verify(state);
                localSucceeded &= verified;
                firstError ??= verified ? null : state.Result.Error ?? $"{state.Plan.Title}没有通过本地独立验证。";
                var artifact = CreateArtifact(invocation, state);
                database.AddArtifact(artifact);
                var localEvent = CreateEvent(invocation, stopwatch, state, actor, target, artifact.ArtifactId, verified);
                database.AddEvent(localEvent);
                AddFacts(database, invocation, state, actor, target, localEvent.LocalEventId, verified);
                SubtestTiming.WaitBetween(invocation, instanceIndex, PowerShellScriptPlans.Methods.Length, state.Plan.Title,
                    instanceIndex + 1 < PowerShellScriptPlans.Methods.Length
                        ? PowerShellScriptPlans.Create(PowerShellScriptPlans.Methods[instanceIndex + 1], invocation.Nonce).Title
                        : null);
            }

            AddFact(database, invocation, "powershell.script_block_succeeded", JsonValue.Create(localSucceeded), null);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);
            var cleanups = states.Select(state => Cleanup(invocation, state)).ToArray();
            foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
            var cleanupFailure = cleanups.FirstOrDefault(value => value.Status != "succeeded");
            if (cleanupFailure is not null)
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "POWERSHELL_CLEANUP_FAILED", cleanupFailure.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", cleanupFailure.ErrorMessage);
                return 30;
            }

            database.CompleteCapability(invocation.CaseRunId, localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR",
                DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, localSucceeded ? null : "POWERSHELL_SUBTEST_FAILED",
                localSucceeded ? null : firstError);
            WriteStatus(localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR", firstError);
            return localSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanups = states.Count == 0 ? [EmptyCleanup(invocation)] : states.Select(state => Cleanup(invocation, state)).ToArray();
                    foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
                    var cleanupSucceeded = cleanups.All(value => value.Status == "succeeded");
                    database.CompleteCapability(invocation.CaseRunId, cleanupSucceeded ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "POWERSHELL_CONTROLLER_ERROR", exception.Message);
                    return cleanupSucceeded ? 20 : 30;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally
        {
            foreach (var state in states) state.Dispose();
            database?.Dispose();
        }
    }

    private static ExecutionState Execute(
        ControllerInvocation invocation,
        CapabilityPackage package,
        JsonObject parameters,
        string method,
        int instanceIndex)
    {
        var plan = PowerShellScriptPlans.Create(method, invocation.Nonce);
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var readyPath = Path.Combine(invocation.WorkDir, $"powershell-target-ready-{method}.json");
        var gatePath = Path.Combine(invocation.WorkDir, $"powershell-execution-gate-{method}.json");
        var resultPath = Path.Combine(invocation.WorkDir, $"powershell-actor-result-{method}.json");
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_000;
        var roleTimeout = Math.Min(invocation.TimeoutMs, 120_000);
        var arguments = new[]
        {
            "--method", method,
            "--nonce", invocation.Nonce,
            "--ready", readyPath,
            "--gate", gatePath,
            "--result", resultPath,
            "--timeout-ms", roleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        try
        {
            var ready = WaitAndRead<PowerShellTargetReady>(readyPath, invocation.TimeoutMs, actor, "PowerShell 目标就绪");
            ValidateReady(plan, ready, actor.Id);
            var targetSnapshot = ObserveLiveTarget(ready);
            ProtocolJson.WriteAtomic(gatePath, new PowerShellExecutionGate { Method = method, CreatedAtUtc = DateTimeOffset.UtcNow });
            var result = WaitAndRead<PowerShellBehaviorResult>(resultPath, invocation.TimeoutMs, actor, "PowerShell Actor 结果");
            WaitForExit(actor, invocation.TimeoutMs, "PowerShell Actor");
            return new ExecutionState(instanceIndex, plan, actorPath, arguments, readyPath, gatePath, resultPath,
                actor, ready, targetSnapshot, result);
        }
        catch
        {
            Stop(actor, []);
            actor.Dispose();
            throw;
        }
    }

    private static void ValidateReady(ScriptPlan plan, PowerShellTargetReady ready, int actorPid)
    {
        if (!string.Equals(ready.Method, plan.Method, StringComparison.Ordinal)
            || ready.TargetProcessId <= 0
            || !string.Equals(Path.GetFullPath(ready.TargetExecutable), Path.GetFullPath(plan.PowerShellExecutable), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ready.TargetCommandLine, plan.TargetCommandLine, StringComparison.Ordinal)
            || ready.TargetProcessId == actorPid)
            throw new InvalidDataException("PowerShell 目标就绪协议与本地计划不一致。");
    }

    private static TargetSnapshot ObserveLiveTarget(PowerShellTargetReady ready)
    {
        using var target = Process.GetProcessById(ready.TargetProcessId);
        if (target.HasExited) throw new InvalidOperationException("PowerShell 目标在 Controller 观察前已经退出。");
        string? actualPath = null;
        try { actualPath = target.MainModule?.FileName; } catch { }
        if (!string.IsNullOrWhiteSpace(actualPath)
            && !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(ready.TargetExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Controller 读取到的 PowerShell 目标路径与 Actor 协议不一致。");
        DateTimeOffset startedAt;
        try { startedAt = target.StartTime.ToUniversalTime(); } catch { startedAt = ready.StartedAtUtc; }
        int? sessionId;
        try { sessionId = target.SessionId; } catch { sessionId = null; }
        return new TargetSnapshot(startedAt, sessionId);
    }

    private static bool Verify(ExecutionState state)
    {
        var result = state.Result;
        return result.Succeeded
            && result.WarmupSucceeded
            && result.OutputVerified
            && result.ExitCode == 0
            && result.ActorProcessId == state.Actor.Id
            && result.TargetProcessId == state.Ready.TargetProcessId
            && string.Equals(result.Method, state.Plan.Method, StringComparison.Ordinal)
            && string.Equals(result.InvocationKind, state.Plan.InvocationKind, StringComparison.Ordinal)
            && string.Equals(result.TargetExecutable, state.Plan.PowerShellExecutable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(result.TargetCommandLine, state.Plan.TargetCommandLine, StringComparison.Ordinal)
            && result.TargetCommandLine.Contains(state.Plan.CommandFormToken, StringComparison.Ordinal)
            && string.Equals(result.SubmittedCommand, state.Plan.SubmittedCommand, StringComparison.Ordinal)
            && string.Equals(result.SubmittedCommandSha256, state.Plan.SubmittedCommandSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(result.ExpectedContent, state.Plan.ExpectedContent, StringComparison.Ordinal)
            && string.Equals(result.ExpectedContentSha256, state.Plan.ExpectedContentSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(result.Marker, state.Plan.Marker, StringComparison.Ordinal)
            && result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), state.Plan.Marker, StringComparison.Ordinal))
            && result.OccurredAtUtc >= result.TargetStartedAtUtc
            && result.CompletedAtUtc >= result.OccurredAtUtc;
    }

    private static ProgramObservation CreateActorProgram(ControllerInvocation invocation, ExecutionState state)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = state.Actor.StartTime.ToUniversalTime(); } catch { startedAt = state.Result.TargetStartedAtUtc; }
        try { endedAt = state.Actor.ExitTime.ToUniversalTime(); exitCode = state.Actor.ExitCode; } catch { endedAt = null; exitCode = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = state.Plan.Method,
            InstanceIndex = state.InstanceIndex,
            ExecutablePath = state.ActorPath,
            Sha256 = Hashing.FileSha256(state.ActorPath),
            Sha1 = Hashing.FileSha1(state.ActorPath),
            Md5 = Hashing.FileMd5(state.ActorPath),
            Pid = state.Actor.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(state.Actor),
            Architecture = Architecture(),
            CommandLine = FormatCommandLine(state.ActorPath, state.ActorArguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["role"] = "powershell_launcher" },
        };
    }

    private static ProgramObservation CreateTargetProgram(ControllerInvocation invocation, ExecutionState state, int actorPid) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Role = "target",
        InstanceName = state.Plan.Method,
        InstanceIndex = state.InstanceIndex,
        ExecutablePath = state.Ready.TargetExecutable,
        Sha256 = Hashing.FileSha256(state.Ready.TargetExecutable),
        Sha1 = Hashing.FileSha1(state.Ready.TargetExecutable),
        Md5 = Hashing.FileMd5(state.Ready.TargetExecutable),
        Pid = state.Ready.TargetProcessId,
        ParentPid = actorPid,
        SessionId = state.TargetSnapshot.SessionId,
        Architecture = Architecture(),
        CommandLine = state.Ready.TargetCommandLine,
        WorkingDirectory = invocation.WorkDir,
        StartedAtUtc = state.TargetSnapshot.StartedAtUtc,
        EndedAtUtc = state.Result.TargetEndedAtUtc,
        ExitCode = state.Result.ExitCode,
        Metadata = new JsonObject { ["method"] = state.Plan.Method, ["invocation_kind"] = state.Plan.InvocationKind, ["command_mode"] = "stdin" },
    };

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        Stopwatch stopwatch,
        ExecutionState state,
        ProgramObservation actor,
        ProgramObservation target,
        string artifactId,
        bool succeeded) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = state.InstanceIndex + 1,
        EventType = "powershell",
        EventAction = "script_block",
        Nonce = invocation.Nonce,
        OccurredAtUtc = state.Result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "powershell_activity_controller",
        CollectionMethod = $"powershell_stdin_{state.Plan.InvocationKind}_nonce_output_handshake",
        Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        TargetProgramId = target.ProgramInstanceId,
        EvidenceRefs = [artifactId],
        Data = new JsonObject
        {
            ["kind"] = "powershell",
            ["operation"] = "script_block",
            ["actor"] = ProcessReference(actor),
            ["script_block"] = new JsonObject
            {
                ["script_block_id"] = null,
                ["runspace_id"] = null,
                ["pipeline_id"] = null,
                ["engine_version"] = state.Result.EngineVersion,
                ["host_name"] = "ConsoleHost",
                ["script_path"] = null,
                ["script_text_sha256"] = state.Plan.ExpectedContentSha256,
                ["script_text"] = state.Plan.ExpectedContent,
                ["command_line"] = target.CommandLine,
            },
            ["result"] = new JsonObject { ["attempted"] = true, ["succeeded"] = succeeded, ["exit_code"] = state.Result.ExitCode, ["error"] = state.Result.Error },
        },
    };

    private static void AddFacts(
        RunDatabase database,
        ControllerInvocation invocation,
        ExecutionState state,
        ProgramObservation actor,
        ProgramObservation target,
        string eventId,
        bool succeeded)
    {
        var prefix = $"powershell.{state.Plan.Method}";
        var values = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}.succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(state.Result.OccurredAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(state.Result.CompletedAtUtc)),
            [$"{prefix}.invocation_kind"] = JsonValue.Create(state.Plan.InvocationKind),
            [$"{prefix}.marker"] = JsonValue.Create(state.Plan.Marker),
            [$"{prefix}.submitted_command"] = JsonValue.Create(state.Plan.SubmittedCommand),
            [$"{prefix}.submitted_command_sha256"] = JsonValue.Create(state.Plan.SubmittedCommandSha256),
            [$"{prefix}.expected_content"] = JsonValue.Create(state.Plan.ExpectedContent),
            [$"{prefix}.expected_content_sha256"] = JsonValue.Create(state.Plan.ExpectedContentSha256),
            [$"{prefix}.command_form_token"] = JsonValue.Create(state.Plan.CommandFormToken),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid),
            [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            [$"{prefix}.target_pid"] = JsonValue.Create(target.Pid),
            [$"{prefix}.target_executable"] = JsonValue.Create(target.ExecutablePath),
            [$"{prefix}.target_command_line"] = JsonValue.Create(target.CommandLine),
            [$"{prefix}.warmup_succeeded"] = JsonValue.Create(state.Result.WarmupSucceeded),
            [$"{prefix}.output_verified"] = JsonValue.Create(state.Result.OutputVerified),
            [$"{prefix}.exit_code"] = JsonValue.Create(state.Result.ExitCode),
            [$"{prefix}.engine_version"] = JsonValue.Create(state.Result.EngineVersion),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, ExecutionState state)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "powershell_behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, state.ResultPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(state.ResultPath),
            SizeBytes = new FileInfo(state.ResultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(state.ResultPath),
            Sensitive = false,
            Metadata = new JsonObject { ["method"] = state.Plan.Method },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(state.Actor, errors);
        StopTarget(state, errors);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = state.InstanceIndex + 1,
            Action = $"stop_powershell_{state.Plan.Method}_process_tree",
            Status = errors.Count == 0 && !IsAlive(state.Actor) && !TargetIsAlive(state) ? "succeeded" : "failed",
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["actor_pid"] = state.Actor.Id, ["target_pid"] = state.Ready.TargetProcessId },
            After = new JsonObject { ["actor_alive"] = IsAlive(state.Actor), ["target_alive"] = TargetIsAlive(state) },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        Action = "no_powershell_process_started",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Before = new JsonObject(),
        After = new JsonObject(),
    };

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 PowerShell 行为程序：{executable}");
    }

    private static T WaitAndRead<T>(string path, int timeoutMs, Process process, string stage) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"{stage}前程序已退出：{process.ExitCode}");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待{stage}超时。");
            Thread.Sleep(5);
        }
        return ProtocolJson.Read<T>(path);
    }

    private static void WaitForExit(Process process, int timeoutMs, string label)
    {
        if (process.WaitForExit(timeoutMs)) return;
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"等待{label}退出超时：PID {process.Id}");
    }

    private static void Stop(Process process, ICollection<string> errors)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5_000)) errors.Add($"Actor PID {process.Id} 未退出。");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception exception) { errors.Add(exception.Message); }
    }

    private static void StopTarget(ExecutionState state, ICollection<string> errors)
    {
        try
        {
            using var target = Process.GetProcessById(state.Ready.TargetProcessId);
            var startedAtUtc = target.StartTime.ToUniversalTime();
            if (Math.Abs((startedAtUtc - state.TargetSnapshot.StartedAtUtc).TotalMilliseconds) > 1_000) return;
            target.Kill(entireProcessTree: true);
            if (!target.WaitForExit(5_000)) errors.Add($"PowerShell PID {target.Id} 未退出。");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Exception exception) { errors.Add(exception.Message); }
    }

    private static bool TargetIsAlive(ExecutionState state)
    {
        try
        {
            using var target = Process.GetProcessById(state.Ready.TargetProcessId);
            return Math.Abs((target.StartTime.ToUniversalTime() - state.TargetSnapshot.StartedAtUtc).TotalMilliseconds) <= 1_000 && !target.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsAlive(Process process) { try { return !process.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static JsonObject ProcessReference(ProgramObservation value) => new() { ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid, ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine };
    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) => database.AddFact(new LocalFactObservation { CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "powershell_activity_controller", Confidence = "high" });
    private static void WriteStatus(string status, string? error) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = CapabilityId, ["operation"] = "script_block", ["methods"] = 2, ["error"] = error }.ToJsonString(JsonDefaults.Options));

    private sealed record TargetSnapshot(DateTimeOffset StartedAtUtc, int? SessionId);

    private sealed class ExecutionState(
        int instanceIndex,
        ScriptPlan plan,
        string actorPath,
        IReadOnlyList<string> actorArguments,
        string readyPath,
        string gatePath,
        string resultPath,
        Process actor,
        PowerShellTargetReady ready,
        TargetSnapshot targetSnapshot,
        PowerShellBehaviorResult result) : IDisposable
    {
        public int InstanceIndex { get; } = instanceIndex;
        public ScriptPlan Plan { get; } = plan;
        public string ActorPath { get; } = actorPath;
        public IReadOnlyList<string> ActorArguments { get; } = actorArguments;
        public string ReadyPath { get; } = readyPath;
        public string GatePath { get; } = gatePath;
        public string ResultPath { get; } = resultPath;
        public Process Actor { get; } = actor;
        public PowerShellTargetReady Ready { get; } = ready;
        public TargetSnapshot TargetSnapshot { get; } = targetSnapshot;
        public PowerShellBehaviorResult Result { get; } = result;
        public void Dispose() => Actor.Dispose();
    }
}
