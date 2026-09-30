namespace CheckGithub.Services;

public sealed class GitHubFetchException : Exception
{
    public GitHubFetchException(string message, bool isRateLimit = false) : base(message)
    {
        IsRateLimit = isRateLimit;
    }

    /// <summary>是否为「API 额度已用尽」。调用方据此上报额度，而不是去比对提示文案。</summary>
    public bool IsRateLimit { get; }
}
