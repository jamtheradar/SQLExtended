using System.Windows;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Shell;

/// <summary>The shell's only window: a frame round the extension's <see cref="SchemaCompareView"/>.</summary>
internal sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Schema Compare — SQLExtended";
        Width = 1500;
        Height = 860;
        MinWidth = 900;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "SqlxSurface");

        View = new SchemaCompareView();
        Content = View;
        WindowTitleBar.Follow(this);
        Closing += (_, _) => View.SaveState();
    }

    public SchemaCompareView View { get; }
}
