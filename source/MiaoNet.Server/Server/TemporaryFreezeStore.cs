using System.Collections.Concurrent;

namespace MiaoNet.Server;

/// <summary>
/// 临时冻结名单（本地管理层，只在 deploy 分支上）。
/// 按论坛账号 id 记录，被冻结的账号在握手阶段直接拒绝，直到冻结到期。
/// 纯内存态：服务端重启即清空 —— 别在团建中途重启。
/// </summary>
public sealed class TemporaryFreezeStore
{
    public sealed record Entry(int AuthID, string? Name, DateTimeOffset Until, string? Reason);

    private readonly ConcurrentDictionary<int, Entry> entries = new();

    /// <summary>
    /// 冻结一个账号，返回 false = 没有生效。
    /// 非正账号 id 一律拒绝：CustomAuth 构建（压测服 / 本地 Debug）下所有客户端都自报同一个非正
    /// id，冻一个就等于锁死全服。已存在未到期冻结时取较长的那次，避免把长封覆盖成短封。
    /// </summary>
    public bool Freeze(int authID, TimeSpan duration, string? name, string? reason)
    {
        if (authID <= 0)
            return false;

        DateTimeOffset until = DateTimeOffset.UtcNow + duration;
        while (true)
        {
            if (entries.TryGetValue(authID, out Entry? existing))
            {
                if (existing.Until >= until)
                    return true; // 已有更长的冻结，保留它

                if (!entries.TryUpdate(authID, new Entry(authID, name ?? existing.Name, until, reason), existing))
                    continue; // 并发改过，重来
                return true;
            }

            if (entries.TryAdd(authID, new Entry(authID, name, until, reason)))
                return true;
        }
    }

    /// <summary>仍在冻结期内则返回条目，否则返回 null（顺带清掉已过期的）。</summary>
    public Entry? GetFrozen(int authID)
    {
        if (!entries.TryGetValue(authID, out Entry? entry))
            return null;
        if (entry.Until > DateTimeOffset.UtcNow)
            return entry;

        // 值匹配删除：只有条目仍是读到的这一条时才删，否则会误删刚写入的新冻结
        entries.TryRemove(new KeyValuePair<int, Entry>(authID, entry));
        return null;
    }

    public bool Unfreeze(int authID) => entries.TryRemove(authID, out _);

    /// <summary>当前有效的冻结条目，按到期时间升序。</summary>
    public IReadOnlyList<Entry> List()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<Entry> result = [];
        foreach ((int authID, Entry entry) in entries)
        {
            if (entry.Until > now)
                result.Add(entry);
            else
                entries.TryRemove(new KeyValuePair<int, Entry>(authID, entry));
        }

        result.Sort(static (a, b) => a.Until.CompareTo(b.Until));
        return result;
    }
}
