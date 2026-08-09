using System.ComponentModel;

namespace UserAccountActivity;

internal static class Program
{
    private const int BehaviorError = 20;

    public static int Main(string[] args)
    {
        string? resultPath = null;
        BehaviorRequest? request = null;
        try
        {
            var options = ArgumentReader.Parse(args);
            var requestPath = Path.GetFullPath(options.Require("request"));
            resultPath = Path.GetFullPath(options.Require("result"));
            request = ProtocolJson.Read<BehaviorRequest>(requestPath);
            Validate(request);

            var result = Execute(request, resultPath);
            return result.Succeeded ? 0 : BehaviorError;
        }
        catch (Exception exception)
        {
            var error = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF;
            if (!string.IsNullOrWhiteSpace(resultPath) && request is not null)
            {
                var snapshot = SafeSnapshot(request.AccountName);
                ProtocolJson.WriteAtomic(resultPath, new BehaviorResult
                {
                    Operation = request.Operation,
                    Succeeded = false,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Win32Error = error == 0 ? null : error,
                    Error = exception.Message,
                    NativeApi = NativeApi(request.Operation),
                    ChangedField = request.Operation == "local_modify" ? "comment" : null,
                    Before = snapshot,
                    After = snapshot,
                    Session = request.Operation is "login" or "logoff"
                        ? new SessionSnapshot
                        {
                            SessionId = null,
                            LogonId = null,
                            LogonType = 3,
                            AuthenticationPackage = "Negotiate",
                            SourceAddress = "local",
                            TokenValidated = false,
                        }
                        : null,
                });
            }
            Console.Error.WriteLine(exception);
            return BehaviorError;
        }
    }

    private static BehaviorResult Execute(BehaviorRequest request, string resultPath)
    {
        var before = AccountNative.Snapshot(request.AccountName);
        if (before.Exists) throw new InvalidOperationException($"本轮临时账号已存在，拒绝操作：{request.AccountName}");

        return request.Operation switch
        {
            "local_create" => CreateAccount(request, before, resultPath),
            "local_modify" => ModifyAccount(request, resultPath),
            "local_delete" => DeleteAccount(request, resultPath),
            "login" => LoginAccount(request, resultPath),
            "logoff" => LogoffAccount(request, resultPath),
            _ => throw new ArgumentException($"不支持的账号操作：{request.Operation}"),
        };
    }

    private static BehaviorResult CreateAccount(BehaviorRequest request, AccountSnapshot before, string resultPath)
    {
        var started = DateTimeOffset.UtcNow;
        AccountNative.Create(request.AccountName, request.Password, request.Comment);
        var ended = DateTimeOffset.UtcNow;
        var after = AccountNative.Snapshot(request.AccountName);
        var succeeded = !before.Exists && after.Exists && !string.IsNullOrWhiteSpace(after.Sid);
        var result = Result(request, succeeded, Midpoint(started, ended), "NetUserAdd", before, after);
        ProtocolJson.WriteAtomic(resultPath, result);
        Hold(request.HoldMs);
        return result;
    }

    private static BehaviorResult ModifyAccount(BehaviorRequest request, string resultPath)
    {
        AccountNative.Create(request.AccountName, request.Password, $"EDRTest setup {request.Nonce}");
        Hold(request.SetupDelayMs);
        var before = AccountNative.Snapshot(request.AccountName);
        var started = DateTimeOffset.UtcNow;
        AccountNative.SetComment(request.AccountName, request.Comment);
        var ended = DateTimeOffset.UtcNow;
        var after = AccountNative.Snapshot(request.AccountName);
        var succeeded = before.Exists && after.Exists && !string.Equals(before.Comment, after.Comment, StringComparison.Ordinal)
            && string.Equals(after.Comment, request.Comment, StringComparison.Ordinal);
        var result = Result(request, succeeded, Midpoint(started, ended), "NetUserSetInfo(level=1007)", before, after, "comment");
        ProtocolJson.WriteAtomic(resultPath, result);
        Hold(request.HoldMs);
        return result;
    }

    private static BehaviorResult DeleteAccount(BehaviorRequest request, string resultPath)
    {
        AccountNative.Create(request.AccountName, request.Password, request.Comment);
        Hold(request.SetupDelayMs);
        var before = AccountNative.Snapshot(request.AccountName);
        var started = DateTimeOffset.UtcNow;
        _ = AccountNative.DeleteIfExists(request.AccountName);
        var ended = DateTimeOffset.UtcNow;
        var after = AccountNative.Snapshot(request.AccountName);
        var succeeded = before.Exists && !after.Exists;
        var result = Result(request, succeeded, Midpoint(started, ended), "NetUserDel", before, after);
        ProtocolJson.WriteAtomic(resultPath, result);
        Hold(request.HoldMs);
        return result;
    }

    private static BehaviorResult LoginAccount(BehaviorRequest request, string resultPath)
    {
        AccountNative.Create(request.AccountName, request.Password, request.Comment);
        Hold(request.SetupDelayMs);
        var before = AccountNative.Snapshot(request.AccountName);
        var started = DateTimeOffset.UtcNow;
        using var session = AccountNative.Logon(request.AccountName, request.Password);
        var ended = DateTimeOffset.UtcNow;
        var after = AccountNative.Snapshot(request.AccountName);
        var sessionSnapshot = Session(session.LogonId);
        var succeeded = after.Exists && sessionSnapshot.TokenValidated;
        var result = Result(request, succeeded, Midpoint(started, ended), "LogonUserW(LOGON32_LOGON_NETWORK)",
            before, after, session: sessionSnapshot);
        ProtocolJson.WriteAtomic(resultPath, result);
        Hold(request.HoldMs); // 保持令牌，避免登录事件与随后关闭令牌的注销事件时间重叠。
        return result;
    }

    private static BehaviorResult LogoffAccount(BehaviorRequest request, string resultPath)
    {
        AccountNative.Create(request.AccountName, request.Password, request.Comment);
        Hold(request.SetupDelayMs);
        var before = AccountNative.Snapshot(request.AccountName);
        var session = AccountNative.Logon(request.AccountName, request.Password);
        var sessionSnapshot = Session(session.LogonId);
        Hold(request.HoldMs); // 先让登录会话稳定，再以关闭令牌的时刻作为注销本地基准。
        var started = DateTimeOffset.UtcNow;
        session.Dispose();
        var ended = DateTimeOffset.UtcNow;
        var after = AccountNative.Snapshot(request.AccountName);
        var succeeded = after.Exists && sessionSnapshot.TokenValidated;
        var result = Result(request, succeeded, Midpoint(started, ended), "CloseHandle(logon token)",
            before, after, session: sessionSnapshot);
        ProtocolJson.WriteAtomic(resultPath, result);
        return result;
    }

    private static BehaviorResult Result(
        BehaviorRequest request,
        bool succeeded,
        DateTimeOffset occurredAtUtc,
        string nativeApi,
        AccountSnapshot before,
        AccountSnapshot after,
        string? changedField = null,
        SessionSnapshot? session = null) => new()
        {
            Operation = request.Operation,
            Succeeded = succeeded,
            OccurredAtUtc = occurredAtUtc,
            Win32Error = 0,
            Error = succeeded ? null : "账号行为后的本地状态未满足预期。",
            NativeApi = nativeApi,
            ChangedField = changedField,
            Before = before,
            After = after,
            Session = session,
        };

    private static SessionSnapshot Session(string logonId) => new()
    {
        SessionId = null,
        LogonId = logonId,
        LogonType = 3,
        AuthenticationPackage = "Negotiate",
        SourceAddress = "local",
        TokenValidated = true,
    };

    private static DateTimeOffset Midpoint(DateTimeOffset started, DateTimeOffset ended) =>
        started.AddTicks((ended - started).Ticks / 2);

    private static void Hold(int milliseconds)
    {
        if (milliseconds > 0) Thread.Sleep(milliseconds);
    }

    private static AccountSnapshot SafeSnapshot(string accountName)
    {
        try { return AccountNative.Snapshot(accountName); }
        catch { return AccountNative.Missing(accountName); }
    }

    private static string NativeApi(string operation) => operation switch
    {
        "local_create" => "NetUserAdd",
        "local_modify" => "NetUserSetInfo(level=1007)",
        "local_delete" => "NetUserDel",
        "login" => "LogonUserW(LOGON32_LOGON_NETWORK)",
        "logoff" => "CloseHandle(logon token)",
        _ => "unknown",
    };

    private static void Validate(BehaviorRequest request)
    {
        if (!request.AccountName.StartsWith("edrt", StringComparison.Ordinal) || request.AccountName.Length > 20
            || request.AccountName.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("临时账号名必须以 edrt 开头、仅含 ASCII 字母数字且不超过 20 个字符。");
        }
        if (request.Password.Length < 12) throw new InvalidDataException("临时账号密码长度不足。");
        if (request.SetupDelayMs is < 0 or > 30_000 || request.HoldMs is < 0 or > 30_000)
        {
            throw new InvalidDataException("账号行为等待参数越界。");
        }
    }
}
