using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinForms = System.Windows.Forms;

namespace ShowTell;

public sealed record KeyFrame(string Path, TimeSpan At, double Score);

public sealed class RecordingResult
{
    public required string Folder { get; init; }
    public required TimeSpan Duration { get; init; }
    public required List<KeyFrame> Frames { get; init; }
    public string? AudioPath { get; init; }
    public string? AudioError { get; init; }
}

/// <summary>
/// Records the microphone (WASAPI, converted to a 16 kHz mono WAV) and samples the screen a few times per second,
/// keeping only frames where something visibly changed (plus a periodic heartbeat frame).
/// </summary>
public sealed class Recorder
{
    readonly Settings _s;
    readonly string _folder;
    readonly Stopwatch _clock = new();
    readonly List<KeyFrame> _candidates = new();
    Thread? _screenThread;
    volatile bool _running;
    WasapiCapture? _capture;
    readonly ManualResetEventSlim _captureStopped = new(false);
    string? _rawPath;
    int _channels, _bits;
    bool _isFloat;
    WaveFileWriter? _writer;
    string? _audioError;
    Rectangle _area;

    public Rectangle CaptureBounds => _area;
    public string? AudioError => _audioError;
    public TimeSpan Elapsed => _clock.Elapsed;

    public Recorder(Settings settings)
    {
        _s = settings;
        _folder = Path.Combine(Settings.RecordingsDir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        Directory.CreateDirectory(Path.Combine(_folder, "frames"));
    }

    public void Start()
    {
        _area = ResolveArea(_s.CaptureArea);
        _running = true;
        _clock.Start();
        StartAudio();
        _screenThread = new Thread(ScreenLoop) { IsBackground = true, Name = "ShowTell screen sampler", Priority = ThreadPriority.AboveNormal };
        _screenThread.Start();
        Log.Write($"Recording started: area={_area} folder={_folder}");
    }

    public RecordingResult Stop()
    {
        _running = false;
        _screenThread?.Join(3000);
        var duration = _clock.Elapsed;
        _clock.Stop();
        StopAudio();

        // One final frame so the end state is always included.
        try { SaveFrame(Capture(), duration, double.MaxValue / 2); } catch (Exception ex) { Log.Write("final frame failed: " + ex.Message); }

        var chosen = SelectFrames(_candidates, _s.MaxFrames);
        foreach (var f in _candidates.Except(chosen))
            TryDelete(f.Path);

        Log.Write($"Recording stopped: {duration}, {_candidates.Count} candidates -> {chosen.Count} frames");
        return new RecordingResult
        {
            Folder = _folder,
            Duration = duration,
            Frames = chosen,
            AudioPath = _audioError == null ? Path.Combine(_folder, "audio.wav") : null,
            AudioError = _audioError,
        };
    }

    /// <summary>Always keep the first and last frame, then the biggest visual changes, returned in time order.</summary>
    public static List<KeyFrame> SelectFrames(List<KeyFrame> candidates, int max)
    {
        if (candidates.Count <= max) return candidates.ToList();
        var first = candidates[0];
        var last = candidates[^1];
        var middle = candidates.Skip(1).Take(candidates.Count - 2)
            .OrderByDescending(c => c.Score)
            .Take(Math.Max(0, max - 2));
        return new[] { first, last }.Concat(middle).OrderBy(c => c.At).ToList();
    }

    // ---------------- audio ----------------
    // WASAPI, not WinMM: WinMM keeps the device list from when the process started, so a mic plugged in
    // (or switched on) later is never seen. WASAPI asks Windows for the current device every recording.

    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    void StartAudio()
    {
        // NAudio's WASAPI objects must be created on an MTA thread; Start() runs on the UI (STA) thread.
        Task.Run(StartAudioCore).Wait();
    }

    void StartAudioCore()
    {
        try
        {
            var dev = FindMic(_s.MicDeviceId) ?? throw new InvalidOperationException("No microphone found");
            _capture = new WasapiCapture(dev, true, 100);
            var fmt = _capture.WaveFormat;
            _channels = fmt.Channels;
            _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
            _bits = fmt.BitsPerSample;
            _rawPath = Path.Combine(_folder, "audio_raw.wav");
            _writer = new WaveFileWriter(_rawPath, WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, 1));
            _captureStopped.Reset();
            _capture.DataAvailable += (_, e) => WriteMono(e.Buffer, e.BytesRecorded);
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Write("Mic stopped with error: " + e.Exception.Message);
                _captureStopped.Set();
            };
            _capture.StartRecording();
            Log.Write($"Mic: {dev.FriendlyName} ({fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.BitsPerSample}-bit {(_isFloat ? "float" : "PCM")})");
        }
        catch (Exception ex)
        {
            _audioError = ex.Message;
            Log.Write("Audio start failed: " + ex);
            _writer?.Dispose();
            _writer = null;
            _capture?.Dispose();
            _capture = null;
        }
    }

    /// <summary>Active capture device by id, falling back to the Windows default microphone.</summary>
    public static MMDevice? FindMic(string? id)
    {
        var en = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return d; }
            catch { }
            Log.Write("Chosen microphone not available; using the Windows default");
        }
        foreach (var role in new[] { Role.Communications, Role.Console })
            if (en.HasDefaultAudioEndpoint(DataFlow.Capture, role))
                return en.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault();
    }

    /// <summary>(id, name) of every active microphone, for the settings list.</summary>
    public static List<(string Id, string Name)> ListMics()
    {
        return Task.Run(() =>
        {
            var list = new List<(string, string)>();
            try
            {
                foreach (var d in new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    list.Add((d.ID, d.FriendlyName));
            }
            catch (Exception ex) { Log.Write("ListMics failed: " + ex.Message); }
            return list;
        }).Result;
    }

    void WriteMono(byte[] buf, int count)
    {
        int bytesPerSample = _bits / 8;
        int frameBytes = bytesPerSample * _channels;
        if (frameBytes == 0) return;
        int frames = count / frameBytes;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                int o = f * frameBytes + c * bytesPerSample;
                sum += _isFloat ? BitConverter.ToSingle(buf, o) : _bits switch
                {
                    16 => BitConverter.ToInt16(buf, o) / 32768f,
                    24 => ((buf[o] << 8) | (buf[o + 1] << 16) | (buf[o + 2] << 24)) / 2147483648f,
                    32 => BitConverter.ToInt32(buf, o) / 2147483648f,
                    _ => 0f,
                };
            }
            mono[f] = sum / _channels;
        }
        lock (this) _writer?.WriteSamples(mono, 0, frames);
    }

    void StopAudio()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _captureStopped.Wait(2000);
        lock (this)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _capture.Dispose();
        _capture = null;

        // Convert to exactly what Whisper wants: 16 kHz, mono, 16-bit.
        try
        {
            using (var reader = new WaveFileReader(_rawPath!))
                WaveFileWriter.CreateWaveFile16(Path.Combine(_folder, "audio.wav"), new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000));
            TryDelete(_rawPath!);
        }
        catch (Exception ex)
        {
            _audioError = "couldn't convert audio: " + ex.Message;
            Log.Write("Audio convert failed: " + ex);
        }
    }

    // ---------------- screen ----------------

    static Rectangle ResolveArea(CaptureArea mode)
    {
        switch (mode)
        {
            case CaptureArea.AllMonitors:
                return WinForms.SystemInformation.VirtualScreen;
            case CaptureArea.PrimaryMonitor:
                return WinForms.Screen.PrimaryScreen!.Bounds;
            default:
                Native.GetCursorPos(out var p);
                return WinForms.Screen.FromPoint(new Point(p.X, p.Y)).Bounds;
        }
    }

    void ScreenLoop()
    {
        byte[]? lastThumb = null;
        var lastKept = TimeSpan.Zero;
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, _s.SampleIntervalMs));
        var next = TimeSpan.Zero;

        while (_running)
        {
            var now = _clock.Elapsed;
            if (now < next) { Thread.Sleep(Math.Max(1, (int)(next - now).TotalMilliseconds)); continue; }
            next = now + interval;

            try
            {
                using var bmp = Capture();
                var thumb = Thumbnail(bmp);
                double diff = lastThumb == null ? double.MaxValue / 2 : MeanAbsDiff(thumb, lastThumb);
                bool changed = diff >= _s.ChangeThreshold;
                bool heartbeat = (now - lastKept).TotalSeconds >= _s.HeartbeatSeconds;
                if (changed || heartbeat)
                {
                    SaveFrame(bmp, now, changed ? diff : 0.01);
                    lastThumb = thumb;
                    lastKept = now;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Frame capture failed: " + ex.Message);
                Thread.Sleep(500);
            }
        }
    }

    Bitmap Capture()
    {
        var bmp = new Bitmap(_area.Width, _area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(_area.Location, Point.Empty, _area.Size, CopyPixelOperation.SourceCopy);
        if (_s.DrawCursor) DrawCursor(g);
        return bmp;
    }

    void DrawCursor(Graphics g)
    {
        var ci = new Native.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0) return;
        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        int x = ci.ptScreenPos.X - _area.X - hx, y = ci.ptScreenPos.Y - _area.Y - hy;
        var hdc = g.GetHdc();
        try { Native.DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    void SaveFrame(Bitmap bmp, TimeSpan at, double score)
    {
        int idx;
        lock (_candidates) idx = _candidates.Count;
        var path = Path.Combine(_folder, "frames", $"frame_{idx:D4}_{(int)at.TotalMilliseconds:D7}ms.jpg");

        double scale = Math.Min(1.0, (double)_s.MaxImageLongEdge / Math.Max(bmp.Width, bmp.Height));
        if (scale < 1.0)
        {
            using var small = new Bitmap((int)(bmp.Width * scale), (int)(bmp.Height * scale), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, small.Width, small.Height);
            }
            SaveJpeg(small, path);
        }
        else SaveJpeg(bmp, path);

        lock (_candidates) _candidates.Add(new KeyFrame(path, at, score));
    }

    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    static void SaveJpeg(Bitmap bmp, string path)
    {
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        bmp.Save(path, Jpeg, p);
    }

    const int TW = 128, TH = 72;

    static byte[] Thumbnail(Bitmap bmp)
    {
        using var t = new Bitmap(TW, TH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(bmp, 0, 0, TW, TH);
        }
        var data = t.LockBits(new Rectangle(0, 0, TW, TH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var gray = new byte[TW * TH];
            var row = new byte[data.Stride];
            for (int y = 0; y < TH; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < TW; x++)
                    gray[y * TW + x] = (byte)((row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8);
            }
            return gray;
        }
        finally { t.UnlockBits(data); }
    }

    /// <summary>Percent of thumbnail pixels that changed noticeably (ignores noise and the moving cursor).</summary>
    static double MeanAbsDiff(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 16) changed++;
        return 100.0 * changed / a.Length;
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>Delete all but the newest <paramref name="keep"/> recording folders.</summary>
    public static void Prune(int keep)
    {
        try
        {
            if (!Directory.Exists(Settings.RecordingsDir)) return;
            foreach (var d in new DirectoryInfo(Settings.RecordingsDir).GetDirectories()
                         .OrderByDescending(d => d.Name).Skip(Math.Max(1, keep)))
                d.Delete(true);
        }
        catch (Exception ex) { Log.Write("Prune failed: " + ex.Message); }
    }
}
