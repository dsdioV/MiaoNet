using MiaoNet.CelesteNetBridge;

namespace MiaoNet.UnitTest;

/// <summary>
/// The bridge draws no CelesteNet name tag of its own; MiaoNet draws the name instead. Whatever a mod
/// wrote into CelesteNet's tag has to survive that handover, so the difference between the drawn text
/// and CelesteNet's own name is what gets appended to the MiaoNet name.
/// </summary>
[TestClass]
public class CelesteNetNameTagSuffixTests {

    [TestMethod]
    public void AppendsWhatAModAdded() {
        Assert.AreEqual("  [1]", CelesteNetNameTagSuffix.Append("Madeline  [1]", "Madeline", out bool replaced));
        Assert.IsFalse(replaced);
    }

    [TestMethod]
    public void AppendsNothingWhenNoModTouchedTheTag() {
        Assert.IsNull(CelesteNetNameTagSuffix.Append("Madeline", "Madeline", out bool replaced));
        Assert.IsFalse(replaced);
    }

    [TestMethod]
    public void FollowsAModThatRewroteItsOwnSuffix() {
        // PvPDash replaces "  [1]" with "  [2]" through a regex, so only the last state matters.
        Assert.AreEqual("  [2]", CelesteNetNameTagSuffix.Append("Madeline  [2]", "Madeline", out _));
    }

    [TestMethod]
    public void ReportsAReplacedNameInsteadOfMergingIt() {
        Assert.IsNull(CelesteNetNameTagSuffix.Append("Someone Else", "Madeline", out bool replaced));
        Assert.IsTrue(replaced);
    }

    [TestMethod]
    public void IgnoresEmptyTextOnEitherSide() {
        Assert.IsNull(CelesteNetNameTagSuffix.Append("", "Madeline", out bool replaced));
        Assert.IsFalse(replaced);

        Assert.IsNull(CelesteNetNameTagSuffix.Append("Madeline  [1]", "", out replaced));
        Assert.IsFalse(replaced);

        Assert.IsNull(CelesteNetNameTagSuffix.Append(null, "Madeline", out replaced));
        Assert.IsFalse(replaced);
    }

    [TestMethod]
    public void KeepsUnicodeAndEmojiInTheSuffix() {
        Assert.AreEqual(" ❤", CelesteNetNameTagSuffix.Append("Madeline ❤", "Madeline", out _));
    }
}
