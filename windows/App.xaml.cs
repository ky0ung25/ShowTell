using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace ShowTell;

public partial class App : System.Windows.Application
{
    Mutex? _single;
    Settings _settings = new();
    KeyboardHook? _hook;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem? _toggleItem;
    Icon? _idleIcon, _recIcon;
    Recorder? _recorder;
    RecordingOverlay? _overlay;
    DispatcherTimer? _maxTimer;
    List<InstalledTarget> _targets = new();
    Task? _detectTask;
    const string OpenSettingsEvent = "ShowTell.OpenSettings";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest"))
        {
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        _single = new Mutex(true, "ShowTell.SingleInstance", out bool first);
        if (!first)
        {
            // Already running (e.g. launched again from the Start Menu): ask that copy to open Settings.
            try { using var ev = EventWaitHandle.OpenExisting(OpenSettingsEvent); ev.Set(); }
            catch { System.Windows.MessageBox.Show("ShowTell is already running. Look for it in the system tray.", "ShowTell"); }
            Shutdown();
            return;
        }
        var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEvent);
        new Thread(() => { while (openSettings.WaitOne()) Dispatcher.BeginInvoke(OpenSettings); }) { IsBackground = true, Name = "ShowTell settings signal" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Unhandled: " + ex.Exception);
            ex.Handled = true;
        };

        _settings = Settings.Load();
        _settings.Save(); // writes defaults on first run so the file is there to edit

        _hook = new KeyboardHook();
        _hook.Configure(_settings);
        _hook.Toggled += Toggle;

        CreateTray();
        Recorder.Prune(_settings.KeepRecordings);
        RefreshTargets();
        PrefetchSpeechModel();

        Log.Write("ShowTell started");
        if (e.Args.Contains("--settings")) { Dispatcher.BeginInvoke(OpenSettings); return; }
        if (!e.Args.Contains("--quiet"))
            _tray!.ShowBalloonTip(4000, "ShowTell is running",
                $"Press {HotkeyText} together, twice, to start recording. Do it again to stop.", WinForms.ToolTipIcon.None);
    }

    string HotkeyText => $"{KeyboardHook.KeyName(_settings.KeyA)} + {KeyboardHook.KeyName(_settings.KeyB)}";


    /// <summary>Fetch the chosen speech model in the background so the first recording doesn't wait for it.</summary>
    void PrefetchSpeechModel()
    {
        var model = Transcriber.Find(_settings.SpeechModel);
        if (model.Exists) return;
        Task.Run(async () =>
        {
            try { await Transcriber.EnsureModelAsync(model, null, CancellationToken.None); Log.Write("Speech model ready: " + model.Id); }
            catch (Exception ex) { Log.Write("Speech model pre-download failed (will retry when needed): " + ex.Message); }
        });
    }
    void RefreshTargets()
    {
        _detectTask = Task.Run(() =>
        {
            var found = AppCatalog.Detect(_settings);
            Dispatcher.Invoke(() => _targets = found);
        });
    }

    // ---------------- tray ----------------

    void CreateTray()
    {
        _idleIcon = MakeIcon(Color.FromArgb(0x52, 0x52, 0x5B), false);
        _recIcon = MakeIcon(Color.FromArgb(0xE5, 0x48, 0x4D), true);

        var menu = new WinForms.ContextMenuStrip();
        _toggleItem = new WinForms.ToolStripMenuItem("Start recording", null, (_, _) => Toggle());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open recordings folder", null, (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Settings.RecordingsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.RecordingsDir}\"") { UseShellExecute = true });
        });
        menu.Items.Add("Reload settings.json", null, (_, _) => ApplySettings(Settings.Load()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit ShowTell", null, (_, _) => Quit());

        _tray = new WinForms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = "ShowTell",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Toggle(); };
        UpdateTray();
    }

    void UpdateTray()
    {
        bool rec = _recorder != null;
        _tray!.Icon = rec ? _recIcon : _idleIcon;
        _tray.Text = rec ? "ShowTell — recording" : $"ShowTell — {HotkeyText} ×2 to record";
        _toggleItem!.Text = rec ? $"Stop recording ({HotkeyText} ×2)" : $"Start recording ({HotkeyText} ×2)";
    }

    static Icon MakeIcon(Color color, bool recording)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(color, 3);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var fill = new SolidBrush(color);
            if (recording) g.FillEllipse(fill, 9, 9, 14, 14);
            else g.FillEllipse(fill, 11, 11, 10, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------------- recording ----------------

    void Toggle()
    {
        if (_recorder == null) StartRecording();
        else StopRecording();
    }

    void StartRecording()
    {
        try
        {
            _recorder = new Recorder(_settings);
            _recorder.Start();
            if (_settings.PlaySounds) SystemSounds.Asterisk.Play();
            var hint = $"{HotkeyText} ×2 to stop";
            if (_recorder.AudioError != null) hint = "no mic, screen only · " + hint;
            _overlay = new RecordingOverlay(_recorder.CaptureBounds, () => _recorder?.Elapsed ?? TimeSpan.Zero, hint);
            _overlay.Show();
            _maxTimer = new DispatcherTimer(TimeSpan.FromMinutes(_settings.MaxRecordingMinutes), DispatcherPriority.Normal, (_, _) => StopRecording(), Dispatcher);
            _maxTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Write("Start failed: " + ex);
            _recorder = null;
            _tray?.ShowBalloonTip(4000, "ShowTell", "Couldn't start recording: " + ex.Message, WinForms.ToolTipIcon.Error);
        }
        UpdateTray();
    }

    async void StopRecording()
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        _maxTimer?.Stop();
        _overlay?.Close();
        _overlay = null;
        UpdateTray();
        if (_settings.PlaySounds) SystemSounds.Exclamation.Play();

        RecordingResult result;
        try { result = await Task.Run(rec.Stop); }
        catch (Exception ex)
        {
            Log.Write("Stop failed: " + ex);
            _tray?.ShowBalloonTip(4000, "ShowTell", "Recording failed: " + ex.Message, WinForms.ToolTipIcon.Error);
            return;
        }

        if (_detectTask != null) await _detectTask; // usually long done
        var dialog = new SendDialog(result, _settings, _targets);
        bool pasted = false;
        dialog.TargetUsed += t =>
        {
            pasted = true;
            _settings.LastTarget = t.Target.Name;
            _settings.Save();
        };
        dialog.SettingsRequested += OpenSettings;
        dialog.Closed += (_, _) =>
        {
            if (pasted && _settings.DeleteAfterPaste) DeleteRecording(result.Folder);
            Recorder.Prune(_settings.KeepRecordings);
            RefreshTargets();
        };
        dialog.Show();
    }

    /// <summary>Removes a recording's frames, audio and text once it has been pasted (the target app has its own copy).</summary>
    static async void DeleteRecording(string folder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
                Log.Write("Deleted pasted recording " + System.IO.Path.GetFileName(folder));
                return;
            }
            catch (Exception ex) when (attempt < 4) { Log.Write("Delete retry: " + ex.Message); await Task.Delay(1000); }
            catch (Exception ex) { Log.Write("Couldn't delete recording: " + ex.Message); }
        }
    }

    // ---------------- settings ----------------

    SettingsWindow? _settingsWindow;

    void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, _hook!) { Topmost = true };
        _settingsWindow.Loaded += (_, _) => _settingsWindow?.Activate();
        if (_settingsWindow.ShowDialog() == true && _settingsWindow.Result != null)
            ApplySettings(_settingsWindow.Result);
        _settingsWindow = null;
    }

    void ApplySettings(Settings s)
    {
        _settings = s;
        _settings.Save();
        _hook!.Configure(_settings);
        UpdateTray();
        RefreshTargets();
        PrefetchSpeechModel();
    }

    void Quit()
    {
        if (_recorder != null) { try { _recorder.Stop(); } catch { } }
        _tray!.Visible = false;
        _tray.Dispose();
        _hook?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        if (_tray != null) _tray.Visible = false;
        base.OnExit(e);
    }
}
