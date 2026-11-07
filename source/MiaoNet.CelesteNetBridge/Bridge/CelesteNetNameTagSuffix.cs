namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Works out what a mod appended to a CelesteNet name tag.
/// <para>
/// CelesteNet draws a tag by passing <c>GhostNameTag.Name</c> to
/// <c>CelesteNetClientFont.DrawOutline</c> (see <c>GhostNameTag.Render</c> in the upstream source).
/// Mods do not have an API for decorating a name: they just write into that public field, usually by
/// appending (PvPDash adds <c>"  [1]"</c> for the player's team) and sometimes by replacing it
/// outright. The bridge takes the drawing over, so it has to hand the appended part back to MiaoNet,
/// which draws the name with its own font, colours and avatar emoji.
/// </para>
/// <para>
/// The baseline is CelesteNet's own <c>DisplayName</c> for that player, which the bridge itself
/// synthesises from the MiaoNet name, so the difference is exactly what the mod added.
/// </para>
/// </summary>
public static class CelesteNetNameTagSuffix {

    /// <summary>
    /// Returns what <paramref name="drawn"/> adds after <paramref name="baseline"/>, or null when
    /// there is nothing to add.
    /// </summary>
    /// <remarks>
    /// A tag whose text no longer starts with the baseline was replaced rather than decorated. There
    /// is no safe way to merge that into MiaoNet's own rendering, so it is reported as
    /// <paramref name="replaced"/> and left alone: MiaoNet keeps drawing its own name.
    /// </remarks>
    public static string? Append(string? drawn, string? baseline, out bool replaced) {
        replaced = false;

        if (string.IsNullOrEmpty(drawn) || string.IsNullOrEmpty(baseline))
            return null;

        if (!drawn.StartsWith(baseline, StringComparison.Ordinal)) {
            replaced = true;
            return null;
        }

        return drawn.Length == baseline.Length ? null : drawn[baseline.Length..];
    }
}
