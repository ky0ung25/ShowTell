using System.IO;
using System.Text;

namespace ShowTell;

public static class PromptBuilder
{
    public static string Build(string instructions, RecordingResult rec, IReadOnlyList<KeyFrame> frames,
                               IReadOnlyList<TranscriptSegment>? transcript, string? transcriptError)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(instructions))
            sb.AppendLine(instructions.Trim()).AppendLine();

        sb.AppendLine($"[Screen recording, {Transcriber.Format(rec.Duration)} long. {frames.Count} key frame{(frames.Count == 1 ? "" : "s")} from it are attached in time order.]");
        if (frames.Count > 0)
            sb.AppendLine("Frame times: " + string.Join(", ", frames.Select((f, i) => $"#{i + 1} @ {Transcriber.Format(f.At)}")));
        sb.AppendLine();

        if (rec.AudioError != null)
            sb.AppendLine($"Voice transcript: (microphone unavailable: {rec.AudioError})");
        else if (transcriptError != null)
            sb.AppendLine($"Voice transcript: (transcription failed: {transcriptError})");
        else if (transcript == null || transcript.Count == 0)
            sb.AppendLine("Voice transcript: (no speech detected)");
        else
        {
            sb.AppendLine("Voice transcript (auto-generated, may contain small errors):");
            foreach (var seg in transcript)
                sb.AppendLine($"[{Transcriber.Format(seg.Start)}] {seg.Text}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Brings the target app forward, opens the chosen thread, and pastes frames + text into its input box. Never presses Enter.</summary>
public static class Sender
{
    public static async Task SendAsync(InstalledTarget target, ThreadChoice thread, IReadOnlyList<KeyFrame> frames,
                                       string text, Settings s, IProgress<string> status, CancellationToken ct)
    {
        await Automation.WaitForKeysReleasedAsync();

        var hwnd = await Automation.LaunchAndWaitAsync(target, status, ct);
        status.Report($"Switching to {target.Target.Name}…");
        Native.ForceForeground(hwnd);
        await Task.Delay(350, ct);

        switch (thread.Kind)
        {
            case ThreadKind.NewChat:
                if (!await Task.Run(() => Automation.StartNewChat(hwnd, target.Target), ct))
                    Log.Write("No new-chat button or shortcut found; pasting into the current chat");
                await Task.Delay(1200, ct);
                break;
            case ThreadKind.Existing:
                if (!await Task.Run(() => Automation.OpenThread(hwnd, thread.Name!), ct))
                    throw new InvalidOperationException($"Couldn't find the chat \"{thread.Name}\" in {target.Target.Name}'s sidebar. Is the sidebar open?");
                await Task.Delay(1500, ct);
                break;
        }

        Native.ForceForeground(hwnd);
        await Task.Delay(150, ct);
        if (!await Task.Run(() => Automation.FocusComposer(hwnd), ct))
            Log.Write("Composer not found via UI Automation; relying on the app's default focus");
        await Task.Delay(250, ct);

        int i = 0;
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            EnsureForeground(hwnd, target);
            status.Report($"Pasting frame {++i} of {frames.Count}…");
            Automation.SetClipboardImage(f.Path);
            await Task.Delay(60, ct);
            Automation.PressPaste();
            await Task.Delay(s.PasteDelayMs, ct);
        }

        EnsureForeground(hwnd, target);
        status.Report("Pasting instructions…");
        Automation.SetClipboardText(text);
        await Task.Delay(60, ct);
        Automation.PressPaste();
        await Task.Delay(200, ct);
        Log.Write($"Pasted {frames.Count} frames + {text.Length} chars into {target.Target.Name} ({thread.Kind})");
    }

    static void EnsureForeground(IntPtr hwnd, InstalledTarget target)
    {
        if (Automation.IsTargetForeground(hwnd)) return;
        Native.ForceForeground(hwnd);
        Thread.Sleep(200);
        if (!Automation.IsTargetForeground(hwnd))
            throw new InvalidOperationException($"{target.Target.Name} lost focus, so pasting stopped. Click into its message box and try again.");
    }

    public static void SaveArtifacts(RecordingResult rec, string prompt, IReadOnlyList<TranscriptSegment>? transcript)
    {
        try
        {
            File.WriteAllText(Path.Combine(rec.Folder, "prompt.txt"), prompt);
            if (transcript != null)
                File.WriteAllLines(Path.Combine(rec.Folder, "transcript.txt"),
                    transcript.Select(t => $"[{Transcriber.Format(t.Start)} - {Transcriber.Format(t.End)}] {t.Text}"));
        }
        catch (Exception ex) { Log.Write("SaveArtifacts failed: " + ex.Message); }
    }
}
