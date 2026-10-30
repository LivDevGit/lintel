using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lintel.Core;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Lintel.Services;

public sealed record MediaSnapshot
{
    public bool HasSession { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public BitmapSource? Art { get; init; }
    public Color? ArtColor { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionStamp { get; init; }
    public bool CanSeek { get; init; }

    /// <summary>Playback position extrapolated from the last timeline report.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            var pos = Position;
            if (IsPlaying && PositionStamp != default)
                pos += DemoClock.Now() - PositionStamp;
            if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
            return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        }
    }
}

/// <summary>
/// Wraps the Windows "global system media transport controls" API, which every
/// well-behaved player (Spotify, browsers, Media Player, VLC...) reports into.
/// </summary>
public sealed class MediaService
{
    private SessionManager? _manager;
    private Session? _session;
    private readonly HashSet<Session> _watched = new();
    private string _trackKey = "";

    public MediaSnapshot Current { get; private set; } = new();

    public event Action? Changed;
    public event Action? TrackChanged;

    public async Task StartAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Ui.Post(Pick);
            _manager.CurrentSessionChanged += (_, _) => Ui.Post(Pick);
            Pick();
        }
        catch (Exception ex)
        {
            Log.Error("media.start", ex);
        }
    }

    /// <summary>Prefers a session that is actually playing over the system's "current" one.</summary>
    private void Pick()
    {
        if (_manager == null) return;
        IReadOnlyList<Session> sessions;
        try { sessions = _manager.GetSessions(); }
        catch (Exception ex) { Log.Error("media.sessions", ex); return; }

        foreach (var s in sessions.Where(s => !_watched.Contains(s)))
        {
            _watched.Add(s);
            s.PlaybackInfoChanged += (_, _) => Ui.Post(Pick);
        }
        _watched.RemoveWhere(s => !sessions.Contains(s));

        Session? best = sessions.FirstOrDefault(IsPlaying);
        if (best == null && _session != null && sessions.Contains(_session)) best = _session;
        best ??= SafeCurrent() ?? sessions.FirstOrDefault();

        if (!ReferenceEquals(best, _session)) Bind(best);
        else RefreshPlayback();
    }

    private Session? SafeCurrent()
    {
        try { return _manager?.GetCurrentSession(); } catch { return null; }
    }

    private static bool IsPlaying(Session s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing; }
        catch { return false; }
    }

    private void Bind(Session? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = session;
        _trackKey = "";

        if (session == null)
        {
            Current = new MediaSnapshot();
            Changed?.Invoke();
            return;
        }

        session.MediaPropertiesChanged += OnMediaProperties;
        session.TimelinePropertiesChanged += OnTimeline;
        Current = new MediaSnapshot { HasSession = true, SourceApp = FriendlyAppName(session.SourceAppUserModelId) };
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    private void OnMediaProperties(Session s, MediaPropertiesChangedEventArgs e) => Ui.Post(() => _ = RefreshPropertiesAsync());
    private void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => Ui.Post(RefreshTimeline);

    private void RefreshPlayback()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            var playing = info.PlaybackStatus == PlaybackStatus.Playing;
            var canSeek = info.Controls.IsPlaybackPositionEnabled;
            if (playing == Current.IsPlaying && canSeek == Current.CanSeek) return;

            // Re-anchor the extrapolated position so pausing doesn't make the bar jump.
            Current = Current with
            {
                IsPlaying = playing,
                CanSeek = canSeek,
                Position = Current.LivePosition,
                PositionStamp = DateTimeOffset.Now,
            };
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media.playback", ex);
        }
    }

    private void RefreshTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            Current = Current with
            {
                Position = t.Position,
                Duration = t.EndTime - t.StartTime,
                PositionStamp = t.LastUpdatedTime == default ? DateTimeOffset.Now : t.LastUpdatedTime,
            };
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media.timeline", ex);
        }
    }

    private async Task RefreshPropertiesAsync()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(session, _session) || props == null) return;

            var key = props.Title + "\u0001" + props.Artist;
            var isNewTrack = key != _trackKey;
            _trackKey = key;

            BitmapSource? art = isNewTrack ? null : Current.Art;
            Color? artColor = isNewTrack ? null : Current.ArtColor;
            if ((art == null) && props.Thumbnail != null)
            {
                art = await LoadThumbnailAsync(props.Thumbnail);
                artColor = art == null ? null : ArtPalette.Vibrant(art);
            }
            if (!ReferenceEquals(session, _session)) return;

            Current = Current with
            {
                HasSession = true,
                Title = props.Title ?? "",
                Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist,
                Album = props.AlbumTitle ?? "",
                Art = art,
                ArtColor = artColor,
            };
            Changed?.Invoke();
            if (isNewTrack && !string.IsNullOrWhiteSpace(Current.Title)) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media.properties", ex);
        }
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.StreamSource = buffer;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("media.thumbnail", ex);
            return null;
        }
    }

    /// <summary>Demo mode: show a made-up track instead of a real session.</summary>
    public void Inject(MediaSnapshot snapshot)
    {
        var newTrack = snapshot.Title != Current.Title || snapshot.Artist != Current.Artist;
        Current = snapshot;
        Changed?.Invoke();
        if (newTrack && snapshot.HasSession) TrackChanged?.Invoke();
    }

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Error("media.toggle", ex); } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch (Exception ex) { Log.Error("media.next", ex); } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Error("media.prev", ex); } }

    public async void SeekTo(double fraction)
    {
        if (_session == null || Current.Duration <= TimeSpan.Zero) return;
        try
        {
            var target = TimeSpan.FromTicks((long)(Current.Duration.Ticks * Math.Clamp(fraction, 0, 1)));
            await _session.TryChangePlaybackPositionAsync(target.Ticks);
            Current = Current with { Position = target, PositionStamp = DateTimeOffset.Now };
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media.seek", ex);
        }
    }

    private static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid;
        var bang = name.IndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name switch
        {
            "ZuneMusic" => "Media Player",
            "msedge" or "MSEdge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name,
        };
    }
}

/// <summary>Picks a lively accent colour out of album art.</summary>
internal static class ArtPalette
{
    public static Color? Vibrant(BitmapSource source)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var scaled = new TransformedBitmap(converted,
                new ScaleTransform(24.0 / source.PixelWidth, 24.0 / source.PixelHeight));
            int w = scaled.PixelWidth, h = scaled.PixelHeight;
            var px = new byte[w * h * 4];
            scaled.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                var sat = max <= 0 ? 0 : (max - min) / max;
                // Favour saturated, reasonably bright pixels; grey and black barely count.
                var weight = 0.02 + sat * sat * max;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total <= 0) return null;
            return Boost(r / total, g / total, b / total);
        }
        catch
        {
            return null;
        }
    }

    private static Color Boost(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = max <= 0 ? 0 : (max - min) / max, v = max;
        var d = max - min;
        if (d > 1e-6)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        s = s < 0.12 ? s : Math.Max(s, 0.5);
        v = Math.Max(v, 0.82);
        return FromHsv(h, s, v);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
