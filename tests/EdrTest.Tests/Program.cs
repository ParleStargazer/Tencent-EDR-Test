using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdrTest;
using EdrTest.SampleProtocol;

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
        await RunTest("协议 JSON 遇到短暂文件独占时可靠重试", TestReliableProtocolFile, failures);
        await RunTest("L2/L3 默认风险门禁", TestHighRiskGate, failures);
        await RunTest("同一轮按顺序执行多个能力", TestMultipleCapabilities, failures);
        await RunTest("Runner 与 SQLite 完整保留长日志", TestLongControllerOutput, failures);
        await RunTest("Controller 超时封存为 SAMPLE_ERROR", TestControllerTimeout, failures);
        await RunTest("取消轮次会终止进程树并封存 ABORTED", TestCancellation, failures);
        await RunTest("Runner → SQLite → Export → Compare 最小闭环", TestEndToEnd, failures);
        await RunTest("同类多子项使用独立锚点与时间关联", TestExpectationCorrelation, failures);
        await RunTest("文件原始字段仅筛选 EDR 候选", TestFileRawFieldFilters, failures);
        await RunTest("用户账号五项 BASELINE 与通用/腾讯映射闭环", TestUserAccountComparison, failures);
        await RunTest("注册表三项 BASELINE 与通用/腾讯映射闭环", TestRegistryComparison, failures);
        await RunTest("计划任务三项 BASELINE 与通用/腾讯映射闭环", TestScheduledTaskComparison, failures);
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

    private static async Task TestReliableProtocolFile()
    {
        using var fixture = TestDirectory.Create();
        var path = Path.Combine(fixture.Path, "behavior-result.json");
        ReliableProtocolFile.WriteAtomic(path, new JsonObject { ["value"] = "original" }, JsonDefaults.Options);

        using (var readLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var readTask = Task.Run(() => ReliableProtocolFile.Read<JsonObject>(path, JsonDefaults.Options, 3_000));
            await Task.Delay(200);
            Assert(!readTask.IsCompleted, "协议读取没有等待 FileShare.None 独占锁释放。");
            readLock.Dispose();
            var document = await readTask;
            Assert(document["value"]?.GetValue<string>() == "original", "锁释放后的协议 JSON 内容不正确。");
        }

        using (var replaceLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var writeTask = Task.Run(() => ReliableProtocolFile.WriteAtomic(
                path,
                new JsonObject { ["value"] = "updated" },
                JsonDefaults.Options,
                3_000));
            await Task.Delay(200);
            replaceLock.Dispose();
            await writeTask;
        }

        var updated = ReliableProtocolFile.Read<JsonObject>(path, JsonDefaults.Options);
        Assert(updated["value"]?.GetValue<string>() == "updated", "锁释放后的原子协议替换未生效。");
        Assert(!Directory.EnumerateFiles(fixture.Path, "*.tmp-*", SearchOption.TopDirectoryOnly).Any(), "协议写入遗留临时文件。");
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
        var comparisonProgress = new List<CompareProgressUpdate>();
        var validation = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [cloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            validationPath,
            ProgressCallback: comparisonProgress.Add));
        Assert(comparisonProgress.Count == 2
            && comparisonProgress[0].CompletedCapabilities == 0
            && comparisonProgress[0].TotalCapabilities == 1
            && comparisonProgress[0].Progress == 0
            && comparisonProgress[1].CompletedCapabilities == 1
            && comparisonProgress[1].TotalCapabilities == 1
            && comparisonProgress[1].Progress == 100
            && comparisonProgress[1].CapabilityId == "win.process.create"
            && comparisonProgress[1].ValidationStatus == "PASS",
            "离线比较应在开始和每项能力完成后报告“已完成/总数×100%”的真实进度。");
        Assert(new CompareProgressUpdate(1, 3, null, null, null).Progress == 33.3
            && new CompareProgressUpdate(2, 3, null, null, null).Progress == 66.7,
            "离线比较进度百分比应保留一位小数并按能力数计算。");
        Assert(validation["summary"]?["pass"]?.GetValue<int>() == 1, $"合成云端事件应通过比较：{validation.ToJsonString(JsonDefaults.Options)}");
        Assert(validation["summary"]?["fail"]?.GetValue<int>() == 0, "不应出现 FAIL。");
        Assert(validation["schema_version"]?.GetValue<string>() == "1.1", "验证结果 Schema 应为 1.1。");
        Assert(validation["conclusion"]?["verdict"]?.GetValue<string>() == "PASS", "单项能力通过时总体结论应为 PASS。");
        Assert(validation["conclusion"]?["pass_rate"]?.GetValue<double>() == 1, "单项能力通过率应为 100%。");
        var requirements = validation["capabilities"]?[0]?["baseline_requirements"]?.AsArray() ?? throw new InvalidOperationException("结果应包含 BASELINE 要求。");
        Assert(requirements.Count == 14, "进程创建应展示 5 项本地要求、事件数量与时间差要求，以及 7 项云端字段要求。");
        var timeRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "event.time_difference_ms");
        Assert(timeRequirement?["status"]?.GetValue<string>() == "passed"
            && timeRequirement["expected"]?["max"]?.GetValue<int>() == 15
            && timeRequirement["actual"]?.GetValue<long>() <= 15,
            "15 ms 时间差必须作为显式的必需 EDR 关联条件并保留实际毫秒差。");
        Assert(requirements.Where(value => value?["severity"]?.GetValue<string>() == "required").All(value => value?["status"]?.GetValue<string>() == "passed"), "所有必需 BASELINE 要求都应通过。");
        var firstCandidate = validation["capabilities"]?[0]?["edr_candidates"]?.AsArray().Single()
            ?? throw new InvalidOperationException("结果应包含完整 EDR 候选日志。");
        Assert(firstCandidate["rank"]?.GetValue<int>() == 1 && firstCandidate["raw_event"]?["@table"]?.GetValue<string>() == "ProcEvents", "EDR 候选应保留排名和原始完整日志。");
        Assert(Math.Abs(firstCandidate["time_offset_ms"]?.GetValue<long>() ?? long.MaxValue) <= 1
            && firstCandidate["local_event_time_utc"]?.GetValue<string>() == local["local_events"]?[0]?["occurred_at_utc"]?.GetValue<string>(),
            "候选应以本地行为时间为零点保存有符号时间偏移和本地基准时间；Unix 毫秒映射允许 1 ms 量化误差。");
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
        var timeMatch = firstCandidate["baseline_matches"]?.AsArray()
            .Single(value => value?["canonical_field"]?.GetValue<string>() == "event.created");
        Assert(timeMatch?["local_json_pointer"]?.GetValue<string>() == "/local_events/0/occurred_at_utc"
            && timeMatch["raw_json_pointer"]?.GetValue<string>() == "/Common.EventTime",
            "时间差证据必须同时指向本地与 EDR 原始 JSON 的实际时间戳。");
        var actorPidRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "programs.actor.pid");
        Assert(actorPidRequirement?["operator"]?.GetValue<string>() == "present" && actorPidRequirement["expected"] is null && actorPidRequirement["actual"]?.GetValue<int>() > 0, "PID 存在性规则应在 actual 中保留本地 PID，expected 为空表示没有固定 PID。");
        var actorCommandRequirement = requirements.Single(value => value?["field"]?.GetValue<string>() == "programs.actor.command_line");
        var commandMarker = actorCommandRequirement?["expected"]?.GetValue<string>() ?? string.Empty;
        Assert(commandMarker.Length == 32 && actorCommandRequirement?["actual"]?.GetValue<string>().Contains(commandMarker, StringComparison.Ordinal) == true, "命令行 contains 规则的 expected 应是本轮测试标记，而不是文件哈希。");
        Assert(File.Exists(validationPath), "应写出 validation-result.json。");
        var conclusionPath = ConclusionExportService.DefaultOutputPath(validationPath);
        Assert(File.Exists(conclusionPath), "应同时写出中文 Markdown 结论。");
        Assert(File.ReadAllText(conclusionPath).Contains("全部能力满足验证基准", StringComparison.Ordinal), "Markdown 应包含中文总体结论。");

        var customTimeResultPath = Path.Combine(fixture.Path, "custom-time-result.json");
        var timeFilteredCloud = CreateCloudExport(local);
        var outsideCandidateLimit = timeFilteredCloud[0]!.DeepClone().AsObject();
        outsideCandidateLimit["Common.EventUUId"] = Ids.NewUuid7();
        outsideCandidateLimit["Common.EventTime"] = outsideCandidateLimit["Common.EventTime"]!.GetValue<long>() + 1_500;
        timeFilteredCloud.Add(outsideCandidateLimit);
        var timeFilteredCloudPath = Path.Combine(fixture.Path, "time-filtered-cloud.json");
        File.WriteAllText(timeFilteredCloudPath, timeFilteredCloud.ToJsonString(JsonDefaults.Options));
        var customTime = CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [timeFilteredCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            customTimeResultPath,
            StrongCorrelationTimeMs: 20,
            CandidateTimeLimitMs: 1_000));
        var customTimeCandidates = customTime["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("自定义时间参数结果缺少候选事件。");
        var customTimeRequirement = customTime["capabilities"]?[0]?["baseline_requirements"]?.AsArray()
            .Single(value => value?["field"]?.GetValue<string>() == "event.time_difference_ms");
        Assert(customTimeCandidates.Count == 1,
            "超出无关联候选事件时间上限的 EDR 记录必须在锚点评分前被裁剪。");
        Assert(customTimeRequirement?["expected"]?["max"]?.GetValue<int>() == 20
            && customTime["inputs"]?["strong_correlation_time_ms"]?.GetValue<int>() == 20
            && customTime["inputs"]?["candidate_time_limit_ms"]?.GetValue<int>() == 1_000,
            "自定义强关联时间和候选时间上限必须覆盖本轮判断并写入结果。");
        var customTimeConclusion = File.ReadAllText(ConclusionExportService.DefaultOutputPath(customTimeResultPath));
        Assert(customTimeConclusion.Contains("强关联时间：`20 ms`", StringComparison.Ordinal)
            && customTimeConclusion.Contains("无关联候选事件时间上限：`1000 ms`", StringComparison.Ordinal),
            "中文结论必须记录本轮使用的两项时间参数。");
        AssertThrows<ArgumentException>(() => CompareService.Compare(new CompareRequest(
            result.LocalExportPath,
            [timeFilteredCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            [Path.Combine(repository, "baselines", "windows", "process_create.yaml")],
            Path.Combine(fixture.Path, "invalid-time-result.json"),
            StrongCorrelationTimeMs: 1_001,
            CandidateTimeLimitMs: 1_000)));

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
        lowerConfidenceCandidate["Common.EventTime"] = multipleCloud[0]!["Common.EventTime"]!.GetValue<long>() - 2;
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
        var originalOffset = rankedCandidates[0]?["time_offset_ms"]?.GetValue<long>() ?? long.MaxValue;
        Assert(rankedCandidates[1]?["time_offset_ms"]?.GetValue<long>() == originalOffset + 8
            && rankedCandidates[2]?["time_offset_ms"]?.GetValue<long>() == originalOffset - 2
            && rankedCandidates[1]?["time_offset_ms"]?.GetValue<long>() > 0
            && rankedCandidates[2]?["time_offset_ms"]?.GetValue<long>() < 0,
            "有符号时间偏移必须以 EDR 时间减本地时间计算：正数表示延后，负数表示提前，并保留 Unix 毫秒量化偏移。");
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

    private static Task TestUserAccountComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject
                {
                    ["hostname"] = "ACCOUNT-FIXTURE",
                    ["machine_id"] = "account-fixture-host",
                },
            },
            ["capabilities"] = new JsonArray(),
            ["programs"] = new JsonArray(),
            ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(),
            ["artifacts"] = new JsonArray(),
            ["cleanup_results"] = new JsonArray(),
            ["execution_logs"] = new JsonArray(),
        };
        var genericCloud = new JsonArray();
        var tencentCloud = new JsonArray();
        var definitions = new[]
        {
            (Capability: "win.account.local.create", Action: "local_create", Baseline: "account_local_create.yaml", EventId: 4720),
            (Capability: "win.account.local.modify", Action: "local_modify", Baseline: "account_local_modify.yaml", EventId: 4738),
            (Capability: "win.account.local.delete", Action: "local_delete", Baseline: "account_local_delete.yaml", EventId: 4726),
            (Capability: "win.account.login", Action: "login", Baseline: "account_login.yaml", EventId: 4624),
            (Capability: "win.account.logoff", Action: "logoff", Baseline: "account_logoff.yaml", EventId: 4634),
        };
        var baselinePaths = new List<string>();
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var eventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var accountName = $"edrtfixture{index:00}";
            var accountSid = $"S-1-5-21-100-200-300-{1100 + index}";
            var logonId = $"0x{5000 + index:X}";
            var actorPid = 7000 + index;
            var actorPath = $@"C:\EDR-Test\Account{index}.Actor.exe";
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["capability_id"] = definition.Capability,
                ["capability_version"] = "0.1.0",
                ["display_name_zh"] = definition.Action,
                ["display_name_en"] = definition.Action,
                ["status"] = "LOCAL_PASS",
                ["nonce"] = $"account-fixture-{index}",
                ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(1)),
            });
            local["programs"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["program_instance_id"] = Ids.NewUuid7(),
                ["role"] = "actor",
                ["pid"] = actorPid,
                ["executable"] = actorPath,
                ["command_line"] = $"{actorPath} --request fixture.json",
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = eventId,
                ["case_run_id"] = caseRunId,
                ["sequence"] = 1,
                ["event_type"] = "account",
                ["event_action"] = definition.Action,
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject
                {
                    ["kind"] = "account",
                    ["operation"] = definition.Action,
                },
            });

            var facts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [$"account.{definition.Action}_succeeded"] = true,
                ["account.occurred_at_utc"] = Values.Utc(occurredAt),
                ["account.name"] = accountName,
                ["account.sid"] = accountSid,
                ["account.domain"] = "ACCOUNT-FIXTURE",
                ["account.account_type"] = "local_user",
                ["account.actor_pid"] = actorPid,
                ["account.actor_executable"] = actorPath,
                ["account.before.exists"] = definition.Action != "local_create",
                ["account.after.exists"] = definition.Action != "local_delete",
                ["account.before.comment"] = definition.Action == "local_modify" ? "EDR Test setup" : null,
                ["account.after.comment"] = definition.Action == "local_modify" ? "EDR Test modified" : null,
                ["account.changed_field"] = definition.Action == "local_modify" ? "comment" : null,
                ["account.session.logon_id"] = definition.Action is "login" or "logoff" ? logonId : null,
                ["account.session.logon_type"] = definition.Action is "login" or "logoff" ? 3 : null,
                ["account.session.authentication_package"] = definition.Action is "login" or "logoff" ? "Negotiate" : null,
                ["account.session.token_validated"] = definition.Action is "login" or "logoff" ? true : null,
            };
            foreach (var (key, value) in facts)
            {
                local["local_facts"]!.AsArray().Add(new JsonObject
                {
                    ["local_fact_id"] = Ids.NewUuid7(),
                    ["case_run_id"] = caseRunId,
                    ["local_event_id"] = eventId,
                    ["key"] = key,
                    ["value"] = value?.DeepClone(),
                });
            }

            genericCloud.Add(new JsonObject
            {
                ["table"] = "UserAccountActivity",
                ["event_id"] = eventId,
                ["host_id"] = "account-fixture-host",
                ["host_name"] = "ACCOUNT-FIXTURE",
                ["event_time"] = Values.Utc(occurredAt),
                ["action"] = definition.Action,
                ["actor_pid"] = actorPid,
                ["actor_name"] = Path.GetFileName(actorPath),
                ["actor_executable"] = actorPath,
                ["actor_command_line"] = $"{actorPath} --request fixture.json",
                ["target_user_name"] = accountName,
                ["target_domain_name"] = "ACCOUNT-FIXTURE",
                ["target_user_sid"] = accountSid,
                ["event_log_id"] = definition.EventId,
                ["logon_id"] = definition.Action is "login" or "logoff" ? logonId : null,
                ["logon_type"] = definition.Action is "login" or "logoff" ? 3 : null,
                ["authentication_package"] = definition.Action is "login" or "logoff" ? "Negotiate" : null,
            });
            var tencent = new JsonObject
            {
                ["OS"] = "Windows",
                ["@table"] = definition.Action is "login" or "logoff" ? "LoginEvents" : "AccountEvents",
                ["@timestamp"] = Values.Utc(occurredAt),
                ["Action.Type"] = "WinEventLog",
                ["Action.Name"] = "FixtureAccountEvent",
                ["Action.EventLogId"] = definition.EventId,
                ["Common.EventUUId"] = eventId,
                ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(),
                ["Common.Mid"] = "account-fixture-host",
                ["Environment.HostName"] = "ACCOUNT-FIXTURE",
                ["Parent.ProcPid"] = actorPid,
                ["Parent.FileName"] = definition.Action == "local_create" ? "lsass.exe" : Path.GetFileName(actorPath),
                ["Parent.FilePath"] = definition.Action == "local_create" ? @"C:\Windows\System32\lsass.exe" : actorPath,
                ["Parent.ProcCmdline"] = definition.Action == "local_create" ? @"C:\Windows\System32\lsass.exe" : $"{actorPath} --request fixture.json",
                ["Child.TargetUserName"] = accountName,
                ["Child.TargetDomainName"] = "ACCOUNT-FIXTURE",
                ["Child.TargetLogonId"] = definition.Action is "login" or "logoff" ? logonId : null,
                ["Child.LogonType"] = definition.Action is "login" or "logoff" ? "网络" : null,
                ["Child.AuthenticationPackageName"] = definition.Action is "login" or "logoff" ? "Negotiate" : null,
            };
            tencent[definition.Action is "login" or "logoff" ? "Child.TargetUserSid" : "Child.TargetSid"] = accountSid;
            tencentCloud.Add(tencent);
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", definition.Baseline));
        }

        var localPath = Path.Combine(fixture.Path, "account-local.json");
        var genericCloudPath = Path.Combine(fixture.Path, "account-generic-cloud.json");
        var tencentCloudPath = Path.Combine(fixture.Path, "account-tencent-cloud.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericCloudPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentCloudPath, tencentCloud.ToJsonString(JsonDefaults.Options));

        var generic = CompareService.Compare(new CompareRequest(
            localPath,
            [genericCloudPath],
            Path.Combine(repository, "mappings", "generic-user-account-activity-v1.yaml"),
            baselinePaths,
            Path.Combine(fixture.Path, "account-generic-validation.json")));
        var tencentResult = CompareService.Compare(new CompareRequest(
            localPath,
            [tencentCloudPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"),
            baselinePaths,
            Path.Combine(fixture.Path, "account-tencent-validation.json")));

        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 5,
            $"通用账号映射应使五项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencentResult["summary"]?["pass"]?.GetValue<int>() == 5,
            $"腾讯账号事件 ID 路由应使五项 BASELINE 全部通过：{tencentResult.ToJsonString(JsonDefaults.Options)}");
        var genericCreate = generic["capabilities"]?.AsArray().Single(value => value?["capability_id"]?.GetValue<string>() == "win.account.local.create")?.AsObject()
            ?? throw new InvalidOperationException("通用比较结果缺少本地账号创建能力。");
        var tencentCreate = tencentResult["capabilities"]?.AsArray().Single(value => value?["capability_id"]?.GetValue<string>() == "win.account.local.create")?.AsObject()
            ?? throw new InvalidOperationException("腾讯比较结果缺少本地账号创建能力。");
        var lsassRequirement = tencentCreate["baseline_requirements"]?.AsArray().Single(value => value?["field"]?.GetValue<string>() == "process.executable")
            ?? throw new InvalidOperationException("本地账号创建结果缺少 process.executable BASELINE 项。");
        Assert(lsassRequirement["status"]?.GetValue<string>() == "passed"
            && string.Equals(lsassRequirement["actual"]?.GetValue<string>(), @"C:\Windows\System32\lsass.exe", StringComparison.OrdinalIgnoreCase)
            && lsassRequirement["expected"]?.AsArray().Any(value => string.Equals(value?.GetValue<string>(), @"C:\Windows\System32\lsass.exe", StringComparison.OrdinalIgnoreCase)) == true,
            "本地账号创建的 EDR process.executable 应同时接受 Actor 与 lsass.exe。");
        Assert(lsassRequirement["message"]?.GetValue<string>() == "缺少上级调用链，需要优化",
            "命中 lsass.exe 时应仅在逐项比较结果中给出上级调用链优化提示。");
        Assert(tencentCreate["warnings"]?.AsArray().Any(value => value?.GetValue<string>().Contains("缺少上级调用链", StringComparison.Ordinal) == true) == false,
            "lsass.exe 优化提示不应进入影响结论导出的能力 warnings。");
        Assert(genericCreate["edr_candidates"]?[0]?["correlation_score"]?.GetValue<double>()
            == tencentCreate["edr_candidates"]?[0]?["correlation_score"]?.GetValue<double>(),
            "Actor 与 lsass.exe 两种 process.executable 通过路径不应改变候选关联得分。");
        Assert(tencentCreate["edr_candidates"]?[0]?["baseline_matches"]?.AsArray().Any(match =>
            match?["canonical_field"]?.GetValue<string>() == "process.executable"
            && match?["status"]?.GetValue<string>() == "passed"
            && match?["message"]?.GetValue<string>() == "缺少上级调用链，需要优化") == true,
            "JSON 对照块应将 lsass.exe 高亮为 BASELINE 一致，并保留优化提示。");
        Assert(tencentResult["capabilities"]?.AsArray().All(value =>
            value?["edr_candidates"]?.AsArray()[0]?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "user.target.id"
                && match?["raw_json_pointer"]?.GetValue<string>() is "/Child.TargetSid" or "/Child.TargetUserSid") == true) == true,
            "五项腾讯候选都应把本地 SID 映射回正确的 EDR 原始目标 SID 字段。");
        return Task.CompletedTask;
    }

    private static Task TestRegistryComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "REGISTRY-FIXTURE", ["machine_id"] = "registry-fixture-host" },
            },
            ["capabilities"] = new JsonArray(), ["programs"] = new JsonArray(), ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["cleanup_results"] = new JsonArray(),
            ["execution_logs"] = new JsonArray(),
        };
        var genericCloud = new JsonArray();
        var tencentCloud = new JsonArray();
        var baselinePaths = new List<string>();
        var definitions = new[]
        {
            (Capability: "win.registry.create", Action: "create", Baseline: "registry_create.yaml", TencentAction: "RegSetValue"),
            (Capability: "win.registry.modify", Action: "modify", Baseline: "registry_modify.yaml", TencentAction: "RegSetValue"),
            (Capability: "win.registry.delete", Action: "delete", Baseline: "registry_delete.yaml", TencentAction: "RegDeleteValueW"),
        };
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var eventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var keyPath = $@"HKEY_CURRENT_USER\Software\EdrTest\Runs\fixture{index}\{definition.Action}";
            var valueName = "EdrTestValue";
            var beforeData = definition.Action == "create" ? null : $"EDRTEST|fixture-{index}|REGISTRY_BEFORE";
            var afterData = definition.Action == "delete" ? null : $"EDRTEST|fixture-{index}|REGISTRY_{definition.Action.ToUpperInvariant()}";
            var actorPid = 8100 + index;
            var actorPath = $@"C:\EDR-Test\Registry{definition.Action}.Actor.exe";
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = definition.Capability, ["capability_version"] = "0.2.0",
                ["display_name_zh"] = definition.Action, ["display_name_en"] = definition.Action, ["status"] = "LOCAL_PASS",
                ["nonce"] = $"registry-fixture-{index}", ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(1)),
            });
            local["programs"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["program_instance_id"] = Ids.NewUuid7(), ["role"] = "actor",
                ["pid"] = actorPid, ["executable"] = actorPath, ["command_line"] = $"{actorPath} --operation {definition.Action}",
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = eventId, ["case_run_id"] = caseRunId, ["sequence"] = 1,
                ["event_type"] = "registry", ["event_action"] = definition.Action, ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject { ["kind"] = "registry", ["operation"] = definition.Action },
            });
            var facts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [$"registry.{definition.Action}_succeeded"] = true,
            };
            foreach (var method in new[] { "isolated_key", "run_key_native" })
            {
                var prefix = $"registry.{method}";
                facts[$"{prefix}.{definition.Action}_succeeded"] = true;
                facts[$"{prefix}.occurred_at_utc"] = Values.Utc(occurredAt);
                facts[$"{prefix}.hive"] = "HKCU";
                facts[$"{prefix}.key_path"] = keyPath;
                facts[$"{prefix}.value_name"] = valueName;
                facts[$"{prefix}.view"] = "default";
                facts[$"{prefix}.actor_pid"] = actorPid;
                facts[$"{prefix}.actor_executable"] = actorPath;
                facts[$"{prefix}.before.key_exists"] = definition.Action != "create";
                facts[$"{prefix}.before.value_exists"] = definition.Action != "create";
                facts[$"{prefix}.before.value_kind"] = definition.Action == "create" ? null : "String";
                facts[$"{prefix}.before.value_data"] = beforeData;
                facts[$"{prefix}.before.value_data_sha256"] = beforeData is null ? null : new string('a', 64);
                facts[$"{prefix}.after.key_exists"] = definition.Action != "delete";
                facts[$"{prefix}.after.value_exists"] = definition.Action != "delete";
                facts[$"{prefix}.after.value_kind"] = definition.Action == "delete" ? null : "String";
                facts[$"{prefix}.after.value_data"] = afterData;
                facts[$"{prefix}.after.value_data_sha256"] = afterData is null ? null : new string('b', 64);
            }
            foreach (var (key, value) in facts)
            {
                local["local_facts"]!.AsArray().Add(new JsonObject
                {
                    ["local_fact_id"] = Ids.NewUuid7(), ["case_run_id"] = caseRunId, ["local_event_id"] = eventId,
                    ["key"] = key, ["value"] = value?.DeepClone(),
                });
            }

            var cloudValue = definition.Action == "delete" ? beforeData : afterData;
            genericCloud.Add(new JsonObject
            {
                ["table"] = "RegistryActivity", ["event_id"] = eventId, ["host_id"] = "registry-fixture-host",
                ["event_time"] = Values.Utc(occurredAt), ["action"] = definition.Action, ["actor_pid"] = actorPid,
                ["actor_entity_id"] = Ids.NewUuid7(), ["actor_name"] = Path.GetFileName(actorPath),
                ["actor_executable"] = actorPath, ["actor_command_line"] = $"{actorPath} --operation {definition.Action}",
                ["user_name"] = "fixture", ["user_domain"] = "REGISTRY-FIXTURE", ["registry_key"] = keyPath,
                ["registry_value_name"] = valueName, ["registry_value_data"] = cloudValue,
                ["registry_old_value_data"] = beforeData, ["registry_old_value_type"] = definition.Action == "create" ? 0 : "字符串",
                ["registry_value_type"] = "字符串", ["registry_group_name"] = "启动项",
            });
            tencentCloud.Add(new JsonObject
            {
                ["OS"] = "Windows", ["@table"] = "RegEvents", ["@timestamp"] = Values.Utc(occurredAt),
                ["Action.Type"] = "Reg", ["Action.Name"] = definition.TencentAction,
                ["Common.EventUUId"] = eventId, ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(),
                ["Common.Mid"] = "registry-fixture-host", ["Environment.HostName"] = "REGISTRY-FIXTURE",
                ["Parent.ProcPid"] = actorPid, ["Parent.FileName"] = Path.GetFileName(actorPath),
                ["Parent.FilePath"] = actorPath, ["Parent.ProcCmdline"] = $"{actorPath} --operation {definition.Action}",
                ["Parent.ProcUserName"] = "fixture", ["Parent.ProcDomainName"] = "REGISTRY-FIXTURE",
                ["Child.RegistryPath"] = definition.Action == "create"
                    ? keyPath.Replace("HKEY_CURRENT_USER", "HKEY_USERS\\S-1-5-21-111-222-333-1001", StringComparison.Ordinal)
                    : keyPath,
                ["Child.RegistryValueName"] = valueName, ["Child.RegValData"] = cloudValue,
                ["Child.RegOldValData"] = beforeData, ["Child.RegOldValType"] = definition.Action == "create" ? 0 : "字符串",
                ["Child.RegValType"] = "字符串", ["Child.RegGroupName"] = "启动项",
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", definition.Baseline));
        }

        var localPath = Path.Combine(fixture.Path, "registry-local.json");
        var genericPath = Path.Combine(fixture.Path, "registry-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "registry-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-registry-activity-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "registry-generic-validation.json")));
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "registry-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 3,
            $"通用注册表映射应使三项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 3,
            $"腾讯 RegEvents 路由应使三项 BASELINE 全部通过：{tencent.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["capabilities"]?.AsArray().All(capability =>
            capability?["edr_candidates"]?.AsArray()[0]?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "registry.key"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.RegistryPath"
                && match?["status"]?.GetValue<string>() == "passed") == true) == true,
            "腾讯注册表候选应记录实际命中的字段别名，并在 JSON 对照中高亮键路径。");
        return Task.CompletedTask;
    }

    private static Task TestScheduledTaskComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "TASK-FIXTURE", ["machine_id"] = "task-fixture-host" },
            },
            ["capabilities"] = new JsonArray(), ["programs"] = new JsonArray(), ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["cleanup_results"] = new JsonArray(),
            ["execution_logs"] = new JsonArray(),
        };
        var genericCloud = new JsonArray();
        var tencentCloud = new JsonArray();
        var baselinePaths = new List<string>();
        var definitions = new[]
        {
            (Operation: "create", EventId: 4698, ActionName: "SchedTaskCreate"),
            (Operation: "modify", EventId: 4702, ActionName: "SchedTaskUpdate"),
            (Operation: "delete", EventId: 4699, ActionName: "SchedTaskDelete"),
        };
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var capabilityTime = baseTime.AddSeconds(index * 10);
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = $"win.scheduled_task.{definition.Operation}",
                ["capability_version"] = "0.2.0", ["display_name_zh"] = definition.Operation,
                ["display_name_en"] = definition.Operation, ["status"] = "LOCAL_PASS",
                ["nonce"] = $"scheduled-task-fixture-{index}", ["started_at_utc"] = Values.Utc(capabilityTime.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(capabilityTime.AddSeconds(3)),
            });
            var methods = new[] { "task_scheduler_com", "schtasks_cli" };
            foreach (var (method, methodIndex) in methods.Select((value, index) => (value, index)))
            {
                var eventId = Ids.NewUuid7();
                var occurredAt = capabilityTime.AddSeconds(methodIndex);
                var methodSuffix = method == "schtasks_cli" ? "cli" : "com";
                var taskPath = $@"\EdrTest_fixture{index}_{definition.Operation}_{methodSuffix}";
                var marker = $"EDRTEST|scheduled-task-fixture-{index}|SCHEDULED_TASK|{method}|{definition.Operation.ToUpperInvariant()}";
                var beforeMarker = $"EDRTEST|scheduled-task-fixture-{index}|SCHEDULED_TASK|{method}|BEFORE";
                var actorPid = 9300 + index * 10 + methodIndex;
                var actorPath = method == "schtasks_cli"
                    ? @"C:\Windows\System32\schtasks.exe"
                    : $@"C:\EDR-Test\ScheduledTask{definition.Operation}.Actor.exe";
                var beforeExists = definition.Operation != "create";
                var afterExists = definition.Operation != "delete";
                var afterEnabled = method == "schtasks_cli" && definition.Operation is "create" or "modify";
                var prefix = $"scheduled_task.{method}";
                var afterArguments = $"/d /c rem EDRTEST_fixture{index}_{methodSuffix}";
                var beforeArguments = "/d /c exit 0";
                var cliStateOnlyModify = definition.Operation == "modify" && method == "schtasks_cli";
                var cloudArguments = definition.Operation == "delete" || cliStateOnlyModify ? beforeArguments : afterArguments;
                var cloudMarker = cliStateOnlyModify ? beforeMarker : marker;
                var taskContent = $"<Task><RegistrationInfo><Description>{cloudMarker}</Description></RegistrationInfo>"
                    + $"<Actions><Exec><Arguments>{cloudArguments}</Arguments></Exec></Actions></Task>";
                local["local_events"]!.AsArray().Add(new JsonObject
                {
                    ["local_event_id"] = eventId, ["case_run_id"] = caseRunId, ["sequence"] = methodIndex + 1,
                    ["event_type"] = "scheduled_task", ["event_action"] = definition.Operation,
                    ["occurred_at_utc"] = Values.Utc(occurredAt),
                    ["data"] = new JsonObject { ["kind"] = "scheduled_task", ["operation"] = definition.Operation, ["method"] = method },
                });
                var facts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    [$"{prefix}.{definition.Operation}_succeeded"] = true,
                    [$"{prefix}.occurred_at_utc"] = Values.Utc(occurredAt.AddMilliseconds(-1)),
                    [$"{prefix}.completed_at_utc"] = Values.Utc(occurredAt),
                    [$"{prefix}.task_path"] = taskPath, [$"{prefix}.marker"] = marker,
                    [$"{prefix}.actor_pid"] = actorPid, [$"{prefix}.actor_executable"] = actorPath,
                    [$"{prefix}.actor_command_line"] = $"{actorPath} --operation {definition.Operation}",
                    [$"{prefix}.before.exists"] = beforeExists,
                    [$"{prefix}.before.xml_sha256"] = beforeExists ? new string('a', 64) : null,
                    [$"{prefix}.before.principal"] = beforeExists ? "S-1-5-21-111-222-333-1001" : null,
                    [$"{prefix}.before.enabled"] = beforeExists ? false : null,
                    [$"{prefix}.before.marker"] = beforeExists ? beforeMarker : null,
                    [$"{prefix}.before.action_command"] = beforeExists ? @"C:\Windows\System32\cmd.exe" : null,
                    [$"{prefix}.before.action_arguments"] = beforeExists ? beforeArguments : null,
                    [$"{prefix}.after.exists"] = afterExists,
                    [$"{prefix}.after.xml_sha256"] = afterExists ? new string('b', 64) : null,
                    [$"{prefix}.after.principal"] = afterExists ? "S-1-5-21-111-222-333-1001" : null,
                    [$"{prefix}.after.enabled"] = afterExists ? afterEnabled : null,
                    [$"{prefix}.after.marker"] = afterExists ? cloudMarker : null,
                    [$"{prefix}.after.action_command"] = afterExists ? @"C:\Windows\System32\cmd.exe" : null,
                    [$"{prefix}.after.action_arguments"] = afterExists ? cloudArguments : null,
                };
                if (method == "schtasks_cli")
                {
                    facts[$"{prefix}.security_event_id"] = definition.EventId;
                    facts[$"{prefix}.security_event_found"] = false;
                    if (definition.Operation == "create")
                    {
                        facts[$"{prefix}.after.triggers"] = new JsonArray("TimeTrigger");
                        facts[$"{prefix}.security_event_4698_found"] = false;
                    }
                }
                foreach (var (key, value) in facts)
                {
                    local["local_facts"]!.AsArray().Add(new JsonObject
                    {
                        ["local_fact_id"] = Ids.NewUuid7(), ["case_run_id"] = caseRunId,
                        ["local_event_id"] = eventId, ["key"] = key, ["value"] = value?.DeepClone(),
                    });
                }
                genericCloud.Add(new JsonObject
                {
                    ["table"] = "ScheduledTaskActivity", ["event_id"] = eventId, ["host_id"] = "task-fixture-host",
                    ["host_name"] = "TASK-FIXTURE", ["event_time"] = Values.Utc(occurredAt),
                    ["event_type"] = definition.Operation is "create" or "modify" && method == "task_scheduler_com" ? "scheduled_task_rpc" : "scheduled_task",
                    ["action"] = definition.Operation is "create" or "modify" && method == "task_scheduler_com" ? "register" : definition.Operation,
                    ["actor_pid"] = actorPid, ["actor_name"] = Path.GetFileName(actorPath),
                    ["actor_executable"] = actorPath, ["actor_command_line"] = $"{actorPath} --operation {definition.Operation}",
                    ["subject_user_name"] = "fixture", ["subject_domain_name"] = "TASK-FIXTURE",
                    ["subject_user_sid"] = "S-1-5-21-111-222-333-1001", ["event_log_id"] = definition.EventId,
                    ["task_name"] = taskPath, ["task_content"] = definition.Operation == "delete" ? null : taskContent,
                    ["task_command"] = @"C:\Windows\System32\cmd.exe", ["task_arguments"] = cloudArguments,
                });
                if (definition.Operation is "create" or "modify" && method == "task_scheduler_com")
                {
                    tencentCloud.Add(new JsonObject
                    {
                        ["OS"] = "Windows", ["@table"] = "ServiceEvents", ["@timestamp"] = Values.Utc(occurredAt),
                        ["Action.Type"] = "InjectHook", ["Action.Name"] = "RpcSchedTaskCreate", ["Common.EventUUId"] = eventId,
                        ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(), ["Common.Mid"] = "task-fixture-host",
                        ["Environment.HostName"] = "TASK-FIXTURE", ["Parent.ProcPid"] = actorPid,
                        ["Parent.FileName"] = Path.GetFileName(actorPath), ["Parent.FilePath"] = actorPath,
                        ["Parent.ProcCmdline"] = actorPath, ["Child.TaskName"] = taskPath,
                        ["Child.NodeName"] = @"C:\Windows\System32\cmd.exe", ["Child.FilePath"] = @"C:\Windows\System32\cmd.exe",
                        ["Child.TaskArg"] = cloudArguments,
                    });
                }
                else
                {
                    var useServiceSideProcess = definition.Operation == "modify" && method == "schtasks_cli";
                    var tencentProcess = useServiceSideProcess ? @"C:\Windows\System32\svchost.exe" : actorPath;
                    var tencent = new JsonObject
                    {
                        ["OS"] = "Windows", ["@table"] = "ScheduleTaskEvents", ["@timestamp"] = Values.Utc(occurredAt),
                        ["Action.Type"] = "WinEventLog", ["Action.Name"] = definition.ActionName,
                        ["Action.EventLogId"] = definition.EventId, ["Common.EventUUId"] = eventId,
                        ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(), ["Common.Mid"] = "task-fixture-host",
                        ["Environment.HostName"] = "TASK-FIXTURE", ["Parent.ProcPid"] = useServiceSideProcess ? 64204 : actorPid,
                        ["Parent.FileName"] = Path.GetFileName(tencentProcess), ["Parent.FilePath"] = tencentProcess,
                        ["Parent.ProcCmdline"] = tencentProcess, ["Child.SubjectUserName"] = "fixture",
                        ["Child.SubjectDomainName"] = "TASK-FIXTURE", ["Child.SubjectUserSid"] = "S-1-5-21-111-222-333-1001",
                        ["Child.TaskName"] = taskPath, ["Child.NodeName"] = taskPath,
                    };
                    if (definition.Operation == "create") tencent["Child.TaskContent"] = taskContent;
                    if (definition.Operation == "modify") tencent["Child.TaskContentNew"] = taskContent;
                    tencentCloud.Add(tencent);
                }
            }
            local["local_facts"]!.AsArray().Add(new JsonObject
            {
                ["local_fact_id"] = Ids.NewUuid7(), ["case_run_id"] = caseRunId,
                ["key"] = $"scheduled_task.{definition.Operation}_succeeded", ["value"] = true,
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"scheduled_task_{definition.Operation}.yaml"));
        }

        var localPath = Path.Combine(fixture.Path, "scheduled-task-local.json");
        var genericPath = Path.Combine(fixture.Path, "scheduled-task-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "scheduled-task-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-scheduled-task-activity-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "scheduled-task-generic-validation.json")));
        var tencentResult = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "scheduled-task-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 3,
            $"通用计划任务映射应使三项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencentResult["summary"]?["pass"]?.GetValue<int>() == 3,
            $"腾讯 ScheduleTaskEvents 路由应使三项 BASELINE 全部通过：{tencentResult.ToJsonString(JsonDefaults.Options)}");
        var modify = tencentResult["capabilities"]?.AsArray().Single(value =>
            value?["capability_id"]?.GetValue<string>() == "win.scheduled_task.modify")?.AsObject()
            ?? throw new InvalidOperationException("腾讯比较结果缺少计划任务修改能力。");
        var processRequirement = modify["baseline_requirements"]?.AsArray().Single(value =>
            value?["expectation_id"]?.GetValue<string>() == "scheduled-task-modify-security-event"
            && value?["field"]?.GetValue<string>() == "process.executable")
            ?? throw new InvalidOperationException("计划任务修改结果缺少 process.executable BASELINE 项。");
        Assert(processRequirement["status"]?.GetValue<string>() == "passed"
            && processRequirement["message"]?.GetValue<string>()?.Contains("服务侧调用链", StringComparison.Ordinal) == true,
            "修改日志仅保留 svchost.exe 时应通过推荐项并提示补充 Task Scheduler 客户端调用链。");
        foreach (var operation in new[] { "create", "modify", "delete" })
        {
            var capability = tencentResult["capabilities"]?.AsArray().Single(value =>
                value?["capability_id"]?.GetValue<string>() == $"win.scheduled_task.{operation}")?.AsObject()
                ?? throw new InvalidOperationException($"腾讯比较结果缺少计划任务 {operation} 能力。");
            Assert(capability["method_results"]?.AsArray().Count == 2
                && capability["method_results"]?.AsArray().Any(value => value?["method_id"]?.GetValue<string>() == "task_scheduler_com") == true
                && capability["method_results"]?.AsArray().Any(value => value?["method_id"]?.GetValue<string>() == "schtasks_cli") == true
                && capability["method_results"]?.AsArray().All(value => value?["status"]?.GetValue<string>() == "PASS") == true,
                $"计划任务 {operation} 必须分别输出两个通过的方法结果。");
        }
        var create = tencentResult["capabilities"]?.AsArray().Single(value =>
            value?["capability_id"]?.GetValue<string>() == "win.scheduled_task.create")?.AsObject()
            ?? throw new InvalidOperationException("腾讯比较结果缺少计划任务创建能力。");
        Assert(create["edr_candidates"]?.AsArray().Any(candidate =>
            candidate?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "scheduled_task.content"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.TaskContent") == true) == true,
            "4698 方法必须把 Child.TaskContent 映射回 JSON 对照高亮。");
        return Task.CompletedTask;
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
              max_time_difference_ms: 15
              anchors:
                - { local_field: programs.target.executable, cloud_field: process.executable, strength: strong, normalizers: [windows_path] }
            method_selection: { strategy: best }
            cloud_expectations:
              - id: first-event
                method: { id: first, title: 第一种加载方法 }
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
                method: { id: second, title: 第二种加载方法 }
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
        Assert(candidates.Count == 2, "默认 1 秒候选上限应让每个子项只保留自身时间附近的记录。");
        var eligibleCandidates = candidates.Where(value => value?["eligible_for_validation"]?.GetValue<bool>() == true).ToArray();
        Assert(eligibleCandidates.Length == 2
            && eligibleCandidates.Any(value => value?["expectation_id"]?.GetValue<string>() == "first-event" && value?["event_id"]?.GetValue<string>() == "first-event")
            && eligibleCandidates.Any(value => value?["expectation_id"]?.GetValue<string>() == "second-event" && value?["event_id"]?.GetValue<string>() == "second-event"),
            "每个子项只能让命中自身本地路径的事件进入自动判定。");
        Assert(candidates.All(value => value?["eligible_for_validation"]?.GetValue<bool>() == true),
            "相隔 10 秒的其他子项事件应在锚点评分前被候选时间上限排除。");
        Assert(eligibleCandidates.All(value => value?["time_distance_ms"]?.GetValue<long>() == 0), "子项应使用自己的本地发生时间计算强匹配候选的距离。");
        var allMethodResults = validation["capabilities"]?[0]?["method_results"]?.AsArray()
            ?? throw new InvalidOperationException("多方法结果缺少 method_results。");
        Assert(allMethodResults.Count == 2
            && allMethodResults.All(value => value?["status"]?.GetValue<string>() == "PASS")
            && allMethodResults.Count(value => value?["selected_for_conclusion"]?.GetValue<bool>() == true) == 1,
            "两种加载方法都应独立展示通过状态，且只能选择一种形成能力结论。");
        Assert(allMethodResults.All(value => value?["passed_requirement_count"]?.GetValue<int>() == 5
            && value?["requirement_count"]?.GetValue<int>() == 5),
            "方法统计应包含 2 条本地要求与各方法自身的 3 条 EDR 要求，不能只统计 EDR 要求或候选日志数。");

        var secondOnlyCloudPath = Path.Combine(fixture.Path, "second-only-cloud.json");
        File.WriteAllText(secondOnlyCloudPath, new JsonArray(
            CloudImage("second-event", secondTime, secondPath, "edrtest_nonce_version.dll", targetPath)).ToJsonString(JsonDefaults.Options));
        var bestMethodResultPath = Path.Combine(fixture.Path, "best-method-result.json");
        var bestMethodValidation = CompareService.Compare(new CompareRequest(
            localPath,
            [secondOnlyCloudPath],
            Path.Combine(repository, "mappings", "generic-process-activity-v1.yaml"),
            [baselinePath],
            bestMethodResultPath));
        var bestMethodCapability = bestMethodValidation["capabilities"]?[0]?.AsObject()
            ?? throw new InvalidOperationException("最佳方法比较缺少能力结果。");
        var bestMethodSelection = bestMethodCapability["method_selection"]?.AsObject()
            ?? throw new InvalidOperationException("最佳方法比较缺少 method_selection。");
        Assert(bestMethodCapability["validation_status"]?.GetValue<string>() == "PASS"
            && bestMethodSelection["selected_method_id"]?.GetValue<string>() == "second"
            && bestMethodSelection["selected_method_status"]?.GetValue<string>() == "PASS",
            "只有第二种方法检出时，应采用通过情况最好的第二种方法形成 PASS 结论。");
        Assert(bestMethodCapability["method_results"]?.AsArray().Single(value => value?["method_id"]?.GetValue<string>() == "first")?["status"]?.GetValue<string>() == "INCONCLUSIVE"
            && bestMethodCapability["method_results"]?.AsArray().Single(value => value?["method_id"]?.GetValue<string>() == "second")?["selected_for_conclusion"]?.GetValue<bool>() == true,
            "第一种方法在导出范围不足且没有 1 秒内候选时应显示无法判定，第二种方法应标记为结论采用。");
        Assert(bestMethodSelection["notice"]?.GetValue<string>().Contains("结果最好的“第二种加载方法”", StringComparison.Ordinal) == true
            && File.ReadAllText(ConclusionExportService.DefaultOutputPath(bestMethodResultPath)).Contains("第二种加载方法", StringComparison.Ordinal),
            "结构化结果和中文结论都应提示采用了哪一种最佳方法。");

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

    private static Task TestFileRawFieldFilters()
    {
        using var fixture = TestDirectory.Create();
        var caseRunId = Ids.NewUuid7();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var occurred = started.AddSeconds(10);
        const string actorPath = @"C:\samples\FileCreate.Actor.exe";
        const string filePath = @"C:\runs\edrtest-file.json";
        var local = new JsonObject
        {
            ["run"] = new JsonObject { ["host"] = new JsonObject { ["hostname"] = "fixture-host" } },
            ["capabilities"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["capability_id"] = "win.file.create",
                ["capability_version"] = "0.1.0",
                ["status"] = "LOCAL_PASS",
                ["nonce"] = "fixture-file-nonce",
                ["started_at_utc"] = Values.Utc(started),
                ["ended_at_utc"] = Values.Utc(started.AddSeconds(30)),
            }),
            ["programs"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["role"] = "actor",
                ["pid"] = 5151,
                ["executable"] = actorPath,
            }),
            ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(
                Fact(caseRunId, "file.test.succeeded", true),
                Fact(caseRunId, "file.test.path", filePath),
                Fact(caseRunId, "file.test.occurred_at_utc", Values.Utc(occurred))),
        };
        var localPath = Path.Combine(fixture.Path, "file-local.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));

        var cloud = new JsonArray(
            CloudFileEvent("open-event", occurred, filePath, actorPath, "打开文件"),
            CloudFileEvent("create-event", occurred.AddMilliseconds(5), filePath, actorPath, "新建文件"));
        var cloudPath = Path.Combine(fixture.Path, "file-cloud.json");
        File.WriteAllText(cloudPath, cloud.ToJsonString(JsonDefaults.Options));
        var baselinePath = Path.Combine(fixture.Path, "file-baseline.yaml");
        File.WriteAllText(baselinePath, """
            schema_version: "1.1"
            baseline_id: win.file.create
            version: "0.1.0"
            title: 文件创建原始字段筛选测试
            risk_level: L0
            capability: { id: win.file.create, version: "0.1.0" }
            local_requirements:
              - { field: facts.file.test.succeeded, operator: equals, expected: true, severity: required }
            correlation:
              time_before_seconds: 60
              time_after_seconds: 60
              max_time_difference_ms: 15
              anchors:
                - { local_field: facts.file.test.path, cloud_field: file.path, strength: strong, normalizers: [windows_path] }
                - { local_field: programs.actor.executable, cloud_field: process.executable, strength: strong, normalizers: [windows_path] }
            cloud_expectations:
              - id: file-create
                event_type: file
                event_actions: [create]
                cardinality: { min: 1, max: 1 }
                correlation:
                  time_from_local: facts.file.test.occurred_at_utc
                  anchors:
                    - { local_field: facts.file.test.path, cloud_field: file.path, strength: strong, normalizers: [windows_path] }
                    - { local_field: programs.actor.executable, cloud_field: process.executable, strength: strong, normalizers: [windows_path] }
                assertions:
                  - { field: file.path, operator: equals, expected_from_local: facts.file.test.path, severity: required, normalizers: [windows_path] }
            """);
        var repository = FindRepositoryRoot();
        var mappingPath = Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml");

        var unfiltered = CompareService.Compare(new CompareRequest(
            localPath,
            [cloudPath],
            mappingPath,
            [baselinePath],
            Path.Combine(fixture.Path, "file-unfiltered.json")));
        var unfilteredCandidates = unfiltered["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("留空筛选结果缺少 EDR 候选。");
        Assert(unfilteredCandidates.Count(value => value?["eligible_for_validation"]?.GetValue<bool>() == true) == 2
            && unfilteredCandidates.All(value => value?["custom_child_file_create_op_name_matched"] is null),
            "Child.FileCreateOpName 留空时不得改变原有候选资格或额外筛选记录。");

        var filteredResultPath = Path.Combine(fixture.Path, "file-filtered.json");
        var filtered = CompareService.Compare(new CompareRequest(
            localPath,
            [cloudPath],
            mappingPath,
            [baselinePath],
            filteredResultPath,
            ActionNameStandards: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["win.file.create"] = ["FileWriteClose"],
            },
            ChildFileCreateOpNameStandards: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["win.file.create"] = ["不存在", "新建文件"],
            }));
        var filteredCapability = filtered["capabilities"]?[0]?.AsObject()
            ?? throw new InvalidOperationException("文件字段筛选结果缺少能力。");
        var filteredCandidates = filteredCapability["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("文件字段筛选结果缺少候选。");
        Assert(filteredCapability["validation_status"]?.GetValue<string>() == "PASS"
            && filteredCandidates.Count(value => value?["eligible_for_validation"]?.GetValue<bool>() == true) == 1
            && filteredCandidates[0]?["raw_event"]?["Child.FileCreateOpName"]?.GetValue<string>() == "新建文件",
            "Action.Name 与 Child.FileCreateOpName 必须在同一强候选上共同通过，并支持多值任选其一。");
        Assert(filteredCandidates[0]?["baseline_matches"]?.AsArray().Any(value => value?["kind"]?.GetValue<string>() == "custom_filter"
            && value?["raw_field"]?.GetValue<string>() == "Child.FileCreateOpName"
            && value?["raw_json_pointer"]?.GetValue<string>() == "/Child.FileCreateOpName"
            && value?["status"]?.GetValue<string>() == "passed") == true,
            "Child.FileCreateOpName 命中应进入候选原始 JSON 高亮信息。");
        Assert(filteredCapability["local_status"]?.GetValue<string>() == "LOCAL_PASS"
            && filteredCapability["baseline_requirements"]?.AsArray()
                .Where(value => value?["scope"]?.GetValue<string>() == "local")
                .All(value => value?["status"]?.GetValue<string>() == "passed") == true,
            "文件原始字段筛选不得改变 LOCAL_PASS 或任何本地要求。");
        Assert(filtered["inputs"]?["child_file_create_op_name_standards"]?["win.file.create"]?[1]?.GetValue<string>() == "新建文件"
            && File.ReadAllText(ConclusionExportService.DefaultOutputPath(filteredResultPath)).Contains("Child.FileCreateOpName", StringComparison.Ordinal),
            "结构化结果和中文结论都必须记录文件字段标准。");

        AssertThrows<ArgumentException>(() => CompareService.Compare(new CompareRequest(
            localPath,
            [cloudPath],
            mappingPath,
            [baselinePath],
            Path.Combine(fixture.Path, "invalid-file-filter.json"),
            ChildFileCreateOpNameStandards: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["win.process.create"] = ["新建文件"],
            })));
        return Task.CompletedTask;
    }

    private static JsonObject Fact(string caseRunId, string key, object value) => new()
    {
        ["case_run_id"] = caseRunId,
        ["key"] = key,
        ["value"] = JsonValue.Create(value),
    };

    private static JsonObject CloudFileEvent(string eventId, DateTimeOffset occurred, string filePath, string actorPath, string operationName) => new()
    {
        ["OS"] = "Windows",
        ["@table"] = "FileEvents",
        ["@timestamp"] = Values.Utc(occurred),
        ["Action.Type"] = "File",
        ["Action.Name"] = "FileWriteClose",
        ["Child.FileCreateOpName"] = operationName,
        ["Common.EventUUId"] = eventId,
        ["Common.EventTime"] = occurred.ToUnixTimeMilliseconds(),
        ["Common.Mid"] = "fixture-mid",
        ["Environment.HostName"] = "fixture-host",
        ["Parent.ProcPid"] = 5151,
        ["Parent.FileName"] = Path.GetFileName(actorPath),
        ["Parent.FilePath"] = actorPath,
        ["Child.FileName"] = Path.GetFileName(filePath),
        ["Child.FilePath"] = filePath,
        ["Child.FileSize"] = 128,
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
        var occurred = DateTimeOffset.Parse(local["local_events"]!.AsArray()[0]!["occurred_at_utc"]!.GetValue<string>());
        var first = CreateCloudExport(local)[0]!.DeepClone().AsObject();
        first["Common.EventTime"] = occurred.AddMilliseconds(-500).ToUnixTimeMilliseconds();
        first["Child.FilePath"] = @"C:\unrelated\first.exe";
        first["Child.ProcCmdline"] = @"C:\unrelated\first.exe --noise";
        first["Parent.FilePath"] = @"C:\unrelated\parent.exe";
        first["Parent.ProcCmdline"] = @"C:\unrelated\parent.exe --noise";
        var second = first.DeepClone().AsObject();
        second["Common.EventUUId"] = Ids.NewUuid7();
        second["Common.EventTime"] = occurred.AddMilliseconds(500).ToUnixTimeMilliseconds();
        var coverageStart = first.DeepClone().AsObject();
        coverageStart["Common.EventUUId"] = Ids.NewUuid7();
        coverageStart["Common.EventTime"] = start.AddSeconds(-1).ToUnixTimeMilliseconds();
        var coverageEnd = first.DeepClone().AsObject();
        coverageEnd["Common.EventUUId"] = Ids.NewUuid7();
        coverageEnd["Common.EventTime"] = end.AddSeconds(1).ToUnixTimeMilliseconds();
        return new JsonArray(first, second, coverageStart, coverageEnd);
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
