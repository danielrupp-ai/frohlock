using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FrohLock.Service.Interop;

/// <summary>
/// Startet einen Prozess (den Overlay-Agent) im Kontext des interaktiv angemeldeten
/// Benutzers aus dem SYSTEM-Dienst heraus (CreateProcessAsUser in der aktiven Session).
/// Best-effort: schlägt der Start fehl, greift zusätzlich die Autostart-Aufgabe des Installers.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionLauncher
{
    public static uint ActiveConsoleSessionId => WTSGetActiveConsoleSessionId();

    /// <summary>True, wenn aktuell ein Benutzer interaktiv angemeldet ist.</summary>
    public static bool HasActiveUserSession()
    {
        var id = WTSGetActiveConsoleSessionId();
        return id != 0xFFFFFFFF;
    }

    public static bool TryLaunchInActiveSession(string exePath, string arguments = "")
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return false;

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            return false;

        IntPtr envBlock = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);

            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    TOKEN_TYPE.TokenPrimary, out dupToken))
                return false;

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
                envBlock = IntPtr.Zero;

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";

            var cmd = $"\"{exePath}\" {arguments}".TrimEnd();

            bool ok = CreateProcessAsUser(
                dupToken, null, cmd,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock, Path.GetDirectoryName(exePath),
                ref si, out PROCESS_INFORMATION pi);

            if (ok)
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            }
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ---- Win32 ----
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL impLevel,
        TOKEN_TYPE tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? appName, string cmdLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
