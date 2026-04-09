using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FsCopilot;

public class Installer
{
    private const string BridgeName = "fscopilot-bridge";
    
    private const string YcPackage = "YourControls";
    
    private static readonly string Source = Path.Combine(AppContext.BaseDirectory, "Community", BridgeName);

    private static readonly string ProfilesPath = Path.Combine(AppContext.BaseDirectory, "Definitions");

    private static readonly IReadOnlyCollection<string> PackagesPaths = new List<string>
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
        }
        .Where(File.Exists)
        .Select(ReadInstalledPackagesPath)
        .Where(path => !string.IsNullOrEmpty(path))
        .Select(packagesPath => Path.Combine(packagesPath, "Community"))
        .ToArray();

    /// Finds InstalledPackagesPath from UserCfg.opt (MS Store or Steam).
    private static string ReadInstalledPackagesPath(string userCfgPath)
    {
        // Log.Debug("[Application] Detected configuration path: {0}", userCfgPath);
        foreach (var line in File.ReadAllLines(userCfgPath))
        {
            if (!line.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(line, "\"([^\"]+)\"");
            if (!m.Success) continue;
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(m.Groups[1].Value));
        }

        return string.Empty;
    }
    
    public static bool RequiresInstallation =>
        RequiresBridgeInstallation || RequiresProfilesSetup || RequiresUninstallYC;

    public static bool RequiresBridgeInstallation =>
        Directory.Exists(Source) &&
        PackagesPaths
            .Select(community => Path.Combine(community, BridgeName))
            .Any(target => !Directory.Exists(target) || DirectoriesDiffer(Source, target));
    
    public static bool RequiresUninstallYC => PackagesPaths
        .Select(community => Path.Combine(community, YcPackage))
        .Any(Directory.Exists);

    public static bool RequiresProfilesSetup =>
        !HasInstalledYamlProfiles();
    
    private static bool HasInstalledYamlProfiles()
    {
        if (!Directory.Exists(ProfilesPath))
            return false;

        return Directory.EnumerateFiles(ProfilesPath, "*", SearchOption.TopDirectoryOnly)
            .Any(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
            });
    }

    public static void DeployBridge()
    {
        try
        {
            if (!Directory.Exists(Source))
            {
                Log.Debug("[Application] Missing FS Copilot module. Skipped");
                return;
            }

            foreach (var community in PackagesPaths)
            {
                Log.Debug("[Application] Found community folder: {0}", community);
                if (!Directory.Exists(community)) Directory.CreateDirectory(community);
                var target = Path.Combine(community, BridgeName);
                
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                
                CopyDirectory(Source, target, overwrite: true);
                Log.Debug("[Application] FS Copilot module has been deployed to community");
            }
        }
        catch (Exception e)
        {
            Log.Debug(e.Message);
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
    
    private static bool DirectoriesDiffer(string sourceDir, string targetDir)
    {
        var sourceFiles = GetDirectoryFileMap(sourceDir);
        var targetFiles = GetDirectoryFileMap(targetDir);

        if (sourceFiles.Count != targetFiles.Count)
            return true;

        foreach (var (relativePath, sourceFilePath) in sourceFiles)
        {
            if (!targetFiles.TryGetValue(relativePath, out var targetFilePath))
                return true;

            var sourceInfo = new FileInfo(sourceFilePath);
            var targetInfo = new FileInfo(targetFilePath);

            if (sourceInfo.Length != targetInfo.Length)
                return true;

            if (!FilesHaveSameContent(sourceFilePath, targetFilePath))
                return true;
        }

        return false;
    }

    private static Dictionary<string, string> GetDirectoryFileMap(string rootDir)
    {
        return Directory
            .GetFiles(rootDir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(rootDir, path).Replace('\\', '/'),
                path => path,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool FilesHaveSameContent(string file1, string file2)
    {
        using var sha256 = SHA256.Create();

        using var stream1 = File.OpenRead(file1);
        using var stream2 = File.OpenRead(file2);

        var hash1 = sha256.ComputeHash(stream1);
        var hash2 = sha256.ComputeHash(stream2);

        return hash1.AsSpan().SequenceEqual(hash2);
    }
    
    public static void DeployProfiles(IEnumerable<ProfileFile> profiles)
    {
        try
        {
            Directory.CreateDirectory(ProfilesPath);

            foreach (var profile in profiles)
            {
                var safeRelativePath = profile.RelativePath.Replace('\\', '/').TrimStart('/');

                if (string.IsNullOrWhiteSpace(safeRelativePath))
                    continue;

                var fullPath = Path.GetFullPath(Path.Combine(ProfilesPath, safeRelativePath));
                var profilesRootFullPath = Path.GetFullPath(ProfilesPath);

                if (!fullPath.StartsWith(profilesRootFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(fullPath, profile.Content);
            }
        }
        catch (Exception e)
        {
            Log.Warning("[Application] Failed to deploy profiles. {Error}", e.Message);
        }
    }

    public static void DeleteYC()
    {
        foreach (var community in PackagesPaths)
        {
            var target = Path.Combine(community, YcPackage);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}