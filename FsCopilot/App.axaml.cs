namespace FsCopilot;

using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Network;
using Simulation;
using Splat;
using ViewModels;
using Views;

public class App : Application
{
    public static readonly string Version =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
            var skipInstall = args.Contains("--skip-install", StringComparer.OrdinalIgnoreCase);
            
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            if (!skipInstall && Installer.RequiresInstallation)
            {
                var vm = Locator.Current.GetService<SetupViewModel>()!;
                var setup = new SetupWindow { DataContext = vm };

                vm.Completed += () =>
                {
                    CreateWindow(desktop, dev);
                    setup.Close();
                };

                desktop.MainWindow = setup;
            }
            else
            {
                CreateWindow(desktop, dev);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CreateWindow(IClassicDesktopStyleApplicationLifetime desktop, bool dev)
    {
        if (!dev)
        {
            desktop.Exit += (_, _) =>
            {
                Locator.Current.GetService<INetwork>()?.Disconnect();
                Locator.Current.GetService<MasterSwitch>()?.TakeControl();
            };
        }
        
        var window = desktop.MainWindow = !dev 
            ? new MainWindow { DataContext = Locator.Current.GetService<MainViewModel>() }
            : new DevelopWindow { DataContext = Locator.Current.GetService<DevelopViewModel>() };
        window.Show();
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