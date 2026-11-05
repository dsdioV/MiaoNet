using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;
using MiaoNet.Shared;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetHoldableInputTests
{
    private const uint Player = 7;

    [TestMethod]
    public void ADroppedHoldableIsNotPutInTheFrame()
    {
        // PlayerState keeps the last item after a drop, so the delta flag is what says it is gone.
        CelesteNetHoldableInput translated = CelesteNetHoldableInputs.For(
            hasHoldable: false,
            new HoldableInfo(HoldableType.Theo, new Vector2(3, -4))
        );

        Assert.AreEqual(CelesteNetHoldableKind.None, translated.Kind);

        CelesteNetPlayerFrame frame = Synthesise(translated);
        Assert.IsNull(frame.Holding, "A released item must clear the holding flag.");
    }

    [TestMethod]
    public void AHeldHoldableIsPutInTheFrame()
    {
        CelesteNetHoldableInput translated = CelesteNetHoldableInputs.For(
            hasHoldable: true,
            new HoldableInfo(HoldableType.Theo, new Vector2(3, -4))
        );

        Assert.AreEqual(CelesteNetHoldableKind.Theo, translated.Kind);
        Assert.AreEqual(3f, translated.OffsetX);
        Assert.AreEqual(-4f, translated.OffsetY);

        CelesteNetPlayerFrame frame = Synthesise(translated);
        Assert.IsNotNull(frame.Holding);
        Assert.AreEqual("theo_crystal", frame.Holding.SpriteID);
    }

    [TestMethod]
    public void UnsupportedHoldablesAreMarkedButNotDrawn()
    {
        CelesteNetHoldableInput translated = CelesteNetHoldableInputs.For(
            hasHoldable: true,
            new HoldableInfo(HoldableType.Player, null)
        );

        // The local client cannot draw a grabbed player as a cnet sprite; the flag has to stay clear.
        Assert.AreEqual(CelesteNetHoldableKind.Unsupported, translated.Kind);
        Assert.IsNull(Synthesise(translated).Holding);
    }

    [TestMethod]
    public void TheOffsetComesFromTheMergedState()
    {
        // The delta elides an unchanged offset, so the state is the only place that still has it.
        HoldableInfo state = new(HoldableType.Theo, new Vector2(10, 20));

        CelesteNetHoldableInput translated = CelesteNetHoldableInputs.For(hasHoldable: true, state);

        Assert.AreEqual(10f, translated.OffsetX);
        Assert.AreEqual(20f, translated.OffsetY);
    }

    private static CelesteNetPlayerFrame Synthesise(CelesteNetHoldableInput holdable)
    {
        CelesteNetFrameSynthesizer synthesizer = new();
        CelesteNetPlayerFrameInput input = new() {
            PlayerID = Player,
            AnimationName = "idle",
            Holdable = holdable,
        };

        Assert.IsTrue(synthesizer.Queue(input, 0));
        IReadOnlyList<CelesteNetSynthesisedFrame> frames = synthesizer.Drain(1);
        Assert.HasCount(1, frames);
        Assert.IsTrue(CelesteNetPlayerFrame.TryDecode(frames[0].Payload, out CelesteNetPlayerFrame? frame));
        Assert.IsNotNull(frame);
        return frame;
    }
}
