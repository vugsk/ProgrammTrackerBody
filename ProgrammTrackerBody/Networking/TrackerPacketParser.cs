using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProgrammTrackerBody.Networking;

public static class TrackerPacketParser
{
    private static readonly byte[] DiscoverySignature = Encoding.ASCII.GetBytes("Hey OVR =D 5");

    public static IReadOnlyList<ParsedPacket> ParseDatagram(byte[] datagram, DateTime timestampUtc)
    {
        var packets = new List<ParsedPacket>();
        if (datagram.Length == 0)
        {
            return packets;
        }

        if (IsSpecialHandshakePacket(datagram))
        {
            packets.Add(new ParsedPacket(
                (byte)ReceivePacketType.Handshake,
                null,
                datagram,
                false,
                false,
                timestampUtc));
            return packets;
        }

        if (datagram.Length < 12 || datagram[0] != 0 || datagram[1] != 0 || datagram[2] != 0)
        {
            return packets;
        }

        var packetType = datagram[3];
        if (packetType == (byte)SendPacketType.Bundle)
        {
            ParseBundle(datagram, timestampUtc, packets);
            return packets;
        }

        var packetNumber = BinaryPrimitives.ReadUInt64BigEndian(datagram.AsSpan(4, 8));
        var payload = datagram[12..];

        DiscoveryHandshakeInfo? discoveryInfo = null;
        var isDiscoveryHandshake = false;
        if (packetType == (byte)SendPacketType.Handshake)
        {
            isDiscoveryHandshake = true;
            TryParseTrackerDiscoveryPayload(payload, out discoveryInfo);
        }

        packets.Add(new ParsedPacket(packetType, packetNumber, payload, isDiscoveryHandshake, false, timestampUtc, discoveryInfo));
        return packets;
    }

    private static void ParseBundle(byte[] datagram, DateTime timestampUtc, List<ParsedPacket> packets)
    {
        var outerPacketNumber = BinaryPrimitives.ReadUInt64BigEndian(datagram.AsSpan(4, 8));
        var position = 12;

        while (position + 2 <= datagram.Length)
        {
            var innerSize = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(position, 2));
            position += 2;

            if (innerSize < 12 || position + innerSize > datagram.Length)
            {
                break;
            }

            var innerPacketBytes = datagram.AsSpan(position, innerSize).ToArray();
            position += innerSize;

            if (innerPacketBytes[0] != 0 || innerPacketBytes[1] != 0 || innerPacketBytes[2] != 0)
            {
                continue;
            }

            var innerType = innerPacketBytes[3];
            var innerPayload = innerPacketBytes[12..];
            packets.Add(new ParsedPacket(innerType, outerPacketNumber, innerPayload, false, true, timestampUtc));
        }
    }

    private static bool TryParseTrackerDiscoveryPayload(byte[] payload, out DiscoveryHandshakeInfo? info)
    {
        info = null;

        // Discovery payload starts with 7 int32 fields (board/sensors/mcu/protocolVersion).
        if (payload.Length < 28)
        {
            return false;
        }

        var protocolVersion = ReadInt32Network(payload, 24);
        var offset = 28;

        if (!TryReadShortString(payload, ref offset, out var firmwareVersion))
        {
            return false;
        }

        if (offset + 7 > payload.Length)
        {
            return false;
        }

        var mac = payload.AsSpan(offset, 6).ToArray();
        offset += 6;
        var trackerType = payload[offset++];

        TryReadShortString(payload, ref offset, out var vendorName);
        TryReadShortString(payload, ref offset, out var vendorUrl);
        TryReadShortString(payload, ref offset, out var productName);
        TryReadShortString(payload, ref offset, out var updateAddress);
        TryReadShortString(payload, ref offset, out var updateName);

        _ = vendorUrl;
        _ = updateAddress;
        _ = updateName;

        var macKey = Convert.ToHexString(mac).ToLowerInvariant();
        info = new DiscoveryHandshakeInfo(macKey, mac, protocolVersion, firmwareVersion, trackerType, vendorName, productName);
        return true;
    }

    private static bool TryReadShortString(byte[] payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset >= payload.Length)
        {
            return false;
        }

        var length = payload[offset++];
        if (offset + length > payload.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return true;
    }

    private static int ReadInt32Network(byte[] payload, int offset)
    {
        var bigEndian = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        if (bigEndian > 0 && bigEndian < 10_000)
        {
            return bigEndian;
        }

        var littleEndian = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        if (littleEndian > 0 && littleEndian < 10_000)
        {
            return littleEndian;
        }

        return bigEndian;
    }

    private static bool IsSpecialHandshakePacket(byte[] datagram)
    {
        if (datagram.Length != 13 || datagram[0] != (byte)ReceivePacketType.Handshake)
        {
            return false;
        }

        return datagram.AsSpan(1, 12).SequenceEqual(DiscoverySignature);
    }
}
