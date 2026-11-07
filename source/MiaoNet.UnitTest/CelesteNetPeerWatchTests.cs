using System.Linq;
using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public class CelesteNetPeerWatchTests {
    private const double Timeout = 5;

    [TestMethod]
    public void APlayerThatKeepsSendingIsNeverExpired() {
        CelesteNetPeerWatch watch = new();
        watch.Saw(7, 0);

        Assert.IsEmpty(watch.TakeExpired(4.9, Timeout));
        watch.Saw(7, 4.9);
        Assert.IsEmpty(watch.TakeExpired(9.8, Timeout));
        Assert.AreEqual(1, watch.Count);
    }

    [TestMethod]
    public void APlayerThatGoesQuietExpiresOnceTheTimeoutPasses() {
        CelesteNetPeerWatch watch = new();
        watch.Saw(7, 100);

        Assert.IsEmpty(watch.TakeExpired(104.9, Timeout));
        CollectionAssert.AreEqual(new uint[] { 7 }, watch.TakeExpired(105.1, Timeout));
        Assert.AreEqual(0, watch.Count);
    }

    [TestMethod]
    public void ExpiryReportsEachPlayerOnlyOnce() {
        CelesteNetPeerWatch watch = new();
        watch.Saw(3, 0);
        watch.Saw(9, 0);

        Assert.HasCount(2, watch.TakeExpired(10, Timeout));
        Assert.IsEmpty(watch.TakeExpired(20, Timeout));
    }

    [TestMethod]
    public void OnlyThePlayersThatWentQuietAreReported() {
        CelesteNetPeerWatch watch = new();
        watch.Saw(3, 0);
        watch.Saw(9, 4);

        CollectionAssert.AreEqual(new uint[] { 3 }, watch.TakeExpired(8, Timeout));
        CollectionAssert.AreEqual(new uint[] { 9 }, watch.TakeExpired(10, Timeout));
    }

    [TestMethod]
    public void ForgetStopsWatchingAPlayer() {
        CelesteNetPeerWatch watch = new();
        watch.Saw(5, 0);

        Assert.IsTrue(watch.Forget(5));
        Assert.IsFalse(watch.Forget(5));
        Assert.IsEmpty(watch.TakeExpired(60, Timeout));
    }

    [TestMethod]
    public void NothingIsReportedWhileNobodyIsWatched() {
        CelesteNetPeerWatch watch = new();

        Assert.IsEmpty(watch.TakeExpired(1000, Timeout));
    }
}
