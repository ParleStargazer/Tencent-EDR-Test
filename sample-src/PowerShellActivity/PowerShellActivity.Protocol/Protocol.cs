using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace PowerShellActivity;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path) where T : class => ReliableProtocolFile.Read<T>(path, Options);
    public static void WriteAtomic<T>(string path, T value) => ReliableProtocolFile.WriteAtomic(path, value, Options);
}

public sealed record ScriptPlan(
    string Method,
    string Title,
    string InvocationKind,
    string Marker,
    string WarmupMarker,
    string SubmittedCommand,
    string ExpectedContent,
    string PowerShellExecutable,
    string CommandFormToken,
    IReadOnlyList<string> TargetArguments)
{
    public string TargetCommandLine => Quote(PowerShellExecutable) + " " + string.Join(" ", TargetArguments.Select(Quote));
    public string SubmittedCommandSha256 => Sha256(SubmittedCommand);
    public string ExpectedContentSha256 => Sha256(ExpectedContent);

    private static string Quote(string value) => value.Any(char.IsWhiteSpace)
        ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : value;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class PowerShellScriptPlans
{
    public static readonly string[] Methods = ["direct_command", "explicit_script_block"];
    public const string CommandFormToken = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -";

    public static ScriptPlan Create(string method, string nonce)
    {
        ValidateNonce(nonce);
        if (!Methods.Contains(method, StringComparer.Ordinal)) throw new ArgumentException($"不支持的 PowerShell 子测试：{method}");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-" };
        var warmupMarker = $"EDRTEST_READY_{method.ToUpperInvariant()}_{nonce}";
        if (method == "direct_command")
        {
            var marker = $"EDRTEST_COMMAND_{nonce}";
            var command = $"Write-Output '{marker}'";
            return new ScriptPlan(method, "一般命令执行", "direct_command", marker, warmupMarker,
                command, command, executable, CommandFormToken, arguments);
        }

        var scriptBlockMarker = $"EDRTEST_SCRIPTBLOCK_{nonce}";
        var inner = $"Write-Output '{scriptBlockMarker}'";
        var outer = $"& ([ScriptBlock]::Create(\"{inner}\"))";
        return new ScriptPlan(method, "显式脚本块执行", "scriptblock_create", scriptBlockMarker, warmupMarker,
            outer, inner, executable, CommandFormToken, arguments);
    }

    public static string WarmupCommand(ScriptPlan plan) => $"Write-Output '{plan.WarmupMarker}'";

    public static void ValidateNonce(string nonce)
    {
        if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("nonce 必须是 32 位十六进制字符串。");
    }
}

public sealed class PowerShellTargetReady
{
    public required string Method { get; init; }
    public required int TargetProcessId { get; init; }
    public required string TargetExecutable { get; init; }
    public required string TargetCommandLine { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}

public sealed class PowerShellExecutionGate
{
    public required string Method { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class PowerShellBehaviorResult
{
    public required string Method { get; init; }
    public required string InvocationKind { get; init; }
    public required int ActorProcessId { get; init; }
    public required int TargetProcessId { get; init; }
    public required string TargetExecutable { get; init; }
    public required string TargetCommandLine { get; init; }
    public required string CommandFormToken { get; init; }
    public required string SubmittedCommand { get; init; }
    public required string SubmittedCommandSha256 { get; init; }
    public required string ExpectedContent { get; init; }
    public required string ExpectedContentSha256 { get; init; }
    public required string Marker { get; init; }
    public required DateTimeOffset TargetStartedAtUtc { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public DateTimeOffset? TargetEndedAtUtc { get; init; }
    public required bool WarmupSucceeded { get; init; }
    public required bool OutputVerified { get; init; }
    public required bool Succeeded { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public string? EngineVersion { get; init; }
    public int? ExitCode { get; init; }
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

    public static ArgumentReader Parse(IEnumerable<string> values) => new(values);
    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少参数 --{name}。");
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        return value;
    }
}
