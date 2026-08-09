using System.Diagnostics;
using System.Text.Json;

namespace EdrTest.SampleProtocol;

public static class ReliableProtocolFile
{
    public const int DefaultRetryTimeoutMs = 5_000;

    public static T Read<T>(string path, JsonSerializerOptions options, int retryTimeoutMs = DefaultRetryTimeoutMs)
        where T : class
    {
        ValidateTimeout(retryTimeoutMs);
        var fullPath = Path.GetFullPath(path);
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4_096,
                    FileOptions.SequentialScan);
                return JsonSerializer.Deserialize<T>(stream, options)
                    ?? throw new InvalidDataException($"协议文件不是有效的 {typeof(T).Name}：{fullPath}");
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                lastError = exception;
                if (stopwatch.ElapsedMilliseconds >= retryTimeoutMs) break;
                Thread.Sleep(RetryDelay(stopwatch.ElapsedMilliseconds));
            }
        }

        throw new IOException(
            $"协议文件在 {retryTimeoutMs} ms 内始终无法稳定读取：{fullPath}。文件可能正被 EDR、防病毒软件或写入进程短暂占用。",
            lastError);
    }

    public static void WriteAtomic<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        int retryTimeoutMs = DefaultRetryTimeoutMs)
    {
        ValidateTimeout(retryTimeoutMs);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var moved = false;
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            var stopwatch = Stopwatch.StartNew();
            Exception? lastError = null;
            while (true)
            {
                try
                {
                    File.Move(temporary, fullPath, overwrite: true);
                    moved = true;
                    return;
                }
                catch (Exception exception) when (IsFileLock(exception))
                {
                    lastError = exception;
                    if (stopwatch.ElapsedMilliseconds >= retryTimeoutMs) break;
                    Thread.Sleep(RetryDelay(stopwatch.ElapsedMilliseconds));
                }
            }

            throw new IOException(
                $"协议文件在 {retryTimeoutMs} ms 内始终无法原子发布：{fullPath}。目标可能正被 EDR 或防病毒软件短暂占用。",
                lastError);
        }
        finally
        {
            if (!moved) TryDeleteTemporary(temporary);
        }
    }

    private static bool IsTransient(Exception exception) => exception is IOException or UnauthorizedAccessException or JsonException;
    private static bool IsFileLock(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static int RetryDelay(long elapsedMilliseconds) =>
        (int)Math.Clamp(10 + elapsedMilliseconds / 20, 10, 100);

    private static void ValidateTimeout(int retryTimeoutMs)
    {
        if (retryTimeoutMs is < 0 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(retryTimeoutMs), "协议文件重试时间必须在 0..60000 ms 范围内。");
    }

    private static void TryDeleteTemporary(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
