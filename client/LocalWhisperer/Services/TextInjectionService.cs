using LocalWhisperer.Helpers;

namespace LocalWhisperer.Services;

/// <summary>
/// Injects text into whatever input field currently has focus.
/// Short texts (&lt;50 chars) use SendInput with KEYEVENTF_UNICODE so Norwegian
/// characters (æøå) work correctly. Longer texts use clipboard + Ctrl+V.
/// </summary>
public class TextInjectionService
{
    private const int ClipboardThreshold = 50;

    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (text.Length <= ClipboardThreshold)
            NativeMethods.SendUnicodeString(text);
        else
            PasteViaClipboard(text);
    }

    public void SendBackspaces(int count)
    {
        if (count <= 0) return;
        NativeMethods.SendBackspaces(count);
    }

    private static void PasteViaClipboard(string text)
    {
        // Save current clipboard, paste new text, restore original.
        // Must run on STA thread — WinUI 3 UI thread qualifies.
        var previous = TryGetClipboard();

        SetClipboard(text);
        NativeMethods.SendCtrlV();

        // Small delay so the target app has time to process the paste before
        // we restore — otherwise the restore races the paste.
        Task.Delay(150).ContinueWith(_ =>
        {
            if (previous is not null)
                SetClipboard(previous);
        });
    }

    private static string? TryGetClipboard()
    {
        try { return Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                        .GetTextAsync().AsTask().GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private static void SetClipboard(string text)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }
}
