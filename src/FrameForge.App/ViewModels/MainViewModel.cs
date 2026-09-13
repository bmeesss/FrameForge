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
    private readonly IBenchmarkEngine _benchmarkEngine;
    private readonly IBenchmarkStore _benchmarkStore;
    private readonly IGuidedOptimizationService _guided;
    private readonly IGuidedOptimizationStore _guidedStore;
    private readonly IIndividualOptimizationCatalog _individualCatalog;
    private readonly ICustomOptimizationSetStore _customSetStore;
    private readonly IPerformanceIntelligenceService _perfIntel;
    private readonly ITargetedRestoreEvaluator _targetedRestore;
    private readonly ITargetedRestoreService _targetedRestoreService;
    private readonly IIntelligenceExportService _intelExport;
    private readonly IManagedConfigWatcher _configWatcher;
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
        IBenchmarkEngine benchmarkEngine,
        IBenchmarkStore benchmarkStore,
        IGuidedOptimizationService guided,
        IGuidedOptimizationStore guidedStore,
        IIndividualOptimizationCatalog individualCatalog,
        ICustomOptimizationSetStore customSetStore,
        IPerformanceIntelligenceService perfIntel,
        ITargetedRestoreEvaluator targetedRestore,
        ITargetedRestoreService targetedRestoreService,
        IIntelligenceExportService intelExport,
        IManagedConfigWatcher configWatcher,
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
        _benchmarkEngine = benchmarkEngine;
        _benchmarkStore = benchmarkStore;
        _guided = guided;
        _guidedStore = guidedStore;
        _individualCatalog = individualCatalog;
        _customSetStore = customSetStore;
        _perfIntel = perfIntel;
        _targetedRestore = targetedRestore;
        _targetedRestoreService = targetedRestoreService;
        _intelExport = intelExport;
        _configWatcher = configWatcher;
        _log = log;
        _guided.StatusChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(GuidedStatusText));
            RaisePropertyChanged(nameof(IsGuidedRunning));
            RaisePropertyChanged(nameof(IsGuidedAwaitingConfirmation));
            RaisePropertyChanged(nameof(IsGuidedAwaitingDecision));
            RaiseCanExecutes();
        };
        _guided.ProgressChanged += (_, p) =>
        {
            GuidedProgressMessage = p.Message;
            if (p.ProgressPercent is not null)
            {
                GuidedProgressPercent = p.ProgressPercent.Value;
            }
        };
        _benchmarkEngine.StatusChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(BenchmarkStatusText));
            RaisePropertyChanged(nameof(IsBenchmarkRunning));
            RaiseCanExecutes();
        };
        _benchmarkEngine.SampleCaptured += (_, sample) =>
        {
            if (sample.IsWarmup)
            {
                return;
            }

            // Keep chart bounded
            while (BenchmarkChartPoints.Count > 300)
            {
                BenchmarkChartPoints.RemoveAt(0);
            }

            var y = sample.SystemCpuPercent ?? sample.ProcessCpuPercent ?? 0;
            BenchmarkChartPoints.Add(new BenchmarkChartPoint
            {
                ElapsedMs = sample.ElapsedMs,
                Value = y,
                Label = sample.SystemCpuPercent is not null ? "CPU%" : (sample.ProcessCpuPercent is not null ? "ProcCPU%" : "n/a")
            });
            RaisePropertyChanged(nameof(HasBenchmarkChart));
        };
        _benchmarkEngine.ProgressChanged += (_, progress) =>
        {
            ApplyBenchmarkProgress(progress);
        };
        _configWatcher.ExternalChangeDetected += (_, e) =>
        {
            if (e.UserSectionOnly)
            {
                return;
            }

            StatusMessage = e.Message;
            // Invalidate restore UI assessment
            _lastTargetedAssessment = null;
            TargetedRestoreAssessmentText = "Re-assess required — managed cfg may have changed externally.";
            RaisePropertyChanged(nameof(CanTargetedRestore));
            RaisePropertyChanged(nameof(TargetedRestoreHint));
            RaiseCanExecutes();
        };

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

        StartBenchmarkCommand = new AsyncRelayCommand(StartBenchmarkAsync, () => !IsBenchmarkRunning);
        StopBenchmarkCommand = new RelayCommand(() => _benchmarkEngine.RequestStop(), () => IsBenchmarkRunning);
        CancelBenchmarkCommand = new RelayCommand(() =>
        {
            BenchmarkLiveStatus = "Stopping benchmark…";
            BenchmarkPhaseText = "Stopping";
            StatusMessage = "Stopping benchmark…";
            _benchmarkEngine.RequestCancel();
        }, () => IsBenchmarkRunning);
        RefreshBenchmarkHistoryCommand = new AsyncRelayCommand(LoadBenchmarkHistoryAsync, () => !IsBusy);
        CompareBenchmarksCommand = new RelayCommand(CompareSelectedBenchmarks, () => SelectedBenchmarkA is not null && SelectedBenchmarkB is not null);
        ExportBenchmarkJsonCommand = new AsyncRelayCommand(ExportBenchmarkJsonAsync, () => SelectedBenchmarkA is not null);
        ExportBenchmarkCsvCommand = new AsyncRelayCommand(ExportBenchmarkCsvAsync, () => SelectedBenchmarkA is not null);
        DeleteBenchmarkCommand = new AsyncRelayCommand(DeleteSelectedBenchmarkAsync, () => SelectedBenchmarkA is not null && !IsBenchmarkRunning);

        StartGuidedCommand = new AsyncRelayCommand(StartGuidedAsync, () => !IsGuidedRunning && !IsBenchmarkRunning && (SelectedProfile is not null || HasCustomSelection));
        PreviewGuidedCommand = new AsyncRelayCommand(PreviewGuidedAsync, () => !IsGuidedRunning && (SelectedProfile is not null || HasCustomSelection));
        ConfirmGuidedCommand = new RelayCommand(() => _guided.ConfirmApply(), () => IsGuidedAwaitingConfirmation);
        CancelGuidedCommand = new RelayCommand(() => _guided.Cancel(), () => IsGuidedRunning);
        KeepGuidedCommand = new RelayCommand(() => _guided.Decide(GuidedUserDecision.Keep), () => IsGuidedAwaitingDecision);
        RestoreGuidedCommand = new RelayCommand(() => _guided.Decide(GuidedUserDecision.Restore), () => IsGuidedAwaitingDecision);
        RefreshGuidedHistoryCommand = new AsyncRelayCommand(LoadGuidedHistoryAsync, () => !IsBusy);

        // Phase 6 — Custom / individual optimization
        RefreshCustomCatalogCommand = new AsyncRelayCommand(RefreshCustomCatalogAsync, () => !IsBusy);
        SelectAllVisibleCustomCommand = new RelayCommand(() =>
        {
            foreach (var row in CustomOptimizationRows)
            {
                row.IsSelected = true;
            }
            NotifyCustomSelectionChanged();
        }, () => CustomOptimizationRows.Count > 0);
        ClearCustomSelectionCommand = new RelayCommand(() =>
        {
            _customSelectedKeys.Clear();
            foreach (var row in CustomOptimizationRows)
            {
                row.IsSelected = false;
            }
            NotifyCustomSelectionChanged();
            ApplyCustomFilter();
        }, () => HasCustomSelection);
        ApplyRecommendedToSelectionCommand = new RelayCommand(ApplyRecommendedValuesToSelected, () => HasCustomSelection);
        PreviewCustomSelectionCommand = new AsyncRelayCommand(PreviewCustomSelectionAsync, () => !IsBusy && HasCustomSelection);
        GuidedCustomSelectionCommand = new AsyncRelayCommand(StartGuidedCustomAsync, () => !IsGuidedRunning && !IsBenchmarkRunning && HasCustomSelection);
        QuickTestSingleSettingCommand = new AsyncRelayCommand(QuickTestSingleSettingAsync, () => !IsGuidedRunning && !IsBenchmarkRunning && CustomSelectedCount == 1);
        SaveCustomSetCommand = new AsyncRelayCommand(SaveCustomSetAsync, () => !IsBusy && HasCustomSelection);
        SaveSelectionAsProfileCommand = new AsyncRelayCommand(SaveSelectionAsProfileAsync, () => !IsBusy && HasCustomSelection);
        LoadCustomSetCommand = new AsyncRelayCommand(LoadSelectedCustomSetAsync, () => !IsBusy && SelectedCustomSet is not null);
        DeleteCustomSetCommand = new AsyncRelayCommand(DeleteSelectedCustomSetAsync, () => !IsBusy && SelectedCustomSet is not null);
        DuplicateCustomSetCommand = new AsyncRelayCommand(DuplicateSelectedCustomSetAsync, () => !IsBusy && SelectedCustomSet is not null);
        RenameCustomSetCommand = new AsyncRelayCommand(RenameSelectedCustomSetAsync, () => !IsBusy && SelectedCustomSet is not null);
        ResetSelectedSettingViaBackupCommand = new AsyncRelayCommand(ResetSelectedViaBackupAsync, () => !IsBusy && HasCustomSelection);

        RefreshPerformanceIntelCommand = new AsyncRelayCommand(RefreshPerformanceIntelAsync, () => !IsBusy);
        SelectPerformanceSettingCommand = new RelayCommand(p =>
        {
            if (p is SettingPerformanceRecord rec)
            {
                SelectedPerformanceRecord = rec;
            }
            else if (p is string key)
            {
                SelectedPerformanceRecord = PerformanceRecords.FirstOrDefault(r =>
                    r.SettingKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            }
        });
        ComparePerformanceEvidenceCommand = new RelayCommand(BuildPerformanceMultiRunComparison, () =>
            SelectedPerformanceRecord is not null && SelectedPerformanceEvidenceA is not null && SelectedPerformanceEvidenceB is not null);
        EvaluateTargetedRestoreCommand = new AsyncRelayCommand(EvaluateTargetedRestoreAsync, () => !IsBusy && (HasCustomSelection || SelectedPerformanceRecord is not null));
        RestoreSettingCommand = new AsyncRelayCommand(RestoreSettingAsync, () => !IsBusy && CanTargetedRestore);
        RetestSettingCommand = new AsyncRelayCommand(RetestSettingAsync, () => !IsGuidedRunning && !IsBenchmarkRunning && (HasCustomSelection || SelectedPerformanceRecord is not null));
        ExportIntelligenceCommand = new AsyncRelayCommand(ExportIntelligenceAsync, () => !IsBusy);
        ExportSnapshotsCommand = new AsyncRelayCommand(ExportSnapshotsAsync, () => !IsBusy);
        ImportIntelligenceCommand = new AsyncRelayCommand(ImportIntelligenceAsync, () => !IsBusy);
        ConfirmImportMergeCommand = new AsyncRelayCommand(() => ConfirmImportAsync(IntelligenceImportMode.Merge), () => CanConfirmImport && !IsBusy);
        ConfirmImportAsNewCommand = new AsyncRelayCommand(() => ConfirmImportAsync(IntelligenceImportMode.ImportAsNew), () => CanConfirmImport && !IsBusy);
        CancelImportPreviewCommand = new RelayCommand(() =>
        {
            if (ImportPreview is not null) ImportPreview.IsVisible = false;
            ImportPreview = null;
            StatusMessage = "Import cancelled.";
        });
    }

    public ObservableCollection<OptimizationListItem> Optimizations { get; } = new();
    public ObservableCollection<PerformanceProfile> Profiles { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<string> Cs2ConfigLines { get; } = new();
    public ObservableCollection<ScoreFactor> ScoreFactors { get; } = new();
    public ObservableCollection<Cs2SettingCategory> SettingCategories { get; } = new();
    public ObservableCollection<SettingRowViewModel> SettingRows { get; } = new();
    public ObservableCollection<SettingsDiffEntry> DiffEntries { get; } = new();
    public ObservableCollection<BenchmarkRun> BenchmarkHistory { get; } = new();
    public ObservableCollection<BenchmarkChartPoint> BenchmarkChartPoints { get; } = new();
    public ObservableCollection<BenchmarkComparisonMetric> BenchmarkComparisonRows { get; } = new();
    public ObservableCollection<BenchmarkComparisonDisplayRow> BenchmarkComparisonDisplayRows { get; } = new();
    public ObservableCollection<BenchmarkConditionWarning> BenchmarkConditionWarnings { get; } = new();
    public ObservableCollection<string> ImportPreviewLines { get; } = new();
    public ObservableCollection<GuidedOptimizationRun> GuidedHistory { get; } = new();
    public ObservableCollection<GuidedComparisonRow> GuidedComparisonRows { get; } = new();
    public ObservableCollection<string> GuidedProgressLines { get; } = new();
    public ObservableCollection<CustomOptimizationRowViewModel> CustomOptimizationRows { get; } = new();
    public ObservableCollection<CustomOptimizationSet> CustomOptimizationSets { get; } = new();
    public ObservableCollection<SettingPerformanceRecord> PerformanceRecords { get; } = new();
    public ObservableCollection<SettingTestEvidence> PerformanceEvidenceRows { get; } = new();
    public ObservableCollection<PerformanceRunCompareRow> PerformanceCompareRows { get; } = new();
    public ObservableCollection<PerformanceTrendPoint> PerformanceTrendPoints { get; } = new();
    public ObservableCollection<IndividualOptimizationUiCategory> CustomUiCategories { get; } = new();
    public ObservableCollection<RiskLevel> CustomRiskLevels { get; } = new();
    public IReadOnlyList<string> CustomCategoryFilterOptions { get; } = new[]
    {
        "(All categories)", "Performance", "Hud", "Mouse", "Audio", "Communication", "Gameplay", "Other"
    };
    public IReadOnlyList<string> CustomRiskFilterOptions { get; } = new[]
    {
        "(All risks)", "Low", "Medium", "High"
    };
    public IReadOnlyList<int> BenchmarkDurationOptions { get; } = BenchmarkConfiguration.AllowedDurationsSeconds;

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
                RaisePropertyChanged(nameof(IsBenchmarkPage));
                RaisePropertyChanged(nameof(IsGuidedPage));
                RaisePropertyChanged(nameof(IsCustomPage));
                RaisePropertyChanged(nameof(IsPerformancePage));
            }
        }
    }

    public bool IsHomePage => CurrentPage.Equals("Home", StringComparison.OrdinalIgnoreCase);
    public bool IsOptimizePage => CurrentPage.Equals("Optimize", StringComparison.OrdinalIgnoreCase);
    public bool IsCs2Page => CurrentPage.Equals("Cs2", StringComparison.OrdinalIgnoreCase);
    public bool IsProfilesPage => CurrentPage.Equals("Profiles", StringComparison.OrdinalIgnoreCase);
    public bool IsBackupsPage => CurrentPage.Equals("Backups", StringComparison.OrdinalIgnoreCase);
    public bool IsSettingsPage => CurrentPage.Equals("Settings", StringComparison.OrdinalIgnoreCase);
    public bool IsBenchmarkPage => CurrentPage.Equals("Benchmark", StringComparison.OrdinalIgnoreCase);
    public bool IsGuidedPage => CurrentPage.Equals("Guided", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomPage => CurrentPage.Equals("Custom", StringComparison.OrdinalIgnoreCase);
    public bool IsPerformancePage => CurrentPage.Equals("Performance", StringComparison.OrdinalIgnoreCase);

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
    public ICommand StartBenchmarkCommand { get; }
    public ICommand StopBenchmarkCommand { get; }
    public ICommand CancelBenchmarkCommand { get; }
    public ICommand RefreshBenchmarkHistoryCommand { get; }
    public ICommand CompareBenchmarksCommand { get; }
    public ICommand ExportBenchmarkJsonCommand { get; }
    public ICommand ExportBenchmarkCsvCommand { get; }
    public ICommand DeleteBenchmarkCommand { get; }
    public ICommand StartGuidedCommand { get; }
    public ICommand PreviewGuidedCommand { get; }
    public ICommand ConfirmGuidedCommand { get; }
    public ICommand CancelGuidedCommand { get; }
    public ICommand KeepGuidedCommand { get; }
    public ICommand RestoreGuidedCommand { get; }
    public ICommand RefreshGuidedHistoryCommand { get; }
    public ICommand RefreshCustomCatalogCommand { get; }
    public ICommand SelectAllVisibleCustomCommand { get; }
    public ICommand ClearCustomSelectionCommand { get; }
    public ICommand ApplyRecommendedToSelectionCommand { get; }
    public ICommand PreviewCustomSelectionCommand { get; }
    public ICommand GuidedCustomSelectionCommand { get; }
    public ICommand QuickTestSingleSettingCommand { get; }
    public ICommand SaveCustomSetCommand { get; }
    public ICommand SaveSelectionAsProfileCommand { get; }
    public ICommand LoadCustomSetCommand { get; }
    public ICommand DeleteCustomSetCommand { get; }
    public ICommand DuplicateCustomSetCommand { get; }
    public ICommand RenameCustomSetCommand { get; }
    public ICommand ResetSelectedSettingViaBackupCommand { get; }

    private readonly List<IndividualOptimizationItem> _customCatalogItems = new();
    private readonly HashSet<string> _customSelectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customPendingValues = new(StringComparer.OrdinalIgnoreCase);

    public bool HasCustomSelection => _customSelectedKeys.Count > 0;
    public int CustomSelectedCount => _customSelectedKeys.Count;
    public string CustomSelectionSummary =>
        CustomSelectedCount == 0
            ? "No settings selected."
            : $"{CustomSelectedCount} setting(s) selected. Recommended values are not measured results.";

    public string CustomSearchText
    {
        get => _customSearchText;
        set
        {
            if (SetProperty(ref _customSearchText, value ?? string.Empty))
            {
                ApplyCustomFilter(); // in-memory only
            }
        }
    }
    private string _customSearchText = string.Empty;

    public string CustomCategoryFilter
    {
        get => _customCategoryFilter;
        set
        {
            if (SetProperty(ref _customCategoryFilter, value ?? "(All categories)"))
            {
                ApplyCustomFilter();
            }
        }
    }
    private string _customCategoryFilter = "(All categories)";

    public string CustomRiskFilter
    {
        get => _customRiskFilter;
        set
        {
            if (SetProperty(ref _customRiskFilter, value ?? "(All risks)"))
            {
                ApplyCustomFilter();
            }
        }
    }
    private string _customRiskFilter = "(All risks)";

    public bool CustomOnlyChanged
    {
        get => _customOnlyChanged;
        set
        {
            if (SetProperty(ref _customOnlyChanged, value))
            {
                ApplyCustomFilter();
            }
        }
    }
    private bool _customOnlyChanged;

    public bool CustomOnlyRecommendedDiff
    {
        get => _customOnlyRecommendedDiff;
        set
        {
            if (SetProperty(ref _customOnlyRecommendedDiff, value))
            {
                ApplyCustomFilter();
            }
        }
    }
    private bool _customOnlyRecommendedDiff;

    public CustomOptimizationSet? SelectedCustomSet
    {
        get => _selectedCustomSet;
        set
        {
            if (SetProperty(ref _selectedCustomSet, value))
            {
                RaiseCanExecutes();
            }
        }
    }
    private CustomOptimizationSet? _selectedCustomSet;

    public string NewCustomSetName
    {
        get => _newCustomSetName;
        set => SetProperty(ref _newCustomSetName, value ?? string.Empty);
    }
    private string _newCustomSetName = string.Empty;

    public string CustomResetExplanation { get; } =
        "Reset uses the existing FrameForge backup/restore path for managed cfg + autoexec. " +
        "It restores the last FrameForge settings backup as a whole — not a single-key surgical restore. " +
        "Precise single-key restore without a dedicated per-key snapshot is not guaranteed.";

    public ICommand RefreshPerformanceIntelCommand { get; }
    public ICommand SelectPerformanceSettingCommand { get; }
    public ICommand ComparePerformanceEvidenceCommand { get; }
    public ICommand EvaluateTargetedRestoreCommand { get; }
    public ICommand ExportIntelligenceCommand { get; }
    public ICommand ExportSnapshotsCommand { get; }
    public ICommand ImportIntelligenceCommand { get; }

    public string PerformanceFingerprintSummary
    {
        get => _performanceFingerprintSummary;
        set => SetProperty(ref _performanceFingerprintSummary, value ?? string.Empty);
    }
    private string _performanceFingerprintSummary = "Fingerprint not loaded.";

    public string PerformanceIntelStatus
    {
        get => _performanceIntelStatus;
        set => SetProperty(ref _performanceIntelStatus, value ?? string.Empty);
    }
    private string _performanceIntelStatus = "Local intelligence idle.";

    public string GlobalRecommendationBlurb
    {
        get => _globalRecommendationBlurb;
        set => SetProperty(ref _globalRecommendationBlurb, value ?? string.Empty);
    }
    private string _globalRecommendationBlurb = "No local benchmark evidence yet.";

    public bool HasPerformanceRecords => PerformanceRecords.Count > 0;

    public SettingPerformanceRecord? SelectedPerformanceRecord
    {
        get => _selectedPerformanceRecord;
        set
        {
            if (SetProperty(ref _selectedPerformanceRecord, value))
            {
                PerformanceEvidenceRows.Clear();
                PerformanceTrendPoints.Clear();
                if (value is not null)
                {
                    foreach (var e in value.DirectEvidence)
                    {
                        PerformanceEvidenceRows.Add(e);
                    }
                    foreach (var e in value.AssociatedEvidence)
                    {
                        PerformanceEvidenceRows.Add(e);
                    }
                    foreach (var e in value.DirectEvidence.OrderBy(x => x.TestedAt))
                    {
                        var cpu = e.ComparisonRows.FirstOrDefault(r =>
                            r.IsAvailable && r.Metric.Contains("System CPU", StringComparison.OrdinalIgnoreCase));
                        PerformanceTrendPoints.Add(new PerformanceTrendPoint
                        {
                            Label = e.TestedAt.LocalDateTime.ToString("MM-dd HH:mm"),
                            Value = cpu?.AbsoluteDifference ?? 0,
                            Classification = e.Classification.ToString(),
                            EvidenceType = e.EvidenceType.ToString()
                        });
                    }
                }
                RaisePropertyChanged(nameof(SelectedPerformanceSummary));
                RaisePropertyChanged(nameof(HasPerformanceEvidence));
                RaiseCanExecutes();
                if (value is not null)
                {
                    _ = BuildSettingDetailAsync(value);
                }
                else
                {
                    SettingDetail = null;
                }
            }
        }
    }
    private SettingPerformanceRecord? _selectedPerformanceRecord;
    private List<SettingPerformanceRecord> _allPerformanceRecords = new();

    public string SelectedPerformanceSummary
    {
        get
        {
            var r = SelectedPerformanceRecord;
            if (r is null) return "Select a setting to inspect local evidence.";
            return $"{r.SettingName} ({r.SettingKey}) · tests={r.TestCount} (direct={r.DirectTestCount}, multi={r.AssociatedMultiSettingTestCount}) · " +
                   $"latest={r.DisplayLatest} · confidence={r.DisplayConfidence} · {r.RecommendationSummary}";
        }
    }

    public bool HasPerformanceEvidence => PerformanceEvidenceRows.Count > 0;

    public SettingTestEvidence? SelectedPerformanceEvidenceA
    {
        get => _selectedPerformanceEvidenceA;
        set { if (SetProperty(ref _selectedPerformanceEvidenceA, value)) RaiseCanExecutes(); }
    }
    private SettingTestEvidence? _selectedPerformanceEvidenceA;

    public SettingTestEvidence? SelectedPerformanceEvidenceB
    {
        get => _selectedPerformanceEvidenceB;
        set { if (SetProperty(ref _selectedPerformanceEvidenceB, value)) RaiseCanExecutes(); }
    }
    private SettingTestEvidence? _selectedPerformanceEvidenceB;


    public ImportPreviewViewState? ImportPreview
    {
        get => _importPreview;
        set
        {
            if (SetProperty(ref _importPreview, value))
            {
                RaisePropertyChanged(nameof(IsImportPreviewVisible));
                RaisePropertyChanged(nameof(CanConfirmImport));
                ImportPreviewLines.Clear();
                if (value is not null)
                {
                    ImportPreviewLines.Add(value.SummaryText);
                    foreach (var e in value.Errors.Take(12))
                    {
                        ImportPreviewLines.Add("Error: " + e);
                    }

                    foreach (var c in value.ConflictDetails.Take(8))
                    {
                        ImportPreviewLines.Add("Conflict: " + c);
                    }

                    foreach (var f in value.DifferentFingerprints.Take(8))
                    {
                        ImportPreviewLines.Add("Different fingerprint: " + f);
                    }
                }

                RaiseCanExecutes();
            }
        }
    }
    private ImportPreviewViewState? _importPreview;

    public bool IsImportPreviewVisible => ImportPreview?.IsVisible == true;
    public bool CanConfirmImport => ImportPreview is { IsValid: true, IsVisible: true };

    public ICommand ConfirmImportMergeCommand { get; private set; } = null!;
    public ICommand ConfirmImportAsNewCommand { get; private set; } = null!;
    public ICommand CancelImportPreviewCommand { get; private set; } = null!;

    public string PerformanceSystemFilter
    {
        get => _performanceSystemFilter;
        set
        {
            if (SetProperty(ref _performanceSystemFilter, value ?? "Current system"))
            {
                ApplyPerformanceFilters();
            }
        }
    }
    private string _performanceSystemFilter = "Current system";

    public IReadOnlyList<string> PerformanceSystemFilterOptions { get; } = new[]
    {
        "Current system",
        "Other systems",
        "All systems"
    };

    public string PerformanceClassificationFilter
    {
        get => _performanceClassificationFilter;
        set
        {
            if (SetProperty(ref _performanceClassificationFilter, value ?? "(All classifications)"))
            {
                ApplyPerformanceFilters();
            }
        }
    }
    private string _performanceClassificationFilter = "(All classifications)";

    public IReadOnlyList<string> PerformanceClassificationFilterOptions { get; } = new[]
    {
        "(All classifications)",
        "Improved",
        "Regressed",
        "Neutral",
        "Mixed",
        "Inconclusive"
    };

    public string PerformanceConfidenceFilter
    {
        get => _performanceConfidenceFilter;
        set
        {
            if (SetProperty(ref _performanceConfidenceFilter, value ?? "(All confidence)"))
            {
                ApplyPerformanceFilters();
            }
        }
    }
    private string _performanceConfidenceFilter = "(All confidence)";

    public IReadOnlyList<string> PerformanceConfidenceFilterOptions { get; } = new[]
    {
        "(All confidence)",
        "Unknown",
        "Low",
        "Medium",
        "High"
    };

    public string PerformanceSettingFilter
    {
        get => _performanceSettingFilter;
        set
        {
            if (SetProperty(ref _performanceSettingFilter, value ?? string.Empty))
            {
                ApplyPerformanceFilters();
            }
        }
    }
    private string _performanceSettingFilter = string.Empty;

    public string PerformanceSortKind
    {
        get => _performanceSortKind;
        set
        {
            if (SetProperty(ref _performanceSortKind, value ?? "Latest"))
            {
                ApplyPerformanceFilters();
            }
        }
    }
    private string _performanceSortKind = "Latest";

    public IReadOnlyList<string> PerformanceSortOptions { get; } = new[]
    {
        "Latest",
        "Most tested",
        "Best result",
        "Worst result"
    };

    public SettingDetailViewState? SettingDetail
    {
        get => _settingDetail;
        set => SetProperty(ref _settingDetail, value);
    }
    private SettingDetailViewState? _settingDetail;

    public string BenchmarkConditionWarningsSummary
    {
        get => _benchmarkConditionWarningsSummary;
        set => SetProperty(ref _benchmarkConditionWarningsSummary, value ?? string.Empty);
    }
    private string _benchmarkConditionWarningsSummary = string.Empty;

    public bool HasBenchmarkConditionWarnings => BenchmarkConditionWarnings.Count > 0;

    public string TargetedRestoreAssessmentText
    {
        get => _targetedRestoreAssessmentText;
        set => SetProperty(ref _targetedRestoreAssessmentText, value ?? string.Empty);
    }
    private string _targetedRestoreAssessmentText =
        "Assess a setting to see if targeted restore is safe. Unsafe cases are refused — full backup restore is a separate action.";

    public ICommand RestoreSettingCommand { get; }
    public ICommand RetestSettingCommand { get; }

    public bool CanTargetedRestore =>
        _lastTargetedAssessment?.CanRestore == true;

    public string TargetedRestoreHint =>
        _lastTargetedAssessment?.UiHint
        ?? "Select a setting and assess restore safety first.";

    private TargetedRestoreAssessment? _lastTargetedAssessment;

    public bool ShowAllSystemFingerprints
    {
        get => _showAllSystemFingerprints;
        set
        {
            if (SetProperty(ref _showAllSystemFingerprints, value))
            {
                _ = RefreshPerformanceIntelAsync();
            }
        }
    }
    private bool _showAllSystemFingerprints;

    public IReadOnlyList<string> RetestTargetOptions { get; } = new[]
    {
        "Recommended",
        "Previously Tested",
        "Custom Value"
    };

    public string RetestTargetKind
    {
        get => _retestTargetKind;
        set => SetProperty(ref _retestTargetKind, value ?? "Recommended");
    }
    private string _retestTargetKind = "Recommended";

    public string RetestCustomValue
    {
        get => _retestCustomValue;
        set => SetProperty(ref _retestCustomValue, value ?? string.Empty);
    }
    private string _retestCustomValue = string.Empty;

    public bool IsGuidedRunning =>
        _guided.Status is not GuidedOptimizationStatus.Idle
        and not GuidedOptimizationStatus.Completed
        and not GuidedOptimizationStatus.Failed
        and not GuidedOptimizationStatus.Cancelled
        and not GuidedOptimizationStatus.Restored;

    public bool IsGuidedAwaitingConfirmation => _guided.Status == GuidedOptimizationStatus.AwaitingConfirmation;
    public bool IsGuidedAwaitingDecision => _guided.Status == GuidedOptimizationStatus.AwaitingDecision;
    public string GuidedStatusText => _guided.Status.ToString();

    public string GuidedProgressMessage
    {
        get => _guidedProgressMessage;
        set
        {
            if (SetProperty(ref _guidedProgressMessage, value))
            {
                GuidedProgressLines.Insert(0, value);
                while (GuidedProgressLines.Count > 40)
                {
                    GuidedProgressLines.RemoveAt(GuidedProgressLines.Count - 1);
                }
            }
        }
    }
    private string _guidedProgressMessage = "Idle — select a profile, then Preview or Run Guided Optimization.";

    public double GuidedProgressPercent
    {
        get => _guidedProgressPercent;
        set => SetProperty(ref _guidedProgressPercent, value);
    }
    private double _guidedProgressPercent;

    public GuidedOptimizationRun? LastGuidedRun
    {
        get => _lastGuidedRun;
        set
        {
            if (SetProperty(ref _lastGuidedRun, value))
            {
                GuidedComparisonRows.Clear();
                if (value is not null)
                {
                    foreach (var r in value.ComparisonRows)
                    {
                        GuidedComparisonRows.Add(r);
                    }
                }
                RaisePropertyChanged(nameof(LastGuidedSummary));
                RaisePropertyChanged(nameof(HasGuidedComparison));
            }
        }
    }
    private GuidedOptimizationRun? _lastGuidedRun;

    public string LastGuidedSummary
    {
        get
        {
            var r = LastGuidedRun;
            if (r is null) return "No guided run yet.";
            return $"{r.Status} · {r.Classification} · decision={r.UserDecision} · backup={r.BackupId ?? "—"} · {r.ClassificationReason}";
        }
    }

    public bool HasGuidedHistory => GuidedHistory.Count > 0;
    public bool HasGuidedComparison => GuidedComparisonRows.Count > 0;

    public GuidedOptimizationRun? SelectedGuidedRun
    {
        get => _selectedGuidedRun;
        set
        {
            if (SetProperty(ref _selectedGuidedRun, value) && value is not null)
            {
                LastGuidedRun = value;
            }
        }
    }
    private GuidedOptimizationRun? _selectedGuidedRun;

    public bool IsBenchmarkRunning =>
        _benchmarkEngine.Status is BenchmarkStatus.Preparing or BenchmarkStatus.Running or BenchmarkStatus.Stopping;

    public string BenchmarkStatusText => _benchmarkEngine.Status.ToString();

    public bool HasBenchmarkHistory => BenchmarkHistory.Count > 0;
    public bool ShowEmptyBenchmarks => _hasLoaded && !IsBusy && !HasBenchmarkHistory && !IsBenchmarkRunning;
    public bool HasBenchmarkChart => BenchmarkChartPoints.Count > 0;
    public bool HasBenchmarkComparison => BenchmarkComparisonRows.Count > 0;

    public int BenchmarkDurationSeconds
    {
        get => _benchmarkDurationSeconds;
        set => SetProperty(ref _benchmarkDurationSeconds, value);
    }
    private int _benchmarkDurationSeconds = BenchmarkConfiguration.DefaultDurationSeconds;

    public int BenchmarkSampleIntervalMs
    {
        get => _benchmarkSampleIntervalMs;
        set => SetProperty(ref _benchmarkSampleIntervalMs, value);
    }
    private int _benchmarkSampleIntervalMs = BenchmarkConfiguration.DefaultSampleIntervalMs;

    public int BenchmarkWarmupSeconds
    {
        get => _benchmarkWarmupSeconds;
        set => SetProperty(ref _benchmarkWarmupSeconds, value);
    }
    private int _benchmarkWarmupSeconds = BenchmarkConfiguration.DefaultWarmupSeconds;

    public string BenchmarkLiveStatus
    {
        get => _benchmarkLiveStatus;
        set => SetProperty(ref _benchmarkLiveStatus, value);
    }
    private string _benchmarkLiveStatus = "Idle — external OS counters only (no CS2 injection).";

    public string BenchmarkPhaseText
    {
        get => _benchmarkPhaseText;
        set => SetProperty(ref _benchmarkPhaseText, value);
    }
    private string _benchmarkPhaseText = "Idle";

    public string BenchmarkProgressDetail
    {
        get => _benchmarkProgressDetail;
        set => SetProperty(ref _benchmarkProgressDetail, value);
    }
    private string _benchmarkProgressDetail = string.Empty;

    public double BenchmarkProgressPercent
    {
        get => _benchmarkProgressPercent;
        set => SetProperty(ref _benchmarkProgressPercent, value);
    }
    private double _benchmarkProgressPercent;

    public bool HasBenchmarkProgress => IsBenchmarkRunning || BenchmarkProgressPercent > 0;

    public string BenchmarkUnavailableNote { get; } =
        "Frame-time / GPU utilization: Unavailable (no injection / no graphics hook).";

    public BenchmarkRun? LastCompletedBenchmark
    {
        get => _lastCompletedBenchmark;
        set
        {
            if (SetProperty(ref _lastCompletedBenchmark, value))
            {
                RaisePropertyChanged(nameof(LastBenchmarkSummary));
            }
        }
    }
    private BenchmarkRun? _lastCompletedBenchmark;

    public string LastBenchmarkSummary
    {
        get
        {
            var r = LastCompletedBenchmark?.Result;
            if (r is null)
            {
                return "No completed run in this session.";
            }

            var cpu = r.SystemCpu.IsAvailable ? $"{r.SystemCpu.Average:0.0}%" : "n/a";
            var mem = r.SystemMemory.IsAvailable ? $"{r.SystemMemory.Average:0.0}%" : "n/a";
            var proc = r.ProcessCpu.IsAvailable ? $"{r.ProcessCpu.Average:0.0}%" : "n/a";
            var ft = r.FrameTimeMs.IsAvailable
                ? $"avg {r.FrameTimeMs.Average:0.00} ms (P1 {r.FrameTimeMs.P1:0.00})"
                : "unavailable (no injection)";
            return $"CPU {cpu} · CS2 CPU {proc} · RAM {mem} · Frame-time {ft} · samples {r.MeasuredSampleCount}";
        }
    }

    public BenchmarkRun? SelectedBenchmarkA
    {
        get => _selectedBenchmarkA;
        set
        {
            if (SetProperty(ref _selectedBenchmarkA, value))
            {
                (CompareBenchmarksCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExportBenchmarkJsonCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ExportBenchmarkCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeleteBenchmarkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private BenchmarkRun? _selectedBenchmarkA;

    public BenchmarkRun? SelectedBenchmarkB
    {
        get => _selectedBenchmarkB;
        set
        {
            if (SetProperty(ref _selectedBenchmarkB, value))
            {
                (CompareBenchmarksCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private BenchmarkRun? _selectedBenchmarkB;

    public string BenchmarkComparisonSummary
    {
        get => _benchmarkComparisonSummary;
        set => SetProperty(ref _benchmarkComparisonSummary, value);
    }
    private string _benchmarkComparisonSummary = string.Empty;

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
        (StartBenchmarkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopBenchmarkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelBenchmarkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshBenchmarkHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CompareBenchmarksCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportBenchmarkJsonCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExportBenchmarkCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteBenchmarkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartGuidedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreviewGuidedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmGuidedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelGuidedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (KeepGuidedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreGuidedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshGuidedHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCustomCatalogCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllVisibleCustomCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCustomSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyRecommendedToSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviewCustomSelectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (GuidedCustomSelectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (QuickTestSingleSettingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveCustomSetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveSelectionAsProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LoadCustomSetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCustomSetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateCustomSetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RenameCustomSetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResetSelectedSettingViaBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshPerformanceIntelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ComparePerformanceEvidenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EvaluateTargetedRestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmImportMergeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmImportAsNewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestoreSettingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetestSettingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
            await RefreshCustomCatalogAsync().ConfigureAwait(true);
            await LoadCustomSetsAsync().ConfigureAwait(true);
            await RefreshPerformanceIntelAsync().ConfigureAwait(true);

            _hasLoaded = true;
            try { await _configWatcher.StartAsync().ConfigureAwait(true); } catch { /* optional */ }
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
            RaisePropertyChanged(nameof(HasBenchmarkHistory));
            RaisePropertyChanged(nameof(ShowEmptyBenchmarks));

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




    public void NotifyCustomSelectionChanged()
    {
        RaisePropertyChanged(nameof(HasCustomSelection));
        RaisePropertyChanged(nameof(CustomSelectedCount));
        RaisePropertyChanged(nameof(CustomSelectionSummary));
        RaiseCanExecutes();
    }

    public void NotifyCustomPendingValueChanged(string configKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(configKey))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            _customPendingValues.Remove(configKey);
        }
        else
        {
            _customPendingValues[configKey] = value.Trim();
        }

        var item = _customCatalogItems.FirstOrDefault(i =>
            i.ConfigKey.Equals(configKey, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.PendingValue = value;
        }

        if (CustomOnlyChanged)
        {
            ApplyCustomFilter();
        }

        RaiseCanExecutes();
    }

    public void SetCustomRowSelected(string configKey, bool selected)
    {
        if (selected)
        {
            _customSelectedKeys.Add(configKey);
        }
        else
        {
            _customSelectedKeys.Remove(configKey);
        }

        NotifyCustomSelectionChanged();
    }

    private async Task RefreshCustomCatalogAsync()
    {
        try
        {
            if (_cs2Snapshot is null)
            {
                _cs2Snapshot = await _cs2SettingsService.ReadSettingsAsync().ConfigureAwait(true);
            }

            var built = _individualCatalog.BuildCatalog(_cs2Snapshot);
            _customCatalogItems.Clear();
            _customCatalogItems.AddRange(built);

            // restore pending values onto items
            foreach (var item in _customCatalogItems)
            {
                if (_customPendingValues.TryGetValue(item.ConfigKey, out var p))
                {
                    item.PendingValue = p;
                }
            }

            CustomUiCategories.Clear();
            foreach (var c in Enum.GetValues<IndividualOptimizationUiCategory>())
            {
                CustomUiCategories.Add(c);
            }

            CustomRiskLevels.Clear();
            foreach (var r in new[] { RiskLevel.Low, RiskLevel.Medium, RiskLevel.High })
            {
                CustomRiskLevels.Add(r);
            }

            ApplyCustomFilter();
            StatusMessage = $"Custom catalog: {_customCatalogItems.Count} supported setting(s).";
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Custom catalog refresh failed: {ex.Message}");
            ErrorMessage = ex.Message;
        }
    }

    private void ApplyCustomFilter()
    {
        IndividualOptimizationUiCategory? cat = null;
        if (!string.IsNullOrWhiteSpace(CustomCategoryFilter) &&
            !CustomCategoryFilter.StartsWith("(All", StringComparison.Ordinal) &&
            Enum.TryParse<IndividualOptimizationUiCategory>(CustomCategoryFilter, true, out var parsedCat))
        {
            cat = parsedCat;
        }

        RiskLevel? risk = null;
        if (!string.IsNullOrWhiteSpace(CustomRiskFilter) &&
            !CustomRiskFilter.StartsWith("(All", StringComparison.Ordinal) &&
            Enum.TryParse<RiskLevel>(CustomRiskFilter, true, out var parsedRisk))
        {
            risk = parsedRisk;
        }

        // Apply pending onto items for "only changed" filter
        foreach (var item in _customCatalogItems)
        {
            item.PendingValue = _customPendingValues.TryGetValue(item.ConfigKey, out var p) ? p : item.PendingValue;
        }

        var filter = new IndividualOptimizationFilter
        {
            SearchText = CustomSearchText,
            Category = cat,
            Risk = risk,
            OnlyChangedFromCurrent = CustomOnlyChanged,
            OnlyDifferFromRecommended = CustomOnlyRecommendedDiff
        };

        var filtered = _individualCatalog.Filter(_customCatalogItems, filter);
        CustomOptimizationRows.Clear();
        foreach (var item in filtered)
        {
            var row = new CustomOptimizationRowViewModel(item, this)
            {
                IsSelected = _customSelectedKeys.Contains(item.ConfigKey)
            };
            row.ApplyPerformanceRecord(_perfIntel.GetRecord(item.ConfigKey));
            if (_customPendingValues.TryGetValue(item.ConfigKey, out var pending))
            {
                row.SetPendingSilently(pending);
            }
            else if (!string.IsNullOrWhiteSpace(item.RecommendedValue))
            {
                // Default pending target = recommended when selected later; keep empty until selected
                row.SetPendingSilently(item.PendingValue ?? string.Empty);
            }

            CustomOptimizationRows.Add(row);
        }

        RaisePropertyChanged(nameof(CustomSelectionSummary));
    }

    private async Task LoadCustomSetsAsync()
    {
        try
        {
            var list = await _customSetStore.ListAsync().ConfigureAwait(true);
            CustomOptimizationSets.Clear();
            foreach (var s in list)
            {
                CustomOptimizationSets.Add(s);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Custom sets load failed: {ex.Message}");
        }
    }

    private void ApplyRecommendedValuesToSelected()
    {
        foreach (var key in _customSelectedKeys.ToList())
        {
            var item = _customCatalogItems.FirstOrDefault(i =>
                i.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item?.RecommendedValue is null)
            {
                continue;
            }

            _customPendingValues[key] = item.RecommendedValue;
            item.PendingValue = item.RecommendedValue;
        }

        ApplyCustomFilter();
        StatusMessage = "Applied recommended values to selection (not yet written to disk).";
    }

    private bool ConfirmHighRiskIfNeeded(IReadOnlyDictionary<string, string> map)
    {
        var high = new List<string>();
        foreach (var key in map.Keys)
        {
            var item = _customCatalogItems.FirstOrDefault(i =>
                i.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item?.Risk == RiskLevel.High)
            {
                high.Add(item.Name);
            }
        }

        if (high.Count == 0)
        {
            return true;
        }

        var result = MessageBox.Show(
            "This selection includes High-risk setting(s):\n\n• " +
            string.Join("\n• ", high) +
            "\n\nHigh-risk changes require explicit confirmation. Continue?",
            "High-risk confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private async Task PreviewCustomSelectionAsync()
    {
        var map = BuildSelectedSettingsMap();
        if (map.Count == 0)
        {
            StatusMessage = "Select at least one setting.";
            return;
        }

        if (!ConfirmHighRiskIfNeeded(map))
        {
            StatusMessage = "Preview cancelled (high-risk not confirmed).";
            return;
        }

        ErrorMessage = null;
        StatusMessage = "Previewing custom selection…";
        try
        {
            var run = await _guided.PreviewAsync(BuildCustomGuidedRequest(previewOnly: true)).ConfigureAwait(true);
            LastGuidedRun = run;
            if (run.PreviewDiff is not null)
            {
                PendingDiff = run.PreviewDiff;
                PendingDiffSummary = run.PreviewDiff.HasChanges
                    ? $"Custom preview: {run.PreviewDiff.ChangeCount} change(s) — no files written."
                    : "Custom preview: already matches selection.";
            }

            StatusMessage = run.Error ?? "Custom preview complete (no modifications).";
            if (!string.IsNullOrWhiteSpace(run.Error))
            {
                ErrorMessage = run.Error;
            }

            CurrentPage = "Custom";
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Custom preview failed.";
        }
        finally
        {
            RaiseCanExecutes();
        }
    }

    private async Task StartGuidedCustomAsync()
    {
        var map = BuildSelectedSettingsMap();
        if (map.Count == 0)
        {
            StatusMessage = "Select at least one setting.";
            return;
        }

        if (!ConfirmHighRiskIfNeeded(map))
        {
            StatusMessage = "Guided run cancelled (high-risk not confirmed).";
            return;
        }

        var confirm = MessageBox.Show(
            $"Run guided benchmark for {map.Count} selected setting(s)?\n\n" +
            "Baseline → Confirm → Backup → Apply → Post benchmark → Compare → Keep/Restore\n\n" +
            "Uses the same guided service and CS2 settings apply path. No FPS guarantees.\n" +
            "Recommended values ≠ measured results.",
            "Guided custom optimization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Guided custom run cancelled.";
            return;
        }

        ErrorMessage = null;
        GuidedProgressLines.Clear();
        GuidedProgressPercent = 0;
        CurrentPage = "Guided";
        StatusMessage = "Guided custom optimization running…";

        try
        {
            var run = await _guided.RunAsync(BuildCustomGuidedRequest(previewOnly: false)).ConfigureAwait(true);
            LastGuidedRun = run;
            if (!string.IsNullOrWhiteSpace(run.Error) && run.Status == GuidedOptimizationStatus.Failed)
            {
                ErrorMessage = run.Error;
            }

            StatusMessage = $"Guided custom finished: {run.Status} / {run.Classification} / {run.UserDecision}";
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
            await RefreshCustomCatalogAsync().ConfigureAwait(true);
            _perfIntel.InvalidateCache();
            await RefreshPerformanceIntelAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Guided custom run failed.";
            _log.LogError("Guided custom run failed.", ex);
        }
        finally
        {
            RaiseCanExecutes();
        }
    }

    private async Task QuickTestSingleSettingAsync()
    {
        if (CustomSelectedCount != 1)
        {
            StatusMessage = "Quick test requires exactly one selected setting.";
            return;
        }

        await StartGuidedCustomAsync().ConfigureAwait(true);
    }

    private async Task SaveCustomSetAsync()
    {
        var map = BuildSelectedSettingsMap();
        if (map.Count == 0)
        {
            StatusMessage = "Nothing to save.";
            return;
        }

        var name = NewCustomSetName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Prompt("Save custom set", "Name for this custom optimization set:", "My set");
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusMessage = "Save cancelled.";
                return;
            }
        }

        try
        {
            var set = new CustomOptimizationSet
            {
                Id = "set-" + Guid.NewGuid().ToString("N")[..10],
                Name = name.Trim(),
                Description = $"User selection of {map.Count} setting(s).",
                Settings = map,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _customSetStore.SaveAsync(set).ConfigureAwait(true);
            NewCustomSetName = string.Empty;
            await LoadCustomSetsAsync().ConfigureAwait(true);
            SelectedCustomSet = CustomOptimizationSets.FirstOrDefault(s => s.Id == set.Id);
            StatusMessage = $"Saved custom set '{set.Name}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Save custom set failed.";
        }
    }

    private async Task SaveSelectionAsProfileAsync()
    {
        var map = BuildSelectedSettingsMap();
        if (map.Count == 0)
        {
            StatusMessage = "Nothing to save.";
            return;
        }

        var name = Prompt(
            "Save as Profile",
            "Creates a custom profile via the existing profile system:",
            NewCustomSetName.Length > 0 ? NewCustomSetName : "Custom selection");
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Save as profile cancelled.";
            return;
        }

        try
        {
            var profile = new PerformanceProfile
            {
                Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
                Name = name.Trim(),
                Description = $"Saved from custom optimization selection ({map.Count} setting(s)).",
                IsCustom = true,
                IsBuiltIn = false,
                Settings = map,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _profileService.SaveCustomProfileAsync(profile).ConfigureAwait(true);
            Profiles.Clear();
            foreach (var p in await _profileService.GetProfilesAsync().ConfigureAwait(true))
            {
                Profiles.Add(p);
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
            StatusMessage = $"Saved profile '{profile.Name}' via existing profile system.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Save as profile failed.";
        }
    }

    private async Task LoadSelectedCustomSetAsync()
    {
        if (SelectedCustomSet is null)
        {
            return;
        }

        _customSelectedKeys.Clear();
        _customPendingValues.Clear();
        foreach (var (k, v) in SelectedCustomSet.Settings)
        {
            _customSelectedKeys.Add(k);
            _customPendingValues[k] = v;
        }

        ApplyCustomFilter();
        NotifyCustomSelectionChanged();
        StatusMessage = $"Loaded set '{SelectedCustomSet.Name}' ({SelectedCustomSet.SettingCount} setting(s)).";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task DeleteSelectedCustomSetAsync()
    {
        if (SelectedCustomSet is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete custom set '{SelectedCustomSet.Name}'?",
            "Delete set",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _customSetStore.DeleteAsync(SelectedCustomSet.Id).ConfigureAwait(true);
            SelectedCustomSet = null;
            await LoadCustomSetsAsync().ConfigureAwait(true);
            StatusMessage = "Custom set deleted.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task DuplicateSelectedCustomSetAsync()
    {
        if (SelectedCustomSet is null)
        {
            return;
        }

        try
        {
            var copy = await _customSetStore.DuplicateAsync(SelectedCustomSet.Id).ConfigureAwait(true);
            await LoadCustomSetsAsync().ConfigureAwait(true);
            SelectedCustomSet = CustomOptimizationSets.FirstOrDefault(s => s.Id == copy.Id);
            StatusMessage = $"Duplicated set as '{copy.Name}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task RenameSelectedCustomSetAsync()
    {
        if (SelectedCustomSet is null)
        {
            return;
        }

        var name = Prompt("Rename set", "New name:", SelectedCustomSet.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _customSetStore.RenameAsync(SelectedCustomSet.Id, name.Trim()).ConfigureAwait(true);
            await LoadCustomSetsAsync().ConfigureAwait(true);
            SelectedCustomSet = CustomOptimizationSets.FirstOrDefault(s =>
                s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            StatusMessage = "Custom set renamed.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ResetSelectedViaBackupAsync()
    {
        var confirm = MessageBox.Show(
            CustomResetExplanation + "\n\nRestore the last FrameForge settings backup now?",
            "Reset via backup",
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
            StatusMessage = result.Success ? result.Message : ("Reset failed: " + result.Message);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
            }

            await LoadCs2SettingsAsync().ConfigureAwait(true);
            await RefreshCustomCatalogAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
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



    private void ApplyPerformanceFilters()
    {
        IEnumerable<SettingPerformanceRecord> q = _allPerformanceRecords;
        switch (PerformanceSystemFilter)
        {
            case "Other systems":
                q = q.Where(r => !r.IsCurrentSystem);
                break;
            case "All systems":
                break;
            default:
                q = q.Where(r => r.IsCurrentSystem);
                break;
        }

        if (!string.IsNullOrWhiteSpace(PerformanceSettingFilter))
        {
            var f = PerformanceSettingFilter.Trim();
            q = q.Where(r =>
                r.SettingName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                r.SettingKey.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        if (PerformanceClassificationFilter is not "(All classifications)")
        {
            q = q.Where(r =>
                string.Equals(r.LatestClassification?.ToString(), PerformanceClassificationFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (PerformanceConfidenceFilter is not "(All confidence)")
        {
            q = q.Where(r =>
                string.Equals(r.Confidence.ToString(), PerformanceConfidenceFilter, StringComparison.OrdinalIgnoreCase));
        }

        q = PerformanceSortKind switch
        {
            "Most tested" => q.OrderByDescending(r => r.TestCount).ThenBy(r => r.SettingName),
            "Best result" => q.OrderByDescending(r => r.DirectImprovedCount)
                .ThenByDescending(r => r.ImprovedCount)
                .ThenBy(r => r.SettingName),
            "Worst result" => q.OrderByDescending(r => r.DirectRegressedCount)
                .ThenByDescending(r => r.RegressedCount)
                .ThenBy(r => r.SettingName),
            _ => q.OrderByDescending(r => r.LastTestedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.SettingName)
        };

        var keep = SelectedPerformanceRecord?.SettingKey;
        PerformanceRecords.Clear();
        foreach (var r in q)
        {
            PerformanceRecords.Add(r);
        }

        RaisePropertyChanged(nameof(HasPerformanceRecords));
        if (keep is not null)
        {
            SelectedPerformanceRecord = PerformanceRecords.FirstOrDefault(r =>
                r.SettingKey.Equals(keep, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task BuildSettingDetailAsync(SettingPerformanceRecord record)
    {
        try
        {
            string? current = null;
            string? recommended = null;
            try
            {
                var snap = await _cs2SettingsService.ReadSettingsAsync().ConfigureAwait(true);
                var live = snap.Settings.FirstOrDefault(s =>
                    s.Definition.ConfigKey.Equals(record.SettingKey, StringComparison.OrdinalIgnoreCase));
                current = live?.CurrentValue;
                recommended = live?.RecommendedValue ?? live?.Definition.RecommendedValue;
            }
            catch
            {
                // leave null
            }

            await AssessSelectedForRestoreAsync(record.SettingKey).ConfigureAwait(true);

            SettingDetail = new SettingDetailViewState
            {
                SettingKey = record.SettingKey,
                SettingName = record.SettingName,
                CurrentValue = current,
                RecommendedValue = recommended,
                LastMeasuredResult = record.DisplayLatest,
                TestCount = record.TestCount,
                DirectTestCount = record.DirectTestCount,
                AssociatedTestCount = record.AssociatedMultiSettingTestCount,
                Confidence = record.DisplayConfidence,
                ConfidenceReason = record.ConfidenceReason,
                RecommendationSummary = record.RecommendationSummary,
                FingerprintLabel = record.FingerprintLabel,
                LatestTestAt = record.LastTestedAt,
                RestoreState = TargetedRestoreAssessmentText,
                CanRestore = CanTargetedRestore,
                HasEvidence = record.HasEvidence
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Setting detail failed: {ex.Message}");
        }
    }

    private async Task RefreshPerformanceIntelAsync()
    {
        try
        {
            var index = await _perfIntel.GetIndexAsync(forceRebuild: true).ConfigureAwait(true);
            PerformanceFingerprintSummary = index.CurrentFingerprint?.DisplaySummary
                ?? "Fingerprint unavailable.";
            _allPerformanceRecords = index.Records.Where(x => x.HasEvidence).ToList();
            // Sync system filter with legacy checkbox when toggled from older UI
            if (ShowAllSystemFingerprints && PerformanceSystemFilter == "Current system")
            {
                PerformanceSystemFilter = "All systems";
            }

            ApplyPerformanceFilters();

            if (index.Records.Count == 0 || index.Records.All(r => !r.HasEvidence))
            {
                GlobalRecommendationBlurb = "No local benchmark evidence yet.";
            }
            else
            {
                var withDirect = index.Records.Count(r => r.DirectTestCount > 0);
                GlobalRecommendationBlurb =
                    $"Based on your tests: {index.AllEvidence.Count} evidence row(s) across {withDirect} setting(s) with direct tests. " +
                    "Heuristic only — not a guarantee. Single-setting evidence is stronger than multi-setting.";
            }

            PerformanceIntelStatus =
                $"Built {index.BuiltAt.LocalDateTime:g} · {index.GuidedRunsAnalyzed} guided run(s) · {PerformanceRecords.Count} setting(s) with evidence · local only, no telemetry.";

            // refresh custom rows with intel
            ApplyCustomFilter();
            RaisePropertyChanged(nameof(HasPerformanceRecords));
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Performance intel refresh failed: {ex.Message}");
            PerformanceIntelStatus = "Intelligence refresh failed: " + ex.Message;
        }
    }

    private void BuildPerformanceMultiRunComparison()
    {
        PerformanceCompareRows.Clear();
        var a = SelectedPerformanceEvidenceA;
        var b = SelectedPerformanceEvidenceB;
        if (a is null || b is null)
        {
            return;
        }

        var metrics = a.ComparisonRows.Select(r => r.Metric)
            .Union(b.ComparisonRows.Select(r => r.Metric), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var metric in metrics)
        {
            var ra = a.ComparisonRows.FirstOrDefault(r => r.Metric.Equals(metric, StringComparison.OrdinalIgnoreCase));
            var rb = b.ComparisonRows.FirstOrDefault(r => r.Metric.Equals(metric, StringComparison.OrdinalIgnoreCase));
            PerformanceCompareRows.Add(new PerformanceRunCompareRow
            {
                Metric = metric,
                RunALabel = a.TestedAt.LocalDateTime.ToString("g"),
                RunBLabel = b.TestedAt.LocalDateTime.ToString("g"),
                BeforeA = ra?.Before,
                AfterA = ra?.After,
                DeltaA = ra?.AbsoluteDifference,
                BeforeB = rb?.Before,
                AfterB = rb?.After,
                DeltaB = rb?.AbsoluteDifference,
                ClassificationA = a.Classification.ToString(),
                ClassificationB = b.Classification.ToString(),
                EvidenceTypeA = a.EvidenceType.ToString(),
                EvidenceTypeB = b.EvidenceType.ToString()
            });
        }

        StatusMessage = $"Compared two runs for {SelectedPerformanceRecord?.SettingKey ?? "setting"} (raw recorded values only).";
    }

    private string? ResolveTargetSettingKey()
    {
        if (SelectedPerformanceRecord is not null)
        {
            return SelectedPerformanceRecord.SettingKey;
        }

        if (_customSelectedKeys.Count == 1)
        {
            return _customSelectedKeys.First();
        }

        return _customSelectedKeys.FirstOrDefault();
    }

    private async Task AssessSelectedForRestoreAsync(string key)
    {
        try
        {
            var assessment = await _targetedRestoreService.AssessAsync(key).ConfigureAwait(true);
            _lastTargetedAssessment = assessment;
            TargetedRestoreAssessmentText = $"{assessment.Safety}: {assessment.Message}";
            RaisePropertyChanged(nameof(CanTargetedRestore));
            RaisePropertyChanged(nameof(TargetedRestoreHint));
            RaiseCanExecutes();
        }
        catch (Exception ex)
        {
            TargetedRestoreAssessmentText = ex.Message;
            _lastTargetedAssessment = null;
            RaisePropertyChanged(nameof(CanTargetedRestore));
            RaisePropertyChanged(nameof(TargetedRestoreHint));
        }
    }

    private async Task EvaluateTargetedRestoreAsync()
    {
        var key = ResolveTargetSettingKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            TargetedRestoreAssessmentText = "Select a setting first.";
            return;
        }

        await AssessSelectedForRestoreAsync(key).ConfigureAwait(true);
        StatusMessage = CanTargetedRestore
            ? "Targeted restore is available for this setting."
            : "Targeted restore is not available — see reason. Full backup restore remains a separate action.";
    }

    private async Task RestoreSettingAsync()
    {
        var key = ResolveTargetSettingKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusMessage = "Select a setting first.";
            return;
        }

        var assessment = await _targetedRestoreService.AssessAsync(key).ConfigureAwait(true);
        _lastTargetedAssessment = assessment;
        RaisePropertyChanged(nameof(CanTargetedRestore));
        RaisePropertyChanged(nameof(TargetedRestoreHint));
        TargetedRestoreAssessmentText = $"{assessment.Safety}: {assessment.Message}";

        if (!assessment.CanRestore)
        {
            StatusMessage = "Targeted restore refused: " + assessment.UiHint;
            MessageBox.Show(
                assessment.Message + "

Full backup restore is a separate explicit action on the Backups page.",
                "Targeted restore unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Restore setting?

" +
            $"Setting: {assessment.SettingName ?? key}
" +
            $"Current: {assessment.CurrentValue ?? "—"}
" +
            $"Restore to: {assessment.RestoreValue ?? "—"}
" +
            $"File: {assessment.AffectedFile ?? "—"}
" +
            $"Reason: {assessment.Message}

" +
            "A backup of the current managed file will be created first. " +
            "This does not silently run a full backup restore.",
            "Confirm targeted restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Targeted restore cancelled.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _targetedRestoreService.RestoreAsync(key).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = result.Message;
                await LoadCs2SettingsAsync().ConfigureAwait(true);
                await RefreshCustomCatalogAsync().ConfigureAwait(true);
                await AssessSelectedForRestoreAsync(key).ConfigureAwait(true);
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = result.Refused ? "Restore refused." : "Restore failed.";
                if (result.RolledBack)
                {
                    StatusMessage += " Recovered from pre-restore backup.";
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Targeted restore failed.";
        }
        finally
        {
            IsBusy = false;
            RaiseCanExecutes();
        }
    }

    private async Task RetestSettingAsync()
    {
        var key = ResolveTargetSettingKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusMessage = "Select exactly one setting to retest.";
            return;
        }

        var item = _customCatalogItems.FirstOrDefault(i =>
            i.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? _individualCatalog.GetByConfigKey(key, _cs2Snapshot);

        if (item is null)
        {
            StatusMessage = $"Setting '{key}' not found in catalog.";
            return;
        }

        string? targetValue = null;
        var kind = RetestTargetKind;
        if (kind.StartsWith("Previous", StringComparison.OrdinalIgnoreCase))
        {
            var evidence = _perfIntel.GetEvidenceForSetting(key, currentSystemOnly: true)
                .FirstOrDefault(e => e.EvidenceType == PerformanceEvidenceType.SingleSetting &&
                                     !string.IsNullOrWhiteSpace(e.AppliedValue));
            targetValue = evidence?.AppliedValue;
            if (string.IsNullOrWhiteSpace(targetValue))
            {
                StatusMessage = "No previously tested value recorded for this setting on the current system.";
                return;
            }
        }
        else if (kind.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
        {
            targetValue = string.IsNullOrWhiteSpace(RetestCustomValue)
                ? (_customPendingValues.TryGetValue(key, out var p) ? p : null)
                : RetestCustomValue.Trim();
            if (string.IsNullOrWhiteSpace(targetValue))
            {
                StatusMessage = "Enter a custom value for retest.";
                return;
            }
        }
        else
        {
            targetValue = item.RecommendedValue ?? item.DefaultValue;
            if (string.IsNullOrWhiteSpace(targetValue))
            {
                StatusMessage = "No recommended value available for this setting.";
                return;
            }
        }

        var confirm = MessageBox.Show(
            $"Retest setting '{item.Name}' ({key})?

" +
            $"Target ({kind}): {targetValue}
" +
            $"Current: {item.CurrentValue ?? "—"}

" +
            "Uses the existing guided workflow:
" +
            "Baseline → Preview → Confirm → Backup → Apply → Verify → Post benchmark → Compare → Keep/Restore.

" +
            "No FPS guarantees. CS2 should already be running.",
            "Retest setting",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Retest cancelled.";
            return;
        }

        // Drive selection for guided path
        _customSelectedKeys.Clear();
        _customSelectedKeys.Add(key);
        _customPendingValues[key] = targetValue!;
        item.PendingValue = targetValue;
        ApplyCustomFilter();
        NotifyCustomSelectionChanged();

        ErrorMessage = null;
        GuidedProgressLines.Clear();
        GuidedProgressPercent = 0;
        CurrentPage = "Guided";
        StatusMessage = $"Retesting {key}…";

        try
        {
            var request = BuildCustomGuidedRequest(
                previewOnly: false,
                labelOverride: $"Retest {key} ({kind})");
            // ensure desired map uses target
            request.DesiredSettings[key] = targetValue!;

            var run = await _guided.RunAsync(request).ConfigureAwait(true);
            LastGuidedRun = run;
            if (!string.IsNullOrWhiteSpace(run.Error) && run.Status == GuidedOptimizationStatus.Failed)
            {
                ErrorMessage = run.Error;
            }

            StatusMessage = $"Retest finished: {run.Status} / {run.Classification} / {run.UserDecision}";
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
            _perfIntel.InvalidateCache();
            await RefreshPerformanceIntelAsync().ConfigureAwait(true);
            await RefreshCustomCatalogAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Retest failed.";
            _log.LogError("Retest failed.", ex);
        }
        finally
        {
            RaiseCanExecutes();
        }
    }

    private async Task LoadGuidedHistoryAsync()
    {
        try
        {
            var list = await _guidedStore.ListAsync().ConfigureAwait(true);
            GuidedHistory.Clear();
            foreach (var run in list)
            {
                GuidedHistory.Add(run);
            }
            RaisePropertyChanged(nameof(HasGuidedHistory));
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Guided history load failed: {ex.Message}");
        }
    }

    private GuidedOptimizationRequest BuildGuidedRequest(bool previewOnly)
    {
        // Prefer explicit custom selection when present (Phase 6).
        if (HasCustomSelection)
        {
            return BuildCustomGuidedRequest(previewOnly);
        }

        return new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.Profile,
            ProfileId = SelectedProfile?.Id ?? Settings.ActiveProfileId,
            ProfileName = SelectedProfile?.Name,
            PreviewOnly = previewOnly,
            RequireCs2ProcessForBenchmark = !previewOnly,
            BenchmarkConfiguration = new BenchmarkConfiguration
            {
                DurationSeconds = BenchmarkDurationSeconds,
                SampleIntervalMs = BenchmarkSampleIntervalMs,
                WarmupSeconds = BenchmarkWarmupSeconds,
                ProfileId = SelectedProfile?.Id,
                ProfileName = SelectedProfile?.Name
            },
            Label = SelectedProfile?.Name
        };
    }

    private GuidedOptimizationRequest BuildCustomGuidedRequest(bool previewOnly, string? labelOverride = null)
    {
        var map = BuildSelectedSettingsMap();
        return new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = map,
            PreviewOnly = previewOnly,
            RequireCs2ProcessForBenchmark = !previewOnly,
            BenchmarkConfiguration = new BenchmarkConfiguration
            {
                DurationSeconds = BenchmarkDurationSeconds,
                SampleIntervalMs = BenchmarkSampleIntervalMs,
                WarmupSeconds = BenchmarkWarmupSeconds,
                ProfileId = SelectedCustomSet?.Id,
                ProfileName = SelectedCustomSet?.Name
            },
            Label = labelOverride
                ?? (map.Count == 1 ? map.Keys.First() : $"{map.Count} custom setting(s)"),
            CustomSetId = SelectedCustomSet?.Id,
            CustomSetName = SelectedCustomSet?.Name
        };
    }

    private Dictionary<string, string> BuildSelectedSettingsMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _customSelectedKeys)
        {
            var item = _customCatalogItems.FirstOrDefault(i =>
                i.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                continue;
            }

            var value = _customPendingValues.TryGetValue(key, out var pending) && !string.IsNullOrWhiteSpace(pending)
                ? pending.Trim()
                : item.EffectiveTargetValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            map[item.ConfigKey] = value;
        }

        return map;
    }

    private async Task PreviewGuidedAsync()
    {
        if (!HasCustomSelection && SelectedProfile is null)
        {
            StatusMessage = "Select a profile or custom settings first.";
            return;
        }

        ErrorMessage = null;
        GuidedProgressLines.Clear();
        StatusMessage = "Guided preview…";
        try
        {
            var run = await _guided.PreviewAsync(BuildGuidedRequest(previewOnly: true)).ConfigureAwait(true);
            LastGuidedRun = run;
            if (run.PreviewDiff is not null)
            {
                PendingDiff = run.PreviewDiff;
                PendingDiffSummary = run.PreviewDiff.HasChanges
                    ? $"Preview: {run.PreviewDiff.ChangeCount} change(s) — no files written."
                    : "Preview: already matches profile.";
            }
            StatusMessage = run.Status == GuidedOptimizationStatus.Completed
                ? "Preview complete (no modifications)."
                : run.Error ?? run.Status.ToString();
            if (!string.IsNullOrWhiteSpace(run.Error))
            {
                ErrorMessage = run.Error;
            }
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Preview failed.";
        }
        finally
        {
            RaiseCanExecutes();
        }
    }

    private async Task StartGuidedAsync()
    {
        if (!HasCustomSelection && SelectedProfile is null)
        {
            StatusMessage = "Select a profile or custom settings first.";
            return;
        }

        var target = HasCustomSelection
            ? $"{CustomSelectedCount} custom setting(s)"
            : $"profile '{SelectedProfile!.Name}'";

        var confirm = MessageBox.Show(
            "Start guided optimization for " + target + "?\n\n" +
            "1) Baseline benchmark (CS2 should be running)\n" +
            "2) Preview diff — you must confirm before apply\n" +
            "3) Backup + apply settings (existing CS2 settings path only)\n" +
            "4) Post benchmark + compare\n" +
            "5) Keep or Restore\n\n" +
            "Recommended values are not measured gains. FrameForge does not guarantee FPS improvements.",
            "Guided Optimization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            StatusMessage = "Guided run cancelled.";
            return;
        }

        ErrorMessage = null;
        GuidedProgressLines.Clear();
        GuidedProgressPercent = 0;
        StatusMessage = "Guided optimization running…";
        CurrentPage = "Guided";

        try
        {
            var runTask = _guided.RunAsync(BuildGuidedRequest(previewOnly: false));

            // Poll UI while waiting for confirmation / decision is driven by commands
            var run = await runTask.ConfigureAwait(true);
            LastGuidedRun = run;
            if (!string.IsNullOrWhiteSpace(run.Error) && run.Status == GuidedOptimizationStatus.Failed)
            {
                ErrorMessage = run.Error;
            }
            StatusMessage = $"Guided finished: {run.Status} / {run.Classification} / {run.UserDecision}";
            await LoadGuidedHistoryAsync().ConfigureAwait(true);
            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
            _perfIntel.InvalidateCache();
            await RefreshPerformanceIntelAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Guided run failed.";
            _log.LogError("Guided run failed.", ex);
        }
        finally
        {
            RaiseCanExecutes();
            RaisePropertyChanged(nameof(IsGuidedRunning));
            RaisePropertyChanged(nameof(GuidedStatusText));
        }
    }

    private async Task LoadBenchmarkHistoryAsync()
    {
        try
        {
            var list = await _benchmarkStore.ListAsync().ConfigureAwait(true);
            BenchmarkHistory.Clear();
            foreach (var run in list)
            {
                BenchmarkHistory.Add(run);
            }

            RaisePropertyChanged(nameof(HasBenchmarkHistory));
            RaisePropertyChanged(nameof(ShowEmptyBenchmarks));
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Benchmark history load failed: {ex.Message}");
        }
    }


    private void ApplyBenchmarkProgress(BenchmarkProgress progress)
    {
        BenchmarkPhaseText = progress.Phase.ToString();
        BenchmarkLiveStatus = progress.Message;
        if (progress.ProgressPercent is double pct)
        {
            BenchmarkProgressPercent = pct;
        }

        var remain = progress.RemainingSeconds is double r ? $"{r:0.0}s remaining" : "—";
        BenchmarkProgressDetail =
            $"Phase: {progress.Phase} · Elapsed {progress.ElapsedSeconds:0.0}s · {remain} · " +
            $"Samples {progress.SamplesCollected} (warm-up {progress.WarmupSamples}, measured {progress.MeasuredSamples}) · " +
            $"Interval {progress.SampleIntervalMs}ms · Warm-up {progress.WarmupSeconds}s · {progress.Cs2ProcessStatus}";
        if (!string.IsNullOrWhiteSpace(progress.UnavailableNote))
        {
            BenchmarkProgressDetail += " · " + progress.UnavailableNote;
        }

        RaisePropertyChanged(nameof(HasBenchmarkProgress));
        RaisePropertyChanged(nameof(BenchmarkStatusText));
        RaisePropertyChanged(nameof(IsBenchmarkRunning));
    }

    private async Task ExportIntelligenceAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "FrameForge intelligence (*.frameforge-intelligence.json)|*.frameforge-intelligence.json|JSON (*.json)|*.json",
            FileName = $"frameforge-intelligence-{DateTime.Now:yyyyMMdd-HHmm}",
            AddExtension = true,
            DefaultExt = "frameforge-intelligence.json"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        try
        {
            await _intelExport.ExportIntelligenceAsync(dialog.FileName).ConfigureAwait(true);
            StatusMessage = "Intelligence exported (no PII).";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ExportSnapshotsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "FrameForge snapshots (*.frameforge-snapshots.json)|*.frameforge-snapshots.json|JSON (*.json)|*.json",
            FileName = $"frameforge-snapshots-{DateTime.Now:yyyyMMdd-HHmm}",
            AddExtension = true,
            DefaultExt = "frameforge-snapshots.json"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        try
        {
            await _intelExport.ExportSnapshotsAsync(dialog.FileName).ConfigureAwait(true);
            StatusMessage = "Setting snapshots exported.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ImportIntelligenceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter =
                "FrameForge packages (*.frameforge-intelligence.json;*.frameforge-snapshots.json)|*.frameforge-intelligence.json;*.frameforge-snapshots.json|JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Import cancelled.";
            return;
        }

        try
        {
            var preview = await _intelExport.PreviewImportAsync(dialog.FileName).ConfigureAwait(true);
            var kind = dialog.FileName.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                ? "Snapshots"
                : "Intelligence";
            var direct = preview.Package?.Evidence.Count(e => e.EvidenceType == PerformanceEvidenceType.SingleSetting) ?? 0;
            var multi = preview.Package?.Evidence.Count(e => e.EvidenceType == PerformanceEvidenceType.MultiSetting) ?? 0;
            ImportPreview = new ImportPreviewViewState
            {
                IsVisible = true,
                IsValid = preview.IsValid,
                SourcePath = dialog.FileName,
                PackageKind = kind,
                Message = preview.Message,
                Errors = preview.Errors.ToList(),
                FilesDetected = 1,
                Records = preview.Package?.Records.Count ?? 0,
                DirectEvidence = direct,
                MultiSettingEvidence = multi,
                Fingerprints = preview.Package?.Fingerprints.Count
                    ?? preview.DifferentFingerprints.Count,
                NewRecords = preview.RecordsToAdd,
                ExistingConflicts = preview.Conflicts,
                SkippedRecords = preview.RecordsToSkip,
                InvalidRecords = preview.Errors.Count,
                SnapshotsTotal = preview.SnapshotPackage?.Snapshots.Count ?? preview.SnapshotsToAdd,
                SnapshotsValid = preview.IsValid ? (preview.SnapshotPackage?.Snapshots.Count ?? preview.SnapshotsToAdd) : 0,
                SnapshotsInvalid = preview.IsValid ? 0 : Math.Max(1, preview.Errors.Count),
                SnapshotsConflicting = 0,
                DifferentFingerprints = preview.DifferentFingerprints.ToList(),
                ConflictDetails = preview.ConflictDetails.ToList(),
                SourcePreview = preview,
                ProposedMode = IntelligenceImportMode.Merge
            };

            if (!preview.IsValid)
            {
                ErrorMessage = "Import validation failed — see preview panel. No files were written.";
                StatusMessage = "Import rejected (preview only).";
                return;
            }

            StatusMessage = "Import preview ready. Confirm Merge or Import as new — default is Cancel.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ConfirmImportAsync(IntelligenceImportMode mode)
    {
        if (ImportPreview is not { IsValid: true, SourcePath: not null and not "" })
        {
            StatusMessage = "Nothing to import.";
            return;
        }

        var path = ImportPreview.SourcePath;
        var confirm = MessageBox.Show(
            mode == IntelligenceImportMode.Merge
                ? "Merge imported history into local intelligence? Fingerprints stay separate."
                : "Import as new history entries? Fingerprints stay separate.",
            "Confirm import",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK)
        {
            StatusMessage = "Import not confirmed.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _intelExport.ImportAsync(path, mode).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
                StatusMessage = "Import failed.";
                return;
            }

            StatusMessage = result.Message;
            ImportPreview = null;
            await RefreshPerformanceIntelAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }


    private async Task StartBenchmarkAsync()
    {
        var config = new BenchmarkConfiguration
        {
            DurationSeconds = BenchmarkDurationSeconds,
            SampleIntervalMs = BenchmarkSampleIntervalMs,
            WarmupSeconds = BenchmarkWarmupSeconds,
            ProfileId = SelectedProfile?.Id ?? Settings.ActiveProfileId,
            ProfileName = SelectedProfile?.Name
        };

        var validation = BenchmarkConfiguration.Validate(config);
        if (!validation.IsValid)
        {
            ErrorMessage = string.Join(Environment.NewLine, validation.Issues);
            StatusMessage = "Invalid benchmark configuration.";
            return;
        }

        BenchmarkChartPoints.Clear();
        RaisePropertyChanged(nameof(HasBenchmarkChart));
        BenchmarkLiveStatus = "Preparing…";
        StatusMessage = "Benchmark starting (external OS counters only)…";
        ErrorMessage = null;
        RaiseCanExecutes();

        try
        {
            var run = await _benchmarkEngine.StartAsync(config).ConfigureAwait(true);
            LastCompletedBenchmark = run;
            if (run.Status == BenchmarkStatus.Completed)
            {
                StatusMessage = $"Benchmark completed: {run.Id}";
            }
            else if (run.Status == BenchmarkStatus.Cancelled)
            {
                StatusMessage = "Benchmark cancelled.";
            }
            else if (run.Status == BenchmarkStatus.Failed)
            {
                ErrorMessage = run.Error ?? "Benchmark failed.";
                StatusMessage = "Benchmark failed.";
            }
            else
            {
                StatusMessage = $"Benchmark ended: {run.Status}";
            }

            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
            SelectedBenchmarkA = BenchmarkHistory.FirstOrDefault(b => b.Id == run.Id) ?? run;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Benchmark failed to start.";
            _log.LogError("Benchmark start failed.", ex);
        }
        finally
        {
            RaiseCanExecutes();
            RaisePropertyChanged(nameof(BenchmarkStatusText));
            RaisePropertyChanged(nameof(IsBenchmarkRunning));
        }
    }

    private void CompareSelectedBenchmarks()
    {
        if (SelectedBenchmarkA is null || SelectedBenchmarkB is null)
        {
            StatusMessage = "Select two benchmark runs (A and B).";
            return;
        }

        var comparison = _benchmarkEngine.Compare(SelectedBenchmarkA, SelectedBenchmarkB);
        var report = BenchmarkComparisonPresenter.Build(
            comparison,
            SelectedBenchmarkA.SystemInformation.SystemFingerprintId,
            SelectedBenchmarkB.SystemInformation.SystemFingerprintId);

        BenchmarkComparisonRows.Clear();
        BenchmarkComparisonDisplayRows.Clear();
        BenchmarkConditionWarnings.Clear();
        foreach (var m in comparison.Metrics)
        {
            BenchmarkComparisonRows.Add(m);
        }

        foreach (var r in report.Rows)
        {
            BenchmarkComparisonDisplayRows.Add(r);
        }

        foreach (var w in report.ConditionWarnings)
        {
            BenchmarkConditionWarnings.Add(w);
        }

        // Prefer calculator warnings if already populated
        if (comparison.ConditionWarnings.Count > 0 && BenchmarkConditionWarnings.Count == 0)
        {
            foreach (var w in comparison.ConditionWarnings)
            {
                BenchmarkConditionWarnings.Add(w);
            }
        }

        BenchmarkConditionWarningsSummary = BenchmarkConditionWarnings.Count == 0
            ? "No condition mismatches detected."
            : string.Join(" · ", BenchmarkConditionWarnings.Select(w => w.Message).Take(6));
        RaisePropertyChanged(nameof(HasBenchmarkConditionWarnings));

        BenchmarkComparisonSummary = report.Summary +
            " A=" + SelectedBenchmarkA.DisplayTitle + " · B=" + SelectedBenchmarkB.DisplayTitle;
        RaisePropertyChanged(nameof(HasBenchmarkComparison));
        StatusMessage = BenchmarkConditionWarnings.Count > 0
            ? "Comparison ready — condition warnings present."
            : "Comparison ready.";
    }

    private async Task ExportBenchmarkJsonAsync()
    {
        if (SelectedBenchmarkA is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = SelectedBenchmarkA.Id + ".json",
            AddExtension = true,
            DefaultExt = "json"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        try
        {
            await _benchmarkStore.ExportJsonAsync(SelectedBenchmarkA.Id, dialog.FileName).ConfigureAwait(true);
            StatusMessage = $"Exported JSON to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Export failed.";
        }
    }

    private async Task ExportBenchmarkCsvAsync()
    {
        if (SelectedBenchmarkA is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = SelectedBenchmarkA.Id + ".csv",
            AddExtension = true,
            DefaultExt = "csv"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Export cancelled.";
            return;
        }

        try
        {
            await _benchmarkStore.ExportCsvAsync(SelectedBenchmarkA.Id, dialog.FileName).ConfigureAwait(true);
            StatusMessage = $"Exported CSV samples to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "CSV export failed.";
        }
    }

    private async Task DeleteSelectedBenchmarkAsync()
    {
        if (SelectedBenchmarkA is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete local benchmark '{SelectedBenchmarkA.Id}'?",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _benchmarkStore.DeleteAsync(SelectedBenchmarkA.Id).ConfigureAwait(true);
            StatusMessage = "Benchmark deleted.";
            SelectedBenchmarkA = null;
            await LoadBenchmarkHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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


public sealed class CustomOptimizationRowViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _isSelected;
    private string _pendingValue = string.Empty;

    public CustomOptimizationRowViewModel(IndividualOptimizationItem item, MainViewModel owner)
    {
        _owner = owner;
        Item = item;
        _pendingValue = item.PendingValue
            ?? item.RecommendedValue
            ?? item.CurrentValue
            ?? string.Empty;
    }

    public IndividualOptimizationItem Item { get; }
    public string Id => Item.Id;
    public string ConfigKey => Item.ConfigKey;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string CategoryLabel => Item.CategoryLabel;
    public string? CurrentValue => Item.CurrentValue;
    public string? RecommendedValue => Item.RecommendedValue;
    public string RiskLabel => Item.RiskLabel;
    public string ExpectedImpactLabel => Item.ExpectedImpactLabel;
    public string ExpectedImpactDescription => Item.ExpectedImpactDescription;
    public bool RequiresRestart => Item.RequiresRestart;
    public bool Supported => Item.Supported;

    public string PendingValue
    {
        get => _pendingValue;
        set
        {
            if (SetProperty(ref _pendingValue, value ?? string.Empty))
            {
                _owner.NotifyCustomPendingValueChanged(ConfigKey, _pendingValue);
                RaisePropertyChanged(nameof(IsChangedFromCurrent));
            }
        }
    }

    public void SetPendingSilently(string? value)
    {
        _pendingValue = value ?? string.Empty;
        RaisePropertyChanged(nameof(PendingValue));
        RaisePropertyChanged(nameof(IsChangedFromCurrent));
    }

    public bool IsChangedFromCurrent =>
        !string.Equals(
            (CurrentValue ?? string.Empty).Trim(),
            (_pendingValue ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _owner.SetCustomRowSelected(ConfigKey, value);
            }
        }
    }

    public string DistinctionHint =>
        $"Recommended: {RecommendedValue ?? "—"}  ·  Current: {CurrentValue ?? "—"}  ·  Target: {PendingValue}  ·  Impact: {ExpectedImpactLabel} (not measured)";

    public bool HasLocalEvidence { get; private set; }
    public string LastTestedDisplay { get; private set; } = string.Empty;
    public string TestCountDisplay { get; private set; } = string.Empty;
    public string LatestResultDisplay { get; private set; } = string.Empty;
    public string ConfidenceDisplay { get; private set; } = string.Empty;
    public string BasedOnYourTests { get; private set; } = "No local benchmark evidence yet.";

    public void ApplyPerformanceRecord(SettingPerformanceRecord? record)
    {
        if (record is null || !record.HasEvidence)
        {
            HasLocalEvidence = false;
            LastTestedDisplay = string.Empty;
            TestCountDisplay = string.Empty;
            LatestResultDisplay = string.Empty;
            ConfidenceDisplay = string.Empty;
            BasedOnYourTests = "No local benchmark evidence yet.";
        }
        else
        {
            HasLocalEvidence = true;
            LastTestedDisplay = record.LastTestedAt?.LocalDateTime.ToString("g") ?? "—";
            TestCountDisplay = $"{record.DirectTestCount} direct / {record.AssociatedMultiSettingTestCount} multi";
            LatestResultDisplay = record.DisplayLatest;
            ConfidenceDisplay = record.DisplayConfidence;
            BasedOnYourTests = record.RecommendationSummary;
        }

        RaisePropertyChanged(nameof(HasLocalEvidence));
        RaisePropertyChanged(nameof(LastTestedDisplay));
        RaisePropertyChanged(nameof(TestCountDisplay));
        RaisePropertyChanged(nameof(LatestResultDisplay));
        RaisePropertyChanged(nameof(ConfidenceDisplay));
        RaisePropertyChanged(nameof(BasedOnYourTests));
        RaisePropertyChanged(nameof(IntelligenceHint));
    }

    public string IntelligenceHint =>
        HasLocalEvidence
            ? $"Based on your tests: {BasedOnYourTests} · Last {LastTestedDisplay} · {TestCountDisplay} · Latest {LatestResultDisplay} · Confidence {ConfidenceDisplay} (heuristic)"
            : "Based on your tests: No local benchmark evidence yet.";
}

public sealed class PerformanceRunCompareRow
{
    public string Metric { get; init; } = string.Empty;
    public string RunALabel { get; init; } = string.Empty;
    public string RunBLabel { get; init; } = string.Empty;
    public double? BeforeA { get; init; }
    public double? AfterA { get; init; }
    public double? DeltaA { get; init; }
    public double? BeforeB { get; init; }
    public double? AfterB { get; init; }
    public double? DeltaB { get; init; }
    public string ClassificationA { get; init; } = string.Empty;
    public string ClassificationB { get; init; } = string.Empty;
    public string EvidenceTypeA { get; init; } = string.Empty;
    public string EvidenceTypeB { get; init; } = string.Empty;
}

public sealed class PerformanceTrendPoint
{
    public string Label { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Classification { get; init; } = string.Empty;
    public string EvidenceType { get; init; } = string.Empty;
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


public sealed class BenchmarkChartPoint
{
    public double ElapsedMs { get; init; }
    public double Value { get; init; }
    public string Label { get; init; } = string.Empty;
}
