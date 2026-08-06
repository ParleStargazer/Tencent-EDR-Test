using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EdrTest;

public sealed class CapabilityManifest
{
    public string SchemaVersion { get; init; } = "1.1";
    public required string CapabilityId { get; init; }
    public required string Version { get; init; }
    public string? DisplayName { get; init; }
    public string? DisplayNameZh { get; init; }
    public string? DisplayNameEn { get; init; }
    public string? Description { get; init; }
    public required PlatformDefinition Platform { get; init; }
    public required string RiskLevel { get; init; }
    public required string RequiredPrivilege { get; init; }
    public required ProgramDefinition Controller { get; init; }
    public required List<ParticipantDefinition> Participants { get; init; }
    public Dictionary<string, ParameterDefinition> Parameters { get; init; } = [];
    public required TimeoutDefinition Timeouts { get; init; }
    public NetworkDefinition? Network { get; init; }
    public List<string> ExpectedFactKeys { get; init; } = [];
}

public sealed class PlatformDefinition
{
    public required string Os { get; init; }
    public string? MinimumVersion { get; init; }
    public required List<string> Architectures { get; init; }
}

public class ProgramDefinition
{
    public required string Executable { get; init; }
    public string? Sha256 { get; init; }
    public List<string> Arguments { get; init; } = [];
}

public sealed class ParticipantDefinition : ProgramDefinition
{
    public required string Role { get; init; }
    public string? InstanceName { get; init; }
}

public sealed class ParameterDefinition
{
    public required string Type { get; init; }
    public bool Required { get; init; }
    public JsonElement? Default { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public List<JsonElement>? AllowedValues { get; init; }
}

public sealed class TimeoutDefinition
{
    public int ExecuteSeconds { get; init; }
    public int CleanupSeconds { get; init; }
}

public sealed class NetworkDefinition
{
    public bool Required { get; init; }
    public List<string> AllowedDestinationParameters { get; init; } = [];
}

public sealed record CapabilityPackage(string ManifestPath, string PackageDirectory, string ManifestSha256, CapabilityManifest Manifest)
{
    public string ResolveProgram(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"程序路径必须相对能力包：{relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(PackageDirectory, relativePath));
        var root = Path.GetFullPath(PackageDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"程序路径越过能力包目录：{relativePath}");
        }

        return fullPath;
    }
}

public static partial class CapabilityCatalog
{
    private static readonly HashSet<string> RiskLevels = ["L0", "L1", "L2", "L3"];
    private static readonly HashSet<string> Privileges = ["standard_user", "administrator", "system"];
    private static readonly HashSet<string> Roles = ["actor", "target", "helper"];

    public static CapabilityPackage Load(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到能力清单。", fullPath);
        }

        var json = File.ReadAllText(fullPath);
        var manifest = JsonSerializer.Deserialize<CapabilityManifest>(json, JsonDefaults.Options)
            ?? throw new InvalidDataException("能力清单不是有效 JSON 对象。");
        Validate(manifest);
        var package = new CapabilityPackage(fullPath, Path.GetDirectoryName(fullPath)!, Hashing.TextSha256(json), manifest);
        ValidateProgram(package, manifest.Controller, "controller");
        foreach (var participant in manifest.Participants)
        {
            ValidateProgram(package, participant, participant.Role);
        }

        return package;
    }

    public static IReadOnlyList<CapabilityPackage> Discover(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "capability.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToArray();
    }

    public static JsonElement BuildParameters(CapabilityManifest manifest, string? suppliedJson)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, definition) in manifest.Parameters)
        {
            if (definition.Default is { } defaultValue)
            {
                values[name] = defaultValue.Clone();
            }
        }

        if (!string.IsNullOrWhiteSpace(suppliedJson))
        {
            using var supplied = JsonDocument.Parse(suppliedJson);
            if (supplied.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("--parameters 必须是 JSON 对象。");
            }

            foreach (var property in supplied.RootElement.EnumerateObject())
            {
                if (!manifest.Parameters.ContainsKey(property.Name))
                {
                    throw new InvalidDataException($"能力未声明参数：{property.Name}");
                }

                values[property.Name] = property.Value.Clone();
            }
        }

        foreach (var (name, definition) in manifest.Parameters)
        {
            if (definition.Required && !values.ContainsKey(name))
            {
                throw new InvalidDataException($"缺少必需参数：{name}");
            }

            if (values.TryGetValue(name, out var value)) ValidateParameter(name, definition, value);
        }

        return JsonSerializer.SerializeToElement(values, JsonDefaults.Options);
    }

    private static void Validate(CapabilityManifest manifest)
    {
        if (manifest.SchemaVersion is not ("1.0" or "1.1")) throw new InvalidDataException("仅支持能力清单 1.0/1.1。");
        if (!CapabilityIdRegex().IsMatch(manifest.CapabilityId)) throw new InvalidDataException("capability_id 格式无效。");
        if (!SemverRegex().IsMatch(manifest.Version)) throw new InvalidDataException("version 必须是 SemVer 三段式版本。");
        if (!string.Equals(manifest.Platform.Os, "windows", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("当前 Runner 仅支持 Windows 能力包。");
        if (manifest.Platform.Architectures.Count == 0) throw new InvalidDataException("platform.architectures 不能为空。");
        if (!RiskLevels.Contains(manifest.RiskLevel)) throw new InvalidDataException("risk_level 无效。");
        if (!Privileges.Contains(manifest.RequiredPrivilege)) throw new InvalidDataException("required_privilege 无效。");
        if (manifest.Timeouts.ExecuteSeconds is < 1 or > 3600) throw new InvalidDataException("execute_seconds 必须在 1..3600 内。");
        if (manifest.Timeouts.CleanupSeconds is < 1 or > 3600) throw new InvalidDataException("cleanup_seconds 必须在 1..3600 内。");
        if (manifest.Participants.Count == 0 || !manifest.Participants.Any(x => x.Role == "actor")) throw new InvalidDataException("能力包至少需要一个 Actor。");
        if (manifest.Participants.Any(x => !Roles.Contains(x.Role))) throw new InvalidDataException("参与程序 role 无效。");
    }

    private static void ValidateProgram(CapabilityPackage package, ProgramDefinition program, string role)
    {
        if (!program.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{role} 必须是 EXE：{program.Executable}");
        var path = package.ResolveProgram(program.Executable);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 {role} 程序。", path);
        if (program.Sha256 is not null && !string.Equals(program.Sha256, Hashing.FileSha256(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{role} 程序 SHA-256 与清单不一致：{program.Executable}");
        }
    }

    private static void ValidateParameter(string name, ParameterDefinition definition, JsonElement value)
    {
        var typeMatches = definition.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false,
        };
        if (!typeMatches) throw new InvalidDataException($"参数 {name} 的类型必须是 {definition.Type}。");
        if (definition.Type == "integer")
        {
            var number = value.GetInt64();
            if (definition.Minimum is { } minimum && number < minimum) throw new InvalidDataException($"参数 {name} 小于最小值。");
            if (definition.Maximum is { } maximum && number > maximum) throw new InvalidDataException($"参数 {name} 大于最大值。");
        }
        if (definition.AllowedValues is { Count: > 0 } allowed && !allowed.Any(x => string.Equals(x.GetRawText(), value.GetRawText(), StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"参数 {name} 不在 allowed_values 中。");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)+$")]
    private static partial Regex CapabilityIdRegex();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$")]
    private static partial Regex SemverRegex();
}
