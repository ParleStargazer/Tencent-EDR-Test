using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HashAlgorithms;

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
            var nonce = options.Require("nonce");
            var payloadSize = options.GetInt("payload-size", 8_192, 256, 1_048_576);
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            var result = Execute(operation, path, nonce, payloadSize);
            ProtocolJson.WriteAtomic(resultPath, result);
            if (holdMs > 0) Thread.Sleep(holdMs);
            return result.Succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = operation,
                    Succeeded = false,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Path = path,
                    Algorithm = Algorithm(operation),
                    Before = Snapshot(path),
                    After = Snapshot(path),
                    Win32Error = exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static BehaviorResult Execute(string operation, string path, string nonce, int payloadSize)
    {
        if (operation is not ("md5" or "sha" or "imphash")) throw new ArgumentException($"不支持的哈希能力：{operation}");
        var expectedExtension = operation == "imphash" ? ".exe" : ".json";
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{operation} 测试文件必须使用 {expectedExtension} 后缀。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = Snapshot(path);
        if (before.Exists) throw new IOException($"哈希测试要求文件事先不存在：{path}");

        string? sourcePePath = null;
        long bytesWritten;
        var occurredAt = DateTimeOffset.UtcNow;
        if (operation == "imphash")
        {
            sourcePePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取 Actor PE 路径。");
            using var source = new FileStream(sourcePePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
            bytesWritten = destination.Length;
        }
        else
        {
            var payload = JsonPayload(nonce, operation, payloadSize);
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            destination.Write(payload);
            destination.Flush(flushToDisk: true);
            bytesWritten = payload.Length;
        }

        var after = Snapshot(path);
        var algorithm = Algorithm(operation);
        var digest = operation switch
        {
            "md5" => after.Md5,
            "sha" => after.Sha256,
            "imphash" => after.ImpHash,
            _ => null,
        };
        var contentValid = operation == "imphash"
            ? after.IsPortableExecutable && after.ImportCount > 0 && after.ImpHash?.Length == 32
            : IsJson(path);
        var succeeded = !before.Exists && after.Exists && after.SizeBytes == bytesWritten && contentValid
            && digest?.Length == (operation == "sha" ? 64 : 32);
        return new BehaviorResult
        {
            Operation = operation,
            Succeeded = succeeded,
            OccurredAtUtc = occurredAt,
            Path = path,
            Algorithm = algorithm,
            Digest = digest,
            BytesWritten = bytesWritten,
            SourcePortableExecutablePath = sourcePePath,
            Before = before,
            After = after,
            Win32Error = 0,
            Error = succeeded ? null : after.ImpHashError ?? "创建后的文件状态或摘要未满足预期。",
        };
    }

    private static string Algorithm(string operation) => operation switch
    {
        "md5" => "md5",
        "sha" => "sha256",
        "imphash" => "imphash",
        _ => "unknown",
    };

    private static byte[] JsonPayload(string nonce, string operation, int size)
    {
        var prefix = Encoding.UTF8.GetBytes($"{{\"schema_version\":\"1.0\",\"nonce\":\"{nonce}\",\"operation\":\"{operation}\",\"payload\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}");
        if (prefix.Length + suffix.Length > size) throw new ArgumentOutOfRangeException(nameof(size), "JSON 载荷空间不足。");
        return prefix.Concat(Enumerable.Repeat((byte)'H', size - prefix.Length - suffix.Length)).Concat(suffix).ToArray();
    }

    private static bool IsJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static HashSnapshot Snapshot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new HashSnapshot { Exists = false, Path = fullPath };
        var file = new FileInfo(fullPath);
        var isPe = ImportHashCalculator.TryCompute(fullPath, out var impHash, out var impHashError);
        return new HashSnapshot
        {
            Exists = true,
            Path = fullPath,
            SizeBytes = file.Length,
            Md5 = FileHash(fullPath, MD5.HashData),
            Sha1 = FileHash(fullPath, SHA1.HashData),
            Sha256 = FileHash(fullPath, SHA256.HashData),
            Sha512 = FileHash(fullPath, SHA512.HashData),
            ImpHash = impHash?.Digest,
            IsPortableExecutable = isPe,
            ImportCount = impHash?.ImportCount,
            ImpHashError = isPe ? null : impHashError,
        };
    }

    private static string FileHash(string path, Func<Stream, byte[]> hash)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(hash(stream)).ToLowerInvariant();
    }
}
