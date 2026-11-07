using System.Collections.Generic;
using Celeste.Mod.CelesteNet.Client;
using Celeste.Mod.CelesteNet.Client.Components;
using Monocle;
using MonoMod.RuntimeDetour;

namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Silences the real CelesteNet client's own interface. The client stays connected and keeps its data
/// pipeline alive; it just must not build or draw UI. Its ghosts stay visible: they are the remote
/// players.
/// <para>
/// The components' own Update/Tick keep running: the main component is what drives the player
/// pipeline.
/// </para>
/// </summary>
internal sealed class CelesteNetSuppression {

    /// <summary>Components whose Init() is skipped entirely: never Enabled, never register handlers.</summary>
    /// <remarks>
    /// The player list is deliberately not here. Skipping its Init leaves its <c>Channels</c> field null
    /// forever, because that field is filled by its <c>DataChannelList</c> handler, and that handler only
    /// exists once Init has registered it. Third-party mods read the field: PvPDash, for one, compares the
    /// current channel name against it on every team update and crashes the whole receive thread on null.
    /// Letting the component initialize costs nothing visible: it only draws while its hotkey has been
    /// pressed, and <see cref="CelesteNetGameComponent.Draw"/> is suppressed either way.
    /// </remarks>
    private static readonly Type[] SkipInitTypes = [
        typeof(CelesteNetChatComponent),
        typeof(CelesteNetEmoteComponent),
        typeof(CelesteNetStatusComponent),
        typeof(CelesteNetDebugMapComponent),
    ];

    private readonly List<Hook> hooks = [];

    /// <summary>Number of detours the last <see cref="Install"/> call managed to install.</summary>
    public int InstalledCount { get; private set; }

    /// <summary>Number of detours the bridge expects to install.</summary>
    public const int ExpectedCount = 3;

    public void Install() {
        InstalledCount = 0;
        Add("CelesteNetGameComponent.Init", typeof(CelesteNetGameComponent).GetMethod("Init")!, new Action<
            Action<CelesteNetGameComponent>, CelesteNetGameComponent>(OnComponentInit));

        Add("CelesteNetGameComponent.Draw", typeof(CelesteNetGameComponent).GetMethod("Draw")!, new Action<
            Action<CelesteNetGameComponent, Microsoft.Xna.Framework.GameTime?>, CelesteNetGameComponent, Microsoft.Xna.Framework.GameTime?>(
            (_, _, _) => { }));

        Add("CelesteNetRenderHelperComponent.Initialize",
            typeof(CelesteNetRenderHelperComponent).GetMethod("Initialize")!,
            new Action<Action<CelesteNetRenderHelperComponent>, CelesteNetRenderHelperComponent>((_, _) => { }));

        // The main component is the player pipeline itself, so it gets no detour: its Initialize,
        // Update and Tick have to run for CelesteNet's ghosts to exist and move.

        Logger.Log(LogLevel.Info, "cnet-bridge",
            $"suppression: {InstalledCount}/{ExpectedCount} hooks installed, "
            + "CelesteNet UI hidden, its player pipeline running"
        );
    }

    public void Uninstall() {
        foreach (Hook hook in hooks)
            hook.Dispose();
        hooks.Clear();
        InstalledCount = 0;
    }

    private void Add(string name, System.Reflection.MethodInfo from, Delegate to) {
        try {
            hooks.Add(new Hook(from, to));
            InstalledCount++;
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "cnet-bridge", "hook failed: " + name + " -> " + e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// The main component is not a UI component: its Init only registers it with the client context, and
    /// its Update/Tick must keep running because they drive the player pipeline. Only the UI components
    /// are skipped below.
    /// </summary>
    private static void OnComponentInit(Action<CelesteNetGameComponent> orig, CelesteNetGameComponent self) {
        if (Array.IndexOf(SkipInitTypes, self.GetType()) >= 0)
            return;

        orig(self);
    }

    // Everything else stays hidden: the HUD components never initialize and the render helper never
    // draws, so the only CelesteNet things a player can see are its ghosts.
}
