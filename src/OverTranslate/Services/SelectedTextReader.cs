using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;
using Clipboard = System.Windows.Clipboard;

namespace OverTranslate.Services;

/// <summary>
/// Whatever the user had selected in the application they were reading, at the moment a shortcut
/// was pressed.
/// </summary>
/// <remarks>
/// Windows has no way to ask another process what it has selected. UI Automation's TextPattern is
/// the closest thing and it answers for a minority of applications — not Chrome's page content, not
/// most games, not anything drawing its own text — so the general mechanism is the one every other
/// dictionary tool on this platform uses: synthesise the copy shortcut and read the clipboard.
///
/// That means borrowing something that belongs to the user, so most of this file is about giving it
/// back: the clipboard's previous contents are snapshotted before the copy and restored after it,
/// and a failure anywhere leaves the caller with an empty string rather than an exception. Coming
/// back with nothing is not an error condition here — the popup opens with an empty box and the
/// user types, which is exactly what it does when nothing was selected.
/// </remarks>
internal static class SelectedTextReader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Keeps the managed data object alive while this process owns the restored clipboard.
    /// </summary>
    private static System.Windows.DataObject? _restoredClipboardOwner;

    /// <summary>
    /// How long the foreground application is given to start answering the copy.
    /// </summary>
    /// <remarks>
    /// An empty selection produces no clipboard event at all. Keeping this separate from the longer
    /// completion timeout lets that common path open the popup promptly without giving up on a copy
    /// that has already started publishing its formats.
    /// </remarks>
    private static readonly TimeSpan CopyStartTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>How long an observed clipboard update may take to publish readable text.</summary>
    private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromMilliseconds(320);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>How long Ctrl+C stays physically observable to applications that poll per frame.</summary>
    private static readonly TimeSpan CopyChordHold = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The most text this will carry into the popup.
    /// </summary>
    /// <remarks>
    /// 取詞翻譯 is a single-line box over someone else's window, not a document translator: a
    /// selection longer than this was a drag that got away, or a Ctrl+A, and pouring it into a
    /// one-line field gives the user something they cannot read and a translation they did not ask
    /// for. 文字翻譯 is the window for that, and it is one shortcut away.
    /// </remarks>
    public const int MaxLength = 1000;

    /// <summary>Runs of whitespace, newlines included.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Copies the current selection out of the foreground application, restoring the clipboard.
    /// </summary>
    /// <remarks>
    /// Must be awaited before the popup is shown. Showing it first takes the foreground away from
    /// the application holding the selection, and the copy would then be sent to a text box that has
    /// nothing in it.
    /// </remarks>
    public static async Task<string> ReadAsync()
    {
        object? snapshot;
        uint before;

        try
        {
            snapshot = Snapshot();
            before = GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            // The clipboard is one system-wide resource and any process can have it locked. With no
            // snapshot there is nothing to put back, so the copy is not attempted at all.
            Log.Debug(ex, "Could not snapshot the clipboard; 取詞翻譯 opens with an empty box");
            return "";
        }

        try
        {
            await SendCopy();

            var maxStartPolls = (int)Math.Ceiling(
                CopyStartTimeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
            var maxCompletionPolls = (int)Math.Ceiling(
                CopyCompletionTimeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
            return await PollForCopiedTextAsync(
                before,
                GetClipboardSequenceNumber,
                ReadClipboardTextIfAvailable,
                () => Task.Delay(PollInterval),
                maxStartPolls,
                maxCompletionPolls);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the selection failed; 取詞翻譯 opens with an empty box");
            return "";
        }
        finally
        {
            Restore(snapshot);
        }
    }

    /// <summary>Keeps an injected copy chord down long enough to cross a frame boundary.</summary>
    internal static async Task HoldCopyChordAsync(
        Action press,
        Func<Task> hold,
        Action release)
    {
        press();
        try
        {
            await hold();
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// Waits for a copy operation to publish readable text after changing the clipboard.
    /// </summary>
    /// <remarks>
    /// A clipboard sequence change means that an update started, not that every format is ready.
    /// Applications commonly empty the clipboard before publishing text, and another process can
    /// briefly hold it between those steps. Returning on that first change mistakes an in-progress
    /// copy for an empty selection and restores the old clipboard over the result that is still
    /// arriving.
    /// </remarks>
    internal static async Task<string> PollForCopiedTextAsync(
        uint before,
        Func<uint> getSequenceNumber,
        Func<string?> readText,
        Func<Task> delay,
        int maxStartPolls,
        int maxCompletionPolls)
    {
        var changed = false;
        var startPolls = 0;
        var completionPolls = 0;

        while (true)
        {
            changed |= getSequenceNumber() != before;

            if (changed && readText() is { } text)
                return Sanitize(text);

            if (changed)
            {
                if (completionPolls >= maxCompletionPolls)
                {
                    Log.Debug("Copy changed the clipboard but did not publish readable text before timeout");
                    return "";
                }
                completionPolls++;
            }
            else
            {
                if (startPolls >= maxStartPolls)
                {
                    Log.Debug("Copy did not change the clipboard before timeout");
                    return "";
                }
                startPolls++;
            }

            await delay();
        }
    }

    /// <summary>Returns null while copied text is not published or cannot be read yet.</summary>
    private static string? ReadClipboardTextIfAvailable()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            // Clipboard ownership changes asynchronously. A lock here is a state to poll through,
            // not the final answer to the user's copy request.
            Log.Trace(ex, "Copied text is not readable yet");
            return null;
        }
    }

    /// <summary>
    /// Trims the copied text down to one line of at most <see cref="MaxLength"/> characters.
    /// </summary>
    /// <remarks>
    /// Whitespace is collapsed rather than kept because a selection dragged across a web page or a
    /// PDF arrives hard-wrapped at whatever width it was laid out to, and those line breaks belong
    /// to the page rather than to the sentence. Left in, they reach the translation engine as
    /// sentence boundaries that are not there.
    /// </remarks>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = Whitespace.Replace(raw, " ").Trim();
        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// Everything the clipboard holds, in a form <see cref="Restore"/> can put back.
    /// </summary>
    /// <remarks>
    /// Format by format rather than by handing the live data object back later: the object the
    /// clipboard gives out is owned by whichever process put it there, and by the time it would be
    /// restored that process may have exited and taken its handles with it.
    ///
    /// A format that will not come out is skipped rather than failing the snapshot. The alternative
    /// is to abandon the read whenever anything exotic is on the clipboard, which in practice means
    /// abandoning it for anyone who has recently copied out of Office.
    /// </remarks>
    private static object? Snapshot()
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return null;

        var copy = new System.Windows.DataObject();
        var kept = 0;

        foreach (var format in source.GetFormats())
        {
            try
            {
                var data = source.GetData(format);
                if (data is null) continue;
                copy.SetData(format, data);
                kept++;
            }
            catch
            {
                // See the remarks: one unreadable format is not a reason to lose the others.
            }
        }

        return kept > 0 ? copy : null;
    }

    /// <remarks>
    /// An empty snapshot clears the clipboard rather than leaving the copied selection on it. The
    /// user never asked for that text to be on their clipboard, and leaving it there is a surprise
    /// they would find later, in whatever they next paste into.
    /// </remarks>
    private static void Restore(object? snapshot)
    {
        try
        {
            if (snapshot is System.Windows.DataObject data)
            {
                // copy:true calls OleFlushClipboard after publishing every format. Some third-party
                // clipboard data providers make that native call access-violate inside ole32.dll;
                // a corrupted-state exception cannot be caught here and terminates the process.
                // Keep the data object alive instead so OLE can request its formats lazily without
                // taking that unsafe flush path.
                Clipboard.SetDataObject(data, copy: false);
                _restoredClipboardOwner = data;
            }
            else
            {
                Clipboard.Clear();
                _restoredClipboardOwner = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not restore the clipboard after reading the selection");
        }
    }

    /// <summary>
    /// Sends Ctrl+C to whatever has the foreground.
    /// </summary>
    /// <remarks>
    /// The modifiers are released first so the injected chord is a plain Ctrl+C. Ctrl and C then
    /// remain down briefly rather than being pressed and released in one SendInput batch: ordinary
    /// controls consume every key event, but a game may sample keyboard state once per frame and
    /// miss a chord whose complete lifetime falls between two samples.
    ///
    /// The user's own key releases arrive afterwards as repeats of an up state, which every
    /// application already tolerates.
    /// </remarks>
    private static Task SendCopy()
    {
        var press = new List<INPUT>();

        foreach (var modifier in new ushort[] { VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN, VK_CONTROL })
            press.Add(Key(modifier, up: true));

        press.Add(Key(VK_CONTROL, up: false));
        press.Add(Key(VK_C, up: false));

        return HoldCopyChordAsync(
            () => InjectKeys(press.ToArray()),
            () => Task.Delay(CopyChordHold),
            () => InjectKeys([Key(VK_C, up: true), Key(VK_CONTROL, up: true)]));
    }

    private static void InjectKeys(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
            Log.Debug("SendInput accepted {SentCount} of {RequestedCount} keyboard events", sent, inputs.Length);
    }

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_C = 0x43;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static INPUT Key(ushort virtualKey, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;

        // SendInput rejects a structure that is not the size it expects, and the size it expects is
        // that of the widest member of the union — so MOUSEINPUT has to be declared even though
        // nothing here sends a mouse event.
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>
    /// Counts every change to the clipboard, system-wide.
    /// </summary>
    /// <remarks>
    /// Polled rather than reading the text back and comparing, because the two cases that matter are
    /// indistinguishable by content: nothing was selected, and the selection happened to be the text
    /// already on the clipboard. The sequence number separates them.
    /// </remarks>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
