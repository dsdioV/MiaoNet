using System.Buffers.Binary;
using System.Text;

namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>
/// Reads just enough of a DataType payload to tell whether it carries a bound reference
/// (<c>MetaBoundRef</c>, meta id <c>"bound"</c>).
/// </summary>
/// <remarks>
/// CelesteNet's server stores every data blob that declares a bound reference and replays the stored
/// blobs of all players to each new session (<c>CelesteNet.Server/CelesteNetPlayerSession.cs</c>,
/// <c>ResendPlayerStates</c>). The bridge mirrors that rule, so it needs the same predicate the
/// server uses. The payload is never deserialized: only the
/// <c>u16 flags | netstring typeID | netstring source | length | u8 metaCount | metas</c> prefix is
/// walked.
/// </remarks>
public static class CelesteNetFrameMeta {

    /// <summary>DataFlags.Slim: the type id is a mapping varint, never a third-party type.</summary>
    private const ushort FlagSlim = 0x8000;

    /// <summary>DataFlags.Small: the body length is one byte instead of two.</summary>
    private const ushort FlagSmall = 0x2000;

    /// <summary>
    /// Meta id of <c>MetaBoundRef</c> (<c>CelesteNet.Shared/MetaTypes/MetaBoundRef.cs</c>). Note that
    /// <c>MetaRef</c>, the identity meta of <c>DataPlayerInfo</c>, uses the id <c>"ref"</c> and is not a
    /// bound reference: the server only stores and replays data that declares <c>"bound"</c>.
    /// </summary>
    public const string BoundRefMetaId = "bound";

    /// <summary>Netstring control byte for a string taken from the dedupe map.</summary>
    private const byte NetStringMapped = 0x01;

    /// <summary>Highest netstring control byte (control strings 0x01-0x1f are single characters).</summary>
    private const byte NetStringControlLast = 0x1f;

    /// <summary>
    /// Whether <paramref name="payload"/> declares a bound reference. Returns false for anything this
    /// minimal reader cannot walk, which only costs a replay, never correctness.
    /// </summary>
    public static bool CarriesBoundRef(ReadOnlySpan<byte> payload) {
        if (payload.Length < sizeof(ushort))
            return false;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if ((flags & FlagSlim) != 0)
            return false;

        int offset = sizeof(ushort);
        if (!TrySkipNetString(payload, ref offset))   // type id
            return false;
        if (!TrySkipNetString(payload, ref offset))   // source
            return false;

        bool small = (flags & FlagSmall) != 0;
        if (offset + (small ? 1 : sizeof(ushort)) > payload.Length)
            return false;

        int bodyLength = small ? payload[offset] : BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += small ? 1 : sizeof(ushort);

        // The client itself requires the length field to match exactly, so a truncated or padded frame
        // is not a frame at all and must never be cached for replay.
        if (payload.Length - offset != bodyLength || bodyLength < 1)
            return false;

        int metaCount = payload[offset++];
        bool carriesBoundRef = false;
        for (int i = 0; i < metaCount; i++) {
            if (!TryReadMetaId(payload, ref offset, out bool isBoundRef))
                return false;
            carriesBoundRef |= isBoundRef;

            if (offset >= payload.Length)
                return false;
            int metaLength = payload[offset++];
            if (offset + metaLength > payload.Length)
                return false;
            offset += metaLength;
        }

        return carriesBoundRef;
    }

    /// <summary>
    /// Reads one meta id and advances past it. <paramref name="isBoundRef"/> is only true when the id
    /// could be read literally; a dedupe-map reference cannot be matched and is treated as "not a ref".
    /// </summary>
    private static bool TryReadMetaId(ReadOnlySpan<byte> payload, ref int offset, out bool isBoundRef) {
        isBoundRef = false;
        if (offset >= payload.Length)
            return false;

        byte first = payload[offset];
        if (first == 0) {
            offset++;
            return true;
        }

        if (first == NetStringMapped) {
            offset++;
            return TrySkip7BitEncodedInt(payload, ref offset);
        }

        if (first <= NetStringControlLast) {
            offset++;
            return true;
        }

        int terminator = payload[offset..].IndexOf((byte)0);
        if (terminator <= 0)
            return false;

        isBoundRef = payload.Slice(offset, terminator).SequenceEqual(Encoding.ASCII.GetBytes(BoundRefMetaId));
        offset += terminator + 1;
        return true;
    }

    /// <summary>Advances past a netstring (control short form, dedupe reference or literals).</summary>
    private static bool TrySkipNetString(ReadOnlySpan<byte> payload, ref int offset) {
        if (offset >= payload.Length)
            return false;

        byte first = payload[offset];
        if (first == 0) {
            offset++;
            return true;
        }

        if (first == NetStringMapped) {
            offset++;
            return TrySkip7BitEncodedInt(payload, ref offset);
        }

        if (first <= NetStringControlLast) {
            offset++;
            return true;
        }

        int terminator = payload[offset..].IndexOf((byte)0);
        if (terminator <= 0)
            return false;

        offset += terminator + 1;
        return true;
    }

    /// <summary>Skips a 7-bit encoded int, the form used for dedupe-map indices.</summary>
    private static bool TrySkip7BitEncodedInt(ReadOnlySpan<byte> payload, ref int offset) {
        while (true) {
            if (offset >= payload.Length)
                return false;
            byte value = payload[offset++];
            if ((value & 0x80) == 0)
                return true;
        }
    }
}
