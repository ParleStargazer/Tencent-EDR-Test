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
        await RunTest("云端导出 JSON/JSONL 统一解析与凭据隔离", TestCloudExportFile, failures);
        await RunTest("协议 JSON 遇到短暂文件独占时可靠重试", TestReliableProtocolFile, failures);
        await RunTest("多子测试使用独立程序与事件序号入库", TestMultipleObservationIndexes, failures);
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
        await RunTest("组策略修改 BASELINE 与通用/腾讯映射闭环", TestGroupPolicyComparison, failures);
        await RunTest("命名管道格式归一化与完整候选优先", TestNamedPipeComparison, failures);
        await RunTest("计划任务三项 BASELINE 与通用/腾讯映射闭环", TestScheduledTaskComparison, failures);
        await RunTest("服务活动三项 BASELINE 与通用/腾讯映射闭环", TestServiceComparison, failures);
        await RunTest("驱动三项 BASELINE、LoadDriver 映射与未实现结论", TestDriverComparison, failures);
        await RunTest("USB 挂载卸载 BASELINE、直接映射与腾讯未实现结论", TestUsbDeviceComparison, failures);
        await RunTest("哈希算法三项 BASELINE 与通用/腾讯映射闭环", TestHashAlgorithmsComparison, failures);
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

    private static Task TestCloudExportFile()
    {
        using var fixture = TestDirectory.Create();
        var arrayPath = Path.Combine(fixture.Path, "cloud-array.json");
        File.WriteAllText(arrayPath, "[{\"id\":1},{\"id\":2}]");
        var array = CloudExportFile.Inspect(arrayPath);
        Assert(array.Format == "json_array" && array.RecordCount == 2, "JSON 数组识别或记录计数不正确。");
        Assert(array.Sha256.Length == 64 && array.SizeBytes > 0, "云端日志完整性摘要不正确。");

        var objectPath = Path.Combine(fixture.Path, "cloud-object.json");
        File.WriteAllText(objectPath, "{\"id\":1}");
        var single = CloudExportFile.Inspect(objectPath);
        Assert(single.Format == "json_object" && single.RecordCount == 1, "单个 JSON 对象应作为一条云端事件导入。");

        var jsonlPath = Path.Combine(fixture.Path, "cloud.jsonl");
        File.WriteAllText(jsonlPath, "{\"id\":1}\n{\"id\":2}\n");
        var jsonl = CloudExportFile.Inspect(jsonlPath);
        Assert(jsonl.Format == "jsonl" && jsonl.RecordCount == 2, "JSONL 识别或记录计数不正确。");

        var invalidPath = Path.Combine(fixture.Path, "invalid.json");
        File.WriteAllText(invalidPath, "[{\"id\":1},2]");
        AssertThrows<InvalidDataException>(() => CloudExportFile.Inspect(invalidPath));
        var persistedFields = typeof(ApiCloudImportRecord).GetProperties().Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(!persistedFields.Contains("account") && !persistedFields.Contains("password"), "云端导入记录不得包含账号或密码字段。");

        var progressFields = typeof(ApiCloudProgressEntry).GetProperties().Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(progressFields.IsSupersetOf(["TimestampUtc", "Stage", "Message", "Progress", "Detailed"]), "云端导入进度缺少浏览器诊断所需字段。");
        var journalType = typeof(ApiCloudImportRecord).Assembly.GetType("EdrTest.CloudAutomationJournal")
            ?? throw new InvalidOperationException("未找到云端自动化日志组件。");
        var debugLogPath = Path.Combine(fixture.Path, "cloud-automation-debug.jsonl");
        var progressEntries = new List<ApiCloudProgressEntry>();
        Action<ApiCloudProgressEntry> callback = progressEntries.Add;
        var journal = Activator.CreateInstance(journalType, debugLogPath, callback, new[] { "child-user", "secret-value" })
            ?? throw new InvalidOperationException("无法创建云端自动化日志组件。");
        var report = journalType.GetMethod("Report") ?? throw new InvalidOperationException("云端自动化日志组件缺少 Report 方法。");
        report.Invoke(journal, ["fill_credentials", "账号 child-user，密码 secret-value", 34, "info", false, null]);
        report.Invoke(journal, ["browser_console", "child-user console secret-value", 12, "debug", true, null]);
        Assert(progressEntries.Count == 2 && progressEntries[0].Progress == 34 && progressEntries[1].Progress == 34, "云端导入进度必须单调递增，详细事件不能使进度倒退。");
        Assert(progressEntries.All(value => !value.Message.Contains("child-user", StringComparison.Ordinal) && !value.Message.Contains("secret-value", StringComparison.Ordinal)), "进度回调不得暴露云端账号或密码。");
        var persistedDebugLog = File.ReadAllText(debugLogPath);
        Assert(persistedDebugLog.Contains("[REDACTED]", StringComparison.Ordinal) && !persistedDebugLog.Contains("child-user", StringComparison.Ordinal) && !persistedDebugLog.Contains("secret-value", StringComparison.Ordinal), "持久化调试日志必须脱敏账号和密码。");
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

    private static Task TestMultipleObservationIndexes()
    {
        using var fixture = TestDirectory.Create();
        var manifestPath = PreparePackage(fixture.Path, "L0", "--fixture-controller");
        var package = CapabilityCatalog.Load(manifestPath);
        var runId = Ids.NewUuid7();
        var caseRunId = Ids.NewUuid7();
        var observedAt = DateTimeOffset.UtcNow;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得测试程序路径。");
        var databasePath = Path.Combine(fixture.Path, "program-instance-indexes.db");

        using var database = RunDatabase.Create(databasePath, new RunSeed(runId, "program-instance-indexes", null, observedAt));
        database.AddCapability(runId, caseRunId, 1, "instance-indexes", package, JsonSerializer.SerializeToElement(new { }));
        var firstActor = CreateProgram(caseRunId, "actor", 0, executable, 10001, Environment.ProcessId,
            $"\"{executable}\" --method first", observedAt);
        var secondActor = CreateProgram(caseRunId, "actor", 1, executable, 10002, Environment.ProcessId,
            $"\"{executable}\" --method second", observedAt.AddMilliseconds(1));
        database.AddProgram(firstActor);
        database.AddProgram(secondActor);
        database.AddEvent(IndexedEvent(caseRunId, firstActor.ProgramInstanceId, 1, observedAt, "first"));
        database.AddEvent(IndexedEvent(caseRunId, secondActor.ProgramInstanceId, 2, observedAt.AddMilliseconds(1), "second"));

        return Task.CompletedTask;
    }

    private static LocalEventObservation IndexedEvent(
        string caseRunId, string actorProgramId, int sequence, DateTimeOffset occurredAt, string method) => new()
    {
        CaseRunId = caseRunId,
        Sequence = sequence,
        EventType = "group_policy",
        EventAction = "modify",
        Nonce = "instance-indexes",
        OccurredAtUtc = occurredAt,
        ObservedAtUtc = occurredAt,
        Source = "multi_observation_index_test",
        CollectionMethod = method,
        ActorProgramId = actorProgramId,
        Data = new JsonObject
        {
            ["kind"] = "group_policy",
            ["operation"] = "modify",
            ["method"] = method,
        },
    };

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
        Assert(local["cleanup_results"]?.AsArray().Count == 2
            && local["cleanup_results"]?.AsArray().Select(value => value?["sequence"]?.GetValue<int>()).SequenceEqual([1, 2]) == true,
            "同一能力的多条清理结果应使用独立递增序号。");
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

    private static Task TestGroupPolicyComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var caseRunId = Ids.NewUuid7();
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        const string actorPath = @"C:\EDR-Test\GroupPolicyModify.Actor.exe";
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject { ["run_id"] = Ids.NewUuid7(), ["host"] = new JsonObject { ["hostname"] = "POLICY-FIXTURE", ["machine_id"] = "policy-fixture-host" } },
            ["capabilities"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = "win.group_policy.modify", ["capability_version"] = "0.3.0",
                ["display_name_zh"] = "组策略修改", ["display_name_en"] = "Group Policy Modification", ["status"] = "LOCAL_PASS",
                ["nonce"] = "0123456789abcdef0123456789abcdef", ["started_at_utc"] = Values.Utc(baseTime.AddSeconds(-1)), ["ended_at_utc"] = Values.Utc(baseTime.AddSeconds(2)),
            }),
            ["programs"] = new JsonArray(), ["local_events"] = new JsonArray(),
            ["local_facts"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["cleanup_results"] = new JsonArray(), ["execution_logs"] = new JsonArray(),
        };
        var genericCloud = new JsonArray();
        var tencentCloud = new JsonArray();
        var definitions = new[]
        {
            new
            {
                Method = "isolated_policy_key", EventId = Ids.NewUuid7(), OccurredAt = baseTime, ActorPid = 8240,
                KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\EdrTest\Runs\0123456789abcdef0123456789abcdef",
                ValueName = "ValidationMarker", BeforeValue = "EDRTEST|0123456789abcdef0123456789abcdef|BEFORE",
                AfterValue = "EDRTEST|0123456789abcdef0123456789abcdef|AFTER", BeforeHash = new string('a', 64), AfterHash = new string('b', 64), RawLength = 100,
            },
            new
            {
                Method = "known_policy_same_value", EventId = Ids.NewUuid7(), OccurredAt = baseTime.AddMilliseconds(100), ActorPid = 8241,
                KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "EnableSmartScreen", BeforeValue = "1", AfterValue = "1", BeforeHash = new string('c', 64), AfterHash = new string('c', 64), RawLength = 4,
            },
        };
        local["local_facts"]!.AsArray().Add(Fact(caseRunId, "group_policy.modify_succeeded", true));
        local["local_facts"]!.AsArray().Add(Fact(caseRunId, "group_policy.known_policy_same_value.prepared_for_test", false));
        local["local_facts"]!.AsArray().Add(Fact(caseRunId, "group_policy.known_policy_same_value.original_value_exists", true));
        foreach (var definition in definitions)
        {
            local["programs"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["program_instance_id"] = Ids.NewUuid7(), ["role"] = "actor",
                ["pid"] = definition.ActorPid, ["executable"] = actorPath,
                ["command_line"] = $"{actorPath} --method {definition.Method}",
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = definition.EventId, ["case_run_id"] = caseRunId,
                ["sequence"] = definition.Method == "isolated_policy_key" ? 1 : 2,
                ["event_type"] = "group_policy", ["event_action"] = "modify",
                ["occurred_at_utc"] = Values.Utc(definition.OccurredAt),
                ["data"] = new JsonObject { ["kind"] = "group_policy", ["operation"] = "modify", ["method"] = definition.Method },
            });
            var prefix = $"group_policy.{definition.Method}";
            var facts = new Dictionary<string, object?>
            {
                [$"{prefix}.applicable"] = true, [$"{prefix}.modify_succeeded"] = true,
                [$"{prefix}.occurred_at_utc"] = Values.Utc(definition.OccurredAt),
                [$"{prefix}.key_path"] = definition.KeyPath, [$"{prefix}.value_name"] = definition.ValueName,
                [$"{prefix}.before.value_data"] = definition.BeforeValue, [$"{prefix}.after.value_data"] = definition.AfterValue,
                [$"{prefix}.before.value_data_sha256"] = definition.BeforeHash, [$"{prefix}.after.value_data_sha256"] = definition.AfterHash,
                [$"{prefix}.before.raw_data_length"] = definition.RawLength, [$"{prefix}.after.raw_data_length"] = definition.RawLength,
                [$"{prefix}.target_id"] = definition.Method == "known_policy_same_value" ? "windows-smart-screen-enable" : null,
                [$"{prefix}.actor_pid"] = definition.ActorPid, [$"{prefix}.actor_executable"] = actorPath,
            };
            foreach (var (key, value) in facts.Where(item => item.Value is not null))
                local["local_facts"]!.AsArray().Add(Fact(caseRunId, key, value!));
            genericCloud.Add(new JsonObject
            {
                ["table"] = "GroupPolicyActivity", ["event_id"] = definition.EventId, ["host_id"] = "policy-fixture-host",
                ["event_time"] = Values.Utc(definition.OccurredAt), ["action"] = "modify", ["actor_pid"] = definition.ActorPid,
                ["actor_name"] = Path.GetFileName(actorPath), ["actor_executable"] = actorPath,
                ["actor_command_line"] = $"{actorPath} --method {definition.Method}", ["registry_key"] = definition.KeyPath,
                ["registry_value_name"] = definition.ValueName, ["registry_value_data"] = definition.AfterValue,
                ["registry_old_value_data"] = definition.BeforeValue, ["registry_value_type"] = "DWORD",
                ["registry_old_value_type"] = "DWORD", ["registry_group_name"] = "组策略",
                ["monitor_name"] = "组策略",
            });
            tencentCloud.Add(new JsonObject
            {
                ["OS"] = "Windows", ["@table"] = "RegEvents", ["@timestamp"] = Values.Utc(definition.OccurredAt),
                ["Action.Type"] = "Reg", ["Action.Name"] = "RegSetValue", ["Common.EventUUId"] = definition.EventId,
                ["Common.EventTime"] = definition.OccurredAt.ToUnixTimeMilliseconds(), ["Common.Mid"] = "policy-fixture-host",
                ["Environment.HostName"] = "POLICY-FIXTURE", ["Parent.ProcPid"] = definition.ActorPid,
                ["Parent.FileName"] = Path.GetFileName(actorPath), ["Parent.FilePath"] = actorPath,
                ["Parent.ProcCmdline"] = $"{actorPath} --method {definition.Method}", ["Child.RegKeyPath"] = definition.KeyPath,
                ["Child.RegValName"] = definition.ValueName, ["Child.RegValData"] = definition.AfterValue,
                ["Child.RegOldValData"] = definition.BeforeValue, ["Child.RegValType"] = "DWORD",
                ["Child.RegOldValType"] = "DWORD", ["Child.RegGroupName"] = "组策略", ["Common.MonitorName"] = "组策略",
            });
        }
        var localPath = Path.Combine(fixture.Path, "group-policy-local.json");
        var genericPath = Path.Combine(fixture.Path, "group-policy-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "group-policy-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var baseline = new[] { Path.Combine(repository, "baselines", "windows", "group_policy_modify.yaml") };
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath], Path.Combine(repository, "mappings", "generic-group-policy-activity-v1.yaml"), baseline, Path.Combine(fixture.Path, "generic-result.json")));
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath], Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baseline, Path.Combine(fixture.Path, "tencent-result.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 1, $"通用组策略映射应通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 1, $"腾讯组策略映射应通过：{tencent.ToJsonString(JsonDefaults.Options)}");
        var capability = tencent["capabilities"]?.AsArray()[0]?.AsObject() ?? throw new InvalidOperationException("缺少组策略比较结果。");
        Assert(capability["method_results"]?.AsArray().Count == 2
            && capability["method_results"]?.AsArray().All(method => method?["status"]?.GetValue<string>() == "PASS") == true
            && capability["method_results"]?.AsArray().Any(method => method?["method_id"]?.GetValue<string>() == "known_policy_same_value") == true,
            $"组策略必须独立输出隔离控制组与 L2 同值回写两个通过的方法：{capability["method_results"]?.ToJsonString(JsonDefaults.Options)}");
        Assert(capability["edr_candidates"]?.AsArray().Any(candidate =>
            candidate?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "registry.group_name"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.RegGroupName") == true) == true,
            "组策略候选应高亮 Child.RegGroupName。");
        Assert(capability["edr_candidates"]?.AsArray().Any(candidate =>
            candidate?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "registry.monitor_name"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Common.MonitorName") == true) == true,
            "组策略候选应高亮 Common.MonitorName。");
        return Task.CompletedTask;
    }

    private static Task TestNamedPipeComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var caseRunId = Ids.NewUuid7();
        var eventId = Ids.NewUuid7();
        var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        const int actorPid = 1452;
        const int helperPid = 4244;
        const string actorPath = @"C:\EDR-Test\NamedPipeConnect.Actor.exe";
        const string localPipeName = @"\\.\pipe\EdrTest_4c6dae54912029b48066eb4dfef9cc70_connect";
        const string tencentPipeName = @"\EdrTest_4c6dae54912029b48066eb4dfef9cc70_connect";
        const string canonicalPipeName = @"\\.\pipe\edrtest_4c6dae54912029b48066eb4dfef9cc70_connect";
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "PIPE-FIXTURE", ["machine_id"] = "pipe-fixture-host" },
            },
            ["capabilities"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = "win.named_pipe.connect", ["capability_version"] = "0.1.0",
                ["display_name_zh"] = "管道连接", ["display_name_en"] = "Pipe Connection", ["status"] = "LOCAL_PASS",
                ["nonce"] = "4c6dae54912029b48066eb4dfef9cc70", ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(1)),
            }),
            ["programs"] = new JsonArray(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["program_instance_id"] = Ids.NewUuid7(), ["role"] = "actor",
                ["pid"] = actorPid, ["executable"] = actorPath, ["command_line"] = actorPath + " --operation connect",
            }),
            ["local_events"] = new JsonArray(new JsonObject
            {
                ["local_event_id"] = eventId, ["case_run_id"] = caseRunId, ["sequence"] = 1,
                ["event_type"] = "named_pipe", ["event_action"] = "connect", ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject { ["kind"] = "named_pipe", ["operation"] = "connect" },
            }),
            ["local_facts"] = new JsonArray(
                Fact(caseRunId, "named_pipe.operation_succeeded", true),
                Fact(caseRunId, "named_pipe.operation", "connect"),
                Fact(caseRunId, "named_pipe.name", localPipeName),
                Fact(caseRunId, "named_pipe.actor_pid", actorPid),
                Fact(caseRunId, "named_pipe.actor_executable", actorPath),
                Fact(caseRunId, "named_pipe.helper_pid", helperPid),
                Fact(caseRunId, "named_pipe.nonce_verified", true),
                Fact(caseRunId, "named_pipe.occurred_at_utc", Values.Utc(occurredAt))),
            ["artifacts"] = new JsonArray(), ["cleanup_results"] = new JsonArray(), ["execution_logs"] = new JsonArray(),
        };

        JsonObject TencentEvent(string id, string pipeName) => new()
        {
            ["OS"] = "Windows", ["@table"] = "FileEvents", ["@timestamp"] = Values.Utc(occurredAt),
            ["Action.Type"] = "File", ["Action.Name"] = "NamedPipe", ["Common.EventUUId"] = id,
            ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(), ["Common.Mid"] = "pipe-fixture-host",
            ["Environment.HostName"] = "PIPE-FIXTURE", ["Parent.ProcPid"] = actorPid,
            ["Parent.FileName"] = Path.GetFileName(actorPath), ["Parent.FilePath"] = actorPath,
            ["Parent.ProcCmdline"] = actorPath + " --operation connect", ["Child.PipeName"] = pipeName,
            ["Child.NodeName"] = pipeName, ["Child.PipeOpName"] = "打开管道", ["Child.Type"] = "管道",
        };

        var localPath = Path.Combine(fixture.Path, "named-pipe-local.json");
        var tencentPath = Path.Combine(fixture.Path, "named-pipe-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, new JsonArray(
            TencentEvent("00000000-placeholder", @"\"),
            TencentEvent("ffffffff-complete", tencentPipeName)).ToJsonString(JsonDefaults.Options));
        var baseline = new[] { Path.Combine(repository, "baselines", "windows", "named_pipe_connect.yaml") };
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baseline,
            Path.Combine(fixture.Path, "named-pipe-tencent-result.json")));
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 1,
            $"短格式腾讯管道名应与本地完整格式匹配：{tencent.ToJsonString(JsonDefaults.Options)}");
        var candidates = tencent["capabilities"]?[0]?["edr_candidates"]?.AsArray()
            ?? throw new InvalidOperationException("命名管道结果缺少 EDR 候选。");
        Assert(candidates.Count == 2, "占位记录与完整名称记录都应保留供 JSON 对照查看。");
        Assert(candidates[0]?["raw_event"]?["Child.PipeName"]?.GetValue<string>() == tencentPipeName
            && candidates[0]?["canonical_event"]?["named_pipe.name"]?.GetValue<string>() == canonicalPipeName,
            $"完整管道名记录必须排在首位并输出统一格式：{candidates.ToJsonString(JsonDefaults.Options)}");
        Assert(candidates[0]?["correlation_score"]?.GetValue<double>()
                > candidates[1]?["correlation_score"]?.GetValue<double>()
            && candidates[1]?["canonical_event"]?["named_pipe.name"] is null,
            "单独反斜杠必须作为缺失值降级，不能与完整名称记录同分或排在其前面。");
        Assert(candidates[0]?["baseline_matches"]?.AsArray().Any(match =>
            match?["canonical_field"]?.GetValue<string>() == "named_pipe.name"
            && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.PipeName"
            && match?["status"]?.GetValue<string>() == "passed") == true,
            "JSON 对照必须高亮本地与 EDR 管道名的一致关系。");

        var genericPath = Path.Combine(fixture.Path, "named-pipe-generic.json");
        File.WriteAllText(genericPath, new JsonArray(new JsonObject
        {
            ["table"] = "NamedPipeActivity", ["event_id"] = "device-format", ["host_id"] = "pipe-fixture-host",
            ["event_time"] = Values.Utc(occurredAt), ["action"] = "connect", ["actor_pid"] = actorPid,
            ["actor_name"] = Path.GetFileName(actorPath), ["actor_executable"] = actorPath,
            ["actor_command_line"] = actorPath + " --operation connect",
            ["pipe_name"] = @"\Device\NamedPipe\EdrTest_4c6dae54912029b48066eb4dfef9cc70_connect",
            ["node_name"] = @"\Device\NamedPipe\EdrTest_4c6dae54912029b48066eb4dfef9cc70_connect",
            ["operation_name"] = "打开管道", ["pipe_type"] = "管道",
        }).ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-named-pipe-activity-v1.yaml"), baseline,
            Path.Combine(fixture.Path, "named-pipe-generic-result.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 1
            && generic["capabilities"]?[0]?["edr_candidates"]?[0]?["canonical_event"]?["named_pipe.name"]?.GetValue<string>() == canonicalPipeName,
            $"Device NamedPipe 格式也应统一并通过：{generic.ToJsonString(JsonDefaults.Options)}");
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
                ["capability_version"] = "0.3.1", ["display_name_zh"] = definition.Operation,
                ["display_name_en"] = definition.Operation, ["status"] = "LOCAL_PASS",
                ["nonce"] = $"scheduled-task-fixture-{index}", ["started_at_utc"] = Values.Utc(capabilityTime.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(capabilityTime.AddSeconds(3)),
            });
            var auditMethod = definition.Operation == "modify" ? "security_audit_update" : $"security_audit_{definition.Operation}";
            var methods = new[] { "task_scheduler_com", "schtasks_cli", auditMethod };
            foreach (var (method, methodIndex) in methods.Select((value, index) => (value, index)))
            {
                var eventId = Ids.NewUuid7();
                var occurredAt = capabilityTime.AddSeconds(methodIndex);
                var isSecurityAudit = method == auditMethod;
                var isSchtasksCli = method == "schtasks_cli";
                var methodSuffix = isSecurityAudit ? "audit" : isSchtasksCli ? "cli" : "com";
                var taskPath = $@"\EdrTest_fixture{index}_{definition.Operation}_{methodSuffix}";
                var marker = $"EDRTEST|scheduled-task-fixture-{index}|SCHEDULED_TASK|{method}|{definition.Operation.ToUpperInvariant()}";
                var beforeMarker = $"EDRTEST|scheduled-task-fixture-{index}|SCHEDULED_TASK|{method}|BEFORE";
                var actorPid = 9300 + index * 10 + methodIndex;
                var actorPath = isSecurityAudit || isSchtasksCli
                    ? @"C:\Windows\System32\schtasks.exe"
                    : $@"C:\EDR-Test\ScheduledTask{definition.Operation}.Actor.exe";
                var beforeExists = definition.Operation != "create";
                var afterExists = definition.Operation != "delete";
                var afterEnabled = (isSecurityAudit && definition.Operation is "create" or "modify")
                    || (isSchtasksCli && definition.Operation is "create" or "modify");
                var prefix = $"scheduled_task.{method}";
                var afterArguments = $"/d /c rem EDRTEST_fixture{index}_{methodSuffix}";
                var beforeArguments = "/d /c exit 0";
                var preserveBeforeDefinition = definition.Operation == "delete"
                    || isSchtasksCli && definition.Operation == "modify";
                var cloudArguments = preserveBeforeDefinition ? beforeArguments : afterArguments;
                var cloudMarker = preserveBeforeDefinition ? beforeMarker : marker;
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
                    [$"{prefix}.correlation_at_utc"] = Values.Utc(occurredAt),
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
                if (isSecurityAudit || isSchtasksCli)
                {
                    facts[$"{prefix}.security_event_id"] = definition.EventId;
                    facts[$"{prefix}.security_event_found"] = true;
                    facts[$"{prefix}.security_event_occurred_at_utc"] = Values.Utc(occurredAt);
                    facts[$"{prefix}.security_event_record_id"] = 8000 + index;
                    if (isSecurityAudit)
                    {
                        facts[$"{prefix}.audit_policy_active"] = 1;
                        facts[$"{prefix}.audit_success_enabled"] = true;
                        facts[$"{prefix}.audit_policy_restore_succeeded"] = true;
                    }
                    if (definition.Operation == "create" || isSecurityAudit && definition.Operation == "modify")
                        facts[$"{prefix}.after.triggers"] = new JsonArray("TimeTrigger");
                    if (definition.Operation == "create")
                    {
                        facts[$"{prefix}.security_event_4698_found"] = true;
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
                    var useServiceSideProcess = definition.Operation == "modify" && (isSecurityAudit || isSchtasksCli);
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
            value?["expectation_id"]?.GetValue<string>() == "scheduled-task-modify-security-audit-event"
            && value?["field"]?.GetValue<string>() == "process.executable")
            ?? throw new InvalidOperationException("计划任务修改结果缺少 process.executable BASELINE 项。");
        Assert(processRequirement["status"]?.GetValue<string>() == "passed",
            "修改日志仅保留服务侧进程时，进程存在性推荐项仍应通过。");
        foreach (var operation in new[] { "create", "modify", "delete" })
        {
            var capability = tencentResult["capabilities"]?.AsArray().Single(value =>
                value?["capability_id"]?.GetValue<string>() == $"win.scheduled_task.{operation}")?.AsObject()
                ?? throw new InvalidOperationException($"腾讯比较结果缺少计划任务 {operation} 能力。");
            var auditMethod = operation == "modify" ? "security_audit_update" : $"security_audit_{operation}";
            var expectedMethodCount = 3;
            Assert(capability["method_results"]?.AsArray().Count == expectedMethodCount
                && capability["method_results"]?.AsArray().Any(value => value?["method_id"]?.GetValue<string>() == "task_scheduler_com") == true
                && capability["method_results"]?.AsArray().Any(value => value?["method_id"]?.GetValue<string>() == "schtasks_cli") == true
                && capability["method_results"]?.AsArray().Any(value => value?["method_id"]?.GetValue<string>() == auditMethod) == true
                && capability["method_results"]?.AsArray().All(value => value?["status"]?.GetValue<string>() == "PASS") == true,
                $"计划任务 {operation} 必须输出对应的通过方法结果。");
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

    private static Task TestDriverComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "DRIVER-FIXTURE", ["machine_id"] = "driver-fixture-host" },
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
        var baselinePaths = new List<string>();
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var sourceHash = "1111111111111111111111111111111111111111111111111111111111111111";
        var modifiedHash = "2222222222222222222222222222222222222222222222222222222222222222";
        var sourceMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var modifiedMd5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var driverPath = @"C:\EDR-Test\EdrTestDriver_fixture.sys";
        var driverName = Path.GetFileName(driverPath);
        foreach (var (operation, index) in new[] { "load", "modify", "unload" }.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var eventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var serviceName = $"EdrTestDrv_fixture_{operation}";
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["capability_id"] = $"win.driver.{operation}",
                ["capability_version"] = "0.1.0",
                ["display_name_zh"] = operation,
                ["display_name_en"] = operation,
                ["status"] = "LOCAL_PASS",
                ["nonce"] = $"driver-fixture-{index}",
                ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(2)),
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = eventId,
                ["case_run_id"] = caseRunId,
                ["sequence"] = 1,
                ["event_type"] = "driver",
                ["event_action"] = operation,
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject { ["kind"] = "driver", ["operation"] = operation, ["driver_name"] = driverName },
            });
            var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["driver.environment.ready"] = true,
                [$"driver.{operation}_succeeded"] = true,
                ["driver.occurred_at_utc"] = Values.Utc(occurredAt),
                ["driver.name"] = driverName,
                ["driver.service_name"] = serviceName,
                ["driver.image_path"] = driverPath,
            };
            if (operation == "load")
            {
                facts["driver.native_api"] = "CreateServiceW+StartServiceW";
                facts["driver.before.loaded"] = false;
                facts["driver.after.loaded"] = true;
                facts["driver.after.service_state"] = "running";
                facts["driver.after.base_address"] = "0xfffff80123450000";
                facts["driver.after.size_bytes"] = 16384L;
                facts["driver.after.module_size_bytes"] = 20480L;
                facts["driver.after.hashes.md5"] = sourceMd5;
                facts["driver.after.hashes.sha256"] = sourceHash;
                facts["driver.package.signature_valid"] = true;
                facts["driver.package.sha256"] = sourceHash;
            }
            else if (operation == "modify")
            {
                facts["driver.native_api"] = "FileStream.Write";
                facts["driver.before.loaded"] = false;
                facts["driver.after.loaded"] = false;
                facts["driver.before.service_exists"] = false;
                facts["driver.after.service_exists"] = false;
                facts["driver.before.size_bytes"] = 16384L;
                facts["driver.after.size_bytes"] = 16440L;
                facts["driver.before.hashes.md5"] = sourceMd5;
                facts["driver.after.hashes.md5"] = modifiedMd5;
                facts["driver.before.hashes.sha256"] = sourceHash;
                facts["driver.after.hashes.sha256"] = modifiedHash;
                facts["driver.modification.marker"] = "EDRTEST_DRIVER_MODIFY|fixture";
            }
            else
            {
                facts["driver.setup_load_succeeded"] = true;
                facts["driver.load_isolation_ms"] = 2200;
                facts["driver.native_api"] = "ControlService(STOP)";
                facts["driver.before.loaded"] = true;
                facts["driver.before.base_address"] = "0xfffff80123450000";
                facts["driver.before.module_size_bytes"] = 20480L;
                facts["driver.before.hashes.md5"] = sourceMd5;
                facts["driver.before.hashes.sha256"] = sourceHash;
                facts["driver.after.loaded"] = false;
                facts["driver.after.service_exists"] = true;
                facts["driver.after.service_state"] = "stopped";
            }
            foreach (var (key, value) in facts) local["local_facts"]!.AsArray().Add(Fact(caseRunId, key, value!));

            genericCloud.Add(new JsonObject
            {
                ["event_type"] = "driver",
                ["event_id"] = eventId + "-generic",
                ["host_id"] = "driver-fixture-host",
                ["hostname"] = "DRIVER-FIXTURE",
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["operation"] = operation,
                ["driver_name"] = driverName,
                ["service_name"] = serviceName,
                ["image_path"] = driverPath,
                ["base_address"] = "0xfffff80123450000",
                ["size_bytes"] = operation == "modify" ? 16440 : 16384,
                ["module_size_bytes"] = 20480,
                ["md5"] = operation == "modify" ? modifiedMd5 : sourceMd5,
                ["sha256"] = operation == "modify" ? modifiedHash : sourceHash,
                ["signer"] = "Tencent EDR Test Driver",
                ["signature_valid"] = true,
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"driver_{operation}.yaml"));
        }

        var loadTime = baseTime;
        const long signedModuleBase = -8790327230464;
        tencentCloud.Add(new JsonObject
        {
            ["OS"] = "Windows",
            ["@table"] = "ModuleEvents",
            ["@timestamp"] = Values.Utc(loadTime.AddMilliseconds(3)),
            ["Action.Type"] = "Module",
            ["Action.Name"] = "LoadDriver",
            ["Common.Source"] = "KernelMon",
            ["Common.MonitorName"] = "加载驱动",
            ["Common.EventUUId"] = Ids.NewUuid7(),
            ["Common.EventTime"] = loadTime.ToUnixTimeMilliseconds(),
            ["Common.Mid"] = "driver-fixture-host",
            ["Environment.HostName"] = "DRIVER-FIXTURE",
            ["Parent.ProcPid"] = 0,
            ["Parent.FileName"] = "SystemIdle",
            ["Parent.FilePath"] = "SystemIdle",
            ["Parent.ProcCmdline"] = "SystemIdle",
            ["Child.FileName"] = driverName,
            ["Child.FilePath"] = driverPath,
            ["Child.FileMd5"] = sourceMd5,
            ["Child.FileSize"] = 16384,
            ["Child.ModuleBase"] = signedModuleBase,
            ["Child.ModuleSize"] = 20480,
            ["Child.FileSign"] = "Tencent EDR Test Driver",
            ["Child.FileSignStatus"] = "验证通过",
            ["Child.FileSignWhite"] = "是",
        });

        var localPath = Path.Combine(fixture.Path, "driver-local.json");
        var genericPath = Path.Combine(fixture.Path, "driver-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "driver-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-driver-activity-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "driver-generic-validation.json")));
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "driver-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 3,
            $"通用驱动直接事件应使三项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 1,
            $"腾讯实测 LoadDriver 只能使加载通过：{tencent.ToJsonString(JsonDefaults.Options)}");
        var capabilities = tencent["capabilities"]?.AsArray()
            ?? throw new InvalidOperationException("腾讯驱动比较结果缺少能力数组。");
        Assert(capabilities.Single(value => value?["capability_id"]?.GetValue<string>() == "win.driver.load")?
                ["validation_status"]?.GetValue<string>() == "PASS",
            "ModuleEvents/LoadDriver 应使驱动加载通过。");
        Assert(capabilities.Where(value => value?["capability_id"]?.GetValue<string>() is "win.driver.modify" or "win.driver.unload")
                .All(value => value?["validation_status"]?.GetValue<string>() != "PASS"),
            "LoadDriver 预置或侧面事件不能使驱动修改、卸载通过。");
        var load = capabilities.Single(value => value?["capability_id"]?.GetValue<string>() == "win.driver.load")?.AsObject()
            ?? throw new InvalidOperationException("腾讯驱动比较结果缺少加载能力。");
        Assert(load["edr_candidates"]?.AsArray().Any(candidate => candidate?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "driver.base_address"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.ModuleBase") == true) == true,
            "Child.ModuleBase 应映射并参与 JSON 对照高亮。");
        return Task.CompletedTask;
    }

    private static Task TestUsbDeviceComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "USB-FIXTURE", ["machine_id"] = "usb-fixture-host" },
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
        var tencentSideEvidence = new JsonArray();
        var baselinePaths = new List<string>();
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (var (operation, index) in new[] { "mount", "unmount" }.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var eventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var serial = $"EDR_USB_{new string(index == 0 ? 'A' : 'B', 24)}";
            var instanceId = $@"USB\VID_ED1D&PID_0001\{serial}";
            var actorPath = operation == "mount"
                ? @"C:\EDR-Test\UsbDeviceMount.Actor.exe"
                : @"C:\EDR-Test\UsbDeviceUnmount.Actor.exe";
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId,
                ["capability_id"] = $"win.device.usb.{operation}",
                ["capability_version"] = "0.1.0",
                ["display_name_zh"] = operation,
                ["display_name_en"] = operation,
                ["status"] = "LOCAL_PASS",
                ["nonce"] = $"usb-fixture-{index}",
                ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-2)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(2)),
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = eventId,
                ["case_run_id"] = caseRunId,
                ["sequence"] = 1,
                ["event_type"] = "device",
                ["event_action"] = $"usb_{operation}",
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject { ["kind"] = "device", ["operation"] = $"usb_{operation}" },
            });
            var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["usb.environment.ready"] = true,
                ["usb.package.signature_valid"] = true,
                ["usb.package.inf_sha256"] = new string('1', 64),
                ["usb.package.catalog_sha256"] = new string('2', 64),
                ["usb.package.certificate_sha256"] = new string('3', 64),
                ["usb.package.catalog_membership_verified"] = true,
                ["usb.install.stage"] = "ready",
                ["usb.install.driver_store_present"] = true,
                ["usb.install.published_inf_path"] = @"C:\Windows\INF\oem42.inf",
                ["usb.install.root_devnode_present"] = true,
                ["usb.install.root_instance_id"] = @"ROOT\USB_UDE_TEST\0000",
                ["usb.install.bound_service"] = "UsbUdeTest",
                ["usb.install.bound_inf_name"] = "oem42.inf",
                ["usb.install.config_manager_result"] = 0,
                ["usb.install.devnode_problem_code"] = 0,
                ["usb.install.devnode_started"] = true,
                ["usb.install.driver_initialization_stage"] = "evt_device_add_succeeded",
                ["usb.install.driver_initialization_status"] = 0,
                ["usb.install.driver_interface_guid"] = "{77DC40F2-80FB-4F86-A6D4-793AB56D2D45}",
                ["usb.install.expected_interface_guid"] = "{77DC40F2-80FB-4F86-A6D4-793AB56D2D45}",
                ["usb.install.interface_present"] = true,
                ["usb.install.interface_path"] = @"\\?\root#usb_ude_test#0000#{77dc40f2-80fb-4f86-a6d4-793ab56d2d45}",
                ["usb.operation_succeeded"] = true,
                ["usb.operation"] = operation,
                ["usb.method"] = "USB_UDE_PNP",
                ["usb.occurred_at_utc"] = Values.Utc(occurredAt),
                ["usb.completed_at_utc"] = Values.Utc(occurredAt.AddMilliseconds(6)),
                ["usb.instance_id"] = instanceId,
                ["usb.class_guid"] = "{36FC9E60-C465-11CF-8056-444553540000}",
                ["usb.vendor_id"] = "ED1D",
                ["usb.product_id"] = "0001",
                ["usb.serial_number"] = serial,
                ["usb.before_present"] = operation == "unmount",
                ["usb.after_present"] = operation == "mount",
                ["usb.ioctl_succeeded"] = true,
                ["usb.controller_pnp_verified"] = true,
                ["usb.actor_pid"] = 8200 + index,
                ["usb.actor_executable"] = actorPath,
                ["usb.actor_command_line"] = $"\"{actorPath}\" --operation {(operation == "mount" ? "attach" : "detach")}",
                ["usb.volume_guid"] = null,
                ["usb.drive_letter"] = null,
                ["usb.mount_point"] = null,
            };
            if (operation == "unmount")
            {
                facts["usb.setup_attach_succeeded"] = true;
                facts["usb.setup_actor_pid"] = 8100;
            }
            foreach (var (key, value) in facts)
                local["local_facts"]!.AsArray().Add(Fact(caseRunId, key, value!));

            genericCloud.Add(new JsonObject
            {
                ["table"] = "UsbDeviceActivity",
                ["action"] = operation == "mount" ? "UsbDeviceMount" : "UsbDeviceUnmount",
                ["event_id"] = eventId + "-generic",
                ["host_id"] = "usb-fixture-host",
                ["host_name"] = "USB-FIXTURE",
                ["event_time"] = Values.Utc(occurredAt.AddMilliseconds(4)),
                ["actor_pid"] = 8200 + index,
                ["actor_executable"] = actorPath,
                ["actor_command_line"] = $"\"{actorPath}\" --operation {(operation == "mount" ? "attach" : "detach")}",
                ["instance_id"] = instanceId,
                ["class_guid"] = "{36FC9E60-C465-11CF-8056-444553540000}",
                ["vendor_id"] = "ED1D",
                ["product_id"] = "0001",
                ["serial_number"] = serial,
                ["description"] = "EDR USB Telemetry Device",
                ["manufacturer"] = "Tencent EDR Test",
                ["service"] = "UsbUdeTest",
                ["driver_key"] = "{usb-fixture-driver-key}",
                ["method"] = "USB_UDE_PNP",
                ["provider"] = "UsbUdeTest/UdeCx",
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"device_usb_{operation}.yaml"));
        }

        tencentSideEvidence.Add(new JsonObject
        {
            ["OS"] = "Windows",
            ["@table"] = "ModuleEvents",
            ["@timestamp"] = Values.Utc(baseTime.AddMilliseconds(3)),
            ["Action.Type"] = "Module",
            ["Action.Name"] = "LoadDriver",
            ["Common.Source"] = "KernelMon",
            ["Common.EventUUId"] = Ids.NewUuid7(),
            ["Common.EventTime"] = baseTime.ToUnixTimeMilliseconds(),
            ["Common.Mid"] = "usb-fixture-host",
            ["Environment.HostName"] = "USB-FIXTURE",
            ["Child.FileName"] = "UsbUdeTest.sys",
            ["Child.FilePath"] = @"C:\Windows\System32\drivers\UsbUdeTest.sys",
        });

        var localPath = Path.Combine(fixture.Path, "usb-local.json");
        var genericPath = Path.Combine(fixture.Path, "usb-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "usb-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentSideEvidence.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-usb-device-activity-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "usb-generic-validation.json")));
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "usb-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 2,
            $"通用 USB 直接事件应使挂载和卸载 BASELINE 通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 0,
            "腾讯侧的驱动加载记录不能替代 USB 挂载或卸载直接 telemetry。");
        Assert(tencent["capabilities"]?.AsArray().All(value =>
            value?["validation_status"]?.GetValue<string>() != "PASS") == true,
            "腾讯 EDR 没有 USB 专属事件时，两项能力必须保持未通过。" );
        return Task.CompletedTask;
    }

    private static Task TestServiceComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "SERVICE-FIXTURE", ["machine_id"] = "service-fixture-host" },
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
            (Operation: "create", NativeApi: "CreateServiceW", EventId: (int?)7045),
            (Operation: "modify", NativeApi: "ChangeServiceConfigW", EventId: (int?)7040),
            (Operation: "delete", NativeApi: "DeleteService", EventId: (int?)null),
        };
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var eventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var apiOccurredAt = occurredAt.AddMilliseconds(-1);
            var actorPid = 10_400 + index;
            var actorPath = $@"C:\EDR-Test\Service{definition.Operation}.Actor.exe";
            var serviceName = $"EdrTestSvc_fixture{index}_{definition.Operation}";
            var displayName = $"EDRTEST|service-fixture-{index}|SERVICE|{definition.Operation.ToUpperInvariant()}";
            var beforeDisplayName = $"EDRTEST|service-fixture-{index}|SERVICE|BEFORE";
            var beforeBinaryPath = "\"C:\\Windows\\System32\\cmd.exe\" /d /c exit 0";
            var binaryPath = $"\"C:\\Windows\\System32\\cmd.exe\" /d /c rem EDRTEST_fixture{index}_{definition.Operation.ToUpperInvariant()}";
            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = $"win.service.{definition.Operation}",
                ["capability_version"] = "0.1.0", ["display_name_zh"] = definition.Operation,
                ["display_name_en"] = definition.Operation, ["status"] = "LOCAL_PASS",
                ["nonce"] = $"service-fixture-{index}", ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(2)),
            });
            local["programs"]!.AsArray().Add(new JsonObject
            {
                ["program_instance_id"] = Ids.NewUuid7(), ["case_run_id"] = caseRunId, ["role"] = "actor",
                ["pid"] = actorPid, ["file_name"] = Path.GetFileName(actorPath), ["executable"] = actorPath,
                ["command_line"] = $"{actorPath} --operation {definition.Operation}",
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = eventId, ["case_run_id"] = caseRunId, ["sequence"] = 1,
                ["event_type"] = "service", ["event_action"] = definition.Operation,
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject { ["kind"] = "service", ["operation"] = definition.Operation, ["service_name"] = serviceName },
            });
            var facts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [$"service.{definition.Operation}_succeeded"] = true,
                ["service.native_api"] = definition.NativeApi, ["service.name"] = serviceName,
                ["service.display_name"] = displayName, ["service.binary_path"] = binaryPath,
                ["service.actor_pid"] = actorPid, ["service.actor_executable"] = actorPath,
                ["service.completed_at_utc"] = Values.Utc(occurredAt),
                ["service.before.exists"] = definition.Operation != "create",
                ["service.after.exists"] = definition.Operation != "delete",
            };
            if (definition.Operation is "modify" or "delete")
            {
                facts["service.before.display_name"] = beforeDisplayName;
                facts["service.before.binary_path"] = beforeBinaryPath;
                facts["service.before.start_type"] = definition.Operation == "modify" ? "demand" : "disabled";
                facts["service.before.state"] = "stopped";
            }
            if (definition.Operation == "delete")
            {
                facts["service.before.account"] = "LocalSystem";
                facts["service.before.service_type"] = "win32_own_process";
            }
            else
            {
                facts["service.after.display_name"] = displayName;
                facts["service.after.binary_path"] = binaryPath;
                facts["service.after.start_type"] = "disabled";
                facts["service.after.account"] = "LocalSystem";
                facts["service.after.service_type"] = "win32_own_process";
                facts["service.after.state"] = "stopped";
            }
            foreach (var (key, value) in facts)
                local["local_facts"]!.AsArray().Add(Fact(caseRunId, key, value));

            genericCloud.Add(new JsonObject
            {
                ["table"] = "ServiceActivity", ["event_id"] = eventId + "-api", ["host_id"] = "service-fixture-host",
                ["host_name"] = "SERVICE-FIXTURE", ["event_time"] = Values.Utc(apiOccurredAt), ["event_type"] = "service_api",
                ["action"] = definition.Operation, ["actor_pid"] = actorPid, ["actor_name"] = Path.GetFileName(actorPath),
                ["actor_executable"] = actorPath, ["actor_command_line"] = $"{actorPath} --operation {definition.Operation}",
                ["service_name"] = serviceName,
                ["service_display_name"] = definition.Operation == "delete" ? beforeDisplayName : displayName,
                ["service_binary_path"] = definition.Operation == "delete" ? beforeBinaryPath : binaryPath,
                ["service_start_type"] = "disabled", ["service_old_start_type"] = definition.Operation == "modify" ? "demand" : null,
                ["service_account"] = "LocalSystem", ["service_type"] = "win32_own_process", ["service_state"] = "stopped",
            });
            tencentCloud.Add(new JsonObject
            {
                ["OS"] = "Windows", ["@table"] = "ServiceEvents", ["@timestamp"] = Values.Utc(apiOccurredAt),
                ["Action.Type"] = "InjectHook", ["Action.Name"] = definition.NativeApi,
                ["Common.EventUUId"] = eventId + "-api", ["Common.EventTime"] = apiOccurredAt.ToUnixTimeMilliseconds(),
                ["Common.Mid"] = "service-fixture-host", ["Environment.HostName"] = "SERVICE-FIXTURE",
                ["Parent.ProcPid"] = actorPid, ["Parent.FileName"] = Path.GetFileName(actorPath), ["Parent.FilePath"] = actorPath,
                ["Parent.ProcCmdline"] = $"{actorPath} --operation {definition.Operation}", ["Child.ServiceName"] = serviceName,
                ["Child.DisplayName"] = definition.Operation == "delete" ? beforeDisplayName : displayName,
                ["Child.BinaryPath"] = definition.Operation == "delete" ? beforeBinaryPath : binaryPath,
                ["Child.NewStartType"] = "disabled", ["Child.OldStartType"] = definition.Operation == "modify" ? "demand" : null,
                ["Child.ServiceAccount"] = "LocalSystem", ["Child.ServiceType"] = "win32_own_process",
            });
            if (definition.EventId is not int systemEventId) { baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"service_{definition.Operation}.yaml")); continue; }
            genericCloud.Add(new JsonObject
            {
                ["table"] = "ServiceActivity", ["event_id"] = eventId + "-system", ["host_id"] = "service-fixture-host",
                ["host_name"] = "SERVICE-FIXTURE", ["event_time"] = Values.Utc(occurredAt), ["event_type"] = "service",
                ["action"] = definition.Operation, ["actor_pid"] = 748, ["actor_name"] = "services.exe",
                ["actor_executable"] = @"C:\Windows\System32\services.exe", ["event_log_id"] = systemEventId,
                ["service_name"] = serviceName, ["service_display_name"] = displayName, ["service_binary_path"] = binaryPath,
                ["service_start_type"] = "disabled", ["service_old_start_type"] = definition.Operation == "modify" ? "demand" : null,
                ["service_account"] = "LocalSystem", ["service_type"] = "win32_own_process",
            });
            tencentCloud.Add(new JsonObject
            {
                ["OS"] = "Windows", ["@table"] = "SystemEvents", ["@timestamp"] = Values.Utc(occurredAt),
                ["Action.Type"] = "WinEventLog", ["Action.Name"] = definition.Operation == "create" ? "ServiceInstall" : "ServiceConfigChange",
                ["Action.EventLogId"] = systemEventId, ["Common.EventUUId"] = eventId + "-system",
                ["Common.EventTime"] = occurredAt.ToUnixTimeMilliseconds(), ["Common.Mid"] = "service-fixture-host",
                ["Environment.HostName"] = "SERVICE-FIXTURE", ["Parent.ProcPid"] = 748,
                ["Parent.FileName"] = "services.exe", ["Parent.FilePath"] = @"C:\Windows\System32\services.exe",
                ["Parent.ProcCmdline"] = @"C:\Windows\System32\services.exe", ["Child.ServiceName"] = serviceName,
                ["Child.DisplayName"] = displayName, ["Child.ServiceFileName"] = binaryPath,
                ["Child.StartType"] = "disabled", ["Child.OldStartType"] = definition.Operation == "modify" ? "demand" : null,
                ["Child.ServiceAccount"] = "LocalSystem", ["Child.ServiceType"] = "win32_own_process",
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"service_{definition.Operation}.yaml"));
        }

        var localPath = Path.Combine(fixture.Path, "service-local.json");
        var genericPath = Path.Combine(fixture.Path, "service-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "service-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-service-activity-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "service-generic-validation.json")));
        var tencentResult = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "service-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 3,
            $"通用服务映射应使三项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencentResult["summary"]?["pass"]?.GetValue<int>() == 3,
            $"腾讯服务路由应使三项 BASELINE 全部通过：{tencentResult.ToJsonString(JsonDefaults.Options)}");
        foreach (var operation in new[] { "create", "modify" })
        {
            var capability = tencentResult["capabilities"]?.AsArray().Single(value =>
                value?["capability_id"]?.GetValue<string>() == $"win.service.{operation}")?.AsObject()
                ?? throw new InvalidOperationException($"腾讯比较结果缺少服务 {operation} 能力。");
            Assert(capability["method_results"]?.AsArray().Count == 2
                && capability["method_results"]?.AsArray().All(value => value?["status"]?.GetValue<string>() == "PASS") == true,
                $"服务 {operation} 必须分别输出 API Hook 与 System Event 两个通过的方法结果：{capability["method_results"]?.ToJsonString(JsonDefaults.Options)}");
        }
        var delete = tencentResult["capabilities"]?.AsArray().Single(value =>
            value?["capability_id"]?.GetValue<string>() == "win.service.delete")?.AsObject()
            ?? throw new InvalidOperationException("腾讯比较结果缺少服务删除能力。");
        Assert(delete["method_results"]?.AsArray().Count == 1
            && delete["method_results"]?[0]?["method_id"]?.GetValue<string>() == "scm_api_hook"
            && delete["method_results"]?[0]?["status"]?.GetValue<string>() == "PASS"
            && delete["method_results"]?[0]?["selected_for_conclusion"]?.GetValue<bool>() == true
            && delete["method_selection"]?["notice"]?.GetValue<string>().Contains("单一测试方法", StringComparison.Ordinal) == true,
            $"服务删除必须输出并采用 DeleteService API Hook 方法结果：{delete["method_results"]?.ToJsonString(JsonDefaults.Options)}");
        var create = tencentResult["capabilities"]?.AsArray().Single(value =>
            value?["capability_id"]?.GetValue<string>() == "win.service.create")?.AsObject()
            ?? throw new InvalidOperationException("腾讯比较结果缺少服务创建能力。");
        Assert(create["edr_candidates"]?.AsArray().Any(candidate =>
            candidate?["baseline_matches"]?.AsArray().Any(match =>
                match?["canonical_field"]?.GetValue<string>() == "service.name"
                && match?["raw_json_pointer"]?.GetValue<string>() == "/Child.ServiceName") == true) == true,
            "服务创建候选必须把 Child.ServiceName 映射回 JSON 对照高亮。");
        return Task.CompletedTask;
    }

    private static Task TestHashAlgorithmsComparison()
    {
        using var fixture = TestDirectory.Create();
        var repository = FindRepositoryRoot();
        var local = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = Ids.NewUuid7(),
                ["host"] = new JsonObject { ["hostname"] = "HASH-FIXTURE", ["machine_id"] = "hash-fixture-host" },
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
            (Capability: "win.hash.md5", Operation: "md5", Extension: ".json", Digest: "0123456789abcdef0123456789abcdef"),
            (Capability: "win.hash.sha", Operation: "sha", Extension: ".json", Digest: new string('a', 64)),
            (Capability: "win.hash.imphash", Operation: "imphash", Extension: ".exe", Digest: "fedcba9876543210fedcba9876543210"),
        };
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var caseRunId = Ids.NewUuid7();
            var localEventId = Ids.NewUuid7();
            var occurredAt = baseTime.AddSeconds(index * 10);
            var actorPid = 12_000 + index;
            var actorPath = $@"C:\EDR-Test\Hash{definition.Operation}.Actor.exe";
            var filePath = $@"C:\EDR-Test\work\hash-{index}{definition.Extension}";
            var md5 = definition.Operation == "md5" ? definition.Digest : new string('b', 32);
            var sha1 = new string('c', 40);
            var sha256 = definition.Operation == "sha" ? definition.Digest : new string('d', 64);
            var sha512 = new string('e', 128);
            var imphash = definition.Operation == "imphash" ? definition.Digest : null;

            local["capabilities"]!.AsArray().Add(new JsonObject
            {
                ["case_run_id"] = caseRunId, ["capability_id"] = definition.Capability,
                ["capability_version"] = "0.1.0", ["display_name_zh"] = definition.Operation,
                ["display_name_en"] = definition.Operation, ["status"] = "LOCAL_PASS",
                ["nonce"] = $"hash-fixture-{index}", ["started_at_utc"] = Values.Utc(occurredAt.AddSeconds(-1)),
                ["ended_at_utc"] = Values.Utc(occurredAt.AddSeconds(2)),
            });
            local["programs"]!.AsArray().Add(new JsonObject
            {
                ["program_instance_id"] = Ids.NewUuid7(), ["case_run_id"] = caseRunId, ["role"] = "actor",
                ["pid"] = actorPid, ["file_name"] = Path.GetFileName(actorPath), ["executable"] = actorPath,
                ["command_line"] = $"{actorPath} --operation {definition.Operation}",
            });
            local["local_events"]!.AsArray().Add(new JsonObject
            {
                ["local_event_id"] = localEventId, ["case_run_id"] = caseRunId, ["sequence"] = 1,
                ["event_type"] = "hash", ["event_action"] = definition.Operation,
                ["occurred_at_utc"] = Values.Utc(occurredAt),
                ["data"] = new JsonObject
                {
                    ["kind"] = "hash", ["operation"] = definition.Operation, ["file_path"] = filePath,
                    ["algorithm"] = definition.Operation == "sha" ? "sha256" : definition.Operation,
                },
            });
            var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hash.operation_succeeded"] = true, ["hash.occurred_at_utc"] = Values.Utc(occurredAt),
                ["hash.actor_pid"] = actorPid, ["hash.actor_executable"] = actorPath,
                ["hash.extension"] = definition.Extension, ["hash.path"] = filePath,
                ["hash.file_size_bytes"] = definition.Operation == "imphash" ? 150_528 : 8_192,
                ["hash.algorithm"] = definition.Operation == "sha" ? "sha256" : definition.Operation,
                ["hash.digest"] = definition.Digest, ["hash.md5"] = md5, ["hash.sha1"] = sha1,
                ["hash.sha256"] = sha256, ["hash.sha512"] = sha512, ["hash.imphash"] = imphash,
                ["hash.is_portable_executable"] = definition.Operation == "imphash",
                ["hash.import_count"] = definition.Operation == "imphash" ? 118 : null,
                ["hash.source_pe_sha256"] = definition.Operation == "imphash" ? sha256 : null,
                ["hash.source_matches_target"] = definition.Operation == "imphash" ? true : null,
            };
            foreach (var (key, value) in facts)
            {
                if (value is not null) local["local_facts"]!.AsArray().Add(Fact(caseRunId, key, value));
            }

            var size = definition.Operation == "imphash" ? 150_528 : 8_192;
            genericCloud.Add(new JsonObject
            {
                ["table"] = "HashAlgorithms", ["event_id"] = localEventId, ["host_id"] = "hash-fixture-host",
                ["host_name"] = "HASH-FIXTURE", ["event_time"] = Values.Utc(occurredAt.AddMilliseconds(4)),
                ["actor_pid"] = actorPid, ["actor_name"] = Path.GetFileName(actorPath), ["actor_executable"] = actorPath,
                ["file_path"] = filePath, ["file_name"] = Path.GetFileName(filePath), ["file_size"] = size,
                ["file_format"] = definition.Operation == "imphash" ? "PE32+ executable" : "JSON",
                ["file_md5"] = md5, ["file_sha1"] = sha1, ["file_sha256"] = sha256,
                ["file_sha512"] = sha512, ["file_imphash"] = imphash,
                ["file_sha_type"] = definition.Operation == "sha" ? 3 : null,
            });
            tencentCloud.Add(new JsonObject
            {
                ["OS"] = "Windows", ["@table"] = "FileEvents", ["@timestamp"] = Values.Utc(occurredAt.AddMilliseconds(4)),
                ["Action.Type"] = "File", ["Action.Name"] = "FileWriteClose", ["Child.FileCreateOpName"] = "新建文件",
                ["Common.EventUUId"] = localEventId, ["Common.EventTime"] = occurredAt.AddMilliseconds(4).ToUnixTimeMilliseconds(),
                ["Common.Mid"] = "hash-fixture-host", ["Environment.HostName"] = "HASH-FIXTURE",
                ["Parent.ProcPid"] = actorPid, ["Parent.FileName"] = Path.GetFileName(actorPath), ["Parent.FilePath"] = actorPath,
                ["Child.FilePath"] = filePath, ["Child.FileName"] = Path.GetFileName(filePath), ["Child.FileSize"] = size,
                ["Child.FileFormat"] = definition.Operation == "imphash" ? "PE32+ executable" : "JSON",
                ["Child.FileMd5"] = md5, ["Child.FileSha"] = sha256,
                ["Child.FileShaType"] = definition.Operation == "sha" ? 3 : null,
                ["Child.FileImpHash"] = imphash,
            });
            baselinePaths.Add(Path.Combine(repository, "baselines", "windows", $"hash_{definition.Operation}.yaml"));
        }

        var localPath = Path.Combine(fixture.Path, "hash-local.json");
        var genericPath = Path.Combine(fixture.Path, "hash-generic.json");
        var tencentPath = Path.Combine(fixture.Path, "hash-tencent.json");
        File.WriteAllText(localPath, local.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(genericPath, genericCloud.ToJsonString(JsonDefaults.Options));
        File.WriteAllText(tencentPath, tencentCloud.ToJsonString(JsonDefaults.Options));
        var generic = CompareService.Compare(new CompareRequest(localPath, [genericPath],
            Path.Combine(repository, "mappings", "generic-hash-algorithms-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "hash-generic-validation.json")));
        var tencent = CompareService.Compare(new CompareRequest(localPath, [tencentPath],
            Path.Combine(repository, "mappings", "tencent-edr-proc-events-v1.yaml"), baselinePaths,
            Path.Combine(fixture.Path, "hash-tencent-validation.json")));
        Assert(generic["summary"]?["pass"]?.GetValue<int>() == 3,
            $"通用哈希映射应使三项 BASELINE 全部通过：{generic.ToJsonString(JsonDefaults.Options)}");
        Assert(tencent["summary"]?["pass"]?.GetValue<int>() == 3,
            $"腾讯哈希字段路由应使三项 BASELINE 全部通过：{tencent.ToJsonString(JsonDefaults.Options)}");
        var rawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["win.hash.md5"] = "/Child.FileMd5",
            ["win.hash.sha"] = "/Child.FileSha",
            ["win.hash.imphash"] = "/Child.FileImpHash",
        };
        foreach (var (capabilityId, rawPointer) in rawFields)
        {
            var capability = tencent["capabilities"]?.AsArray().Single(value =>
                value?["capability_id"]?.GetValue<string>() == capabilityId)?.AsObject()
                ?? throw new InvalidOperationException($"腾讯比较结果缺少哈希能力：{capabilityId}");
            Assert(capability["edr_candidates"]?.AsArray().Any(candidate =>
                candidate?["baseline_matches"]?.AsArray().Any(match =>
                    match?["raw_json_pointer"]?.GetValue<string>() == rawPointer
                    && match?["status"]?.GetValue<string>() == "passed") == true) == true,
                $"{capabilityId} 必须将核心摘要字段映射回 JSON 对照高亮：{rawPointer}");
        }
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
        var configuredSubtestDelays = local["local_facts"]?.AsArray()
            .Where(value => value?["key"]?.GetValue<string>() == "execution.inter_subtest_delay_ms")
            .Select(value => value?["value"]?.GetValue<int>())
            .ToArray() ?? [];
        Assert(configuredSubtestDelays.SequenceEqual(new int?[] { 1_000, 1_000 }), "每项能力都应保存 Runner 实际使用的子测试间隔。");
        Assert(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), "能力之间应执行配置的等待时间。");
        Assert(updates.Any(value => value.Kind == "waiting_next" && value.WaitRemainingSeconds == 1), "进度流应包含下一项能力倒计时。");
        var starts = updates.Where(value => value.Kind == "capability_started").Select(value => value.CapabilityId).ToArray();
        Assert(starts.SequenceEqual(new[] { "win.process.create", "win.process.terminate" }), "能力开始事件必须保持用户选择顺序。");
        Assert(new RunRequest([], "runs", null, false).InterCapabilityDelaySeconds == 3, "能力间默认等待时间应为 3 秒。");
        Assert(new RunRequest([], "runs", null, false).InterSubtestDelayMilliseconds == 1_000, "子测试间默认等待时间应为 1000 ms。");
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
            database.AddCleanup(new CleanupObservation
            {
                CaseRunId = invocation.CaseRunId,
                Sequence = 2,
                Action = "verify_fixture_artifact_absent",
                Status = "succeeded",
                StartedAtUtc = observedAt.AddMilliseconds(22),
                EndedAtUtc = observedAt.AddMilliseconds(23),
                Before = new JsonObject { ["exists"] = false },
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
