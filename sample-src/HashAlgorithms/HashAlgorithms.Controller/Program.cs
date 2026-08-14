using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdrTest;

namespace HashAlgorithms;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.hash.md5"] = "md5",
        ["win.hash.sha"] = "sha",
        ["win.hash.imphash"] = "imphash",
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        ExecutionState? state = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
                throw new InvalidDataException($"HashAlgorithms Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            state = Execute(invocation, package, operation, parameters);
            var actor = CreateProgram(invocation, state);
            database.AddProgram(actor);

            var locallyVerified = VerifyOutcome(operation, state);
            var succeeded = state.Result.Succeeded && locallyVerified;
            var evidence = CreateEvidenceArtifact(invocation, state);
            database.AddArtifact(evidence);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state, actor, evidence.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, state, localEvent.LocalEventId, actor, succeeded);
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                Key = "correlation.nonce",
                Value = JsonValue.Create(invocation.Nonce),
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "hash_algorithms_controller",
                Confidence = "high",
            });

            var cleanup = Cleanup(invocation, state);
            database.AddCleanup(cleanup);
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "HASH_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = succeeded ? null : state.Result.Error ?? "Controller 独立复算摘要未通过。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "HASH_VERIFICATION_FAILED", error);
            WriteStatus(status, package.Manifest.CapabilityId, operation, error);
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                var cleanup = state is null ? EmptyCleanup(invocation) : Cleanup(invocation, state);
                try
                {
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "HASH_ALGORITHMS_CONTROLLER_ERROR", exception.Message);
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
            state?.Dispose();
            database?.Dispose();
        }
    }

    private static ExecutionState Execute(
        ControllerInvocation invocation,
        CapabilityPackage package,
        string operation,
        JsonObject parameters)
    {
        var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var tag = new string(invocation.Nonce.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToLowerInvariant();
        var extension = operation == "imphash" ? ".exe" : ".json";
        var path = Path.Combine(invocation.WorkDir, $"edrtest_{tag}_hash_{operation}{extension}");
        var resultPath = Path.Combine(invocation.WorkDir, "hash-behavior-result.json");
        Directory.CreateDirectory(invocation.WorkDir);
        if (File.Exists(path)) throw new IOException($"能力工作目录中已存在本轮测试文件：{path}");

        var payloadSize = parameters["payload_size"]?.GetValue<int>() ?? 8_192;
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        var arguments = new[]
        {
            "--operation", operation,
            "--path", path,
            "--nonce", invocation.Nonce,
            "--result", resultPath,
            "--payload-size", payloadSize.ToString(),
            "--hold-ms", holdMs.ToString(),
        };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs);
        if (!actor.WaitForExit(invocation.TimeoutMs))
        {
            actor.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待哈希行为 Actor 退出超时：PID {actor.Id}");
        }
        return new ExecutionState(operation, actorPath, arguments, actor, resultPath, path, result);
    }

    private static bool VerifyOutcome(string operation, ExecutionState state)
    {
        var current = Snapshot(state.Path);
        if (!current.Exists || current.SizeBytes != state.Result.BytesWritten || current.Md5 != state.Result.After.Md5
            || current.Sha256 != state.Result.After.Sha256 || !state.Path.EndsWith(operation == "imphash" ? ".exe" : ".json", StringComparison.OrdinalIgnoreCase))
            return false;
        return operation switch
        {
            "md5" => IsJson(state.Path) && state.Result.Algorithm == "md5" && state.Result.Digest == current.Md5,
            "sha" => IsJson(state.Path) && state.Result.Algorithm == "sha256" && state.Result.Digest == current.Sha256
                && current.Sha1?.Length == 40 && current.Sha512?.Length == 128,
            "imphash" => state.Result.Algorithm == "imphash" && current.IsPortableExecutable && current.ImportCount > 0
                && state.Result.Digest == current.ImpHash && state.Result.SourcePortableExecutablePath is { } source
                && File.Exists(source) && Hashing.FileSha256(source) == current.Sha256
                && ImportHashCalculator.TryCompute(source, out var sourceImpHash, out _) && sourceImpHash?.Digest == current.ImpHash,
            _ => false,
        };
    }

    private static ProgramObservation CreateProgram(ControllerInvocation invocation, ExecutionState state)
    {
        DateTimeOffset startedAt;
        try { startedAt = state.Actor.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { startedAt = state.Result.OccurredAtUtc; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = state.Operation,
            InstanceIndex = 0,
            ExecutablePath = state.ActorPath,
            Sha256 = Hashing.FileSha256(state.ActorPath),
            Sha1 = Hashing.FileSha1(state.ActorPath),
            Md5 = Hashing.FileMd5(state.ActorPath),
            Pid = state.Actor.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(state.Actor),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x86" => "x86",
                "arm64" => "arm64",
                _ => "x64",
            },
            CommandLine = FormatCommandLine(state.ActorPath, state.ActorArguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = state.Actor.ExitTime.ToUniversalTime(),
            ExitCode = state.Actor.ExitCode,
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "HashAlgorithms.Controller",
                ["nonce_in_command_line"] = true,
                ["test_file_extension"] = Path.GetExtension(state.Path),
            },
        };
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        string operation,
        Stopwatch stopwatch,
        ExecutionState state,
        ProgramObservation actor,
        string evidenceArtifactId) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        EventType = "hash",
        EventAction = operation,
        Nonce = invocation.Nonce,
        OccurredAtUtc = state.Result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "hash_algorithms_controller",
        CollectionMethod = operation == "imphash" ? "copy_pe_and_parse_import_table" : "create_json_and_independently_rehash",
        Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "hash",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["file_path"] = state.Result.Path,
            ["file_size_bytes"] = state.Result.After.SizeBytes,
            ["algorithm"] = state.Result.Algorithm,
            ["digest"] = state.Result.Digest,
            ["hashes"] = Hashes(state.Result.After),
            ["is_portable_executable"] = state.Result.After.IsPortableExecutable,
            ["import_count"] = state.Result.After.ImportCount,
            ["source_pe_path"] = state.Result.SourcePortableExecutablePath,
            ["source_pe_sha256"] = SourcePeSha256(state),
            ["source_matches_target"] = SourcePeSha256(state) is { } sourceSha256 && sourceSha256 == state.Result.After.Sha256,
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = state.Result.Succeeded,
                ["win32_error"] = state.Result.Win32Error,
                ["message"] = state.Result.Error,
            },
        },
        EvidenceRefs = [evidenceArtifactId],
    };

    private static void AddFacts(
        RunDatabase database,
        ControllerInvocation invocation,
        ExecutionState state,
        string eventId,
        ProgramObservation actor,
        bool succeeded)
    {
        var snapshot = state.Result.After;
        var sourcePeSha256 = SourcePeSha256(state);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["hash.operation_succeeded"] = JsonValue.Create(succeeded),
            ["hash.occurred_at_utc"] = JsonValue.Create(Values.Utc(state.Result.OccurredAtUtc)),
            ["hash.actor_pid"] = JsonValue.Create(actor.Pid),
            ["hash.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["hash.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["hash.extension"] = JsonValue.Create(Path.GetExtension(state.Path).ToLowerInvariant()),
            ["hash.path"] = JsonValue.Create(state.Path),
            ["hash.file_size_bytes"] = JsonValue.Create(snapshot.SizeBytes),
            ["hash.algorithm"] = JsonValue.Create(state.Result.Algorithm),
            ["hash.digest"] = JsonValue.Create(state.Result.Digest),
            ["hash.md5"] = JsonValue.Create(snapshot.Md5),
            ["hash.sha1"] = JsonValue.Create(snapshot.Sha1),
            ["hash.sha256"] = JsonValue.Create(snapshot.Sha256),
            ["hash.sha512"] = JsonValue.Create(snapshot.Sha512),
            ["hash.imphash"] = JsonValue.Create(snapshot.ImpHash),
            ["hash.is_portable_executable"] = JsonValue.Create(snapshot.IsPortableExecutable),
            ["hash.import_count"] = JsonValue.Create(snapshot.ImportCount),
            ["hash.source_pe_path"] = JsonValue.Create(state.Result.SourcePortableExecutablePath),
            ["hash.source_pe_sha256"] = JsonValue.Create(sourcePeSha256),
            ["hash.source_matches_target"] = JsonValue.Create(sourcePeSha256 is not null && sourcePeSha256 == snapshot.Sha256),
        };
        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = eventId,
                Key = key,
                Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "hash_algorithms_controller",
                Confidence = "high",
            });
        }
    }

    private static ArtifactObservation CreateEvidenceArtifact(ControllerInvocation invocation, ExecutionState state)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, state.ResultPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(state.ResultPath),
            SizeBytes = new FileInfo(state.ResultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(state.ResultPath),
            Sensitive = false,
            Metadata = new JsonObject
            {
                ["operation"] = state.Operation,
                ["test_file_extension"] = Path.GetExtension(state.Path),
            },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var before = new JsonObject { ["actor_alive"] = IsAlive(state.Actor), ["file_exists"] = File.Exists(state.Path) };
        var errors = new List<string>();
        Stop(state.Actor, errors);
        DeleteExact(state.Path, invocation.WorkDir, errors);
        var after = new JsonObject { ["actor_alive"] = IsAlive(state.Actor), ["file_exists"] = File.Exists(state.Path) };
        var succeeded = errors.Count == 0 && !IsAlive(state.Actor) && !File.Exists(state.Path);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_hash_actor_and_remove_controlled_file",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = before,
            After = after,
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Action = "remove_controlled_hash_file",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Before = new JsonObject { ["actor_alive"] = false },
        After = new JsonObject { ["actor_alive"] = false },
    };

    private static HashSnapshot Snapshot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new HashSnapshot { Exists = false, Path = fullPath };
        var impHashValid = ImportHashCalculator.TryCompute(fullPath, out var impHash, out var impHashError);
        return new HashSnapshot
        {
            Exists = true,
            Path = fullPath,
            SizeBytes = new FileInfo(fullPath).Length,
            Md5 = Hashing.FileMd5(fullPath),
            Sha1 = Hashing.FileSha1(fullPath),
            Sha256 = Hashing.FileSha256(fullPath),
            Sha512 = FileSha512(fullPath),
            ImpHash = impHash?.Digest,
            IsPortableExecutable = impHashValid,
            ImportCount = impHash?.ImportCount,
            ImpHashError = impHashValid ? null : impHashError,
        };
    }

    private static string FileSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    private static string? SourcePeSha256(ExecutionState state) =>
        state.Result.SourcePortableExecutablePath is { } source && File.Exists(source)
            ? Hashing.FileSha256(source)
            : null;

    private static bool IsJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static JsonObject Hashes(HashSnapshot snapshot) => new()
    {
        ["md5"] = snapshot.Md5,
        ["sha1"] = snapshot.Sha1,
        ["sha256"] = snapshot.Sha256,
        ["sha512"] = snapshot.Sha512,
        ["imphash"] = snapshot.ImpHash,
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
        var startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"启动哈希行为程序失败：{executable}");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待哈希行为结果超时：{path}");
            Thread.Sleep(25);
        }
        return ProtocolJson.Read<BehaviorResult>(path);
    }

    private static void DeleteExact(string path, string workDirectory, ICollection<string> errors)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetFullPath(workDirectory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"拒绝删除工作目录外文件：{fullPath}");
                return;
            }
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception exception) { errors.Add($"删除测试文件失败：{exception.Message}"); }
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

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
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
            ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));

    private sealed class ExecutionState : IDisposable
    {
        public ExecutionState(string operation, string actorPath, IReadOnlyList<string> actorArguments, Process actor, string resultPath, string path, BehaviorResult result)
        {
            Operation = operation;
            ActorPath = actorPath;
            ActorArguments = actorArguments;
            Actor = actor;
            ResultPath = resultPath;
            Path = path;
            Result = result;
        }

        public string Operation { get; }
        public string ActorPath { get; }
        public IReadOnlyList<string> ActorArguments { get; }
        public Process Actor { get; }
        public string ResultPath { get; }
        public string Path { get; }
        public BehaviorResult Result { get; }
        public void Dispose() => Actor.Dispose();
    }
}
