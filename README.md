# Tracker Wi-Fi Prototype (WPF)

Prototype app for receiving SlimeVR-like tracker packets over UDP, showing telemetry in UI, and sending commands back to tracker.

## Implemented

- UDP server on configurable port (default `6969`)
- Step 1 (`DISCOVERY`):
  - legacy discovery handshake broadcast (`[3] + "Hey OVR =D 5"`)
  - tracker endpoint is fixed by discovery handshake
- Step 2 (`SESSION_START`):
  - automatic `HeartBeat` + periodic `PingPong` after discovery
  - session state in UI (`DISCOVERY`, `SESSION_START`, `STREAMING`)
  - transition to `STREAMING` on first `SensorInfo`
- Parser for:
  - normal packets (`00 00 00 type + packetNumber + payload`)
  - `Bundle (100)` packets with inner packets
  - legacy discovery handshake packet
- UI tables for:
  - sensor data (`RotationData`, `Accel`, `SensorInfo`)
  - packet log (in/out/system)
- Manual commands to tracker:
  - `HeartBeat` (`ReceivePacketType = 1`)
  - `PingPong` (`ReceivePacketType = 10`)
  - `SetConfigFlag` (`ReceivePacketType = 25`)

## Run

```powershell
cd C:\Users\nikit\RiderProjects\ProgrammTrackerBody
dotnet run --project .\ProgrammTrackerBody\ProgrammTrackerBody.csproj
```

## Quick local test without real tracker

You can send a synthetic `RotationData` packet to localhost:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\send_sample_tracker_packet.ps1 -TargetHost 127.0.0.1 -Port 6969 -SensorId 0
```

Then in app:
1. Start server.
2. Check state `DISCOVERY` in window header.
3. Send/receive handshake from tracker, endpoint should appear.
4. Confirm automatic outgoing `HeartBeat` and `PingPong` in `Packet log`.
5. Check that sensor row updates in `Sensors`.

## Notes

- This is a prototype focused on visibility/debugging.
- Payload decoding currently covers the main telemetry packets.
- Any unknown packet is still logged as hex payload for inspection.