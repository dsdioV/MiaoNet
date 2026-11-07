using System.Reflection;

namespace Celeste.Mod.MiaoNet;

/// <summary>
/// Keeps MiaoNet and the real CelesteNet client from being connected at the same time, and wires the
/// CelesteNet side of the bridge switch to the real client.
/// <para>
/// With the bridge off, whichever client the player connects last wins: connecting CelesteNet makes
/// MiaoNet step aside, and connecting MiaoNet disconnects CelesteNet first.
/// </para>
/// <para>
/// With the bridge on, CelesteNet talks to the bridge's local server. Turning the switch on connects
/// it there by itself (after dropping whatever it was connected to), and disconnecting it from
/// CelesteNet's own menu turns the switch back off, so the switch and the connection always agree.
/// </para>
/// <para>
/// Everything here goes through reflection: MiaoNet.dll must never reference CelesteNet types, or the
/// mod would stop loading for players who do not have CelesteNet.
/// </para>
/// </summary>
internal static class CelesteNetCompat {

    /// <summary>Frames to wait before trying to connect CelesteNet again after a failed attempt.</summary>
    private const ulong ConnectRetryFrames = 120;

    /// <summary>
    /// Frames a connection that was alive when the switch turned on gets to finish tearing down. Until
    /// then its disappearance is the bridge's doing, not the player disconnecting CelesteNet.
    /// </summary>
    private const ulong BridgeStartGraceFrames = 120;

    /// <summary>Frames between attempts to bring MiaoNet online for CelesteNet.</summary>
    private const ulong MiaoNetKickFrames = 300;

    private static bool resolved;
    private static Type? moduleType;
    private static PropertyInfo? settingsProperty;
    private static PropertyInfo? connectedProperty;
    private static PropertyInfo? wantsToBeConnectedProperty;
    private static PropertyInfo? serverOverrideProperty;
    private static PropertyInfo? sessionProperty;
    private static PropertyInfo? interactionsOverrideProperty;
    private static MethodInfo? stopMethod;

    /// <summary>The session object the interaction override was written to, to notice a new one.</summary>
    private static object? overriddenSession;

    /// <summary>The interaction value written to CelesteNet's session, or null when it is not overridden.</summary>
    private static bool? appliedInteractions;

    /// <summary>Set while MiaoNet is disconnecting because CelesteNet connected first.</summary>
    private static bool steppingAside;

    /// <summary>Whether the bridge was running on the previous frame, to notice the switch turning on.</summary>
    private static bool bridgeWasActive;

    /// <summary>Whether the bridge started CelesteNet and keeps it connected while the switch is on.</summary>
    private static bool bridgeOwnsCelesteNet;

    /// <summary>Set while CelesteNet was dropped because MiaoNet has no session to give it an identity.</summary>
    private static bool waitingForMiaoNet;

    /// <summary>Whether CelesteNet had a live connection, so losing it means the player disconnected it.</summary>
    private static bool celesteNetWasAlive;

    private static ulong nextConnectAttemptFrame;
    private static ulong disconnectGraceUntilFrame;
    private static ulong nextMiaoNetKickFrame;

    /// <summary>Whether the CelesteNet client mod is installed and enabled.</summary>
    public static bool IsInstalled => Resolve();

    /// <summary>Whether the real CelesteNet client currently has a connection.</summary>
    public static bool IsConnected
        => Resolve() && TryGetSetting(connectedProperty, out bool connected) && connected;

    /// <summary>
    /// Whether CelesteNet has been asked to connect but has not got a client object yet. Its settings
    /// toggle starts dialing inside the same call that sets this flag, so a frame-time check that only
    /// looks at <see cref="IsConnected"/> arrives after the socket is already open.
    /// </summary>
    private static bool IsPending
        => Resolve() && TryGetSetting(wantsToBeConnectedProperty, out bool wants) && wants;

    /// <summary>
    /// Stops CelesteNet whether it is connected or still connecting. Called right before MiaoNet
    /// connects, so the connection the player just asked for is the one that survives.
    /// </summary>
    public static void Quiesce() {
        if (!Resolve())
            return;

        try {
            if (IsConnected) {
                connectedProperty!.SetValue(Settings, false);
                Logger.Info(LT.MiaoNet, "CelesteNet was connected, disconnecting it");
                return;
            }

            // Connected stays false while the client is still connecting, so the setter above would
            // not have stopped anything.
            if (TryGetSetting(wantsToBeConnectedProperty, out bool wants) && wants) {
                wantsToBeConnectedProperty!.SetValue(Settings, false);
                stopMethod!.Invoke(FindModule(), null);
                Logger.Info(LT.MiaoNet, "CelesteNet was connecting, stopping it");
            }
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Failed to stop CelesteNet: " + e.Message);
        }
    }

    /// <summary>
    /// Called every frame. With the bridge off, a CelesteNet connection that comes up while MiaoNet is
    /// connected makes MiaoNet step aside. With the bridge on, CelesteNet is connected to the local
    /// server and the switch follows the player when they disconnect it.
    /// </summary>
    public static void OnFrame() {
        if (!Resolve())
            return;

        SyncInteractions();

        MiaoNetContext? context = MiaoNetModule.Instance?.MiaoNetContextOptional;
        bool miaoOnline = context is not null && context.HasConnection;

        if (!MiaoNetModule.IsCelesteNetBridgeActive) {
            if (bridgeWasActive) {
                bridgeWasActive = false;
                ForgetBridgeOwnership();
            }
            StepAsideIfCelesteNetIsLive(context);
            return;
        }

        // The switch just turned on. Whatever CelesteNet was connected to is being dropped by the
        // bridge, and the connection the bridge starts must not be mistaken for the player's.
        if (!bridgeWasActive) {
            bridgeWasActive = true;
            ForgetBridgeOwnership();
            disconnectGraceUntilFrame = Engine.FrameCounter + BridgeStartGraceFrames;
        }

        bool connected = IsConnected;
        bool pending = IsPending;

        if (connected || pending) {
            if (!miaoOnline) {
                // The bridge identifies CelesteNet players by their MiaoNet identity; without a session
                // the local server can only refuse them. Bring MiaoNet online instead of dropping
                // CelesteNet until the player works out why nothing stays connected.
                waitingForMiaoNet = true;
                celesteNetWasAlive = false;
                EnsureMiaoNetSession(context);
                Quiesce();
                return;
            }

            // Only a connection pointed at the bridge belongs to the bridge. One the player aimed at a
            // real server is their own, and its disconnection must not turn the switch off.
            if (MiaoNetModule.IsCelesteNetPointedAtBridge) {
                bridgeOwnsCelesteNet = true;
                if (connected)
                    celesteNetWasAlive = true;
            }

            return;
        }

        // CelesteNet is idle.
        if (!miaoOnline) {
            if (bridgeOwnsCelesteNet || celesteNetWasAlive)
                waitingForMiaoNet = true;
            celesteNetWasAlive = false;
            return;
        }

        if (waitingForMiaoNet) {
            // MiaoNet is back, so CelesteNet can be given an identity again.
            waitingForMiaoNet = false;
            celesteNetWasAlive = false;
            ConnectCelesteNetToBridge();
            return;
        }

        if (celesteNetWasAlive) {
            if (Engine.FrameCounter < disconnectGraceUntilFrame) {
                // The old connection is still tearing down; start the bridge's one instead of treating
                // the gap as the player disconnecting CelesteNet.
                celesteNetWasAlive = false;
                ConnectCelesteNetToBridge();
                return;
            }

            // The connection the bridge started is gone: the player disconnected it from CelesteNet's
            // own menu. Keeping the switch on would leave the two disagreeing.
            TurnBridgeOff(context);
            return;
        }

        if (!bridgeOwnsCelesteNet || Engine.FrameCounter >= nextConnectAttemptFrame)
            ConnectCelesteNetToBridge();
    }

    /// <summary>
    /// Mirrors MiaoNet's own "player interactions" setting onto CelesteNet while the bridge is on.
    /// </summary>
    /// <remarks>
    /// With the bridge on, MiaoNet's interactions are off by design: the remote bodies are CelesteNet
    /// ghosts, so CelesteNet's <c>Interactions</c> setting is the only thing that decides whether
    /// players can be picked up or booped. Two independent switches with the same meaning is a trap:
    /// a player who turned CelesteNet's side off earlier gets a world where neither pipeline allows
    /// interactions, and nothing tells them why. Following MiaoNet's setting keeps one switch.
    /// <para>
    /// The value goes to the session override rather than the setting, so the player's CelesteNet
    /// settings file is left alone. It is cleared again when the bridge turns off, which hands the
    /// decision back to CelesteNet itself.
    /// </para>
    /// </remarks>
    private static void SyncInteractions() {
        object? session = sessionProperty?.GetValue(null);
        if (session is null || interactionsOverrideProperty is null)
            return;

        bool? desired = MiaoNetModule.IsCelesteNetBridgeActive
            ? MiaoNetModule.Settings.PlayerInteractions
            : null;

        if (ReferenceEquals(session, overriddenSession) && desired == appliedInteractions)
            return;

        bool changed = desired != appliedInteractions;

        try {
            interactionsOverrideProperty.SetValue(session, desired);
            overriddenSession = session;
            appliedInteractions = desired;

            // A new session object needs the same value again, which is not worth a log line.
            if (!changed)
                return;

            Logger.Info(LT.MiaoNet, desired is null
                ? "CelesteNet interactions follow its own setting again"
                : "CelesteNet interactions follow MiaoNet's interactions: " + (desired.Value ? "on" : "off")
            );
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Failed to mirror the interaction setting onto CelesteNet: " + e.Message);
        }
    }

    /// <summary>
    /// Starts a MiaoNet connection so the local CelesteNet server has an identity to hand out. Called
    /// when CelesteNet tries to connect while MiaoNet is offline, at most once every few seconds.
    /// </summary>
    private static void EnsureMiaoNetSession(MiaoNetContext? context) {
        if (Engine.FrameCounter < nextMiaoNetKickFrame)
            return;

        nextMiaoNetKickFrame = Engine.FrameCounter + MiaoNetKickFrames;
        if (context is not null && (context.HasConnection || context.HasConnectionOperation))
            return;

        try {
            (context ?? MiaoNetModule.Instance.MiaoNetContext).QueueConnect();
            Logger.Info(LT.MiaoNet, "CelesteNet is waiting for MiaoNet, connecting MiaoNet to give it an identity");
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Failed to start a MiaoNet connection for CelesteNet: " + e.Message);
            return;
        }

        try {
            context?.StatusComponent.ShowStatusMessage(
                Dialog.Get("miaonet_cnet_waiting_for_miao_message"), spin: true
            );
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Could not show the CelesteNet waiting message: " + e.Message);
        }
    }

    /// <summary>
    /// Points CelesteNet at the bridge's loopback server and asks it to connect. The bridge does not
    /// intercept CelesteNet's own connect button, so the target is set here, before CelesteNet reads it.
    /// </summary>
    private static void ConnectCelesteNetToBridge() {
        int port = MiaoNetModule.CelesteNetBridgePort;
        if (port == 0) {
            Logger.Warn(LT.MiaoNet, "The CelesteNet bridge is not listening; leaving CelesteNet disconnected");
            nextConnectAttemptFrame = Engine.FrameCounter + ConnectRetryFrames;
            return;
        }

        bridgeOwnsCelesteNet = true;
        celesteNetWasAlive = false;
        nextConnectAttemptFrame = Engine.FrameCounter + ConnectRetryFrames;

        try {
            serverOverrideProperty!.SetValue(Settings, $"127.0.0.1:{port}");
            connectedProperty!.SetValue(Settings, true);
            Logger.Info(LT.MiaoNet, "Connecting CelesteNet to the MiaoNet bridge");
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Failed to connect CelesteNet to the bridge: " + e.Message);
        }
    }

    /// <summary>
    /// Turns the bridge switch off because CelesteNet is no longer connected. Stopping the bridge
    /// unregisters its NetMod, which drops the MiaoNet session for a moment, so the switch also asks
    /// for a reconnect exactly like the options menu does.
    /// </summary>
    private static void TurnBridgeOff(MiaoNetContext? context) {
        Logger.Info(LT.MiaoNet, "CelesteNet was disconnected, turning the CelesteNet bridge off");
        ForgetBridgeOwnership();

        MiaoNetModule.Settings.CelesteNetBridgeEnabled = false;
        MiaoNetModule.ApplyCelesteNetBridgeSetting(false);

        try {
            // The switch was not changed from the options menu, so nothing else persists it.
            MiaoNetModule.Instance.SaveSettings();
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Could not save the CelesteNet bridge setting: " + e.Message);
        }

        try {
            context?.StatusComponent.ShowStatusMessage(Dialog.Get("miaonet_cnet_bridge_off_message"));
        } catch (Exception e) {
            Logger.Warn(LT.MiaoNet, "Could not show the bridge-off message: " + e.Message);
        }
    }

    private static void ForgetBridgeOwnership() {
        bridgeOwnsCelesteNet = false;
        waitingForMiaoNet = false;
        celesteNetWasAlive = false;
    }

    private static void StepAsideIfCelesteNetIsLive(MiaoNetContext? context) {
        if (!IsConnected) {
            steppingAside = false;
            return;
        }

        if (context is null || !context.HasConnection || steppingAside)
            return;

        steppingAside = true;
        Logger.Info(LT.MiaoNet, "CelesteNet connected while MiaoNet was connected, disconnecting MiaoNet");
        context.Disconnect();
        context.StatusComponent.ShowStatusMessage(Dialog.Get("miaonet_cnet_took_over_message"));
    }

    private static EverestModule? FindModule()
        => Everest.Modules.FirstOrDefault(module => module.Metadata.Name == "CelesteNet.Client");

    private static object? Settings => settingsProperty?.GetValue(null);

    private static bool TryGetSetting(PropertyInfo? property, out bool value) {
        value = false;
        if (property?.GetValue(Settings) is not bool current)
            return false;

        value = current;
        return true;
    }

    private static bool Resolve() {
        if (resolved)
            return moduleType is not null;

        resolved = true;
        EverestModule? module = FindModule();
        if (module is null)
            return false;

        moduleType = module.GetType();
        settingsProperty = moduleType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
        Type? settingsType = settingsProperty?.PropertyType;
        connectedProperty = settingsType?.GetProperty("Connected");
        wantsToBeConnectedProperty = settingsType?.GetProperty("WantsToBeConnected");
        serverOverrideProperty = settingsType?.GetProperty("ServerOverride");
        sessionProperty = moduleType.GetProperty("Session", BindingFlags.Public | BindingFlags.Static);
        interactionsOverrideProperty = sessionProperty?.PropertyType
            .GetProperty("InteractionsOverride", BindingFlags.Public | BindingFlags.Instance);
        stopMethod = moduleType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);

        if (settingsProperty is null || connectedProperty is null || wantsToBeConnectedProperty is null
            || serverOverrideProperty is null || stopMethod is null) {
            Logger.Warn(LT.MiaoNet, "CelesteNet has an unknown shape; the two clients cannot be kept apart");
            moduleType = null;
        }

        // The interaction override is a convenience, not a requirement: without it the two switches
        // simply stay independent, which is how CelesteNet behaved before the bridge existed.
        if (sessionProperty is null || interactionsOverrideProperty is null) {
            sessionProperty = null;
            interactionsOverrideProperty = null;
            Logger.Warn(LT.MiaoNet, "CelesteNet has no session interaction override; its own setting stays authoritative");
        }

        return moduleType is not null;
    }
}
