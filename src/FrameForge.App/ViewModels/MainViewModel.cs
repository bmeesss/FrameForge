using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using Microsoft.Win32;

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
    private readonly ICs2SettingsService _cs2SettingsService;
    private readonly ICs2SettingCatalog _settingCatalog;
    private readonly IAppLog _log;

    private string _currentPage = "Home";
    private string _statusMessage = "Ready";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _hasLoaded;
    private DashboardSnapshot? _dashboard;
    private AppSettings _settings = AppSettings.CreateDefault();
    private Cs2SettingsSnapshot? _cs2Snapshot;
    private Cs2SettingCategory? _selectedCategory;
    private SettingRowViewModel? _selectedSetting;
    private SettingsDiff? _pendingDiff;
    private string _pendingDiffSummary = string.Empty;

    public MainViewModel(
        IDashboardService dashboardService,
        IOptimizationCatalog catalog,
        IOptimizationPipeline pipeline,
        IProfileService profileService,
        IBackupService backupService,
        IAppSettingsService settingsService,
        ICs2DetectionService cs2Detection,
        ICs2ConfigService configService,
        ICs2SettingsService cs2SettingsService,
        ICs2SettingCatalog settingCatalog,
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
        _cs2SettingsService = cs2SettingsService;
        _settingCatalog = settingCatalog;
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

        SelectCategoryCommand = new RelayCommand(p =>
        {
            if (p is Cs2SettingCategory cat)
            {
                SelectedCategory = cat;
            }
            else if (p is string s && Enum.TryParse<Cs2SettingCategory>(s, true, out var parsed))
            {
                SelectedCategory = parsed;
            }
        });

        PreviewSettingsApplyCommand = new AsyncRelayCommand(PreviewPendingSettingsAsync, () => !IsBusy && HasPendingSettingEdits);
        ApplySettingsDiffCommand = new AsyncRelayCommand(ApplyPendingDiffAsync, () => !IsBusy && PendingDiff is { HasChanges: true });
        ClearSettingsDiffCommand = new RelayCommand(() =>
        {
            PendingDiff = null;
            PendingDiffSummary = string.Empty;
        });
        ResetFrameForgeSettingsCommand = new AsyncRelayCommand(ResetFrameForgeSettingsAsync, () => !IsBusy);
        SaveAsProfileCommand = new AsyncRelayCommand(SaveCurrentAsProfileAsync, () => !IsBusy && _cs2Snapshot is { Cs2Available: true });
        CompareWithProfileCommand = new AsyncRelayCommand(CompareWithSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);

        PreviewProfileCommand = new AsyncRelayCommand(PreviewSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        ApplyProfileCommand = new AsyncRelayCommand(ApplySelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        DuplicateProfileCommand = new AsyncRelayCommand(DuplicateSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        ExportProfileCommand = new AsyncRelayCommand(ExportSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        ImportProfileCommand = new AsyncRelayCommand(ImportProfileAsync, () => !IsBusy);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteSelectedProfileAsync, () => !IsBusy && SelectedProfile is { IsBuiltIn: false });
        CreateProfileCommand = new AsyncRelayCommand(CreateEmptyProfileAsync, () => !IsBusy);
        RenameProfileCommand = new AsyncRelayCommand(RenameSelectedProfileAsync, () => !IsBusy && SelectedProfile is { IsBuiltIn: false });
    }

    public ObservableCollection<OptimizationListItem> Optimizations { get; } = new();
    public ObservableCollection<PerformanceProfile> Profiles { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<string> Cs2ConfigLines { get; } = new();
    public ObservableCollection<ScoreFactor> ScoreFactors { get; } = new();
    public ObservableCollection<Cs2SettingCategory> SettingCategories { get; } = new();
    public ObservableCollection<SettingRowViewModel> SettingRows { get; } = new();
    public ObservableCollection<SettingsDiffEntry> DiffEntries { get; } = new();

    public bool HasSelectedOptimizations => Optimizations.Any(o => o.IsSelected);
    public bool HasOptimizations => Optimizations.Count > 0;
    public bool HasProfiles => Profiles.Count > 0;
    public bool HasBackups => Backups.Count > 0;
    public bool HasCs2ConfigLines => Cs2ConfigLines.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowEmptyOptimizations => _hasLoaded && !IsBusy && !HasOptimizations;
    public bool ShowEmptyBackups => _hasLoaded && !IsBusy && !HasBackups;
    public bool ShowEmptyProfiles => _hasLoaded && !IsBusy && !HasProfiles;
    public bool HasPendingSettingEdits => SettingRows.Any(r => r.IsModified);
    public bool HasPendingDiff => PendingDiff is { HasChanges: true };
    public bool HasSettingRows => SettingRows.Count > 0;
    public bool ShowEmptySettings => _hasLoaded && !IsBusy && !HasSettingRows;

    public PerformanceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RaisePropertyChanged(nameof(SelectedProfileIsBuiltIn));
                RaisePropertyChanged(nameof(SelectedProfileSettingCount));
                (ActivateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (PreviewProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DuplicateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ExportProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeleteProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RenameProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CompareWithProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private PerformanceProfile? _selectedProfile;

    public bool SelectedProfileIsBuiltIn => SelectedProfile?.IsBuiltIn == true;
    public int SelectedProfileSettingCount => SelectedProfile?.SettingCount ?? 0;

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

    public Cs2SettingCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RebuildSettingRows();
            }
        }
    }

    public SettingRowViewModel? SelectedSetting
    {
        get => _selectedSetting;
        set => SetProperty(ref _selectedSetting, value);
    }

    public SettingsDiff? PendingDiff
    {
        get => _pendingDiff;
        set
        {
            if (SetProperty(ref _pendingDiff, value))
            {
                DiffEntries.Clear();
                if (value is not null)
                {
                    foreach (var e in value.Entries.Where(x => x.IsChange))
                    {
                        DiffEntries.Add(e);
                    }
                }

                RaisePropertyChanged(nameof(HasPendingDiff));
                (ApplySettingsDiffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PendingDiffSummary
    {
        get => _pendingDiffSummary;
        set => SetProperty(ref _pendingDiffSummary, value);
    }

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
                RaisePropertyChanged(nameof(ShowEmptySettings));
                RaiseCanExecutes();
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

    public string Cs2SettingsStatus =>
        _cs2Snapshot is null
            ? "Not loaded"
            : _cs2Snapshot.Message;

    public string? Cs2ExecutionRecommendation => _cs2Snapshot?.ExecutionRecommendation;

    public bool HasCs2ExecutionWarning =>
        !string.IsNullOrWhiteSpace(_cs2Snapshot?.ExecutionRecommendation);

    public string Cs2AffectedFilesHint =>
        _cs2Snapshot?.AffectedFilesOnApply is { Count: > 0 } files
            ? "Files FrameForge may modify: " + string.Join(", ", files.Select(Path.GetFileName))
            : "No CS2 cfg directory detected.";

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
    public ICommand SelectCategoryCommand { get; }
    public ICommand PreviewSettingsApplyCommand { get; }
    public ICommand ApplySettingsDiffCommand { get; }
    public ICommand ClearSettingsDiffCommand { get; }
    public ICommand ResetFrameForgeSettingsCommand { get; }
    public ICommand SaveAsProfileCommand { get; }
    public ICommand CompareWithProfileCommand { get; }
    public ICommand PreviewProfileCommand { get; }
    public ICommand ApplyProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand ExportProfileCommand { get; }
    public ICommand ImportProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand CreateProfileCommand { get; }
    public ICommand RenameProfileCommand { get; }

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

    public void NotifySettingEdited()
    {
        RaisePropertyChanged(nameof(HasPendingSettingEdits));
        (PreviewSettingsApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseCanExecutes()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplySelectedOptimizationsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResetSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ActivateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreviewSettingsApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplySettingsDiffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResetFrameForgeSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveAsProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CompareWithProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreviewProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExportProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ImportProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RenameProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                Optimizations.Add(new OptimizationListItem(opt, preview, this));
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
            await LoadCs2SettingsAsync().ConfigureAwait(true);

            _hasLoaded = true;
            RaisePropertyChanged(nameof(HasOptimizations));
            RaisePropertyChanged(nameof(HasProfiles));
            RaisePropertyChanged(nameof(HasBackups));
            RaisePropertyChanged(nameof(HasCs2ConfigLines));
            RaisePropertyChanged(nameof(ShowEmptyOptimizations));
            RaisePropertyChanged(nameof(ShowEmptyBackups));
            RaisePropertyChanged(nameof(ShowEmptyProfiles));
            RaisePropertyChanged(nameof(ShowEmptySettings));
            RaisePropertyChanged(nameof(ScoreDisplay));
            RaisePropertyChanged(nameof(ScoreSummary));
            RaisePropertyChanged(nameof(HasSelectedOptimizations));
            RaisePropertyChanged(nameof(Cs2SettingsStatus));

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

    private async Task LoadCs2SettingsAsync()
    {
        _cs2Snapshot = await _cs2SettingsService.ReadSettingsAsync().ConfigureAwait(true);

        SettingCategories.Clear();
        foreach (var cat in Enum.GetValues<Cs2SettingCategory>())
        {
            if (_settingCatalog.GetByCategory(cat).Count > 0)
            {
                SettingCategories.Add(cat);
            }
        }

        SelectedCategory ??= SettingCategories.FirstOrDefault();
        RebuildSettingRows();
        RaisePropertyChanged(nameof(Cs2SettingsStatus));
        RaisePropertyChanged(nameof(Cs2ExecutionRecommendation));
        RaisePropertyChanged(nameof(HasCs2ExecutionWarning));
        RaisePropertyChanged(nameof(Cs2AffectedFilesHint));
        RaisePropertyChanged(nameof(HasSettingRows));
        RaisePropertyChanged(nameof(ShowEmptySettings));
    }

    private void RebuildSettingRows()
    {
        var previousEdits = SettingRows
            .Where(r => r.IsModified)
            .ToDictionary(r => r.ConfigKey, r => r.EditValue, StringComparer.OrdinalIgnoreCase);

        SettingRows.Clear();
        if (_cs2Snapshot is null)
        {
            return;
        }

        IEnumerable<Cs2SettingValue> source = _cs2Snapshot.Settings;
        if (SelectedCategory is not null)
        {
            source = source.Where(s => s.Category == SelectedCategory);
        }

        foreach (var setting in source.OrderBy(s => s.DisplayName))
        {
            var row = new SettingRowViewModel(setting, this);
            if (previousEdits.TryGetValue(row.ConfigKey, out var edit))
            {
                row.EditValue = edit;
            }

            SettingRows.Add(row);
        }

        RaisePropertyChanged(nameof(HasSettingRows));
        RaisePropertyChanged(nameof(HasPendingSettingEdits));
        NotifySettingEdited();
    }

    private async Task LoadCs2ConfigPreviewAsync()
    {
        Cs2ConfigLines.Clear();
        var install = await _cs2Detection.DetectAsync().ConfigureAwait(true);
        if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
        {
            Cs2ConfigLines.Add(install.DetectionMessage);
            Cs2ConfigLines.Add("Install CS2 via Steam or set a custom path in Settings.");
            return;
        }

        Cs2ConfigLines.Add($"Install: {install.InstallPath}");
        Cs2ConfigLines.Add($"CFG directory: {install.CfgDirectory}");
        var managed = Path.Combine(install.CfgDirectory, _cs2SettingsService.ManagedConfigFileName);
        if (File.Exists(managed))
        {
            Cs2ConfigLines.Add($"--- {_cs2SettingsService.ManagedConfigFileName} ---");
            var doc = await _configService.ReadAsync(managed).ConfigureAwait(true);
            foreach (var entry in doc.Entries.Where(e => !e.IsCommentOnly).Take(60))
            {
                Cs2ConfigLines.Add($"{entry.Key} {entry.Value}");
            }
        }
        else
        {
            Cs2ConfigLines.Add("No FrameForge managed settings file yet.");
        }
    }

    private Dictionary<string, string> CollectEditedSettings()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in SettingRows.Where(r => r.IsModified && !string.IsNullOrWhiteSpace(r.EditValue)))
        {
            map[row.ConfigKey] = row.EditValue!.Trim();
        }

        return map;
    }

    private async Task PreviewPendingSettingsAsync()
    {
        if (_cs2Snapshot is null)
        {
            StatusMessage = "Settings not loaded.";
            return;
        }

        var desired = CollectEditedSettings();
        if (desired.Count == 0)
        {
            StatusMessage = "No edited settings to preview.";
            return;
        }

        var validation = _cs2SettingsService.ValidateSettings(desired);
        if (!validation.IsValid)
        {
            ErrorMessage = string.Join(Environment.NewLine, validation.Issues.Select(i => i.Message));
            StatusMessage = "Validation failed.";
            return;
        }

        PendingDiff = _cs2SettingsService.CreateDiff(_cs2Snapshot, desired, "Manual CS2 Settings edit");
        PendingDiffSummary = PendingDiff.HasChanges
            ? $"{PendingDiff.ChangeCount} change(s) ready. Review the diff, then press Apply."
            : "No effective changes.";
        StatusMessage = PendingDiffSummary;
    }

    private async Task ApplyPendingDiffAsync()
    {
        if (PendingDiff is null || !PendingDiff.HasChanges)
        {
            StatusMessage = "Nothing to apply.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Apply {PendingDiff.ChangeCount} change(s) to real CS2 config files?");
        sb.AppendLine();
        if (PendingDiff.AffectedFiles.Count > 0)
        {
            sb.AppendLine("Files that will be modified:");
            foreach (var f in PendingDiff.AffectedFiles)
            {
                sb.AppendLine($"  • {f}");
            }

            sb.AppendLine();
        }
        else if (_cs2Snapshot?.AffectedFilesOnApply is { Count: > 0 } paths)
        {
            sb.AppendLine("Files that will be modified:");
            foreach (var f in paths)
            {
                sb.AppendLine($"  • {f}");
            }

            sb.AppendLine();
        }

        foreach (var e in PendingDiff.Entries.Where(x => x.IsChange).Take(12))
        {
            sb.AppendLine($"• {e.DisplayName}: {e.CurrentValue ?? "(default)"} → {e.NewValue}");
        }

        if (PendingDiff.ChangeCount > 12)
        {
            sb.AppendLine($"… and {PendingDiff.ChangeCount - 12} more");
        }

        sb.AppendLine();
        sb.AppendLine("autoexec.cfg only receives a marked // FRAMEFORGE BEGIN…END section (user lines outside markers stay).");
        sb.Append("A backup will be created when automatic backup is enabled.");

        var confirm = MessageBox.Show(
            sb.ToString(),
            "Confirm settings apply",
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
            StatusMessage = "Applying settings…";
            var result = await _cs2SettingsService.ApplyDiffAsync(PendingDiff).ConfigureAwait(true);
            if (result.Success && result.WrittenFiles.Count > 0)
            {
                StatusMessage = result.Message + (result.BackupId is null ? "" : $" Backup: {result.BackupId}");
                PendingDiff = null;
                PendingDiffSummary = string.Empty;
            }
            else if (result.Success)
            {
                StatusMessage = result.Message;
                PendingDiff = null;
                PendingDiffSummary = string.Empty;
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = result.RolledBack ? "Failed — rolled back." : "Apply failed.";
            }

            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Apply failed.";
            IsBusy = false;
        }
    }

    private async Task ResetFrameForgeSettingsAsync()
    {
        var confirm = MessageBox.Show(
            "Restore the last FrameForge-managed settings backup? This only reverts FrameForge-written cfg changes, not unrelated user files.",
            "Reset FrameForge changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Reset cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cs2SettingsService.RestoreLastFrameForgeChangesAsync().ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = result.Message;
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = "Reset failed.";
            }

            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Reset failed.";
            IsBusy = false;
        }
    }

    private async Task SaveCurrentAsProfileAsync()
    {
        if (_cs2Snapshot is null)
        {
            return;
        }

        var name = Prompt("New custom profile name:", "Save as Profile", "My Profile");
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Save as profile cancelled.";
            return;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in SettingRows)
        {
            var value = row.IsModified ? row.EditValue : row.CurrentValue;
            if (!string.IsNullOrWhiteSpace(value))
            {
                settings[row.ConfigKey] = value.Trim();
            }
        }

        // If no category filter edits, include full snapshot current values
        if (settings.Count == 0)
        {
            foreach (var s in _cs2Snapshot.Settings)
            {
                if (!string.IsNullOrWhiteSpace(s.CurrentValue))
                {
                    settings[s.Definition.ConfigKey] = s.CurrentValue!;
                }
            }
        }

        var profile = new PerformanceProfile
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            Description = "Custom profile created from current CS2 settings.",
            IsBuiltIn = false,
            IsCustom = true,
            SchemaVersion = ProfileSchema.CurrentVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Settings = settings
        };

        try
        {
            IsBusy = true;
            await _profileService.SaveCustomProfileAsync(profile).ConfigureAwait(true);
            StatusMessage = $"Saved profile '{profile.Name}'.";
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Save profile failed.";
            IsBusy = false;
        }
    }

    private async Task CompareWithSelectedProfileAsync()
    {
        if (SelectedProfile is null || _cs2Snapshot is null)
        {
            return;
        }

        PendingDiff = _cs2SettingsService.CreateDiff(
            _cs2Snapshot,
            SelectedProfile.Settings,
            $"{SelectedProfile.Name} profile recommendation",
            SelectedProfile.Id,
            SelectedProfile.Name);
        PendingDiffSummary = PendingDiff.HasChanges
            ? $"Compare with {SelectedProfile.Name}: {PendingDiff.ChangeCount} difference(s)."
            : $"Already matches {SelectedProfile.Name}.";
        StatusMessage = PendingDiffSummary;
        CurrentPage = "Cs2";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task PreviewSelectedProfileAsync()
    {
        await CompareWithSelectedProfileAsync().ConfigureAwait(true);
    }

    private async Task ApplySelectedProfileAsync()
    {
        if (SelectedProfile is null || _cs2Snapshot is null)
        {
            return;
        }

        var diff = _cs2SettingsService.CreateDiff(
            _cs2Snapshot,
            SelectedProfile.Settings,
            $"{SelectedProfile.Name} profile recommendation",
            SelectedProfile.Id,
            SelectedProfile.Name);

        if (!diff.HasChanges)
        {
            StatusMessage = "Profile already matches current settings.";
            return;
        }

        PendingDiff = diff;
        PendingDiffSummary = $"{diff.ChangeCount} change(s) from profile '{SelectedProfile.Name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Apply profile '{SelectedProfile.Name}' ({diff.ChangeCount} change(s)) to real CS2 config?");
        sb.AppendLine();
        var files = diff.AffectedFiles.Count > 0
            ? diff.AffectedFiles
            : _cs2Snapshot?.AffectedFilesOnApply ?? Array.Empty<string>();
        if (files.Count > 0)
        {
            sb.AppendLine("Files:");
            foreach (var f in files)
            {
                sb.AppendLine($"  • {f}");
            }

            sb.AppendLine();
        }

        foreach (var e in diff.Entries.Where(x => x.IsChange).Take(15))
        {
            sb.AppendLine($"• {e.DisplayName}: {e.CurrentValue ?? "(default)"} → {e.NewValue}");
            sb.AppendLine($"  Reason: {e.Reason}  Risk: {e.Risk}");
        }

        var confirm = MessageBox.Show(
            sb.ToString(),
            "Confirm profile apply",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Profile apply cancelled. Diff kept for review.";
            CurrentPage = "Cs2";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _cs2SettingsService.ApplyDiffAsync(diff).ConfigureAwait(true);
            if (result.Success && (result.WrittenFiles.Count > 0 || !diff.HasChanges))
            {
                Settings.ActiveProfileId = SelectedProfile.Id;
                await _settingsService.SaveAsync(Settings).ConfigureAwait(true);
                StatusMessage = result.Message;
                PendingDiff = null;
            }
            else if (result.Success)
            {
                // Success with no written files and prior changes expected — treat as incomplete
                ErrorMessage = "Apply reported success but no CS2 files were written.";
                StatusMessage = "Profile apply incomplete.";
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = "Profile apply failed.";
            }

            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Profile apply failed.";
            IsBusy = false;
        }
    }

    private async Task DuplicateSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var copy = await _profileService.DuplicateProfileAsync(SelectedProfile.Id).ConfigureAwait(true);
            StatusMessage = $"Duplicated as '{copy.Name}'.";
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == copy.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Duplicate failed.";
            IsBusy = false;
        }
    }

    private async Task ExportSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "FrameForge profile (*.frameforge-profile.json)|*.frameforge-profile.json|JSON (*.json)|*.json",
            FileName = SelectedProfile.Id + ProfileSchema.FileExtension,
            AddExtension = true,
            DefaultExt = "frameforge-profile.json"
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            await _profileService.ExportProfileAsync(SelectedProfile.Id, dialog.FileName).ConfigureAwait(true);
            StatusMessage = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Export failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "FrameForge profile (*.frameforge-profile.json;*.json)|*.frameforge-profile.json;*.json|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Import cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _profileService.ImportProfileAsync(dialog.FileName).ConfigureAwait(true);
            if (result.Success && result.Profile is not null)
            {
                StatusMessage = result.Message;
                IsBusy = false;
                await RefreshAsync().ConfigureAwait(true);
                SelectedProfile = Profiles.FirstOrDefault(p => p.Id == result.Profile.Id);
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = "Import rejected.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Import failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile is null || SelectedProfile.IsBuiltIn)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete custom profile '{SelectedProfile.Name}'?",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Delete cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            await _profileService.DeleteCustomProfileAsync(SelectedProfile.Id).ConfigureAwait(true);
            StatusMessage = "Profile deleted.";
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Delete failed.";
            IsBusy = false;
        }
    }

    private async Task CreateEmptyProfileAsync()
    {
        var name = Prompt("Name for the new custom profile:", "Create Profile", "Custom Profile");
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Create cancelled.";
            return;
        }

        var profile = new PerformanceProfile
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            Description = "Custom profile.",
            IsBuiltIn = false,
            IsCustom = true,
            SchemaVersion = ProfileSchema.CurrentVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        try
        {
            IsBusy = true;
            await _profileService.SaveCustomProfileAsync(profile).ConfigureAwait(true);
            StatusMessage = $"Created '{profile.Name}'.";
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Create failed.";
            IsBusy = false;
        }
    }

    private async Task RenameSelectedProfileAsync()
    {
        if (SelectedProfile is null || SelectedProfile.IsBuiltIn)
        {
            return;
        }

        var name = Prompt("New name:", "Rename Profile", SelectedProfile.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Rename cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            await _profileService.RenameCustomProfileAsync(SelectedProfile.Id, name.Trim()).ConfigureAwait(true);
            StatusMessage = "Profile renamed.";
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Rename failed.";
            IsBusy = false;
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
            "Reset application settings to defaults? This does not delete backups or CS2 cfg files.",
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

            StatusMessage = $"Active profile: {SelectedProfile.Name}. Use Preview/Apply on Profiles to change CS2 settings.";
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

    private static string? Prompt(string message, string title, string defaultValue)
    {
        // Simple prompt via WPF input dialog substitute
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var box = new System.Windows.Controls.TextBox { Text = defaultValue, Padding = new Thickness(6) };
        panel.Children.Add(box);
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; window.DialogResult = true; };
        cancel.Click += (_, _) => { window.DialogResult = false; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        box.Focus();
        box.SelectAll();
        return window.ShowDialog() == true ? result : null;
    }
}

public sealed class SettingRowViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private string? _editValue;

    public SettingRowViewModel(Cs2SettingValue setting, MainViewModel owner)
    {
        _owner = owner;
        Setting = setting;
        _editValue = setting.CurrentValue;
    }

    public Cs2SettingValue Setting { get; }
    public string Id => Setting.Id;
    public string ConfigKey => Setting.Definition.ConfigKey;
    public string DisplayName => Setting.DisplayName;
    public string Description => Setting.Description;
    public string Category => Setting.Category.ToString();
    public string? CurrentValue => Setting.CurrentValue;
    public string? RecommendedValue => Setting.RecommendedValue;
    public string Source => Setting.Source.ToString();
    public string AllowedHint =>
        Setting.Definition.AllowedValues is { Count: > 0 }
            ? string.Join(" | ", Setting.Definition.AllowedValues)
            : Setting.Definition.MinValue is not null || Setting.Definition.MaxValue is not null
                ? $"{Setting.Definition.MinValue} … {Setting.Definition.MaxValue}"
                : Setting.Definition.ValueKind.ToString();

    public string? EditValue
    {
        get => _editValue;
        set
        {
            if (SetProperty(ref _editValue, value))
            {
                RaisePropertyChanged(nameof(IsModified));
                _owner.NotifySettingEdited();
            }
        }
    }

    public bool IsModified =>
        !string.Equals(
            (CurrentValue ?? string.Empty).Trim(),
            (EditValue ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
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
