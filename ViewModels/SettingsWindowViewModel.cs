using CommunityToolkit.Mvvm.ComponentModel;

namespace CheckGithub.ViewModels;

public partial class SettingsWindowViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public SettingsWindowViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        TokenInput = owner.EffectiveToken;
        Parallelism = Math.Clamp(owner.Settings.Parallelism, 1, 8);
        TokenHint = owner.TokenFromEnvironment
            ? "当前使用的是环境变量 GITHUB_TOKEN 中的令牌，在此填写会覆盖它。"
            : "留空时依次尝试环境变量 GITHUB_TOKEN；仍然为空则以匿名方式访问，每小时只有 60 次请求。";
        ConfigPathHint = $"配置文件：{owner.SettingsFilePath}";
    }

    public IReadOnlyList<int> ParallelismOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    public string TokenHint { get; }

    public string ConfigPathHint { get; }

    [ObservableProperty]
    public partial string TokenInput { get; set; }

    [ObservableProperty]
    public partial bool ShowToken { get; set; }

    [ObservableProperty]
    public partial int Parallelism { get; set; }

    public void Apply()
    {
        _owner.Settings.Parallelism = Parallelism;
        _owner.ApplyToken(TokenInput);
        _owner.SaveSettings();
    }
}
