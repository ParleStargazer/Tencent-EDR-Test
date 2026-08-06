using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessActivity;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"协议文件不是有效的 {typeof(T).Name}：{path}");

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}

public sealed class ProcessSnapshot
{
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required string Executable { get; init; }
    public required string CommandLine { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public bool Attempted { get; init; } = true;
    public required bool Succeeded { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public ProcessSnapshot? Target { get; init; }

    public int? InitialThreadId { get; init; }
    public int? RequestedExitCode { get; init; }
    public int? ObservedExitCode { get; init; }
    public DateTimeOffset? ObservedExitAtUtc { get; init; }

    public string? AccessOperationName { get; init; }
    public uint? RequestedAccessMask { get; init; }
    public uint? GrantedAccessMask { get; init; }
    public bool? HandleObtained { get; init; }

    public string? ImagePath { get; init; }
    public string? ImageBaseAddress { get; init; }
    public long? ImageSizeBytes { get; init; }
    public string? ImageSha256 { get; init; }
    public bool? BeforeLoaded { get; init; }
    public bool? AfterLoaded { get; init; }

    public int? ThreadId { get; init; }
    public string? StartAddress { get; init; }
    public string? ParameterAddress { get; init; }
    public uint? CreationFlags { get; init; }

    public string? TamperTechnique { get; init; }
    public string? TargetAddress { get; init; }
    public int? SizeBytes { get; init; }
    public string? BeforeSha256 { get; init; }
    public string? AfterSha256 { get; init; }
    public bool? MemoryReleased { get; set; }
}

public sealed class ArgumentReader
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    private ArgumentReader(IEnumerable<string> arguments)
    {
        var items = arguments.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!item.StartsWith("--", StringComparison.Ordinal) || item.Length == 2)
            {
                throw new ArgumentException($"无法识别的参数：{item}");
            }

            var name = item[2..];
            if (index + 1 >= items.Length || items[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 --{name} 缺少值。");
            }

            if (!values.TryAdd(name, items[++index])) throw new ArgumentException($"参数 --{name} 重复。");
        }
    }

    public static ArgumentReader Parse(IEnumerable<string> arguments) => new(arguments);

    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少参数 --{name}。");

    public string Get(string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;

    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        }
        return value;
    }

    public uint GetUInt(string name, uint fallback)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!uint.TryParse(text, out var value)) throw new ArgumentException($"--{name} 必须是无符号整数。");
        return value;
    }
}
