using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetIdentityCacheTests
{
    private static readonly byte[] Info = [0x01];
    private static readonly byte[] Graphics = [0x02, 0x02];
    private static readonly byte[] State = [0x03, 0x03, 0x03];
    private static readonly byte[] Appearance = [0x04, 0x04, 0x04, 0x04];

    [TestMethod]
    public void EmptyCacheCannotAnswer()
    {
        CelesteNetIdentityCache cache = new();

        Assert.IsFalse(cache.IsComplete);
        Assert.IsFalse(cache.TryBuildResponse(out byte[][] frames));
        Assert.IsEmpty(frames);
    }

    [TestMethod]
    public void OnlyTheIdentityTypesAreStored()
    {
        CelesteNetIdentityCache cache = new();

        Assert.IsTrue(cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics));
        Assert.IsTrue(cache.Remember(CelesteNetIdentityCache.PlayerStateTypeId, State));
        Assert.IsTrue(cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance));

        // A player record is synthesised by the receiver from MiaoNet, so caching one would be dead data.
        Assert.IsFalse(cache.Remember(CelesteNetIdentityCache.PlayerInfoTypeId, Info));

        // Passed through the frame handler as well, so every other type must be ignored.
        Assert.IsFalse(cache.Remember("chat", [0x05]));
        Assert.IsFalse(cache.Remember("ready", [0x06]));
    }

    [TestMethod]
    public void ResponseFollowsTheBindingOrderNotTheArrivalOrder()
    {
        CelesteNetIdentityCache cache = new();

        cache.Remember(CelesteNetIdentityCache.PlayerStateTypeId, State);
        cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance);
        cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics);

        Assert.IsTrue(cache.TryBuildResponse(out byte[][] frames));
        Assert.HasCount(3, frames, "A late joiner gets the appearance frame after state.");
        CollectionAssert.AreEqual(Graphics, frames[0]);
        CollectionAssert.AreEqual(State, frames[1]);
        CollectionAssert.AreEqual(Appearance, frames[2]);
    }

    [TestMethod]
    public void ResponseOmitsTheAppearanceFrameUntilOneIsKnown()
    {
        CelesteNetIdentityCache cache = new();
        cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics);
        cache.Remember(CelesteNetIdentityCache.PlayerStateTypeId, State);

        Assert.IsTrue(cache.IsComplete);
        Assert.IsTrue(cache.TryBuildResponse(out byte[][] frames));
        Assert.HasCount(2, frames, "The two binding frames are answered while no appearance frame is known.");
        CollectionAssert.AreEqual(State, frames[1]);

        // Once our own client sends a real frame, the next answer carries it.
        cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance);
        Assert.IsTrue(cache.TryBuildResponse(out frames));
        Assert.HasCount(3, frames);
        CollectionAssert.AreEqual(Appearance, frames[2]);
    }

    [TestMethod]
    public void IncompleteCacheCannotAnswerEvenWithAnAppearanceFrame()
    {
        CelesteNetIdentityCache cache = new();
        cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics);
        cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance);

        Assert.IsFalse(cache.IsComplete);
        Assert.IsFalse(cache.TryBuildResponse(out _));
    }

    [TestMethod]
    public void NewerFramesReplaceOlderOnes()
    {
        CelesteNetIdentityCache cache = new();
        cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics);
        cache.Remember(CelesteNetIdentityCache.PlayerStateTypeId, State);
        cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance);

        byte[] newGraphics = [0x07];
        byte[] newAppearance = [0x08];
        Assert.IsTrue(cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, newGraphics));
        Assert.IsTrue(cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, newAppearance));

        Assert.IsTrue(cache.TryBuildResponse(out byte[][] frames));
        CollectionAssert.AreEqual(newGraphics, frames[0]);
        CollectionAssert.AreEqual(newAppearance, frames[2]);
    }

    [TestMethod]
    public void RepeatedAnswersAreIdentical()
    {
        // Answering must not consume the cache: several peers can ask, and a peer can ask again after
        // reconnecting.
        CelesteNetIdentityCache cache = Complete();

        Assert.IsTrue(cache.TryBuildResponse(out byte[][] first));
        Assert.IsTrue(cache.TryBuildResponse(out byte[][] second));

        Assert.HasCount(3, first);
        Assert.HasCount(3, second);
        for (int i = 0; i < first.Length; i++)
            CollectionAssert.AreEqual(first[i], second[i]);
    }

    [TestMethod]
    public void TheAnswerNeverCarriesAPlayerInfoFrame()
    {
        CelesteNetIdentityCache cache = Complete();

        Assert.IsFalse(cache.Remember(CelesteNetIdentityCache.PlayerInfoTypeId, Info));
        Assert.IsTrue(cache.TryBuildResponse(out byte[][] frames));
        Assert.HasCount(3, frames);
        CollectionAssert.AreEqual(Graphics, frames[0], "The answer starts with graphics, not a player record.");
    }

    private static CelesteNetIdentityCache Complete()
    {
        CelesteNetIdentityCache cache = new();
        cache.Remember(CelesteNetIdentityCache.PlayerGraphicsTypeId, Graphics);
        cache.Remember(CelesteNetIdentityCache.PlayerStateTypeId, State);
        cache.Remember(CelesteNetIdentityCache.PlayerFrameTypeId, Appearance);
        return cache;
    }
}
