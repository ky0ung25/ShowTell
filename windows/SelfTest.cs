using System.IO;
using System.Text;

namespace ShowTell;

/// <summary>
/// `ShowTell.exe --selftest [record] [targets] [transcribe=path.wav]` â€” checks the parts that can be verified
/// without a human at the keyboard and writes a report to %LOCALAPPDATA%\ShowTell\selftest.txt.
/// </summary>
public static class SelfTest
{
    static readonly StringBuilder Out = new();
    static int _failures;

    static void Check(bool ok, string what)
    {
        Out.AppendLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    public static int Run()
    {
        var args = Environment.GetCommandLineArgs();
        try
        {
            ChordTests();
            FrameSelectionTests();
            PromptTests();
            if (args.Contains("targets")) TargetTests();
            if (args.Contains("record")) RecordTest();
            var t = args.FirstOrDefault(a => a.StartsWith("transcribe="));
            var m = args.FirstOrDefault(a => a.StartsWith("model="));
            if (t != null) TranscribeTest(t["transcribe=".Length..], m?["model=".Length..]);
        }
        catch (Exception ex)
        {
            Out.AppendLine("CRASH " + ex);
            _failures++;
        }
        Out.AppendLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        Directory.CreateDirectory(Settings.DataDir);
        File.WriteAllText(Path.Combine(Settings.DataDir, "selftest.txt"), Out.ToString());
        return _failures == 0 ? 0 : 1;
    }

    static void ChordTests()
    {
        const int F1 = 0x70, F2 = 0x71;
        var d = new ChordDetector { KeyA = F1, KeyB = F2, WindowMs = 500, Swallow = true };
        int toggles = 0, graceRequests = 0;
        var replays = new List<(int, bool)>();
        d.Toggled += () => toggles++;
        d.Replay = (vk, up) => replays.Add((vk, up));
        d.StartGraceTimer = _ => graceRequests++;

        // Double chord within the window â†’ one toggle, everything swallowed.
        bool all = true;
        all &= d.OnKey(F1, false, 0);
        all &= d.OnKey(F2, false, 20);
        all &= d.OnKey(F1, true, 90);
        all &= d.OnKey(F2, true, 95);
        all &= d.OnKey(F2, false, 200);
        all &= d.OnKey(F1, false, 215);
        all &= d.OnKey(F2, true, 280);
        all &= d.OnKey(F1, true, 290);
        Check(toggles == 1, "double chord toggles once");
        Check(all, "chord keys are swallowed");
        Check(replays.Count == 0, "no keys leak during a chord");

        // Same thing again = second toggle (stop).
        d.OnKey(F1, false, 2000); d.OnKey(F2, false, 2010); d.OnKey(F1, true, 2060); d.OnKey(F2, true, 2070);
        d.OnKey(F1, false, 2300); d.OnKey(F2, false, 2305); d.OnKey(F1, true, 2350); d.OnKey(F2, true, 2360);
        Check(toggles == 2, "second double chord toggles again");

        // Two chords too far apart â†’ nothing.
        d.OnKey(F1, false, 5000); d.OnKey(F2, false, 5010); d.OnKey(F1, true, 5050); d.OnKey(F2, true, 5060);
        d.OnKey(F1, false, 5800); d.OnKey(F2, false, 5810); d.OnKey(F1, true, 5850); d.OnKey(F2, true, 5860);
        Check(toggles == 2, "slow chords don't toggle");

        // A lone quick F2 tap is delivered to the system unchanged.
        replays.Clear();
        d.Reset();
        bool sw1 = d.OnKey(F2, false, 9000);
        bool sw2 = d.OnKey(F2, true, 9040);
        Check(sw1 && sw2 && replays.SequenceEqual(new[] { (F2, false), (F2, true) }), "lone F2 tap is replayed (F2 rename still works)");

        // A held lone F1 is released after the grace period and its key-up passes through.
        replays.Clear();
        d.OnKey(F1, false, 10000);
        d.OnGraceElapsed();
        bool upSwallowed = d.OnKey(F1, true, 10500);
        Check(replays.SequenceEqual(new[] { (F1, false) }) && !upSwallowed, "held lone F1 is released after grace");

        // Unrelated keys are never touched.
        Check(!d.OnKey(0x41, false, 11000) && !d.OnKey(0x41, true, 11010), "other keys pass through");

        // Swallow off â†’ nothing swallowed, still toggles.
        var d2 = new ChordDetector { KeyA = F1, KeyB = F2, Swallow = false };
        int t2 = 0; d2.Toggled += () => t2++;
        bool any = false;
        foreach (var (vk, up, ms) in new[] { (F1, false, 0), (F2, false, 10), (F1, true, 50), (F2, true, 60), (F1, false, 200), (F2, false, 210), (F1, true, 260), (F2, true, 270) })
            any |= d2.OnKey(vk, up, ms);
        Check(t2 == 1 && !any, "swallow off: toggles without blocking keys");

        // Modifier keys as the chord are never swallowed.
        var d3 = new ChordDetector { KeyA = 0xA2, KeyB = 0xA4, Swallow = true };
        Check(!d3.OnKey(0xA2, false, 0), "modifier chord keys are not swallowed");
    }

    static void FrameSelectionTests()
    {
        var c = Enumerable.Range(0, 30).Select(i => new KeyFrame($"f{i}", TimeSpan.FromSeconds(i), i == 17 ? 90 : i % 5)).ToList();
        var s = Recorder.SelectFrames(c, 6);
        Check(s.Count == 6, "frame selection respects the cap");
        Check(s[0] == c[0] && s[^1] == c[^1], "first and last frames kept");
        Check(s.Contains(c[17]), "biggest change kept");
        Check(s.Zip(s.Skip(1)).All(p => p.First.At < p.Second.At), "frames stay in time order");
    }

    static void PromptTests()
    {
        var rec = new RecordingResult { Folder = "x", Duration = TimeSpan.FromSeconds(42), Frames = new() };
        var frames = new List<KeyFrame> { new("a", TimeSpan.Zero, 1), new("b", TimeSpan.FromSeconds(5), 1) };
        var p = PromptBuilder.Build("Copy this style", rec, frames,
            new List<TranscriptSegment> { new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "I like this header.") }, null);
        Check(p.StartsWith("Copy this style"), "prompt starts with instructions");
        Check(p.Contains("#2 @ 0:05") && p.Contains("[0:01] I like this header."), "prompt has frame times and transcript");
        Out.AppendLine("---- sample prompt ----\n" + p + "\n-----------------------");
    }

    static void TargetTests()
    {
        var s = Settings.Load();
        var found = AppCatalog.Detect(s);
        Out.AppendLine("Installed targets: " + string.Join(", ", found.Select(f => $"{f.Target.Name} [{f.DisplayName} | {f.AppId}]")));
        foreach (var t in found)
        {
            var h = Automation.FindMainWindow(t.Target);
            if (h == IntPtr.Zero) { Out.AppendLine($"  {t.Target.Name}: not running"); continue; }
            Out.AppendLine($"  {t.Target.Name}: current = \"{Automation.CurrentTitle(h)}\", input box = \"{Automation.FindComposer(h)?.Current.Name ?? "(not found)"}\"");
            foreach (var th in Automation.ListThreads(h)) Out.AppendLine("    - " + th);
        }
        Check(found.Count > 0, "at least one AI app detected");
    }

    static void RecordTest()
    {
        var s = Settings.Load();
        var r = new Recorder(s);
        r.Start();
        Thread.Sleep(3000);
        var res = r.Stop();
        Out.AppendLine($"Recorded {res.Duration}: {res.Frames.Count} frames, audio={res.AudioPath ?? res.AudioError} folder={res.Folder}");
        Check(res.Frames.Count >= 1 && res.Frames.All(f => File.Exists(f.Path)), "screen frames captured");
        Check(res.AudioPath != null && new FileInfo(res.AudioPath).Length > 44, "microphone audio captured");
    }

    static void TranscribeTest(string wav, string? model)
    {
        var s = Settings.Load();
        if (!string.IsNullOrEmpty(model)) s.SpeechModel = model; // e.g. model=base.en to test Whisper
        Out.AppendLine($"Speech model: {Transcriber.Find(s.SpeechModel).Id}");
        // Run off the UI thread: blocking the dispatcher while awaiting on it would deadlock.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segs = Task.Run(() => Transcriber.TranscribeAsync(wav, s, null, CancellationToken.None)).GetAwaiter().GetResult();
        Out.AppendLine($"Transcribed in {sw.Elapsed.TotalSeconds:F1}s (includes loading the model):");
        foreach (var seg in segs) Out.AppendLine($"  [{Transcriber.Format(seg.Start)}] {seg.Text}");
        Check(segs.Count > 0, "transcription produced text");
    }
}
