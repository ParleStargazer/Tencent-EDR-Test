using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace FileManipulation;

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
        ReliableProtocolFile.Read<T>(path, Options);

    public static void WriteAtomic<T>(string path, T value)
    {
        ReliableProtocolFile.WriteAtomic(path, value, Options);
    }
}

public sealed class FileSnapshot
{
    public required bool Exists { get; init; }
    public required string Path { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? CreatedAtUtc { get; init; }
    public DateTimeOffset? ModifiedAtUtc { get; init; }
    public IReadOnlyList<string>? Attributes { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public string? DeletionMethod { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
    public required string Path { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public required FileSnapshot Before { get; init; }
    public required FileSnapshot After { get; init; }
    public FileSnapshot? SourceAfter { get; init; }
    public FileSnapshot? DestinationBefore { get; init; }
    public long? DesiredAccess { get; init; }
    public long? ShareMode { get; init; }
    public long? CreationDisposition { get; init; }
    public long? BytesRead { get; init; }
    public long? BytesWritten { get; init; }
    public long? WriteOffset { get; init; }
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
    public string? Get(string name) => values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        }
        return value;
    }
}
