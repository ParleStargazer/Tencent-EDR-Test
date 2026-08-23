using System.ComponentModel;

namespace DriverActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        var operation = "unknown";
        var serviceName = "EdrTestDrv_unallocated";
        var driverName = "EdrTestDriver.sys";
        var imagePath = Path.Combine(Path.GetTempPath(), driverName);
        var nativeApi = "unknown";
        var before = EmptySnapshot(serviceName, imagePath);
        FileSnapshot? fileBefore = null;
        string? marker = null;
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation");
            serviceName = options.Require("service-name");
            imagePath = DriverClient.ValidateDriverPath(options.Require("image-path"), options.Require("allowed-root"));
            driverName = Path.GetFileName(imagePath);
            resultPath = Path.GetFullPath(options.Require("result"));
            marker = operation == "modify" ? options.Require("marker") : null;
            var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
            DriverClient.ValidateServiceName(serviceName);

            before = DriverClient.Snapshot(serviceName, imagePath);
            fileBefore = DriverClient.SnapshotFile(imagePath);
            var occurredAtUtc = Execute(operation, serviceName, imagePath, marker, out nativeApi);
            var completedAtUtc = DateTimeOffset.UtcNow;
            var after = DriverClient.Snapshot(serviceName, imagePath);
            var fileAfter = DriverClient.SnapshotFile(imagePath);
            var succeeded = Verify(operation, marker, before, after, fileBefore, fileAfter);
            ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
            {
                Operation = operation,
                Succeeded = succeeded,
                OccurredAtUtc = occurredAtUtc,
                CompletedAtUtc = completedAtUtc,
                ServiceName = serviceName,
                DriverName = driverName,
                ImagePath = imagePath,
                NativeApi = nativeApi,
                Before = before,
                After = after,
                FileBefore = fileBefore,
                FileAfter = fileAfter,
                Marker = marker,
                Win32Error = 0,
                Error = succeeded ? null : "驱动操作后的独立状态未满足预期。",
            });
            if (holdMs > 0) Thread.Sleep(holdMs);
            return succeeded ? 0 : BehaviorError;
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
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ServiceName = serviceName,
                    DriverName = driverName,
                    ImagePath = imagePath,
                    NativeApi = nativeApi,
                    Before = before,
                    After = SafeSnapshot(serviceName, imagePath),
                    FileBefore = fileBefore,
                    FileAfter = SafeFileSnapshot(imagePath),
                    Marker = marker,
                    Win32Error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF,
                    Error = exception.Message,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static DateTimeOffset Execute(string operation, string serviceName, string imagePath,
        string? marker, out string nativeApi)
    {
        switch (operation)
        {
            case "load":
            case "setup_load":
                DriverClient.Create(serviceName, imagePath);
                nativeApi = "CreateServiceW+StartServiceW";
                var loadTime = DateTimeOffset.UtcNow;
                DriverClient.Start(serviceName);
                DriverClient.WaitForLoaded(serviceName, imagePath, expected: true);
                return loadTime;
            case "modify":
                if (marker is null) throw new ArgumentException("modify 缺少确定性标记。");
                nativeApi = "FileStream.Write";
                var modifyTime = DateTimeOffset.UtcNow;
                DriverClient.AppendMarker(imagePath, marker);
                return modifyTime;
            case "unload":
                nativeApi = "ControlService(STOP)";
                var unloadTime = DateTimeOffset.UtcNow;
                DriverClient.Stop(serviceName);
                DriverClient.WaitForLoaded(serviceName, imagePath, expected: false);
                return unloadTime;
            default:
                throw new ArgumentException($"不支持的驱动操作：{operation}");
        }
    }

    private static bool Verify(string operation, string? marker, DriverSnapshot before, DriverSnapshot after,
        FileSnapshot fileBefore, FileSnapshot fileAfter) => operation switch
    {
        "load" or "setup_load" => !before.Loaded && !before.ServiceExists
            && after.Loaded && after.ServiceExists && after.ServiceState == "running"
            && fileBefore.Sha256 == fileAfter.Sha256 && !string.IsNullOrWhiteSpace(after.BaseAddress),
        "modify" => !before.Loaded && !after.Loaded && !before.ServiceExists && !after.ServiceExists
            && fileBefore.Exists && fileAfter.Exists && fileAfter.SizeBytes > fileBefore.SizeBytes
            && fileBefore.Md5 != fileAfter.Md5 && fileBefore.Sha256 != fileAfter.Sha256
            && marker is not null && File.ReadAllText(fileAfter.Path!).Contains(marker, StringComparison.Ordinal),
        "unload" => before.Loaded && before.ServiceExists && !after.Loaded
            && after.ServiceExists && after.ServiceState == "stopped",
        _ => false,
    };

    private static DriverSnapshot SafeSnapshot(string serviceName, string imagePath)
    {
        try { return DriverClient.Snapshot(serviceName, imagePath); }
        catch { return EmptySnapshot(serviceName, imagePath); }
    }

    private static FileSnapshot SafeFileSnapshot(string imagePath)
    {
        try { return DriverClient.SnapshotFile(imagePath); }
        catch { return new FileSnapshot { Exists = false, Path = imagePath }; }
    }

    private static DriverSnapshot EmptySnapshot(string serviceName, string imagePath) => new()
    {
        Loaded = false,
        ServiceExists = false,
        ServiceName = serviceName,
        ImagePath = imagePath,
    };
}
