namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// Decides whether one local CelesteNet session should ask its peers for their identity frames, and
/// keeps the question from being repeated on every keepAlive heartbeat.
/// </summary>
public sealed class CelesteNetIdentityRequester
{
    /// <summary>Whether an identity request already went out for this connection.</summary>
    public bool AlreadyRequested { get; private set; }

    /// <summary>
    /// Asks once, and only when the real client finished its startup sequence (its reference table
    /// exists by then) and MiaoNet can carry the question. Stays false after the first ask.
    /// </summary>
    public bool ShouldRequest(bool sessionReady, bool miaoNetConnected)
        => sessionReady && miaoNetConnected && !AlreadyRequested;

    public void MarkRequested() => AlreadyRequested = true;

    /// <summary>MiaoNet went away: the peers we asked are gone, so a later connection asks again.</summary>
    public void Reset() => AlreadyRequested = false;
}
