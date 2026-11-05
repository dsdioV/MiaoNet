using System.Diagnostics.CodeAnalysis;

namespace Celeste.Mod.MiaoNet;

/// <summary>
/// Resolves the scene entity that renders a remote player.
/// <para>
/// MiaoNet.Client must never reference CelesteNet. While the bridge is active the remote players are
/// CelesteNet's <c>Ghost</c> entities, a type only the bridge assembly knows; the bridge therefore
/// installs a resolver here at runtime. While no bridge runs the resolver stays null and every caller
/// falls back to MiaoNet's own ghost.
/// </para>
/// </summary>
public static class RemotePlayerAnchor
{
    private static Func<int, Entity?>? resolver;

    /// <summary>
    /// Installs, or clears with <c>null</c>, the resolver that maps a MiaoNet player id to the entity
    /// currently rendering that remote player. Only the bridge calls this.
    /// </summary>
    public static void SetResolver(Func<int, Entity?>? value)
        => resolver = value;

    /// <summary>
    /// Gets the entity that renders the remote player with the given MiaoNet id right now, if any.
    /// The entity may appear later: the resolver is asked again on every call.
    /// </summary>
    /// <returns><c>true</c> when an entity is currently available.</returns>
    public static bool TryGet(int playerId, [NotNullWhen(true)] out Entity? entity)
    {
        entity = resolver?.Invoke(playerId);
        return entity is not null;
    }
}
