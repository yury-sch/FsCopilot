namespace FsCopilot;

using System.Text.RegularExpressions;
using Connection;
using Microsoft.Extensions.DependencyInjection;
using Network;
using ReactiveUI.Avalonia;
using ReactiveUI.Avalonia.Splat;
using Serilog;
using Serilog.Events;
using Simulation;
using ViewModels;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isDebug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                path: "log",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                shared: true,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: isDev || isDebug ? LogEventLevel.Verbose : LogEventLevel.Debug
            )
            .CreateLogger();

        try
        {
            DeployModuleToCommunity();
            BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Something went wrong");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
    
    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]); 

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isExperimental = args.Any(a => string.Equals(a, "--experimental", StringComparison.OrdinalIgnoreCase));
        var peerId = Random.String(8);
        var name = Environment.UserName;
        
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    if (!isDev)
                    {
                        services.AddSingleton<INetwork>(!isExperimental
                            ? new P2PNetwork("p2p.fscopilot.com", peerId, name)
                            : new HybridNetwork("p2p.fscopilot.com", peerId, name));
                        services.AddSingleton<MasterSwitch>();
                        services.AddSingleton<Coordinator>();
                        services.AddSingleton(new SimClient("FS Copilot"));
                        services.AddSingleton(sp => new MainViewModel(
                            peerId,
                            name,
                            sp.GetRequiredService<INetwork>(),
                            sp.GetRequiredService<SimClient>(),
                            sp.GetRequiredService<MasterSwitch>(),
                            sp.GetRequiredService<Coordinator>()
                        ));    
                    }
                    else
                    {
                        services.AddSingleton(new SimClient("FS Copilot DEV"));
                        services.AddSingleton<DevelopViewModel>();
                    }
                },
                null)
            .RegisterReactiveUIViewsFromEntryAssembly()
            .WithInterFont()
            .LogToTrace();
    }

    private static void DeployModuleToCommunity()
    {
        Log.Debug("Deploying module to Community");
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Community", "fscopilot-bridge");
            if (!Directory.Exists(source))
            {
                Log.Debug("Missing FS copilot module. Skipped");
                return;
            }

            var packagesPaths = GetInstalledPackagesPath();
            foreach (var packagesPath in packagesPaths)
            {
                var community = Path.Combine(packagesPath, "Community");
                if (!Directory.Exists(community)) Directory.CreateDirectory(community);
                Log.Debug("Found community folder: {0}", community);
                var target = Path.Combine(community, "fscopilot-bridge");
                if (Directory.Exists(target)) Directory.Delete(target, true);
                CopyDirectory(source, target, overwrite: true);
                // {
                //     CopyDirectory(source, target, overwrite: true);
                // }
                // else
                // {
                //     CopyDirectory(Path.Combine(source, "html_ui"), Path.Combine(target, "html_ui"), overwrite: true);
                //     File.Copy(Path.Combine(source, "layout.json"), Path.Combine(target, "layout.json"), overwrite: true);
                //     File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(target, "manifest.json"), overwrite: true);
                // }
                Log.Debug("FS copilot module has been deployed to community");
            }
        }
        catch (Exception e)
        {
            Log.Debug(e.Message);
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
            Log.Debug("Detected configuration path: {0}", path);
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
