using System.Diagnostics;
using System.Text;

namespace VirtualDiskActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string? resultPath = null;
        string? imagePath = null;
        VirtualDiskPlan? plan = null;
        VirtualDiskSnapshot? before = null;
        VirtualDiskSnapshot? after = null;
        ProcessCommandObservation? initiator = null;
        AttachedVirtualDisk? nativeAttachment = null;
        DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow;
        var gateObserved = false;
        try
        {
            var options = ArgumentReader.Parse(args);
            var method = options.Require("method");
            var nonce = options.Require("nonce");
            var workDir = Path.GetFullPath(options.Require("work-dir"));
            var imageRoot = Path.GetFullPath(options.Require("image-root"));
            var readyPath = Path.GetFullPath(options.Require("ready"));
            var gatePath = Path.GetFullPath(options.Require("gate"));
            resultPath = Path.GetFullPath(options.Require("result"));
            imagePath = Path.GetFullPath(options.Require("image-path"));
            var expectedSha256 = options.Require("image-sha256");
            var timeoutMs = options.GetInt("timeout-ms", 90_000, 5_000, 180_000);
            var holdMs = options.GetInt("hold-ms", 1_000, 0, 30_000);
            plan = VirtualDiskPlans.Create(method, nonce);
            ValidateScopedImage(imageRoot, imagePath, plan.ImageFileName);
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Controller 预创建的 VHD 不存在。", imagePath);
            if (!string.Equals(ComputeSha256(imagePath), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VHD 在 Actor 启动前已发生变化。");

            before = VirtualDiskNative.Inspect(imagePath);
            if (!before.ImageExists || before.Attached) throw new InvalidDataException("VHD 初始状态不是“存在且未附加”。");

            occurredAtUtc = DateTimeOffset.UtcNow;
            if (method == VirtualDiskPlans.PowerShell)
            {
                initiator = await RunPowerShellMountAsync(imagePath, workDir, timeoutMs);
                if (initiator.ExitCode != 0)
                    throw new InvalidOperationException($"Mount-DiskImage 失败（退出码 {initiator.ExitCode}）：{initiator.StandardError}");
                occurredAtUtc = initiator.OperationStartedAtUtc;
            }
            else
            {
                nativeAttachment = VirtualDiskNative.AttachReadOnlyWithoutDriveLetter(imagePath);
            }

            after = nativeAttachment?.Inspect(imagePath) ?? VirtualDiskNative.Inspect(imagePath);
            if (!after.Attached || string.IsNullOrWhiteSpace(after.PhysicalPath))
                throw new InvalidDataException("Actor 无法从 VHD 句柄取得已附加的物理磁盘路径。");

            ProtocolJson.WriteAtomic(readyPath, new VirtualDiskReady
            {
                Method = plan.Method,
                InvocationKind = plan.InvocationKind,
                ActorProcessId = Environment.ProcessId,
                ImagePath = imagePath,
                VirtualSizeBytes = VirtualDiskPlans.VirtualSizeBytes,
                ImageSha256 = expectedSha256,
                OccurredAtUtc = occurredAtUtc,
                Before = before,
                After = after,
                InitiatorProcess = initiator,
            });

            WaitForGate(gatePath, plan.Method, after.PhysicalPath, timeoutMs);
            gateObserved = true;
            if (holdMs > 0) await Task.Delay(holdMs);

            if (nativeAttachment is not null) nativeAttachment.Detach();
            else
            {
                var dismount = await RunPowerShellDiskImageCommandAsync(imagePath, workDir, timeoutMs, mount: false);
                if (dismount.ExitCode != 0)
                    throw new InvalidOperationException($"Dismount-DiskImage 失败（退出码 {dismount.ExitCode}）：{dismount.StandardError}");
            }
            nativeAttachment?.Dispose();
            nativeAttachment = null;

            var final = WaitForDetached(imagePath, 5_000);
            var succeeded = gateObserved && !final.Attached;
            ProtocolJson.WriteAtomic(resultPath, CreateResult(plan, imagePath, expectedSha256, occurredAtUtc,
                before, after, final, initiator, gateObserved, succeeded, null, null));
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            uint? error = exception is System.ComponentModel.Win32Exception win32 ? unchecked((uint)win32.NativeErrorCode) : null;
            if (imagePath is not null)
            {
                try { nativeAttachment?.Dispose(); } catch { }
                nativeAttachment = null;
                try
                {
                    if (plan?.Method == VirtualDiskPlans.PowerShell)
                    {
                        var workDir = Path.GetDirectoryName(imagePath)!;
                        _ = await RunPowerShellDiskImageCommandAsync(imagePath, workDir, 30_000, mount: false);
                    }
                    else VirtualDiskNative.DetachIfAttached(imagePath);
                }
                catch { }
            }
            if (resultPath is not null && imagePath is not null && plan is not null && before is not null)
            {
                try
                {
                    var current = after ?? SafeInspect(imagePath);
                    var final = SafeInspect(imagePath);
                    ProtocolJson.WriteAtomic(resultPath, CreateResult(plan, imagePath, File.Exists(imagePath) ? ComputeSha256(imagePath) : string.Empty,
                        occurredAtUtc, before, current, final, initiator, gateObserved, false, error, exception.Message));
                }
                catch { }
            }
            return BehaviorError;
        }
        finally
        {
            nativeAttachment?.Dispose();
        }
    }

    private static VirtualDiskBehaviorResult CreateResult(VirtualDiskPlan plan, string imagePath, string imageSha256,
        DateTimeOffset occurredAtUtc, VirtualDiskSnapshot before, VirtualDiskSnapshot after, VirtualDiskSnapshot final,
        ProcessCommandObservation? initiator, bool gateObserved, bool succeeded, uint? error, string? message) => new()
    {
        Method = plan.Method,
        InvocationKind = plan.InvocationKind,
        ActorProcessId = Environment.ProcessId,
        ImagePath = imagePath,
        VirtualSizeBytes = VirtualDiskPlans.VirtualSizeBytes,
        ImageSha256 = imageSha256,
        OccurredAtUtc = occurredAtUtc,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        ControllerGateObserved = gateObserved,
        ActorAttachVerified = after.Attached && !string.IsNullOrWhiteSpace(after.PhysicalPath),
        ActorDetachVerified = !final.Attached,
        ReadOnly = true,
        NoDriveLetter = true,
        Before = before,
        After = after,
        Final = final,
        Succeeded = succeeded,
        Win32Error = error,
        Error = message,
        InitiatorProcess = initiator,
    };

    private static Task<ProcessCommandObservation> RunPowerShellMountAsync(string imagePath, string workDir, int timeoutMs) =>
        RunPowerShellDiskImageCommandAsync(imagePath, workDir, timeoutMs, mount: true);

    private static async Task<ProcessCommandObservation> RunPowerShellDiskImageCommandAsync(string imagePath, string workDir, int timeoutMs, bool mount)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Windows PowerShell 5.1。", executable);
        var escapedPath = imagePath.Replace("'", "''", StringComparison.Ordinal);
        const string marker = "__EDRTEST_OPERATION_MS__=";
        var operation = mount
            ? $"Mount-DiskImage -ImagePath '{escapedPath}' -StorageType VHD -Access ReadOnly -NoDriveLetter -PassThru -Confirm:$false -ErrorAction Stop | Out-Null"
            : $"Dismount-DiskImage -ImagePath '{escapedPath}' -Confirm:$false -ErrorAction Stop | Out-Null";
        var script = $"$ErrorActionPreference='Stop'; Write-Output ('{marker}' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()); {operation}";
        var arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script };
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows PowerShell。 ");
        var startedAt = SafeStartTime(process, DateTimeOffset.UtcNow);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待{(mount ? "Mount-DiskImage" : "Dismount-DiskImage")}超时：PID {process.Id}");
        }
        var output = await standardOutput;
        return new ProcessCommandObservation
        {
            ProcessId = process.Id,
            Executable = executable,
            CommandLine = FormatCommandLine(executable, arguments),
            StartedAtUtc = startedAt,
            OperationStartedAtUtc = ParseOperationTime(output, marker, startedAt),
            EndedAtUtc = SafeExitTime(process, DateTimeOffset.UtcNow),
            ExitCode = process.ExitCode,
            StandardOutput = output,
            StandardError = await standardError,
        };
    }

    private static DateTimeOffset ParseOperationTime(string output, string marker, DateTimeOffset fallback)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));
        return line is not null && long.TryParse(line[marker.Length..], out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : fallback;
    }

    private static void WaitForGate(string gatePath, string method, string physicalPath, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(gatePath))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待 Controller 独立验证超时。");
            Thread.Sleep(5);
        }
        var gate = ProtocolJson.Read<VirtualDiskVerificationGate>(gatePath);
        if (!string.Equals(gate.Method, method, StringComparison.Ordinal)
            || !string.Equals(gate.PhysicalPath, physicalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Controller 验证门协议与当前挂载不一致。");
    }

    private static VirtualDiskSnapshot WaitForDetached(string imagePath, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        VirtualDiskSnapshot value;
        do
        {
            value = VirtualDiskNative.Inspect(imagePath);
            if (!value.Attached) return value;
            Thread.Sleep(10);
        } while (stopwatch.ElapsedMilliseconds < timeoutMs);
        return value;
    }

    private static void ValidateScopedImage(string imageRoot, string imagePath, string expectedFileName)
    {
        var expected = Path.GetFullPath(Path.Combine(imageRoot, expectedFileName));
        if (!string.Equals(expected, imagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VHD 路径必须是 Controller 声明的镜像目录中的唯一计划文件。");
    }

    private static VirtualDiskSnapshot SafeInspect(string imagePath)
    {
        try { return VirtualDiskNative.Inspect(imagePath); }
        catch { return new VirtualDiskSnapshot { ImagePath = imagePath, ImageExists = File.Exists(imagePath), Attached = false, PhysicalPathError = uint.MaxValue }; }
    }

    private static string ComputeSha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static DateTimeOffset SafeStartTime(Process process, DateTimeOffset fallback) { try { return process.StartTime.ToUniversalTime(); } catch { return fallback; } }
    private static DateTimeOffset SafeExitTime(Process process, DateTimeOffset fallback) { try { return process.ExitTime.ToUniversalTime(); } catch { return fallback; } }
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
}
