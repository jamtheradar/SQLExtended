using System.Windows.Input;

namespace SQLExtended.SchemaCompare.UI;

public static class Commands
{
    public static readonly RoutedUICommand Compare = new("Compare", nameof(Compare), typeof(Commands));
    public static readonly RoutedUICommand NextDifference = new("Next difference", nameof(NextDifference), typeof(Commands));
    public static readonly RoutedUICommand PreviousDifference = new("Previous difference", nameof(PreviousDifference), typeof(Commands));
    public static readonly RoutedUICommand NextObject = new("Next object", nameof(NextObject), typeof(Commands));
    public static readonly RoutedUICommand PreviousObject = new("Previous object", nameof(PreviousObject), typeof(Commands));
}
