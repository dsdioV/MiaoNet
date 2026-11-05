using System;
using System.Collections.Generic;

namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>
/// Decides which frames the local real client produces are worth sending over MiaoNet, and which frames
/// arriving over MiaoNet may be pushed into the local real client.
/// </summary>
/// <remarks>
/// CelesteNet's own DataTypes describe two different things. The connection itself (handshake, tick
/// rate, string maps, channels, sessions) is rebuilt by each fake server, so relaying it would give
/// every player one player's server state. The frames about players (player frames, graphics, state,
/// chat, emotes) are the ones the bridge's player pipeline needs. Which assembly declares a DataType is
/// read from the local client's DataContext, so a CelesteNet update that adds a type needs no change
/// here: unknown CelesteNet types are relayed unless they are listed as session-local.
/// </remarks>
public static class CelesteNetRelayFilter
{
    /// <summary>The DataID of <c>DataPlayerInfo</c>.</summary>
    public const string PlayerInfoTypeId = "playerInfo";

    /// <summary>Assemblies that declare CelesteNet's own DataTypes.</summary>
    public static readonly IReadOnlySet<string> UpstreamSources =
        new HashSet<string>(StringComparer.Ordinal) { "CelesteNet.Shared", "CelesteNet.Client" };

    /// <summary>
    /// CelesteNet DataTypes that only make sense between one client and its own server.
    /// </summary>
    /// <remarks>
    /// Grouped by what they carry:
    /// <list type="bullet">
    /// <item>the connection: keepAlive, pings, tick rate, ready, disconnect reason, the per-connection
    /// string and slim type maps, and the UDP upgrade handshake;</item>
    /// <item>server-owned session state: channels, sessions, server status, client info, commands,
    /// filters, map/mod checksums and mod recommendations;</item>
    /// <item>the local player's own identity record. A <c>playerInfo</c> about somebody else is the
    /// opposite of session-local: it is what makes that player exist for this client, so
    /// <see cref="IsRelayable"/> treats it separately.</item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlySet<string> SessionLocalTypeIds = new HashSet<string>(StringComparer.Ordinal)
    {
        // The connection itself.
        "keepAlive",
        "pingRequest",
        "pingReply",
        "tickRate",
        "ready",
        "dcReason",
        "stringMap",
        "slimMap",
        "udpInfo",
        // Server-owned session state.
        "channelList",
        "channelMove",
        "session",
        "sessionReq",
        "serverStatus",
        "clientInfo",
        "clientInfoReq",
        "conInfo",
        "commandList",
        "filterList",
        "mapModInfo",
        "mapModInfoReq",
        "modRec",
        // The local player's own record; a remote player's is handled in IsRelayable.
        PlayerInfoTypeId,
    };

    /// <param name="typeId">The frame's type id.</param>
    /// <param name="dataSource">
    /// Assembly that declares the DataType, or null when the local client does not know the type at all.
    /// Unknown types are relayed, because dropping a frame nobody understands is the worse guess.
    /// </param>
    /// <param name="aboutLocalPlayer">
    /// For a <c>playerInfo</c> frame, whether it describes the player of this client. The local player's
    /// own record is session state, while a remote player's record has to travel. Ignored for every
    /// other type id.
    /// </param>
    public static bool IsRelayable(
        string typeId,
        string? dataSource,
        bool aboutLocalPlayer = true
    )
    {
        ArgumentNullException.ThrowIfNull(typeId);

        bool thirdParty = dataSource is null || !UpstreamSources.Contains(dataSource);

        return thirdParty
            || !SessionLocalTypeIds.Contains(typeId)
            || (typeId == PlayerInfoTypeId && !aboutLocalPlayer);
    }

    /// <summary>
    /// Decides which frames arriving over MiaoNet may be pushed into the local real client. Each fake
    /// server owns its own connection and session state, so a teammate's session-local frames would make
    /// this client believe it is on their server.
    /// </summary>
    /// <remarks>
    /// A remote <c>playerInfo</c> is never delivered raw: the id and name in it are the sender's, and
    /// the bridge synthesises the record from MiaoNet instead (see <c>CelesteNetBridgeEntry</c>).
    /// Unknown types are delivered, so a mod whose data types this client has not seen yet still works.
    /// </remarks>
    /// <param name="typeId">The frame's type id.</param>
    /// <param name="dataSource">
    /// Assembly that declares the DataType, or null when the local client does not know the type at all.
    /// </param>
    public static bool IsDeliverable(string typeId, string? dataSource)
    {
        ArgumentNullException.ThrowIfNull(typeId);

        bool thirdParty = dataSource is null || !UpstreamSources.Contains(dataSource);

        return thirdParty || !SessionLocalTypeIds.Contains(typeId);
    }
}
