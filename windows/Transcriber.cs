using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SherpaOnnx;
using Whisper.net;

namespace ShowTell;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

public enum SpeechEngine { Parakeet, Whisper }

/// <summary>A downloadable speech-to-text model. Files go in ModelsDir (Whisper: one .bin; Parakeet: a folder of ONNX files).</summary>
public sealed record SpeechModel(string Id, string Label, SpeechEngine Engine, string BaseUrl, string[] Files)
{
    public string Dir => Engine == SpeechEngine.Whisper ? Settings.ModelsDir : Path.Combine(Settings.ModelsDir, Id);
    public string PathOf(string file) => Path.Combine(Dir, file);
    public bool Exists => Files.All(f => File.Exists(PathOf(f)));
    public override string ToString() => Label;
}

/// <summary>Local speech-to-text. Nothing leaves the machine except the one-time model download.</summary>
public static class Transcriber
{
    const string Hf = "https://huggingface.co";

    static SpeechModel ParakeetModel(string id, string label, string repo) => new(id, label, SpeechEngine.Parakeet,
        $"{Hf}/csukuangfj/{repo}/resolve/main/", new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" });

    static SpeechModel WhisperModel(string name, string label) => new(name, label, SpeechEngine.Whisper,
        $"{Hf}/ggerganov/whisper.cpp/resolve/main/", new[] { $"ggml-{name}.bin" });

    public static readonly SpeechModel[] Models =
    {
        ParakeetModel("parakeet-v2", "Parakeet v2 · English, most accurate (630 MB)", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"),
        ParakeetModel("parakeet-v3", "Parakeet v3 · 25 European languages (640 MB)", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"),
        WhisperModel("tiny.en", "Whisper tiny.en · fastest, least accurate (75 MB)"),
        WhisperModel("base.en", "Whisper base.en · English (142 MB)"),
        WhisperModel("small.en", "Whisper small.en · English (466 MB)"),
        WhisperModel("tiny", "Whisper tiny · any language (75 MB)"),
        WhisperModel("base", "Whisper base · any language (142 MB)"),
        WhisperModel("small", "Whisper small · any language (466 MB)"),
    };

    public const string DefaultModel = "parakeet-v2";

    public static SpeechModel Find(string id) => Models.FirstOrDefault(m => m.Id == id) ?? Models.First(m => m.Id == DefaultModel);

    static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static async Task EnsureModelAsync(SpeechModel model, IProgress<string>? status, CancellationToken ct)
    {
        if (model.Exists) return;
        status?.Report("Downloading speech model…");
        await DownloadLock.WaitAsync(ct); // the background pre-download and a transcription may race
        try { await DownloadAsync(model, status, ct); }
        finally { DownloadLock.Release(); }
    }

    static async Task DownloadAsync(SpeechModel model, IProgress<string>? status, CancellationToken ct)
    {
        if (model.Exists) return;
        Directory.CreateDirectory(model.Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ShowTell/0.2");

        // Sizes first, so the progress percentage covers every file.
        var missing = model.Files.Where(f => !File.Exists(model.PathOf(f))).ToList();
        long total = 0;
        foreach (var f in missing)
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, model.BaseUrl + f), ct);
            total += head.Content.Headers.ContentLength ?? 0;
        }

        long done = 0; int lastPct = -1;
        foreach (var f in missing)
        {
            var url = model.BaseUrl + f;
            Log.Write("Downloading " + url);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var path = model.PathOf(f);
            var tmp = path + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != lastPct) { lastPct = pct; status?.Report(pct >= 0 ? $"Downloading speech model… {pct}%" : $"Downloading speech model… {done >> 20} MB"); }
                }
            }
            File.Move(tmp, path, true);
        }
    }

    /// <summary>Loudest one-second RMS of a 16-bit WAV, 0..1. Used to skip transcription of silent recordings.</summary>
    public static double Loudness(string wavPath)
    {
        using var r = new WaveFileReader(wavPath);
        var buf = new byte[r.WaveFormat.AverageBytesPerSecond];
        double peakWindow = 0; int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
        {
            double winSum = 0; int winCount = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                double v = BitConverter.ToInt16(buf, i) / 32768.0;
                winSum += v * v; winCount++;
            }
            if (winCount > 0) peakWindow = Math.Max(peakWindow, Math.Sqrt(winSum / winCount));
        }
        return peakWindow;
    }

    public static async Task<List<TranscriptSegment>> TranscribeAsync(string wavPath, Settings s, IProgress<string>? status, CancellationToken ct)
    {
        var result = new List<TranscriptSegment>();
        if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 16000) return result; // < 0.5 s of audio

        if (Loudness(wavPath) < 0.004)
        {
            Log.Write("Audio is silent; skipping transcription");
            return result;
        }

        var model = Find(s.SpeechModel);
        if (model.Engine == SpeechEngine.Whisper)
        {
            var dll = WhisperNativeLibrary();
            // whisper.cpp's native loader aborts the whole process when its DLL paths get near MAX_PATH.
            if (dll.Length > 200)
                throw new InvalidOperationException("ShowTell is installed in a folder with a very long path, which breaks Whisper. Move it somewhere shorter, e.g. %LOCALAPPDATA%\\Programs\\ShowTell.");
        }

        await EnsureModelAsync(model, status, ct);
        status?.Report("Transcribing…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segments = await Task.Run(() => model.Engine == SpeechEngine.Parakeet
            ? TranscribeParakeet(model, wavPath, ct)
            : TranscribeWhisperAsync(model, wavPath, s, ct), ct);
        Log.Write($"Transcribed with {model.Id} in {sw.Elapsed.TotalSeconds:0.0}s: {segments.Count} segments");
        return segments;
    }

    // ---------------- Parakeet (sherpa-onnx) ----------------

    const int Rate = 16000;

    static Task<List<TranscriptSegment>> TranscribeParakeet(SpeechModel model, string wavPath, CancellationToken ct)
    {
        var samples = ReadSamples(wavPath);
        // Inaudible dither (about -80 dB). Stretches of exact digital silence (muted mics, some drivers) make
        // Parakeet skip nearby speech; real room noise never does.
        var rng = new Random(7);
        for (int i = 0; i < samples.Length; i++) samples[i] += (float)((rng.NextDouble() - 0.5) * 2e-4);
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = model.PathOf("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = model.PathOf("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = model.PathOf("joiner.int8.onnx");
        config.ModelConfig.Tokens = model.PathOf("tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        Log.Write("Loading Parakeet model " + model.Id);
        using var recognizer = new OfflineRecognizer(config);
        var segments = new List<TranscriptSegment>();
        foreach (var (start, length) in Chunks(samples))
        {
            ct.ThrowIfCancellationRequested();
            var chunk = new float[length];
            Array.Copy(samples, start, chunk, 0, length);
            if (Rms(chunk, 0, chunk.Length) < 0.002) continue; // silent stretch

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(Rate, chunk);
            recognizer.Decode(stream);
            var r = stream.Result;
            if (Environment.GetCommandLineArgs().Contains("--selftest"))
                Log.Write($"parakeet chunk {start / (double)Rate:0.0}-{(start + length) / (double)Rate:0.0}s: {r.Text} | tokens={string.Join("|", r.Tokens ?? Array.Empty<string>())}");
            double offset = (double)start / Rate, chunkEnd = (double)(start + length) / Rate;
            segments.AddRange(Sentences(r.Text, r.Tokens, r.Timestamps, offset, chunkEnd));
        }
        return Task.FromResult(segments);
    }

    static float[] ReadSamples(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var sp = reader.ToSampleProvider();
        var all = new List<float>((int)(reader.SampleCount));
        var buf = new float[Rate];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) all.Add(buf[i]);
        return all.ToArray();
    }

    static double Rms(float[] x, int start, int count)
    {
        double sum = 0;
        for (int i = start; i < start + count; i++) sum += x[i] * x[i];
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>Splits long audio into pieces of at most 20 s, cutting at the quietest moment between 10 and 20 s.</summary>
    static IEnumerable<(int Start, int Length)> Chunks(float[] x)
    {
        int max = 20 * Rate, min = 10 * Rate, win = Rate / 10;
        int pos = 0;
        while (pos < x.Length)
        {
            if (x.Length - pos <= max) { yield return (pos, x.Length - pos); yield break; }
            int best = pos + max; double bestRms = double.MaxValue;
            for (int i = pos + min; i + win <= pos + max; i += win / 2)
            {
                double r = Rms(x, i, win);
                if (r < bestRms) { bestRms = r; best = i + win / 2; }
            }
            yield return (pos, best - pos);
            pos = best;
        }
    }

    /// <summary>Groups Parakeet's tokens into sentences, each stamped with the time of its first token.</summary>
    static IEnumerable<TranscriptSegment> Sentences(string text, string[]? tokens, float[]? times, double offset, double chunkEnd)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) yield break;
        if (tokens == null || times == null || tokens.Length == 0 || tokens.Length != times.Length)
        {
            yield return new TranscriptSegment(TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(chunkEnd), text);
            yield break;
        }

        var sb = new StringBuilder();
        double segStart = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i].Replace('▁', ' '); // SentencePiece word marker
            if (segStart < 0) segStart = offset + times[i];
            sb.Append(tok);
            bool sentenceEnd = Regex.IsMatch(tok.TrimEnd(), @"[.?!]$");
            bool last = i == tokens.Length - 1;
            // Break at sentence ends, or at a word boundary once a sentence runs past ~15 s.
            bool tooLong = i + 1 < tokens.Length && (tokens[i + 1].StartsWith('▁') || tokens[i + 1].StartsWith(' ')) && offset + times[i + 1] - segStart > 15;
            if (sentenceEnd || last || tooLong)
            {
                double end = last ? chunkEnd : offset + times[i + 1];
                var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                if (s.Length > 0) yield return new TranscriptSegment(TimeSpan.FromSeconds(segStart), TimeSpan.FromSeconds(end), s);
                sb.Clear();
                segStart = -1;
            }
        }
    }

    // ---------------- Whisper (whisper.cpp) ----------------

    static readonly Regex Junk = new(@"^\s*[\[\(].*[\]\)]\s*$", RegexOptions.Compiled); // [BLANK_AUDIO], (music) …

    /// <summary>
    /// Path of whisper.dll. Next to the exe in a normal install; in the single-file ShowTell.exe the native DLLs are
    /// unpacked to a temp folder that Whisper.net doesn't search, so point it there.
    /// </summary>
    static string WhisperNativeLibrary()
    {
        const string rel = @"runtimes\win-x64\whisper.dll";
        var local = Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(local)) return local;
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, rel);
            if (!File.Exists(p)) continue;
            // Whisper.net searches <folder of LibraryPath>\runtimes\<variant>\win-x64, so give it a file in the unpack root.
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(d, "whisper.dll");
            return p;
        }
        return local;
    }

    static async Task<List<TranscriptSegment>> TranscribeWhisperAsync(SpeechModel model, string wavPath, Settings s, CancellationToken ct)
    {
        var result = new List<TranscriptSegment>();
        using var logs = Whisper.net.Logger.LogProvider.AddLogger((level, msg) =>
        {
            if (level <= Whisper.net.Logger.WhisperLogLevel.Warning || Environment.GetCommandLineArgs().Contains("--selftest"))
                Log.Write($"whisper[{level}]: {msg?.TrimEnd()}");
        });
        Log.Write("Loading Whisper model " + model.Id);
        using var factory = WhisperFactory.FromPath(model.PathOf(model.Files[0]));
        bool englishOnly = model.Id.EndsWith(".en");
        var builder = factory.CreateBuilder().WithThreads(Math.Max(1, Environment.ProcessorCount - 1));
        builder = englishOnly || s.Language != "auto"
            ? builder.WithLanguage(englishOnly ? "en" : s.Language)
            : builder.WithLanguageDetection();
        await using var processor = builder.Build();
        await using var fs = File.OpenRead(wavPath);
        await foreach (var seg in processor.ProcessAsync(fs, ct))
        {
            var text = seg.Text.Trim();
            if (text.Length == 0 || Junk.IsMatch(text)) continue;
            result.Add(new TranscriptSegment(seg.Start, seg.End, text));
        }
        return result;
    }

    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
