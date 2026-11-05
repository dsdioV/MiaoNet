using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetAppearanceKeyframeTests
{
    private const uint Player = 7;

    [TestMethod]
    public void FirstAppearanceIsSentImmediately()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player));

        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0, out CelesteNetPlayerFrame? frame));
        Assert.IsNotNull(frame);
        Assert.AreEqual(Player, frame.PlayerID);

        // Taking it consumes the flag: the unchanged stream that follows is not a keyframe.
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 1, out _));
    }

    [TestMethod]
    public void AnAppearanceChangeIsRateLimited()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player, hairRed: 0x44));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0, out _));

        synthesizer.AcceptAppearance(Player, Appearance(Player, hairRed: 0xAA));
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 0.1, out _));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0.25, out CelesteNetPlayerFrame? latest));
        Assert.AreEqual(0xAA, latest.HairColors[0].R);
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 0.5, out _));
    }

    [TestMethod]
    public void OnlyTheNewestAppearanceInsideTheWindowIsKept()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player, hairRed: 0x11));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0, out _));

        synthesizer.AcceptAppearance(Player, Appearance(Player, hairRed: 0x22));
        synthesizer.AcceptAppearance(Player, Appearance(Player, hairRed: 0x33));
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 0.1, out _));

        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0.3, out CelesteNetPlayerFrame? latest));
        Assert.AreEqual(0x33, latest.HairColors[0].R);
    }

    [TestMethod]
    public void AnUnchangedAppearanceNeverBecomesAKeyframe()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0, out _));

        synthesizer.AcceptAppearance(Player, Appearance(Player));
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 1, out _));
    }

    [TestMethod]
    public void PlayersHaveIndependentWindows()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(1, Appearance(1));
        synthesizer.AcceptAppearance(2, Appearance(2));

        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(1, 0, out _));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(2, 0.05, out _));

        synthesizer.AcceptAppearance(1, Appearance(1, hairRed: 0xAA));
        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(1, 0.1, out _));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(1, 0.26, out _));
    }

    [TestMethod]
    public void MarkingPendingResendsAfterALevelEntryButKeepsTheRateLimit()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0, out _));

        synthesizer.MarkAppearanceKeyframesPending();

        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 0.1, out _));
        Assert.IsTrue(synthesizer.TryTakeAppearanceKeyframe(Player, 0.25, out _));
    }

    [TestMethod]
    public void TombstoneDropsThePendingKeyframe()
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        synthesizer.AcceptAppearance(Player, Appearance(Player));
        synthesizer.Tombstone(Player);

        Assert.IsFalse(synthesizer.TryTakeAppearanceKeyframe(Player, 10, out _));
    }

    private static CelesteNetPlayerFrame Appearance(uint playerID, byte hairRed = 0x44)
        => new() {
            PlayerID = playerID,
            HairColors = [new CelesteNetColor(hairRed, 0xB7, 0xFF)],
            HairTexture0 = "characters/player/bangs0",
            Color = CelesteNetColor.White,
            HairSimulateMotion = true,
        };
}
