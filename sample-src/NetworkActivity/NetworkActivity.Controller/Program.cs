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

    private static IReadOnlyList<SubtestDefinition> Subtests(string operation) => operation switch
    {
        "url_access" =>
        [
            new("raw_socket", "原始套接字 HTTP", true),
            new("wininet", "WinINet InternetOpenUrlW", true),
        ],
        "dns_query" =>
        [
            new("raw_udp", "原始 UDP DNS", true),
            new("windows_dns_client", "Windows DNS Client / svchost", false),
        ],
        "file_download" => [new("raw_http_three_part", "网络连接 + 同进程关联 + 文件写入三部分验证", true)],
        "udp_connect" => [new("datagram", "UDP 数据报", true)],
        _ => [new("socket", "TCP 套接字", true)],
    };

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        var states = new List<ExecutionState>();
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
            var localSucceeded = true;
            string? firstError = null;
            var subtests = Subtests(operation);
            foreach (var (subtest, instanceIndex) in subtests.Select((value, index) => (value, index)))
            {
                var state = Execute(invocation, package, operation, subtest, instanceIndex, parameters);
                states.Add(state);
                var actor = ObserveProgram(invocation, state.Actor, state.ActorPath, state.ActorArguments,
                    "actor", state.Result.OccurredAtUtc, instanceIndex, subtest.Id);
                var supportingProgram = state.Helper is not null
                    ? ObserveProgram(invocation, state.Helper, state.HelperPath!, state.HelperArguments,
                        "helper", state.HelperResult!.ObservedAtUtc, instanceIndex, subtest.Id)
                    : ObserveDnsClientService(invocation, state, instanceIndex);
                database.AddProgram(actor);
                database.AddProgram(supportingProgram);

                var verified = Verify(state);
                var subtestSucceeded = state.Result.Succeeded
                    && (state.HelperResult?.Succeeded ?? state.DnsClientServicePid is > 0)
                    && verified;
                localSucceeded &= subtestSucceeded;
                firstError ??= subtestSucceeded ? null : state.Result.Error ?? state.HelperResult?.Error
                    ?? $"{subtest.Title}子测试的独立观察未通过。";

                var artifacts = new List<ArtifactObservation>
                {
                    CreateArtifact(invocation, state.ActorResultPath, operation, "actor_protocol", subtest.Id),
                };
                if (state.HelperResultPath is not null)
                    artifacts.Add(CreateArtifact(invocation, state.HelperResultPath, operation, "helper_protocol", subtest.Id));
                foreach (var artifact in artifacts) database.AddArtifact(artifact);
                var evidenceRefs = artifacts.Select(value => value.ArtifactId).ToList();

                if (operation == "file_download")
                {
                    var connectionEvent = CreateEvent(invocation, operation, stopwatch, state, actor, evidenceRefs);
                    var fileEvent = CreateDownloadFileEvent(invocation, stopwatch, state, actor, evidenceRefs);
                    database.AddEvent(connectionEvent);
                    database.AddEvent(fileEvent);
                    AddFacts(database, invocation, operation, state, connectionEvent.LocalEventId,
                        actor, supportingProgram, subtestSucceeded, fileEvent.LocalEventId);
                }
                else
                {
                    var localEvent = CreateEvent(invocation, operation, stopwatch, state, actor, evidenceRefs);
                    database.AddEvent(localEvent);
                    AddFacts(database, invocation, operation, state, localEvent.LocalEventId,
                        actor, supportingProgram, subtestSucceeded, null);
                }
                SubtestTiming.WaitBetween(invocation, instanceIndex, subtests.Count, subtest.Title,
                    instanceIndex + 1 < subtests.Count ? subtests[instanceIndex + 1].Title : null);
            }

            AddCapabilityFacts(database, invocation, operation, states, localSucceeded);

            var cleanups = states.Select(state => Cleanup(invocation, state)).ToArray();
            foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
            var failedCleanup = cleanups.FirstOrDefault(value => value.Status != "succeeded");
            if (failedCleanup is not null)
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "NETWORK_CLEANUP_FAILED", failedCleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, operation, failedCleanup.ErrorMessage);
                return 30;
            }

            var status = localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = localSucceeded ? null : firstError ?? "Controller 独立校验未通过。";
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
                    var cleanups = states.Count == 0
                        ? [EmptyCleanup(invocation)]
                        : states.Select(state => Cleanup(invocation, state)).ToArray();
                    foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
                    var cleanupSucceeded = cleanups.All(value => value.Status == "succeeded");
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanupSucceeded ? "SAMPLE_ERROR" : "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds, "NETWORK_CONTROLLER_ERROR", exception.Message);
                    return cleanupSucceeded ? 20 : 30;
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
            foreach (var state in states) state.Dispose();
            database?.Dispose();
        }
    }

    private static ExecutionState Execute(
        ControllerInvocation invocation, CapabilityPackage package, string operation,
        SubtestDefinition subtest, int instanceIndex, JsonObject parameters)
    {
        Directory.CreateDirectory(invocation.WorkDir);
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var suffix = subtest.Id.Replace('_', '-');
        var actorResultPath = Path.Combine(invocation.WorkDir, $"network-actor-result-{suffix}.json");
        var payloadSize = parameters["payload_size"]?.GetValue<int>() ?? 8_192;
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
        var setupDelayMs = parameters["setup_delay_ms"]?.GetValue<int>() ?? 250;
        var subtestNonce = $"{invocation.Nonce}-{suffix}";

        string? helperPath = null;
        string? helperReadyPath = null;
        string? helperResultPath = null;
        var helperArguments = new List<string>();
        Process? helper = null;
        HelperResult? helperResult = null;
        HelperReady ready;
        int? dnsClientServicePid = null;

        if (subtest.UseHelper)
        {
            var helperDefinition = package.Manifest.Participants.Single(value => value.Role == "helper");
            helperPath = package.ResolveProgram(helperDefinition.Executable);
            helperReadyPath = Path.Combine(invocation.WorkDir, $"network-helper-ready-{suffix}.json");
            helperResultPath = Path.Combine(invocation.WorkDir, $"network-helper-result-{suffix}.json");
            helperArguments.AddRange([
                "--role", "helper", "--operation", operation, "--ready", helperReadyPath,
                "--result", helperResultPath, "--nonce", subtestNonce,
                "--payload-size", payloadSize.ToString(),
            ]);
            helper = Start(helperPath, helperArguments, invocation.WorkDir);
            try
            {
                ready = WaitAndRead<HelperReady>(helperReadyPath, invocation.TimeoutMs, helper, "Helper 就绪");
            }
            catch
            {
                Stop(helper, []);
                helper.Dispose();
                throw;
            }
        }
        else
        {
            dnsClientServicePid = DnsClientServicePid();
            if (dnsClientServicePid is null or <= 0)
                throw new InvalidOperationException("Windows DNS Client（Dnscache）服务没有可用的 svchost PID。");
            var question = $"{SanitizeDnsLabel(subtestNonce)}.dns.msftncsi.com";
            ready = new HelperReady
            {
                Operation = operation,
                Endpoint = new EndpointInfo { Address = "0.0.0.0", Port = 53, Family = "ipv4" },
                DnsQuestion = question,
            };
        }

        try
        {
            if (setupDelayMs > 0) Thread.Sleep(setupDelayMs);
            var actorArguments = new List<string>
            {
                "--role", "actor", "--operation", operation, "--method", subtest.Id,
                "--result", actorResultPath, "--nonce", subtestNonce,
                "--address", ready.Endpoint.Address, "--port", ready.Endpoint.Port.ToString(),
                "--hold-ms", holdMs.ToString(),
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
                if (helper is not null && helperResultPath is not null)
                    helperResult = WaitAndRead<HelperResult>(helperResultPath, invocation.TimeoutMs, helper, "Helper 结果");
                WaitForExit(actor, invocation.TimeoutMs, "Actor");
                if (helper is not null) WaitForExit(helper, invocation.TimeoutMs, "Helper");
                return new ExecutionState(subtest, instanceIndex, subtestNonce, actorPath, actorArguments,
                    actor, actorResultPath, helperPath, helperArguments, helper, helperReadyPath,
                    helperResultPath, destination, ready, result, helperResult, dnsClientServicePid);
            }
            catch
            {
                actor.Dispose();
                throw;
            }
        }
        catch
        {
            if (helper is not null)
            {
                Stop(helper, []);
                helper.Dispose();
            }
            throw;
        }
    }

    private static bool Verify(ExecutionState state)
    {
        var result = state.Result;
        var helper = state.HelperResult;
        var endpointsAgree = helper is null
            ? result.Remote.Port == 53 && state.DnsClientServicePid is > 0
            : result.Remote.Address == helper.Local.Address && result.Remote.Port == helper.Local.Port
                && helper.Remote is not null
                && (state.Subtest.Id == "wininet"
                    || result.Local.Address == helper.Remote.Address && result.Local.Port == helper.Remote.Port);
        var nonceAgrees = (helper?.ReceivedNonce ?? state.SubtestNonce) == state.SubtestNonce
            && result.RequestNonce == state.SubtestNonce;
        var operationSpecific = result.Operation switch
        {
            "tcp_connect" or "udp_connect" => result.BytesSent > 0 && result.BytesReceived > 0,
            "url_access" => result.Url is not null && result.Url == state.Ready.Url && result.Method == "GET" && result.StatusCode == 200
                && helper?.RequestedPath == new Uri(result.Url).PathAndQuery
                && (state.Subtest.Id != "wininet" || result.NativeApi == "InternetOpenUrlW"),
            "dns_query" when state.Subtest.Id == "windows_dns_client" =>
                result.DnsQuestion == state.Ready.DnsQuestion && result.DnsQueryType == "A"
                && result.NativeApi == "DnsQuery_W" && result.NativeStatusCode is 0 or 9003,
            "dns_query" => result.DnsQuestion == state.Ready.DnsQuestion && result.DnsQueryType == "A"
                && result.DnsAnswers?.Contains("192.0.2.123", StringComparer.Ordinal) == true,
            "file_download" => result.Url is not null && result.Url == state.Ready.Url && result.StatusCode == 200
                && result.FileOccurredAtUtc is not null && result.FileCompletedAtUtc is not null
                && result.OccurredAtUtc <= result.FileOccurredAtUtc && result.FileOccurredAtUtc <= result.FileCompletedAtUtc
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
        var eventAction = operation;
        var effectiveLocal = state.HelperResult?.Remote ?? result.Local;
        var effectiveRemote = state.HelperResult?.Local ?? result.Remote;
        var data = new JsonObject
        {
            ["kind"] = "network",
            ["operation"] = eventAction,
            ["subtest"] = state.Subtest.Id,
            ["method"] = state.Subtest.Title,
            ["actor"] = ProcessReference(actor),
            ["connection"] = new JsonObject
            {
                ["transport"] = operation is "udp_connect" or "dns_query" ? "udp" : "tcp",
                ["direction"] = "outbound",
                ["local"] = Endpoint(effectiveLocal),
                ["remote"] = Endpoint(effectiveRemote),
            },
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = result.Succeeded,
                ["win32_error"] = null,
                ["message"] = result.Error,
            },
        };
        if (state.DnsClientServicePid is > 0)
        {
            data["dns_client_service"] = new JsonObject
            {
                ["service_name"] = "Dnscache",
                ["pid"] = state.DnsClientServicePid,
                ["executable"] = Path.Combine(Environment.SystemDirectory, "svchost.exe"),
                ["command_line_hint"] = "svchost.exe -k NetworkService -p -s Dnscache",
            };
        }
        if (operation == "dns_query")
        {
            data["dns"] = new JsonObject
            {
                ["question"] = result.DnsQuestion,
                ["query_type"] = result.DnsQueryType,
                ["answers"] = new JsonArray((result.DnsAnswers ?? []).Select(value => JsonValue.Create(value)).ToArray()),
                ["resolver"] = state.Subtest.Id == "windows_dns_client"
                    ? "Windows DNS Client (Dnscache)"
                    : $"{effectiveRemote.Address}:{effectiveRemote.Port}",
                ["native_api"] = result.NativeApi,
                ["native_status_code"] = result.NativeStatusCode,
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
            data["stage"] = new JsonObject { ["sequence"] = 1, ["title"] = "第一部分：网络连接" };
            data["download"] = DownloadData(result);
        }
        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = state.InstanceIndex + 1,
            EventType = "network",
            EventAction = eventAction,
            Nonce = invocation.Nonce,
            OccurredAtUtc = result.OccurredAtUtc,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "network_activity_controller",
            CollectionMethod = state.Subtest.Id switch
            {
                "wininet" => "wininet_http_and_helper_endpoint_cross_check",
                "windows_dns_client" => "dnsapi_and_dnscache_service_cross_check",
                _ => "actor_helper_protocol_and_endpoint_cross_check",
            },
            Confidence = "high",
            ActorProgramId = actor.ProgramInstanceId,
            Data = data,
            EvidenceRefs = evidenceRefs,
        };
    }

    private static LocalEventObservation CreateDownloadFileEvent(
        ControllerInvocation invocation, Stopwatch stopwatch, ExecutionState state,
        ProgramObservation actor, List<string> evidenceRefs)
    {
        var result = state.Result;
        var effectiveLocal = state.HelperResult?.Remote ?? result.Local;
        var effectiveRemote = state.HelperResult?.Local ?? result.Remote;
        var uri = new Uri(result.Url!);
        return new LocalEventObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = 3,
            EventType = "network",
            EventAction = "file_download",
            Nonce = invocation.Nonce,
            OccurredAtUtc = result.FileOccurredAtUtc!.Value,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
            Source = "network_activity_controller",
            CollectionMethod = "before_after_download_file_hash_and_size_cross_check",
            Confidence = "high",
            ActorProgramId = actor.ProgramInstanceId,
            Data = new JsonObject
            {
                ["kind"] = "network",
                ["operation"] = "file_download",
                ["subtest"] = state.Subtest.Id,
                ["method"] = state.Subtest.Title,
                ["stage"] = new JsonObject { ["sequence"] = 3, ["title"] = "第三部分：文件写入验证" },
                ["actor"] = ProcessReference(actor),
                ["connection"] = new JsonObject
                {
                    ["transport"] = "tcp",
                    ["direction"] = "outbound",
                    ["local"] = Endpoint(effectiveLocal),
                    ["remote"] = Endpoint(effectiveRemote),
                },
                ["http"] = new JsonObject
                {
                    ["url"] = result.Url,
                    ["scheme"] = uri.Scheme,
                    ["host"] = uri.Host,
                    ["port"] = uri.Port,
                    ["path"] = uri.PathAndQuery,
                    ["method"] = result.Method,
                    ["status_code"] = result.StatusCode,
                    ["request_nonce"] = result.RequestNonce,
                },
                ["download"] = DownloadData(result),
                ["result"] = new JsonObject
                {
                    ["attempted"] = true,
                    ["succeeded"] = result.Succeeded,
                    ["win32_error"] = null,
                    ["message"] = result.Error,
                },
            },
            EvidenceRefs = evidenceRefs,
        };
    }

    private static JsonObject DownloadData(BehaviorResult result) => new()
    {
        ["destination_path"] = result.DestinationPath,
        ["size_bytes"] = result.DownloadSizeBytes,
        ["hashes"] = new JsonObject
        {
            ["md5"] = result.DownloadMd5,
            ["sha256"] = result.DownloadSha256,
        },
        ["write_started_at_utc"] = Values.Utc(result.FileOccurredAtUtc!.Value),
        ["write_completed_at_utc"] = Values.Utc(result.FileCompletedAtUtc!.Value),
    };

    private static void AddFacts(
        RunDatabase database, ControllerInvocation invocation, string operation, ExecutionState state,
        string eventId, ProgramObservation actor, ProgramObservation supportingProgram, bool succeeded,
        string? downloadFileEventId)
    {
        var result = state.Result;
        var effectiveLocal = state.HelperResult?.Remote ?? result.Local;
        var effectiveRemote = state.HelperResult?.Local ?? result.Remote;
        var prefix = operation switch
        {
            "url_access" => $"network.url.{state.Subtest.Id}",
            "dns_query" => $"network.dns.{state.Subtest.Id}",
            "file_download" => "network.download.connection",
            _ => "network",
        };
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"{prefix}.succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.OccurredAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(result.CompletedAtUtc)),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid),
            [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.actor_command_line"] = JsonValue.Create(actor.CommandLine),
            [$"{prefix}.supporting_pid"] = JsonValue.Create(supportingProgram.Pid),
            [$"{prefix}.supporting_executable"] = JsonValue.Create(supportingProgram.ExecutablePath),
            [$"{prefix}.transport"] = JsonValue.Create(operation is "udp_connect" or "dns_query" ? "udp" : "tcp"),
            [$"{prefix}.direction"] = JsonValue.Create("outbound"),
            [$"{prefix}.local.address"] = JsonValue.Create(effectiveLocal.Address),
            [$"{prefix}.local.port"] = JsonValue.Create(effectiveLocal.Port),
            [$"{prefix}.remote.address"] = JsonValue.Create(effectiveRemote.Address),
            [$"{prefix}.remote.port"] = JsonValue.Create(effectiveRemote.Port),
            [$"{prefix}.bytes_sent"] = JsonValue.Create(result.BytesSent),
            [$"{prefix}.bytes_received"] = JsonValue.Create(result.BytesReceived),
            [$"{prefix}.method_id"] = JsonValue.Create(state.Subtest.Id),
            [$"{prefix}.request_nonce"] = JsonValue.Create(state.SubtestNonce),
        };
        if (prefix == "network")
        {
            values["network.helper_pid"] = JsonValue.Create(supportingProgram.Pid);
            values["network.helper_executable"] = JsonValue.Create(supportingProgram.ExecutablePath);
        }
        if (operation == "dns_query")
        {
            values[$"{prefix}.question"] = JsonValue.Create(result.DnsQuestion);
            values[$"{prefix}.query_type"] = JsonValue.Create(result.DnsQueryType);
            values[$"{prefix}.answers"] = new JsonArray((result.DnsAnswers ?? []).Select(value => JsonValue.Create(value)).ToArray());
            values[$"{prefix}.native_api"] = JsonValue.Create(result.NativeApi);
            values[$"{prefix}.native_status_code"] = JsonValue.Create(result.NativeStatusCode);
            if (state.DnsClientServicePid is > 0)
            {
                values[$"{prefix}.service_name"] = JsonValue.Create("Dnscache");
                values[$"{prefix}.service_pid"] = JsonValue.Create(state.DnsClientServicePid);
                values[$"{prefix}.service_executable"] = JsonValue.Create(Path.Combine(Environment.SystemDirectory, "svchost.exe"));
            }
        }
        if (operation is "url_access" or "file_download")
        {
            var uri = new Uri(result.Url!);
            values[$"{prefix}.http.url"] = JsonValue.Create(result.Url);
            values[$"{prefix}.http.host"] = JsonValue.Create(uri.Host);
            values[$"{prefix}.http.path"] = JsonValue.Create(uri.PathAndQuery);
            values[$"{prefix}.http.method"] = JsonValue.Create(result.Method);
            values[$"{prefix}.http.status_code"] = JsonValue.Create(result.StatusCode);
            values[$"{prefix}.native_api"] = JsonValue.Create(result.NativeApi);
        }
        if (operation == "file_download")
        {
            values["network.download.file.succeeded"] = JsonValue.Create(succeeded);
            values["network.download.file.occurred_at_utc"] = JsonValue.Create(Values.Utc(result.FileOccurredAtUtc!.Value));
            values["network.download.file.completed_at_utc"] = JsonValue.Create(Values.Utc(result.FileCompletedAtUtc!.Value));
            values["network.download.file.actor_pid"] = JsonValue.Create(actor.Pid);
            values["network.download.file.actor_executable"] = JsonValue.Create(actor.ExecutablePath);
            values["network.download.file.destination_path"] = JsonValue.Create(result.DestinationPath);
            values["network.download.file.size_bytes"] = JsonValue.Create(result.DownloadSizeBytes);
            values["network.download.file.md5"] = JsonValue.Create(result.DownloadMd5);
            values["network.download.file.sha256"] = JsonValue.Create(result.DownloadSha256);
            values["network.download.stage_order_succeeded"] = JsonValue.Create(
                result.OccurredAtUtc <= result.FileOccurredAtUtc && result.FileOccurredAtUtc <= result.FileCompletedAtUtc);
            values["network.download.association.succeeded"] = JsonValue.Create(
                result.OccurredAtUtc <= result.FileOccurredAtUtc
                && actor.Pid > 0
                && !string.IsNullOrWhiteSpace(actor.ExecutablePath));
            values["network.download.association.same_process_pid"] = JsonValue.Create(true);
            values["network.download.association.same_process_executable"] = JsonValue.Create(true);
            values["network.download.association.connection_event_id"] = JsonValue.Create(eventId);
            values["network.download.association.file_event_id"] = JsonValue.Create(downloadFileEventId);
            values["network.download.association.local_interval_ms"] = JsonValue.Create(
                (long)Math.Round((result.FileOccurredAtUtc!.Value - result.OccurredAtUtc).TotalMilliseconds));
        }
        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = operation == "file_download" && key.StartsWith("network.download.file.", StringComparison.Ordinal)
                    ? downloadFileEventId
                    : eventId,
                Key = key,
                Value = value,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "network_activity_controller",
                Confidence = "high",
            });
        }
    }

    private static void AddCapabilityFacts(
        RunDatabase database, ControllerInvocation invocation, string operation,
        IReadOnlyList<ExecutionState> states, bool succeeded)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [$"network.{operation}_succeeded"] = JsonValue.Create(succeeded),
            ["network.subtest_count"] = JsonValue.Create(states.Count),
            ["correlation.nonce"] = JsonValue.Create(invocation.Nonce),
        };
        foreach (var (key, value) in values)
        {
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
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
        string role, DateTimeOffset fallbackTime, int instanceIndex, string instanceName)
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
            InstanceIndex = instanceIndex,
            InstanceName = instanceName,
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
            Metadata = new JsonObject
            {
                ["captured_by"] = "NetworkActivity.Controller",
                ["loopback_only"] = true,
                ["subtest"] = instanceName,
            },
        };
    }

    private static ProgramObservation ObserveDnsClientService(
        ControllerInvocation invocation, ExecutionState state, int instanceIndex)
    {
        var servicePid = state.DnsClientServicePid
            ?? throw new InvalidOperationException("Windows DNS Client 子测试缺少服务 PID。");
        var path = Path.Combine(Environment.SystemDirectory, "svchost.exe");
        DateTimeOffset startedAt = state.Result.OccurredAtUtc;
        int sessionId = 0;
        try
        {
            using var process = Process.GetProcessById(servicePid);
            startedAt = process.StartTime.ToUniversalTime();
            sessionId = process.SessionId;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // PID 已由 SCM 在行为前确认；标准用户不一定能读取系统服务的启动属性。
        }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "helper",
            InstanceIndex = instanceIndex,
            InstanceName = state.Subtest.Id,
            ExecutablePath = path,
            Sha256 = Hashing.FileSha256(path),
            Sha1 = Hashing.FileSha1(path),
            Md5 = Hashing.FileMd5(path),
            Pid = servicePid,
            ParentPid = 0,
            SessionId = sessionId,
            Architecture = "x64",
            CommandLine = $"{path} -k NetworkService -p -s Dnscache",
            WorkingDirectory = Environment.SystemDirectory,
            StartedAtUtc = startedAt,
            EndedAtUtc = null,
            ExitCode = null,
            StartupAttempted = false,
            StartupSucceeded = true,
            Metadata = new JsonObject
            {
                ["captured_by"] = "NetworkActivity.Controller/SCM",
                ["external_system_service"] = true,
                ["service_name"] = "Dnscache",
                ["must_not_stop_during_cleanup"] = true,
            },
        };
    }

    private static ArtifactObservation CreateArtifact(
        ControllerInvocation invocation, string path, string operation, string kind, string subtest)
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
            Metadata = new JsonObject { ["operation"] = operation, ["subtest"] = subtest },
        };
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var before = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["owned_helper_alive"] = state.Helper is not null && IsAlive(state.Helper),
            ["external_dnscache_pid"] = state.DnsClientServicePid,
            ["download_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        Stop(state.Actor, errors);
        if (state.Helper is not null) Stop(state.Helper, errors);
        if (state.Destination is not null) DeleteExact(state.Destination, invocation.WorkDir, errors);
        var after = new JsonObject
        {
            ["actor_alive"] = IsAlive(state.Actor),
            ["owned_helper_alive"] = state.Helper is not null && IsAlive(state.Helper),
            ["external_dnscache_untouched"] = state.DnsClientServicePid is > 0,
            ["download_exists"] = state.Destination is not null && File.Exists(state.Destination),
        };
        var succeeded = errors.Count == 0 && !IsAlive(state.Actor)
            && (state.Helper is null || !IsAlive(state.Helper))
            && (state.Destination is null || !File.Exists(state.Destination));
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = state.InstanceIndex + 1,
            Action = state.Helper is null
                ? "stop_network_actor_preserve_external_dnscache"
                : "stop_network_actor_helper_and_remove_download",
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

    private static string SanitizeDnsLabel(string value)
    {
        var label = new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(40).ToArray());
        return string.IsNullOrWhiteSpace(label) ? "edrtest" : label;
    }

    private static int? DnsClientServicePid()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法打开服务控制管理器。");
        try
        {
            var service = OpenService(manager, "Dnscache", ServiceQueryStatus);
            if (service == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法查询 Dnscache 服务。");
            try
            {
                var status = new ServiceStatusProcess();
                var size = Marshal.SizeOf<ServiceStatusProcess>();
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, size, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Dnscache 服务 PID。");
                return status.CurrentState == ServiceRunning && status.ProcessId > 0
                    ? checked((int)status.ProcessId)
                    : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

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
        public ExecutionState(SubtestDefinition subtest, int instanceIndex, string subtestNonce,
            string actorPath, IReadOnlyList<string> actorArguments, Process actor, string actorResultPath,
            string? helperPath, IReadOnlyList<string> helperArguments, Process? helper, string? helperReadyPath,
            string? helperResultPath, string? destination, HelperReady ready, BehaviorResult result,
            HelperResult? helperResult, int? dnsClientServicePid)
        {
            Subtest = subtest; InstanceIndex = instanceIndex; SubtestNonce = subtestNonce;
            ActorPath = actorPath; ActorArguments = actorArguments; Actor = actor; ActorResultPath = actorResultPath;
            HelperPath = helperPath; HelperArguments = helperArguments; Helper = helper; HelperReadyPath = helperReadyPath;
            HelperResultPath = helperResultPath; Destination = destination; Ready = ready; Result = result;
            HelperResult = helperResult; DnsClientServicePid = dnsClientServicePid;
        }
        public SubtestDefinition Subtest { get; }
        public int InstanceIndex { get; }
        public string SubtestNonce { get; }
        public string ActorPath { get; }
        public IReadOnlyList<string> ActorArguments { get; }
        public Process Actor { get; }
        public string ActorResultPath { get; }
        public string? HelperPath { get; }
        public IReadOnlyList<string> HelperArguments { get; }
        public Process? Helper { get; }
        public string? HelperReadyPath { get; }
        public string? HelperResultPath { get; }
        public string? Destination { get; }
        public HelperReady Ready { get; }
        public BehaviorResult Result { get; }
        public HelperResult? HelperResult { get; }
        public int? DnsClientServicePid { get; }
        public void Dispose() { Actor.Dispose(); Helper?.Dispose(); }
    }

    private sealed record SubtestDefinition(string Id, string Title, bool UseHelper);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int infoLevel, ref ServiceStatusProcess buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
