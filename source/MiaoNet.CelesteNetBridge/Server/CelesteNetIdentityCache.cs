namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// The last identity frames the local real client produced, kept so a peer that joined late can be told
/// who we are. CelesteNet only sends graphics when they change, not periodically, so without this a
/// player who joined after us would never receive our sprites and their ghost would stay hidden until
/// we happened to change them.
/// </summary>
/// <remarks>
/// A player record is deliberately not cached. The receiver discards every <c>playerInfo</c> that
/// arrives from a peer and synthesises the remote record from MiaoNet before that player's first frame,
/// so an answer that carried it would only put dead bytes on the wire.
/// </remarks>
public sealed class CelesteNetIdentityCache
{
    public const string PlayerInfoTypeId = "playerInfo";
    public const string PlayerGraphicsTypeId = "playerGraphics";
    public const string PlayerStateTypeId = "playerState";

    private readonly byte[]?[] frames = new byte[]?[2];

    /// <summary>Whether every identity frame a ghost cannot be born without is known.</summary>
    public bool IsComplete => frames[0] is not null && frames[1] is not null;

    /// <summary>Stores the newest payload of one identity type.</summary>
    /// <returns>
    /// False when the type is not part of the identity, so callers can pass every frame they see.
    /// </returns>
    public bool Remember(string typeId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(typeId);
        ArgumentNullException.ThrowIfNull(payload);

        int index = IndexOf(typeId);
        if (index < 0)
            return false;

        frames[index] = payload;
        return true;
    }

    /// <summary>
    /// The identity frames in the order the real client can bind them: graphics, then state. Responding
    /// consumes nothing, so asking twice is harmless.
    /// </summary>
    public bool TryBuildResponse(out byte[][] frames)
    {
        if (!IsComplete)
        {
            frames = [];
            return false;
        }

        frames = [this.frames[0]!, this.frames[1]!];
        return true;
    }

    private static int IndexOf(string typeId)
        => typeId switch
        {
            PlayerGraphicsTypeId => 0,
            PlayerStateTypeId => 1,
            _ => -1,
        };
}
