using MiaoNet.CelesteNetBridge.Protocol;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetBoundRefTests
{
    [TestMethod]
    public void PlayerInfoDeclaresNoBoundReference()
    {
        // DataPlayerInfo carries MetaRef ("ref"), which is an identity, not a bound reference.
        Assert.IsFalse(CelesteNetFrameMeta.CarriesBoundRef(CelesteNetDataTypes.PlayerInfo(7, "Player")));
    }

    [TestMethod]
    public void ABoundRefMetaIsFound()
    {
        byte[] payload = CelesteNetDataTypes.Encode(
            0, "Foxeline/TailData", [9], [("bound", [1, 7, 1])]
        );

        Assert.IsTrue(CelesteNetFrameMeta.CarriesBoundRef(payload));
    }

    [TestMethod]
    public void LocallySynthesisedFramesDeclareNoBoundReference()
    {
        Assert.IsFalse(CelesteNetFrameMeta.CarriesBoundRef(CelesteNetDataTypes.Ready()));
        Assert.IsFalse(CelesteNetFrameMeta.CarriesBoundRef(CelesteNetDataTypes.KeepAlive()));
    }

    [TestMethod]
    public void AMetaThatIsNotABoundReferenceIsIgnored()
    {
        byte[] payload = CelesteNetDataTypes.Encode(
            0, "custom/SomeState", [1, 2, 3], [("sid", [4, 5])]
        );

        Assert.IsFalse(CelesteNetFrameMeta.CarriesBoundRef(payload));
    }

    [TestMethod]
    public void ABoundRefMetaAfterAnotherMetaIsStillFound()
    {
        // Foxeline and the other skin mods declare MetaPlayerPrivateState/MetaPlayerPublicState first
        // and MetaBoundRef second, so the reader has to walk past metas it does not care about.
        byte[] payload = CelesteNetDataTypes.Encode(
            0, "Foxeline/TailData", [9],
            [("playerPrivSt", [1]), ("bound", [7, 1])]
        );

        Assert.IsTrue(CelesteNetFrameMeta.CarriesBoundRef(payload));
    }

    [TestMethod]
    public void TruncatedPayloadsAreNeverABoundReferenceAndNeverThrow()
    {
        byte[] payload = CelesteNetDataTypes.Encode(
            0, "Foxeline/TailData", [9], [("playerPrivSt", [1]), ("bound", [7, 1])]
        );
        for (int length = 0; length < payload.Length; length++)
            Assert.IsFalse(CelesteNetFrameMeta.CarriesBoundRef(payload.AsSpan(0, length)));
    }

    [TestMethod]
    public void TheCacheKeepsTheNewestPayloadPerType()
    {
        CelesteNetBoundRefCache cache = new();
        cache.Remember("Foxeline/TailData", [1, 2, 3]);
        cache.Remember("Foxeline/TailData", [4, 5]);
        cache.Remember("Hateline_PlayerHat", [6]);

        Assert.AreEqual(2, cache.Count);

        byte[][] frames = cache.Snapshot();
        Assert.AreEqual(2, frames.Length);
        CollectionAssert.Contains(
            frames.Select(frame => frame.AsSpan().ToArray()).Select(Convert.ToHexString).ToList(),
            "0405"
        );
    }

    [TestMethod]
    public void TheCacheCanBeCleared()
    {
        CelesteNetBoundRefCache cache = new();
        cache.Remember("Foxeline/TailData", [1]);

        cache.Clear();

        Assert.AreEqual(0, cache.Count);
        Assert.IsEmpty(cache.Snapshot());
    }
}
