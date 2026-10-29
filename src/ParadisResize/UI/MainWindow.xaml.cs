using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;
using ParadisResize.Core.Util;
using ParadisResize.Services;

namespace ParadisResize.UI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueItem> _items = [];
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Channel<QueueItem> _toRead = Channel.CreateUnbounded<QueueItem>();
    private string? _customFolder;
    private CancellationTokenSource? _batchCts;
    private Task? _batchTask;
    private bool _loading = true;
    private bool _closeAfterCancel;
    private int _scansRunning;

    // The list can hold thousands of images; counting them on every change would make adding a big
    // folder slow, so changes only mark the summary stale and a timer refreshes it a few times a second.
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private bool _refreshPending;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        VersionFooterText.Text = $"v{AppInfo.Version}";
        QueueList.ItemsSource = _items;
        _refreshTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => RefreshIfPending(), Dispatcher);
        _items.CollectionChanged += (_, _) =>
        {
            bool any = _items.Count > 0;
            EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            QueuePanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ScheduleRefresh();
        };

        // Restore the remembered choices.
        (_settings.Mode == JobMode.Optimize ? OptimizeModeRadio : ResizeModeRadio).IsChecked = true;
        (_settings.ResizeBy == ResizeBy.Percentage ? ByPercentRadio : ByDimensionsRadio).IsChecked = true;
        WidthBox.Text = _settings.Width?.ToString() ?? "";
        HeightBox.Text = _settings.Height?.ToString() ?? "";
        PercentBox.Text = _settings.Percent.ToString("0.##");
        KeepProportionsCheck.IsChecked = _settings.KeepProportions;
        DontEnlargeCheck.IsChecked = _settings.DontEnlarge;
        ConversionCheck.IsChecked = _settings.AllowFormatConversion;
        FormatCombo.SelectedIndex = (int)_settings.OutputFormat;
        SubfoldersCheck.IsChecked = SubfoldersCheckEmpty.IsChecked = _settings.IncludeSubfolders;

        _loading = false;
        _ = Task.Run(ReadHeadersLoop);
        UpdateQueueView();
        UpdateSettingsView();
    }

    // ======================== Adding images ========================

    /// <summary>Files or folders given on the command line (e.g. dropped onto ParadisResize.exe).</summary>
    public void LoadFromCommandLine(IEnumerable<string> paths) => AddPaths(paths.ToArray());

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add images",
            Filter = SupportedFormats.FileDialogFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Add a folder of images", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames);
    }

    private void OnSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = ((CheckBox)sender).IsChecked == true;
        _loading = true;
        SubfoldersCheck.IsChecked = SubfoldersCheckEmpty.IsChecked = on;
        _loading = false;
        _settings.IncludeSubfolders = on;
    }

    /// <summary>
    /// Expands files and folders on a background thread (a folder may hold thousands of images) and adds
    /// them to the list in batches. Headers are then read one by one in the background.
    /// </summary>
    private async void AddPaths(string[] paths)
    {
        if (paths.Length == 0 || IsRunning) return;
        bool subfolders = SubfoldersCheck.IsChecked == true;
        var scanner = new InputScanner(subfolders, _customFolder, _items.Select(i => i.Path));
        Log.Info($"Adding {paths.Length} dropped/chosen path(s), subfolders {(subfolders ? "on" : "off")}: {string.Join(" | ", paths.Take(5))}{(paths.Length > 5 ? " | …" : "")}");

        _scansRunning++;
        UpdateQueueView();
        int added = 0;
        try
        {
            var progress = new Progress<List<InputFile>>(batch =>
            {
                foreach (var f in batch)
                {
                    var item = new QueueItem(f.Path, f.RelativeFolder);
                    _items.Add(item);
                    _toRead.Writer.TryWrite(item);
                }
                added += batch.Count;
            });
            await Task.Run(() =>
            {
                var batch = new List<InputFile>();
                foreach (var f in scanner.Expand(paths))
                {
                    batch.Add(f);
                    if (batch.Count >= 250)
                    {
                        ((IProgress<List<InputFile>>)progress).Report(batch);
                        batch = [];
                    }
                }
                if (batch.Count > 0) ((IProgress<List<InputFile>>)progress).Report(batch);
            });
            // Let the last Progress callback run before reporting.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Error("Adding files failed", ex);
            Dialogs.Error(this, "Couldn't add those files", "Something went wrong while looking for images. The log has the details.");
        }
        finally
        {
            _scansRunning--;
            UpdateQueueView();
        }

        Log.Info($"Added {added} image(s); {scanner.SkippedNotImages} other file(s) ignored" +
                 (scanner.FoundByContent.Count > 0 ? $"; {scanner.FoundByContent.Count} added by their contents (extension doesn't match)" : "") +
                 (scanner.SkippedOutputFolders.Count > 0 ? $"; skipped output folder(s): {string.Join(" | ", scanner.SkippedOutputFolders)}" : ""));
        scanner.LogDetails(Log.Info);
        if (added == 0)
        {
            Dialogs.Info(this, "No images found",
                scanner.SkippedNotImages > 0
                    ? $"None of those files are {SupportedFormats.ReadableNames} images. The log lists each file and why it was left out."
                    : subfolders || paths.All(File.Exists)
                        ? $"No new {SupportedFormats.ReadableNames} images were found there."
                        : $"No new {SupportedFormats.ReadableNames} images were found in that folder. To include the folders inside it, tick \"Include subfolders\" and add it again.");
        }
    }

    /// <summary>Reads image headers one at a time, in the background, for the list's Format/Dimensions/Size columns.</summary>
    private async Task ReadHeadersLoop()
    {
        try
        {
            VipsRuntime.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("libvips failed to load", ex);
            await Dispatcher.InvokeAsync(() => Dialogs.Error(this, "Paradis Resize can't start its image engine",
                "The image library (libvips-42.dll) couldn't be loaded. Make sure it is in the same folder as ParadisResize.exe; unzipping the download again should fix this.\n\n" +
                $"Technical details are in:\n{Log.Directory}"));
            return;
        }

        await foreach (var item in _toRead.Reader.ReadAllAsync())
        {
            ImageInfo? info = null;
            ImageJobException? error = null;
            try
            {
                info = ImageProbe.Read(item.Path);
                Log.Info($"Detected {item.Path}: {info.Describe()}");
            }
            catch (ImageJobException ex)
            {
                error = ex;
                Log.Warn($"Can't read {item.Path}: {ex.Title}. {ex.Detail}");
            }
            catch (Exception ex)
            {
                error = FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
                Log.Warn($"Can't read {item.Path}", ex);
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (item.State != QueueState.Reading) return; // removed, or already processed by a batch
                if (info != null) item.SetInfo(info);
                else item.SetUnreadable(error!);
                ScheduleRefresh();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // ======================== The list ========================

    private void ScheduleRefresh()
    {
        _refreshPending = true;
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private void RefreshIfPending()
    {
        _refreshTimer.Stop();
        if (!_refreshPending) return;
        _refreshPending = false;
        UpdateQueueView();
    }

    private void UpdateQueueView()
    {
        bool any = _items.Count > 0;
        EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        QueuePanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueSummary();
        UpdateStartButton();
        UpdateResizeHint();
    }

    private void UpdateQueueSummary()
    {
        int count = _items.Count;
        long bytes = _items.Sum(i => i.Info?.FileSize ?? 0);
        int unreadable = _items.Count(i => i.State == QueueState.Unreadable);
        string text = count == 1 ? "1 image" : $"{count:N0} images";
        if (bytes > 0) text += $" · {Units.FormatSize(bytes)}";
        if (unreadable > 0) text += $" · {unreadable} can't be read";
        if (_scansRunning > 0) text += " · adding…";
        QueueSummaryText.Text = text;
        UpdateStartButton();
    }

    private void OnQueueSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The Image column takes whatever width the fixed columns leave.
        if (QueueList.View is not GridView gv) return;
        double fixedWidth = gv.Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
        NameColumn.Width = Math.Max(140, QueueList.ActualWidth - fixedWidth - 28);
    }

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled = QueueList.SelectedItems.Count > 0 && !IsRunning;
        UpdateResizeHint();
    }

    private void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !IsRunning) OnRemoveSelected(sender, e);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        foreach (var item in QueueList.SelectedItems.Cast<QueueItem>().ToList()) _items.Remove(item);
    }

    private void OnClearList(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        _items.Clear();
        Log.Info("List cleared");
    }

    private QueueItem? ClickedItem => QueueList.SelectedItem as QueueItem;

    private void OnQueueMenuOpened(object sender, RoutedEventArgs e)
    {
        var item = ClickedItem;
        ShowNewMenu.IsEnabled = item?.Result?.OutputPath is string p && File.Exists(p);
        ShowOriginalMenu.IsEnabled = item != null;
        RemoveMenu.IsEnabled = item != null && !IsRunning;
    }

    private void OnShowNewFile(object sender, RoutedEventArgs e)
    {
        if (ClickedItem?.Result?.OutputPath is string p) Dialogs.RevealInExplorer(p);
    }

    private void OnShowOriginal(object sender, RoutedEventArgs e)
    {
        if (ClickedItem is { } item) Dialogs.RevealInExplorer(item.Path);
    }

    private void OnQueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickedItem is not { } item) return;
        if (item.Result?.OutputPath is string p && File.Exists(p)) Dialogs.RevealInExplorer(p);
        else Dialogs.RevealInExplorer(item.Path);
    }

    // ======================== Drag and drop ========================

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool ok = !IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DragOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (IsRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AddPaths(paths);
    }

    // ======================== Settings ========================

    private JobMode Mode => OptimizeModeRadio.IsChecked == true ? JobMode.Optimize : JobMode.Resize;

    /// <summary>Empty = Auto (null). Anything that isn't a whole number (e.g. pasted "4,096") is -1, which validation rejects.</summary>
    private static int? ParseDimension(string text) =>
        text.Trim().Length == 0 ? null : int.TryParse(text.Trim(), out int v) ? v : -1;

    private ResizeSettings CurrentResize() => new()
    {
        By = ByPercentRadio.IsChecked == true ? ResizeBy.Percentage : ResizeBy.Dimensions,
        Width = ParseDimension(WidthBox.Text),
        Height = ParseDimension(HeightBox.Text),
        Percent = double.TryParse(PercentBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double p) ? p : 0,
        KeepProportions = KeepProportionsCheck.IsChecked == true,
        DontEnlarge = DontEnlargeCheck.IsChecked == true,
    };

    private OutputFormatChoice CurrentFormat => FormatCombo.SelectedIndex switch
    {
        1 => OutputFormatChoice.Png,
        2 => OutputFormatChoice.Jpeg,
        3 => OutputFormatChoice.WebP,
        _ => OutputFormatChoice.KeepOriginal,
    };

    private JobSettings CurrentJob() => new()
    {
        Mode = Mode,
        Resize = CurrentResize(),
        OutputFormat = CurrentFormat,
        AllowFormatConversion = ConversionCheck.IsChecked == true,
        CustomOutputFolder = _customFolder,
    };

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateSettingsView();
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnDimensionBoxFocus(object sender, RoutedEventArgs e)
    {
        ByDimensionsRadio.IsChecked = true;
        ((TextBox)sender).SelectAll();
    }

    private void OnPercentBoxFocus(object sender, RoutedEventArgs e)
    {
        ByPercentRadio.IsChecked = true;
        PercentBox.SelectAll();
    }

    private void UpdateSettingsView()
    {
        var mode = Mode;
        ResizePanel.Visibility = mode == JobMode.Resize ? Visibility.Visible : Visibility.Collapsed;
        OptimizePanel.Visibility = mode == JobMode.Optimize ? Visibility.Visible : Visibility.Collapsed;

        bool byPercent = ByPercentRadio.IsChecked == true;
        DimensionsInputs.Opacity = byPercent ? 0.5 : 1;
        PercentInputs.Opacity = byPercent ? 1 : 0.5;
        WidthPlaceholder.Visibility = WidthBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeightPlaceholder.Visibility = HeightBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeepProportionsCheck.IsEnabled = !byPercent;

        // A chosen format takes over from automatic conversion.
        var format = CurrentFormat;
        ConversionCheck.IsEnabled = format == OutputFormatChoice.KeepOriginal;
        ConversionHint.Opacity = ConversionCheck.IsEnabled ? 1 : 0.5;
        FormatHintText.Text = format switch
        {
            OutputFormatChoice.Jpeg => "Images with transparent areas are skipped (JPEG can't store transparency).",
            OutputFormatChoice.WebP => "WebP is small and keeps transparency, but some older programs can't open it.",
            OutputFormatChoice.Png => "PNG keeps every pixel exactly, but photos stay large.",
            _ => mode == JobMode.Optimize && ConversionCheck.IsChecked == true ? "Except where conversion helps (see above)." : "",
        };

        string folderName = mode == JobMode.Resize ? OutputPlanner.ResizedFolderName : OutputPlanner.OptimizedFolderName;
        FolderText.Text = _customFolder ?? $"A “{folderName}” folder next to each image";
        FolderText.ToolTip = _customFolder ?? $"For example D:\\Pictures\\Photo.png → D:\\Pictures\\{folderName}\\Photo.png";
        DefaultFolderButton.Visibility = _customFolder != null ? Visibility.Visible : Visibility.Collapsed;

        UpdateResizeHint();
        UpdateStartButton();
    }

    /// <summary>
    /// One line under the resize settings: either what's wrong, or what will happen to an actual image
    /// in the list ("Example: Huge.png 12000 × 10000 → 4096 × 3413"), which explains Auto and
    /// Keep proportions better than any label could.
    /// </summary>
    private void UpdateResizeHint()
    {
        if (Mode != JobMode.Resize) return;
        var resize = CurrentResize();
        string? problem = resize.Validate();
        if (problem != null)
        {
            ResizeHintText.Text = problem;
            ResizeHintText.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            return;
        }
        ResizeHintText.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");

        var sample = (QueueList.SelectedItem as QueueItem)?.Info is not null
            ? (QueueItem)QueueList.SelectedItem
            : _items.FirstOrDefault(i => i.Info != null);
        if (sample?.Info is not { } info)
        {
            ResizeHintText.Text = resize.By == ResizeBy.Percentage
                ? $"Width and height become {resize.Percent:0.##}% of the original."
                : resize.KeepProportions
                    ? resize.Width != null && resize.Height != null
                        ? "Each image is made to fit inside this size, keeping its shape."
                        : "The Auto side is worked out for each image, keeping its shape."
                    : "Each side is set separately; an Auto side stays as it is.";
            return;
        }
        var plan = SizePlanner.For(info.Width, info.Height, resize);
        string example = $"Example: {sample.FileName}  {Units.FormatDimensions(info.Width, info.Height)} → {Units.FormatDimensions(plan.Width, plan.Height)}";
        if (plan.IsSameAs(info.Width, info.Height) && resize.DontEnlarge) example += " (already small enough, copied unchanged)";
        ResizeHintText.Text = example;
    }

    private void UpdateStartButton()
    {
        int count = _items.Count(i => i.State != QueueState.Unreadable);
        string verb = Mode == JobMode.Resize ? "RESIZE" : "OPTIMIZE";
        StartButton.Content = count switch
        {
            0 => verb,
            1 => $"{verb} 1 IMAGE",
            _ => $"{verb} {count:N0} IMAGES",
        };
        bool valid = Mode == JobMode.Optimize || CurrentResize().Validate() == null;
        StartButton.IsEnabled = count > 0 && valid && !IsRunning && _scansRunning == 0;
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save the new images" };
        if (_customFolder != null) dlg.InitialDirectory = _customFolder;
        if (dlg.ShowDialog(this) != true) return;
        string folder = dlg.FolderName;
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".paradis-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Log.Warn($"Output folder {folder} isn't writable", ex);
            Dialogs.Error(this, "Can't save there", "Windows doesn't allow saving files in that folder. Choose another one, such as Pictures or Documents.");
            return;
        }
        _customFolder = folder;
        Log.Info($"Output folder set to {folder}");
        UpdateSettingsView();
    }

    private void OnDefaultFolder(object sender, RoutedEventArgs e)
    {
        _customFolder = null;
        UpdateSettingsView();
    }

    // ======================== Running a batch ========================

    private bool IsRunning => _batchTask is { IsCompleted: false };

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        var job = CurrentJob();
        if (job.Mode == JobMode.Resize && job.Resize.Validate() is string problem)
        {
            Dialogs.Info(this, "Check the new size", problem);
            return;
        }
        var queue = _items.Where(i => i.State != QueueState.Unreadable).ToList();
        if (queue.Count == 0) return;
        SaveSettings();

        foreach (var item in queue) item.SetWaiting();
        SetRunning(true);
        _batchCts = new CancellationTokenSource();
        var items = queue.Select(i => new BatchItem(i.Path, i.RelativeFolder)).ToList();
        string verb = job.Mode == JobMode.Resize ? "Resizing" : "Optimizing";
        int done = 0, failed = 0;
        long savedBytes = 0;
        QueueItem? current = null;

        var progress = new Progress<BatchProgress>(p =>
        {
            var item = queue[p.Index];
            if (p.Result == null)
            {
                if (current != item)
                {
                    current = item;
                    item.SetWorking();
                    QueueList.ScrollIntoView(item);
                    ProgressTitleText.Text = $"{verb} image {p.Index + 1:N0} of {p.Total:N0}";
                    ProgressFileText.Text = item.FileName;
                }
                item.Progress = p.ItemFraction;
            }
            else
            {
                item.SetResult(p.Result);
                if (p.Result.Saved)
                {
                    done++;
                    if (p.Result.Source is { IsFrameRecovery: false } source) savedBytes += source.FileSize - p.Result.OutputSize;
                }
                else if (p.Result.Outcome is ItemOutcome.Failed or ItemOutcome.Skipped) failed++;
            }
            BatchProgressBar.Value = (p.Index + (p.Result != null ? 1 : p.ItemFraction)) / p.Total;
            ProgressCountsText.Text = $"{done:N0} done · {failed:N0} failed or skipped" +
                                      (savedBytes > 0 ? $" · {Units.FormatSize(savedBytes)} saved so far" : "");
        });

        BatchSummary? summary = null;
        try
        {
            _batchTask = new BatchRunner(job).RunAsync(items, progress, _batchCts.Token);
            summary = await (Task<BatchSummary>)_batchTask;
        }
        catch (Exception ex)
        {
            Log.Error("Batch failed unexpectedly", ex);
            Dialogs.Error(this, "Something went wrong",
                $"The batch stopped unexpectedly. Your original images are untouched.\n\nTechnical details are in:\n{Log.Directory}");
        }
        finally
        {
            // Let queued progress callbacks land before tidying up.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            foreach (var item in queue) item.ResetIfWaiting();
            _batchCts.Dispose();
            _batchCts = null;
            SetRunning(false);
        }

        if (_closeAfterCancel)
        {
            Close();
            return;
        }
        if (summary != null) new SummaryWindow(summary, job.Mode) { Owner = this }.ShowDialog();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_batchCts == null || _batchCts.IsCancellationRequested) return;
        Log.Info("Cancel requested by the user");
        _batchCts.Cancel();
        CancelButton.IsEnabled = false;
        ProgressTitleText.Text = "Stopping…";
    }

    private void SetRunning(bool running)
    {
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        SettingsPanel.IsEnabled = !running;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = ClearButton.IsEnabled = SubfoldersCheck.IsEnabled = !running;
        RemoveButton.IsEnabled = !running && QueueList.SelectedItems.Count > 0;
        if (running)
        {
            BatchProgressBar.Value = 0;
            ProgressTitleText.Text = "Getting ready…";
            ProgressFileText.Text = "";
            ProgressCountsText.Text = "";
        }
        UpdateStartButton();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsRunning)
        {
            e.Cancel = true;
            if (_batchCts?.IsCancellationRequested == true) return; // already stopping; closes when done
            if (Dialogs.Confirm(this, "Stop and close?",
                    "Images are still being processed. Stop after the current image and close?\n\nFinished images are kept. Your originals are never changed."))
            {
                _closeAfterCancel = true;
                OnCancel(this, new RoutedEventArgs());
            }
            return;
        }
        SaveSettings();
        _toRead.Writer.TryComplete();
    }

    private void SaveSettings()
    {
        var r = CurrentResize();
        _settings.Mode = Mode;
        _settings.ResizeBy = r.By;
        _settings.Width = r.Width;
        _settings.Height = r.Height;
        if (r.Percent > 0) _settings.Percent = r.Percent;
        _settings.KeepProportions = r.KeepProportions;
        _settings.DontEnlarge = r.DontEnlarge;
        _settings.OutputFormat = CurrentFormat;
        _settings.AllowFormatConversion = ConversionCheck.IsChecked == true;
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.Save();
    }

    // ======================== Footer ========================

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Dialogs.RevealInExplorer(Log.Directory);

    private void OnMoreLink(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.OpenUrl(AppInfo.CollectionUrl))
        {
            try { Clipboard.SetText(AppInfo.CollectionUrl); } catch { }
            Dialogs.Info(this, "More from Paradis EZ Utilities", $"Couldn't open the browser. The address was copied:\n{AppInfo.CollectionUrl}");
        }
    }
}
