using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace GroupPolicyActivity;

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

public sealed class PolicySnapshot
{
    public required bool KeyExists { get; init; }
    public required bool ValueExists { get; init; }
    public string? ValueKind { get; init; }
    public uint? NativeType { get; init; }
    public string? ValueData { get; init; }
    public string? ValueDataSha256 { get; init; }
    public int? RawDataLength { get; init; }
}

public sealed class BehaviorResult
{
    public required string Method { get; init; }
    public required bool Applicable { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string Hive { get; init; }
    public required string KeyPath { get; init; }
    public required string ValueName { get; init; }
    public required string NativeApi { get; init; }
    public required PolicySnapshot Before { get; init; }
    public required PolicySnapshot After { get; init; }
    public string? TargetId { get; init; }
    public int? Win32Error { get; init; }
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
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        return value;
    }
    public string Get(string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;
}

public sealed record KnownPolicyTarget(string Id, string KeyPath, string ValueName, string Title);

public static class KnownPolicyTargetCatalog
{
    public static readonly IReadOnlyList<KnownPolicyTarget> All =
    [
        new("windows-smart-screen-enable", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", "Windows SmartScreen 启用状态"),
        new("windows-smart-screen-level", @"SOFTWARE\Policies\Microsoft\Windows\System", "ShellSmartScreenLevel", "Windows SmartScreen 级别"),
        new("defender-smart-screen-control", @"SOFTWARE\Policies\Microsoft\Windows Defender\SmartScreen", "ConfigureAppInstallControl", "Defender SmartScreen 应用安装控制"),
        new("defender-smart-screen-enable", @"SOFTWARE\Policies\Microsoft\Windows Defender\SmartScreen", "ConfigureAppInstallControlEnabled", "Defender SmartScreen 应用安装控制状态"),
        new("windows-lsa-run-as-ppl", @"SOFTWARE\Policies\Microsoft\Windows\System", "RunAsPPL", "Windows LSA 受保护进程策略"),
        new("defender-disable-antispyware", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", "Defender AntiSpyware 策略"),
        new("defender-realtime-monitoring", @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", "Defender 实时保护策略"),
    ];

    public static IReadOnlyList<KnownPolicyTarget> ResolveCandidates(string selection)
    {
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase)) return All;
        var target = All.SingleOrDefault(value => string.Equals(value.Id, selection, StringComparison.Ordinal));
        return target is null
            ? throw new ArgumentException($"known_policy_target 不在白名单内：{selection}")
            : [target];
    }

    public static KnownPolicyTarget ResolveExact(string id) => ResolveCandidates(id).Single();
}

public sealed class NativePolicyValue
{
    public required PolicySnapshot Snapshot { get; init; }
    public required uint NativeType { get; init; }
    public required byte[] RawData { get; init; }
}

public static class RegistryNative
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;
    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;
    private const int KeyWow6464Key = 0x0100;
    private static readonly UIntPtr HkeyLocalMachine = new(0x80000002u);

    public static PolicySnapshot Snapshot(string keyPath, string valueName)
    {
        var status = RegOpenKeyExW(HkeyLocalMachine, keyPath, 0, KeyQueryValue | KeyWow6464Key, out var key);
        if (status == ErrorFileNotFound) return Missing(keyExists: false);
        if (status != 0) throw new Win32Exception(status, $"RegOpenKeyExW 读取失败：{status}");
        try
        {
            return QueryValue(key, valueName)?.Snapshot ?? Missing(keyExists: true);
        }
        finally { RegCloseKey(key); }
    }

    public static (NativePolicyValue Before, NativePolicyValue After) RewriteSameValue(string keyPath, string valueName)
    {
        var status = RegOpenKeyExW(HkeyLocalMachine, keyPath, 0, KeyQueryValue | KeySetValue | KeyWow6464Key, out var key);
        if (status != 0) throw new Win32Exception(status, $"RegOpenKeyExW 同值回写失败：{status}");
        try
        {
            var before = QueryValue(key, valueName)
                ?? throw new Win32Exception(ErrorFileNotFound, "白名单策略值在写入前已不存在；拒绝创建新值。");
            status = RegSetValueExW(key, valueName, 0, before.NativeType, before.RawData, before.RawData.Length);
            if (status != 0) throw new Win32Exception(status, $"RegSetValueExW 同值回写失败：{status}");
            var after = QueryValue(key, valueName)
                ?? throw new IOException("同值回写后策略值不可读取。");
            return (before, after);
        }
        finally { RegCloseKey(key); }
    }

    public static void WriteStringValue(string keyPath, string valueName, string valueData)
    {
        var status = RegOpenKeyExW(HkeyLocalMachine, keyPath, 0, KeySetValue | KeyWow6464Key, out var key);
        if (status != 0) throw new Win32Exception(status, $"RegOpenKeyExW 字符串写入失败：{status}");
        try
        {
            var data = Encoding.Unicode.GetBytes(valueData + '\0');
            status = RegSetValueExW(key, valueName, 0, 1, data, data.Length);
            if (status != 0) throw new Win32Exception(status, $"RegSetValueExW 字符串写入失败：{status}");
        }
        finally { RegCloseKey(key); }
    }

    private static NativePolicyValue? QueryValue(IntPtr key, string valueName)
    {
        uint type;
        uint size = 0;
        var status = RegQueryValueExW(key, valueName, IntPtr.Zero, out type, null, ref size);
        if (status == ErrorFileNotFound) return null;
        if (status is not (0 or ErrorMoreData)) throw new Win32Exception(status, $"RegQueryValueExW 读取大小失败：{status}");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var data = new byte[checked((int)size)];
            var actualSize = size;
            status = RegQueryValueExW(key, valueName, IntPtr.Zero, out type, data, ref actualSize);
            if (status == ErrorMoreData) { size = actualSize; continue; }
            if (status != 0) throw new Win32Exception(status, $"RegQueryValueExW 读取数据失败：{status}");
            if (actualSize != data.Length) Array.Resize(ref data, checked((int)actualSize));
            return new NativePolicyValue
            {
                NativeType = type,
                RawData = data,
                Snapshot = new PolicySnapshot
                {
                    KeyExists = true,
                    ValueExists = true,
                    ValueKind = KindName(type),
                    NativeType = type,
                    ValueData = DisplayValue(type, data),
                    ValueDataSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                    RawDataLength = data.Length,
                },
            };
        }
        throw new IOException("策略值在读取期间持续变化，无法取得稳定快照。");
    }

    private static PolicySnapshot Missing(bool keyExists) => new() { KeyExists = keyExists, ValueExists = false };
    private static string KindName(uint type) => type switch
    {
        0 => "None", 1 => "String", 2 => "ExpandString", 3 => "Binary", 4 => "DWord",
        7 => "MultiString", 11 => "QWord", _ => $"NativeType({type})",
    };
    private static string DisplayValue(uint type, byte[] data) => type switch
    {
        1 or 2 => Encoding.Unicode.GetString(data).TrimEnd('\0'),
        4 when data.Length >= sizeof(uint) => BitConverter.ToUInt32(data).ToString(CultureInfo.InvariantCulture),
        7 => string.Join(" | ", Encoding.Unicode.GetString(data).TrimEnd('\0').Split('\0', StringSplitOptions.RemoveEmptyEntries)),
        11 when data.Length >= sizeof(ulong) => BitConverter.ToUInt64(data).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(data).ToLowerInvariant(),
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(UIntPtr root, string subKey, int options, int desiredAccess, out IntPtr result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved, out uint type, byte[]? data, ref uint dataSize);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr key, string valueName, int reserved, uint type, byte[] data, int dataSize);
    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr key);
}
