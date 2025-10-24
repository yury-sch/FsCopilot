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
    private static IEnumerable<string> GetInstalledPackagesPath()
    {
        var cfgPaths = new List<string>()
        {
            // MS Store location
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            // MS Store 2024 location
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            // Steam location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator", "UserCfg.opt"),
            // Steam 2024 location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator 2024", "UserCfg.opt")
        };

        foreach (var path in cfgPaths.Where(File.Exists))
        {
            Debug.WriteLine("Detected configuration path: {0}", path);
            foreach (var line in File.ReadAllLines(path))
            {
                if (!line.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                var m = Regex.Match(line, "\"([^\"]+)\"");
                if (m.Success)
                {
                    yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(m.Groups[1].Value));
                    break;
                }
            }
        }
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

            var basePaths = GetInstalledPackagesPath();
            foreach (var basePath in basePaths)
            {
                var community = Path.Combine(basePath, "Community");
                if (!Directory.Exists(community)) Directory.CreateDirectory(community);
                Debug.WriteLine("Found community folder: {0}", community);
                var target = Path.Combine(community, "FsCopilot");
                if (!Directory.Exists(target))
                {
                    CopyDirectory(source, target, overwrite: true);
                }
                else
                {
                    CopyDirectory(Path.Combine(source, "html_ui"), Path.Combine(target, "html_ui"), overwrite: true);
                    CopyDirectory(Path.Combine(source, "modules"), Path.Combine(target, "modules"), overwrite: true);
                }
                Debug.WriteLine("FS copilot module has been deployed to community");
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool overwrite)
    {
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

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