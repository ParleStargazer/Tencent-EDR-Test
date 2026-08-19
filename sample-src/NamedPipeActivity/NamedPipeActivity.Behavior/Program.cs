using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NamedPipeActivity;

internal static class Program
{
    private const int BehaviorError = 20;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint PipeTypeByte = 0;
    private const uint PipeReadmodeByte = 0;
    private const uint PipeWait = 0;
    private const int ErrorPipeConnected = 535;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var role = "unknown";
        var pipeName = string.Empty;
        try
        {
            var options = ArgumentReader.Parse(args);
            role = options.Require("role");
            pipeName = ValidatePipeName(options.Require("pipe-name"));
            var nonce = options.Require("nonce");
            if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character))) throw new ArgumentException("nonce 格式无效。");
            resultPath = Path.GetFullPath(options.Require("result"));
            var readyPath = Path.GetFullPath(options.Require("ready"));
            var timeoutMs = options.GetInt("timeout-ms", 15_000, 100, 120_000);
            var holdMs = options.GetInt("hold-ms", 1_000, 0, 30_000);
            var result = role switch
            {
                "server" => RunServer(pipeName, nonce, readyPath),
                "client" => RunClient(pipeName, nonce, readyPath, timeoutMs),
                _ => throw new ArgumentException($"不支持的命名管道角色：{role}"),
            };
            ProtocolJson.WriteAtomic(resultPath, result);
            if (holdMs > 0) Thread.Sleep(holdMs);
            return result.Succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath)) ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Role = role, NativeApi = role == "server" ? "CreateNamedPipeW" : "CreateFileW", PipeName = pipeName,
                ProcessId = Environment.ProcessId, Succeeded = false, NonceVerified = false,
                OccurredAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                BytesWritten = 0, BytesRead = 0,
                Win32Error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF,
                Error = exception.Message,
            });
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static BehaviorResult RunServer(string pipeName, string nonce, string readyPath)
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        using var handle = CreateNamedPipeW(pipeName, PipeAccessDuplex, PipeTypeByte | PipeReadmodeByte | PipeWait,
            1, 4096, 4096, 15_000, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastWin32("CreateNamedPipeW");
        var completedAtUtc = DateTimeOffset.UtcNow;
        ProtocolJson.WriteAtomic(readyPath, new PipeReady { PipeName = pipeName, ServerPid = Environment.ProcessId, CreatedAtUtc = completedAtUtc });
        if (!ConnectNamedPipe(handle, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorPipeConnected) ThrowLastWin32("ConnectNamedPipe");
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        var request = ReadExact(stream, 32);
        var verified = Encoding.ASCII.GetString(request) == nonce;
        var response = Encoding.ASCII.GetBytes("ACK" + nonce);
        stream.Write(response); stream.Flush(flushToDisk: true);
        return new BehaviorResult { Role = "server", NativeApi = "CreateNamedPipeW", PipeName = pipeName, ProcessId = Environment.ProcessId,
            Succeeded = verified, NonceVerified = verified, OccurredAtUtc = occurredAtUtc, CompletedAtUtc = completedAtUtc,
            BytesWritten = response.Length, BytesRead = request.Length, Win32Error = 0, Error = verified ? null : "服务端收到的 nonce 不一致。" };
    }

    private static BehaviorResult RunClient(string pipeName, string nonce, string readyPath, int timeoutMs)
    {
        WaitForReady(readyPath, pipeName, timeoutMs);
        if (!WaitNamedPipeW(pipeName, timeoutMs)) ThrowLastWin32("WaitNamedPipeW");
        var occurredAtUtc = DateTimeOffset.UtcNow;
        using var handle = CreateFileW(pipeName, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastWin32("CreateFileW");
        var completedAtUtc = DateTimeOffset.UtcNow;
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        var request = Encoding.ASCII.GetBytes(nonce);
        stream.Write(request); stream.Flush(flushToDisk: true);
        var response = ReadExact(stream, 35);
        var verified = Encoding.ASCII.GetString(response) == "ACK" + nonce;
        return new BehaviorResult { Role = "client", NativeApi = "CreateFileW", PipeName = pipeName, ProcessId = Environment.ProcessId,
            Succeeded = verified, NonceVerified = verified, OccurredAtUtc = occurredAtUtc, CompletedAtUtc = completedAtUtc,
            BytesWritten = request.Length, BytesRead = response.Length, Win32Error = 0, Error = verified ? null : "客户端收到的 ACK 不一致。" };
    }

    private static void WaitForReady(string path, string pipeName, int timeoutMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path)) { if (watch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待命名管道服务端就绪超时。"); Thread.Sleep(5); }
        var ready = ProtocolJson.Read<PipeReady>(path);
        if (!string.Equals(ready.PipeName, pipeName, StringComparison.Ordinal)) throw new InvalidDataException("服务端就绪协议中的管道名不一致。");
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length]; var offset = 0;
        while (offset < length) { var count = stream.Read(buffer, offset, length - offset); if (count == 0) throw new EndOfStreamException("命名管道对端提前关闭。"); offset += count; }
        return buffer;
    }

    private static string ValidatePipeName(string value)
    {
        if (!value.StartsWith("\\\\.\\pipe\\EdrTest_", StringComparison.Ordinal) || value.Length > 200
            || value[17..].Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("命名管道不在本轮 EdrTest_ 受控命名范围内。");
        return value;
    }
    private static void ThrowLastWin32(string operation) { var error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, $"{operation} 失败：{error}"); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConnectNamedPipe(SafeFileHandle pipe, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, int timeout);
}
