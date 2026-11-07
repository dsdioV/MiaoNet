using System.Collections.Generic;
using System.Net.Sockets;
using MiaoNet.CelesteNetBridge.Protocol;

namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// One connected real CelesteNet client. Speaks exactly the server side of the protocol:
/// teapot handshake, startup sequence, keepAlive, then it only relays frames.
/// </summary>
public sealed class CelesteNetFakeSession : IDisposable {
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly CelesteNetFakeServerOptions options;
    private readonly uint playerId;
    private readonly string playerName;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource cts = new();

    private int disposed;

    /// <summary>
    /// Whether the extra <c>ready</c> has already been sent. See <see cref="SendReadyAgainAsync"/>.
    /// </summary>
    private int readySentAgain;

    /// <summary>
    /// Type ids already reported. The client sends player frames at tick rate, so naming each kind
    /// once is what makes the log readable while still proving that the frame is coming through.
    /// </summary>
    private readonly HashSet<string> loggedTypes = [];

    public uint PlayerId => playerId;

    public string PlayerName => playerName;

    public bool IsHandshaken { get; private set; }

    public bool IsClosed => Volatile.Read(ref disposed) != 0;

    /// <summary>Raised for every inbound frame that is not swallowed by the handshake.</summary>
    public event Action<CelesteNetFakeSession, CelesteNetFrame>? FrameReceived;

    public event Action<CelesteNetFakeSession>? Closed;

    internal CelesteNetFakeSession(
        TcpClient client, CelesteNetFakeServerOptions options, uint playerId, string playerName
    ) {
        this.client = client;
        this.options = options;
        this.playerId = playerId;
        this.playerName = playerName;
        client.NoDelay = true;
        stream = client.GetStream();
        Log = options.Logger ?? (_ => { });
    }

    private Action<string> Log { get; }

    internal void Start()
        => _ = Task.Run(() => PumpAsync(cts.Token));

    /// <summary>Sends one DataType payload to the client, framed.</summary>
    public Task SendAsync(byte[] payload, CancellationToken token = default) {
        if (payload.Length > CelesteNetFraming.MaxPacketSize)
            throw new ArgumentException("payload exceeds MaxPacketSize", nameof(payload));

        return SendRawAsync(CelesteNetFraming.Wrap(payload), token);
    }

    public async Task SendRawAsync(byte[] bytes, CancellationToken token = default) {
        await sendLock.WaitAsync(token).ConfigureAwait(false);
        try {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        } finally {
            sendLock.Release();
        }
    }

    private async Task PumpAsync(CancellationToken token) {
        try {
            await HandshakeAsync(token).ConfigureAwait(false);
            await SendStartupSequenceAsync(token).ConfigureAwait(false);
            IsHandshaken = true;
            Log($"handshake done (playerInfo id={PlayerId}, name={PlayerName})");

            using CancellationTokenSource keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task keepAlive = KeepAliveLoopAsync(keepAliveCts.Token);

            await ReadFramesAsync(token).ConfigureAwait(false);

            keepAliveCts.Cancel();
            try {
                await keepAlive.ConfigureAwait(false);
            } catch (OperationCanceledException) {
            }
        } catch (OperationCanceledException) {
        } catch (Exception e) {
            if (!IsClosed)
                Log("session error: " + e.GetType().Name + ": " + e.Message);
        } finally {
            Dispose();
            Log("session ended");
            Closed?.Invoke(this);
        }
    }

    private async Task HandshakeAsync(CancellationToken token) {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.HandshakeTimeout);

        byte[] buffer = new byte[8192];
        int length = 0;
        while (true) {
            int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read <= 0)
                throw new IOException("client closed during handshake");
            length += read;

            if (!CelesteNetHandshake.TryReadRequest(buffer.AsSpan(0, length), out string firstLine, out int requestBytes))
                continue;

            Log($"request: {firstLine} ({requestBytes} B)");
            break;
        }

        await SendRawAsync(CelesteNetHandshake.BuildResponse(), token).ConfigureAwait(false);
        Log($"handshake response sent ({CelesteNetHandshake.ResponseSize} B)");
    }

    private async Task SendStartupSequenceAsync(CancellationToken token) {
        await SendNamedAsync("tickRate", CelesteNetDataTypes.TickRate(options.TickRate), token).ConfigureAwait(false);

        if (options.SendChannelList) {
            await SendNamedAsync("channelList", CelesteNetDataTypes.ChannelList(
                [(options.ChannelName, 0u, [PlayerId])]
            ), token).ConfigureAwait(false);
        }

        // The FIRST DataPlayerInfo the client receives is taken as the client itself
        // (CelesteNetClient.cs:323-326), which is what makes it adopt this id and name.
        await SendNamedAsync("playerInfo", CelesteNetDataTypes.PlayerInfo(PlayerId, PlayerName), token).ConfigureAwait(false);

        // DataReady is the only switch that sets IsReady; without it Start() waits forever.
        await SendNamedAsync("ready", CelesteNetDataTypes.Ready(), token).ConfigureAwait(false);
    }

    private async Task SendNamedAsync(string name, byte[] payload, CancellationToken token) {
        await SendAsync(payload, token).ConfigureAwait(false);
        Log($"sent {name} ({payload.Length} B)");
    }

    /// <summary>
    /// Sends <c>ready</c> a second time, once the client has proved it can send.
    /// <para>
    /// The startup sequence goes out back to back, and the client starts reading it before
    /// <c>CelesteNetClient.Con</c> is assigned: <c>CelesteNetClient.cs:234</c> assigns the field only
    /// after <c>Handshake.DoConnectionHandshake</c> returns, while the receive thread is already
    /// running from <c>CelesteNetClientTCPUDPConnection.cs:59-67</c>. When <c>ready</c> is dispatched
    /// inside that window, every mod handler that answers <c>DataReady</c> with a send hits
    /// <c>CelesteNetClient.Send</c> (<c>CelesteNetClient.cs:302-305</c>), whose <c>Con?.Send</c> does
    /// nothing at all: no exception, no log. Foxeline sends its tail settings only there, so its
    /// payload never left the process and the peers kept their defaults.
    /// </para>
    /// <para>
    /// The real server has no such window because it sends <c>DataReady</c> last
    /// (<c>CelesteNetPlayerSession.cs:309-310</c>). The client's own handling of <c>DataReady</c> is
    /// just <c>_ReadyEvent.Set()</c> (<c>CelesteNetClient.cs:352-354</c>), which is idempotent, so a
    /// second one is harmless. What makes it safe to send now is that the client has just sent a
    /// frame of its own: any outbound frame implies <c>Con != null</c>, because the connection object
    /// queues through itself while <c>Con</c> is only assigned afterwards
    /// (<c>CelesteNetClient.cs:219-228</c> starts the heartbeat timer before <c>:234</c>).
    /// </para>
    /// </summary>
    private async Task SendReadyAgainAsync(CancellationToken token) {
        if (Interlocked.Exchange(ref readySentAgain, 1) != 0)
            return;

        await SendAsync(CelesteNetDataTypes.Ready(), token).ConfigureAwait(false);
        Log("sent ready a second time (the client has sent a frame, so Con is assigned)");
    }

    private async Task KeepAliveLoopAsync(CancellationToken token) {
        byte[] frame = CelesteNetFraming.Wrap(CelesteNetDataTypes.KeepAlive());
        while (!token.IsCancellationRequested) {
            try {
                await Task.Delay(options.KeepAliveInterval, token).ConfigureAwait(false);
                await SendRawAsync(frame, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (Exception) {
                return;
            }
        }
    }

    private async Task ReadFramesAsync(CancellationToken token) {
        byte[] buffer = new byte[CelesteNetFraming.MaxPacketSize * 2 + 8];
        int length = 0;

        while (!token.IsCancellationRequested) {
            if (length == buffer.Length) {
                Log($"read buffer full at {length} B, dropping session");
                return;
            }

            int read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read <= 0)
                return;
            length += read;

            while (CelesteNetFraming.TryRead(buffer.AsSpan(0, length), out byte[] payload, out int consumed)) {
                length -= consumed;
                if (length > 0)
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, length);

                var frame = new CelesteNetFrame(CelesteNetTypeReader.TryReadTypeId(payload), payload);
                if (loggedTypes.Add(frame.TypeId ?? "<unparsed>"))
                    Log($"frame {frame.TypeId ?? "<unparsed>"} ({payload.Length} B)");

                // See SendReadyAgainAsync: the first frame proves the client can send, which means
                // every DataReady handler that wanted to send could not do so until now.
                await SendReadyAgainAsync(token).ConfigureAwait(false);
                try {
                    FrameReceived?.Invoke(this, frame);
                } catch (Exception e) {
                    Log("handler threw: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        cts.Cancel();
        try {
            stream.Dispose();
        } catch (Exception) {
        }
        try {
            client.Dispose();
        } catch (Exception) {
        }
        cts.Dispose();
        sendLock.Dispose();
    }
}
