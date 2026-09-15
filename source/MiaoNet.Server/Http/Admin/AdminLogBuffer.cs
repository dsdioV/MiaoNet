using Microsoft.Extensions.Logging;

namespace MiaoNet.Server;

/// <summary>
/// 管理面板用的内存日志缓冲（本地管理层，只在 deploy 分支上）。
/// 滚动保留最近 <c>capacity</c> 条，面板按自增 seq 增量拉取。只存格式化后的一行文本。
/// </summary>
public sealed class AdminLogBuffer
{
    public sealed record Entry(long Seq, DateTimeOffset At, LogLevel Level, string Category, string Message);

    private readonly object sync = new();
    private readonly Entry?[] entries;
    private readonly int capacity;
    private int head;
    private int count;
    // 从 1 开始: 面板首次拉取用 since=0, 条件是 seq > since, 从 0 开始会漏掉第一条
    private long nextSeq = 1;

    public AdminLogBuffer(int capacity = 1000)
    {
        this.capacity = capacity;
        entries = new Entry?[capacity];
    }

    public void Add(LogLevel level, string category, string message)
    {
        lock (sync)
        {
            entries[head] = new Entry(nextSeq++, DateTimeOffset.Now, level, category, message);
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
