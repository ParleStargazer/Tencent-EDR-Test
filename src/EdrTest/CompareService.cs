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
    string? ComparisonId = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ActionNameStandards = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ChildFileCreateOpNameStandards = null,
    int StrongCorrelationTimeMs = CompareService.DefaultStrongCorrelationTimeMs,
    int CandidateTimeLimitMs = CompareService.DefaultCandidateTimeLimitMs,
    Action<CompareProgressUpdate>? ProgressCallback = null);

public sealed record CompareProgressUpdate(
    int CompletedCapabilities,
    int TotalCapabilities,
    string? CapabilityId,
    string? DisplayNameZh,
    string? ValidationStatus)
{
    public double Progress => TotalCapabilities == 0
        ? 0
        : Math.Round(CompletedCapabilities * 100d / TotalCapabilities, 1, MidpointRounding.AwayFromZero);
}

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
    public List<string> Sources { get; init; } = [];
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
    public MethodSelectionDefinition? MethodSelection { get; init; }
    public List<CloudExpectation> CloudExpectations { get; init; } = [];
    public List<CloudRelationship> CloudRelationships { get; init; } = [];
}

public sealed class MethodSelectionDefinition
{
    public string Strategy { get; init; } = "all";
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
    public List<AcceptedValueDefinition> AcceptedValues { get; init; } = [];
    public string Severity { get; init; } = "required";
    public List<string> Normalizers { get; init; } = [];
}

public sealed class AcceptedValueDefinition
{
    public object? Value { get; init; }
    public string? Message { get; init; }
}

public sealed class CorrelationDefinition
{
    public int TimeBeforeSeconds { get; init; }
    public int TimeAfterSeconds { get; init; }
    public int MaxTimeDifferenceMs { get; init; }
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
    public MethodDefinition? Method { get; init; }
    public StageDefinition? Stage { get; init; }
    public required string EventType { get; init; }
    public List<string> EventActions { get; init; } = [];
    public required CardinalityDefinition Cardinality { get; init; }
    public ExpectationCorrelationDefinition? Correlation { get; init; }
    public List<BaselineAssertion> Assertions { get; init; } = [];
}

public sealed class StageDefinition
{
    public int Sequence { get; init; }
    public required string Title { get; init; }
    public string? DependsOn { get; init; }
}

public sealed class CloudRelationship
{
    public required string Id { get; init; }
    public required StageDefinition Stage { get; init; }
    public required string Type { get; init; }
    public required string LeftExpectation { get; init; }
    public required string RightExpectation { get; init; }
    public int MaxIntervalDifferenceMs { get; init; }
}

public sealed class MethodDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
}

public sealed class ExpectationCorrelationDefinition
{
    public string? TimeFromLocal { get; init; }
    public int? MaxTimeDifferenceMs { get; init; }
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

internal sealed record Candidate(
    CanonicalEvent Event,
    double Score,
    long TimeDistanceMs,
    long? TimeOffsetMs,
    DateTimeOffset LocalCorrelationTime,
    string? LocalTimeField,
    string? LocalTimeJsonPointer,
    IReadOnlyList<string> MatchedAnchors,
    bool AnchorQualified,
    bool Qualified,
    bool TypeHintMatched,
    bool ActionHintMatched,
    string? VendorActionName,
    bool? CustomActionNameMatched,
    IReadOnlyList<string> CustomActionNameStandards,
    string? ChildFileCreateOpName,
    bool? CustomChildFileCreateOpNameMatched,
    IReadOnlyList<string> CustomChildFileCreateOpNameStandards,
    int MaximumTimeDifferenceMs,
    bool TimeDifferenceMatched,
    string QualificationReason);
internal sealed record CorrelationTimeReference(
    DateTimeOffset Value,
    string? LocalField,
    string? LocalJsonPointer);
internal sealed record CloudRecordObservation(DateTimeOffset? EventTime, string? HostId, string? HostName);
internal sealed record CloudLoadResult(IReadOnlyList<CanonicalEvent> Events, IReadOnlyList<CloudRecordObservation> Observations);
internal sealed record MethodOutcome(
    CloudExpectation Expectation,
    string Status,
    JsonObject Result,
    JsonObject? SelectedEvent,
    Candidate? SelectedCandidate,
    double SelectedScore,
    long SelectedTimeDistanceMs,
    IReadOnlyList<string> Warnings);
internal sealed record StageFlowEntry(
    string Id,
    StageDefinition Stage,
    MethodOutcome? Outcome,
    CloudRelationship? Relationship);

public static class CompareService
{
    public const int DefaultStrongCorrelationTimeMs = 15;
    public const int DefaultCandidateTimeLimitMs = 1_000;
    public const int MaximumStrongCorrelationTimeMs = 60_000;
    public const int MaximumCandidateTimeLimitMs = 300_000;
    private const int MaximumDisplayedCandidates = 50;
    private static readonly HashSet<string> FileCapabilityIds = new(StringComparer.Ordinal)
    {
        "win.file.create",
        "win.file.open",
        "win.file.delete",
        "win.file.modify",
        "win.file.rename",
    };

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static JsonObject Compare(CompareRequest request)
    {
        ValidateInputs(request);
        var actionNameStandards = NormalizeEdrFieldStandards(request.ActionNameStandards, "Action.Name");
        var childFileCreateOpNameStandards = NormalizeEdrFieldStandards(
            request.ChildFileCreateOpNameStandards,
            "Child.FileCreateOpName",
            FileCapabilityIds);
        var localPath = Path.GetFullPath(request.LocalExportPath);
        var mappingPath = Path.GetFullPath(request.MappingPath);
        var localRoot = JsonNode.Parse(File.ReadAllText(localPath)) as JsonObject ?? throw new InvalidDataException("本地导出必须是 JSON 对象。");
        var capabilityNodes = localRoot["capabilities"]?.AsArray()
            ?? throw new InvalidDataException("本地导出缺少 capabilities。");
        ReportProgress(request, new CompareProgressUpdate(0, capabilityNodes.Count, null, null, null));
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

        for (var capabilityIndex = 0; capabilityIndex < capabilityNodes.Count; capabilityIndex++)
        {
            var capabilityNode = capabilityNodes[capabilityIndex];
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
                    var capabilityActionNames = actionNameStandards.TryGetValue(capabilityId, out var configuredActionNames)
                        ? configuredActionNames
                        : Array.Empty<string>();
                    var capabilityFileCreateOpNames = childFileCreateOpNameStandards.TryGetValue(capabilityId, out var configuredFileCreateOpNames)
                        ? configuredFileCreateOpNames
                        : Array.Empty<string>();
                    result = CompareCapability(
                        localRoot,
                        capability,
                        baselineEntry.Value,
                        cloud,
                        cloudManifest,
                        manifestFilesVerified,
                        capabilityActionNames,
                        capabilityFileCreateOpNames,
                        request.StrongCorrelationTimeMs,
                        request.CandidateTimeLimitMs);
                    DecorateCapabilityResult(result, capability, baselineEntry.Value);
                }
            }
            AttachJsonComparisonEvidence(result, localRoot, capability);
            results.Add(result);
            ReportProgress(request, new CompareProgressUpdate(
                capabilityIndex + 1,
                capabilityNodes.Count,
                capabilityId,
                capability["display_name_zh"]?.GetValue<string>() ?? capabilityId,
                result["validation_status"]?.GetValue<string>()));
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
                ["action_name_standards"] = new JsonObject(actionNameStandards.Select(value =>
                    KeyValuePair.Create<string, JsonNode?>(value.Key, new JsonArray(value.Value.Select(action => (JsonNode)action).ToArray())))),
                ["child_file_create_op_name_standards"] = new JsonObject(childFileCreateOpNameStandards.Select(value =>
                    KeyValuePair.Create<string, JsonNode?>(value.Key, new JsonArray(value.Value.Select(operation => (JsonNode)operation).ToArray())))),
                ["strong_correlation_time_ms"] = request.StrongCorrelationTimeMs,
                ["candidate_time_limit_ms"] = request.CandidateTimeLimitMs,
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

    private static void ReportProgress(CompareRequest request, CompareProgressUpdate update)
    {
        if (request.ProgressCallback is null) return;
        try
        {
            request.ProgressCallback(update);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"离线比较进度回调失败：{exception.Message}");
        }
    }

    private static JsonObject CompareCapability(
        JsonObject localRoot,
        JsonObject capability,
        BaselineDefinition baseline,
        CloudLoadResult cloud,
        JsonObject? cloudManifest,
        bool manifestFilesVerified,
        IReadOnlyList<string> actionNameStandards,
        IReadOnlyList<string> childFileCreateOpNameStandards,
        int strongCorrelationTimeMs,
        int candidateTimeLimitMs)
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
        var failedLocal = localEvaluations
            .Select(value => value.Evaluation)
            .Where(x => x.Status != "passed")
            .ToArray();
        var overallStatus = "PASS";
        if (localStatus != "LOCAL_PASS")
        {
            warnings.Add("本地能力未达到 LOCAL_PASS；仍继续执行可完成的 EDR 候选关联与字段检查，最终结论至少为无法判定。");
            overallStatus = Worse(overallStatus, "INCONCLUSIVE");
        }
        if (failedLocal.Length > 0)
        {
            foreach (var assertion in failedLocal) warnings.Add($"本地前置断言未通过：{assertion.Field}");
            warnings.Add("本地基准字段异常缺失，能力不能判定通过；比较器仍保留云端候选与逐字段结果，便于诊断采集链路。");
            overallStatus = Worse(overallStatus, "INCONCLUSIVE");
        }

        var start = DateTimeOffset.Parse(RequiredString(capability, "started_at_utc"), CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(RequiredString(capability, "ended_at_utc"), CultureInfo.InvariantCulture);
        var exportCoverage = DetermineExportCoverage(localRoot, start, end, cloud.Observations, cloudManifest, manifestFilesVerified);
        var defaultCorrelationTime = LocalCorrelationTime(localRoot, caseRunId, start);
        var totalCandidates = 0;
        var outputAssertions = new JsonArray();
        var outputCandidates = new JsonArray();
        JsonObject? firstSelected = null;
        if (actionNameStandards.Count > 0)
        {
            warnings.Add($"已启用自定义 Action.Name 消歧：{string.Join("、", actionNameStandards)}。该条件只筛选已命中本地锚点的候选，不参与候选召回或锚点评分。");
        }
        if (childFileCreateOpNameStandards.Count > 0)
        {
            warnings.Add($"已启用自定义 Child.FileCreateOpName 消歧：{string.Join("、", childFileCreateOpNameStandards)}。该条件只筛选五项文件能力中已命中本地锚点的 EDR 候选，不影响本地规则。");
        }
        var customFilterNames = new[]
        {
            actionNameStandards.Count > 0 ? "Action.Name" : null,
            childFileCreateOpNameStandards.Count > 0 ? "Child.FileCreateOpName" : null,
        }.Where(value => value is not null).Cast<string>().ToArray();
        var customFilterDescription = string.Join(" 与 ", customFilterNames);
        var methodOutcomes = new List<MethodOutcome>();
        var exposesMethods = baseline.MethodSelection is not null || baseline.CloudExpectations.Any(expectation => expectation.Method is not null);
        foreach (var expectation in baseline.CloudExpectations)
        {
            var requirementStartIndex = outputRequirements.Count;
            var expectationStatus = "PASS";
            var methodWarnings = new List<string>();
            JsonObject? expectationSelectedEvent = null;
            Candidate? expectationSelectedCandidate = null;
            var expectationAnchors = expectation.Correlation?.Anchors is { Count: > 0 }
                ? expectation.Correlation.Anchors
                : baseline.Correlation.Anchors;
            var maximumTimeDifferenceMs = strongCorrelationTimeMs;
            var correlationTime = ResolveCorrelationTime(expectation.Correlation?.TimeFromLocal, resolver, defaultCorrelationTime);
            var candidates = cloud.Events
                .Where(item => EventWithinCandidateTimeLimit(item, correlationTime.Value, candidateTimeLimitMs))
                .Where(item => EventWithinWindow(item, start, end, baseline.Correlation))
                .Select(item => Score(
                    item,
                    expectation,
                    expectationAnchors,
                    resolver,
                    correlationTime,
                    maximumTimeDifferenceMs,
                    actionNameStandards,
                    childFileCreateOpNameStandards))
                .GroupBy(item => item.Event.RawRef, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.Qualified)
                    .ThenByDescending(item => item.AnchorQualified)
                    .ThenByDescending(item => item.Score)
                    .ThenByDescending(item => item.TypeHintMatched)
                    .ThenByDescending(item => item.ActionHintMatched)
                    .ThenBy(item => item.TimeDistanceMs)
                    .First())
                .OrderByDescending(item => item.Qualified)
                .ThenByDescending(item => item.AnchorQualified)
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.TypeHintMatched)
                .ThenByDescending(item => item.ActionHintMatched)
                .ThenBy(item => item.TimeDistanceMs)
                .ThenBy(item => item.Event.RawRef, StringComparer.Ordinal)
                .Take(MaximumDisplayedCandidates)
                .ToArray();
            var anchorQualifiedCandidates = candidates.Where(item => item.AnchorQualified).ToArray();
            var qualifiedCandidates = candidates.Where(item => item.Qualified).ToArray();
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
            var cardinalityStatus = qualifiedCandidates.Length < expectation.Cardinality.Min
                ? exportCoverage is "verified" or "inferred" or "assumed" ? "failed" : "not_evaluated"
                : expectation.Cardinality.Max is { } cardinalityMaximum && qualifiedCandidates.Length > cardinalityMaximum
                    ? "not_evaluated"
                    : "passed";
            var cardinalityMessage = cardinalityStatus switch
            {
                "failed" when customFilterNames.Length > 0 => $"EDR 日志范围足以形成判断；{anchorQualifiedCandidates.Length} 条候选命中本地锚点，其中 {qualifiedCandidates.Length} 条同时符合自定义 {customFilterDescription}。其余记录仍保留供排查。",
                "failed" => $"EDR 日志范围足以形成判断，但只有 {qualifiedCandidates.Length} 条候选达到关联阈值；另展示 {candidates.Length - qualifiedCandidates.Length} 条低置信度记录供排查。",
                "not_evaluated" when qualifiedCandidates.Length < expectation.Cardinality.Min && customFilterNames.Length > 0 => $"日志范围或关联证据不足；{anchorQualifiedCandidates.Length} 条候选命中本地锚点，其中 {qualifiedCandidates.Length} 条同时符合自定义 {customFilterDescription}。",
                "not_evaluated" when qualifiedCandidates.Length < expectation.Cardinality.Min => $"日志范围或关联证据不足；已展示 {candidates.Length} 条时间相近记录供继续核对。",
                "not_evaluated" => "候选事件过多，无法唯一关联。",
                _ => null,
            };
            outputRequirements.Add(CardinalityRequirementJson(expectation, qualifiedCandidates.Length, cardinalityStatus, cardinalityMessage));
            if (actionNameStandards.Count > 0)
            {
                var actionRequirement = CustomEdrFieldRequirementJson(
                    expectation,
                    "Action.Name",
                    "custom-action-name",
                    actionNameStandards,
                    anchorQualifiedCandidates,
                    candidate => candidate.VendorActionName,
                    candidate => candidate.CustomActionNameMatched);
                outputRequirements.Add(actionRequirement);
                if (actionRequirement["status"]?.GetValue<string>() == "failed") expectationStatus = Worse(expectationStatus, "FAIL");
                else if (actionRequirement["status"]?.GetValue<string>() == "not_evaluated") expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            }
            if (childFileCreateOpNameStandards.Count > 0)
            {
                var fileOperationRequirement = CustomEdrFieldRequirementJson(
                    expectation,
                    "Child.FileCreateOpName",
                    "custom-child-file-create-op-name",
                    childFileCreateOpNameStandards,
                    anchorQualifiedCandidates,
                    candidate => candidate.ChildFileCreateOpName,
                    candidate => candidate.CustomChildFileCreateOpNameMatched);
                outputRequirements.Add(fileOperationRequirement);
                if (fileOperationRequirement["status"]?.GetValue<string>() == "failed") expectationStatus = Worse(expectationStatus, "FAIL");
                else if (fileOperationRequirement["status"]?.GetValue<string>() == "not_evaluated") expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            }
            if (qualifiedCandidates.Length < expectation.Cardinality.Min)
            {
                if (exportCoverage is "verified" or "inferred" or "assumed")
                {
                    methodWarnings.Add($"未找到达到关联阈值的 EDR 事件（合格 {qualifiedCandidates.Length} 条，候选时间上限内 {candidates.Length} 条；导出覆盖状态：{exportCoverage}）。");
                    expectationStatus = Worse(expectationStatus, "FAIL");
                }
                else
                {
                    methodWarnings.Add($"没有候选达到关联阈值，但仍保留候选时间上限内的低置信度记录供核对（合格 {qualifiedCandidates.Length} 条，候选 {candidates.Length} 条）。");
                    expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
                }
            }
            if (expectation.Cardinality.Max is { } maximum && qualifiedCandidates.Length > maximum)
            {
                methodWarnings.Add($"合格候选事件过多，无法唯一判定（找到 {qualifiedCandidates.Length} 条，最多允许 {maximum} 条）。");
                expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            }
            if (candidates.Length == 0)
            {
                outputRequirements.Add(TimeDifferenceRequirementJson(expectation, maximumTimeDifferenceMs, null));
                AddUnevaluatedExpectationRequirements(outputRequirements, expectation, resolver, "候选时间上限内没有可展示的 EDR 记录，无法检查该项。", includeCardinality: false);
                methodOutcomes.Add(CreateMethodOutcome(expectation, expectationStatus, requirementStartIndex, outputRequirements,
                    localEvaluations.Length, localEvaluations.Count(value => value.Evaluation.Status == "passed"),
                    candidates.Length, qualifiedCandidates.Length, null, null, methodWarnings));
                continue;
            }
            if (qualifiedCandidates.Length > 1 && qualifiedCandidates[0].Score == qualifiedCandidates[1].Score && qualifiedCandidates[0].TimeDistanceMs == qualifiedCandidates[1].TimeDistanceMs)
            {
                methodWarnings.Add("存在多个同分候选事件，无法唯一关联。");
                expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            }

            var selected = qualifiedCandidates.FirstOrDefault() ?? candidates[0];
            if (!selected.Qualified)
            {
                var rejectedFilters = new[]
                {
                    selected.CustomActionNameMatched is false ? "Action.Name" : null,
                    selected.CustomChildFileCreateOpNameMatched is false ? "Child.FileCreateOpName" : null,
                }.Where(value => value is not null).Cast<string>().ToArray();
                methodWarnings.Add(selected.AnchorQualified && rejectedFilters.Length > 0
                    ? $"{expectation.Id} 已找到命中本地锚点的记录，但原始 {string.Join("、", rejectedFilters)} 不符合自定义标准；仍保留该记录和逐字段结果供核对。"
                    : $"{expectation.Id} 当前使用低置信度候选继续展示可验证字段，不会把该候选当作已可靠关联。");
                expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            }
            expectationSelectedCandidate = selected;
            expectationSelectedEvent = new JsonObject
            {
                ["raw_ref"] = selected.Event.RawRef,
                ["event_id"] = Values.ToNode(selected.Event.Get("event.id")),
                ["correlation_score"] = selected.Score,
                ["confidence"] = CandidateConfidence(selected),
                ["anchor_qualified"] = selected.AnchorQualified,
                ["eligible_for_validation"] = selected.Qualified,
                ["qualification_reason"] = selected.QualificationReason,
                ["custom_action_name_expected"] = new JsonArray(selected.CustomActionNameStandards.Select(value => (JsonNode)value).ToArray()),
                ["custom_action_name_actual"] = selected.VendorActionName,
                ["custom_action_name_matched"] = selected.CustomActionNameMatched,
                ["custom_child_file_create_op_name_expected"] = new JsonArray(selected.CustomChildFileCreateOpNameStandards.Select(value => (JsonNode)value).ToArray()),
                ["custom_child_file_create_op_name_actual"] = selected.ChildFileCreateOpName,
                ["custom_child_file_create_op_name_matched"] = selected.CustomChildFileCreateOpNameMatched,
                ["maximum_time_difference_ms"] = selected.MaximumTimeDifferenceMs,
                ["time_difference_matched"] = selected.TimeDifferenceMatched,
                ["time_distance_ms"] = selected.TimeDistanceMs,
                ["time_offset_ms"] = selected.TimeOffsetMs,
                ["local_event_time_utc"] = Values.Utc(selected.LocalCorrelationTime),
                ["event_time_utc"] = Values.ToNode(selected.Event.Get("event.created")),
                ["matched_anchors"] = new JsonArray(selected.MatchedAnchors.Select(x => (JsonNode)x).ToArray()),
            };
            firstSelected ??= expectationSelectedEvent.DeepClone().AsObject();
            var timeRequirement = TimeDifferenceRequirementJson(expectation, maximumTimeDifferenceMs, selected);
            outputRequirements.Add(timeRequirement);
            if (timeRequirement["status"]?.GetValue<string>() == "failed") expectationStatus = Worse(expectationStatus, "FAIL");
            else if (timeRequirement["status"]?.GetValue<string>() == "not_evaluated") expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
            foreach (var assertion in expectation.Assertions)
            {
                var evaluated = Evaluate(assertion, selected.Event.Get(assertion.Field), resolver);
                var fieldName = baseline.CloudExpectations.Count == 1 ? evaluated.Field : $"{expectation.Id}:{evaluated.Field}";
                outputAssertions.Add(evaluated.ToJson(fieldName));
                outputRequirements.Add(RequirementJson($"{expectation.Id}-{expectation.Assertions.IndexOf(assertion) + 1}", "cloud", expectation.Id, evaluated));
                if (evaluated.Status == "not_evaluated" && assertion.Severity == "required") expectationStatus = Worse(expectationStatus, "INCONCLUSIVE");
                else if (evaluated.Status == "not_evaluated" && assertion.Severity == "recommended") expectationStatus = Worse(expectationStatus, "PARTIAL");
                else if (evaluated.Status == "failed" && assertion.Severity == "required") expectationStatus = Worse(expectationStatus, "FAIL");
                else if (evaluated.Status == "failed" && assertion.Severity == "recommended") expectationStatus = Worse(expectationStatus, "PARTIAL");
            }
            methodOutcomes.Add(CreateMethodOutcome(expectation, expectationStatus, requirementStartIndex, outputRequirements,
                localEvaluations.Length, localEvaluations.Count(value => value.Evaluation.Status == "passed"),
                candidates.Length, qualifiedCandidates.Length, expectationSelectedEvent, expectationSelectedCandidate, methodWarnings));
        }

        var outputStages = new JsonArray();
        JsonObject? stageFlow = null;
        var stagedEntries = methodOutcomes
            .Where(outcome => outcome.Expectation.Stage is not null)
            .Select(outcome => new StageFlowEntry(outcome.Expectation.Id, outcome.Expectation.Stage!, outcome, null))
            .Concat(baseline.CloudRelationships.Select(relationship =>
                new StageFlowEntry(relationship.Id, relationship.Stage, null, relationship)))
            .OrderBy(entry => entry.Stage.Sequence)
            .ToArray();
        if (stagedEntries.Length > 0)
        {
            var sequenceIsValid = stagedEntries.Select(entry => entry.Stage.Sequence)
                .SequenceEqual(Enumerable.Range(1, stagedEntries.Length));
            if (!sequenceIsValid)
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的验证阶段 sequence 必须从 1 开始且连续。");

            var flowStatus = "PASS";
            StageFlowEntry? previous = null;
            foreach (var entry in stagedEntries)
            {
                var stage = entry.Stage;
                if (stage.Sequence > 1
                    && (previous is null || !string.Equals(stage.DependsOn, previous.Id, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"验证阶段 {entry.Id} 必须通过 depends_on 指向紧邻的前一阶段。");
                }

                if (entry.Relationship is { } relationship)
                {
                    var left = methodOutcomes.Single(outcome => outcome.Expectation.Id == relationship.LeftExpectation);
                    var right = methodOutcomes.Single(outcome => outcome.Expectation.Id == relationship.RightExpectation);
                    var relationshipResult = RelationshipStageJson(relationship, left, right, outputRequirements);
                    outputStages.Add(relationshipResult.Stage);
                    flowStatus = Worse(flowStatus, relationshipResult.Status);
                    previous = entry;
                    continue;
                }

                var outcome = entry.Outcome!;
                var stageStatus = outcome.Status;
                string? orderStatus = null;
                string? orderMessage = null;
                if (previous?.Outcome is { } previousOutcome)
                {
                    var previousTime = SelectedEventTime(previousOutcome.SelectedEvent);
                    var currentTime = SelectedEventTime(outcome.SelectedEvent);
                    orderStatus = previousTime is null || currentTime is null
                        ? "not_evaluated"
                        : currentTime >= previousTime ? "passed" : "failed";
                    orderMessage = orderStatus switch
                    {
                        "passed" => $"EDR 第二阶段时间不早于前一阶段（{Values.Utc(previousTime!.Value)} → {Values.Utc(currentTime!.Value)}）。",
                        "failed" => $"EDR 阶段顺序反转（{Values.Utc(previousTime!.Value)} → {Values.Utc(currentTime!.Value)}）。",
                        _ => "至少一个阶段没有可可靠关联的 EDR 时间，无法验证阶段先后关系。",
                    };
                    outputRequirements.Add(new JsonObject
                    {
                        ["requirement_id"] = $"{outcome.Expectation.Id}-stage-order",
                        ["scope"] = "cloud",
                        ["title_zh"] = $"{stage.Title}必须晚于或等于{previous.Stage.Title}",
                        ["expectation_id"] = outcome.Expectation.Id,
                        ["field"] = "event.created_order",
                        ["operator"] = "ordered_after",
                        ["severity"] = "required",
                        ["status"] = orderStatus,
                        ["expected"] = previousTime is null ? null : Values.Utc(previousTime.Value),
                        ["actual"] = currentTime is null ? null : Values.Utc(currentTime.Value),
                        ["message"] = orderMessage,
                    });
                    stageStatus = orderStatus == "failed"
                        ? Worse(stageStatus, "FAIL")
                        : orderStatus == "not_evaluated" ? Worse(stageStatus, "INCONCLUSIVE") : stageStatus;
                }
                flowStatus = Worse(flowStatus, stageStatus);
                outputStages.Add(new JsonObject
                {
                    ["kind"] = "event",
                    ["sequence"] = stage.Sequence,
                    ["title"] = stage.Title,
                    ["expectation_id"] = outcome.Expectation.Id,
                    ["depends_on"] = stage.DependsOn,
                    ["status"] = stageStatus,
                    ["candidate_count"] = outcome.Result["candidate_count"]?.DeepClone(),
                    ["qualified_candidate_count"] = outcome.Result["qualified_candidate_count"]?.DeepClone(),
                    ["selected_event"] = outcome.SelectedEvent?.DeepClone(),
                    ["related_expectation_ids"] = new JsonArray((JsonNode)outcome.Expectation.Id),
                    ["relationship"] = null,
                    ["order_status"] = orderStatus,
                    ["order_message"] = orderMessage,
                    ["warnings"] = outcome.Result["warnings"]?.DeepClone(),
                });
                previous = entry;
            }
            stageFlow = new JsonObject
            {
                ["strategy"] = "ordered_all",
                ["status"] = flowStatus,
                ["stage_count"] = stagedEntries.Length,
                ["notice"] = baseline.CloudRelationships.Count > 0
                    ? "文件下载采用三部分证据链：先按 TCP 标准验证连接，再证明连接与文件写入属于同一进程的连续行为，最后验证目标文件；三部分必须全部满足。"
                    : "该能力采用有序多阶段验证；所有阶段及其依赖顺序都满足时才能通过。",
            };
        }

        if (baseline.CloudExpectations.Count == 0)
        {
            warnings.Add("BASELINE 没有 cloud_expectations。");
            overallStatus = "INCONCLUSIVE";
        }
        JsonObject? methodSelection = null;
        var outputMethods = new JsonArray();
        if (methodOutcomes.Count > 0)
        {
            if (baseline.MethodSelection?.Strategy == "best")
            {
                var selectedMethod = methodOutcomes
                    .OrderBy(outcome => StatusRank(outcome.Status))
                    .ThenByDescending(outcome => outcome.SelectedScore)
                    .ThenBy(outcome => outcome.SelectedTimeDistanceMs)
                    .First();
                selectedMethod.Result["selected_for_conclusion"] = true;
                firstSelected = selectedMethod.SelectedEvent?.DeepClone().AsObject();
                overallStatus = Worse(overallStatus, selectedMethod.Status);
                var selectedTitle = selectedMethod.Expectation.Method?.Title ?? selectedMethod.Expectation.Id;
                methodSelection = new JsonObject
                {
                    ["strategy"] = "best",
                    ["selected_method_id"] = selectedMethod.Expectation.Method?.Id ?? selectedMethod.Expectation.Id,
                    ["selected_method_title"] = selectedTitle,
                    ["selected_method_status"] = selectedMethod.Status,
                    ["notice"] = methodOutcomes.Count == 1
                        ? $"该能力采用“{selectedTitle}”单一测试方法形成 EDR 结论（{ValidationStatusLabel(selectedMethod.Status)}）。"
                        : $"该能力包含 {methodOutcomes.Count} 种测试方法，EDR 结论默认采用结果最好的“{selectedTitle}”（{ValidationStatusLabel(selectedMethod.Status)}）；其他方法仍完整展示，不会拉低所选方法的结论。",
                };
            }
            else
            {
                foreach (var outcome in methodOutcomes)
                {
                    overallStatus = Worse(overallStatus, outcome.Status);
                    foreach (var warning in outcome.Warnings) warnings.Add($"{outcome.Expectation.Id}：{warning}");
                }
                if (stageFlow is not null)
                    overallStatus = Worse(overallStatus, stageFlow["status"]!.GetValue<string>());
            }
            if (exposesMethods)
            {
                foreach (var outcome in methodOutcomes) outputMethods.Add(outcome.Result);
            }
        }
        return CapabilityResult(caseRunId, capabilityId, localStatus, overallStatus, exportCoverage, totalCandidates,
            firstSelected, outputCandidates, outputAssertions, outputRequirements, methodSelection,
            outputMethods, stageFlow, outputStages, warnings);
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
        ["title_zh"] = $"必须找到至少 {expectation.Cardinality.Min} 条 {EventActionTitle(expectation.EventType, expectation.EventActions)} EDR 事件",
        ["expectation_id"] = expectation.Id,
        ["field"] = "event.count",
        ["operator"] = "range",
        ["severity"] = "required",
        ["status"] = status,
        ["expected"] = new JsonObject { ["min"] = expectation.Cardinality.Min, ["max"] = expectation.Cardinality.Max },
        ["actual"] = actual,
        ["message"] = message,
    };

    private static JsonObject CustomEdrFieldRequirementJson(
        CloudExpectation expectation,
        string rawField,
        string requirementSuffix,
        IReadOnlyList<string> standards,
        IReadOnlyList<Candidate> anchorQualifiedCandidates,
        Func<Candidate, string?> actualSelector,
        Func<Candidate, bool?> matchSelector)
    {
        var actual = anchorQualifiedCandidates
            .Select(actualSelector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matchedCount = anchorQualifiedCandidates.Count(candidate => matchSelector(candidate) == true);
        var status = anchorQualifiedCandidates.Count == 0
            ? "not_evaluated"
            : matchedCount > 0
                ? "passed"
                : "failed";
        var message = status switch
        {
            "passed" => $"命中本地锚点的候选中，有 {matchedCount} 条符合自定义 {rawField}。",
            "failed" => $"已找到命中本地锚点的候选，但它们的原始 {rawField} 均不符合自定义标准。",
            _ => $"尚无命中本地锚点的候选，无法应用自定义 {rawField} 消歧。",
        };
        return new JsonObject
        {
            ["requirement_id"] = $"{expectation.Id}-{requirementSuffix}",
            ["scope"] = "cloud",
            ["title_zh"] = $"原始 {rawField} 必须符合本次自定义标准",
            ["expectation_id"] = expectation.Id,
            ["field"] = rawField,
            ["operator"] = "one_of",
            ["severity"] = "required",
            ["status"] = status,
            ["expected"] = new JsonArray(standards.Select(value => (JsonNode)value).ToArray()),
            ["actual"] = new JsonArray(actual.Select(value => (JsonNode)value).ToArray()),
            ["message"] = message,
        };
    }

    private static JsonObject TimeDifferenceRequirementJson(CloudExpectation expectation, int maximumTimeDifferenceMs, Candidate? candidate)
    {
        var actual = candidate is null || candidate.TimeDistanceMs == long.MaxValue ? (long?)null : candidate.TimeDistanceMs;
        var status = actual is null ? "not_evaluated" : actual <= maximumTimeDifferenceMs ? "passed" : "failed";
        return new JsonObject
        {
            ["requirement_id"] = $"{expectation.Id}-time-difference",
            ["scope"] = "cloud",
            ["title_zh"] = $"EDR 事件与本地行为时间差必须不超过 {maximumTimeDifferenceMs} ms",
            ["expectation_id"] = expectation.Id,
            ["field"] = "event.time_difference_ms",
            ["operator"] = "range",
            ["severity"] = "required",
            ["status"] = status,
            ["expected"] = new JsonObject { ["min"] = 0, ["max"] = maximumTimeDifferenceMs },
            ["actual"] = actual,
            ["message"] = status switch
            {
                "passed" => $"EDR 事件相对本地行为为 {FormatSignedTimeOffset(candidate?.TimeOffsetMs)}，绝对差 {actual} ms，满足时间差基准。",
                "failed" => $"EDR 事件相对本地行为为 {FormatSignedTimeOffset(candidate?.TimeOffsetMs)}，绝对差 {actual} ms，超过 {maximumTimeDifferenceMs} ms。",
                _ => "候选缺少有效事件时间，无法计算与本地行为的时间差。",
            },
        };
    }

    private static string FormatSignedTimeOffset(long? offsetMs) => offsetMs switch
    {
        null => "未知",
        > 0 => $"+{offsetMs} ms（EDR 延后）",
        < 0 => $"{offsetMs} ms（EDR 提前）",
        _ => "0 ms（时间一致）",
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
            "file.size" => "EDR 记录的文件大小",
            "file.hash.md5" => "EDR 记录的文件 MD5",
            "file.hash.sha256" => "EDR 记录的文件 SHA-256",
            "file.hash.sha512" => "EDR 记录的文件 SHA-512",
            "file.hash.imphash" => "EDR 记录的文件 IMPHASH",
            "file.hash.sha_type" => "EDR 记录的 SHA 类型",
            "file.operation_name" => "EDR 记录的文件操作",
            "file.format" => "EDR 识别的文件格式",
            "registry.key" => "EDR 记录的注册表键路径",
            "registry.value_name" => "EDR 记录的注册表值名",
            "registry.value_data" => "EDR 记录的注册表值数据",
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

    internal static string EventActionTitle(string eventType, IReadOnlyList<string> actions) => string.Join("/", actions.Select(action => (eventType, action) switch
    {
        ("process", "create") => "进程创建",
        ("process", "terminate") => "进程终止",
        ("process", "access") => "进程访问",
        ("process", "image_load") => "镜像或动态库加载",
        ("process", "remote_thread_create") => "远程线程创建",
        ("process", "tamper") => "进程篡改",
        ("file", "create") => "文件创建",
        ("file", "open") => "文件打开",
        ("file", "delete") => "文件删除",
        ("file", "modify") => "文件修改",
        ("file", "rename") => "文件重命名",
        ("account", "local_create") => "本地账号创建",
        ("account", "local_modify") => "本地账号修改",
        ("account", "local_delete") => "本地账号删除",
        ("account", "login") => "账号登录",
        ("account", "logoff") => "账号注销",
        ("registry", "create") => "注册表键/值创建",
        ("registry", "modify") => "注册表键/值修改",
        ("registry", "delete") => "注册表键/值删除",
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

    private static Candidate Score(
        CanonicalEvent item,
        CloudExpectation expectation,
        IReadOnlyList<CorrelationAnchor> anchors,
        LocalResolver resolver,
        CorrelationTimeReference correlationTime,
        int maximumTimeDifferenceMs,
        IReadOnlyList<string> actionNameStandards,
        IReadOnlyList<string> childFileCreateOpNameStandards)
    {
        double score = 0;
        var matched = new List<string>();
        var matchedStrong = false;
        var matchedMedium = false;
        var matchedIdentity = false;
        var availableStrong = false;
        var availableMedium = false;
        foreach (var anchor in anchors)
        {
            var local = Normalize(resolver.Resolve(anchor.LocalField), anchor.Normalizers);
            if (local is null) continue;
            if (anchor.Strength == "strong") availableStrong = true;
            if (anchor.Strength == "medium") availableMedium = true;
            var cloud = Normalize(item.Get(anchor.CloudField), anchor.Normalizers);
            if (!Equivalent(local, cloud)) continue;
            score += anchor.Strength switch { "strong" => 100, "medium" => 25, "weak" => 5, _ => 0 };
            if (anchor.Strength == "strong") matchedStrong = true;
            if (anchor.Strength == "medium") matchedMedium = true;
            matchedIdentity = true;
            matched.Add($"{anchor.LocalField}={anchor.CloudField}");
        }
        var eventTime = CanonicalEventTime(item);
        var timeOffsetMs = eventTime is null
            ? (long?)null
            : (long)Math.Round((eventTime.Value - correlationTime.Value).TotalMilliseconds, MidpointRounding.AwayFromZero);
        var distance = timeOffsetMs is null ? long.MaxValue : Math.Abs(timeOffsetMs.Value);
        var timeDifferenceMatched = timeOffsetMs is not null && distance <= maximumTimeDifferenceMs;
        if (timeDifferenceMatched)
        {
            score += 100;
            matched.Add($"event.time_difference_ms<={maximumTimeDifferenceMs}");
        }
        var typeHintMatched = Equivalent(item.Get("event.type"), expectation.EventType);
        var actionHintMatched = expectation.EventActions.Any(action => Equivalent(item.Get("event.action"), action));

        var anchorQualified = matchedStrong || (!availableStrong && matchedMedium) || (timeDifferenceMatched && matchedIdentity);
        var vendorActionName = RawStringField(item, "Action.Name");
        bool? customActionNameMatched = actionNameStandards.Count == 0
            ? null
            : actionNameStandards.Any(action => Equivalent(vendorActionName, action));
        var childFileCreateOpName = RawStringField(item, "Child.FileCreateOpName");
        bool? customChildFileCreateOpNameMatched = childFileCreateOpNameStandards.Count == 0
            ? null
            : childFileCreateOpNameStandards.Any(operation => Equivalent(childFileCreateOpName, operation));
        var qualified = anchorQualified
            && customActionNameMatched is not false
            && customChildFileCreateOpNameMatched is not false;
        var rejectedCustomFields = new[]
        {
            customActionNameMatched is false ? "Action.Name" : null,
            customChildFileCreateOpNameMatched is false ? "Child.FileCreateOpName" : null,
        }.Where(value => value is not null).Cast<string>().ToArray();
        var reason = !anchorQualified
            ? availableStrong
                ? "未命中可用的强本地锚点，仅作为低置信度候选展示"
                : availableMedium
                    ? "未命中可用的中等本地锚点，仅作为低置信度候选展示"
                    : "本地关联锚点未采集完整，仅按时间距离展示；本地基准异常会阻止能力通过"
            : rejectedCustomFields.Length > 0
                ? $"已命中本地锚点，但原始 {string.Join("、", rejectedCustomFields)} 不符合本次自定义标准"
                : timeDifferenceMatched && !matchedStrong
                    ? $"时间差不超过 {maximumTimeDifferenceMs} ms，并命中至少一个本地身份锚点"
                : matchedStrong
                    ? timeDifferenceMatched
                        ? $"命中至少一个强本地锚点，且时间差不超过 {maximumTimeDifferenceMs} ms"
                        : "命中至少一个强本地锚点"
                    : "强锚点缺失，命中可用的中等本地锚点";
        return new Candidate(item, score, distance, timeOffsetMs, correlationTime.Value, correlationTime.LocalField, correlationTime.LocalJsonPointer,
            matched, anchorQualified, qualified, typeHintMatched, actionHintMatched,
            vendorActionName, customActionNameMatched, actionNameStandards,
            childFileCreateOpName, customChildFileCreateOpNameMatched, childFileCreateOpNameStandards,
            maximumTimeDifferenceMs, timeDifferenceMatched, reason);
    }

    private static string? RawStringField(CanonicalEvent item, string rawField)
    {
        var node = RawFieldNode(item, rawField).Node;
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        return null;
    }

    private static (JsonNode? Node, string? Pointer) RawFieldNode(CanonicalEvent item, string rawField)
    {
        foreach (var property in item.Raw)
        {
            if (string.Equals(property.Key, rawField, StringComparison.OrdinalIgnoreCase))
            {
                return (property.Value, JsonPointer(property.Key));
            }
        }

        JsonNode? current = item.Raw;
        var pointer = string.Empty;
        foreach (var segment in rawField.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject currentObject) return (null, null);
            var found = false;
            foreach (var property in currentObject)
            {
                if (!string.Equals(property.Key, segment, StringComparison.OrdinalIgnoreCase)) continue;
                current = property.Value;
                pointer += JsonPointer(property.Key);
                found = true;
                break;
            }
            if (!found) return (null, null);
        }
        return (current, pointer.Length == 0 ? null : pointer);
    }

    private static CorrelationTimeReference LocalCorrelationTime(JsonObject localRoot, string caseRunId, DateTimeOffset fallback)
    {
        var eventTime = (localRoot["local_events"]?.AsArray() ?? [])
            .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
            .Select((value, index) => new
            {
                Value = value?.AsObject(),
                CaseIndex = index,
            })
            .OrderBy(value => value.Value?["sequence"]?.GetValue<int>() ?? int.MaxValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Value?["occurred_at_utc"]?.GetValue<string>()));
        var eventTimeText = eventTime?.Value?["occurred_at_utc"]?.GetValue<string>();
        return DateTimeOffset.TryParse(eventTimeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? new CorrelationTimeReference(parsed, "local_events.occurred_at_utc", $"/local_events/{eventTime!.CaseIndex}/occurred_at_utc")
            : new CorrelationTimeReference(fallback, "capability.started_at_utc", "/capability/started_at_utc");
    }

    private static CorrelationTimeReference ResolveCorrelationTime(string? localField, LocalResolver resolver, CorrelationTimeReference fallback) =>
        !string.IsNullOrWhiteSpace(localField)
        && resolver.Resolve(localField)?.ToString() is { } value
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? new CorrelationTimeReference(parsed, localField, resolver.JsonPointer(localField))
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
        if (candidate.CustomActionNameStandards.Count > 0)
        {
            var customStatus = candidate.VendorActionName is null
                ? "not_evaluated"
                : candidate.CustomActionNameMatched == true
                    ? "passed"
                    : "failed";
            baselineMatches.Add(new JsonObject
            {
                ["kind"] = "custom_filter",
                ["requirement_id"] = $"{expectationId}-custom-action-name",
                ["status"] = customStatus,
                ["local_field"] = null,
                ["local_json_pointer"] = null,
                ["canonical_field"] = "event.action",
                ["raw_field"] = "Action.Name",
                ["raw_json_pointer"] = RawFieldNode(candidate.Event, "Action.Name").Pointer,
                ["expected"] = new JsonArray(candidate.CustomActionNameStandards.Select(value => (JsonNode)value).ToArray()),
                ["actual"] = candidate.VendorActionName,
                ["message"] = customStatus switch
                {
                    "passed" => "原始 Action.Name 符合本次自定义标准。",
                    "failed" => "原始 Action.Name 不符合本次自定义标准。",
                    _ => "候选记录没有可读取的原始 Action.Name。",
                },
            });
        }
        if (candidate.CustomChildFileCreateOpNameStandards.Count > 0)
        {
            var customStatus = candidate.ChildFileCreateOpName is null
                ? "not_evaluated"
                : candidate.CustomChildFileCreateOpNameMatched == true
                    ? "passed"
                    : "failed";
            baselineMatches.Add(new JsonObject
            {
                ["kind"] = "custom_filter",
                ["requirement_id"] = $"{expectationId}-custom-child-file-create-op-name",
                ["status"] = customStatus,
                ["local_field"] = null,
                ["local_json_pointer"] = null,
                ["canonical_field"] = "file.operation_name",
                ["raw_field"] = "Child.FileCreateOpName",
                ["raw_json_pointer"] = RawFieldNode(candidate.Event, "Child.FileCreateOpName").Pointer,
                ["expected"] = new JsonArray(candidate.CustomChildFileCreateOpNameStandards.Select(value => (JsonNode)value).ToArray()),
                ["actual"] = candidate.ChildFileCreateOpName,
                ["message"] = customStatus switch
                {
                    "passed" => "原始 Child.FileCreateOpName 符合本次自定义标准。",
                    "failed" => "原始 Child.FileCreateOpName 不符合本次自定义标准。",
                    _ => "候选记录没有可读取的原始 Child.FileCreateOpName。",
                },
            });
        }
        var eventTimeSource = candidate.Event.SourceField("event.created");
        baselineMatches.Add(new JsonObject
        {
            ["kind"] = "correlation",
            ["requirement_id"] = $"{expectationId}-time-difference",
            ["status"] = candidate.Event.Get("event.created") is null ? "not_evaluated" : candidate.TimeDifferenceMatched ? "passed" : "failed",
            ["local_field"] = candidate.LocalTimeField,
            ["local_json_pointer"] = candidate.LocalTimeJsonPointer,
            ["canonical_field"] = "event.created",
            ["raw_field"] = eventTimeSource,
            ["raw_json_pointer"] = eventTimeSource is null ? null : JsonPointer(eventTimeSource),
            ["expected"] = new JsonObject { ["min"] = 0, ["max"] = candidate.MaximumTimeDifferenceMs },
            ["actual"] = candidate.TimeDistanceMs == long.MaxValue ? null : candidate.TimeDistanceMs,
            ["message"] = candidate.Event.Get("event.created") is null
                ? "候选记录没有可计算的事件时间。"
                : candidate.TimeDifferenceMatched
                    ? $"EDR 时间相对本地为 {FormatSignedTimeOffset(candidate.TimeOffsetMs)}，绝对差 {candidate.TimeDistanceMs} ms。"
                    : $"EDR 时间相对本地为 {FormatSignedTimeOffset(candidate.TimeOffsetMs)}，绝对差超过 {candidate.MaximumTimeDifferenceMs} ms。",
        });
        return new JsonObject
        {
            ["expectation_id"] = expectationId,
            ["rank"] = rank,
            ["confidence"] = CandidateConfidence(candidate),
            ["correlation_score"] = candidate.Score,
            ["anchor_qualified"] = candidate.AnchorQualified,
            ["eligible_for_validation"] = candidate.Qualified,
            ["qualification_reason"] = candidate.QualificationReason,
            ["event_type_hint_matched"] = candidate.TypeHintMatched,
            ["event_action_hint_matched"] = candidate.ActionHintMatched,
            ["custom_action_name_expected"] = new JsonArray(candidate.CustomActionNameStandards.Select(value => (JsonNode)value).ToArray()),
            ["custom_action_name_actual"] = candidate.VendorActionName,
            ["custom_action_name_matched"] = candidate.CustomActionNameMatched,
            ["custom_child_file_create_op_name_expected"] = new JsonArray(candidate.CustomChildFileCreateOpNameStandards.Select(value => (JsonNode)value).ToArray()),
            ["custom_child_file_create_op_name_actual"] = candidate.ChildFileCreateOpName,
            ["custom_child_file_create_op_name_matched"] = candidate.CustomChildFileCreateOpNameMatched,
            ["maximum_time_difference_ms"] = candidate.MaximumTimeDifferenceMs,
            ["time_difference_matched"] = candidate.TimeDifferenceMatched,
            ["time_distance_ms"] = candidate.TimeDistanceMs,
            ["time_offset_ms"] = candidate.TimeOffsetMs,
            ["local_event_time_utc"] = Values.Utc(candidate.LocalCorrelationTime),
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

    private static bool EventWithinWindow(CanonicalEvent item, DateTimeOffset start, DateTimeOffset end, CorrelationDefinition correlation)
    {
        if (item.Get("event.created") is not string createdText || !DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)) return false;
        return created >= start.AddSeconds(-correlation.TimeBeforeSeconds) && created <= end.AddSeconds(correlation.TimeAfterSeconds);
    }

    private static bool EventWithinCandidateTimeLimit(CanonicalEvent item, DateTimeOffset localTime, int candidateTimeLimitMs)
    {
        var eventTime = CanonicalEventTime(item);
        return eventTime is not null && Math.Abs((eventTime.Value - localTime).TotalMilliseconds) <= candidateTimeLimitMs;
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
        var acceptedValues = definition.AcceptedValues
            .Select(item => new
            {
                Definition = item,
                Value = item.Value is string acceptedTemplate ? resolver.Expand(acceptedTemplate) : item.Value,
            })
            .ToArray();
        var acceptedMatch = acceptedValues.FirstOrDefault(item =>
            Equivalent(normalizedActual, Normalize(item.Value, definition.Normalizers)));
        var acceptedValueMatched = acceptedMatch is not null;
        bool? passed = definition.Operator switch
        {
            "present" => IsPresent(normalizedActual),
            "absent" => !IsPresent(normalizedActual),
            "equals" or "ref_equals" => Equivalent(normalizedActual, normalizedExpected) || acceptedValueMatched,
            "not_equals" => !Equivalent(normalizedActual, normalizedExpected),
            "contains" => normalizedActual?.ToString()?.Contains(normalizedExpected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true,
            "regex" when normalizedExpected is not null => Regex.IsMatch(normalizedActual?.ToString() ?? string.Empty, normalizedExpected.ToString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            "one_of" when normalizedExpected is IEnumerable values and not string => values.Cast<object?>().Any(value => Equivalent(normalizedActual, value)),
            "range" => InRange(normalizedActual, normalizedExpected),
            "cidr" => InCidr(normalizedActual, normalizedExpected),
            "timestamp_between" => TimestampBetween(normalizedActual, normalizedExpected),
            _ => null,
        };
        var displayedExpected = acceptedValues.Length == 0
            ? expected
            : new object?[] { expected }.Concat(acceptedValues.Select(item => item.Value)).ToArray();
        return new AssertionEvaluation(definition.Field, definition.Operator, definition.Severity, passed is null ? "not_evaluated" : passed.Value ? "passed" : "failed", displayedExpected, actual,
            passed is null
                ? $"尚未实现操作符：{definition.Operator}"
                : passed.Value
                    ? acceptedMatch?.Definition.Message
                    : "实际值不满足期望。");
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
                string? selectedSource = null;
                try
                {
                    IReadOnlyList<string> candidateSources = rule.Sources.Count > 0
                        ? rule.Sources
                        : string.IsNullOrWhiteSpace(rule.Source) ? [] : [rule.Source];
                    value = rule.Constant;
                    foreach (var candidateSource in candidateSources)
                    {
                        if (!record.TryGetProperty(candidateSource, out var source)) continue;
                        var candidateValue = Scalar(source);
                        if (candidateValue is null || candidateValue is string candidateText && string.IsNullOrWhiteSpace(candidateText)) continue;
                        value = candidateValue;
                        selectedSource = candidateSource;
                        break;
                    }
                    if (rule.HasOnEmpty && value is string text && string.IsNullOrWhiteSpace(text)) value = rule.OnEmpty;
                    if (rule.HasOnZero && TryDecimal(value, out var number) && number == 0) value = rule.OnZero;
                    foreach (var transform in rule.Transform) value = Transform(value, transform);
                }
                catch (Exception) when (rule.HasOnError)
                {
                    value = rule.OnError;
                }
                fields[field] = value;
                sourceFields[field] = selectedSource ?? rule.Source ?? rule.Sources.FirstOrDefault();
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
            "iso8601" when DateTimeOffset.TryParse(scalar?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) => timestamp,
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
            if (!record.TryGetProperty(field, out var actual) || !ConditionMatches(Scalar(actual), expected)) return false;
        }
        return true;
    }

    private static bool ConditionMatches(object? actual, object? expected)
    {
        if (expected is System.Collections.IEnumerable values and not string)
        {
            return values.Cast<object?>().Any(value => Equivalent(actual, value));
        }
        return Equivalent(actual, expected);
    }

    private static object? Transform(object? value, string transform) => transform switch
    {
        "lowercase" => value?.ToString()?.ToLowerInvariant(),
        "trim" => value?.ToString()?.Trim(),
        "windows_path" => NormalizeWindowsPath(value?.ToString()),
        "network_direction" => NormalizeNetworkDirection(value?.ToString()),
        "http_method" => NormalizeHttpMethod(value?.ToString()),
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
                "registry_hive_path" => NormalizeRegistryHivePath(value?.ToString()),
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

    private static string? NormalizeRegistryHivePath(string? value)
    {
        var path = NormalizeWindowsPath(value);
        if (string.IsNullOrWhiteSpace(path)) return path;
        path = Regex.Replace(path, @"^hkey_users\\s-1-5-21-(?:\d+-){3}\d+\\", @"hkey_current_user\", RegexOptions.CultureInvariant);
        path = Regex.Replace(path, @"^hkcu\\", @"hkey_current_user\", RegexOptions.CultureInvariant);
        path = Regex.Replace(path, @"^hklm\\", @"hkey_local_machine\", RegexOptions.CultureInvariant);
        path = Regex.Replace(path, @"^hkcr\\", @"hkey_classes_root\", RegexOptions.CultureInvariant);
        return Regex.Replace(path, @"^hkcc\\", @"hkey_current_config\", RegexOptions.CultureInvariant);
    }

    private static string? NormalizeNetworkDirection(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "出站" or "outbound" or "egress" => "outbound",
        "入站" or "inbound" or "ingress" => "inbound",
        var direction => direction,
    };

    private static string? NormalizeHttpMethod(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "httpget" or "get" => "GET",
        "httppost" or "post" => "POST",
        "httpput" or "put" => "PUT",
        "httpdelete" or "delete" => "DELETE",
        "httphead" or "head" => "HEAD",
        var method => method?.ToUpperInvariant(),
    };

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

    private static MethodOutcome CreateMethodOutcome(
        CloudExpectation expectation,
        string status,
        int requirementStartIndex,
        JsonArray requirements,
        int localRequirementCount,
        int passedLocalRequirementCount,
        int candidateCount,
        int qualifiedCandidateCount,
        JsonObject? selectedEvent,
        Candidate? selectedCandidate,
        IReadOnlyList<string> warnings)
    {
        var methodRequirements = requirements
            .Skip(requirementStartIndex)
            .Select(value => value?.AsObject())
            .Where(value => value is not null)
            .Cast<JsonObject>()
            .ToArray();
        var result = new JsonObject
        {
            ["method_id"] = expectation.Method?.Id ?? expectation.Id,
            ["method_title"] = expectation.Method?.Title ?? expectation.Id,
            ["expectation_id"] = expectation.Id,
            ["status"] = status,
            ["selected_for_conclusion"] = false,
            ["candidate_count"] = candidateCount,
            ["qualified_candidate_count"] = qualifiedCandidateCount,
            ["passed_requirement_count"] = passedLocalRequirementCount + methodRequirements.Count(value => value["status"]?.GetValue<string>() == "passed"),
            ["requirement_count"] = localRequirementCount + methodRequirements.Length,
            ["warnings"] = new JsonArray(warnings.Select(value => (JsonNode)value).ToArray()),
        };
        return new MethodOutcome(
            expectation,
            status,
            result,
            selectedEvent?.DeepClone().AsObject(),
            selectedCandidate,
            selectedCandidate?.Score ?? double.MinValue,
            selectedCandidate?.TimeDistanceMs ?? long.MaxValue,
            warnings);
    }

    private static (JsonObject Stage, string Status) RelationshipStageJson(
        CloudRelationship relationship,
        MethodOutcome left,
        MethodOutcome right,
        JsonArray outputRequirements)
    {
        var leftCandidate = left.SelectedCandidate;
        var rightCandidate = right.SelectedCandidate;
        var candidatesQualified = leftCandidate?.Qualified == true && rightCandidate?.Qualified == true;
        var leftPid = leftCandidate?.Event.Get("process.pid");
        var rightPid = rightCandidate?.Event.Get("process.pid");
        var leftExecutable = NormalizeWindowsPath(leftCandidate?.Event.Get("process.executable")?.ToString());
        var rightExecutable = NormalizeWindowsPath(rightCandidate?.Event.Get("process.executable")?.ToString());
        bool? samePid = candidatesQualified && leftPid is not null && rightPid is not null
            ? Equivalent(leftPid, rightPid)
            : null;
        bool? sameExecutable = candidatesQualified
            && !string.IsNullOrWhiteSpace(leftExecutable)
            && !string.IsNullOrWhiteSpace(rightExecutable)
                ? string.Equals(leftExecutable, rightExecutable, StringComparison.OrdinalIgnoreCase)
                : null;

        var leftCloudTime = CanonicalEventTime(leftCandidate);
        var rightCloudTime = CanonicalEventTime(rightCandidate);
        long? localIntervalMs = candidatesQualified
            ? (long)Math.Round((rightCandidate!.LocalCorrelationTime - leftCandidate!.LocalCorrelationTime).TotalMilliseconds)
            : null;
        long? cloudIntervalMs = candidatesQualified && leftCloudTime is not null && rightCloudTime is not null
            ? (long)Math.Round((rightCloudTime.Value - leftCloudTime.Value).TotalMilliseconds)
            : null;
        bool? ordered = cloudIntervalMs is null ? null : cloudIntervalMs >= 0;
        long? intervalDifferenceMs = localIntervalMs is null || cloudIntervalMs is null
            ? null
            : Math.Abs(cloudIntervalMs.Value - localIntervalMs.Value);
        bool? intervalMatched = intervalDifferenceMs is null
            ? null
            : intervalDifferenceMs <= relationship.MaxIntervalDifferenceMs;

        var checks = new[]
        {
            RelationshipRequirementJson(relationship, "same-pid", "连接记录与文件记录必须属于同一 PID",
                "relationship.process.pid", "equals", samePid, leftPid, rightPid,
                samePid is null ? "至少一条所选记录缺少可靠 PID，无法证明同一进程。" : samePid.Value ? "两条 EDR 记录的进程 PID 一致。" : "两条 EDR 记录的进程 PID 不一致。"),
            RelationshipRequirementJson(relationship, "same-executable", "连接记录与文件记录必须属于同一程序路径",
                "relationship.process.executable", "equals", sameExecutable, leftExecutable, rightExecutable,
                sameExecutable is null ? "至少一条所选记录缺少程序路径，无法证明同一进程。" : sameExecutable.Value ? "两条 EDR 记录的规范化程序路径一致。" : "两条 EDR 记录的程序路径不一致。"),
            RelationshipRequirementJson(relationship, "ordered", "文件写入时间必须晚于或等于网络连接时间",
                "relationship.event.created_order", "ordered_after", ordered,
                leftCloudTime is null ? null : Values.Utc(leftCloudTime.Value),
                rightCloudTime is null ? null : Values.Utc(rightCloudTime.Value),
                ordered is null ? "至少一条所选记录缺少可靠时间，无法检查连续行为顺序。" : ordered.Value ? "EDR 文件写入时间不早于网络连接时间。" : "EDR 文件写入时间早于网络连接时间，行为顺序反转。"),
            RelationshipRequirementJson(relationship, "interval", $"云端行为间隔与本地行为间隔的误差必须不超过 {relationship.MaxIntervalDifferenceMs} ms",
                "relationship.interval_difference_ms", "range", intervalMatched,
                new JsonObject { ["local_interval_ms"] = localIntervalMs, ["maximum_difference_ms"] = relationship.MaxIntervalDifferenceMs },
                new JsonObject { ["cloud_interval_ms"] = cloudIntervalMs, ["difference_ms"] = intervalDifferenceMs },
                intervalMatched is null ? "缺少可靠的两侧时间，无法比较连续行为间隔。" : intervalMatched.Value
                    ? $"本地间隔 {localIntervalMs} ms，云端间隔 {cloudIntervalMs} ms，误差 {intervalDifferenceMs} ms。"
                    : $"本地间隔 {localIntervalMs} ms，云端间隔 {cloudIntervalMs} ms，误差 {intervalDifferenceMs} ms，超过允许值。"),
        };
        foreach (var check in checks) outputRequirements.Add(check);

        var status = "PASS";
        foreach (var check in checks)
        {
            status = check["status"]?.GetValue<string>() switch
            {
                "failed" => Worse(status, "FAIL"),
                "not_evaluated" => Worse(status, "INCONCLUSIVE"),
                _ => status,
            };
        }
        var message = status switch
        {
            "PASS" => $"连接与文件写入由同一 PID 和程序产生，顺序正确；云端间隔 {cloudIntervalMs} ms与本地间隔 {localIntervalMs} ms一致。",
            "FAIL" => "连接记录与文件写入记录未形成同一进程的连续行为证据链。",
            _ => "所选 EDR 记录不足以完整证明连接与文件写入属于同一进程的连续行为。",
        };
        var warnings = status == "PASS" ? new JsonArray() : new JsonArray((JsonNode)message);
        return (new JsonObject
        {
            ["kind"] = "relationship",
            ["sequence"] = relationship.Stage.Sequence,
            ["title"] = relationship.Stage.Title,
            ["expectation_id"] = relationship.Id,
            ["depends_on"] = relationship.Stage.DependsOn,
            ["status"] = status,
            ["candidate_count"] = 2,
            ["qualified_candidate_count"] = (leftCandidate?.Qualified == true ? 1 : 0) + (rightCandidate?.Qualified == true ? 1 : 0),
            ["selected_event"] = null,
            ["related_expectation_ids"] = new JsonArray((JsonNode)relationship.LeftExpectation, (JsonNode)relationship.RightExpectation),
            ["relationship"] = new JsonObject
            {
                ["type"] = relationship.Type,
                ["left_expectation_id"] = relationship.LeftExpectation,
                ["right_expectation_id"] = relationship.RightExpectation,
                ["same_process_pid"] = samePid,
                ["same_process_executable"] = sameExecutable,
                ["ordered"] = ordered,
                ["local_interval_ms"] = localIntervalMs,
                ["cloud_interval_ms"] = cloudIntervalMs,
                ["interval_difference_ms"] = intervalDifferenceMs,
                ["maximum_interval_difference_ms"] = relationship.MaxIntervalDifferenceMs,
            },
            ["order_status"] = ordered is null ? "not_evaluated" : ordered.Value ? "passed" : "failed",
            ["order_message"] = message,
            ["warnings"] = warnings,
        }, status);
    }

    private static JsonObject RelationshipRequirementJson(
        CloudRelationship relationship,
        string suffix,
        string title,
        string field,
        string @operator,
        bool? matched,
        object? expected,
        object? actual,
        string message) => new()
    {
        ["requirement_id"] = $"{relationship.Id}-{suffix}",
        ["scope"] = "cloud",
        ["title_zh"] = title,
        ["expectation_id"] = relationship.Id,
        ["field"] = field,
        ["operator"] = @operator,
        ["severity"] = "required",
        ["status"] = matched is null ? "not_evaluated" : matched.Value ? "passed" : "failed",
        ["expected"] = Values.ToNode(expected),
        ["actual"] = Values.ToNode(actual),
        ["message"] = message,
    };

    private static DateTimeOffset? CanonicalEventTime(Candidate? candidate)
    {
        if (candidate?.Event.Get("event.created") is not string text
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return null;
        return parsed.ToUniversalTime();
    }

    private static DateTimeOffset? SelectedEventTime(JsonObject? selectedEvent)
    {
        if (selectedEvent?["event_time_utc"] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return null;
        return parsed.ToUniversalTime();
    }

    private static JsonObject CapabilityResult(string caseRunId, string capabilityId, string localStatus,
        string validationStatus, string exportCoverage, int candidateCount, JsonObject? selected,
        JsonArray candidates, JsonArray assertions, JsonArray requirements, JsonObject? methodSelection,
        JsonArray methods, JsonObject? stageFlow, JsonArray stages, JsonArray warnings) => new()
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
        ["method_selection"] = methodSelection,
        ["method_results"] = methods,
        ["stage_flow"] = stageFlow,
        ["stage_results"] = stages,
        ["warnings"] = warnings,
    };

    private static JsonObject NotCompared(string capabilityId, string caseRunId, string localStatus, string warning) =>
        CapabilityResult(caseRunId, capabilityId, localStatus, "NOT_COMPARED", "insufficient", 0,
            null, new JsonArray(), new JsonArray(), new JsonArray(), null, new JsonArray(),
            null, new JsonArray(), new JsonArray(warning));

    private static string Worse(string current, string candidate)
    {
        return StatusRank(candidate) > StatusRank(current) ? candidate : current;
    }

    private static int StatusRank(string value) => value switch { "PASS" => 0, "PARTIAL" => 1, "INCONCLUSIVE" => 2, "FAIL" => 3, _ => 4 };

    private static string ValidationStatusLabel(string value) => value switch
    {
        "PASS" => "通过",
        "PARTIAL" => "部分通过",
        "INCONCLUSIVE" => "无法判定",
        "FAIL" => "失败",
        _ => value,
    };

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

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeEdrFieldStandards(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? configured,
        string rawField,
        IReadOnlySet<string>? allowedCapabilityIds = null)
    {
        if (configured is null || configured.Count == 0) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (configured.Count > 128) throw new ArgumentException($"{rawField} 自定义标准最多覆盖 128 项能力。");
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (capabilityId, values) in configured)
        {
            if (string.IsNullOrWhiteSpace(capabilityId) || capabilityId.Length > 128 || capabilityId.Any(char.IsControl))
            {
                throw new ArgumentException($"{rawField} 自定义标准包含无效 capability_id。");
            }
            var normalizedCapabilityId = capabilityId.Trim();
            if (allowedCapabilityIds is not null && !allowedCapabilityIds.Contains(normalizedCapabilityId))
            {
                throw new ArgumentException($"{rawField} 只允许配置到五项文件能力；{normalizedCapabilityId} 不在允许范围内。");
            }
            if (values.Count > 20) throw new ArgumentException($"能力 {capabilityId} 最多配置 20 个 {rawField} 标准值。");
            var normalizedValues = values
                .Select(value => value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedValues.Any(value => value.Length > 128 || value.Any(char.IsControl)))
            {
                throw new ArgumentException($"能力 {capabilityId} 的 {rawField} 标准值无效或超过 128 个字符。");
            }
            if (normalizedValues.Length > 0) normalized[normalizedCapabilityId] = normalizedValues;
        }
        return normalized;
    }

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
        if (request.StrongCorrelationTimeMs is < 1 or > MaximumStrongCorrelationTimeMs)
        {
            throw new ArgumentException($"强关联时间必须是 1..{MaximumStrongCorrelationTimeMs} ms 的整数。");
        }
        if (request.CandidateTimeLimitMs is < 1 or > MaximumCandidateTimeLimitMs)
        {
            throw new ArgumentException($"无关联候选事件时间上限必须是 1..{MaximumCandidateTimeLimitMs} ms 的整数。");
        }
        if (request.CandidateTimeLimitMs < request.StrongCorrelationTimeMs)
        {
            throw new ArgumentException("无关联候选事件时间上限不能小于强关联时间。");
        }
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
            if (route.Canonical.Values.Any(rule => rule.Sources.Count > 0
                && (rule.Source is not null || rule.Constant is not null
                    || rule.Sources.Any(string.IsNullOrWhiteSpace)
                    || rule.Sources.Distinct(StringComparer.Ordinal).Count() != rule.Sources.Count)))
            {
                throw new InvalidDataException($"Mapping route {route.RouteId} 的 sources 必须非空且唯一，并且不能与 source/constant 同时使用。");
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
        if (baseline.Correlation.MaxTimeDifferenceMs is < 1 or > 60_000) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 correlation.max_time_difference_ms 必须在 1..60000 内。");
        if (baseline.CloudExpectations is not { Count: > 0 }) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 缺少 cloud_expectations。");
        if (baseline.MethodSelection is { } selection)
        {
            if (selection.Strategy is not ("all" or "best")) throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 method_selection.strategy 无效。");
            if (selection.Strategy == "best"
                && (baseline.CloudExpectations.Any(expectation => expectation.Method is null)
                    || baseline.CloudExpectations.Select(expectation => expectation.Method!.Id).Distinct(StringComparer.Ordinal).Count() != baseline.CloudExpectations.Count))
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 使用 best 方法选择时必须为每个 cloud_expectation 声明唯一的 method.id。");
            }
        }
        foreach (var expectation in baseline.CloudExpectations)
        {
            if (expectation.Method is { } method && (string.IsNullOrWhiteSpace(method.Id) || string.IsNullOrWhiteSpace(method.Title)))
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation.method 缺少 id 或 title。");
            }
            if (expectation.EventActions.Count == 0 || expectation.Assertions.Count == 0 || expectation.Cardinality.Min < 0)
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation 无效。");
            }
            if (expectation.Cardinality.Max is { } maximum && maximum < expectation.Cardinality.Min)
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cardinality.max 小于 min。");
            }
            if (expectation.Assertions.Any(assertion => assertion.AcceptedValues.Any(value => value.Value is null)
                || (assertion.AcceptedValues.Count > 0 && assertion.Operator is not ("equals" or "ref_equals"))))
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 accepted_values 只允许用于 equals/ref_equals，且 value 不能为空。");
            }
            if (expectation.Correlation is { } correlation
                && (correlation.Anchors.Count == 0 || correlation.Anchors.Any(anchor => string.IsNullOrWhiteSpace(anchor.LocalField)
                    || string.IsNullOrWhiteSpace(anchor.CloudField))
                    || correlation.MaxTimeDifferenceMs is < 1 or > 60_000))
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation.correlation 无效。");
            }
        }
        var expectationIds = baseline.CloudExpectations.Select(expectation => expectation.Id).ToHashSet(StringComparer.Ordinal);
        if (expectationIds.Count != baseline.CloudExpectations.Count)
            throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_expectation.id 必须唯一。");
        foreach (var relationship in baseline.CloudRelationships)
        {
            if (relationship.Type != "same_process_continuity"
                || relationship.Stage is null
                || !expectationIds.Contains(relationship.LeftExpectation)
                || !expectationIds.Contains(relationship.RightExpectation)
                || relationship.LeftExpectation == relationship.RightExpectation
                || relationship.MaxIntervalDifferenceMs is < 1 or > 60_000)
            {
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的 cloud_relationship {relationship.Id} 无效。");
            }
        }
        var flowStages = baseline.CloudExpectations
            .Where(expectation => expectation.Stage is not null)
            .Select(expectation => (Id: expectation.Id, Stage: expectation.Stage!))
            .Concat(baseline.CloudRelationships.Select(relationship => (relationship.Id, relationship.Stage)))
            .OrderBy(item => item.Stage.Sequence)
            .ToArray();
        if (flowStages.Length > 0)
        {
            if (!flowStages.Select(item => item.Stage.Sequence).SequenceEqual(Enumerable.Range(1, flowStages.Length)))
                throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的验证阶段 sequence 必须从 1 开始且连续。");
            for (var index = 1; index < flowStages.Length; index++)
            {
                if (!string.Equals(flowStages[index].Stage.DependsOn, flowStages[index - 1].Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"BASELINE {baseline.BaselineId} 的阶段 {flowStages[index].Id} 必须依赖紧邻的前一阶段。");
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
