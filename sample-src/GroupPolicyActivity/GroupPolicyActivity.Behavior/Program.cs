using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GroupPolicyActivity;

internal static class Program
{
    private const int BehaviorError = 20;
    private const int KeySetValue = 0x0002;
    private const int KeyWow6464Key = 0x0100;
    private const uint RegSz = 1;
    private static readonly UIntPtr HkeyLocalMachine = new(0x80000002u);

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var keyPath = string.Empty;
        var valueName = string.Empty;
        try
        {
            var options = ArgumentReader.Parse(args);
            keyPath = ValidateKeyPath(options.Require("key-path"));
            valueName = options.Require("value-name");
            var valueData = options.Require("value-data");
            resultPath = Path.GetFullPath(options.Require("result"));
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            if (!string.Equals(valueName, "ValidationMarker", StringComparison.Ordinal))
                throw new ArgumentException("组策略样本只允许修改 ValidationMarker。");

            var before = Snapshot(keyPath, valueName);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            SetStringValue(keyPath, valueName, valueData);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var after = Snapshot(keyPath, valueName);
            var succeeded = before.KeyExists && before.ValueExists && after.KeyExists && after.ValueExists
                && !string.Equals(before.ValueData, valueData, StringComparison.Ordinal)
                && string.Equals(after.ValueData, valueData, StringComparison.Ordinal);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Succeeded = succeeded, OccurredAtUtc = occurredAtUtc, CompletedAtUtc = completedAtUtc,
                Hive = "HKLM", KeyPath = $"HKEY_LOCAL_MACHINE\\{keyPath}", ValueName = valueName,
                NativeApi = "RegSetValueExW", Before = before, After = after, Win32Error = 0,
                Error = succeeded ? null : "RegSetValueExW 后的本地状态未满足预期。",
            });
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
                    Succeeded = false, OccurredAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                    Hive = "HKLM", KeyPath = string.IsNullOrWhiteSpace(keyPath) ? "HKEY_LOCAL_MACHINE" : $"HKEY_LOCAL_MACHINE\\{keyPath}",
                    ValueName = valueName, NativeApi = "RegSetValueExW", Before = snapshot, After = snapshot,
                    Win32Error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    public static PolicySnapshot Snapshot(string keyPath, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(keyPath, writable: false);
        if (key is null) return new PolicySnapshot { KeyExists = false, ValueExists = false };
        var names = key.GetValueNames();
        var exists = names.Contains(valueName, StringComparer.Ordinal);
        return new PolicySnapshot
        {
            KeyExists = true, ValueExists = exists,
            ValueKind = exists ? key.GetValueKind(valueName).ToString() : null,
            ValueData = exists ? key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() : null,
        };
    }

    private static PolicySnapshot SafeSnapshot(string keyPath, string valueName)
    {
        try { return string.IsNullOrWhiteSpace(keyPath) ? new PolicySnapshot { KeyExists = false, ValueExists = false } : Snapshot(keyPath, valueName); }
        catch { return new PolicySnapshot { KeyExists = false, ValueExists = false }; }
    }

    private static void SetStringValue(string keyPath, string valueName, string valueData)
    {
        var status = RegOpenKeyExW(HkeyLocalMachine, keyPath, 0, KeySetValue | KeyWow6464Key, out var key);
        if (status != 0) throw new Win32Exception(status, $"RegOpenKeyExW 失败：{status}");
        try
        {
            var bytes = Encoding.Unicode.GetBytes(valueData + '\0');
            status = RegSetValueExW(key, valueName, 0, RegSz, bytes, bytes.Length);
            if (status != 0) throw new Win32Exception(status, $"RegSetValueExW 失败：{status}");
        }
        finally { RegCloseKey(key); }
    }

    private static string ValidateKeyPath(string value)
    {
        var path = value.Trim().TrimStart('\\');
        const string prefix = "SOFTWARE\\Policies\\EdrTest\\Runs\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || path.Length <= prefix.Length || path.Length > 240
            || path[prefix.Length..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("组策略键不在本轮 HKLM\\SOFTWARE\\Policies\\EdrTest\\Runs 隔离范围内。");
        return path;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(UIntPtr root, string subKey, int options, int desiredAccess, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr key, string valueName, int reserved, uint type, byte[] data, int dataSize);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr key);
}
