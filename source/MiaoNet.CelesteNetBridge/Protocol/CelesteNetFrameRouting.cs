using System;
using System.Collections.Generic;

namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>What the bridge should do with one frame the local real client produced.</summary>
public enum CelesteNetFrameLane : byte
{
    /// <summary>Do not send it: it describes this client's own connection or its own player record.</summary>
    Drop = 0,

    /// <summary>Send it on the legacy bridge NetMod, where every peer can receive it.</summary>
    ThirdParty = 1,

    /// <summary>Send it on the player stream this client declares.</summary>
    PlayerStream = 2,
}

/// <summary>
/// Splits the frames the local real client produces between the legacy bridge NetMod and the one
/// synthesis stream this client always declares while the bridge is on.
/// </summary>
/// <remarks>
/// Frames that carry a bound reference to the sender's <c>playerInfo</c> have to travel on the same
/// stream as that player's identity frames: a receiver that never sees the identity frames cannot
/// resolve the reference and drops the CelesteNet connection. Third-party frames, and upstream frames
/// that carry no player reference (chat), stay on the legacy stream so peers that never declare the
/// player stream keep receiving them exactly as before.
/// </remarks>
public static class CelesteNetFrameRouting
{
    /// <summary>CelesteNet player-pipeline type ids that must stay together with the player identity.</summary>
    public static readonly IReadOnlySet<string> PlayerStreamTypeIds =
        new HashSet<string>(StringComparer.Ordinal) {
            "playerInfo",
            "playerGraphics",
            "playerState",
            "playerFrame",
            "playerGrabPlayer",
            "audioPlay",
            "dashTrail",
            "emote",
        };

    /// <summary>
    /// Chooses the stream for one local frame.
    /// </summary>
    /// <param name="typeId">The frame's type id.</param>
    /// <param name="declaringSource">Assembly that declares the DataType, as the local client knows it.</param>
    /// <param name="aboutLocalPlayer">Whether a <c>playerInfo</c> describes this client's own player.</param>
    public static CelesteNetFrameLane Classify(
        string typeId,
        string? declaringSource,
        bool aboutLocalPlayer
    ) {
        ArgumentNullException.ThrowIfNull(typeId);

        if (!CelesteNetRelayFilter.IsRelayable(typeId, declaringSource, aboutLocalPlayer))
            return CelesteNetFrameLane.Drop;

        bool thirdParty = declaringSource is null
            || !CelesteNetRelayFilter.UpstreamSources.Contains(declaringSource);

        return thirdParty || !PlayerStreamTypeIds.Contains(typeId)
            ? CelesteNetFrameLane.ThirdParty
            : CelesteNetFrameLane.PlayerStream;
    }
}
