using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdrTest;
using Microsoft.Win32;

namespace RegistryActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.registry.create"] = "create",
        ["win.registry.modify"] = "modify",
        ["win.registry.delete"] = "delete",
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
                throw new InvalidDataException($"RegistryActivity Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");

            state = Execute(invocation, package, operation, parameters);
            var actor = ObserveActor(invocation, state);
            database.AddProgram(actor);
            var independentlyVerified = VerifyOutcome(operation, state);
            var succeeded = state.Result.Succeeded && independentlyVerified;
            var artifact = CreateEvidenceArtifact(invocation, state);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state, actor, artifact.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, state.Result, localEvent.LocalEventId, actor, succeeded);

            var cleanup = Cleanup(invocation, state);
            database.AddCleanup(cleanup);
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "REGISTRY_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var errorMessage = succeeded ? null : state.Result.Error ?? "Controller 独立观察未确认预期注册表状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "REGISTRY_BEHAVIOR_FAILED", errorMessage);
            WriteStatus(status, package.Manifest.CapabilityId, operation, errorMessage);
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
                        "REGISTRY_CONTROLLER_ERROR", exception.Message);
                }
                catch (Exception databaseException) { Console.Error.WriteLine(databaseException); }
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
        var tag = new string(invocation.Nonce.Where(char.IsLetterOrDigit).Take(16).ToArray()).ToLowerInvariant();
        if (tag.Length < 8) throw new InvalidDataException("本轮 nonce 不能生成安全的注册表键名。");
        var keyPath = $"Software\\EdrTest\\Runs\\{tag}\\{operation}";
        var valueName = "EdrTestValue";
        var beforeValue = $"EDRTEST|{invocation.Nonce}|REGISTRY_BEFORE";
        var afterValue = $"EDRTEST|{invocation.Nonce}|REGISTRY_{operation.ToUpperInvariant()}";
        var resultPath = Path.Combine(invocation.WorkDir, "registry-actor-result.json");
        Directory.CreateDirectory(invocation.WorkDir);

        RemoveControlledKey(keyPath);
        if (operation is "modify" or "delete")
        {
            using var seed = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new IOException("Controller 无法预置 HKCU 注册表测试键。");
            seed.SetValue(valueName, beforeValue, RegistryValueKind.String);
            seed.Flush();
            var seeded = Snapshot(keyPath, valueName);
            if (!seeded.KeyExists || !seeded.ValueExists || seeded.ValueData != beforeValue)
                throw new IOException("Controller 未能独立确认注册表预置状态。");
        }

        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        var arguments = new List<string>
        {
            "--operation", operation,
            "--key-path", keyPath,
            "--value-name", valueName,
            "--value-data", afterValue,
            "--result", resultPath,
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs, actor);
        if (!actor.WaitForExit(invocation.TimeoutMs))
        {
            actor.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待注册表行为 Actor 退出超时：PID {actor.Id}");
        }
        return new ExecutionState(actorPath, [.. arguments], actor, resultPath, keyPath, valueName, beforeValue, afterValue, result);
    }

    private static ProgramObservation ObserveActor(ControllerInvocation invocation, ExecutionState state)
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
        catch (InvalidOperationException) { endedAt = null; exitCode = null; }

        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = state.Result.Operation,
            ExecutablePath = state.ActorPath,
            Sha256 = Hashing.FileSha256(state.ActorPath),
            Sha1 = Hashing.FileSha1(state.ActorPath),
            Md5 = Hashing.FileMd5(state.ActorPath),
            Pid = state.Actor.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(state.Actor),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x86" => "x86", "arm64" => "arm64", _ => "x64",
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
                ["captured_by"] = "RegistryActivity.Controller",
                ["hive"] = "HKCU",
                ["controlled_key"] = true,
                ["nonce_in_value_data"] = true,
            },
        };
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        string operation,
        Stopwatch stopwatch,
        ExecutionState state,
        ProgramObservation actor,
        string artifactId) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        EventType = "registry",
        EventAction = operation,
        Nonce = invocation.Nonce,
        OccurredAtUtc = state.Result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "registry_activity_controller",
        CollectionMethod = "actor_protocol_plus_independent_registry_read",
        Confidence = "high",
        ActorProgramId = actor.ProgramInstanceId,
        Data = new JsonObject
        {
            ["kind"] = "registry",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["hive"] = "HKCU",
            ["key_path"] = state.Result.KeyPath,
            ["value_name"] = state.ValueName,
            ["registry_view"] = "default",
            ["before"] = RegistryState(state.Result.Before),
            ["after"] = RegistryState(state.Result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = state.Result.Succeeded,
                ["win32_error"] = state.Result.Win32Error,
                ["message"] = state.Result.Error,
            },
        },
        EvidenceRefs = [artifactId],
    };

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
            [$"registry.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["registry.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["registry.hive"] = JsonValue.Create(result.Hive),
            ["registry.key_path"] = JsonValue.Create(result.KeyPath),
            ["registry.value_name"] = JsonValue.Create(result.ValueName),
            ["registry.view"] = JsonValue.Create(result.RegistryView),
            ["registry.actor_pid"] = JsonValue.Create(actor.Pid),
            ["registry.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["registry.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["registry.before.key_exists"] = JsonValue.Create(result.Before.KeyExists),
            ["registry.before.value_exists"] = JsonValue.Create(result.Before.ValueExists),
            ["registry.before.value_kind"] = JsonValue.Create(result.Before.ValueKind),
            ["registry.before.value_data"] = JsonValue.Create(result.Before.ValueData),
            ["registry.before.value_data_sha256"] = JsonValue.Create(result.Before.ValueDataSha256),
            ["registry.after.key_exists"] = JsonValue.Create(result.After.KeyExists),
            ["registry.after.value_exists"] = JsonValue.Create(result.After.ValueExists),
            ["registry.after.value_kind"] = JsonValue.Create(result.After.ValueKind),
            ["registry.after.value_data"] = JsonValue.Create(result.After.ValueData),
            ["registry.after.value_data_sha256"] = JsonValue.Create(result.After.ValueDataSha256),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = key == "correlation.nonce" ? null : eventId,
                Key = key,
                Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "registry_activity_controller",
                Confidence = "high",
            });
        }
    }

    private static bool VerifyOutcome(string operation, ExecutionState state)
    {
        var current = Snapshot(state.KeyPath, state.ValueName);
        var result = state.Result;
        return operation switch
        {
            "create" => !result.Before.KeyExists && !result.Before.ValueExists
                && current.KeyExists && current.ValueExists && current.ValueData == state.AfterValue
                && current.ValueDataSha256 == result.After.ValueDataSha256,
            "modify" => result.Before.KeyExists && result.Before.ValueExists && result.Before.ValueData == state.BeforeValue
                && current.KeyExists && current.ValueExists && current.ValueData == state.AfterValue
                && current.ValueDataSha256 == result.After.ValueDataSha256,
            "delete" => result.Before.KeyExists && result.Before.ValueExists && result.Before.ValueData == state.BeforeValue
                && !current.KeyExists && !current.ValueExists,
            _ => false,
        };
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
                ["operation"] = state.Result.Operation,
                ["hive"] = "HKCU",
                ["controlled_key"] = true,
            },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var started = DateTimeOffset.UtcNow;
        var before = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["key_exists"] = Snapshot(state.KeyPath, state.ValueName).KeyExists,
        };
        var errors = new List<string>();
        Stop(state.Actor, errors);
        try { RemoveControlledKey(state.KeyPath); }
        catch (Exception exception) { errors.Add($"删除本轮注册表键失败：{exception.Message}"); }
        var afterSnapshot = Snapshot(state.KeyPath, state.ValueName);
        var after = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["key_exists"] = afterSnapshot.KeyExists,
            ["value_exists"] = afterSnapshot.ValueExists,
        };
        var succeeded = errors.Count == 0 && !IsAlive(state.Actor) && !afterSnapshot.KeyExists;
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = 1,
            Action = "stop_registry_actor_and_remove_exact_hkcu_key",
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
        Action = "no_registry_key_allocated",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
    };

    private static RegistrySnapshot Snapshot(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null) return new RegistrySnapshot { KeyExists = false, ValueExists = false };
        var exists = key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        if (!exists) return new RegistrySnapshot { KeyExists = true, ValueExists = false };
        var data = Convert.ToString(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            System.Globalization.CultureInfo.InvariantCulture);
        return new RegistrySnapshot
        {
            KeyExists = true,
            ValueExists = true,
            ValueKind = key.GetValueKind(valueName).ToString(),
            ValueData = data,
            ValueDataSha256 = data is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant(),
        };
    }

    private static void RemoveControlledKey(string keyPath)
    {
        if (!keyPath.StartsWith("Software\\EdrTest\\Runs\\", StringComparison.OrdinalIgnoreCase)
            || keyPath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理受控范围外的注册表键。");
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        var separator = keyPath.LastIndexOf('\\');
        if (separator <= "Software\\EdrTest\\Runs".Length) return;
        var nonceContainer = keyPath[..separator];
        using var container = Registry.CurrentUser.OpenSubKey(nonceContainer, writable: false);
        if (container is null || container.SubKeyCount != 0 || container.ValueCount != 0) return;
        container.Dispose();
        Registry.CurrentUser.DeleteSubKey(nonceContainer, throwOnMissingSubKey: false);
    }

    private static JsonObject RegistryState(RegistrySnapshot value) => new()
    {
        ["exists"] = value.ValueExists,
        ["type"] = value.ValueKind,
        ["data"] = value.ValueData,
        ["data_sha256"] = value.ValueDataSha256,
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
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"启动注册表行为程序失败：{executable}");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"Actor 写入结果前已退出，退出码 {process.ExitCode}。");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待注册表行为结果超时：{path}");
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
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"' : value;

    private static void WriteStatus(string status, string capabilityId, string operation, string? error) =>
        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = capabilityId,
            ["operation"] = operation, ["error"] = error,
        }.ToJsonString(JsonDefaults.Options));

    private sealed class ExecutionState : IDisposable
    {
        public ExecutionState(string actorPath, IReadOnlyList<string> actorArguments, Process actor, string resultPath,
            string keyPath, string valueName, string beforeValue, string afterValue, BehaviorResult result)
        {
            ActorPath = actorPath; ActorArguments = actorArguments; Actor = actor; ResultPath = resultPath;
            KeyPath = keyPath; ValueName = valueName; BeforeValue = beforeValue; AfterValue = afterValue; Result = result;
        }
        public string ActorPath { get; }
        public IReadOnlyList<string> ActorArguments { get; }
        public Process Actor { get; }
        public string ResultPath { get; }
        public string KeyPath { get; }
        public string ValueName { get; }
        public string BeforeValue { get; }
        public string AfterValue { get; }
        public BehaviorResult Result { get; }
        public void Dispose() => Actor.Dispose();
    }
}
