using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace NetworkActivity;

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

public sealed class EndpointInfo
{
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Family { get; init; }
}

public sealed class HelperReady
{
    public required string Operation { get; init; }
    public required EndpointInfo Endpoint { get; init; }
    public string? Url { get; init; }
    public string? DnsQuestion { get; init; }
}

public sealed class HelperResult
{
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required EndpointInfo Local { get; init; }
    public EndpointInfo? Remote { get; init; }
    public string? ReceivedNonce { get; init; }
    public string? RequestedPath { get; init; }
    public string? DnsQuestion { get; init; }
    public string? Error { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required EndpointInfo Local { get; init; }
    public required EndpointInfo Remote { get; init; }
    public required long BytesSent { get; init; }
    public required long BytesReceived { get; init; }
    public string? Url { get; init; }
    public string? Method { get; init; }
    public int? StatusCode { get; init; }
    public string? RequestNonce { get; init; }
    public string? DnsQuestion { get; init; }
    public string? DnsQueryType { get; init; }
    public IReadOnlyList<string>? DnsAnswers { get; init; }
    public string? DestinationPath { get; init; }
    public long? DownloadSizeBytes { get; init; }
    public string? DownloadMd5 { get; init; }
    public string? DownloadSha256 { get; init; }
    public DateTimeOffset? FileOccurredAtUtc { get; init; }
    public string? Error { get; init; }
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
                throw new ArgumentException($"无法识别的参数：{item}");
            var name = item[2..];
            if (index + 1 >= items.Length || items[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数 --{name} 缺少值。");
            if (!values.TryAdd(name, items[++index])) throw new ArgumentException($"参数 --{name} 重复。");
        }
    }

    public static ArgumentReader Parse(IEnumerable<string> arguments) => new(arguments);
    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少参数 --{name}。");
    public string? Get(string name) => values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        return value;
    }
}
