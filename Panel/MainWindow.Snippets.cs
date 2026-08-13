using System.Windows;

namespace Deckhand;

// Typing into whatever window is in front. Pasting borrows the clipboard and gives it
// back, typing sends the characters one at a time, and either can be followed by a key.
// The caret is always somebody else's — the panel never takes focus — which is why one
// insertion at a time is a rule here rather than a nicety.
public partial class MainWindow
{
    /// <summary>
    /// Whether a snippet is still being inserted. The awaits below leave windows a
    /// second tap can land in, and two insertions interleaved share one clipboard:
    /// the second overwrites it mid-paste of the first, and the first's restore then
    /// stomps the second's text before its Ctrl+V lands. So a tap during one is
    /// refused outright — a queue would type into whatever is focused by then.
    /// </summary>
    private bool _snippetInFlight;

    private async void InsertSnippet(SnippetEntry snippet)
    {
        if (_snippetInFlight)
        {
            _log.Add(LogKind.Warning,
                     $"\"{snippet.Label}\" ignored — still inserting the previous snippet");
            return;
        }

        _snippetInFlight = true;
        try
        {
            await InsertSnippetNow(snippet);
        }
        finally
        {
            _snippetInFlight = false;
        }
    }

    private async Task InsertSnippetNow(SnippetEntry snippet)
    {
        // {date}, {time} and {clipboard}, resolved at the tap rather than written into
        // the config — the same idea as {name} on a folder tile's command. Resolved before
        // the paste path stashes the clipboard, so {clipboard} reads what the user
        // actually copied and not the snippet mid-flight.
        string text = ExpandPlaceholders(snippet.Text);

        if (snippet.Method == InsertMethod.Type)
        {
            // Character-by-character Unicode input; no clipboard involved.
            if (!NativeMethods.TypeText(text))
            {
                WarnInputBlocked($"insert \"{snippet.Label}\"");
                return;
            }

            // The text itself is never logged: a snippet can be an address, a password
            // reset link, anything. What it was and where it went is what's useful.
            _log.Add(LogKind.Action, $"typed \"{snippet.Label}\" into {_contextText}");
            await PressSnippetKeys(snippet);
            return;
        }

        // Paste method: stash the user's clipboard, paste ours, restore theirs.
        IDataObject? saved = null;
        try
        {
            saved = CaptureClipboard();
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard is occasionally locked by another process; fall back to typing.
            if (NativeMethods.TypeText(text))
            {
                _log.Add(LogKind.Action,
                         $"typed \"{snippet.Label}\" into {_contextText} — the clipboard was busy");
                await PressSnippetKeys(snippet);
            }
            else
            {
                WarnInputBlocked($"insert \"{snippet.Label}\"");
            }
            return;
        }

        await Task.Delay(50);          // let the clipboard settle
        bool pasted = NativeMethods.SendCtrlV();
        await Task.Delay(250);         // let the target app read it before we restore

        if (saved != null)
        {
            try { Clipboard.SetDataObject(saved, copy: true); } catch { /* best effort */ }
        }

        // Warn after restoring, so a blocked paste doesn't also leave the clipboard clobbered.
        if (!pasted)
        {
            WarnInputBlocked($"paste \"{snippet.Label}\"");
            return;
        }

        _log.Add(LogKind.Action, $"pasted \"{snippet.Label}\" into {_contextText}");
        await PressSnippetKeys(snippet);
    }

    /// <summary>
    /// The placeholders a snippet may carry, resolved at the moment of the tap.
    /// {date} and {time} are in the local short formats, so they read the way the rest
    /// of the machine writes them; {clipboard} is whatever text is on the clipboard,
    /// and comes out empty when what's there isn't text.
    /// </summary>
    private static string ExpandPlaceholders(string text)
    {
        if (!text.Contains('{')) return text;

        text = text.Replace("{date}", DateTime.Now.ToString("d"))
                   .Replace("{time}", DateTime.Now.ToString("t"));

        if (text.Contains("{clipboard}"))
        {
            string clip = "";
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { /* locked by another process; empty is the honest answer */ }
            text = text.Replace("{clipboard}", clip);
        }

        return text;
    }

    /// <summary>
    /// A copy of the clipboard that survives the clipboard changing — what
    /// <see cref="Clipboard.GetDataObject"/> returns is a window onto the clipboard
    /// itself, not a snapshot, so restoring it after SetText would restore the snippet.
    /// Formats are taken as-is, without conversion, so an image or a set of copied files
    /// goes back exactly as it was; one format failing to read (a delayed render the
    /// source app no longer honours) drops that format rather than the lot. Null for an
    /// empty clipboard, which needs nothing restored.
    /// </summary>
    private static IDataObject? CaptureClipboard()
    {
        if (Clipboard.GetDataObject() is not { } current) return null;

        var copy = new DataObject();
        bool any = false;

        foreach (string format in current.GetFormats(autoConvert: false))
        {
            try
            {
                if (current.GetData(format, autoConvert: false) is { } data)
                {
                    copy.SetData(format, data, autoConvert: false);
                    any = true;
                }
            }
            catch
            {
                // A format the source app promised but couldn't deliver; keep the rest.
            }
        }

        return any ? copy : null;
    }

    /// <summary>
    /// The keys a snippet asks for once its text is in, in the order it named them.
    /// This is how a tile reaches the editor's own snippets: the text is a prefix like
    /// "rafce", and the Tab after it is what makes VS Code expand that prefix into a
    /// component with its name selected in all three places. Inserted text can't select
    /// anything by itself — only the editor can.
    /// </summary>
    private async Task PressSnippetKeys(SnippetEntry snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet.Keys)) return;

        foreach (string name in snippet.Keys.Split(new[] { ' ', ',' },
                                                   StringSplitOptions.RemoveEmptyEntries))
        {
            // TryParse also takes the numbers behind the names, hence the second check.
            if (!Enum.TryParse(name, ignoreCase: true, out KeyPress key) || !Enum.IsDefined(key))
            {
                _log.Add(LogKind.Warning, $"\"{snippet.Label}\" asks for the key \"{name}\", "
                                          + "which isn't one of tab, enter or escape");
                continue;
            }

            // The text arrives as input events and the editor works out what Tab means
            // from the document those events produced. They're delivered in order, but
            // the editor having caught up with them is a separate thing, and a Tab that
            // arrives too early only indents.
            await Task.Delay(60);

            if (!NativeMethods.SendKey(key))
            {
                WarnInputBlocked($"press {key} after \"{snippet.Label}\"");
                return;
            }

            _log.Add(LogKind.Action, $"pressed {key} after \"{snippet.Label}\"");
        }
    }
}
