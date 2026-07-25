using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace YtDlpGui;

public partial class MainWindow : Window
{
    private Process? _process;
    private string? _cookiesFilePath;
    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+(\d{1,3}(?:\.\d+)?)%",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        OutputFolderTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        YtDlpPathTextBox.Text = FindExecutable("yt-dlp.exe");
        FfmpegPathTextBox.Text = FindExecutable("ffmpeg.exe", "ffmpeg\\bin\\ffmpeg.exe");
        FormatComboBox.SelectionChanged += FormatComboBox_SelectionChanged;
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

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        CustomFormatTextBox.IsEnabled = tag.StartsWith("CUSTOM");
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

    private void ApplySelectedFormats()
    {
        if (FormatsListBox.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "請先在清單中選取至少一個格式。", "尚未選取", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ids = new List<string>();
        foreach (var item in FormatsListBox.SelectedItems)
        {
            var line = (item as string ?? "").TrimStart();
            var m = Regex.Match(line, @"^(\S+)");
            if (m.Success)
                ids.Add(m.Groups[1].Value);
        }

        if (ids.Count == 0) return;

        CustomFormatTextBox.Text = string.Join("+", ids);

        foreach (var obj in FormatComboBox.Items)
        {
            if (obj is ComboBoxItem cbi && (cbi.Tag as string)?.StartsWith("CUSTOM") == true)
            {
                FormatComboBox.SelectedItem = cbi;
                break;
            }
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

        var tag = (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var isAudioOnly = tag.StartsWith("AUDIO_ONLY");

        if (isAudioOnly)
        {
            var audioCodec = tag.Split('|') is [_, var codec] ? codec : "mp3";
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(audioCodec);
        }
        else if (tag == "CUSTOM")
        {
            var custom = CustomFormatTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(custom))
            {
                args.Add("-f");
                args.Add(custom);
            }
        }
        else
        {
            args.Add("-f");
            args.Add(tag);
        }

        // yt-dlp shells out to ffmpeg whenever the chosen format needs a separate
        // video+audio merge; forcing the container to mp4 here covers both the
        // presets above and any custom "video+audio" format code from -F.
        if (!isAudioOnly && MergeMp4CheckBox.IsChecked == true)
        {
            args.Add("--merge-output-format");
            args.Add("mp4");
            args.Add("--remux-video");
            args.Add("mp4");
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

        var ffmpegPath = FfmpegPathTextBox.Text.Trim();
        var ffmpegMissing = string.IsNullOrEmpty(ffmpegPath)
            || (!File.Exists(ffmpegPath) && ffmpegPath.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        if (MergeMp4CheckBox.IsChecked == true && ffmpegMissing)
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

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "0%";
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

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
        _process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await _process.WaitForExitAsync();
            AppendLog(_process.ExitCode == 0 ? "=== 下載完成 ===" : $"=== 結束，離開代碼 {_process.ExitCode} ===");
        }
        catch (Exception ex)
        {
            AppendLog($"執行失敗：{ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                AppendLog("=== 使用者中止下載 ===");
            }
            catch (Exception ex)
            {
                AppendLog($"停止失敗：{ex.Message}");
            }
        }
    }
}
