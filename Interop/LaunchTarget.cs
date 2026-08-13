using System.IO;
using Microsoft.Win32;

namespace Deckhand;

/// <summary>
/// What a tile's "path" actually names, and where it is.
///
/// A tile's path is written the way it would be typed into a Run box — "code", a folder, a
/// URL, a full path to an exe — and every way of starting one of those needs to know which
/// it got before it can do the right thing. Both launch paths ask here first, for reasons
/// that turn out to be the same reason twice:
///
///  - <see cref="DeElevatedLauncher"/> has to name an image to CreateProcess, which does no
///    lookup of its own at all.
///  - The ordinary launch goes through ShellExecute, which does — but searches PATH before
///    App Paths, so a bare "code" finds code.cmd, VS Code's CLI shim, in preference to
///    Code.exe. Running a .cmd through ShellExecute puts a console window on the desktop,
///    and the shim holds it open with a `call` for as long as VS Code is running. Resolved
///    here instead, "code" is the program and there's no shim and no window.
///
/// The order is App Paths before PATH, which is the opposite of ShellExecute's and is the
/// point: App Paths is where an installer registers the program itself, PATH is where it
/// puts the wrapper.
/// </summary>
internal static class LaunchTarget
{
    /// <summary>Which of the three things a path turned out to be.</summary>
    internal enum Kind
    {
        /// <summary>An .exe or .com — a program that can be started as itself.</summary>
        Program,

        /// <summary>A .cmd or .bat. Running one is running cmd, whoever starts it.</summary>
        Script,

        /// <summary>
        /// A folder, a URL, a document: something whose "what opens this" lives in the
        /// shell's associations rather than in an image anyone could name.
        /// </summary>
        Shell,
    }

    internal readonly record struct Resolved(Kind Kind, string Path);

    /// <summary>
    /// What <paramref name="target"/> names, or null if nothing by that name could be
    /// found — a typo in a tile, most likely, which each caller reports its own way.
    /// </summary>
    internal static Resolved? Of(string target)
    {
        string trimmed = target.Trim();
        if (trimmed.Length == 0) return null;

        // Neither of these is a file to go looking for.
        if (UrlLauncher.IsUrl(trimmed) || Directory.Exists(trimmed))
        {
            return new Resolved(Kind.Shell, trimmed);
        }

        if (Program(trimmed) is not { } path) return null;

        string extension = Path.GetExtension(path);

        if (Is(extension, ".exe") || Is(extension, ".com"))
        {
            return new Resolved(Kind.Program, path);
        }

        if (Is(extension, ".cmd") || Is(extension, ".bat"))
        {
            return new Resolved(Kind.Script, path);
        }

        // A file that exists but isn't a program: a document, a shortcut.
        return new Resolved(Kind.Shell, path);
    }

    private static bool Is(string extension, string wanted) =>
        extension.Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full path a target names. A rooted or relative path is taken as one; a bare name
    /// is looked up the way a Run box would, App Paths first.
    /// </summary>
    private static string? Program(string target)
    {
        if (target.Contains(Path.DirectorySeparatorChar)
            || target.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(target))
        {
            return File.Exists(target) ? Full(target) : null;
        }

        return FromAppPaths(target) ?? OnPath(target);
    }

    /// <summary>
    /// The App Paths registry key, which is how "chrome" resolves from a Run box without
    /// Chrome being on PATH at all. Per-user first, as the lookup itself is ordered.
    /// </summary>
    private static string? FromAppPaths(string name)
    {
        string leaf = Path.HasExtension(name) ? name : name + ".exe";

        foreach (string root in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            string key = $@"{root}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{leaf}";

            // The default value is the full path, sometimes quoted by whoever wrote it.
            if (Registry.GetValue(key, null, null) is string value
                && value.Trim().Trim('"') is { Length: > 0 } path
                && File.Exists(path))
            {
                return Full(path);
            }
        }

        return null;
    }

    /// <summary>
    /// PATH, tried with each extension in turn for a name that doesn't carry one. .exe
    /// before .cmd, so a program shipping both is started rather than its wrapper.
    /// </summary>
    private static string? OnPath(string name)
    {
        string[] extensions = Path.HasExtension(name)
            ? new[] { "" }
            : new[] { ".exe", ".com", ".cmd", ".bat" };

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                                     .Split(Path.PathSeparator,
                                            StringSplitOptions.RemoveEmptyEntries
                                            | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory, name + extension); }
                catch (ArgumentException) { break; } // a junk PATH entry; on to the next

                if (File.Exists(candidate)) return Full(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// GetFullPath, but a path it won't accept is a miss rather than a throw — this is
    /// reached from a foreground change, where an exception would be a dialog per focus.
    /// </summary>
    private static string? Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                     or PathTooLongException)
        {
            return null;
        }
    }
}
