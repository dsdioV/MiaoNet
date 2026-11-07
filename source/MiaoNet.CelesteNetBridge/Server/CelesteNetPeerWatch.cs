using System.Collections.Generic;

namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// Remembers when each remote player last sent a frame, so a peer that stops sending can be treated as
/// gone.
/// </summary>
/// <remarks>
/// CelesteNet's client has no timeout of its own: a ghost whose player went quiet stays where it was,
/// keeps its name, and can still be grabbed. That is right behind a real server, which knows when a
/// connection drops, but the bridge has no server to tell it. A player who turns the bridge off, loses
/// the connection or crashes simply stops talking, so silence is the only signal there is. MiaoNet sends
/// a player frame at tick rate for every player on the map, so seconds of silence mean the player left
/// rather than that they are standing still.
/// </remarks>
public sealed class CelesteNetPeerWatch {
    private readonly object gate = new();

    private readonly Dictionary<uint, double> lastSeen = [];

    /// <summary>How many players are being watched right now.</summary>
    public int Count {
        get {
            lock (gate)
                return lastSeen.Count;
        }
    }

    /// <summary>Records that this player just sent something.</summary>
    public void Saw(uint id, double now) {
        lock (gate)
            lastSeen[id] = now;
    }

    /// <summary>Stops watching a player, e.g. because MiaoNet already reported them as gone.</summary>
    public bool Forget(uint id) {
        lock (gate)
            return lastSeen.Remove(id);
    }

    public void Clear() {
        lock (gate)
            lastSeen.Clear();
    }

    /// <summary>
    /// Returns, and stops watching, every player that has been silent for longer than
    /// <paramref name="timeout"/> seconds.
    /// </summary>
    /// <param name="now">Current time in seconds, from any monotonic clock.</param>
    /// <param name="timeout">How long a player may stay silent before being reported as gone.</param>
    public uint[] TakeExpired(double now, double timeout) {
        List<uint>? expired = null;

        lock (gate) {
            if (lastSeen.Count == 0)
                return [];

            foreach (KeyValuePair<uint, double> entry in lastSeen) {
                if (now - entry.Value <= timeout)
                    continue;

                (expired ??= []).Add(entry.Key);
            }

            if (expired is null)
                return [];

            foreach (uint id in expired)
                lastSeen.Remove(id);
        }

        return [.. expired];
    }
}
