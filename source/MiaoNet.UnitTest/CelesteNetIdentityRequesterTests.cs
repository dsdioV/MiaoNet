using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetIdentityRequesterTests
{
    [TestMethod]
    public void DoesNotAskBeforeTheSessionIsReady()
    {
        CelesteNetIdentityRequester requester = new();

        Assert.IsFalse(requester.ShouldRequest(sessionReady: false, miaoNetConnected: true));
    }

    [TestMethod]
    public void DoesNotAskWhileMiaoNetIsDisconnected()
    {
        CelesteNetIdentityRequester requester = new();

        Assert.IsFalse(requester.ShouldRequest(sessionReady: true, miaoNetConnected: false));
    }

    [TestMethod]
    public void AsksOnlyOnce()
    {
        CelesteNetIdentityRequester requester = new();

        Assert.IsTrue(requester.ShouldRequest(sessionReady: true, miaoNetConnected: true));
        requester.MarkRequested();

        // The heartbeat calls this every 250 ms; asking again would make every peer answer every time.
        for (int i = 0; i < 10; i++)
            Assert.IsFalse(requester.ShouldRequest(sessionReady: true, miaoNetConnected: true));

        Assert.IsTrue(requester.AlreadyRequested);
    }

    [TestMethod]
    public void AsksAgainAfterReconnecting()
    {
        CelesteNetIdentityRequester requester = new();
        Assert.IsTrue(requester.ShouldRequest(sessionReady: true, miaoNetConnected: true));
        requester.MarkRequested();

        // MiaoNet went away: the peers we asked are gone, so the next connection asks the new ones.
        requester.Reset();

        Assert.IsFalse(requester.AlreadyRequested);
        Assert.IsTrue(requester.ShouldRequest(sessionReady: true, miaoNetConnected: true));
    }
}
