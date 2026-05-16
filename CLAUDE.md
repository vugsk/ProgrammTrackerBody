# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```powershell
# Build
dotnet build .\ProgrammTrackerBody\ProgrammTrackerBody.csproj

# Run
dotnet run --project .\ProgrammTrackerBody\ProgrammTrackerBody.csproj

# Send a synthetic tracker packet to test without real hardware (start server first)
powershell -ExecutionPolicy Bypass -File .\scripts\send_sample_tracker_packet.ps1 -TargetHost 127.0.0.1 -Port 6969 -SensorId 0
```

There are no automated tests in the project — manual testing is done via the PowerShell script above.

## Architecture

**Stack:** C# / WPF / .NET 10.0-windows. No external NuGet dependencies.

**Purpose:** A desktop UI for receiving SlimeVR-compatible tracker telemetry over UDP, visualizing sensor data, and sending commands back to the tracker.

### Layer separation

The app uses a SlimeVR-Server-style multi-tracker architecture: `Networking` is the UDP transport, `Domain` holds plain models (no UI deps), `Services` wires server events to mutable view models, `ViewModels` expose them to MVVM-bound views.

| Layer | Files | Responsibility |
|---|---|---|
| Networking | `Networking/UdpTrackerServer.cs`, `TrackerPacketParser.cs`, `ProtocolTypes.cs` | UDP server, packet parsing, protocol records. Raises per-tracker events (`TrackerConnected`, `TrackerDisconnected`, `RotationReceived`, …) |
| Domain | `Domain/{BodyPart,MountingOrientation,TrackerModel,ResetMath}.cs` | Pure models. `TrackerModel` is `INotifyPropertyChanged`. `ResetMath` does yaw/full/mounting offset quaternion math. |
| Persistence | `Persistence/ConfigStore.cs` | `AppConfig` JSON in `%AppData%/ProgrammTrackerBody/config.json` (atomic write via `.tmp` + `Move`). |
| Services | `Services/TrackerManager.cs`, `LocalizationService.cs` | `TrackerManager` is the single bridge between server events and `ObservableCollection<TrackerModel>`. `LocalizationService` swaps `Strings.<lang>.xaml` `MergedDictionaries` at runtime. |
| Resources | `Resources/Strings.{ru,en}.xaml` | `ResourceDictionary` with `x:Key`-keyed strings. Bind via `{DynamicResource}` for static text, `{StaticResource ResourceLookupConverter}` for VM-supplied keys. |
| ViewModels | `ViewModels/{Main,Dashboard,Trackers,Calibration,Logs}ViewModel.cs` + `RelayCommand.cs`, `ViewModelBase.cs` | MVVM. `MainViewModel` owns the others. |
| Views | `Views/MainWindow.xaml`, `Views/{Dashboard,Trackers,Calibration,Logs}Tab.xaml` + `Converters/*.cs` | TabControl shell with one `UserControl` per tab. Converters under `Views/Converters/`. |
| App entry | `App.xaml`, `App.xaml.cs` | `OnStartup` boots `ConfigStore`, `UdpTrackerServer`, `TrackerManager`, sets language, creates `MainWindow` + `MainViewModel`. No `StartupUri`. |

### UDP protocol & state machine

Default port: **6969**. Packet format: `00 00 00 [type:1 byte] [packetNumber:8 bytes big-endian] [payload]`. Bundle packets (type 100) contain multiple inner packets with size prefixes. Discovery uses a plaintext handshake `"Hey OVR =D 5"`.

Session states (managed inside `UdpTrackerServer`):
1. **DISCOVERY** — broadcasts handshake every 1 s, waits for tracker response
2. **SESSION_START** — endpoint pinned, sends HeartBeat every 1 s and PingPong every 3 s
3. **STREAMING** — entered after first `SensorInfo` packet; actively receiving telemetry

Sessions are keyed by MAC address. A session is dropped after 10 s of inactivity, reverting to DISCOVERY.

### Threading model

Three background loops run concurrently via `async Task` + `CancellationToken`:
- **ReceiveLoopAsync** — awaits `UdpClient.ReceiveAsync`, parses and dispatches packets
- **DiscoveryBroadcastLoopAsync** — sends handshake every 1 s while in DISCOVERY state
- **SessionKeepAliveLoopAsync** — sends heartbeat every 1 s, pings every 3 s, cleans up stale sessions; uses a separate `_sessionCts` so it can be replaced without stopping the server

Shared state is protected by `_syncRoot`. All UI mutations go through `Dispatcher.Invoke()`. `UdpTrackerServer` raises events; `MainWindow` subscribes and marshals to the UI thread.

### Non-obvious protocol details

- **Discovery handshake format differs**: it is only 13 bytes total with type byte at offset 0, not the standard 12-byte header with type at offset 3. Parsing these is a special case in `TrackerPacketParser`.
- **Bundle inner packets have no individual packet numbers**: they inherit the outer Bundle's packet number (set as `IsFromBundle = true`).
- **PingPong correlation uses payload echo**: the 8-byte UTC-milliseconds payload sent with a ping is echoed back; the server matches the echo across pending commands if direct packet-number matching fails.
- **Battery percentage is 0.0–1.0**: `BatteryLevel` payload sends a normalized float; the UI multiplies by 100 for display.
- **Sessions are keyed by MAC as hex string** (`StringComparer.OrdinalIgnoreCase`). If a tracker's IP changes but MAC matches, the server re-binds the endpoint automatically.
- **Pending commands time out after 3 s** and are logged as `TimedOut`; the keep-alive loop handles cleanup.
- All multi-byte fields are **big-endian**; use `BinaryPrimitives.ReadInt32BigEndian` + `BitConverter.Int32BitsToSingle` for floats.

### UI data flow

`UdpTrackerServer` raises events on background threads → `TrackerManager` and `LogsViewModel` marshal via `Dispatcher.Invoke` → mutate `ObservableCollection<TrackerModel>` / `ObservableCollection<PacketLogRow>` → views auto-refresh.

- Sensor rotation comes in raw, then `TrackerManager.RecomputeDisplay` applies `mounting → yaw → full` offsets (`ResetMath.ApplyOffsets`) to produce `DisplayRotation`. Trackers/Calibration tabs bind to `DisplayRotation`, not `RawRotation`.
- Calibration "Reset" buttons compute new offsets from current `RawRotation` and store them on `TrackerModel`. `TrackerManager` listens to `TrackerModel.PropertyChanged` for offset/setting changes and debounces `ConfigStore.SaveAsync` (500 ms `DispatcherTimer`).
- Packet log is capped at 600 rows; system log at 12 000 chars (both in `LogsViewModel`).

### Localization

UI is RU/EN with runtime switching. `LocalizationService.Instance.SetLanguage("ru" | "en")` swaps the `Strings.<lang>.xaml` `ResourceDictionary` in `Application.Current.Resources.MergedDictionaries`. Static strings use `{DynamicResource Key}` so they refresh automatically. ViewModel-driven keys (e.g. computed `SessionStateKey`) bind via `{Binding ..., Converter={StaticResource ResourceLookupConverter}}`; the VM listens to `LocalizationService.PropertyChanged` and re-fires its own properties so the converter re-evaluates.

### Reset math (server-side, like SlimeVR)

`Domain/ResetMath.cs`:
- **Yaw reset**: `ComputeYawOffset` extracts yaw from current raw quat, returns inverse yaw rotation around world Y. Cancels heading drift only.
- **Full reset**: `ComputeFullOffset` returns `Inverse(currentRaw)`. Snaps the displayed orientation to identity.
- **Mounting reset**: `ComputeMountingOffset(raw, expected)` aligns the sensor's current yaw with the orientation enum (Front/Back/Left/Right). Approximation; tune against real hardware if needed.
- All offsets are stored on `TrackerModel` and persisted to JSON. Trackers themselves never see these — pure server-side math.

### Protocol documentation

Russian-language specs are in the project directory:
- `TRACKER_PROTOCOL_CONTEXT.md` — full SlimeVR packet types and payload structures
- `SERVER_PROTOCOL_SPEC_RU.md` — server-side protocol details
- `SERVER_BYTE_OFFSETS_ROTATION_BATTERY_RU.md` — binary field offsets reference
