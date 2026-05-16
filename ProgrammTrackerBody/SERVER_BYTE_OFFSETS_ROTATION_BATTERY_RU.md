# Смещения по байтам: `RotationData` и `BatteryLevel` (SlimeVR UDP)

Этот документ — отдельная шпаргалка для разработчика сервера/парсера.

Источник форматов:
- `src/network/connection.cpp`
- `src/network/connection.h`
- `src/network/packets.h`
- `src/batterymonitor.cpp`

---

## 1. Базовый формат обычного UDP-пакета

Для обычных пакетов протокола:

- bytes `[0..2]`: префикс `00 00 00`
- byte `[3]`: `packetType`
- bytes `[4..11]`: `packetNumber` (`uint64`, big-endian)
- bytes `[12..]`: payload

Итого:
- `header = 12 bytes`
- `minUdpBytes = 12 + payloadBytes`

---

## 2. Packet `RotationData` (type `17`)

Структура в прошивке: `RotationDataPacket` (`src/network/packets.h`).

### 2.1 Полный layout (с абсолютными смещениями в UDP)

| Offset | Size | Type | Endian | Поле | Примечание |
|---:|---:|---|---|---|---|
| 0 | 3 | bytes | - | prefix | всегда `00 00 00` |
| 3 | 1 | uint8 | - | packetType | `17` |
| 4 | 8 | uint64 | BE | packetNumber | счетчик пакетов |
| 12 | 1 | uint8 | - | sensorId | ID сенсора |
| 13 | 1 | uint8 | - | dataType | тип данных сенсора |
| 14 | 4 | float32 | BE | x | кватернион X |
| 18 | 4 | float32 | BE | y | кватернион Y |
| 22 | 4 | float32 | BE | z | кватернион Z |
| 26 | 4 | float32 | BE | w | кватернион W |
| 30 | 1 | uint8 | - | accuracyInfo | точность |

Размеры:
- payload `RotationData` = `18 bytes`
- полный UDP пакет = `30 bytes`

### 2.2 Payload-only offsets (от `payloadBase = 12`)

| Payload offset | Size | Type | Endian | Поле |
|---:|---:|---|---|---|
| 0 | 1 | uint8 | - | sensorId |
| 1 | 1 | uint8 | - | dataType |
| 2 | 4 | float32 | BE | x |
| 6 | 4 | float32 | BE | y |
| 10 | 4 | float32 | BE | z |
| 14 | 4 | float32 | BE | w |
| 18 | 1 | uint8 | - | accuracyInfo |

---

## 3. Packet `BatteryLevel` (type `12`)

Структура в прошивке: `BatteryLevelPacket` (`src/network/packets.h`).

### 3.1 Полный layout (с абсолютными смещениями в UDP)

| Offset | Size | Type | Endian | Поле | Примечание |
|---:|---:|---|---|---|---|
| 0 | 3 | bytes | - | prefix | всегда `00 00 00` |
| 3 | 1 | uint8 | - | packetType | `12` |
| 4 | 8 | uint64 | BE | packetNumber | счетчик пакетов |
| 12 | 4 | float32 | BE | batteryVoltage | напряжение, В |
| 16 | 4 | float32 | BE | batteryPercentage | уровень в диапазоне `0.0..1.0` |

Размеры:
- payload `BatteryLevel` = `8 bytes`
- полный UDP пакет = `20 bytes`

### 3.2 Payload-only offsets (от `payloadBase = 12`)

| Payload offset | Size | Type | Endian | Поле |
|---:|---:|---|---|---|
| 0 | 4 | float32 | BE | batteryVoltage |
| 4 | 4 | float32 | BE | batteryPercentage |

---

## 4. Важное про `batteryPercentage`

В прошивке `level` ограничивается в `0..1` и отправляется как есть (`sendBatteryLevel(voltage, level)`), см. `src/batterymonitor.cpp`.

На сервере:
- raw: `batteryPercentage` (`0.0..1.0`)
- для UI удобно: `batteryPercent = batteryPercentage * 100.0f`

---

## 5. Минимальные проверки валидности пакета

Для надежного парсинга:

1. `data.Length >= 12` (иначе даже заголовок невалиден)
2. `data[0..2] == 00 00 00` для обычного пакета
3. `packetType = data[3]`
4. Для `RotationData(17)`: `data.Length >= 31`
5. Для `BatteryLevel(12)`: `data.Length >= 20`

---

## 6. C# helper-функции (Big Endian)

```csharp
using System.Buffers.Binary;

static ulong ReadBEUInt64(byte[] data, int offset)
{
    return BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
}

static float ReadBEFloat(byte[] data, int offset)
{
    uint raw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    return BitConverter.Int32BitsToSingle((int)raw);
}
```

---

## 7. C# пример чтения `RotationData` и `BatteryLevel`

```csharp
static bool TryParseRotationData(byte[] data, out byte sensorId, out byte dataType, out float x, out float y, out float z, out float w, out byte accuracy)
{
    sensorId = dataType = accuracy = 0;
    x = y = z = w = 0;

    if (data.Length < 31) return false;
    if (data[0] != 0 || data[1] != 0 || data[2] != 0) return false;
    if (data[3] != 17) return false;

    int o = 12;
    sensorId = data[o++];
    dataType = data[o++];
    x = ReadBEFloat(data, o); o += 4;
    y = ReadBEFloat(data, o); o += 4;
    z = ReadBEFloat(data, o); o += 4;
    w = ReadBEFloat(data, o); o += 4;
    accuracy = data[o];

    return true;
}

static bool TryParseBatteryLevel(byte[] data, out float voltage, out float level01)
{
    voltage = level01 = 0;

    if (data.Length < 20) return false;
    if (data[0] != 0 || data[1] != 0 || data[2] != 0) return false;
    if (data[3] != 12) return false;

    voltage = ReadBEFloat(data, 12);
    level01 = ReadBEFloat(data, 16);
    return true;
}
```

---

## 8. Hex-примеры для теста парсера

### 8.1 `BatteryLevel` (packetType=12)

Пример:
- packetNumber = `1`
- voltage = `4.00f` (`0x40800000`)
- level = `0.75f` (`0x3F400000`)

```text
00 00 00 0C 00 00 00 00 00 00 00 01 40 80 00 00 3F 40 00 00
```

### 8.2 `RotationData` (packetType=17)

Пример:
- packetNumber = `2`
- sensorId = `0`
- dataType = `1`
- x=0, y=0, z=0, w=1.0 (`0x3F800000`)
- accuracy = `3`

```text
00 00 00 11 00 00 00 00 00 00 00 02 00 01 00 00 00 00 00 00 00 00 00 00 00 00 3F 80 00 00 03
```

---

## 9. Частые ошибки

1. Читать `packetType` из `data[0]` вместо `data[3]`.
2. Читать `float` как little-endian.
3. Начинать payload с `offset=4`, а не `offset=12`.
4. Не проверять минимальную длину и падать на коротких пакетах.
5. Путать `batteryPercentage` (`0..1`) с уже умноженным `%`.

