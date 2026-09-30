using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace CheckGithub.Views;

public partial class RepoTableView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.Register<RepoTableView, IEnumerable?>(nameof(Source));

    public RepoTableView()
    {
        InitializeComponent();
    }

    public IEnumerable? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
}
