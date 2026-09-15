using Microsoft.Extensions.Logging;
using MiaoNet.Server;
using MiaoNet.Shared;

namespace MiaoNet.UnitTest;

/// <summary>管理面板两个环形缓冲的不变量：回绕、seq 单调、max 截断、重启自愈。</summary>
[TestClass]
public sealed class AdminBufferTest
{
    [TestMethod]
    public void ChatBufferReturnsEverythingSinceZeroInOrder()
    {
        AdminChatBuffer buffer = new(3);
        buffer.Record(ChatMessageType.Chat, "a", "1");
        buffer.Record(ChatMessageType.Chat, "b", "2");

        var (latest, items) = buffer.Since(0, 100);

        Assert.AreEqual(2, latest);
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(1, items[0].Seq); // seq 从 1 开始: since=0 时首条不能被跳过
        Assert.AreEqual("1", items[0].Content);
        Assert.AreEqual("2", items[1].Content);
    }

    [TestMethod]
    public void ChatBufferWrapsAroundKeepingOrderAndNewest()
    {
        AdminChatBuffer buffer = new(3);
        for (int i = 1; i <= 5; i++)
            buffer.Record(ChatMessageType.Chat, "n", i.ToString());

        var (latest, items) = buffer.Since(0, 100);

        Assert.AreEqual(5, latest);
        Assert.AreEqual(3, items.Count);
        CollectionAssert.AreEqual(new[] { "3", "4", "5" }, items.Select(i => i.Content).ToArray());
    }

    [TestMethod]
    public void ChatBufferSinceSkipsAlreadySeenEntries()
    {
        AdminChatBuffer buffer = new(10);
        buffer.Record(ChatMessageType.Chat, "n", "1");
        buffer.Record(ChatMessageType.Chat, "n", "2");
        var (latest, _) = buffer.Since(0, 100);

        buffer.Record(ChatMessageType.Chat, "n", "3");
        var (latest2, items) = buffer.Since(latest, 100);

        Assert.AreEqual(3, latest2);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("3", items[0].Content);
    }

    [TestMethod]
    public void ChatBufferMaxReturnsNewestEntries()
    {
        AdminChatBuffer buffer = new(10);
        for (int i = 1; i <= 6; i++)
            buffer.Record(ChatMessageType.Chat, "n", i.ToString());

        var (_, items) = buffer.Since(0, 2);

        CollectionAssert.AreEqual(new[] { "5", "6" }, items.Select(i => i.Content).ToArray());
    }

    [TestMethod]
    public void ChatBufferKeepsPrivateMessageTargetSeparateFromContent()
    {
        AdminChatBuffer buffer = new(4);
        buffer.Record(ChatMessageType.PrivateMessage, "alice", "hi", "bob");

        var (_, items) = buffer.Since(0, 10);

        Assert.AreEqual(ChatMessageType.PrivateMessage, items[0].Type);
        Assert.AreEqual("alice", items[0].Name);
        Assert.AreEqual("bob", items[0].Target);
        Assert.AreEqual("hi", items[0].Content);
    }

    [TestMethod]
    public void LogBufferWrapsAndKeepsLevel()
    {
        AdminLogBuffer buffer = new(2);
        buffer.Add(LogLevel.Information, "cat", "first");

        var (latest, items) = buffer.Since(0, 100);
        Assert.AreEqual(1, latest);
        Assert.AreEqual(1, items.Count);

        buffer.Add(LogLevel.Warning, "cat", "second");
        buffer.Add(LogLevel.Error, "cat", "third");

        var (latest2, items2) = buffer.Since(0, 100);
        Assert.AreEqual(3, latest2);
        CollectionAssert.AreEqual(new[] { "second", "third" }, items2.Select(i => i.Message).ToArray());
        Assert.AreEqual(LogLevel.Warning, items2[0].Level);
        Assert.AreEqual(LogLevel.Error, items2[1].Level);
    }

    [TestMethod]
    public void StaleSinceValueReportsSmallerLatestSeq()
    {
        // 服务端契约: 面板带着重启前的大 seq 来拉, 应只回一个小 LatestSeq 且不吐条目;
        // 面板据此发现"服务端重启过"并改从 0 重拉（见 admin.html 的 refreshLogs/refreshChat）。
        AdminLogBuffer buffer = new(4);
        buffer.Add(LogLevel.Information, "cat", "after restart");

        var (latest, items) = buffer.Since(9999, 100);

        Assert.AreEqual(1, latest);
        Assert.AreEqual(0, items.Count);

        // 面板看到 LatestSeq(1) < 自己持有的 seq(9999) 后改从 0 拉, 应拿到那条
        var (latest2, items2) = buffer.Since(0, 100);
        Assert.AreEqual(1, latest2);
        Assert.AreEqual(1, items2.Count);
        Assert.AreEqual("after restart", items2[0].Message);
    }

    [TestMethod]
    public void SinceNeverReturnsEntriesAtOrBelowGivenSeq()
    {
        // 边界: 恰好等于最新 seq 时不应重复吐条目（否则面板轮询会重复渲染）
        AdminChatBuffer buffer = new(4);
        buffer.Record(ChatMessageType.Chat, "n", "only");
        var (latest, _) = buffer.Since(0, 100);

        var (latest2, items2) = buffer.Since(latest, 100);

        Assert.AreEqual(latest, latest2);
        Assert.AreEqual(0, items2.Count);
    }
}
