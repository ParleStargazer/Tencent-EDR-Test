using System.ComponentModel;
using System.Text;

namespace UsbDeviceActivity;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string? resultPath = null;
        string? operation = null;
        string? serial = null;
        UsbDriverStatus? before = null;
        var occurredAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var options = ArgumentReader.Parse(args);
            operation = options.Require("operation").ToLowerInvariant();
            serial = options.Require("serial");
            resultPath = Path.GetFullPath(options.Require("result"));
            if (operation is not ("attach" or "detach")) throw new ArgumentException($"不支持的 USB 操作：{operation}");
            if (!UsbTestConstants.IsValidSerial(serial))
                throw new ArgumentException("USB 测试序列号格式无效。", nameof(serial));

            before = UsbUdeClient.Query();
            occurredAtUtc = DateTimeOffset.UtcNow;
            if (operation == "attach") UsbUdeClient.Attach(serial);
            else UsbUdeClient.Detach();
            var after = UsbUdeClient.Query();
            var succeeded = operation == "attach"
                ? !before.Attached && after.Attached && string.Equals(after.SerialNumber, serial, StringComparison.Ordinal)
                : before.Attached && string.Equals(before.SerialNumber, serial, StringComparison.Ordinal) && !after.Attached;
            ProtocolJson.WriteAtomic(resultPath, CreateResult(operation, serial, occurredAtUtc, before, after,
                ioctlSucceeded: true, succeeded, null, null));
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (resultPath is not null && operation is not null && serial is not null)
            {
                try
                {
                    var current = SafeQuery(before);
                    ProtocolJson.WriteAtomic(resultPath, CreateResult(operation, serial, occurredAtUtc,
                        before ?? current, current, ioctlSucceeded: false, succeeded: false,
                        exception is Win32Exception win32 ? win32.NativeErrorCode : null, exception.Message));
                }
                catch { }
            }
            return 20;
        }
    }

    private static UsbDriverStatus SafeQuery(UsbDriverStatus? fallback)
    {
        try { return UsbUdeClient.Query(); }
        catch { return fallback ?? new UsbDriverStatus { Attached = false }; }
    }

    private static UsbBehaviorResult CreateResult(string operation, string serial, DateTimeOffset occurredAtUtc,
        UsbDriverStatus before, UsbDriverStatus after, bool ioctlSucceeded, bool succeeded, int? win32Error, string? error) => new()
    {
        Operation = operation,
        Method = UsbTestConstants.Method,
        ActorProcessId = Environment.ProcessId,
        OccurredAtUtc = occurredAtUtc,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        SerialNumber = serial,
        ExpectedInstanceId = UsbTestConstants.ExpectedInstanceId(serial),
        Before = before,
        After = after,
        IoctlSucceeded = ioctlSucceeded,
        Succeeded = succeeded,
        Win32Error = win32Error,
        Error = error,
    };
}
