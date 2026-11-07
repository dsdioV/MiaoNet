using MiaoNet.Shared;

namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Says that the sender stopped rendering remote players through CelesteNet, because its bridge is
/// being turned off or the game is closing MiaoNet.
/// <para>
/// Peers cannot work this out on their own: the sender stays connected to MiaoNet, so no leave event
/// fires, and it simply stops producing CelesteNet frames. CelesteNet's client has no timeout on a
/// ghost either, so without this packet the last ghost it built would stay on screen, frozen in place
/// and still grabbable -- grabbing is a purely local behaviour in the real client.
/// </para>
/// <para>
/// Empty on the wire: who is leaving is the MiaoNet envelope, and the answer on the other side is the
/// same dead <c>playerInfo</c> any other leave produces.
/// </para>
/// </summary>
internal sealed class PacketBridgeLeave : IContextualPacket<PacketBridgeLeave> {
    public void Serialize(ref RefBinaryWriter writer, IPacketSerializationContext context) {
    }

    public static PacketBridgeLeave Deserialize(
        ref RefBinaryReader reader,
        IPacketSerializationContext context
    )
        => new();
}
