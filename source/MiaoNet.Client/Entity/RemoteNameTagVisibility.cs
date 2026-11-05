namespace Celeste.Mod.MiaoNet;

/// <summary>
/// Whether something else decides that a remote player's name may be drawn. The CelesteNet bridge
/// installs a resolver while it owns the players, so MiaoNet's name tag mirrors CelesteNet's own
/// visibility rules (MadHunt hides other players' names during a round, for example) instead of
/// always drawing. Without a resolver MiaoNet draws its tags as usual.
/// </summary>
public static class RemoteNameTagVisibility
{
    private static Func<int, bool>? resolver;

    public static void SetResolver(Func<int, bool>? value) => resolver = value;

    public static bool ShouldShow(int playerId) => resolver?.Invoke(playerId) ?? true;
}
