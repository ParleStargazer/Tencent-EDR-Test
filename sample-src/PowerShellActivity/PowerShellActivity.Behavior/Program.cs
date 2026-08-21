using System.Diagnostics;
using System.Text;

namespace PowerShellActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static async Task<int> Main(string[] args)
    {
        string? resultPath = null;
        ScriptPlan? plan = null;
        Process? target = null;
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var completedAtUtc = occurredAtUtc;
        var targetStartedAtUtc = occurredAtUtc;
        var warmupSucceeded = false;
        try
        {
            var options = ArgumentReader.Parse(args);
            var method = options.Require("method");
            var nonce = options.Require("nonce");
            plan = PowerShellScriptPlans.Create(method, nonce);
            resultPath = Path.GetFullPath(options.Require("result"));
            var readyPath = Path.GetFullPath(options.Require("ready"));
            var gatePath = Path.GetFullPath(options.Require("gate"));
            var timeoutMs = options.GetInt("timeout-ms", 20_000, 1_000, 120_000);
            var holdMs = options.GetInt("hold-ms", 1_000, 0, 30_000);
            if (!File.Exists(plan.PowerShellExecutable))
                throw new FileNotFoundException("找不到系统 Windows PowerShell。", plan.PowerShellExecutable);

            var startInfo = new ProcessStartInfo
            {
                FileName = plan.PowerShellExecutable,
                WorkingDirectory = Path.GetDirectoryName(resultPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in plan.TargetArguments) startInfo.ArgumentList.Add(argument);
            target = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动系统 Windows PowerShell。");
            targetStartedAtUtc = TryStartTime(target, DateTimeOffset.UtcNow);
            ProtocolJson.WriteAtomic(readyPath, new PowerShellTargetReady
            {
                Method = plan.Method,
                TargetProcessId = target.Id,
                TargetExecutable = plan.PowerShellExecutable,
                TargetCommandLine = plan.TargetCommandLine,
                StartedAtUtc = targetStartedAtUtc,
            });

            await WaitForGateAsync(gatePath, plan.Method, timeoutMs, target);
            await target.StandardInput.WriteLineAsync(PowerShellScriptPlans.WarmupCommand(plan));
            await target.StandardInput.FlushAsync();
            warmupSucceeded = await ReadUntilMarkerAsync(target.StandardOutput, plan.WarmupMarker, timeoutMs);
            if (!warmupSucceeded) throw new InvalidOperationException("PowerShell 预热握手没有返回预期标记。");

            var outputTask = target.StandardOutput.ReadToEndAsync();
            var errorTask = target.StandardError.ReadToEndAsync();
            occurredAtUtc = DateTimeOffset.UtcNow;
            await target.StandardInput.WriteLineAsync(plan.SubmittedCommand);
            await target.StandardInput.WriteLineAsync("exit 0");
            await target.StandardInput.FlushAsync();
            target.StandardInput.Close();
            using (var timeout = new CancellationTokenSource(timeoutMs))
            {
                await target.WaitForExitAsync(timeout.Token);
            }
            completedAtUtc = DateTimeOffset.UtcNow;
            var output = await outputTask;
            var error = await errorTask;
            var outputVerified = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), plan.Marker, StringComparison.Ordinal));
            var succeeded = target.ExitCode == 0 && outputVerified;
            var result = Result(plan, target, targetStartedAtUtc, occurredAtUtc, completedAtUtc,
                warmupSucceeded, outputVerified, succeeded, output, error, succeeded ? null : "PowerShell 输出或退出码不符合预期。");
            ProtocolJson.WriteAtomic(resultPath, result);
            if (holdMs > 0) await Task.Delay(holdMs);
            return succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            completedAtUtc = DateTimeOffset.UtcNow;
            TryStop(target);
            if (!string.IsNullOrWhiteSpace(resultPath) && plan is not null)
            {
                ProtocolJson.WriteAtomic(resultPath, Result(plan, target, targetStartedAtUtc, occurredAtUtc,
                    completedAtUtc, warmupSucceeded, false, false, string.Empty, string.Empty, exception.Message));
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
        finally
        {
            target?.Dispose();
        }
    }

    private static PowerShellBehaviorResult Result(
        ScriptPlan plan,
        Process? target,
        DateTimeOffset targetStartedAtUtc,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset completedAtUtc,
        bool warmupSucceeded,
        bool outputVerified,
        bool succeeded,
        string output,
        string errorOutput,
        string? error)
    {
        int? exitCode = null;
        DateTimeOffset? endedAtUtc = null;
        if (target is not null)
        {
            try { if (target.HasExited) { exitCode = target.ExitCode; endedAtUtc = target.ExitTime.ToUniversalTime(); } } catch (InvalidOperationException) { }
        }
        string? engineVersion = null;
        try { engineVersion = FileVersionInfo.GetVersionInfo(plan.PowerShellExecutable).ProductVersion; } catch { }
        return new PowerShellBehaviorResult
        {
            Method = plan.Method,
            InvocationKind = plan.InvocationKind,
            ActorProcessId = Environment.ProcessId,
            TargetProcessId = target?.Id ?? 0,
            TargetExecutable = plan.PowerShellExecutable,
            TargetCommandLine = plan.TargetCommandLine,
            CommandFormToken = plan.CommandFormToken,
            SubmittedCommand = plan.SubmittedCommand,
            SubmittedCommandSha256 = plan.SubmittedCommandSha256,
            ExpectedContent = plan.ExpectedContent,
            ExpectedContentSha256 = plan.ExpectedContentSha256,
            Marker = plan.Marker,
            TargetStartedAtUtc = targetStartedAtUtc,
            TargetEndedAtUtc = endedAtUtc,
            OccurredAtUtc = occurredAtUtc,
            CompletedAtUtc = completedAtUtc,
            WarmupSucceeded = warmupSucceeded,
            OutputVerified = outputVerified,
            Succeeded = succeeded,
            StandardOutput = output,
            StandardError = errorOutput,
            EngineVersion = engineVersion,
            ExitCode = exitCode,
            Error = error,
        };
    }

    private static async Task WaitForGateAsync(string path, string method, int timeoutMs, Process target)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (target.HasExited) throw new InvalidOperationException($"PowerShell 在 Controller 放行前退出：{target.ExitCode}");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("等待 Controller 放行 PowerShell 子测试超时。");
            await Task.Delay(5);
        }
        var gate = ProtocolJson.Read<PowerShellExecutionGate>(path);
        if (!string.Equals(gate.Method, method, StringComparison.Ordinal))
            throw new InvalidDataException("Controller 放行协议中的方法不一致。");
    }

    private static async Task<bool> ReadUntilMarkerAsync(StreamReader reader, string marker, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return false;
            if (string.Equals(line.Trim(), marker, StringComparison.Ordinal)) return true;
        }
    }

    private static DateTimeOffset TryStartTime(Process process, DateTimeOffset fallback)
    {
        try { return process.StartTime.ToUniversalTime(); } catch { return fallback; }
    }

    private static void TryStop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5_000); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
