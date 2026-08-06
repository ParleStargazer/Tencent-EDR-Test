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
    string? CloudManifestPath = null);

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
    public List<BaselineAssertion> Assertions { get; init; } = [];
}

public sealed class CardinalityDefinition
{
    public int Min { get; init; }
    public int? Max { get; init; }
}

internal sealed record CanonicalEvent(string RawRef, Dictionary<string, object?> Fields)
{
    public object? Get(string field) => Fields.TryGetValue(field, out var value) ? value : null;
}

internal sealed record Candidate(CanonicalEvent Event, double Score, IReadOnlyList<string> MatchedAnchors);

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
        var baselineByCapability = baselines.ToDictionary(x => x.Value.Capability.Id, x => x, StringComparer.Ordinal);
        var cloudEvents = LoadCloud(request.CloudPaths, mapping);
        var results = new JsonArray();

        foreach (var capabilityNode in localRoot["capabilities"]?.AsArray() ?? throw new InvalidDataException("本地导出缺少 capabilities。"))
        {
            var capability = capabilityNode?.AsObject() ?? throw new InvalidDataException("capabilities 元素必须是对象。");
            var capabilityId = RequiredString(capability, "capability_id");
            var caseRunId = RequiredString(capability, "case_run_id");
            var localStatus = RequiredString(capability, "status");
            if (!baselineByCapability.TryGetValue(capabilityId, out var baselineEntry))
            {
                results.Add(NotCompared(capabilityId, caseRunId, localStatus, "没有匹配的 BASELINE。"));
                continue;
            }

            results.Add(CompareCapability(localRoot, capability, baselineEntry.Value, cloudEvents));
        }

        var summary = Summarize(results);
        var root = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["comparison_id"] = Ids.NewUuid7(),
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
            ["capabilities"] = results,
        };

        var output = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, root.ToJsonString(JsonDefaults.Options) + Environment.NewLine, new UTF8Encoding(false));
        return root;
    }

    private static JsonObject CompareCapability(JsonObject localRoot, JsonObject capability, BaselineDefinition baseline, IReadOnlyList<CanonicalEvent> cloudEvents)
    {
        var capabilityId = RequiredString(capability, "capability_id");
        var caseRunId = RequiredString(capability, "case_run_id");
        var localStatus = RequiredString(capability, "status");
        var warnings = new JsonArray();
        if (localStatus != "LOCAL_PASS") return NotCompared(capabilityId, caseRunId, localStatus, "本地能力未达到 LOCAL_PASS。");

        var resolver = new LocalResolver(localRoot, capability);
        var failedLocal = baseline.LocalRequirements
            .Select(requirement => Evaluate(requirement, resolver.Resolve(requirement.Field), resolver))
            .Where(x => x.Status != "passed")
            .ToArray();
        if (failedLocal.Length > 0)
        {
            foreach (var assertion in failedLocal) warnings.Add($"本地前置断言未通过：{assertion.Field}");
            return CapabilityResult(caseRunId, capabilityId, localStatus, "INCONCLUSIVE", 0, null, new JsonArray(), warnings);
        }

        var start = DateTimeOffset.Parse(RequiredString(capability, "started_at_utc"), CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(RequiredString(capability, "ended_at_utc"), CultureInfo.InvariantCulture);
        var totalCandidates = 0;
        var outputAssertions = new JsonArray();
        JsonObject? firstSelected = null;
        var overallStatus = "PASS";

        foreach (var expectation in baseline.CloudExpectations)
        {
            var candidates = cloudEvents
                .Where(item => EventMatches(item, expectation, start, end, baseline.Correlation))
                .Select(item => Score(item, baseline.Correlation.Anchors, resolver))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Event.RawRef, StringComparer.Ordinal)
                .ToArray();
            totalCandidates += candidates.Length;
            if (candidates.Length < expectation.Cardinality.Min)
            {
                warnings.Add($"{expectation.Id} 候选数 {candidates.Length} 小于最小值 {expectation.Cardinality.Min}。");
                overallStatus = Worse(overallStatus, "FAIL");
                continue;
            }
            if (expectation.Cardinality.Max is { } maximum && candidates.Length > maximum)
            {
                warnings.Add($"{expectation.Id} 候选数 {candidates.Length} 超过最大值 {maximum}。");
                overallStatus = Worse(overallStatus, "INCONCLUSIVE");
            }
            if (candidates.Length == 0) continue;
            if (candidates.Length > 1 && candidates[0].Score == candidates[1].Score)
            {
                warnings.Add($"{expectation.Id} 最高分候选不唯一。");
                overallStatus = Worse(overallStatus, "INCONCLUSIVE");
                continue;
            }

            var selected = candidates[0];
            firstSelected ??= new JsonObject
            {
                ["raw_ref"] = selected.Event.RawRef,
                ["event_id"] = Values.ToNode(selected.Event.Get("event.id")),
                ["correlation_score"] = selected.Score,
                ["matched_anchors"] = new JsonArray(selected.MatchedAnchors.Select(x => (JsonNode)x).ToArray()),
            };
            foreach (var assertion in expectation.Assertions)
            {
                var evaluated = Evaluate(assertion, selected.Event.Get(assertion.Field), resolver);
                var fieldName = baseline.CloudExpectations.Count == 1 ? evaluated.Field : $"{expectation.Id}:{evaluated.Field}";
                outputAssertions.Add(evaluated.ToJson(fieldName));
                if (evaluated.Status == "not_evaluated") overallStatus = Worse(overallStatus, "INCONCLUSIVE");
                else if (evaluated.Status == "failed" && assertion.Severity == "required") overallStatus = Worse(overallStatus, "FAIL");
                else if (evaluated.Status == "failed" && assertion.Severity == "recommended") overallStatus = Worse(overallStatus, "PARTIAL");
            }
        }

        if (baseline.CloudExpectations.Count == 0)
        {
            warnings.Add("BASELINE 没有 cloud_expectations。");
            overallStatus = "INCONCLUSIVE";
        }
        return CapabilityResult(caseRunId, capabilityId, localStatus, overallStatus, totalCandidates, firstSelected, outputAssertions, warnings);
    }

    private static Candidate Score(CanonicalEvent item, IReadOnlyList<CorrelationAnchor> anchors, LocalResolver resolver)
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
        return new Candidate(item, requiresStrong && !matchedStrong ? 0 : score, matched);
    }

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

    private static IReadOnlyList<CanonicalEvent> LoadCloud(IReadOnlyList<string> paths, MappingProfile mapping)
    {
        var events = new List<CanonicalEvent>();
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
                    if (item.ValueKind == JsonValueKind.Object) MapRecord(item, $"{path}#/{index}", mapping, events);
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
                    if (document.RootElement.ValueKind == JsonValueKind.Object) MapRecord(document.RootElement, $"{path}#/{index}", mapping, events);
                    index++;
                }
            }
        }
        return events;
    }

    private static void MapRecord(JsonElement record, string rawRef, MappingProfile mapping, ICollection<CanonicalEvent> output)
    {
        if (!Matches(record, mapping.Input.RecordSelector.All)) return;
        foreach (var route in mapping.Routes)
        {
            if (!Matches(record, route.When)) continue;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
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
            }
            output.Add(new CanonicalEvent(rawRef, fields));
        }
    }

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

    private static JsonObject CapabilityResult(string caseRunId, string capabilityId, string localStatus, string validationStatus, int candidateCount, JsonObject? selected, JsonArray assertions, JsonArray warnings) => new()
    {
        ["case_run_id"] = caseRunId,
        ["capability_id"] = capabilityId,
        ["local_status"] = localStatus,
        ["validation_status"] = validationStatus,
        ["export_coverage"] = "assumed",
        ["candidate_count"] = candidateCount,
        ["selected_event"] = selected,
        ["assertions"] = assertions,
        ["warnings"] = warnings,
    };

    private static JsonObject NotCompared(string capabilityId, string caseRunId, string localStatus, string warning) =>
        CapabilityResult(caseRunId, capabilityId, localStatus, "NOT_COMPARED", 0, null, new JsonArray(), new JsonArray(warning));

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
        var output = Path.GetFullPath(request.OutputPath);
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
}
