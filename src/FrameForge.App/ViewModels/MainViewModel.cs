using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IDashboardService _dashboardService;
    private readonly IOptimizationCatalog _catalog;
    private readonly IOptimizationPipeline _pipeline;
    private readonly IProfileService _profileService;
    private readonly IBackupService _backupService;
    private readonly IAppSettingsService _settingsService;
    private readonly ICs2DetectionService _cs2Detection;
    private readonly ICs2ConfigService _configService;
    private readonly IAppLog _log;

    private string _currentPage = "Home";
    private string _statusMessage = "Ready";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _hasLoaded;
    private DashboardSnapshot? _dashboard;
    private AppSettings _settings = AppSettings.CreateDefault();

    public MainViewModel(
        IDashboardService dashboardService,
        IOptimizationCatalog catalog,
        IOptimizationPipeline pipeline,
        IProfileService profileService,
        IBackupService backupService,
        IAppSettingsService settingsService,
        ICs2DetectionService cs2Detection,
        ICs2ConfigService configService,
        IAppLog log)
    {
        _dashboardService = dashboardService;
        _catalog = catalog;
        _pipeline = pipeline;
        _profileService = profileService;
        _backupService = backupService;
        _settingsService = settingsService;
        _cs2Detection = cs2Detection;
        _configService = configService;
        _log = log;

        NavigateCommand = new RelayCommand(p =>
        {
            CurrentPage = p?.ToString() ?? "Home";
            ErrorMessage = null;
        });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ApplySelectedOptimizationsCommand = new AsyncRelayCommand(ApplySelectedAsync, () => !IsBusy && HasSelectedOptimizations);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedBackupAsync, () => !IsBusy && SelectedBackup is not null);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, () => !IsBusy);
        ActivateProfileCommand = new AsyncRelayCommand(ActivateSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        ClearErrorCommand = new RelayCommand(() => ErrorMessage = null);
    }

    public ObservableCollection<OptimizationListItem> Optimizations { get; } = new();
    public ObservableCollection<PerformanceProfile> Profiles { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<string> Cs2ConfigLines { get; } = new();
    public ObservableCollection<ScoreFactor> ScoreFactors { get; } = new();

    public bool HasSelectedOptimizations => Optimizations.Any(o => o.IsSelected);
    public bool HasOptimizations => Optimizations.Count > 0;
    public bool HasProfiles => Profiles.Count > 0;
    public bool HasBackups => Backups.Count > 0;
    public bool HasCs2ConfigLines => Cs2ConfigLines.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowEmptyOptimizations => _hasLoaded && !IsBusy && !HasOptimizations;
    public bool ShowEmptyBackups => _hasLoaded && !IsBusy && !HasBackups;
    public bool ShowEmptyProfiles => _hasLoaded && !IsBusy && !HasProfiles;

    public PerformanceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                (ActivateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private PerformanceProfile? _selectedProfile;

    public BackupEntry? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private BackupEntry? _selectedBackup;

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                RaisePropertyChanged(nameof(IsHomePage));
                RaisePropertyChanged(nameof(IsOptimizePage));
                RaisePropertyChanged(nameof(IsCs2Page));
                RaisePropertyChanged(nameof(IsProfilesPage));
                RaisePropertyChanged(nameof(IsBackupsPage));
                RaisePropertyChanged(nameof(IsSettingsPage));
            }
        }
    }

    public bool IsHomePage => CurrentPage.Equals("Home", StringComparison.OrdinalIgnoreCase);
    public bool IsOptimizePage => CurrentPage.Equals("Optimize", StringComparison.OrdinalIgnoreCase);
    public bool IsCs2Page => CurrentPage.Equals("Cs2", StringComparison.OrdinalIgnoreCase);
    public bool IsProfilesPage => CurrentPage.Equals("Profiles", StringComparison.OrdinalIgnoreCase);
    public bool IsBackupsPage => CurrentPage.Equals("Backups", StringComparison.OrdinalIgnoreCase);
    public bool IsSettingsPage => CurrentPage.Equals("Settings", StringComparison.OrdinalIgnoreCase);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(ShowEmptyOptimizations));
                RaisePropertyChanged(nameof(ShowEmptyBackups));
                RaisePropertyChanged(nameof(ShowEmptyProfiles));
                (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ApplySelectedOptimizationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (SaveSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ResetSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ActivateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public DashboardSnapshot? Dashboard
    {
        get => _dashboard;
        set => SetProperty(ref _dashboard, value);
    }

    public AppSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    public string ScoreDisplay =>
        Dashboard?.Score is null
            ? "—"
            : $"{Dashboard.Score.Score}/{Dashboard.Score.MaxScore}";

    public string ScoreSummary => Dashboard?.Score?.Summary ?? "Score not calculated yet.";

    public IReadOnlyList<LogLevelSetting> LoggingLevels { get; private set; } = Enum.GetValues<LogLevelSetting>();
    public IReadOnlyList<ThemeSetting> Themes { get; private set; } = Enum.GetValues<ThemeSetting>();

    public void SetEnumCollections(IReadOnlyList<LogLevelSetting> loggingLevels, IReadOnlyList<ThemeSetting> themes)
    {
        LoggingLevels = loggingLevels;
        Themes = themes;
        RaisePropertyChanged(nameof(LoggingLevels));
        RaisePropertyChanged(nameof(Themes));
    }

    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ApplySelectedOptimizationsCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand ActivateProfileCommand { get; }
    public ICommand ClearErrorCommand { get; }

    public async Task InitializeAsync()
    {
        _log.LogInformation("Application UI initializing.");
        await RefreshAsync().ConfigureAwait(true);
    }

    public void NotifyOptimizationSelectionChanged()
    {
        RaisePropertyChanged(nameof(HasSelectedOptimizations));
        (ApplySelectedOptimizationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = "Refreshing…";

            Settings = await _settingsService.LoadAsync().ConfigureAwait(true);
            Dashboard = await _dashboardService.GetSnapshotAsync().ConfigureAwait(true);

            Optimizations.Clear();
            foreach (var opt in _catalog.GetAll())
            {
                var preview = await opt.ExplainAsync().ConfigureAwait(true);
                var item = new OptimizationListItem(opt, preview, this);
                Optimizations.Add(item);
            }

            Profiles.Clear();
            foreach (var profile in await _profileService.GetProfilesAsync().ConfigureAwait(true))
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault(p =>
                p.Id.Equals(Settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ?? Profiles.FirstOrDefault();

            Backups.Clear();
            foreach (var backup in await _backupService.ListBackupsAsync().ConfigureAwait(true))
            {
                Backups.Add(backup);
            }

            ScoreFactors.Clear();
            if (Dashboard?.Score is not null)
            {
                foreach (var factor in Dashboard.Score.Factors)
                {
                    ScoreFactors.Add(factor);
                }
            }

            await LoadCs2ConfigPreviewAsync().ConfigureAwait(true);

            _hasLoaded = true;
            RaisePropertyChanged(nameof(HasOptimizations));
            RaisePropertyChanged(nameof(HasProfiles));
            RaisePropertyChanged(nameof(HasBackups));
            RaisePropertyChanged(nameof(HasCs2ConfigLines));
            RaisePropertyChanged(nameof(ShowEmptyOptimizations));
            RaisePropertyChanged(nameof(ShowEmptyBackups));
            RaisePropertyChanged(nameof(ShowEmptyProfiles));
            RaisePropertyChanged(nameof(ScoreDisplay));
            RaisePropertyChanged(nameof(ScoreSummary));
            RaisePropertyChanged(nameof(HasSelectedOptimizations));

            StatusMessage = $"Updated {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            _log.LogError("Refresh failed.", ex);
            ErrorMessage = ex.Message;
            StatusMessage = "Refresh failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCs2ConfigPreviewAsync()
    {
        Cs2ConfigLines.Clear();
        var install = await _cs2Detection.DetectAsync().ConfigureAwait(true);
        if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
        {
            Cs2ConfigLines.Add(install.DetectionMessage);
            Cs2ConfigLines.Add("Install CS2 via Steam or set a custom path in Settings.");
            if (install.SearchedLibraries.Count > 0)
            {
                Cs2ConfigLines.Add($"Libraries searched: {install.SearchedLibraries.Count}");
            }

            if (install.InaccessiblePaths.Count > 0)
            {
                Cs2ConfigLines.Add($"Inaccessible paths: {install.InaccessiblePaths.Count}");
            }

            return;
        }

        Cs2ConfigLines.Add($"Install: {install.InstallPath}");
        Cs2ConfigLines.Add($"CFG directory: {install.CfgDirectory}");
        if (!Directory.Exists(install.CfgDirectory))
        {
            Cs2ConfigLines.Add("Directory does not exist yet — it will be created when applying config optimizations.");
            return;
        }

        var any = false;
        foreach (var file in Directory.EnumerateFiles(install.CfgDirectory, "frameforge_*.cfg"))
        {
            any = true;
            Cs2ConfigLines.Add($"--- {Path.GetFileName(file)} ---");
            var doc = await _configService.ReadAsync(file).ConfigureAwait(true);
            foreach (var entry in doc.Entries.Where(e => !e.IsCommentOnly).Take(40))
            {
                Cs2ConfigLines.Add($"{entry.Key} {entry.Value}");
            }
        }

        if (!any)
        {
            Cs2ConfigLines.Add("No FrameForge cfg files present yet. Apply a config optimization from the Optimize page.");
        }
    }

    private async Task ApplySelectedAsync()
    {
        var ids = Optimizations.Where(o => o.IsSelected).Select(o => o.Id).ToList();
        if (ids.Count == 0)
        {
            StatusMessage = "Select one or more optimizations first.";
            return;
        }

        var names = string.Join(Environment.NewLine, Optimizations.Where(o => o.IsSelected).Select(o => "• " + o.Name));
        var confirm = MessageBox.Show(
            $"Apply {ids.Count} optimization(s)?{Environment.NewLine}{Environment.NewLine}{names}{Environment.NewLine}{Environment.NewLine}" +
            "A backup will be created first when automatic backup is enabled.",
            "Confirm apply",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Apply cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = "Running optimization pipeline…";
            var result = await _pipeline.ExecuteAsync(ids).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? $"Success. Backup: {result.BackupId ?? "none"}"
                    : result.ErrorMessage;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Pipeline failed.";
                StatusMessage = result.RolledBack
                    ? "Failed — changes were rolled back."
                    : "Failed — see error details.";
            }

            // Refresh without re-entering busy gate incorrectly
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError("Apply pipeline failed.", ex);
            ErrorMessage = ex.Message;
            StatusMessage = "Apply failed.";
            IsBusy = false;
        }
    }

    private async Task RestoreSelectedBackupAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Restore backup '{SelectedBackup.Id}'?{Environment.NewLine}{Environment.NewLine}" +
            $"{SelectedBackup.Description}{Environment.NewLine}" +
            $"Files: {SelectedBackup.AffectedFiles.Count}",
            "Confirm restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Restore cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = $"Restoring {SelectedBackup.Id}…";
            var result = await _backupService.RestoreAsync(SelectedBackup.Id).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = result.Message;
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = "Restore failed.";
            }

            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError("Restore failed.", ex);
            ErrorMessage = ex.Message;
            StatusMessage = "Restore failed.";
            IsBusy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await _settingsService.SaveAsync(Settings).ConfigureAwait(true);
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Save failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetSettingsAsync()
    {
        var confirm = MessageBox.Show(
            "Reset application settings to defaults? This does not delete backups.",
            "Confirm reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Reset cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            await _settingsService.ResetAsync().ConfigureAwait(true);
            Settings = await _settingsService.LoadAsync().ConfigureAwait(true);
            StatusMessage = "Settings reset.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Reset failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ActivateSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            Settings.ActiveProfileId = SelectedProfile.Id;
            await _settingsService.SaveAsync(Settings).ConfigureAwait(true);

            if (SelectedProfile.RecommendedOptimizationIds.Count > 0)
            {
                foreach (var item in Optimizations)
                {
                    item.IsSelected = SelectedProfile.RecommendedOptimizationIds
                        .Contains(item.Id, StringComparer.OrdinalIgnoreCase);
                }
            }

            StatusMessage = $"Active profile: {SelectedProfile.Name}. Review selections on Optimize, then apply.";
            RaisePropertyChanged(nameof(Settings));
            NotifyOptimizationSelectionChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Failed to activate profile.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class OptimizationListItem : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _isSelected;

    public OptimizationListItem(IOptimization optimization, OptimizationPreview preview, MainViewModel owner)
    {
        _owner = owner;
        Id = optimization.Id;
        Name = optimization.Name;
        Description = optimization.Description;
        Category = optimization.Category.ToString();
        Risk = optimization.RiskLevel.ToString();
        Impact = optimization.ExpectedImpact.ToString();
        CanApply = preview.CanApply;
        Summary = preview.Summary;
        BlockReason = preview.BlockReason;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public string Risk { get; }
    public string Impact { get; }
    public bool CanApply { get; }
    public string Summary { get; }
    public string? BlockReason { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _owner.NotifyOptimizationSelectionChanged();
            }
        }
    }

    public string StatusText => CanApply ? "Available" : (BlockReason ?? "Not applicable");
}
