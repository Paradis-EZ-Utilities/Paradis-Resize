using System.Diagnostics;
using System.Windows;
using ParadisResize.Services;

namespace ParadisResize.UI;

/// <summary>One place for the handful of message boxes the app shows.</summary>
public static class Dialogs
{
    public static void Error(Window? owner, string title, string message) =>
        Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(Window? owner, string title, string message) =>
        Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>A yes/no question. Returns true for Yes.</summary>
    public static bool Confirm(Window? owner, string title, string message) =>
        Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        string caption = $"Paradis Resize — {title}";
        return owner != null && owner.IsVisible
            ? MessageBox.Show(owner, message, caption, buttons, icon)
            : MessageBox.Show(message, caption, buttons, icon);
    }

    /// <summary>Opens a file with its default program (the finished image in the user's viewer).</summary>
    public static bool OpenWithDefaultApp(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path} with its default program", ex);
            return false;
        }
    }

    /// <summary>
    /// Opens a web address in the user's default browser. Returns false (and logs why) if Windows
    /// couldn't open it, so the caller can show the address instead.
    /// </summary>
    public static bool OpenUrl(string url) => OpenWithDefaultApp(url);

    /// <summary>Opens a folder in File Explorer, selecting the file when given one.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            bool isFile = File.Exists(path);
            var psi = new ProcessStartInfo("explorer.exe")
            {
                // Windows paths can't contain quotes, so quoting here is safe.
                Arguments = isFile ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path} in Explorer", ex);
        }
    }
}
