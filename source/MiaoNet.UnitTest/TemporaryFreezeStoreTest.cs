using MiaoNet.Server;
using MiaoNet.Shared;

namespace MiaoNet.UnitTest;

[TestClass]
public sealed class TemporaryFreezeStoreTest
{
    private sealed class StaticAuthenticator(AuthenticationResult result) : IMiaoAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(
            byte[] authenticationData,
            bool isAuthorize,
            CancellationToken cancellationToken
        ) => Task.FromResult(result);
    }

    private static AuthenticationResult Success(int authID)
        => new(
            AuthenticationResultType.Success,
            new PlayerInfo(authID, "Tester", string.Empty, string.Empty, Color.White),
            null
        );

    [TestMethod]
    public void FreezeListsAndUnfreezes()
    {
        TemporaryFreezeStore store = new();
        store.Freeze(7, TimeSpan.FromMinutes(5), "Tester", "e2e");

        TemporaryFreezeStore.Entry? entry = store.GetFrozen(7);
        Assert.IsNotNull(entry);
        Assert.AreEqual("e2e", entry!.Reason);
        Assert.AreEqual("Tester", entry.Name);
        Assert.IsTrue(entry.Until > DateTimeOffset.UtcNow);
        Assert.AreEqual(1, store.List().Count);

        Assert.IsTrue(store.Unfreeze(7));
        Assert.IsFalse(store.Unfreeze(7));
        Assert.IsNull(store.GetFrozen(7));
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public void ExpiredFreezeIsReportedAsNotFrozen()
    {
        TemporaryFreezeStore store = new();
        store.Freeze(9, TimeSpan.FromMilliseconds(-1), null, null);

        Assert.IsNull(store.GetFrozen(9));
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public void NonPositiveAuthIdIsNeverFrozen()
    {
        TemporaryFreezeStore store = new();

        // CustomAuth 构建下所有客户端都自报 -1/0，冻一个就等于锁死全服
        Assert.IsFalse(store.Freeze(-1, TimeSpan.FromMinutes(5), "mock", null));
        Assert.IsFalse(store.Freeze(0, TimeSpan.FromMinutes(5), null, null));
        Assert.IsNull(store.GetFrozen(-1));
        Assert.IsNull(store.GetFrozen(0));
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public void FreezeKeepsTheLongerDuration()
    {
        TemporaryFreezeStore store = new();
        Assert.IsTrue(store.Freeze(11, TimeSpan.FromMinutes(60), "Tester", "长封"));
        DateTimeOffset until = store.GetFrozen(11)!.Until;

        // 再冻一次更短的：不能把长封缩短（等同提前解冻）
        Assert.IsTrue(store.Freeze(11, TimeSpan.FromMinutes(1), "Tester", "短封"));
        Assert.AreEqual(until, store.GetFrozen(11)!.Until);
        Assert.AreEqual("长封", store.GetFrozen(11)!.Reason);

        // 更长的可以覆盖
        Assert.IsTrue(store.Freeze(11, TimeSpan.FromHours(5), "Tester", "更久"));
        Assert.IsTrue(store.GetFrozen(11)!.Until > until);
        Assert.AreEqual("更久", store.GetFrozen(11)!.Reason);
    }

    [TestMethod]
    public async Task FrozenAccountIsRejectedAsSuspended()
    {
        TemporaryFreezeStore store = new();
        store.Freeze(42, TimeSpan.FromMinutes(3), "Tester", "刷屏");
        FreezingAuthenticator authenticator = new(new StaticAuthenticator(Success(42)), store);

        AuthenticationResult result = await authenticator.AuthenticateAsync([], false, CancellationToken.None);

        Assert.AreEqual(AuthenticationResultType.Suspended, result.Type);
        Assert.IsNull(result.PlayerInfo);
        StringAssert.Contains(result.SuspendMessage!, "临时冻结");
        StringAssert.Contains(result.SuspendMessage!, "后解冻");
        StringAssert.Contains(result.SuspendMessage!, "刷屏");
    }

    [TestMethod]
    public async Task UnfrozenAccountPassesThroughUntouched()
    {
        TemporaryFreezeStore store = new();
        FreezingAuthenticator authenticator = new(new StaticAuthenticator(Success(1)), store);

        AuthenticationResult result = await authenticator.AuthenticateAsync([], false, CancellationToken.None);

        Assert.AreEqual(AuthenticationResultType.Success, result.Type);
        Assert.AreEqual(1, result.PlayerInfo!.AuthID);
    }

    [TestMethod]
    public async Task AuthenticatorFailureIsNotMaskedByFreeze()
    {
        TemporaryFreezeStore store = new();
        store.Freeze(5, TimeSpan.FromMinutes(3), null, null);
        FreezingAuthenticator authenticator = new(
            new StaticAuthenticator(new AuthenticationResult(AuthenticationResultType.LoginExpired)),
            store
        );

        AuthenticationResult result = await authenticator.AuthenticateAsync([], false, CancellationToken.None);

        Assert.AreEqual(AuthenticationResultType.LoginExpired, result.Type);
    }
}
