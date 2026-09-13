using System.Collections.ObjectModel;
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
    private bool _isBusy;
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

        NavigateCommand = new RelayCommand(p => CurrentPage = p?.ToString() ?? "Home");
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApplySelectedOptimizationsCommand = new AsyncRelayCommand(ApplySelectedAsync, () => SelectedOptimizations.Count > 0);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedBackupAsync, () => SelectedBackup is not null);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
        ActivateProfileCommand = new AsyncRelayCommand(ActivateSelectedProfileAsync, () => SelectedProfile is not null);
    }

    public ObservableCollection<OptimizationListItem> Optimizations { get; } = new();
    public ObservableCollection<OptimizationListItem> SelectedOptimizations { get; } = new();
    public ObservableCollection<PerformanceProfile> Profiles { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<string> Cs2ConfigLines { get; } = new();

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
        set => SetProperty(ref _currentPage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
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

    public async Task InitializeAsync()
    {
        _log.LogInformation("Application UI initializing.");
        await RefreshAsync().ConfigureAwait(true);
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
            StatusMessage = "Refreshing…";

            Settings = await _settingsService.LoadAsync().ConfigureAwait(true);
            Dashboard = await _dashboardService.GetSnapshotAsync().ConfigureAwait(true);

            Optimizations.Clear();
            SelectedOptimizations.Clear();
            foreach (var opt in _catalog.GetAll())
            {
                var preview = await opt.ExplainAsync().ConfigureAwait(true);
                var item = new OptimizationListItem(opt, preview);
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

            await LoadCs2ConfigPreviewAsync().ConfigureAwait(true);

            StatusMessage = $"Updated {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            _log.LogError("Refresh failed.", ex);
            StatusMessage = $"Error: {ex.Message}";
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
            Cs2ConfigLines.Add("CS2 configuration folder not found.");
            Cs2ConfigLines.Add("Install CS2 via Steam or set a custom path in Settings.");
            return;
        }

        Cs2ConfigLines.Add($"CFG directory: {install.CfgDirectory}");
        if (!Directory.Exists(install.CfgDirectory))
        {
            Cs2ConfigLines.Add("Directory does not exist yet — it will be created when applying config optimizations.");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(install.CfgDirectory, "frameforge_*.cfg"))
        {
            Cs2ConfigLines.Add($"--- {Path.GetFileName(file)} ---");
            var doc = await _configService.ReadAsync(file).ConfigureAwait(true);
            foreach (var entry in doc.Entries.Where(e => !e.IsCommentOnly).Take(40))
            {
                Cs2ConfigLines.Add($"{entry.Key} {entry.Value}");
            }
        }

        if (Cs2ConfigLines.Count == 1)
        {
            Cs2ConfigLines.Add("No FrameForge cfg files present yet.");
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

        try
        {
            IsBusy = true;
            StatusMessage = "Running optimization pipeline…";
            var result = await _pipeline.ExecuteAsync(ids).ConfigureAwait(true);
            StatusMessage = result.Success
                ? $"Pipeline succeeded. Backup: {result.BackupId ?? "none"}"
                : $"Pipeline finished with issues: {result.ErrorMessage}";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError("Apply pipeline failed.", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreSelectedBackupAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Restoring {SelectedBackup.Id}…";
            await _backupService.RestoreAsync(SelectedBackup.Id).ConfigureAwait(true);
            StatusMessage = "Backup restored.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError("Restore failed.", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsService.SaveAsync(Settings).ConfigureAwait(true);
        StatusMessage = "Settings saved.";
    }

    private async Task ResetSettingsAsync()
    {
        await _settingsService.ResetAsync().ConfigureAwait(true);
        Settings = await _settingsService.LoadAsync().ConfigureAwait(true);
        StatusMessage = "Settings reset.";
    }

    private async Task ActivateSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

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

        StatusMessage = $"Active profile: {SelectedProfile.Name}";
        RaisePropertyChanged(nameof(Settings));
    }
}

public sealed class OptimizationListItem : ViewModelBase
{
    private bool _isSelected;

    public OptimizationListItem(IOptimization optimization, OptimizationPreview preview)
    {
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
        set => SetProperty(ref _isSelected, value);
    }

    public string StatusText => CanApply ? "Available" : (BlockReason ?? "Not applicable");
}
