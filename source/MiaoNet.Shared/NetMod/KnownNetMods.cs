namespace MiaoNet.Shared;

/// <summary>
/// Names of the NetMods the CelesteNet bridge declares, shared so the client and the bridge cannot
/// drift apart on a string literal.
/// </summary>
/// <remarks>
/// A NetMod's <c>ProtocolVersion</c> says whether the bytes of its packets are the same. Changing what
/// a stream means must change its name instead of bumping the version: delivery matches both, and the
/// other side cannot tell a semantic change from the version alone.
/// </remarks>
public static class KnownNetMods
{
    /// <summary>The legacy bridge stream: every bridge peer receives it, whatever player stream it declares.</summary>
    public const string LegacyBridgeName = "MiaoNetCelesteNetBridge";

    /// <summary>
    /// Synthesis stream: the low-frequency player pipeline frames (graphics, state) plus appearance
    /// keyframes. The receiving side builds the per-tick <c>playerFrame</c> locally.
    /// </summary>
    public const string CelesteNetPlayerSynth = "MiaoNetCelesteNetBridge.PlayerSynth";

    /// <summary>Wire format version of the player stream.</summary>
    public const ushort CelesteNetPlayerProtocolVersion = 1;
}
