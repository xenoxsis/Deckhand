using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deckhand;

/// <summary>
/// Starts a process at the logged-on user's normal integrity level, even though the
/// dashboard itself runs elevated.
///
/// Children normally inherit their parent's token, so an elevated dashboard would
/// launch an elevated VS Code — which in a GVFS working tree can leave files that
/// later trip up non-elevated git operations. To avoid that we borrow Explorer's
/// token (Explorer always runs as the user at Medium) and start the process with it.
///
/// CreateProcessW takes an image and a command line, and does none of what makes a tile
/// work: no PATH lookup, no App Paths, no file associations, no notion of a folder or a
/// URL. That used to be papered over by starting `cmd /c start /b "" "&lt;target&gt;" &lt;args&gt;`,
/// which does all of it — and re-reads the whole line as shell syntax while it's at it. A
/// path is not shell syntax: a folder honestly named "R&amp;D" was split at the ampersand and
/// never opened, and anything that could write a tile could hide a second command in one.
///
/// So the target is resolved here instead, and each shape is started as itself:
///
///  - **A program** — an .exe at the path given, on PATH, or under App Paths — is named to
///    CreateProcess directly. Nothing re-reads it, and its arguments reach it the way they
///    were written instead of being expanded by a shell on the way past.
///  - **A folder, a URL, a document** goes to explorer.exe as a single argument. Explorer
///    is the shell for all three, and unlike cmd it's a program we can name — so the target
///    travels as one argv entry rather than as a line of text to be parsed again.
///  - **A batch shim** — "code" on PATH is code.cmd, which runs the real Code.exe — is the
///    one shape that can't get away from a shell, since running a .cmd *is* running cmd.
///    That one keeps `cmd /c`, with CREATE_NO_WINDOW below doing what `start /b` used to:
///    the shim inherits a console that doesn't exist, so the VS Code tile doesn't put a
///    stray terminal on the desktop. Its pieces are checked for shell syntax rather than
///    escaped — see <see cref="ShellSyntax"/>.
///
/// A tile that can't be resolved to any of those returns false like any other failure, and
/// the caller's ordinary launch reports it — where before it became a cmd line that failed
/// silently inside a window nobody could see.
/// </summary>
public static class DeElevatedLauncher
{
    /// <summary>
    /// Why the last <see cref="TryStart"/> gave up, or null after a success. Without
    /// this a marshalling mistake would look identical to a missing privilege, since
    /// both just fall back to an ordinary launch.
    /// </summary>
    public static string? LastFailure { get; private set; }

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    /// <summary>
    /// Returns false if de-elevation isn't possible — most commonly because the
    /// dashboard isn't elevated, so it holds no SeImpersonatePrivilege. Callers
    /// should fall back to an ordinary launch.
    /// </summary>
    public static bool TryStart(string target, string? args)
    {
        LastFailure = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            LastFailure = "empty target";
            return false;
        }

        IntPtr shellToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr shellProcess = IntPtr.Zero;

        try
        {
            // Before the token work, so a target that can't be started fails cheaply and
            // says why rather than after three privileged calls have succeeded.
            if (Plan(target, args) is not { } launch) return false;

            IntPtr shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                LastFailure = "no shell window (Explorer not running)";
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(shellWindow, out uint shellPid);
            if (shellPid == 0)
            {
                LastFailure = "could not identify the Explorer process";
                return false;
            }

            shellProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
            if (shellProcess == IntPtr.Zero)
            {
                LastFailure = $"OpenProcess failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE, out shellToken))
            {
                LastFailure = $"OpenProcessToken failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!DuplicateTokenEx(shellToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                                  SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                LastFailure = $"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

            // wShowWindow is only read when dwFlags says so, which is why setting it alone
            // would do nothing at all.
            if (launch.Hide)
            {
                startupInfo.dwFlags = STARTF_USESHOWWINDOW;
                startupInfo.wShowWindow = SW_HIDE;
            }

            // The image is named as well as written into the command line, so which program
            // runs is settled here and not by anything parsing that line.
            bool started = CreateProcessWithTokenW(
                primaryToken, 0, launch.Program, launch.CommandLine,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                IntPtr.Zero, null, ref startupInfo, out PROCESS_INFORMATION processInfo);

            if (started)
            {
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
            }
            else
            {
                // 1314 is ERROR_PRIVILEGE_NOT_HELD: the dashboard isn't elevated, so
                // it holds no SeImpersonatePrivilege and there is nothing to drop from.
                LastFailure = $"CreateProcessWithTokenW failed: {Marshal.GetLastWin32Error()}";
            }

            return started;
        }
        catch (Exception ex)
        {
            LastFailure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (shellProcess != IntPtr.Zero) CloseHandle(shellProcess);
        }
    }

    // ---- Working out what to start -----------------------------------------

    /// <summary>
    /// The image to run, the command line to run it with, and whether to start it with its
    /// window hidden — which is only ever the batch shim, since SW_HIDE reaches a GUI
    /// program as its nCmdShow and one that honours it would come up invisible.
    /// </summary>
    private readonly record struct Launch(string Program, string CommandLine, bool Hide);

    /// <summary>
    /// Characters cmd reads as syntax rather than as text. Only the batch-shim shape reaches
    /// a shell at all, so this is only consulted there — and it refuses rather than escaping,
    /// because cmd's quoting has too many exceptions to be worth being clever with: ^ means
    /// one thing inside quotes and another outside it, and % expands on a schedule of its
    /// own. A tile that trips this falls back to the caller's ordinary launch, which passes
    /// its arguments as a field rather than as a line and so never had the problem — at the
    /// cost of running elevated, which <see cref="LastFailure"/> is there to explain.
    /// </summary>
    private static readonly char[] ShellSyntax =
        { '"', '&', '|', '<', '>', '^', '%', '\r', '\n' };

    /// <summary>
    /// How to start <paramref name="target"/>, or null with <see cref="LastFailure"/> set.
    /// See the notes on the class for the three shapes and why they differ.
    /// </summary>
    private static Launch? Plan(string target, string? args)
    {
        string trimmed = target.Trim();

        // Every path below quotes the target, and a Windows path can't contain a quote in
        // the first place — so one here is malformed input rather than a case to support.
        if (trimmed.Contains('"'))
        {
            LastFailure = "the target contains a quote";
            return null;
        }

        if (LaunchTarget.Of(trimmed) is not { } resolved)
        {
            LastFailure = $"couldn't find {trimmed} at that path, on PATH or under App Paths";
            return null;
        }

        switch (resolved.Kind)
        {
            case LaunchTarget.Kind.Program:
                // Arguments are appended as written. With no shell in the way nothing
                // expands them, so they arrive at the program exactly as the tile spelt
                // them — which is both safer and more predictable than what cmd was doing
                // to them.
                return new Launch(resolved.Path, Quote(resolved.Path) + Tail(args), Hide: false);

            case LaunchTarget.Kind.Script:
                if (resolved.Path.IndexOfAny(ShellSyntax) >= 0
                    || args?.IndexOfAny(ShellSyntax) >= 0)
                {
                    LastFailure = $"{Path.GetFileName(resolved.Path)} is a batch file, which "
                                  + "has to be run by cmd, and the path or arguments contain "
                                  + "characters cmd would read as commands";
                    return null;
                }

                // cmd named outright rather than left to be found, so what runs it isn't up
                // to whatever PATH happens to say. Hidden, because CREATE_NO_WINDOW is not
                // reliably honoured by CreateProcessWithTokenW — a console it opens anyway
                // is a window nobody asked for that outlives the launch.
                string shell =
                    Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
                        ? comSpec
                        : Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "cmd.exe");

                return new Launch(shell,
                                  $"{Quote(shell)} /c {Quote(resolved.Path)}{Tail(args)}",
                                  Hide: true);

            default:
                // A folder, a URL, a document. The shell knows what opens it and we don't.
                return Explorer(resolved.Path);
        }
    }

    /// <summary>
    /// Explorer, given the target as one argument. It's the shell for a folder, a URL and a
    /// document alike, and being a real image means the target is an argv entry rather than
    /// a line something reads looking for syntax.
    /// </summary>
    private static Launch Explorer(string target)
    {
        string explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        return new Launch(explorer, $"{Quote(explorer)} {Quote(target)}", Hide: false);
    }

    /// <summary>The arguments as a command-line tail, or nothing at all.</summary>
    private static string Tail(string? args) =>
        string.IsNullOrWhiteSpace(args) ? "" : " " + args.Trim();

    private static string Quote(string value) => $"\"{value}\"";

    // ---- Interop -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
        public int dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint access,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
        string? applicationName, string commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);
}
