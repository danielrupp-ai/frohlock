using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FrohLock.Service.Interop;

/// <summary>
/// Liest aus dem Dienst (LocalSystem) heraus, welches Programm hinter einer Prozess-Kennung steckt:
/// voller Pfad der Programmdatei, Windows-Sitzung, Startzeit und Befehlszeile. Grundlage dafür, dass der
/// DIENST selbst prüft, ob wirklich das erlaubte Ausnahme-Programm vorn ist (der Agent meldet nur die Kennung).
/// Jeder Fehler -> null (= nicht erlaubt).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessInfo
{
    public sealed record Snapshot(int Pid, string ImagePath, int SessionId, DateTime StartUtc, string? CommandLine);

    public static Snapshot? Get(int pid)
    {
        if (pid <= 4) return null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            string path = sb.ToString(0, size);
            if (!ProcessIdToSessionId((uint)pid, out uint session)) return null;
            if (!GetProcessTimes(h, out long created, out _, out _, out _)) return null;
            return new Snapshot(pid, path, (int)session, DateTime.FromFileTimeUtc(created), CommandLine(h));
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Befehlszeile (ProcessCommandLineInformation, ab Windows 8.1). Null, wenn nicht lesbar.</summary>
    private static string? CommandLine(IntPtr h)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            int len = 0;
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out len);
            if (len <= 0 || len > 1 << 20) len = 8192;
            buf = Marshal.AllocHGlobal(len);
            int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out _);
            if (status != 0) return null;
            int bytes = (ushort)Marshal.ReadInt16(buf);               // UNICODE_STRING.Length
            IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);        // UNICODE_STRING.Buffer (nach Ausrichtung)
            if (str == IntPtr.Zero) return bytes == 0 ? "" : null;
            return Marshal.PtrToStringUni(str, bytes / 2);
        }
        catch { return null; }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// NTFS-„ChangeTime“ der Datei (ändert sich auch bei Umbenennen/Verschieben) – fürs Erkennen eines
    /// Austauschs der Programmdatei nach dem Start. Null, wenn nicht lesbar.
    /// </summary>
    public static DateTime? ChangeTimeUtc(string path)
    {
        try
        {
            using SafeFileHandle fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandleEx(fh, FileBasicInfo, out FILE_BASIC_INFO info, Marshal.SizeOf<FILE_BASIC_INFO>()))
                return null;
            return info.ChangeTime > 0 ? DateTime.FromFileTimeUtc(info.ChangeTime) : null;
        }
        catch { return null; }
    }

    /// <summary>Prozess-Kennung des Programms am anderen Ende der Pipe (-1 = unbekannt).</summary>
    public static int PipeClientPid(SafePipeHandle pipe)
    {
        try { return GetNamedPipeClientProcessId(pipe, out uint pid) ? (int)pid : -1; }
        catch { return -1; }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int FileBasicInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_BASIC_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
