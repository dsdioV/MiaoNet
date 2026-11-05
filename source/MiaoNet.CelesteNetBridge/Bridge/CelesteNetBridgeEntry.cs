using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Celeste.Mod.CelesteNet.Client;
using Celeste.Mod.MiaoNet;
using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;
using MiaoNet.Shared;

namespace MiaoNet.CelesteNetBridge;

/// <summary>
/// Keeps the real CelesteNet client, replaces the server it talks to, and carries the frames that
/// client exchange over MiaoNet's NetMod pipeline.
/// <para>
/// The bridge is a NetMod consumer, nothing more: MiaoNet never learns that CelesteNet exists, and
/// the bridge never learns what any DataType means.
/// </para>
/// <para>
/// It ships inside the MiaoNet mod as a second assembly and is started by <c>MiaoNetModule</c> through
/// reflection, so MiaoNet.dll itself never references CelesteNet and still loads when it is absent.
/// </para>
/// </summary>
public static class CelesteNetBridgeEntry {

    /// <summary>NetMod name declared in the MiaoNet handshake.</summary>
    private const string NetModName = "MiaoNetCelesteNetBridge";

    /// <summary>Bumped whenever the packet layout below changes.</summary>
    private const ushort ProtocolVersion = 1;

    private static readonly List<CelesteNetFakeSession> sessions = [];

    /// <summary>Type ids already relayed per session, for a log that names each kind once.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, HashSet<string>> relayedTypes = [];

    /// <summary>Remote players already announced to each session.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, HashSet<uint>> introducedPlayers = [];

    /// <summary>Remote type ids already skipped per session, so the log names each kind once.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, HashSet<string>> undeliverableTypes = [];

    /// <summary>
    /// Tail of the send chain of each session. Every bridge frame goes through it, so the real client
    /// receives them in the order they were decided on: playerInfo before the graphics it is bound to.
    /// </summary>
    private static readonly Dictionary<CelesteNetFakeSession, Task> sendTails = [];

    /// <summary>The newest CelesteNet release this bridge was verified against.</summary>
    private static readonly Version TestedCelesteNetVersion = new(2, 4, 2);

    private static NetMod? netMod;
    private static CelesteNetFakeServer? server;
    private static CelesteNetSuppression? suppress;

    public static bool Initialized { get; private set; }

    /// <summary>The port the local server listens on, or 0 when it is not running.</summary>
    public static int LocalPort => server?.Port ?? 0;

    /// <summary>Turns the bridge on or off at runtime, the way the settings menu does.</summary>
    public static void SetEnabled(bool enabled) {
        if (enabled)
            Initialize();
        else
            Shutdown();
    }

    /// <summary>Starts the bridge: declares the NetMod, suppresses the CelesteNet UI, opens the port.</summary>
    public static void Initialize() {
        if (Initialized)
            return;

        WarnAboutUntestedVersion();

        suppress = new();
        suppress.Install();
        if (suppress.InstalledCount < CelesteNetSuppression.ExpectedCount) {
            // CelesteNet's interface leaking back on screen is the one failure players cannot miss.
            ReportFailure(
                $"only {suppress.InstalledCount}/{CelesteNetSuppression.ExpectedCount} CelesteNet UI hooks installed; " +
                "CelesteNet's own interface may show up"
            );
        }

        server = new(new CelesteNetFakeServerOptions {
            Logger = message => Logger.Log(LogLevel.Verbose, "cnet-bridge", message),
            IdentityProvider = LocalIdentity,
        });
        server.SessionStarted += OnSessionStarted;

        try {
            server.Start();
        } catch (Exception e) {
            server = null;
            suppress.Uninstall();
            suppress = null;
            ReportFailure("could not start the local CelesteNet server: " + e.Message);
            return;
        }

        // The declaration has to exist before the MiaoNet handshake goes out.
        netMod = MiaoNetNetMods.Register(new NetMod(
            NetModName,
            ProtocolVersion,
            NetModPacketEntry.Create<PacketBridgeFrame>(PacketScope.Map, OnRemoteFrame)
        ));

        Initialized = true;
        Logger.Log(LogLevel.Info, "cnet-bridge",
            $"relaying CelesteNet through 127.0.0.1:{server.Port}"
            + (server.UsedFallbackPort ? " (the default port 17230 was busy)" : "")
        );
    }

    /// <summary>
    /// The suppression detours are the only part of the bridge bound to CelesteNet's internals, so a
    /// newer client is worth a warning even when everything installs today.
    /// </summary>
    private static void WarnAboutUntestedVersion() {
        Version? installed = Everest.Modules
            .FirstOrDefault(module => module.Metadata.Name == "CelesteNet.Client")?.Metadata.Version;
        if (installed is null || installed <= TestedCelesteNetVersion)
            return;

        Logger.Log(
            LogLevel.Warn, "cnet-bridge",
            $"CelesteNet {installed} is newer than the tested {TestedCelesteNetVersion}; " +
            "the UI suppression detours may need an update"
        );
    }

    /// <summary>
    /// A failure the player has to know about: the bridge not working is invisible otherwise, because
    /// its own log lines are verbose. The status message only shows up once MiaoNet has a context.
    /// </summary>
    private static void ReportFailure(string message) {
        Logger.Log(LogLevel.Error, "cnet-bridge", message);

        try {
            MiaoNetModule.Instance?.MiaoNetContextOptional?.StatusComponent.ShowStatusMessage(message);
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "cnet-bridge", "could not show the failure on screen: " + e.Message);
        }
    }

    public static void Shutdown() {
        if (!Initialized)
            return;

        Initialized = false;
        Quiesce();
        if (server != null) {
            server.SessionStarted -= OnSessionStarted;
            server.Dispose();
            server = null;
        }

        lock (sessions)
            sessions.Clear();

        suppress?.Uninstall();
        suppress = null;

        netMod?.Dispose();
        netMod = null;
    }

    /// <summary>
    /// Stops the real client's connection, in whatever state it is: while the bridge runs it owns
    /// CelesteNet, and two live clients at once freeze the game.
    /// </summary>
    public static void Quiesce() {
        CelesteNetClientModule? module = CelesteNetClientModule.Instance;
        if (module is null)
            return;

        CelesteNetClientSettings settings = CelesteNetClientModule.Settings;
        if (settings.Connected) {
            settings.Connected = false;
            Logger.Log(LogLevel.Info, "cnet-bridge", "disconnected CelesteNet because the bridge owns the connection");
            return;
        }

        // Connected stays false while the client is still connecting, so stop it by hand.
        if (settings.WantsToBeConnected) {
            settings.WantsToBeConnected = false;
            module.Stop();
            Logger.Log(LogLevel.Info, "cnet-bridge", "stopped a pending CelesteNet connection");
        }
    }

    private static void OnSessionStarted(CelesteNetFakeSession session) {
        lock (sessions) {
            sessions.Add(session);
            relayedTypes[session] = [];
            introducedPlayers[session] = [];
            undeliverableTypes[session] = [];
        }

        session.FrameReceived += OnLocalFrame;
        session.Closed += OnSessionClosed;
    }

    private static void OnSessionClosed(CelesteNetFakeSession session) {
        session.FrameReceived -= OnLocalFrame;
        session.Closed -= OnSessionClosed;

        lock (sessions) {
            sessions.Remove(session);
            relayedTypes.Remove(session);
            introducedPlayers.Remove(session);
            undeliverableTypes.Remove(session);
            sendTails.Remove(session);
        }
    }

    /// <summary>
    /// The local real client sent something. Anything it sends is about the local player, so a
    /// playerInfo here is its own record; a remote player's record never comes this way.
    /// </summary>
    private static void OnLocalFrame(CelesteNetFakeSession session, CelesteNetFrame frame) {
        if (frame.TypeId is not { } typeId)
            return;

        string? source = DeclaringSourceOf(typeId);

        if (!CelesteNetRelayFilter.IsRelayable(typeId, source, aboutLocalPlayer: true))
            return;

        if (netMod is null || !MiaoNetNetMods.IsConnected)
            return;

        SendRelayed(session, netMod, typeId, frame.Payload);
    }

    private static void SendRelayed(
        CelesteNetFakeSession session, NetMod target, string typeId, byte[] payload
    ) {
        try {
            target.Send(new PacketBridgeFrame(payload));

            bool first;
            lock (sessions)
                first = relayedTypes.TryGetValue(session, out HashSet<string>? seen) && seen.Add(typeId);
            if (!first)
                return;

            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                $"relay {typeId} on {target.Name} ({payload.Length} B)"
            );
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "cnet-bridge", "send failed: " + e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>
    /// Which assembly declares the type the frame belongs to, as the local client knows it. Reading it from
    /// the client's own DataContext is what makes the bridge survive a CelesteNet update that adds a type:
    /// anything new that ships inside CelesteNet is treated as connection state without being listed here.
    /// </summary>
    private static string? DeclaringSourceOf(string typeId) {
        Celeste.Mod.CelesteNet.DataContext? data = CelesteNetClientModule.Instance?.Context?.Client?.Data;
        if (data is null || !data.IDToDataType.TryGetValue(typeId, out Type? type))
            return null;

        return data.DataTypeToSource.TryGetValue(type, out string? source) ? source : null;
    }

    /// <summary>
    /// Another player's payload arrived: push it into the local real client, unless it describes the
    /// sender's connection instead of the sender's player.
    /// </summary>
    private static void OnRemoteFrame(PacketEnvelope envelope, PacketBridgeFrame packet) {
        uint sender = (uint)envelope.SenderPlayerID;

        // The type id is the only part of the payload that can be checked without understanding it.
        // A frame whose type cannot even be named is never pushed into the real client.
        string? typeId = CelesteNetTypeReader.TryReadTypeId(packet.Payload);
        bool deliverable = typeId is not null
            && CelesteNetRelayFilter.IsDeliverable(typeId, DeclaringSourceOf(typeId));

        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        foreach (CelesteNetFakeSession session in targets) {
            // Our own frames already went out; echoing them back would duplicate everything.
            if (session.PlayerId == sender)
                continue;

            if (!deliverable) {
                // A teammate on the bridge can only send player frames, so this is a safety net for an
                // unexpected upstream type; keep the log to one line each.
                ReportUndeliverable(session, typeId ?? "<unparsed>");
                continue;
            }

            bool first;
            lock (sessions)
                first = introducedPlayers[session].Add(sender);

            if (first) {
                // The real client can only resolve refs to a player it has seen a DataPlayerInfo for.
                string name = RemoteName(sender);
                QueueSend(session, CelesteNetDataTypes.PlayerInfo(sender, name));
                Logger.Log(LogLevel.Verbose, "cnet-bridge", $"announce remote #{sender} as {name}");
            }

            QueueSend(session, CelesteNetFrameRewriter.RewriteSource(packet.Payload, sender));
        }
    }

    private static void ReportUndeliverable(CelesteNetFakeSession session, string typeId) {
        bool first;
        lock (sessions)
            first = undeliverableTypes.TryGetValue(session, out HashSet<string>? seen) && seen.Add(typeId);

        if (first) {
            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                $"skip remote {typeId}: it describes the sender's server, not this one"
            );
        }
    }

    /// <summary>
    /// Queues one payload behind everything already queued for this session. Ordering is load bearing:
    /// a CelesteNet frame can only bind to references the client has already received, and a bound ref
    /// to an unknown player makes the client drop the connection.
    /// </summary>
    private static void QueueSend(CelesteNetFakeSession session, byte[] payload) {
        lock (sessions) {
            Task previous = sendTails.TryGetValue(session, out Task? tail) ? tail : Task.CompletedTask;
            sendTails[session] = SendAfterAsync(previous, session, payload);
        }
    }

    private static async Task SendAfterAsync(Task previous, CelesteNetFakeSession session, byte[] payload) {
        try {
            await previous.ConfigureAwait(false);
        } catch (Exception) {
            // The previous send already reported through SendSafeAsync.
        }

        await SendSafeAsync(session, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// The CelesteNet identity this client should use. Reusing the MiaoNet player id keeps both
    /// sides' references consistent, and gives every player a distinct id.
    /// </summary>
    private static (uint Id, string Name) LocalIdentity() {
        ClientState? state = MiaoNetModule.Instance?.MiaoNetContext?.ClientState;
        OnlinePlayer? self = state?.Self;
        if (self is not null)
            return ((uint)self.ID, self.Info.Name);

        return (1, "Player");
    }

    /// <summary>
    /// The remote player's real MiaoNet name. CoopHelper keys players by (machine, name), so a made-up
    /// name would make one player look like two different people.
    /// </summary>
    private static string RemoteName(uint id) {
        ClientState? state = MiaoNetModule.Instance?.MiaoNetContext?.ClientState;
        if (state is not null && state.Players.TryGetValue((int)id, out OnlinePlayer? player))
            return player.Info.Name;

        return "Player" + id;
    }

    private static async Task SendSafeAsync(CelesteNetFakeSession session, byte[] payload) {
        try {
            await session.SendAsync(payload).ConfigureAwait(false);
        } catch (Exception) {
            // The session is going away; its own pump reports it.
        }
    }
}
