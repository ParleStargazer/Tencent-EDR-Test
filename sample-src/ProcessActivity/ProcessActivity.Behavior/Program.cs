using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Loader;

namespace ProcessActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        try
        {
            var options = ArgumentReader.Parse(args);
            return options.Require("role") switch
            {
                "actor" => RunActor(options),
                "target" => RunTarget(options),
                var role => throw new ArgumentException($"未知行为角色：{role}"),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static int RunActor(ArgumentReader options)
    {
        var operation = options.Require("operation");
        var resultPath = Path.GetFullPath(options.Require("result"));
        BehaviorResult result;
        try
        {
            result = operation switch
            {
                "create" => CreateProcess(options),
                "terminate" => TerminateProcess(options),
                "access" => AccessProcess(options),
                "image_load" => TriggerImageLoad(options),
                "remote_thread_create" => CreateRemoteThread(options),
                "tamper" => TamperProcess(options),
                _ => throw new ArgumentException($"未知进程行为：{operation}"),
            };
        }
        catch (Exception exception)
        {
            result = Failure(operation, exception);
        }

        ProtocolJson.WriteAtomic(resultPath, result);
        Thread.Sleep(options.GetInt("hold-ms", 750, 0, 10_000));
        return result.Succeeded ? 0 : BehaviorError;
    }

    private static int RunTarget(ArgumentReader options)
    {
        var operation = options.Require("operation");
        var readyPath = Path.GetFullPath(options.Require("ready"));
        ProtocolJson.WriteAtomic(readyPath, CaptureCurrentSnapshot());
        if (operation == "idle")
        {
            Thread.Sleep(options.GetInt("lifetime-ms", 15_000, 100, 120_000));
            return 0;
        }

        return operation switch
        {
            "image_load" => RunNativeImageTarget(options),
            "managed_image_load" => RunManagedImageTarget(options),
            _ => throw new ArgumentException($"未知 Target 行为：{operation}"),
        };
    }

    private static int RunNativeImageTarget(ArgumentReader options)
    {
        const string operation = "image_load";
        var goPath = Path.GetFullPath(options.Require("go"));
        var resultPath = Path.GetFullPath(options.Require("result"));
        WaitForFile(goPath, options.GetInt("wait-ms", 15_000, 100, 120_000));
        var nonceTag = SafeFileTag(options.Require("nonce"));
        var workDirectory = Path.GetDirectoryName(resultPath)!;
        var attempts = new List<ImageLoadAttempt>();
        var plans = new[]
        {
            new ImageLoadPlan(
                "system_loadlibrary",
                "系统目录 DLL 显式加载",
                "Explicit System DLL Load",
                "LoadLibraryW",
                ResolveSystemLibrary(options.Get("library", "winhttp.dll")),
                null,
                false,
                null),
            new ImageLoadPlan(
                "application_local_loadlibrary",
                "应用目录 DLL 加载",
                "Application-local DLL Load",
                "LoadLibraryW",
                ResolveSystemLibrary(options.Get("application-local-library", "version.dll")),
                Path.Combine(workDirectory, $"edrtest_{nonceTag}_version.dll"),
                false,
                null),
            new ImageLoadPlan(
                "application_local_loadlibrary_ex",
                "应用目录 DLL 扩展加载",
                "Application-local DLL LoadLibraryEx",
                "LoadLibraryExW(LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32)",
                ResolveSystemLibrary(options.Get("loadlibraryex-library", "dbghelp.dll")),
                Path.Combine(workDirectory, $"edrtest_{nonceTag}_dbghelp.dll"),
                true,
                null),
            new ImageLoadPlan(
                "application_private_unsigned_native",
                "应用私有无签名原生 DLL 加载并调用导出",
                "Application-private Unsigned Native DLL Load and Export Call",
                "LoadLibraryW + GetProcAddress(sqlite3_libversion_number)",
                Path.GetFullPath(options.Require("private-native-library")),
                Path.Combine(workDirectory, $"edrtest_{nonceTag}_e_sqlite3.dll"),
                false,
                "sqlite3_libversion_number"),
        };
        var interLoadDelay = options.GetInt("inter-load-delay-ms", 1_000, 0, 10_000);
        for (var index = 0; index < plans.Length; index++)
        {
            var plan = plans[index];
            var loadPath = plan.DestinationPath ?? plan.SourcePath;
            if (plan.DestinationPath is not null) File.Copy(plan.SourcePath, plan.DestinationPath, overwrite: false);
            attempts.Add(LoadImage(plan, loadPath));
            if (index + 1 < plans.Length && interLoadDelay > 0)
                WaitBetweenImageLoads(plan.DisplayNameZh, plans[index + 1].DisplayNameZh, index, plans.Length + 1, interLoadDelay);
        }

        var primary = attempts[0];
        var errors = attempts.Where(value => !value.Succeeded)
            .Select(value => $"{value.SubtestId}: {value.Error ?? $"Win32 {value.Win32Error}"}")
            .ToArray();
        var result = new BehaviorResult
        {
            Operation = operation,
            Succeeded = attempts.All(value => value.Succeeded),
            Win32Error = attempts.FirstOrDefault(value => !value.Succeeded)?.Win32Error ?? 0,
            Error = errors.Length == 0 ? null : string.Join(" | ", errors),
            OccurredAtUtc = primary.OccurredAtUtc,
            Target = CaptureCurrentSnapshot(),
            ImagePath = primary.ImagePath,
            ImageBaseAddress = primary.BaseAddress,
            ImageSizeBytes = primary.SizeBytes,
            ImageSha256 = primary.Sha256,
            BeforeLoaded = primary.BeforeLoaded,
            AfterLoaded = primary.AfterLoaded,
            ImageLoads = attempts,
        };
        ProtocolJson.WriteAtomic(resultPath, result);
        Thread.Sleep(options.GetInt("hold-ms", 2_000, 0, 30_000));
        return result.Succeeded ? 0 : BehaviorError;
    }

    private static int RunManagedImageTarget(ArgumentReader options)
    {
        const string operation = "managed_image_load";
        var goPath = Path.GetFullPath(options.Require("go"));
        var resultPath = Path.GetFullPath(options.Require("result"));
        WaitForFile(goPath, options.GetInt("wait-ms", 15_000, 100, 120_000));
        var sourcePath = Path.GetFullPath(options.Require("managed-assembly"));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到托管 DLL 测试载荷。", sourcePath);
        var nonceTag = SafeFileTag(options.Require("nonce"));
        var imagePath = Path.Combine(Path.GetDirectoryName(resultPath)!, $"edrtest_{nonceTag}_managed.dll");
        File.Copy(sourcePath, imagePath, overwrite: false);
        var before = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            !string.IsNullOrWhiteSpace(assembly.Location)
            && string.Equals(Path.GetFullPath(assembly.Location), imagePath, StringComparison.OrdinalIgnoreCase));
        var occurred = DateTimeOffset.UtcNow;
        var loadContext = new AssemblyLoadContext($"EDRTest-{nonceTag}", isCollectible: false);
        var assembly = loadContext.LoadFromAssemblyPath(imagePath);
        var after = string.Equals(Path.GetFullPath(assembly.Location), imagePath, StringComparison.OrdinalIgnoreCase)
            && loadContext.Assemblies.Contains(assembly);
        var attempt = new ImageLoadAttempt
        {
            SubtestId = "managed_assembly_load_context",
            TargetRole = "helper",
            DisplayNameZh = "dotnet 托管宿主加载新落盘程序集",
            DisplayNameEn = "Newly Written Managed Assembly Load",
            Method = "AssemblyLoadContext.LoadFromAssemblyPath",
            SourcePath = sourcePath,
            ImagePath = imagePath,
            FileName = Path.GetFileName(imagePath),
            OccurredAtUtc = occurred,
            Succeeded = !before && after,
            Win32Error = 0,
            Error = before ? "托管测试程序集在触发加载前已经存在。" : after ? null : "AssemblyLoadContext 未保留目标程序集。",
            BaseAddress = null,
            SizeBytes = new FileInfo(imagePath).Length,
            Sha256 = FileSha256(imagePath),
            BeforeLoaded = before,
            AfterLoaded = after,
            TemporaryCopy = true,
        };
        var result = new BehaviorResult
        {
            Operation = operation,
            Succeeded = attempt.Succeeded,
            Win32Error = attempt.Win32Error,
            Error = attempt.Error,
            OccurredAtUtc = attempt.OccurredAtUtc,
            Target = CaptureCurrentSnapshot(),
            ImagePath = attempt.ImagePath,
            ImageSizeBytes = attempt.SizeBytes,
            ImageSha256 = attempt.Sha256,
            BeforeLoaded = attempt.BeforeLoaded,
            AfterLoaded = attempt.AfterLoaded,
            ImageLoads = [attempt],
        };
        ProtocolJson.WriteAtomic(resultPath, result);
        Thread.Sleep(options.GetInt("hold-ms", 5_000, 0, 30_000));
        GC.KeepAlive(assembly);
        GC.KeepAlive(loadContext);
        return result.Succeeded ? 0 : BehaviorError;
    }

    private static ImageLoadAttempt LoadImage(ImageLoadPlan plan, string requestedPath)
    {
        var before = CurrentProcessModuleLoaded(requestedPath);
        var occurred = DateTimeOffset.UtcNow;
        var module = plan.UseLoadLibraryEx
            ? NativeMethods.LoadLibraryExW(
                requestedPath,
                IntPtr.Zero,
                NativeMethods.LoadLibrarySearchDllLoadDir | NativeMethods.LoadLibrarySearchSystem32)
            : NativeMethods.LoadLibraryW(requestedPath);
        var error = module == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        var loadedPath = module == IntPtr.Zero ? requestedPath : GetModulePath(module);
        var after = CurrentProcessModuleLoaded(loadedPath);
        bool? exportResolved = null;
        bool? exportInvoked = null;
        long? exportResult = null;
        string? exportError = null;
        if (module != IntPtr.Zero && plan.ExportName is not null)
        {
            var exportAddress = NativeMethods.GetProcAddress(module, plan.ExportName);
            exportResolved = exportAddress != IntPtr.Zero;
            if (exportAddress == IntPtr.Zero)
            {
                exportInvoked = false;
                exportError = $"未找到原生导出：{plan.ExportName}";
            }
            else
            {
                try
                {
                    var export = Marshal.GetDelegateForFunctionPointer<Sqlite3LibVersionNumber>(exportAddress);
                    exportResult = export();
                    exportInvoked = true;
                    if (exportResult < 3_000_000)
                        exportError = $"sqlite3_libversion_number 返回异常版本号：{exportResult}";
                }
                catch (Exception exception)
                {
                    exportInvoked = false;
                    exportError = $"调用原生导出 {plan.ExportName} 失败：{exception.Message}";
                }
            }
        }
        var succeeded = module != IntPtr.Zero && !before && after
            && (plan.ExportName is null || exportResolved == true && exportInvoked == true && exportResult >= 3_000_000);
        return new ImageLoadAttempt
        {
            SubtestId = plan.SubtestId,
            TargetRole = "target",
            DisplayNameZh = plan.DisplayNameZh,
            DisplayNameEn = plan.DisplayNameEn,
            Method = plan.Method,
            SourcePath = plan.SourcePath,
            ImagePath = loadedPath,
            FileName = Path.GetFileName(loadedPath),
            OccurredAtUtc = occurred,
            Succeeded = succeeded,
            Win32Error = error,
            Error = module == IntPtr.Zero
                ? new Win32Exception(error).Message
                : before ? $"目标模块在触发加载前已经存在：{Path.GetFileName(requestedPath)}" : exportError,
            BaseAddress = module == IntPtr.Zero ? null : Hex(module),
            SizeBytes = File.Exists(loadedPath) ? new FileInfo(loadedPath).Length : null,
            Sha256 = File.Exists(loadedPath) ? FileSha256(loadedPath) : null,
            ExportName = plan.ExportName,
            ExportResolved = exportResolved,
            ExportInvoked = exportInvoked,
            ExportResult = exportResult,
            BeforeLoaded = before,
            AfterLoaded = after,
            TemporaryCopy = plan.DestinationPath is not null,
        };
    }

    private static BehaviorResult CreateProcess(ArgumentReader options)
    {
        var target = Path.GetFullPath(options.Require("target"));
        var ready = Path.GetFullPath(options.Require("target-ready"));
        var lifetime = options.GetInt("target-lifetime-ms", 5_000, 500, 120_000);
        var nonce = options.Require("nonce");
        var targetArguments = new[]
        {
            "--role", "target", "--operation", "idle", "--ready", ready,
            "--lifetime-ms", lifetime.ToString(), "--nonce", nonce,
        };
        using var process = Start(target, targetArguments);
        WaitForFile(ready, Math.Min(lifetime, 10_000));
        var snapshot = ProtocolJson.Read<ProcessSnapshot>(ready);
        var threadId = TryGetInitialThreadId(process);
        return new BehaviorResult
        {
            Operation = "create",
            Succeeded = !process.HasExited && snapshot.Pid == process.Id,
            Win32Error = 0,
            OccurredAtUtc = snapshot.StartedAtUtc,
            Target = snapshot,
            InitialThreadId = threadId,
        };
    }

    private static BehaviorResult TerminateProcess(ArgumentReader options)
    {
        var pid = options.GetInt("target-pid", 0, 1, int.MaxValue);
        var exitCode = options.GetInt("exit-code", 197, 0, 255);
        using var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessTerminate | NativeMethods.Synchronize | NativeMethods.ProcessQueryLimitedInformation,
            false,
            pid);
        if (handle.IsInvalid) throw LastWin32("OpenProcess(PROCESS_TERMINATE)");
        var occurred = DateTimeOffset.UtcNow;
        if (!NativeMethods.TerminateProcess(handle, (uint)exitCode)) throw LastWin32("TerminateProcess");
        if (NativeMethods.WaitForSingleObject(handle.DangerousGetHandle(), 10_000) != NativeMethods.WaitObject0)
        {
            throw new TimeoutException("等待 Target 终止超时。");
        }
        if (!NativeMethods.GetExitCodeProcess(handle, out var observedExitCode)) throw LastWin32("GetExitCodeProcess");
        return new BehaviorResult
        {
            Operation = "terminate",
            Succeeded = observedExitCode == (uint)exitCode,
            Win32Error = 0,
            OccurredAtUtc = occurred,
            RequestedExitCode = exitCode,
            ObservedExitCode = checked((int)observedExitCode),
            ObservedExitAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static BehaviorResult AccessProcess(ArgumentReader options)
    {
        var pid = options.GetInt("target-pid", 0, 1, int.MaxValue);
        var accessMask = options.GetUInt("access-mask", NativeMethods.ProcessQueryLimitedInformation);
        var occurred = DateTimeOffset.UtcNow;
        using var handle = NativeMethods.OpenProcess(accessMask, false, pid);
        var obtained = !handle.IsInvalid;
        var error = obtained ? 0 : Marshal.GetLastWin32Error();
        if (obtained)
        {
            var path = new StringBuilder(32_768);
            var size = (uint)path.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, path, ref size))
            {
                throw LastWin32("QueryFullProcessImageName");
            }
        }
        return new BehaviorResult
        {
            Operation = "access",
            Succeeded = obtained,
            Win32Error = error,
            Error = obtained ? null : new Win32Exception(error).Message,
            OccurredAtUtc = occurred,
            AccessOperationName = "OpenProcess/QueryFullProcessImageName",
            RequestedAccessMask = accessMask,
            GrantedAccessMask = obtained ? accessMask : null,
            HandleObtained = obtained,
        };
    }

    private static BehaviorResult TriggerImageLoad(ArgumentReader options)
    {
        var goPath = Path.GetFullPath(options.Require("go"));
        Directory.CreateDirectory(Path.GetDirectoryName(goPath)!);
        File.WriteAllText(goPath, options.Require("nonce"));
        return new BehaviorResult
        {
            Operation = "image_load",
            Succeeded = File.Exists(goPath),
            Win32Error = 0,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static BehaviorResult CreateRemoteThread(ArgumentReader options)
    {
        var pid = options.GetInt("target-pid", 0, 1, int.MaxValue);
        var library = ResolveSystemLibrary(options.Get("library", "winhttp.dll"));
        var bytes = Encoding.Unicode.GetBytes(library + '\0');
        const uint access = NativeMethods.ProcessCreateThread | NativeMethods.ProcessQueryInformation |
            NativeMethods.ProcessVmOperation | NativeMethods.ProcessVmWrite | NativeMethods.ProcessVmRead;
        using var handle = NativeMethods.OpenProcess(access, false, pid);
        if (handle.IsInvalid) throw LastWin32("OpenProcess(remote-thread)");
        var parameter = NativeMethods.VirtualAllocEx(handle, IntPtr.Zero, (nuint)bytes.Length,
            NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
        if (parameter == IntPtr.Zero) throw LastWin32("VirtualAllocEx");
        try
        {
            if (!NativeMethods.WriteProcessMemory(handle, parameter, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
            {
                throw LastWin32("WriteProcessMemory(remote-thread parameter)");
            }

            var startAddress = ResolveRemoteLoadLibrary(pid);
            var occurred = DateTimeOffset.UtcNow;
            var thread = NativeMethods.CreateRemoteThread(handle, IntPtr.Zero, 0, startAddress, parameter, 0, out var threadId);
            if (thread == IntPtr.Zero) throw LastWin32("CreateRemoteThread");
            try
            {
                if (NativeMethods.WaitForSingleObject(thread, 10_000) != NativeMethods.WaitObject0)
                {
                    throw new TimeoutException("等待远程线程执行超时。");
                }
                if (!NativeMethods.GetExitCodeThread(thread, out var remoteResult)) throw LastWin32("GetExitCodeThread");
                return new BehaviorResult
                {
                    Operation = "remote_thread_create",
                    Succeeded = remoteResult != 0,
                    Win32Error = 0,
                    Error = remoteResult == 0 ? "远程 LoadLibraryW 返回空模块句柄。" : null,
                    OccurredAtUtc = occurred,
                    ImagePath = library,
                    ThreadId = checked((int)threadId),
                    StartAddress = Hex(startAddress),
                    ParameterAddress = Hex(parameter),
                    CreationFlags = 0,
                };
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        finally
        {
            NativeMethods.VirtualFreeEx(handle, parameter, 0, NativeMethods.MemRelease);
        }
    }

    private static BehaviorResult TamperProcess(ArgumentReader options)
    {
        var pid = options.GetInt("target-pid", 0, 1, int.MaxValue);
        var requestedSize = options.GetInt("payload-size", 64, 16, 4096);
        var nonce = options.Require("nonce");
        var seed = Encoding.UTF8.GetBytes("EDRTEST:" + nonce + ":CONTROLLED_MEMORY_WRITE");
        var payload = Enumerable.Range(0, requestedSize).Select(index => seed[index % seed.Length]).ToArray();
        const uint access = NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmOperation |
            NativeMethods.ProcessVmWrite | NativeMethods.ProcessVmRead;
        using var handle = NativeMethods.OpenProcess(access, false, pid);
        if (handle.IsInvalid) throw LastWin32("OpenProcess(tamper)");
        var address = NativeMethods.VirtualAllocEx(handle, IntPtr.Zero, (nuint)payload.Length,
            NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
        if (address == IntPtr.Zero) throw LastWin32("VirtualAllocEx(tamper)");
        BehaviorResult? result = null;
        var memoryReleased = false;
        try
        {
            var before = new byte[payload.Length];
            if (!NativeMethods.ReadProcessMemory(handle, address, before, (nuint)before.Length, out var beforeRead) || beforeRead != (nuint)before.Length)
            {
                throw LastWin32("ReadProcessMemory(before)");
            }
            var occurred = DateTimeOffset.UtcNow;
            if (!NativeMethods.WriteProcessMemory(handle, address, payload, (nuint)payload.Length, out var written) || written != (nuint)payload.Length)
            {
                throw LastWin32("WriteProcessMemory(tamper)");
            }
            var after = new byte[payload.Length];
            if (!NativeMethods.ReadProcessMemory(handle, address, after, (nuint)after.Length, out var afterRead) || afterRead != (nuint)after.Length)
            {
                throw LastWin32("ReadProcessMemory(after)");
            }
            var beforeHash = ByteSha256(before);
            var afterHash = ByteSha256(after);
            result = new BehaviorResult
            {
                Operation = "tamper",
                Succeeded = payload.SequenceEqual(after) && !string.Equals(beforeHash, afterHash, StringComparison.Ordinal),
                Win32Error = 0,
                Error = payload.SequenceEqual(after) ? null : "远程内存回读与写入内容不一致。",
                OccurredAtUtc = occurred,
                TamperTechnique = "VirtualAllocEx/WriteProcessMemory controlled buffer",
                TargetAddress = Hex(address),
                SizeBytes = payload.Length,
                BeforeSha256 = beforeHash,
                AfterSha256 = afterHash,
            };
        }
        finally
        {
            memoryReleased = NativeMethods.VirtualFreeEx(handle, address, 0, NativeMethods.MemRelease);
            if (result is not null) result.MemoryReleased = memoryReleased;
        }
        if (!memoryReleased) throw LastWin32("VirtualFreeEx(tamper cleanup)");
        return result ?? throw new InvalidOperationException("进程篡改行为未产生结果。");
    }

    private static IntPtr ResolveRemoteLoadLibrary(int pid)
    {
        var localKernel = NativeMethods.GetModuleHandleW("kernel32.dll");
        if (localKernel == IntPtr.Zero) throw LastWin32("GetModuleHandleW(kernel32.dll)");
        var localLoadLibrary = NativeMethods.GetProcAddress(localKernel, "LoadLibraryW");
        if (localLoadLibrary == IntPtr.Zero) throw LastWin32("GetProcAddress(LoadLibraryW)");
        using var target = Process.GetProcessById(pid);
        var remoteKernel = target.Modules.Cast<ProcessModule>()
            .FirstOrDefault(module => string.Equals(module.ModuleName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Target 未列出 kernel32.dll 模块。");
        var offset = localLoadLibrary.ToInt64() - localKernel.ToInt64();
        return new IntPtr(remoteKernel.BaseAddress.ToInt64() + offset);
    }

    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"启动程序失败：{executable}");
    }

    private static ProcessSnapshot CaptureCurrentSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessSnapshot
        {
            Pid = Environment.ProcessId,
            ParentPid = 0,
            StartedAtUtc = process.StartTime.ToUniversalTime(),
            Executable = Environment.ProcessPath ?? process.MainModule?.FileName ?? throw new InvalidOperationException("无法取得当前程序路径。"),
            CommandLine = Environment.CommandLine,
        };
    }

    private static int? TryGetInitialThreadId(Process process)
    {
        try
        {
            return process.Threads.Cast<ProcessThread>().OrderBy(thread => thread.StartTime).FirstOrDefault()?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void WaitForFile(string path, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待协议文件超时：{path}");
            Thread.Sleep(25);
        }
    }

    private static string ResolveSystemLibrary(string name)
    {
        if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("library 只能是系统 DLL 文件名或绝对路径。");
        }
        var path = Path.Combine(Environment.SystemDirectory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到系统 DLL。", path);
        return path;
    }

    private static string GetModulePath(IntPtr module)
    {
        var path = new StringBuilder(32_768);
        var length = NativeMethods.GetModuleFileNameW(module, path, path.Capacity);
        if (length == 0) throw LastWin32("GetModuleFileNameW");
        return path.ToString();
    }

    private static bool CurrentProcessModuleLoaded(string expectedPath)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var fullPath = Path.GetFullPath(expectedPath);
        return process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(Path.GetFullPath(module.FileName), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeFileTag(string value)
    {
        var tag = new string(value.Where(char.IsLetterOrDigit).Take(12).ToArray());
        return tag.Length == 0 ? "run" : tag.ToLowerInvariant();
    }

    private static BehaviorResult Failure(string operation, Exception exception)
    {
        var win32Error = exception is Win32Exception win32 ? win32.NativeErrorCode : Marshal.GetLastWin32Error();
        return new BehaviorResult
        {
            Operation = operation,
            Succeeded = false,
            Win32Error = win32Error == 0 ? null : win32Error,
            Error = exception.Message,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static Win32Exception LastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} 失败：{new Win32Exception(error).Message}");
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ByteSha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string Hex(IntPtr value) => $"0x{value.ToInt64():X}";

    private static void WaitBetweenImageLoads(
        string completedSubtest,
        string nextSubtest,
        int completedIndex,
        int totalSubtests,
        int delayMilliseconds)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            status = "SUBTEST_WAITING",
            completed_subtest = completedSubtest,
            next_subtest = nextSubtest,
            completed_index = completedIndex + 1,
            total_subtests = totalSubtests,
            delay_ms = delayMilliseconds,
            message = $"子测试“{completedSubtest}”已完成，等待 {delayMilliseconds} ms 后执行“{nextSubtest}”。",
        }));
        Console.Out.Flush();
        Thread.Sleep(delayMilliseconds);
    }

    private sealed record ImageLoadPlan(
        string SubtestId,
        string DisplayNameZh,
        string DisplayNameEn,
        string Method,
        string SourcePath,
        string? DestinationPath,
        bool UseLoadLibraryEx,
        string? ExportName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Sqlite3LibVersionNumber();
}
