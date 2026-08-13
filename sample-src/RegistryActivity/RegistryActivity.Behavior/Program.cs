using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace RegistryActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        string operation = "unknown";
        string keyPath = string.Empty;
        string valueName = string.Empty;
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation");
            keyPath = ValidateSubKey(options.Require("key-path"));
            valueName = options.Require("value-name");
            resultPath = Path.GetFullPath(options.Require("result"));
            var valueData = options.Require("value-data");
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);

            var before = Snapshot(keyPath, valueName);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            Execute(operation, keyPath, valueName, valueData);
            var after = Snapshot(keyPath, valueName);
            var succeeded = Verify(operation, before, after, valueData);
            var result = new BehaviorResult
            {
                Operation = operation,
                Succeeded = succeeded,
                OccurredAtUtc = occurredAtUtc,
                Hive = "HKCU",
                KeyPath = $"HKCU\\{keyPath}",
                ValueName = valueName,
                RegistryView = "default",
                Before = before,
                After = after,
                Win32Error = 0,
                Error = succeeded ? null : "注册表操作后的键或值状态未满足预期。",
            };
            ProtocolJson.WriteAtomic(resultPath, result);
            if (holdMs > 0) Thread.Sleep(holdMs);
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                var snapshot = SafeSnapshot(keyPath, valueName);
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = operation,
                    Succeeded = false,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Hive = "HKCU",
                    KeyPath = string.IsNullOrWhiteSpace(keyPath) ? "HKCU" : $"HKCU\\{keyPath}",
                    ValueName = valueName,
                    RegistryView = "default",
                    Before = snapshot,
                    After = snapshot,
                    Win32Error = exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static void Execute(string operation, string keyPath, string valueName, string valueData)
    {
        switch (operation)
        {
            case "create":
                if (Registry.CurrentUser.OpenSubKey(keyPath, writable: false) is not null)
                    throw new IOException($"创建测试要求注册表键事先不存在：HKCU\\{keyPath}");
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                    ?? throw new IOException("RegCreateKeyExW 未返回可写键。"))
                {
                    key.SetValue(valueName, valueData, RegistryValueKind.String);
                    key.Flush();
                }
                break;
            case "modify":
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
                    ?? throw new IOException($"修改测试要求注册表键事先存在：HKCU\\{keyPath}"))
                {
                    if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
                        throw new IOException($"修改测试要求注册表值事先存在：{valueName}");
                    key.SetValue(valueName, valueData, RegistryValueKind.String);
                    key.Flush();
                }
                break;
            case "delete":
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
                    ?? throw new IOException($"删除测试要求注册表键事先存在：HKCU\\{keyPath}"))
                {
                    if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
                        throw new IOException($"删除测试要求注册表值事先存在：{valueName}");
                    key.DeleteValue(valueName, throwOnMissingValue: true);
                    key.Flush();
                }
                Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: true);
                break;
            default:
                throw new ArgumentException($"不支持的注册表操作：{operation}");
        }
    }

    private static bool Verify(string operation, RegistrySnapshot before, RegistrySnapshot after, string expectedData) => operation switch
    {
        "create" => !before.KeyExists && !before.ValueExists && after.KeyExists && after.ValueExists
            && after.ValueKind == "String" && after.ValueData == expectedData,
        "modify" => before.KeyExists && before.ValueExists && after.KeyExists && after.ValueExists
            && before.ValueData != after.ValueData && after.ValueData == expectedData,
        "delete" => before.KeyExists && before.ValueExists && !after.KeyExists && !after.ValueExists,
        _ => false,
    };

    internal static RegistrySnapshot Snapshot(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null) return new RegistrySnapshot { KeyExists = false, ValueExists = false };
        var names = key.GetValueNames();
        var exists = names.Contains(valueName, StringComparer.OrdinalIgnoreCase);
        if (!exists) return new RegistrySnapshot { KeyExists = true, ValueExists = false };
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var text = value switch
        {
            null => null,
            string stringValue => stringValue,
            string[] values => string.Join("\0", values),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        };
        return new RegistrySnapshot
        {
            KeyExists = true,
            ValueExists = true,
            ValueKind = key.GetValueKind(valueName).ToString(),
            ValueData = text,
            ValueDataSha256 = text is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
        };
    }

    private static RegistrySnapshot SafeSnapshot(string keyPath, string valueName)
    {
        try { return string.IsNullOrWhiteSpace(keyPath) ? new RegistrySnapshot { KeyExists = false, ValueExists = false } : Snapshot(keyPath, valueName); }
        catch { return new RegistrySnapshot { KeyExists = false, ValueExists = false }; }
    }

    private static string ValidateSubKey(string value)
    {
        var keyPath = value.Trim().TrimStart('\\');
        if (!keyPath.StartsWith("Software\\EdrTest\\", StringComparison.OrdinalIgnoreCase)
            || keyPath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("注册表测试只允许 HKCU\\Software\\EdrTest 下的本轮临时键。", nameof(value));
        return keyPath;
    }
}
