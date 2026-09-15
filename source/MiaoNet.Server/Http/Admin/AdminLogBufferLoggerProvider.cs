using Microsoft.Extensions.Logging;

namespace MiaoNet.Server;

/// <summary>
/// 把日志同时写进 <see cref="AdminLogBuffer"/>，供本地管理面板实时查看（只在 deploy 分支上）。
/// 通过 <c>builder.Services.AddSingleton&lt;ILoggerProvider, ...&gt;()</c> 注册即可，无需改日志管线。
/// </summary>
public sealed class AdminLogBufferLoggerProvider(AdminLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);

    public void Dispose()
    {
    }

    private sealed class BufferLogger(AdminLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} | {exception.GetType().Name}: {exception.Message}";
            if (message.Length > 600)
                message = string.Concat(message.AsSpan(0, 600), "…");

            buffer.Add(logLevel, category, message);
        }
    }
}
