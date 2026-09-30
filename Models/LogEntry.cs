namespace CheckGithub.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message, string? Detail)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}
