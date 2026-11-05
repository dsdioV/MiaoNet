using System.Diagnostics.CodeAnalysis;

namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>
/// Reader for the appearance half of CelesteNet's <c>playerGraphics</c> DataType.
/// </summary>
/// <remarks>
/// <para>
/// The bridge keeps relaying <c>playerGraphics</c> verbatim: it carries the sender's real sprite
/// animation table, hair count, hair textures and depth, which MiaoNet's own data plane does not
/// have. The rebuilt <c>playerFrame</c> only needs a small projection of it, so this reader skips
/// the metas and the fields the frame does not use instead of mirroring the whole DataType.
/// </para>
/// <para>
/// Layout (CelesteNet 2.4.2, <c>DataPlayerGraphics.Read</c>), after the standard meta block:
/// <c>i32 Depth | 7bit SpriteMode | f32 SpriteRate | 7bit anim count + netstrings | u8 HairCount</c>
/// then the hair step fields, per-segment scales and per-segment textures.
/// </para>
/// </remarks>
public sealed class CelesteNetPlayerGraphics
{
    /// <summary>DataType ID on the wire.</summary>
    public const string TypeId = "playerGraphics";

    /// <summary>A hard ceiling so a corrupt count cannot allocate unbounded memory.</summary>
    private const int MaxAnimations = 4096;

    /// <summary>Player depth; the ghost is drawn one deeper.</summary>
    public int Depth { get; private set; }

    /// <summary><c>PlayerSpriteMode</c>; only a real client can apply it.</summary>
    public int SpriteMode { get; private set; }

    public float SpriteRate { get; private set; } = 1f;

    /// <summary>Animation names; a frame's animation id indexes this table.</summary>
    public string[] SpriteAnimations { get; private set; } = [];

    /// <summary>How many hair segments the sender has; frames should provide that many colors.</summary>
    public int HairCount { get; private set; }

    /// <summary>Per-segment hair textures; index 0 is the bangs, which a frame may override.</summary>
    public string[] HairTextures { get; private set; } = [];

    /// <summary>The first hair texture, when the sender sent one.</summary>
    public string HairTexture0 => HairTextures.Length > 0 ? HairTextures[0] : "";

    /// <summary>
    /// Decodes a complete <c>playerGraphics</c> payload.
    /// </summary>
    /// <returns>False when the payload is malformed; the bridge then keeps relaying it as before.</returns>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out CelesteNetPlayerGraphics? graphics
    )
    {
        graphics = null;
        try
        {
            graphics = DecodeBody(CelesteNetPayload.SplitBody(payload, TypeId));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static CelesteNetPlayerGraphics DecodeBody(ReadOnlySpan<byte> body)
    {
        CelesteNetWireReader reader = new(body);

        int metaCount = reader.ReadByte();
        for (int i = 0; i < metaCount; i++)
        {
            // Meta IDs and sources are never mapped on this connection; the body itself is skipped.
            reader.ReadMappedString();
            int metaLength = reader.ReadByte();
            reader.Take(metaLength);
        }

        CelesteNetPlayerGraphics graphics = new()
        {
            Depth = reader.ReadInt32(),
            SpriteMode = reader.Read7BitInt(),
            SpriteRate = reader.ReadSingle(),
        };

        int animationCount = reader.Read7BitInt();
        if (animationCount < 0 || animationCount > MaxAnimations)
        {
            throw new InvalidDataException($"playerGraphics animation count {animationCount} is out of range.");
        }

        string[] animations = new string[animationCount];
        for (int i = 0; i < animations.Length; i++)
        {
            animations[i] = reader.ReadMappedString();
        }

        graphics.SpriteAnimations = animations;
        graphics.HairCount = reader.ReadByte();

        // HairStepPerSegment (two floats), HairStepInFacingPerSegment, HairStepApproach and
        // HairStepYSinePerSegment: the frame does not carry any of them, the relayed graphics does.
        reader.Take(5 * sizeof(float));

        // HairScales: one Vector2 per segment.
        reader.Take(graphics.HairCount * 2 * sizeof(float));

        string[] textures = new string[graphics.HairCount];
        for (int i = 0; i < textures.Length; i++)
        {
            string texture = reader.ReadMappedString();
            if (texture == CelesteNetPayload.RepeatString && i > 0)
            {
                texture = textures[i - 1];
            }

            textures[i] = texture;
        }

        graphics.HairTextures = textures;

        if (reader.Remaining != 0)
        {
            throw new InvalidDataException(
                $"playerGraphics body has {reader.Remaining} trailing bytes."
            );
        }

        return graphics;
    }
}
