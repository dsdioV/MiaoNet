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

    private static Func<int, bool>? installedResolver;

    private ILHook? hook;

    private Hook? fallback;

    /// <summary>Whether CelesteNet's own rules are mirrored; false means its tags are just hidden.</summary>
    public bool Mirrored => hook is not null;

    public void Install() {
        drawnAt.Clear();
        reported.Clear();

        if (!TryInstallIlHook()) {
            InstallFallback();
            return;
        }

        installedResolver = ShouldShow;
        Celeste.Mod.MiaoNet.RemoteNameTagVisibility.SetResolver(installedResolver);
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

        drawnAt.Clear();
    }

    private bool TryInstallIlHook() {
        MethodInfo? render = typeof(GhostNameTag).GetMethod(nameof(GhostNameTag.Render));
        MethodInfo? skip = typeof(CelesteNetNameTagMirror).GetMethod(
            nameof(SkipDraw), BindingFlags.NonPublic | BindingFlags.Static
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
                cursor.EmitDelegate<Action<GhostNameTag>>(MarkDrawn);
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

    /// <summary>Signature-identical replacement for the original draw call; draws nothing.</summary>
    private static void SkipDraw(
        string text, Vector2 position, Vector2 justify, Vector2 scale, Color color, float stroke,
        Color strokeColor
    ) {
    }

    private static void MarkDrawn(GhostNameTag tag) {
        if (tag.Tracking is not Ghost ghost || ghost.PlayerInfo is null)
            return;

        ulong frame = Engine.FrameCounter;
        drawnAt[ghost.PlayerInfo.ID] = frame;

        if (reported.Add(ghost.PlayerInfo.ID))
            Logger.Log(LogLevel.Verbose, "cnet-bridge", $"name tag draw reached for #{ghost.PlayerInfo.ID}");

        if (drawnAt.Count > 32)
            Prune(frame);
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
    }

    private static bool ShouldShow(int playerId) {
        if (!drawnAt.TryGetValue((uint)playerId, out ulong frame))
            return false;

        return Engine.FrameCounter - frame <= VisibleFrames;
    }
}
