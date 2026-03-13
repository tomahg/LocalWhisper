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
        var previous = NativeMethods.GetClipboardText();
        NativeMethods.SetClipboardText(text);
        NativeMethods.SendCtrlV();

        // Restore the original clipboard after a short delay so the target app
        // has time to process the paste before we overwrite it again.
        Task.Delay(200).ContinueWith(_ =>
        {
            if (previous is not null)
                NativeMethods.SetClipboardText(previous);
        });
    }
}
