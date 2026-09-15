using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiaoNet.Server;

/// <summary>
/// 管理面板用的指标采样器（本地管理层，只在 deploy 分支上）。
/// 每 <see cref="SampleInterval"/> 采一次进程 CPU / GC / 流量快照，滚动保留 <see cref="Capacity"/> 条（默认 1 小时）。
/// 面板只读，不参与任何游戏逻辑。
/// </summary>
public sealed class AdminMetricsSampler : BackgroundService
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    public const int Capacity = 720; // 720 * 5s = 1h

    public sealed record Sample(
        DateTimeOffset At,
        double CpuPercent,
        double AllocMbPerSec,
        double GcPauseMsPerSec,
        double HeapMb,
        int Players,
        double UploadMbps,
        double DownloadMbps,
        double PacketsPerSec
    );

    private readonly object sync = new();
    private readonly Sample?[] samples = new Sample?[Capacity];
    private int head;
    private int count;

    private readonly MiaoMetricsService metricsService;
    private readonly IMiaoServerService serverService;
    private readonly ILogger<AdminMetricsSampler> logger;

    private readonly Process process;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private TimeSpan lastCpu;
    private long lastAllocated;
    private TimeSpan lastPause;
    private long lastUploadBytes;
    private long lastDownloadBytes;
    private long lastUploadPackets;
    private long lastDownloadPackets;
    private long lastTickTicks;

    public AdminMetricsSampler(
        MiaoMetricsService metricsService,
        IMiaoServerService serverService,
        ILogger<AdminMetricsSampler> logger
    )
    {
        this.metricsService = metricsService;
        this.serverService = serverService;
        this.logger = logger;

        process = Process.GetCurrentProcess();
        lastCpu = process.TotalProcessorTime;
        lastAllocated = GC.GetTotalAllocatedBytes();
        lastPause = GC.GetTotalPauseDuration();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SampleInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // 采样失败绝不能影响服务端
                logger.LogWarning(AppEvents.Http, e, "Failed to sample admin metrics.");
            }
        }
    }

    private void Tick()
    {
        long nowTicks = stopwatch.ElapsedTicks;
        double elapsedSeconds = (nowTicks - lastTickTicks) / (double)Stopwatch.Frequency;
        lastTickTicks = nowTicks;

        // 第一次采样（或时钟异常）时没有可用的差值
        if (elapsedSeconds <= 0.001)
            return;

        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes();
        TimeSpan pause = GC.GetTotalPauseDuration();
        MiaoMetrics metrics = metricsService.Get();

        // 计数器在重启/重置后会倒退，负值一律按 0 处理
        double cpuDeltaSeconds = Math.Max(0, (cpu - lastCpu).TotalSeconds);
        long allocDelta = Math.Max(0, allocated - lastAllocated);
        TimeSpan pauseDelta = pause - lastPause;
        long uploadDelta = Math.Max(0, metrics.TcpUploadByBytes - lastUploadBytes);
        long downloadDelta = Math.Max(0, metrics.TcpDownloadByBytes - lastDownloadBytes);
        long packetDelta = Math.Max(0, metrics.TcpUploadByPackets - lastUploadPackets)
            + Math.Max(0, metrics.TcpDownloadByPackets - lastDownloadPackets);

        lastCpu = cpu;
        lastAllocated = allocated;
        lastPause = pause;
        lastUploadBytes = metrics.TcpUploadByBytes;
        lastDownloadBytes = metrics.TcpDownloadByBytes;
        lastUploadPackets = metrics.TcpUploadByPackets;
        lastDownloadPackets = metrics.TcpDownloadByPackets;

        Sample sample = new(
            DateTimeOffset.UtcNow,
            Math.Round(cpuDeltaSeconds / elapsedSeconds / Environment.ProcessorCount * 100, 2),
            Math.Round(allocDelta / elapsedSeconds / 1_000_000, 3),
            Math.Round(Math.Max(0, pauseDelta.TotalMilliseconds) / elapsedSeconds, 3),
            Math.Round(GC.GetTotalMemory(false) / 1_000_000.0, 2),
            serverService.Players.Count,
            Math.Round(uploadDelta * 8 / elapsedSeconds / 1_000_000, 3),
            Math.Round(downloadDelta * 8 / elapsedSeconds / 1_000_000, 3),
            Math.Round(packetDelta / elapsedSeconds, 1)
        );

        lock (sync)
        {
            samples[head] = sample;
            head = (head + 1) % Capacity;
            if (count < Capacity)
                count++;
        }
    }

    /// <summary>按时间升序返回已采到的样本。</summary>
    public IReadOnlyList<Sample> Snapshot()
    {
        lock (sync)
        {
            List<Sample> result = new(count);
            for (int i = 0; i < count; i++)
            {
                int index = (head - count + i + Capacity) % Capacity;
                if (samples[index] is { } sample)
                    result.Add(sample);
            }
            return result;
        }
    }
}
