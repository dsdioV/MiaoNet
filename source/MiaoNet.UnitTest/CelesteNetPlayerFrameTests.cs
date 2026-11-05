using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetPlayerFrameCodecTests
{
    /// <summary>
    /// A minimal hand-written frame: one player, no followers, no holding, no dash. The body starts
    /// with the player id, not with a meta count, and the length field counts exactly the body.
    /// </summary>
    [TestMethod]
    public void PayloadMatchesHandWrittenBytesWithoutMetaCount()
    {
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 3,
            PositionX = 1f,
            PositionY = 2f,
            ScaleX = 1f,
            ScaleY = 1f,
            Color = CelesteNetColor.White,
            SpeedX = 0f,
            SpeedY = 0f,
            AnimationID = 0,
            AnimationFrame = 0,
            FacingLeft = true,
            HairColors = [CelesteNetColor.White, CelesteNetColor.White],
            HairTexture0 = "",
            Followers = [],
        };

        byte[] payload = CelesteNetPlayerFrame.Encode(frame);

        const string Expected =
            "31 00 " // Unreliable | CoreType | NoStandardMeta
            + "70 6c 61 79 65 72 46 72 61 6d 65 00 " // playerFrame
            + "43 65 6c 65 73 74 65 4e 65 74 2e 53 68 61 72 65 64 00 " // CelesteNet.Shared
            + "14 00 " // body length: 20
            + "03 " // player id 3, no meta count in front of it
            + "01 " // FacingLeft
            + "01 02 " // position (1, 2)
            + "10 10 " // scale (1, 1)
            + "ff 7f " // packed white
            + "00 00 00 00 " // speed (0, 0)
            + "00 00 " // animation id 0, frame 0
            + "02 " // two hair colors
            + "7f ff 81 " // white, then a run of one repeat
            + "00 " // empty hair texture
            + "00"; // no followers

        Assert.AreEqual(Expected, ToHex(payload));
        Assert.HasCount(54, payload);
    }

    [TestMethod]
    public void FullBodyRoundTrips()
    {
        CelesteNetColor tint = new(0xF8, 0x80, 0x08);
        CelesteNetColor hairA = new(0x44, 0xB7, 0xFF);
        CelesteNetColor hairB = new(0xAC, 0x32, 0x32);
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 1234,
            PositionX = -100f,
            PositionY = 250f,
            ScaleX = 1.5f,
            ScaleY = -0.5f,
            Color = tint,
            FacingLeft = true,
            HairSimulateMotion = true,
            SpeedX = 123.9f,
            SpeedY = -500f,
            AnimationID = 7,
            AnimationFrame = 33,
            HairColors = [hairA, hairA, hairB, hairB, hairB],
            HairTexture0 = "characters/player/bangs2",
            Followers = [
                new() {
                    ScaleX = 0.5f,
                    ScaleY = 1.25f,
                    Color = new(1, 2, 3, 4),
                    Depth = 42,
                    SpriteRate = 2f,
                    HasJustify = true,
                    JustifyX = 0.25f,
                    JustifyY = 0.75f,
                    SpriteID = "collectables/strawberry",
                    AnimationID = "idle",
                    AnimationFrame = 3,
                },
                new() {
                    ScaleX = 0.5f,
                    ScaleY = 1.25f,
                    Color = new(5, 6, 7, 8),
                    Depth = 43,
                    SpriteRate = 1.5f,
                    SpriteID = "collectables/strawberry",
                    AnimationID = "idle",
                    AnimationFrame = 4,
                },
            ],
            Holding = new() {
                OffsetX = 4f,
                OffsetY = -8f,
                ScaleX = 1f,
                ScaleY = 1f,
                Color = new(9, 10, 11, 12),
                Depth = 44,
                SpriteRate = 1f,
                SpriteID = "theo_crystal",
                AnimationID = "idle",
                AnimationFrame = 5,
            },
            Dashing = true,
            DashWasB = true,
            DashDirection = 200,
            Dead = true,
        };

        Assert.IsTrue(CelesteNetPlayerFrame.TryDecode(CelesteNetPlayerFrame.Encode(frame), out CelesteNetPlayerFrame? decoded));
        Assert.IsNotNull(decoded);

        Assert.AreEqual(frame.PlayerID, decoded.PlayerID);
        Assert.AreEqual(frame.PositionX, decoded.PositionX);
        Assert.AreEqual(frame.PositionY, decoded.PositionY);
        Assert.AreEqual(frame.ScaleX, decoded.ScaleX);
        Assert.AreEqual(frame.ScaleY, decoded.ScaleY);
        Assert.AreEqual(frame.Color.Pack(), decoded.Color.Pack());
        Assert.AreEqual(frame.FacingLeft, decoded.FacingLeft);
        Assert.AreEqual(frame.HairSimulateMotion, decoded.HairSimulateMotion);
        Assert.AreEqual(123f, decoded.SpeedX);
        Assert.AreEqual(-500f, decoded.SpeedY);
        Assert.AreEqual(frame.AnimationID, decoded.AnimationID);
        Assert.AreEqual(frame.AnimationFrame, decoded.AnimationFrame);
        Assert.HasCount(frame.HairColors.Length, decoded.HairColors);
        for (int i = 0; i < frame.HairColors.Length; i++)
        {
            Assert.AreEqual(frame.HairColors[i].Pack(), decoded.HairColors[i].Pack());
        }

        Assert.AreEqual(frame.HairTexture0, decoded.HairTexture0);
        Assert.HasCount(2, decoded.Followers);
        Assert.AreEqual("collectables/strawberry", decoded.Followers[0].SpriteID);
        Assert.AreEqual("idle", decoded.Followers[0].AnimationID);
        Assert.AreEqual(3, decoded.Followers[0].AnimationFrame);
        Assert.AreEqual(42, decoded.Followers[0].Depth);
        Assert.AreEqual(new CelesteNetColor(1, 2, 3, 4), decoded.Followers[0].Color);
        Assert.AreEqual(0.25f, decoded.Followers[0].JustifyX);
        // The second follower reuses the first one's strings; the encoder writes that as one byte.
        Assert.AreEqual("collectables/strawberry", decoded.Followers[1].SpriteID);
        Assert.AreEqual("idle", decoded.Followers[1].AnimationID);
        Assert.AreEqual(4, decoded.Followers[1].AnimationFrame);
        Assert.AreEqual(new CelesteNetColor(5, 6, 7, 8), decoded.Followers[1].Color);

        Assert.IsNotNull(decoded.Holding);
        Assert.AreEqual("theo_crystal", decoded.Holding.SpriteID);
        Assert.AreEqual("idle", decoded.Holding.AnimationID);
        Assert.AreEqual(5, decoded.Holding.AnimationFrame);
        Assert.AreEqual(4f, decoded.Holding.OffsetX);
        Assert.AreEqual(-8f, decoded.Holding.OffsetY);
        Assert.AreEqual(new CelesteNetColor(9, 10, 11, 12), decoded.Holding.Color);
        Assert.AreEqual(200, decoded.DashDirection);
        Assert.IsTrue(decoded.Dashing);
        Assert.IsTrue(decoded.DashWasB);
        Assert.IsTrue(decoded.Dead);
    }

    [TestMethod]
    public void RepeatedFollowerStringsCompressToOneByte()
    {
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 1,
            Followers = [MakeFollower(7), MakeFollower(8), MakeFollower(9)],
        };

        byte[] payload = CelesteNetPlayerFrame.Encode(frame);
        Assert.IsTrue(CelesteNetPlayerFrame.TryDecode(payload, out CelesteNetPlayerFrame? decoded));
        Assert.IsNotNull(decoded);
        Assert.HasCount(3, decoded.Followers);
        Assert.AreEqual(7, decoded.Followers[0].AnimationFrame);
        Assert.AreEqual(8, decoded.Followers[1].AnimationFrame);
        Assert.AreEqual(9, decoded.Followers[2].AnimationFrame);

        static CelesteNetPlayerFrameEntity MakeFollower(int animationFrame) => new() {
            SpriteID = "collectables/strawberry",
            AnimationID = "idle",
            AnimationFrame = animationFrame,
        };
    }

    [TestMethod]
    public void TruncatedOrMistypedPayloadsAreRejected()
    {
        byte[] payload = CelesteNetPlayerFrame.Encode(new() { PlayerID = 1 });

        Assert.IsFalse(CelesteNetPlayerFrame.TryDecode(payload.AsSpan(0, payload.Length - 2), out _));
        Assert.IsFalse(CelesteNetPlayerFrame.TryDecode(Array.Empty<byte>(), out _));

        byte[] wrongType = (byte[])payload.Clone();
        wrongType[2] = (byte)'x';
        Assert.IsFalse(CelesteNetPlayerFrame.TryDecode(wrongType, out _));
    }

    [TestMethod]
    public void PlayerGraphicsReaderSkipsMetasAndFillsTheAnimationTable()
    {
        byte[] payload = CelesteNetTestPayloads.PlayerGraphics(
            animations: ["idle", "walk", "run"],
            hairCount: 3,
            hairTextures: ["characters/player/bangs0", "characters/player/hair00", "characters/player/hair00"],
            depth: 5,
            spriteMode: 2
        );

        Assert.IsTrue(CelesteNetPlayerGraphics.TryDecode(payload, out CelesteNetPlayerGraphics? graphics));
        Assert.IsNotNull(graphics);
        Assert.AreEqual(5, graphics.Depth);
        Assert.AreEqual(2, graphics.SpriteMode);
        Assert.AreEqual(1f, graphics.SpriteRate);
        CollectionAssert.AreEqual(new[] { "idle", "walk", "run" }, graphics.SpriteAnimations);
        Assert.AreEqual(3, graphics.HairCount);
        Assert.HasCount(3, graphics.HairTextures);
        Assert.AreEqual("characters/player/bangs0", graphics.HairTexture0);
        // Index 2 repeats index 1 on the wire; the reader resolves the repeat.
        Assert.AreEqual("characters/player/hair00", graphics.HairTextures[2]);
    }

    [TestMethod]
    public void PlayerGraphicsReaderRejectsTruncatedPayloads()
    {
        byte[] payload = CelesteNetTestPayloads.PlayerGraphics(["idle"], 1, ["a"], 0, 0);

        for (int length = 0; length < payload.Length; length++)
        {
            Assert.IsFalse(
                CelesteNetPlayerGraphics.TryDecode(payload.AsSpan(0, length), out _),
                $"A {length} B prefix must not decode."
            );
        }
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
        => string.Join(' ', bytes.ToArray().Select(static b => b.ToString("x2", CultureInfo.InvariantCulture)));
}

[TestClass]
public sealed class CelesteNetFrameSynthesizerTests
{
    private const uint PlayerID = 9;

    private const string TemplateHairTexture = "characters/player/bangs3";

    [TestMethod]
    public void UnknownAnimationNameKeepsTheCurrentAnimation()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        CelesteNetPlayerFrame frame = SynthesiseOne(synthesizer, NewInput() with { AnimationName = "dance" });

        Assert.AreEqual(-1, frame.AnimationID);
    }

    [TestMethod]
    public void DeathIsRememberedAcrossFrames()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        synthesizer.SetDead(PlayerID, true);
        Assert.IsTrue(SynthesiseOne(synthesizer, NewInput() with { PositionX = 1f }).Dead);

        synthesizer.SetDead(PlayerID, false);
        Assert.IsFalse(SynthesiseOne(synthesizer, NewInput() with { PositionX = 2f }).Dead);
    }

    [TestMethod]
    public void DashKindIsTakenFromTheDashCountAtDashStart()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        // A dash that started with no dashes left is an A dash, and stays one while it runs.
        CelesteNetPlayerFrame first = SynthesiseOne(
            synthesizer,
            NewInput() with { Dashing = true, Dashes = 0, DashDirection = 42 }
        );
        Assert.IsTrue(first.Dashing);
        Assert.IsFalse(first.DashWasB);
        Assert.AreEqual(42, first.DashDirection);

        CelesteNetPlayerFrame sameDash = SynthesiseOne(
            synthesizer,
            NewInput() with { Dashing = true, Dashes = 1, DashDirection = 42 }
        );
        Assert.IsFalse(sameDash.DashWasB, "A dash already running does not change its particle type.");

        SynthesiseOne(synthesizer, NewInput() with { Dashing = false, Dashes = 1 });

        CelesteNetPlayerFrame nextDash = SynthesiseOne(
            synthesizer,
            NewInput() with { Dashing = true, Dashes = 1, DashDirection = 7 }
        );
        Assert.IsTrue(nextDash.DashWasB);
        Assert.AreEqual(7, nextDash.DashDirection);

        CelesteNetPlayerFrame noDash = SynthesiseOne(synthesizer, NewInput() with { Dashing = false });
        Assert.IsFalse(noDash.Dashing);
        Assert.IsFalse(noDash.DashWasB);
    }

    [TestMethod]
    public void SpeedIsDerivedFromPositionDelta()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        CelesteNetPlayerFrame first = SynthesiseOne(
            synthesizer,
            NewInput() with { PositionX = 0f, PositionY = 0f, DeltaSeconds = 1f / 60f }
        );
        Assert.AreEqual(0f, first.SpeedX);

        CelesteNetPlayerFrame second = SynthesiseOne(
            synthesizer,
            NewInput() with { PositionX = 6f, PositionY = -3f, DeltaSeconds = 1f / 60f }
        );
        // The wire truncates speed to an integer, so a 6 px frame at 60 Hz lands on 359 or 360.
        Assert.AreEqual(360f, second.SpeedX, 1f);
        Assert.AreEqual(-180f, second.SpeedY, 1f);
    }

    [TestMethod]
    public void BacklogOfDeltasYieldsOneFramePerPlayerPerEngineFrame()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        Assert.IsTrue(synthesizer.Queue(NewInput() with { PositionX = 1f }, 5));
        Assert.IsTrue(synthesizer.Queue(NewInput() with { PositionX = 2f }, 5));
        Assert.IsTrue(synthesizer.Queue(NewInput() with { PositionX = 3f }, 5));

        Assert.IsEmpty(synthesizer.Drain(5), "The current engine frame is still open.");

        IReadOnlyList<CelesteNetSynthesisedFrame> frames = synthesizer.Drain(6);
        Assert.HasCount(1, frames);
        Assert.AreEqual(3f, Decode(frames[0]).PositionX, "The newest delta of the burst wins.");
        Assert.IsEmpty(synthesizer.Drain(6));
    }

    [TestMethod]
    public void EachPlayerGetsItsOwnFrameInTheSameEngineFrame()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());
        synthesizer.AcceptGraphics(PlayerID + 1, Graphics());

        synthesizer.Queue(NewInput() with { PositionX = 1f }, 5);
        synthesizer.Queue(NewInput() with { PlayerID = PlayerID + 1, PositionX = 2f }, 5);

        IReadOnlyList<CelesteNetSynthesisedFrame> frames = synthesizer.Drain(6);
        Assert.HasCount(2, frames);
        Assert.IsTrue(frames.Any(frame => frame.PlayerID == PlayerID));
        Assert.IsTrue(frames.Any(frame => frame.PlayerID == PlayerID + 1));
    }

    [TestMethod]
    public void TombstoneDropsPendingAndFutureFrames()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        Assert.IsTrue(synthesizer.Queue(NewInput(), 5));

        // The player left MiaoNet: the bridge queues a dead playerInfo right after this, so any frame
        // still buffered here would reach the client after its reference was freed.
        synthesizer.Tombstone(PlayerID);
        Assert.IsTrue(synthesizer.IsTombstoned(PlayerID));
        Assert.IsEmpty(synthesizer.Drain(6), "A frame buffered before the tombstone must not be sent.");

        Assert.IsFalse(synthesizer.Queue(NewInput(), 6));
        Assert.IsEmpty(synthesizer.Drain(7));
    }

    [TestMethod]
    public void RevivingAPlayerAllowsNewFrames()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());
        synthesizer.Tombstone(PlayerID);

        synthesizer.Revive(PlayerID);

        Assert.IsFalse(synthesizer.IsTombstoned(PlayerID));
        Assert.IsTrue(synthesizer.Queue(NewInput(), 5));
        Assert.HasCount(1, synthesizer.Drain(6));
    }

    [TestMethod]
    public void WithoutATemplateTheDefaultHairPaletteIsUsed()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        // Draw counts from the relayed graphics; the color comes from MiaoNet's default palette.
        synthesizer.AcceptGraphics(PlayerID, Graphics(hairCount: 4, hairTextures: ["characters/player/bangs0"]));

        CelesteNetPlayerFrame frame = SynthesiseOne(synthesizer, NewInput() with { Dashes = 0 });

        CelesteNetColor dash0 = new(0x44, 0xB7, 0xFF);
        Assert.HasCount(4, frame.HairColors);
        Assert.AreEqual(dash0.Pack(), frame.HairColors[0].Pack());
        Assert.AreEqual("characters/player/bangs0", frame.HairTexture0);

        CelesteNetPlayerFrame starFrame = SynthesiseOne(
            synthesizer,
            NewInput() with { Dashes = 0, StarFlying = true, PositionX = 5f }
        );
        CelesteNetColor feather = new(0xF2, 0xEB, 0x6D);
        Assert.AreEqual(feather.Pack(), starFrame.HairColors[0].Pack());
    }

    [TestMethod]
    public void HeldItemsCelesteNetCannotExpressAreOmitted()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        Assert.IsNotNull(SynthesiseOne(
            synthesizer,
            NewInput() with { Holdable = new() { Kind = CelesteNetHoldableKind.Theo } }
        ).Holding);

        CelesteNetPlayerFrame jelly = SynthesiseOne(
            synthesizer,
            NewInput() with {
                Holdable = new() {
                    Kind = CelesteNetHoldableKind.Jelly,
                    AnimationID = "squished",
                    AnimationFrame = 3,
                    ScaleX = 0.5f,
                    ScaleY = 0.75f,
                },
            }
        );
        Assert.IsNotNull(jelly.Holding);
        Assert.AreEqual("glider", jelly.Holding.SpriteID);
        Assert.AreEqual("squished", jelly.Holding.AnimationID);
        Assert.AreEqual(3, jelly.Holding.AnimationFrame);
        Assert.AreEqual(0.5f, jelly.Holding.ScaleX);

        Assert.IsNull(SynthesiseOne(
            synthesizer,
            NewInput() with { Holdable = new() { Kind = CelesteNetHoldableKind.Unsupported } }
        ).Holding);
        Assert.IsNull(SynthesiseOne(synthesizer, NewInput()).Holding);
    }

    [TestMethod]
    public void ClearPendingAndResetTransientStateForgetBufferedWork()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());
        synthesizer.Queue(NewInput() with { PositionX = 1f }, 5);
        synthesizer.ClearPending();
        Assert.IsEmpty(synthesizer.Drain(6));

        synthesizer.Queue(NewInput() with { PositionX = 5f }, 6);
        Assert.AreEqual(5f, Decode(synthesizer.Drain(7)[0]).PositionX);
        synthesizer.ResetTransientState();
        CelesteNetPlayerFrame reset = SynthesiseOne(synthesizer, NewInput() with { PositionX = 9f });
        Assert.AreEqual(0f, reset.SpeedX, "A map change forgets the previous position.");
    }

    [TestMethod]
    public void DeadStartsFalseAndFollowsTheLiveStateEdges()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptGraphics(PlayerID, Graphics());

        Assert.IsFalse(
            SynthesiseOne(synthesizer, NewInput()).Dead,
            "Without a LiveState event a player is alive, even before the first delta."
        );

        synthesizer.SetDead(PlayerID, true);
        Assert.IsTrue(SynthesiseOne(synthesizer, NewInput() with { PositionX = 1f }).Dead);

        synthesizer.SetDead(PlayerID, false);
        Assert.IsFalse(SynthesiseOne(synthesizer, NewInput() with { PositionX = 2f }).Dead);
    }

    private static CelesteNetPlayerFrame SynthesiseOne(CelesteNetFrameSynthesizer synthesizer, CelesteNetPlayerFrameInput input)
    {
        ulong frame = 100;
        Assert.IsTrue(synthesizer.Queue(input, frame));
        IReadOnlyList<CelesteNetSynthesisedFrame> frames = synthesizer.Drain(frame + 1);
        Assert.HasCount(1, frames);
        return Decode(frames[0]);
    }

    private static CelesteNetPlayerFrame Decode(CelesteNetSynthesisedFrame frame)
    {
        Assert.IsTrue(CelesteNetPlayerFrame.TryDecode(frame.Payload, out CelesteNetPlayerFrame? decoded));
        Assert.IsNotNull(decoded);
        return decoded;
    }

    private static CelesteNetPlayerFrameInput NewInput() => new() {
        PlayerID = PlayerID,
        AnimationName = "idle",
        Dashes = 0,
        DeltaSeconds = 1f / 60f,
    };

    private static CelesteNetPlayerGraphics Graphics(int hairCount = 4, string[]? hairTextures = null)
    {
        byte[] payload = CelesteNetTestPayloads.PlayerGraphics(
            animations: ["idle", "walk", "run"],
            hairCount: hairCount,
            hairTextures: hairTextures ?? [TemplateHairTexture, "characters/player/hair07"],
            depth: 0,
            spriteMode: 0
        );

        Assert.IsTrue(CelesteNetPlayerGraphics.TryDecode(payload, out CelesteNetPlayerGraphics? graphics));
        Assert.IsNotNull(graphics);
        return graphics;
    }
}

internal static class CelesteNetTestPayloads
{
    private const string Source = "CelesteNet.Shared";

    public static byte[] PlayerGraphics(
        string[] animations,
        int hairCount,
        string[] hairTextures,
        int depth,
        int spriteMode
    )
    {
        List<byte> body = [];
        body.Add(2);
        // MetaPlayerPublicState: one 7-bit player id.
        AddNetString(body, "playerPubSt");
        body.Add(1);
        body.Add(0x03);
        // MetaBoundRef: bound to playerInfo, id, alive.
        AddNetString(body, "bound");
        body.Add((byte)(11 + 1 + 1));
        AddNetString(body, "playerInfo");
        body.Add(0x03);
        body.Add(1);

        AddInt32(body, depth);
        Add7Bit(body, spriteMode);
        AddSingle(body, 1f);

        Add7Bit(body, animations.Length);
        foreach (string animation in animations)
        {
            AddNetString(body, animation);
        }

        body.Add((byte)hairCount);
        AddSingle(body, 1f);
        AddSingle(body, 1f);
        AddSingle(body, 0f);
        AddSingle(body, 64f);
        AddSingle(body, 0f);
        for (int i = 0; i < hairCount; i++)
        {
            AddSingle(body, 1f);
            AddSingle(body, 1f);
        }

        string lastTexture = "";
        for (int i = 0; i < hairCount; i++)
        {
            string texture = i < hairTextures.Length ? hairTextures[i] : hairTextures[^1];
            if (i > 0 && texture == lastTexture)
            {
                body.Add(0x02);
            }
            else
            {
                AddNetString(body, texture);
            }

            lastTexture = texture;
        }

        List<byte> payload = [];
        AddUInt16(payload, 0x0010);
        AddNetString(payload, "playerGraphics");
        AddNetString(payload, Source);
        AddUInt16(payload, (ushort)body.Count);
        payload.AddRange(body);
        return [.. payload];
    }

    private static void AddNetString(List<byte> target, string value)
    {
        target.AddRange(Encoding.UTF8.GetBytes(value));
        target.Add(0);
    }

    private static void AddUInt16(List<byte> target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    private static void AddInt32(List<byte> target, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    private static void AddSingle(List<byte> target, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    private static void Add7Bit(List<byte> target, int value)
    {
        uint current = unchecked((uint)value);
        while (current >= 0x80)
        {
            target.Add((byte)(current | 0x80));
            current >>= 7;
        }

        target.Add((byte)current);
    }
}
