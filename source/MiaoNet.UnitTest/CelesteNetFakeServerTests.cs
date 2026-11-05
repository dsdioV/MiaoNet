using System.Net;
using System.Net.Sockets;
using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.CelesteNetBridge.Server;

namespace MiaoNet.UnitTest;

[TestClass]
public class CelesteNetFakeServerTests {

    private static CelesteNetFakeServer StartServer(Action<CelesteNetFakeSession>? onSession = null) {
        var options = new CelesteNetFakeServerOptions {
            Port = 0,
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        };
        var server = new CelesteNetFakeServer(options);
        if (onSession != null)
            server.SessionStarted += onSession;
        server.Start();
        return server;
    }

    [TestMethod]
    public async Task ServesHandshakeStartupSequenceAndKeepAlive() {
        using CelesteNetFakeServer server = StartServer();
        using CelesteNetTestClient client = new(server.Port);

        await client.HandshakeAsync();

        byte[] tickRate = await client.ReceiveAsync();
        Assert.AreEqual("tickRate", CelesteNetTypeReader.TryReadTypeId(tickRate));
        CollectionAssert.AreEqual(CelesteNetDataTypes.TickRate(60f), tickRate);

        byte[] channelList = await client.ReceiveAsync();
        Assert.AreEqual("channelList", CelesteNetTypeReader.TryReadTypeId(channelList));
        CollectionAssert.AreEqual(
            CelesteNetDataTypes.ChannelList([("main", 0u, [1u])]),
            channelList
        );

        byte[] playerInfo = await client.ReceiveAsync();
        Assert.AreEqual("playerInfo", CelesteNetTypeReader.TryReadTypeId(playerInfo));
        CollectionAssert.AreEqual(CelesteNetDataTypes.PlayerInfo(1, "Player"), playerInfo);

        byte[] ready = await client.ReceiveAsync();
        Assert.AreEqual("ready", CelesteNetTypeReader.TryReadTypeId(ready));
        CollectionAssert.AreEqual(CelesteNetDataTypes.Ready(), ready);

        byte[] keepAlive = await client.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("keepAlive", CelesteNetTypeReader.TryReadTypeId(keepAlive));
    }

    [TestMethod]
    public void FallsBackToAFreePortWhenThePreferredOneIsBusy() {
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.ExclusiveAddressUse = true;
        occupied.Start();
        int busy = ((IPEndPoint)occupied.LocalEndpoint).Port;

        using CelesteNetFakeServer server = new(new CelesteNetFakeServerOptions {
            Port = busy,
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        });
        server.Start();

        Assert.IsTrue(server.UsedFallbackPort, "a busy port must fall back instead of failing");
        Assert.AreNotEqual(busy, server.Port);
    }

    [TestMethod]
    public async Task RelaysInboundFramesToTheServerSideByteForByte() {
        TaskCompletionSource<CelesteNetFrame> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CelesteNetFakeServer server = StartServer(session => session.FrameReceived += (_, frame) => received.TrySetResult(frame));
        using CelesteNetTestClient client = new(server.Port);

        await client.HandshakeAsync();
        for (int i = 0; i < 4; i++)
            await client.ReceiveAsync();

        byte[] payload = CelesteNetDataTypes.Encode(
            0,
            "corkr900CoopHelper_BundledEntityUpdate_1_1_0",
            [1, 2, 3, 4, 5, 6, 7, 8]
        );
        await client.SendAsync(payload);

        CelesteNetFrame frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("corkr900CoopHelper_BundledEntityUpdate_1_1_0", frame.TypeId);
        CollectionAssert.AreEqual(payload, frame.Payload);
    }

    [TestMethod]
    public async Task ReassemblesFramesSplitAcrossTcpWrites() {
        TaskCompletionSource<CelesteNetFrame> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CelesteNetFakeServer server = StartServer(session => session.FrameReceived += (_, frame) => received.TrySetResult(frame));
        using CelesteNetTestClient client = new(server.Port);

        await client.HandshakeAsync();
        for (int i = 0; i < 4; i++)
            await client.ReceiveAsync();

        byte[] payload = CelesteNetDataTypes.Encode(0, "splitType", [9, 9, 9]);
        byte[] framed = CelesteNetFraming.Wrap(payload);
        foreach (byte b in framed) {
            await client.SendRawAsync([b]);
            await Task.Delay(2);
        }

        CelesteNetFrame frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("splitType", frame.TypeId);
        CollectionAssert.AreEqual(payload, frame.Payload);
    }

    /// <summary>
    /// The startup sequence is written before the client can send anything, and the client drops a
    /// <c>ready</c> that lands before <c>CelesteNetClient.Con</c> is assigned: every mod handler that
    /// answers <c>DataReady</c> with a send then silently sends nothing. The fake server therefore
    /// repeats <c>ready</c> once the client has proved it can send, and only once.
    /// </summary>
    [TestMethod]
    public async Task RepeatsReadyOnceAfterTheClientStartsSending() {
        using CelesteNetFakeServer server = StartServer();
        using CelesteNetTestClient client = new(server.Port);

        await client.HandshakeAsync();
        for (int i = 0; i < 4; i++)
            await client.ReceiveAsync();

        await client.SendAsync(CelesteNetDataTypes.Encode(0, "firstFrame", [1]));

        Assert.AreEqual("ready", await ReceiveTypeIdAsync(client, "ready", TimeSpan.FromSeconds(5)));

        // Any further frame from the client must not produce another ready.
        await client.SendAsync(CelesteNetDataTypes.Encode(0, "secondFrame", [2]));

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < deadline) {
            byte[] frame;
            try {
                frame = await client.ReceiveAsync(deadline - DateTime.UtcNow);
            } catch (Exception e) when (e is TimeoutException or OperationCanceledException) {
                break;
            }

            Assert.AreNotEqual("ready", CelesteNetTypeReader.TryReadTypeId(frame), "sent a second extra ready");
        }
    }

    private static async Task<string?> ReceiveTypeIdAsync(CelesteNetTestClient client, string expected, TimeSpan timeout) {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            byte[] frame = await client.ReceiveAsync(deadline - DateTime.UtcNow);
            if (CelesteNetTypeReader.TryReadTypeId(frame) == expected)
                return expected;
        }

        Assert.Fail($"never received {expected}");
        return null;
    }

    [TestMethod]
    public async Task SessionIsMarkedClosedWhenTheClientDisconnects() {
        TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CelesteNetFakeServer server = StartServer(session => session.Closed += _ => closed.TrySetResult(true));
        using (CelesteNetTestClient client = new(server.Port)) {
            await client.HandshakeAsync();
            for (int i = 0; i < 4; i++)
                await client.ReceiveAsync();
        }

        Assert.IsTrue(await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task DropsConnectionsThatArriveBeforeAnIdentityIsKnown() {
        int sessions = 0;
        using CelesteNetFakeServer server = new(new CelesteNetFakeServerOptions {
            Port = 0,
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            IdentityProvider = () => null,
        });
        server.SessionStarted += _ => Interlocked.Increment(ref sessions);
        server.Start();

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        client.ReceiveTimeout = 5000;

        int read;
        try {
            read = client.GetStream().Read(new byte[1], 0, 1);
        } catch (IOException) {
            read = 0; // a reset counts as a drop too
        }

        Assert.AreEqual(0, read, "the connection must be closed instead of served under a made-up id");
        Assert.AreEqual(0, sessions, "no session may start when there is no identity to give it");
    }

    [TestMethod]
    public async Task UsesTheIdentityProviderForThePlayerInfoItSends() {
        using CelesteNetFakeServer server = new(new CelesteNetFakeServerOptions {
            Port = 0,
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            IdentityProvider = () => (7u, "Zed"),
        });
        server.Start();

        using CelesteNetTestClient client = new(server.Port);
        await client.HandshakeAsync();

        for (int i = 0; i < 2; i++)
            await client.ReceiveAsync();
        byte[] playerInfo = await client.ReceiveAsync();

        Assert.AreEqual("playerInfo", CelesteNetTypeReader.TryReadTypeId(playerInfo));
        CollectionAssert.AreEqual(CelesteNetDataTypes.PlayerInfo(7, "Zed"), playerInfo);
    }
}
