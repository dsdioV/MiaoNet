using System.Text;

using MiaoNet.CelesteNetBridge.Protocol;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class CelesteNetPlayerFrameSafetyTests
{
    [TestMethod]
    public void AnOversizedBodyIsRejectedInsteadOfTruncated()
    {
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 7,
            HairTexture0 = "characters/player/bangs0",
        };

        // Twelve followers whose ids are long but each still under the string limit. The ids differ,
        // so the encoder cannot use a repeat marker and the body exceeds the 16-bit wire length.
        frame.Followers = new CelesteNetPlayerFrameEntity[12];
        for (int i = 0; i < frame.Followers.Length; i++)
        {
            frame.Followers[i] = new() {
                SpriteID = new string((char)('a' + i), 4000),
                AnimationID = new string((char)('A' + i), 4000),
            };
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => CelesteNetPlayerFrame.Encode(frame));
    }

    [TestMethod]
    public void PeerStringsThatCannotBeWrittenAreRejected()
    {
        // A malformed relayed string must make the frame fail to encode, not corrupt the wire.
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 7,
            HairTexture0 = "\u0001abc",
        };

        Assert.ThrowsExactly<ArgumentException>(() => CelesteNetPlayerFrame.Encode(frame));
    }

    [TestMethod]
    public void APlayerFrameWithMalformedUtf8IsNotHandled()
    {
        CelesteNetPlayerFrame frame = new() {
            PlayerID = 7,
            HairTexture0 = "ab",
        };

        byte[] payload = CelesteNetPlayerFrame.Encode(frame);
        int at = IndexOf(payload, [(byte)'a', (byte)'b', 0]);
        Assert.IsGreaterThanOrEqualTo(0, at, "the test needs the hair texture in the payload");
        payload[at] = 0xFF;

        Assert.IsFalse(CelesteNetPlayerFrame.TryDecode(payload, out _));
    }

    [TestMethod]
    public void AGraphicsFrameWithMalformedUtf8IsNotHandled()
    {
        byte[] payload = CelesteNetTestPayloads.PlayerGraphics(["idle"], 1, ["ab"], 42, 0);
        int at = IndexOf(payload, [(byte)'a', (byte)'b', 0]);
        Assert.IsGreaterThanOrEqualTo(0, at, "the test needs the hair texture in the payload");
        payload[at] = 0xFF;

        Assert.IsFalse(CelesteNetPlayerGraphics.TryDecode(payload, out _));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }
}
