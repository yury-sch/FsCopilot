namespace FsCopilot.ViewModels;

using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Connection;
using ReactiveUI;

public enum InstallerStep
{
    Initial,
    DownloadingProfiles,
    NeedRemoveYourControls,
    InstallingBridge,
    Completed,
    Failed
}

public sealed class SetupViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _d = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Updater _updater;

    private bool _isMsfsRunning;
    private bool _isBusy;
    private InstallerStep _step = InstallerStep.Initial;
    private int _downloadedProfilesCount;
    private string? _errorText;

    public event Action? Completed;

    public bool IsMsfsRunning
    {
        get => _isMsfsRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isMsfsRunning, value);
            this.RaisePropertyChanged(nameof(CanExecutePrimaryAction));
            this.RaisePropertyChanged(nameof(Subtitle));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanExecutePrimaryAction));
        }
    }

    public InstallerStep Step
    {
        get => _step;
        private set
        {
            this.RaiseAndSetIfChanged(ref _step, value);
            this.RaisePropertyChanged(nameof(Subtitle));
            this.RaisePropertyChanged(nameof(PrimaryButtonText));
            this.RaisePropertyChanged(nameof(CanExecutePrimaryAction));
            this.RaisePropertyChanged(nameof(ShowPrimaryButton));
        }
    }

    public int DownloadedProfilesCount
    {
        get => _downloadedProfilesCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _downloadedProfilesCount, value);
            this.RaisePropertyChanged(nameof(Subtitle));
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorText, value);
            this.RaisePropertyChanged(nameof(Subtitle));
        }
    }

    public bool CanExecutePrimaryAction =>
        !IsBusy &&
        !IsMsfsRunning &&
        Step is InstallerStep.Initial
            or InstallerStep.NeedRemoveYourControls
            or InstallerStep.Completed
            or InstallerStep.Failed;

    public bool ShowPrimaryButton => true;

    public string Subtitle =>
        Step switch
        {
            InstallerStep.Initial when IsMsfsRunning =>
                "Setup required. Close Microsoft Flight Simulator to continue",

            InstallerStep.Initial =>
                "Setup required",

            InstallerStep.DownloadingProfiles =>
                "Downloading profile pack...",

            InstallerStep.NeedRemoveYourControls when IsMsfsRunning =>
                "YourControls module detected. Close Microsoft Flight Simulator to continue",

            InstallerStep.NeedRemoveYourControls =>
                "YourControls module detected in Community folder. Remove it to continue",

            InstallerStep.InstallingBridge =>
                "Installing FS Copilot components...",

            InstallerStep.Completed when DownloadedProfilesCount > 0 =>
                $"Setup complete. Downloaded {DownloadedProfilesCount} profiles",

            InstallerStep.Completed =>
                "Setup complete",

            InstallerStep.Failed =>
                ErrorText ?? "Setup failed",

            _ => "Setup required"
        };

    public string PrimaryButtonText =>
        Step switch
        {
            InstallerStep.Initial => "Install",
            InstallerStep.NeedRemoveYourControls => "Remove YourControls",
            InstallerStep.Completed => "Continue",
            InstallerStep.Failed => "Retry",
            _ => "Working..."
        };

    public ReactiveCommand<Unit, Unit> PrimaryCommand { get; }

    public SetupViewModel(SimClient sim, Updater updater)
    {
        _updater = updater;

        sim.Connected
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isConnected => IsMsfsRunning = isConnected)
            .DisposeWith(_d);

        var canExecute = this.WhenAnyValue(
            x => x.CanExecutePrimaryAction);

        PrimaryCommand = ReactiveCommand.CreateFromTask(
            ExecutePrimaryActionAsync,
            canExecute);

        PrimaryCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                ErrorText = ex.Message;
                Step = InstallerStep.Failed;
                IsBusy = false;
            })
            .DisposeWith(_d);
    }

    private async Task ExecutePrimaryActionAsync()
    {
        switch (Step)
        {
            case InstallerStep.Initial:
            case InstallerStep.Failed:
                await RunInstallationFlowAsync();
                break;

            case InstallerStep.NeedRemoveYourControls:
                await RemoveYourControlsAndContinueAsync();
                break;

            case InstallerStep.Completed:
                Completed?.Invoke();
                break;
        }
    }

    private async Task RunInstallationFlowAsync()
    {
        IsBusy = true;
        ErrorText = null;
        DownloadedProfilesCount = 0;

        try
        {
            if (Installer.RequiresProfilesSetup)
            {
                Step = InstallerStep.DownloadingProfiles;

                var profiles = await _updater.Download(_cts.Token);
                if (profiles.Count > 0)
                {
                    Installer.DeployProfiles(profiles);
                    DownloadedProfilesCount = profiles.Count;
                }
            }

            if (Installer.RequiresUninstallYC)
            {
                Step = InstallerStep.NeedRemoveYourControls;
                return;
            }

            if (Installer.RequiresBridgeInstallation)
            {
                Step = InstallerStep.InstallingBridge;
                Installer.DeployBridge();
            }

            Step = InstallerStep.Completed;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Step = InstallerStep.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveYourControlsAndContinueAsync()
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            Installer.DeleteYC();

            Step = InstallerStep.InstallingBridge;
            Installer.DeployBridge();

            Step = InstallerStep.Completed;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Step = InstallerStep.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _d.Dispose();
    }
}