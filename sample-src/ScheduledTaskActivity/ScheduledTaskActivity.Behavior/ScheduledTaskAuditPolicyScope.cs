using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScheduledTaskActivity;

/// <summary>
/// 仅在非 RPC 的 Windows 安全审计子测试期间启用“其他对象访问事件”的成功审核，
/// 并在行为完成后恢复该子类别原有位掩码。使用原生 Audit Policy API，避免依赖
/// auditpol.exe 的本地化文本输出，也不会覆盖其他审核子类别。
/// </summary>
internal sealed class ScheduledTaskAuditPolicyScope : IDisposable
{
    public static readonly Guid OtherObjectAccessEvents = new("0CCE9227-69AE-11D9-BED3-505054503030");
    private const uint AuditSuccess = 0x1;

    private bool restored;
    private readonly TokenPrivilegeScope privilegeScope;

    private ScheduledTaskAuditPolicyScope(uint before, uint active, bool changed, TokenPrivilegeScope privilegeScope)
    {
        Before = before;
        Active = active;
        Changed = changed;
        this.privilegeScope = privilegeScope;
    }

    public uint Before { get; }
    public uint Active { get; }
    public bool Changed { get; }
    public bool SuccessEnabled => (Active & AuditSuccess) != 0;
    public uint? Restored { get; private set; }
    public bool RestoreSucceeded { get; private set; }

    public static ScheduledTaskAuditPolicyScope EnableSuccess()
    {
        var privilegeScope = TokenPrivilegeScope.Enable("SeSecurityPrivilege");
        try
        {
            var before = Query();
            var active = before | AuditSuccess;
            var changed = active != before;
            if (changed) Set(active, "启用计划任务成功审核");
            var verified = Query();
            if ((verified & AuditSuccess) == 0)
                throw new InvalidOperationException("已请求启用‘其他对象访问事件’成功审核，但系统返回的策略仍未启用。请使用管理员权限运行。");
            return new ScheduledTaskAuditPolicyScope(before, verified, changed, privilegeScope);
        }
        catch
        {
            privilegeScope.Dispose();
            throw;
        }
    }

    public void Restore()
    {
        if (restored) return;
        if (Changed) Set(Before, "恢复计划任务审核策略");
        Restored = Query();
        RestoreSucceeded = Restored == Before;
        restored = true;
        if (!RestoreSucceeded)
            throw new InvalidOperationException($"计划任务审核策略未恢复到原值：期望 0x{Before:X}，实际 0x{Restored:X}。");
    }

    public void Dispose()
    {
        try
        {
            if (!restored) Restore();
        }
        finally { privilegeScope.Dispose(); }
    }

    private static uint Query()
    {
        var categories = new[] { OtherObjectAccessEvents };
        if (!AuditQuerySystemPolicy(categories, 1, out var buffer))
            throw Win32("读取‘其他对象访问事件’审核策略");
        try
        {
            if (buffer == IntPtr.Zero) throw new InvalidOperationException("审核策略 API 返回了空缓冲区。");
            return Marshal.PtrToStructure<AuditPolicyInformation>(buffer).AuditingInformation;
        }
        finally
        {
            if (buffer != IntPtr.Zero) AuditFree(buffer);
        }
    }

    private static void Set(uint value, string operation)
    {
        var policies = new[]
        {
            new AuditPolicyInformation
            {
                AuditSubCategoryGuid = OtherObjectAccessEvents,
                AuditingInformation = value,
                AuditCategoryGuid = Guid.Empty,
            },
        };
        if (!AuditSetSystemPolicy(policies, 1)) throw Win32(operation);
    }

    private static Win32Exception Win32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}失败（Win32 {error}）。计划任务安全审计子测试需要管理员权限。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuditPolicyInformation
    {
        public Guid AuditSubCategoryGuid;
        public uint AuditingInformation;
        public Guid AuditCategoryGuid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuditQuerySystemPolicy(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] Guid[] pSubCategoryGuids,
        uint policyCount,
        out IntPtr ppAuditPolicy);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuditSetSystemPolicy(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] AuditPolicyInformation[] pAuditPolicy,
        uint policyCount);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    private sealed class TokenPrivilegeScope : IDisposable
    {
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x00000002;
        private const int ErrorNotAllAssigned = 1300;

        private IntPtr token;
        private readonly TokenPrivileges previous;

        private TokenPrivilegeScope(IntPtr token, TokenPrivileges previous)
        {
            this.token = token;
            this.previous = previous;
        }

        public static TokenPrivilegeScope Enable(string privilegeName)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
                throw Win32("打开当前进程令牌");
            try
            {
                if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                    throw Win32($"查询 {privilegeName}");
                var requested = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled,
                };
                if (!AdjustTokenPrivilegesWithPrevious(token, false, ref requested,
                        (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                    throw Win32($"启用 {privilegeName}");
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotAllAssigned)
                    throw new Win32Exception(error, $"当前令牌没有 {privilegeName}。请使用管理员权限启动平台。");
                return new TokenPrivilegeScope(token, previous);
            }
            catch
            {
                CloseHandle(token);
                throw;
            }
        }

        public void Dispose()
        {
            if (token == IntPtr.Zero) return;
            var restore = previous;
            _ = AdjustTokenPrivileges(token, false, ref restore, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
            token = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivilegesWithPrevious(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TokenPrivileges newState, uint bufferLength, out TokenPrivileges previousState, out uint returnLength);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
    }
}
