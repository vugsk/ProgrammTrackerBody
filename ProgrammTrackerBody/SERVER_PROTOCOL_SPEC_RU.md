# Спецификация сервера для совместимости с `SlimeVR-Tracker-ESP`

Этот документ предназначен как контекст для AI-агента/разработчика, который реализует серверную часть (UDP), совместимую с прошивкой трекера.

Документ основан на текущей логике прошивки в:
- `src/network/connection.cpp`
- `src/network/connection.h`
- `src/network/packets.h`
- `src/network/featureflags.h`
- `src/debug.h`

---

## 1. Цель сервера

Сервер должен:
1. Обнаруживать трекер в LAN (через UDP discovery/handshake).
2. Удерживать соединение (heartbeat, keepalive).
3. Принимать телеметрию от трекера (rotation, battery, sensor info и т.д.).
4. Опционально отправлять команды/флаги конфигурации обратно трекеру.

Если сервер не отправляет корректные входящие пакеты после handshake, трекер уходит в timeout примерно через 3 секунды.

---

## 2. Сетевые требования

- Транспорт: UDP
- Порт по умолчанию: `6969`
- Трекер и сервер должны быть в одной LAN/подсети (broadcast discovery)
- На ПК/сервере входящий/исходящий UDP на `6969` не должен блокироваться firewall

Важно: даже если discovery прошел, соединение развалится без heartbeat от сервера.

---

## 3. Жизненный цикл (state machine)

### Состояния трекера (с точки зрения сервера)
1. `DISCOVERY`
   - трекер периодически шлет discovery/handshake в сеть
2. `CONNECTED`
   - сервер ответил handshake, трекер принял сервер endpoint
3. `ACTIVE`
   - идет обмен данными (rotation/sensor info/...)
4. `TIMEOUT`
   - если нет валидных входящих пакетов от сервера > ~3000ms, трекер возвращается в `DISCOVERY`

### Что должен делать сервер
1. Слушать UDP `:6969`
2. На discovery от нового endpoint:
   - зарегистрировать endpoint трекера (`IP:port`)
   - отправить handshake-ответ
3. После handshake:
   - слать heartbeat каждые ~1000ms (рекомендуется 500-1000ms)
4. Принимать телеметрию и обновлять состояние устройства
5. При потере активности endpoint помечать трекер offline

---

## 4. Формат пакетов

## 4.1 Обычный пакет (большинство пакетов)

Бинарный формат:
- bytes `[0..2]`: `0x00 0x00 0x00`
- byte `[3]`: `packetType`
- bytes `[4..11]`: `packetNumber` (`uint64`, big-endian)
- bytes `[12..]`: payload

## 4.2 Особый handshake-пакет сервера (для этапа поиска)

На этапе discovery в прошивке проверяется особый формат:
- byte `[0]`: `0x03` (`Handshake`)
- bytes `[1..12]`: ASCII строка `"Hey OVR =D 5"`

Если строка не совпала, трекер не примет сервер.

---

## 5. Таймауты и keepalive

- В прошивке задан timeout соединения: `TIMEOUT = 3000ms` (`src/network/connection.cpp`)
- Если сервер не отправляет валидные пакеты (обычно heartbeat) в этот интервал, трекер disconnect/reconnect

Рекомендуемая политика сервера:
- HeartBeat кадр: каждые 1 сек
- Если нет сообщений от трекера 5-10 сек: пометить endpoint как stale
- При новом discovery того же MAC/ID: пере-биндить endpoint

---

## 6. Пакеты от трекера к серверу (`SendPacketType`)

Источник: `src/network/packets.h`

- `0` `HeartBeat`
- `3` `Handshake` (discovery + инфо о прошивке/железе)
- `4` `Accel`
- `12` `BatteryLevel`
- `13` `Tap`
- `14` `Error`
- `15` `SensorInfo`
- `17` `RotationData`
- `18` `MagnetometerAccuracy`
- `19` `SignalStrength`
- `20` `Temperature`
- `22` `FeatureFlags`
- `24` `AcknowledgeConfigChange`
- `26` `FlexData`
- `100` `Bundle` (если сервер сообщил поддержку)
- `105` `Inspection` (если включено в сборке)

### Ключевые payload для обработки в MVP
1. `RotationData(17)` — ориентация сенсора (кватернион)
2. `SensorInfo(15)` — состояние и тип сенсоров
3. `BatteryLevel(12)` — батарея
4. `SignalStrength(19)` — RSSI

---

## 7. Пакеты от сервера к трекеру (`ReceivePacketType`)

Источник: `src/network/packets.h` и обработка в `src/network/connection.cpp`

- `1` `HeartBeat` **(обязательно для стабильного соединения)**
- `3` `Handshake` (на этапе discovery, особый формат)
- `10` `PingPong` (трекер возвращает тот же пакет)
- `15` `SensorInfo` (опционально)
- `22` `FeatureFlags` (опционально)
- `25` `SetConfigFlag` (опционально, remote config)
- `2` `Vibrate`, `4` `Command`, `8` `Config` — в текущей прошивке обработка ограничена/заглушка

---

## 8. Что обязательно реализовать серверу (MVP)

1. UDP listener на `6969`
2. Discovery parser
3. Handshake-response (`0x03 + "Hey OVR =D 5"`)
4. HeartBeat sender (периодический)
5. Parser для `RotationData`, `SensorInfo`, `BatteryLevel`, `SignalStrength`
6. Device/session manager по endpoint + MAC
7. Таймеры и cleanup stale sessions
8. Логи протокола (hex dump на debug уровне)

Без пункта 4 будет стабильный reconnect-loop.

---

## 9. Структуры payload (минимум)

## 9.1 `RotationDataPacket` (type 17)
Поля (см. `src/network/packets.h`):
- `uint8 sensorId`
- `uint8 dataType`
- `float x` (big-endian)
- `float y` (big-endian)
- `float z` (big-endian)
- `float w` (big-endian)
- `uint8 accuracyInfo`

## 9.2 `BatteryLevelPacket` (type 12)
- `float batteryVoltage` (big-endian)
- `float batteryPercentage` (big-endian)

## 9.3 `SignalStrengthPacket` (type 19)
- `uint8 sensorId` (обычно `255`)
- `uint8 signalStrength`

---

## 10. Discovery payload от трекера (handshake body)

Внутри `sendTrackerDiscovery()` прошивка отправляет:
1. `BOARD` (`int32`)
2. legacy `sensorType0` (`int32`)
3. `HARDWARE_MCU` (`int32`)
4. legacy `0` (`int32`)
5. legacy `0` (`int32`)
6. legacy `0` (`int32`)
7. `PROTOCOL_VERSION` (`int32`, сейчас 22)
8. `FIRMWARE_VERSION` (`short string`)
9. MAC (`6 bytes`)
10. `TRACKER_TYPE` (`uint8`)
11. `VENDOR_NAME` (`short string`)
12. `VENDOR_URL` (`short string`)
13. `PRODUCT_NAME` (`short string`)
14. `UPDATE_ADDRESS` (`short string`)
15. `UPDATE_NAME` (`short string`)

`short string` = `1 byte length` + `N bytes ASCII/UTF-8`

---

## 11. Feature Flags

Трекер может запрашивать/получать feature flags сервера (`type 22`).

Серверная структура флагов в прошивке описана в `src/network/featureflags.h`.

Критичный флаг сейчас:
- `PROTOCOL_BUNDLE_SUPPORT` (бит 0)

Если сервер выставляет этот флаг, трекер может присылать `Bundle` (`type 100`) — вложенные пакеты в одном UDP datagram.

Рекомендация для MVP:
- Не объявлять bundle support, пока parser bundle не реализован.

---

## 12. Диагностика и observability

Добавить в сервер обязательные метрики/логи:
1. Кол-во discovery/handshake в минуту
2. Кол-во heartbeats sent/received
3. Packet loss/unknown packet types
4. Время с последнего пакета по каждому tracker endpoint
5. Причины disconnect (timeout, format error, port changed)

Рекомендуемые debug логи:
- endpoint (`ip:port`)
- `packetType`
- `packetNumber`
- payload length
- hex dump первых 16-32 байт

---

## 13. Типовые ошибки и как распознать

1. **Handshake successful -> timeout через ~3с**
   - Причина: сервер не шлет heartbeat
2. **Постоянно Searching for server**
   - Причина: не проходит discovery/handshake, firewall, не та сеть
3. **Handshake приходит, но дальше мусор**
   - Причина: неправильный формат обычных пакетов (header/endianness)
4. **Случайные disconnect при смене Wi-Fi**
   - Причина: endpoint изменился, сервер не обновляет session

---

## 14. Acceptance checklist (готовность сервера)

Считать реализацию рабочей, если:
- [ ] трекер находит сервер < 3 сек
- [ ] нет `Connection to server timed out` при idle 60+ сек
- [ ] корректно принимаются `RotationData` и обновляются значения
- [ ] `BatteryLevel` и `SignalStrength` видны в UI/логах
- [ ] при перезапуске трекера сервер корректно переподключает session
- [ ] firewall/proxy режим не ломает LAN discovery

---

## 15. Рекомендуемая архитектура сервера

Слои:
1. `UdpTransport`
   - receive/send, socket lifecycle
2. `ProtocolCodec`
   - parse/serialize packet headers + payload
3. `TrackerSessionManager`
   - endpoint binding, таймауты, heartbeat scheduler
4. `DomainModel`
   - состояние трекера (pose, battery, sensors)
5. `App/API Layer`
   - UI/REST/WebSocket интеграция

Это позволит тестировать codec/session отдельно от UI.

---

## 16. Минимальный roadmap реализации

1. Listener + handshake-response
2. Heartbeat loop + timeout handling
3. Parser `RotationData` + in-memory state
4. Parser `SensorInfo/Battery/SignalStrength`
5. Логи, метрики, reconnect robustness
6. Опционально: `SetConfigFlag`, `FeatureFlags`, `Bundle`

---

## 17. Важные константы (из текущей прошивки)

- UDP порт по умолчанию: `6969`
- Connection timeout: `3000ms`
- Protocol version: `22`
- Handshake magic: `"Hey OVR =D 5"`

---

## 18. Короткая памятка для AI-агента в серверном проекте

Если цель: "чтобы трекер стабильно подключился", сначала реализуй только это:
1. UDP `:6969`
2. handshake-response с `"Hey OVR =D 5"`
3. heartbeat `type=1` каждые 1 сек
4. parser `type=17` RotationData

Только после этого добавляй расширенные команды и feature flags.

---

## 19. Бинарные примеры (test vectors)

## 19.1 Handshake-ответ сервера (особый формат)

Hex (13 байт):

```text
03 48 65 79 20 4F 56 52 20 3D 44 20 35
```

Где `48 65 79 20 4F 56 52 20 3D 44 20 35` = ASCII `Hey OVR =D 5`.

## 19.2 HeartBeat сервера (обычный формат)

Пример для `packetNumber = 1`:

```text
00 00 00 01 00 00 00 00 00 00 00 01
```

- `00 00 00` — префикс
- `01` — `ReceivePacketType::HeartBeat`
- `00 00 00 00 00 00 00 01` — номер пакета `uint64` big-endian

---

## 20. Требования к надежности сервера

1. Сервер должен принимать, что endpoint трекера (`IP:port`) может меняться после reconnect.
2. Сервер не должен считать один timeout фатальным: трекер может быстро переподключиться.
3. Отправка heartbeat должна идти в отдельном планировщике, не блокироваться тяжелым парсингом.
4. UDP receive loop должен быть неблокирующим или с коротким timeout (например, 10-50ms).
5. Парсер должен быть устойчив к коротким/битым пакетам (без падений процесса).

---

## 21. Минимальный контракт для AI-агента (готовые критерии)

Считаем реализацию корректной, если AI-агент обеспечивает:

- `Bind` UDP сокета на `0.0.0.0:6969`
- Обработку discovery от любого LAN endpoint
- Handshake-ответ `03 + "Hey OVR =D 5"`
- Периодический heartbeat (1 сек)
- Парсинг минимум `RotationData(17)` и `BatteryLevel(12)`
- Таблицу сессий по tracker key (MAC/endpoint)
- Логи уровня `Info` и `Debug` по типам пакетов

---

## 22. План интеграционных тестов

## 22.1 Smoke test
1. Запустить сервер.
2. Включить трекер.
3. Убедиться в логах:
   - discovery получен
   - handshake отправлен
   - heartbeat отправляется раз в 1 сек
4. В логе трекера нет `Connection to server timed out`.

## 22.2 Reconnect test
1. Отключить Wi-Fi на трекере на 5-10 сек.
2. Вернуть Wi-Fi.
3. Проверить, что сервер переиспользует/обновляет session без ручного рестарта.

## 22.3 Format test
1. Подать на вход сервера короткий пакет `< 4 байт`.
2. Убедиться, что сервер логирует ошибку формата, но не падает.

---

## 23. Полезные фильтры для Wireshark

Для диагностики в LAN:

```text
udp.port == 6969
```

Для просмотра только трафика трекера к серверу:

```text
udp.port == 6969 && ip.src == <TRACKER_IP>
```

Для просмотра только трафика сервера к трекеру:

```text
udp.port == 6969 && ip.src == <SERVER_IP>
```

---

## 24. Ограничения и совместимость версий

1. Протокол ориентирован на текущую прошивку (`PROTOCOL_VERSION = 22`).
2. При несовпадении версии сервер должен логировать предупреждение, но не падать.
3. Новые типы пакетов нужно обрабатывать по принципу: `unknown => ignore + debug log`.

---

## 25. Что делать, если нужно «просто чтобы работало сегодня»

Если времени мало, реализовать только:
1. UDP listener `:6969`
2. Handshake-response (`Hey OVR =D 5`)
3. Heartbeat loop 1 сек
4. Парсинг `RotationData(17)`

Это покрывает главный сценарий и убирает reconnect-loop.

---

## 26. Таблица пакетов и минимальные длины UDP

Правило расчета для обычного пакета:
- `minUdpBytes = 12 + payloadBytes`
- где `12` = `3 bytes prefix + 1 byte type + 8 bytes packetNumber`

Исключение:
- handshake-ответ сервера в discovery имеет special-формат и длину `13` байт (`0x03 + "Hey OVR =D 5"`).

| Направление | Type | Имя | Payload bytes | Min UDP bytes | Комментарий |
|---|---:|---|---:|---:|---|
| server -> tracker | 1 | HeartBeat | 0 | 12 | Обязателен для keepalive |
| server -> tracker | 3 | Handshake (special) | 12 (magic string) | 13 | Формат без стандартного 12-byte заголовка |
| tracker -> server | 0 | HeartBeat | 0 | 12 | Ответ на heartbeat сервера |
| tracker -> server | 12 | BatteryLevel | 8 | 20 | `float + float` |
| tracker -> server | 15 | SensorInfo | 17+ | 29+ | Есть расширенные debug-поля в конце |
| tracker -> server | 17 | RotationData | 18 | 30 | Ключевой пакет позы |
| tracker -> server | 19 | SignalStrength | 2 | 14 | Обычно `sensorId=255` |
| tracker -> server | 22 | FeatureFlags | N | 12+N | Variable payload |
| tracker -> server | 26 | FlexData | 5 | 17 | `uint8 + float` |
| tracker -> server | 100 | Bundle | variable | variable | Только при `PROTOCOL_BUNDLE_SUPPORT` |

Размеры структур смотреть в `src/network/packets.h`:
- `BatteryLevelPacket`
- `SensorInfoPacket`
- `RotationDataPacket`
- `SignalStrengthPacket`
- `FlexDataPacket`

---

## 27. C# skeleton сервера (MVP)

Ниже не полная реализация, а каркас, который можно напрямую отдать агенту в серверном проекте.

```csharp
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Collections.Concurrent;

public sealed class TrackerSession
{
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Any, 0);
    public byte[] Mac { get; set; } = new byte[6];
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public ulong OutPacketNumber { get; set; } = 1;
}

public sealed class SlimeUdpServer
{
    private const int Port = 6969;
    private static readonly byte[] HandshakeMagic = "Hey OVR =D 5"u8.ToArray();

    private readonly UdpClient _udp = new(Port);
    private readonly ConcurrentDictionary<string, TrackerSession> _sessions = new();
    private readonly PeriodicTimer _heartbeatTimer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _cts = new();

    public async Task RunAsync()
    {
        var receiveTask = ReceiveLoopAsync(_cts.Token);
        var heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        await Task.WhenAll(receiveTask, heartbeatTask);
    }

    public void Stop() => _cts.Cancel();

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }

            var data = r.Buffer;
            var ep = r.RemoteEndPoint;

            if (TryParseSpecialDiscoveryHandshake(data, out var macKey))
            {
                var s = _sessions.GetOrAdd(macKey, _ => new TrackerSession());
                s.EndPoint = ep;
                s.LastSeenUtc = DateTime.UtcNow;

                await SendSpecialHandshakeAsync(ep, ct);
                continue;
            }

            if (!TryReadPacketType(data, out var packetType))
                continue; // invalid/short packet

            // packet from connected tracker
            var session = FindByEndpoint(ep);
            if (session != null)
                session.LastSeenUtc = DateTime.UtcNow;

            switch (packetType)
            {
                case 17: // RotationData
                    TryParseRotationData(data, out _);
                    break;
                case 12: // BatteryLevel
                    break;
                case 19: // SignalStrength
                    break;
                default:
                    break; // unknown -> ignore + debug log
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (await _heartbeatTimer.WaitForNextTickAsync(ct))
        {
            foreach (var kv in _sessions)
            {
                var s = kv.Value;
                if ((DateTime.UtcNow - s.LastSeenUtc) > TimeSpan.FromSeconds(10))
                    continue; // stale session

                var packet = BuildStandardPacket(packetType: 1, s.OutPacketNumber++, ReadOnlySpan<byte>.Empty);
                await _udp.SendAsync(packet, s.EndPoint, ct);
            }
        }
    }

    private static bool TryParseSpecialDiscoveryHandshake(byte[] data, out string macKey)
    {
        macKey = string.Empty;

        // Упрощенно: discovery у трекера имеет type=3 и payload с маком.
        // Здесь оставьте реальный parser под формат sendTrackerDiscovery().
        if (data.Length < 1 || data[0] != 3)
            return false;

        // TODO: извлечь MAC из payload, пока fallback ключ по endpoint не используем.
        macKey = Guid.NewGuid().ToString("N");
        return true;
    }

    private async Task SendSpecialHandshakeAsync(IPEndPoint ep, CancellationToken ct)
    {
        var p = new byte[13];
        p[0] = 3;
        HandshakeMagic.CopyTo(p, 1);
        await _udp.SendAsync(p, ep, ct);
    }

    private static byte[] BuildStandardPacket(byte packetType, ulong packetNumber, ReadOnlySpan<byte> payload)
    {
        var p = new byte[12 + payload.Length];
        p[0] = 0; p[1] = 0; p[2] = 0;
        p[3] = packetType;
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(4, 8), packetNumber);
        payload.CopyTo(p.AsSpan(12));
        return p;
    }

    private static bool TryReadPacketType(byte[] data, out byte packetType)
    {
        packetType = 0;
        if (data.Length < 4) return false;
        packetType = data[3];
        return true;
    }

    private static bool TryParseRotationData(byte[] data, out object dto)
    {
        dto = new();
        // TODO: data[12..] -> sensorId, dataType, x,y,z,w(float BE), accuracyInfo
        return data.Length >= 30;
    }

    private TrackerSession? FindByEndpoint(IPEndPoint ep)
    {
        foreach (var s in _sessions.Values)
        {
            if (Equals(s.EndPoint, ep)) return s;
        }
        return null;
    }
}
```

### 27.1 Рекомендуемые классы/контракты

1. `UdpTransport`
   - bind, receive/send, cancellation.
2. `PacketCodec`
   - сериализация стандартного пакета, parser packetType/payload.
3. `HandshakeService`
   - parser discovery, отправка special handshake.
4. `TrackerSessionManager`
   - хранит `endpoint`, `MAC`, `lastSeen`, `outPacketNumber`.
5. `HeartbeatScheduler`
   - периодическая отправка `type=1` по активным session.
6. `TelemetryParser`
   - `RotationData`, `BatteryLevel`, `SignalStrength`.

### 27.2 Sequence flow (MVP)

`Discovery -> HandshakeResponse -> SessionBind -> HeartbeatLoop -> RotationDataParse`
