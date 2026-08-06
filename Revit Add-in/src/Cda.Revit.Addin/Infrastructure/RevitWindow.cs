using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Shows a WPF window owned by the Revit main window.
///
/// Without an explicit owner a modal dialog can end up behind Revit, which looks like a
/// hang: Revit is blocked waiting for a dialog the user cannot see or reach.
/// </summary>
internal static class RevitWindow
{
    /// <summary>
    /// The owner is nullable because not every caller has a UIApplication to hand — the
    /// time tracker prompts from Revit's Idling event, where the sender is not guaranteed
    /// to be one. An unowned window is worse than an owned one, but far better than no
    /// window at all, so it falls back to centre-screen and topmost rather than refusing.
    /// </summary>
    public static bool? ShowDialog(Window window, UIApplication? uiApp)
    {
        if (uiApp is not null)
        {
            new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return window.ShowDialog();
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Topmost = true;
        return window.ShowDialog();
    }
}
