using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiaoNet.Shared;
namespace MiaoNet.Server;

public partial class MiaoHttpService
{
    /// <summary>单次冻结时长上限（7 天）。</summary>
    private const int MaxFreezeMinutes = 7 * 24 * 60;

    private Task Admin(NameValueCollection query, HttpListenerContext context)
    {
        Stream? stream = typeof(MiaoHttpService).Assembly.GetManifestResourceStream("admin.html");
        if (stream is null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        return stream.CopyToAsync(context.Response.OutputStream);
    }

    // TODO uh, we may need to switch our connection id fully to auth id
    // TODO don't use MiaoServerServices directly, use sth like IMiaoServerService
    private async Task Player(NameValueCollection query, HttpListenerContext context)
    {
        switch (context.Request.HttpMethod)
        {
        case "DELETE":
        {
            string? reason = query["reason"];
            if (reason is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                break;
            }

            // minutes>0 = 踢出并把该账号临时冻结(握手期拒绝); 缺省/空串/0 = 只踢
            int freezeMinutes = 0;
            string? minutesRaw = query["minutes"];
            if (!string.IsNullOrEmpty(minutesRaw)
                && (!int.TryParse(minutesRaw, CultureInfo.InvariantCulture, out freezeMinutes)
                    || freezeMinutes < 0
                    || freezeMinutes > MaxFreezeMinutes))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                break;
            }

            if (int.TryParse(query["cid"], CultureInfo.InvariantCulture, out int cid))
            {
                if (!miaoServerService.Players.TryGetValue(cid, out var client))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    break;
                }
                PlayerInfo info = client.Player.Info;

                // 非正账号 id 一律不冻: CustomAuth 构建下所有人都自报同一个 id, 冻一个=锁死全服
                if (freezeMinutes > 0 && info.AuthID <= 0)
                {
                    logger.LogWarning(
                        AppEvents.Http,
                        "Refusing to freeze auth id {aid} ({name}): non-positive account ids come from "
                        + "CustomAuth builds, freezing one would lock out every player.",
                        info.AuthID,
                        info.Name
                    );
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    break;
                }

                if (freezeMinutes > 0)
                {
                    // 先冻结再踢, 免掉"踢了但没冻上"的窗口
                    FreezeAccount(info.AuthID, info.Name, freezeMinutes, reason);
                    // 同账号可能有多条连接, 冻结时一并清场; Players 是不可变快照, 遍历中断连是安全的
                    foreach (var p in miaoServerService.Players)
                    {
                        if (p.Value.Player.Info.AuthID == info.AuthID)
                            await p.Value.DisconnectAsync(DisconnectReason.Kicked, reason);
                    }
                }
                else
                {
                    await client.DisconnectAsync(DisconnectReason.Kicked, reason);
                }

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                break;
            }
            else if (int.TryParse(query["aid"], CultureInfo.InvariantCulture, out int aid))
            {
                if (freezeMinutes > 0 && aid <= 0)
                {
                    logger.LogWarning(
                        AppEvents.Http,
                        "Refusing to freeze auth id {aid}: non-positive account ids come from CustomAuth "
                        + "builds, freezing one would lock out every player.",
                        aid
                    );
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    break;
                }

                List<MiaoClientConnection> targets =
                    [.. miaoServerService.Players.Values.Where(p => p.Player.Info.AuthID == aid)];

                if (freezeMinutes > 0)
                    FreezeAccount(aid, targets.Count > 0 ? targets[0].Player.Info.Name : null, freezeMinutes, reason);

                foreach (MiaoClientConnection target in targets)
                    await target.DisconnectAsync(DisconnectReason.Kicked, reason);

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                break;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                break;
            }
        }
        default:
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            break;
        }
        }
    }

    private void FreezeAccount(int authID, string? name, int minutes, string? reason)
    {
        bool frozen = temporaryFreezeStore.Freeze(authID, TimeSpan.FromMinutes(minutes), name, reason);
        if (frozen)
        {
            logger.LogWarning(
                AppEvents.Http,
                "Froze auth id {aid} ({name}) for {minutes} minute(s), reason: {reason}",
                authID,
                name,
                minutes,
                reason
            );
        }
        else
        {
            logger.LogWarning(
                AppEvents.Http,
                "Refused to freeze auth id {aid} ({name}): the store rejects non-positive account ids.",
                authID,
                name
            );
        }
    }

    /// <summary>GET 当前冻结名单; DELETE ?aid= 解除冻结.</summary>
    private async Task Freeze(NameValueCollection query, HttpListenerContext context)
    {
        switch (context.Request.HttpMethod)
        {
        case "GET":
        {
            context.Response.ContentType = MediaTypeNames.Application.Json;

            var response = new
            {
                MaxFreezeMinutes,
                Frozen = temporaryFreezeStore.List().Select(static f => new
                {
                    f.AuthID,
                    f.Name,
                    Until = f.Until,
                    RemainingMinutes = Math.Max(0, (int)Math.Ceiling((f.Until - DateTimeOffset.UtcNow).TotalMinutes)),
                    f.Reason
                })
            };

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, response, jsonSerializerOptions);
            break;
        }
        case "DELETE":
        {
            if (int.TryParse(query["aid"], CultureInfo.InvariantCulture, out int aid))
            {
                if (temporaryFreezeStore.Unfreeze(aid))
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                else
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
            break;
        }
        default:
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            break;
        }
        }
    }

    private async Task Status(NameValueCollection query, HttpListenerContext context)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

#pragma warning disable IDE0037
        var response = new
        {
            PlayersCount = miaoServerService.Players.Count,
            Frozen = temporaryFreezeStore.List().Select(static f => new
            {
                f.AuthID,
                f.Name,
                RemainingMinutes = Math.Max(0, (int)Math.Ceiling((f.Until - DateTimeOffset.UtcNow).TotalMinutes)),
                f.Reason
            }),
            Channels = miaoServerService.Channels.Select(c => new
            {
                ID = c.Key,
                Name = c.Value.Info.Name,
                IsPrivate = c.Value.IsPrivate,
                Players = c.Value.Players.Select(c => new
                {
                    ID = c.ID,
                    Name = c.Player.Info.Name,
                    Location = c.Player.Location.ToString(),
                    Latency = c.Player.LastPing,
                    Frozen = temporaryFreezeStore.GetFrozen(c.Player.Info.AuthID) is not null
                })
            })
        };
#pragma warning restore IDE0037

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, response, jsonSerializerOptions);
    }

    private async Task Announce(NameValueCollection query, HttpListenerContext context)
    {
        string? message = query["msg"];
        if (string.IsNullOrWhiteSpace(message))
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        await miaoServerService.BroadcastAsync(new PacketChatMessage(DateTime.UtcNow, ChatMessageType.Server, null, message));
        AdminChatBuffer.Instance.Record(ChatMessageType.Server, null, message);

        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
    }

    private Task DoGC(NameValueCollection query, HttpListenerContext context)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        return Task.CompletedTask;
    }

    private async Task GetMetrics(NameValueCollection query, HttpListenerContext context)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var values = miaoMetricsService.Get();
        var ret = new
        {
            OnlinePlayersCount = miaoServerService.Players.Count,
            Metrics = values,
            GC = new
            {
                TotalAllocatedBytes = GC.GetTotalAllocatedBytes(),
                TotalMemory = GC.GetTotalMemory(false),
                TotalPauseDuration = GC.GetTotalPauseDuration()
            }
        };
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, ret, jsonSerializerOptions);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
    }

    private async Task GetMetricsHistory(NameValueCollection query, HttpListenerContext context)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var ret = new
        {
            IntervalSeconds = AdminMetricsSampler.SampleInterval.TotalSeconds,
            Capacity = AdminMetricsSampler.Capacity,
            Samples = metricsSampler.Snapshot()
        };

        await JsonSerializer.SerializeAsync(context.Response.OutputStream, ret, jsonSerializerOptions);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
    }

    /// <summary>面板用：拉取 seq &gt; since 的日志条目（增量）。</summary>
    private async Task GetLogs(NameValueCollection query, HttpListenerContext context)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        long since = ParseLong(query["since"], 0);
        int max = (int)Math.Clamp(ParseLong(query["max"], 200), 1, 1000);
        var (latestSeq, items) = adminLogBuffer.Since(since, max);

        await JsonSerializer.SerializeAsync(
            context.Response.OutputStream,
            new { LatestSeq = latestSeq, Items = items },
            jsonSerializerOptions
        );
        context.Response.StatusCode = (int)HttpStatusCode.OK;
    }

    /// <summary>面板用：拉取 seq &gt; since 的聊天/公告（增量）。</summary>
    private async Task GetChat(NameValueCollection query, HttpListenerContext context)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        long since = ParseLong(query["since"], 0);
        int max = (int)Math.Clamp(ParseLong(query["max"], 200), 1, 1000);
        var (latestSeq, items) = AdminChatBuffer.Instance.Since(since, max);

        await JsonSerializer.SerializeAsync(
            context.Response.OutputStream,
            new { LatestSeq = latestSeq, Items = items },
            jsonSerializerOptions
        );
        context.Response.StatusCode = (int)HttpStatusCode.OK;
    }

    private static long ParseLong(string? raw, long fallback)
        => long.TryParse(raw, CultureInfo.InvariantCulture, out long value) ? value : fallback;
}
