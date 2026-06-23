using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProgrammTrackerBody.Networking;

public sealed class UdpTrackerServer : IAsyncDisposable
{
    private static readonly byte[] DiscoveryHandshakePacket = BuildDiscoveryHandshakePacket();
    private static readonly TimeSpan DiscoveryBroadcastInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionStaleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingCommandTimeout = TimeSpan.FromSeconds(3);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, TrackerSession> _sessionsByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, PendingCommandEntry> _pendingCommands = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveLoopTask;
    private Task? _broadcastTask;
    private Task? _sessionKeepAliveTask;
    private TrackerSessionState _sessionState = TrackerSessionState.Stopped;
    private string? _activeMacKey;
    private ulong _fallbackPacketNumber = 1;

    public event Action<PacketLogMessage>? PacketLogged;
    public event Action<RotationDataMessage>? RotationReceived;
    public event Action<AccelDataMessage>? AccelReceived;
    public event Action<SensorInfoMessage>? SensorInfoReceived;
    public event Action<BatteryLevelMessage>? BatteryLevelReceived;
    public event Action<SignalStrengthMessage>? SignalStrengthReceived;
    public event Action<PingPongMessage>? PingPongReceived;
    public event Action<IPEndPoint>? TrackerEndpointChanged;
    public event Action<TrackerSessionState>? SessionStateChanged;
    public event Action<ActiveTrackerInfoMessage>? ActiveTrackerInfoChanged;

    // New per-session events for multi-tracker UI.
    public event Action<TrackerConnectedMessage>? TrackerConnected;
    public event Action<string>? TrackerDisconnected;

    public bool IsRunning => _udpClient != null;

    public TrackerSessionState SessionState
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessionState;
            }
        }
    }

    public Task StartAsync(int port)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(port)
        {
            EnableBroadcast = true,
        };

        lock (_syncRoot)
        {
            _sessionsByMac.Clear();
            _macByEndpoint.Clear();
            _pendingCommands.Clear();
            _activeMacKey = null;
            _fallbackPacketNumber = 1;
        }

        TransitionState(TrackerSessionState.Discovery);
        Log("SYSTEM", "-", "Server", $"UDP server started on port {port}");

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _broadcastTask = Task.Run(() => DiscoveryBroadcastLoopAsync(port, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var sessionCts = _sessionCts;
        var client = _udpClient;

        _cts = null;
        _sessionCts = null;
        _udpClient = null;

        List<string> disconnectedMacs;
        lock (_syncRoot)
        {
            disconnectedMacs = _sessionsByMac.Keys.ToList();
            _sessionsByMac.Clear();
            _macByEndpoint.Clear();
            _pendingCommands.Clear();
            _activeMacKey = null;
        }

        foreach (var mac in disconnectedMacs)
        {
            TrackerDisconnected?.Invoke(mac);
        }

        if (cts is null || client is null)
        {
            TransitionState(TrackerSessionState.Stopped);
            return;
        }

        cts.Cancel();
        sessionCts?.Cancel();
        client.Dispose();

        var tasks = new List<Task>();
        if (_receiveLoopTask is not null)
        {
            tasks.Add(_receiveLoopTask);
        }

        if (_broadcastTask is not null)
        {
            tasks.Add(_broadcastTask);
        }

        if (_sessionKeepAliveTask is not null)
        {
            tasks.Add(_sessionKeepAliveTask);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            _receiveLoopTask = null;
            _broadcastTask = null;
            _sessionKeepAliveTask = null;
            cts.Dispose();
            sessionCts?.Dispose();
        }

        TransitionState(TrackerSessionState.Stopped);
        Log("SYSTEM", "-", "Server", "UDP server stopped");
        PublishEmptyTrackerInfo();
    }

    public async Task SendHeartbeatAsync()
    {
        await SendCommandAsync(ReceivePacketType.HeartBeat, Array.Empty<byte>(), "HeartBeat", null, false);
    }

    public async Task SendPingAsync()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await SendCommandAsync(ReceivePacketType.PingPong, payload, "PingPong", SendPacketType.PingPong, true);
    }

    public async Task SendSetConfigFlagAsync(byte sensorId, uint flag, bool newState)
    {
        var payload = new byte[6];
        payload[0] = sensorId;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), flag);
        payload[5] = newState ? (byte)1 : (byte)0;

        var summary = $"SetConfigFlag sensorId={sensorId}, flag=0x{flag:X8}, newState={newState}";
        await SendCommandAsync(ReceivePacketType.SetConfigFlag, payload, summary, SendPacketType.AcknowledgeConfigChange, true);
    }

    // Targeted overloads — required for multi-tracker UI.
    public Task SendHeartbeatAsync(string macKey)
        => SendCommandToMacAsync(macKey, ReceivePacketType.HeartBeat, Array.Empty<byte>(), "HeartBeat", null, false);

    public Task SendPingAsync(string macKey)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return SendCommandToMacAsync(macKey, ReceivePacketType.PingPong, payload, "PingPong", SendPacketType.PingPong, true);
    }

    public Task SendSetConfigFlagAsync(string macKey, byte sensorId, uint flag, bool newState)
    {
        var payload = new byte[6];
        payload[0] = sensorId;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), flag);
        payload[5] = newState ? (byte)1 : (byte)0;

        var summary = $"SetConfigFlag sensorId={sensorId}, flag=0x{flag:X8}, newState={newState}";
        return SendCommandToMacAsync(macKey, ReceivePacketType.SetConfigFlag, payload, summary, SendPacketType.AcknowledgeConfigChange, true);
    }

    // Vibrate payload format: 2 bytes BE durationMs + 1 byte amplitude.
    // Stub firmware ignores payload — the send-path is implemented for completeness.
    public Task SendVibrateAsync(string macKey, byte sensorId, ushort durationMs, byte amplitude)
    {
        var payload = new byte[4];
        payload[0] = sensorId;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1, 2), durationMs);
        payload[3] = amplitude;
        var summary = $"Vibrate sensorId={sensorId}, duration={durationMs}ms, amp={amplitude}";
        return SendCommandToMacAsync(macKey, ReceivePacketType.Vibrate, payload, summary, null, false);
    }

    private async Task SendCommandToMacAsync(
        string macKey,
        ReceivePacketType type,
        byte[] payload,
        string summary,
        SendPacketType? expectedResponseType,
        bool trackPending)
    {
        TrackerSession? session;
        lock (_syncRoot)
        {
            _sessionsByMac.TryGetValue(macKey, out session);
        }

        if (session is null)
        {
            Log("OUT", "-", type.ToString(), $"No session for mac={macKey}");
            return;
        }

        await SendPacketToSessionAsync(session, type, payload, summary, expectedResponseType, trackPending);
    }

    public async Task SendDiscoveryHandshakeAsync(int port)
    {
        if (_udpClient is null)
        {
            return;
        }

        var endpoint = new IPEndPoint(IPAddress.Broadcast, port);
        await _udpClient.SendAsync(DiscoveryHandshakePacket, DiscoveryHandshakePacket.Length, endpoint);
        Log("OUT", endpoint.ToString(), "DiscoveryHandshake", "Broadcast handshake packet");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_udpClient is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udpClient.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("SYSTEM", "-", "ReceiveError", ex.Message);
                continue;
            }

            var parsedPackets = TrackerPacketParser.ParseDatagram(received.Buffer, DateTime.UtcNow);
            if (parsedPackets.Count == 0)
            {
                Log("IN", received.RemoteEndPoint.ToString(), "Unknown", ToHex(received.Buffer, 32));
                continue;
            }

            foreach (var packet in parsedPackets)
            {
                ProcessParsedPacket(received.RemoteEndPoint, packet);
            }
        }
    }

    private async Task DiscoveryBroadcastLoopAsync(int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (SessionState == TrackerSessionState.Discovery)
                {
                    await SendDiscoveryHandshakeAsync(port);
                }

                await Task.Delay(DiscoveryBroadcastInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("SYSTEM", "-", "DiscoveryError", ex.Message);
                await Task.Delay(DiscoveryBroadcastInterval, token);
            }
        }
    }

    private void ProcessParsedPacket(IPEndPoint endpoint, ParsedPacket packet)
    {
        var packetType = (SendPacketType)packet.PacketType;

        if (packetType == SendPacketType.Handshake && packet.IsDiscoveryHandshake)
        {
            HandleDiscoveryPacket(endpoint, packet);
            return;
        }

        if (packet.PacketType == (byte)ReceivePacketType.Handshake)
        {
            Log("IN", endpoint.ToString(), "SpecialHandshake", "Received special handshake packet");
            return;
        }

        var session = ResolveSessionForIncoming(endpoint, packetType);
        if (session is null)
        {
            Log("SYSTEM", endpoint.ToString(), "Ignored", "Packet from unknown tracker endpoint");
            return;
        }

        session.LastSeenUtc = DateTime.UtcNow;
        TryResolvePendingCommand(session, endpoint, packetType, packet);
        PublishActiveTrackerInfo(session);

        var packetTypeName = GetSendPacketTypeName(packet.PacketType);
        switch (packetType)
        
        {
            case SendPacketType.RotationData:
                if (TryParseRotation(GetTrackerId(session), packet.Payload, out var rotation))
                {
                    RotationReceived?.Invoke(rotation);
                    Log("IN", endpoint.ToString(), packetTypeName,
                        $"sensor={rotation.SensorId}, quat=({rotation.X:F3}, {rotation.Y:F3}, {rotation.Z:F3}, {rotation.W:F3}), acc={rotation.Accuracy}");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, $"Invalid payload ({packet.Payload.Length} bytes)");
                }

                break;

            case SendPacketType.Accel:
                if (TryParseAccel(GetTrackerId(session), packet.Payload, out var accel))
                {
                    AccelReceived?.Invoke(accel);
                    Log("IN", endpoint.ToString(), packetTypeName,
                        $"sensor={accel.SensorId}, accel=({accel.X:F3}, {accel.Y:F3}, {accel.Z:F3})");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, $"Invalid payload ({packet.Payload.Length} bytes)");
                }

                break;

            case SendPacketType.SensorInfo:
                if (TryParseSensorInfo(GetTrackerId(session), packet.Payload, out var sensorInfo))
                {
                    SensorInfoReceived?.Invoke(sensorInfo);
                    OnSensorInfoReceivedInSession(session, endpoint);
                    Log("IN", endpoint.ToString(), packetTypeName,
                        $"sensor={sensorInfo.SensorId}, state={sensorInfo.SensorState}, type={sensorInfo.SensorType}, pos={sensorInfo.SensorPosition}");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, $"Invalid payload ({packet.Payload.Length} bytes)");
                }

                break;

            case SendPacketType.BatteryLevel:
                if (TryParseBatteryLevel(GetTrackerId(session), packet.Payload, out var battery))
                {
                    BatteryLevelReceived?.Invoke(battery);
                    Log("IN", endpoint.ToString(), packetTypeName,
                        $"voltage={battery.Voltage:F2}V, battery={(battery.Percentage * 100f):F1}%");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, $"Invalid payload ({packet.Payload.Length} bytes)");
                }

                break;

            case SendPacketType.SignalStrength:
                if (TryParseSignalStrength(GetTrackerId(session), packet.Payload, out var signal))
                {
                    SignalStrengthReceived?.Invoke(signal);
                    Log("IN", endpoint.ToString(), packetTypeName,
                        $"sensor={signal.SensorId}, rssi={signal.Strength}");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, $"Invalid payload ({packet.Payload.Length} bytes)");
                }

                break;

            case SendPacketType.PingPong:
                if (TryParsePingPong(GetTrackerId(session), packet.Payload, out var ping))
                {
                    PingPongReceived?.Invoke(ping);
                    Log("IN", endpoint.ToString(), packetTypeName, $"echo={ping.EchoValue}");
                }
                else
                {
                    Log("IN", endpoint.ToString(), packetTypeName, ToHex(packet.Payload, 32));
                }

                break;

            case SendPacketType.Handshake:
            case SendPacketType.HeartBeat:
            case SendPacketType.FeatureFlags:
            case SendPacketType.AcknowledgeConfigChange:
            case SendPacketType.Temperature:
                Log("IN", endpoint.ToString(), packetTypeName, ToHex(packet.Payload, 32));
                break;

            default:
                Log("IN", endpoint.ToString(), packetTypeName, ToHex(packet.Payload, 32));
                break;
        }
    }

    private void HandleDiscoveryPacket(IPEndPoint endpoint, ParsedPacket packet)
    {
        var info = packet.DiscoveryInfo;
        var macKey = info?.MacKey ?? $"ep:{endpoint}";

        TrackerSession session;
        var isNewSession = false;
        var endpointChanged = false;
        var oldEndpoint = default(IPEndPoint);

        lock (_syncRoot)
        {
            if (!_sessionsByMac.TryGetValue(macKey, out session!))
            {
                session = new TrackerSession(macKey, info?.Mac ?? Array.Empty<byte>(), endpoint);
                _sessionsByMac[macKey] = session;
                isNewSession = true;
            }

            oldEndpoint = session.EndPoint;
            endpointChanged = !session.EndPoint.Equals(endpoint);
            if (endpointChanged)
            {
                _macByEndpoint.Remove(GetEndpointKey(session.EndPoint));
                session.EndPoint = endpoint;
            }

            session.LastSeenUtc = DateTime.UtcNow;
            session.ProtocolVersion = info?.ProtocolVersion;
            session.FirmwareVersion = info?.FirmwareVersion;
            session.VendorName = info?.VendorName;
            session.ProductName = info?.ProductName;
            session.TrackerType = info?.TrackerType;

            _macByEndpoint[GetEndpointKey(endpoint)] = macKey;
            _activeMacKey = macKey;
        }

        if (endpointChanged)
        {
            TrackerEndpointChanged?.Invoke(endpoint);
            Log("SYSTEM", endpoint.ToString(), "TrackerEndpoint",
                $"Tracker endpoint updated (mac={macKey}, old={oldEndpoint})");
        }

        Log("IN", endpoint.ToString(), "DiscoveryHandshake",
            info is null
                ? "Tracker discovery received (MAC parse failed)"
                : $"mac={macKey}, protocol={info.ProtocolVersion}, fw={info.FirmwareVersion}");

        if (isNewSession)
        {
            TrackerConnected?.Invoke(new TrackerConnectedMessage(
                macKey,
                GetTrackerId(session),
                endpoint,
                info?.ProtocolVersion,
                info?.FirmwareVersion,
                info?.VendorName,
                info?.ProductName));
        }

        _ = SendSpecialHandshakeToEndpointAsync(endpoint);
        TransitionState(TrackerSessionState.SessionStart);
        StartSessionKeepAliveLoop();
        PublishActiveTrackerInfo(session);
    }

    private async Task SendSpecialHandshakeToEndpointAsync(IPEndPoint endpoint)
    {
        if (_udpClient is null)
        {
            return;
        }

        try
        {
            await _udpClient.SendAsync(DiscoveryHandshakePacket, DiscoveryHandshakePacket.Length, endpoint);
            Log("OUT", endpoint.ToString(), "DiscoveryHandshake", "Special handshake sent to tracker endpoint");
        }
        catch (Exception ex)
        {
            Log("SYSTEM", endpoint.ToString(), "HandshakeSendError", ex.Message);
        }
    }

    private TrackerSession? GetPreferredSession()
    {
        lock (_syncRoot)
        {
            if (_activeMacKey is not null && _sessionsByMac.TryGetValue(_activeMacKey, out var active))
            {
                return active;
            }

            return _sessionsByMac.Values
                .OrderByDescending(x => x.LastSeenUtc)
                .FirstOrDefault();
        }
    }

    private void OnSensorInfoReceivedInSession(TrackerSession session, IPEndPoint endpoint)
    {
        if (session.FirstSensorInfoReceived)
        {
            return;
        }

        session.FirstSensorInfoReceived = true;
        Log("SYSTEM", endpoint.ToString(), "Session", "First SensorInfo received");

        if (session.MacKey == _activeMacKey)
        {
            TransitionState(TrackerSessionState.Streaming);
        }
    }

    private void StartSessionKeepAliveLoop()
    {
        var newCts = new CancellationTokenSource();
        CancellationTokenSource? oldCts;
        lock (_syncRoot)
        {
            oldCts = _sessionCts;
            _sessionCts = newCts;
        }

        oldCts?.Cancel();
        oldCts?.Dispose();

        _sessionKeepAliveTask = Task.Run(() => SessionKeepAliveLoopAsync(newCts.Token), newCts.Token);
    }

    private TrackerSession? ResolveSessionForIncoming(IPEndPoint endpoint, SendPacketType packetType)
    {
        var endpointKey = GetEndpointKey(endpoint);
        TrackerSession? reboundCandidate = null;

        lock (_syncRoot)
        {
            if (_macByEndpoint.TryGetValue(endpointKey, out var macKey) && _sessionsByMac.TryGetValue(macKey, out var existing))
            {
                return existing;
            }

            // If tracker changed only source port/IP but we have one active candidate with same IP, rebind it.
            var candidates = _sessionsByMac.Values
                .Where(x => DateTime.UtcNow - x.LastSeenUtc <= SessionStaleTimeout)
                .Where(x => Equals(x.EndPoint.Address, endpoint.Address))
                .ToList();

            if (candidates.Count == 1)
            {
                var candidate = candidates[0];
                _macByEndpoint.Remove(GetEndpointKey(candidate.EndPoint));
                candidate.EndPoint = endpoint;
                candidate.LastSeenUtc = DateTime.UtcNow;
                _macByEndpoint[endpointKey] = candidate.MacKey;
                _activeMacKey = candidate.MacKey;
                reboundCandidate = candidate;
            }
        }

        if (reboundCandidate is not null)
        {
            TrackerEndpointChanged?.Invoke(endpoint);
            Log("SYSTEM", endpoint.ToString(), "TrackerEndpoint",
                $"Endpoint rebind by IP for packet {packetType} (mac={reboundCandidate.MacKey})");
            return reboundCandidate;
        }

        return null;
    }

    private async Task SendCommandAsync(ReceivePacketType type, byte[] payload, string summary, SendPacketType? expectedResponseType, bool trackPending)
    {
        var session = GetPreferredSession();
        if (session is null)
        {
            Log("OUT", "-", type.ToString(), "No tracker endpoint available yet");
            return;
        }

        await SendPacketToSessionAsync(session, type, payload, summary, expectedResponseType, trackPending);
    }

    private async Task SendPacketToSessionAsync(
        TrackerSession session,
        ReceivePacketType type,
        byte[] payload,
        string summary,
        SendPacketType? expectedResponseType,
        bool trackPending)
    {
        if (_udpClient is null)
        {
            return;
        }

        var packet = BuildCommandPacket(type, payload, session, out var packetNumber);
        await _udpClient.SendAsync(packet, packet.Length, session.EndPoint);

        if (trackPending)
        {
            RegisterPendingCommand(packetNumber, session, type, expectedResponseType, payload);
            Log("OUT", session.EndPoint.ToString(), type.ToString(), $"#{packetNumber} {summary} (await {expectedResponseType})");
        }
        else
        {
            Log("OUT", session.EndPoint.ToString(), type.ToString(), summary);
        }
    }

    private byte[] BuildCommandPacket(ReceivePacketType type, byte[] payload, TrackerSession? session, out ulong packetNumber)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = (byte)type;

        lock (_syncRoot)
        {
            if (session is not null)
            {
                packetNumber = session.OutPacketNumber++;
            }
            else
            {
                packetNumber = _fallbackPacketNumber++;
            }
        }

        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(4, 8), packetNumber);
        payload.CopyTo(packet, 12);
        return packet;
    }

    private void RegisterPendingCommand(
        ulong packetNumber,
        TrackerSession session,
        ReceivePacketType commandType,
        SendPacketType? expectedResponseType,
        byte[] correlationPayload)
    {
        lock (_syncRoot)
        {
            _pendingCommands[packetNumber] = new PendingCommandEntry(new PendingCommandInfo(
                packetNumber,
                session.EndPoint.ToString(),
                commandType,
                expectedResponseType,
                DateTime.UtcNow,
                DateTime.UtcNow + PendingCommandTimeout,
                PendingCommandStatus.Pending,
                null,
                "pending"),
                correlationPayload.ToArray());
        }
    }

    private void TryResolvePendingCommand(TrackerSession session, IPEndPoint endpoint, SendPacketType incomingType, ParsedPacket packet)
    {
        PendingCommandInfo? completed = null;

        lock (_syncRoot)
        {
            if (packet.PacketNumber is ulong packetNumber &&
                _pendingCommands.TryGetValue(packetNumber, out var direct) &&
                direct.Info.Endpoint == endpoint.ToString() &&
                (direct.Info.ExpectedResponseType is null || direct.Info.ExpectedResponseType == incomingType))
            {
                completed = direct.Info with
                {
                    Status = PendingCommandStatus.Completed,
                    CompletedAtUtc = DateTime.UtcNow,
                    Result = $"resolved by packetNumber with {incomingType}",
                };
                _pendingCommands.Remove(packetNumber);
            }
            else if (incomingType == SendPacketType.PingPong && TryParsePingPong(GetTrackerId(session), packet.Payload, out var pong))
            {
                var expectedPayload = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(expectedPayload, pong.EchoValue);

                foreach (var kv in _pendingCommands.ToArray())
                {
                    var pending = kv.Value;
                    if (pending.Info.Endpoint != endpoint.ToString() || pending.Info.CommandType != ReceivePacketType.PingPong)
                    {
                        continue;
                    }

                    if (!pending.CorrelationPayload.SequenceEqual(expectedPayload))
                    {
                        continue;
                    }

                    completed = pending.Info with
                    {
                        Status = PendingCommandStatus.Completed,
                        CompletedAtUtc = DateTime.UtcNow,
                        Result = "resolved by ping echo payload",
                    };
                    _pendingCommands.Remove(kv.Key);
                    break;
                }
            }
        }

        if (completed is not null)
        {
            Log("SYSTEM", endpoint.ToString(), "CommandAck",
                $"#{completed.PacketNumber} {completed.CommandType} -> {incomingType} ({completed.Result})");
        }
    }

    private void CheckPendingCommandTimeouts()
    {
        var timedOut = new List<PendingCommandInfo>();

        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _pendingCommands.ToArray())
            {
                if (kv.Value.Info.DeadlineUtc > now)
                {
                    continue;
                }

                timedOut.Add(kv.Value.Info with
                {
                    Status = PendingCommandStatus.TimedOut,
                    CompletedAtUtc = now,
                    Result = "timeout",
                });
                _pendingCommands.Remove(kv.Key);
            }
        }

        foreach (var pending in timedOut)
        {
            Log("SYSTEM", pending.Endpoint, "CommandTimeout",
                $"#{pending.PacketNumber} {pending.CommandType} timeout, expected={pending.ExpectedResponseType}");
        }
    }

    private async Task SessionKeepAliveLoopAsync(CancellationToken token)
    {
        var nextPingAt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var sessions = GetAliveSessions();
                foreach (var session in sessions)
                {
                    await SendPacketToSessionAsync(session, ReceivePacketType.HeartBeat, Array.Empty<byte>(), "HeartBeat", null, false);

                    var now = DateTime.UtcNow;
                    if (now >= nextPingAt)
                    {
                        var pingPayload = new byte[8];
                        BinaryPrimitives.WriteInt64BigEndian(pingPayload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        await SendPacketToSessionAsync(session, ReceivePacketType.PingPong, pingPayload, "PingPong", SendPacketType.PingPong, true);
                    }

                    if (session.MacKey == _activeMacKey)
                    {
                        PublishActiveTrackerInfo(session);
                    }
                }

                if (DateTime.UtcNow >= nextPingAt)
                {
                    nextPingAt = DateTime.UtcNow + PingInterval;
                }

                if (sessions.Count == 0)
                {
                    TransitionState(TrackerSessionState.Discovery);
                    PublishEmptyTrackerInfo();
                }

                CheckPendingCommandTimeouts();
                await Task.Delay(HeartbeatInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("SYSTEM", "-", "KeepAliveError", ex.Message);
                await Task.Delay(HeartbeatInterval, token);
            }
        }
    }

    private List<TrackerSession> GetAliveSessions()
    {
        var now = DateTime.UtcNow;
        var removed = new List<TrackerSession>();
        List<TrackerSession> aliveSessions;
        IPEndPoint? newActiveEndpoint = null;

        lock (_syncRoot)
        {
            foreach (var kv in _sessionsByMac.ToArray())
            {
                if (now - kv.Value.LastSeenUtc <= SessionStaleTimeout)
                {
                    continue;
                }

                _sessionsByMac.Remove(kv.Key);
                _macByEndpoint.Remove(GetEndpointKey(kv.Value.EndPoint));
                removed.Add(kv.Value);
            }

            if (_activeMacKey is not null && !_sessionsByMac.ContainsKey(_activeMacKey))
            {
                _activeMacKey = _sessionsByMac.Values.OrderByDescending(x => x.LastSeenUtc).Select(x => x.MacKey).FirstOrDefault();
                if (_activeMacKey is not null && _sessionsByMac.TryGetValue(_activeMacKey, out var active))
                {
                    newActiveEndpoint = active.EndPoint;
                }
            }

            aliveSessions = _sessionsByMac.Values.ToList();
        }

        if (newActiveEndpoint is not null)
        {
            TrackerEndpointChanged?.Invoke(newActiveEndpoint);
        }

        foreach (var stale in removed)
        {
            Log("SYSTEM", stale.EndPoint.ToString(), "SessionStale", $"Session removed (mac={stale.MacKey})");
            TrackerDisconnected?.Invoke(stale.MacKey);
        }

        return aliveSessions;
    }

    private void TransitionState(TrackerSessionState newState)
    {
        var changed = false;

        lock (_syncRoot)
        {
            if (_sessionState != newState)
            {
                _sessionState = newState;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Log("SYSTEM", "-", "SessionState", $"{newState}");
        SessionStateChanged?.Invoke(newState);
    }

    private void Log(string direction, string endpoint, string packetType, string summary)
    {
        PacketLogged?.Invoke(new PacketLogMessage(DateTime.UtcNow, direction, endpoint, packetType, summary));
    }

    // Public hook for non-protocol diagnostic messages (e.g. calibration debug
    // output from TrackerManager). Routes through the same PacketLogged channel
    // so it appears in the existing system log view.
    public void LogSystem(string category, string summary)
    {
        Log("SYSTEM", "-", category, summary);
    }

    private static bool TryParseRotation(string trackerId, byte[] payload, out RotationDataMessage rotation)
    {
        // RotationData payload offsets (base=0):
        // 0:sensorId, 1:dataType, 2:x, 6:y, 10:z, 14:w, 18:accuracy
        if (payload.Length < 19)
        {
            rotation = default!;
            return false;
        }

        var sensorId = payload[0];
        var dataType = payload[1];
        var x = ReadFloatBigEndian(payload, 2);
        var y = ReadFloatBigEndian(payload, 6);
        var z = ReadFloatBigEndian(payload, 10);
        var w = ReadFloatBigEndian(payload, 14);
        var accuracy = payload[18];

        rotation = new RotationDataMessage(trackerId, sensorId, dataType, x, y, z, w, accuracy);
        return true;
    }

    private static bool TryParseAccel(string trackerId, byte[] payload, out AccelDataMessage accel)
    {
        if (payload.Length < 13)
        {
            accel = default!;
            return false;
        }

        var x = ReadFloatBigEndian(payload, 0);
        var y = ReadFloatBigEndian(payload, 4);
        var z = ReadFloatBigEndian(payload, 8);
        var sensorId = payload[12];

        accel = new AccelDataMessage(trackerId, sensorId, x, y, z);
        return true;
    }

    private static bool TryParseSensorInfo(string trackerId, byte[] payload, out SensorInfoMessage sensorInfo)
    {
        if (payload.Length < 18)
        {
            sensorInfo = default!;
            return false;
        }

        var sensorId = payload[0];
        var sensorState = payload[1];
        var sensorType = payload[2];
        var sensorConfig = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(3, 4));
        var hasCompletedRestCalibration = payload[7] != 0;
        var sensorPosition = payload[8];
        var sensorDataType = payload[9];
        var tpsCounter = ReadFloatBigEndian(payload, 10);
        var dataCounter = ReadFloatBigEndian(payload, 14);

        sensorInfo = new SensorInfoMessage(
            trackerId,
            sensorId,
            sensorState,
            sensorType,
            sensorConfig,
            hasCompletedRestCalibration,
            sensorPosition,
            sensorDataType,
            tpsCounter,
            dataCounter);
        return true;
    }

    private static bool TryParseBatteryLevel(string trackerId, byte[] payload, out BatteryLevelMessage message)
    {
        // BatteryLevel payload offsets (base=0): 0:voltage(float BE), 4:level01(float BE)
        if (payload.Length < 8)
        {
            message = default!;
            return false;
        }

        var voltage = ReadFloatBigEndian(payload, 0);
        var level01 = ReadFloatBigEndian(payload, 4);

        // Firmware sends battery level in normalized range 0..1.
        if (float.IsNaN(level01) || float.IsInfinity(level01))
        {
            level01 = 0f;
        }

        level01 = Math.Clamp(level01, 0f, 1f);
        message = new BatteryLevelMessage(trackerId, voltage, level01);
        return true;
    }

    private static bool TryParseSignalStrength(string trackerId, byte[] payload, out SignalStrengthMessage message)
    {
        if (payload.Length < 2)
        {
            message = default!;
            return false;
        }

        message = new SignalStrengthMessage(trackerId, payload[0], payload[1]);
        return true;
    }

    private static bool TryParsePingPong(string trackerId, byte[] payload, out PingPongMessage message)
    {
        if (payload.Length < 8)
        {
            message = default!;
            return false;
        }

        var echoValue = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(0, 8));
        message = new PingPongMessage(trackerId, echoValue);
        return true;
    }

    private static float ReadFloatBigEndian(byte[] bytes, int offset)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static string ToHex(byte[] payload, int maxBytes)
    {
        if (payload.Length == 0)
        {
            return "empty";
        }

        var take = Math.Min(payload.Length, maxBytes);
        var hex = Convert.ToHexString(payload.AsSpan(0, take));
        if (payload.Length > maxBytes)
        {
            return $"{hex}... ({payload.Length.ToString(CultureInfo.InvariantCulture)} bytes)";
        }

        return $"{hex} ({payload.Length.ToString(CultureInfo.InvariantCulture)} bytes)";
    }

    private static string GetSendPacketTypeName(byte rawValue)
    {
        return Enum.IsDefined(typeof(SendPacketType), rawValue)
            ? ((SendPacketType)rawValue).ToString()
            : $"Unknown({rawValue})";
    }

    private static byte[] BuildDiscoveryHandshakePacket()
    {
        var signature = Encoding.ASCII.GetBytes("Hey OVR =D 5");
        var packet = new byte[1 + signature.Length];
        packet[0] = (byte)ReceivePacketType.Handshake;
        signature.CopyTo(packet, 1);
        return packet;
    }

    private static string GetEndpointKey(IPEndPoint endpoint)
    {
        return endpoint.ToString();
    }

    private static string GetTrackerId(TrackerSession session)
    {
        if (session.Mac.Length == 6)
        {
            return string.Join(":", session.Mac.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
        }

        return session.MacKey;
    }

    private void PublishActiveTrackerInfo(TrackerSession session)
    {
        var trackerId = GetTrackerId(session);
        var mac = session.Mac.Length == 6 ? trackerId : "-";
        var info = new ActiveTrackerInfoMessage(
            trackerId,
            session.EndPoint.ToString(),
            mac,
            session.ProtocolVersion,
            session.FirmwareVersion ?? "-",
            session.VendorName ?? "-",
            session.ProductName ?? "-",
            session.ConnectedSinceUtc,
            session.LastSeenUtc,
            DateTime.UtcNow - session.ConnectedSinceUtc);

        ActiveTrackerInfoChanged?.Invoke(info);
    }

    private void PublishEmptyTrackerInfo()
    {
        ActiveTrackerInfoChanged?.Invoke(new ActiveTrackerInfoMessage(
            "-",
            "-",
            "-",
            null,
            "-",
            "-",
            "-",
            DateTime.UtcNow,
            DateTime.UtcNow,
            TimeSpan.Zero));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private sealed class TrackerSession
    {
        public TrackerSession(string macKey, byte[] mac, IPEndPoint endPoint)
        {
            MacKey = macKey;
            Mac = mac;
            EndPoint = endPoint;
            ConnectedSinceUtc = DateTime.UtcNow;
            LastSeenUtc = DateTime.UtcNow;
        }

        public string MacKey { get; }

        public byte[] Mac { get; }

        public IPEndPoint EndPoint { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime ConnectedSinceUtc { get; set; }

        public ulong OutPacketNumber { get; set; } = 1;

        public bool FirstSensorInfoReceived { get; set; }

        public int? ProtocolVersion { get; set; }

        public string? FirmwareVersion { get; set; }

        public string? VendorName { get; set; }

        public string? ProductName { get; set; }

        public byte? TrackerType { get; set; }
    }

    private sealed record PendingCommandEntry(PendingCommandInfo Info, byte[] CorrelationPayload);
}
