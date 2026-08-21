using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace WmiActivity;

internal static class Program
{
    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        Process? actorProcess = null;
        WmiPlan? plan = null;
        var terminalWritten = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            plan = WmiPlans.Create(package.Manifest.CapabilityId, invocation.Nonce, invocation.WorkDir);
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var readyPath = Path.Combine(invocation.WorkDir, "wmi-ready.json");
            var gatePath = Path.Combine(invocation.WorkDir, "wmi-controller-verified.json");
            var resultPath = Path.Combine(invocation.WorkDir, "wmi-actor-result.json");
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_000;
            var actorArguments = new[]
            {
                "--capability-id", plan.CapabilityId,
                "--nonce", invocation.Nonce,
                "--work-dir", invocation.WorkDir,
                "--ready", readyPath,
                "--gate", gatePath,
                "--result", resultPath,
                "--timeout-ms", Math.Min(invocation.TimeoutMs, 180_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            actorProcess = Start(actorPath, actorArguments, invocation.WorkDir);
            var ready = WaitAndRead<WmiReady>(readyPath, invocation.TimeoutMs, actorProcess, "Actor 创建 WMI 对象");
            ValidateReady(plan, ready, actorProcess.Id);

            var independentSnapshot = WmiRepository.CaptureTarget(plan);
            if (!WmiRepository.MatchesPlan(plan, independentSnapshot))
                throw new InvalidDataException("Controller 独立查询的 WMI 对象与计划不一致。");
            if (!string.Equals(independentSnapshot.ObjectPath, ready.After.ObjectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Controller 与 Actor 查询到的 WMI 对象路径不一致。");
            ProtocolJson.WriteAtomic(gatePath, new WmiVerificationGate
            {
                CapabilityId = plan.CapabilityId,
                Operation = plan.Operation,
                VerifiedAtUtc = DateTimeOffset.UtcNow,
            });

            var result = WaitAndRead<WmiBehaviorResult>(resultPath, invocation.TimeoutMs, actorProcess, "Actor 清理并返回结果");
            WaitForExit(actorProcess, invocation.TimeoutMs, "WMI Actor");
            ValidateResult(plan, ready, result, actorProcess.Id);
            var actor = ObserveActor(invocation, actorProcess, actorPath, actorArguments, result);
            database.AddProgram(actor);
            var artifacts = new[]
            {
                CreateArtifact(invocation, readyPath, "wmi_ready_protocol"),
                CreateArtifact(invocation, resultPath, "wmi_result_protocol"),
            };
            foreach (var artifact in artifacts) database.AddArtifact(artifact);

            var succeeded = result.Succeeded
                && result.ControllerGateObserved
                && result.ActorVerificationSucceeded
                && result.CleanupSucceeded
                && !result.Before.Exists
                && WmiRepository.MatchesPlan(plan, result.After)
                && !result.Final.Exists;
            var localEvent = CreateEvent(invocation, stopwatch, plan, actor, ready, result, artifacts, succeeded);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, plan, actor, ready, result, localEvent.LocalEventId, succeeded);

            var cleanup = Cleanup(invocation, plan, actorProcess);
            database.AddCleanup(cleanup);
            actorProcess.Dispose();
            actorProcess = null;
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "WMI_CLEANUP_FAILED", cleanup.ErrorMessage);
                terminalWritten = true;
                return 30;
            }
            database.CompleteCapability(invocation.CaseRunId, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "WMI_LOCAL_VERIFICATION_FAILED",
                succeeded ? null : "WMI 对象创建、双端独立查询或清理验证没有全部通过。");
            terminalWritten = true;
            WriteStatus(plan, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR");
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null && !terminalWritten)
            {
                try
                {
                    var cleanup = plan is null
                        ? EmptyCleanup(invocation, actorProcess)
                        : Cleanup(invocation, plan, actorProcess);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId, cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "WMI_CONTROLLER_ERROR", exception.Message);
                    terminalWritten = true;
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
            actorProcess?.Dispose();
            database?.Dispose();
        }
    }

    private static void ValidateReady(WmiPlan plan, WmiReady ready, int actorPid)
    {
        if (!string.Equals(ready.CapabilityId, plan.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(ready.Operation, plan.Operation, StringComparison.Ordinal)
            || ready.ActorProcessId != actorPid
            || ready.Before.Exists
            || !WmiRepository.MatchesPlan(plan, ready.After))
            throw new InvalidDataException("Actor WMI 就绪协议与能力计划不一致。");
    }

    private static void ValidateResult(WmiPlan plan, WmiReady ready, WmiBehaviorResult result, int actorPid)
    {
        if (!string.Equals(result.CapabilityId, plan.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(result.Operation, plan.Operation, StringComparison.Ordinal)
            || result.ActorProcessId != actorPid
            || result.OccurredAtUtc != ready.OccurredAtUtc
            || !string.Equals(result.After.ObjectPath, ready.After.ObjectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Actor WMI 结果协议与就绪协议不一致。");
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        Stopwatch stopwatch,
        WmiPlan plan,
        ProgramObservation actor,
        WmiReady ready,
        WmiBehaviorResult result,
        IReadOnlyList<ArtifactObservation> artifacts,
        bool succeeded) => new()
    {
        CaseRunId = invocation.CaseRunId,
        EventType = "wmi",
        EventAction = plan.Operation,
        Nonce = invocation.Nonce,
        OccurredAtUtc = ready.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "wmi_activity_controller",
        CollectionMethod = "system_management_root_subscription_dual_repository_query",
        Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        EvidenceRefs = artifacts.Select(value => value.ArtifactId).ToList(),
        Data = new JsonObject
        {
            ["kind"] = "wmi",
            ["operation"] = plan.Operation,
            ["actor"] = ProcessReference(actor),
            ["namespace"] = plan.Namespace,
            ["object_name"] = plan.ObjectName,
            ["object_class"] = plan.ObjectClass,
            ["object_path"] = result.After.ObjectPath,
            ["consumer_class"] = plan.Operation is "consumer" or "consumer_filter_bind" ? plan.ConsumerClass : null,
            ["query"] = plan.Operation is "filter" or "consumer_filter_bind" ? plan.Query : null,
            ["query_language"] = plan.Operation is "filter" or "consumer_filter_bind" ? plan.QueryLanguage : null,
            ["event_namespace"] = plan.Operation is "filter" or "consumer_filter_bind" ? plan.EventNamespace : null,
            ["log_file_path"] = plan.Operation is "consumer" or "consumer_filter_bind" ? plan.LogFilePath : null,
            ["text_template"] = plan.Operation is "consumer" or "consumer_filter_bind" ? plan.TextTemplate : null,
            ["command_line_template"] = null,
            ["script_text_sha256"] = null,
            ["filter_path"] = result.FilterPath,
            ["consumer_path"] = result.ConsumerPath,
            ["binding_path"] = result.BindingPath,
            ["before"] = SnapshotJson(result.Before),
            ["after"] = SnapshotJson(result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = succeeded,
                ["win32_error"] = null,
                ["message"] = result.Error,
            },
        },
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, WmiPlan plan,
        ProgramObservation actor, WmiReady ready, WmiBehaviorResult result, string eventId, bool succeeded)
    {
        var values = new Dictionary<string, JsonNode?>
        {
            ["wmi.operation_succeeded"] = JsonValue.Create(succeeded),
            ["wmi.operation"] = JsonValue.Create(plan.Operation),
            ["wmi.occurred_at_utc"] = JsonValue.Create(Values.Utc(ready.OccurredAtUtc)),
            ["wmi.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            ["wmi.namespace"] = JsonValue.Create(plan.Namespace),
            ["wmi.object_name"] = JsonValue.Create(plan.ObjectName),
            ["wmi.object_class"] = JsonValue.Create(plan.ObjectClass),
            ["wmi.object_path"] = JsonValue.Create(result.After.ObjectPath),
            ["wmi.filter_name"] = JsonValue.Create(plan.FilterName),
            ["wmi.filter_path"] = JsonValue.Create(result.FilterPath),
            ["wmi.query"] = JsonValue.Create(plan.Query),
            ["wmi.query_language"] = JsonValue.Create(plan.QueryLanguage),
            ["wmi.event_namespace"] = JsonValue.Create(plan.EventNamespace),
            ["wmi.consumer_name"] = JsonValue.Create(plan.ConsumerName),
            ["wmi.consumer_path"] = JsonValue.Create(result.ConsumerPath),
            ["wmi.consumer_class"] = JsonValue.Create(plan.ConsumerClass),
            ["wmi.log_file_path"] = JsonValue.Create(plan.LogFilePath),
            ["wmi.text_template"] = JsonValue.Create(plan.TextTemplate),
            ["wmi.binding_path"] = JsonValue.Create(result.BindingPath),
            ["wmi.filter_reference"] = JsonValue.Create(result.After.FilterReference),
            ["wmi.consumer_reference"] = JsonValue.Create(result.After.ConsumerReference),
            ["wmi.controller_verified"] = JsonValue.Create(true),
            ["wmi.actor_verified"] = JsonValue.Create(result.ActorVerificationSucceeded),
            ["wmi.cleanup_succeeded"] = JsonValue.Create(result.CleanupSucceeded),
            ["wmi.actor_pid"] = JsonValue.Create(actor.Pid),
            ["wmi.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["wmi.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        foreach (var (key, value) in values)
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = eventId,
                Key = key,
                Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "wmi_activity_controller",
                Confidence = "high",
            });
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, WmiPlan plan, Process? actor)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(actor, errors);
        WmiCleanupResult? repository = null;
        try { repository = WmiRepository.Cleanup(plan); }
        catch (Exception exception) { errors.Add(exception.Message); }
        if (repository is not null) errors.AddRange(repository.Errors);
        var actorAlive = actor is not null && IsAlive(actor);
        var succeeded = errors.Count == 0 && !actorAlive && repository?.Succeeded == true;
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "delete_wmi_binding_consumer_filter_and_stop_actor",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject
            {
                ["actor_pid"] = actor?.Id,
                ["filter_name"] = plan.FilterName,
                ["consumer_name"] = plan.ConsumerName,
            },
            After = new JsonObject
            {
                ["actor_alive"] = actorAlive,
                ["binding_exists"] = repository?.BindingExistsAfter,
                ["consumer_exists"] = repository?.ConsumerExistsAfter,
                ["filter_exists"] = repository?.FilterExistsAfter,
                ["cleanup_order"] = new JsonArray((repository?.Order ?? []).Select(value => JsonValue.Create(value)).ToArray()),
            },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation, Process? actor)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(actor, errors);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_wmi_actor_before_plan_creation",
            Status = errors.Count == 0 ? "succeeded" : "failed",
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["actor_pid"] = actor?.Id },
            After = new JsonObject { ["actor_alive"] = actor is not null && IsAlive(actor) },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static ProgramObservation ObserveActor(ControllerInvocation invocation, Process process, string executable,
        IEnumerable<string> arguments, WmiBehaviorResult result)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = process.StartTime.ToUniversalTime(); } catch { startedAt = result.OccurredAtUtc; }
        try { endedAt = process.ExitTime.ToUniversalTime(); exitCode = process.ExitCode; } catch { endedAt = null; exitCode = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = result.Operation,
            InstanceIndex = 0,
            ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable),
            Sha1 = Hashing.FileSha1(executable),
            Md5 = Hashing.FileMd5(executable),
            Pid = process.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(process),
            Architecture = Architecture(),
            CommandLine = FormatCommandLine(executable, arguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            Metadata = new JsonObject { ["operation"] = result.Operation, ["native_api"] = "System.Management.ManagementObject.Put" },
        };
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, string path, string kind)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = kind,
            RelativePath = Path.GetRelativePath(runDirectory, path).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(path),
            SizeBytes = new FileInfo(path).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(path),
            Sensitive = false,
        };
    }

    private static JsonObject SnapshotJson(WmiSnapshot value) => new()
    {
        ["exists"] = value.Exists,
        ["object_class"] = value.ObjectClass,
        ["object_path"] = value.ObjectPath,
        ["name"] = value.Name,
        ["query"] = value.Query,
        ["query_language"] = value.QueryLanguage,
        ["event_namespace"] = value.EventNamespace,
        ["log_file_path"] = value.LogFilePath,
        ["text_template"] = value.TextTemplate,
        ["filter_reference"] = value.FilterReference,
        ["consumer_reference"] = value.ConsumerReference,
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
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 WMI Actor：{executable}");
    }

    private static T WaitAndRead<T>(string path, int timeoutMs, Process process, string stage) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"{stage}前 Actor 已退出：{process.ExitCode}");
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

    private static void Stop(Process? process, ICollection<string> errors)
    {
        if (process is null) return;
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
    private static void WriteStatus(WmiPlan plan, string status) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = plan.CapabilityId, ["operation"] = plan.Operation, ["methods"] = 1 }.ToJsonString(JsonDefaults.Options));
}
