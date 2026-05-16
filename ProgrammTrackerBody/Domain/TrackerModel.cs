using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProgrammTrackerBody.Domain;

public sealed class TrackerModel : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private BodyPart _bodyPart = BodyPart.None;
    private MountingOrientation _mountingOrientation = MountingOrientation.Front;
    private Quaternion _rawRotation = Quaternion.Identity;
    private Quaternion _displayRotation = Quaternion.Identity;
    private Quaternion _yawOffset = Quaternion.Identity;
    private Quaternion _fullOffset = Quaternion.Identity;
    private Quaternion _mountingOffset = Quaternion.Identity;
    private float _batteryPercentage;
    private float _batteryVoltage;
    private byte _rssi;
    private bool _isOnline;
    private DateTime _lastSeenUtc;
    private string _firmwareVersion = "-";
    private int? _protocolVersion;
    private byte _sensorId;
    private bool _logRawAndDisplay;

    public required string Mac { get; init; }

    public byte SensorId
    {
        get => _sensorId;
        set => SetField(ref _sensorId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public BodyPart BodyPart
    {
        get => _bodyPart;
        set => SetField(ref _bodyPart, value);
    }

    public MountingOrientation MountingOrientation
    {
        get => _mountingOrientation;
        set => SetField(ref _mountingOrientation, value);
    }

    public Quaternion RawRotation
    {
        get => _rawRotation;
        set => SetField(ref _rawRotation, value);
    }

    public Quaternion DisplayRotation
    {
        get => _displayRotation;
        set
        {
            if (SetField(ref _displayRotation, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayRotationText)));
            }
        }
    }

    public string DisplayRotationText =>
        $"{_displayRotation.X:F3}, {_displayRotation.Y:F3}, {_displayRotation.Z:F3}, {_displayRotation.W:F3}";

    public Quaternion YawOffset
    {
        get => _yawOffset;
        set
        {
            if (SetField(ref _yawOffset, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(YawOffsetText)));
            }
        }
    }

    public Quaternion FullOffset
    {
        get => _fullOffset;
        set
        {
            if (SetField(ref _fullOffset, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullOffsetText)));
            }
        }
    }

    public Quaternion MountingOffset
    {
        get => _mountingOffset;
        set
        {
            if (SetField(ref _mountingOffset, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MountingOffsetText)));
            }
        }
    }

    public string YawOffsetText =>
        $"{_yawOffset.X:F3}, {_yawOffset.Y:F3}, {_yawOffset.Z:F3}, {_yawOffset.W:F3}";

    public string FullOffsetText =>
        $"{_fullOffset.X:F3}, {_fullOffset.Y:F3}, {_fullOffset.Z:F3}, {_fullOffset.W:F3}";

    public string MountingOffsetText =>
        $"{_mountingOffset.X:F3}, {_mountingOffset.Y:F3}, {_mountingOffset.Z:F3}, {_mountingOffset.W:F3}";

    public float BatteryPercentage
    {
        get => _batteryPercentage;
        set => SetField(ref _batteryPercentage, value);
    }

    public float BatteryVoltage
    {
        get => _batteryVoltage;
        set => SetField(ref _batteryVoltage, value);
    }

    public byte Rssi
    {
        get => _rssi;
        set => SetField(ref _rssi, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetField(ref _isOnline, value);
    }

    public DateTime LastSeenUtc
    {
        get => _lastSeenUtc;
        set => SetField(ref _lastSeenUtc, value);
    }

    // Per-tracker toggle for streaming raw + display rotation to the system log.
    // Used to debug calibration math against real hardware.
    public bool LogRawAndDisplay
    {
        get => _logRawAndDisplay;
        set => SetField(ref _logRawAndDisplay, value);
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        set => SetField(ref _firmwareVersion, value);
    }

    public int? ProtocolVersion
    {
        get => _protocolVersion;
        set => SetField(ref _protocolVersion, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value))
        {
            return false;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
