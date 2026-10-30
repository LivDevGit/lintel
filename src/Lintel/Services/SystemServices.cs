using System;
using System.Management;
using System.Windows.Threading;
using Lintel.Core;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Forms = System.Windows.Forms;

namespace Lintel.Services;

/// <summary>Default playback device volume, via Core Audio.</summary>
public sealed class VolumeService : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;

    /// <summary>Level 0..1 and mute state.</summary>
    public event Action<float, bool>? VolumeChanged;

    public void Start()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Log.Error("volume.register", ex);
        }
        Attach();
    }

    private void Attach()
    {
        try
        {
            if (_volume != null) _volume.OnVolumeNotification -= OnVolume;
            _device?.Dispose();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volume = _device.AudioEndpointVolume;
            _volume.OnVolumeNotification += OnVolume;
        }
        catch (Exception ex)
        {
            _device = null;
            _volume = null;
            Log.Error("volume.attach", ex);
        }
    }

    private void OnVolume(AudioVolumeNotificationData data) =>
        Ui.Post(() => VolumeChanged?.Invoke(data.MasterVolume, data.Muted));

    public void Nudge(float delta)
    {
        try
        {
            if (_volume == null) return;
            var next = Math.Clamp(_volume.MasterVolumeLevelScalar + delta, 0f, 1f);
            _volume.MasterVolumeLevelScalar = next;
            if (_volume.Mute && delta > 0) _volume.Mute = false;
        }
        catch (Exception ex)
        {
            Log.Error("volume.nudge", ex);
        }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Ui.Post(Attach);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        if (_volume != null) _volume.OnVolumeNotification -= OnVolume;
        _device?.Dispose();
        _enumerator.Dispose();
    }
}

/// <summary>Built-in panel brightness changes, via WMI (laptops and tablets only).</summary>
public sealed class BrightnessService : IDisposable
{
    private ManagementEventWatcher? _watcher;

    /// <summary>Brightness 0..100.</summary>
    public event Action<int>? BrightnessChanged;

    public void Start()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            _watcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                var level = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                Ui.Post(() => BrightnessChanged?.Invoke(level));
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            // External monitors and most desktops don't expose this class.
            _watcher = null;
            Log.Write($"[brightness] unavailable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        _watcher?.Dispose();
    }
}

public sealed record BatterySnapshot(
    bool HasBattery,
    int Percent,
    bool PluggedIn,
    bool Charging,
    bool Saver,
    int? MinutesToFull,
    int? MinutesLeft);

public sealed class BatteryService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };

    public BatterySnapshot Current { get; private set; } = new(false, 100, true, false, false, null, null);

    /// <summary>(previous, current)</summary>
    public event Action<BatterySnapshot, BatterySnapshot>? Changed;

    public void Start()
    {
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            Windows.Devices.Power.Battery.AggregateBattery.ReportUpdated += (_, _) => Ui.Post(Poll);
        }
        catch (Exception ex)
        {
            Log.Error("battery.report", ex);
        }
        Poll();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Ui.Post(Poll);

    private void Poll()
    {
        try
        {
            var ps = Forms.SystemInformation.PowerStatus;
            var status = ps.BatteryChargeStatus;
            var has = (status & Forms.BatteryChargeStatus.NoSystemBattery) == 0 && status != Forms.BatteryChargeStatus.Unknown;
            var percent = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 100);
            var plugged = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
            var charging = (status & Forms.BatteryChargeStatus.Charging) != 0;
            int? left = ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining / 60 : null;

            int? toFull = null;
            try
            {
                var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
                if (report.ChargeRateInMilliwatts is int rate && rate > 0 &&
                    report.FullChargeCapacityInMilliwattHours is int full &&
                    report.RemainingCapacityInMilliwattHours is int remaining && full > remaining)
                {
                    toFull = (int)Math.Round((full - remaining) / (double)rate * 60);
                }
            }
            catch
            {
                // Report is optional detail.
            }

            var saver = false;
            try { saver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
            catch { }

            var next = new BatterySnapshot(has, percent, plugged, charging, saver, charging ? toFull : null, plugged ? null : left);
            if (next == Current) return;
            var previous = Current;
            Current = next;
            Changed?.Invoke(previous, next);
        }
        catch (Exception ex)
        {
            Log.Error("battery.poll", ex);
        }
    }

    /// <summary>Demo mode: pretend the power state changed.</summary>
    public void Inject(BatterySnapshot next)
    {
        var previous = Current;
        Current = next;
        Changed?.Invoke(previous, next);
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
