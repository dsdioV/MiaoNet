using MiaoNet.Shared;

namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Asks every peer on the player stream for its identity frames: playerGraphics and playerState, then
/// the appearance keyframe when one is known, in that order. Needed because CelesteNet only sends
/// graphics when they change, so a player who joins after us never receives ours and their ghost stays
/// hidden until we change sprites.
/// <para>
/// Empty on the wire: who asks is the MiaoNet envelope, and the answer is the cached frames themselves.
/// </para>
/// </summary>
internal sealed class PacketBridgeIdentityRequest : IContextualPacket<PacketBridgeIdentityRequest> {
    public void Serialize(ref RefBinaryWriter writer, IPacketSerializationContext context) {
    }

    public static PacketBridgeIdentityRequest Deserialize(
        ref RefBinaryReader reader,
        IPacketSerializationContext context
    )
        => new();
}
