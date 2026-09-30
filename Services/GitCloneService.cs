using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CheckGithub.Models;

namespace CheckGithub.Services;

public enum ClonePathDecision
{
    Clone,
    AlreadyExists,
}

public enum CleanupOutcome
{
    NothingToDo,
    Deleted,
    KeptValidRepository,
    Failed,
}

public sealed record ClonePlan(string TargetPath, ClonePathDecision Decision, string Reason);

public sealed record CloneResult(bool Success, bool Canceled, int ExitCode, string Output);

public sealed partial class GitCloneService
{
    private const int MaxOutputChars = 20000;
    private const int VerifyTimeoutSeconds = 5;

    private readonly ConcurrentDictionary<string, string> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 决定某个仓库落到哪个目录：优先 {root}/{repo}，同名冲突时退化为 {root}/{owner}/{repo}。
    /// 只把「本次运行将要新建的目录」记为可清理，绝不触碰克隆开始前已存在的目录。
    /// </summary>
    public ClonePlan Plan(RepoItem repo, string root)
    {
        var byName = Path.Combine(root, repo.Name);
        var byOwner = Path.Combine(root, repo.Owner, repo.Name);

        foreach (var candidate in new[] { byName, byOwner })
        {
            if (_claims.TryGetValue(candidate, out var claimedBy))
            {
                if (string.Equals(claimedBy, repo.FullName, StringComparison.OrdinalIgnoreCase))
                    return new ClonePlan(candidate, ClonePathDecision.AlreadyExists, "本次任务中已安排克隆到该目录");

                continue;
            }

            if (Directory.Exists(candidate))
            {
                if (IsSameRepository(candidate, repo))
                    return new ClonePlan(candidate, ClonePathDecision.AlreadyExists, "目标目录已存在该仓库");

                continue;
            }

            _claims[candidate] = repo.FullName;
            return new ClonePlan(candidate, ClonePathDecision.Clone, "待克隆");
        }

        return new ClonePlan(byOwner, ClonePathDecision.AlreadyExists, "目标目录已被其他仓库占用");
    }

    public async Task<CloneResult> CloneAsync(
        RepoItem repo,
        string targetPath,
        bool shallow,
        IProgress<double>? progress,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(targetPath)!;
        var buffer = new StringBuilder();
        var gate = new Lock();

        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (IsProgressLine(line))
            {
                var match = ReceivingProgressRegex().Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
                    progress?.Report(percent);

                return;
            }

            lock (gate)
            {
                if (buffer.Length < MaxOutputChars)
                    buffer.AppendLine(line);
            }

            output?.Report(line);
        }

        try
        {
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                _created.TryAdd(parent, 0);
            }

            _created.TryAdd(targetPath, 0);
        }
        catch (Exception ex)
        {
            return new CloneResult(false, false, -1, $"无法创建目标目录：{ex.Message}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = parent,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.longpaths=true");
        startInfo.ArgumentList.Add("clone");
        startInfo.ArgumentList.Add("--progress");

        if (shallow)
        {
            startInfo.ArgumentList.Add("--depth");
            startInfo.ArgumentList.Add("1");
        }

        startInfo.ArgumentList.Add(repo.CloneUrl);
        startInfo.ArgumentList.Add(targetPath);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CloneResult(false, false, -1, $"无法启动 git 进程：{ex.Message}");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已经退出
            }
        });

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        process.WaitForExit();

        string captured;
        lock (gate)
            captured = buffer.ToString();

        var canceled = cancellationToken.IsCancellationRequested;
        return new CloneResult(!canceled && process.ExitCode == 0, canceled, process.ExitCode, captured);
    }

    /// <summary>
    /// 删除本次运行产生的不完整目录。目录内若已存在有效提交则保留。
    /// </summary>
    public async Task<CleanupOutcome> CleanupIncompleteAsync(string path)
    {
        if (!_created.ContainsKey(path))
            return CleanupOutcome.NothingToDo;

        try
        {
            if (Directory.Exists(path))
            {
                if (await HasValidCommitAsync(path))
                    return CleanupOutcome.KeptValidRepository;

                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }

            var parent = Path.GetDirectoryName(path);
            if (parent is not null
                && _created.ContainsKey(parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            return CleanupOutcome.Deleted;
        }
        catch
        {
            return CleanupOutcome.Failed;
        }
    }

    /// <summary>
    /// git 在 Windows 上会把 pack 等文件标记为只读，Directory.Delete 无法删除只读文件，必须先清除该属性。
    /// </summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsSameRepository(string directory, RepoItem repo)
    {
        var configPath = Path.Combine(directory, ".git", "config");
        if (!File.Exists(configPath))
            return false;

        var suffix = $"/{repo.Owner}/{repo.Name}";

        try
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                var separator = trimmed.IndexOf('=');
                if (separator <= 0 || !trimmed[..separator].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = trimmed[(separator + 1)..].Trim().TrimEnd('/');
                if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    url = url[..^4];

                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static async Task<bool> HasValidCommitAsync(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("--verify");
            startInfo.ArgumentList.Add("HEAD");

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(VerifyTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProgressLine(string line) => ProgressLineRegex().IsMatch(line);

    [GeneratedRegex(@"(?:Counting|Compressing|Receiving|Resolving|Updating files|Checking out files)\b.*\d+%")]
    private static partial Regex ProgressLineRegex();

    [GeneratedRegex(@"Receiving objects:\s+(\d+)%")]
    private static partial Regex ReceivingProgressRegex();
}
