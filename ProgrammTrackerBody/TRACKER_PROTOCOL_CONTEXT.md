# SlimeVR Tracker ESP — протокол обмена (контекст для AI/клиента)

Этот документ описывает, какие данные прошивка трекера отправляет и принимает по сети, и как это использовать при разработке программы связи/обработки.

Источник правды в коде прошивки:
- `src/network/packets.h`
- `src/network/connection.h`
- `src/network/connection.cpp`
- `src/network/featureflags.h`
- `src/sensors/sensor.cpp`
- `src/sensors/SensorManager.cpp`
- `src/sensors/softfusion/softfusionsensor.h`
- `src/sensors/bno080sensor.cpp`

## 1) Транспорт и discovery

- Транспорт: UDP (Wi-Fi).
- Порт по умолчанию: `6969` (`Connection::m_ServerPort`).
- Пока сервер не найден, трекер отправляет discovery/handshake broadсast (`255.255.255.255`).
- После успешного ответа фиксирует `server IP + server port` и работает с ними.
- Таймаут соединения: 3 секунды (`TIMEOUT = 3000`), после чего reconnect.

### Особенность handshake-входа

В режиме поиска сервера входящий handshake читается по **первому байту**:
- `m_Packet[0] == ReceivePacketType::Handshake`
- проверяется сигнатура: `"Hey OVR =D 5"`

Это отдельный legacy-формат discovery, отличающийся от обычного формата кадров.

## 2) Общий формат исходящего пакета

Обычный исходящий кадр:

```text
[0x00][0x00][0x00][packet_type:1]
[packet_number:8, big-endian]
[payload:N]
```

Формируется через:
- `sendPacketType()` — пишет `00 00 00 type`
- `sendPacketNumber()` — 8 байт, big-endian
- `sendBytes(payload)`

### Endianness

В payload структуры используют `BigEndian<T>` из `src/network/packets.h`, т.е. `float`, `uint16`, `uint32`, `uint64` передаются в big-endian.

## 3) Типы пакетов

### Исходящие (`SendPacketType`)

```cpp
enum class SendPacketType : uint8_t {
    HeartBeat = 0,
    Handshake = 3,
    Accel = 4,
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
};
```

### Входящие (`ReceivePacketType`)

```cpp
enum class ReceivePacketType : uint8_t {
    HeartBeat = 1,
    Vibrate = 2,
    Handshake = 3,
    Command = 4,
    Config = 8,
    PingPong = 10,
    SensorInfo = 15,
    FeatureFlags = 22,
    SetConfigFlag = 25,
};
```

## 4) Ключевые структуры payload (из `packets.h`)

```cpp
struct RotationDataPacket {
    uint8_t sensorId;
    uint8_t dataType;
    BigEndian<float> x;
    BigEndian<float> y;
    BigEndian<float> z;
    BigEndian<float> w;
    uint8_t accuracyInfo;
};
```

```cpp
struct AccelPacket {
    BigEndian<float> x;
    BigEndian<float> y;
    BigEndian<float> z;
    uint8_t sensorId;
};
```

```cpp
struct SensorInfoPacket {
    uint8_t sensorId;
    SensorStatus sensorState;
    SensorTypeID sensorType;
    BigEndian<SensorConfigBits> sensorConfigData;
    bool hasCompletedRestCalibration;
    SensorPosition sensorPosition;
    SensorDataType sensorDataType;
    BigEndian<float> tpsCounterAveragedTps;
    BigEndian<float> dataCounterAveragedTps;
};
```

```cpp
struct SetConfigFlagPacket {
    uint8_t sensorId;
    BigEndian<SensorToggles> flag;
    bool newState;
};
```

## 5) Что именно трекер отправляет и зачем

### 5.1 Body tracking данные

1. `RotationData (17)`
- Основной поток трекинга (ориентация сегмента тела).
- Поля: `sensorId`, `dataType`, quaternion `x,y,z,w`, `accuracyInfo`.
- Отправляется из `Sensor::sendData()` (`src/sensors/sensor.cpp`) через `Connection::sendRotationData()`.

2. `Accel (4)`
- Линейное ускорение (`x,y,z`) для сенсора.
- Отправляется условно, если `SEND_ACCELERATION == true` (`src/debug.h`).

3. `SensorInfo (15)`
- Метаданные сенсора: состояние, тип IMU, позиция на теле, флаги конфигурации.
- Нужен для синхронизации состояния трекера и привязки к body part.
- Переотправляется при изменении состояния/флагов.

### 5.2 Сервис/диагностика

- `Handshake (3)` — объявление трекера (board/mcu/protocol/firmware/mac/vendor/product/trackerType).
- `HeartBeat (0)` — keepalive.
- `BatteryLevel (12)` — напряжение и процент батареи.
- `SignalStrength (19)` — RSSI Wi-Fi.
- `Temperature (20)` — температура IMU (обычно до ~2 Гц).
- `Error (14)` — коды ошибок сенсора/reset/watchdog timeout.
- `Tap (13)` — tap события (например, BNO08x).
- `FlexData (26)` — данные flex-датчиков (например, перчатки).
- `FeatureFlags (22)` — флаги возможностей прошивки.
- `AcknowledgeConfigChange (24)` — подтверждение применения флага.

### 5.3 Debug-only

`Inspection (105)` отправляется только при `ENABLE_INSPECTION == true`.

## 6) Что трекер принимает и как реагирует

Обрабатывается в `Connection::update()` (`src/network/connection.cpp`):

1. `HeartBeat (1)`
- Ответ: прошивка отправляет `SendPacketType::HeartBeat`.

2. `PingPong (10)`
- Ответ: возвращает полученный пакет (`returnLastPacket(len)`).

3. `SensorInfo (15)`
- Используется как ack состояния сенсора на стороне сервера.

4. `FeatureFlags (22)`
- Парсит server feature flags (`ServerFeatures::from(...)`).
- Влияет, например, на поддержку bundling.

5. `SetConfigFlag (25)`
- Применяет флаги сенсоров (`SensorToggles`) для одного сенсора или всех (`sensorId == 255`).
- Сохраняет конфиг и отправляет `AcknowledgeConfigChange (24)`.

6. `Vibrate`, `Command`, `Config`
- Сейчас присутствуют в enum, но в данном коде фактически не реализованы (заглушки).

## 7) Bundling (тип 100)

Если сервер поддерживает `PROTOCOL_BUNDLE_SUPPORT`, прошивка может отправлять несколько внутренних пакетов в одном UDP datagram.

Формат outer-packet `Bundle`:

```text
[00 00 00 100]
[outer_packet_number:8]
repeat {
  [inner_size:2, big-endian]
  [inner_packet_bytes:inner_size]
}
```

Важно:
- Внутренний пакет в bundle содержит `type + payload`.
- Внутренний `packetNumber` обычно не пишется (в bundle он подавлен логикой `sendPacketNumber()`).
- Парсер клиента должен уметь разбирать и обычные пакеты, и bundle.

## 8) Рекомендуемая state machine клиента

1. `DISCOVERY`
- Слушать UDP, принять handshake-сигнатуру `Hey OVR =D 5`.
- Запомнить адрес трекера.

2. `SESSION_START`
- Начать обмен heartbeat/ping.
- Принять `SensorInfo` и построить карту `sensorId -> bodyPart`.

3. `STREAMING`
- Принимать `RotationData` (обязательно), `Accel` (опционально), остальные telem/diag.
- Поддерживать timeout сессии.

4. `RECONNECT`
- При timeout/ошибке вернуться в discovery.

## 9) Чеклист совместимости для реализации

- [ ] Поддержан big-endian для всех числовых полей payload.
- [ ] Учтен обычный формат packet header `00 00 00 type`.
- [ ] Учтен special-case handshake discovery (`type` в `byte[0]`, сигнатура `Hey OVR =D 5`).
- [ ] Поддержан разбор `Bundle (100)`.
- [ ] Реализована обработка `RotationData`, `SensorInfo`, `SetConfigFlag`, `FeatureFlags`, `HeartBeat`, `PingPong`.
- [ ] Реализован timeout/reconnect.

## 10) Минимальные поля, без которых FBT не взлетит

Критический минимум для рабочего трекинга:
- вход: `RotationData` + `SensorInfo`
- служебно: `Handshake`, `HeartBeat`
- желательно: `SetConfigFlag` (чтобы remote-менять поведение сенсоров)

---

## Appendix A: пример разбора обычного кадра (псевдокод)

```python
def parse_udp_datagram(data: bytes):
    if len(data) < 4:
        return None

    # обычный путь
    if data[0] == 0 and data[1] == 0 and data[2] == 0:
        packet_type = data[3]

        # bundle
        if packet_type == 100:
            outer_pn = be_u64(data[4:12])
            pos = 12
            packets = []
            while pos + 2 <= len(data):
                inner_size = be_u16(data[pos:pos+2]); pos += 2
                if pos + inner_size > len(data):
                    break
                inner = data[pos:pos+inner_size]; pos += inner_size
                # inner начинается с 00 00 00 type
                packets.append(parse_inner(inner))
            return {"type": "bundle", "pn": outer_pn, "inner": packets}

        # normal packet
        pn = be_u64(data[4:12]) if len(data) >= 12 else None
        payload = data[12:] if len(data) >= 12 else b""
        return {"type": packet_type, "pn": pn, "payload": payload}

    # discovery handshake legacy путь
    if data[0] == 3 and b"Hey OVR =D 5" in data[1:13]:
        return {"type": "discovery_handshake"}

    return None
```

## Appendix B: текущие compile-time флаги в этой копии проекта

Из `src/debug.h`:
- `PROTOCOL_VERSION = 22`
- `SEND_ACCELERATION = true`
- `ENABLE_INSPECTION = false`
- `PACKET_BUNDLING = PACKET_BUNDLING_BUFFERED`

Если эти флаги изменятся, фактическое поведение потока может отличаться.

