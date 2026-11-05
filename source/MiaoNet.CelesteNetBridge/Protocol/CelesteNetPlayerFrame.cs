using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>
/// Byte-exact codec for CelesteNet's <c>playerFrame</c> DataType.
/// </summary>
/// <remarks>
/// <para>
/// <c>playerFrame</c> is one of the two <c>NoStandardMeta</c> core frames: its body starts with the
/// player reference, not with a meta count. <see cref="CelesteNetDataTypes.Encode"/> always writes
/// that count, so it cannot be used here; this type writes the whole payload itself.
/// </para>
/// <para>
/// The layout mirrors <c>CelesteNet.Shared/DataTypes/DataPlayerFrame.cs</c> (CelesteNet 2.4.2), whose
/// <c>ReadAll</c>/<c>WriteAll</c> override the meta-carrying base implementation:
/// </para>
/// <code>
/// u16 flags (Unreliable | CoreType | NoStandardMeta)
/// netstring "playerFrame"
/// netstring "CelesteNet.Shared"
/// u16 body length
///
/// body:
///   7bit uint player id
///   u8 flags (1 FacingLeft, 2 HairSimulateMotion, 4 Dashing, 8 DashB, 16 Holding, 32 Dead)
///   7bit int X, 7bit int Y
///   sbyte ScaleX * 16, sbyte ScaleY * 16
///   u16 packed color (R&gt;&gt;3 | G&gt;&gt;3&lt;&lt;5 | B&gt;&gt;3&lt;&lt;10)
///   i16 SpeedX, i16 SpeedY
///   7bit int animation id, 7bit int animation frame
///   u8 hair color count + colors (5-5-5, high-bit repeat run)
///   netstring hair texture 0
///   u8 follower count + followers
///   optional holding (7bit dx, dy, then the follower layout without string repeats)
///   optional u8 dash direction
/// </code>
/// <para>
/// Strings are always written as literal netstrings: a mapped string is only legal after a
/// <c>stringMap</c>, which the bridge's loopback server never sends.
/// </para>
/// </remarks>
public sealed class CelesteNetPlayerFrame
{
    /// <summary>DataType ID on the wire.</summary>
    public const string TypeId = "playerFrame";

    /// <summary>Declaring assembly CelesteNet expects in the source netstring.</summary>
    internal const string Source = "CelesteNet.Shared";

    /// <summary><c>DataFlags.Unreliable | CoreType | NoStandardMeta</c>.</summary>
    public const ushort Flags =
        CelesteNetDataTypes.FlagUnreliable | CelesteNetDataTypes.FlagCoreType | CelesteNetDataTypes.FlagNoStandardMeta;

    /// <summary>The MiaoNet player id the frame belongs to; the client resolves its playerInfo ref by it.</summary>
    public uint PlayerID { get; set; }

    public bool FacingLeft { get; set; }

    public bool HairSimulateMotion { get; set; }

    /// <summary>Whether a dash is running. Only then are <see cref="DashWasB"/> and <see cref="DashDirection"/> read.</summary>
    public bool Dashing { get; set; }

    /// <summary>Whether the running dash is a B dash (particle choice only).</summary>
    public bool DashWasB { get; set; }

    public bool Dead { get; set; }

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public float ScaleX { get; set; } = 1f;

    public float ScaleY { get; set; } = 1f;

    /// <summary>Sprite tint. CelesteNet keeps only five bits per channel.</summary>
    public CelesteNetColor Color { get; set; } = CelesteNetColor.White;

    public float SpeedX { get; set; }

    public float SpeedY { get; set; }

    /// <summary>Index into the player's <c>playerGraphics.SpriteAnimations</c>; negative means "keep the current animation".</summary>
    public int AnimationID { get; set; } = -1;

    public int AnimationFrame { get; set; }

    /// <summary>One color per hair segment. On the wire consecutive equal colors are run-length compressed.</summary>
    public CelesteNetColor[] HairColors { get; set; } = [];

    /// <summary>Texture of the first hair segment, usually the bangs.</summary>
    public string HairTexture0 { get; set; } = "";

    public CelesteNetPlayerFrameEntity[] Followers { get; set; } = [];

    public CelesteNetPlayerFrameEntity? Holding { get; set; }

    /// <summary>Raw wire angle byte, only meaningful while <see cref="Dashing"/>.</summary>
    public byte DashDirection { get; set; }

    /// <summary>Encodes one complete DataType payload, header included.</summary>
    public static byte[] Encode(CelesteNetPlayerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        byte[] body = EncodeBody(frame);
        if (body.Length > ushort.MaxValue)
        {
            // The wire length field is 16 bits; truncating here would make the real client see a
            // length mismatch and drop the connection.
            throw new InvalidOperationException(
                "playerFrame body is " + body.Length.ToString(CultureInfo.InvariantCulture)
                + " bytes; the wire length field holds at most " + ushort.MaxValue.ToString(CultureInfo.InvariantCulture)
            );
        }

        ArrayBufferWriter<byte> payload = new(body.Length + 64);
        CelesteNetWireWriter writer = new(payload);

        writer.WriteUInt16(Flags);
        writer.WriteNetString(TypeId);
        writer.WriteNetString(Source);
        writer.WriteUInt16((ushort)body.Length);
        payload.Write(body);

        return payload.WrittenSpan.ToArray();
    }

    private static byte[] EncodeBody(CelesteNetPlayerFrame frame)
    {
        ArrayBufferWriter<byte> body = new(96);
        CelesteNetWireWriter writer = new(body);

        writer.Write7BitUInt(frame.PlayerID);
        writer.WriteByte(EncodeFlags(frame));

        writer.Write7BitInt((int)frame.PositionX);
        writer.Write7BitInt((int)frame.PositionY);
        writer.WriteSByte(RoundScale(frame.ScaleX));
        writer.WriteSByte(RoundScale(frame.ScaleY));
        writer.WriteUInt16(frame.Color.Pack());
        writer.WriteInt16(RoundShort(frame.SpeedX));
        writer.WriteInt16(RoundShort(frame.SpeedY));

        writer.Write7BitInt(frame.AnimationID);
        writer.Write7BitInt(frame.AnimationFrame);

        CelesteNetColor[] hairColors = frame.HairColors ?? [];
        writer.WriteByte((byte)Math.Min(hairColors.Length, byte.MaxValue));
        int count = Math.Min(hairColors.Length, byte.MaxValue);
        for (int i = 0; i < count;)
        {
            int runStart = i;
            ushort packed = hairColors[i].Pack();
            writer.WriteByte((byte)(packed >> 8));
            writer.WriteByte((byte)packed);
            for (i++; i < count && hairColors[i] == hairColors[runStart]; i++)
            {
            }

            if (i - runStart > 1)
            {
                // The high bit marks a run; the low bits are the repeat count minus one.
                writer.WriteByte((byte)(0x80 | (i - runStart - 1)));
            }
        }

        writer.WriteNetString(frame.HairTexture0 ?? "");

        CelesteNetPlayerFrameEntity[] followers = frame.Followers ?? [];
        int followerCount = Math.Min(followers.Length, byte.MaxValue);
        writer.WriteByte((byte)followerCount);
        for (int i = 0; i < followerCount; i++)
        {
            CelesteNetPlayerFrameEntity follower = followers[i];
            WriteEntity(writer, follower, holding: false);
            if (i >= 1 && follower.SpriteID == followers[i - 1].SpriteID
                && follower.AnimationID == followers[i - 1].AnimationID)
            {
                // Written as a raw control byte: the reader reuses the previous sprite and animation.
                writer.WriteByte(0x02);
            }
            else
            {
                writer.WriteNetString(follower.SpriteID ?? "");
                writer.WriteNetString(follower.AnimationID ?? "");
            }

            writer.Write7BitInt(follower.AnimationFrame);
        }

        if (frame.Holding is { } holding)
        {
            WriteEntity(writer, holding, holding: true);
            writer.WriteNetString(holding.SpriteID ?? "");
            writer.WriteNetString(holding.AnimationID ?? "");
            writer.Write7BitInt(holding.AnimationFrame);
        }

        if (frame.Dashing)
        {
            writer.WriteByte(frame.DashDirection);
        }

        return body.WrittenSpan.ToArray();
    }

    /// <summary>Scale and speed are the only fields the encoder clamps; the client clamps them again.</summary>
    private static sbyte RoundScale(float scale)
        => (sbyte)Math.Clamp((int)(scale * 16f), sbyte.MinValue, sbyte.MaxValue);

    private static short RoundShort(float value)
        => (short)Math.Clamp((int)value, short.MinValue, short.MaxValue);

    private static byte EncodeFlags(CelesteNetPlayerFrame frame)
    {
        byte flags = 0;
        if (frame.FacingLeft)
        {
            flags |= 0x01;
        }

        if (frame.HairSimulateMotion)
        {
            flags |= 0x02;
        }

        if (frame.Dashing)
        {
            flags |= 0x04;
        }

        // CelesteNet writes the dash-kind bit whenever it has a dash kind, but its reader only trusts
        // it while the dashing bit is set as well.
        if (frame.DashWasB)
        {
            flags |= 0x08;
        }

        if (frame.Holding is not null)
        {
            flags |= 0x10;
        }

        if (frame.Dead)
        {
            flags |= 0x20;
        }

        return flags;
    }

    private static void WriteEntity(CelesteNetWireWriter writer, CelesteNetPlayerFrameEntity entity, bool holding)
    {
        if (holding)
        {
            writer.Write7BitInt((int)entity.OffsetX);
            writer.Write7BitInt((int)entity.OffsetY);
        }

        writer.WriteSByte(RoundScale(entity.ScaleX));
        writer.WriteSByte(RoundScale(entity.ScaleY));
        writer.WriteByte(entity.Color.R);
        writer.WriteByte(entity.Color.G);
        writer.WriteByte(entity.Color.B);
        writer.WriteByte(entity.Color.A);
        writer.Write7BitInt(entity.Depth);
        writer.WriteSingle(entity.SpriteRate);
        if (entity.HasJustify)
        {
            writer.WriteBoolean(true);
            writer.WriteSingle(entity.JustifyX);
            writer.WriteSingle(entity.JustifyY);
        }
        else
        {
            writer.WriteBoolean(false);
        }
    }

    /// <summary>
    /// Decodes a complete DataType payload (header included).
    /// </summary>
    /// <returns>
    /// False when the payload is not a well-formed <c>playerFrame</c>. The bridge keeps relaying the
    /// raw frame in that case, so a decode surprise degrades instead of breaking the session.
    /// </returns>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out CelesteNetPlayerFrame? frame
    )
    {
        frame = null;
        try
        {
            frame = DecodeBody(CelesteNetPayload.SplitBody(payload, TypeId));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException)
        {
            // Malformed strings must degrade to "do not handle this frame", not escape the handler.
            return false;
        }
    }

    /// <summary>Decodes a bare body, for tests and for callers that already split the payload.</summary>
    public static CelesteNetPlayerFrame DecodeBody(ReadOnlySpan<byte> body)
    {
        CelesteNetWireReader reader = new(body);

        CelesteNetPlayerFrame frame = new() { PlayerID = reader.Read7BitUInt() };
        byte flags = reader.ReadByte();

        frame.FacingLeft = (flags & 0x01) != 0;
        frame.HairSimulateMotion = (flags & 0x02) != 0;
        frame.Dashing = (flags & 0x04) != 0;
        frame.DashWasB = (flags & 0x08) != 0;
        frame.Dead = (flags & 0x20) != 0;

        frame.PositionX = reader.Read7BitInt();
        frame.PositionY = reader.Read7BitInt();
        frame.ScaleX = reader.ReadSByte() / 16f;
        frame.ScaleY = reader.ReadSByte() / 16f;
        frame.Color = CelesteNetColor.Unpack(reader.ReadUInt16());
        frame.SpeedX = reader.ReadInt16();
        frame.SpeedY = reader.ReadInt16();

        frame.AnimationID = reader.Read7BitInt();
        frame.AnimationFrame = reader.Read7BitInt();

        CelesteNetColor[] hairColors = new CelesteNetColor[reader.ReadByte()];
        CelesteNetColor lastColor = CelesteNetColor.White;
        for (int i = 0; i < hairColors.Length;)
        {
            byte first = reader.ReadByte();
            if ((first & 0x80) != 0)
            {
                // A run of the previous color; the frame that produced it never has more segments
                // than the count said, so a too-long run simply stops at the end.
                int repeat = first & 0x7f;
                for (int j = 0; i < hairColors.Length && j < repeat; j++)
                {
                    hairColors[i++] = lastColor;
                }
            }
            else
            {
                lastColor = CelesteNetColor.Unpack((ushort)((first << 8) | reader.ReadByte()));
                hairColors[i++] = lastColor;
            }
        }

        frame.HairColors = hairColors;
        frame.HairTexture0 = reader.ReadMappedString();

        CelesteNetPlayerFrameEntity[] followers = new CelesteNetPlayerFrameEntity[reader.ReadByte()];
        for (int i = 0; i < followers.Length; i++)
        {
            CelesteNetPlayerFrameEntity follower = new();
            ReadEntity(ref reader, follower, holding: false);

            string spriteID = reader.ReadMappedString();
            if (spriteID == CelesteNetPayload.RepeatString)
            {
                if (i == 0)
                {
                    throw new InvalidDataException("The first follower cannot reuse string control ids.");
                }

                follower.SpriteID = followers[i - 1].SpriteID;
                follower.AnimationID = followers[i - 1].AnimationID;
            }
            else
            {
                follower.SpriteID = spriteID;
                follower.AnimationID = reader.ReadMappedString();
            }

            follower.AnimationFrame = reader.Read7BitInt();
            followers[i] = follower;
        }

        frame.Followers = followers;

        if ((flags & 0x10) != 0)
        {
            CelesteNetPlayerFrameEntity holding = new();
            ReadEntity(ref reader, holding, holding: true);
            holding.SpriteID = reader.ReadMappedString();
            holding.AnimationID = reader.ReadMappedString();
            holding.AnimationFrame = reader.Read7BitInt();
            frame.Holding = holding;
        }

        if (frame.Dashing)
        {
            frame.DashDirection = reader.ReadByte();
        }
        else
        {
            frame.DashWasB = false;
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidDataException(
                $"playerFrame body has {reader.Remaining} trailing bytes."
            );
        }

        return frame;
    }

    private static void ReadEntity(
        ref CelesteNetWireReader reader,
        CelesteNetPlayerFrameEntity entity,
        bool holding
    )
    {
        if (holding)
        {
            entity.OffsetX = reader.Read7BitInt();
            entity.OffsetY = reader.Read7BitInt();
        }

        entity.ScaleX = reader.ReadSByte() / 16f;
        entity.ScaleY = reader.ReadSByte() / 16f;
        entity.Color = new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        entity.Depth = reader.Read7BitInt();
        entity.SpriteRate = reader.ReadSingle();
        if (reader.ReadBoolean())
        {
            entity.HasJustify = true;
            entity.JustifyX = reader.ReadSingle();
            entity.JustifyY = reader.ReadSingle();
        }
    }
}

/// <summary>One follower or the held entity of a <c>playerFrame</c>.</summary>
public sealed class CelesteNetPlayerFrameEntity
{
    public float ScaleX { get; set; } = 1f;

    public float ScaleY { get; set; } = 1f;

    /// <summary>Full RGBA on the wire, unlike the frame's packed sprite tint.</summary>
    public CelesteNetColor Color { get; set; } = CelesteNetColor.White;

    public int Depth { get; set; }

    public float SpriteRate { get; set; } = 1f;

    public bool HasJustify { get; set; }

    public float JustifyX { get; set; }

    public float JustifyY { get; set; }

    public string SpriteID { get; set; } = "";

    public string AnimationID { get; set; } = "";

    public int AnimationFrame { get; set; }

    /// <summary>Only used while holding: the offset from the player position, as the wire stores it.</summary>
    public float OffsetX { get; set; }

    /// <summary>Only used while holding: the offset from the player position, as the wire stores it.</summary>
    public float OffsetY { get; set; }
}

/// <summary>An 8-bit-per-channel color, optionally packed into CelesteNet's 5-5-5 form.</summary>
public readonly record struct CelesteNetColor(byte R, byte G, byte B, byte A = 255)
{
    public static CelesteNetColor White => new(255, 255, 255);

    /// <summary>Packs the color the way <c>playerFrame</c>'s sprite tint and hair colors are stored.</summary>
    public ushort Pack()
        => (ushort)(((R >> 3) & 0x1f) | (((G >> 3) & 0x1f) << 5) | (((B >> 3) & 0x1f) << 10));

    /// <summary>Unpacks a 5-5-5 color; the low bits are lost and alpha becomes opaque.</summary>
    public static CelesteNetColor Unpack(ushort packed)
        => new(
            (byte)(((packed >> 0) & 0x1f) << 3),
            (byte)(((packed >> 5) & 0x1f) << 3),
            (byte)(((packed >> 10) & 0x1f) << 3)
        );

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}{A:x2}");
}

/// <summary>
/// Splits a CelesteNet DataType payload into its body, which is what the type codecs parse.
/// </summary>
/// <remarks>
/// Every payload is <c>u16 flags | netstring typeID | netstring source | length | body</c>; the length
/// byte count follows <c>DataFlags.Small</c>. The reader is not meant for the slim form: the fake
/// server never registers a slim map, so no slim payload is ever legal on this connection.
/// </remarks>
internal static class CelesteNetPayload
{
    /// <summary>The string control byte that means "same sprite and animation as the previous entry".</summary>
    internal const string RepeatString = "\x02";

    private const ushort FlagSmall = 0x2000;

    public static ReadOnlySpan<byte> SplitBody(ReadOnlySpan<byte> payload, string expectedTypeId)
    {
        CelesteNetWireReader reader = new(payload);

        ushort flags = reader.ReadUInt16();
        string typeId = reader.ReadLiteralString();
        if (typeId != expectedTypeId)
        {
            throw new InvalidDataException($"Expected a '{expectedTypeId}' payload, got '{typeId}'.");
        }

        // The source names the declaring assembly and carries no information this side needs.
        reader.ReadLiteralString();

        int length = (flags & FlagSmall) != 0 ? reader.ReadByte() : reader.ReadUInt16();
        if (reader.Remaining != length)
        {
            throw new InvalidDataException(
                $"'{typeId}' declares a {length} B body but carries {reader.Remaining} B."
            );
        }

        return reader.Take(length);
    }
}

/// <summary>Little-endian reader for the CelesteNet primitive encodings used by the type codecs.</summary>
internal ref struct CelesteNetWireReader
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> data;
    private int offset;

    public CelesteNetWireReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        offset = 0;
    }

    public int Remaining => data.Length - offset;

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new InvalidDataException(
                $"Payload ended early: {count} B requested, {Remaining} B left."
            );
        }

        ReadOnlySpan<byte> slice = data.Slice(offset, count);
        offset += count;
        return slice;
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() != 0;

    public sbyte ReadSByte() => (sbyte)Take(1)[0];

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>LEB128, matching <c>BinaryReader.Read7BitEncodedInt</c>.</summary>
    public uint Read7BitUInt()
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte current = ReadByte();
            value |= (uint)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("7-bit integer is too long.");
    }

    public int Read7BitInt() => unchecked((int)Read7BitUInt());

    /// <summary>
    /// Reads a netstring that may not use the string map. Used for type IDs, meta IDs and sources,
    /// which are never mapped in practice: a mapped string is only legal after a <c>stringMap</c>,
    /// and the bridge's loopback server never sends one.
    /// </summary>
    public string ReadLiteralString()
    {
        byte first = ReadByte();
        if (first == 0)
        {
            return "";
        }

        if (first < 0x20)
        {
            throw new InvalidDataException($"Unexpected control byte 0x{first:x2} in a literal string.");
        }

        return ReadUtf8(first);
    }

    /// <summary>
    /// Reads a netstring the way <c>ReadNetMappedString</c> does: the empty string, a free control
    /// string, or plain UTF-8. A mapped string cannot be resolved without the server's string map and
    /// is treated as malformed.
    /// </summary>
    public string ReadMappedString()
    {
        byte first = ReadByte();
        if (first == 0)
        {
            return "";
        }

        if (first == 0x01)
        {
            throw new InvalidDataException("Mapped string without a string map.");
        }

        if (first <= 0x1f)
        {
            if (first < 0x10)
            {
                if (first == 0x02)
                {
                    return CelesteNetPayload.RepeatString;
                }

                throw new InvalidDataException($"Reserved control byte 0x{first:x2} in a string.");
            }

            return ((char)first).ToString();
        }

        return ReadUtf8(first);
    }

    private string ReadUtf8(byte first)
    {
        // The first byte was already consumed, so the string bytes are [offset - 1, terminator).
        int terminator = data[offset..].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("String is missing its terminator.");
        }

        ReadOnlySpan<byte> bytes = data.Slice(offset - 1, terminator + 1);
        offset += terminator + 1;
        return Utf8.GetString(bytes);
    }
}

/// <summary>Little-endian writer for the CelesteNet primitive encodings used by the type codecs.</summary>
internal ref struct CelesteNetWireWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    private readonly ArrayBufferWriter<byte> buffer;

    public CelesteNetWireWriter(ArrayBufferWriter<byte> buffer)
    {
        this.buffer = buffer;
    }

    public void WriteByte(byte value)
    {
        buffer.GetSpan(1)[0] = value;
        buffer.Advance(1);
    }

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.GetSpan(2), value);
        buffer.Advance(2);
    }

    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(buffer.GetSpan(2), value);
        buffer.Advance(2);
    }

    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    /// <summary>LEB128 over the raw bits, exactly like <c>BinaryWriter.Write7BitEncodedInt</c>.</summary>
    public void Write7BitUInt(uint value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    public void Write7BitInt(int value) => Write7BitUInt(unchecked((uint)value));

    /// <summary>Writes a literal netstring: UTF-8 bytes plus a terminator.</summary>
    public void WriteNetString(string value)
    {
        if (value.Length == 0)
        {
            WriteByte(0);
            return;
        }

        char first = value[0];
        if (first is >= '\x01' and <= '\x1f')
        {
            if (first is >= '\x03' and <= '\x0f')
            {
                throw new ArgumentException("Reserved control strings cannot be written.", nameof(value));
            }

            if (value.Length > 1)
            {
                throw new ArgumentException("Control strings cannot be used to start a string.", nameof(value));
            }

            WriteByte((byte)first);
            return;
        }

        int byteCount = Utf8.GetByteCount(value);
        if (byteCount > 4096)
        {
            throw new ArgumentException("String too long.", nameof(value));
        }

        Span<byte> destination = buffer.GetSpan(byteCount + 1);
        Utf8.GetBytes(value, destination);
        destination[byteCount] = 0;
        buffer.Advance(byteCount + 1);
    }
}
