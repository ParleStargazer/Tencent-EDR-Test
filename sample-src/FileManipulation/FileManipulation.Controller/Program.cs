using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EdrTest;

namespace FileManipulation;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.file.create"] = "create",
        ["win.file.open"] = "open",
        ["win.file.delete"] = "delete",
        ["win.file.modify"] = "modify",
        ["win.file.rename"] = "rename",
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
            {
                throw new InvalidDataException($"FileManipulation Controller 不支持能力：{package.Manifest.CapabilityId}");
            }

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            state = Execute(invocation, package, operation, parameters);
            var actor = CreateProgram(invocation, state);
            database.AddProgram(actor);

            var verified = VerifyOutcome(operation, state);
            var localSucceeded = state.Result.Succeeded && verified;
            var evidence = CreateEvidenceArtifact(invocation, state.ResultPath);
            database.AddArtifact(evidence);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state.Result, actor, evidence.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, state.Result, localEvent.LocalEventId, actor, localSucceeded);

            var cleanup = Cleanup(invocation, state);
            database.AddCleanup(cleanup);
            if (!string.Equals(cleanup.Status, "succeeded", StringComparison.Ordinal))
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "FILE_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var errorCode = localSucceeded ? null : verified ? "BEHAVIOR_OPERATION_FAILED" : "INDEPENDENT_OBSERVATION_FAILED";
            var errorMessage = localSucceeded ? null : state.Result.Error ?? "Controller 独立观察未确认预期文件状态。";
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
                var cleanup = state is null ? EmptyCleanup(invocation) : Cleanup(invocation, state);
                try
                {
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "FILE_MANIPULATION_CONTROLLER_ERROR", exception.Message);
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
        var path = Path.Combine(invocation.WorkDir, $"edrtest_{tag}_file_{operation}.txt");
        var destination = operation == "rename"
            ? Path.Combine(invocation.WorkDir, $"edrtest_{tag}_file_renamed.txt")
            : null;
        var resultPath = Path.Combine(invocation.WorkDir, "behavior-result.json");
        Directory.CreateDirectory(invocation.WorkDir);

        if (File.Exists(path) || destination is not null && File.Exists(destination))
        {
            throw new IOException("能力工作目录中已存在本轮将使用的文件，拒绝覆盖。");
        }

        var payloadSize = parameters["payload_size"]?.GetValue<int>() ?? 8_192;
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        if (operation != "create")
        {
            File.WriteAllBytes(path, Payload(invocation.Nonce, "seed", payloadSize));
            using var seed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            seed.Flush(flushToDisk: true);
        }

        var arguments = new List<string>
        {
            "--operation", operation,
            "--path", path,
            "--nonce", invocation.Nonce,
            "--result", resultPath,
            "--payload-size", payloadSize.ToString(),
            "--hold-ms", holdMs.ToString(),
        };
        if (destination is not null)
        {
            arguments.Add("--destination");
            arguments.Add(destination);
        }

        var process = Start(actorPath, arguments, invocation.WorkDir);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs);
        if (!process.WaitForExit(invocation.TimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待文件行为 Actor 退出超时：PID {process.Id}");
        }
        return new ExecutionState(actorPath, [.. arguments], process, resultPath, path, destination, result);
    }

    private static ProgramObservation CreateProgram(ControllerInvocation invocation, ExecutionState state)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = state.Actor.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { startedAt = state.Result.OccurredAtUtc; }
        try
        {
            endedAt = state.Actor.HasExited ? state.Actor.ExitTime.ToUniversalTime() : null;
            exitCode = state.Actor.HasExited ? state.Actor.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            endedAt = null;
            exitCode = null;
        }

        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
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
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "FileManipulation.Controller",
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
        string evidenceArtifactId)
    {
        var data = new JsonObject
        {
            ["kind"] = "file",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["before"] = FileState(result.Before),
            ["after"] = FileState(result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.Win32Error,
                ["message"] = result.Error,
            },
        };
        if (operation == "rename")
        {
            data["source_path"] = result.SourcePath;
            data["destination_path"] = result.DestinationPath;
        }
        else
        {
            data["path"] = result.Path;
        }
        if (operation == "open")
        {
            data["open"] = new JsonObject
            {
                ["desired_access"] = result.DesiredAccess,
                ["share_mode"] = result.ShareMode,
                ["creation_disposition"] = result.CreationDisposition,
            };
        }
        if (operation == "modify")
        {
            data["write"] = new JsonObject
            {
                ["bytes_written"] = result.BytesWritten,
                ["offset"] = result.WriteOffset,
            };
        }

        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            EventType = "file",
            EventAction = operation,
            Nonce = invocation.Nonce,
            OccurredAtUtc = result.OccurredAtUtc,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "file_manipulation_controller",
            CollectionMethod = operation switch
            {
                "open" => "before_after_hash_and_io_count",
                "rename" => "source_destination_state_and_hash",
                _ => "before_after_file_state_and_hash",
            },
            Confidence = "high",
            ActorProgramId = actor.ProgramInstanceId,
            Data = data,
            EvidenceRefs = [evidenceArtifactId],
        };
    }

    private static void AddFacts(
        RunDatabase database,
        ControllerInvocation invocation,
        string operation,
        BehaviorResult result,
        string eventId,
        ProgramObservation actor,
        bool succeeded)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"file.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["file.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["file.actor_pid"] = JsonValue.Create(actor.Pid),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        if (operation == "rename")
        {
            values["file.source_path"] = JsonValue.Create(result.SourcePath);
            values["file.destination_path"] = JsonValue.Create(result.DestinationPath);
            values["file.before.md5"] = JsonValue.Create(result.Before.Md5);
            values["file.after.md5"] = JsonValue.Create(result.After.Md5);
            values["file.after.size_bytes"] = JsonValue.Create(result.After.SizeBytes);
            values["file.source_after.exists"] = JsonValue.Create(result.SourceAfter?.Exists);
            values["file.destination_before.exists"] = JsonValue.Create(result.DestinationBefore?.Exists);
        }
        else
        {
            values["file.path"] = JsonValue.Create(result.Path);
        }

        switch (operation)
        {
            case "create":
                values["file.after.exists"] = JsonValue.Create(result.After.Exists);
                values["file.after.size_bytes"] = JsonValue.Create(result.After.SizeBytes);
                values["file.after.md5"] = JsonValue.Create(result.After.Md5);
                values["file.after.sha256"] = JsonValue.Create(result.After.Sha256);
                break;
            case "open":
                values["file.before.md5"] = JsonValue.Create(result.Before.Md5);
                values["file.after.md5"] = JsonValue.Create(result.After.Md5);
                values["file.after.size_bytes"] = JsonValue.Create(result.After.SizeBytes);
                values["file.open.content_unchanged"] = JsonValue.Create(result.Before.Md5 == result.After.Md5);
                values["file.open.bytes_read"] = JsonValue.Create(result.BytesRead);
                values["file.open.bytes_written"] = JsonValue.Create(result.BytesWritten);
                break;
            case "delete":
                values["file.before.exists"] = JsonValue.Create(result.Before.Exists);
                values["file.before.size_bytes"] = JsonValue.Create(result.Before.SizeBytes);
                values["file.before.md5"] = JsonValue.Create(result.Before.Md5);
                values["file.after.exists"] = JsonValue.Create(result.After.Exists);
                break;
            case "modify":
                values["file.before.md5"] = JsonValue.Create(result.Before.Md5);
                values["file.after.md5"] = JsonValue.Create(result.After.Md5);
                values["file.after.size_bytes"] = JsonValue.Create(result.After.SizeBytes);
                values["file.modify.bytes_written"] = JsonValue.Create(result.BytesWritten);
                break;
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
                Source = "file_manipulation_controller",
                Confidence = "high",
            });
        }
    }

    private static bool VerifyOutcome(string operation, ExecutionState state)
    {
        var result = state.Result;
        var currentSource = Snapshot(state.Path);
        var currentDestination = state.Destination is null ? null : Snapshot(state.Destination);
        return operation switch
        {
            "create" => !result.Before.Exists && currentSource.Exists
                && currentSource.SizeBytes == result.BytesWritten && currentSource.Sha256 == result.After.Sha256,
            "open" => result.Before.Exists && currentSource.Exists && result.BytesRead > 0
                && result.BytesWritten == result.BytesRead && result.Before.Sha256 == result.After.Sha256
                && currentSource.Sha256 == result.After.Sha256,
            "delete" => result.Before.Exists && !currentSource.Exists,
            "modify" => result.Before.Exists && currentSource.Exists && result.Before.Sha256 != result.After.Sha256
                && currentSource.SizeBytes == result.BytesWritten && currentSource.Sha256 == result.After.Sha256,
            "rename" => result.Before.Exists && !currentSource.Exists && currentDestination?.Exists == true
                && result.DestinationBefore?.Exists == false && currentDestination.Sha256 == result.Before.Sha256,
            _ => false,
        };
    }

    private static ArtifactObservation CreateEvidenceArtifact(ControllerInvocation invocation, string resultPath)
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
            Metadata = new JsonObject { ["operation"] = JsonNode.Parse(File.ReadAllText(resultPath))?["operation"]?.GetValue<string>() },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var started = DateTimeOffset.UtcNow;
        var before = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["source_exists"] = File.Exists(state.Path),
            ["destination_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        var errors = new List<string>();
        Stop(state.Actor, errors);
        DeleteExact(state.Path, invocation.WorkDir, errors);
        if (state.Destination is not null) DeleteExact(state.Destination, invocation.WorkDir, errors);
        var after = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["source_exists"] = File.Exists(state.Path),
            ["destination_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        var succeeded = errors.Count == 0 && !IsAlive(state.Actor) && !File.Exists(state.Path)
            && (state.Destination is null || !File.Exists(state.Destination));
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_actor_and_remove_controlled_files",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = started,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = before,
            After = after,
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Action = "remove_controlled_files",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Before = new JsonObject { ["actor_alive"] = false },
        After = new JsonObject { ["actor_alive"] = false },
    };

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
        catch (Exception exception)
        {
            errors.Add($"删除临时文件 {path} 失败：{exception.Message}");
        }
    }

    private static JsonObject FileState(FileSnapshot snapshot)
    {
        var hashes = snapshot.Exists
            ? new JsonObject { ["md5"] = snapshot.Md5, ["sha1"] = snapshot.Sha1, ["sha256"] = snapshot.Sha256 }
            : null;
        return new JsonObject
        {
            ["exists"] = snapshot.Exists,
            ["path"] = snapshot.Path,
            ["size_bytes"] = snapshot.SizeBytes,
            ["created_at_utc"] = snapshot.CreatedAtUtc is { } created ? Values.Utc(created) : null,
            ["modified_at_utc"] = snapshot.ModifiedAtUtc is { } modified ? Values.Utc(modified) : null,
            ["attributes"] = snapshot.Attributes is null
                ? null
                : new JsonArray(snapshot.Attributes.Select(value => JsonValue.Create(value)).ToArray()),
            ["hashes"] = hashes,
        };
    }

    private static FileSnapshot Snapshot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new FileSnapshot { Exists = false, Path = fullPath };
        var file = new FileInfo(fullPath);
        return new FileSnapshot
        {
            Exists = true,
            Path = fullPath,
            SizeBytes = file.Length,
            CreatedAtUtc = file.CreationTimeUtc,
            ModifiedAtUtc = file.LastWriteTimeUtc,
            Attributes = file.Attributes.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries),
            Md5 = Hashing.FileMd5(fullPath),
            Sha1 = Hashing.FileSha1(fullPath),
            Sha256 = Hashing.FileSha256(fullPath),
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

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
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

    private static BehaviorResult WaitAndRead(string path, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待行为结果超时：{path}");
            Thread.Sleep(25);
        }
        return ProtocolJson.Read<BehaviorResult>(path);
    }

    private static byte[] Payload(string nonce, string operation, int size)
    {
        var marker = Encoding.UTF8.GetBytes($"EDRTEST|{nonce}|FILE_{operation.ToUpperInvariant()}|");
        return Enumerable.Range(0, size).Select(index => marker[index % marker.Length]).ToArray();
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
        public ExecutionState(
            string actorPath,
            IReadOnlyList<string> actorArguments,
            Process actor,
            string resultPath,
            string path,
            string? destination,
            BehaviorResult result)
        {
            ActorPath = actorPath;
            ActorArguments = actorArguments;
            Actor = actor;
            ResultPath = resultPath;
            Path = path;
            Destination = destination;
            Result = result;
        }

        public string ActorPath { get; }
        public IReadOnlyList<string> ActorArguments { get; }
        public Process Actor { get; }
        public string ResultPath { get; }
        public string Path { get; }
        public string? Destination { get; }
        public BehaviorResult Result { get; }
        public void Dispose() => Actor.Dispose();
    }
}
