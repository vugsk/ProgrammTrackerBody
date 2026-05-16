using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.Networking;
using ProgrammTrackerBody.Persistence;

namespace ProgrammTrackerBody.Services;

public sealed class TrackerManager : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly UdpTrackerServer _server;
    private readonly ConfigStore _configStore;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, TrackerModel> _byMacKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistLock = new();
    private readonly Dictionary<string, DateTime> _lastDiagLogUtc = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _persistTimer;
    private AppConfig _config = new();
    private bool _isLoaded;

    public TrackerManager(UdpTrackerServer server, ConfigStore configStore, Dispatcher dispatcher)
    {
        _server = server;
        _configStore = configStore;
        _dispatcher = dispatcher;

        _server.TrackerConnected += OnTrackerConnected;
        _server.TrackerDisconnected += OnTrackerDisconnected;
        _server.RotationReceived += OnRotationReceived;
        _server.BatteryLevelReceived += OnBatteryReceived;
        _server.SignalStrengthReceived += OnSignalReceived;
        _server.SessionStateChanged += OnSessionStateChanged;
    }

    public ObservableCollection<TrackerModel> Trackers { get; } = new();

    public AppConfig Config => _config;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Fires after any Yaw/Full/Mounting reset is applied. The skeleton view uses
    // this to briefly snap to T-pose so the user can visually confirm the reset.
    public event Action? CalibrationApplied;

    public async Task InitializeAsync()
    {
        _config = await _configStore.LoadAsync();
        _isLoaded = true;
    }

    public Task PersistConfigAsync() => SchedulePersist();

    public void RequestYawReset(string macKey)
    {
        if (!_byMacKey.TryGetValue(macKey, out var model))
        {
            return;
        }

        // Yaw correction is computed in body frame, after mounting change-of-basis
        // and full reset. Matches ApplyOffsets: yaw · full · (mounting · raw · mounting⁻¹).
        var m = ResetMath.Normalize(model.MountingOffset);
        var mInv = Quaternion.Conjugate(m);
        var bodyRot = model.FullOffset * (m * ResetMath.Normalize(model.RawRotation) * mInv);
        model.YawOffset = ResetMath.ComputeYawOffset(bodyRot);
        RecomputeDisplay(model);
        _ = SchedulePersist();
        CalibrationApplied?.Invoke();
    }

    public void RequestFullReset(string macKey)
    {
        if (!_byMacKey.TryGetValue(macKey, out var model))
        {
            return;
        }

        // Full reset zeroes the body-frame rotation, computed via mounting conjugation.
        var m = ResetMath.Normalize(model.MountingOffset);
        var mInv = Quaternion.Conjugate(m);
        var bodyRot = m * ResetMath.Normalize(model.RawRotation) * mInv;
        model.FullOffset = ResetMath.ComputeFullOffset(bodyRot);
        // Yaw reference is captured RELATIVE to full reset — invalidate it.
        model.YawOffset = Quaternion.Identity;
        RecomputeDisplay(model);
        _ = SchedulePersist();
        CalibrationApplied?.Invoke();
    }

    public void RequestMountingReset(string macKey)
    {
        if (!_byMacKey.TryGetValue(macKey, out var model))
        {
            return;
        }

        // Mounting reset establishes a new baseline. The mounting offset is a
        // discrete Y-only rotation derived from the auto-detected orientation
        // (Front/Back/Left/Right). Downstream offsets (yaw/full) are computed
        // against this baseline, so clear them.
        model.MountingOrientation = ResetMath.DetectMountingOrientation(model.RawRotation);
        model.MountingOffset = ResetMath.ComputeMountingOffset(model.MountingOrientation);
        model.YawOffset = Quaternion.Identity;
        model.FullOffset = Quaternion.Identity;
        RecomputeDisplay(model);
        _ = SchedulePersist();
        CalibrationApplied?.Invoke();
    }

    // One-click clean baseline: auto-detect mounting orientation, then capture
    // a full reset on top of it so the user's current pose becomes identity.
    // After this, display == identity in the rest pose.
    public void RequestFullSetup(string macKey)
    {
        if (!_byMacKey.TryGetValue(macKey, out var model))
        {
            return;
        }

        model.MountingOrientation = ResetMath.DetectMountingOrientation(model.RawRotation);
        model.MountingOffset = ResetMath.ComputeMountingOffset(model.MountingOrientation);
        var m = ResetMath.Normalize(model.MountingOffset);
        var mInv = Quaternion.Conjugate(m);
        var bodyRot = m * ResetMath.Normalize(model.RawRotation) * mInv;
        model.FullOffset = ResetMath.ComputeFullOffset(bodyRot);
        model.YawOffset = Quaternion.Identity;
        RecomputeDisplay(model);
        _ = SchedulePersist();
        CalibrationApplied?.Invoke();
    }

    public Task SendVibrateAsync(string macKey, ushort durationMs = 200, byte amplitude = 200)
    {
        if (!_byMacKey.TryGetValue(macKey, out var model))
        {
            return Task.CompletedTask;
        }

        return _server.SendVibrateAsync(macKey, model.SensorId, durationMs, amplitude);
    }

    public void Dispose()
    {
        UnsubscribeEvents();
        _persistTimer?.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        UnsubscribeEvents();
        _persistTimer?.Stop();
        await PersistNowAsync();
    }

    private void UnsubscribeEvents()
    {
        _server.TrackerConnected -= OnTrackerConnected;
        _server.TrackerDisconnected -= OnTrackerDisconnected;
        _server.RotationReceived -= OnRotationReceived;
        _server.BatteryLevelReceived -= OnBatteryReceived;
        _server.SignalStrengthReceived -= OnSignalReceived;
        _server.SessionStateChanged -= OnSessionStateChanged;
    }

    private void OnTrackerConnected(TrackerConnectedMessage message)
    {
        _dispatcher.Invoke(() =>
        {
            if (_byMacKey.TryGetValue(message.MacKey, out var existing))
            {
                existing.IsOnline = true;
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.FirmwareVersion = message.FirmwareVersion ?? "-";
                existing.ProtocolVersion = message.ProtocolVersion;
                return;
            }

            var model = new TrackerModel
            {
                Mac = message.MacKey,
            };
            model.IsOnline = true;
            model.LastSeenUtc = DateTime.UtcNow;
            model.FirmwareVersion = message.FirmwareVersion ?? "-";
            model.ProtocolVersion = message.ProtocolVersion;

            ApplySavedConfig(model);
            model.PropertyChanged += OnTrackerModelPropertyChanged;
            _byMacKey[message.MacKey] = model;
            Trackers.Add(model);
            RaisePropertyChanged(nameof(ConnectedCount));
        });
    }

    private void OnTrackerModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Persist user-edited settings; ignore high-frequency telemetry properties.
        if (e.PropertyName is nameof(TrackerModel.DisplayName)
            or nameof(TrackerModel.BodyPart)
            or nameof(TrackerModel.MountingOrientation)
            or nameof(TrackerModel.YawOffset)
            or nameof(TrackerModel.FullOffset)
            or nameof(TrackerModel.MountingOffset))
        {
            _ = SchedulePersist();
        }

        if (sender is TrackerModel model
            && e.PropertyName is nameof(TrackerModel.MountingOrientation)
                or nameof(TrackerModel.MountingOffset)
                or nameof(TrackerModel.YawOffset)
                or nameof(TrackerModel.FullOffset))
        {
            // Any offset change must immediately reflect in DisplayRotation,
            // otherwise the user sees no visual feedback until the next packet.
            RecomputeDisplay(model);
        }
    }

    private void OnTrackerDisconnected(string macKey)
    {
        _dispatcher.Invoke(() =>
        {
            if (_byMacKey.TryGetValue(macKey, out var model))
            {
                model.IsOnline = false;
            }

            RaisePropertyChanged(nameof(ConnectedCount));
        });
    }

    private void OnRotationReceived(RotationDataMessage message)
    {
        var macKey = TrackerIdToMacKey(message.TrackerId);
        _dispatcher.Invoke(() =>
        {
            if (!_byMacKey.TryGetValue(macKey, out var model))
            {
                return;
            }

            model.SensorId = message.SensorId;
            model.RawRotation = new Quaternion(message.X, message.Y, message.Z, message.W);
            model.LastSeenUtc = DateTime.UtcNow;
            model.IsOnline = true;
            RecomputeDisplay(model);
        });
    }

    private void OnBatteryReceived(BatteryLevelMessage message)
    {
        var macKey = TrackerIdToMacKey(message.TrackerId);
        _dispatcher.Invoke(() =>
        {
            if (!_byMacKey.TryGetValue(macKey, out var model))
            {
                return;
            }

            model.BatteryVoltage = message.Voltage;
            model.BatteryPercentage = message.Percentage * 100f;
        });
    }

    private void OnSignalReceived(SignalStrengthMessage message)
    {
        var macKey = TrackerIdToMacKey(message.TrackerId);
        _dispatcher.Invoke(() =>
        {
            if (!_byMacKey.TryGetValue(macKey, out var model))
            {
                return;
            }

            model.Rssi = message.Strength;
        });
    }

    private void OnSessionStateChanged(TrackerSessionState state)
    {
        if (state == TrackerSessionState.Stopped)
        {
            _dispatcher.Invoke(() =>
            {
                foreach (var model in Trackers)
                {
                    model.IsOnline = false;
                }
            });
        }
    }

    private void ApplySavedConfig(TrackerModel model)
    {
        if (!_config.Trackers.TryGetValue(model.Mac, out var entry))
        {
            return;
        }

        model.DisplayName = entry.DisplayName;
        model.BodyPart = entry.BodyPart;
        model.MountingOrientation = entry.Mounting;
        model.YawOffset = QuaternionUtil.FromArray(entry.YawOffset);
        model.FullOffset = QuaternionUtil.FromArray(entry.FullOffset);
        model.MountingOffset = QuaternionUtil.FromArray(entry.MountingOffset);
    }

    private void RecomputeDisplay(TrackerModel model)
    {
        model.DisplayRotation = ResetMath.ApplyOffsets(
            model.RawRotation,
            model.MountingOffset,
            model.YawOffset,
            model.FullOffset);

        if (model.LogRawAndDisplay)
        {
            LogCalibrationDiagnostic(model);
        }
    }

    private void LogCalibrationDiagnostic(TrackerModel model)
    {
        // Throttle to at most one line per second per tracker — raw packets can
        // arrive at 100+ Hz and would drown the system log.
        var now = DateTime.UtcNow;
        if (_lastDiagLogUtc.TryGetValue(model.Mac, out var last)
            && (now - last) < TimeSpan.FromSeconds(1))
        {
            return;
        }
        _lastDiagLogUtc[model.Mac] = now;

        static string F(Quaternion q) =>
            $"({q.X,6:F3},{q.Y,6:F3},{q.Z,6:F3},{q.W,6:F3})";

        _server.LogSystem("CalibDiag",
            $"mac={model.Mac} raw={F(model.RawRotation)} body={F(model.DisplayRotation)} "
            + $"mount={F(model.MountingOffset)} full={F(model.FullOffset)} yaw={F(model.YawOffset)}");
    }

    private static string TrackerIdToMacKey(string trackerId)
    {
        // TrackerId is "AA:BB:CC:DD:EE:FF"; macKey is "aabbccddeeff".
        return trackerId.Replace(":", string.Empty).ToLowerInvariant();
    }

    private Task SchedulePersist()
    {
        if (!_isLoaded)
        {
            return Task.CompletedTask;
        }

        _dispatcher.Invoke(() =>
        {
            if (_persistTimer is null)
            {
                _persistTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(500),
                };
                _persistTimer.Tick += async (_, _) =>
                {
                    _persistTimer.Stop();
                    await PersistNowAsync();
                };
            }

            _persistTimer.Stop();
            _persistTimer.Start();
        });

        return Task.CompletedTask;
    }

    private async Task PersistNowAsync()
    {
        AppConfig snapshot;
        lock (_persistLock)
        {
            // Start from existing saved entries so settings for trackers that
            // are not yet reconnected are preserved, not overwritten with defaults.
            var trackers = new Dictionary<string, TrackerConfigEntry>(
                _config.Trackers, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in _byMacKey)
            {
                trackers[kv.Key] = new TrackerConfigEntry
                {
                    DisplayName = kv.Value.DisplayName,
                    BodyPart = kv.Value.BodyPart,
                    Mounting = kv.Value.MountingOrientation,
                    YawOffset = QuaternionUtil.ToArray(kv.Value.YawOffset),
                    FullOffset = QuaternionUtil.ToArray(kv.Value.FullOffset),
                    MountingOffset = QuaternionUtil.ToArray(kv.Value.MountingOffset),
                };
            }

            snapshot = new AppConfig
            {
                Language = _config.Language,
                LastPort = _config.LastPort,
                Trackers = trackers,
            };
            _config = snapshot;
        }

        try
        {
            await _configStore.SaveAsync(snapshot);
        }
        catch
        {
            // Persistence failures shouldn't crash the UI; future writes will retry.
        }
    }

    public int ConnectedCount => Trackers.Count(t => t.IsOnline);

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
