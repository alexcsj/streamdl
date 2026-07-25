using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace YtDlpGui;

public partial class MainWindow : Window
{
    private const int MaxAutoRetries = 20;
    private static readonly TimeSpan AutoRetryDelay = TimeSpan.FromSeconds(5);

    private Process? _process;
    private string? _cookiesFilePath;
    private bool _userStopRequested;
    private CancellationTokenSource? _downloadCts;
    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+(\d{1,3}(?:\.\d+)?)%",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        OutputFolderTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        YtDlpPathTextBox.Text = FindExecutable("yt-dlp.exe");
        FfmpegPathTextBox.Text = FindExecutable("ffmpeg.exe", "ffmpeg\\bin\\ffmpeg.exe");

        VideoQualityComboBox.SelectionChanged += VideoQualityComboBox_SelectionChanged;
        AudioFormatComboBox.SelectionChanged += AudioFormatComboBox_SelectionChanged;
        UpdateVideoCustomEnabled();
        UpdateAudioCustomEnabled();
        UpdateMergeAvailability();
    }

    private static string FindExecutable(string exeName, params string[] extraRelativeCandidates)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName),
            Path.Combine(Directory.GetCurrentDirectory(), exeName),
        };
        foreach (var extra in extraRelativeCandidates)
        {
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, extra));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), extra));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, exeName);
                if (File.Exists(full))
                    return full;
            }
            catch (ArgumentException)
            {
                // ignore malformed PATH entries
            }
        }

        return exeName;
    }

    private void VideoQualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVideoCustomEnabled();
        UpdateMergeAvailability();
    }

    private void AudioFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAudioCustomEnabled();
        UpdateMergeAvailability();
    }

    private void UpdateVideoCustomEnabled()
    {
        var tag = (VideoQualityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        CustomVideoFormatTextBox.IsEnabled = tag == "CUSTOM";
    }

    private void UpdateAudioCustomEnabled()
    {
        var tag = (AudioFormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        CustomAudioFormatTextBox.IsEnabled = tag == "CUSTOM";
    }

    private bool IsVideoIncluded()
    {
        var tag = (VideoQualityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        return tag != "NONE";
    }

    private bool IsAudioIncluded()
    {
        var tag = (AudioFormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        return tag != "NONE";
    }

    private bool _wasBothSelected;

    // -x/--extract-audio (mp3/m4a) throws away any video track, so it only
    // makes sense when no video format is selected at all.
    private void UpdateMergeAvailability()
    {
        var videoIncluded = IsVideoIncluded();
        AudioMp3Item.IsEnabled = !videoIncluded;
        AudioM4aItem.IsEnabled = !videoIncluded;
        if (videoIncluded && AudioFormatComboBox.SelectedItem is ComboBoxItem { Tag: string audioTag } && audioTag.StartsWith("AUDIO_ONLY"))
            AudioFormatComboBox.SelectedIndex = 0;

        var bothSelected = videoIncluded && IsAudioIncluded();
        MergeMp4CheckBox.IsEnabled = bothSelected;
        if (!bothSelected)
            MergeMp4CheckBox.IsChecked = false;
        else if (!_wasBothSelected)
            MergeMp4CheckBox.IsChecked = true; // default to merging when both first become available

        _wasBothSelected = bothSelected;
    }

    private enum FormatKind { Audio, Video }

    private static FormatKind ClassifyFormatLine(string line)
    {
        if (Regex.IsMatch(line, @"\baudio only\b", RegexOptions.IgnoreCase))
            return FormatKind.Audio;
        return FormatKind.Video;
    }

    private static List<string> ParseFormatLines(string output)
    {
        var result = new List<string>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('[')) continue;                 // log lines like [youtube] / [info]
            if (trimmed.StartsWith("ID", StringComparison.OrdinalIgnoreCase)) continue; // header row
            if (trimmed.StartsWith("---") || trimmed.StartsWith("───") || trimmed.StartsWith("═")) continue; // divider row
            if (!Regex.IsMatch(trimmed, @"^[A-Za-z0-9][A-Za-z0-9_.\-]*\s")) continue; // must look like "<id> ..."
            result.Add(line);
        }
        return result;
    }

    private async void FetchFormatsButton_Click(object sender, RoutedEventArgs e)
    {
        var ytDlpPath = YtDlpPathTextBox.Text.Trim();
        var url = UrlTextBox.Text.Trim();

        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(this, "請輸入目標影片網址。", "缺少網址", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(ytDlpPath) || (!File.Exists(ytDlpPath) && !ytDlpPath.Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "找不到 yt-dlp.exe，請確認路徑是否正確。", "找不到執行檔", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var args = new List<string> { "-F", "--no-playlist" };
        if (!string.IsNullOrEmpty(_cookiesFilePath))
        {
            args.Add("--cookies");
            args.Add(_cookiesFilePath);
        }
        args.Add(url);

        FetchFormatsButton.IsEnabled = false;
        FormatsListBox.Items.Clear();
        FetchFormatsStatus.Text = "查詢中…";
        AppendLog($"> {ytDlpPath} {string.Join(' ', args.Select(QuoteIfNeeded))}");

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var stdout = new StringBuilder();

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, ev) => { if (ev.Data != null) { stdout.AppendLine(ev.Data); AppendLog(ev.Data); } };
            proc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();

            var formatLines = ParseFormatLines(stdout.ToString());
            foreach (var line in formatLines)
                FormatsListBox.Items.Add(line);

            if (formatLines.Count == 0)
            {
                FetchFormatsStatus.Text = "沒有找到格式資訊，請查看下方執行紀錄。";
            }
            else
            {
                FetchFormatsStatus.Text = $"共 {formatLines.Count} 個格式，選取後按「套用選取的格式」或雙擊套用。";
                FormatsExpander.IsExpanded = true;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"取得格式失敗：{ex.Message}");
            FetchFormatsStatus.Text = "取得格式失敗。";
        }
        finally
        {
            FetchFormatsButton.IsEnabled = true;
        }
    }

    private static void SelectCustomTag(ComboBox comboBox)
    {
        foreach (var obj in comboBox.Items)
        {
            if (obj is ComboBoxItem { Tag: "CUSTOM" } cbi)
            {
                comboBox.SelectedItem = cbi;
                return;
            }
        }
    }

    private void ApplySelectedFormats()
    {
        if (FormatsListBox.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "請先在清單中選取至少一個格式。", "尚未選取", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? videoId = null;
        string? audioId = null;
        foreach (var item in FormatsListBox.SelectedItems)
        {
            var line = (item as string ?? "").TrimStart();
            var m = Regex.Match(line, @"^(\S+)");
            if (!m.Success) continue;
            var id = m.Groups[1].Value;

            if (ClassifyFormatLine(line) == FormatKind.Audio)
                audioId ??= id;
            else
                videoId ??= id;
        }

        if (videoId == null && audioId == null) return;

        if (videoId != null)
        {
            CustomVideoFormatTextBox.Text = videoId;
            SelectCustomTag(VideoQualityComboBox);
        }
        else
        {
            VideoQualityComboBox.SelectedItem = VideoQualityComboBox.Items
                .OfType<ComboBoxItem>().First(i => (string)i.Tag == "NONE");
        }

        if (audioId != null)
        {
            CustomAudioFormatTextBox.Text = audioId;
            SelectCustomTag(AudioFormatComboBox);
        }
        else
        {
            AudioFormatComboBox.SelectedItem = AudioFormatComboBox.Items
                .OfType<ComboBoxItem>().First(i => (string)i.Tag == "NONE");
        }
    }

    private void ApplyFormatsButton_Click(object sender, RoutedEventArgs e) => ApplySelectedFormats();

    private void FormatsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ApplySelectedFormats();

    private void BrowseYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "yt-dlp 執行檔 (yt-dlp.exe)|yt-dlp.exe|所有執行檔 (*.exe)|*.exe|所有檔案 (*.*)|*.*",
            Title = "選擇 yt-dlp.exe"
        };
        if (dialog.ShowDialog() == true)
            YtDlpPathTextBox.Text = dialog.FileName;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ffmpeg 執行檔 (ffmpeg.exe)|ffmpeg.exe|所有執行檔 (*.exe)|*.exe|所有檔案 (*.*)|*.*",
            Title = "選擇 ffmpeg.exe"
        };
        if (dialog.ShowDialog() == true)
            FfmpegPathTextBox.Text = dialog.FileName;
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇下載儲存資料夾",
            InitialDirectory = Directory.Exists(OutputFolderTextBox.Text) ? OutputFolderTextBox.Text : ""
        };
        if (dialog.ShowDialog() == true)
            OutputFolderTextBox.Text = dialog.FolderName;
    }

    private void BrowseCookies_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Cookies 檔案 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            Title = "選擇 cookies 檔案"
        };
        if (dialog.ShowDialog() == true)
            SetCookiesFile(dialog.FileName);
    }

    private void ClearCookies_Click(object sender, RoutedEventArgs e)
    {
        _cookiesFilePath = null;
        CookieDropLabel.Text = "將 cookies.txt 拖曳到這裡（或點擊瀏覽）";
        CookieDropLabel.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private void CookieDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            CookieDropZone.Tag = "hover";
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void CookieDropZone_DragLeave(object sender, DragEventArgs e)
    {
        CookieDropZone.Tag = null;
    }

    private void CookieDropZone_Drop(object sender, DragEventArgs e)
    {
        CookieDropZone.Tag = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            SetCookiesFile(files[0]);
    }

    private void SetCookiesFile(string path)
    {
        _cookiesFilePath = path;
        CookieDropLabel.Text = path;
        CookieDropLabel.Foreground = System.Windows.Media.Brushes.Black;
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolderTextBox.Text.Trim();
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private List<string> BuildArguments()
    {
        var args = new List<string>();
        var url = UrlTextBox.Text.Trim();

        var videoTag = (VideoQualityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "NONE";
        var audioTag = (AudioFormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "NONE";

        var videoFormat = videoTag switch
        {
            "NONE" => null,
            "CUSTOM" => CustomVideoFormatTextBox.Text.Trim() is { Length: > 0 } v ? v : null,
            _ => videoTag,
        };

        // AudioFormatComboBox.IsEnabled logic keeps AUDIO_ONLY (mp3/m4a) unselectable
        // whenever a video format is present, so this only ever fires in audio-only mode.
        var isAudioExtract = audioTag.StartsWith("AUDIO_ONLY");
        var audioFormat = audioTag switch
        {
            "NONE" => null,
            "CUSTOM" => CustomAudioFormatTextBox.Text.Trim() is { Length: > 0 } a ? a : null,
            _ when isAudioExtract => null,
            _ => audioTag,
        };

        if (isAudioExtract && videoFormat == null)
        {
            var audioCodec = audioTag.Split('|') is [_, var codec] ? codec : "mp3";
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(audioCodec);
        }
        else if (videoFormat != null && audioFormat != null)
        {
            var merge = MergeMp4CheckBox.IsEnabled && MergeMp4CheckBox.IsChecked == true;
            args.Add("-f");
            args.Add(merge ? $"{videoFormat}+{audioFormat}" : $"{videoFormat},{audioFormat}");
            if (merge)
            {
                args.Add("--merge-output-format");
                args.Add("mp4");
                args.Add("--remux-video");
                args.Add("mp4");
            }
        }
        else if (videoFormat != null)
        {
            args.Add("-f");
            args.Add(videoFormat);
        }
        else if (audioFormat != null)
        {
            args.Add("-f");
            args.Add(audioFormat);
        }

        var ffmpegPath = FfmpegPathTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegPath);
        }

        if (PlaylistCheckBox.IsChecked == true)
            args.Add("--yes-playlist");
        else
            args.Add("--no-playlist");

        if (SubtitlesCheckBox.IsChecked == true)
        {
            args.Add("--write-subs");
            args.Add("--write-auto-subs");
            args.Add("--embed-subs");
        }

        if (ThumbnailCheckBox.IsChecked == true)
            args.Add("--embed-thumbnail");

        if (MetadataCheckBox.IsChecked == true)
            args.Add("--embed-metadata");

        if (RestrictFilenameCheckBox.IsChecked == true)
            args.Add("--restrict-filenames");

        if (KeepOriginalCheckBox.IsChecked == true)
            args.Add("--no-post-overwrites");

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(outputFolder))
        {
            var nameTemplate = IncludeIdInFilenameCheckBox.IsChecked == true
                ? "%(title)s [%(id)s].%(ext)s"
                : "%(title)s.%(ext)s";
            args.Add("-o");
            args.Add(Path.Combine(outputFolder, nameTemplate));
        }

        if (!string.IsNullOrEmpty(_cookiesFilePath))
        {
            args.Add("--cookies");
            args.Add(_cookiesFilePath);
        }

        var extra = ExtraArgsTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(extra))
            args.AddRange(SplitArguments(extra));

        args.Add("--newline");
        args.Add(url);

        return args;
    }

    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var matches = Regex.Matches(commandLine, @"(""[^""]*""|\S+)");
        foreach (Match m in matches)
        {
            var value = m.Value;
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];
            yield return value;
        }
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (arg.Length == 0 || arg.Contains(' '))
            return $"\"{arg}\"";
        return arg;
    }

    private void AppendLog(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(text + Environment.NewLine);
            LogTextBox.ScrollToEnd();

            var match = ProgressRegex.Match(text);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
            {
                DownloadProgressBar.Value = pct;
                ProgressText.Text = $"{pct:0.0}%";
            }
        });
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var ytDlpPath = YtDlpPathTextBox.Text.Trim();
        var url = UrlTextBox.Text.Trim();

        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(this, "請輸入目標影片網址。", "缺少網址", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(ytDlpPath) || (!File.Exists(ytDlpPath) && !ytDlpPath.Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "找不到 yt-dlp.exe，請確認路徑是否正確。", "找不到執行檔", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!IsVideoIncluded() && !IsAudioIncluded())
        {
            MessageBox.Show(this, "影片與音訊都設為「不下載」，請至少選擇一項。", "沒有可下載的內容", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ffmpegPath = FfmpegPathTextBox.Text.Trim();
        var ffmpegMissing = string.IsNullOrEmpty(ffmpegPath)
            || (!File.Exists(ffmpegPath) && ffmpegPath.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        if (MergeMp4CheckBox.IsEnabled && MergeMp4CheckBox.IsChecked == true && ffmpegMissing)
        {
            var result = MessageBox.Show(this,
                "找不到 ffmpeg.exe，無法自動合併影音為 MP4。要繼續下載嗎？（可能會下載成分離的影音檔或失敗）",
                "找不到 ffmpeg", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(outputFolder) && !Directory.Exists(outputFolder))
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"無法建立下載資料夾：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var args = BuildArguments();

        _userStopRequested = false;
        _downloadCts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "0%";

        try
        {
            await RunWithAutoRetryAsync(ytDlpPath, args, _downloadCts.Token);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    // yt-dlp resumes partial downloads by default (--continue), so re-running the
    // same command after an unexpected exit picks up where it left off instead of
    // starting over. A deliberate Stop click (_userStopRequested) skips all of this.
    private async Task RunWithAutoRetryAsync(string ytDlpPath, List<string> args, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            AppendLog($"> {ytDlpPath} {string.Join(' ', args.Select(QuoteIfNeeded))}");

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            int exitCode;
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
            _process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                await _process.WaitForExitAsync();
                exitCode = _process.ExitCode;
            }
            catch (Exception ex)
            {
                AppendLog($"執行失敗：{ex.Message}");
                exitCode = -1;
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }

            if (exitCode == 0)
            {
                AppendLog("=== 下載完成 ===");
                return;
            }

            if (_userStopRequested)
            {
                AppendLog("=== 使用者中止下載 ===");
                return;
            }

            attempt++;
            if (attempt > MaxAutoRetries)
            {
                AppendLog($"=== 下載中斷（離開代碼 {exitCode}），已自動重試 {MaxAutoRetries} 次仍未完成，停止重試 ===");
                return;
            }

            AppendLog($"=== 下載未完成（離開代碼 {exitCode}），{AutoRetryDelay.TotalSeconds:0} 秒後自動繼續下載（第 {attempt} 次重試）===");
            try
            {
                await Task.Delay(AutoRetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AppendLog("=== 使用者中止下載 ===");
                return;
            }
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _userStopRequested = true;
        _downloadCts?.Cancel();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                AppendLog($"停止失敗：{ex.Message}");
            }
        }
    }
}
