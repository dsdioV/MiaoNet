using MiaoNet.Shared;

namespace MiaoNet.Server;

/// <summary>
/// 管理面板用的聊天缓冲（本地管理层，只在 deploy 分支上）。
/// 服务端把每条对外广播的聊天/公告写进来，面板按自增 seq 增量拉取。
/// 刻意做成静态单例：这样只需在广播处加一行，不必给上游的 MiaoServerService
/// 构造函数添依赖（同步 wip 时不产生冲突面）。
/// </summary>
public sealed class AdminChatBuffer
{
    public static AdminChatBuffer Instance { get; } = new();

    public sealed record Entry(
        long Seq,
        DateTimeOffset At,
        ChatMessageType Type,
        string? Name,
        string? Target,
        string Content
    );

    private readonly object sync = new();
    private readonly Entry?[] entries;
    private readonly int capacity;
    private int head;
    private int count;
    // 从 1 开始: 面板首次拉取用 since=0, 条件是 seq > since, 从 0 开始会漏掉第一条
    private long nextSeq = 1;

    public AdminChatBuffer(int capacity = 500)
    {
        this.capacity = capacity;
        entries = new Entry?[capacity];
    }

    public void Record(ChatMessageType type, string? name, string content, string? target = null)
    {
        lock (sync)
        {
            entries[head] = new Entry(nextSeq++, DateTimeOffset.Now, type, name, target, content);
            head = (head + 1) % capacity;
            if (count < capacity)
                count++;
        }
    }

    /// <summary>返回 seq 大于 <paramref name="since"/> 的条目（至多 <paramref name="max"/> 条，按时间升序）与当前最大 seq。</summary>
    public (long LatestSeq, IReadOnlyList<Entry> Items) Since(long since, int max)
    {
        lock (sync)
        {
            List<Entry> result = [];
            for (int i = 0; i < count && result.Count < max; i++)
            {
                int index = ((head - 1 - i) % capacity + capacity) % capacity;
                Entry? entry = entries[index];
                if (entry is null || entry.Seq <= since)
                    break;
                result.Add(entry);
            }
            result.Reverse();
            return (nextSeq - 1, result);
        }
    }
}
