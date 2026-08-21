using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace BitsActivity;

internal static class Program
{
    private const string CapabilityId = "win.bits.job";

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
                throw new InvalidDataException($"BitsActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var localSucceeded = true;
            string? firstError = null;
            foreach (var (method, instanceIndex) in BitsPlans.Methods.Select((value, index) => (value, index)))
            {
                var state = Execute(invocation, package, parameters, method, instanceIndex);
                states.Add(state);
                var actor = CreateActorProgram(invocation, state);
                var initiator = CreateInitiatorProgram(invocation, state, actor);
                database.AddProgram(actor);
                database.AddProgram(initiator);
                var verified = Verify(state);
                localSucceeded &= verified;
                firstError ??= verified ? null : state.Result.Error ?? $"{state.Plan.Title}没有通过本地独立验证。";
                var artifact = CreateArtifact(invocation, state);
                database.AddArtifact(artifact);
                var localEvent = CreateEvent(invocation, stopwatch, state, initiator, artifact.ArtifactId, verified);
                database.AddEvent(localEvent);
                AddFacts(database, invocation, state, actor, initiator, localEvent.LocalEventId, verified);
            }

            AddFact(database, invocation, "bits.job_succeeded", JsonValue.Create(localSucceeded), null);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);
            var cleanups = states.Select(state => Cleanup(invocation, state)).ToArray();
            foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
            var cleanupFailure = cleanups.FirstOrDefault(value => value.Status != "succeeded");
            if (cleanupFailure is not null)
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "BITS_CLEANUP_FAILED", cleanupFailure.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", cleanupFailure.ErrorMessage);
                return 30;
            }

            database.CompleteCapability(invocation.CaseRunId, localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR",
                DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, localSucceeded ? null : "BITS_SUBTEST_FAILED",
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
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "BITS_CONTROLLER_ERROR", exception.Message);
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

    private static ExecutionState Execute(ControllerInvocation invocation, CapabilityPackage package, JsonObject parameters,
        string method, int instanceIndex)
    {
        var plan = BitsPlans.Create(method, invocation.Nonce);
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var methodWorkDir = Path.Combine(invocation.WorkDir, $"bits-{method}");
        Directory.CreateDirectory(methodWorkDir);
        var readyPath = Path.Combine(methodWorkDir, "bits-job-ready.json");
        var gatePath = Path.Combine(methodWorkDir, "bits-execution-gate.json");
        var resultPath = Path.Combine(methodWorkDir, "bits-actor-result.json");
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_000;
        var roleTimeout = Math.Min(invocation.TimeoutMs, 180_000);
        var arguments = new[]
        {
            "--method", method,
            "--nonce", invocation.Nonce,
            "--ready", readyPath,
            "--gate", gatePath,
            "--result", resultPath,
            "--work-dir", methodWorkDir,
            "--timeout-ms", roleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        try
        {
            var ready = WaitAndRead<BitsJobReady>(readyPath, invocation.TimeoutMs, actor, "BITS 任务就绪");
            ValidateReady(invocation, plan, methodWorkDir, ready, actor.Id);
            var independent = BitsCom.Inspect(ready.JobId);
            ValidateIndependentSnapshot(ready, independent);
            ProtocolJson.WriteAtomic(gatePath, new BitsExecutionGate { Method = method, CreatedAtUtc = DateTimeOffset.UtcNow });
            var result = WaitAndRead<BitsBehaviorResult>(resultPath, invocation.TimeoutMs, actor, "BITS Actor 结果");
            WaitForExit(actor, invocation.TimeoutMs, "BITS Actor");
            return new ExecutionState(instanceIndex, plan, actorPath, arguments, methodWorkDir, readyPath, gatePath,
                resultPath, actor, ready, independent, result);
        }
        catch
        {
            Stop(actor, []);
            actor.Dispose();
            throw;
        }
    }

    private static void ValidateReady(ControllerInvocation invocation, BitsPlan plan, string methodWorkDir,
        BitsJobReady ready, int actorPid)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(methodWorkDir, plan.LocalFileName));
        if (!string.Equals(ready.Method, plan.Method, StringComparison.Ordinal)
            || !string.Equals(ready.InvocationKind, plan.InvocationKind, StringComparison.Ordinal)
            || ready.ActorProcessId != actorPid
            || ready.JobId == Guid.Empty
            || !string.Equals(ready.DisplayName, plan.DisplayName, StringComparison.Ordinal)
            || ready.JobType != nameof(BG_JOB_TYPE.DOWNLOAD)
            || ready.State != nameof(BG_JOB_STATE.SUSPENDED)
            || string.IsNullOrWhiteSpace(ready.OwnerSid)
            || !Uri.TryCreate(ready.RemoteUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Host != "127.0.0.1"
            || !string.Equals(Path.GetFullPath(ready.LocalPath), expectedPath, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(ready.LocalPath).StartsWith(Path.GetFullPath(invocation.WorkDir), StringComparison.OrdinalIgnoreCase)
            || ready.PayloadSize <= 0
            || ready.PayloadSha256.Length != 64)
            throw new InvalidDataException("BITS 任务就绪协议与本地计划不一致。");
        if (plan.Method == "bitsadmin" && (ready.InitiatorProcess is null
            || !string.Equals(Path.GetFileName(ready.InitiatorProcess.Executable), "bitsadmin.exe", StringComparison.OrdinalIgnoreCase)
            || ready.InitiatorProcess.ExitCode != 0
            || !ready.InitiatorProcess.CommandLine.Contains(plan.DisplayName, StringComparison.Ordinal)))
            throw new InvalidDataException("bitsadmin 子测试缺少可信的命令进程观测。");
    }

    private static void ValidateIndependentSnapshot(BitsJobReady ready, BitsJobSnapshot snapshot)
    {
        if (snapshot.JobId != ready.JobId
            || !string.Equals(snapshot.DisplayName, ready.DisplayName, StringComparison.Ordinal)
            || !string.Equals(snapshot.JobType, ready.JobType, StringComparison.Ordinal)
            || !string.Equals(snapshot.State, ready.State, StringComparison.Ordinal)
            || !string.Equals(snapshot.OwnerSid, ready.OwnerSid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.RemoteUrl, ready.RemoteUrl, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(snapshot.LocalPath), Path.GetFullPath(ready.LocalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Controller 独立读取的 BITS Job/File 快照与 Actor 协议不一致。");
    }

    private static bool Verify(ExecutionState state)
    {
        var value = state.Result;
        return value.Succeeded
            && value.ControllerGateObserved
            && value.DownloadVerified
            && value.JobRemovedAfterComplete
            && value.ActorProcessId == state.Actor.Id
            && value.JobId == state.Ready.JobId
            && string.Equals(value.Method, state.Plan.Method, StringComparison.Ordinal)
            && string.Equals(value.InvocationKind, state.Plan.InvocationKind, StringComparison.Ordinal)
            && string.Equals(value.DisplayName, state.Plan.DisplayName, StringComparison.Ordinal)
            && value.JobType == nameof(BG_JOB_TYPE.DOWNLOAD)
            && value.StateBeforeResume == nameof(BG_JOB_STATE.SUSPENDED)
            && value.StateBeforeComplete == nameof(BG_JOB_STATE.TRANSFERRED)
            && value.StateAfterComplete == nameof(BG_JOB_STATE.ACKNOWLEDGED)
            && value.BytesTotal == value.PayloadSize
            && value.BytesTransferred == value.PayloadSize
            && value.PayloadSize == state.Ready.PayloadSize
            && string.Equals(value.PayloadSha256, state.Ready.PayloadSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.DownloadedSha256, value.PayloadSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.RemoteUrl, state.Ready.RemoteUrl, StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(value.LocalPath), Path.GetFullPath(state.Ready.LocalPath), StringComparison.OrdinalIgnoreCase)
            && value.HttpRequestCount > 0
            && value.ResumedAtUtc >= value.CreatedAtUtc
            && value.TransferredAtUtc >= value.ResumedAtUtc
            && value.CompletedAtUtc >= value.TransferredAtUtc
            && File.Exists(value.LocalPath)
            && Hashing.FileSha256(value.LocalPath).Equals(value.PayloadSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static ProgramObservation CreateActorProgram(ControllerInvocation invocation, ExecutionState state)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = state.Actor.StartTime.ToUniversalTime(); } catch { startedAt = state.Result.CreatedAtUtc; }
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
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["role"] = "bits_orchestrated_actor" },
        };
    }

    private static ProgramObservation CreateInitiatorProgram(ControllerInvocation invocation, ExecutionState state, ProgramObservation actor)
    {
        if (state.Result.InitiatorProcess is not { } process)
        {
            return new ProgramObservation
            {
                CaseRunId = invocation.CaseRunId,
                Role = "target",
                InstanceName = state.Plan.Method,
                InstanceIndex = state.InstanceIndex,
                ExecutablePath = actor.ExecutablePath,
                Sha256 = actor.Sha256,
                Sha1 = actor.Sha1,
                Md5 = actor.Md5,
                Pid = actor.Pid,
                ParentPid = actor.ParentPid,
                SessionId = actor.SessionId,
                Architecture = actor.Architecture,
                CommandLine = actor.CommandLine,
                WorkingDirectory = actor.WorkingDirectory,
                StartedAtUtc = actor.StartedAtUtc,
                EndedAtUtc = actor.EndedAtUtc,
                ExitCode = actor.ExitCode,
                Metadata = new JsonObject { ["method"] = state.Plan.Method, ["invocation_kind"] = state.Plan.InvocationKind },
            };
        }

        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "target",
            InstanceName = state.Plan.Method,
            InstanceIndex = state.InstanceIndex,
            ExecutablePath = process.Executable,
            Sha256 = Hashing.FileSha256(process.Executable),
            Sha1 = Hashing.FileSha1(process.Executable),
            Md5 = Hashing.FileMd5(process.Executable),
            Pid = process.ProcessId,
            ParentPid = actor.Pid,
            SessionId = null,
            Architecture = Architecture(),
            CommandLine = process.CommandLine,
            WorkingDirectory = state.MethodWorkDir,
            StartedAtUtc = process.StartedAtUtc,
            EndedAtUtc = process.EndedAtUtc,
            ExitCode = process.ExitCode,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["invocation_kind"] = state.Plan.InvocationKind },
        };
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, ExecutionState state)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "bits_downloaded_payload",
            RelativePath = Path.GetRelativePath(runDirectory, state.Result.LocalPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(state.Result.LocalPath),
            SizeBytes = new FileInfo(state.Result.LocalPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(state.Result.LocalPath),
            Sensitive = false,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["job_id"] = state.Result.JobId.ToString("D") },
        };
    }

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, Stopwatch stopwatch,
        ExecutionState state, ProgramObservation initiator, string artifactId, bool succeeded) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = state.InstanceIndex + 1,
        EventType = "bits",
        EventAction = "job",
        Nonce = invocation.Nonce,
        OccurredAtUtc = state.Result.CreatedAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "bits_activity_controller",
        CollectionMethod = $"bits_{state.Plan.InvocationKind}_com_job_reopen_file_and_hash_verification",
        Confidence = "high",
        ActorProgramId = initiator.ProgramInstanceId,
        EvidenceRefs = [artifactId],
        Data = new JsonObject
        {
            ["kind"] = "bits",
            ["operation"] = "job",
            ["actor"] = ProcessReference(initiator),
            ["job"] = new JsonObject
            {
                ["job_id"] = state.Result.JobId.ToString("D"),
                ["display_name"] = state.Result.DisplayName,
                ["job_type"] = state.Result.JobType,
                ["state"] = state.Result.StateBeforeComplete,
                ["owner_sid"] = state.Result.OwnerSid,
                ["remote_url"] = state.Result.RemoteUrl,
                ["local_path"] = state.Result.LocalPath,
                ["bytes_total"] = state.Result.BytesTotal,
                ["bytes_transferred"] = state.Result.BytesTransferred,
                ["notification_command"] = null,
            },
            ["before"] = new JsonObject { ["exists"] = false, ["job_id"] = state.Result.JobId.ToString("D") },
            ["after"] = new JsonObject
            {
                ["exists"] = true,
                ["job_created"] = true,
                ["transfer_completed"] = true,
                ["download_verified"] = state.Result.DownloadVerified,
                ["job_removed_after_complete"] = state.Result.JobRemovedAfterComplete,
            },
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = succeeded,
                ["win32_error"] = null,
                ["message"] = state.Result.Error,
            },
        },
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, ExecutionState state,
        ProgramObservation actor, ProgramObservation initiator, string eventId, bool succeeded)
    {
        var prefix = $"bits.{state.Plan.Method}";
        var values = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}.succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(state.Result.CreatedAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(state.Result.CompletedAtUtc)),
            [$"{prefix}.invocation_kind"] = JsonValue.Create(state.Plan.InvocationKind),
            [$"{prefix}.job_id"] = JsonValue.Create(state.Result.JobId.ToString("D")),
            [$"{prefix}.display_name"] = JsonValue.Create(state.Result.DisplayName),
            [$"{prefix}.job_type"] = JsonValue.Create(state.Result.JobType),
            [$"{prefix}.state_before_resume"] = JsonValue.Create(state.Result.StateBeforeResume),
            [$"{prefix}.state_before_complete"] = JsonValue.Create(state.Result.StateBeforeComplete),
            [$"{prefix}.state_after_complete"] = JsonValue.Create(state.Result.StateAfterComplete),
            [$"{prefix}.owner_sid"] = JsonValue.Create(state.Result.OwnerSid),
            [$"{prefix}.remote_url"] = JsonValue.Create(state.Result.RemoteUrl),
            [$"{prefix}.local_path"] = JsonValue.Create(state.Result.LocalPath),
            [$"{prefix}.bytes_total"] = JsonValue.Create(state.Result.BytesTotal),
            [$"{prefix}.bytes_transferred"] = JsonValue.Create(state.Result.BytesTransferred),
            [$"{prefix}.payload_sha256"] = JsonValue.Create(state.Result.PayloadSha256),
            [$"{prefix}.downloaded_sha256"] = JsonValue.Create(state.Result.DownloadedSha256),
            [$"{prefix}.download_verified"] = JsonValue.Create(state.Result.DownloadVerified),
            [$"{prefix}.job_removed_after_complete"] = JsonValue.Create(state.Result.JobRemovedAfterComplete),
            [$"{prefix}.controller_job_verified"] = JsonValue.Create(true),
            [$"{prefix}.http_request_count"] = JsonValue.Create(state.Result.HttpRequestCount),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid),
            [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.initiator_pid"] = JsonValue.Create(initiator.Pid),
            [$"{prefix}.initiator_executable"] = JsonValue.Create(initiator.ExecutablePath),
            [$"{prefix}.initiator_command_line"] = JsonValue.Create(initiator.CommandLine),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(state.Actor, errors);
        bool jobRemoved;
        try { jobRemoved = BitsCom.CancelIfExists(state.Ready.JobId); }
        catch (Exception exception) { errors.Add(exception.Message); jobRemoved = false; }
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = state.InstanceIndex + 1,
            Action = $"cancel_bits_job_{state.Plan.Method}_if_present",
            Status = errors.Count == 0 && !IsAlive(state.Actor) && jobRemoved ? "succeeded" : "failed",
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["actor_pid"] = state.Actor.Id, ["job_id"] = state.Ready.JobId.ToString("D") },
            After = new JsonObject { ["actor_alive"] = IsAlive(state.Actor), ["job_exists"] = !jobRemoved },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        Action = "no_bits_job_started",
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
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 BITS 行为程序：{executable}");
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

    private static bool IsAlive(Process process) { try { return !process.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static JsonObject ProcessReference(ProgramObservation value) => new() { ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid, ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine };
    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) => database.AddFact(new LocalFactObservation { CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "bits_activity_controller", Confidence = "high" });
    private static void WriteStatus(string status, string? error) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = CapabilityId, ["operation"] = "job", ["methods"] = 2, ["error"] = error }.ToJsonString(JsonDefaults.Options));

    private sealed class ExecutionState(
        int instanceIndex, BitsPlan plan, string actorPath, IReadOnlyList<string> actorArguments,
        string methodWorkDir, string readyPath, string gatePath, string resultPath, Process actor,
        BitsJobReady ready, BitsJobSnapshot independentSnapshot, BitsBehaviorResult result) : IDisposable
    {
        public int InstanceIndex { get; } = instanceIndex;
        public BitsPlan Plan { get; } = plan;
        public string ActorPath { get; } = actorPath;
        public IReadOnlyList<string> ActorArguments { get; } = actorArguments;
        public string MethodWorkDir { get; } = methodWorkDir;
        public string ReadyPath { get; } = readyPath;
        public string GatePath { get; } = gatePath;
        public string ResultPath { get; } = resultPath;
        public Process Actor { get; } = actor;
        public BitsJobReady Ready { get; } = ready;
        public BitsJobSnapshot IndependentSnapshot { get; } = independentSnapshot;
        public BitsBehaviorResult Result { get; } = result;
        public void Dispose() => Actor.Dispose();
    }
}
