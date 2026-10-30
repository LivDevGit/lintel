using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lintel.Core;
using Lintel.Native;
using NAudio.Dsp;
using NAudio.Wave;

namespace Lintel.Services;

/// <summary>
/// Notices browser downloads in the Downloads folder by their temporary
/// extensions, and reports when they turn into the finished file.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private static readonly string[] PartialExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public int ActiveCount => _active.Count;
    public event Action? ActiveChanged;
    public event Action<string>? Completed;

    public void Start()
    {
        if (_watcher != null) return;
        var folder = Win32.DownloadsFolder();
        if (folder == null || !Directory.Exists(folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => Ui.Post(() => OnCreated(e.FullPath));
            _watcher.Renamed += (_, e) => Ui.Post(() => OnRenamed(e.OldFullPath, e.FullPath));
            _watcher.Deleted += (_, e) => Ui.Post(() => OnDeleted(e.FullPath));
        }
        catch (Exception ex)
        {
            Log.Error("downloads.start", ex);
        }
    }

    private static bool IsPartial(string path) =>
        PartialExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private void OnCreated(string path)
    {
        if (IsPartial(path) && _active.Add(path)) ActiveChanged?.Invoke();
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        var wasPartial = _active.Remove(oldPath) || IsPartial(oldPath);
        if (IsPartial(newPath))
        {
            _active.Add(newPath);
            ActiveChanged?.Invoke();
            return;
        }
        if (!wasPartial) return;
        ActiveChanged?.Invoke();
        Completed?.Invoke(newPath);
    }

    private void OnDeleted(string path)
    {
        if (!_active.Remove(path)) return;
        ActiveChanged?.Invoke();
        // Firefox writes "name.ext" alongside "name.ext.part", then deletes the .part.
        var finalPath = path[..path.LastIndexOf('.')];
        if (File.Exists(finalPath)) Completed?.Invoke(finalPath);
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_active.Count == 0) return;
        _active.Clear();
        ActiveChanged?.Invoke();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Real-time spectrum of whatever the PC is playing, from WASAPI loopback capture.
/// </summary>
public sealed class SpectrumService : IDisposable
{
    public const int Bands = 6;
    private const int FftSize = 1024;
    private const int FftLog2 = 10;

    private readonly float[] _ring = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[Bands];
    private int _ringPos;
    private WasapiLoopbackCapture? _capture;
    private DateTime _lastPublish;

    public float[] Levels { get; } = new float[Bands];
    public bool Running => _capture != null || _simulation != null;
    public event Action? Updated;

    /// <summary>Demo mode: animate plausible bars instead of capturing real audio.</summary>
    public bool Simulated { get; set; }
    private System.Windows.Threading.DispatcherTimer? _simulation;
    private readonly Random _random = new(7);
    private double _phase;

    private void StartSimulation()
    {
        _simulation = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(42) };
        _simulation.Tick += (_, _) =>
        {
            _phase += 0.042;
            var beat = Math.Pow(Math.Max(0, Math.Sin(_phase * Math.PI * 2 * 2.0)), 6); // ~120 bpm pulse
            for (var b = 0; b < Bands; b++)
            {
                var body = 0.35 + 0.25 * Math.Sin(_phase * (2.1 + b * 0.9) + b);
                var target = Math.Clamp(body + beat * (0.55 - b * 0.06) + (_random.NextDouble() - 0.5) * 0.18, 0.08, 1);
                Levels[b] = (float)(Levels[b] * 0.45 + target * 0.55);
            }
            Updated?.Invoke();
        };
        _simulation.Start();
    }

    public void Start()
    {
        if (Simulated)
        {
            if (_simulation == null) StartSimulation();
            return;
        }
        if (_capture != null) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Error("spectrum.start", ex);
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (_simulation != null)
        {
            _simulation.Stop();
            _simulation = null;
            Array.Clear(Levels);
            Updated?.Invoke();
        }
        if (_capture == null) return;
        try
        {
            _capture.DataAvailable -= OnData;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("spectrum.stop", ex);
        }
        _capture = null;
        Array.Clear(_levels);
        Ui.Post(() =>
        {
            Array.Clear(Levels);
            Updated?.Invoke();
        });
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture == null || e.BytesRecorded == 0) return;
        var fmt = capture.WaveFormat;
        var channels = Math.Max(1, fmt.Channels);

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            var frames = e.BytesRecorded / (4 * channels);
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                Push(sum / channels);
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            var frames = e.BytesRecorded / (2 * channels);
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                Push(sum / channels);
            }
        }
        else
        {
            return;
        }

        var now = DateTime.UtcNow;
        // ~24 updates per second: lively enough for bars, cheap enough to leave on.
        if ((now - _lastPublish).TotalMilliseconds < 42) return;
        _lastPublish = now;
        Analyse(fmt.SampleRate);
    }

    private void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % FftSize;
    }

    private void Analyse(int sampleRate)
    {
        for (var i = 0; i < FftSize; i++)
        {
            var s = _ring[(_ringPos + i) % FftSize];
            _fft[i].X = s * (float)FastFourierTransform.HannWindow(i, FftSize);
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftLog2, _fft);

        // Log-spaced bands between ~60 Hz and ~12 kHz.
        const double lo = 60, hi = 12000;
        var binHz = sampleRate / (double)FftSize;
        for (var b = 0; b < Bands; b++)
        {
            var f0 = lo * Math.Pow(hi / lo, b / (double)Bands);
            var f1 = lo * Math.Pow(hi / lo, (b + 1) / (double)Bands);
            int i0 = Math.Max(1, (int)(f0 / binHz)), i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
            double peak = 0;
            for (var i = i0; i < i1 && i < FftSize / 2; i++)
            {
                var mag = Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                if (mag > peak) peak = mag;
            }
            var db = 20 * Math.Log10(peak + 1e-9);
            var level = (float)Math.Clamp((db + 62) / 50, 0, 1);
            // Fast attack, slower release, so bars feel bouncy rather than jittery.
            _levels[b] = level > _levels[b] ? level * 0.7f + _levels[b] * 0.3f : _levels[b] * 0.82f + level * 0.18f;
        }

        var snapshot = (float[])_levels.Clone();
        Ui.Post(() =>
        {
            Array.Copy(snapshot, Levels, Bands);
            Updated?.Invoke();
        });
    }

    public void Dispose() => Stop();
}
