using CheckGithub.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CheckGithub.ViewModels;

public partial class LogRowViewModel : ObservableObject
{
    public LogRowViewModel(LogEntry entry)
    {
        Entry = entry;
    }

    public LogEntry Entry { get; }

    public string TimeText => Entry.Time.ToString("HH:mm:ss");

    public string LevelText => Entry.Level switch
    {
        LogLevel.Success => "完成",
        LogLevel.Warning => "注意",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    public string Message => Entry.Message;

    public string? Detail => Entry.Detail;

    public bool HasDetail => Entry.HasDetail;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
    }
}
