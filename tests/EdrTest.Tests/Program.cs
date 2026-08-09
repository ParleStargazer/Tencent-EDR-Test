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
        if (args.FirstOrDefault() == "--fixture-long-output")
        {
            return RunFixtureController(args.Skip(1).ToArray(), emitLongOutput: true);
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
        await RunTest("Runner 与 SQLite 完整保留长日志", TestLongControllerOutput, failures);
        await RunTest("Controller 超时封存为 SAMPLE_ERROR", TestControllerTimeout, failures);
        await RunTest("取消轮次会终止进程树并封存 ABORTED", TestCancellation, failures);
        await RunTest("Runner → SQLite → Export → Compare 最小闭环", TestEndToEnd, failures);
        await RunTest("同类多子项使用独立锚点与时间关联", TestExpectationCorrelation, failures);
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
        Assert(requirements.Count == 14, "进程创建应展示 5 项本地要求、事件数量与时间差要求，以及 7 项云端字段要求。");
        var timeRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "event.time_difference_ms");
        Assert(timeRequirement?["status"]?.GetValue<string>() == "passed"
            && timeRequirement["expected"]?["max"]?.GetValue<int>() == 10
            && timeRequirement["actual"]?.GetValue<long>() <= 10,
            "10 ms 时间差必须作为显式的必需 EDR 关联条件并保留实际毫秒差。");
        Assert(requirements.Where(value => value?["severity"]?.GetValue<string>() == "required").All(value => value?["status"]?.GetValue<string>() == "passed"), "所有必需 BASELINE 要求都应通过。");
        var firstCandidate = validation["capabilities"]?[0]?["edr_candidates"]?.AsArray().Single()
            ?? throw new InvalidOperationException("结果应包含完整 EDR 候选日志。");
        Assert(firstCandidate["rank"]?.GetValue<int>() == 1 && firstCandidate["raw_event"]?["@table"]?.GetValue<string>() == "ProcEvents", "EDR 候选应保留排名和原始完整日志。");
        var localExportBlock = validation["capabilities"]?[0]?["local_export_block"]?.AsObject()
            ?? throw new InvalidOperationException("能力结果应包含可供悬浮窗展示的本地导出 JSON 块。");
        Assert(localExportBlock["programs"]?.AsArray().Count == 3
            && localExportBlock["local_events"]?.AsArray().Count == 1,
            "本地导出 JSON 块只应保留当前能力的程序和事件。");
        var localPidMatch = validation["capabilities"]?[0]?["local_baseline_matches"]?.AsArray()
            .Single(value => value?["field"]?.GetValue<string>() == "programs.actor.pid");
        Assert(localPidMatch?["status"]?.GetValue<string>() == "passed"
            && localPidMatch["json_pointer"]?.GetValue<string>().StartsWith("/programs/", StringComparison.Ordinal) == true,
            "本地 BASELINE 命中应带有原 JSON 精确位置。");
        var cloudPidMatch = firstCandidate["baseline_matches"]?.AsArray()
            .Single(value => value?["kind"]?.GetValue<string>() == "assertion"
                && value?["canonical_field"]?.GetValue<string>() == "process.pid");
        Assert(cloudPidMatch?["status"]?.GetValue<string>() == "passed"
            && cloudPidMatch["raw_field"]?.GetValue<string>() == "Child.ProcPid"
            && cloudPidMatch["raw_json_pointer"]?.GetValue<string>() == "/Child.ProcPid",
            "每条 EDR 候选应把通过的规范字段映射回原始 JSON 字段供高亮。");
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
        fartherCandidate["Common.EventTime"] = multipleCloud[0]!["Common.EventTime"]!.GetValue<long>() + 8;
        fartherCandidate["Action.Name"] = "PreferredProcessCreate";
        multipleCloud.Add(fartherCandidate);
        var lowerConfidenceCandidate = multipleCloud[0]!.DeepClone().AsObject();
        lowerConfidenceCandidate["Common.EventUUId"] = Ids.NewUuid7();
        lowerConfidenceCandidate["Common.EventTime"] = multipleCloud[0]!["Common.EventTime"]!.GetValue<long>() + 2;
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
        Assert(rankedCandidates[2]?["baseline_matches"]?.AsArray().Any(value => value?["canonical_field"]?.GetValue<string>() == "parent_process.executable"
            && value?["status"]?.GetValue<string>() == "failed") == true, "候选切换时应能看到该候选自身不满足的 BASELINE 字段。");
        Assert(multipleCandidates["capabilities"]?[0]?["validation_status"]?.GetValue<string>() == "PASS", "时间距离可以消除同分候选歧义。");

        var customActionResultPath = Path.Combine(fixture.Path, "custom-action-result.json");
        var customActionCandidates = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [multipleCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            customActionResultPath,
            ActionNameStandards: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["win.process.create"] = ["AnotherAcceptedAction", "PreferredProcessCreate"],
            }));
        var customCandidates = customActionCandidates["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("自定义 Action.Name 结果缺少候选事件。");
        Assert(customActionCandidates["summary"]?["pass"]?.GetValue<int>() == 1, "正确的自定义 Action.Name 应在强锚点候选中完成消歧并通过。");
        Assert(customCandidates[0]?["raw_event"]?["Action.Name"]?.GetValue<string>() == "PreferredProcessCreate"
            && customCandidates[0]?["anchor_qualified"]?.GetValue<bool>() == true
            && customCandidates[0]?["eligible_for_validation"]?.GetValue<bool>() == true,
            "自定义 Action.Name 应优先选择符合标准的强锚点记录，即使它的时间距离更远。");
        Assert(customCandidates.Count(value => value?["anchor_qualified"]?.GetValue<bool>() == true
            && value?["eligible_for_validation"]?.GetValue<bool>() == false) == 2,
            "Action.Name 不符的强锚点记录仍必须保留，但不能进入自动判定。");
        Assert(customCandidates[0]?["baseline_matches"]?.AsArray().Any(value => value?["kind"]?.GetValue<string>() == "custom_filter"
            && value?["status"]?.GetValue<string>() == "passed"
            && value?["raw_json_pointer"]?.GetValue<string>() == "/Action.Name") == true,
            "符合自定义标准的原始 Action.Name 应进入候选字段高亮信息。");
        Assert(customActionCandidates["inputs"]?["action_name_standards"]?["win.process.create"]?[1]?.GetValue<string>() == "PreferredProcessCreate",
            "验证结果必须记录本次使用的 Action.Name 自定义标准以便审计。");
        Assert(File.ReadAllText(ConclusionExportService.DefaultOutputPath(customActionResultPath)).Contains("PreferredProcessCreate", StringComparison.Ordinal),
            "中文结论应记录本次使用的 Action.Name 自定义标准。");

        var configuredActions = customActionCandidates["inputs"]?["action_name_standards"]?["win.process.create"]?.AsArray()
            ?? throw new InvalidOperationException("验证结果缺少 Action.Name 多值标准。");
        Assert(configuredActions.Count == 2
            && configuredActions[0]?.GetValue<string>() == "AnotherAcceptedAction"
            && configuredActions[1]?.GetValue<string>() == "PreferredProcessCreate",
            "同一能力的多个 Action.Name 标准应采用任选其一语义并完整留痕。");

        var noRawActionCloud = CreateCloudExport(local);
        noRawActionCloud[0]!.AsObject().Remove("Action.Name");
        var noRawActionCloudPath = Path.Combine(fixture.Path, "no-raw-action-cloud.json");
        File.WriteAllText(noRawActionCloudPath, noRawActionCloud.ToJsonString(JsonDefaults.Options));
        var noRawAction = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [noRawActionCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "no-raw-action-result.json"),
            ActionNameStandards: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["win.process.create"] = ["candidate"],
            }));
        var noRawActionCapability = noRawAction["capabilities"]?[0]?.AsObject()
            ?? throw new InvalidOperationException("缺少无原始 Action.Name 的比较结果。");
        Assert(noRawActionCapability["local_status"]?.GetValue<string>() == "LOCAL_PASS"
            && noRawActionCapability["baseline_requirements"]?.AsArray()
                .Where(value => value?["scope"]?.GetValue<string>() == "local")
                .All(value => value?["status"]?.GetValue<string>() == "passed") == true,
            "EDR Action.Name 筛选不得改变 LOCAL_PASS 或任何本地要求。");
        Assert(noRawActionCapability["baseline_requirements"]?.AsArray().Any(value => value?["field"]?.GetValue<string>() == "Action.Name"
            && value?["status"]?.GetValue<string>() == "failed") == true,
            "原始 EDR JSON 缺少 Action.Name 时不得回退使用规范化 event.action 通过筛选。");

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
        var exploratoryCandidates = inferredMiss["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("低置信度候选结果缺少 edr_candidates。");
        Assert(exploratoryCandidates.Count == 2
            && exploratoryCandidates.All(value => value?["eligible_for_validation"]?.GetValue<bool>() == false),
            "未命中强本地锚点时仍应保留时间相近的低置信度 EDR JSON 块，但不能计为可靠关联。");
        Assert(exploratoryCandidates[0]?["baseline_matches"]?.AsArray().Any(value => value?["status"]?.GetValue<string>() == "passed") == true
            && exploratoryCandidates[0]?["baseline_matches"]?.AsArray().Any(value => value?["status"]?.GetValue<string>() == "failed") == true,
            "低置信度候选也必须继续逐字段比较，不能因部分 EDR 字段不匹配而提前停止。");

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

    private static Task TestExpectationCorrelation()
    {
        using var fixture = TestDirectory.Create();
        var caseRunId = Ids.NewUuid7();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var firstTime = started.AddSeconds(10);
        var secondTime = started.AddSeconds(20);
        const string targetPath = @"C:\samples\ImageLoad.Target.exe";
        const string firstPath = @"C:\Windows\System32\winhttp.dll";
        const string secondPath = @"C:\runs\edrtest_nonce_version.dll";
        var local = new JsonObject
        {
            ["run"] = new JsonObject { ["host"] = new JsonObject { ["hostname"] = "fixture-host" } },
            ["capabilities"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["capability_id"] = "win.process.image_load",
                ["capability_version"] = "0.2.0",
                ["status"] = "LOCAL_PASS",
                ["nonce"] = "fixture-nonce",
                ["started_at_utc"] = Values.Utc(started),
                ["ended_at_utc"] = Values.Utc(started.AddSeconds(30)),
            }),
            ["programs"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["role"] = "target",
                ["pid"] = 4242,
                ["executable"] = targetPath,
            }),
            ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(
                Fact(caseRunId, "process.image_load.first.succeeded", true),
                Fact(caseRunId, "process.image_load.first.path", firstPath),
                Fact(caseRunId, "process.image_load.first.file_name", "winhttp.dll"),
                Fact(caseRunId, "process.image_load.first.occurred_at_utc", Values.Utc(firstTime)),
                Fact(caseRunId, "process.image_load.second.succeeded", true),
                Fact(caseRunId, "process.image_load.second.path", secondPath),
                Fact(caseRunId, "process.image_load.second.file_name", "edrtest_nonce_version.dll"),
                Fact(caseRunId, "process.image_load.second.occurred_at_utc", Values.Utc(secondTime))),
        };
        var localPath = Path.Combine(fixture.Path, "local.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));

        var cloud = new JsonArray(
            CloudImage("first-event", firstTime, firstPath, "winhttp.dll", targetPath),
            CloudImage("second-event", secondTime, secondPath, "edrtest_nonce_version.dll", targetPath));
        var cloudPath = Path.Combine(fixture.Path, "cloud.json");
        File.WriteAllText(cloudPath, cloud.ToJsonString(JsonDefaults.Options));
        var baselinePath = Path.Combine(fixture.Path, "baseline.yaml");
        File.WriteAllText(baselinePath, """
            schema_version: "1.1"
            baseline_id: win.process.image_load
            version: "0.2.0"
            title: 多子项镜像加载测试
            risk_level: L0
            capability: { id: win.process.image_load, version: "0.2.0" }
            local_requirements:
              - { field: facts.process.image_load.first.succeeded, operator: equals, expected: true }
              - { field: facts.process.image_load.second.succeeded, operator: equals, expected: true }
            correlation:
              time_before_seconds: 60
              time_after_seconds: 60
              max_time_difference_ms: 10
              anchors:
                - { local_field: programs.target.executable, cloud_field: process.executable, strength: strong, normalizers: [windows_path] }
            cloud_expectations:
              - id: first-event
                event_type: process
                event_actions: [image_load]
                cardinality: { min: 1, max: 1 }
                correlation:
                  time_from_local: facts.process.image_load.first.occurred_at_utc
                  anchors:
                    - { local_field: facts.process.image_load.first.path, cloud_field: file.path, strength: strong, normalizers: [windows_path] }
                    - { local_field: facts.process.image_load.first.file_name, cloud_field: file.name, strength: strong, normalizers: [lowercase] }
                assertions:
                  - { field: file.path, operator: equals, expected_from_local: facts.process.image_load.first.path, severity: required, normalizers: [windows_path] }
              - id: second-event
                event_type: process
                event_actions: [image_load]
                cardinality: { min: 1, max: 1 }
                correlation:
                  time_from_local: facts.process.image_load.second.occurred_at_utc
                  anchors:
                    - { local_field: facts.process.image_load.second.path, cloud_field: file.path, strength: strong, normalizers: [windows_path] }
                    - { local_field: facts.process.image_load.second.file_name, cloud_field: file.name, strength: strong, normalizers: [lowercase] }
                assertions:
                  - { field: file.path, operator: equals, expected_from_local: facts.process.image_load.second.path, severity: required, normalizers: [windows_path] }
            """);
        var repository = FindRepositoryRoot();
        var validation = CompareService.Compare(new CompareRequest(
            localPath,
            [cloudPath],
            Path.Combine(repository, "mappings", "generic-process-activity-v1.yaml"),
            [baselinePath],
            Path.Combine(fixture.Path, "result.json")));
        Assert(validation["summary"]?["pass"]?.GetValue<int>() == 1, "两个同类子项应分别关联并通过。");
        var candidates = validation["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("多子项结果缺少候选事件。");
        Assert(candidates.Count == 4, "每个子项应保留自身强匹配和另一条低置信度候选供排查。");
        var eligibleCandidates = candidates.Where(value => value?["eligible_for_validation"]?.GetValue<bool>() == true).ToArray();
        Assert(eligibleCandidates.Length == 2
            && eligibleCandidates.Any(value => value?["expectation_id"]?.GetValue<string>() == "first-event" && value?["event_id"]?.GetValue<string>() == "first-event")
            && eligibleCandidates.Any(value => value?["expectation_id"]?.GetValue<string>() == "second-event" && value?["event_id"]?.GetValue<string>() == "second-event"),
            "每个子项只能让命中自身本地路径的事件进入自动判定。");
        Assert(candidates.Count(value => value?["eligible_for_validation"]?.GetValue<bool>() == false) == 2,
            "未命中子项路径的事件仍应作为低置信度 JSON 块展示。");
        Assert(eligibleCandidates.All(value => value?["time_distance_ms"]?.GetValue<long>() == 0), "子项应使用自己的本地发生时间计算强匹配候选的距离。");

        var oldVersionLocal = local.DeepClone().AsObject();
        oldVersionLocal["capabilities"]![0]!["capability_version"] = "0.1.0";
        var oldVersionLocalPath = Path.Combine(fixture.Path, "old-version-local.json");
        File.WriteAllText(oldVersionLocalPath, oldVersionLocal.ToJsonString(JsonDefaults.Options));
        var versionMismatch = CompareService.Compare(new CompareRequest(
            oldVersionLocalPath,
            [cloudPath],
            Path.Combine(repository, "mappings", "generic-process-activity-v1.yaml"),
            [baselinePath],
            Path.Combine(fixture.Path, "version-mismatch-result.json")));
        var mismatchCapability = versionMismatch["capabilities"]?[0]
            ?? throw new InvalidOperationException("版本错配结果缺少能力结论。");
        Assert(mismatchCapability["validation_status"]?.GetValue<string>() == "NOT_COMPARED", "旧能力包不得套用新版 BASELINE 形成采集失败误报。");
        Assert(mismatchCapability["baseline_requirements"]?.AsArray().Count == 0, "版本不匹配时不应展示新版 BASELINE 条件。");
        Assert(mismatchCapability["warnings"]?.AsArray().Any(value => value?.GetValue<string>().Contains("能力版本 0.1.0", StringComparison.Ordinal) == true) == true,
            "版本错配应明确提示缺少对应版本的 BASELINE。");
        return Task.CompletedTask;
    }

    private static JsonObject Fact(string caseRunId, string key, object value) => new()
    {
        ["case_run_id"] = caseRunId,
        ["key"] = key,
        ["value"] = JsonValue.Create(value),
    };

    private static JsonObject CloudImage(string eventId, DateTimeOffset time, string imagePath, string fileName, string targetPath) => new()
    {
        ["table"] = "ProcessActivity",
        ["event_id"] = eventId,
        ["host_id"] = "fixture-host",
        ["event_time"] = Values.Utc(time),
        ["action"] = "image_load",
        ["target_pid"] = 4242,
        ["target_executable"] = targetPath,
        ["file_path"] = imagePath,
        ["file_name"] = fileName,
    };

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

    private static async Task TestLongControllerOutput()
    {
        using var fixture = TestDirectory.Create();
        var manifestPath = PreparePackage(fixture.Path, "L0", "--fixture-long-output");
        var progress = new List<RunProgressUpdate>();
        var result = await new RunnerService().RunAsync(new RunRequest(
            [manifestPath],
            Path.Combine(fixture.Path, "runs"),
            null,
            false,
            ProgressCallback: progress.Add));
        var expected = BuildLongFixtureLine();
        var streamed = progress.Single(value => value.Kind == "controller_stdout" && value.Message.StartsWith("LONG_LOG_START:", StringComparison.Ordinal));
        Assert(streamed.Message == expected, "实时进度必须完整保留超过 16 KiB 的单行输出。");

        var local = JsonNode.Parse(File.ReadAllText(result.LocalExportPath))!.AsObject();
        var persisted = local["execution_logs"]?.AsArray()
            .Single(value => value?["phase"]?.GetValue<string>() == "controller.stdout")?["message"]?.GetValue<string>()
            ?? throw new InvalidOperationException("本地导出缺少 Controller 标准输出。");
        Assert(persisted.Contains(expected, StringComparison.Ordinal), "SQLite 与本地导出必须完整保留长日志。");
        Assert(!persisted.Contains("[truncated]", StringComparison.OrdinalIgnoreCase), "长日志中不应再写入截断标记。");
    }

    private static int RunFixtureController(string[] args, bool emitLongOutput = false)
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
            if (emitLongOutput) Console.WriteLine(BuildLongFixtureLine());
            Console.WriteLine("{\"schema_version\":\"1.0\",\"status\":\"LOCAL_PASS\"}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 20;
        }
    }

    private static string BuildLongFixtureLine() => $"LONG_LOG_START:{new string('L', 20_000)}:LONG_LOG_END";

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
        var programs = local["programs"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var actor = programs.Single(x => x["role"]!.GetValue<string>() == "actor");
        var target = programs.Single(x => x["role"]!.GetValue<string>() == "target");
        var host = local["run"]!["host"]!.AsObject();
        var eventTime = DateTimeOffset.Parse(local["local_events"]!.AsArray()[0]!["occurred_at_utc"]!.GetValue<string>()).ToUnixTimeMilliseconds();
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
