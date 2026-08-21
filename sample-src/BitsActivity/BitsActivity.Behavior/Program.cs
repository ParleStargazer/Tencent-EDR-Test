using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BitsActivity;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        Guid jobId = Guid.Empty;
        BitsJobSnapshot? createdSnapshot = null;
        BitsPlan? plan = null;
        string? resultPath = null;
        ProcessCommandObservation? initiator = null;
        DateTimeOffset createdAt = default;
        var controllerGateObserved = false;
        await using var server = new LoopbackHttpServer();
        try
        {
            var arguments = ArgumentReader.Parse(args);
            var method = arguments.Require("method");
            var nonce = arguments.Require("nonce");
            var readyPath = Path.GetFullPath(arguments.Require("ready"));
            var gatePath = Path.GetFullPath(arguments.Require("gate"));
            resultPath = Path.GetFullPath(arguments.Require("result"));
            var workDir = Path.GetFullPath(arguments.Require("work-dir"));
            var timeoutMs = arguments.GetInt("timeout-ms", 90_000, 5_000, 300_000);
            var holdMs = arguments.GetInt("hold-ms", 1_000, 0, 30_000);
            plan = BitsPlans.Create(method, nonce);
            Directory.CreateDirectory(workDir);
            var payload = BitsPlans.CreatePayload(method, nonce);
            var payloadSha256 = BitsPlans.Sha256(payload);
            var localPath = Path.GetFullPath(Path.Combine(workDir, plan.LocalFileName));
            if (File.Exists(localPath)) File.Delete(localPath);
            await server.StartAsync(payload, $"/{plan.LocalFileName}");
            var remoteUrl = server.Url;

            createdAt = DateTimeOffset.UtcNow;
            if (method == "bitsadmin")
            {
                var create = await RunBitsAdminAsync(["/create", "/download", plan.DisplayName], workDir, timeoutMs);
                initiator = create;
                EnsureSuccess(create, "创建 BITS 任务");
                jobId = ParseJobId(create.StandardOutput + Environment.NewLine + create.StandardError);
                var add = await RunBitsAdminAsync(["/addfile", FormatJobId(jobId), remoteUrl, localPath], workDir, timeoutMs);
                EnsureSuccess(add, "添加 BITS 下载文件");
            }
            else
            {
                jobId = CreateComJob(plan.DisplayName, remoteUrl, localPath);
            }

            createdSnapshot = WaitForInspectableJob(jobId, timeoutMs);
            ValidateCreatedSnapshot(createdSnapshot, plan, remoteUrl, localPath);
            ProtocolJson.WriteAtomic(readyPath, new BitsJobReady
            {
                Method = method,
                InvocationKind = plan.InvocationKind,
                ActorProcessId = Environment.ProcessId,
                JobId = jobId,
                DisplayName = createdSnapshot.DisplayName,
                JobType = createdSnapshot.JobType,
                State = createdSnapshot.State,
                OwnerSid = createdSnapshot.OwnerSid,
                RemoteUrl = createdSnapshot.RemoteUrl,
                LocalPath = createdSnapshot.LocalPath,
                PayloadSize = payload.LongLength,
                PayloadSha256 = payloadSha256,
                CreatedAtUtc = createdAt,
                InitiatorProcess = initiator,
            });

            await WaitForGateAsync(gatePath, method, timeoutMs);
            controllerGateObserved = true;
            var resumedAt = DateTimeOffset.UtcNow;
            if (method == "bitsadmin")
            {
                var resume = await RunBitsAdminAsync(["/resume", FormatJobId(jobId)], workDir, timeoutMs);
                EnsureSuccess(resume, "恢复 BITS 任务");
            }
            else ResumeComJob(jobId);

            var transferred = await WaitForTransferredAsync(jobId, timeoutMs);
            var transferredAt = DateTimeOffset.UtcNow;
            if (holdMs > 0) await Task.Delay(holdMs);
            if (method == "bitsadmin")
            {
                var complete = await RunBitsAdminAsync(["/complete", FormatJobId(jobId)], workDir, timeoutMs);
                EnsureSuccess(complete, "完成 BITS 任务");
            }
            else CompleteComJob(jobId);
            var completedAt = DateTimeOffset.UtcNow;

            var removed = WaitForRemoved(jobId, 5_000);
            var downloaded = await File.ReadAllBytesAsync(localPath);
            var downloadedSha256 = BitsPlans.Sha256(downloaded);
            var verified = downloaded.AsSpan().SequenceEqual(payload)
                && string.Equals(downloadedSha256, payloadSha256, StringComparison.OrdinalIgnoreCase);
            var succeeded = verified && removed && transferred.BytesTransferred == payload.LongLength;
            var result = new BitsBehaviorResult
            {
                Method = method,
                InvocationKind = plan.InvocationKind,
                ActorProcessId = Environment.ProcessId,
                JobId = jobId,
                DisplayName = createdSnapshot.DisplayName,
                JobType = createdSnapshot.JobType,
                OwnerSid = createdSnapshot.OwnerSid,
                RemoteUrl = createdSnapshot.RemoteUrl,
                LocalPath = createdSnapshot.LocalPath,
                StateBeforeResume = createdSnapshot.State,
                StateBeforeComplete = transferred.State,
                StateAfterComplete = "ACKNOWLEDGED",
                BytesTotal = transferred.BytesTotal,
                BytesTransferred = transferred.BytesTransferred,
                PayloadSize = payload.LongLength,
                PayloadSha256 = payloadSha256,
                DownloadedSha256 = downloadedSha256,
                CreatedAtUtc = createdAt,
                ResumedAtUtc = resumedAt,
                TransferredAtUtc = transferredAt,
                CompletedAtUtc = completedAt,
                ControllerGateObserved = controllerGateObserved,
                DownloadVerified = verified,
                JobRemovedAfterComplete = removed,
                HttpRequestCount = server.RequestCount,
                Succeeded = succeeded,
                Error = succeeded ? null : "BITS 下载内容、字节进度或任务移除验证失败。",
                InitiatorProcess = initiator,
            };
            ProtocolJson.WriteAtomic(resultPath, result);
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            if (jobId != Guid.Empty)
            {
                try { BitsCom.CancelIfExists(jobId); } catch { }
            }
            Console.Error.WriteLine(exception);
            if (resultPath is not null && plan is not null && createdSnapshot is not null)
            {
                ProtocolJson.WriteAtomic(resultPath, new BitsBehaviorResult
                {
                    Method = plan.Method,
                    InvocationKind = plan.InvocationKind,
                    ActorProcessId = Environment.ProcessId,
                    JobId = jobId,
                    DisplayName = createdSnapshot.DisplayName,
                    JobType = createdSnapshot.JobType,
                    OwnerSid = createdSnapshot.OwnerSid,
                    RemoteUrl = createdSnapshot.RemoteUrl,
                    LocalPath = createdSnapshot.LocalPath,
                    StateBeforeResume = createdSnapshot.State,
                    StateBeforeComplete = createdSnapshot.State,
                    StateAfterComplete = "CANCELLED",
                    BytesTotal = createdSnapshot.BytesTotal,
                    BytesTransferred = createdSnapshot.BytesTransferred,
                    PayloadSize = 0,
                    PayloadSha256 = string.Empty,
                    CreatedAtUtc = createdAt,
                    ResumedAtUtc = DateTimeOffset.UtcNow,
                    TransferredAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ControllerGateObserved = controllerGateObserved,
                    DownloadVerified = false,
                    JobRemovedAfterComplete = !BitsCom.Exists(jobId),
                    HttpRequestCount = server.RequestCount,
                    Succeeded = false,
                    HResult = exception.HResult,
                    Error = exception.Message,
                    InitiatorProcess = initiator,
                });
            }
            return 20;
        }
    }

    private static Guid CreateComJob(string displayName, string remoteUrl, string localPath)
    {
        var manager = BitsCom.CreateManager();
        IBackgroundCopyJob? job = null;
        try
        {
            manager.CreateJob(displayName, BG_JOB_TYPE.DOWNLOAD, out var jobId, out job);
            job.AddFile(remoteUrl, localPath);
            return jobId;
        }
        catch
        {
            try { job?.Cancel(); } catch { }
            throw;
        }
        finally
        {
            if (job is not null) Marshal.FinalReleaseComObject(job);
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static void ResumeComJob(Guid jobId) => WithJob(jobId, job => job.Resume());
    private static void CompleteComJob(Guid jobId) => WithJob(jobId, job => job.Complete());

    private static void WithJob(Guid jobId, Action<IBackgroundCopyJob> action)
    {
        var manager = BitsCom.CreateManager();
        IBackgroundCopyJob? job = null;
        try { manager.GetJob(ref jobId, out job); action(job); }
        finally
        {
            if (job is not null) Marshal.FinalReleaseComObject(job);
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static BitsJobSnapshot WaitForInspectableJob(Guid jobId, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? last = null;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try { return BitsCom.Inspect(jobId); }
            catch (Exception exception) { last = exception; Thread.Sleep(25); }
        }
        throw new TimeoutException($"等待 BITS 任务可读取超时：{last?.Message}");
    }

    private static async Task<BitsJobSnapshot> WaitForTransferredAsync(Guid jobId, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var snapshot = BitsCom.Inspect(jobId);
            if (snapshot.State == nameof(BG_JOB_STATE.TRANSFERRED)) return snapshot;
            if (snapshot.State is nameof(BG_JOB_STATE.ERROR) or nameof(BG_JOB_STATE.TRANSIENT_ERROR)
                or nameof(BG_JOB_STATE.CANCELLED))
                throw new InvalidOperationException($"BITS 任务进入异常状态：{snapshot.State}");
            await Task.Delay(25);
        }
        throw new TimeoutException("等待 BITS 任务进入 TRANSFERRED 状态超时。");
    }

    private static bool WaitForRemoved(Guid jobId, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (!BitsCom.Exists(jobId)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static void ValidateCreatedSnapshot(BitsJobSnapshot snapshot, BitsPlan plan, string remoteUrl, string localPath)
    {
        if (snapshot.JobId == Guid.Empty
            || !string.Equals(snapshot.DisplayName, plan.DisplayName, StringComparison.Ordinal)
            || snapshot.JobType != nameof(BG_JOB_TYPE.DOWNLOAD)
            || snapshot.State != nameof(BG_JOB_STATE.SUSPENDED)
            || string.IsNullOrWhiteSpace(snapshot.OwnerSid)
            || !string.Equals(snapshot.RemoteUrl, remoteUrl, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(snapshot.LocalPath), localPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BITS 任务创建后的 COM 快照与测试计划不一致。");
    }

    private static async Task WaitForGateAsync(string path, string method, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待 Controller 放行 BITS 传输超时。");
            await Task.Delay(5);
        }
        var gate = ProtocolJson.Read<BitsExecutionGate>(path);
        if (!string.Equals(gate.Method, method, StringComparison.Ordinal)) throw new InvalidDataException("BITS 放行协议方法不一致。");
    }

    private static async Task<ProcessCommandObservation> RunBitsAdminAsync(IReadOnlyList<string> arguments, string workDir, int timeoutMs)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bitsadmin.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 bitsadmin.exe。");
        var startedAt = TryStartTime(process);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"bitsadmin 命令执行超时：{FormatCommandLine(executable, arguments)}");
        }
        return new ProcessCommandObservation
        {
            ProcessId = process.Id,
            Executable = executable,
            CommandLine = FormatCommandLine(executable, arguments),
            StartedAtUtc = startedAt,
            EndedAtUtc = TryExitTime(process),
            ExitCode = process.ExitCode,
            StandardOutput = await outputTask,
            StandardError = await errorTask,
        };
    }

    private static void EnsureSuccess(ProcessCommandObservation value, string operation)
    {
        if (value.ExitCode != 0) throw new InvalidOperationException($"{operation}失败（{value.ExitCode}）：{value.StandardError}\n{value.StandardOutput}");
    }

    private static Guid ParseJobId(string output)
    {
        var match = JobIdRegex().Match(output);
        if (!match.Success || !Guid.TryParse(match.Value, out var value))
            throw new InvalidDataException($"无法从 bitsadmin 输出解析 Job ID：{output}");
        return value;
    }

    private static string FormatJobId(Guid value) => "{" + value.ToString("D") + "}";
    private static DateTimeOffset TryStartTime(Process process) { try { return process.StartTime.ToUniversalTime(); } catch { return DateTimeOffset.UtcNow; } }
    private static DateTimeOffset TryExitTime(Process process) { try { return process.ExitTime.ToUniversalTime(); } catch { return DateTimeOffset.UtcNow; } }
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    [GeneratedRegex("\\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\}?")]
    private static partial Regex JobIdRegex();
}

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private TcpListener? listener;
    private Task? loop;
    private byte[] payload = [];
    private string path = "/";
    private int requestCount;

    public string Url { get; private set; } = string.Empty;
    public int RequestCount => Volatile.Read(ref requestCount);

    public Task StartAsync(byte[] content, string requestPath)
    {
        payload = content.ToArray();
        path = requestPath;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Url = $"http://127.0.0.1:{endpoint.Port}{path}";
        loop = AcceptLoopAsync(listener, cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener value, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await value.AcceptTcpClientAsync(token);
                _ = HandleAsync(client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var headerBytes = new List<byte>(1024);
                var buffer = new byte[1];
                while (headerBytes.Count < 65_536)
                {
                    var read = await stream.ReadAsync(buffer, token);
                    if (read == 0) break;
                    headerBytes.Add(buffer[0]);
                    var count = headerBytes.Count;
                    if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10
                        && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
                }
                var header = Encoding.ASCII.GetString(headerBytes.ToArray());
                var lines = header.Split("\r\n", StringSplitOptions.None);
                var request = lines.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (request.Length < 2 || !string.Equals(request[1], path, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", [], null, false, token);
                    return;
                }
                Interlocked.Increment(ref requestCount);
                var isHead = string.Equals(request[0], "HEAD", StringComparison.OrdinalIgnoreCase);
                var rangeHeader = lines.FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                var start = 0;
                if (rangeHeader is not null)
                {
                    var value = rangeHeader[(rangeHeader.IndexOf(':') + 1)..].Trim();
                    var match = Regex.Match(value, "^bytes=(\\d+)-");
                    if (match.Success) start = Math.Clamp(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 0, payload.Length);
                }
                var body = payload[start..];
                await WriteResponseAsync(stream, rangeHeader is null ? 200 : 206,
                    rangeHeader is null ? "OK" : "Partial Content", body,
                    rangeHeader is null ? null : $"bytes {start}-{payload.Length - 1}/{payload.Length}", !isHead, token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private async Task WriteResponseAsync(NetworkStream stream, int code, string reason, byte[] body, string? contentRange, bool includeBody, CancellationToken token)
    {
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("ETag: \"").Append(BitsPlans.Sha256(payload)).Append("\"\r\n")
            .Append("Last-Modified: ").Append(DateTimeOffset.UtcNow.AddMinutes(-1).ToString("R", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Connection: close\r\n");
        if (contentRange is not null) headers.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token);
        if (includeBody && body.Length > 0) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener?.Stop();
        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
        cancellation.Dispose();
    }
}
