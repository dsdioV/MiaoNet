namespace MiaoNet.CelesteNetBridge.Protocol;

/// <summary>
/// Remembers the newest payload of every third-party data type we sent that declared a bound
/// reference, so it can be replayed to a player who joins later.
/// </summary>
/// <remarks>
/// This is the bridge's version of the real server's bound reference storage. The real server keeps
/// the stored blobs per player and replays them in <c>ResendPlayerStates</c>
/// (<c>CelesteNet.Server/CelesteNetPlayerSession.cs</c>), which is why, on CelesteNet, a late joiner
/// receives e.g. another player's Foxeline tail settings even though that player only sent them once
/// when connecting. Only data that declares a bound reference is stored: event data such as a
/// MadHunt round start does not, exactly like on the real server.
/// </remarks>
public sealed class CelesteNetBoundRefCache {

    private readonly Dictionary<string, byte[]> frames = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Stores the newest payload of one data type, replacing the previous one.</summary>
    public void Remember(string typeId, byte[] payload) {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        ArgumentNullException.ThrowIfNull(payload);

        lock (gate)
            frames[typeId] = payload;
    }

    /// <summary>Snapshots the payloads to replay, one per data type.</summary>
    public byte[][] Snapshot() {
        lock (gate)
            return [.. frames.Values];
    }

    /// <summary>How many data types are currently stored.</summary>
    public int Count {
        get {
            lock (gate)
                return frames.Count;
        }
    }

    public void Clear() {
        lock (gate)
            frames.Clear();
    }
}
