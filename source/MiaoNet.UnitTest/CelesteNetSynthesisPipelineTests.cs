using MiaoNet.CelesteNetBridge;
using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;
using MiaoNet.Shared;

namespace MiaoNet.UnitTest;

/// <summary>
/// The offline end-to-end suite for the synthesis pipeline: frame classification, stream selection,
/// ghost-birth ordering, the synthesizer and the fake server's send path, all in one process and
/// without Celeste.
/// </summary>
/// <remarks>
/// The real <c>CelesteNetBridgeEntry</c> needs Celeste and is not linked into this project, so the
/// wiring it does around the Celeste-free components is mirrored here. Every component under test is
/// the production one, so a contract change in any of them fails these tests even though the bridge
/// itself is not compiled in.
/// </remarks>
[TestClass]
[DoNotParallelize] // MiaoNetNetMods is static and not thread-safe.
public sealed class CelesteNetSynthesisRoutingTests
{
    private const string LegacyStreamName = KnownNetMods.LegacyBridgeName;
    private const string SharedSource = "CelesteNet.Shared";
    private const string ThirdPartySource = "SomeOtherMod";

    private static readonly byte[] Payload = [0x51, 0x52, 0x53];

    /// <summary>
    /// (a) A third-party DataType stays on the legacy bridge stream while the player pipeline frames
    /// travel on the one player stream the client declared.
    /// </summary>
    [TestMethod]
    public void ThirdPartyFramesGoToBaseWhilePlayerPipelineFramesGoToThePlayerStream()
    {
        using StreamHarness streams = new();

        Assert.AreEqual(KnownNetMods.CelesteNetPlayerSynth, streams.PlayerStreamName);
        Assert.AreNotEqual(streams.LegacyIndex, streams.PlayerIndex);

        foreach ((string typeId, string? source) in new (string, string?)[] {
            ("chat", SharedSource),
            ("corkr900CoopHelper_BundledEntityUpdate_1_1_0", ThirdPartySource),
            ("aTypeThisClientDoesNotKnow", null),
        }) {
            CelesteNetFrameLane lane = Classify(typeId, source);
            Assert.AreEqual(CelesteNetFrameLane.ThirdParty, lane, typeId);
            Assert.AreEqual(streams.LegacyIndex, streams.SendFor(lane), typeId);
        }

        foreach (string typeId in new[] { "playerGraphics", "playerState" }) {
            CelesteNetFrameLane lane = Classify(typeId, SharedSource);
            Assert.AreEqual(CelesteNetFrameLane.PlayerStream, lane, typeId);
            Assert.AreEqual(streams.PlayerIndex, streams.SendFor(lane), typeId);
        }
    }

    /// <summary>
    /// (a) The bridge always synthesises: <c>.PlayerSynth</c> is the only player stream, and the
    /// per-tick <c>playerFrame</c> becomes an appearance keyframe instead of a relayed frame.
    /// </summary>
    [TestMethod]
    public void APerTickFrameBecomesAnAppearanceKeyframe()
    {
        using StreamHarness streams = new();
        Assert.AreEqual(KnownNetMods.CelesteNetPlayerSynth, streams.PlayerStreamName);

        CelesteNetFrameLane lane = Classify("playerFrame", SharedSource);
        Assert.AreEqual(CelesteNetFrameLane.AppearanceKeyframe, lane);
        Assert.AreNotEqual(
            CelesteNetFrameLane.PlayerStream, lane,
            "a per-tick frame must never be selected for the synthesis stream"
        );

        // The keyframe and the ghost-birth frames still travel on that one player stream.
        Assert.AreEqual(streams.PlayerIndex, streams.SendFor(lane));
        foreach (string typeId in new[] { "playerGraphics", "playerState" }) {
            CelesteNetFrameLane ghostBirth = Classify(typeId, SharedSource);
            Assert.AreEqual(CelesteNetFrameLane.PlayerStream, ghostBirth, typeId);
            Assert.AreEqual(streams.PlayerIndex, streams.SendFor(ghostBirth), typeId);
        }
    }

    /// <summary>
    /// (d) In every classification case a frame either goes nowhere or to exactly one of the two
    /// streams: a relayable frame can never be emitted on both.
    /// </summary>
    [TestMethod]
    public void EveryClassifiedFrameTargetsExactlyOneStream()
    {
        using StreamHarness streams = new();

        foreach ((string typeId, string? source, bool aboutLocalPlayer) in ClassificationCases()) {
            CelesteNetFrameLane lane = CelesteNetFrameRouting.Classify(
                typeId, source, aboutLocalPlayer
            );

            switch (lane) {
                case CelesteNetFrameLane.Drop:
                    streams.AssertNothingSent(lane);
                    break;

                case CelesteNetFrameLane.ThirdParty:
                    Assert.AreEqual(streams.LegacyIndex, streams.SendFor(lane), typeId);
                    break;

                default:
                    Assert.IsTrue(
                        lane is CelesteNetFrameLane.PlayerStream or CelesteNetFrameLane.AppearanceKeyframe,
                        typeId
                    );
                    Assert.AreEqual(streams.PlayerIndex, streams.SendFor(lane), typeId);
                    break;
            }
        }
    }

    private static CelesteNetFrameLane Classify(string typeId, string? source)
        => CelesteNetFrameRouting.Classify(
            typeId, source, aboutLocalPlayer: true
        );

    private static IEnumerable<(string TypeId, string? Source, bool AboutLocalPlayer)> ClassificationCases()
    {
        // Third-party frames and upstream frames without a player reference stay on the legacy stream.
        yield return ("chat", SharedSource, true);
        yield return ("modFrame", ThirdPartySource, true);
        yield return ("unknownWithNoSource", null, true);
        yield return ("someFutureCelesteType", SharedSource, true);

        // Session-local frames never leave this machine.
        yield return ("keepAlive", SharedSource, true);
        yield return ("stringMap", SharedSource, true);
        yield return ("tickRate", SharedSource, true);
        yield return ("playerInfo", SharedSource, true);

        // A remote player's record is not session state, and every player-pipeline type is bound to it.
        foreach (string typeId in CelesteNetFrameRouting.PlayerStreamTypeIds) {
            yield return (typeId, SharedSource, typeId != "playerInfo");
        }
    }

    private static NetMod CreateStream(string name)
        => new(
            name,
            KnownNetMods.CelesteNetPlayerProtocolVersion,
            NetModPacketEntry.Create<PacketBridgeFrame>(PacketScope.Map, static (_, _) => { }),
            NetModPacketEntry.Create<PacketBridgeIdentityRequest>(PacketScope.Map, static (_, _) => { })
        );

    /// <summary>
    /// The bridge's stream declaration: the legacy stream plus the one player stream it always
    /// declares, and the same lane-to-stream decision <c>OnLocalFrame</c> makes.
    /// </summary>
    private sealed class StreamHarness : IDisposable
    {
        private readonly MockTransport transport = new();
        private readonly List<NetMod> registered = [];

        public StreamHarness()
        {
            string playerName = KnownNetMods.CelesteNetPlayerSynth;

            MiaoNetNetMods.SetTransport(transport);
            Legacy = Register(LegacyStreamName, out byte legacyIndex);
            LegacyIndex = legacyIndex;
            Player = Register(playerName, out byte playerIndex);
            PlayerIndex = playerIndex;
        }

        public NetMod Legacy { get; }

        public NetMod Player { get; }

        public byte LegacyIndex { get; }

        public byte PlayerIndex { get; }

        public string PlayerStreamName => Player.Name;

        /// <summary>Routes one frame the way the bridge does and returns the stream index it landed on.</summary>
        public byte SendFor(CelesteNetFrameLane lane)
        {
            NetMod? target = lane switch {
                CelesteNetFrameLane.ThirdParty => Legacy,
                CelesteNetFrameLane.PlayerStream or CelesteNetFrameLane.AppearanceKeyframe => Player,
                _ => null,
            };

            Assert.IsNotNull(target, "a frame that is not dropped must have exactly one target stream");

            transport.Sent.Clear();
            target.Send(new PacketBridgeFrame(Payload));
            Assert.HasCount(1, transport.Sent, "one frame must not be emitted on two streams");
            return transport.Sent[0].NetModID;
        }

        public void AssertNothingSent(CelesteNetFrameLane lane)
        {
            Assert.AreEqual(CelesteNetFrameLane.Drop, lane);
            transport.Sent.Clear();
            Assert.HasCount(0, transport.Sent, "a dropped frame must not be emitted anywhere");
        }

        private NetMod Register(string name, out byte index)
        {
            NetMod mod = MiaoNetNetMods.Register(CreateStream(name));
            registered.Add(mod);

            transport.Sent.Clear();
            mod.Send(new PacketBridgeFrame(Payload));
            Assert.HasCount(1, transport.Sent);
            index = transport.Sent[0].NetModID;
            transport.Sent.Clear();
            return mod;
        }

        public void Dispose()
        {
            for (int i = registered.Count - 1; i >= 0; i--)
                registered[i].Dispose();
            MiaoNetNetMods.SetTransport(null);
        }
    }

    private sealed class MockTransport : INetModTransport
    {
        public List<(byte NetModID, byte LocalID, PacketRoute Route, IContextualPacket Packet)> Sent { get; } = [];

        public void Send(byte netModID, byte localID, PacketRoute route, IContextualPacket packet)
            => Sent.Add((netModID, localID, route, packet));
    }
}

/// <summary>
/// Proves the delivery half of the pipeline through the real fake-server send path: the order the
/// real CelesteNet client sees the frames in, the tombstone guard and the one-frame-per-engine-frame
/// coalescing.
/// </summary>
[TestClass]
public sealed class CelesteNetSynthesisDeliveryTests
{
    private const uint LocalId = 1;
    private const string LocalName = "Self";

    /// <summary>
    /// (b) The client receives playerInfo, playerGraphics, playerState and only then the synthesised
    /// playerFrame. A synthesised frame queued while the ghost-birth frames are still missing is
    /// encoded but never emitted.
    /// </summary>
    [TestMethod]
    public async Task DeliversPlayerInfoGraphicsStateThenTheSynthesisedFrame()
    {
        using FakeClientLink link = await FakeClientLink.StartAsync();
        SynthesisPipeline pipeline = new(link.Session);

        // Two MiaoNet deltas arrive before the player exists for the local client.
        await pipeline.TickAsync(1, Input(7, positionX: 10f));
        await pipeline.TickAsync(2, Input(7, positionX: 12f));
        Assert.IsEmpty(SynthesisedFrames(pipeline), "the ghost cannot be born yet");
        Assert.IsFalse(pipeline.IsGhostReady(7));

        await pipeline.AcceptRemoteAsync(
            7, "Zed", CelesteNetRemoteFeed.PlayerGraphicsTypeId, GraphicsPayload()
        );
        Assert.IsFalse(pipeline.IsGhostReady(7), "playerGraphics alone does not create the ghost");

        await pipeline.AcceptRemoteAsync(
            7, "Zed", CelesteNetRemoteFeed.PlayerStateTypeId, StatePayload()
        );
        Assert.IsTrue(pipeline.IsGhostReady(7), "the gate opens once graphics and state are through");

        await pipeline.TickAsync(3, Input(7, positionX: 14f));

        string?[] received = await ReceiveTypesAsync(link.Client, 4);
        CollectionAssert.AreEqual(
            new string?[] { "playerInfo", "playerGraphics", "playerState", "playerFrame" },
            received,
            "the client must be given the ghost-birth frames before any frame that names the ghost"
        );

        List<(string TypeId, byte[] Payload)> synthesised = SynthesisedFrames(pipeline);
        Assert.HasCount(1, synthesised);
        CelesteNetPlayerFrame frame = Decode(synthesised[0].Payload);
        Assert.AreEqual(7u, frame.PlayerID);
        Assert.AreEqual(
            12f, frame.PositionX,
            "the newest delta queued while the ghost could not be born is the one delivered"
        );
        Assert.AreEqual(0, frame.AnimationID, "the animation table came from the relayed playerGraphics");
    }

    /// <summary>
    /// (c) After a tombstone no synthesised frame is delivered, neither one still queued in the
    /// synthesizer nor one already encoded but not yet sent.
    /// </summary>
    [TestMethod]
    public async Task TombstoneSuppressesQueuedAndEncodedSynthesisedFrames()
    {
        using FakeClientLink link = await FakeClientLink.StartAsync();
        SynthesisPipeline pipeline = new(link.Session);

        // A frame is queued in the current engine frame, then the player is tombstoned before the
        // next engine frame drains it.
        await BringGhostToLifeAsync(pipeline, 7, "Zed", link.Client);
        await pipeline.TickAsync(1, Input(7, positionX: 1f));
        await pipeline.TickAsync(2, Input(7, positionX: 2f));
        Assert.HasCount(1, SynthesisedFrames(pipeline));
        CollectionAssert.AreEqual(new string?[] { "playerFrame" }, await ReceiveTypesAsync(link.Client, 1));

        await pipeline.TickAsync(2, Input(7, positionX: 3f)); // a backlog delta replaces the pending frame
        await pipeline.TombstoneAsync(7);                     // drops the pending frame, queues the leave
        await pipeline.TickAsync(3, Input(7, positionX: 4f)); // drain empty, queue refused

        Assert.HasCount(
            1, SynthesisedFrames(pipeline),
            "a frame queued before the tombstone must never be delivered"
        );
        Assert.IsTrue(pipeline.IsTombstoned(7));

        // The leave is the very next frame: nothing was synthesised after it.
        CollectionAssert.AreEqual(new string?[] { "playerInfo" }, await ReceiveTypesAsync(link.Client, 1));
        await pipeline.SendSentinelAsync("chat", CelesteNetDataTypes.Encode(0, "chat", [0x01]));
        CollectionAssert.AreEqual(new string?[] { "chat" }, await ReceiveTypesAsync(link.Client, 1));

        // A frame encoded (drained) while the player was alive, then tombstoned before the send
        // decision, must be refused by the delivery-time check.
        await BringGhostToLifeAsync(pipeline, 8, "Yan", link.Client);
        await pipeline.TickAsync(10, Input(8, positionX: 5f));
        IReadOnlyList<CelesteNetSynthesisedFrame> encoded = pipeline.DrainFramesForTest(11);
        Assert.HasCount(1, encoded);

        await pipeline.TombstoneAsync(8);
        Assert.IsFalse(
            pipeline.DeliverFrame(encoded[0]),
            "a frame encoded before the tombstone must be refused after it"
        );

        CollectionAssert.AreEqual(new string?[] { "playerInfo" }, await ReceiveTypesAsync(link.Client, 1));
        await pipeline.SendSentinelAsync("chat", CelesteNetDataTypes.Encode(0, "chat", [0x02]));
        CollectionAssert.AreEqual(new string?[] { "chat" }, await ReceiveTypesAsync(link.Client, 1));
    }

    /// <summary>
    /// (e) A backlog of MiaoNet deltas inside one engine frame coalesces into exactly one synthesised
    /// frame, so the client gets one frame per player per engine frame.
    /// </summary>
    [TestMethod]
    public async Task SeveralDeltasInOneEngineFrameYieldOneSynthesisedFrame()
    {
        using FakeClientLink link = await FakeClientLink.StartAsync();
        SynthesisPipeline pipeline = new(link.Session);
        await BringGhostToLifeAsync(pipeline, 9, "Bud", link.Client);
        Assert.IsEmpty(SynthesisedFrames(pipeline));

        await pipeline.TickAsync(5, Input(9, positionX: 10f));
        await pipeline.TickAsync(5, Input(9, positionX: 20f));
        await pipeline.TickAsync(5, Input(9, positionX: 30f));
        Assert.IsEmpty(SynthesisedFrames(pipeline), "the current engine frame is still open");

        await pipeline.TickAsync(6, Input(9, positionX: 40f));
        Assert.HasCount(1, SynthesisedFrames(pipeline));
        Assert.AreEqual(30f, Decode(SynthesisedFrames(pipeline)[0].Payload).PositionX);

        await pipeline.TickAsync(7, Input(9, positionX: 50f));
        Assert.HasCount(2, SynthesisedFrames(pipeline));
        Assert.AreEqual(40f, Decode(SynthesisedFrames(pipeline)[1].Payload).PositionX);

        // One frame per player per engine frame on the wire as well, never a burst.
        CollectionAssert.AreEqual(
            new string?[] { "playerFrame", "playerFrame" },
            await ReceiveTypesAsync(link.Client, 2)
        );
    }

    private static async Task BringGhostToLifeAsync(
        SynthesisPipeline pipeline, uint playerId, string name, CelesteNetTestClient client
    )
    {
        await pipeline.AcceptRemoteAsync(
            playerId, name, CelesteNetRemoteFeed.PlayerGraphicsTypeId, GraphicsPayload()
        );
        await pipeline.AcceptRemoteAsync(
            playerId, name, CelesteNetRemoteFeed.PlayerStateTypeId, StatePayload()
        );

        Assert.IsTrue(pipeline.IsGhostReady(playerId));
        CollectionAssert.AreEqual(
            new string?[] { "playerInfo", "playerGraphics", "playerState" },
            await ReceiveTypesAsync(client, 3)
        );
    }

    private static CelesteNetPlayerFrameInput Input(uint playerId, float positionX)
        => new() {
            PlayerID = playerId,
            AnimationName = "idle",
            PositionX = positionX,
            DeltaSeconds = 1f / 60f,
        };

    private static byte[] GraphicsPayload()
        => CelesteNetTestPayloads.PlayerGraphics(
            animations: ["idle", "walk"],
            hairCount: 4,
            hairTextures: ["characters/player/bangs0"],
            depth: 0,
            spriteMode: 0
        );

    private static byte[] StatePayload()
        => CelesteNetDataTypes.Encode(0, CelesteNetRemoteFeed.PlayerStateTypeId, [0x01, 0x02, 0x03]);

    private static List<(string TypeId, byte[] Payload)> SynthesisedFrames(SynthesisPipeline pipeline)
        => [.. pipeline.Delivered.Where(entry => entry.TypeId == CelesteNetPlayerFrame.TypeId)];

    private static CelesteNetPlayerFrame Decode(byte[] payload)
    {
        Assert.IsTrue(CelesteNetPlayerFrame.TryDecode(payload, out CelesteNetPlayerFrame? frame));
        Assert.IsNotNull(frame);
        return frame;
    }

    private static async Task<string?[]> ReceiveTypesAsync(CelesteNetTestClient client, int count)
    {
        string?[] typeIds = new string?[count];
        for (int i = 0; i < count; i++)
            typeIds[i] = CelesteNetTypeReader.TryReadTypeId(await client.ReceiveAsync());
        return typeIds;
    }

    private static async Task WaitForAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline)
                Assert.Fail(message);
            await Task.Delay(5);
        }
    }

    /// <summary>One connected fake client and the server-side session the pipeline sends through.</summary>
    private sealed class FakeClientLink : IDisposable
    {
        private readonly CelesteNetFakeServer server;
        private readonly CelesteNetTestClient client;

        private FakeClientLink(
            CelesteNetFakeServer server, CelesteNetTestClient client, CelesteNetFakeSession session
        )
        {
            this.server = server;
            this.client = client;
            Session = session;
        }

        public CelesteNetFakeSession Session { get; }

        public CelesteNetTestClient Client => client;

        public static async Task<FakeClientLink> StartAsync()
        {
            TaskCompletionSource<CelesteNetFakeSession> started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            CelesteNetFakeServer server = new(new CelesteNetFakeServerOptions {
                Port = 0,
                IdentityProvider = () => (LocalId, LocalName),
                // The heartbeat must not interleave with the ordered reads; the test drives the frames.
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            server.SessionStarted += session => started.TrySetResult(session);
            server.Start();

            CelesteNetTestClient client = new(server.Port);
            try {
                await client.HandshakeAsync();

                // tickRate, channelList, the local client's own playerInfo, ready.
                for (int i = 0; i < 4; i++)
                    await client.ReceiveAsync();

                CelesteNetFakeSession session = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForAsync(
                    () => session.IsHandshaken,
                    "the fake session never finished its startup sequence"
                );
                return new FakeClientLink(server, client, session);
            } catch {
                client.Dispose();
                server.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            client.Dispose();
            server.Dispose();
        }
    }

    /// <summary>
    /// The Celeste-free remote half of the bridge, mirrored from <c>CelesteNetBridgeEntry</c>: one
    /// synthesizer, one ghost-birth gate per player, the tombstones and the serialised send chain.
    /// </summary>
    private sealed class SynthesisPipeline
    {
        private readonly CelesteNetFakeSession session;
        private readonly CelesteNetFrameSynthesizer synthesizer = new();
        private readonly Dictionary<uint, CelesteNetRemoteFeed> feeds = [];
        private readonly HashSet<uint> introduced = [];
        private readonly List<(string TypeId, byte[] Payload)> delivered = [];
        private readonly object gate = new();
        private Task sendTail = Task.CompletedTask;

        public SynthesisPipeline(CelesteNetFakeSession session) => this.session = session;

        /// <summary>Everything already written to the client, in wire order.</summary>
        public IReadOnlyList<(string TypeId, byte[] Payload)> Delivered {
            get {
                lock (gate) {
                    return [.. delivered];
                }
            }
        }

        public bool IsGhostReady(uint playerId)
        {
            lock (gate) {
                return feeds.TryGetValue(playerId, out CelesteNetRemoteFeed? feed) && feed.IsGhostReady;
            }
        }

        public bool IsTombstoned(uint playerId)
        {
            lock (gate) {
                return synthesizer.IsTombstoned(playerId);
            }
        }

        /// <summary>One remote frame arriving over MiaoNet, handled the way <c>OnRemoteFrame</c> does.</summary>
        public Task AcceptRemoteAsync(uint playerId, string name, string typeId, byte[] payload)
        {
            lock (gate) {
                if (introduced.Add(playerId)) {
                    // The client cannot resolve a frame that names a player it has never seen.
                    synthesizer.Revive(playerId);
                    QueueSendLocked(CelesteNetDataTypes.PlayerInfo(playerId, name));
                }

                if (typeId == CelesteNetRemoteFeed.PlayerGraphicsTypeId) {
                    if (CelesteNetPlayerGraphics.TryDecode(payload, out CelesteNetPlayerGraphics? graphics))
                        synthesizer.AcceptGraphics(playerId, graphics);
                } else if (typeId == CelesteNetRemoteFeed.PlayerFrameTypeId) {
                    // Local synthesis keeps only the appearance template and never relays the real frame.
                    if (CelesteNetPlayerFrame.TryDecode(payload, out CelesteNetPlayerFrame? appearance))
                        synthesizer.AcceptAppearance(playerId, appearance);
                    return sendTail;
                }

                CelesteNetRemoteFeed feed = FeedLocked(playerId);
                if (feed.Accept(typeId, payload) == CelesteNetRemoteFeedAction.Hold)
                    return sendTail;

                QueueSendLocked(CelesteNetFrameRewriter.RewriteSource(payload, playerId));
                if (feed.TakeHeldFrame() is { } held)
                    QueueSendLocked(CelesteNetFrameRewriter.RewriteSource(held, playerId));
                return sendTail;
            }
        }

        /// <summary>One MiaoNet player tick, handled the way <c>OnSynthesisedFrameTick</c> does.</summary>
        public Task TickAsync(ulong engineFrame, CelesteNetPlayerFrameInput input)
        {
            lock (gate) {
                // Frames captured before this engine frame are due now; the current one stays open.
                foreach (CelesteNetSynthesisedFrame frame in synthesizer.Drain(engineFrame)) {
                    if (CanDeliverLocked(frame.PlayerID))
                        QueueSendLocked(frame.Payload);
                }

                synthesizer.Queue(input, engineFrame);
                return sendTail;
            }
        }

        /// <summary>Encodes the frames that are due, without the delivery check, as a seam for the test.</summary>
        public IReadOnlyList<CelesteNetSynthesisedFrame> DrainFramesForTest(ulong engineFrame)
        {
            lock (gate) {
                return synthesizer.Drain(engineFrame);
            }
        }

        /// <summary>The delivery-time tombstone/ghost check <c>CanDeliverSynthesised</c> performs.</summary>
        public bool DeliverFrame(CelesteNetSynthesisedFrame frame)
        {
            lock (gate) {
                if (!CanDeliverLocked(frame.PlayerID))
                    return false;

                QueueSendLocked(frame.Payload);
                return true;
            }
        }

        /// <summary>Announces a player as gone, the way <c>OnPlayerLeft</c> does.</summary>
        public Task TombstoneAsync(uint playerId)
        {
            lock (gate) {
                // Before the dead playerInfo: the client frees the reference when it reads it.
                synthesizer.Tombstone(playerId);
                if (introduced.Remove(playerId)) {
                    feeds.Remove(playerId);
                    QueueSendLocked(CelesteNetDataTypes.PlayerInfoLeave(playerId));
                }

                return sendTail;
            }
        }

        /// <summary>Sends one marker frame on the same ordered chain, for absence assertions.</summary>
        public Task SendSentinelAsync(string typeId, byte[] payload)
        {
            lock (gate) {
                QueueSendLocked(payload);
                return sendTail;
            }
        }

        private bool CanDeliverLocked(uint playerId)
            => session.IsHandshaken
                && introduced.Contains(playerId)
                && feeds.TryGetValue(playerId, out CelesteNetRemoteFeed? feed)
                && feed.IsGhostReady
                && !synthesizer.IsTombstoned(playerId);

        private CelesteNetRemoteFeed FeedLocked(uint playerId)
        {
            if (!feeds.TryGetValue(playerId, out CelesteNetRemoteFeed? feed))
                feeds[playerId] = feed = new();
            return feed;
        }

        private void QueueSendLocked(byte[] payload)
        {
            Task previous = sendTail;
            sendTail = SendAfterAsync(previous, payload);
        }

        private async Task SendAfterAsync(Task previous, byte[] payload)
        {
            try {
                await previous.ConfigureAwait(false);
            } catch (Exception) {
                // The previous send failed for the same reason this one will; keep the chain alive.
            }

            await session.SendAsync(payload).ConfigureAwait(false);
            string typeId = CelesteNetTypeReader.TryReadTypeId(payload) ?? "<unparsed>";
            lock (gate) {
                delivered.Add((typeId, payload));
            }
        }
    }
}
