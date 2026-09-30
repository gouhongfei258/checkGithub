using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CheckGithub.ViewModels;

namespace CheckGithub.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var settings = viewModel.Settings;

        if (settings.WindowWidth > 200)
            Width = settings.WindowWidth;

        if (settings.WindowHeight > 200)
            Height = settings.WindowHeight;

        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            var position = new PixelPoint((int)x, (int)y);
            var visible = Screens.All.Any(screen =>
                screen.Bounds.Contains(new PixelPoint(position.X + 40, position.Y + 20)));

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = position;
            }
        }

        viewModel.LogRows.CollectionChanged += OnLogRowsChanged;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is MainWindowViewModel viewModel)
            viewModel.CaptureWindow(Position.X, Position.Y, Width, Height);
    }

    private void OnLogRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScroll.ScrollToEnd();
    }

    private async void OnBrowseTargetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择克隆目标目录",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
                return;

            viewModel.TargetDirectory = folders[0].Path.LocalPath;
            viewModel.SaveSettings();
        }
        catch (Exception ex)
        {
            // async void 事件处理器里逃逸的异常会直接打到界面线程上。
            viewModel.ReportUnexpected("选择目标目录", ex);
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            var window = new SettingsWindow
            {
                DataContext = new SettingsWindowViewModel(viewModel),
            };

            window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            viewModel.ReportUnexpected("打开设置", ex);
        }
    }

    private void OnUserNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel viewModel)
            return;

        if (viewModel.FetchCommand.CanExecute(null))
            viewModel.FetchCommand.Execute(null);

        e.Handled = true;
    }
}
