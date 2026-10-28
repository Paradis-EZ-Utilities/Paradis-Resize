using System.Windows;
using System.Windows.Input;
using ParadisResize.Core.Processing;
using ParadisResize.Services;

namespace ParadisResize.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version} · {AppInfo.Tagline} · © 2026 D.Y. Paradis";
        LibvipsVersionText.Text = string.IsNullOrEmpty(VipsRuntime.Version)
            ? "libvips is not loaded."
            : $"libvips {VipsRuntime.Version}\n{Path.Combine(AppContext.BaseDirectory, "libvips-42.dll")}";
    }

    private void OnCollection(object sender, MouseButtonEventArgs e)
    {
        if (!Dialogs.OpenUrl(AppInfo.CollectionUrl))
        {
            CollectionUrlText.Text = AppInfo.CollectionUrl + " (copied)";
            CollectionUrlText.Visibility = Visibility.Visible;
            try { Clipboard.SetText(AppInfo.CollectionUrl); } catch { }
        }
    }

    private void OnLicenses(object sender, MouseButtonEventArgs e)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "licenses");
        Dialogs.RevealInExplorer(Directory.Exists(dir) ? dir : AppContext.BaseDirectory);
    }

    private void OnLogs(object sender, MouseButtonEventArgs e) => Dialogs.RevealInExplorer(Log.Directory);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
