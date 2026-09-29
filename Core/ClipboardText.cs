using System.Windows;

namespace PPObjectSearch.Core;

/// <summary>
/// Putting text on the clipboard, reliably enough to tell the user it worked.
/// </summary>
public static class ClipboardText
{
    /// <summary>
    /// Clipboard.SetText makes a single OLE attempt and throws if anything else currently holds
    /// the clipboard - clipboard history, a remote desktop session, another app mid-copy - which
    /// happens often enough that one try silently loses the copy. Retry briefly, and hand back
    /// what went wrong so the caller can say so rather than leaving it to be found at the paste.
    /// </summary>
    public static bool TryCopy(string text, out string? failure)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                failure = null;
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(30);
            }
        }

        failure = last?.Message;
        return false;
    }
}
