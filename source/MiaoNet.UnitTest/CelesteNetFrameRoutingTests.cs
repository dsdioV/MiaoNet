using MiaoNet.CelesteNetBridge.Protocol;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetFrameRoutingTests
{
    private const string Shared = "CelesteNet.Shared";
    private const string Client = "CelesteNet.Client";
    private const string Mod = "SomeOtherMod";

    [TestMethod]
    public void ThirdPartyFramesStayOnTheLegacyStream()
    {
        Assert.AreEqual(
            CelesteNetFrameLane.ThirdParty,
            CelesteNetFrameRouting.Classify("chat", Mod, aboutLocalPlayer: true)
        );
        Assert.AreEqual(
            CelesteNetFrameLane.ThirdParty,
            CelesteNetFrameRouting.Classify("unknownThing", declaringSource: null, aboutLocalPlayer: true)
        );
    }

    [TestMethod]
    public void CelesteNetFramesWithoutAPlayerReferenceStayOnTheLegacyStream()
    {
        Assert.AreEqual(
            CelesteNetFrameLane.ThirdParty,
            CelesteNetFrameRouting.Classify("chat", Shared, aboutLocalPlayer: true)
        );
        Assert.AreEqual(
            CelesteNetFrameLane.ThirdParty,
            CelesteNetFrameRouting.Classify("someFutureCelesteType", Client, aboutLocalPlayer: true)
        );
    }

    [TestMethod]
    public void SessionLocalFramesAreDropped()
    {
        foreach (string typeId in new[] { "keepAlive", "stringMap", "tickRate", "serverStatus", "ready" })
        {
            Assert.AreEqual(
                CelesteNetFrameLane.Drop,
                CelesteNetFrameRouting.Classify(typeId, Shared, aboutLocalPlayer: true),
                typeId
            );
        }

        // The local player's own record is session state; a remote record is not.
        Assert.AreEqual(
            CelesteNetFrameLane.Drop,
            CelesteNetFrameRouting.Classify("playerInfo", Shared, aboutLocalPlayer: true)
        );
        Assert.AreEqual(
            CelesteNetFrameLane.PlayerStream,
            CelesteNetFrameRouting.Classify("playerInfo", Shared, aboutLocalPlayer: false)
        );
    }

    [TestMethod]
    public void PlayerPipelineFramesGoToThePlayerStream()
    {
        foreach (string typeId in CelesteNetFrameRouting.PlayerStreamTypeIds)
        {
            // A playerInfo about us is session state; a remote player's record has to travel.
            bool aboutLocalPlayer = typeId != "playerInfo";

            Assert.AreEqual(
                CelesteNetFrameLane.PlayerStream,
                CelesteNetFrameRouting.Classify(typeId, Shared, aboutLocalPlayer),
                typeId
            );
        }
    }
}
