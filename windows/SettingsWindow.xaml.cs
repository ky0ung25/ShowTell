using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ShowTell;

public partial class SettingsWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly Settings _draft;
    readonly KeyboardHook _hook;
    List<(string Id, string Name)> _mics = new();

    /// <summary>The saved settings, or null if the user cancelled.</summary>
    public Settings? Result { get; private set; }

    public SettingsWindow(Settings current, KeyboardHook hook)
    {
        InitializeComponent();
        _draft = current.Clone();
        _hook = hook;

        RefreshKeyButtons();
        KeyABtn.Click += (_, _) => CaptureKey(true);
        KeyBBtn.Click += (_, _) => CaptureKey(false);
        WindowMs.Text = _draft.DoubleTapWindowMs.ToString();
        Swallow.IsChecked = _draft.SwallowChordKeys;

        Area.ItemsSource = new[] { "Monitor under the mouse", "Primary monitor", "All monitors" };
        Area.SelectedIndex = (int)_draft.CaptureArea;

        _mics = Recorder.ListMics();
        var mics = new List<string> { "Windows default" };
        mics.AddRange(_mics.Select(m => m.Name));
        Mic.ItemsSource = mics;
        int micIdx = _mics.FindIndex(m => m.Id == _draft.MicDeviceId);
        Mic.SelectedIndex = micIdx < 0 ? 0 : micIdx + 1;
        DeleteAfter.IsChecked = _draft.DeleteAfterPaste;
        Sounds.IsChecked = _draft.PlaySounds;

        MaxFrames.Text = _draft.MaxFrames.ToString();
        MaxMinutes.Text = _draft.MaxRecordingMinutes.ToString();
        DrawCursor.IsChecked = _draft.DrawCursor;

        Model.ItemsSource = Transcriber.Models.Select(m => m.Label + (m.Exists ? "  ✓ downloaded" : "")).ToList();
        Model.SelectedIndex = Math.Max(0, Array.IndexOf(Transcriber.Models, Transcriber.Find(_draft.SpeechModel)));
        LanguageBox.Text = _draft.Language;

        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            AutoStart.IsChecked = k?.GetValue("ShowTell") != null;

        EditJson.Click += (_, _) =>
        {
            current.Save();
            Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Settings.DataDir, "settings.json")) { UseShellExecute = true });
        };
        SaveBtn.Click += (_, _) => Save();
        Closed += (_, _) => _hook.CaptureNextKey = null;
    }

    void RefreshKeyButtons()
    {
        KeyABtn.Content = KeyboardHook.KeyName(_draft.KeyA);
        KeyBBtn.Content = KeyboardHook.KeyName(_draft.KeyB);
    }

    void CaptureKey(bool first)
    {
        (first ? KeyABtn : KeyBBtn).Content = "Press a key…";
        _hook.CaptureNextKey = vk =>
        {
            if (vk == 0x1B) { RefreshKeyButtons(); return; } // Esc cancels
            if (first) _draft.KeyA = vk; else _draft.KeyB = vk;
            RefreshKeyButtons();
        };
    }

    void Save()
    {
        if (_draft.KeyA == _draft.KeyB)
        {
            MessageBox.Show(this, "Pick two different keys.", "ShowTell");
            return;
        }
        _draft.DoubleTapWindowMs = ParseInt(WindowMs.Text, _draft.DoubleTapWindowMs, 200, 1500);
        _draft.SwallowChordKeys = Swallow.IsChecked == true;
        _draft.CaptureArea = (CaptureArea)Math.Max(0, Area.SelectedIndex);
        _draft.MicDeviceId = Mic.SelectedIndex > 0 && Mic.SelectedIndex - 1 < _mics.Count ? _mics[Mic.SelectedIndex - 1].Id : "";
        _draft.DeleteAfterPaste = DeleteAfter.IsChecked == true;
        _draft.PlaySounds = Sounds.IsChecked == true;
        _draft.MaxFrames = ParseInt(MaxFrames.Text, _draft.MaxFrames, 1, 50);
        _draft.MaxRecordingMinutes = ParseInt(MaxMinutes.Text, _draft.MaxRecordingMinutes, 1, 120);
        _draft.DrawCursor = DrawCursor.IsChecked == true;
        _draft.SpeechModel = Transcriber.Models[Math.Max(0, Model.SelectedIndex)].Id;
        _draft.Language = string.IsNullOrWhiteSpace(LanguageBox.Text) ? "auto" : LanguageBox.Text.Trim();

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (AutoStart.IsChecked == true) k.SetValue("ShowTell", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("ShowTell", false);
        }

        Result = _draft;
        DialogResult = true;
    }

    static int ParseInt(string s, int fallback, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;
}
