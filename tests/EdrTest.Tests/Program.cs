using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdrTest;

namespace EdrTest.Tests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture-controller")
        {
            return RunFixtureController(args.Skip(1).ToArray());
        }
        if (args.FirstOrDefault() == "--fixture-hang")
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 0;
        }

        var failures = new List<string>();
        await RunTest("能力包路径和参数校验", TestManifestValidation, failures);
        await RunTest("L2/L3 默认风险门禁", TestHighRiskGate, failures);
        await RunTest("同一轮按顺序执行多个能力", TestMultipleCapabilities, failures);
        await RunTest("Controller 超时封存为 SAMPLE_ERROR", TestControllerTimeout, failures);
        await RunTest("取消轮次会终止进程树并封存 ABORTED", TestCancellation, failures);
        await RunTest("Runner → SQLite → Export → Compare 最小闭环", TestEndToEnd, failures);
        if (failures.Count == 0)
        {
            Console.WriteLine("全部框架测试通过。");
            return 0;
        }

        Console.Error.WriteLine($"失败 {failures.Count} 项：");
        failures.ForEach(Console.Error.WriteLine);
        return 1;
    }

    private static async Task RunTest(string name, Func<Task> test, ICollection<string> failures)
    {
        try
        {
            await test();
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"[FAIL] {name}: {exception}");
        }
    }

    private static Task TestManifestValidation()
    {
        using var fixture = TestDirectory.Create();
        var manifest = CreateManifest("..\\outside.exe", "L0");
        var path = Path.Combine(fixture.Path, "capability.json");
        File.WriteAllText(path, manifest.ToJsonString(JsonDefaults.Options));
        AssertThrows<InvalidDataException>(() => CapabilityCatalog.Load(path));
        return Task.CompletedTask;
    }

    private static async Task TestEndToEnd()
    {
        using var fixture = TestDirectory.Create();
        var packageDirectory = Path.Combine(fixture.Path, "package");
        CopyDirectory(AppContext.BaseDirectory, packageDirectory);
        var executableName = Path.GetFileName(Environment.ProcessPath) ?? "EdrTest.Tests.exe";
        var manifestPath = Path.Combine(packageDirectory, "capability.json");
        File.WriteAllText(manifestPath, CreateManifest(executableName, "L0").ToJsonString(JsonDefaults.Options));

        var progress = new List<RunProgressUpdate>();
        var result = await new RunnerService().RunAsync(new RunRequest(
            [manifestPath],
            Path.Combine(fixture.Path, "runs"),
            null,
            false,
            SuiteId: "framework-e2e",
            ProgressCallback: progress.Add));
        Assert(result.Status == "COMPLETED", "轮次应完成。");
        Assert(File.Exists(result.DatabasePath), "应生成 SQLite 数据库。");
        Assert(File.Exists(result.LocalExportPath), "应自动生成 local-run.json。");

        var local = JsonNode.Parse(File.ReadAllText(result.LocalExportPath))!.AsObject();
        Assert(local["schema_version"]?.GetValue<string>() == "1.1", "导出 Schema 应为 1.1。");
        Assert(local["capabilities"]?.AsArray().Count == 1, "应有一个能力结果。");
        Assert(local["programs"]?.AsArray().Count == 3, "应记录 Controller/Actor/Target。");
        var targetProgram = local["programs"]?.AsArray().Single(value => value?["role"]?.GetValue<string>() == "target");
        Assert(targetProgram?["md5"]?.GetValue<string>().Length == 32, "Actor/Target 程序应采集 MD5，供 EDR 哈希字段比较。");
        Assert(local["execution_logs"]?.AsArray().Count >= 1, "本地导出应保存运行日志，供历史轮次按能力查看。");
        Assert(local["local_events"]?.AsArray().Count == 1, "应记录一个进程创建事件。");
        Assert(local["cleanup_results"]?.AsArray().Count == 1, "应记录清理结果。");
        var completedProgress = progress.Single(value => value.Kind == "capability_completed");
        var progressEvidence = completedProgress.LocalEvidence ?? throw new InvalidOperationException("能力完成进度应携带本地证据。");
        Assert(progressEvidence["capability"]?["started_at_utc"]?.GetValue<string>() is { Length: > 0 }
            && progressEvidence["capability"]?["ended_at_utc"]?.GetValue<string>() is { Length: > 0 }, "已完成队列应取得能力开始与结束时间。");
        Assert(progressEvidence["programs"]?.AsArray().Any(value => value?["role"]?.GetValue<string>() == "actor" && value?["pid"]?.GetValue<int>() > 0) == true, "已完成队列应取得 Actor PID。");

        var cloudPath = Path.Combine(fixture.Path, "cloud.json");
        File.WriteAllText(cloudPath, CreateCloudExport(local).ToJsonString(JsonDefaults.Options));
        var repository = FindRepositoryRoot();
        var validationPath = Path.Combine(fixture.Path, "validation-result.json");
        var validation = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [cloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            validationPath));
        Assert(validation["summary"]?["pass"]?.GetValue<int>() == 1, $"合成云端事件应通过比较：{validation.ToJsonString(JsonDefaults.Options)}");
        Assert(validation["summary"]?["fail"]?.GetValue<int>() == 0, "不应出现 FAIL。");
        Assert(validation["schema_version"]?.GetValue<string>() == "1.1", "验证结果 Schema 应为 1.1。");
        Assert(validation["conclusion"]?["verdict"]?.GetValue<string>() == "PASS", "单项能力通过时总体结论应为 PASS。");
        Assert(validation["conclusion"]?["pass_rate"]?.GetValue<double>() == 1, "单项能力通过率应为 100%。");
        var requirements = validation["capabilities"]?[0]?["baseline_requirements"]?.AsArray() ?? throw new InvalidOperationException("结果应包含 BASELINE 要求。");
        Assert(requirements.Count == 13, "进程创建应展示 5 项本地要求、1 项事件数量要求和 7 项云端字段要求。");
        Assert(requirements.Where(value => value?["severity"]?.GetValue<string>() == "required").All(value => value?["status"]?.GetValue<string>() == "passed"), "所有必需 BASELINE 要求都应通过。");
        var firstCandidate = validation["capabilities"]?[0]?["edr_candidates"]?.AsArray().Single()
            ?? throw new InvalidOperationException("结果应包含完整 EDR 候选日志。");
        Assert(firstCandidate["rank"]?.GetValue<int>() == 1 && firstCandidate["raw_event"]?["@table"]?.GetValue<string>() == "ProcEvents", "EDR 候选应保留排名和原始完整日志。");
        var actorPidRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "programs.actor.pid");
        Assert(actorPidRequirement?["operator"]?.GetValue<string>() == "present" && actorPidRequirement["expected"] is null && actorPidRequirement["actual"]?.GetValue<int>() > 0, "PID 存在性规则应在 actual 中保留本地 PID，expected 为空表示没有固定 PID。");
        var actorCommandRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "programs.actor.command_line");
        var commandMarker = actorCommandRequirement?["expected"]?.GetValue<string>() ?? string.Empty;
        Assert(commandMarker.Length == 32 && actorCommandRequirement?["actual"]?.GetValue<string>().Contains(commandMarker, StringComparison.Ordinal) == true, "命令行 contains 规则的 expected 应是本轮测试标记，而不是文件哈希。");
        Assert(File.Exists(validationPath), "应写出 validation-result.json。");
        var conclusionPath = ConclusionExportService.DefaultOutputPath(validationPath);
        Assert(File.Exists(conclusionPath), "应同时写出中文 Markdown 结论。");
        Assert(File.ReadAllText(conclusionPath).Contains("全部能力满足验证基准", StringComparison.Ordinal), "Markdown 应包含中文总体结论。");

        var missingMd5LocalPath = Path.Combine(fixture.Path, "missing-md5-local.json");
        var missingMd5Local = local.DeepClone().AsObject();
        var missingMd5Target = missingMd5Local["programs"]?.AsArray().Single(value => value?["role"]?.GetValue<string>() == "target")
            ?? throw new InvalidOperationException("测试导出缺少 Target 程序。");
        missingMd5Target["md5"] = null;
        File.WriteAllText(missingMd5LocalPath, missingMd5Local.ToJsonString(JsonDefaults.Options));
        var missingMd5 = CompareService.Compare(new CompareRequest(
            missingMd5LocalPath,
            [cloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "missing-md5-result.json")));
        var md5Requirement = missingMd5["capabilities"]?[0]?["baseline_requirements"]?.AsArray().Single(value => value?["field"]?.GetValue<string>() == "process.hash.md5");
        Assert(md5Requirement?["status"]?.GetValue<string>() == "not_evaluated", "本地未采集 MD5 时应标记为未检查，而不是用空值判定失败。");
        Assert(missingMd5["capabilities"]?[0]?["validation_status"]?.GetValue<string>() == "PASS", "信息级 MD5 未检查不应降低必需字段全部通过的能力结论。");

        var multipleCloud = CreateCloudExport(local);
        var fartherCandidate = multipleCloud[0]!.DeepClone().AsObject();
        fartherCandidate["Common.EventUUId"] = Ids.NewUuid7();
        fartherCandidate["Common.EventTime"] = DateTimeOffset.Parse(local["capabilities"]![0]!["started_at_utc"]!.GetValue<string>()).AddMinutes(2).ToUnixTimeMilliseconds();
        multipleCloud.Add(fartherCandidate);
        var lowerConfidenceCandidate = multipleCloud[0]!.DeepClone().AsObject();
        lowerConfidenceCandidate["Common.EventUUId"] = Ids.NewUuid7();
        lowerConfidenceCandidate["Common.EventTime"] = DateTimeOffset.Parse(local["capabilities"]![0]!["started_at_utc"]!.GetValue<string>()).AddSeconds(1).ToUnixTimeMilliseconds();
        lowerConfidenceCandidate["Parent.FilePath"] = "C:\\different-parent.exe";
        multipleCloud.Add(lowerConfidenceCandidate);
        var multipleCloudPath = Path.Combine(fixture.Path, "multiple-cloud.json");
        File.WriteAllText(multipleCloudPath, multipleCloud.ToJsonString(JsonDefaults.Options));
        var multipleCandidates = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [multipleCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "multiple-candidates-result.json")));
        var rankedCandidates = multipleCandidates["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("多候选结果缺少 edr_candidates。");
        Assert(rankedCandidates.Count == 3, "应保留全部三条符合关联条件的 EDR 完整日志。");
        Assert(rankedCandidates[0]?["correlation_score"]?.GetValue<double>() == rankedCandidates[1]?["correlation_score"]?.GetValue<double>()
            && rankedCandidates[0]?["time_distance_ms"]?.GetValue<long>() < rankedCandidates[1]?["time_distance_ms"]?.GetValue<long>(), "同关联得分候选应按与本地行为时间的距离由近到远排序。");
        Assert(rankedCandidates[1]?["correlation_score"]?.GetValue<double>() > rankedCandidates[2]?["correlation_score"]?.GetValue<double>()
            && rankedCandidates[2]?["time_distance_ms"]?.GetValue<long>() < rankedCandidates[1]?["time_distance_ms"]?.GetValue<long>(), "关联得分应优先于时间距离决定候选置信度顺序。");
        Assert(multipleCandidates["capabilities"]?[0]?["validation_status"]?.GetValue<string>() == "PASS", "时间距离可以消除同分候选歧义。");

        var emptyCloudPath = Path.Combine(fixture.Path, "empty-cloud.json");
        File.WriteAllText(emptyCloudPath, "[]");
        var insufficient = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [emptyCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "insufficient-result.json")));
        Assert(insufficient["summary"]?["inconclusive"]?.GetValue<int>() == 1, "无法证明导出范围时，未命中应为 INCONCLUSIVE。");
        Assert(insufficient["capabilities"]?[0]?["export_coverage"]?.GetValue<string>() == "insufficient", "空日志的覆盖状态应为 insufficient。");
        Assert(insufficient["capabilities"]?[0]?["baseline_requirements"]?.AsArray().Any(value => value?["scope"]?.GetValue<string>() == "cloud" && value?["status"]?.GetValue<string>() == "not_evaluated") == true, "没有云端候选时应明确标记未检查的云端要求。");

        var unmatchedCloudPath = Path.Combine(fixture.Path, "unmatched-cloud.json");
        File.WriteAllText(unmatchedCloudPath, CreateUnmatchedCloudExport(local).ToJsonString(JsonDefaults.Options));
        var inferredMiss = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [unmatchedCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "inferred-miss-result.json")));
        Assert(inferredMiss["summary"]?["fail"]?.GetValue<int>() == 1, "同主机日志包住能力时间窗时，未命中应形成 FAIL。");
        Assert(inferredMiss["capabilities"]?[0]?["export_coverage"]?.GetValue<string>() == "inferred", "时间窗证据应标记为 inferred。");
        var missingEventRequirement = inferredMiss["capabilities"]?[0]?["baseline_requirements"]?.AsArray().Single(value => value?["field"]?.GetValue<string>() == "event.count");
        Assert(missingEventRequirement?["status"]?.GetValue<string>() == "failed" && missingEventRequirement["actual"]?.GetValue<int>() == 0, "事件缺失应显示为“至少 1 条，实际 0 条”。");

        var inspect = InspectService.Inspect(result.DatabasePath);
        Assert(inspect["status"]?.GetValue<string>() == "COMPLETED", "Inspect 应看到封存终态。");
        var secondExport = Path.Combine(fixture.Path, "second-local-run.json");
        ExportService.Export(result.DatabasePath, secondExport);
        var second = JsonNode.Parse(File.ReadAllText(secondExport))!.AsObject();
        Assert(second["integrity"]?["database_sha256"]?.GetValue<string>() == local["integrity"]?["database_sha256"]?.GetValue<string>(), "重复导出数据库 hash 应稳定。");
        Assert(second["local_events"]!.ToJsonString() == local["local_events"]!.ToJsonString(), "重复导出业务事件应稳定。");
    }

    private static async Task TestHighRiskGate()
    {
        using var fixture = TestDirectory.Create();
        var manifestPath = PreparePackage(fixture.Path, "L2", "--fixture-controller");
        var result = await new RunnerService().RunAsync(new RunRequest([manifestPath], Path.Combine(fixture.Path, "runs"), null, false));
        var local = JsonNode.Parse(File.ReadAllText(result.LocalExportPath))!.AsObject();
        Assert(result.Status == "COMPLETED", "安全跳过不应使整轮报错。");
        Assert(local["capabilities"]?[0]?["status"]?.GetValue<string>() == "SKIPPED", "L2 能力默认应跳过。");
        Assert(local["capabilities"]?[0]?["error_code"]?.GetValue<string>() == "RISK_APPROVAL_REQUIRED", "应记录风险授权原因。");
        Assert(local["programs"]?.AsArray().Count == 0, "跳过能力不应伪造程序实例。");
    }

    private static async Task TestControllerTimeout()
    {
        using var fixture = TestDirectory.Create();
        var manifestPath = PreparePackage(fixture.Path, "L0", "--fixture-hang", executeSeconds: 1, cleanupSeconds: 1);
        var result = await new RunnerService().RunAsync(new RunRequest([manifestPath], Path.Combine(fixture.Path, "runs"), null, false));
        var local = JsonNode.Parse(File.ReadAllText(result.LocalExportPath))!.AsObject();
        Assert(result.Status == "COMPLETED_WITH_ERRORS", "Controller 超时应使轮次带错误完成。");
        Assert(local["capabilities"]?[0]?["status"]?.GetValue<string>() == "SAMPLE_ERROR", "超时能力应为 SAMPLE_ERROR。");
        Assert(local["capabilities"]?[0]?["error_code"]?.GetValue<string>() == "CONTROLLER_TIMEOUT", "应保留超时错误码。");
    }

    private static async Task TestMultipleCapabilities()
    {
        using var fixture = TestDirectory.Create();
        var firstManifest = PreparePackage(fixture.Path, "L0", "--fixture-controller");
        var secondManifest = Path.Combine(Path.GetDirectoryName(firstManifest)!, "capability-second.json");
        var second = JsonNode.Parse(File.ReadAllText(firstManifest))!.AsObject();
        second["capability_id"] = "win.process.terminate";
        second["display_name_zh"] = "进程终止测试夹具";
        second["display_name_en"] = "Process Termination Fixture";
        File.WriteAllText(secondManifest, second.ToJsonString(JsonDefaults.Options));
        var updates = new ConcurrentQueue<RunProgressUpdate>();
        var stopwatch = Stopwatch.StartNew();
        var result = await new RunnerService().RunAsync(new RunRequest(
            [firstManifest, secondManifest],
            Path.Combine(fixture.Path, "runs"),
            null,
            false,
            InterCapabilityDelaySeconds: 1,
            ProgressCallback: updates.Enqueue));
        stopwatch.Stop();
        var local = JsonNode.Parse(File.ReadAllText(result.LocalExportPath))!.AsObject();
        Assert(local["capabilities"]?.AsArray().Count == 2, "一轮应保存两个能力。");
        Assert(local["programs"]?.AsArray().Count == 6, "两个能力应分别保存三个程序实例。");
        Assert(local["local_events"]?.AsArray().Count == 2, "两个能力应分别保存本地事件。");
        Assert(local["capabilities"]?[0]?["sequence"]?.GetValue<int>() == 1, "首个能力顺序应为 1。");
        Assert(local["capabilities"]?[1]?["sequence"]?.GetValue<int>() == 2, "第二个能力顺序应为 2。");
        Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), "能力之间应执行配置的等待时间。");
        Assert(updates.Any(value => value.Kind == "waiting_next" && value.WaitRemainingSeconds == 1), "进度流应包含下一项能力倒计时。");
        var starts = updates.Where(value => value.Kind == "capability_started").Select(value => value.CapabilityId).ToArray();
        Assert(starts.SequenceEqual(new[] { "win.process.create", "win.process.terminate" }), "能力开始事件必须保持用户选择顺序。");
        Assert(new RunRequest([], "runs", null, false).InterCapabilityDelaySeconds == 3, "能力间默认等待时间应为 3 秒。");
    }

    private static async Task TestCancellation()
    {
        using var fixture = TestDirectory.Create();
        var manifestPath = PreparePackage(fixture.Path, "L0", "--fixture-hang", executeSeconds: 20, cleanupSeconds: 5);
        var runs = Path.Combine(fixture.Path, "runs");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await AssertThrowsAsync<OperationCanceledException>(() =>
            new RunnerService().RunAsync(new RunRequest([manifestPath], runs, null, false), cancellation.Token));
        var databasePath = Directory.EnumerateFiles(runs, "*.db", SearchOption.AllDirectories).Single();
        var inspect = InspectService.Inspect(databasePath);
        Assert(inspect["status"]?.GetValue<string>() == "ABORTED", "取消后的数据库应封存为 ABORTED。");
        Assert(inspect["capabilities"]?[0]?["status"]?.GetValue<string>() == "ABORTED", "当前能力应封存为 ABORTED。");
    }

    private static int RunFixtureController(string[] args)
    {
        try
        {
            var invocation = ControllerInvocation.Parse(args);
            using var database = RunDatabase.OpenReadWrite(invocation.RunDb);
            var observedAt = DateTimeOffset.UtcNow;
            var controller = ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller");
            var executable = controller.ExecutablePath;
            var actor = CreateProgram(invocation.CaseRunId, "actor", 0, executable, Environment.ProcessId + 100, Environment.ProcessId, $"\"{executable}\" --fixture-actor --nonce {invocation.Nonce}", observedAt);
            var target = CreateProgram(invocation.CaseRunId, "target", 0, executable, Environment.ProcessId + 101, actor.Pid, $"\"{executable}\" --fixture-target --nonce {invocation.Nonce}", observedAt.AddMilliseconds(10));
            database.AddProgram(controller);
            database.AddProgram(actor);
            database.AddProgram(target);

            var localEvent = new LocalEventObservation
            {
                CaseRunId = invocation.CaseRunId,
                EventType = "process",
                EventAction = "create",
                Nonce = invocation.Nonce,
                OccurredAtUtc = observedAt.AddMilliseconds(10),
                ObservedAtUtc = observedAt.AddMilliseconds(15),
                MonotonicOffsetMs = 15,
                Source = "fixture_controller",
                CollectionMethod = "process_handle_query",
                Confidence = "high",
                ActorProgramId = actor.ProgramInstanceId,
                TargetProgramId = target.ProgramInstanceId,
                Data = new JsonObject
                {
                    ["kind"] = "process",
                    ["operation"] = "create",
                    ["actor"] = ProcessReference(actor),
                    ["target"] = ProcessReference(target),
                    ["result"] = new JsonObject { ["attempted"] = true, ["succeeded"] = true, ["win32_error"] = 0 },
                    ["creation"] = new JsonObject { ["creation_flags"] = 0, ["inherit_handles"] = false, ["initial_thread_id"] = 42 },
                },
            };
            database.AddEvent(localEvent);
            database.AddFact(new LocalFactObservation
            {
                CaseRunId = invocation.CaseRunId,
                LocalEventId = localEvent.LocalEventId,
                Key = "process.create_succeeded",
                Value = JsonValue.Create(true),
                ObservedAtUtc = observedAt,
                Source = "fixture_controller",
            });
            database.AddCleanup(new CleanupObservation
            {
                CaseRunId = invocation.CaseRunId,
                Action = "wait_fixture_target_exit",
                Status = "succeeded",
                StartedAtUtc = observedAt.AddMilliseconds(20),
                EndedAtUtc = observedAt.AddMilliseconds(21),
                Before = new JsonObject { ["exists"] = true },
                After = new JsonObject { ["exists"] = false },
            });
            database.CompleteCapability(invocation.CaseRunId, "LOCAL_PASS", observedAt.AddMilliseconds(25), 25);
            Console.WriteLine("{\"schema_version\":\"1.0\",\"status\":\"LOCAL_PASS\"}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 20;
        }
    }

    private static ProgramObservation CreateProgram(string caseRunId, string role, int index, string executable, int pid, int parentPid, string commandLine, DateTimeOffset startedAt) => new()
    {
        CaseRunId = caseRunId,
        Role = role,
        InstanceIndex = index,
        ExecutablePath = executable,
        Sha256 = Hashing.FileSha256(executable),
        Sha1 = Hashing.FileSha1(executable),
        Md5 = Hashing.FileMd5(executable),
        Pid = pid,
        ParentPid = parentPid,
        Architecture = "x64",
        CommandLine = commandLine,
        WorkingDirectory = Environment.CurrentDirectory,
        StartedAtUtc = startedAt,
        StartupAttempted = true,
        StartupSucceeded = true,
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

    private static string PreparePackage(string root, string riskLevel, string controllerArgument, int executeSeconds = 10, int cleanupSeconds = 5)
    {
        var packageDirectory = Path.Combine(root, "package");
        CopyDirectory(AppContext.BaseDirectory, packageDirectory);
        var executableName = Path.GetFileName(Environment.ProcessPath) ?? "EdrTest.Tests.exe";
        var manifestPath = Path.Combine(packageDirectory, "capability.json");
        File.WriteAllText(manifestPath, CreateManifest(executableName, riskLevel, controllerArgument, executeSeconds, cleanupSeconds).ToJsonString(JsonDefaults.Options));
        return manifestPath;
    }

    private static JsonObject CreateManifest(string executable, string riskLevel, string controllerArgument = "--fixture-controller", int executeSeconds = 10, int cleanupSeconds = 5) => new()
    {
        ["schema_version"] = "1.1",
        ["capability_id"] = "win.process.create",
        ["version"] = "0.1.0",
        ["display_name_zh"] = "进程创建",
        ["display_name_en"] = "Process Creation",
        ["platform"] = new JsonObject { ["os"] = "windows", ["architectures"] = new JsonArray("x64") },
        ["risk_level"] = riskLevel,
        ["required_privilege"] = "standard_user",
        ["controller"] = new JsonObject { ["executable"] = executable, ["arguments"] = new JsonArray(controllerArgument) },
        ["participants"] = new JsonArray(
            new JsonObject { ["role"] = "actor", ["executable"] = executable, ["arguments"] = new JsonArray("--fixture-actor") },
            new JsonObject { ["role"] = "target", ["executable"] = executable, ["arguments"] = new JsonArray("--fixture-target") }),
        ["parameters"] = new JsonObject
        {
            ["target_lifetime_ms"] = new JsonObject { ["type"] = "integer", ["required"] = true, ["default"] = 100 },
        },
        ["timeouts"] = new JsonObject { ["execute_seconds"] = executeSeconds, ["cleanup_seconds"] = cleanupSeconds },
        ["network"] = new JsonObject { ["required"] = false },
        ["expected_fact_keys"] = new JsonArray("process.create_succeeded"),
    };

    private static JsonArray CreateCloudExport(JsonObject local)
    {
        var capability = local["capabilities"]!.AsArray()[0]!.AsObject();
        var programs = local["programs"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var actor = programs.Single(x => x["role"]!.GetValue<string>() == "actor");
        var target = programs.Single(x => x["role"]!.GetValue<string>() == "target");
        var host = local["run"]!["host"]!.AsObject();
        var eventTime = DateTimeOffset.Parse(capability["started_at_utc"]!.GetValue<string>()).AddMilliseconds(20).ToUnixTimeMilliseconds();
        return new JsonArray(new JsonObject
        {
            ["@table"] = "ProcEvents",
            ["@collection"] = Values.Utc(DateTimeOffset.UtcNow),
            ["OS"] = "Windows",
            ["Action.Type"] = "Proc",
            ["Action.Name"] = "ProcessCreate",
            ["Common.EventUUId"] = Ids.NewUuid7(),
            ["Common.EventTime"] = eventTime,
            ["Common.Mid"] = "fixture-mid",
            ["Common.Guid"] = "fixture-agent",
            ["Common.ClientVer"] = "fixture-1",
            ["Environment.HostName"] = host["hostname"]!.GetValue<string>(),
            ["Environment.OsVersion"] = host["os_version"]!.GetValue<string>(),
            ["Child.ProcPid"] = target["pid"]!.GetValue<int>(),
            ["Child.ProcGuid"] = Ids.NewUuid7(),
            ["Child.FileName"] = target["file_name"]!.GetValue<string>(),
            ["Child.FilePath"] = target["executable"]!.GetValue<string>(),
            ["Child.ProcCmdline"] = target["command_line"]!.GetValue<string>(),
            ["Child.ProcCreateTime"] = DateTimeOffset.Parse(target["started_at_utc"]!.GetValue<string>()).ToUnixTimeMilliseconds(),
            ["Child.FileMd5"] = target["md5"]?.GetValue<string>() ?? string.Empty,
            ["Child.ProcUserName"] = "fixture",
            ["Child.ProcDomainName"] = "TEST",
            ["Parent.ProcPid"] = actor["pid"]!.GetValue<int>(),
            ["Parent.ProcGuid"] = Ids.NewUuid7(),
            ["Parent.FileName"] = actor["file_name"]!.GetValue<string>(),
            ["Parent.FilePath"] = actor["executable"]!.GetValue<string>(),
            ["Parent.ProcCmdline"] = actor["command_line"]!.GetValue<string>(),
            ["Parent.ProcCreateTime"] = DateTimeOffset.Parse(actor["started_at_utc"]!.GetValue<string>()).ToUnixTimeMilliseconds(),
        });
    }

    private static JsonArray CreateUnmatchedCloudExport(JsonObject local)
    {
        var capability = local["capabilities"]!.AsArray()[0]!.AsObject();
        var start = DateTimeOffset.Parse(capability["started_at_utc"]!.GetValue<string>());
        var end = DateTimeOffset.Parse(capability["ended_at_utc"]!.GetValue<string>());
        var first = CreateCloudExport(local)[0]!.DeepClone().AsObject();
        first["Common.EventTime"] = start.AddSeconds(-1).ToUnixTimeMilliseconds();
        first["Child.FilePath"] = @"C:\unrelated\first.exe";
        first["Child.ProcCmdline"] = @"C:\unrelated\first.exe --noise";
        first["Parent.FilePath"] = @"C:\unrelated\parent.exe";
        first["Parent.ProcCmdline"] = @"C:\unrelated\parent.exe --noise";
        var second = first.DeepClone().AsObject();
        second["Common.EventUUId"] = Ids.NewUuid7();
        second["Common.EventTime"] = end.AddSeconds(1).ToUnixTimeMilliseconds();
        return new JsonArray(first, second);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "schemas", "run-db.sql"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("无法定位仓库根目录。");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"预期抛出 {typeof(T).Name}。");
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"预期抛出 {typeof(T).Name}。");
    }
}

internal sealed class TestDirectory : IDisposable
{
    private readonly bool preserve;

    private TestDirectory(string path, bool preserve)
    {
        Path = path;
        this.preserve = preserve;
    }

    public string Path { get; }

    public static TestDirectory Create()
    {
        var preservedRoot = Environment.GetEnvironmentVariable("EDRTEST_TEST_OUTPUT");
        var preserve = !string.IsNullOrWhiteSpace(preservedRoot);
        var root = preserve ? System.IO.Path.GetFullPath(preservedRoot!) : System.IO.Path.GetTempPath();
        var path = System.IO.Path.Combine(root, "edrtest-framework-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestDirectory(path, preserve);
    }

    public void Dispose()
    {
        if (!preserve && Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
