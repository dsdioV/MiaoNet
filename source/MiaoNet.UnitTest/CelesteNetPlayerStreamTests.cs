using System.Collections.Generic;

using MiaoNet.CelesteNetBridge;
using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.Shared;

namespace MiaoNet.UnitTest;

[TestClass]
[DoNotParallelize] // MiaoNetNetMods is static and not thread-safe.
public sealed class CelesteNetPlayerStreamTests
{
    private const string LegacyNetModName = KnownNetMods.LegacyBridgeName;

    private static readonly byte[] FramePayload = [0x01, 0x02, 0x03];

    [TestMethod]
    public void ThePlayerStreamDeclaresTheLegacyPacketsInOrder()
    {
        MockTransport transport = new();
        MiaoNetNetMods.SetTransport(transport);

        List<byte[]> frames = [];
        int requests = 0;
        NetMod legacy = MiaoNetNetMods.Register(CreateStream(
            LegacyNetModName,
            (_, packet) => frames.Add(packet.Payload),
            (_, _) => requests++
        ));
        NetMod player = MiaoNetNetMods.Register(CreateStream(
            KnownNetMods.CelesteNetPlayerSynth,
            (_, packet) => frames.Add(packet.Payload),
            (_, _) => requests++
        ));

        try {
            TestSerializationContext context = new();
            PacketEnvelope envelope = PacketEnvelope.FromSender(3);

            // local id 1 is the frame carrier on both streams, id 2 the identity request.
            Assert.IsTrue(legacy.TryDispatch(1, envelope, FramePacketPayload(FramePayload), context));
            CollectionAssert.AreEqual(FramePayload, frames[0]);

            Assert.IsTrue(legacy.TryDispatch(2, envelope, ReadOnlyMemory<byte>.Empty, context));
            Assert.AreEqual(1, requests);

            Assert.IsTrue(player.TryDispatch(1, envelope, FramePacketPayload(FramePayload), context));
            CollectionAssert.AreEqual(FramePayload, frames[1]);

            Assert.IsTrue(player.TryDispatch(2, envelope, ReadOnlyMemory<byte>.Empty, context));
            Assert.AreEqual(2, requests);

            // The declaration is append-only: an id nobody declared is not silently accepted.
            Assert.IsFalse(legacy.TryDispatch(3, envelope, ReadOnlyMemory<byte>.Empty, context));
            Assert.IsFalse(legacy.TryDispatch(0, envelope, ReadOnlyMemory<byte>.Empty, context));
        } finally {
            player.Dispose();
            legacy.Dispose();
            MiaoNetNetMods.SetTransport(null);
        }
    }

    [TestMethod]
    public void RegisteringThePlayerStreamKeepsTheLegacyPacketIds()
    {
        MockTransport transport = new();
        MiaoNetNetMods.SetTransport(transport);

        NetMod legacy = MiaoNetNetMods.Register(CreateStream(
            LegacyNetModName,
            static (_, _) => { },
            static (_, _) => { }
        ));

        try {
            byte legacyIndex = SendAndTakeIndex(legacy, transport);

            for (int round = 0; round < 2; round++)
            {
                NetMod player = MiaoNetNetMods.Register(CreateStream(
                    KnownNetMods.CelesteNetPlayerSynth,
                    static (_, _) => { },
                    static (_, _) => { }
                ));

                try {
                    byte playerIndex = SendAndTakeIndex(player, transport);
                    Assert.AreEqual(legacyIndex + 1, playerIndex, "The player stream follows the legacy stream.");
                    Assert.AreEqual(legacyIndex, SendAndTakeIndex(legacy, transport), "Re-registering must not shift the legacy index.");
                } finally {
                    player.Dispose();
                }
            }
        } finally {
            legacy.Dispose();
            MiaoNetNetMods.SetTransport(null);
        }
    }

    [TestMethod]
    public void EveryRoutedFrameGoesToExactlyOneStream()
    {
        MockTransport transport = new();
        MiaoNetNetMods.SetTransport(transport);

        NetMod legacy = MiaoNetNetMods.Register(CreateStream(
            LegacyNetModName,
            static (_, _) => { },
            static (_, _) => { }
        ));
        NetMod player = MiaoNetNetMods.Register(CreateStream(
            KnownNetMods.CelesteNetPlayerSynth,
            static (_, _) => { },
            static (_, _) => { }
        ));

        try {
            byte legacyIndex = SendAndTakeIndex(legacy, transport);
            byte playerIndex = SendAndTakeIndex(player, transport);

            (string TypeId, string? Source)[] cases = [
                ("playerGraphics", "CelesteNet.Shared"),
                ("playerState", "CelesteNet.Shared"),
                ("playerFrame", "CelesteNet.Shared"),
                ("audioPlay", "CelesteNet.Shared"),
                ("chat", "CelesteNet.Shared"),
                ("modFrame", "SomeOtherMod"),
                ("unknownWithNoSource", null),
                ("keepAlive", "CelesteNet.Shared"),
            ];

            foreach ((string typeId, string? source) in cases) {
                CelesteNetFrameLane lane = CelesteNetFrameRouting.Classify(
                    typeId,
                    source,
                    aboutLocalPlayer: true
                );
                if (lane == CelesteNetFrameLane.Drop)
                    continue;

                NetMod target = lane == CelesteNetFrameLane.ThirdParty ? legacy : player;
                byte expectedIndex = lane == CelesteNetFrameLane.ThirdParty ? legacyIndex : playerIndex;

                transport.Sent.Clear();
                target.Send(new PacketBridgeFrame(FramePayload));
                Assert.HasCount(1, transport.Sent, typeId);
                Assert.AreEqual(expectedIndex, transport.Sent[0].NetModID, typeId);
            }
        } finally {
            player.Dispose();
            legacy.Dispose();
            MiaoNetNetMods.SetTransport(null);
        }
    }

    private static NetMod CreateStream(
        string name,
        Action<PacketEnvelope, PacketBridgeFrame> onFrame,
        Action<PacketEnvelope, PacketBridgeIdentityRequest> onRequest
    )
        => new(
            name,
            KnownNetMods.CelesteNetPlayerProtocolVersion,
            NetModPacketEntry.Create<PacketBridgeFrame>(PacketScope.Map, (envelope, packet) => onFrame(envelope, packet)),
            NetModPacketEntry.Create<PacketBridgeIdentityRequest>(PacketScope.Map, (envelope, packet) => onRequest(envelope, packet))
        );

    private static byte SendAndTakeIndex(NetMod netMod, MockTransport transport)
    {
        transport.Sent.Clear();
        netMod.Send(new PacketBridgeFrame(FramePayload));
        Assert.HasCount(1, transport.Sent);
        byte index = transport.Sent[0].NetModID;
        transport.Sent.Clear();
        return index;
    }

    private static byte[] FramePacketPayload(byte[] frame)
    {
        ByteArrayBufferWriter output = new(16);
        RefBinaryWriter writer = new(output);
        writer.Write7BitEncodedInt(frame.Length);
        writer.WriteSpan(frame);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private sealed class MockTransport : INetModTransport
    {
        public List<(byte NetModID, byte LocalID, PacketRoute Route, IContextualPacket Packet)> Sent { get; } = [];

        public void Send(byte netModID, byte localID, PacketRoute route, IContextualPacket packet)
            => Sent.Add((netModID, localID, route, packet));
    }

    private sealed class TestSerializationContext : IPacketSerializationContext
    {
        public PooledStringManager PooledStringManager { get; } = new(KnownPooledStrings.All);
    }
}
