namespace FsCopilot;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Connection;
using Network;
using Simulation;
using ViewModels;
using Views;
using WatsonWebsocket;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DeployModuleToCommunity();
        // var configuration = new Configuration();
        
        // var sc = new ServiceCollection()
        //     .AddSingleton<ICoordinatorState>()
        //     .AddSingleton<MainWindowViewModel>();

        // var services = sc.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
            
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            if (!dev)
            {
                var peer2Peer = new Peer2Peer("p2p.fscopilot.ru", 0);
                var simConnect = new SimClient("FS Copilot");
                var control = new MasterSwitch(simConnect, peer2Peer);
                var coordinator = new Coordinator(simConnect, peer2Peer, control);
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(peer2Peer, simConnect, control, coordinator)
                };
            }
            else
            {
                var simConnect = new SimClient("FS Copilot Develop");
                desktop.MainWindow = new DevelopWindow
                {
                    DataContext = new DevelopWindowViewModel(simConnect)
                };
            }
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
    
    /// Finds InstalledPackagesPath from UserCfg.opt (MS Store or Steam).
    private static string? GetInstalledPackagesPath()
    {
        // MS Store location
        var msStore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");

        //AppData\Roaming\Microsoft Flight Simulator 2024\Packages\Community
        // Steam location
        var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft Flight Simulator", "UserCfg.opt");
        
        // Steam location
        var steam24 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "Microsoft Flight Simulator 2024", "UserCfg.opt");

        var cfgPath = File.Exists(msStore) 
            ? msStore 
            : File.Exists(steam) 
                ? steam 
                : File.Exists(steam24) 
                    ? steam24 
                    : null;
        if (cfgPath == null)
        {
            Debug.WriteLine("Failed to detect installed simulator");
            return null;
        }
        Debug.WriteLine("Detected configuration path: {0}", cfgPath);

        foreach (var line in File.ReadAllLines(cfgPath))
        {
            if (!line.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(line, "\"([^\"]+)\"");
            if (m.Success) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(m.Groups[1].Value));
        }
        
        Debug.WriteLine("Failed to execute installed packages path");
        return null;
    }
    
    private static void DeployModuleToCommunity()
    {
        Debug.WriteLine("Deploying module to Community");
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Community", "FsCopilot");
            if (!Directory.Exists(source))
            {
                Debug.WriteLine("Missing FS copilot module. Skipped");
                return;
            }

            var basePath = GetInstalledPackagesPath();
            if (string.IsNullOrWhiteSpace(basePath)) return;
            var community = Path.Combine(basePath, "Community");
            if (!Directory.Exists(community)) Directory.CreateDirectory(community);
            Debug.WriteLine("Found community folder: {0}", community);

            var target = Path.Combine(community, "FsCopilot");

            if (!Directory.Exists(target))
            {
                CopyDirectory(source, target, overwrite: true);
                Debug.WriteLine("FS copilot module has been deployed to community");
            }
            else
            {
                Debug.WriteLine("FS copilot module already deployed. Skipping deployment.");
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSub, overwrite);
        }
    }
}