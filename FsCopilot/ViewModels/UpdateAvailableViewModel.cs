namespace FsCopilot.ViewModels;

using ReactiveUI;

public sealed class UpdateAvailableViewModel : ReactiveObject
{
    public string CurrentVersion { get; }
    public string LatestVersion { get; }

    public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> LaterCommand { get; }

    public event Action? CloseRequested;

    public UpdateAvailableViewModel(string currentVersion, string latestVersion, string url)
    {
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion.StartsWith('v') ? latestVersion[1..] : latestVersion;

        OpenGitHubCommand = ReactiveCommand.Create(() =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            CloseRequested?.Invoke();
        });

        LaterCommand = ReactiveCommand.Create(() =>
        {
            CloseRequested?.Invoke();
        });
    }
}
