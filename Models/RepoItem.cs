using Octokit;

namespace CheckGithub.Models;

public sealed class RepoItem
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string CloneUrl { get; init; }

    public string FullName => $"{Owner}/{Name}";
    public string HtmlUrl => $"https://github.com/{FullName}";

    public string? Description { get; init; }
    public string? Language { get; init; }
    public int Stars { get; init; }
    public bool IsFork { get; init; }
    public bool IsPrivate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public static RepoItem FromOctokit(Repository repository) => new()
    {
        Owner = repository.Owner.Login,
        Name = repository.Name,
        CloneUrl = repository.CloneUrl,
        Description = repository.Description,
        Language = repository.Language,
        Stars = repository.StargazersCount,
        IsFork = repository.Fork,
        IsPrivate = repository.Private,
        UpdatedAt = repository.UpdatedAt,
    };
}
