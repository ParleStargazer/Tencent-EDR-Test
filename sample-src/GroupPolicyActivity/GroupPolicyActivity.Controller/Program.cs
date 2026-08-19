using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;
using Microsoft.Win32;

namespace GroupPolicyActivity;

internal static class Program
{
    private const string CapabilityId = "win.group_policy.modify";
    private const string ValueName = "ValidationMarker";

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        Process? actorProcess = null;
        string? keyPath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (package.Manifest.CapabilityId != CapabilityId)
                throw new InvalidDataException($"GroupPolicyActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);
            keyPath = $"SOFTWARE\\Policies\\EdrTest\\Runs\\{invocation.Nonce}";
            var beforeValue = $"EDRTEST|{invocation.Nonce}|BEFORE";
            var afterValue = $"EDRTEST|{invocation.Nonce}|AFTER";
            RemoveExactKey(keyPath);
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.CreateSubKey(keyPath, writable: true) ?? throw new IOException("无法创建组策略隔离键。"))
                key.SetValue(ValueName, beforeValue, RegistryValueKind.String);
            var prepared = Snapshot(keyPath);
            if (!prepared.ValueExists || prepared.ValueData != beforeValue) throw new IOException("Controller 未确认预置值。");

            var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var resultPath = Path.Combine(invocation.WorkDir, "group-policy-actor-result.json");
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var actorArguments = new[]
            {
                "--key-path", keyPath, "--value-name", ValueName, "--value-data", afterValue,
                "--result", resultPath, "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            actorProcess = Start(actorPath, actorArguments, invocation.WorkDir);
            var result = WaitAndRead(resultPath, invocation.TimeoutMs, actorProcess);
            if (!actorProcess.WaitForExit(invocation.TimeoutMs))
            {
                actorProcess.Kill(entireProcessTree: true);
                throw new TimeoutException($"等待组策略 Actor 退出超时：PID {actorProcess.Id}");
            }
            var actor = Observe(invocation, actorProcess, actorPath, actorArguments, result.OccurredAtUtc, "actor", "RegSetValueExW");
            database.AddProgram(actor);
            var independent = Snapshot(keyPath);
            var succeeded = result.Succeeded && independent.ValueExists && independent.ValueData == afterValue
                && result.Before.ValueData == beforeValue && result.After.ValueData == afterValue;
            var artifact = Artifact(invocation, resultPath);
            database.AddArtifact(artifact);
            var localEvent = new LocalEventObservation
            {
                CaseRunId = invocation.CaseRunId, EventType = "group_policy", EventAction = "modify", Nonce = invocation.Nonce,
                OccurredAtUtc = result.OccurredAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow,
                MonotonicOffsetMs = stopwatch.ElapsedMilliseconds, Source = "group_policy_activity_controller",
                CollectionMethod = "native_reg_set_value_plus_independent_registry_snapshot", Confidence = "high",
                ActorProgramId = actor.ProgramInstanceId, EvidenceRefs = [artifact.ArtifactId],
                Data = new JsonObject
                {
                    ["kind"] = "group_policy", ["operation"] = "modify", ["actor"] = ProcessReference(actor),
                    ["scope"] = "computer", ["policy_path"] = result.KeyPath, ["policy_name"] = result.ValueName,
                    ["backing_registry_path"] = result.KeyPath,
                    ["hive"] = result.Hive, ["key_path"] = result.KeyPath, ["value_name"] = result.ValueName,
                    ["value_type"] = result.After.ValueKind, ["before_value"] = result.Before.ValueData,
                    ["after_value"] = result.After.ValueData, ["native_api"] = result.NativeApi,
                    ["before"] = new JsonObject { ["exists"] = result.Before.KeyExists, ["value_exists"] = result.Before.ValueExists, ["value_data"] = result.Before.ValueData, ["value_type"] = result.Before.ValueKind },
                    ["after"] = new JsonObject { ["exists"] = result.After.KeyExists, ["value_exists"] = result.After.ValueExists, ["value_data"] = result.After.ValueData, ["value_type"] = result.After.ValueKind },
                    ["result"] = new JsonObject { ["attempted"] = true, ["succeeded"] = succeeded, ["win32_error"] = result.Win32Error, ["message"] = result.Error },
                },
            };
            database.AddEvent(localEvent);
            var facts = new Dictionary<string, JsonNode?>
            {
                ["group_policy.modify_succeeded"] = JsonValue.Create(succeeded),
                ["group_policy.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
                ["group_policy.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
                ["group_policy.hive"] = JsonValue.Create(result.Hive), ["group_policy.key_path"] = JsonValue.Create(result.KeyPath),
                ["group_policy.value_name"] = JsonValue.Create(result.ValueName), ["group_policy.value_type"] = JsonValue.Create(result.After.ValueKind),
                ["group_policy.before_value"] = JsonValue.Create(result.Before.ValueData), ["group_policy.after_value"] = JsonValue.Create(result.After.ValueData),
                ["group_policy.native_api"] = JsonValue.Create(result.NativeApi), ["group_policy.actor_pid"] = JsonValue.Create(actor.Pid),
                ["group_policy.actor_executable"] = JsonValue.Create(actor.ExecutablePath), ["group_policy.actor_command_line"] = JsonValue.Create(actor.CommandLine),
                ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
            };
            foreach (var (key, value) in facts) AddFact(database, invocation, key, value, localEvent.LocalEventId);
            var cleanup = Cleanup(invocation, keyPath, actorProcess);
            database.AddCleanup(cleanup);
            keyPath = null;
            actorProcess.Dispose(); actorProcess = null;
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                    "GROUP_POLICY_CLEANUP_FAILED", cleanup.ErrorMessage);
                return 30;
            }
            database.CompleteCapability(invocation.CaseRunId, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "GROUP_POLICY_OUTCOME_MISMATCH", succeeded ? null : result.Error ?? "独立读取未确认修改结果。");
            WriteStatus(succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", succeeded ? null : result.Error);
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanup = keyPath is null ? EmptyCleanup(invocation) : Cleanup(invocation, keyPath, actorProcess);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId, cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "GROUP_POLICY_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally { actorProcess?.Dispose(); database?.Dispose(); }
    }

    private static PolicySnapshot Snapshot(string keyPath)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(keyPath, writable: false);
        if (key is null) return new PolicySnapshot { KeyExists = false, ValueExists = false };
        var exists = key.GetValueNames().Contains(ValueName, StringComparer.Ordinal);
        return new PolicySnapshot { KeyExists = true, ValueExists = exists, ValueKind = exists ? key.GetValueKind(ValueName).ToString() : null,
            ValueData = exists ? key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() : null };
    }

    private static void RemoveExactKey(string keyPath)
    {
        const string parent = "SOFTWARE\\Policies\\EdrTest\\Runs";
        var leaf = keyPath[(parent.Length + 1)..];
        if (leaf.Length != 32 || leaf.Any(character => !Uri.IsHexDigit(character))) throw new ArgumentException("拒绝清理非本轮组策略键。");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parentKey = root.OpenSubKey(parent, writable: true);
        parentKey?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, string keyPath, Process? actor)
    {
        var started = DateTimeOffset.UtcNow; var errors = new List<string>(); var before = SafeSnapshot(keyPath);
        Stop(actor, errors);
        try { RemoveExactKey(keyPath); } catch (Exception exception) { errors.Add(exception.Message); }
        var after = SafeSnapshot(keyPath); var alive = actor is not null && IsAlive(actor);
        return new CleanupObservation { CaseRunId = invocation.CaseRunId, Action = "delete_exact_group_policy_test_key",
            Status = errors.Count == 0 && !after.KeyExists && !alive ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["key_path"] = $"HKEY_LOCAL_MACHINE\\{keyPath}", ["key_exists"] = before.KeyExists },
            After = new JsonObject { ["key_exists"] = after.KeyExists, ["actor_alive"] = alive }, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation) => new()
    { CaseRunId = invocation.CaseRunId, Action = "no_group_policy_key_allocated", Status = "succeeded", StartedAtUtc = DateTimeOffset.UtcNow, EndedAtUtc = DateTimeOffset.UtcNow };
    private static PolicySnapshot SafeSnapshot(string keyPath) { try { return Snapshot(keyPath); } catch { return new PolicySnapshot { KeyExists = false, ValueExists = false }; } }
    private static Process Start(string executable, IEnumerable<string> arguments, string cwd)
    { var info = new ProcessStartInfo { FileName = executable, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true }; foreach (var value in arguments) info.ArgumentList.Add(value); return Process.Start(info) ?? throw new InvalidOperationException("无法启动组策略 Actor。"); }
    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    { var watch = Stopwatch.StartNew(); while (!File.Exists(path)) { if (process.HasExited) throw new InvalidOperationException($"Actor 写结果前已退出：{process.ExitCode}"); if (watch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待组策略结果超时。"); Thread.Sleep(10); } return ProtocolJson.Read<BehaviorResult>(path); }
    private static ProgramObservation Observe(ControllerInvocation invocation, Process process, string executable, IEnumerable<string> arguments, DateTimeOffset fallback, string role, string name)
    { DateTimeOffset started; DateTimeOffset? ended; int? exit; try { started = process.StartTime.ToUniversalTime(); } catch { started = fallback; } try { ended = process.ExitTime.ToUniversalTime(); exit = process.ExitCode; } catch { ended = null; exit = null; }
      return new ProgramObservation { CaseRunId = invocation.CaseRunId, Role = role, InstanceName = name, ExecutablePath = executable,
        Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable), Md5 = Hashing.FileMd5(executable), Pid = process.Id,
        ParentPid = Environment.ProcessId, SessionId = TrySessionId(process), Architecture = Architecture(), CommandLine = FormatCommandLine(executable, arguments),
        WorkingDirectory = invocation.WorkDir, StartedAtUtc = started, EndedAtUtc = ended, ExitCode = exit, Metadata = new JsonObject { ["native_api"] = name } }; }
    private static ArtifactObservation Artifact(ControllerInvocation invocation, string path)
    { var runDir = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName; return new ArtifactObservation { CaseRunId = invocation.CaseRunId,
        Kind = "behavior_protocol", RelativePath = Path.GetRelativePath(runDir, path).Replace('\\', '/'), MediaType = "application/json", Sha256 = Hashing.FileSha256(path),
        SizeBytes = new FileInfo(path).Length, CreatedAtUtc = File.GetCreationTimeUtc(path), Sensitive = false, Metadata = new JsonObject { ["scope"] = "isolated_hklm_policy_key" } }; }
    private static void AddFact(RunDatabase db, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) => db.AddFact(new LocalFactObservation
    { CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "group_policy_activity_controller", Confidence = "high" });
    private static JsonObject ProcessReference(ProgramObservation value) => new() { ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid,
        ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine };
    private static void Stop(Process? process, ICollection<string> errors) { if (process is null) return; try { if (!process.HasExited) { process.Kill(entireProcessTree: true); if (!process.WaitForExit(5_000)) errors.Add($"PID {process.Id} 未退出。"); } } catch (InvalidOperationException) { } catch (Exception exception) { errors.Add(exception.Message); } }
    private static bool IsAlive(Process value) { try { return !value.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process value) { try { return value.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static void WriteStatus(string status, string? error) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = CapabilityId, ["operation"] = "modify", ["methods"] = 1, ["error"] = error }.ToJsonString(JsonDefaults.Options));
}
