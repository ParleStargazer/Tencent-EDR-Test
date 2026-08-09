using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdrTest;

namespace UserAccountActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.account.local.create"] = "local_create",
        ["win.account.local.modify"] = "local_modify",
        ["win.account.local.delete"] = "local_delete",
        ["win.account.login"] = "login",
        ["win.account.logoff"] = "logoff",
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        ExecutionState? state = null;
        string? accountName = null;
        string? requestPath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
            {
                throw new InvalidDataException($"UserAccountActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            }

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            accountName = BuildAccountName(invocation.Nonce);
            requestPath = Path.Combine(invocation.WorkDir, "account-behavior-request.json");
            state = Execute(invocation, package, operation, accountName, requestPath, parameters);
            var actor = CreateProgram(invocation, state);
            database.AddProgram(actor);

            var verified = VerifyOutcome(operation, state);
            var localSucceeded = state.Result.Succeeded && verified;
            var artifact = CreateEvidenceArtifact(invocation, state);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state, actor, artifact.ArtifactId);
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, state, localEvent.LocalEventId, actor, localSucceeded);

            var cleanup = Cleanup(invocation, accountName, requestPath, state.Actor);
            database.AddCleanup(cleanup);
            if (!string.Equals(cleanup.Status, "succeeded", StringComparison.Ordinal))
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "ACCOUNT_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var errorCode = localSucceeded ? null : "ACCOUNT_BEHAVIOR_FAILED";
            var errorMessage = localSucceeded ? null : state.Result.Error ?? "Controller 未确认账号行为后的本地状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, errorCode, errorMessage);
            WriteStatus(status, package.Manifest.CapabilityId, operation, errorMessage);
            return localSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null && accountName is not null)
            {
                try
                {
                    var cleanup = Cleanup(invocation, accountName, requestPath, state?.Actor);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "USER_ACCOUNT_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine(cleanupException);
                    return 30;
                }
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
        string accountName,
        string requestPath,
        JsonObject parameters)
    {
        Directory.CreateDirectory(invocation.WorkDir);
        if (AccountNative.Snapshot(accountName).Exists)
        {
            throw new InvalidOperationException($"本轮临时账号事先已经存在，拒绝覆盖：{accountName}");
        }

        var setupDelayMs = parameters["setup_delay_ms"]?.GetValue<int>() ?? 750;
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        var comment = $"EDRTest {operation} {invocation.Nonce[..Math.Min(12, invocation.Nonce.Length)]}";
        ProtocolJson.WriteAtomic(requestPath, new BehaviorRequest
        {
            Operation = operation,
            AccountName = accountName,
            Password = BuildPassword(),
            Nonce = invocation.Nonce,
            Comment = comment,
            SetupDelayMs = setupDelayMs,
            HoldMs = holdMs,
        });

        var actorDefinition = package.Manifest.Participants.Single(participant => participant.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var resultPath = Path.Combine(invocation.WorkDir, "account-behavior-result.json");
        var arguments = new[] { "--request", requestPath, "--result", resultPath };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        var result = WaitAndRead(resultPath, invocation.TimeoutMs);
        if (!actor.WaitForExit(invocation.TimeoutMs))
        {
            actor.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待账号行为 Actor 退出超时：PID {actor.Id}");
        }
        return new ExecutionState(actorPath, arguments, actor, requestPath, resultPath, accountName, comment, result);
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
                ["captured_by"] = "UserAccountActivity.Controller",
                ["nonce_in_command_line"] = false,
                ["secret_in_command_line"] = false,
                ["account_name"] = state.AccountName,
            },
        };
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation,
        string operation,
        Stopwatch stopwatch,
        ExecutionState state,
        ProgramObservation actor,
        string evidenceArtifactId)
    {
        var account = operation == "local_delete" ? state.Result.Before : state.Result.After;
        var data = new JsonObject
        {
            ["kind"] = "account",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["account"] = AccountIdentity(account),
            ["before"] = AccountState(state.Result.Before),
            ["after"] = AccountState(state.Result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = state.Result.Succeeded,
                ["win32_error"] = state.Result.Win32Error,
                ["message"] = state.Result.Error,
            },
        };
        if (state.Result.Session is { } session)
        {
            data["session"] = new JsonObject
            {
                ["session_id"] = session.SessionId,
                ["logon_id"] = session.LogonId,
                ["logon_type"] = session.LogonType,
                ["authentication_package"] = session.AuthenticationPackage,
                ["source_address"] = session.SourceAddress,
            };
        }

        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            EventType = "account",
            EventAction = operation,
            Nonce = invocation.Nonce,
            OccurredAtUtc = state.Result.OccurredAtUtc,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "user_account_activity_controller",
            CollectionMethod = operation is "login" or "logoff"
                ? "native_logon_token_and_account_snapshot"
                : "netapi_before_after_account_snapshot",
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
        ExecutionState state,
        string eventId,
        ProgramObservation actor,
        bool succeeded)
    {
        var account = operation == "local_delete" ? state.Result.Before : state.Result.After;
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"account.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["account.occurred_at_utc"] = JsonValue.Create(Values.Utc(state.Result.OccurredAtUtc)),
            ["account.name"] = JsonValue.Create(account.Name),
            ["account.sid"] = JsonValue.Create(account.Sid),
            ["account.domain"] = JsonValue.Create(account.Domain),
            ["account.account_type"] = JsonValue.Create(account.AccountType),
            ["account.actor_pid"] = JsonValue.Create(actor.Pid),
            ["account.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["account.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["account.native_api"] = JsonValue.Create(state.Result.NativeApi),
            ["account.before.exists"] = JsonValue.Create(state.Result.Before.Exists),
            ["account.before.comment"] = JsonValue.Create(state.Result.Before.Comment),
            ["account.after.exists"] = JsonValue.Create(state.Result.After.Exists),
            ["account.after.comment"] = JsonValue.Create(state.Result.After.Comment),
            ["account.changed_field"] = JsonValue.Create(state.Result.ChangedField),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        if (state.Result.Session is { } session)
        {
            values["account.session.logon_id"] = JsonValue.Create(session.LogonId);
            values["account.session.logon_type"] = JsonValue.Create(session.LogonType);
            values["account.session.authentication_package"] = JsonValue.Create(session.AuthenticationPackage);
            values["account.session.source_address"] = JsonValue.Create(session.SourceAddress);
            values["account.session.token_validated"] = JsonValue.Create(session.TokenValidated);
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
                Source = "user_account_activity_controller",
                Confidence = "high",
            });
        }
    }

    private static bool VerifyOutcome(string operation, ExecutionState state)
    {
        var current = AccountNative.Snapshot(state.AccountName);
        return operation switch
        {
            "local_create" => !state.Result.Before.Exists && current.Exists && current.Sid == state.Result.After.Sid,
            "local_modify" => state.Result.Before.Exists && current.Exists && current.Comment == state.ExpectedComment
                && state.Result.ChangedField == "comment",
            "local_delete" => state.Result.Before.Exists && !current.Exists,
            "login" or "logoff" => current.Exists && current.Sid == state.Result.After.Sid
                && state.Result.Session is { TokenValidated: true, LogonId: not null },
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
                ["native_api"] = state.Result.NativeApi,
                ["contains_password"] = false,
            },
        };
    }

    private static CleanupObservation Cleanup(
        ControllerInvocation invocation,
        string accountName,
        string? requestPath,
        Process? actor)
    {
        var started = DateTimeOffset.UtcNow;
        var actorWasAlive = IsAlive(actor);
        var requestExisted = requestPath is not null && File.Exists(requestPath);
        var errors = new List<string>();
        Stop(actor, errors);
        AccountSnapshot? beforeAccount = null;
        try { beforeAccount = AccountNative.Snapshot(accountName); }
        catch (Exception exception) { errors.Add($"清理前无法读取账号 {accountName}：{exception.Message}"); }
        var beforeExists = beforeAccount?.Exists == true;
        if (!IsControlledAccountName(accountName))
        {
            errors.Add($"拒绝清理不符合本轮命名规则的账号：{accountName}");
        }
        else if (beforeAccount?.Exists == true && !IsOwnedAccount(beforeAccount, invocation.Nonce))
        {
            errors.Add($"账号 {accountName} 缺少本轮 nonce 所有权标记，拒绝删除。");
        }
        else if (beforeAccount?.Exists == true)
        {
            try { _ = AccountNative.DeleteIfExists(accountName); }
            catch (Exception exception) { errors.Add($"删除临时账号 {accountName} 失败：{exception.Message}"); }
        }
        DeleteRequest(requestPath, invocation.WorkDir, errors);
        var afterExists = true;
        try { afterExists = AccountNative.Snapshot(accountName).Exists; }
        catch (Exception exception) { errors.Add($"清理后无法读取账号 {accountName}：{exception.Message}"); }
        var succeeded = errors.Count == 0 && !afterExists && !IsAlive(actor);
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_actor_remove_request_and_delete_ephemeral_local_account",
            Status = succeeded ? "succeeded" : "failed",
            StartedAtUtc = started,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject
            {
                ["actor_alive"] = actorWasAlive,
                ["account_name"] = accountName,
                ["account_exists"] = beforeExists,
                ["request_exists"] = requestExisted,
            },
            After = new JsonObject
            {
                ["actor_alive"] = IsAlive(actor),
                ["account_name"] = accountName,
                ["account_exists"] = afterExists,
                ["request_exists"] = requestPath is not null && File.Exists(requestPath),
            },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static void DeleteRequest(string? requestPath, string workDirectory, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(requestPath)) return;
        try
        {
            var fullPath = Path.GetFullPath(requestPath);
            var root = Path.GetFullPath(workDirectory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"拒绝删除工作目录外请求文件：{fullPath}");
                return;
            }
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception exception)
        {
            errors.Add($"删除临时请求文件失败：{exception.Message}");
        }
    }

    private static JsonObject AccountIdentity(AccountSnapshot account) => new()
    {
        ["name"] = account.Name,
        ["sid"] = account.Sid,
        ["domain"] = account.Domain,
        ["account_type"] = account.AccountType,
    };

    private static JsonObject AccountState(AccountSnapshot account) => new()
    {
        ["exists"] = account.Exists,
        ["name"] = account.Name,
        ["sid"] = account.Sid,
        ["domain"] = account.Domain,
        ["account_type"] = account.AccountType,
        ["full_name"] = account.FullName,
        ["comment"] = account.Comment,
        ["flags"] = account.Flags,
        ["active"] = account.Active,
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

    private static string BuildAccountName(string nonce)
    {
        var tag = new string(nonce.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray()).ToLowerInvariant();
        if (tag.Length < 12)
        {
            tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)))[..12].ToLowerInvariant();
        }
        return "edrt" + tag;
    }

    private static string BuildPassword() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static bool IsControlledAccountName(string value) => value.StartsWith("edrt", StringComparison.Ordinal)
        && value.Length is >= 8 and <= 20 && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsOwnedAccount(AccountSnapshot account, string nonce)
    {
        var marker = nonce[..Math.Min(12, nonce.Length)];
        return account.Comment?.Contains(marker, StringComparison.Ordinal) == true;
    }

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

    private static AccountSnapshot SafeSnapshot(string accountName)
    {
        try { return AccountNative.Snapshot(accountName); }
        catch { return AccountNative.Missing(accountName); }
    }

    private static void Stop(Process? process, ICollection<string> errors)
    {
        if (process is null) return;
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

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
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

    private sealed class ExecutionState(
        string actorPath,
        IReadOnlyList<string> actorArguments,
        Process actor,
        string requestPath,
        string resultPath,
        string accountName,
        string expectedComment,
        BehaviorResult result) : IDisposable
    {
        public string ActorPath { get; } = actorPath;
        public IReadOnlyList<string> ActorArguments { get; } = actorArguments;
        public Process Actor { get; } = actor;
        public string RequestPath { get; } = requestPath;
        public string ResultPath { get; } = resultPath;
        public string AccountName { get; } = accountName;
        public string ExpectedComment { get; } = expectedComment;
        public BehaviorResult Result { get; } = result;
        public void Dispose() => Actor.Dispose();
    }
}
