namespace CheckGithub.Models;

public sealed record RateLimitSnapshot(int Limit, int Remaining, DateTimeOffset ResetAt, bool Authenticated);
