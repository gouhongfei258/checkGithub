using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using CheckGithub.Models;
using CheckGithub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CheckGithub.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int MaxLogRows = 500;

    private readonly SettingsService _settingsService;
    private readonly LogService _log;
    private readonly GitHubService _github;

    private readonly Dictionary<string, CachedUser> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 抓取与克隆各持一个取消源：两者可能同时进行（克隆中按「刷新」），
    // 共用一个字段会让后启动的一方覆盖前者，导致「停止」杀不掉正在运行的 git 进程。
    private CancellationTokenSource? _fetchCts;
    private CancellationTokenSource? _cloneCts;
    private string? _loadedUser;
    private bool _loading;

    public MainWindowViewModel()
    {
        _settingsService = new SettingsService();
        _log = new LogService();
        _github = new GitHubService(_log);
        Settings = _settingsService.Load();

        ReposView = new DataGridCollectionView(Repos)
        {
            Filter = o => PassesFilter((RepoRowViewModel)o),
        };
        ReposView.SortDescriptions.Add(
            DataGridSortDescription.FromPath(nameof(RepoRowViewModel.UpdatedAt), ListSortDirection.Descending));

        StarredView = new DataGridCollectionView(StarredRows)
        {
            Filter = o => PassesFilter((RepoRowViewModel)o),
        };
        StarredView.SortDescriptions.Add(
            DataGridSortDescription.FromPath(nameof(RepoRowViewModel.Stars), ListSortDirection.Descending));

        // 必须在两个视图创建之后再赋值：FilterText 的 setter 会触发 OnFilterTextChanged 去刷新视图。
        UserName = "";
        FilterText = "";
        TargetDirectory = Settings.RootDirectory;
        ShallowClone = Settings.ShallowClone;
        StatusMessage = "输入 GitHub 用户名后点击「获取」。";
        RateLimitText = "API 额度：未获取";

        _log.EntryWritten += OnEntryWritten;
        _github.Configure(ResolveToken());
        _log.Info($"程序启动，配置文件：{_settingsService.FilePath}");
    }

    public AppSettings Settings { get; }

    public ObservableCollection<RepoRowViewModel> Repos { get; } = [];

    public ObservableCollection<RepoRowViewModel> StarredRows { get; } = [];

    public DataGridCollectionView ReposView { get; }

    public DataGridCollectionView StarredView { get; }

    public ObservableCollection<LogRowViewModel> LogRows { get; } = [];

    public string EffectiveToken => ResolveToken();

    public bool TokenFromEnvironment => string.IsNullOrWhiteSpace(Settings.Token) && EffectiveToken.Length > 0;

    public string SettingsFilePath => _settingsService.FilePath;

    public string LogDirectory => _log.Directory;

    [ObservableProperty]
    public partial string UserName { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; }

    [ObservableProperty]
    public partial bool HideForks { get; set; }

    [ObservableProperty]
    public partial string TargetDirectory { get; set; }

    [ObservableProperty]
    public partial bool ShallowClone { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial string RateLimitText { get; set; }

    [ObservableProperty]
    public partial int ActiveTabIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCloneCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    public partial bool IsCloning { get; set; }

    public void SaveSettings()
    {
        Settings.RootDirectory = TargetDirectory;
        Settings.ShallowClone = ShallowClone;
        Settings.Parallelism = Math.Clamp(Settings.Parallelism, 1, 8);

        if (_settingsService.Save(Settings))
        {
            _log.Info($"配置已保存：{_settingsService.FilePath}");
            return;
        }

        _log.Warning($"无法写入配置文件：{_settingsService.FilePath}");
        StatusMessage = "配置保存失败：请检查程序目录是否可写。";
    }

    /// <summary>界面事件处理器里出现未预期异常时的统一落点：记日志并提示，不向界面线程抛。</summary>
    public void ReportUnexpected(string action, Exception ex)
    {
        StatusMessage = $"{action}时出现异常：{ex.Message}";
        _log.Error($"{action}时出现异常：{ex.Message}", ex.ToString());
    }

    public void ApplyToken(string token)
    {
        Settings.Token = token.Trim();
        _github.Configure(ResolveToken());
        RateLimitText = "API 额度：未获取";
    }

    public void CaptureWindow(double x, double y, double width, double height)
    {
        Settings.WindowX = x;
        Settings.WindowY = y;
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        SaveSettings();
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        var user = UserName.Trim();
        if (user.Length == 0)
        {
            StatusMessage = "请先输入 GitHub 用户名。";
            return;
        }

        if (_loading || IsCloning)
        {
            // 克隆进行中不能重建列表：清空集合会丢掉每一行的克隆进度，也让「停止」失去目标。
            if (IsCloning)
                StatusMessage = "正在克隆，请先点击「停止」再重新获取。";

            return;
        }

        if (string.Equals(_loadedUser, user, StringComparison.OrdinalIgnoreCase)
            && (Repos.Count > 0 || StarredRows.Count > 0))
        {
            StatusMessage = $"已加载「{user}」的数据，点击「刷新」可重新获取。";
            return;
        }

        if (_cache.TryGetValue(user, out var cached))
        {
            ShowFromCache(user, cached);
            return;
        }

        await LoadAsync(user);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var user = UserName.Trim();
        if (user.Length == 0)
        {
            StatusMessage = "请先输入 GitHub 用户名。";
            return;
        }

        await LoadAsync(user);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // 两个取消源都要触发：克隆过程中可能还叠加着一次抓取，
        // 只取消其中一个会留下仍在运行的 git 子进程。
        _cloneCts?.Cancel();
        _fetchCts?.Cancel();
        StatusMessage = IsCloning ? "正在停止克隆…" : "正在停止…";
    }

    [RelayCommand(CanExecute = nameof(CanStartClone))]
    private async Task StartCloneAsync()
    {
        var selected = CollectSelected();

        if (selected.Count == 0)
        {
            StatusMessage = "请先勾选要克隆的仓库。";
            return;
        }

        await RunCloneAsync(selected);
    }

    [RelayCommand(CanExecute = nameof(CanStartClone))]
    private async Task RetryFailedAsync()
    {
        var failed = AllRows().Where(row => row.State == CloneState.Failed).ToList();

        if (failed.Count == 0)
        {
            StatusMessage = "没有失败的仓库需要重试。";
            return;
        }

        await RunCloneAsync(failed);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (RepoRowViewModel row in RowsOfActiveTab())
            row.IsSelected = true;
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (RepoRowViewModel row in RowsOfActiveTab())
            row.IsSelected = !row.IsSelected;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.Directory);
        }
        catch
        {
            // 打不开时忽略
        }

        if (!ShellHelper.OpenDirectory(_log.Directory))
            _log.Warning($"无法打开日志文件夹：{_log.Directory}");
    }

    private bool CanStop() => IsBusy || IsCloning;

    private bool CanStartClone() => !IsCloning;

    private string ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(Settings.Token))
            return Settings.Token.Trim();

        return Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim() ?? "";
    }

    private void OnEntryWritten(LogEntry entry)
    {
        LogRows.Add(new LogRowViewModel(entry));

        while (LogRows.Count > MaxLogRows)
            LogRows.RemoveAt(0);
    }

    partial void OnFilterTextChanged(string value)
    {
        ReposView.Refresh();
        StarredView.Refresh();
    }

    partial void OnHideForksChanged(bool value)
    {
        ReposView.Refresh();
        StarredView.Refresh();
    }

    private bool PassesFilter(RepoRowViewModel row)
    {
        if (HideForks && row.IsFork)
            return false;

        var text = FilterText.Trim();
        if (text.Length == 0)
            return true;

        return Contains(row.Name, text) || Contains(row.Description, text) || Contains(row.Language, text);
    }

    private static bool Contains(string? source, string text) =>
        source is not null && source.Contains(text, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<RepoRowViewModel> AllRows() => Repos.Concat(StarredRows);

    /// <summary>全选/反选只作用于当前 Tab 的当前过滤结果。</summary>
    private IEnumerable RowsOfActiveTab() => ActiveTabIndex == 1 ? StarredView : ReposView;

    private List<RepoRowViewModel> CollectSelected() =>
        AllRows()
            .Where(row => row.IsSelected)
            .GroupBy(row => row.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    /// <summary>把本次运行抓取成功的用户数据搬回界面，不再访问网络。</summary>
    private void ShowFromCache(string user, CachedUser cached)
    {
        Repos.Clear();
        StarredRows.Clear();

        foreach (var item in cached.Repos)
            Repos.Add(new RepoRowViewModel(item, false));

        foreach (var item in cached.Starred)
            StarredRows.Add(new RepoRowViewModel(item, true));

        _loadedUser = user;
        StatusMessage = $"已从本次运行的缓存恢复「{user}」：仓库 {Repos.Count} 个，Starred {StarredRows.Count} 个。点击「刷新」可重新获取。";
        _log.Info(StatusMessage);
    }

    private async Task LoadAsync(string user)
    {
        if (_loading)
            return;

        if (IsCloning)
        {
            // 克隆在跑的时候清空列表会丢掉每一行的克隆进度，也让「停止」失去目标。
            StatusMessage = "正在克隆，请先点击「停止」再重新获取。";
            return;
        }

        _loading = true;
        IsBusy = true;
        _fetchCts = new CancellationTokenSource();
        var cancellationToken = _fetchCts.Token;

        Repos.Clear();
        StarredRows.Clear();
        _loadedUser = user;
        _github.Configure(ResolveToken());
        StatusMessage = $"正在获取「{user}」的仓库与 Starred 列表…";
        _log.Info($"开始获取「{user}」的仓库与 Starred 列表。");

        var reposBatch = new Progress<IReadOnlyList<RepoItem>>(batch => AddBatch(Repos, batch, false));
        var starredBatch = new Progress<IReadOnlyList<RepoItem>>(batch => AddBatch(StarredRows, batch, true));
        var rateProgress = new Progress<RateLimitSnapshot>(UpdateRateLimit);

        try
        {
            await _github.FetchAsync(user, false, reposBatch, rateProgress, cancellationToken);
            StatusMessage = $"已加载 {Repos.Count} 个仓库，正在获取 Starred 列表…";
            await _github.FetchAsync(user, true, starredBatch, rateProgress, cancellationToken);

            _cache[user] = new CachedUser(
                Repos.Select(row => row.Model).ToList(),
                StarredRows.Select(row => row.Model).ToList());

            StatusMessage = $"加载完成：「{user}」仓库 {Repos.Count} 个，Starred {StarredRows.Count} 个。";
            _log.Success(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            // 抓取被中断时数据不完整，不写入缓存，避免下次静默恢复半份数据。
            StatusMessage = $"已停止获取（仓库 {Repos.Count} 个、Starred {StarredRows.Count} 个已保留）。";
            _log.Warning("获取已取消。");
        }
        catch (GitHubFetchException ex)
        {
            StatusMessage = ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            _loading = false;
            IsBusy = false;
            _fetchCts?.Dispose();
            _fetchCts = null;
        }
    }

    private static void AddBatch(ObservableCollection<RepoRowViewModel> target, IReadOnlyList<RepoItem> batch, bool starred)
    {
        foreach (var item in batch)
            target.Add(new RepoRowViewModel(item, starred));
    }

    private void UpdateRateLimit(RateLimitSnapshot snapshot)
    {
        var authentication = snapshot.Authenticated ? "已认证" : "未认证";
        RateLimitText = $"API 额度 {snapshot.Remaining}/{snapshot.Limit}（{authentication}）· 重置 {snapshot.ResetAt.LocalDateTime:HH:mm}";
    }

    private async Task RunCloneAsync(IReadOnlyList<RepoRowViewModel> rows)
    {
        try
        {
            Directory.CreateDirectory(TargetDirectory);
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法创建目标目录：{ex.Message}";
            _log.Error($"无法创建目标目录 {TargetDirectory}", ex.ToString());
            return;
        }

        var service = new GitCloneService();
        var shallow = ShallowClone;
        var parallelism = Math.Clamp(Settings.Parallelism, 1, 8);

        _cloneCts = new CancellationTokenSource();
        var cancellationToken = _cloneCts.Token;
        IsCloning = true;

        foreach (var row in rows)
        {
            row.Progress = 0;
            row.RawLog = "";
            row.SetState(CloneState.Queued, "等待中");
        }

        _log.Info($"开始克隆 {rows.Count} 个仓库到 {TargetDirectory}（并发 {parallelism}，浅克隆：{(shallow ? "是" : "否")}）。");

        var succeeded = 0;
        var existed = 0;
        var failed = 0;
        var cancelled = 0;

        using var semaphore = new SemaphoreSlim(parallelism);

        async Task RunRowAsync(RepoRowViewModel row)
        {
            ClonePlan plan;

            try
            {
                plan = service.Plan(row.Model, TargetDirectory);
            }
            catch (Exception ex)
            {
                // 目标目录名不合法等情况下，单行失败不能把整批克隆带崩。
                row.SetState(CloneState.Failed, "失败");
                failed++;
                _log.Error($"{row.FullName} 无法规划目标路径：{ex.Message}", ex.ToString());
                return;
            }

            if (plan.Decision == ClonePathDecision.AlreadyExists)
            {
                row.LocalPath = plan.TargetPath;
                row.SetState(CloneState.AlreadyExists, "已存在");
                existed++;
                _log.Info($"跳过 {row.FullName}：{plan.Reason}（{plan.TargetPath}）");
                return;
            }

            try
            {
                await semaphore.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                row.SetState(CloneState.Cancelled, "已取消");
                cancelled++;
                return;
            }
            catch (Exception ex)
            {
                row.SetState(CloneState.Failed, "失败");
                failed++;
                _log.Error($"{row.FullName} 等待并发槽位失败：{ex.Message}", ex.ToString());
                return;
            }

            try
            {
                row.LocalPath = plan.TargetPath;

                if (row.Model.IsPrivate)
                    _log.Info($"注意：{row.FullName} 是私有仓库，克隆时可能弹出 Git 凭据登录窗口。");

                row.SetState(CloneState.Downloading, "克隆中 0%");

                var progress = new Progress<double>(percent =>
                {
                    row.Progress = percent;
                    row.StatusText = $"克隆中 {percent:0}%";
                });
                var output = new Progress<string>(row.AppendRaw);

                var result = await service.CloneAsync(row.Model, plan.TargetPath, shallow, progress, output, cancellationToken);

                if (result.Success)
                {
                    row.Progress = 100;
                    row.SetState(CloneState.Completed, "已完成");
                    row.IsSelected = false;
                    succeeded++;
                    _log.Success($"{row.FullName} 克隆完成 → {plan.TargetPath}");
                    return;
                }

                var cleanup = await service.CleanupIncompleteAsync(plan.TargetPath);

                if (result.Canceled)
                {
                    row.SetState(CloneState.Cancelled, "已取消");
                    cancelled++;
                    _log.Warning($"{row.FullName} 已取消。{DescribeCleanup(cleanup)}", result.Output);
                    return;
                }

                row.SetState(CloneState.Failed, $"失败（退出码 {result.ExitCode}）");
                failed++;
                _log.Error($"{row.FullName} 克隆失败：{Summarize(result.Output)}{DescribeCleanup(cleanup)}", result.Output);
            }
            catch (Exception ex)
            {
                var cleanup = await service.CleanupIncompleteAsync(plan.TargetPath);
                row.SetState(CloneState.Failed, "失败");
                failed++;
                _log.Error($"{row.FullName} 克隆异常：{ex.Message}{DescribeCleanup(cleanup)}", ex.ToString());
            }
            finally
            {
                semaphore.Release();
            }
        }

        try
        {
            await Task.WhenAll(rows.Select(RunRowAsync));
        }
        catch (Exception ex)
        {
            // 兜底：命令处理器里逃逸的异常会直接打到界面线程上。
            _log.Error($"克隆任务出现未预期异常：{ex.Message}", ex.ToString());
        }
        finally
        {
            IsCloning = false;
            _cloneCts?.Dispose();
            _cloneCts = null;
        }

        StatusMessage = $"克隆结束：成功 {succeeded}，已存在 {existed}，失败 {failed}，取消 {cancelled}。";
        _log.Info(StatusMessage);
    }

    private static string DescribeCleanup(CleanupOutcome outcome) => outcome switch
    {
        CleanupOutcome.Deleted => "（已删除未完成的目录）",
        CleanupOutcome.KeptValidRepository => "（目录内已存在有效仓库，未删除）",
        CleanupOutcome.Failed => "（清理未完成目录失败，请手动检查）",
        _ => "",
    };

    private static string Summarize(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var fatal = lines.FirstOrDefault(line =>
            line.Contains("fatal:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error:", StringComparison.OrdinalIgnoreCase));

        if (fatal is not null)
            return fatal;

        return lines.Length > 0 ? lines[^1] : "git 未返回任何输出";
    }

    /// <summary>本次运行内已抓取成功的用户数据，按用户名缓存，避免来回切换时重复消耗 API 额度。</summary>
    private sealed record CachedUser(List<RepoItem> Repos, List<RepoItem> Starred);
}
