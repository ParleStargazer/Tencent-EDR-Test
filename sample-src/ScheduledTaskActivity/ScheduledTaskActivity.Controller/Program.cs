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
        var nextCleanupSequence = 1;
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
            var principalSid = CurrentUserSid();
            Directory.CreateDirectory(invocation.WorkDir);
            var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var methods = new[] { "task_scheduler_com", "schtasks_cli" };
            var allSucceeded = true;
            string? firstError = null;
            foreach (var (method, index) in methods.Select((value, index) => (value, index)))
            {
                var methodTag = method == "schtasks_cli" ? "cli" : "com";
                taskPath = $"\\EdrTest_{tag}_{operation}_{methodTag}";
                var beforeMarker = $"EDRTEST|{invocation.Nonce}|SCHEDULED_TASK|{method}|BEFORE";
                var expectedMarker = $"EDRTEST|{invocation.Nonce}|SCHEDULED_TASK|{method}|{operation.ToUpperInvariant()}";
                var beforeXml = ScheduledTaskClient.CreateDefinition(taskPath, principalSid, beforeMarker, "/d /c exit 0");
                var enabledFutureTask = operation == "create" && method == "schtasks_cli";
                var afterArguments = $"/d /c rem EDRTEST_{tag}_{methodTag}_{operation.ToUpperInvariant()}";
                var afterXml = ScheduledTaskClient.CreateDefinition(taskPath, principalSid, expectedMarker, afterArguments,
                    enabled: enabledFutureTask, futureStartUtc: enabledFutureTask ? DateTimeOffset.UtcNow.AddYears(1) : null);
                var beforeDefinitionPath = Path.Combine(invocation.WorkDir, $"scheduled-task-before-{method}.xml");
                var afterDefinitionPath = Path.Combine(invocation.WorkDir, $"scheduled-task-after-{method}.xml");
                File.WriteAllText(beforeDefinitionPath, beforeXml);
                File.WriteAllText(afterDefinitionPath, afterXml);

                Prepare(operation, taskPath, beforeXml, beforeMarker);
                var resultPath = Path.Combine(invocation.WorkDir, $"scheduled-task-actor-result-{method}.json");
                var actorArguments = new List<string>
                {
                    "--method", method, "--operation", operation, "--task-path", taskPath, "--marker", expectedMarker,
                    "--definition", afterDefinitionPath, "--action-arguments", afterArguments, "--result", resultPath,
                    "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                actorProcess = Start(actorPath, actorArguments, invocation.WorkDir);
                var result = WaitAndRead(resultPath, invocation.TimeoutMs, actorProcess);
                if (!actorProcess.WaitForExit(invocation.TimeoutMs))
                {
                    actorProcess.Kill(entireProcessTree: true);
                    throw new TimeoutException($"等待计划任务 Actor 退出超时：PID {actorProcess.Id}");
                }

                var actor = ObserveActor(invocation, actorProcess, actorPath, actorArguments, result, index);
                database.AddProgram(actor);
                var effectiveActor = actor;
                if (result.ClientProcessId is not null && !string.IsNullOrWhiteSpace(result.ClientExecutable))
                {
                    effectiveActor = ObserveClient(invocation, result, actor, index);
                    database.AddProgram(effectiveActor);
                }
                var independentlyObserved = ScheduledTaskClient.Snapshot(taskPath);
                var succeeded = result.Succeeded && Verify(method, operation, expectedMarker, afterArguments, result, independentlyObserved);
                allSucceeded &= succeeded;
                firstError ??= succeeded ? null : result.Error ?? $"{method} 子测试未确认预期计划任务状态。";
                var artifact = CreateEvidenceArtifact(invocation, resultPath, operation, method, taskPath,
                    result.After.Enabled == true);
                database.AddArtifact(artifact);
                var localEvent = CreateEvent(invocation, operation, method, index, stopwatch, result, effectiveActor, artifact.ArtifactId);
                database.AddEvent(localEvent);
                AddFacts(database, invocation, operation, method, result, effectiveActor, localEvent.LocalEventId, succeeded);

                var cleanup = Cleanup(invocation, taskPath, actorProcess, method, index + 1);
                database.AddCleanup(cleanup);
                nextCleanupSequence = index + 2;
                actorProcess.Dispose();
                actorProcess = null;
                taskPath = null;
                if (cleanup.Status != "succeeded")
                {
                    database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "SCHEDULED_TASK_CLEANUP_FAILED", cleanup.ErrorMessage);
                    WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, methods.Length, cleanup.ErrorMessage);
                    return 30;
                }
            }

            AddGlobalFact(database, invocation, $"scheduled_task.{operation}_succeeded", JsonValue.Create(allSucceeded));
            AddGlobalFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce));
            var status = allSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = allSucceeded ? null : firstError;
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                allSucceeded ? null : "SCHEDULED_TASK_OUTCOME_MISMATCH", error);
            WriteStatus(status, package.Manifest.CapabilityId, operation, methods.Length, error);
            return allSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanup = taskPath is null
                        ? EmptyCleanup(invocation, nextCleanupSequence)
                        : Cleanup(invocation, taskPath, actorProcess, "interrupted", nextCleanupSequence);
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

    private static bool Verify(string method, string operation, string marker, string actionArguments,
        BehaviorResult result, TaskSnapshot independentlyObserved) => operation switch
    {
        "create" => !result.Before.Exists && result.After.Exists && result.After.Enabled == (method == "schtasks_cli")
            && (method == "schtasks_cli"
                ? string.Equals(result.After.ActionArguments, actionArguments, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(independentlyObserved.ActionArguments, actionArguments, StringComparison.OrdinalIgnoreCase)
                : result.After.Marker == marker && independentlyObserved.Marker == marker)
            && independentlyObserved.Exists
            && (method != "schtasks_cli" || independentlyObserved.Triggers?.Contains("TimeTrigger", StringComparer.Ordinal) == true),
        "modify" => result.Before.Exists && result.After.Exists && result.Before.XmlSha256 != result.After.XmlSha256
            && independentlyObserved.Exists
            && (method == "schtasks_cli"
                ? result.Before.Enabled == false && result.After.Enabled == true && independentlyObserved.Enabled == true
                    && string.Equals(result.After.ActionArguments, result.Before.ActionArguments, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(independentlyObserved.ActionArguments, result.Before.ActionArguments, StringComparison.OrdinalIgnoreCase)
                : result.After.Marker == marker && independentlyObserved.Marker == marker),
        "delete" => result.Before.Exists && !result.After.Exists && !independentlyObserved.Exists,
        _ => false,
    };

    private static ProgramObservation ObserveActor(ControllerInvocation invocation, Process process, string executable,
        IReadOnlyList<string> arguments, BehaviorResult result, int instanceIndex)
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
            CaseRunId = invocation.CaseRunId, Role = "actor", InstanceName = result.Method, InstanceIndex = instanceIndex,
            ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable), Md5 = Hashing.FileMd5(executable),
            Pid = process.Id, ParentPid = Environment.ProcessId, SessionId = TrySessionId(process),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" },
            CommandLine = FormatCommandLine(executable, arguments), WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt, EndedAtUtc = endedAt, ExitCode = exitCode,
            StartupAttempted = true, StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "ScheduledTaskActivity.Controller", ["method"] = result.Method,
                ["task_enabled"] = result.After.Enabled, ["task_was_never_started"] = true,
            },
        };
    }

    private static ProgramObservation ObserveClient(ControllerInvocation invocation, BehaviorResult result,
        ProgramObservation actor, int instanceIndex) => new()
    {
        CaseRunId = invocation.CaseRunId, Role = "helper", InstanceName = result.Method, InstanceIndex = instanceIndex,
        ExecutablePath = result.ClientExecutable!, Sha256 = Hashing.FileSha256(result.ClientExecutable!),
        Sha1 = Hashing.FileSha1(result.ClientExecutable!), Md5 = Hashing.FileMd5(result.ClientExecutable!),
        Pid = result.ClientProcessId!.Value, ParentPid = actor.Pid, SessionId = actor.SessionId,
        Architecture = actor.Architecture, CommandLine = result.ClientCommandLine ?? result.ClientExecutable!,
        WorkingDirectory = invocation.WorkDir, StartedAtUtc = result.ClientStartedAtUtc ?? result.OccurredAtUtc,
        EndedAtUtc = result.ClientEndedAtUtc ?? result.CompletedAtUtc, ExitCode = result.ClientExitCode,
        StartupAttempted = true, StartupSucceeded = result.ClientExitCode == 0,
        Metadata = new JsonObject
        {
            ["captured_by"] = "ScheduledTaskActivity.Actor", ["method"] = result.Method,
            ["system_client"] = true, ["task_was_never_started"] = true,
        },
    };

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, string operation, string method, int index, Stopwatch stopwatch,
        BehaviorResult result, ProgramObservation actor, string artifactId) => new()
    {
        CaseRunId = invocation.CaseRunId, Sequence = index + 1, EventType = "scheduled_task", EventAction = operation,
        Nonce = invocation.Nonce, OccurredAtUtc = result.CompletedAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds, Source = "scheduled_task_activity_controller",
        CollectionMethod = method == "schtasks_cli" ? "schtasks_cli_plus_independent_query_and_security_event_diagnostic" : "task_scheduler_2_com_plus_independent_query",
        Confidence = "high", ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "scheduled_task", ["operation"] = operation, ["method"] = method, ["actor"] = ProcessReference(actor),
            ["task_path"] = result.TaskPath, ["before"] = TaskDefinition(result.Before), ["after"] = TaskDefinition(result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true, ["succeeded"] = result.Succeeded,
                ["win32_error"] = result.HResult is null ? null : result.HResult.Value & 0xFFFF, ["message"] = result.Error,
            },
        },
        EvidenceRefs = [artifactId],
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, string operation, string method,
        BehaviorResult result, ProgramObservation actor, string eventId, bool succeeded)
    {
        var prefix = $"scheduled_task.{method}";
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"{prefix}.{operation}_succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            [$"{prefix}.task_path"] = JsonValue.Create(result.TaskPath), [$"{prefix}.marker"] = JsonValue.Create(result.ExpectedMarker),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid), [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            [$"{prefix}.before.exists"] = JsonValue.Create(result.Before.Exists), [$"{prefix}.before.xml_sha256"] = JsonValue.Create(result.Before.XmlSha256),
            [$"{prefix}.before.principal"] = JsonValue.Create(result.Before.Principal), [$"{prefix}.before.enabled"] = JsonValue.Create(result.Before.Enabled),
            [$"{prefix}.before.marker"] = JsonValue.Create(result.Before.Marker), [$"{prefix}.before.action_command"] = JsonValue.Create(result.Before.ActionCommand),
            [$"{prefix}.before.action_arguments"] = JsonValue.Create(result.Before.ActionArguments),
            [$"{prefix}.after.exists"] = JsonValue.Create(result.After.Exists), [$"{prefix}.after.xml_sha256"] = JsonValue.Create(result.After.XmlSha256),
            [$"{prefix}.after.principal"] = JsonValue.Create(result.After.Principal), [$"{prefix}.after.enabled"] = JsonValue.Create(result.After.Enabled),
            [$"{prefix}.after.marker"] = JsonValue.Create(result.After.Marker), [$"{prefix}.after.action_command"] = JsonValue.Create(result.After.ActionCommand),
            [$"{prefix}.after.action_arguments"] = JsonValue.Create(result.After.ActionArguments),
            [$"{prefix}.after.triggers"] = StringArray(result.After.Triggers),
            [$"{prefix}.security_event_id"] = JsonValue.Create(result.SecurityEventId),
            [$"{prefix}.security_event_found"] = JsonValue.Create(result.SecurityEventFound),
            [$"{prefix}.audit_policy_output"] = JsonValue.Create(result.AuditPolicyOutput),
            [$"{prefix}.security_event_query_output"] = JsonValue.Create(result.SecurityEventQueryOutput),
            [$"{prefix}.diagnostic_error"] = JsonValue.Create(result.DiagnosticError),
        };
        if (operation == "create")
            values[$"{prefix}.security_event_4698_found"] = JsonValue.Create(result.SecurityEvent4698Found);
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
        string operation, string method, string taskPath, bool enabled)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId, Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, resultPath).Replace('\\', '/'), MediaType = "application/json",
            Sha256 = Hashing.FileSha256(resultPath), SizeBytes = new FileInfo(resultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(resultPath), Sensitive = false,
            Metadata = new JsonObject { ["operation"] = operation, ["method"] = method, ["task_path"] = taskPath, ["task_enabled"] = enabled },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, string taskPath, Process? actor,
        string method, int sequence)
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
            CaseRunId = invocation.CaseRunId, Sequence = sequence, Action = $"delete_exact_test_task_{method}",
            Status = succeeded ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["task_path"] = taskPath, ["task_exists"] = beforeSnapshot.Exists },
            After = new JsonObject { ["task_exists"] = afterSnapshot.Exists, ["actor_alive"] = actorAlive },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation, int sequence) => new()
    {
        CaseRunId = invocation.CaseRunId, Sequence = sequence, Action = "no_scheduled_task_allocated", Status = "succeeded",
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

    private static void WriteStatus(string status, string capabilityId, string operation, int methods, string? error) =>
        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = capabilityId,
            ["operation"] = operation, ["methods"] = methods, ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));
}
