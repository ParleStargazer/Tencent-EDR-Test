using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace NetworkActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win.network.tcp"] = "tcp_connect",
        ["win.network.udp"] = "udp_connect",
        ["win.network.url"] = "url_access",
        ["win.network.dns"] = "dns_query",
        ["win.network.file_download"] = "file_download",
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
                throw new InvalidDataException($"NetworkActivity Controller 不支持能力：{package.Manifest.CapabilityId}");

            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            state = Execute(invocation, package, operation, parameters);
            var actor = ObserveProgram(invocation, state.Actor, state.ActorPath, state.ActorArguments, "actor", state.Result.OccurredAtUtc);
            var helper = ObserveProgram(invocation, state.Helper, state.HelperPath, state.HelperArguments, "helper", state.HelperResult.ObservedAtUtc);
            database.AddProgram(actor);
            database.AddProgram(helper);

            var verified = Verify(state, invocation.Nonce);
            var localSucceeded = state.Result.Succeeded && state.HelperResult.Succeeded && verified;
            var artifacts = new[]
            {
                CreateArtifact(invocation, state.ActorResultPath, operation, "actor_protocol"),
                CreateArtifact(invocation, state.HelperResultPath, operation, "helper_protocol"),
            };
            foreach (var artifact in artifacts) database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, operation, stopwatch, state, actor, artifacts.Select(value => value.ArtifactId).ToList());
            database.AddEvent(localEvent);
            AddFacts(database, invocation, operation, state, localEvent.LocalEventId, actor, helper, localSucceeded);

            var cleanup = Cleanup(invocation, state);
            database.AddCleanup(cleanup);
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "NETWORK_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, cleanup.ErrorMessage);
                return 30;
            }

            var status = localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = localSucceeded ? null : state.Result.Error ?? state.HelperResult.Error ?? "Controller 独立校验未通过。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                localSucceeded ? null : "NETWORK_BEHAVIOR_FAILED", error);
            WriteStatus(status, package.Manifest.CapabilityId, operation, error);
            return localSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanup = state is null ? EmptyCleanup(invocation) : Cleanup(invocation, state);
                    database.AddCleanup(cleanup);
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "NETWORK_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception databaseException)
                {
                    Console.Error.WriteLine(databaseException);
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
        ControllerInvocation invocation, CapabilityPackage package, string operation, JsonObject parameters)
    {
        Directory.CreateDirectory(invocation.WorkDir);
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var helperDefinition = package.Manifest.Participants.Single(value => value.Role == "helper");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var helperPath = package.ResolveProgram(helperDefinition.Executable);
        var helperReadyPath = Path.Combine(invocation.WorkDir, "network-helper-ready.json");
        var helperResultPath = Path.Combine(invocation.WorkDir, "network-helper-result.json");
        var actorResultPath = Path.Combine(invocation.WorkDir, "network-actor-result.json");
        var payloadSize = parameters["payload_size"]?.GetValue<int>() ?? 8_192;
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        var setupDelayMs = parameters["setup_delay_ms"]?.GetValue<int>() ?? 250;
        var helperArguments = new List<string>
        {
            "--role", "helper", "--operation", operation, "--ready", helperReadyPath,
            "--result", helperResultPath, "--nonce", invocation.Nonce,
            "--payload-size", payloadSize.ToString(),
        };
        var helper = Start(helperPath, helperArguments, invocation.WorkDir);
        try
        {
            var ready = WaitAndRead<HelperReady>(helperReadyPath, invocation.TimeoutMs, helper, "Helper 就绪");
            if (setupDelayMs > 0) Thread.Sleep(setupDelayMs);
            var actorArguments = new List<string>
            {
                "--role", "actor", "--operation", operation, "--result", actorResultPath,
                "--nonce", invocation.Nonce, "--address", ready.Endpoint.Address,
                "--port", ready.Endpoint.Port.ToString(), "--hold-ms", holdMs.ToString(),
            };
            if (ready.Url is not null) actorArguments.AddRange(["--url", ready.Url]);
            if (ready.DnsQuestion is not null) actorArguments.AddRange(["--question", ready.DnsQuestion]);
            string? destination = null;
            if (operation == "file_download")
            {
                var tag = new string(invocation.Nonce.Where(char.IsLetterOrDigit).Take(16).ToArray()).ToLowerInvariant();
                destination = Path.Combine(invocation.WorkDir, $"edrtest_{tag}_network_download.bin");
                if (File.Exists(destination)) throw new IOException($"受控下载目标已存在，拒绝覆盖：{destination}");
                actorArguments.AddRange(["--destination", destination]);
            }
            var actor = Start(actorPath, actorArguments, invocation.WorkDir);
            try
            {
                var result = WaitAndRead<BehaviorResult>(actorResultPath, invocation.TimeoutMs, actor, "Actor 结果");
                var helperResult = WaitAndRead<HelperResult>(helperResultPath, invocation.TimeoutMs, helper, "Helper 结果");
                WaitForExit(actor, invocation.TimeoutMs, "Actor");
                WaitForExit(helper, invocation.TimeoutMs, "Helper");
                return new ExecutionState(actorPath, actorArguments, actor, actorResultPath, helperPath,
                    helperArguments, helper, helperReadyPath, helperResultPath, destination, ready, result, helperResult);
            }
            catch
            {
                actor.Dispose();
                throw;
            }
        }
        catch
        {
            Stop(helper, []);
            helper.Dispose();
            throw;
        }
    }

    private static bool Verify(ExecutionState state, string nonce)
    {
        var result = state.Result;
        var helper = state.HelperResult;
        var endpointsAgree = result.Remote.Address == helper.Local.Address && result.Remote.Port == helper.Local.Port
            && helper.Remote is not null && result.Local.Address == helper.Remote.Address && result.Local.Port == helper.Remote.Port;
        var nonceAgrees = helper.ReceivedNonce == nonce && result.RequestNonce == nonce;
        var operationSpecific = result.Operation switch
        {
            "tcp_connect" or "udp_connect" => result.BytesSent > 0 && result.BytesReceived > 0,
            "url_access" => result.Url is not null && result.Url == state.Ready.Url && result.Method == "GET" && result.StatusCode == 200
                && helper.RequestedPath == new Uri(result.Url).PathAndQuery,
            "dns_query" => result.DnsQuestion == state.Ready.DnsQuestion && result.DnsQueryType == "A"
                && result.DnsAnswers?.Contains("192.0.2.123", StringComparer.Ordinal) == true,
            "file_download" => result.Url is not null && result.Url == state.Ready.Url && result.StatusCode == 200 && result.FileOccurredAtUtc is not null
                && state.Destination is not null && result.DestinationPath == state.Destination && File.Exists(state.Destination)
                && new FileInfo(state.Destination).Length == result.DownloadSizeBytes
                && Hashing.FileMd5(state.Destination) == result.DownloadMd5
                && Hashing.FileSha256(state.Destination) == result.DownloadSha256,
            _ => false,
        };
        return endpointsAgree && nonceAgrees && operationSpecific;
    }

    private static LocalEventObservation CreateEvent(
        ControllerInvocation invocation, string operation, Stopwatch stopwatch, ExecutionState state,
        ProgramObservation actor, List<string> evidenceRefs)
    {
        var result = state.Result;
        var data = new JsonObject
        {
            ["kind"] = "network",
            ["operation"] = operation,
            ["actor"] = ProcessReference(actor),
            ["connection"] = new JsonObject
            {
                ["transport"] = operation is "udp_connect" or "dns_query" ? "udp" : "tcp",
                ["direction"] = "outbound",
                ["local"] = Endpoint(result.Local),
                ["remote"] = Endpoint(result.Remote),
            },
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = result.Succeeded,
                ["win32_error"] = null,
                ["message"] = result.Error,
            },
        };
        if (operation == "dns_query")
        {
            data["dns"] = new JsonObject
            {
                ["question"] = result.DnsQuestion,
                ["query_type"] = result.DnsQueryType,
                ["answers"] = new JsonArray((result.DnsAnswers ?? []).Select(value => JsonValue.Create(value)).ToArray()),
                ["resolver"] = $"{result.Remote.Address}:{result.Remote.Port}",
            };
        }
        if (operation is "url_access" or "file_download")
        {
            var uri = new Uri(result.Url!);
            data["http"] = new JsonObject
            {
                ["url"] = result.Url,
                ["scheme"] = uri.Scheme,
                ["host"] = uri.Host,
                ["port"] = uri.Port,
                ["path"] = uri.PathAndQuery,
                ["method"] = result.Method,
                ["status_code"] = result.StatusCode,
                ["request_nonce"] = result.RequestNonce,
            };
        }
        if (operation == "file_download")
        {
            data["download"] = new JsonObject
            {
                ["destination_path"] = result.DestinationPath,
                ["size_bytes"] = result.DownloadSizeBytes,
                ["hashes"] = new JsonObject { ["md5"] = result.DownloadMd5, ["sha256"] = result.DownloadSha256 },
            };
        }
        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            EventType = "network",
            EventAction = operation,
            Nonce = invocation.Nonce,
            OccurredAtUtc = result.OccurredAtUtc,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "network_activity_controller",
            CollectionMethod = "actor_helper_protocol_and_endpoint_cross_check",
            Confidence = "high",
            ActorProgramId = actor.ProgramInstanceId,
            Data = data,
            EvidenceRefs = evidenceRefs,
        };
    }

    private static void AddFacts(
        RunDatabase database, ControllerInvocation invocation, string operation, ExecutionState state,
        string eventId, ProgramObservation actor, ProgramObservation helper, bool succeeded)
    {
        var result = state.Result;
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"network.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["network.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            ["network.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            ["network.actor_pid"] = JsonValue.Create(actor.Pid),
            ["network.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            ["network.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            ["network.helper_pid"] = JsonValue.Create(helper.Pid),
            ["network.helper_executable"] = JsonValue.Create(helper.ExecutablePath),
            ["network.transport"] = JsonValue.Create(operation is "udp_connect" or "dns_query" ? "udp" : "tcp"),
            ["network.direction"] = JsonValue.Create("outbound"),
            ["network.local.address"] = JsonValue.Create(result.Local.Address),
            ["network.local.port"] = JsonValue.Create(result.Local.Port),
            ["network.remote.address"] = JsonValue.Create(result.Remote.Address),
            ["network.remote.port"] = JsonValue.Create(result.Remote.Port),
            ["network.bytes_sent"] = JsonValue.Create(result.BytesSent),
            ["network.bytes_received"] = JsonValue.Create(result.BytesReceived),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        if (operation == "dns_query")
        {
            values["network.dns.question"] = JsonValue.Create(result.DnsQuestion);
            values["network.dns.query_type"] = JsonValue.Create(result.DnsQueryType);
            values["network.dns.answers"] = new JsonArray((result.DnsAnswers ?? []).Select(value => JsonValue.Create(value)).ToArray());
        }
        if (operation is "url_access" or "file_download")
        {
            var uri = new Uri(result.Url!);
            values["network.http.url"] = JsonValue.Create(result.Url);
            values["network.http.host"] = JsonValue.Create(uri.Host);
            values["network.http.path"] = JsonValue.Create(uri.PathAndQuery);
            values["network.http.method"] = JsonValue.Create(result.Method);
            values["network.http.status_code"] = JsonValue.Create(result.StatusCode);
        }
        if (operation == "file_download")
        {
            values["network.download.file_occurred_at_utc"] = JsonValue.Create(Values.Utc(result.FileOccurredAtUtc!.Value));
            values["network.download.destination_path"] = JsonValue.Create(result.DestinationPath);
            values["network.download.size_bytes"] = JsonValue.Create(result.DownloadSizeBytes);
            values["network.download.md5"] = JsonValue.Create(result.DownloadMd5);
            values["network.download.sha256"] = JsonValue.Create(result.DownloadSha256);
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
                Source = "network_activity_controller",
                Confidence = "high",
            });
        }
    }

    private static ProgramObservation ObserveProgram(
        ControllerInvocation invocation, Process process, string path, IReadOnlyList<string> arguments,
        string role, DateTimeOffset fallbackTime)
    {
        DateTimeOffset started;
        DateTimeOffset? ended;
        int? exitCode;
        try { started = process.StartTime.ToUniversalTime(); } catch (InvalidOperationException) { started = fallbackTime; }
        try { ended = process.HasExited ? process.ExitTime.ToUniversalTime() : null; exitCode = process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { ended = null; exitCode = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = role,
            ExecutablePath = path,
            Sha256 = Hashing.FileSha256(path),
            Sha1 = Hashing.FileSha1(path),
            Md5 = Hashing.FileMd5(path),
            Pid = process.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(process),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x86" => "x86", "arm64" => "arm64", _ => "x64",
            },
            CommandLine = FormatCommandLine(path, arguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = started,
            EndedAtUtc = ended,
            ExitCode = exitCode,
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject { ["captured_by"] = "NetworkActivity.Controller", ["loopback_only"] = true },
        };
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, string path, string operation, string kind)
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
            Metadata = new JsonObject { ["operation"] = operation },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var before = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor), ["helper_alive"] = IsAlive(state.Helper),
            ["download_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        Stop(state.Actor, errors);
        Stop(state.Helper, errors);
        if (state.Destination is not null) DeleteExact(state.Destination, invocation.WorkDir, errors);
        var after = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor), ["helper_alive"] = IsAlive(state.Helper),
            ["download_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        var succeeded = errors.Count == 0 && !IsAlive(state.Actor) && !IsAlive(state.Helper)
            && (state.Destination is null || !File.Exists(state.Destination));
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Action = "stop_network_actor_helper_and_remove_download",
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
        Action = "no_network_process_started",
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
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

    private static JsonObject Endpoint(EndpointInfo endpoint) => new()
    {
        ["address"] = endpoint.Address, ["port"] = endpoint.Port, ["family"] = endpoint.Family,
    };

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"启动网络行为程序失败：{executable}");
    }

    private static T WaitAndRead<T>(string path, int timeoutMs, Process process, string stage) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"{stage}前进程已退出，退出码 {process.ExitCode}。");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待{stage}超时：{path}");
            Thread.Sleep(10);
        }
        return ProtocolJson.Read<T>(path);
    }

    private static void WaitForExit(Process process, int timeoutMs, string role)
    {
        if (process.WaitForExit(timeoutMs)) return;
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"等待网络 {role} 退出超时：PID {process.Id}");
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
        catch (Exception exception) { errors.Add($"删除受控下载文件失败：{exception.Message}"); }
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
        try { return !process.HasExited; } catch (InvalidOperationException) { return false; }
    }

    private static int? TrySessionId(Process process)
    {
        try { return process.SessionId; } catch (InvalidOperationException) { return null; }
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
        public ExecutionState(string actorPath, IReadOnlyList<string> actorArguments, Process actor, string actorResultPath,
            string helperPath, IReadOnlyList<string> helperArguments, Process helper, string helperReadyPath,
            string helperResultPath, string? destination, HelperReady ready, BehaviorResult result, HelperResult helperResult)
        {
            ActorPath = actorPath; ActorArguments = actorArguments; Actor = actor; ActorResultPath = actorResultPath;
            HelperPath = helperPath; HelperArguments = helperArguments; Helper = helper; HelperReadyPath = helperReadyPath;
            HelperResultPath = helperResultPath; Destination = destination; Ready = ready; Result = result; HelperResult = helperResult;
        }
        public string ActorPath { get; }
        public IReadOnlyList<string> ActorArguments { get; }
        public Process Actor { get; }
        public string ActorResultPath { get; }
        public string HelperPath { get; }
        public IReadOnlyList<string> HelperArguments { get; }
        public Process Helper { get; }
        public string HelperReadyPath { get; }
        public string HelperResultPath { get; }
        public string? Destination { get; }
        public HelperReady Ready { get; }
        public BehaviorResult Result { get; }
        public HelperResult HelperResult { get; }
        public void Dispose() { Actor.Dispose(); Helper.Dispose(); }
    }
}
