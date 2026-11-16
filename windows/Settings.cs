using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShowTell;

/// <summary>A desktop AI app ShowTell can paste into.</summary>
public sealed class TargetApp
{
    public string Name { get; set; } = "";
    /// <summary>Regex matched against Start-menu display names.</summary>
    public string DisplayNamePattern { get; set; } = "";
    /// <summary>Regex matched against the AppUserModelID (catches MSIX packages that use a different display name).</summary>
    public string AppIdPattern { get; set; } = "";
    /// <summary>Process names (without .exe) that own the app's main window.</summary>
    public List<string> ProcessNames { get; set; } = new();
    /// <summary>Accessible names of the "new chat" button, tried in order.</summary>
    public List<string> NewChatButtonNames { get; set; } = new() { "New chat" };
    /// <summary>Keyboard shortcut used when no new-chat button is found, e.g. "Ctrl+Shift+O". Empty = none.</summary>
    public string NewChatShortcut { get; set; } = "";
}

public enum CaptureArea { MonitorUnderCursor, PrimaryMonitor, AllMonitors }

public sealed class Settings
{
    // Hotkey: both keys pressed together, twice within DoubleTapWindowMs.
    public int KeyA { get; set; } = 0x70; // VK_F1
    public int KeyB { get; set; } = 0x71; // VK_F2
    public int DoubleTapWindowMs { get; set; } = 500;
    /// <summary>Hold back F-key presses for a moment so the chord doesn't also trigger F1 Help / F2 Rename.</summary>
    public bool SwallowChordKeys { get; set; } = true;

    // Recording
    public CaptureArea CaptureArea { get; set; } = CaptureArea.MonitorUnderCursor;
    public int SampleIntervalMs { get; set; } = 500;
    public int MaxFrames { get; set; } = 12;
    /// <summary>Keep a frame at least this often even if the screen barely changed.</summary>
    public int HeartbeatSeconds { get; set; } = 8;
    /// <summary>Percent of the screen that must change before a frame counts as a new key frame.</summary>
    public double ChangeThreshold { get; set; } = 0.4;
    public int MaxImageLongEdge { get; set; } = 1568;
    public bool DrawCursor { get; set; } = true;
    public string MicDeviceId { get; set; } = ""; // "" = Windows default microphone
    public bool PlaySounds { get; set; } = false; // chime when recording starts/stops
    public bool DeleteAfterPaste { get; set; } = true; // remove frames + audio once they are pasted
    public int MaxRecordingMinutes { get; set; } = 15;

    // Transcription
    public string SpeechModel { get; set; } = Transcriber.DefaultModel; // see Transcriber.Models
    public string Language { get; set; } = "auto";

    // Pasting
    public int PasteDelayMs { get; set; } = 700;
    public string LastTarget { get; set; } = "";
    public int KeepRecordings { get; set; } = 20;

    public List<TargetApp> Targets { get; set; } = DefaultTargets();

    public static List<TargetApp> DefaultTargets() => new()
    {
        new TargetApp
        {
            Name = "Claude",
            DisplayNamePattern = "^Claude$",
            AppIdPattern = "^Claude_|AnthropicClaude",
            ProcessNames = new() { "claude" },
            NewChatButtonNames = new() { "New chat", "New" },
        },
        new TargetApp
        {
            Name = "Codex",
            DisplayNamePattern = "^(Codex|ChatGPT)$",
            AppIdPattern = "^OpenAI\\.(Codex|ChatGPT)",
            ProcessNames = new() { "ChatGPT", "Codex" },
            NewChatButtonNames = new() { "New chat", "New thread", "New task" },
            NewChatShortcut = "Ctrl+Shift+O",
        },
        new TargetApp
        {
            Name = "Grok",
            DisplayNamePattern = "^Grok",
            AppIdPattern = "Grok",
            ProcessNames = new() { "Grok", "Grok Bot" },
            NewChatButtonNames = new() { "New chat", "New Chat", "New conversation", "Chat" },
        },
    };

    // ---------- persistence ----------

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowTell");
    public static string RecordingsDir => Path.Combine(DataDir, "Recordings");
    public static string ModelsDir => Path.Combine(DataDir, "Models");
    static string SettingsPath => Path.Combine(DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed, using defaults: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;
}

public static class Log
{
    static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(Path.Combine(Settings.DataDir, "showtell.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
