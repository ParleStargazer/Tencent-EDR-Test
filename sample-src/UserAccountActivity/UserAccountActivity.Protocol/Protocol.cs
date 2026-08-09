using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace UserAccountActivity;

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

public sealed class BehaviorRequest
{
    public required string Operation { get; init; }
    public required string AccountName { get; init; }
    public required string Password { get; init; }
    public required string Nonce { get; init; }
    public required string Comment { get; init; }
    public required int SetupDelayMs { get; init; }
    public required int HoldMs { get; init; }
}

public sealed class AccountSnapshot
{
    public required bool Exists { get; init; }
    public required string Name { get; init; }
    public string? Sid { get; init; }
    public string? Domain { get; init; }
    public string? AccountType { get; init; }
    public string? FullName { get; init; }
    public string? Comment { get; init; }
    public uint? Flags { get; init; }
    public bool? Active { get; init; }
}

public sealed class SessionSnapshot
{
    public int? SessionId { get; init; }
    public string? LogonId { get; init; }
    public int? LogonType { get; init; }
    public string? AuthenticationPackage { get; init; }
    public string? SourceAddress { get; init; }
    public required bool TokenValidated { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
    public required string NativeApi { get; init; }
    public string? ChangedField { get; init; }
    public required AccountSnapshot Before { get; init; }
    public required AccountSnapshot After { get; init; }
    public SessionSnapshot? Session { get; init; }
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
}
