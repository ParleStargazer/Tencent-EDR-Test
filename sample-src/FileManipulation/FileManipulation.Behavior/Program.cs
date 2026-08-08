using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace FileManipulation;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        string operation = "unknown";
        string path = string.Empty;
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation");
            path = Path.GetFullPath(options.Require("path"));
            resultPath = Path.GetFullPath(options.Require("result"));
            var destination = options.Get("destination") is { } value ? Path.GetFullPath(value) : null;
            var nonce = options.Require("nonce");
            var payloadSize = options.GetInt("payload-size", 8_192, 256, 1_048_576);
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);

            var result = Execute(operation, path, destination, nonce, payloadSize);
            ProtocolJson.WriteAtomic(resultPath, result);
            if (holdMs > 0) Thread.Sleep(holdMs);
            return result.Succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            var error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF;
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = operation,
                    Succeeded = false,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Win32Error = error == 0 ? null : error,
                    Error = exception.Message,
                    Path = path,
                    Before = Snapshot(path),
                    After = Snapshot(path),
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static BehaviorResult Execute(string operation, string path, string? destination, string nonce, int payloadSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = Snapshot(path);
        var destinationBefore = destination is null ? null : Snapshot(destination);
        DateTimeOffset occurred;
        long? bytesRead = null;
        long? bytesWritten = null;
        long? desiredAccess = null;
        long? shareMode = null;
        long? creationDisposition = null;

        switch (operation)
        {
            case "create":
            {
                if (before.Exists) throw new IOException($"创建测试要求文件事先不存在：{path}");
                var payload = Payload(nonce, operation, payloadSize);
                occurred = DateTimeOffset.UtcNow;
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
                bytesWritten = payload.Length;
                creationDisposition = 1; // CREATE_NEW
                break;
            }
            case "open":
            {
                if (!before.Exists) throw new FileNotFoundException("打开测试要求文件事先存在。", path);
                desiredAccess = 0xC0000000L; // GENERIC_READ | GENERIC_WRITE
                shareMode = 1; // FILE_SHARE_READ
                creationDisposition = 3; // OPEN_EXISTING
                occurred = DateTimeOffset.UtcNow;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                bytesRead = bytes.Length;
                stream.Position = 0;
                stream.Write(bytes); // 原样回写，触发可导出的 FileWriteClose/打开文件，同时保持内容哈希不变。
                stream.Flush(flushToDisk: true);
                bytesWritten = bytes.Length;
                break;
            }
            case "modify":
            {
                if (!before.Exists) throw new FileNotFoundException("修改测试要求文件事先存在。", path);
                var payload = Payload(nonce, operation, payloadSize);
                occurred = DateTimeOffset.UtcNow;
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
                bytesWritten = payload.Length;
                creationDisposition = 2; // CREATE_ALWAYS
                break;
            }
            case "delete":
                if (!before.Exists) throw new FileNotFoundException("删除测试要求文件事先存在。", path);
                occurred = DateTimeOffset.UtcNow;
                File.Delete(path);
                break;
            case "rename":
                if (destination is null) throw new ArgumentException("重命名测试缺少 --destination。");
                if (!before.Exists) throw new FileNotFoundException("重命名测试要求源文件事先存在。", path);
                if (destinationBefore!.Exists) throw new IOException($"重命名目标事先已经存在：{destination}");
                occurred = DateTimeOffset.UtcNow;
                File.Move(path, destination);
                break;
            default:
                throw new ArgumentException($"不支持的文件操作：{operation}");
        }

        var afterPath = operation == "rename" ? destination! : path;
        var after = Snapshot(afterPath);
        var sourceAfter = operation == "rename" ? Snapshot(path) : null;
        var succeeded = operation switch
        {
            "create" => !before.Exists && after.Exists && after.SizeBytes == bytesWritten,
            "open" => before.Exists && after.Exists && before.Md5 == after.Md5 && bytesRead > 0 && bytesWritten == bytesRead,
            "modify" => before.Exists && after.Exists && before.Sha256 != after.Sha256 && after.SizeBytes == bytesWritten,
            "delete" => before.Exists && !after.Exists,
            "rename" => before.Exists && !sourceAfter!.Exists && after.Exists && before.Sha256 == after.Sha256,
            _ => false,
        };

        return new BehaviorResult
        {
            Operation = operation,
            Succeeded = succeeded,
            OccurredAtUtc = occurred,
            Win32Error = 0,
            Error = succeeded ? null : "文件操作后的状态或内容未满足预期。",
            Path = afterPath,
            SourcePath = operation == "rename" ? path : null,
            DestinationPath = destination,
            Before = before,
            After = after,
            SourceAfter = sourceAfter,
            DestinationBefore = destinationBefore,
            DesiredAccess = desiredAccess,
            ShareMode = shareMode,
            CreationDisposition = creationDisposition,
            BytesRead = bytesRead,
            BytesWritten = bytesWritten,
            WriteOffset = bytesWritten is null ? null : 0,
        };
    }

    private static byte[] Payload(string nonce, string operation, int size)
    {
        var marker = Encoding.UTF8.GetBytes($"EDRTEST|{nonce}|FILE_{operation.ToUpperInvariant()}|");
        return Enumerable.Range(0, size).Select(index => marker[index % marker.Length]).ToArray();
    }

    private static FileSnapshot Snapshot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new FileSnapshot { Exists = false, Path = fullPath };
        var file = new FileInfo(fullPath);
        using var md5Stream = File.OpenRead(fullPath);
        var md5 = Convert.ToHexString(MD5.HashData(md5Stream)).ToLowerInvariant();
        using var sha1Stream = File.OpenRead(fullPath);
        var sha1 = Convert.ToHexString(SHA1.HashData(sha1Stream)).ToLowerInvariant();
        using var sha256Stream = File.OpenRead(fullPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(sha256Stream)).ToLowerInvariant();
        return new FileSnapshot
        {
            Exists = true,
            Path = fullPath,
            SizeBytes = file.Length,
            CreatedAtUtc = file.CreationTimeUtc,
            ModifiedAtUtc = file.LastWriteTimeUtc,
            Attributes = file.Attributes.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries),
            Md5 = md5,
            Sha1 = sha1,
            Sha256 = sha256,
        };
    }
}
