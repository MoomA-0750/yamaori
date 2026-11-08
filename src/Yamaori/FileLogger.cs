using System.Text;
using Microsoft.Extensions.Logging;

namespace Yamaori;

/// <summary>
/// 常駐しているときは窓がないので、ログをファイル（データのフォルダの yamaori.log）に書く。
/// 5 MB を超えたら yamaori.log.1 に回す。
/// </summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    const long MaxBytes = 5 * 1024 * 1024;
    readonly Lock _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // ログが書けなくても止めない
            }
        }
    }

    public void Dispose() { }

    sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // ASP.NET の内部（要求 1 つごとの行など）は多いので、警告以上だけ書く
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || category.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
                ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logLevel,-11} {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception != null) text += exception + Environment.NewLine;
            owner.Write(text);
        }
    }
}
