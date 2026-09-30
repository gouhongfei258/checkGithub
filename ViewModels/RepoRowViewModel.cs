using CheckGithub.Models;
using CheckGithub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CheckGithub.ViewModels;

public partial class RepoRowViewModel : ObservableObject
{
    private const int MaxRawLogChars = 20000;

    public RepoRowViewModel(RepoItem model, bool starred)
    {
        Model = model;
        IsStarred = starred;
        StatusText = "未下载";
        RawLog = "";
    }

    public RepoItem Model { get; }

    public bool IsStarred { get; }

    public string Name => Model.Name;
    public string Owner => Model.Owner;
    public string FullName => Model.FullName;
    public string? Description => Model.Description;
    public string? Language => Model.Language;
    public int Stars => Model.Stars;
    public string StarsText => Model.Stars.ToString("N0");
    public bool IsFork => Model.IsFork;
    public bool IsPrivateRepo => Model.IsPrivate;
    public string VisibilityText => Model.IsPrivate ? "私有" : "公开";
    public DateTimeOffset UpdatedAt => Model.UpdatedAt;
    public string UpdatedText => Model.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial CloneState State { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRawLog))]
    public partial string RawLog { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalPath))]
    public partial string? LocalPath { get; set; }

    public bool IsBusy => State is CloneState.Queued or CloneState.Downloading;

    public bool HasRawLog => RawLog.Length > 0;

    public bool HasLocalPath => !string.IsNullOrWhiteSpace(LocalPath);

    public void SetState(CloneState state, string statusText)
    {
        State = state;
        StatusText = statusText;
    }

    public void AppendRaw(string line)
    {
        var text = RawLog.Length == 0 ? line : RawLog + Environment.NewLine + line;

        if (text.Length > MaxRawLogChars)
            text = text[^MaxRawLogChars..];

        RawLog = text;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (LocalPath is { Length: > 0 } path)
            ShellHelper.OpenDirectory(path);
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        ShellHelper.OpenUrl(Model.HtmlUrl);
    }
}
