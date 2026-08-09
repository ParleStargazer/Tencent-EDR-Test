using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UserAccountActivity;

public static class AccountNative
{
    private const uint Success = 0;
    private const uint UserNotFound = 2221;
    private const uint UserPrivilege = 1;
    private const uint UserScript = 0x0001;
    private const uint AccountDisable = 0x0002;
    private const uint NormalAccount = 0x0200;
    private const uint PasswordNeverExpires = 0x10000;
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;
    private const int TokenStatisticsClass = 10;

    public static AccountSnapshot Snapshot(string accountName)
    {
        var status = NetUserGetInfo(null, accountName, 1, out var buffer);
        if (status == UserNotFound)
        {
            return Missing(accountName);
        }
        ThrowIfError(status, nameof(NetUserGetInfo));
        try
        {
            var info = Marshal.PtrToStructure<UserInfo1>(buffer);
            return new AccountSnapshot
            {
                Exists = true,
                Name = accountName,
                Sid = LookupSid(accountName),
                Domain = Environment.MachineName,
                AccountType = "local_user",
                Comment = info.Comment,
                Flags = info.Flags,
                Active = (info.Flags & AccountDisable) == 0,
            };
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public static void Create(string accountName, string password, string comment)
    {
        var info = new UserInfo1
        {
            Name = accountName,
            Password = password,
            Privilege = UserPrivilege,
            Comment = comment,
            Flags = UserScript | NormalAccount | PasswordNeverExpires,
        };
        var status = NetUserAdd(null, 1, ref info, out _);
        ThrowIfError(status, nameof(NetUserAdd));
    }

    public static void SetComment(string accountName, string comment)
    {
        var info = new UserInfo1007 { Comment = comment };
        var status = NetUserSetInfo(null, accountName, 1007, ref info, out _);
        ThrowIfError(status, nameof(NetUserSetInfo));
    }

    public static bool DeleteIfExists(string accountName)
    {
        var status = NetUserDel(null, accountName);
        if (status == UserNotFound) return false;
        ThrowIfError(status, nameof(NetUserDel));
        return true;
    }

    public static LogonSession Logon(string accountName, string password)
    {
        if (!LogonUser(accountName, Environment.MachineName, password, Logon32LogonNetwork,
                Logon32ProviderDefault, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LogonUserW 失败。");
        }
        try
        {
            var size = Marshal.SizeOf<TokenStatistics>();
            if (!GetTokenInformation(token, TokenStatisticsClass, out var statistics, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenStatistics) 失败。");
            }
            var authenticationId = ((ulong)(uint)statistics.AuthenticationId.HighPart << 32)
                | statistics.AuthenticationId.LowPart;
            return new LogonSession(token, $"0x{authenticationId:X}");
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    public static AccountSnapshot Missing(string accountName) => new()
    {
        Exists = false,
        Name = accountName,
        Domain = Environment.MachineName,
        AccountType = "local_user",
    };

    private static string? LookupSid(string accountName)
    {
        uint sidSize = 0;
        uint domainSize = 0;
        _ = LookupAccountName(null, $"{Environment.MachineName}\\{accountName}", null, ref sidSize,
            null, ref domainSize, out _);
        if (sidSize == 0) return null;
        var sid = new byte[sidSize];
        var domain = new StringBuilder((int)domainSize);
        if (!LookupAccountName(null, $"{Environment.MachineName}\\{accountName}", sid, ref sidSize,
                domain, ref domainSize, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupAccountNameW 失败。");
        }
        return new SecurityIdentifier(sid, 0).Value;
    }

    private static void ThrowIfError(uint status, string api)
    {
        if (status != Success) throw new Win32Exception((int)status, $"{api} 失败，状态码 {status}。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
        public uint PasswordAge;
        public uint Privilege;
        [MarshalAs(UnmanagedType.LPWStr)] public string? HomeDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1007
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserAdd(string? serverName, uint level, ref UserInfo1 buffer, out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserGetInfo(string? serverName, string userName, uint level, out IntPtr buffer);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserSetInfo(string? serverName, string userName, uint level, ref UserInfo1007 buffer, out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserDel(string? serverName, string userName);

    [DllImport("Netapi32.dll")]
    private static extern uint NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountName(string? systemName, string accountName, byte[]? sid,
        ref uint sidSize, StringBuilder? referencedDomainName, ref uint domainNameSize, out int use);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string userName, string? domain, string password, int logonType,
        int logonProvider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        out TokenStatistics tokenInformation, int tokenInformationLength, out int returnLength);
}

public sealed class LogonSession(SafeAccessTokenHandle token, string logonId) : IDisposable
{
    private SafeAccessTokenHandle? token = token;
    public string LogonId { get; } = logonId;
    public void Dispose() => Interlocked.Exchange(ref token, null)?.Dispose();
}
