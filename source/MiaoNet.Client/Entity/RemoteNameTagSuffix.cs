namespace Celeste.Mod.MiaoNet;

/// <summary>
/// Text a mod added to a remote player's name, when something else decides what the name looks like.
/// <para>
/// While the CelesteNet bridge owns the players, it also owns the name tags, so a mod that decorates
/// a name by writing into CelesteNet's tag (PvPDash appends the player's team, for example) would
/// have its decoration dropped. The bridge installs a resolver here and MiaoNet appends whatever it
/// gets to the end of the name it draws itself, which keeps MiaoNet's prefix, avatar emoji, colours
/// and font. Without a resolver nothing is appended.
/// </para>
/// </summary>
public static class RemoteNameTagSuffix
{
    /// <summary>
    /// Key for the local player's own tag, which has no remote player id. MiaoNet ids are
    /// non-negative, so this cannot collide with one.
    /// </summary>
    public const int SelfKey = int.MinValue;

    private static Func<int, string?>? resolver;

    public static void SetResolver(Func<int, string?>? value) => resolver = value;

    /// <summary>What to append after the name of the given player, or null for nothing.</summary>
    public static string? Get(int playerId) => resolver?.Invoke(playerId) ?? null;
}
