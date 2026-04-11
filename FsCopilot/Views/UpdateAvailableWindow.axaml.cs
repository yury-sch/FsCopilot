namespace FsCopilot.Views;

using ViewModels;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(UpdateAvailableViewModel vm)
    {
        InitializeComponent();

        DataContext = vm;

        vm.CloseRequested += () => Close();
    }
}
