using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;
using Microsoft.Win32;

namespace GroupPolicyActivity;

internal static class Program
{
    private const string CapabilityId = "win.group_policy.modify";
    private const string IsolatedValueName = "ValidationMarker";

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        Process? activeActor = null;
        string? isolatedKeyPath = null;
        KnownPolicyTarget? knownTarget = null;
        PolicySnapshot? knownOriginal = null;
        PolicySnapshot? knownPrepared = null;
        var isolatedCleanupCompleted = false;
        var knownCleanupCompleted = false;
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
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var targetSelection = parameters["known_policy_target"]?.GetValue<string>() ?? "auto";

            isolatedKeyPath = $"SOFTWARE\\Policies\\EdrTest\\Runs\\{invocation.Nonce}";
            var beforeValue = $"EDRTEST|{invocation.Nonce}|BEFORE";
            var afterValue = $"EDRTEST|{invocation.Nonce}|AFTER";
            RemoveExactKey(isolatedKeyPath);
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.CreateSubKey(isolatedKeyPath, writable: true) ?? throw new IOException("无法创建组策略隔离键。"))
                key.SetValue(IsolatedValueName, beforeValue, RegistryValueKind.String);
            var isolatedPrepared = RegistryNative.Snapshot(isolatedKeyPath, IsolatedValueName);
            if (!isolatedPrepared.ValueExists || isolatedPrepared.ValueData != beforeValue)
                throw new IOException("Controller 未确认隔离方法预置值。");

            var isolatedArguments = new[]
            {
                "--method", "isolated_policy_key", "--key-path", isolatedKeyPath,
                "--value-name", IsolatedValueName, "--value-data", afterValue,
            };
            var isolated = ExecuteMethod(database, invocation, package, "isolated_policy_key", "隔离策略键", isolatedArguments,
                "group-policy-isolated-result.json", holdMs, ref activeActor, stopwatch,
                result => result.Succeeded
                    && result.Before.ValueData == beforeValue
                    && result.After.ValueData == afterValue
                    && RegistryNative.Snapshot(isolatedKeyPath, IsolatedValueName).ValueDataSha256 == result.After.ValueDataSha256);
            var isolatedCleanup = CleanupIsolated(invocation, isolatedKeyPath, activeActor);
            activeActor?.Dispose(); activeActor = null;
            database.AddCleanup(isolatedCleanup);
            isolatedCleanupCompleted = true;
            isolatedKeyPath = null;
            if (isolatedCleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                    "GROUP_POLICY_ISOLATED_CLEANUP_FAILED", isolatedCleanup.ErrorMessage);
                return 30;
            }

            knownTarget = SelectKnownTarget(targetSelection, out var notApplicableReason);
            AddFact(database, invocation, "group_policy.known_policy_same_value.requested_target", JsonValue.Create(targetSelection), null);
            var knownMethodSucceeded = true;
            if (knownTarget is null)
            {
                AddFact(database, invocation, "group_policy.known_policy_same_value.applicable", JsonValue.Create(false), null);
                AddFact(database, invocation, "group_policy.known_policy_same_value.prepared_for_test", JsonValue.Create(false), null);
                AddFact(database, invocation, "group_policy.known_policy_same_value.not_applicable_reason", JsonValue.Create(notApplicableReason), null);
                database.AddCleanup(new CleanupObservation
                {
                    CaseRunId = invocation.CaseRunId, Sequence = 2,
                    Action = "no_known_policy_value_selected", Status = "succeeded",
                    StartedAtUtc = DateTimeOffset.UtcNow, EndedAtUtc = DateTimeOffset.UtcNow,
                    Before = new JsonObject { ["requested_target"] = targetSelection },
                    After = new JsonObject { ["registry_modified"] = false }, ErrorMessage = null,
                });
                knownCleanupCompleted = true;
            }
            else
            {
                knownOriginal = RegistryNative.Snapshot(knownTarget.KeyPath, knownTarget.ValueName);
                var preparedForTest = !knownOriginal.ValueExists;
                if (preparedForTest)
                {
                    PrepareSafeKnownPolicyValue(knownTarget);
                    knownPrepared = RegistryNative.Snapshot(knownTarget.KeyPath, knownTarget.ValueName);
                    if (!IsSafeFallbackValue(knownTarget, knownPrepared))
                        throw new IOException("未能确认 L2 兜底策略值已按 DWORD 1 安全预置。");
                }
                else knownPrepared = knownOriginal;
                AddFact(database, invocation, "group_policy.known_policy_same_value.prepared_for_test", JsonValue.Create(preparedForTest), null);
                AddFact(database, invocation, "group_policy.known_policy_same_value.original_key_exists", JsonValue.Create(knownOriginal.KeyExists), null);
                AddFact(database, invocation, "group_policy.known_policy_same_value.original_value_exists", JsonValue.Create(knownOriginal.ValueExists), null);
                var knownArguments = new[] { "--method", "known_policy_same_value", "--known-policy-target", knownTarget.Id };
                var known = ExecuteMethod(database, invocation, package, "known_policy_same_value", "真实策略同值回写", knownArguments,
                    "group-policy-known-value-result.json", holdMs, ref activeActor, stopwatch,
                    result => result.Succeeded
                        && result.TargetId == knownTarget.Id
                        && SnapshotsEqual(knownPrepared, result.Before)
                        && SnapshotsEqual(result.Before, result.After)
                        && SnapshotsEqual(result.After, RegistryNative.Snapshot(knownTarget.KeyPath, knownTarget.ValueName)));
                knownMethodSucceeded = known.Succeeded;
                var knownCleanup = RestoreKnownPolicyOriginalState(invocation, knownTarget, knownOriginal, knownPrepared, activeActor);
                activeActor?.Dispose(); activeActor = null;
                database.AddCleanup(knownCleanup);
                knownCleanupCompleted = true;
                if (knownCleanup.Status != "succeeded")
                {
                    database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "KNOWN_POLICY_RESTORE_FAILED", knownCleanup.ErrorMessage);
                    return 30;
                }
            }

            var succeeded = isolated.Succeeded && knownMethodSucceeded;
            AddFact(database, invocation, "group_policy.modify_succeeded", JsonValue.Create(succeeded), null);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);
            database.CompleteCapability(invocation.CaseRunId, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "GROUP_POLICY_OUTCOME_MISMATCH",
                succeeded ? null : "隔离策略键或真实策略同值回写的独立复核未通过。");
            WriteStatus(succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", knownTarget is not null, notApplicableReason,
                succeeded ? null : "组策略子测试未通过本地绝对基准。");
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanupFailed = false;
                    Stop(activeActor, []);
                    if (!isolatedCleanupCompleted && isolatedKeyPath is not null)
                    {
                        var cleanup = CleanupIsolated(invocation, isolatedKeyPath, activeActor);
                        database.AddCleanup(cleanup);
                        cleanupFailed |= cleanup.Status != "succeeded";
                    }
                    if (!knownCleanupCompleted && knownTarget is not null && knownOriginal is not null)
                    {
                        var cleanup = RestoreKnownPolicyOriginalState(invocation, knownTarget, knownOriginal, knownPrepared, activeActor);
                        database.AddCleanup(cleanup);
                        cleanupFailed |= cleanup.Status != "succeeded";
                    }
                    database.CompleteCapability(invocation.CaseRunId, cleanupFailed ? "CLEANUP_ERROR" : "SAMPLE_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "GROUP_POLICY_CONTROLLER_ERROR", exception.Message);
                    return cleanupFailed ? 30 : 20;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally { activeActor?.Dispose(); database?.Dispose(); }
    }

    private static MethodExecution ExecuteMethod(
        RunDatabase database,
        ControllerInvocation invocation,
        CapabilityPackage package,
        string method,
        string methodTitle,
        IReadOnlyList<string> methodArguments,
        string resultFileName,
        int holdMs,
        ref Process? activeActor,
        Stopwatch stopwatch,
        Func<BehaviorResult, bool> independentCheck)
    {
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var resultPath = Path.Combine(invocation.WorkDir, resultFileName);
        var actorArguments = methodArguments.Concat(new[]
        {
            "--result", resultPath,
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).ToArray();
        activeActor = Start(actorPath, actorArguments, invocation.WorkDir);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs, activeActor);
        if (!activeActor.WaitForExit(invocation.TimeoutMs))
        {
            activeActor.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待组策略 Actor 退出超时：PID {activeActor.Id}");
        }
        var actorInstanceIndex = method switch
        {
            "isolated_policy_key" => 0,
            "known_policy_same_value" => 1,
            _ => throw new InvalidDataException($"未知组策略测试方法：{method}"),
        };
        var actor = Observe(invocation, activeActor, actorPath, actorArguments, result.OccurredAtUtc,
            "actor", method, actorInstanceIndex);
        database.AddProgram(actor);
        var succeeded = result.Applicable && independentCheck(result);
        var artifact = Artifact(invocation, resultPath, method);
        database.AddArtifact(artifact);
        var localEvent = new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId, Sequence = actorInstanceIndex + 1,
            EventType = "group_policy", EventAction = "modify", Nonce = invocation.Nonce,
            OccurredAtUtc = result.OccurredAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds, Source = "group_policy_activity_controller",
            CollectionMethod = method == "known_policy_same_value"
                ? "native_reg_set_value_same_raw_bytes_plus_independent_snapshot"
                : "native_reg_set_value_plus_independent_snapshot",
            Confidence = "high", ActorProgramId = actor.ProgramInstanceId, EvidenceRefs = [artifact.ArtifactId],
            Data = new JsonObject
            {
                ["kind"] = "group_policy", ["operation"] = "modify", ["method"] = method, ["method_title"] = methodTitle,
                ["actor"] = ProcessReference(actor), ["scope"] = "computer", ["policy_path"] = result.KeyPath,
                ["policy_name"] = result.ValueName, ["backing_registry_path"] = result.KeyPath,
                ["hive"] = result.Hive, ["key_path"] = result.KeyPath, ["value_name"] = result.ValueName,
                ["value_type"] = result.After.ValueKind, ["native_type"] = result.After.NativeType,
                ["before_value"] = result.Before.ValueData, ["after_value"] = result.After.ValueData,
                ["before_value_sha256"] = result.Before.ValueDataSha256, ["after_value_sha256"] = result.After.ValueDataSha256,
                ["target_id"] = result.TargetId, ["native_api"] = result.NativeApi,
                ["before"] = SnapshotJson(result.Before), ["after"] = SnapshotJson(result.After),
                ["result"] = new JsonObject
                {
                    ["attempted"] = true, ["applicable"] = result.Applicable, ["succeeded"] = succeeded,
                    ["win32_error"] = result.Win32Error, ["message"] = result.Error,
                },
            },
        };
        database.AddEvent(localEvent);
        AddMethodFacts(database, invocation, method, result, actor, succeeded, localEvent.LocalEventId);
        return new MethodExecution(result, actor, localEvent, succeeded);
    }

    private static void AddMethodFacts(RunDatabase database, ControllerInvocation invocation, string method, BehaviorResult result,
        ProgramObservation actor, bool succeeded, string eventId)
    {
        var prefix = $"group_policy.{method}";
        var facts = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}.applicable"] = JsonValue.Create(result.Applicable),
            [$"{prefix}.modify_succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            [$"{prefix}.hive"] = JsonValue.Create(result.Hive), [$"{prefix}.key_path"] = JsonValue.Create(result.KeyPath),
            [$"{prefix}.value_name"] = JsonValue.Create(result.ValueName), [$"{prefix}.value_type"] = JsonValue.Create(result.After.ValueKind),
            [$"{prefix}.native_type"] = JsonValue.Create(result.After.NativeType),
            [$"{prefix}.before.value_data"] = JsonValue.Create(result.Before.ValueData),
            [$"{prefix}.before.value_data_sha256"] = JsonValue.Create(result.Before.ValueDataSha256),
            [$"{prefix}.before.raw_data_length"] = JsonValue.Create(result.Before.RawDataLength),
            [$"{prefix}.after.value_data"] = JsonValue.Create(result.After.ValueData),
            [$"{prefix}.after.value_data_sha256"] = JsonValue.Create(result.After.ValueDataSha256),
            [$"{prefix}.after.raw_data_length"] = JsonValue.Create(result.After.RawDataLength),
            [$"{prefix}.native_api"] = JsonValue.Create(result.NativeApi), [$"{prefix}.target_id"] = JsonValue.Create(result.TargetId),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid), [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.actor_command_line"] = JsonValue.Create(actor.CommandLine),
        };
        foreach (var (key, value) in facts) AddFact(database, invocation, key, value, eventId);
    }

    private static KnownPolicyTarget? SelectKnownTarget(string selection, out string? reason)
    {
        var errors = new List<string>();
        var candidates = KnownPolicyTargetCatalog.ResolveCandidates(selection);
        foreach (var target in candidates)
        {
            try
            {
                var snapshot = RegistryNative.Snapshot(target.KeyPath, target.ValueName);
                if (snapshot.ValueExists) { reason = null; return target; }
            }
            catch (Exception exception) { errors.Add($"{target.Id}: {exception.Message}"); }
        }
        var fallback = candidates.FirstOrDefault(target => target.Id == "windows-smart-screen-enable");
        if (fallback is not null)
        {
            reason = "当前机器没有已存在的白名单策略值；L2 子测试将临时预置安全增强值 EnableSmartScreen=1，并在采证后恢复原状态。";
            return fallback;
        }
        reason = errors.Count > 0
            ? $"白名单策略值均不可读取：{string.Join(" | ", errors)}"
            : selection == "auto"
                ? "当前机器没有任何已存在的白名单策略值；为避免改变安全配置，未创建真实策略值。"
                : $"指定白名单策略值 {selection} 不存在；为避免改变安全配置，未创建该值。";
        return null;
    }

    private static void PrepareSafeKnownPolicyValue(KnownPolicyTarget target)
    {
        if (target.Id != "windows-smart-screen-enable"
            || target.KeyPath != @"SOFTWARE\Policies\Microsoft\Windows\System"
            || target.ValueName != "EnableSmartScreen")
            throw new InvalidOperationException("拒绝为非安全兜底目标创建组策略值。");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(target.KeyPath, writable: true)
            ?? throw new IOException("无法创建 Windows System 策略键。");
        if (key.GetValueNames().Any(name => string.Equals(name, target.ValueName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("安全兜底值在预置前被其他进程创建；拒绝覆盖并发策略更新，请重试。");
        key.SetValue(target.ValueName, 1, RegistryValueKind.DWord);
    }

    private static bool IsSafeFallbackValue(KnownPolicyTarget target, PolicySnapshot snapshot) =>
        target.Id == "windows-smart-screen-enable"
        && snapshot.ValueExists && snapshot.NativeType == 4 && snapshot.RawDataLength == 4
        && string.Equals(snapshot.ValueData, "1", StringComparison.Ordinal);

    private static bool SnapshotsEqual(PolicySnapshot left, PolicySnapshot right) =>
        left.KeyExists && left.ValueExists && right.KeyExists && right.ValueExists
        && left.NativeType == right.NativeType && left.RawDataLength == right.RawDataLength
        && string.Equals(left.ValueDataSha256, right.ValueDataSha256, StringComparison.Ordinal);

    private static CleanupObservation RestoreKnownPolicyOriginalState(ControllerInvocation invocation, KnownPolicyTarget target,
        PolicySnapshot original, PolicySnapshot? prepared, Process? actor)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(actor, errors);
        var valueRemoved = false;
        var emptyCreatedKeyRemoved = false;
        PolicySnapshot beforeRestore;
        try { beforeRestore = RegistryNative.Snapshot(target.KeyPath, target.ValueName); }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            beforeRestore = new PolicySnapshot { KeyExists = false, ValueExists = false };
        }
        if (original.ValueExists)
        {
            if (!SnapshotsEqual(original, beforeRestore))
                errors.Add("真实策略值的类型、长度或原始数据哈希与测试前不同；未自动覆盖可能的并发策略更新。");
        }
        else if (beforeRestore.ValueExists)
        {
            if ((prepared is not null && SnapshotsEqual(prepared, beforeRestore)) || IsSafeFallbackValue(target, beforeRestore))
            {
                try
                {
                    DeleteSafeKnownPolicyValue(target);
                    valueRemoved = true;
                }
                catch (Exception exception) { errors.Add(exception.Message); }
            }
            else errors.Add("临时策略值已被外部进程改变；为避免删除并发策略更新，未自动移除。");
        }
        var afterValueRestore = SafeSnapshot(target.KeyPath, target.ValueName);
        if (!original.KeyExists && !afterValueRestore.ValueExists)
        {
            try { emptyCreatedKeyRemoved = RemoveSafeKnownPolicyKeyIfEmpty(target); }
            catch (Exception exception) { errors.Add(exception.Message); }
        }
        PolicySnapshot current;
        try { current = RegistryNative.Snapshot(target.KeyPath, target.ValueName); }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            current = new PolicySnapshot { KeyExists = false, ValueExists = false };
        }
        if (original.ValueExists ? !SnapshotsEqual(original, current) : current.ValueExists)
            errors.Add("真实策略值未恢复到测试前状态。");
        var alive = actor is not null && IsAlive(actor);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId, Sequence = 2,
            Action = original.ValueExists ? "verify_known_policy_value_unchanged" : "restore_created_known_policy_value",
            Status = errors.Count == 0 && !alive ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject
            {
                ["target_id"] = target.Id, ["key_path"] = $"HKEY_LOCAL_MACHINE\\{target.KeyPath}",
                ["value_name"] = target.ValueName, ["original_snapshot"] = SnapshotJson(original),
                ["prepared_snapshot"] = prepared is null ? null : SnapshotJson(prepared),
                ["before_restore_snapshot"] = SnapshotJson(beforeRestore),
            },
            After = new JsonObject
            {
                ["snapshot"] = SnapshotJson(current), ["actor_alive"] = alive,
                ["registry_write_during_cleanup"] = !original.ValueExists,
                ["temporary_value_removed"] = valueRemoved,
                ["empty_created_key_removed"] = emptyCreatedKeyRemoved,
            },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static void DeleteSafeKnownPolicyValue(KnownPolicyTarget target)
    {
        if (target.Id != "windows-smart-screen-enable")
            throw new InvalidOperationException("拒绝删除非安全兜底目标的策略值。");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(target.KeyPath, writable: true);
        if (key is null) return;
        key.DeleteValue(target.ValueName, throwOnMissingValue: false);
    }

    private static bool RemoveSafeKnownPolicyKeyIfEmpty(KnownPolicyTarget target)
    {
        if (target.Id != "windows-smart-screen-enable")
            throw new InvalidOperationException("拒绝清理非安全兜底目标的策略键。");
        var separator = target.KeyPath.LastIndexOf('\\');
        var parentPath = target.KeyPath[..separator];
        var leaf = target.KeyPath[(separator + 1)..];
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = root.OpenSubKey(target.KeyPath, writable: false))
        {
            if (key is null) return true;
            if (key.GetValueNames().Length > 0 || key.GetSubKeyNames().Length > 0) return false;
        }
        using var parent = root.OpenSubKey(parentPath, writable: true)
            ?? throw new IOException("安全兜底策略父键在清理前已不存在。");
        parent.DeleteSubKey(leaf, throwOnMissingSubKey: false);
        using var remaining = root.OpenSubKey(target.KeyPath, writable: false);
        return remaining is null;
    }

    private static CleanupObservation CleanupIsolated(ControllerInvocation invocation, string keyPath, Process? actor)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var before = SafeSnapshot(keyPath, IsolatedValueName);
        Stop(actor, errors);
        try { RemoveExactKey(keyPath); } catch (Exception exception) { errors.Add(exception.Message); }
        var after = SafeSnapshot(keyPath, IsolatedValueName);
        var alive = actor is not null && IsAlive(actor);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId, Sequence = 1,
            Action = "delete_exact_group_policy_test_key",
            Status = errors.Count == 0 && !after.KeyExists && !alive ? "succeeded" : "failed", StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["key_path"] = $"HKEY_LOCAL_MACHINE\\{keyPath}", ["snapshot"] = SnapshotJson(before) },
            After = new JsonObject { ["snapshot"] = SnapshotJson(after), ["actor_alive"] = alive },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static JsonObject SnapshotJson(PolicySnapshot snapshot) => new()
    {
        ["key_exists"] = snapshot.KeyExists, ["value_exists"] = snapshot.ValueExists,
        ["value_type"] = snapshot.ValueKind, ["native_type"] = snapshot.NativeType,
        ["value_data"] = snapshot.ValueData, ["value_data_sha256"] = snapshot.ValueDataSha256,
        ["raw_data_length"] = snapshot.RawDataLength,
    };

    private static PolicySnapshot SafeSnapshot(string keyPath, string valueName)
    {
        try { return RegistryNative.Snapshot(keyPath, valueName); }
        catch { return new PolicySnapshot { KeyExists = false, ValueExists = false }; }
    }

    private static void RemoveExactKey(string keyPath)
    {
        const string parent = "SOFTWARE\\Policies\\EdrTest\\Runs";
        var leaf = keyPath[(parent.Length + 1)..];
        if (leaf.Length != 32 || leaf.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("拒绝清理非本轮组策略键。");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parentKey = root.OpenSubKey(parent, writable: true);
        parentKey?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
    }

    private static Process Start(string executable, IEnumerable<string> arguments, string cwd)
    {
        var info = new ProcessStartInfo { FileName = executable, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in arguments) info.ArgumentList.Add(value);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动组策略 Actor。");
    }

    private static BehaviorResult WaitAndRead(string path, int timeoutMs, Process process)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"Actor 写结果前已退出：{process.ExitCode}");
            if (watch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待组策略结果超时。");
            Thread.Sleep(10);
        }
        return ProtocolJson.Read<BehaviorResult>(path);
    }

    private static ProgramObservation Observe(ControllerInvocation invocation, Process process, string executable,
        IEnumerable<string> arguments, DateTimeOffset fallback, string role, string name, int instanceIndex)
    {
        DateTimeOffset started;
        DateTimeOffset? ended;
        int? exit;
        try { started = process.StartTime.ToUniversalTime(); } catch { started = fallback; }
        try { ended = process.ExitTime.ToUniversalTime(); exit = process.ExitCode; } catch { ended = null; exit = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId, Role = role, InstanceName = name, InstanceIndex = instanceIndex,
            ExecutablePath = executable,
            Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable), Md5 = Hashing.FileMd5(executable),
            Pid = process.Id, ParentPid = Environment.ProcessId, SessionId = TrySessionId(process), Architecture = Architecture(),
            CommandLine = FormatCommandLine(executable, arguments), WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = started, EndedAtUtc = ended, ExitCode = exit,
            Metadata = new JsonObject { ["native_api"] = "RegSetValueExW", ["method"] = name },
        };
    }

    private static ArtifactObservation Artifact(ControllerInvocation invocation, string path, string method)
    {
        var runDir = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId, Kind = "behavior_protocol",
            RelativePath = Path.GetRelativePath(runDir, path).Replace('\\', '/'), MediaType = "application/json",
            Sha256 = Hashing.FileSha256(path), SizeBytes = new FileInfo(path).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(path), Sensitive = false,
            Metadata = new JsonObject
            {
                ["scope"] = method == "known_policy_same_value" ? "existing_allowlisted_policy_value" : "isolated_hklm_policy_key",
                ["method"] = method,
            },
        };
    }

    private static void AddFact(RunDatabase db, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) =>
        db.AddFact(new LocalFactObservation
        {
            CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value,
            ObservedAtUtc = DateTimeOffset.UtcNow, Source = "group_policy_activity_controller", Confidence = "high",
        });

    private static JsonObject ProcessReference(ProgramObservation value) => new()
    {
        ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid,
        ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine,
    };

    private static void Stop(Process? process, ICollection<string> errors)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5_000)) errors.Add($"PID {process.Id} 未退出。");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception exception) { errors.Add(exception.Message); }
    }

    private static bool IsAlive(Process value) { try { return !value.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process value) { try { return value.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static void WriteStatus(string status, bool knownApplicable, string? notice, string? error) => Console.WriteLine(new JsonObject
    {
        ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = CapabilityId,
        ["operation"] = "modify", ["methods"] = 2, ["known_policy_same_value_applicable"] = knownApplicable,
        ["notice"] = notice, ["error"] = error,
    }.ToJsonString(JsonDefaults.Options));

    private sealed record MethodExecution(BehaviorResult Result, ProgramObservation Actor, LocalEventObservation Event, bool Succeeded);
}
