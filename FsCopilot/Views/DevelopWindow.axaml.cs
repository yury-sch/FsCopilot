namespace FsCopilot.Views;

using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViewModels;

public partial class DevelopWindow : Window
{
    public DevelopWindow()
    {
        InitializeComponent();
        ExpandOnRowClick(DefTree);
        Opened += OnOpened;
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

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not DevelopViewModel vm)
            return;

        vm.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private async void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DevelopViewModel.FoundNode))
            return;

        if (DataContext is not DevelopViewModel vm || vm.FoundNode is null)
            return;

        await ScrollToNodeAsync(vm.FoundNode);
    }

    private async Task ScrollToNodeAsync(Node node)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var container = FindTreeViewItem(DefTree, node);
        if (container is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            container = FindTreeViewItem(DefTree, node);
        }

        if (container is null)
            return;

        container.BringIntoView();

        node.IsPulse = true;
        await Task.Delay(1000);
        node.IsPulse = false;
    }

    private static TreeViewItem? FindTreeViewItem(Control root, object item)
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is TreeViewItem tvi && ReferenceEquals(tvi.DataContext, item))
                return tvi;
        }

        return null;
    }
}