using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>Remote type ids already delivered per session, for a log that names each kind once.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, HashSet<string>> deliveredTypes = [];

    /// <summary>Remote type ids already skipped per session, so the log names each kind once.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, HashSet<string>> undeliverableTypes = [];

    /// <summary>Ghost-birth gate for every remote player, per session.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, Dictionary<uint, CelesteNetRemoteFeed>> remoteFeeds = [];

    /// <summary>
    /// When each remote player last sent anything, per session. CelesteNet's client never times a ghost
    /// out, so a peer that disappears without a word would leave one standing at its last position.
    /// </summary>
    private static readonly Dictionary<CelesteNetFakeSession, CelesteNetPeerWatch> peers = [];

    /// <summary>Our own identity frames per session, kept for peers who joined after we sent them.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, CelesteNetIdentityCache> localIdentities = [];

    /// <summary>Whether each session already asked its peers for their identity frames.</summary>
    private static readonly Dictionary<CelesteNetFakeSession, CelesteNetIdentityRequester> identityRequesters = [];

    /// <summary>The MiaoNet context whose player events are being watched, if any.</summary>
    private static MiaoNetContext? watchedContext;

    /// <summary>Whether MiaoNet had a connection at the previous heartbeat.</summary>
    private static bool miaoNetWasConnected;

    /// <summary>
    /// Set when a peer asked for our identity before we had both binding frames. The answer follows as
    /// soon as our own client sends the missing ones; CelesteNet will not send graphics again by itself.
    /// </summary>
    private static bool identityResponsePending;

    /// <summary>The stream a pending identity answer has to go back on.</summary>
    private static NetMod? identityResponseTarget;

    /// <summary>
    /// Tail of the send chain of each session. Every bridge frame goes through it, so the real client
    /// receives them in the order they were decided on: playerInfo before the graphics it is bound to.
    /// </summary>
    private static readonly Dictionary<CelesteNetFakeSession, Task> sendTails = [];

    /// <summary>
    /// How long a remote player may stay silent before the bridge treats them as gone.
    /// </summary>
    /// <remarks>
    /// MiaoNet sends a player frame at tick rate, but it stops while the sender is paused, dead or in a
    /// freeze frame, so silence alone is not proof of absence: a paused player is skipped by
    /// <see cref="AnnounceSilentPlayers"/> instead of being timed out. Ten seconds is what is left for
    /// everything else, well past a death animation, and long enough that a wrong guess is rare. Being
    /// wrong in this direction removes a player who is standing right there, which is worse than
    /// noticing a real absence a few seconds late.
    /// </remarks>
    private const double PeerSilenceTimeoutSeconds = 10;

    /// <summary>
    /// How long <see cref="Shutdown"/> waits for the leave frames to reach the local client. This runs on
    /// the main thread while the player waits, so it bounds a hitch rather than being a real deadline.
    /// </summary>
    private static readonly TimeSpan ShutdownSendTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>The newest CelesteNet release this bridge was verified against.</summary>
    private static readonly Version TestedCelesteNetVersion = new(2, 4, 2);

    private static NetMod? netMod;
    private static CelesteNetFakeServer? server;
    private static CelesteNetSuppression? suppress;

    /// <summary>Whether the bridge switch is on, so its NetMod is declared in the MiaoNet handshake.</summary>
    public static bool Initialized { get; private set; }

    /// <summary>
    /// Whether the bridge is actually taking CelesteNet over right now: the switch is on and MiaoNet has
    /// a session. The two are separate on purpose (see <see cref="Engage"/>).
    /// </summary>
    public static bool Engaged { get; private set; }

    /// <summary>The port the local server listens on, or 0 when it is not running.</summary>
    public static int LocalPort => server?.Port ?? 0;

    /// <summary>
    /// Whether CelesteNet's <c>ServerOverride</c> currently points at this bridge's loopback server.
    /// MiaoNet writes that address while the switch is on and this clears it again on shutdown.
    /// </summary>
    public static bool IsCelesteNetPointedAtTheBridge => ResolveTarget(out string host, out int port)
        && IsLoopbackHost(host)
        && port == LocalPort
        && LocalPort != 0;

    /// <summary>Whether a host name in <c>ServerOverride</c> means this machine.</summary>
    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits CelesteNet's <c>ServerOverride</c>. CelesteNet's own "connect locally" button writes just
    /// <c>localhost</c> and lets the port default, while auto connect writes <c>127.0.0.1:&lt;port&gt;</c>.
    /// </summary>
    private static bool ResolveTarget(out string host, out int port) {
        host = "";
        port = 17230;

        string? raw = CelesteNetClientModule.Settings.ServerOverride;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string target = raw.Trim();
        int separator = target.LastIndexOf(':');
        if (separator <= 0) {
            host = target;
            return true;
        }

        host = target[..separator];
        if (!int.TryParse(target[(separator + 1)..], out port))
            port = 17230;

        return true;
    }

    /// <summary>Turns the bridge on or off at runtime, the way the settings menu does.</summary>
    public static void SetEnabled(bool enabled) {
        if (enabled)
            Initialize();
        else
            Shutdown();
    }

    /// <summary>
    /// Takes CelesteNet over, or lets go of it again. MiaoNet calls this whenever its own session comes
    /// and goes; the switch alone never causes a takeover.
    /// </summary>
    public static void SetEngaged(bool engaged) {
        if (engaged)
            Engage();
        else
            Disengage();
    }

    /// <summary>
    /// Turns the switch on: declares the NetMod in the MiaoNet handshake. Nothing is taken over yet.
    /// </summary>
    /// <remarks>
    /// Declaring the NetMod is the one part of the bridge that cannot follow the MiaoNet session.
    /// Registering or unregistering a NetMod disconnects MiaoNet on purpose (the handshake that was
    /// already sent no longer matches), so doing it when a session appears would disconnect the very
    /// session that asked for it, forever. It follows the switch instead, which the player sets by hand.
    /// </remarks>
    public static void Initialize() {
        if (Initialized)
            return;

        WarnAboutUntestedVersion();

        // The declaration has to exist before the MiaoNet handshake goes out.
        netMod = MiaoNetNetMods.Register(new NetMod(
            NetModName,
            ProtocolVersion,
            NetModPacketEntry.Create<PacketBridgeFrame>(PacketScope.Map, OnRemoteFrame),
            // Appended, never inserted: existing peers index the first entry by local id 1.
            NetModPacketEntry.Create<PacketBridgeIdentityRequest>(PacketScope.Map, OnIdentityRequest)
        ));

        Initialized = true;
        Logger.Log(LogLevel.Info, "cnet-bridge",
            "bridge is on; CelesteNet stays untouched until MiaoNet is connected"
        );
    }

    /// <summary>
    /// Takes CelesteNet over: quiesces it, silences its interface and opens the loopback server it will
    /// talk to instead of a real one.
    /// </summary>
    /// <remarks>
    /// Everything here is bound to a MiaoNet session: the identity handed to CelesteNet is the player's
    /// MiaoNet id, and the frames it exchanges are carried inside a MiaoNet NetMod. With the switch on
    /// but no session there is nothing to carry anything, so CelesteNet is left exactly as the player
    /// left it, free to join a real server.
    /// </remarks>
    public static void Engage() {
        if (!Initialized || Engaged)
            return;

        // Whatever the real client was doing before the session showed up, it is over now.
        Quiesce();
        identityResponsePending = false;

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

        Engaged = true;

        // Entering a map is when identity replay starts to matter: the request sent while still in the
        // menus had nobody in scope, and a peer may have joined this map while we were away.
        MiaoNetModule.PlayerLocationChanged += OnLocalPlayerLocationChanged;
        Logger.Log(LogLevel.Info, "cnet-bridge",
            $"relaying CelesteNet through 127.0.0.1:{server.Port}"
            + (server.UsedFallbackPort ? " (the default port 17230 was busy)" : "")
        );
    }

    /// <summary>Lets go of CelesteNet again, putting its interface and its connection back in the player's hands.</summary>
    public static void Disengage() {
        if (!Engaged)
            return;

        Engaged = false;
        MiaoNetModule.PlayerLocationChanged -= OnLocalPlayerLocationChanged;

        if (watchedContext is not null) {
            watchedContext.PlayerLeft -= OnPlayerLeft;
            watchedContext.PlayerLocationChanged -= OnRemotePlayerLocationChanged;
            watchedContext.PlayerChannelMoved -= OnRemotePlayerChannelMoved;
            watchedContext.SelfChannelMoved -= OnSelfChannelMoved;
            watchedContext.ClientInitialized -= OnMiaoNetClientInitialized;
            watchedContext = null;
        }

        // Before anything is torn down: a peer that is never told keeps the last ghost it saw, frozen in
        // place and still grabbable. The frames have to be written while the stream is still up.
        AnnounceEveryoneLeftBeforeShutdown();

        ResetRedirect();
        Quiesce();

        if (server != null) {
            server.SessionStarted -= OnSessionStarted;
            server.Dispose();
            server = null;
        }

        lock (sessions) {
            sessions.Clear();
            peers.Clear();
        }

        suppress?.Uninstall();
        suppress = null;

        Logger.Log(LogLevel.Info, "cnet-bridge", "MiaoNet is gone; CelesteNet is left alone again");
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

        Disengage();
        Initialized = false;

        netMod?.Dispose();
        netMod = null;
    }

    /// <summary>
    /// Drops the redirect once the bridge is gone, so a later "connect" goes wherever the player points
    /// it instead of dialing a loopback port nobody listens on any more.
    /// </summary>
    private static void ResetRedirect() {
        if (IsCelesteNetPointedAtTheBridge)
            CelesteNetClientModule.Settings.ServerOverride = "";
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
            deliveredTypes[session] = [];
            introducedPlayers[session] = [];
            undeliverableTypes[session] = [];
            localIdentities[session] = new();
            identityRequesters[session] = new();
            remoteFeeds[session] = [];
            peers[session] = new();
        }

        session.FrameReceived += OnLocalFrame;
        session.Heartbeat += OnSessionHeartbeat;
        session.Closed += OnSessionClosed;

        WatchPlayerLeaves();
    }

    private static void OnSessionClosed(CelesteNetFakeSession session) {
        session.FrameReceived -= OnLocalFrame;
        session.Heartbeat -= OnSessionHeartbeat;
        session.Closed -= OnSessionClosed;

        lock (sessions) {
            sessions.Remove(session);
            relayedTypes.Remove(session);
            deliveredTypes.Remove(session);
            introducedPlayers.Remove(session);
            undeliverableTypes.Remove(session);
            localIdentities.Remove(session);
            identityRequesters.Remove(session);
            remoteFeeds.Remove(session);
            peers.Remove(session);
            sendTails.Remove(session);
        }
    }

    /// <summary>
    /// The bridge has no update loop of its own, and the keepAlive the fake server already sends every
    /// 250 ms is the only periodic callback it gets. Three things need one: attaching to MiaoNet's
    /// player list (the context may appear after the first CelesteNet connection), noticing that
    /// MiaoNet itself went away, and adopting a player id that a forced reconnect changed.
    /// </summary>
    private static void OnSessionHeartbeat(CelesteNetFakeSession session) {
        WatchPlayerLeaves();
        RestartSessionsWithStaleIdentity();
        AnnounceSilentPlayers(session);

        bool connected = MiaoNetNetMods.IsConnected;
        if (miaoNetWasConnected && !connected)
            AnnounceEveryoneLeft(session);

        if (connected)
            RequestPeersIdentity(session);

        miaoNetWasConnected = connected;
    }

    /// <summary>
    /// A teammate who turns the bridge off, loses the connection or crashes stops sending without ever
    /// saying goodbye. Nothing else notices: CelesteNet's client keeps the last ghost it built, so the
    /// player would stand there frozen, named, and grabbable. Silence is the only signal there is.
    /// </summary>
    private static void AnnounceSilentPlayers(CelesteNetFakeSession session) {
        CelesteNetPeerWatch? watch;
        lock (sessions)
            peers.TryGetValue(session, out watch);

        if (watch is null)
            return;

        foreach (uint id in watch.TakeExpired(Now, PeerSilenceTimeoutSeconds)) {
            // A paused player sends no frames on purpose. Their ghost has to stay.
            if (IsPeerPaused(id)) {
                watch.Saw(id, Now);
                continue;
            }

            AnnouncePlayerLeft(session, id, "went silent");
        }
    }

    /// <summary>Whether MiaoNet last reported this player as paused.</summary>
    private static bool IsPeerPaused(uint id)
        => MiaoNetModule.Instance?.MiaoNetContext?.ClientState is { } state
            && state.Players.TryGetValue((int)id, out OnlinePlayer? player)
            && player.IsPaused;

    /// <summary>Monotonic seconds, for the only thing the bridge times: how long a peer has been quiet.</summary>
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Subscribes to MiaoNet's player events. Idempotent, because the context is created lazily the
    /// first time the player uses MiaoNet, which can be later than the first CelesteNet connection.
    /// </summary>
    private static void WatchPlayerLeaves() {
        if (watchedContext is not null)
            return;

        MiaoNetContext? context = MiaoNetModule.Instance?.MiaoNetContextOptional;
        if (context is null)
            return;

        context.PlayerLeft += OnPlayerLeft;
        // A player who changes room or channel stops sending frames to our map, and CelesteNet's
        // ghost has no timeout: without a leave it would freeze at its last position, nameless.
        context.PlayerLocationChanged += OnRemotePlayerLocationChanged;
        context.PlayerChannelMoved += OnRemotePlayerChannelMoved;
        context.SelfChannelMoved += OnSelfChannelMoved;
        // A reconnect can assign a new MiaoNet player id; the real client cannot adopt it in place.
        context.ClientInitialized += OnMiaoNetClientInitialized;
        watchedContext = context;
    }

    /// <summary>
    /// MiaoNet can give the player a different id when a reconnect is forced (a player stream changed
    /// while connected). The real CelesteNet client froze the old id at its handshake, so every payload
    /// it produces after that names a player its peers no longer know. Let the client handshake again:
    /// ending the session also announces the old id as gone, and the next startup sequence adopts the
    /// new one before any of our frames are relayed under it.
    /// </summary>
    private static void OnMiaoNetClientInitialized(ClientState state)
        => RestartSessionsWithStaleIdentity((uint)state.Self.ID);

    private static void RestartSessionsWithStaleIdentity(uint? currentPlayerId = null) {
        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        foreach (CelesteNetFakeSession session in targets) {
            if (session.IsIdentityCurrent())
                continue;

            uint? newId = currentPlayerId ?? LocalIdentity()?.Id;
            Logger.Log(
                LogLevel.Info, "cnet-bridge",
                $"MiaoNet player id changed (#{session.PlayerId}"
                + (newId is { } id ? $" -> #{id}" : "")
                + "); restarting the local CelesteNet session so it adopts the new id"
            );
            session.Dispose();
        }
    }

    /// <summary>
    /// The local real client sent something. Anything it sends is about the local player, so a
    /// playerInfo here is its own record; a remote player's record never comes this way.
    /// </summary>
    private static void OnLocalFrame(CelesteNetFakeSession session, CelesteNetFrame frame) {
        if (frame.TypeId is not { } typeId)
            return;

        RememberOwnIdentity(session, typeId, frame.Payload);

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

            if (!session.IsHandshaken) {
                // The client takes the first DataPlayerInfo it receives as itself, and its reference
                // table is not built before the startup sequence is through; inject nothing yet.
                ReportSkip(session, typeId ?? "<unparsed>", "the local client is still connecting");
                continue;
            }

            if (typeId == CelesteNetIdentityCache.PlayerInfoTypeId) {
                // A peer answers an identity request with its own record, but the real client takes the
                // first DataPlayerInfo it ever sees as itself. Every remote record the client needs is
                // synthesised from MiaoNet before that player's first frame instead.
                ReportSkip(session, typeId, "remote player records are synthesised from MiaoNet");
                continue;
            }

            if (!deliverable) {
                // A teammate on the bridge can only send player frames, so this is a safety net for an
                // unexpected upstream type; keep the log to one line each.
                ReportSkip(session, typeId ?? "<unparsed>", "it describes the sender's server, not this one");
                continue;
            }

            WatchPeer(session, sender);

            if (IntroducePlayer(session, sender)) {
                // The real client can only resolve refs to a player it has seen a DataPlayerInfo for.
                string name = RemoteName(sender);
                QueueSend(session, CelesteNetDataTypes.PlayerInfo(sender, name));
                Logger.Log(LogLevel.Verbose, "cnet-bridge", $"announce remote #{sender} as {name}");
            }

            if (FeedOf(session, sender) is not { } feed)
                continue;

            bool wasHolding = feed.HasHeldFrame;
            CelesteNetRemoteFeedAction action = feed.Accept(typeId!, packet.Payload);
            if (action == CelesteNetRemoteFeedAction.Hold) {
                if (!wasHolding) {
                    Logger.Log(
                        LogLevel.Verbose, "cnet-bridge",
                        $"hold playerFrame from #{sender} until its graphics and state arrive"
                    );
                }
                continue;
            }

            QueueSend(session, CelesteNetFrameRewriter.RewriteSource(packet.Payload, sender));
            ReportDelivered(session, typeId!, sender, packet.Payload.Length);

            // The gate holds player frames until graphics and state are through; now that one of them
            // just went out, the newest held frame can follow. It is queued behind them, never before.
            if (feed.TakeHeldFrame() is { } held) {
                QueueSend(session, CelesteNetFrameRewriter.RewriteSource(held, sender));
                ReportDelivered(session, CelesteNetRemoteFeed.PlayerFrameTypeId, sender, held.Length);
            }
        }
    }

    private static bool IntroducePlayer(CelesteNetFakeSession session, uint id) {
        lock (sessions)
            return introducedPlayers.TryGetValue(session, out HashSet<uint>? introduced) && introduced.Add(id);
    }

    private static CelesteNetRemoteFeed? FeedOf(CelesteNetFakeSession session, uint id) {
        lock (sessions) {
            if (!remoteFeeds.TryGetValue(session, out Dictionary<uint, CelesteNetRemoteFeed>? feeds))
                return null;
            if (!feeds.TryGetValue(id, out CelesteNetRemoteFeed? feed))
                feeds[id] = feed = new();
            return feed;
        }
    }

    /// <summary>
    /// Asks for identity frames again on every map change, instead of trusting the one request sent
    /// when the real client connected, which usually happens while still in the menus.
    /// </summary>
    private static void OnLocalPlayerLocationChanged(PlayerLocation location, bool forceFullChange)
        => ResetIdentityRequests();

    /// <summary>Keeps the newest copy of one of our own identity frames, for peers that join later.</summary>
    private static void RememberOwnIdentity(CelesteNetFakeSession session, string typeId, byte[] payload) {
        bool stored;
        lock (sessions)
            stored = localIdentities.TryGetValue(session, out CelesteNetIdentityCache? cache)
                && cache.Remember(typeId, payload);

        // A peer asked before we had this frame, and the cache may have become complete just now.
        if (stored && identityResponsePending)
            TryAnswerIdentityRequest(identityResponseTarget!);
    }

    /// <summary>
    /// Asks every peer on the same map for its identity frames, once per MiaoNet connection. A request
    /// that cannot go out yet is simply retried on the next heartbeat.
    /// </summary>
    private static void RequestPeersIdentity(CelesteNetFakeSession session) {
        CelesteNetIdentityRequester? requester;
        lock (sessions)
            identityRequesters.TryGetValue(session, out requester);

        if (requester is null || netMod is not { } target
            || !requester.ShouldRequest(session.IsHandshaken, MiaoNetNetMods.IsConnected))
            return;

        try {
            target.Send(new PacketBridgeIdentityRequest());
            requester.MarkRequested();
            Logger.Log(LogLevel.Verbose, "cnet-bridge", "asked peers for their identity frames");
        } catch (Exception e) {
            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                "identity request failed, will retry: " + e.GetType().Name + ": " + e.Message
            );
        }
    }

    private static void ResetIdentityRequests() {
        lock (sessions)
            foreach (CelesteNetIdentityRequester requester in identityRequesters.Values)
                requester.Reset();
    }

    /// <summary>
    /// A peer joined the map after we did and asks who we are. Answer with the cached frames, or wait for
    /// our own client to send the missing ones: it will not send graphics again unless they change.
    /// </summary>
    private static void OnIdentityRequest(PacketEnvelope envelope, PacketBridgeIdentityRequest packet) {
        if (netMod is not { } target)
            return;

        identityResponsePending = true;
        identityResponseTarget = target;
        if (!TryAnswerIdentityRequest(target)) {
            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                $"peer #{envelope.SenderPlayerID} asked for our identity; waiting for our own graphics and state"
            );
        }
    }

    /// <summary>
    /// Sends playerGraphics and playerState in that order, the order the real client can bind them in.
    /// The player's playerInfo is never part of the answer; receivers synthesise it from MiaoNet's own
    /// data. Returns false while either binding frame is still unknown.
    /// </summary>
    private static bool TryAnswerIdentityRequest(NetMod target) {
        if (!MiaoNetNetMods.IsConnected)
            return false;

        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        bool answered = false;
        foreach (CelesteNetFakeSession session in targets) {
            byte[][] frames = [];
            lock (sessions) {
                if (!localIdentities.TryGetValue(session, out CelesteNetIdentityCache? cache)
                    || !cache.TryBuildResponse(out frames))
                    continue;
            }

            try {
                foreach (byte[] frame in frames)
                    target.Send(new PacketBridgeFrame(frame));
            } catch (Exception e) {
                Logger.Log(
                    LogLevel.Warn, "cnet-bridge",
                    "identity answer failed: " + e.GetType().Name + ": " + e.Message
                );
                return false;
            }

            answered = true;
        }

        if (!answered)
            return false;

        identityResponsePending = false;
        identityResponseTarget = null;
        Logger.Log(LogLevel.Verbose, "cnet-bridge", "sent our identity frames to peers");
        return true;
    }

    /// <summary>
    /// A player leaving MiaoNet has to leave CelesteNet as well: the client only drops a player when it
    /// receives a dead <c>playerInfo</c>, and every mod that keys data by player id (CoopHelper, the
    /// appearance mods) would otherwise keep a player that is never coming back.
    /// </summary>
    private static void OnPlayerLeft(OnlinePlayer player) {
        uint id = (uint)player.ID;

        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        foreach (CelesteNetFakeSession session in targets)
            AnnouncePlayerLeft(session, id, "left CelesteNet");
    }

    /// <summary>
    /// Removes one remote player from what the local real client knows: the ghost-birth gate, the
    /// silence timer, and then the dead <c>playerInfo</c> that makes the client drop the ghost.
    /// </summary>
    /// <param name="reason">Only used for the log line.</param>
    private static void AnnouncePlayerLeft(CelesteNetFakeSession session, uint id, string reason) {
        ForgetPeer(session, id);

        if (!ForgetIntroduced(session, id))
            return;

        ForgetFeed(session, id);
        if (session.IsHandshaken)
            QueueSend(session, CelesteNetDataTypes.PlayerInfoLeave(id));

        Logger.Log(LogLevel.Verbose, "cnet-bridge", $"announced that #{id} {reason}");
    }

    /// <summary>Records that a peer is alive; the silence timer only ever restarts here.</summary>
    private static void WatchPeer(CelesteNetFakeSession session, uint id) {
        lock (sessions)
            if (peers.TryGetValue(session, out CelesteNetPeerWatch? watch))
                watch.Saw(id, Now);
    }

    private static void ForgetPeer(CelesteNetFakeSession session, uint id) {
        lock (sessions)
            if (peers.TryGetValue(session, out CelesteNetPeerWatch? watch))
                watch.Forget(id);
    }

    /// <summary>
    /// Without a MiaoNet connection this side cannot hear from anybody any more, so every player the
    /// local client knows about is gone as far as it is concerned.
    /// </summary>
    private static void AnnounceEveryoneLeft(CelesteNetFakeSession session) {
        uint[] ids;
        lock (sessions) {
            if (!introducedPlayers.TryGetValue(session, out HashSet<uint>? introduced))
                return;

            ids = [.. introduced];
            introduced.Clear();
            if (remoteFeeds.TryGetValue(session, out Dictionary<uint, CelesteNetRemoteFeed>? feeds))
                feeds.Clear();
            if (peers.TryGetValue(session, out CelesteNetPeerWatch? watch))
                watch.Clear();
        }

        foreach (uint id in ids) {
            if (session.IsHandshaken)
                QueueSend(session, CelesteNetDataTypes.PlayerInfoLeave(id));
        }

        if (ids.Length > 0)
            Logger.Log(LogLevel.Info, "cnet-bridge", $"MiaoNet went away, announced {ids.Length} players as left");
    }

    /// <summary>
    /// The bridge is being turned off, and the peers have no other way to learn that: they keep the last
    /// ghost they saw, frozen in place and still grabbable, because the local client's grab is a purely
    /// local behaviour. Say goodbye while the stream is still open, and give the writes a moment to land
    /// before the session goes away underneath them.
    /// </summary>
    private static void AnnounceEveryoneLeftBeforeShutdown() {
        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        int announced = 0;
        foreach (CelesteNetFakeSession session in targets) {
            lock (sessions) {
                if (introducedPlayers.TryGetValue(session, out HashSet<uint>? introduced))
                    announced += introduced.Count;
            }

            AnnounceEveryoneLeft(session);
        }

        if (announced == 0)
            return;

        Logger.Log(
            LogLevel.Info, "cnet-bridge",
            $"bridge is shutting down, told peers that {announced} players left"
        );

        Task[] tails;
        lock (sessions)
            tails = [.. sendTails.Values];

        try {
            Task.WaitAll(tails, ShutdownSendTimeout);
        } catch (Exception) {
            // Nothing left to tell anybody if a write fails; the session is going away regardless.
        }
    }

    /// <summary>
    /// Changing room or channel means the player's frames stop reaching our map, while CelesteNet keeps
    /// a ghost forever: announce the leave so the body goes away instead of freezing in place.
    /// </summary>
    private static void OnRemotePlayerLocationChanged(OnlinePlayer player, PacketPlayerLocationChangedNotification packet)
        => LeaveCelesteNetIfOutsideOurMap(player);

    private static void OnRemotePlayerChannelMoved(OnlinePlayer player, PacketPlayerChannelMovedNotification packet)
        => LeaveCelesteNetIfOutsideOurMap(player);

    /// <summary>Our own channel move drops every CelesteNet player; the next frames re-introduce them.</summary>
    private static void OnSelfChannelMoved(PacketPlayerChannelMovedResponse response) {
        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        foreach (CelesteNetFakeSession session in targets)
            AnnounceEveryoneLeft(session);
    }

    private static void LeaveCelesteNetIfOutsideOurMap(OnlinePlayer player) {
        if (IsSharingOurMap(player))
            return;

        uint id = (uint)player.ID;
        CelesteNetFakeSession[] targets;
        lock (sessions)
            targets = [.. sessions];

        foreach (CelesteNetFakeSession session in targets)
            AnnouncePlayerLeft(session, id, "left our map");
    }

    private static bool IsSharingOurMap(OnlinePlayer player) {
        ClientState? state = watchedContext?.ClientState;
        if (state is null)
            return false;

        return player.Location.IsInMap
            && state.Self.Location.IsInMap
            && player.Location.Map.Equals(state.Self.Location.Map);
    }

    private static void ForgetFeed(CelesteNetFakeSession session, uint id) {
        lock (sessions)
            if (remoteFeeds.TryGetValue(session, out Dictionary<uint, CelesteNetRemoteFeed>? feeds))
                feeds.Remove(id);
    }

    private static bool ForgetIntroduced(CelesteNetFakeSession session, uint id) {
        lock (sessions)
            return introducedPlayers.TryGetValue(session, out HashSet<uint>? introduced) && introduced.Remove(id);
    }

    private static void ReportDelivered(CelesteNetFakeSession session, string typeId, uint sender, int bytes) {
        bool first;
        lock (sessions)
            first = deliveredTypes.TryGetValue(session, out HashSet<string>? seen) && seen.Add(typeId);

        if (first) {
            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                $"deliver {typeId} from #{sender} ({bytes} B)"
            );
        }
    }

    private static void ReportSkip(CelesteNetFakeSession session, string typeId, string reason) {
        string key = typeId + " (" + reason + ")";
        bool first;
        lock (sessions)
            first = undeliverableTypes.TryGetValue(session, out HashSet<string>? seen) && seen.Add(key);

        if (first) {
            Logger.Log(
                LogLevel.Verbose, "cnet-bridge",
                $"skip remote {typeId}: {reason}"
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
    /// Null while there is no MiaoNet session yet: the fake server drops the connection in that case
    /// rather than handing out an id that every other player also has.
    /// </summary>
    private static (uint Id, string Name)? LocalIdentity() {
        ClientState? state = MiaoNetModule.Instance?.MiaoNetContext?.ClientState;
        OnlinePlayer? self = state?.Self;
        return self is null ? null : ((uint)self.ID, self.Info.Name);
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
