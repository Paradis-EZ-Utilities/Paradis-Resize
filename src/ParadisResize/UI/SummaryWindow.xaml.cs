using System.Windows;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Util;

namespace ParadisResize.UI;

/// <summary>The end-of-batch summary: how many, how big before and after, what failed, where the copies are.</summary>
public partial class SummaryWindow : Window
{
    private readonly BatchSummary _summary;

    public SummaryWindow(BatchSummary summary, JobMode mode)
    {
        InitializeComponent();
        _summary = summary;

        HeadingText.Text = summary.Cancelled ? "Stopped" : "Finished";
        string verb = mode == JobMode.Resize ? "resized" : "optimized";
        string count = summary.Saved == 1 ? "1 image" : $"{summary.Saved:N0} images";
        CountText.Text = summary.Saved == 0 ? "No images were saved."
            : summary.CopiedUnchanged == 0 ? $"{count} {verb}"
            : $"{count} saved ({summary.Done:N0} {verb}, {summary.CopiedUnchanged:N0} copied unchanged because they were already small or efficient)";

        OriginalSizeText.Text = Units.FormatSize(summary.OriginalBytes);
        NewSizeText.Text = Units.FormatSize(summary.NewBytes);
        if (summary.BytesSaved >= 0)
        {
            SavedText.Text = $"{Units.FormatSize(summary.BytesSaved)} ({Units.PercentSaved(summary.OriginalBytes, summary.NewBytes)}%)";
        }
        else
        {
            SavedLabel.Text = "Larger by";
            SavedText.Text = Units.FormatSize(-summary.BytesSaved);
            SavedText.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
        }

        var problems = new List<string>();
        if (summary.Failed > 0) problems.Add($"Failed: {summary.Failed:N0}");
        if (summary.Skipped > 0) problems.Add($"Skipped: {summary.Skipped:N0}");
        if (summary.NotProcessed > 0) problems.Add($"Not processed (cancelled): {summary.NotProcessed:N0}");
        if (problems.Count > 0)
        {
            ProblemsText.Text = string.Join("   ·   ", problems) +
                                (summary.Failed + summary.Skipped > 0 ? "\nHover over an image's status in the list to see why." : "");
            ProblemsText.Visibility = Visibility.Visible;
        }

        // One output folder can be opened directly. Copies spread over a "Resized" folder in each source
        // folder can't be shown as one place, so the window says where they are instead.
        string folderName = mode == JobMode.Resize ? OutputPlanner.ResizedFolderName : OutputPlanner.OptimizedFolderName;
        if (summary.OutputFolders.Count == 1)
        {
            WhereText.Text = $"Saved in {summary.OutputFolders[0]}";
        }
        else if (summary.OutputFolders.Count > 1)
        {
            WhereText.Text = $"Saved in a “{folderName}” folder next to each original ({summary.OutputFolders.Count:N0} folders).";
            OpenFolderButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            WhereText.Visibility = Visibility.Collapsed;
            OpenFolderButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_summary.OutputFolders.Count > 0) Dialogs.RevealInExplorer(_summary.OutputFolders[0]);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
