using Avalonia.Input;

namespace FsCopilot.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
    }

    private void DragArea_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}