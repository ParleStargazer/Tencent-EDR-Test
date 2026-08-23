using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace DriverActivity;

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

public sealed class FileSnapshot
{
    public required bool Exists { get; init; }
    public string? Path { get; init; }
    public long? SizeBytes { get; init; }
    public long? ImageSizeBytes { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public string? Md5 { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class DriverSnapshot
{
    public required bool Loaded { get; init; }
    public required bool ServiceExists { get; init; }
    public required string ServiceName { get; init; }
    public required string ImagePath { get; init; }
    public string? ServiceState { get; init; }
    public string? BaseAddress { get; init; }
    public long? SizeBytes { get; init; }
    public long? ModuleSizeBytes { get; init; }
    public string? Md5 { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class BehaviorResult
{
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string ServiceName { get; init; }
    public required string DriverName { get; init; }
    public required string ImagePath { get; init; }
    public required string NativeApi { get; init; }
    public required DriverSnapshot Before { get; init; }
    public required DriverSnapshot After { get; init; }
    public FileSnapshot? FileBefore { get; init; }
    public FileSnapshot? FileAfter { get; init; }
    public string? Marker { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
}

public static class DriverClient
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint DeleteAccess = 0x00010000;
    private const uint ServiceKernelDriver = 0x00000001;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ScStatusProcessInfo = 0;
    private const uint ServiceControlStop = 0x00000001;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static void ValidateServiceName(string serviceName)
    {
        if (!serviceName.StartsWith("EdrTestDrv_", StringComparison.Ordinal)
            || serviceName.Length > 80
            || serviceName.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("驱动服务名不在本轮 EdrTestDrv_ 受控命名范围内。", nameof(serviceName));
    }

    public static string ValidateDriverPath(string imagePath, string allowedRoot)
    {
        var fullPath = Path.GetFullPath(imagePath);
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".sys", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("驱动路径不在本轮工作目录的 .sys 允许范围内。", nameof(imagePath));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("驱动工作副本不存在。", fullPath);
        return fullPath;
    }

    public static FileSnapshot SnapshotFile(string imagePath)
    {
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath)) return new FileSnapshot { Exists = false, Path = fullPath };
        var info = new FileInfo(fullPath);
        return new FileSnapshot
        {
            Exists = true,
            Path = fullPath,
            SizeBytes = info.Length,
            ImageSizeBytes = ReadPeImageSize(fullPath),
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            Md5 = Hash(fullPath, MD5.Create()),
            Sha256 = Hash(fullPath, SHA256.Create()),
        };
    }

    public static DriverSnapshot Snapshot(string serviceName, string imagePath)
    {
        ValidateServiceName(serviceName);
        var service = QueryService(serviceName);
        var file = SnapshotFile(imagePath);
        var module = FindLoadedModule(Path.GetFileName(imagePath));
        return new DriverSnapshot
        {
            Loaded = module is not null,
            ServiceExists = service.Exists,
            ServiceName = serviceName,
            ImagePath = Path.GetFullPath(imagePath),
            ServiceState = service.State,
            BaseAddress = module?.BaseAddress,
            SizeBytes = file.SizeBytes,
            ModuleSizeBytes = module is null ? null : file.ImageSizeBytes,
            Md5 = file.Md5,
            Sha256 = file.Sha256,
        };
    }

    public static void Create(string serviceName, string imagePath)
    {
        ValidateServiceName(serviceName);
        imagePath = Path.GetFullPath(imagePath);
        var manager = OpenManager(ScManagerConnect | ScManagerCreateService);
        try
        {
            var service = CreateServiceW(
                manager,
                serviceName,
                serviceName,
                ServiceQueryConfig | ServiceQueryStatus | ServiceStart | ServiceStop | DeleteAccess,
                ServiceKernelDriver,
                ServiceDemandStart,
                ServiceErrorNormal,
                imagePath,
                null,
                IntPtr.Zero,
                null,
                null,
                null);
            if (service == IntPtr.Zero) ThrowLastWin32("CreateServiceW");
            CloseServiceHandle(service);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static void Start(string serviceName)
    {
        WithService(serviceName, ServiceStart | ServiceQueryStatus, service =>
        {
            if (!StartServiceW(service, 0, null))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                    throw new Win32Exception(error, $"StartServiceW 失败：{error}");
            }
        });
    }

    public static void Stop(string serviceName, bool ignoreInactive = false)
    {
        WithService(serviceName, ServiceStop | ServiceQueryStatus, service =>
        {
            if (!ControlService(service, ServiceControlStop, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (!(ignoreInactive && error == ErrorServiceNotActive))
                    throw new Win32Exception(error, $"ControlService(STOP) 失败：{error}");
            }
        }, ignoreMissing: ignoreInactive);
    }

    public static void Delete(string serviceName, bool ignoreMissing = false)
    {
        WithService(serviceName, DeleteAccess | ServiceQueryStatus, service =>
        {
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (!(ignoreMissing && error == ErrorServiceMarkedForDelete))
                    throw new Win32Exception(error, $"DeleteService 失败：{error}");
            }
        }, ignoreMissing);
    }

    public static void WaitForLoaded(string serviceName, string imagePath, bool expected, int timeoutMs = 15_000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (Snapshot(serviceName, imagePath).Loaded != expected)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"驱动加载状态在 {timeoutMs} ms 内未变为 {expected}：{serviceName}");
            Thread.Sleep(25);
        }
    }

    public static void WaitForServiceMissing(string serviceName, int timeoutMs = 10_000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (QueryService(serviceName).Exists)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"驱动服务在 {timeoutMs} ms 内未删除：{serviceName}");
            Thread.Sleep(50);
        }
    }

    public static void AppendMarker(string imagePath, string marker)
    {
        if (!marker.StartsWith("EDRTEST_DRIVER_MODIFY|", StringComparison.Ordinal) || marker.Length > 240)
            throw new ArgumentException("驱动修改标记不符合受控格式。", nameof(marker));
        using var stream = new FileStream(imagePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.ASCII.GetBytes(Environment.NewLine + marker + Environment.NewLine);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static ModuleInfo? FindLoadedModule(string fileName)
    {
        EnableDebugPrivilege();
        var addresses = new IntPtr[2048];
        if (!K32EnumDeviceDrivers(addresses, checked((uint)(addresses.Length * IntPtr.Size)), out var bytesNeeded))
            ThrowLastWin32("K32EnumDeviceDrivers");
        var count = Math.Min(addresses.Length, checked((int)(bytesNeeded / (uint)IntPtr.Size)));
        var name = new StringBuilder(1024);
        var path = new StringBuilder(32768);
        for (var index = 0; index < count; index++)
        {
            if (addresses[index] == IntPtr.Zero) continue;
            name.Clear();
            path.Clear();
            K32GetDeviceDriverBaseNameW(addresses[index], name, name.Capacity);
            K32GetDeviceDriverFileNameW(addresses[index], path, path.Capacity);
            if (string.Equals(name.ToString(), fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path.ToString()), fileName, StringComparison.OrdinalIgnoreCase))
            {
                var address = unchecked((ulong)addresses[index].ToInt64());
                return new ModuleInfo($"0x{address:x16}");
            }
        }
        return null;
    }

    private static void EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid)) return;
            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = 0x00000002 },
            };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static ServiceInfo QueryService(string serviceName)
    {
        var manager = OpenManager(ScManagerConnect);
        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete)
                    return new ServiceInfo(false, null);
                throw new Win32Exception(error, $"OpenServiceW 失败：{error}");
            }
            try
            {
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status,
                    Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    ThrowLastWin32("QueryServiceStatusEx");
                return new ServiceInfo(true, StateName(status.CurrentState));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static void WithService(string serviceName, uint access, Action<IntPtr> action, bool ignoreMissing = false)
    {
        ValidateServiceName(serviceName);
        var manager = OpenManager(ScManagerConnect);
        try
        {
            var service = OpenServiceW(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (ignoreMissing && error is ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete) return;
                throw new Win32Exception(error, $"OpenServiceW 失败：{error}");
            }
            try
            {
                action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static IntPtr OpenManager(uint access)
    {
        var manager = OpenSCManagerW(null, null, access);
        if (manager == IntPtr.Zero) ThrowLastWin32("OpenSCManagerW");
        return manager;
    }

    private static string Hash(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = File.OpenRead(path))
            return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long? ReadPeImageSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null;
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 84) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;
            stream.Position = peOffset + 24;
            var magic = reader.ReadUInt16();
            if (magic is not 0x010B and not 0x020B) return null;
            stream.Position = peOffset + 24 + 56;
            var sizeOfImage = reader.ReadUInt32();
            return sizeOfImage == 0 ? null : sizeOfImage;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "stopped",
        2 => "start_pending",
        3 => "stop_pending",
        4 => "running",
        _ => $"unknown:{state}",
    };

    private static void ThrowLastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} 失败：{error}");
    }

    private sealed record ModuleInfo(string BaseAddress);
    private sealed record ServiceInfo(bool Exists, string? State);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
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
    private static extern bool StartServiceW(IntPtr service, int argumentCount, string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel,
        out ServiceStatusProcess buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumDeviceDrivers([Out] IntPtr[] imageBase, uint bufferSize, out uint bytesNeeded);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint K32GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder baseName, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint K32GetDeviceDriverFileNameW(IntPtr imageBase, StringBuilder fileName, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
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
            if (!values.TryAdd(name, items[++index]))
                throw new ArgumentException($"参数 --{name} 重复。");
        }
    }

    public static ArgumentReader Parse(IEnumerable<string> arguments) => new(arguments);

    public string Require(string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
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
