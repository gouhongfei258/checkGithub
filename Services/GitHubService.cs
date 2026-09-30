using CheckGithub.Models;
using Octokit;

namespace CheckGithub.Services;

public sealed class GitHubService
{
    private const string MediaType = "application/vnd.github.v3+json";
    private const int PageSize = 100;
    private const int AnonymousRateLimit = 60;
    private const int AuthenticatedRateLimit = 5000;

    private readonly LogService _log;
    private GitHubClient? _client;
    private bool _authenticated;

    public GitHubService(LogService log)
    {
        _log = log;
    }

    public void Configure(string? token)
    {
        var client = new GitHubClient(new ProductHeaderValue("CheckGithub"));

        _authenticated = !string.IsNullOrWhiteSpace(token);

        if (_authenticated)
        {
            try
            {
                client.Credentials = new Credentials(token);
            }
            catch
            {
                // Token 形态不合法（混入空格/非法字符等）时退回匿名模式，
                // 不让「设置」对话框或抓取流程因此崩溃。
                // 这里刻意不记录异常原文：异常来自凭据对象，日志绝不能出现 Token 相关内容。
                _authenticated = false;
                _log.Warning("Token 无效，已改用未认证方式（每小时 60 次请求）。请在「设置」中检查 Token。");
            }
        }

        _client = client;
        _log.Info($"GitHub 客户端已配置（{(_authenticated ? "已认证" : "未认证，每小时 60 次请求")}）。");
    }

    public async Task FetchAsync(
        string user,
        bool starred,
        IProgress<IReadOnlyList<RepoItem>> batch,
        IProgress<RateLimitSnapshot>? rate,
        CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("GitHub 客户端尚未配置。");

        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = starred
                ? $"users/{Uri.EscapeDataString(user)}/starred?per_page={PageSize}&page={page}&sort=created&direction=desc"
                : $"users/{Uri.EscapeDataString(user)}/repos?per_page={PageSize}&page={page}&type=all&sort=updated";

            IReadOnlyList<Repository> repositories;

            try
            {
                repositories = await RequestPageAsync(client, path, user);
            }
            catch (GitHubFetchException ex)
            {
                // 额度用尽时连一页都没成功过，也要把「0 次剩余」显示出来，
                // 否则界面会一直停在「未获取」，用户看不到自己已经撞上限额。
                ReportRate(client, rate, exhausted: ex.IsRateLimit);
                throw;
            }

            ReportRate(client, rate);

            if (repositories.Count > 0)
                batch.Report(repositories.Select(RepoItem.FromOctokit).ToArray());

            if (repositories.Count < PageSize)
                break;
        }
    }

    private async Task<IReadOnlyList<Repository>> RequestPageAsync(GitHubClient client, string path, string user)
    {
        try
        {
            var response = await client.Connection.Get<Repository[]>(
                new Uri(path, UriKind.Relative), null, MediaType, CancellationToken.None);

            return response.Body;
        }
        catch (NotFoundException)
        {
            throw new GitHubFetchException($"找不到用户「{user}」，请检查用户名是否正确。");
        }
        catch (AuthorizationException)
        {
            throw new GitHubFetchException("Token 无效或权限不足，请在「设置」中检查 Token。");
        }
        catch (RateLimitExceededException)
        {
            throw new GitHubFetchException(RateLimitMessage, isRateLimit: true);
        }
        catch (ForbiddenException)
        {
            throw new GitHubFetchException(RateLimitMessage, isRateLimit: true);
        }
        catch (ApiException ex)
        {
            throw new GitHubFetchException($"请求 GitHub 失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            throw new GitHubFetchException($"请求 GitHub 失败：{ex.Message}");
        }
    }

    private void ReportRate(GitHubClient client, IProgress<RateLimitSnapshot>? rate, bool exhausted = false)
    {
        if (rate is null)
            return;

        if (client.GetLastApiInfo()?.RateLimit is { } limit)
        {
            rate.Report(new RateLimitSnapshot(limit.Limit, limit.Remaining, limit.Reset, _authenticated));
            return;
        }

        if (!exhausted)
            return;

        // 限流响应往往不再带回可解析的额度信息，此时按「已用尽」上报，
        // 保证额度是常驻可见的，而不是长期停在「未获取」。
        rate.Report(new RateLimitSnapshot(
            _authenticated ? AuthenticatedRateLimit : AnonymousRateLimit,
            0,
            DateTimeOffset.UtcNow.AddHours(1),
            _authenticated));
    }

    private const string RateLimitMessage =
        "GitHub API 已触发限流（未认证时每小时仅 60 次请求），已停止抓取并保留当前已加载的数据。" +
        "请在「设置」中填写 Personal Access Token 后点击「刷新」重试。";
}
