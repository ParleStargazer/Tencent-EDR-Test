using System.Globalization;
using System.Collections;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EdrTest;

public sealed record CompareRequest(
    string LocalExportPath,
    IReadOnlyList<string> CloudPaths,
    string MappingPath,
    IReadOnlyList<string> BaselinePaths,
    string OutputPath,
    string? CloudManifestPath = null,
    string? ConclusionOutputPath = null,
    string? ComparisonId = null);

public sealed class MappingProfile
{
    public required string SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public string? Vendor { get; init; }
    public string? Product { get; init; }
    public string? Description { get; init; }
    public required MappingInput Input { get; init; }
    public required List<MappingRoute> Routes { get; init; }
}

public sealed class MappingInput
{
    public required MappingRecordSelector RecordSelector { get; init; }
    public string? EventIdField { get; init; }
    public string? HostIdField { get; init; }
    public string? HostNameField { get; init; }
    public MappingEventTime? EventTime { get; init; }
}

public sealed class MappingEventTime
{
    public required string Field { get; init; }
    public required string Format { get; init; }
}

public sealed class MappingRecordSelector
{
    public Dictionary<string, object?> All { get; init; } = [];
}

public sealed class MappingRoute
{
    public required string RouteId { get; init; }
    public Dictionary<string, object?> When { get; init; } = [];
    public Dictionary<string, MappingRule> Canonical { get; init; } = [];
}

public sealed class MappingRule
{
    public object? Constant { get; init; }
    public string? Source { get; init; }
    public List<string> Transform { get; init; } = [];
    private object? onEmpty;
    private object? onZero;
    private object? onError;

    public object? OnEmpty
    {
        get => onEmpty;
        init
        {
            HasOnEmpty = true;
            onEmpty = value;
        }
    }

    public object? OnZero
    {
        get => onZero;
        init
        {
            HasOnZero = true;
            onZero = value;
        }
    }

    public object? OnError
    {
        get => onError;
        init
        {
            HasOnError = true;
            onError = value;
        }
    }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasOnEmpty { get; private init; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasOnZero { get; private init; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasOnError { get; private init; }
}

public sealed class BaselineDefinition
{
    public required string SchemaVersion { get; init; }
    public required string BaselineId { get; init; }
    public required string Version { get; init; }
    public string? Title { get; init; }
    public string? RiskLevel { get; init; }
    public required BaselineCapability Capability { get; init; }
    public List<BaselineAssertion> LocalRequirements { get; init; } = [];
    public required CorrelationDefinition Correlation { get; init; }
    public List<CloudExpectation> CloudExpectations { get; init; } = [];
}

public sealed class BaselineCapability
{
    public required string Id { get; init; }
    public required string Version { get; init; }
}

public sealed class BaselineAssertion
{
    public required string Field { get; init; }
    public required string Operator { get; init; }
    public object? Expected { get; init; }
    public string? ExpectedFromLocal { get; init; }
    public string Severity { get; init; } = "required";
    public List<string> Normalizers { get; init; } = [];
}

public sealed class CorrelationDefinition
{
    public int TimeBeforeSeconds { get; init; }
    public int TimeAfterSeconds { get; init; }
    public List<CorrelationAnchor> Anchors { get; init; } = [];
}

public sealed class CorrelationAnchor
{
    public required string LocalField { get; init; }
    public required string CloudField { get; init; }
    public required string Strength { get; init; }
    public List<string> Normalizers { get; init; } = [];
}

public sealed class CloudExpectation
{
    public required string Id { get; init; }
    public required string EventType { get; init; }
    public List<string> EventActions { get; init; } = [];
    public required CardinalityDefinition Cardinality { get; init; }
    public ExpectationCorrelationDefinition? Correlation { get; init; }
    public List<BaselineAssertion> Assertions { get; init; } = [];
}

public sealed class ExpectationCorrelationDefinition
{
    public string? TimeFromLocal { get; init; }
    public List<CorrelationAnchor> Anchors { get; init; } = [];
}

public sealed class CardinalityDefinition
{
    public int Min { get; init; }
    public int? Max { get; init; }
}

internal sealed record CanonicalEvent(
    string RawRef,
    Dictionary<string, object?> Fields,
    Dictionary<string, string?> SourceFields,
    JsonObject Raw)
{
    public object? Get(string field) => Fields.TryGetValue(field, out var value) ? value : null;
    public string? SourceField(string field) => SourceFields.TryGetValue(field, out var value) ? value : null;
}

internal sealed record Candidate(CanonicalEvent Event, double Score, long TimeDistanceMs, IReadOnlyList<string> MatchedAnchors);
internal sealed record CloudRecordObservation(DateTimeOffset? EventTime, string? HostId, string? HostName);
internal sealed record CloudLoadResult(IReadOnlyList<CanonicalEvent> Events, IReadOnlyList<CloudRecordObservation> Observations);

public static class CompareService
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static JsonObject Compare(CompareRequest request)
    {
        ValidateInputs(request);
        var localPath = Path.GetFullPath(request.LocalExportPath);
        var mappingPath = Path.GetFullPath(request.MappingPath);
        var localRoot = JsonNode.Parse(File.ReadAllText(localPath)) as JsonObject ?? throw new InvalidDataException("本地导出必须是 JSON 对象。");
        var mapping = ReadYaml<MappingProfile>(mappingPath);
        ValidateMapping(mapping);
        var baselines = request.BaselinePaths.Select(path => (Path: Path.GetFullPath(path), Value: ReadYaml<BaselineDefinition>(path))).ToArray();
        foreach (var baseline in baselines) ValidateBaseline(baseline.Value);
        var baselinesByCapability = baselines
            .GroupBy(value => value.Value.Capability.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var cloud = LoadCloud(request.CloudPaths, mapping);
        var cloudManifest = request.CloudManifestPath is null
            ? null
            : JsonNode.Parse(File.ReadAllText(request.CloudManifestPath)) as JsonObject
                ?? throw new InvalidDataException("云端导出清单必须是 JSON 对象。");
        var manifestFilesVerified = cloudManifest is not null
            && ManifestFilesMatch(cloudManifest, request.CloudManifestPath!, request.CloudPaths);
        var results = new JsonArray();

        foreach (var capabilityNode in localRoot["capabilities"]?.AsArray() ?? throw new InvalidDataException("本地导出缺少 capabilities。"))
        {
            var capability = capabilityNode?.AsObject() ?? throw new InvalidDataException("capabilities 元素必须是对象。");
            var capabilityId = RequiredString(capability, "capability_id");
            var capabilityVersion = RequiredString(capability, "capability_version");
            var caseRunId = RequiredString(capability, "case_run_id");
            var localStatus = RequiredString(capability, "status");
            JsonObject result;
            if (!baselinesByCapability.TryGetValue(capabilityId, out var capabilityBaselines))
            {
                result = NotCompared(capabilityId, caseRunId, localStatus, "没有匹配的 BASELINE。");
                DecorateCapabilityResult(result, capability, null);
            }
            else
            {
                var matchingBaselines = capabilityBaselines
                    .Where(value => string.Equals(value.Value.Capability.Version, capabilityVersion, StringComparison.Ordinal))
                    .ToArray();
                if (matchingBaselines.Length == 0)
                {
                    result = NotCompared(capabilityId, caseRunId, localStatus,
                        $"没有与能力版本 {capabilityVersion} 匹配的 BASELINE；不会使用其他版本的本地条件误判采集失败。");
                    DecorateCapabilityResult(result, capability, null);
                }
                else if (matchingBaselines.Length > 1)
                {
                    throw new InvalidDataException($"能力 {capabilityId} {capabilityVersion} 存在多份 BASELINE，无法唯一选择。");
                }
                else
                {
                    var baselineEntry = matchingBaselines[0];
                    result = CompareCapability(localRoot, capability, baselineEntry.Value, cloud, cloudManifest, manifestFilesVerified);
                    DecorateCapabilityResult(result, capability, baselineEntry.Value);
                }
            }
            AttachJsonComparisonEvidence(result, localRoot, capability);
            results.Add(result);
        }

        var summary = Summarize(results);
        var conclusion = BuildConclusion(results, summary);
        var root = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["comparison_id"] = request.ComparisonId ?? Ids.NewUuid7(),
            ["compared_at_utc"] = Values.Utc(DateTimeOffset.UtcNow),
            ["comparator_version"] = EdrTestVersion.Current,
            ["inputs"] = new JsonObject
            {
                ["local_export"] = FileReference(localPath),
                ["cloud_exports"] = new JsonArray(request.CloudPaths.Select(path => (JsonNode)FileReference(Path.GetFullPath(path))).ToArray()),
                ["cloud_manifest"] = request.CloudManifestPath is null ? null : FileReference(Path.GetFullPath(request.CloudManifestPath)),
                ["mapping_profiles"] = new JsonArray(new JsonObject
                {
                    ["id"] = mapping.ProfileId,
                    ["version"] = mapping.SchemaVersion,
                    ["sha256"] = Hashing.FileSha256(mappingPath),
                }),
                ["baselines"] = new JsonArray(baselines.Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = x.Value.BaselineId,
                    ["version"] = x.Value.Version,
                    ["sha256"] = Hashing.FileSha256(x.Path),
                }).ToArray()),
            },
            ["summary"] = summary,
            ["conclusion"] = conclusion,
            ["capabilities"] = results,
        };

        var output = Path.GetFullPath(request.OutputPath);
        var conclusionOutput = Path.GetFullPath(request.ConclusionOutputPath ?? ConclusionExportService.DefaultOutputPath(output));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, root.ToJsonString(JsonDefaults.Options) + Environment.NewLine, new UTF8Encoding(false));
        ConclusionExportService.Export(root, conclusionOutput);
        return root;
    }

    private static JsonObject CompareCapability(
        JsonObject localRoot,
        JsonObject capability,
        BaselineDefinition baseline,
        CloudLoadResult cloud,
        JsonObject? cloudManifest,
        bool manifestFilesVerified)
    {
        var capabilityId = RequiredString(capability, "capability_id");
        var caseRunId = RequiredString(capability, "case_run_id");
        var localStatus = RequiredString(capability, "status");
        var warnings = new JsonArray();
        var resolver = new LocalResolver(localRoot, capability);
        var outputRequirements = new JsonArray();
        var localEvaluations = baseline.LocalRequirements
            .Select((requirement, index) => (Requirement: requirement, Index: index, Evaluation: Evaluate(requirement, resolver.Resolve(requirement.Field), resolver)))
            .ToArray();
        foreach (var item in localEvaluations)
        {
            outputRequirements.Add(RequirementJson($"local-{item.Index + 1}", "local", null, item.Evaluation));
        }
        if (localStatus != "LOCAL_PASS")
        {
            AddUnevaluatedCloudRequirements(outputRequirements, baseline, resolver, "本地能力未通过，因此未检查云端要求。");
            return CapabilityResult(caseRunId, capabilityId, localStatus, "NOT_COMPARED", "insufficient", 0, null, new JsonArray(), new JsonArray(), outputRequirements, new JsonArray("本地能力未达到 LOCAL_PASS。"));
        }

        var failedLocal = localEvaluations
            .Select(value => value.Evaluation)
            .Where(x => x.Status != "passed")
            .ToArray();
        if (failedLocal.Length > 0)
        {
            foreach (var assertion in failedLocal) warnings.Add($"本地前置断言未通过：{assertion.Field}");
            AddUnevaluatedCloudRequirements(outputRequirements, baseline, resolver, "本地前置要求未满足，因此未检查云端要求。");
            return CapabilityResult(caseRunId, capabilityId, localStatus, "INCONCLUSIVE", "insufficient", 0, null, new JsonArray(), new JsonArray(), outputRequirements, warnings);
        }

        var start = DateTimeOffset.Parse(RequiredString(capability, "started_at_utc"), CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(RequiredString(capability, "ended_at_utc"), CultureInfo.InvariantCulture);
        var exportCoverage = DetermineExportCoverage(localRoot, start, end, cloud.Observations, cloudManifest, manifestFilesVerified);
        var defaultCorrelationTime = LocalCorrelationTime(localRoot, caseRunId, start);
        var totalCandidates = 0;
        var outputAssertions = new JsonArray();
        var outputCandidates = new JsonArray();
        JsonObject? firstSelected = null;
        var overallStatus = "PASS";

        foreach (var expectation in baseline.CloudExpectations)
        {
            var expectationAnchors = expectation.Correlation?.Anchors is { Count: > 0 }
                ? expectation.Correlation.Anchors
                : baseline.Correlation.Anchors;
            var correlationTime = ResolveCorrelationTime(expectation.Correlation?.TimeFromLocal, resolver, defaultCorrelationTime);
            var candidates = cloud.Events
                .Where(item => EventMatches(item, expectation, start, end, baseline.Correlation))
                .Select(item => Score(item, expectationAnchors, resolver, correlationTime))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.TimeDistanceMs)
                .ThenBy(item => item.Event.RawRef, StringComparer.Ordinal)
                .ToArray();
            totalCandidates += candidates.Length;
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                outputCandidates.Add(CandidateJson(
                    expectation.Id,
                    candidateIndex + 1,
                    candidates[candidateIndex],
                    expectation,
                    expectationAnchors,
                    resolver));
            }
            var cardinalityStatus = candidates.Length < expectation.Cardinality.Min
                ? exportCoverage is "verified" or "inferred" or "assumed" ? "failed" : "not_evaluated"
                : expectation.Cardinality.Max is { } cardinalityMaximum && candidates.Length > cardinalityMaximum
                    ? "not_evaluated"
                    : "passed";
            var cardinalityMessage = cardinalityStatus switch
            {
                "failed" => "EDR 日志范围足以形成判断，但没有找到要求数量的事件。",
                "not_evaluated" when candidates.Length < expectation.Cardinality.Min => "日志范围不足，无法确认事件是否真的缺失。",
                "not_evaluated" => "候选事件过多，无法唯一关联。",
                _ => null,
            };
            outputRequirements.Add(CardinalityRequirementJson(expectation, candidates.Length, cardinalityStatus, cardinalityMessage));
            if (candidates.Length < expectation.Cardinality.Min)
            {
                AddUnevaluatedExpectationRequirements(outputRequirements, expectation, resolver, "没有找到可关联的 EDR 事件，无法检查该项。", includeCardinality: false);
                if (exportCoverage is "verified" or "inferred" or "assumed")
                {
                    warnings.Add($"未找到满足“{baseline.Title ?? capabilityId}”基准的 EDR 云端事件（{expectation.Id}：找到 {candidates.Length} 条，至少需要 {expectation.Cardinality.Min} 条；导出覆盖状态：{exportCoverage}）。");
                    overallStatus = Worse(overallStatus, "FAIL");
                }
                else
                {
                    warnings.Add($"未找到满足“{baseline.Title ?? capabilityId}”基准的 EDR 云端事件，但当前日志不足以证明导出范围完整（{expectation.Id}：找到 {candidates.Length} 条，至少需要 {expectation.Cardinality.Min} 条）。");
                    overallStatus = Worse(overallStatus, "INCONCLUSIVE");
                }
                continue;
            }
            if (expectation.Cardinality.Max is { } maximum && candidates.Length > maximum)
            {
                warnings.Add($"“{baseline.Title ?? capabilityId}”的候选事件过多，无法唯一判定（{expectation.Id}：找到 {candidates.Length} 条，最多允许 {maximum} 条）。");
                overallStatus = Worse(overallStatus, "INCONCLUSIVE");
            }
            if (candidates.Length == 0) continue;
            if (candidates.Length > 1 && candidates[0].Score == candidates[1].Score && candidates[0].TimeDistanceMs == candidates[1].TimeDistanceMs)
            {
                warnings.Add($"“{baseline.Title ?? capabilityId}”存在多个同分候选事件，无法唯一关联（{expectation.Id}）。");
                overallStatus = Worse(overallStatus, "INCONCLUSIVE");
                AddUnevaluatedExpectationRequirements(outputRequirements, expectation, resolver, "存在多个同分候选事件，无法确定应检查哪一条。", includeCardinality: false);
                continue;
            }

            var selected = candidates[0];
            firstSelected ??= new JsonObject
            {
                ["raw_ref"] = selected.Event.RawRef,
                ["event_id"] = Values.ToNode(selected.Event.Get("event.id")),
                ["correlation_score"] = selected.Score,
                ["confidence"] = CandidateConfidence(selected),
                ["time_distance_ms"] = selected.TimeDistanceMs,
                ["matched_anchors"] = new JsonArray(selected.MatchedAnchors.Select(x => (JsonNode)x).ToArray()),
            };
            foreach (var assertion in expectation.Assertions)
            {
                var evaluated = Evaluate(assertion, selected.Event.Get(assertion.Field), resolver);
                var fieldName = baseline.CloudExpectations.Count == 1 ? evaluated.Field : $"{expectation.Id}:{evaluated.Field}";
                outputAssertions.Add(evaluated.ToJson(fieldName));
                outputRequirements.Add(RequirementJson($"{expectation.Id}-{expectation.Assertions.IndexOf(assertion) + 1}", "cloud", expectation.Id, evaluated));
                if (evaluated.Status == "not_evaluated" && assertion.Severity == "required") overallStatus = Worse(overallStatus, "INCONCLUSIVE");
                else if (evaluated.Status == "not_evaluated" && assertion.Severity == "recommended") overallStatus = Worse(overallStatus, "PARTIAL");
                else if (evaluated.Status == "failed" && assertion.Severity == "required") overallStatus = Worse(overallStatus, "FAIL");
                else if (evaluated.Status == "failed" && assertion.Severity == "recommended") overallStatus = Worse(overallStatus, "PARTIAL");
            }
        }

        if (baseline.CloudExpectations.Count == 0)
        {
            warnings.Add("BASELINE 没有 cloud_expectations。");
            overallStatus = "INCONCLUSIVE";
        }
        return CapabilityResult(caseRunId, capabilityId, localStatus, overallStatus, exportCoverage, totalCandidates, firstSelected, outputCandidates, outputAssertions, outputRequirements, warnings);
    }

    private static void AddUnevaluatedCloudRequirements(JsonArray output, BaselineDefinition baseline, LocalResolver resolver, string message)
    {
        foreach (var expectation in baseline.CloudExpectations)
        {
            AddUnevaluatedExpectationRequirements(output, expectation, resolver, message);
        }
    }

    private static void AddUnevaluatedExpectationRequirements(JsonArray output, CloudExpectation expectation, LocalResolver resolver, string message, bool includeCardinality = true)
    {
        if (includeCardinality) output.Add(CardinalityRequirementJson(expectation, null, "not_evaluated", message));
        for (var index = 0; index < expectation.Assertions.Count; index++)
        {
            var definition = expectation.Assertions[index];
            var evaluated = Evaluate(definition, null, resolver) with { Status = "not_evaluated", Actual = null, Message = message };
            output.Add(RequirementJson($"{expectation.Id}-{index + 1}", "cloud", expectation.Id, evaluated));
        }
    }

    private static JsonObject CardinalityRequirementJson(CloudExpectation expectation, int? actual, string status, string? message) => new()
    {
        ["requirement_id"] = $"{expectation.Id}-cardinality",
        ["scope"] = "cloud",
        ["title_zh"] = $"必须找到至少 {expectation.Cardinality.Min} 条 {EventActionTitle(expectation.EventActions)} EDR 事件",
        ["expectation_id"] = expectation.Id,
        ["field"] = "event.count",
        ["operator"] = "range",
        ["severity"] = "required",
        ["status"] = status,
        ["expected"] = new JsonObject { ["min"] = expectation.Cardinality.Min, ["max"] = expectation.Cardinality.Max },
        ["actual"] = actual,
        ["message"] = message,
    };

    private static JsonObject RequirementJson(string requirementId, string scope, string? expectationId, AssertionEvaluation evaluation)
    {
        var result = evaluation.ToJson();
        result["requirement_id"] = requirementId;
        result["scope"] = scope;
        result["title_zh"] = RequirementTitle(evaluation.Field, evaluation.Operator);
        result["expectation_id"] = expectationId;
        return result;
    }

    internal static string RequirementTitle(string field, string @operator)
    {
        var subject = field switch
        {
            "facts.process.create_succeeded" => "本地进程创建行为",
            "programs.actor.pid" => "行为执行程序 PID",
            "programs.actor.command_line" => "行为执行程序命令行",
            "programs.actor.executable" => "行为执行程序路径",
            "programs.target.pid" => "被测目标进程 PID",
            "programs.target.command_line" => "被测目标进程命令行",
            "programs.target.executable" => "被测目标程序路径",
            "process.pid" => "EDR 记录的目标进程 PID",
            "process.executable" => "EDR 记录的目标程序路径",
            "process.command_line" => "EDR 记录的目标进程命令行",
            "process.entity_id" => "EDR 进程唯一标识",
            "process.hash.md5" => "EDR 记录的进程 MD5",
            "parent_process.pid" or "source_process.pid" => "EDR 记录的发起进程 PID",
            "parent_process.executable" or "source_process.executable" => "EDR 记录的发起程序路径",
            "file.path" => "EDR 记录的文件路径",
            "file.name" => "EDR 记录的文件名",
            "thread.id" => "EDR 记录的线程 ID",
            _ when field.StartsWith("facts.", StringComparison.Ordinal) => "本地行为证据",
            _ => $"字段 {field}",
        };
        var requirement = @operator switch
        {
            "present" => "必须有值",
            "absent" => "必须为空",
            "equals" or "ref_equals" => "必须与本地事实一致",
            "not_equals" => "必须与禁用值不同",
            "contains" => "必须包含本轮测试标记",
            "one_of" => "必须属于允许值",
            "range" => "必须在允许范围内",
            "timestamp_between" => "必须落在测试时间窗内",
            _ => $"必须满足 {@operator}",
        };
        return $"{subject}{requirement}";
    }

    internal static string EventActionTitle(IReadOnlyList<string> actions) => string.Join("/", actions.Select(action => action switch
    {
        "create" => "进程创建",
        "terminate" => "进程终止",
        "access" => "进程访问",
        "image_load" => "镜像或动态库加载",
        "remote_thread_create" => "远程线程创建",
        "tamper" => "进程篡改",
        _ => action,
    }));

    private static string DetermineExportCoverage(
        JsonObject localRoot,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<CloudRecordObservation> observations,
        JsonObject? manifest,
        bool manifestFilesVerified)
    {
        var localHost = localRoot["run"]?["host"] as JsonObject;
        var localHostname = localHost?["hostname"]?.GetValue<string>();
        var localIds = new[]
        {
            localHost?["machine_id"]?.GetValue<string>(),
            localHost?["agent_id_hint"]?.GetValue<string>(),
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (manifest is not null)
        {
            var queryWindow = manifest["query_window"] as JsonObject;
            var hostFilter = manifest["host_filter"] as JsonObject;
            var windowVerified = DateTimeOffset.TryParse(queryWindow?["start_utc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var queryStart)
                && DateTimeOffset.TryParse(queryWindow?["end_utc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var queryEnd)
                && queryStart <= start
                && queryEnd >= end;
            var manifestHostname = hostFilter?["hostname"]?.GetValue<string>();
            var manifestHostId = hostFilter?["host_id"]?.GetValue<string>();
            var hostVerified = (!string.IsNullOrWhiteSpace(localHostname)
                    && !string.IsNullOrWhiteSpace(manifestHostname)
                    && string.Equals(localHostname, manifestHostname, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(manifestHostId) && localIds.Contains(manifestHostId));
            return windowVerified && hostVerified && manifestFilesVerified ? "verified" : "insufficient";
        }

        var matchingTimes = observations
            .Where(item => HostMatches(item, localHostname, localIds))
            .Select(item => item.EventTime)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return matchingTimes.Length > 1 && matchingTimes.Min() <= start && matchingTimes.Max() >= end
            ? "inferred"
            : "insufficient";
    }

    private static bool HostMatches(CloudRecordObservation observation, string? localHostname, IReadOnlySet<string> localIds) =>
        (!string.IsNullOrWhiteSpace(localHostname)
            && !string.IsNullOrWhiteSpace(observation.HostName)
            && string.Equals(localHostname, observation.HostName, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(observation.HostId) && localIds.Contains(observation.HostId));

    private static Candidate Score(CanonicalEvent item, IReadOnlyList<CorrelationAnchor> anchors, LocalResolver resolver, DateTimeOffset correlationTime)
    {
        double score = 0;
        var matched = new List<string>();
        var matchedStrong = false;
        var requiresStrong = anchors.Any(anchor => anchor.Strength == "strong");
        foreach (var anchor in anchors)
        {
            var local = Normalize(resolver.Resolve(anchor.LocalField), anchor.Normalizers);
            var cloud = Normalize(item.Get(anchor.CloudField), anchor.Normalizers);
            if (!Equivalent(local, cloud) || local is null) continue;
            score += anchor.Strength switch { "strong" => 100, "medium" => 25, "weak" => 5, _ => 0 };
            if (anchor.Strength == "strong") matchedStrong = true;
            matched.Add($"{anchor.LocalField}={anchor.CloudField}");
        }
        var eventTime = CanonicalEventTime(item);
        var distance = eventTime is null
            ? long.MaxValue
            : (long)Math.Min(long.MaxValue, Math.Abs((eventTime.Value - correlationTime).TotalMilliseconds));
        return new Candidate(item, requiresStrong && !matchedStrong ? 0 : score, distance, matched);
    }

    private static DateTimeOffset LocalCorrelationTime(JsonObject localRoot, string caseRunId, DateTimeOffset fallback)
    {
        var eventTime = localRoot["local_events"]?.AsArray()
            .Select(value => value?.AsObject())
            .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
            .OrderBy(value => value?["sequence"]?.GetValue<int>() ?? int.MaxValue)
            .Select(value => value?["occurred_at_utc"]?.GetValue<string>())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return DateTimeOffset.TryParse(eventTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : fallback;
    }

    private static DateTimeOffset ResolveCorrelationTime(string? localField, LocalResolver resolver, DateTimeOffset fallback) =>
        !string.IsNullOrWhiteSpace(localField)
        && resolver.Resolve(localField)?.ToString() is { } value
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : fallback;

    private static DateTimeOffset? CanonicalEventTime(CanonicalEvent item) =>
        item.Get("event.created") is string text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string CandidateConfidence(Candidate candidate) => candidate.Score switch
    {
        >= 200 => "high",
        >= 100 => "medium",
        _ => "low",
    };

    private static JsonObject CandidateJson(
        string expectationId,
        int rank,
        Candidate candidate,
        CloudExpectation expectation,
        IReadOnlyList<CorrelationAnchor> anchors,
        LocalResolver resolver)
    {
        var canonical = new JsonObject();
        foreach (var (field, value) in candidate.Event.Fields.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            canonical[field] = Values.ToNode(value);
        }
        var baselineMatches = new JsonArray();
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var evaluation = Evaluate(new BaselineAssertion
            {
                Field = anchor.CloudField,
                Operator = "equals",
                ExpectedFromLocal = anchor.LocalField,
                Severity = "required",
                Normalizers = anchor.Normalizers,
            }, candidate.Event.Get(anchor.CloudField), resolver);
            baselineMatches.Add(CandidateBaselineMatchJson(
                "correlation",
                $"{expectationId}-anchor-{index + 1}",
                anchor.LocalField,
                candidate.Event,
                evaluation,
                resolver));
        }
        for (var index = 0; index < expectation.Assertions.Count; index++)
        {
            var assertion = expectation.Assertions[index];
            baselineMatches.Add(CandidateBaselineMatchJson(
                "assertion",
                $"{expectationId}-{index + 1}",
                assertion.ExpectedFromLocal,
                candidate.Event,
                Evaluate(assertion, candidate.Event.Get(assertion.Field), resolver),
                resolver));
        }
        return new JsonObject
        {
            ["expectation_id"] = expectationId,
            ["rank"] = rank,
            ["confidence"] = CandidateConfidence(candidate),
            ["correlation_score"] = candidate.Score,
            ["time_distance_ms"] = candidate.TimeDistanceMs,
            ["event_time_utc"] = Values.ToNode(candidate.Event.Get("event.created")),
            ["raw_ref"] = candidate.Event.RawRef,
            ["event_id"] = Values.ToNode(candidate.Event.Get("event.id")),
            ["matched_anchors"] = new JsonArray(candidate.MatchedAnchors.Select(value => (JsonNode)value).ToArray()),
            ["baseline_matches"] = baselineMatches,
            ["canonical_event"] = canonical,
            ["raw_event"] = candidate.Event.Raw.DeepClone(),
        };
    }

    private static JsonObject CandidateBaselineMatchJson(
        string kind,
        string requirementId,
        string? localField,
        CanonicalEvent candidate,
        AssertionEvaluation evaluation,
        LocalResolver resolver)
    {
        var rawField = candidate.SourceField(evaluation.Field);
        return new JsonObject
        {
            ["kind"] = kind,
            ["requirement_id"] = requirementId,
            ["status"] = evaluation.Status,
            ["local_field"] = localField,
            ["local_json_pointer"] = localField is null ? null : resolver.JsonPointer(localField),
            ["canonical_field"] = evaluation.Field,
            ["raw_field"] = rawField,
            ["raw_json_pointer"] = rawField is null ? null : JsonPointer(rawField),
            ["expected"] = Values.ToNode(evaluation.Expected),
            ["actual"] = Values.ToNode(evaluation.Actual),
            ["message"] = evaluation.Message,
        };
    }

    private static string JsonPointer(string property) => "/" + property
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static bool EventMatches(CanonicalEvent item, CloudExpectation expectation, DateTimeOffset start, DateTimeOffset end, CorrelationDefinition correlation)
    {
        if (!Equivalent(item.Get("event.type"), expectation.EventType)) return false;
        if (!expectation.EventActions.Any(action => Equivalent(item.Get("event.action"), action))) return false;
        if (item.Get("event.created") is not string createdText || !DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)) return false;
        return created >= start.AddSeconds(-correlation.TimeBeforeSeconds) && created <= end.AddSeconds(correlation.TimeAfterSeconds);
    }

    private static AssertionEvaluation Evaluate(BaselineAssertion definition, object? actual, LocalResolver resolver)
    {
        object? expected = definition.ExpectedFromLocal is not null ? resolver.Resolve(definition.ExpectedFromLocal) : definition.Expected;
        if (definition.ExpectedFromLocal is not null && expected is null)
        {
            return new AssertionEvaluation(
                definition.Field,
                definition.Operator,
                definition.Severity,
                "not_evaluated",
                null,
                actual,
                $"本地运行结果未采集 {definition.ExpectedFromLocal}，无法形成期望值。");
        }
        if (expected is string template) expected = resolver.Expand(template);
        var normalizedActual = Normalize(actual, definition.Normalizers);
        var normalizedExpected = Normalize(expected, definition.Normalizers);
        bool? passed = definition.Operator switch
        {
            "present" => IsPresent(normalizedActual),
            "absent" => !IsPresent(normalizedActual),
            "equals" or "ref_equals" => Equivalent(normalizedActual, normalizedExpected),
            "not_equals" => !Equivalent(normalizedActual, normalizedExpected),
            "contains" => normalizedActual?.ToString()?.Contains(normalizedExpected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true,
            "regex" when normalizedExpected is not null => Regex.IsMatch(normalizedActual?.ToString() ?? string.Empty, normalizedExpected.ToString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "one_of" when normalizedExpected is IEnumerable values and not string => values.Cast<object?>().Any(value => Equivalent(normalizedActual, value)),
            "range" => InRange(normalizedActual, normalizedExpected),
            "cidr" => InCidr(normalizedActual, normalizedExpected),
            "timestamp_between" => TimestampBetween(normalizedActual, normalizedExpected),
            _ => null,
        };
        return new AssertionEvaluation(definition.Field, definition.Operator, definition.Severity, passed is null ? "not_evaluated" : passed.Value ? "passed" : "failed", expected, actual,
            passed is null ? $"尚未实现操作符：{definition.Operator}" : passed.Value ? null : "实际值不满足期望。");
    }

    private static CloudLoadResult LoadCloud(IReadOnlyList<string> paths, MappingProfile mapping)
    {
        var events = new List<CanonicalEvent>();
        var observations = new List<CloudRecordObservation>();
        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            var text = File.ReadAllText(path);
            var trimmed = text.AsSpan().TrimStart();
            if (!trimmed.IsEmpty && trimmed[0] == '[')
            {
                using var document = JsonDocument.Parse(text);
                var index = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) MapRecord(item, $"{path}#/{index}", mapping, events, observations);
                    index++;
                }
            }
            else
            {
                var index = 0;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) MapRecord(document.RootElement, $"{path}#/{index}", mapping, events, observations);
                    index++;
                }
            }
        }
        return new CloudLoadResult(events, observations);
    }

    private static void MapRecord(
        JsonElement record,
        string rawRef,
        MappingProfile mapping,
        ICollection<CanonicalEvent> output,
        ICollection<CloudRecordObservation> observations)
    {
        if (!Matches(record, mapping.Input.RecordSelector.All)) return;
        observations.Add(new CloudRecordObservation(
            ReadEventTime(record, mapping.Input.EventTime),
            ReadString(record, mapping.Input.HostIdField),
            ReadString(record, mapping.Input.HostNameField)));
        foreach (var route in mapping.Routes)
        {
            if (!Matches(record, route.When)) continue;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            var sourceFields = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (field, rule) in route.Canonical)
            {
                object? value;
                try
                {
                    value = rule.Source is not null && record.TryGetProperty(rule.Source, out var source) ? Scalar(source) : rule.Constant;
                    if (rule.HasOnEmpty && value is string text && string.IsNullOrWhiteSpace(text)) value = rule.OnEmpty;
                    if (rule.HasOnZero && TryDecimal(value, out var number) && number == 0) value = rule.OnZero;
                    foreach (var transform in rule.Transform) value = Transform(value, transform);
                }
                catch (Exception) when (rule.HasOnError)
                {
                    value = rule.OnError;
                }
                fields[field] = value;
                sourceFields[field] = rule.Source;
            }
            output.Add(new CanonicalEvent(
                rawRef,
                fields,
                sourceFields,
                JsonNode.Parse(record.GetRawText())?.AsObject() ?? throw new InvalidDataException("EDR 日志记录必须是 JSON 对象。")));
        }
    }

    private static DateTimeOffset? ReadEventTime(JsonElement record, MappingEventTime? definition)
    {
        if (definition is null || !record.TryGetProperty(definition.Field, out var value)) return null;
        var scalar = Scalar(value);
        return definition.Format switch
        {
            "unix_ms" when TryInt64(scalar, out var milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            "unix_s" when TryInt64(scalar, out var seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds),
            "datetime" when DateTimeOffset.TryParse(scalar?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) => timestamp,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement record, string? field) =>
        !string.IsNullOrWhiteSpace(field) && record.TryGetProperty(field, out var value)
            ? Scalar(value)?.ToString()
            : null;

    private static bool Matches(JsonElement record, IReadOnlyDictionary<string, object?> conditions)
    {
        foreach (var (field, expected) in conditions)
        {
            if (!record.TryGetProperty(field, out var actual) || !Equivalent(Scalar(actual), expected)) return false;
        }
        return true;
    }

    private static object? Transform(object? value, string transform) => transform switch
    {
        "lowercase" => value?.ToString()?.ToLowerInvariant(),
        "trim" => value?.ToString()?.Trim(),
        "windows_path" => NormalizeWindowsPath(value?.ToString()),
        "unix_ms_to_utc" when TryInt64(value, out var milliseconds) => Values.Utc(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)),
        "unix_s_to_utc" when TryInt64(value, out var seconds) => Values.Utc(DateTimeOffset.FromUnixTimeSeconds(seconds)),
        "parse_datetime_to_utc" when DateTimeOffset.TryParse(value?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) => Values.Utc(timestamp),
        _ => value,
    };

    private static object? Normalize(object? value, IReadOnlyList<string> normalizers)
    {
        foreach (var normalizer in normalizers)
        {
            value = normalizer switch
            {
                "lowercase" => value?.ToString()?.ToLowerInvariant(),
                "trim" => value?.ToString()?.Trim(),
                "windows_path" => NormalizeWindowsPath(value?.ToString()),
                "sid" => value?.ToString()?.Trim().ToUpperInvariant(),
                "ip" => value?.ToString()?.Trim().ToLowerInvariant(),
                "timestamp_utc" when DateTimeOffset.TryParse(value?.ToString(), out var timestamp) => Values.Utc(timestamp),
                _ => value,
            };
        }
        return value;
    }

    private static string? NormalizeWindowsPath(string? value) => string.IsNullOrWhiteSpace(value)
        ? value
        : value.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private static bool Equivalent(object? left, object? right)
    {
        left = Unwrap(left);
        right = Unwrap(right);
        if (left is null || right is null) return left is null && right is null;
        if (TryBoolean(left, out var leftBoolean) && TryBoolean(right, out var rightBoolean)) return leftBoolean == rightBoolean;
        if (TryDecimal(left, out var leftNumber) && TryDecimal(right, out var rightNumber)) return leftNumber == rightNumber;
        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static object? Unwrap(object? value) => value switch
    {
        JsonNode node => Values.FromJson(node),
        JsonElement element => Scalar(element),
        _ => value,
    };

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };

    private static bool TryInt64(object? value, out long result) => long.TryParse(Convert.ToString(Unwrap(value), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    private static bool TryDecimal(object? value, out decimal result) => decimal.TryParse(Convert.ToString(Unwrap(value), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    private static bool TryBoolean(object? value, out bool result) => bool.TryParse(Convert.ToString(Unwrap(value), CultureInfo.InvariantCulture), out result);
    private static bool IsPresent(object? value) => value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text));

    private static bool? InRange(object? actual, object? expected)
    {
        if (!TryDecimal(actual, out var number) || !TryBounds(expected, out var minimum, out var maximum)) return null;
        return number >= minimum && number <= maximum;
    }

    private static bool? TimestampBetween(object? actual, object? expected)
    {
        if (!DateTimeOffset.TryParse(Convert.ToString(Unwrap(actual), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) return null;
        if (!TryPair(expected, "start", "end", out var startValue, out var endValue)) return null;
        if (!DateTimeOffset.TryParse(Convert.ToString(Unwrap(startValue), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)) return null;
        if (!DateTimeOffset.TryParse(Convert.ToString(Unwrap(endValue), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)) return null;
        return timestamp >= start && timestamp <= end;
    }

    private static bool? InCidr(object? actual, object? expected)
    {
        var addressText = Convert.ToString(Unwrap(actual), CultureInfo.InvariantCulture);
        var cidr = Convert.ToString(Unwrap(expected), CultureInfo.InvariantCulture);
        if (!IPAddress.TryParse(addressText, out var address) || string.IsNullOrWhiteSpace(cidr)) return null;
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix)) return null;
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length || prefix < 0 || prefix > addressBytes.Length * 8) return false;
        for (var index = 0; index < addressBytes.Length; index++)
        {
            var bits = Math.Clamp(prefix - index * 8, 0, 8);
            if (bits == 0) break;
            var mask = (byte)(0xFF << (8 - bits));
            if ((addressBytes[index] & mask) != (networkBytes[index] & mask)) return false;
        }
        return true;
    }

    private static bool TryBounds(object? expected, out decimal minimum, out decimal maximum)
    {
        minimum = maximum = 0;
        return TryPair(expected, "min", "max", out var minimumValue, out var maximumValue)
            && TryDecimal(minimumValue, out minimum)
            && TryDecimal(maximumValue, out maximum);
    }

    private static bool TryPair(object? value, string firstKey, string secondKey, out object? first, out object? second)
    {
        first = second = null;
        value = Unwrap(value);
        if (value is IDictionary<object, object> objectDictionary)
        {
            return objectDictionary.TryGetValue(firstKey, out first) && objectDictionary.TryGetValue(secondKey, out second);
        }
        if (value is IDictionary<string, object> stringDictionary)
        {
            return stringDictionary.TryGetValue(firstKey, out first) && stringDictionary.TryGetValue(secondKey, out second);
        }
        if (value is JsonObject json)
        {
            first = Values.FromJson(json[firstKey]);
            second = Values.FromJson(json[secondKey]);
            return json.ContainsKey(firstKey) && json.ContainsKey(secondKey);
        }
        if (value is IList list && list.Count == 2)
        {
            first = list[0];
            second = list[1];
            return true;
        }
        return false;
    }

    private static JsonObject CapabilityResult(string caseRunId, string capabilityId, string localStatus, string validationStatus, string exportCoverage, int candidateCount, JsonObject? selected, JsonArray candidates, JsonArray assertions, JsonArray requirements, JsonArray warnings) => new()
    {
        ["case_run_id"] = caseRunId,
        ["capability_id"] = capabilityId,
        ["local_status"] = localStatus,
        ["validation_status"] = validationStatus,
        ["export_coverage"] = exportCoverage,
        ["candidate_count"] = candidateCount,
        ["selected_event"] = selected,
        ["edr_candidates"] = candidates,
        ["assertions"] = assertions,
        ["baseline_requirements"] = requirements,
        ["warnings"] = warnings,
    };

    private static JsonObject NotCompared(string capabilityId, string caseRunId, string localStatus, string warning) =>
        CapabilityResult(caseRunId, capabilityId, localStatus, "NOT_COMPARED", "insufficient", 0, null, new JsonArray(), new JsonArray(), new JsonArray(), new JsonArray(warning));

    private static string Worse(string current, string candidate)
    {
        static int Rank(string value) => value switch { "PASS" => 0, "PARTIAL" => 1, "INCONCLUSIVE" => 2, "FAIL" => 3, _ => 4 };
        return Rank(candidate) > Rank(current) ? candidate : current;
    }

    private static JsonObject Summarize(JsonArray results)
    {
        var statuses = results.Select(x => x?["validation_status"]?.GetValue<string>()).ToArray();
        var compared = statuses.Count(x => x is not "NOT_COMPARED");
        var assertions = results.SelectMany(x => x?["assertions"]?.AsArray() ?? []).ToArray();
        var evaluated = assertions.Count(x => x?["status"]?.GetValue<string>() is not "not_evaluated");
        var passed = assertions.Count(x => x?["status"]?.GetValue<string>() == "passed");
        var determined = statuses.Count(x => x is "PASS" or "PARTIAL" or "FAIL");
        return new JsonObject
        {
            ["pass"] = statuses.Count(x => x == "PASS"),
            ["partial"] = statuses.Count(x => x == "PARTIAL"),
            ["fail"] = statuses.Count(x => x == "FAIL"),
            ["inconclusive"] = statuses.Count(x => x == "INCONCLUSIVE"),
            ["not_compared"] = statuses.Count(x => x == "NOT_COMPARED"),
            ["event_coverage"] = compared == 0 ? null : (double)statuses.Count(x => x is "PASS" or "PARTIAL") / compared,
            ["field_completeness"] = evaluated == 0 ? null : (double)passed / evaluated,
            ["determinacy"] = compared == 0 ? null : (double)determined / compared,
        };
    }

    private static void DecorateCapabilityResult(JsonObject result, JsonObject capability, BaselineDefinition? baseline)
    {
        result["display_name_zh"] = capability["display_name_zh"]?.GetValue<string>();
        result["display_name_en"] = capability["display_name_en"]?.GetValue<string>();
        result["baseline_id"] = baseline?.BaselineId;
        result["baseline_version"] = baseline?.Version;
        result["baseline_title"] = baseline?.Title;
    }

    private static void AttachJsonComparisonEvidence(JsonObject result, JsonObject localRoot, JsonObject capability)
    {
        var caseRunId = RequiredString(capability, "case_run_id");
        result["local_export_block"] = new JsonObject
        {
            ["run"] = localRoot["run"]?.DeepClone(),
            ["capability"] = capability.DeepClone(),
            ["programs"] = CaseItems(localRoot, "programs", caseRunId),
            ["local_events"] = CaseItems(localRoot, "local_events", caseRunId),
            ["local_facts"] = CaseItems(localRoot, "local_facts", caseRunId),
            ["artifacts"] = CaseItems(localRoot, "artifacts", caseRunId),
            ["cleanup_results"] = CaseItems(localRoot, "cleanup_results", caseRunId),
            ["execution_logs"] = CaseItems(localRoot, "execution_logs", caseRunId),
        };

        var resolver = new LocalResolver(localRoot, capability);
        var localMatches = new JsonArray();
        foreach (var requirementNode in result["baseline_requirements"]?.AsArray() ?? [])
        {
            if (requirementNode is not JsonObject requirement
                || requirement["scope"]?.GetValue<string>() != "local") continue;
            var field = requirement["field"]?.GetValue<string>();
            localMatches.Add(new JsonObject
            {
                ["requirement_id"] = requirement["requirement_id"]?.DeepClone(),
                ["status"] = requirement["status"]?.DeepClone(),
                ["field"] = field,
                ["json_pointer"] = field is null ? null : resolver.JsonPointer(field),
                ["expected"] = requirement["expected"]?.DeepClone(),
                ["actual"] = requirement["actual"]?.DeepClone(),
            });
        }
        result["local_baseline_matches"] = localMatches;
    }

    private static JsonArray CaseItems(JsonObject root, string collection, string caseRunId) => new(
        (root[collection]?.AsArray() ?? [])
            .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
            .Select(value => value!.DeepClone())
            .ToArray());

    private static JsonObject BuildConclusion(JsonArray results, JsonObject summary)
    {
        var pass = summary["pass"]!.GetValue<int>();
        var partial = summary["partial"]!.GetValue<int>();
        var fail = summary["fail"]!.GetValue<int>();
        var inconclusive = summary["inconclusive"]!.GetValue<int>();
        var notCompared = summary["not_compared"]!.GetValue<int>();
        var compared = pass + partial + fail + inconclusive;
        var total = compared + notCompared;
        var verdict = fail > 0
            ? "FAIL"
            : inconclusive > 0 || notCompared > 0 || compared == 0
                ? "INCONCLUSIVE"
                : partial > 0
                    ? "PARTIAL"
                    : "PASS";
        var label = verdict switch
        {
            "PASS" => "全部能力满足验证基准",
            "PARTIAL" => "部分能力仅满足部分基准",
            "FAIL" => "发现 EDR 遥测能力缺口",
            _ => "当前证据不足以形成完整结论",
        };
        var statement = compared == 0
            ? $"本轮共有 {total} 项本地能力，但没有可形成判定的比较结果。"
            : $"本轮共纳入 {total} 项本地能力，其中 {compared} 项完成比较：{pass} 项通过、{partial} 项部分通过、{fail} 项失败、{inconclusive} 项无法判定；另有 {notCompared} 项未比较。总体结论：{label}。";

        static JsonArray CapabilityIds(JsonArray values, params string[] statuses)
        {
            var accepted = statuses.ToHashSet(StringComparer.Ordinal);
            return new JsonArray(values
                .Select(value => value?.AsObject())
                .Where(value => value is not null && accepted.Contains(value["validation_status"]?.GetValue<string>() ?? string.Empty))
                .Select(value => (JsonNode)(value!["capability_id"]?.GetValue<string>() ?? string.Empty))
                .ToArray());
        }

        var gapNames = results
            .Select(value => value?.AsObject())
            .Where(value => value?["validation_status"]?.GetValue<string>() == "FAIL")
            .Select(value => value?["display_name_zh"]?.GetValue<string>() ?? value?["capability_id"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (gapNames.Length > 0) statement += $" 未通过能力：{string.Join("、", gapNames)}。";

        return new JsonObject
        {
            ["verdict"] = verdict,
            ["label_zh"] = label,
            ["statement_zh"] = statement,
            ["total_capabilities"] = total,
            ["compared_capabilities"] = compared,
            ["pass_rate"] = compared == 0 ? null : Math.Round((double)pass / compared, 4),
            ["passed_capability_ids"] = CapabilityIds(results, "PASS"),
            ["gap_capability_ids"] = CapabilityIds(results, "FAIL"),
            ["uncertain_capability_ids"] = CapabilityIds(results, "PARTIAL", "INCONCLUSIVE", "NOT_COMPARED"),
        };
    }

    private static bool ManifestFilesMatch(JsonObject manifest, string manifestPath, IReadOnlyList<string> cloudPaths)
    {
        if (manifest["source_files"] is not JsonArray sourceFiles || sourceFiles.Count != cloudPaths.Count) return false;
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var unmatched = sourceFiles.Select(node => node as JsonObject).Where(node => node is not null).Cast<JsonObject>().ToList();
        if (unmatched.Count != sourceFiles.Count) return false;

        foreach (var cloudPathValue in cloudPaths)
        {
            var cloudPath = Path.GetFullPath(cloudPathValue);
            var cloudName = Path.GetFileName(cloudPath);
            var cloudHash = Hashing.FileSha256(cloudPath);
            var cloudSize = new FileInfo(cloudPath).Length;
            var match = unmatched.FirstOrDefault(entry =>
            {
                var declaredPath = entry["path"]?.GetValue<string>();
                var declaredHash = entry["sha256"]?.GetValue<string>();
                var declaredSize = entry["size_bytes"]?.GetValue<long>();
                if (string.IsNullOrWhiteSpace(declaredPath)
                    || !string.Equals(declaredHash, cloudHash, StringComparison.OrdinalIgnoreCase)
                    || declaredSize != cloudSize)
                {
                    return false;
                }

                string? resolvedPath = null;
                try
                {
                    resolvedPath = Path.GetFullPath(declaredPath, manifestDirectory);
                }
                catch (Exception) when (declaredPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    // 文件名仍可与实际导入文件匹配；hash 和大小用于确认内容。
                }
                return string.Equals(resolvedPath, cloudPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(declaredPath), cloudName, StringComparison.OrdinalIgnoreCase);
            });
            if (match is null) return false;
            unmatched.Remove(match);
        }
        return unmatched.Count == 0;
    }

    private static JsonObject FileReference(string path) => new()
    {
        ["path"] = path,
        ["sha256"] = Hashing.FileSha256(path),
        ["size_bytes"] = new FileInfo(path).Length,
    };

    private static T ReadYaml<T>(string path) => Yaml.Deserialize<T>(File.ReadAllText(Path.GetFullPath(path))) ?? throw new InvalidDataException($"YAML 为空：{path}");

    private static string RequiredString(JsonObject value, string property) => value[property]?.GetValue<string>() ?? throw new InvalidDataException($"缺少字符串字段：{property}");

    private static void ValidateInputs(CompareRequest request)
    {
        if (!File.Exists(request.LocalExportPath)) throw new FileNotFoundException("找不到本地导出。", request.LocalExportPath);
        if (request.CloudPaths.Count == 0) throw new ArgumentException("至少需要一个云端 JSON。");
        foreach (var path in request.CloudPaths) if (!File.Exists(path)) throw new FileNotFoundException("找不到云端 JSON。", path);
        if (request.CloudManifestPath is not null && !File.Exists(request.CloudManifestPath)) throw new FileNotFoundException("找不到云端导出清单。", request.CloudManifestPath);
        if (!File.Exists(request.MappingPath)) throw new FileNotFoundException("找不到 Mapping Profile。", request.MappingPath);
        if (request.BaselinePaths.Count == 0) throw new ArgumentException("至少需要一个 BASELINE。");
        foreach (var path in request.BaselinePaths) if (!File.Exists(path)) throw new FileNotFoundException("找不到 BASELINE。", path);
        if (request.ComparisonId is not null && !Guid.TryParse(request.ComparisonId, out _)) throw new ArgumentException("comparison_id 必须是 UUID。");
        var output = Path.GetFullPath(request.OutputPath);
        var conclusionOutput = Path.GetFullPath(request.ConclusionOutputPath ?? ConclusionExportService.DefaultOutputPath(output));
        var inputs = request.CloudPaths
            .Append(request.LocalExportPath)
            .Append(request.MappingPath)
            .Concat(request.CloudManifestPath is null ? [] : [request.CloudManifestPath])
            .Concat(request.BaselinePaths)
            .Select(Path.GetFullPath);
        if (inputs.Any(path => string.Equals(path, output, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("比较结果路径不能覆盖任何输入文件。");
        }
        if (string.Equals(output, conclusionOutput, StringComparison.OrdinalIgnoreCase)
            || inputs.Any(path => string.Equals(path, conclusionOutput, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("结论报告路径不能覆盖比较结果或任何输入文件。");
        }
    }

    private static void ValidateMapping(MappingProfile mapping)
    {
        if (mapping.SchemaVersion != "1.0") throw new InvalidDataException("仅支持 Mapping Profile 1.0。");
        if (string.IsNullOrWhiteSpace(mapping.ProfileId)) throw new InvalidDataException("Mapping Profile 缺少 profile_id。");
        if (mapping.Input?.RecordSelector?.All is not { Count: > 0 }) throw new InvalidDataException("Mapping Profile 缺少 record_selector.all。");
        if (mapping.Routes is not { Count: > 0 }) throw new InvalidDataException("Mapping Profile 缺少 routes。");
        foreach (var route in mapping.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.RouteId) || route.When.Count == 0 || route.Canonical.Count == 0)
            {
                throw new InvalidDataException("Mapping route 必须包含 route_id、when 和 canonical。");
            }
        }
    }

    private static void ValidateBaseline(BaselineDefinition baseline)
    {
        if (baseline.SchemaVersion != "1.1") throw new InvalidDataException($"BASELINE {baseline.BaselineId} 不是 1.1。");
        if (string.IsNullOrWhiteSpace(baseline.BaselineId) || string.IsNullOrWhiteSpace(baseline.Version)) throw new InvalidDataException("BASELINE 缺少 ID 或版本。");
        if (baseline.Capability is null || string.IsNullOrWhiteSpace(baseline.Capability.Id)) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 缺少 capability.id。");
        if (baseline.LocalRequirements is not { Count: > 0 }) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 缺少 local_requirements。");
        if (baseline.Correlation?.Anchors is not { Count: > 0 }) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 缺少 correlation.anchors。");
        if (baseline.CloudExpectations is not { Count: > 0 }) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 缺少 cloud_expectations。");
        foreach (var expectation in baseline.CloudExpectations)
        {
            if (expectation.EventActions.Count == 0 || expectation.Assertions.Count == 0 || expectation.Cardinality.Min < 0)
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation 无效。");
            }
            if (expectation.Cardinality.Max is { } maximum && maximum < expectation.Cardinality.Min)
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cardinality.max 小于 min。");
            }
            if (expectation.Correlation is { } correlation
                && (correlation.Anchors.Count == 0 || correlation.Anchors.Any(anchor => string.IsNullOrWhiteSpace(anchor.LocalField)
                    || string.IsNullOrWhiteSpace(anchor.CloudField))))
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation.correlation 无效。");
            }
        }
    }
}

internal sealed record AssertionEvaluation(string Field, string Operator, string Severity, string Status, object? Expected, object? Actual, string? Message)
{
    public JsonObject ToJson(string? fieldOverride = null) => new()
    {
        ["field"] = fieldOverride ?? Field,
        ["operator"] = Operator,
        ["severity"] = Severity,
        ["status"] = Status,
        ["expected"] = Values.ToNode(Expected),
        ["actual"] = Values.ToNode(Actual),
        ["message"] = Message,
    };
}

internal sealed class LocalResolver
{
    private readonly JsonObject root;
    private readonly JsonObject capability;
    private readonly string caseRunId;

    public LocalResolver(JsonObject root, JsonObject capability)
    {
        this.root = root;
        this.capability = capability;
        caseRunId = capability["case_run_id"]?.GetValue<string>() ?? throw new InvalidDataException("能力缺少 case_run_id。");
    }

    public object? Resolve(string path)
    {
        if (path == "nonce") return capability["nonce"]?.GetValue<string>();
        if (path.StartsWith("facts.", StringComparison.Ordinal))
        {
            var key = path[6..];
            var fact = root["local_facts"]?.AsArray()
                .Select(x => x?.AsObject())
                .FirstOrDefault(x => x?["case_run_id"]?.GetValue<string>() == caseRunId && x?["key"]?.GetValue<string>() == key);
            return Values.FromJson(fact?["value"]);
        }
        if (path.StartsWith("programs.", StringComparison.Ordinal))
        {
            var parts = path.Split('.');
            if (parts.Length < 3) return null;
            var program = root["programs"]?.AsArray()
                .Select(x => x?.AsObject())
                .FirstOrDefault(x => x?["case_run_id"]?.GetValue<string>() == caseRunId && x?["role"]?.GetValue<string>() == parts[1]);
            return ResolveNode(program, parts.Skip(2));
        }
        if (path.StartsWith("capability.", StringComparison.Ordinal)) return ResolveNode(capability, path.Split('.').Skip(1));
        return null;
    }

    public string? JsonPointer(string path)
    {
        if (path == "nonce") return "/capability/nonce";
        if (path.StartsWith("facts.", StringComparison.Ordinal))
        {
            var key = path[6..];
            var facts = root["local_facts"]?.AsArray()
                .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
                .ToArray() ?? [];
            var index = Array.FindIndex(facts, value => value?["key"]?.GetValue<string>() == key);
            return index < 0 ? null : $"/local_facts/{index}/value";
        }
        if (path.StartsWith("programs.", StringComparison.Ordinal))
        {
            var parts = path.Split('.');
            if (parts.Length < 3) return null;
            var programs = root["programs"]?.AsArray()
                .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
                .ToArray() ?? [];
            var index = Array.FindIndex(programs, value => value?["role"]?.GetValue<string>() == parts[1]);
            return index < 0 ? null : "/programs/" + index + "/" + string.Join('/', parts.Skip(2).Select(EscapeJsonPointer));
        }
        if (path.StartsWith("capability.", StringComparison.Ordinal))
        {
            return "/capability/" + string.Join('/', path.Split('.').Skip(1).Select(EscapeJsonPointer));
        }
        return null;
    }

    public string Expand(string template) => template
        .Replace("${nonce}", capability["nonce"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);

    private static object? ResolveNode(JsonNode? node, IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            if (node is not JsonObject value || !value.TryGetPropertyValue(segment, out node)) return null;
        }
        return Values.FromJson(node);
    }

    private static string EscapeJsonPointer(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
