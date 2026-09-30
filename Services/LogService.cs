using System.Text;
using CheckGithub.Models;

namespace CheckGithub.Services;

public sealed class LogService
{
    private readonly Lock _gate = new();

    public LogService()
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public string Directory { get; }

    public event Action<LogEntry>? EntryWritten;

    public void Info(string message, string? detail = null) => Write(LogLevel.Info, message, detail);

    public void Success(string message, string? detail = null) => Write(LogLevel.Success, message, detail);

    public void Warning(string message, string? detail = null) => Write(LogLevel.Warning, message, detail);

    public void Error(string message, string? detail = null) => Write(LogLevel.Error, message, detail);

    public void Write(LogLevel level, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message, detail);

        try
        {
            EntryWritten?.Invoke(entry);
        }
        catch
        {
            // 订阅方（界面）抛错也不能影响主流程
        }

        var text = new StringBuilder()
            .Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(message);

        if (!string.IsNullOrWhiteSpace(detail))
            text.AppendLine().Append(detail);

        text.AppendLine();

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(
                    Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    text.ToString(),
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志无法落盘时不能影响主流程
        }
    }
}
