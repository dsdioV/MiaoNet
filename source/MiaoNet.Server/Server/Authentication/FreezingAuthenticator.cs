using MiaoNet.Shared;

namespace MiaoNet.Server;

/// <summary>
/// 装饰真实鉴权器：认证成功、但账号在临时冻结名单里时改为返回 Suspended，
/// 解冻剩余时间通过握手的失败理由回给客户端（客户端会把这句显示给玩家）。
/// 刻意放在鉴权层而不是握手代码里 —— 同步上游 wip 时这一块不会产生冲突。
/// </summary>
public sealed class FreezingAuthenticator(
    IMiaoAuthenticator inner,
    TemporaryFreezeStore freezeStore
) : IMiaoAuthenticator
{
    public async Task<AuthenticationResult> AuthenticateAsync(
        byte[] authenticationData,
        bool isAuthorize,
        CancellationToken cancellationToken
    )
    {
        AuthenticationResult result = await inner.AuthenticateAsync(authenticationData, isAuthorize, cancellationToken);

        // 鉴权本身失败（令牌到期/解密失败/BBS 拒绝…）就照原样返回，不掩盖真实原因
        if (result.Type != AuthenticationResultType.Success || result.PlayerInfo is null)
            return result;

        TemporaryFreezeStore.Entry? frozen = freezeStore.GetFrozen(result.PlayerInfo.AuthID);
        if (frozen is null)
            return result;

        // 用相对时间而非绝对时间：服务器常年跑 UTC，绝对时间对玩家是错的
        TimeSpan remaining = frozen.Until - DateTimeOffset.UtcNow;
        string remainingText = remaining < TimeSpan.FromMinutes(1)
            ? "不到 1 分钟"
            : $"{(int)Math.Ceiling(remaining.TotalMinutes)} 分钟";

        string message = $"你已被临时冻结，将在 {remainingText}后解冻"
            + (string.IsNullOrWhiteSpace(frozen.Reason) ? string.Empty : $"，原因：{frozen.Reason}");
        return new AuthenticationResult(AuthenticationResultType.Suspended, message);
    }
}
