using System.Collections.Generic;

using MiaoNet.CelesteNetBridge.Protocol;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetRelayFilterTests
{
    private const string Shared = "CelesteNet.Shared";
    private const string Client = "CelesteNet.Client";

    [TestMethod]
    [DataRow("playerFrame")]
    [DataRow("playerGraphics")]
    [DataRow("playerState")]
    [DataRow("playerGrabPlayer")]
    [DataRow("playerMoveTo")]
    [DataRow("dashTrail")]
    [DataRow("chat")]
    [DataRow("emote")]
    [DataRow("audioPlay")]
    [DataRow("netemoji")]
    public void RelaysPlayerFrames(string typeId)
    {
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable(typeId, Shared));
    }

    [TestMethod]
    [DataRow("keepAlive")]
    [DataRow("pingRequest")]
    [DataRow("pingReply")]
    [DataRow("tickRate")]
    [DataRow("ready")]
    [DataRow("dcReason")]
    [DataRow("stringMap")]
    [DataRow("slimMap")]
    [DataRow("udpInfo")]
    [DataRow("channelList")]
    [DataRow("channelMove")]
    [DataRow("session")]
    [DataRow("sessionReq")]
    [DataRow("serverStatus")]
    [DataRow("clientInfo")]
    [DataRow("clientInfoReq")]
    [DataRow("conInfo")]
    [DataRow("commandList")]
    [DataRow("filterList")]
    [DataRow("mapModInfo")]
    [DataRow("mapModInfoReq")]
    [DataRow("modRec")]
    public void DropsSessionLocalFrames(string typeId)
    {
        // Each fake server owns its connection and session state; taking a teammate's would make the
        // local client believe it is on the sender's server.
        Assert.IsFalse(CelesteNetRelayFilter.IsRelayable(typeId, Shared));
    }

    [TestMethod]
    public void DropsLocalPlayerInfoButRelaysRemotePlayerInfo()
    {
        // The local player's record is handed out by the fake server. A remote player's record is the
        // opposite: the client cannot bind any of their frames without it.
        Assert.IsFalse(CelesteNetRelayFilter.IsRelayable(
            "playerInfo", Shared, aboutLocalPlayer: true
        ));
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable(
            "playerInfo", Shared, aboutLocalPlayer: false
        ));
    }

    [TestMethod]
    public void RelaysModTypes()
    {
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable("corkr900CoopHelper_PlayerState_1_1_0", "CoopHelper"));
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable("Foxeline/TailData", "Foxeline"));
    }

    [TestMethod]
    public void RelaysUnknownTypes()
    {
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable("somethingNew", null));
    }

    [TestMethod]
    public void RelaysTypesDeclaredByTheClientAssembly()
    {
        // UpstreamSources names both CelesteNet assemblies even though the client ships no DataTypes
        // of its own today: a future one must follow the same rules rather than slip through as a mod.
        Assert.IsFalse(CelesteNetRelayFilter.IsRelayable("ready", Client));
        Assert.IsTrue(CelesteNetRelayFilter.IsRelayable("playerState", Client));
    }

    #region IsDeliverable: remote frames pushed into the local real client

    [TestMethod]
    [DataRow("playerFrame")]
    [DataRow("playerGraphics")]
    [DataRow("playerState")]
    [DataRow("playerGrabPlayer")]
    [DataRow("playerMoveTo")]
    [DataRow("dashTrail")]
    [DataRow("chat")]
    [DataRow("emote")]
    [DataRow("audioPlay")]
    [DataRow("netemoji")]
    public void DeliversPlayerFrames(string typeId)
    {
        Assert.IsTrue(CelesteNetRelayFilter.IsDeliverable(typeId, Shared));
    }

    [TestMethod]
    [DataRow("keepAlive")]
    [DataRow("tickRate")]
    [DataRow("ready")]
    [DataRow("dcReason")]
    [DataRow("stringMap")]
    [DataRow("slimMap")]
    [DataRow("channelList")]
    [DataRow("channelMove")]
    [DataRow("session")]
    [DataRow("sessionReq")]
    [DataRow("serverStatus")]
    [DataRow("clientInfo")]
    [DataRow("filterList")]
    [DataRow("mapModInfo")]
    [DataRow("modRec")]
    public void DropsSessionLocalFramesOnDelivery(string typeId)
    {
        // A teammate's channel list or string map would overwrite this client's own server state.
        Assert.IsFalse(CelesteNetRelayFilter.IsDeliverable(typeId, Shared));
    }

    [TestMethod]
    public void RemotePlayerInfoIsNeverDeliveredRaw()
    {
        // The exception that is not delivered: the bridge builds the record from MiaoNet (same id,
        // same name) instead of passing on the sender's own record, which is session state there.
        Assert.IsFalse(CelesteNetRelayFilter.IsDeliverable("playerInfo", Shared));
    }

    #endregion

    [TestMethod]
    public void UpstreamSourceListCoversBothCelesteNetAssemblies()
    {
        CollectionAssert.AreEquivalent(
            new[] { Shared, Client },
            new List<string>(CelesteNetRelayFilter.UpstreamSources)
        );
    }

    [TestMethod]
    public void PlayerFramesAreNotSessionLocal()
    {
        // Guards the two sets against drifting into each other.
        Assert.DoesNotContain("playerFrame", CelesteNetRelayFilter.SessionLocalTypeIds);
        Assert.DoesNotContain("playerGraphics", CelesteNetRelayFilter.SessionLocalTypeIds);
        Assert.DoesNotContain("playerState", CelesteNetRelayFilter.SessionLocalTypeIds);
        Assert.DoesNotContain("chat", CelesteNetRelayFilter.SessionLocalTypeIds);
        Assert.Contains("ready", CelesteNetRelayFilter.SessionLocalTypeIds);
        Assert.Contains("channelList", CelesteNetRelayFilter.SessionLocalTypeIds);
    }
}
