using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace NamedPipeActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["win.named_pipe.create"] = "create", ["win.named_pipe.connect"] = "connect" };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null; RunDatabase? database = null; Process? server = null; Process? client = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation)) throw new InvalidDataException($"NamedPipeActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb); database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject() ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);
            var pipeName = $"\\\\.\\pipe\\EdrTest_{invocation.Nonce}_{operation}";
            var readyPath = Path.Combine(invocation.WorkDir, "named-pipe-ready.json");
            var actorResultPath = Path.Combine(invocation.WorkDir, "named-pipe-actor-result.json");
            var helperResultPath = Path.Combine(invocation.WorkDir, "named-pipe-helper-result.json");
            var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
            var helperDefinition = package.Manifest.Participants.Single(value => value.Role == "helper");
            var actorPath = package.ResolveProgram(actorDefinition.Executable); var helperPath = package.ResolveProgram(helperDefinition.Executable);
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_000;
            var roleTimeout = Math.Min(invocation.TimeoutMs, 120_000);
            var serverIsActor = operation == "create";
            var serverPath = serverIsActor ? actorPath : helperPath; var clientPath = serverIsActor ? helperPath : actorPath;
            var serverResultPath = serverIsActor ? actorResultPath : helperResultPath; var clientResultPath = serverIsActor ? helperResultPath : actorResultPath;
            var serverArgs = Arguments("server", pipeName, invocation.Nonce, serverResultPath, readyPath, roleTimeout, holdMs);
            var clientArgs = Arguments("client", pipeName, invocation.Nonce, clientResultPath, readyPath, roleTimeout, holdMs);
            server = Start(serverPath, serverArgs, invocation.WorkDir); WaitForFile(readyPath, invocation.TimeoutMs, server, "服务端就绪");
            client = Start(clientPath, clientArgs, invocation.WorkDir);
            var serverResult = WaitAndRead(serverResultPath, invocation.TimeoutMs, server); var clientResult = WaitAndRead(clientResultPath, invocation.TimeoutMs, client);
            WaitForExit(server, invocation.TimeoutMs, "服务端"); WaitForExit(client, invocation.TimeoutMs, "客户端");
            var actorProcess = serverIsActor ? server : client; var helperProcess = serverIsActor ? client : server;
            var actorResult = serverIsActor ? serverResult : clientResult; var helperResult = serverIsActor ? clientResult : serverResult;
            var actorArgs = serverIsActor ? serverArgs : clientArgs; var helperArgs = serverIsActor ? clientArgs : serverArgs;
            var actor = Observe(invocation, actorProcess, actorPath, actorArgs, actorResult, "actor", 0);
            var helper = Observe(invocation, helperProcess, helperPath, helperArgs, helperResult, "helper", 0);
            database.AddProgram(actor); database.AddProgram(helper);
            var succeeded = serverResult.Succeeded && clientResult.Succeeded && serverResult.NonceVerified && clientResult.NonceVerified
                && serverResult.Role == "server" && clientResult.Role == "client" && serverResult.PipeName == pipeName && clientResult.PipeName == pipeName
                && serverResult.BytesRead == clientResult.BytesWritten && serverResult.BytesWritten == clientResult.BytesRead
                && serverResult.CompletedAtUtc <= clientResult.CompletedAtUtc;
            var artifacts = new[] { Artifact(invocation, actorResultPath, "actor"), Artifact(invocation, helperResultPath, "helper") };
            foreach (var artifact in artifacts) database.AddArtifact(artifact);
            var localEvent = new LocalEventObservation
            {
                CaseRunId = invocation.CaseRunId, EventType = "named_pipe", EventAction = operation, Nonce = invocation.Nonce,
                OccurredAtUtc = actorResult.OccurredAtUtc, ObservedAtUtc = DateTimeOffset.UtcNow, MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
                Source = "named_pipe_activity_controller", CollectionMethod = "win32_named_pipe_dual_process_nonce_handshake", Confidence = "high",
                ActorProgramId = actor.ProgramInstanceId, TargetProgramId = helper.ProgramInstanceId, EvidenceRefs = artifacts.Select(value => value.ArtifactId).ToList(),
                Data = new JsonObject { ["kind"] = "named_pipe", ["operation"] = operation, ["pipe_name"] = pipeName,
                    ["operation_name"] = operation == "create" ? "创建管道" : "打开管道", ["direction"] = "duplex",
                    ["actor"] = ProcessReference(actor), ["helper"] = ProcessReference(helper),
                    ["server"] = ProcessReference(serverIsActor ? actor : helper), ["client"] = ProcessReference(serverIsActor ? helper : actor),
                    ["handshake"] = new JsonObject { ["server"] = ResultState(serverResult), ["client"] = ResultState(clientResult), ["nonce_verified"] = serverResult.NonceVerified && clientResult.NonceVerified },
                    ["result"] = new JsonObject { ["attempted"] = true, ["succeeded"] = succeeded } },
            };
            database.AddEvent(localEvent);
            var facts = new Dictionary<string, JsonNode?>
            {
                ["named_pipe.operation_succeeded"] = JsonValue.Create(succeeded), ["named_pipe.occurred_at_utc"] = JsonValue.Create(Values.Utc(actorResult.OccurredAtUtc)),
                ["named_pipe.completed_at_utc"] = JsonValue.Create(Values.Utc(actorResult.CompletedAtUtc)), ["named_pipe.name"] = JsonValue.Create(pipeName),
                ["named_pipe.operation"] = JsonValue.Create(operation), ["named_pipe.operation_name"] = JsonValue.Create(operation == "create" ? "创建管道" : "打开管道"),
                ["named_pipe.actor_pid"] = JsonValue.Create(actor.Pid), ["named_pipe.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
                ["named_pipe.actor_command_line"] = JsonValue.Create(actor.CommandLine), ["named_pipe.helper_pid"] = JsonValue.Create(helper.Pid),
                ["named_pipe.helper_executable"] = JsonValue.Create(helper.ExecutablePath), ["named_pipe.server_pid"] = JsonValue.Create(serverResult.ProcessId),
                ["named_pipe.client_pid"] = JsonValue.Create(clientResult.ProcessId), ["named_pipe.server_native_api"] = JsonValue.Create(serverResult.NativeApi),
                ["named_pipe.client_native_api"] = JsonValue.Create(clientResult.NativeApi), ["named_pipe.nonce_verified"] = JsonValue.Create(serverResult.NonceVerified && clientResult.NonceVerified),
                ["named_pipe.bytes_client_to_server"] = JsonValue.Create(serverResult.BytesRead), ["named_pipe.bytes_server_to_client"] = JsonValue.Create(clientResult.BytesRead),
                ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
            };
            foreach (var (key, value) in facts) AddFact(database, invocation, key, value, localEvent.LocalEventId);
            var cleanup = Cleanup(invocation, pipeName, server, client); database.AddCleanup(cleanup); server.Dispose(); client.Dispose(); server = null; client = null;
            if (cleanup.Status != "succeeded") { database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "NAMED_PIPE_CLEANUP_FAILED", cleanup.ErrorMessage); return 30; }
            database.CompleteCapability(invocation.CaseRunId, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR", DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                succeeded ? null : "NAMED_PIPE_HANDSHAKE_FAILED", succeeded ? null : "双进程 nonce 握手或角色关系未通过独立验证。");
            WriteStatus(package.Manifest.CapabilityId, operation, succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR"); return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null) try { var cleanup = Cleanup(invocation, null, server, client); database.AddCleanup(cleanup);
                database.CompleteCapability(invocation.CaseRunId, cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "NAMED_PIPE_CONTROLLER_ERROR", exception.Message); return cleanup.Status == "succeeded" ? 20 : 30; } catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            return 20;
        }
        finally { server?.Dispose(); client?.Dispose(); database?.Dispose(); }
    }

    private static string[] Arguments(string role, string pipe, string nonce, string result, string ready, int timeout, int hold) =>
        ["--role", role, "--pipe-name", pipe, "--nonce", nonce, "--result", result, "--ready", ready, "--timeout-ms", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), "--hold-ms", hold.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    private static Process Start(string executable, IEnumerable<string> arguments, string cwd) { var info = new ProcessStartInfo { FileName = executable, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true }; foreach (var value in arguments) info.ArgumentList.Add(value); return Process.Start(info) ?? throw new InvalidOperationException($"无法启动命名管道程序：{executable}"); }
    private static void WaitForFile(string path, int timeout, Process process, string label) { var watch = Stopwatch.StartNew(); while (!File.Exists(path)) { if (process.HasExited) throw new InvalidOperationException($"{label}前程序已退出：{process.ExitCode}"); if (watch.ElapsedMilliseconds >= timeout) throw new TimeoutException($"等待{label}超时。"); Thread.Sleep(5); } }
    private static BehaviorResult WaitAndRead(string path, int timeout, Process process) { WaitForFile(path, timeout, process, "结果文件"); return ProtocolJson.Read<BehaviorResult>(path); }
    private static void WaitForExit(Process process, int timeout, string role) { if (process.WaitForExit(timeout)) return; process.Kill(entireProcessTree: true); throw new TimeoutException($"等待命名管道{role}退出超时：PID {process.Id}"); }
    private static ProgramObservation Observe(ControllerInvocation invocation, Process process, string executable, IEnumerable<string> arguments, BehaviorResult result, string role, int index)
    { DateTimeOffset started; DateTimeOffset? ended; int? exit; try { started = process.StartTime.ToUniversalTime(); } catch { started = result.OccurredAtUtc; } try { ended = process.ExitTime.ToUniversalTime(); exit = process.ExitCode; } catch { ended = null; exit = null; }
      return new ProgramObservation { CaseRunId = invocation.CaseRunId, Role = role, InstanceName = result.NativeApi, InstanceIndex = index, ExecutablePath = executable,
        Sha256 = Hashing.FileSha256(executable), Sha1 = Hashing.FileSha1(executable), Md5 = Hashing.FileMd5(executable), Pid = process.Id, ParentPid = Environment.ProcessId,
        SessionId = TrySessionId(process), Architecture = Architecture(), CommandLine = FormatCommandLine(executable, arguments), WorkingDirectory = invocation.WorkDir,
        StartedAtUtc = started, EndedAtUtc = ended, ExitCode = exit, Metadata = new JsonObject { ["pipe_role"] = result.Role, ["native_api"] = result.NativeApi } }; }
    private static ArtifactObservation Artifact(ControllerInvocation invocation, string path, string role) { var runDir = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
      return new ArtifactObservation { CaseRunId = invocation.CaseRunId, Kind = "behavior_protocol", RelativePath = Path.GetRelativePath(runDir, path).Replace('\\', '/'), MediaType = "application/json",
        Sha256 = Hashing.FileSha256(path), SizeBytes = new FileInfo(path).Length, CreatedAtUtc = File.GetCreationTimeUtc(path), Sensitive = false, Metadata = new JsonObject { ["role"] = role } }; }
    private static CleanupObservation Cleanup(ControllerInvocation invocation, string? pipeName, Process? server, Process? client)
    { var started = DateTimeOffset.UtcNow; var errors = new List<string>(); Stop(server, errors); Stop(client, errors); var serverAlive = server is not null && IsAlive(server); var clientAlive = client is not null && IsAlive(client);
      return new CleanupObservation { CaseRunId = invocation.CaseRunId, Action = "stop_pipe_participants_and_release_ephemeral_pipe", Status = errors.Count == 0 && !serverAlive && !clientAlive ? "succeeded" : "failed",
        StartedAtUtc = started, EndedAtUtc = DateTimeOffset.UtcNow, Before = new JsonObject { ["pipe_name"] = pipeName }, After = new JsonObject { ["server_alive"] = serverAlive, ["client_alive"] = clientAlive, ["pipe_lifetime"] = "process_scoped" },
        ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) }; }
    private static void AddFact(RunDatabase db, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) => db.AddFact(new LocalFactObservation
    { CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "named_pipe_activity_controller", Confidence = "high" });
    private static JsonObject ProcessReference(ProgramObservation value) => new() { ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid, ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine };
    private static JsonObject ResultState(BehaviorResult value) => new() { ["pid"] = value.ProcessId, ["role"] = value.Role, ["native_api"] = value.NativeApi, ["occurred_at_utc"] = Values.Utc(value.OccurredAtUtc), ["completed_at_utc"] = Values.Utc(value.CompletedAtUtc), ["bytes_written"] = value.BytesWritten, ["bytes_read"] = value.BytesRead, ["nonce_verified"] = value.NonceVerified };
    private static void Stop(Process? process, ICollection<string> errors) { if (process is null) return; try { if (!process.HasExited) { process.Kill(entireProcessTree: true); if (!process.WaitForExit(5_000)) errors.Add($"PID {process.Id} 未退出。"); } } catch (InvalidOperationException) { } catch (Exception exception) { errors.Add(exception.Message); } }
    private static bool IsAlive(Process value) { try { return !value.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process value) { try { return value.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static void WriteStatus(string capabilityId, string operation, string status) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = capabilityId, ["operation"] = operation, ["methods"] = 1 }.ToJsonString(JsonDefaults.Options));
}
