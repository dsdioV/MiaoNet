using System.Reflection;
using Celeste.Mod.CelesteNet.Client;
using Celeste.Mod.CelesteNet.Client.Entities;
using Microsoft.Xna.Framework;
using Monocle;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Lets MiaoNet draw the remote names that CelesteNet would have drawn itself, without drawing them
/// twice. CelesteNet's <c>GhostNameTag.Render</c> only reaches its draw call when every rule held:
/// non-empty name, on screen or an off-screen mode that still draws, non-zero opacity, and whatever a
/// mod detouring Render decided (MadHunt skips the tag of other players during a round). The IL hook
/// keeps all of that logic, records the player when the draw call is reached, and swaps the call for
/// a no-op, so MiaoNet's tag can be shown for exactly those players.
/// <para>
/// Taking the tag over would also drop whatever mods wrote into <c>GhostNameTag.Name</c>, because
/// that string only ever existed as an argument to the draw call. PvPDash, for example, appends
/// <c>"  [1]"</c> for the player's team. The same hook therefore hands the part a mod added back to
/// MiaoNet through <see cref="Celeste.Mod.MiaoNet.RemoteNameTagSuffix"/>, so MiaoNet keeps drawing
/// the name with its own font, prefix and avatar emoji while the mod's decoration still shows.
/// </para>
/// <para>
/// The hook has to be inside <c>Render</c> rather than around it: a plain detour that returns without
/// calling the original would hide the tag before CelesteNet's own rules (and the mods riding on
/// them) ever run. When the anchor cannot be found the bridge falls back to the previous behaviour:
/// every CelesteNet tag hidden, MiaoNet draws its own tags unconditionally.
/// </para>
/// </summary>
internal sealed class CelesteNetNameTagMirror {

    /// <summary>How long a recorded draw keeps a MiaoNet tag visible: one frame of slack for order.</summary>
    private const ulong VisibleFrames = 2;

    /// <summary>Players whose CelesteNet tag was drawn recently, and the engine frame it was drawn in.</summary>
    private static readonly Dictionary<uint, ulong> drawnAt = [];

    /// <summary>Players already reported once, so the log shows the mirror working without flooding.</summary>
    private static readonly HashSet<uint> reported = [];

    /// <summary>
    /// What a mod appended to each tag, with the frame it was last seen in, keyed like
    /// <see cref="drawnAt"/> but including <see cref="SelfKey"/>.
    /// </summary>
    private static readonly Dictionary<int, (ulong Frame, string Suffix)> suffixes = [];

    /// <summary>Tags whose text a mod replaced instead of decorated, so the log says why it is ignored.</summary>
    private static readonly HashSet<int> replacedText = [];

    private static Func<int, bool>? installedResolver;

    private static Func<int, string?>? installedSuffixResolver;

    private ILHook? hook;

    private Hook? fallback;

    /// <summary>Whether CelesteNet's own rules are mirrored; false means its tags are just hidden.</summary>
    public bool Mirrored => hook is not null;

    public void Install() {
        drawnAt.Clear();
        reported.Clear();
        suffixes.Clear();
        replacedText.Clear();

        if (!TryInstallIlHook()) {
            InstallFallback();
            return;
        }

        installedResolver = ShouldShow;
        Celeste.Mod.MiaoNet.RemoteNameTagVisibility.SetResolver(installedResolver);

        installedSuffixResolver = CurrentSuffix;
        Celeste.Mod.MiaoNet.RemoteNameTagSuffix.SetResolver(installedSuffixResolver);

        Logger.Log(LogLevel.Info, "cnet-bridge", "name tags: mirroring which names CelesteNet would draw");
    }

    public void Uninstall() {
        reported.Clear();
        hook?.Dispose();
        hook = null;
        fallback?.Dispose();
        fallback = null;

        if (installedResolver is not null) {
            Celeste.Mod.MiaoNet.RemoteNameTagVisibility.SetResolver(null);
            installedResolver = null;
        }

        if (installedSuffixResolver is not null) {
            Celeste.Mod.MiaoNet.RemoteNameTagSuffix.SetResolver(null);
            installedSuffixResolver = null;
        }

        drawnAt.Clear();
        suffixes.Clear();
        replacedText.Clear();
    }

    private bool TryInstallIlHook() {
        MethodInfo? render = typeof(GhostNameTag).GetMethod(nameof(GhostNameTag.Render));
        MethodInfo? skip = typeof(CelesteNetNameTagMirror).GetMethod(
            nameof(PassThrough), BindingFlags.NonPublic | BindingFlags.Static
        );
        if (render is null || skip is null)
            return false;

        try {
            hook = new ILHook(render, il => {
                ILCursor cursor = new(il);
                if (!cursor.TryGotoNext(
                        MoveType.Before,
                        instruction => instruction.MatchCall(
                            "Celeste.Mod.CelesteNet.Client.CelesteNetClientFont",
                            nameof(CelesteNetClientFont.DrawOutline)
                        )
                    )
                ) {
                    throw new InvalidOperationException(
                        "the CelesteNetClientFont.DrawOutline call is missing from GhostNameTag.Render"
                    );
                }

                cursor.Remove();
                cursor.Emit(OpCodes.Call, skip);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.EmitDelegate<Action<string, GhostNameTag>>(RecordDrawn);
            });
            return true;
        } catch (Exception e) {
            hook?.Dispose();
            hook = null;
            Logger.Log(
                LogLevel.Warn, "cnet-bridge",
                "could not mirror CelesteNet name tags: " + e.GetType().Name + ": " + e.Message
            );
            return false;
        }
    }

    private void InstallFallback() {
        try {
            fallback = new Hook(
                typeof(GhostNameTag).GetMethod(nameof(GhostNameTag.Render))!,
                new Action<Action<GhostNameTag>, GhostNameTag>((_, _) => { })
            );
            Logger.Log(
                LogLevel.Info, "cnet-bridge",
                "name tags: CelesteNet tags hidden, MiaoNet draws its own"
            );
        } catch (Exception e) {
            Logger.Log(
                LogLevel.Warn, "cnet-bridge",
                "could not hide CelesteNet name tags: " + e.GetType().Name + ": " + e.Message
            );
        }
    }

    /// <summary>
    /// Signature-identical replacement for the original draw call: it draws nothing and hands the
    /// text CelesteNet was about to draw back to the caller, which records it.
    /// </summary>
    private static string PassThrough(
        string text, Vector2 position, Vector2 justify, Vector2 scale, Color color, float stroke,
        Color strokeColor
    ) => text;

    /// <summary>
    /// Records that CelesteNet would have drawn <paramref name="text"/> for this tag, and what a mod
    /// added to it. Runs for the local player's tag too, where <c>Tracking</c> is a <c>Player</c>.
    /// </summary>
    private static void RecordDrawn(string text, GhostNameTag tag) {
        int key = KeyOf(tag);
        if (key == NoKey)
            return;

        ulong frame = Engine.FrameCounter;

        if (tag.Tracking is Ghost ghost && ghost.PlayerInfo is not null) {
            drawnAt[ghost.PlayerInfo.ID] = frame;
            if (reported.Add(ghost.PlayerInfo.ID))
                Logger.Log(LogLevel.Verbose, "cnet-bridge", $"name tag draw reached for #{ghost.PlayerInfo.ID}");
        }

        string? suffix = CelesteNetNameTagSuffix.Append(text, BaselineOf(tag), out bool replaced);
        if (replaced) {
            suffixes.Remove(key);
            if (replacedText.Add(key))
                Logger.Log(
                    LogLevel.Info, "cnet-bridge",
                    $"a mod replaced the whole name tag text of player {key} ('{text}'); "
                    + "MiaoNet keeps drawing its own name"
                );
        } else if (string.IsNullOrEmpty(suffix)) {
            suffixes.Remove(key);
        } else {
            suffixes[key] = (frame, suffix);
        }

        if (drawnAt.Count > 32 || suffixes.Count > 32)
            Prune(frame);
    }

    /// <summary>
    /// The text CelesteNet itself would put in the tag, which is what makes the difference above
    /// exactly the part a mod added.
    /// </summary>
    private static string? BaselineOf(GhostNameTag tag)
        => tag.Tracking is Ghost ghost
            ? ghost.PlayerInfo?.DisplayName
            : CelesteNetClientModule.Instance?.Client?.PlayerInfo?.DisplayName;

    /// <summary>
    /// MiaoNet's key for this tag: the player id, or the local player's own slot. A ghost whose player
    /// is not known yet has no key to file anything under.
    /// </summary>
    private static int KeyOf(GhostNameTag tag)
        => tag.Tracking switch {
            Ghost { PlayerInfo: { } info } => (int)info.ID,
            Ghost => NoKey,
            _ => Celeste.Mod.MiaoNet.RemoteNameTagSuffix.SelfKey
        };

    /// <summary>Key for tags that cannot be attributed to a player.</summary>
    private const int NoKey = int.MinValue + 1;

    /// <summary>The part a mod added to this tag, if it was drawn recently enough to still apply.</summary>
    private static string? CurrentSuffix(int key) {
        if (!suffixes.TryGetValue(key, out (ulong Frame, string Suffix) entry))
            return null;

        return Engine.FrameCounter - entry.Frame <= VisibleFrames ? entry.Suffix : null;
    }

    private static void Prune(ulong frame) {
        const ulong OldEnoughToForget = 300;

        List<uint> stale = [];
        foreach (KeyValuePair<uint, ulong> entry in drawnAt) {
            if (frame - entry.Value > OldEnoughToForget)
                stale.Add(entry.Key);
        }
        foreach (uint id in stale)
            drawnAt.Remove(id);

        List<int> staleSuffixes = [];
        foreach (KeyValuePair<int, (ulong Frame, string Suffix)> entry in suffixes) {
            if (frame - entry.Value.Frame > OldEnoughToForget)
                staleSuffixes.Add(entry.Key);
        }
        foreach (int key in staleSuffixes)
            suffixes.Remove(key);
    }

    private static bool ShouldShow(int playerId) {
        if (!drawnAt.TryGetValue((uint)playerId, out ulong frame))
            return false;

        return Engine.FrameCounter - frame <= VisibleFrames;
    }
}
