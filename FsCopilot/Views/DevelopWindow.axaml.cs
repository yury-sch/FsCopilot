namespace FsCopilot.Views;

using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ViewModels;

public partial class DevelopWindow : Window
{
    public DevelopWindow()
    {
        InitializeComponent();
        ExpandOnRowClick(DefTree);
    }

    private static void ExpandOnRowClick(TreeView tv)
    {
        tv.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not TreeView tv) return;

            var kind = e.GetCurrentPoint(tv).Properties.PointerUpdateKind;
            if (kind != PointerUpdateKind.LeftButtonPressed) return;
            if (e.Source is ToggleButton) return;

            if (e.Source is not Visual v) return;
            var tvi = v.FindAncestorOfType<TreeViewItem>();
            if (tvi is null) return;

            if (tvi.DataContext is not Node node) return;
            if (node.SubNodes is null || node.SubNodes.Count == 0) return;

            tvi.IsExpanded = !tvi.IsExpanded;

            e.Handled = true;
        }
    }
}