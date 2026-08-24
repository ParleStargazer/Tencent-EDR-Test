using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace UsbDeviceActivity;

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

public static class UsbTestConstants
{
    public const string MountCapabilityId = "win.device.usb.mount";
    public const string UnmountCapabilityId = "win.device.usb.unmount";
    public const string HardwareId = @"ROOT\USB_UDE_TEST";
    public const string VendorId = "ED1D";
    public const string ProductId = "0001";
    public const string Method = "USB_UDE_PNP";
    public static readonly Guid DeviceInterfaceGuid = new("77DC40F2-80FB-4F86-A6D4-793AB56D2D45");
    public static readonly Guid UsbClassGuid = new("36FC9E60-C465-11CF-8056-444553540000");

    public static string CreateSerial(string nonce)
    {
        var compact = new string(nonce.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (compact.Length < 16) throw new ArgumentException("nonce 至少需要 16 个十六进制字符。", nameof(nonce));
        return $"EDR_USB_{compact[..Math.Min(24, compact.Length)]}";
    }

    public static bool IsValidSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || !serial.StartsWith("EDR_USB_", StringComparison.Ordinal))
            return false;
        var suffix = serial["EDR_USB_".Length..];
        return suffix.Length is >= 16 and <= 24
            && suffix.All(value => value is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    public static string ExpectedInstanceId(string serial) => $@"USB\VID_{VendorId}&PID_{ProductId}\{serial}";
}

public sealed class UsbDeviceSnapshot
{
    public required bool Present { get; init; }
    public required string InstanceId { get; init; }
    public required string ClassGuid { get; init; }
    public required string VendorId { get; init; }
    public required string ProductId { get; init; }
    public required string SerialNumber { get; init; }
    public string? Description { get; init; }
    public string? Manufacturer { get; init; }
    public string? Service { get; init; }
    public string? DriverKey { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

public sealed class UsbDriverStatus
{
    public required bool Attached { get; init; }
    public string? SerialNumber { get; init; }
}

public sealed class UsbBehaviorResult
{
    public required string Operation { get; init; }
    public required string Method { get; init; }
    public required int ActorProcessId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string SerialNumber { get; init; }
    public required string ExpectedInstanceId { get; init; }
    public required UsbDriverStatus Before { get; init; }
    public required UsbDriverStatus After { get; init; }
    public required bool IoctlSucceeded { get; init; }
    public required bool Succeeded { get; init; }
    public int? Win32Error { get; init; }
    public string? Error { get; init; }
}

public sealed class UsbDriverPackageLease
{
    public required string SourceInfPath { get; init; }
    public string? PublishedInfPath { get; init; }
    public required string RootInstanceId { get; init; }
    public required bool RebootRequired { get; init; }
    public required UsbDriverInstallDiagnostic Diagnostic { get; init; }
}

public sealed class UsbDriverInstallDiagnostic
{
    public string Stage { get; set; } = "not_started";
    public int? Win32Error { get; set; }
    public bool DriverStorePresent { get; set; }
    public string? PublishedInfPath { get; set; }
    public bool RootDevNodePresent { get; set; }
    public string? RootInstanceId { get; set; }
    public string? BoundService { get; set; }
    public string? BoundDriverKey { get; set; }
    public string? BoundInfName { get; set; }
    public uint? ConfigManagerResult { get; set; }
    public uint? DevNodeStatus { get; set; }
    public uint? DevNodeProblemCode { get; set; }
    public bool DevNodeStarted { get; set; }
    public bool RebootRequired { get; set; }
    public string? DriverInitializationStage { get; set; }
    public uint? DriverInitializationStatus { get; set; }
    public string? DriverInterfaceGuid { get; set; }
    public string ExpectedInterfaceGuid { get; set; } = UsbTestConstants.DeviceInterfaceGuid.ToString("B").ToUpperInvariant();
    public int? InterfaceQueryWin32Error { get; set; }
    public bool InterfacePresent { get; set; }
    public string? InterfacePath { get; set; }

    public string Describe() => string.Join("；", new[]
    {
        $"stage={Stage}",
        $"win32={(Win32Error is null ? "n/a" : Win32Error)}",
        $"driver_store={DriverStorePresent}",
        $"published_inf={PublishedInfPath ?? "n/a"}",
        $"root_devnode={RootDevNodePresent}",
        $"instance={RootInstanceId ?? "n/a"}",
        $"service={BoundService ?? "n/a"}",
        $"bound_inf={BoundInfName ?? "n/a"}",
        $"cm_result={Hex(ConfigManagerResult)}",
        $"devnode_status={Hex(DevNodeStatus)}",
        $"problem={Problem(DevNodeProblemCode)}",
        $"running={DevNodeStarted}",
        $"driver_stage={DriverInitializationStage ?? "n/a"}",
        $"driver_status={Hex(DriverInitializationStatus)}",
        $"driver_guid={DriverInterfaceGuid ?? "n/a"}",
        $"controller_guid={ExpectedInterfaceGuid}",
        $"interface_query_win32={(InterfaceQueryWin32Error is null ? "n/a" : InterfaceQueryWin32Error)}",
        $"interface={InterfacePresent}",
    });

    private static string Hex(uint? value) => value is null ? "n/a" : $"0x{value.Value:X8}";
    private static string Problem(uint? value) => value switch
    {
        null => "n/a",
        0 => "CM_PROB_NONE(0)",
        10 => "CM_PROB_FAILED_START(10)",
        31 => "CM_PROB_FAILED_ADD(31)",
        39 => "CM_PROB_DRIVER_FAILED_LOAD(39)",
        52 => "CM_PROB_UNSIGNED_DRIVER(52)",
        _ => $"CM_PROB_{value.Value}",
    };
}

public sealed class UsbDriverInstallException : Exception
{
    public UsbDriverInstallException(string message, UsbDriverInstallDiagnostic diagnostic, Exception? inner = null)
        : base($"{message}；{diagnostic.Describe()}", inner) => Diagnostic = diagnostic;

    public UsbDriverInstallDiagnostic Diagnostic { get; }
}

public static class UsbUdeClient
{
    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const int SerialCharacters = 64;
    private static readonly uint IoctlAttach = CtlCode(FileDeviceUnknown, 0x800, MethodBuffered, FileWriteData);
    private static readonly uint IoctlDetach = CtlCode(FileDeviceUnknown, 0x801, MethodBuffered, FileWriteData);
    private static readonly uint IoctlQuery = CtlCode(FileDeviceUnknown, 0x802, MethodBuffered, FileReadData);

    public static UsbDriverStatus Query()
    {
        using var handle = Open();
        var output = new byte[4 + SerialCharacters * sizeof(char)];
        if (!DeviceIoControl(handle, IoctlQuery, null, 0, output, output.Length, out var returned, IntPtr.Zero))
            ThrowLastWin32("USB UDE Query IOCTL");
        if (returned < 4) throw new InvalidDataException("USB UDE Query 返回长度不足。", new Win32Exception());
        var serial = Encoding.Unicode.GetString(output, 4, Math.Max(0, Math.Min(returned - 4, SerialCharacters * 2))).TrimEnd('\0');
        return new UsbDriverStatus { Attached = BitConverter.ToUInt32(output, 0) != 0, SerialNumber = string.IsNullOrEmpty(serial) ? null : serial };
    }

    public static void Attach(string serial)
    {
        ValidateSerial(serial);
        using var handle = Open();
        var input = new byte[SerialCharacters * sizeof(char)];
        Encoding.Unicode.GetBytes(serial + "\0").CopyTo(input, 0);
        if (!DeviceIoControl(handle, IoctlAttach, input, input.Length, null, 0, out _, IntPtr.Zero))
            ThrowLastWin32("USB UDE Attach IOCTL");
    }

    public static void Detach(bool ignoreMissing = false)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open();
        }
        catch (FileNotFoundException) when (ignoreMissing)
        {
            return;
        }
        catch (Win32Exception exception) when (ignoreMissing && exception.NativeErrorCode is 2 or 1167)
        {
            return;
        }
        using (handle)
        {
            if (DeviceIoControl(handle, IoctlDetach, null, 0, null, 0, out _, IntPtr.Zero)) return;
            var error = Marshal.GetLastWin32Error();
            if (ignoreMissing && error is 1167 or 2) return;
            throw new Win32Exception(error, $"USB UDE Detach IOCTL 失败：{error}");
        }
    }

    public static string WaitForInterface(int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var path = TryGetInterfacePath();
            if (!string.IsNullOrWhiteSpace(path)) return path;
            Thread.Sleep(50);
        }
        throw new TimeoutException("等待 UsbUdeTest 设备接口超时。");
    }

    public static string? TryGetInterfacePath() => TryGetInterfacePath(out _);

    public static string? TryGetInterfacePath(out int? queryError)
    {
        queryError = null;
        var interfaceGuid = UsbTestConstants.DeviceInterfaceGuid;
        var set = SetupDiGetClassDevsW(ref interfaceGuid, null, IntPtr.Zero, SetupDiGetClassDevsFlags.Present | SetupDiGetClassDevsFlags.DeviceInterface);
        if (set == InvalidHandleValue)
        {
            queryError = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            var data = new SpDeviceInterfaceData { Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, 0, ref data))
            {
                queryError = Marshal.GetLastWin32Error();
                return null;
            }
            _ = SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required == 0)
            {
                queryError = Marshal.GetLastWin32Error();
                return null;
            }
            var detail = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                {
                    queryError = Marshal.GetLastWin32Error();
                    return null;
                }
                return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static SafeFileHandle Open()
    {
        var path = TryGetInterfacePath() ?? throw new FileNotFoundException("未找到 UsbUdeTest 设备接口。请确认驱动包已安装并启动。", UsbTestConstants.HardwareId);
        var handle = CreateFileW(path, FileReadData | FileWriteData, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastWin32("CreateFile(UsbUdeTest)");
        return handle;
    }

    private static void ValidateSerial(string serial)
    {
        if (!serial.StartsWith("EDR_USB_", StringComparison.Ordinal) || serial.Length >= SerialCharacters
            || serial.Any(value => !(value is >= 'A' and <= 'Z') && !char.IsDigit(value) && value is not '_' and not '-'))
            throw new ArgumentException("USB 测试序列号不在 EDR_USB_ 受控范围内。", nameof(serial));
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static void ThrowLastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} 失败：{error}");
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [Flags]
    private enum SetupDiGetClassDevsFlags : uint { Present = 0x2, DeviceInterface = 0x10 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public uint Size; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, SetupDiGetClassDevsFlags flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[]? inputBuffer, int inputBufferSize,
        byte[]? outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);
}

public static class UsbDeviceDiscovery
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpService = 0x00000004;
    private const uint SpdrpDriver = 0x00000009;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static UsbDeviceSnapshot Snapshot(string serial)
    {
        var expected = UsbTestConstants.ExpectedInstanceId(serial);
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == InvalidHandleValue) ThrowLastWin32("SetupDiGetClassDevs(USB)");
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = CreateDeviceInfoData();
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new Win32Exception(error, $"SetupDiEnumDeviceInfo 失败：{error}");
                }
                var instanceId = GetInstanceId(set, ref data);
                if (!string.Equals(instanceId, expected, StringComparison.OrdinalIgnoreCase)) continue;
                return new UsbDeviceSnapshot
                {
                    Present = true,
                    InstanceId = instanceId,
                    ClassGuid = data.ClassGuid.ToString("B").ToUpperInvariant(),
                    VendorId = UsbTestConstants.VendorId,
                    ProductId = UsbTestConstants.ProductId,
                    SerialNumber = serial,
                    Description = GetStringProperty(set, ref data, SpdrpDeviceDesc),
                    Manufacturer = GetStringProperty(set, ref data, SpdrpMfg),
                    Service = GetStringProperty(set, ref data, SpdrpService),
                    DriverKey = GetStringProperty(set, ref data, SpdrpDriver),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return Missing(serial);
    }

    public static UsbDeviceSnapshot WaitFor(string serial, bool present, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        UsbDeviceSnapshot snapshot;
        do
        {
            snapshot = Snapshot(serial);
            if (snapshot.Present == present) return snapshot;
            Thread.Sleep(50);
        } while (stopwatch.ElapsedMilliseconds < timeoutMs);
        return snapshot;
    }

    public static UsbDeviceSnapshot Missing(string serial) => new()
    {
        Present = false,
        InstanceId = UsbTestConstants.ExpectedInstanceId(serial),
        ClassGuid = UsbTestConstants.UsbClassGuid.ToString("B").ToUpperInvariant(),
        VendorId = UsbTestConstants.VendorId,
        ProductId = UsbTestConstants.ProductId,
        SerialNumber = serial,
        ObservedAtUtc = DateTimeOffset.UtcNow,
    };

    internal static SpDevinfoData CreateDeviceInfoData() => new() { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };

    internal static string GetInstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var builder = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceIdW(set, ref data, builder, builder.Capacity, out _)) ThrowLastWin32("SetupDiGetDeviceInstanceId");
        return builder.ToString();
    }

    internal static string? GetStringProperty(IntPtr set, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out _)) return null;
        return Encoding.Unicode.GetString(buffer).Split('\0', 2)[0];
    }

    internal static void ThrowLastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} 失败：{error}");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

public static class UsbDriverInstaller
{
    private const uint SpCopyNewerOrSame = 0x00000004;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpService = 0x00000004;
    private const uint SpdrpDriver = 0x00000009;
    private const uint DicdGenerateId = 0x00000001;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint DifRemove = 0x00000005;
    private const uint InstallFlagForce = 0x00000001;
    private const uint InstallFlagNonInteractive = 0x00000004;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint CrSuccess = 0x00000000;
    private const uint DnStarted = 0x00000008;
    private const int ErrorFileExists = 80;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static UsbDriverPackageLease Install(string infPath)
    {
        infPath = Path.GetFullPath(infPath);
        if (!File.Exists(infPath)) throw new FileNotFoundException("USB UDE INF 不存在。", infPath);
        var diagnostic = new UsbDriverInstallDiagnostic { Stage = "driver_store" };
        string? publishedPath = null;
        try
        {
            var published = new StringBuilder(512);
            var copied = SetupCopyOEMInfW(infPath, null, 1, SpCopyNewerOrSame, published,
                published.Capacity, out _, IntPtr.Zero);
            var copyError = copied ? 0 : Marshal.GetLastWin32Error();
            if (!copied && copyError != ErrorFileExists)
            {
                diagnostic.Win32Error = copyError;
                throw Failure("Driver Store 导入失败", diagnostic, new Win32Exception(copyError));
            }
            publishedPath = ResolvePublishedInfPath(infPath, published.ToString());
            diagnostic.PublishedInfPath = publishedPath;
            diagnostic.DriverStorePresent = !string.IsNullOrWhiteSpace(publishedPath)
                && File.Exists(publishedPath);
            if (!diagnostic.DriverStorePresent)
                throw Failure("SetupCopyOEMInf 返回后未能在 Driver Store 确认对应 OEM INF", diagnostic);

            diagnostic.Stage = "root_devnode";
            var root = FindRootDevNode();
            var rootId = root?.InstanceId ?? CreateRootDevice();
            root = FindRootDevNode(rootId);
            ApplyRootSnapshot(diagnostic, root);
            if (root is null)
                throw Failure("DIF_REGISTERDEVICE 返回后未能确认 ROOT\\USB_UDE_TEST devnode", diagnostic);

            ClearDriverInitializationDiagnostic();
            diagnostic.Stage = "driver_binding_start";
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, UsbTestConstants.HardwareId, infPath,
                    InstallFlagForce | InstallFlagNonInteractive, out var rebootRequired))
            {
                var error = Marshal.GetLastWin32Error();
                diagnostic.Win32Error = error;
                RefreshDiagnostic(diagnostic, rootId);
                throw Failure("UpdateDriverForPlugAndPlayDevices 安装或启动驱动失败", diagnostic,
                    new Win32Exception(error));
            }
            diagnostic.RebootRequired = rebootRequired;
            RefreshDiagnostic(diagnostic, rootId);
            if (!string.Equals(diagnostic.BoundService, "UsbUdeTest", StringComparison.OrdinalIgnoreCase))
                throw Failure("ROOT devnode 未绑定 UsbUdeTest 服务", diagnostic);
            if (!string.Equals(diagnostic.BoundInfName, Path.GetFileName(publishedPath), StringComparison.OrdinalIgnoreCase))
                throw Failure("ROOT devnode 绑定的 OEM INF 与本轮 Driver Store 包不一致", diagnostic);
            if (!diagnostic.DevNodeStarted)
                throw Failure("UsbUdeTest devnode 未进入 Running，停止等待设备接口", diagnostic);
            ValidateInterfaceGuid(diagnostic);

            diagnostic.Stage = "device_interface";
            var interfaceWait = System.Diagnostics.Stopwatch.StartNew();
            while (interfaceWait.ElapsedMilliseconds < 15_000)
            {
                RefreshDiagnostic(diagnostic, rootId);
                if (!diagnostic.RootDevNodePresent || !diagnostic.DevNodeStarted
                    || diagnostic.DevNodeProblemCode is > 0)
                    throw Failure("等待接口期间 UsbUdeTest devnode 停止或出现 PnP Problem", diagnostic);
                ValidateInterfaceGuid(diagnostic);
                var interfacePath = UsbUdeClient.TryGetInterfacePath(out var interfaceQueryError);
                diagnostic.InterfaceQueryWin32Error = interfaceQueryError;
                if (!string.IsNullOrWhiteSpace(interfacePath))
                {
                    diagnostic.InterfacePresent = true;
                    diagnostic.InterfacePath = interfacePath;
                    diagnostic.Stage = "ready";
                    break;
                }
                Thread.Sleep(50);
            }
            if (!diagnostic.InterfacePresent)
                throw Failure("devnode 已 Running，但指定 Device Interface GUID 未出现", diagnostic);
            return new UsbDriverPackageLease
            {
                SourceInfPath = infPath,
                PublishedInfPath = publishedPath,
                RootInstanceId = rootId,
                RebootRequired = rebootRequired,
                Diagnostic = diagnostic,
            };
        }
        catch (Exception exception)
        {
            try { RefreshDiagnostic(diagnostic, diagnostic.RootInstanceId); } catch { }
            try { RemoveRootDevices(); } catch { }
            if (!string.IsNullOrWhiteSpace(publishedPath))
            {
                try { UninstallPublishedInf(publishedPath); } catch { }
            }
            if (exception is UsbDriverInstallException) throw;
            throw Failure("USB UDE 驱动安装发生未分类错误", diagnostic, exception);
        }
    }

    public static void Uninstall(UsbDriverPackageLease? lease)
    {
        var errors = new List<Exception>();
        try { UsbUdeClient.Detach(ignoreMissing: true); }
        catch (Exception exception) { errors.Add(exception); }
        try { RemoveRootDevices(); }
        catch (Exception exception) { errors.Add(exception); }
        if (!string.IsNullOrWhiteSpace(lease?.PublishedInfPath))
        {
            try { UninstallPublishedInf(lease.PublishedInfPath); }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count > 0)
            throw new AggregateException("USB UDE 清理未全部成功。", errors);
    }

    public static bool IsRootDevicePresent() => FindRootInstanceId() is not null;

    private static string CreateRootDevice()
    {
        var classGuid = UsbTestConstants.UsbClassGuid;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandleValue) UsbDeviceDiscovery.ThrowLastWin32("SetupDiCreateDeviceInfoList");
        try
        {
            var data = UsbDeviceDiscovery.CreateDeviceInfoData();
            if (!SetupDiCreateDeviceInfoW(set, "USB_UDE_TEST", ref classGuid,
                    "Tencent EDR Test USB Device Emulation Controller", IntPtr.Zero, DicdGenerateId, ref data))
                UsbDeviceDiscovery.ThrowLastWin32("SetupDiCreateDeviceInfo");
            var hardwareIds = Encoding.Unicode.GetBytes(UsbTestConstants.HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, hardwareIds, (uint)hardwareIds.Length))
                UsbDeviceDiscovery.ThrowLastWin32("SetupDiSetDeviceRegistryProperty(HardwareId)");
            if (!SetupDiCallClassInstaller(DifRegisterDevice, set, ref data))
                UsbDeviceDiscovery.ThrowLastWin32("DIF_REGISTERDEVICE");
            return UsbDeviceDiscovery.GetInstanceId(set, ref data);
        }
        finally { UsbDeviceDiscovery.SetupDiDestroyDeviceInfoList(set); }
    }

    private static string? FindRootInstanceId() => FindRootDevNode()?.InstanceId;

    private static RootDevNodeSnapshot? FindRootDevNode(string? requiredInstanceId = null)
    {
        var set = UsbDeviceDiscovery.SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DigcfAllClasses);
        if (set == InvalidHandleValue) return null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = UsbDeviceDiscovery.CreateDeviceInfoData();
                if (!UsbDeviceDiscovery.SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) return null;
                    UsbDeviceDiscovery.ThrowLastWin32("SetupDiEnumDeviceInfo(ROOT)");
                }
                var ids = GetMultiStringProperty(set, ref data, SpdrpHardwareId);
                if (!ids.Any(value => string.Equals(value, UsbTestConstants.HardwareId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var instanceId = UsbDeviceDiscovery.GetInstanceId(set, ref data);
                if (!string.IsNullOrWhiteSpace(requiredInstanceId)
                    && !string.Equals(instanceId, requiredInstanceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var service = UsbDeviceDiscovery.GetStringProperty(set, ref data, SpdrpService);
                var driverKey = UsbDeviceDiscovery.GetStringProperty(set, ref data, SpdrpDriver);
                var cmResult = CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0);
                return new RootDevNodeSnapshot(instanceId, service, driverKey, ReadBoundInfName(driverKey),
                    cmResult, status, problem);
            }
        }
        finally { UsbDeviceDiscovery.SetupDiDestroyDeviceInfoList(set); }
    }

    private static void RemoveRootDevices()
    {
        var set = UsbDeviceDiscovery.SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DigcfAllClasses);
        if (set == InvalidHandleValue) return;
        try
        {
            for (uint index = 0; ; )
            {
                var data = UsbDeviceDiscovery.CreateDeviceInfoData();
                if (!UsbDeviceDiscovery.SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    UsbDeviceDiscovery.ThrowLastWin32("SetupDiEnumDeviceInfo(ROOT cleanup)");
                }
                var ids = GetMultiStringProperty(set, ref data, SpdrpHardwareId);
                if (!ids.Any(value => string.Equals(value, UsbTestConstants.HardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    index++;
                    continue;
                }
                if (!SetupDiCallClassInstaller(DifRemove, set, ref data))
                    UsbDeviceDiscovery.ThrowLastWin32("DIF_REMOVE");
            }
        }
        finally { UsbDeviceDiscovery.SetupDiDestroyDeviceInfoList(set); }
    }

    private static void UninstallPublishedInf(string publishedInfPath)
    {
        var publishedName = Path.GetFileName(publishedInfPath);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (SetupUninstallOEMInfW(publishedName, 1, IntPtr.Zero)) return;
            if (attempt == 19) UsbDeviceDiscovery.ThrowLastWin32("SetupUninstallOEMInf");
            Thread.Sleep(100);
        }
    }

    private static IReadOnlyList<string> GetMultiStringProperty(IntPtr set, ref UsbDeviceDiscovery.SpDevinfoData data, uint property)
    {
        var buffer = new byte[4096];
        if (!UsbDeviceDiscovery.SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out _)) return [];
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ResolvePublishedInfPath(string sourceInfPath, string destination)
    {
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var candidate = Path.IsPathRooted(destination)
                ? destination
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", destination);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var sourceHash = Hashing.Sha256(sourceInfPath);
        try
        {
            foreach (var path in Directory.EnumerateFiles(infDirectory, "oem*.inf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (string.Equals(Hashing.Sha256(path), sourceHash, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    private static string? ReadBoundInfName(string? driverKey)
    {
        if (string.IsNullOrWhiteSpace(driverKey)) return null;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
        return key?.GetValue("InfPath") as string;
    }

    private static void ClearDriverInitializationDiagnostic()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\UsbUdeTest\Parameters", writable: true);
        if (key is null) return;
        foreach (var name in new[] { "InitializationStage", "InitializationStatus", "InterfaceGuid" })
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void RefreshDiagnostic(UsbDriverInstallDiagnostic diagnostic, string? rootInstanceId)
    {
        var root = FindRootDevNode(rootInstanceId);
        ApplyRootSnapshot(diagnostic, root);
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\UsbUdeTest\Parameters");
        diagnostic.DriverInitializationStage = key?.GetValue("InitializationStage") as string;
        diagnostic.DriverInitializationStatus = ToUInt32(key?.GetValue("InitializationStatus"));
        diagnostic.DriverInterfaceGuid = key?.GetValue("InterfaceGuid") as string;
    }

    private static void ApplyRootSnapshot(UsbDriverInstallDiagnostic diagnostic, RootDevNodeSnapshot? root)
    {
        diagnostic.RootDevNodePresent = root is not null;
        if (root is null)
        {
            diagnostic.DevNodeStarted = false;
            return;
        }
        diagnostic.RootInstanceId = root.InstanceId;
        diagnostic.BoundService = root.Service;
        diagnostic.BoundDriverKey = root.DriverKey;
        diagnostic.BoundInfName = root.InfName;
        diagnostic.ConfigManagerResult = root.ConfigManagerResult;
        diagnostic.DevNodeStatus = root.Status;
        diagnostic.DevNodeProblemCode = root.ProblemCode;
        diagnostic.DevNodeStarted = root.ConfigManagerResult == CrSuccess
            && (root.Status & DnStarted) == DnStarted && root.ProblemCode == 0;
    }

    private static void ValidateInterfaceGuid(UsbDriverInstallDiagnostic diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.DriverInterfaceGuid)
            && !string.Equals(diagnostic.DriverInterfaceGuid, diagnostic.ExpectedInterfaceGuid,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic.Stage = "interface_guid";
            throw Failure("驱动与 Controller 使用的 Device Interface GUID 不一致", diagnostic);
        }
    }

    private static UsbDriverInstallException Failure(string message, UsbDriverInstallDiagnostic diagnostic,
        Exception? inner = null) => new(message, diagnostic, inner);

    private static uint? ToUInt32(object? value) => value switch
    {
        int signed => unchecked((uint)signed),
        long signed => unchecked((uint)signed),
        uint unsigned => unsigned,
        _ => null,
    };

    private sealed record RootDevNodeSnapshot(string InstanceId, string? Service, string? DriverKey,
        string? InfName, uint ConfigManagerResult, uint Status, uint ProblemCode);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupCopyOEMInfW(string sourceInfFileName, string? oemSourceMediaLocation, uint oemSourceMediaType,
        uint copyStyle, StringBuilder destinationInfFileName, int destinationInfFileNameSize, out int requiredSize, IntPtr destinationInfFileNameComponent);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags, ref UsbDeviceDiscovery.SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref UsbDeviceDiscovery.SpDevinfoData deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref UsbDeviceDiscovery.SpDevinfoData deviceInfoData);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupUninstallOEMInfW(string infFileName, uint flags, IntPtr reserved);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber,
        uint devInst, uint flags);
}

public static class Hashing
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed class ArgumentReader
{
    private readonly Dictionary<string, string> values;
    private ArgumentReader(Dictionary<string, string> values) => this.values = values;

    public static ArgumentReader Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"无法识别的参数：{args[index]}");
            values[args[index][2..]] = args[index + 1];
        }
        return new ArgumentReader(values);
    }

    public string Require(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少参数 --{key}");
}
