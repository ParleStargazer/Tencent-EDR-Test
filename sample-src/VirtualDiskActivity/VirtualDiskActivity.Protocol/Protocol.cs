using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace VirtualDiskActivity;

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

public sealed record VirtualDiskPlan(string Method, string FactKey, string Title, string InvocationKind, string ImageFileName);

public static class VirtualDiskPlans
{
    public const string PowerShell = "VDISK_POWERSHELL";
    public const string NativeApi = "VDISK_NATIVE_API";
    public const long VirtualSizeBytes = 16L * 1024 * 1024;
    public static readonly string[] Methods = [PowerShell, NativeApi];

    public static VirtualDiskPlan Create(string method, string nonce)
    {
        ValidateNonce(nonce);
        return method switch
        {
            PowerShell => new(method, "vdisk_powershell", "Mount-DiskImage", "powershell_mount_disk_image", $"edr-test-ps-{nonce}.vhd"),
            NativeApi => new(method, "vdisk_native_api", "OpenVirtualDisk + AttachVirtualDisk", "virtdisk_native_api", $"edr-test-native-{nonce}.vhd"),
            _ => throw new ArgumentException($"不支持的虚拟磁盘子测试：{method}"),
        };
    }

    public static void ValidateNonce(string nonce)
    {
        if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("nonce 必须是 32 位十六进制字符串。");
    }
}

public sealed class ProcessCommandObservation
{
    public required int ProcessId { get; init; }
    public required string Executable { get; init; }
    public required string CommandLine { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset OperationStartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
}

public sealed class VirtualDiskSnapshot
{
    public required string ImagePath { get; init; }
    public required bool ImageExists { get; init; }
    public required bool Attached { get; init; }
    public string? PhysicalPath { get; init; }
    public required uint PhysicalPathError { get; init; }
}

public sealed class VirtualDiskReady
{
    public required string Method { get; init; }
    public required string InvocationKind { get; init; }
    public required int ActorProcessId { get; init; }
    public required string ImagePath { get; init; }
    public required long VirtualSizeBytes { get; init; }
    public required string ImageSha256 { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required VirtualDiskSnapshot Before { get; init; }
    public required VirtualDiskSnapshot After { get; init; }
    public ProcessCommandObservation? InitiatorProcess { get; init; }
}

public sealed class VirtualDiskVerificationGate
{
    public required string Method { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public required string PhysicalPath { get; init; }
}

public sealed class VirtualDiskBehaviorResult
{
    public required string Method { get; init; }
    public required string InvocationKind { get; init; }
    public required int ActorProcessId { get; init; }
    public required string ImagePath { get; init; }
    public required long VirtualSizeBytes { get; init; }
    public required string ImageSha256 { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required bool ControllerGateObserved { get; init; }
    public required bool ActorAttachVerified { get; init; }
    public required bool ActorDetachVerified { get; init; }
    public required bool ReadOnly { get; init; }
    public required bool NoDriveLetter { get; init; }
    public required VirtualDiskSnapshot Before { get; init; }
    public required VirtualDiskSnapshot After { get; init; }
    public required VirtualDiskSnapshot Final { get; init; }
    public required bool Succeeded { get; init; }
    public uint? Win32Error { get; init; }
    public string? Error { get; init; }
    public ProcessCommandObservation? InitiatorProcess { get; init; }
}

public sealed class AttachedVirtualDisk : IDisposable
{
    private SafeFileHandle? handle;

    internal AttachedVirtualDisk(SafeFileHandle handle) => this.handle = handle;

    public VirtualDiskSnapshot Inspect(string imagePath) => VirtualDiskNative.Inspect(handle ?? throw new ObjectDisposedException(nameof(AttachedVirtualDisk)), imagePath);

    public void Detach()
    {
        if (handle is null) return;
        var error = VirtualDiskNative.DetachVirtualDisk(handle, 0, 0);
        if (error != 0) throw new Win32Exception((int)error, $"DetachVirtualDisk 失败：{error}");
    }

    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }
}

public static class VirtualDiskNative
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint VirtualDiskAccessAttachReadOnly = 0x00010000;
    private const uint VirtualDiskAccessDetach = 0x00040000;
    private const uint VirtualDiskAccessGetInfo = 0x00080000;
    private const uint AttachReadOnly = 0x00000001;
    private const uint AttachNoDriveLetter = 0x00000002;
    private const uint OpenFlagNone = 0;
    private const uint CreateFlagNone = 0;
    private static readonly Guid MicrosoftVirtualDiskVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    public static void CreateDynamicVhd(string imagePath, long virtualSizeBytes)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (File.Exists(imagePath)) throw new IOException($"拒绝覆盖已有虚拟磁盘：{imagePath}");
        if (virtualSizeBytes < 3L * 1024 * 1024 || virtualSizeBytes % 512 != 0)
            throw new ArgumentOutOfRangeException(nameof(virtualSizeBytes), "VHD 容量必须至少 3 MiB 且按 512 字节对齐。");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var storageType = new VirtualStorageType { DeviceId = 2, VendorId = MicrosoftVirtualDiskVendor };
        var parameters = new CreateVirtualDiskParameters
        {
            Version = 1,
            Version1 = new CreateVirtualDiskParametersVersion1
            {
                UniqueId = Guid.NewGuid(),
                MaximumSize = checked((ulong)virtualSizeBytes),
                BlockSizeInBytes = 0,
                SectorSizeInBytes = 512,
                ParentPath = null,
                SourcePath = null,
            },
        };
        var error = CreateVirtualDisk(ref storageType, imagePath, 0, IntPtr.Zero, CreateFlagNone, 0, ref parameters, IntPtr.Zero, out var handle);
        if (error != ErrorSuccess)
        {
            handle?.Dispose();
            throw new Win32Exception((int)error, $"CreateVirtualDisk 失败：{error}");
        }
        handle.Dispose();
    }

    public static AttachedVirtualDisk AttachReadOnlyWithoutDriveLetter(string imagePath)
    {
        var handle = Open(imagePath);
        try
        {
            var parameters = new AttachVirtualDiskParameters { Version = 1, Reserved = 0 };
            var error = AttachVirtualDisk(handle, IntPtr.Zero, AttachReadOnly | AttachNoDriveLetter, 0, ref parameters, IntPtr.Zero);
            if (error != ErrorSuccess) throw new Win32Exception((int)error, $"AttachVirtualDisk 失败：{error}");
            return new AttachedVirtualDisk(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static VirtualDiskSnapshot Inspect(string imagePath)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(imagePath)) return new VirtualDiskSnapshot { ImagePath = imagePath, ImageExists = false, Attached = false, PhysicalPathError = 2 };
        using var handle = Open(imagePath);
        return Inspect(handle, imagePath);
    }

    internal static VirtualDiskSnapshot Inspect(SafeFileHandle handle, string imagePath)
    {
        uint sizeBytes = 0;
        var error = GetVirtualDiskPhysicalPath(handle, ref sizeBytes, null);
        if (error != ErrorSuccess && error != ErrorInsufficientBuffer)
        {
            return new VirtualDiskSnapshot
            {
                ImagePath = Path.GetFullPath(imagePath),
                ImageExists = File.Exists(imagePath),
                Attached = false,
                PhysicalPathError = error,
            };
        }

        sizeBytes = Math.Max(sizeBytes, 1024);
        var value = new StringBuilder(checked((int)(sizeBytes / sizeof(char) + 1)));
        error = GetVirtualDiskPhysicalPath(handle, ref sizeBytes, value);
        return new VirtualDiskSnapshot
        {
            ImagePath = Path.GetFullPath(imagePath),
            ImageExists = File.Exists(imagePath),
            Attached = error == ErrorSuccess && value.Length > 0,
            PhysicalPath = error == ErrorSuccess ? value.ToString() : null,
            PhysicalPathError = error,
        };
    }

    public static bool DetachIfAttached(string imagePath)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(imagePath)) return true;
        using var handle = Open(imagePath);
        if (!Inspect(handle, imagePath).Attached) return true;
        var error = DetachVirtualDisk(handle, 0, 0);
        if (error != ErrorSuccess) throw new Win32Exception((int)error, $"DetachVirtualDisk 兜底清理失败：{error}");
        return !Inspect(handle, imagePath).Attached;
    }

    private static SafeFileHandle Open(string imagePath)
    {
        var storageType = new VirtualStorageType { DeviceId = 2, VendorId = MicrosoftVirtualDiskVendor };
        var parameters = new OpenVirtualDiskParameters { Version = 1, RWDepth = 0 };
        var access = VirtualDiskAccessAttachReadOnly | VirtualDiskAccessDetach | VirtualDiskAccessGetInfo;
        var error = OpenVirtualDisk(ref storageType, Path.GetFullPath(imagePath), access, OpenFlagNone, ref parameters, out var handle);
        if (error != ErrorSuccess)
        {
            handle?.Dispose();
            throw new Win32Exception((int)error, $"OpenVirtualDisk 失败：{error}");
        }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateVirtualDiskParameters
    {
        public uint Version;
        public CreateVirtualDiskParametersVersion1 Version1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CreateVirtualDiskParametersVersion1
    {
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ParentPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string? SourcePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenVirtualDiskParameters { public uint Version; public uint RWDepth; }

    // 原生结构的匿名 union 还包含以 ULONGLONG 开头的 Version2，因此 union 在 x86/x64
    // 均按 8 字节边界起始；显式偏移避免把 Version1.Reserved 错放到 offset 4。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct AttachVirtualDiskParameters
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CreateVirtualDisk(ref VirtualStorageType virtualStorageType, string path, uint virtualDiskAccessMask,
        IntPtr securityDescriptor, uint flags, uint providerSpecificFlags, ref CreateVirtualDiskParameters parameters,
        IntPtr overlapped, out SafeFileHandle handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint OpenVirtualDisk(ref VirtualStorageType virtualStorageType, string path, uint virtualDiskAccessMask,
        uint flags, ref OpenVirtualDiskParameters parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern uint AttachVirtualDisk(SafeFileHandle virtualDiskHandle, IntPtr securityDescriptor, uint flags,
        uint providerSpecificFlags, ref AttachVirtualDiskParameters parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    internal static extern uint DetachVirtualDisk(SafeFileHandle virtualDiskHandle, uint flags, uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle virtualDiskHandle, ref uint diskPathSizeInBytes,
        [Out] StringBuilder? diskPath);
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
