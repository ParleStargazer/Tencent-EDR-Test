using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using EdrTest;

namespace ScheduledTaskActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.scheduled_task.create"] = "create",
        ["win.scheduled_task.modify"] = "modify",
        ["win.scheduled_task.delete"] = "delete",
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        Process? actorProcess = null;
        string? taskPath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
                throw new InvalidDataException($"ScheduledTaskActivity Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            var tag = BuildTag(invocation.Nonce);
            taskPath = $"\\EdrTest_{tag}_{operation}";
            var principalSid = CurrentUserSid();
            var beforeMarker = $"EDRTEST|{invocation.Nonce}|SCHEDULED_TASK|BEFORE";
            var expectedMarker = $"EDRTEST|{invocation.Nonce}|SCHEDULED_TASK|{operation.ToUpperInvariant()}";
            var beforeArguments = "/d /c exit 0";
            var afterArguments = $"/d /c rem EDRTEST_{tag}_{operation.ToUpperInvariant()}";
            var beforeXml = ScheduledTaskClient.CreateDefinition(taskPath, principalSid, beforeMarker, beforeArguments);
            var afterXml = ScheduledTaskClient.CreateDefinition(taskPath, principalSid, expectedMarker, afterArguments);
            Directory.CreateDirectory(invocation.WorkDir);
            var beforeDefinitionPath = Path.Combine(invocation.WorkDir, "scheduled-task-before.xml");
            var afterDefinitionPath = Path.Combine(invocation.WorkDir, "scheduled-task-after.xml");
            File.WriteAllText(beforeDefinitionPath, beforeXml);
            File.WriteAllText(afterDefinitionPath, afterXml);

            Prepare(operation, taskPath, beforeXml, beforeMarker);
            var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var resultPath = Path.Combine(invocation.WorkDir, "scheduled-task-actor-result.json");
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var actorArguments = new List<string>
            {
                "--operation", operation, "--task-path", taskPath, "--marker", expectedMarker,
                "--definition", afterDefinitionPath, "--result", resultPath,
                "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            actorProcess = Start(actorPath, actorArguments, invocation.WorkDir);
            var result = WaitAndRead(resultPath, invocation.TimeoutMs, actorProcess);
            if (!actorProcess.WaitForExit(invocation.TimeoutMs))
            {
                actorProcess.Kill(entireProcessTree: true);
                throw new TimeoutException($"等待计划任务 Actor 退出超时：PID {actorProcess.Id}");
            }

            var actor = ObserveActor(invocation, actorProcess, actorPath, actorArguments, result);
            database.AddProgram(actor);
            var independentlyObserved = ScheduledTaskClient.Snapshot(taskPath);
            var succeeded = result.Succeeded && Verify(operation, expectedMarker, result, independentlyObserved);
            var artifact = CreateEvidenceArtifact(invocation, resultPath, operation, taskPath);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, result, actor, artifact.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, result, actor, localEvent.LocalEventId, succeeded);
            AddGlobalFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce));

            var cleanup = Cleanup(invocation, taskPath, actorProcess);
            database.AddCleanup(cleanup);
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "SCHEDULED_TASK_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = succeeded ? null : result.Error ?? "Controller 独立观察未确认预期计划任务状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                succeeded ? null : "SCHEDULED_TASK_OUTCOME_MISMATCH", error);
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
                    var cleanup = taskPath is null ? EmptyCleanup(invocation) : Cleanup(invocation, taskPath, actorProcess);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "SCHEDULED_TASK_CONTROLLER_ERROR", exception.Message);
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

    private static void Prepare(string operation, string taskPath, string beforeXml, string beforeMarker)
    {
        ScheduledTaskClient.Delete(taskPath, ignoreMissing: true);
        if (ScheduledTaskClient.Snapshot(taskPath).Exists)
            throw new IOException($"无法清除冲突的本轮计划任务：{taskPath}");
        if (operation == "create") return;
        ScheduledTaskClient.Register(taskPath, beforeXml, update: false);
        var seeded = ScheduledTaskClient.Snapshot(taskPath);
        if (!seeded.Exists || seeded.Enabled != false || seeded.Marker != beforeMarker)
            throw new IOException("Controller 未能独立确认计划任务预置状态。");
    }

    private static bool Verify(string operation, string marker, BehaviorResult result, TaskSnapshot independentlyObserved) => operation switch
    {
        "create" => !result.Before.Exists && result.After.Exists && result.After.Enabled == false
            && result.After.Marker == marker && independentlyObserved.Exists && independentlyObserved.Marker == marker,
        "modify" => result.Before.Exists && result.After.Exists && result.Before.XmlSha256 != result.After.XmlSha256
            && result.After.Enabled == false && result.After.Marker == marker
            && independentlyObserved.Exists && independentlyObserved.Marker == marker,
        "delete" => result.Before.Exists && !result.After.Exists && !independentlyObserved.Exists,
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
            CaseRunId = invocation.CaseRunId, Role = "actor", InstanceName = "task_scheduler_com", ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable), Md5 = Hashing.FileMd5(executable),
            Pid = process.Id, ParentPid = Environment.ProcessId, SessionId = TrySessionId(process),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" },
            CommandLine = FormatCommandLine(executable, arguments), WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt, EndedAtUtc = endedAt, ExitCode = exitCode,
            StartupAttempted = true, StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "ScheduledTaskActivity.Controller", ["method"] = "task_scheduler_2_com",
                ["task_enabled"] = false, ["task_was_never_started"] = true,
            },
        };
    }

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, string operation, Stopwatch stopwatch,
        BehaviorResult result, ProgramObservation actor, string artifactId) => new()
    {
        CaseRunId = invocation.CaseRunId, EventType = "scheduled_task", EventAction = operation,
        Nonce = invocation.Nonce, OccurredAtUtc = result.OccurredAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds, Source = "scheduled_task_activity_controller",
        CollectionMethod = "task_scheduler_2_com_plus_independent_query", Confidence = "high", ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "scheduled_task", ["operation"] = operation, ["actor"] = ProcessReference(actor),
            ["task_path"] = result.TaskPath, ["before"] = TaskDefinition(result.Before), ["after"] = TaskDefinition(result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true, ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.HResult is null ? null : result.HResult.Value & 0xFFFF, ["message"] = result.Error,
            },
        },
        EvidenceRefs = [artifactId],
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, string operation,
        BehaviorResult result, ProgramObservation actor, string eventId, bool succeeded)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"scheduled_task.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["scheduled_task.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["scheduled_task.task_path"] = JsonValue.Create(result.TaskPath),
            ["scheduled_task.marker"] = JsonValue.Create(result.ExpectedMarker),
            ["scheduled_task.actor_pid"] = JsonValue.Create(actor.Pid),
            ["scheduled_task.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["scheduled_task.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["scheduled_task.before.exists"] = JsonValue.Create(result.Before.Exists),
            ["scheduled_task.before.xml_sha256"] = JsonValue.Create(result.Before.XmlSha256),
            ["scheduled_task.before.principal"] = JsonValue.Create(result.Before.Principal),
            ["scheduled_task.before.enabled"] = JsonValue.Create(result.Before.Enabled),
            ["scheduled_task.before.marker"] = JsonValue.Create(result.Before.Marker),
            ["scheduled_task.before.action_command"] = JsonValue.Create(result.Before.ActionCommand),
            ["scheduled_task.before.action_arguments"] = JsonValue.Create(result.Before.ActionArguments),
            ["scheduled_task.after.exists"] = JsonValue.Create(result.After.Exists),
            ["scheduled_task.after.xml_sha256"] = JsonValue.Create(result.After.XmlSha256),
            ["scheduled_task.after.principal"] = JsonValue.Create(result.After.Principal),
            ["scheduled_task.after.enabled"] = JsonValue.Create(result.After.Enabled),
            ["scheduled_task.after.marker"] = JsonValue.Create(result.After.Marker),
            ["scheduled_task.after.action_command"] = JsonValue.Create(result.After.ActionCommand),
            ["scheduled_task.after.action_arguments"] = JsonValue.Create(result.After.ActionArguments),
        };
        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow, Source = "scheduled_task_activity_controller", Confidence = "high",
            });
        }
    }

    private static void AddGlobalFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value) =>
        database.AddFact(new LocalFactObservation
        {
            CaseRunId = invocation.CaseRunId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow,
            Source = "scheduled_task_activity_controller", Confidence = "high",
        });

    private static ArtifactObservation CreateEvidenceArtifact(ControllerInvocation invocation, string resultPath,
        string operation, string taskPath)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId, Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, resultPath).Replace('\\', '/'), MediaType = "application/json",
            Sha256 = Hashing.FileSha256(resultPath), SizeBytes = new FileInfo(resultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(resultPath), Sensitive = false,
            Metadata = new JsonObject { ["operation"] = operation, ["task_path"] = taskPath, ["task_enabled"] = false },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, string taskPath, Process? actor)
    {
        var started = DateTimeOffset.UtcNow;
        var beforeSnapshot = SafeSnapshot(taskPath);
        var errors = new List<string>();
        if (actor is not null) Stop(actor, errors);
        try { ScheduledTaskClient.Delete(taskPath, ignoreMissing: true); }
        catch (Exception exception) { errors.Add($"清理本轮计划任务失败：{exception.Message}"); }
        var afterSnapshot = SafeSnapshot(taskPath);
        var actorAlive = actor is not null && IsAlive(actor);
        var succeeded = errors.Count == 0 && !actorAlive && !afterSnapshot.Exists;
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId, Action = "delete_exact_disabled_test_task",
            Status = succeeded ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["task_path"] = taskPath, ["task_exists"] = beforeSnapshot.Exists },
            After = new JsonObject { ["task_exists"] = afterSnapshot.Exists, ["actor_alive"] = actorAlive },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    {
        CaseRunId = invocation.CaseRunId, Action = "no_scheduled_task_allocated", Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow, EndedAtUtc = DateTimeOffset.UtcNow,
    };

    private static JsonObject TaskDefinition(TaskSnapshot snapshot) => new()
    {
        ["exists"] = snapshot.Exists, ["xml_sha256"] = snapshot.XmlSha256, ["principal"] = snapshot.Principal,
        ["enabled"] = snapshot.Enabled, ["actions"] = StringArray(snapshot.Actions), ["triggers"] = StringArray(snapshot.Triggers),
    };

    private static JsonArray? StringArray(string[]? values)
    {
        if (values is null) return null;
        var result = new JsonArray();
        foreach (var value in values) result.Add(value);
        return result;
    }

    private static JsonObject ProcessReference(ProgramObservation program) => new()
    {
        ["program_instance_id"] = program.ProgramInstanceId, ["pid"] = program.Pid, ["parent_pid"] = program.ParentPid,
        ["started_at_utc"] = Values.Utc(program.StartedAtUtc), ["executable"] = program.ExecutablePath, ["command_line"] = program.CommandLine,
    };

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("无法取得当前用户 SID，不能安全创建当前用户计划任务。");
    }

    private static string BuildTag(string nonce)
    {
        var tag = new string(nonce.Where(char.IsLetterOrDigit).Take(16).ToArray()).ToLowerInvariant();
        return tag.Length >= 8 ? tag : throw new InvalidDataException("本轮 nonce 不能生成安全的计划任务名称。");
    }

    private static TaskSnapshot SafeSnapshot(string taskPath)
    {
        try { return ScheduledTaskClient.Snapshot(taskPath); }
        catch { return ScheduledTaskClient.MissingSnapshot(); }
    }

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"启动计划任务行为程序失败：{executable}");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"Actor 写入结果前已退出，退出码 {process.ExitCode}。");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待计划任务行为结果超时：{path}");
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

    private static bool IsAlive(Process process) { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch (InvalidOperationException) { return null; } }
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"' : value;

    private static void WriteStatus(string status, string capabilityId, string operation, string? error) =>
        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = capabilityId,
            ["operation"] = operation, ["method"] = "task_scheduler_2_com", ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));
}
