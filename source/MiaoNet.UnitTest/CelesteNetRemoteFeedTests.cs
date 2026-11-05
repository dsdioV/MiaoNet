using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetRemoteFeedTests
{
    private static readonly byte[] Graphics = { 0x01, 0x02 };
    private static readonly byte[] State = { 0x03, 0x04 };
    private static readonly byte[] FrameA = { 0x10 };
    private static readonly byte[] FrameB = { 0x11 };

    [TestMethod]
    public void PlayerFramesAreHeldUntilGraphicsAndStateArrive()
    {
        CelesteNetRemoteFeed feed = new();

        Assert.AreEqual(CelesteNetRemoteFeedAction.Hold,
            feed.Accept(CelesteNetRemoteFeed.PlayerFrameTypeId, FrameA));
        Assert.IsTrue(feed.HasHeldFrame);
        Assert.IsNull(feed.TakeHeldFrame());

        // One birth frame is not enough: the client binds player frames to both graphics and state.
        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept(CelesteNetRemoteFeed.PlayerGraphicsTypeId, Graphics));
        Assert.IsFalse(feed.IsGhostReady);
        Assert.IsNull(feed.TakeHeldFrame());

        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept(CelesteNetRemoteFeed.PlayerStateTypeId, State));
        Assert.IsTrue(feed.IsGhostReady);
        CollectionAssert.AreEqual(FrameA, feed.TakeHeldFrame());
        Assert.IsFalse(feed.HasHeldFrame);
    }

    [TestMethod]
    public void OnlyTheNewestHeldFrameIsKept()
    {
        CelesteNetRemoteFeed feed = new();
        feed.Accept(CelesteNetRemoteFeed.PlayerFrameTypeId, FrameA);
        feed.Accept(CelesteNetRemoteFeed.PlayerFrameTypeId, FrameB);
        feed.Accept(CelesteNetRemoteFeed.PlayerGraphicsTypeId, Graphics);
        feed.Accept(CelesteNetRemoteFeed.PlayerStateTypeId, State);

        CollectionAssert.AreEqual(FrameB, feed.TakeHeldFrame());
    }

    [TestMethod]
    public void FramesFlowOnceTheGhostIsReady()
    {
        CelesteNetRemoteFeed feed = new();
        feed.Accept(CelesteNetRemoteFeed.PlayerGraphicsTypeId, Graphics);
        feed.Accept(CelesteNetRemoteFeed.PlayerStateTypeId, State);

        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept(CelesteNetRemoteFeed.PlayerFrameTypeId, FrameA));
        Assert.IsFalse(feed.HasHeldFrame);
        Assert.IsNull(feed.TakeHeldFrame());
    }

    [TestMethod]
    public void GraphicsAndStateAreLetThroughImmediately()
    {
        CelesteNetRemoteFeed feed = new();

        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept(CelesteNetRemoteFeed.PlayerStateTypeId, State));
        Assert.IsTrue(feed.StateSeen);
        Assert.IsFalse(feed.GraphicsSeen);

        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept(CelesteNetRemoteFeed.PlayerGraphicsTypeId, Graphics));
        Assert.IsTrue(feed.GraphicsSeen);
    }

    [TestMethod]
    public void OtherFramesAreLetThroughImmediately()
    {
        CelesteNetRemoteFeed feed = new();

        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept("chat", FrameA));
        Assert.AreEqual(CelesteNetRemoteFeedAction.Send,
            feed.Accept("CoopHelper/SomeData", FrameB));
        Assert.IsFalse(feed.HasHeldFrame);
    }

    [TestMethod]
    public void HeldFrameIsReleasedOnlyOnce()
    {
        CelesteNetRemoteFeed feed = new();
        feed.Accept(CelesteNetRemoteFeed.PlayerFrameTypeId, FrameA);
        feed.Accept(CelesteNetRemoteFeed.PlayerGraphicsTypeId, Graphics);
        feed.Accept(CelesteNetRemoteFeed.PlayerStateTypeId, State);

        CollectionAssert.AreEqual(FrameA, feed.TakeHeldFrame());
        Assert.IsNull(feed.TakeHeldFrame());
    }

    [TestMethod]
    public void NullsAreRejected()
    {
        CelesteNetRemoteFeed feed = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => feed.Accept(null!, FrameA));
        Assert.ThrowsExactly<ArgumentNullException>(() => feed.Accept("playerFrame", null!));
    }
}
