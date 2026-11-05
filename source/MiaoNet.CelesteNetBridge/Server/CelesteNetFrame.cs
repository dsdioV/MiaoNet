namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// One inbound CelesteNet DataType frame, exactly as it arrived: the payload is everything after
/// the u16 length prefix, so it can be handed back to a client unchanged.
/// </summary>
public readonly record struct CelesteNetFrame(string? TypeId, byte[] Payload);
