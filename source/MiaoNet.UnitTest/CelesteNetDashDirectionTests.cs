using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetDashDirectionTests
{
    [TestMethod]
    public void ZeroStaysZero()
        => Assert.AreEqual((byte)0, CelesteNetDashDirections.FromMiaoNet(0));

    [TestMethod]
    public void TheLargestMiaoNetAngleStaysInRange()
        => Assert.AreEqual((byte)255, CelesteNetDashDirections.FromMiaoNet(255));

    [TestMethod]
    public void AQuarterTurnIsRescaledToCelesteNetsScale()
    {
        // MiaoNet's quarter turn is round(64 / 256 * 255) = 64; CelesteNet needs 64 / 256 -> 64.
        Assert.AreEqual((byte)64, CelesteNetDashDirections.FromMiaoNet(64));
    }

    [TestMethod]
    public void TheScaleIsInterpolatedNotJustShifted()
    {
        // 128 in MiaoNet's 255-step scale is just short of a half turn; CelesteNet needs 129/256.
        Assert.AreEqual((byte)129, CelesteNetDashDirections.FromMiaoNet(128));
    }
}
