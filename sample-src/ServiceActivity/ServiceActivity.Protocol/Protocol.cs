using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace ServiceActivity;

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

public sealed class ServiceSnapshot
{
    public required bool Exists { get; init; }
    public string? DisplayName { get; init; }
    public string? BinaryPath { get; init; }
    public string? StartType { get; init; }
    public string? Account { get; init; }
    public string? ServiceType { get; init; }
    public string? State { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string ServiceName { get; init; }
    public required string ExpectedDisplayName { get; init; }
    public required string ExpectedBinaryPath { get; init; }
    public required string NativeApi { get; init; }
    public required ServiceSnapshot Before { get; init; }
    public required ServiceSnapshot After { get; init; }
    public int? SystemEventId { get; init; }
    public bool? SystemEventFound { get; init; }
    public string? SystemEventQueryOutput { get; init; }
    public string? DiagnosticError { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
}

public static class ServiceClient
{
    public const string DemandStart = "demand";
    public const string DisabledStart = "disabled";
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint DeleteAccess = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceDisabled = 0x00000004;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    public static void Create(string serviceName, string displayName, string binaryPath, string startType)
    {
        ValidateServiceName(serviceName);
        ValidateDisplayName(displayName);
        ValidateTestBinaryPath(binaryPath);
        var manager = OpenManager(ScManagerConnect | ScManagerCreateService);
        try
        {
            var service = CreateServiceW(manager, serviceName, displayName,
                ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus | DeleteAccess,
                ServiceWin32OwnProcess, StartTypeValue(startType), ServiceErrorNormal,
                binaryPath, null, IntPtr.Zero, null, "LocalSystem", null);
            if (service == IntPtr.Zero) ThrowLastWin32("CreateServiceW");
            CloseServiceHandle(service);
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Modify(string serviceName, string displayName, string binaryPath, string startType)
    {
        ValidateServiceName(serviceName);
        ValidateDisplayName(displayName);
        ValidateTestBinaryPath(binaryPath);
        var manager = OpenManager(ScManagerConnect);
        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceChangeConfig | ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero) ThrowLastWin32("OpenServiceW");
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, StartTypeValue(startType), ServiceNoChange,
                    binaryPath, null, IntPtr.Zero, null, null, null, displayName))
                    ThrowLastWin32("ChangeServiceConfigW");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Delete(string serviceName, bool ignoreMissing = false)
    {
        ValidateServiceName(serviceName);
        var manager = OpenManager(ScManagerConnect);
        try
        {
            var service = OpenServiceW(manager, serviceName, DeleteAccess | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (ignoreMissing && error is ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete) return;
                throw new Win32Exception(error, $"OpenServiceW 失败：{error}");
            }
            try
            {
                if (!DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!(ignoreMissing && error == ErrorServiceMarkedForDelete))
                        throw new Win32Exception(error, $"DeleteService 失败：{error}");
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static ServiceSnapshot Snapshot(string serviceName)
    {
        ValidateServiceName(serviceName);
        var manager = OpenManager(ScManagerConnect);
        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete) return MissingSnapshot();
                throw new Win32Exception(error, $"OpenServiceW 失败：{error}");
            }
            try
            {
                QueryServiceConfigW(service, IntPtr.Zero, 0, out var bytesNeeded);
                var queryError = Marshal.GetLastWin32Error();
                if (bytesNeeded <= 0 || queryError != ErrorInsufficientBuffer)
                    throw new Win32Exception(queryError, $"QueryServiceConfigW 计算缓冲区失败：{queryError}");
                var buffer = Marshal.AllocHGlobal(bytesNeeded);
                try
                {
                    if (!QueryServiceConfigW(service, buffer, bytesNeeded, out _)) ThrowLastWin32("QueryServiceConfigW");
                    var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
                    if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                        ThrowLastWin32("QueryServiceStatusEx");
                    return new ServiceSnapshot
                    {
                        Exists = true,
                        DisplayName = Text(config.DisplayName),
                        BinaryPath = Text(config.BinaryPathName),
                        StartType = StartTypeName(config.StartType),
                        Account = Text(config.ServiceStartName),
                        ServiceType = ServiceTypeName(config.ServiceType),
                        State = ServiceStateName(status.CurrentState),
                    };
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static ServiceSnapshot MissingSnapshot() => new() { Exists = false };

    public static void WaitUntilMissing(string serviceName, int timeoutMs = 5_000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (Snapshot(serviceName).Exists)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"服务在 {timeoutMs} ms 内未从 SCM 数据库消失：{serviceName}");
            Thread.Sleep(50);
        }
    }

    public static void ValidateServiceName(string serviceName)
    {
        if (!serviceName.StartsWith("EdrTestSvc_", StringComparison.Ordinal)
            || serviceName.Length > 80 || serviceName.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("服务名不在本轮 EdrTestSvc_ 受控命名范围内。", nameof(serviceName));
    }

    public static void ValidateDisplayName(string displayName)
    {
        if (!displayName.StartsWith("EDRTEST|", StringComparison.Ordinal) || displayName.Length > 240)
            throw new ArgumentException("服务显示名缺少本轮 EDRTEST 标记。", nameof(displayName));
    }

    public static void ValidateTestBinaryPath(string binaryPath)
    {
        var command = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\"";
        if (!binaryPath.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            || !binaryPath.Contains(" /d /c ", StringComparison.OrdinalIgnoreCase)
            || !(binaryPath.EndsWith("exit 0", StringComparison.OrdinalIgnoreCase)
                || binaryPath.Contains("rem EDRTEST_", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("服务二进制路径不是受控的系统 cmd.exe 无害命令。", nameof(binaryPath));
    }

    private static IntPtr OpenManager(uint access)
    {
        var manager = OpenSCManagerW(null, null, access);
        if (manager == IntPtr.Zero) ThrowLastWin32("OpenSCManagerW");
        return manager;
    }

    private static uint StartTypeValue(string value) => value switch
    {
        DemandStart => ServiceDemandStart,
        DisabledStart => ServiceDisabled,
        _ => throw new ArgumentException($"不支持的服务启动类型：{value}"),
    };

    private static string StartTypeName(uint value) => value switch
    {
        0 => "boot", 1 => "system", 2 => "automatic", 3 => DemandStart, 4 => DisabledStart, _ => $"unknown:{value}",
    };

    private static string ServiceTypeName(uint value) => value switch
    {
        0x10 => "win32_own_process", 0x20 => "win32_share_process", 0x1 => "kernel_driver",
        0x2 => "file_system_driver", _ => $"0x{value:x}",
    };

    private static string ServiceStateName(uint value) => value switch
    {
        1 => "stopped", 2 => "start_pending", 3 => "stop_pending", 4 => "running",
        5 => "continue_pending", 6 => "pause_pending", 7 => "paused", _ => $"unknown:{value}",
    };

    private static string? Text(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);
    private static void ThrowLastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} 失败：{error}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(IntPtr manager, string serviceName, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? serviceStartName, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr serviceConfig, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess buffer,
        int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
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
}
