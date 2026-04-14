using System.Threading.Tasks;

namespace WindowWorks.App.UI.Services
{
    public interface IDialogService
    {
        // Show a color picker initialized with optional hex color (#RRGGBB or #AARRGGBB). Returns selected color as #AARRGGBB or null if cancelled.
        string? ShowColorPicker(string? initialHex);

        // Show a shortcut capture dialog (existing ShortcutCaptureWindow) and return captured string or null
        string? ShowShortcutCapture(string? current);
    }
}
