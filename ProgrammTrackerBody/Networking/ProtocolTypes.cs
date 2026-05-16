using System;

namespace ProgrammTrackerBody.Networking;

public enum SendPacketType : byte
{
    HeartBeat = 0,
    Handshake = 3,
    Accel = 4,
    PingPong = 10,
    Serial = 11,
    BatteryLevel = 12,
    Tap = 13,
    Error = 14,
    SensorInfo = 15,
    RotationData = 17,
    MagnetometerAccuracy = 18,
    SignalStrength = 19,
    Temperature = 20,
    FeatureFlags = 22,
    AcknowledgeConfigChange = 24,
    FlexData = 26,
    Bundle = 100,
    Inspection = 105,
}

public enum ReceivePacketType : byte
{
    HeartBeat = 1,
    Vibrate = 2,
    Handshake = 3,
    Command = 4,
    Config = 8,
    PingPong = 10,
    SensorInfo = 15,
    FeatureFlags = 22,
    SetConfigFlag = 25,
}

public enum TrackerSessionState : byte
{
    Stopped = 0,
    Discovery = 1,
    SessionStart = 2,
    Streaming = 3,
}

public enum PendingCommandStatus : byte
{
    Pending = 0,
    Completed = 1,
    TimedOut = 2,
}

public sealed record DiscoveryHandshakeInfo(
    string MacKey,
    byte[] Mac,
    int ProtocolVersion,
    string FirmwareVersion,
    byte TrackerType,
    string VendorName,
    string ProductName);

public sealed record ParsedPacket(
    byte PacketType,
    ulong? PacketNumber,
    byte[] Payload,
    bool IsDiscoveryHandshake,
    bool IsFromBundle,
    DateTime TimestampUtc,
    DiscoveryHandshakeInfo? DiscoveryInfo = null);

public sealed record RotationDataMessage(
    string TrackerId,
    byte SensorId,
    byte DataType,
    float X,
    float Y,
    float Z,
    float W,
    byte Accuracy);

public sealed record AccelDataMessage(
    string TrackerId,
    byte SensorId,
    float X,
    float Y,
    float Z);

public sealed record SensorInfoMessage(
    string TrackerId,
    byte SensorId,
    byte SensorState,
    byte SensorType,
    uint SensorConfig,
    bool HasCompletedRestCalibration,
    byte SensorPosition,
    byte SensorDataType,
    float TpsCounter,
    float DataCounter);

public sealed record BatteryLevelMessage(
    string TrackerId,
    float Voltage,
    float Percentage); // Raw battery level from tracker in range 0..1.

public sealed record SignalStrengthMessage(
    string TrackerId,
    byte SensorId,
    byte Strength);

public sealed record PingPongMessage(
    string TrackerId,
    long EchoValue);

public sealed record PacketLogMessage(
    DateTime TimestampUtc,
    string Direction,
    string Endpoint,
    string PacketType,
    string Summary);

public sealed record TrackerConnectedMessage(
    string MacKey,
    string TrackerId,
    System.Net.IPEndPoint Endpoint,
    int? ProtocolVersion,
    string? FirmwareVersion,
    string? VendorName,
    string? ProductName);

public sealed record ActiveTrackerInfoMessage(
    string TrackerId,
    string Endpoint,
    string Mac,
    int? ProtocolVersion,
    string FirmwareVersion,
    string VendorName,
    string ProductName,
    DateTime ConnectedSinceUtc,
    DateTime LastSeenUtc,
    TimeSpan Uptime);

public sealed record PendingCommandInfo(
    ulong PacketNumber,
    string Endpoint,
    ReceivePacketType CommandType,
    SendPacketType? ExpectedResponseType,
    DateTime SentAtUtc,
    DateTime DeadlineUtc,
    PendingCommandStatus Status,
    DateTime? CompletedAtUtc,
    string Result);
