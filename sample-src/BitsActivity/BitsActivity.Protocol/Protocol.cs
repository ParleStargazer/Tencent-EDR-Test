using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace BitsActivity;

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

public sealed record BitsPlan(string Method, string Title, string InvocationKind, string DisplayName, string LocalFileName);

public static class BitsPlans
{
    public static readonly string[] Methods = ["bitsadmin", "com_api"];

    public static BitsPlan Create(string method, string nonce)
    {
        ValidateNonce(nonce);
        return method switch
        {
            "bitsadmin" => new(method, "bitsadmin.exe 命令行", "bitsadmin_exe",
                $"EDRTEST_BITSADMIN_{nonce}", $"bitsadmin-{nonce}.json"),
            "com_api" => new(method, "BITS COM API", "background_copy_manager_com",
                $"EDRTEST_BITSCOM_{nonce}", $"bits-com-{nonce}.json"),
            _ => throw new ArgumentException($"不支持的 BITS 子测试：{method}"),
        };
    }

    public static byte[] CreatePayload(string method, string nonce) => Encoding.UTF8.GetBytes(
        $"{{\"schema\":\"edr-test-bits-v1\",\"method\":\"{method}\",\"nonce\":\"{nonce}\"}}\n");

    public static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

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
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
}

public sealed class BitsJobReady
{
    public required string Method { get; init; }
    public required string InvocationKind { get; init; }
    public required int ActorProcessId { get; init; }
    public required Guid JobId { get; init; }
    public required string DisplayName { get; init; }
    public required string JobType { get; init; }
    public required string State { get; init; }
    public required string OwnerSid { get; init; }
    public required string RemoteUrl { get; init; }
    public required string LocalPath { get; init; }
    public required long PayloadSize { get; init; }
    public required string PayloadSha256 { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public ProcessCommandObservation? InitiatorProcess { get; init; }
}

public sealed class BitsExecutionGate
{
    public required string Method { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class BitsBehaviorResult
{
    public required string Method { get; init; }
    public required string InvocationKind { get; init; }
    public required int ActorProcessId { get; init; }
    public required Guid JobId { get; init; }
    public required string DisplayName { get; init; }
    public required string JobType { get; init; }
    public required string OwnerSid { get; init; }
    public required string RemoteUrl { get; init; }
    public required string LocalPath { get; init; }
    public required string StateBeforeResume { get; init; }
    public required string StateBeforeComplete { get; init; }
    public required string StateAfterComplete { get; init; }
    public required long BytesTotal { get; init; }
    public required long BytesTransferred { get; init; }
    public required long PayloadSize { get; init; }
    public required string PayloadSha256 { get; init; }
    public string? DownloadedSha256 { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ResumedAtUtc { get; init; }
    public required DateTimeOffset TransferredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required bool ControllerGateObserved { get; init; }
    public required bool DownloadVerified { get; init; }
    public required bool JobRemovedAfterComplete { get; init; }
    public required int HttpRequestCount { get; init; }
    public required bool Succeeded { get; init; }
    public int? HResult { get; init; }
    public string? Error { get; init; }
    public ProcessCommandObservation? InitiatorProcess { get; init; }
}

public sealed class BitsJobSnapshot
{
    public required Guid JobId { get; init; }
    public required string DisplayName { get; init; }
    public required string JobType { get; init; }
    public required string State { get; init; }
    public required string OwnerSid { get; init; }
    public required string RemoteUrl { get; init; }
    public required string LocalPath { get; init; }
    public required long BytesTotal { get; init; }
    public required long BytesTransferred { get; init; }
}

public static class BitsCom
{
    public static IBackgroundCopyManager CreateManager()
    {
        var type = Type.GetTypeFromCLSID(new Guid("4991D34B-80A1-4291-83B6-3328366B9097"), throwOnError: true)
            ?? throw new COMException("无法解析 BackgroundCopyManager COM 类型。");
        return (IBackgroundCopyManager)(Activator.CreateInstance(type)
            ?? throw new COMException("无法创建 BackgroundCopyManager COM 实例。"));
    }

    public static BitsJobSnapshot Inspect(Guid jobId)
    {
        var manager = CreateManager();
        IBackgroundCopyJob? job = null;
        try
        {
            manager.GetJob(ref jobId, out job);
            job.GetId(out var actualId);
            job.GetType(out var type);
            job.GetState(out var state);
            job.GetProgress(out var progress);
            job.GetOwner(out var owner);
            job.GetDisplayName(out var displayName);
            job.EnumFiles(out var files);
            try
            {
                files.Next(1, out var file, out var fetched);
                if (fetched != 1 || file is null) throw new InvalidDataException("BITS 任务没有可枚举的传输文件。");
                try
                {
                    file.GetRemoteName(out var remoteUrl);
                    file.GetLocalName(out var localPath);
                    return new BitsJobSnapshot
                    {
                        JobId = actualId,
                        DisplayName = displayName,
                        JobType = type.ToString(),
                        State = state.ToString(),
                        OwnerSid = owner,
                        RemoteUrl = remoteUrl,
                        LocalPath = localPath,
                        BytesTotal = ConvertSize(progress.BytesTotal),
                        BytesTransferred = ConvertSize(progress.BytesTransferred),
                    };
                }
                finally { Marshal.FinalReleaseComObject(file); }
            }
            finally { Marshal.FinalReleaseComObject(files); }
        }
        finally
        {
            if (job is not null) Marshal.FinalReleaseComObject(job);
            Marshal.FinalReleaseComObject(manager);
        }
    }

    public static bool Exists(Guid jobId)
    {
        try { _ = Inspect(jobId); return true; }
        catch (COMException exception) when ((uint)exception.HResult is 0x80200001 or 0x80200003) { return false; }
    }

    public static bool CancelIfExists(Guid jobId)
    {
        var manager = CreateManager();
        IBackgroundCopyJob? job = null;
        try
        {
            try { manager.GetJob(ref jobId, out job); }
            catch (COMException exception) when ((uint)exception.HResult is 0x80200001 or 0x80200003) { return true; }
            job.Cancel();
            return !Exists(jobId);
        }
        finally
        {
            if (job is not null) Marshal.FinalReleaseComObject(job);
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static long ConvertSize(ulong value) => value == ulong.MaxValue ? 0 : checked((long)value);
}

public enum BG_JOB_TYPE { DOWNLOAD = 0, UPLOAD = 1, UPLOAD_REPLY = 2 }
public enum BG_JOB_STATE { QUEUED = 0, CONNECTING = 1, TRANSFERRING = 2, SUSPENDED = 3, ERROR = 4, TRANSIENT_ERROR = 5, TRANSFERRED = 6, ACKNOWLEDGED = 7, CANCELLED = 8 }

[StructLayout(LayoutKind.Sequential)]
public struct BG_JOB_PROGRESS
{
    public ulong BytesTotal;
    public ulong BytesTransferred;
    public uint FilesTotal;
    public uint FilesTransferred;
}

[ComImport, Guid("5CE34C0D-0DC9-4C1F-897C-DAA1B78CEE7C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IBackgroundCopyManager
{
    void CreateJob([MarshalAs(UnmanagedType.LPWStr)] string displayName, BG_JOB_TYPE type, out Guid jobId, out IBackgroundCopyJob job);
    void GetJob(ref Guid jobId, out IBackgroundCopyJob job);
    void EnumJobs(uint flags, [MarshalAs(UnmanagedType.Interface)] out object jobs);
    void GetErrorDescription(int hresult, uint languageId, [MarshalAs(UnmanagedType.LPWStr)] out string description);
}

[ComImport, Guid("37668D37-507E-4160-9316-26306D150B12"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IBackgroundCopyJob
{
    void AddFileSet(uint fileCount, IntPtr fileSet);
    void AddFile([MarshalAs(UnmanagedType.LPWStr)] string remoteUrl, [MarshalAs(UnmanagedType.LPWStr)] string localName);
    void EnumFiles(out IEnumBackgroundCopyFiles files);
    void Suspend();
    void Resume();
    void Cancel();
    void Complete();
    void GetId(out Guid id);
    void GetType(out BG_JOB_TYPE type);
    void GetProgress(out BG_JOB_PROGRESS progress);
    void GetTimes(IntPtr times);
    void GetState(out BG_JOB_STATE state);
    void GetError([MarshalAs(UnmanagedType.Interface)] out object error);
    void GetOwner([MarshalAs(UnmanagedType.LPWStr)] out string owner);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
}

[ComImport, Guid("CA51E165-C365-424C-8D41-24AAA4FF3C40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumBackgroundCopyFiles
{
    void Next(uint count, out IBackgroundCopyFile file, out uint fetched);
    void Skip(uint count);
    void Reset();
    void Clone(out IEnumBackgroundCopyFiles files);
    void GetCount(out uint count);
}

[ComImport, Guid("01B7BD23-FB88-4A77-8490-5891D3E4653A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IBackgroundCopyFile
{
    void GetRemoteName([MarshalAs(UnmanagedType.LPWStr)] out string remoteName);
    void GetLocalName([MarshalAs(UnmanagedType.LPWStr)] out string localName);
    void GetProgress(IntPtr progress);
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
