namespace GroupPolicyActivity;

internal static class Program
{
    private const int BehaviorError = 20;
    public static int Main(string[] args)
    {
        string? resultPath = null;
        var keyPath = string.Empty;
        var valueName = string.Empty;
        var method = string.Empty;
        string? targetId = null;
        try
        {
            var options = ArgumentReader.Parse(args);
            method = options.Require("method");
            resultPath = Path.GetFullPath(options.Require("result"));
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            PolicySnapshot before;
            PolicySnapshot after;
            bool succeeded;
            DateTimeOffset occurredAtUtc;
            DateTimeOffset completedAtUtc;
            if (method == "isolated_policy_key")
            {
                keyPath = ValidateIsolatedKeyPath(options.Require("key-path"));
                valueName = options.Require("value-name");
                var valueData = options.Require("value-data");
                if (!string.Equals(valueName, "ValidationMarker", StringComparison.Ordinal))
                    throw new ArgumentException("隔离组策略方法只允许修改 ValidationMarker。");
                before = RegistryNative.Snapshot(keyPath, valueName);
                occurredAtUtc = DateTimeOffset.UtcNow;
                RegistryNative.WriteStringValue(keyPath, valueName, valueData);
                completedAtUtc = DateTimeOffset.UtcNow;
                after = RegistryNative.Snapshot(keyPath, valueName);
                succeeded = before.ValueExists && after.ValueExists
                    && !string.Equals(before.ValueDataSha256, after.ValueDataSha256, StringComparison.Ordinal)
                    && string.Equals(after.ValueData, valueData, StringComparison.Ordinal);
            }
            else if (method == "known_policy_same_value")
            {
                targetId = options.Require("known-policy-target");
                var target = KnownPolicyTargetCatalog.ResolveExact(targetId);
                keyPath = target.KeyPath;
                valueName = target.ValueName;
                occurredAtUtc = DateTimeOffset.UtcNow;
                var snapshots = RegistryNative.RewriteSameValue(keyPath, valueName);
                completedAtUtc = DateTimeOffset.UtcNow;
                before = snapshots.Before.Snapshot;
                after = snapshots.After.Snapshot;
                succeeded = before.ValueExists && after.ValueExists
                    && before.NativeType == after.NativeType
                    && before.RawDataLength == after.RawDataLength
                    && string.Equals(before.ValueDataSha256, after.ValueDataSha256, StringComparison.Ordinal);
            }
            else throw new ArgumentException($"未知组策略方法：{method}");

            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Method = method, Applicable = true, Succeeded = succeeded,
                OccurredAtUtc = occurredAtUtc, CompletedAtUtc = completedAtUtc,
                Hive = "HKLM", KeyPath = $"HKEY_LOCAL_MACHINE\\{keyPath}", ValueName = valueName,
                NativeApi = "RegSetValueExW", Before = before, After = after, TargetId = targetId, Win32Error = 0,
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
                    Method = string.IsNullOrWhiteSpace(method) ? "unknown" : method, Applicable = true,
                    Succeeded = false, OccurredAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                    Hive = "HKLM", KeyPath = string.IsNullOrWhiteSpace(keyPath) ? "HKEY_LOCAL_MACHINE" : $"HKEY_LOCAL_MACHINE\\{keyPath}",
                    ValueName = valueName, NativeApi = "RegSetValueExW", Before = snapshot, After = snapshot, TargetId = targetId,
                    Win32Error = exception is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static PolicySnapshot SafeSnapshot(string keyPath, string valueName)
    {
        try { return string.IsNullOrWhiteSpace(keyPath) ? new PolicySnapshot { KeyExists = false, ValueExists = false } : RegistryNative.Snapshot(keyPath, valueName); }
        catch { return new PolicySnapshot { KeyExists = false, ValueExists = false }; }
    }

    private static string ValidateIsolatedKeyPath(string value)
    {
        var path = value.Trim().TrimStart('\\');
        const string prefix = "SOFTWARE\\Policies\\EdrTest\\Runs\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || path.Length <= prefix.Length || path.Length > 240
            || path[prefix.Length..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("组策略键不在本轮 HKLM\\SOFTWARE\\Policies\\EdrTest\\Runs 隔离范围内。");
        return path;
    }

}
