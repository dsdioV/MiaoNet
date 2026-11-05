namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// Orders the frames of one remote player on their way into the local real client.
/// </summary>
/// <remarks>
/// A CelesteNet ghost cannot exist until the client has the player's <c>playerInfo</c> (every player
/// frame carries a bound reference to it), then <c>playerGraphics</c> and a <c>playerState</c> for the
/// current area. The sending client does not produce them in that order: the state goes out on level
/// load before the graphics, and player frames stream at tick rate the whole time. This gate remembers
/// which of the two ghost-birth frames have gone through and holds back player frames until both have,
/// so the client never reads a bound reference it cannot resolve.
/// </remarks>
public sealed class CelesteNetRemoteFeed
{
    public const string PlayerFrameTypeId = "playerFrame";

    public const string PlayerGraphicsTypeId = "playerGraphics";

    public const string PlayerStateTypeId = "playerState";

    private byte[]? heldFrame;

    /// <summary>Whether a <c>playerGraphics</c> frame has been let through.</summary>
    public bool GraphicsSeen { get; private set; }

    /// <summary>Whether a <c>playerState</c> frame has been let through.</summary>
    public bool StateSeen { get; private set; }

    /// <summary>Whether the ghost can be created, so player frames may flow.</summary>
    public bool IsGhostReady => GraphicsSeen && StateSeen;

    /// <summary>Whether a player frame is waiting for the two ghost-birth frames.</summary>
    public bool HasHeldFrame => heldFrame is not null;

    /// <summary>
    /// Classifies one deliverable remote frame: whether to send it now, or to hold it until the ghost
    /// can be born.
    /// </summary>
    public CelesteNetRemoteFeedAction Accept(string typeId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(typeId);
        ArgumentNullException.ThrowIfNull(payload);

        switch (typeId)
        {
            case PlayerGraphicsTypeId:
                GraphicsSeen = true;
                return CelesteNetRemoteFeedAction.Send;

            case PlayerStateTypeId:
                StateSeen = true;
                return CelesteNetRemoteFeedAction.Send;

            case PlayerFrameTypeId:
                if (!IsGhostReady) {
                    // Only the newest frame is worth keeping: the older positions are already stale.
                    heldFrame = payload;
                    return CelesteNetRemoteFeedAction.Hold;
                }

                return CelesteNetRemoteFeedAction.Send;

            default:
                return CelesteNetRemoteFeedAction.Send;
        }
    }

    /// <summary>
    /// The player frame that was held back once both ghost-birth frames are through, or null. Clears
    /// the hold either way.
    /// </summary>
    public byte[]? TakeHeldFrame()
    {
        if (!IsGhostReady)
            return null;

        byte[]? frame = heldFrame;
        heldFrame = null;
        return frame;
    }
}

/// <summary>What the bridge should do with one remote frame.</summary>
public enum CelesteNetRemoteFeedAction
{
    /// <summary>Send it to the local real client now.</summary>
    Send,

    /// <summary>Keep it until the ghost-birth frames have gone through.</summary>
    Hold,
}
