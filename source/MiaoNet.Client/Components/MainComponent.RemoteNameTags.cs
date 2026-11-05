using MiaoNet.Shared;

namespace Celeste.Mod.MiaoNet;

public sealed partial class MainComponent
{
    /// <summary>
    /// Name tags of the remote players while CelesteNet renders the players themselves, together with
    /// the level each tag was added to. Keyed by MiaoNet player id, exactly like <c>ghosts</c> in the
    /// default pipeline.
    /// <para>
    /// The level is kept because a tag added and removed inside the same packet batch has no
    /// <c>Scene</c> yet (Monocle assigns it in <c>Scene.BeforeUpdate</c>), so <c>Entity.RemoveSelf</c>
    /// would silently do nothing; <c>Scene.CompletelyRemove</c> handles that case.
    /// </para>
    /// </summary>
    private readonly Dictionary<int, (Level Level, GhostNameTag Tag)> remoteNameTags = new();

    /// <summary>
    /// Makes sure a remote player has MiaoNet's name tag. The tag is created even when CelesteNet's
    /// ghost does not exist yet: it resolves its target lazily and draws nothing until the ghost shows
    /// up. A player drawn by MiaoNet's own ghost must not get one, that ghost carries its own tag.
    /// </summary>
    private void EnsureRemoteNameTag(Level level, OnlinePlayer player)
    {
        if (remoteNameTags.TryGetValue(player.ID, out var existing))
        {
            bool visible = IsRemoteTagVisible(player);
            if (existing.Tag.Visible != visible)
                Logger.Debug(LT.MiaoNet, $"Name tag for {player.Info} visible: {visible}.");
            existing.Tag.Visible = visible;
            return;
        }

        // A player without an initial state or outside a map has no body to follow yet.
        if (player.State is null || !player.Location.IsInMap)
        {
            RemoveRemoteNameTag(player.ID);
            return;
        }

        GhostNameTag tag = new(player, () => ResolveRemoteNameTagTarget(player.ID), context.ShowAvatar)
        {
            Visible = IsRemoteTagVisible(player)
        };
        remoteNameTags[player.ID] = (level, tag);
        level.Add(tag);
        Logger.Debug(LT.MiaoNet, $"Created a name tag for {player.Info}.");
    }

    /// <summary>
    /// MiaoNet's own ghost hides the tag of a watching player, so the tag that follows CelesteNet's
    /// ghost has to do the same. Dead players keep their tag, exactly like the default pipeline.
    /// <para>
    /// While the CelesteNet bridge owns the players, CelesteNet decides which names it would draw
    /// (its own tag logic plus any mod that hides names), and MiaoNet only draws those.
    /// </para>
    /// </summary>
    private static bool IsRemoteTagVisible(OnlinePlayer player)
        => !player.GlobalFlags.HasFlag(PlayerGlobalFlags.Watching)
            && RemoteNameTagVisibility.ShouldShow(player.ID);

    /// <summary>
    /// Looks up the entity that renders a remote player right now: MiaoNet's own ghost when it exists,
    /// otherwise CelesteNet's ghost through the anchor seam.
    /// </summary>
    private Entity? ResolveRemoteNameTagTarget(int playerId)
    {
        if (TryGetGhostTarget(playerId, out Entity? target))
            return target;
        return null;
    }

    /// <summary>
    /// Removes a remote player's tag through the level it was added to, which also works for a tag
    /// that Monocle has not put into a scene yet or for a level that is already ending.
    /// </summary>
    private void RemoveRemoteNameTag(int playerId)
    {
        if (!remoteNameTags.Remove(playerId, out var entry))
            return;
        entry.Level.CompletelyRemove(entry.Tag);
    }

    private void CleanUpRemoteNameTags()
    {
        foreach (var entry in remoteNameTags.Values)
            entry.Level.CompletelyRemove(entry.Tag);
        remoteNameTags.Clear();
    }
}
