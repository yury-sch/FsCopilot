namespace FsCopilot;

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Connection;
using Network;
using Simulation;
using ViewModels;
using Views;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var peer2Peer = new Peer2Peer("p2p.fscopilot.com", 0);
        var simConnect = new SimConnectClient("FS Copilot");
        var control = new MasterSwitch(simConnect, peer2Peer);
        
        var coordinator = new Coordinator(simConnect, peer2Peer, control);
        // var configuration = new Configuration();
        
        // var sc = new ServiceCollection()
        //     .AddSingleton<ICoordinatorState>()
        //     .AddSingleton<MainWindowViewModel>();

        // var services = sc.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(peer2Peer, simConnect, control, coordinator)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}