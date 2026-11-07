namespace Celeste.Mod.MiaoNet;

public sealed partial class MainComponent
{
    /// <summary>
    /// How long a player may be without a CelesteNet ghost before MiaoNet draws its own body for them.
    /// CelesteNet's ghost appears one or two packets after a player joins, so drawing immediately would
    /// flash a second body; a teammate who does not run CelesteNet at all never gets one and shows up
    /// after this delay.
    /// </summary>
    private const float FallbackGhostDelay = 0.75f;

    /// <summary>Seconds each player has been waiting for a CelesteNet ghost, while the pipeline is on.</summary>
    private readonly Dictionary<int, float> fallbackGhostTimers = new();

    /// <summary>
    /// Keeps exactly one body and one name tag per remote player.
    /// <para>
    /// CelesteNet only has a ghost for players whose frames reach it: a teammate without CelesteNet, or
    /// one whose bridge is off, sends MiaoNet's own frames only and would be invisible in the full
    /// pipeline. MiaoNet's ghost therefore stays as their fallback and is dropped as soon as
    /// CelesteNet's ghost for the same player exists, so a mixed party still sees everybody.
    /// </para>
    /// <para>
    /// The name tag has exactly one owner: MiaoNet's ghost draws its own tag, so the remote tag only
    /// exists while that player is (or is about to be) drawn by CelesteNet.
    /// </para>
    /// </summary>
    private void ReconcileRemotePlayerBodies(Level level)
    {
        bool bridgeActive = MiaoNetModule.IsCelesteNetBridgeActive;

        foreach (OnlinePlayer player in ClientState.Players.Values)
        {
            // Same eligibility as HandleLocationChanging uses for MiaoNet's own ghosts.
            bool inMap = player.State is not null && player.Location.IsInMap;
            if (!inMap)
            {
                fallbackGhostTimers.Remove(player.ID);
                RemoveGhost(level, player.ID, player.Info.ToString());
                RemoveRemoteNameTag(player.ID);
                continue;
            }

            if (bridgeActive && RemotePlayerAnchor.TryGet(player.ID, out _))
            {
                // CelesteNet draws this player: drop MiaoNet's body and follow the real ghost.
                fallbackGhostTimers.Remove(player.ID);
                RemoveGhost(level, player.ID, player.Info.ToString());
                EnsureRemoteNameTag(level, player);
                continue;
            }

            // MiaoNet's own ghost carries its own name tag.
            if (ghosts.ContainsKey(player.ID))
            {
                RemoveRemoteNameTag(player.ID);
                continue;
            }

            if (bridgeActive)
            {
                // Game seconds rather than wall-clock: a CelesteNet ghost is created from a network
                // packet, so a wall-clock timer would keep running while the game is paused and could
                // drop a second body on top of the real one right after the player unpauses.
                float waited = fallbackGhostTimers.GetValueOrDefault(player.ID) + Engine.DeltaTime;
                fallbackGhostTimers[player.ID] = waited;
                if (waited < FallbackGhostDelay)
                {
                    // No body yet, but the tag can already wait for the anchor to resolve.
                    EnsureRemoteNameTag(level, player);
                    continue;
                }
            }

            RemoveRemoteNameTag(player.ID);
            AddFallbackGhost(level, player);
        }

        // A player that left the channel never reaches the loop above.
        foreach (int id in ghosts.Keys.ToArray())
        {
            if (!ClientState.Players.ContainsKey(id))
                RemoveGhost(level, id, $"#{id}");
        }
        foreach (int id in remoteNameTags.Keys.ToArray())
        {
            if (!ClientState.Players.ContainsKey(id))
                RemoveRemoteNameTag(id);
        }
        foreach (int id in fallbackGhostTimers.Keys.ToArray())
        {
            if (!ClientState.Players.ContainsKey(id))
                fallbackGhostTimers.Remove(id);
        }
    }

    private void AddFallbackGhost(Level level, OnlinePlayer player)
    {
        fallbackGhostTimers.Remove(player.ID);
        MiaoNetGhost ghost = new(player, context.ShowAvatar);
        ghosts[player.ID] = ghost;
        level.Add(ghost);
        if (MiaoNetModule.IsCelesteNetBridgeActive)
        {
            // Only worth a line when CelesteNet could have drawn them but did not: that is the
            // symptom to look for when a teammate stays invisible.
            Logger.Log(
                LogLevel.Verbose, LT.MiaoNet,
                $"Created a fallback body for {player.Info}: CelesteNet has no ghost for them yet."
            );
        }
        else
        {
            // With no bridge, MiaoNet's ghost is the only body there is. Nothing went wrong.
            Logger.Debug(LT.MiaoNet, $"Created a body for {player.Info}.");
        }
    }

    private void RemoveGhost(Level level, int playerId, string who)
    {
        if (!ghosts.Remove(playerId, out MiaoNetGhost? ghost))
            return;
        level.CompletelyRemove(ghost);
        Logger.Debug(LT.MiaoNet, $"Removed the body of {who}: its source changed or it left the map.");
    }
}
