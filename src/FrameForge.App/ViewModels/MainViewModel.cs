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
            BenchmarkLiveStatus = $"t={sample.ElapsedMs/1000:0.0}s  CPU={(sample.SystemCpuPercent?.ToString("0.0") ?? "—")}%  CS2={(sample.Cs2ProcessPresent ? "yes" : "no")}";
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
        CancelBenchmarkCommand = new RelayCommand(() => _benchmarkEngine.RequestCancel(), () => IsBenchmarkRunning);
        RefreshBenchmarkHistoryCommand = new AsyncRelayCommand(LoadBenchmarkHistoryAsync, () => !IsBusy);
        CompareBenchmarksCommand = new RelayCommand(CompareSelectedBenchmarks, () => SelectedBenchmarkA is not null && SelectedBenchmarkB is not null);
        ExportBenchmarkJsonCommand = new AsyncRelayCommand(ExportBenchmarkJsonAsync, () => SelectedBenchmarkA is not null);
        ExportBenchmarkCsvCommand = new AsyncRelayCommand(ExportBenchmarkCsvAsync, () => SelectedBenchmarkA is not null);
        DeleteBenchmarkCommand = new AsyncRelayCommand(DeleteSelectedBenchmarkAsync, () => SelectedBenchmarkA is not null && !IsBenchmarkRunning);

        StartGuidedCommand = new AsyncRelayCommand(StartGuidedAsync, () => !IsGuidedRunning && !IsBenchmarkRunning && SelectedProfile is not null);
        PreviewGuidedCommand = new AsyncRelayCommand(PreviewGuidedAsync, () => !IsGuidedRunning && SelectedProfile is not null);
        ConfirmGuidedCommand = new RelayCommand(() => _guided.ConfirmApply(), () => IsGuidedAwaitingConfirmation);
        CancelGuidedCommand = new RelayCommand(() => _guided.Cancel(), () => IsGuidedRunning);
        KeepGuidedCommand = new RelayCommand(() => _guided.Decide(GuidedUserDecision.Keep), () => IsGuidedAwaitingDecision);
        RestoreGuidedCommand = new RelayCommand(() => _guided.Decide(GuidedUserDecision.Restore), () => IsGuidedAwaitingDecision);
        RefreshGuidedHistoryCommand = new AsyncRelayCommand(LoadGuidedHistoryAsync, () => !IsBusy);
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
    public ObservableCollection<GuidedOptimizationRun> GuidedHistory { get; } = new();
    public ObservableCollection<GuidedComparisonRow> GuidedComparisonRows { get; } = new();
    public ObservableCollection<string> GuidedProgressLines { get; } = new();
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

    private async Task PreviewGuidedAsync()
    {
        if (SelectedProfile is null)
        {
            StatusMessage = "Select a profile first.";
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
        if (SelectedProfile is null)
        {
            StatusMessage = "Select a profile first.";
            return;
        }

        var confirm = MessageBox.Show(
            "Start guided optimization?\n\n" +
            "1) Baseline benchmark (CS2 should be running)\n" +
            "2) Preview diff — you must confirm before apply\n" +
            "3) Backup + apply profile settings\n" +
            "4) Post benchmark + compare\n" +
            "5) Keep or Restore\n\n" +
            "FrameForge does not guarantee FPS improvements. Close extra apps for repeatability.",
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
        BenchmarkComparisonRows.Clear();
        foreach (var m in comparison.Metrics)
        {
            BenchmarkComparisonRows.Add(m);
        }

        BenchmarkComparisonSummary = comparison.Summary +
            " A=" + SelectedBenchmarkA.DisplayTitle + " · B=" + SelectedBenchmarkB.DisplayTitle;
        RaisePropertyChanged(nameof(HasBenchmarkComparison));
        StatusMessage = "Comparison ready.";
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
