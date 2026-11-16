using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShowTell;

public partial class SendDialog : Window
{
    readonly RecordingResult _rec;
    readonly Settings _s;
    readonly List<InstalledTarget> _targets;
    readonly HashSet<KeyFrame> _excluded = new();
    readonly CancellationTokenSource _cts = new();
    Task<List<TranscriptSegment>>? _transcription;
    string? _transcriptError;
    bool _sending;
    int _threadLoadVersion;

    /// <summary>Raised after a successful paste so the app can remember the choice.</summary>
    public event Action<InstalledTarget>? TargetUsed;
    public event Action? SettingsRequested;

    public SendDialog(RecordingResult rec, Settings settings, List<InstalledTarget> targets)
    {
        InitializeComponent();
        _rec = rec;
        _s = settings;
        _targets = targets;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; // the monitor with the mouse cursor

        Instructions.AcceptsReturn = true;
        Instructions.TextChanged += (_, _) => Placeholder.Visibility = Instructions.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += OnPreviewKeyDown;
        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CloseBtn.Click += (_, _) => Close();
        SettingsBtn.Click += (_, _) => SettingsRequested?.Invoke();
        SendBtn.Click += async (_, _) => await SendAsync();
        OpenFolder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_rec.Folder}\"") { UseShellExecute = true });
        AppBox.SelectionChanged += (_, _) => _ = LoadThreadsAsync();
        ThreadBox.SelectionChanged += (_, _) => OnThreadSelectionChanged();

        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Meta.Text = $"{Transcriber.Format(_rec.Duration)} · {_rec.Frames.Count} frame{(_rec.Frames.Count == 1 ? "" : "s")}";
        BuildThumbnails();

        AppBox.ItemsSource = _targets;
        if (_targets.Count == 0)
        {
            ShowError("No supported AI desktop app found (Claude, Codex, Grok). Install one, or add it under Settings → targets in settings.json.");
            SendBtn.IsEnabled = false;
        }
        else
        {
            AppBox.SelectedItem = _targets.FirstOrDefault(t => t.Target.Name == _s.LastTarget) ?? _targets[0];
        }

        _transcription = TranscribeAsync();

        var h = new WindowInteropHelper(this).Handle;
        Native.ForceForeground(h);
        Activate();
        Instructions.Focus();
        Keyboard.Focus(Instructions);
    }

    void BuildThumbnails()
    {
        foreach (var f in _rec.Frames)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
            img.DecodePixelHeight = 144;
            img.UriSource = new Uri(f.Path);
            img.EndInit();

            var label = new TextBlock
            {
                Text = Transcriber.Format(f.At), FontSize = 10, Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4),
            };
            var grid = new Grid();
            grid.Children.Add(new Image { Source = img, Height = 72, Stretch = Stretch.Uniform });
            grid.Children.Add(label);
            var card = new Border
            {
                Child = grid, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true,
                BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = "Click to include / leave out",
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (!_excluded.Remove(f)) _excluded.Add(f);
                card.Opacity = _excluded.Contains(f) ? 0.25 : 1;
                int n = _rec.Frames.Count - _excluded.Count;
                Meta.Text = $"{Transcriber.Format(_rec.Duration)} · {n} of {_rec.Frames.Count} frames";
            };
            Thumbs.Items.Add(card);
        }
    }

    async Task<List<TranscriptSegment>> TranscribeAsync()
    {
        var progress = new Progress<string>(m => { if (!_sending) Status.Text = m; });
        try
        {
            if (_rec.AudioPath == null)
            {
                Status.Text = "No microphone audio: " + _rec.AudioError;
                return new();
            }
            Status.Text = "Transcribing…";
            var segs = await Transcriber.TranscribeAsync(_rec.AudioPath, _s, progress, _cts.Token);
            if (!_sending) Status.Text = segs.Count == 0 ? "Transcript ready (no speech detected)" : $"Transcript ready · {segs.Count} segment{(segs.Count == 1 ? "" : "s")}";
            return segs;
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception ex)
        {
            Log.Write("Transcription failed: " + ex);
            _transcriptError = ex.Message;
            if (!_sending) Status.Text = "Transcription failed: " + ex.Message;
            return new();
        }
    }

    async Task LoadThreadsAsync()
    {
        if (AppBox.SelectedItem is not InstalledTarget t) return;
        int version = ++_threadLoadVersion;
        var newChat = new ThreadChoice(ThreadKind.NewChat, "New chat");

        var hwnd = Automation.FindMainWindow(t.Target);
        if (hwnd == IntPtr.Zero)
        {
            SetThreadItems(new() { new(ThreadKind.NewChat, $"New chat ({t.Target.Name} will open)") }, 0);
            return;
        }

        SetThreadItems(new() { newChat, new(ThreadKind.Current, "Currently open chat"), new(ThreadKind.NewChat, "Loading recent chats…") }, 0);
        _userPickedCurrent = false;

        var (title, threads) = await Task.Run(() => (Automation.CurrentTitle(hwnd), Automation.ListThreads(hwnd)));
        if (version != _threadLoadVersion) return; // user switched app meanwhile

        var items = new List<ThreadChoice>
        {
            newChat,
            new(ThreadKind.Current, string.IsNullOrWhiteSpace(title) ? "Currently open chat" : $"Currently open: {Trim(title, 48)}"),
        };
        items.AddRange(threads.Select(n => new ThreadChoice(ThreadKind.Existing, "  " + Trim(n, 60), n)));
        // Keep "currently open" only if the user picked it while the list was loading; otherwise default to New chat.
        SetThreadItems(items, _userPickedCurrent ? 1 : 0);
    }

    bool _settingThreads, _userPickedCurrent;

    void SetThreadItems(List<ThreadChoice> items, int selected)
    {
        _settingThreads = true;
        try
        {
            ThreadBox.ItemsSource = items;
            ThreadBox.SelectedItem = items[selected];
        }
        finally { _settingThreads = false; }
    }

    void OnThreadSelectionChanged()
    {
        if (!_settingThreads && ThreadBox.SelectedItem is ThreadChoice c)
            _userPickedCurrent = c.Kind == ThreadKind.Current;
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_sending)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // Let an open dropdown take Enter to confirm its selection.
            if (AppBox.IsDropDownOpen || ThreadBox.IsDropDownOpen) return;
            e.Handled = true;
            _ = SendAsync();
        }
    }

    async Task SendAsync()
    {
        if (_sending || AppBox.SelectedItem is not InstalledTarget target) return;
        var thread = ThreadBox.SelectedItem as ThreadChoice ?? new ThreadChoice(ThreadKind.NewChat, "New chat");
        if (thread.Label.StartsWith("Loading")) thread = new ThreadChoice(ThreadKind.NewChat, "New chat");

        _sending = true;
        SetBusy(true);
        Error.Visibility = Visibility.Collapsed;
        string? prompt = null;
        try
        {
            Status.Text = "Finishing transcript…";
            var transcript = _transcription != null ? await _transcription : new List<TranscriptSegment>();
            var frames = _rec.Frames.Where(f => !_excluded.Contains(f)).ToList();
            prompt = PromptBuilder.Build(Instructions.Text, _rec, frames, transcript, _transcriptError);
            Sender.SaveArtifacts(_rec, prompt, transcript);

            Hide();
            await Sender.SendAsync(target, thread, frames, prompt, _s, new Progress<string>(m => Log.Write("send: " + m)), _cts.Token);
            TargetUsed?.Invoke(target);
            Close();
        }
        catch (Exception ex)
        {
            Log.Write("Send failed: " + ex);
            Show();
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            var msg = ex.Message;
            if (prompt != null)
            {
                try { Automation.SetClipboardText(prompt); msg += "  The prompt text is on your clipboard; the frames are in the recording folder."; }
                catch { }
            }
            ShowError(msg);
            Status.Text = "";
            _sending = false;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        AppBox.IsEnabled = ThreadBox.IsEnabled = Instructions.IsEnabled = SendBtn.IsEnabled = !busy;
        SendBtn.Content = busy ? "Working…" : "Paste into chat";
    }

    void ShowError(string msg)
    {
        Error.Text = msg;
        Error.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_sending) _cts.Cancel();
        base.OnClosed(e);
    }
}
