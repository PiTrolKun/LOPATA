using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace AIHub;

public partial class MainWindow : Window
{
    private const int WmKeyDown = 0x0100;
    private const int VkF12 = 0x7B;
    private const string ExecutorContinueAfterReadyOptionId = "executor_continue_after_ready";

    private readonly AppSettingsStore _appSettingsStore = new();
    private readonly AppStateStore _appStateStore = new();
    private readonly ComputerPassportService _computerPassportService = new();
    private readonly CoreModelManager _coreModelManager = new();
    private readonly UserProfileStore _userProfileStore = new();
    private readonly UserContextService _userContextService;
    private readonly LocalizationService _localizationService = new();
    private readonly StorageSettingsStore _storageSettingsStore = new();
    private readonly ToolModelManager _toolModelManager = new();
    private readonly ChoiceScenarioService _choiceScenarioService = new();
    private readonly ChoiceScenarioOrchestrator _choiceScenarioOrchestrator;
    private readonly DebugModelDiscoveryService _choiceModelDiscoveryService = new();
    private readonly CapabilityInventoryService _choiceInventoryService = new();
    private readonly HuggingFaceCatalogStartupService _catalogStartupService = new();
    private readonly CoreSpeechPresentationCoordinator _coreSpeechCoordinator;
    private readonly CoreSpeechPresentationCoordinator _executorSpeechCoordinator;
    private readonly ExecutorWorkflowService _executorWorkflowService;
    private readonly ScenarioSessionArchiveService _sessionArchiveService = new();
    private readonly SessionFileManifestService _sessionFileManifestService = new();
    private readonly DispatcherTimer _profileBlinkTimer = new();
    private readonly ObservableCollection<ChoiceScenarioOption> _choiceScenarioOptions = [];
    private readonly ObservableCollection<ChoiceScenarioOption> _executorClarificationOptions = [];
    private readonly ObservableCollection<ChoiceExecutorCandidateDisplay> _executorCandidateOptions = [];
    private readonly ObservableCollection<ResumableSessionCardViewModel> _previousSessionCards = [];
    private readonly ObservableCollection<SessionFileCardViewModel> _sessionFileCards = [];
    private readonly InterfaceLayoutService _interfaceLayoutService = new();

    private AppSettings _appSettings = new();
    private AppState _appState = new();
    private ComputerPassport? _lastPassport;
    private StorageSettings _storageSettings = new();
    private UserProfile _userProfile = new();
    private CancellationTokenSource? _coreModelDownloadCts;
    private JsonlSessionLog? _coreSessionLog;
    private CoreModelCheckResult? _lastCoreModelCheck;
    private PendingModelDownload _pendingModelDownload = PendingModelDownload.Core;
    private DebugChatWindow? _debugChatWindow;
    private CoreMemoryStatus _coreMemoryStatus = CoreMemoryStatus.Inactive();
    private readonly ChoiceScenarioSessionState _choiceScenarioState = new();
    private ChoiceScenarioStep? _currentChoiceScenarioStep;
    private LlamaServerRuntimeService? _choiceScenarioRuntimeService;
    private CancellationTokenSource? _choiceScenarioCts;
    private CancellationTokenSource? _coreSpeechCts;
    private CancellationTokenSource? _executorCts;
    private CancellationTokenSource? _executorSpeechCts;
    private ExecutorResultWindow? _executorResultWindow;
    private SessionTreeWindow? _sessionTreeWindow;
    private ExecutorModelArtifact? _pendingExecutorArtifact;
    private ComponentAcquisitionPlan? _pendingExecutorComponentPlan;
    private ChoiceExecutorCandidate? _selectedExecutorCandidate;
    private bool _startExecutorWithAvailableComponents;
    private readonly CancellationTokenSource _catalogStartupCts = new();
    private ISessionEventLog? _choiceScenarioLog;
    private int _choiceScenarioInvalidJsonCount;
    private bool _choiceScenarioRequestInProgress;
    private bool _isApplyingLanguageSelection;
    private bool _isApplyingCoreVoiceSettings = true;
    private bool _isApplyingCoreAutonomySettings = true;
    private bool _isApplyingModelDownloadSettings = true;
    private bool _isApplyingInterfaceSettings = true;
    private bool _coreSpeechPresentationActive;
    private long _coreSpeechPresentationId;
    private long _executorSpeechPresentationId;
    private bool _executorSpeechPresentationActive;
    private bool _executorVoiceEnabled = true;
    private bool _executorSessionFinishedByUser;
    private bool _executorPracticalLayoutActive;
    private bool _executorFinalizationSuggested;
    private string _executorCurrentResultSummary = string.Empty;
    private ExecutorTurnResult? _currentExecutorTurn;
    private string _executorCurrentStageId = ExecutorStageIds.TaskDefinition;
    private ResumableScenarioSession? _activeResumableSession;
    private SessionRestorationContext? _sessionRestorationContext;
    private bool _resumeExecutorAfterDownload;
    private bool _isCoreModelPromptPostponed;
    private bool _isDarkTheme;
    private FrameworkElement? _settingsReturnPage;
    private string? _settingsReturnStatusText;

    private enum PendingModelDownload
    {
        Core,
        Reranker
    }

    public MainWindow()
    {
        _userContextService = new UserContextService(_userProfileStore, new IpLocationService());
        _executorWorkflowService = new ExecutorWorkflowService(_userContextService);
        _executorWorkflowService.KnowledgeTreeChanged += ExecutorWorkflowService_KnowledgeTreeChanged;
        _executorWorkflowService.CheckpointChanged += ExecutorWorkflowService_CheckpointChanged;
        _choiceScenarioOrchestrator = new ChoiceScenarioOrchestrator(_choiceScenarioService);
        InitializeComponent();
        Title = $"{ProductBrand.WindowsName} {GetAppVersion()}";
        _profileBlinkTimer.Interval = TimeSpan.FromMilliseconds(760);
        _profileBlinkTimer.Tick += ProfileBlinkTimer_Tick;
        _isDarkTheme = IsWindowsAppThemeDark();
        SourceInitialized += (_, _) =>
        {
            ApplyWindowStartupPreference();
            ApplySystemTitleBarTheme();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        SizeChanged += MainWindow_SizeChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        InitializeLocalization();
        InitializeInterfaceSettings();
        InitializeComponentLicenses();
        _coreSpeechCoordinator = new CoreSpeechPresentationCoordinator(
            new CoreVoiceEngineRouter(new EspeakCoreVoiceEngine(), new RhVoiceCoreVoiceEngine()));
        _executorSpeechCoordinator = new CoreSpeechPresentationCoordinator(
            new CoreVoiceEngineRouter(new EspeakCoreVoiceEngine(), new RhVoiceCoreVoiceEngine()));
        ApplyTheme();
        ApplyLocalization();
        ChoiceOptionsItemsControl.ItemsSource = _choiceScenarioOptions;
        ExecutorClarificationOptionsItemsControl.ItemsSource = _executorClarificationOptions;
        ExecutorCandidateItemsControl.ItemsSource = _executorCandidateOptions;
        PreviousWorkItemsControl.ItemsSource = _previousSessionCards;
        ChoiceSessionFilesItemsControl.ItemsSource = _sessionFileCards;
        ExecutorSessionFilesItemsControl.ItemsSource = _sessionFileCards;
        InitializeAppData();
        InitializeImageInput();
        InitializeComponentCatalogUi();
        InitializeManagedModelsUi();
        RefreshPreviousSessions();
        LoadCoreVoiceSettingsIntoControls();
        LoadCoreAutonomySettingsIntoControls();
        LoadModelDownloadSettingsIntoControls();
        LoadInterfaceSettingsIntoControls();
        UpdateCoreVoiceControls();
        _ = RefreshModelCatalogOnStartupAsync();
        UpdatePrimaryActionButton();
        InitializeApplicationUpdates();
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
    }

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        try { await ComponentLicenseGate.EnsureAsync("basic", CancellationToken.None); }
        catch (OperationCanceledException) { return; }
        if (_appState.HasCompletedSetup)
        {
            var coreModelCheck = _coreModelManager.Check(_storageSettings);
            if (coreModelCheck.Availability != CoreModelAvailability.Installed)
            {
                ShowCoreModelPrompt(coreModelCheck);
                return;
            }

            if (!_userProfile.IsComplete())
            {
                ShowProfileReminderPage();
                StatusText.Text = L("Status.ProfileReminderOpened");
                return;
            }

            ShowWorkStartPage();
            StatusText.Text = L("Status.WorkStartOpened");
            return;
        }

        OpenSetupWindow(regeneratePassport: false);
    }

    private void ReconfigureButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSetupWindow(regeneratePassport: true);
    }

    private void ApplyTheme()
    {
        SetBrush("WindowBackgroundBrush", _isDarkTheme ? "#111827" : "#F3F3F3");
        SetBrush("HeaderBackgroundBrush", _isDarkTheme ? "#0B1220" : "#FFFFFF");
        SetBrush("PanelBrush", _isDarkTheme ? "#172033" : "#FFFFFF");
        SetBrush("BrandBackdropBrush", _isDarkTheme ? "#00000000" : "#DBD7D7");
        SetBrush("LineBrush", _isDarkTheme ? "#2D374B" : "#DADDE3");
        SetBrush("TextPrimaryBrush", _isDarkTheme ? "#F8FAFC" : "#1F1F1F");
        SetBrush("TextSecondaryBrush", _isDarkTheme ? "#AAB4C4" : "#5D6470");
        SetBrush("StepBadgeBrush", _isDarkTheme ? "#1E3A5F" : "#EAF1FF");
        SetBrush("SecondaryButtonBackgroundBrush", _isDarkTheme ? "#111827" : "#F8F8F8");

        RootWindow.Background = (Media.Brush)Resources["WindowBackgroundBrush"];
        ProfileButton.Foreground = (Media.Brush)Resources["TextPrimaryBrush"];
        ThemeToggleButton.Content = _isDarkTheme ? "☀" : "☾";
        ThemeToggleButton.Foreground = _isDarkTheme
            ? new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString("#FBBF24"))
            : (Media.Brush)Resources["TextPrimaryBrush"];
        ThemeToggleButton.ToolTip = _isDarkTheme
            ? L("Theme.SwitchToLight")
            : L("Theme.SwitchToDark");

        ApplySystemTitleBarTheme();
        _debugChatWindow?.ApplyTheme(_isDarkTheme);
        _sessionTreeWindow?.ApplyTheme(_isDarkTheme);
        _fileViewerService.ApplyTheme(_isDarkTheme);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsPage();
        StatusText.Text = L("Status.SettingsOpened");
    }

    private void SessionTreeButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _executorWorkflowService.KnowledgeTreeSnapshot;
        if (snapshot is null || !snapshot.HasNodes)
        {
            SessionTreeButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_sessionTreeWindow is null || !_sessionTreeWindow.IsLoaded)
        {
            _sessionTreeWindow = new SessionTreeWindow(
                CreateSessionTreeWindowStrings(),
                snapshot,
                _isDarkTheme)
            {
                Owner = this
            };
            _sessionTreeWindow.Closed += (_, _) => _sessionTreeWindow = null;
            _sessionTreeWindow.Show();
        }
        else
        {
            _sessionTreeWindow.UpdateSnapshot(snapshot, animate: false);
        }

        if (_sessionTreeWindow.WindowState == WindowState.Minimized)
        {
            _sessionTreeWindow.WindowState = WindowState.Normal;
        }

        _sessionTreeWindow.Activate();
    }

    private void ExecutorWorkflowService_KnowledgeTreeChanged(
        object? sender,
        SessionKnowledgeTreeChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
                ExecutorWorkflowService_KnowledgeTreeChanged(sender, e));
            return;
        }

        SessionTreeButton.Visibility = Visibility.Visible;
        SessionTreeButton.IsEnabled = true;
        _sessionTreeWindow?.UpdateSnapshot(e.Snapshot);
    }

    private SessionTreeWindowStrings CreateSessionTreeWindowStrings() =>
        new()
        {
            Title = L("SessionTree.Title"),
            Hint = L("SessionTree.Hint"),
            ZoomIn = L("SessionTree.ZoomIn"),
            ZoomOut = L("SessionTree.ZoomOut"),
            Fit = L("SessionTree.Fit"),
            Details = L("SessionTree.Details"),
            SelectNode = L("SessionTree.SelectNode"),
            Collapse = L("SessionTree.Collapse"),
            Expand = L("SessionTree.Expand"),
            Task = L("SessionTree.TypeTask"),
            Requirement = L("SessionTree.TypeRequirement"),
            Decision = L("SessionTree.TypeDecision"),
            Knowledge = L("SessionTree.TypeKnowledge"),
            ResultFragment = L("SessionTree.TypeResultFragment"),
            OpenQuestion = L("SessionTree.TypeOpenQuestion"),
            Assumption = L("SessionTree.TypeAssumption"),
            Source = L("SessionTree.TypeSource")
        };

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoControls();
        ShowProfilePage();
        StatusText.Text = _userProfile.IsComplete()
            ? L("Status.ProfileOpened")
            : L("Status.ProfileIncomplete");
    }

    private void ProfileBlinkTimer_Tick(object? sender, EventArgs e)
    {
        ProfileButton.Opacity = ProfileButton.Opacity < 0.75 ? 1.0 : 0.48;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _executorSpeechPresentationActive)
        {
            e.Handled = true;
            CancelExecutorSpeech(revealFullText: true, "keyboard_skip");
            return;
        }

        if (e.Key == System.Windows.Input.Key.Escape && _coreSpeechPresentationActive)
        {
            e.Handled = true;
            CancelCoreSpeech(revealFullText: true, "keyboard_skip");
            return;
        }

        if (e.Key == System.Windows.Input.Key.Escape && LiteraryPage.Visibility == Visibility.Visible)
        {
            LiteraryPage.GoBack();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Escape && TryHandleImageAnalysisEscape())
        {
            e.Handled = true;
            return;
        }

        if (e.Key != System.Windows.Input.Key.F12)
        {
            return;
        }

        e.Handled = true;
        OpenDebugChatWindow();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmKeyDown && wParam.ToInt32() == VkF12)
        {
            handled = true;
            OpenDebugChatWindow();
        }

        return IntPtr.Zero;
    }

    private void OpenDebugChatWindow()
    {
        try
        {
            if (_debugChatWindow is not null)
            {
                _debugChatWindow.Activate();
                return;
            }

            _debugChatWindow = new DebugChatWindow(_localizationService, _storageSettings, _userContextService, _isDarkTheme)
            {
                Owner = this
            };
            _debugChatWindow.CoreMemoryStatusChanged += DebugChatWindow_CoreMemoryStatusChanged;
            _debugChatWindow.Closed += (_, _) =>
            {
                _debugChatWindow = null;
                UpdateCoreMemoryIndicator(CoreMemoryStatus.Inactive());
            };
            _debugChatWindow.Show();
            StatusText.Text = L("Status.DebugChatOpened");
        }
        catch (Exception)
        {
            _debugChatWindow = null;
            StatusText.Text = L("Status.DebugChatOpenFailed");
        }
    }

    private void DebugChatWindow_CoreMemoryStatusChanged(CoreMemoryStatus status)
    {
        Dispatcher.Invoke(() => UpdateCoreMemoryIndicator(status));
    }

    private void UpdateCoreMemoryIndicator(CoreMemoryStatus status)
    {
        _coreMemoryStatus = status;
        CoreMemoryProgressBar.IsIndeterminate = status.IsCompressing;
        CoreMemoryProgressBar.Value = status.IsCompressing ? 0 : status.FillPercent;
        CoreMemoryIndicatorPanel.Opacity = status.IsActive ? 1.0 : 0.42;
        CoreMemoryIconText.Text = status.IsNearFull ? "🤯" : "🧠";
        CoreMemoryIconText.Foreground = status.IsActive
            ? status.IsNearFull
                ? new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString("#F97316"))
                : new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString("#38BDF8"))
            : (Media.Brush)Resources["TextSecondaryBrush"];

        if (!status.IsActive)
        {
            CoreMemoryIndicatorPanel.ToolTip = L("CoreMemory.TooltipInactive");
            return;
        }

        CoreMemoryIndicatorPanel.ToolTip = status.IsCompressing
            ? L("CoreMemory.TooltipCompressing")
            : status.HasCompressedSummary
                ? L("CoreMemory.TooltipCompressed")
                : L("CoreMemory.TooltipReady");
    }

    private void SetBrush(string resourceKey, string color)
    {
        Resources[resourceKey] = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(color));
    }

    private static string GetAppVersion()
    {
        return typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
    }

    private void InitializeLocalization()
    {
        _appSettings = _appSettingsStore.LoadOrCreate();
        _appSettings.CoreVoice ??= new CoreVoiceSettings();
        _appSettings.CoreAutonomy ??= new CoreAutonomySettings();
        _appSettings.ModelDownloads ??= new ModelDownloadSettings();
        _appSettings.FileViewer ??= new FileViewerSettings();
        _appSettings.Interface ??= new InterfaceSettings();
        _appSettings.Interface.LastWindowPlacement ??= new RememberedWindowPlacement();
        _executorWorkflowService.ConfigureAutonomy(
            _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds);
        if (!_appSettings.LanguageWasChosen)
        {
            var windowsLanguage = LocalizationService.GetWindowsLanguageCode();
            if (windowsLanguage == "ru")
            {
                _appSettings.LanguageCode = "ru";
            }
            else if (_localizationService.HasLanguage(windowsLanguage))
            {
                _localizationService.Load(windowsLanguage);
                var useWindowsLanguage = System.Windows.MessageBox.Show(
                    L("Dialog.UseWindowsLanguage"),
                    L("Dialog.LanguageTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;

                _appSettings.LanguageCode = useWindowsLanguage ? windowsLanguage : "ru";
            }
            else
            {
                _appSettings.LanguageCode = "ru";
            }

            _appSettings.LanguageWasChosen = true;
            _appSettingsStore.Save(_appSettings);
        }

        _localizationService.Load(_appSettings.LanguageCode);
        PopulateLanguageComboBox();
    }

    private string L(string key) => _localizationService.T(key);

    private string LF(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, L(key), args);

    private void ApplyLocalization()
    {
        AboutButton.ToolTip = L("About.Title");
        System.Windows.Automation.AutomationProperties.SetName(AboutButton, L("About.Title"));
        ApplicationUpdatesButton.Content = L("Updates.Title");
        ApplicationUpdateNotice.Content = L("Updates.Available");
        ComponentLicensesButton.Content = L("licenses.title");
        HeaderProductNameText.Text = L("App.ProductName");
        HeaderSubtitleText.Text = L("App.Subtitle");
        ProfileButton.ToolTip = L("Header.ProfileTooltip");
        SettingsButton.ToolTip = L("Header.SettingsTooltip");
        GlobalFileViewerButton.ToolTip = L("Header.FileViewerTooltip");
        AutomationProperties.SetName(GlobalFileViewerButton, L("Header.FileViewerTooltip"));
        SessionTreeButton.ToolTip = L("Header.SessionTreeTooltip");
        System.Windows.Automation.AutomationProperties.SetName(
            SessionTreeButton,
            L("Header.SessionTreeTooltip"));

        HomeWelcomeTitleText.Text = L("Home.Welcome");
        HomeHeadlineText.Text = L("Home.Headline");
        HomeDescriptionText.Text = L("Home.Description");
        WhatWillBeConfiguredTitleText.Text = L("Home.WhatWillBeConfigured");
        ModelsStorageTitleText.Text = L("Home.ModelsStorage");
        ResultsStorageTitleText.Text = L("Home.ResultsStorage");
        ComputerPassportTitleText.Text = L("Home.ComputerPassport");

        SetupTitleText.Text = L("Setup.Title");
        SetupDescriptionText.Text = L("Setup.Description");
        SetupPriorityHintText.Text = L("Setup.PriorityHint");
        SetupModelsTitleText.Text = L("Home.ModelsStorage");
        SetupModelsHelpText.Text = L("Setup.StorageHelp");
        SetupResultsTitleText.Text = L("Home.ResultsStorage");
        SetupResultsHelpText.Text = L("Setup.StorageHelp");
        SetupPassportTitleText.Text = L("Home.ComputerPassport");

        ModelsAddressLabelText.Text = L("Setup.AddressLabel");
        ResultsAddressLabelText.Text = L("Setup.AddressLabel");
        ModelsLimitLabelText.Text = L("Setup.LocationLimitLabel");
        ResultsLimitLabelText.Text = L("Setup.LocationLimitLabel");
        ModelsPathInput.ToolTip = L("Setup.AddressTooltip");
        ResultsPathInput.ToolTip = L("Setup.AddressTooltip");
        ModelsLocationLimitInput.ToolTip = L("Setup.LocationLimitTooltip");
        ResultsLocationLimitInput.ToolTip = L("Setup.LocationLimitTooltip");

        BrowseModelsLocationButton.Content = L("Setup.Browse");
        BrowseResultsLocationButton.Content = L("Setup.Browse");
        AddModelsLocationButton.Content = L("Setup.Add");
        AddResultsLocationButton.Content = L("Setup.Add");
        MoveModelsLocationUpButton.Content = L("Setup.MoveUp");
        MoveResultsLocationUpButton.Content = L("Setup.MoveUp");
        MoveModelsLocationDownButton.Content = L("Setup.MoveDown");
        MoveResultsLocationDownButton.Content = L("Setup.MoveDown");
        RemoveModelsLocationButton.Content = L("Setup.Delete");
        RemoveResultsLocationButton.Content = L("Setup.Delete");
        ModelsTotalLimitLabelText.Text = L("Setup.TotalLimitGb");
        ResultsTotalLimitLabelText.Text = L("Setup.TotalLimitGb");
        ModelsAllowOverflowCheckBox.Content = L("Setup.AllowTemporaryOverflow");
        ResultsAllowOverflowCheckBox.Content = L("Setup.AllowTemporaryOverflow");
        ModelsPlusGbText.Text = L("Setup.PlusGb");
        ResultsPlusGbText.Text = L("Setup.PlusGb");
        BackToStartButton.Content = L("Setup.Back");
        SaveStorageSettingsButton.Content = L("Setup.Save");

        SettingsTitleText.Text = L("Settings.Title");
        SettingsDescriptionText.Text = L("Settings.Description");
        SettingsLanguageTitleText.Text = L("Settings.LanguageTitle");
        SettingsLanguageHelpText.Text = L("Settings.LanguageHelp");
        SettingsLanguageLabelText.Text = L("Settings.LanguageLabel");
        SettingsLocalizationFolderText.Text = LF("Settings.LocalizationFolder", AppDataPaths.LocalizationDirectory);
        ApplyInterfaceLocalization();
        SettingsCoreVoiceTitleText.Text = L("Settings.CoreVoiceTitle");
        SettingsCoreVoiceHelpText.Text = L("Settings.CoreVoiceHelp");
        SettingsCoreVoiceProviderText.Text = L("Settings.CoreVoiceProvider");
        CoreVoiceEnabledCheckBox.Content = L("Settings.CoreVoiceEnabled");
        SettingsCoreVoiceVolumeText.Text = L("Settings.CoreVoiceVolume");
        SettingsCoreVoiceRateText.Text = L("Settings.CoreVoiceRate");
        CoreVoiceTestButton.Content = L("Settings.CoreVoiceTest");
        SettingsCoreAutonomyTitleText.Text = L("Settings.CoreAutonomyTitle");
        SettingsCoreAutonomyHelpText.Text = L("Settings.CoreAutonomyHelp");
        SettingsCoreAutonomyTimeText.Text = L("Settings.CoreAutonomyTime");
        UpdateCoreAutonomyTimeText();
        SettingsModelDownloadsTitleText.Text = L("Settings.ModelDownloadsTitle");
        SettingsModelDownloadsHelpText.Text = L("Settings.ModelDownloadsHelp");
        SettingsModelDownloadConnectionsText.Text = L("Settings.ModelDownloadConnections");
        PopulateModelDownloadConnectionsComboBox();
        RefreshManagedModelLocalization();
        ProcessingComponentsExpander.Header = L("Components.ProcessingTitle");
        ProcessingComponentsHelpText.Text = L("Components.ProcessingHelp");
        ViewerComponentsExpander.Header = L("Components.ViewersTitle");
        ViewerComponentsHelpText.Text = L("Components.ViewersHelp");
        PreferInternalViewersCheckBox.Content = L("Components.PreferInternal");
        DownloadSelectedComponentsButton.Content = L("Components.DownloadSelected");
        DownloadAllComponentsButton.Content = L("Components.DownloadAll");
        VerifyInstalledComponentsButton.Content = L("Components.VerifyInstalled");
        DownloadSelectedViewersButton.Content = L("Components.DownloadSelectedViewers");
        DownloadAllViewersButton.Content = L("Components.DownloadAllViewers");
        VerifyInstalledViewersButton.Content = L("Components.VerifyViewers");
        PopulateCoreVoiceProviderComboBox();
        BackFromSettingsButton.Content = L("Settings.Back");

        ProfileTitleText.Text = L("Profile.Title");
        ProfileDescriptionText.Text = L("Profile.Description");
        ProfileIdentityTitleText.Text = L("Profile.IdentityTitle");
        ProfileIdentityHelpText.Text = L("Profile.IdentityHelp");
        ProfileLocationTitleText.Text = L("Profile.LocationTitle");
        ProfileLocationHelpText.Text = L("Profile.LocationHelp");
        ProfileCityLabelText.Text = L("Profile.City");
        ProfileRegionLabelText.Text = L("Profile.Region");
        ProfileCountryLabelText.Text = L("Profile.Country");
        ProfileTimezoneLabelText.Text = L("Profile.Timezone");
        ProfileAnswerPreferencesTitleText.Text = L("Profile.AnswerPreferencesTitle");
        ProfileAnswerPreferencesHelpText.Text = L("Profile.AnswerPreferencesHelp");
        PreferenceConciseCheckBox.Content = L("Profile.PreferenceConcise");
        PreferenceDetailedCheckBox.Content = L("Profile.PreferenceDetailed");
        PreferenceSimpleCheckBox.Content = L("Profile.PreferenceSimple");
        PreferenceStepsCheckBox.Content = L("Profile.PreferenceSteps");
        PreferenceExamplesCheckBox.Content = L("Profile.PreferenceExamples");
        PreferenceSourcesCheckBox.Content = L("Profile.PreferenceSources");
        PreferenceRisksCheckBox.Content = L("Profile.PreferenceRisks");
        ProfileWorkloadTitleText.Text = L("Profile.WorkloadTitle");
        ProfileWorkloadHelpText.Text = L("Profile.WorkloadHelp");
        WorkloadLightTitleText.Text = L("Profile.WorkloadLightTitle");
        WorkloadLightDescriptionText.Text = L("Profile.WorkloadLightDescription");
        WorkloadBalancedTitleText.Text = L("Profile.WorkloadBalancedTitle");
        WorkloadBalancedDescriptionText.Text = L("Profile.WorkloadBalancedDescription");
        WorkloadExtremeTitleText.Text = L("Profile.WorkloadExtremeTitle");
        WorkloadExtremeDescriptionText.Text = L("Profile.WorkloadExtremeDescription");
        BackFromProfileButton.Content = L("Settings.Back");
        SaveProfileButton.Content = L("Setup.Save");
        ProfileReminderTitleText.Text = L("Profile.ReminderTitle");
        ProfileReminderDescriptionText.Text = L("Profile.ReminderDescription");
        BackFromProfileReminderButton.Content = L("Settings.Back");
        ContinueWithoutProfileButton.Content = L("Profile.ContinueWithoutProfile");
        FillProfileFromReminderButton.Content = L("Profile.FillProfile");

        WorkStartTitleText.Text = L("WorkStart.Title");
        WorkStartDescriptionText.Text = L("WorkStart.Description");
        NewProjectTitleText.Text = L("WorkStart.NewProject");
        ReasoningModeTitleText.Text = L("WorkStart.ReasoningTitle");
        ReasoningModeDescriptionText.Text = L("WorkStart.ReasoningDescription");
        SelectReasoningModeButton.Content = L("WorkStart.SelectMode");
        PreviousWorkHeaderText.Text = L("WorkStart.PreviousWork");
        PreviousWorkEmptyText.Text = L("WorkStart.Empty");
        ClearPreviousWorkSelectionButton.Content = L("WorkStart.ClearSelection");
        DeletePreviousWorkSelectionButton.Content = L("WorkStart.DeleteSelected");
        BackFromWorkStartButton.Content = L("Settings.Back");
        RefreshImageAnalysisLocalization();
        RefreshLiteraryLocalization();
        ChoiceScenarioTitleText.Text = L("ChoiceScenario.Title");
        ChoiceScenarioDescriptionText.Text = L("ChoiceScenario.Description");
        ChoiceScenarioCoreThoughtTitleText.Text = L("ChoiceScenario.CoreThoughtTitle");
        ChoiceSessionFilesTitleText.Text = L("ChoiceScenario.Files.PanelTitle");
        ExecutorSessionFilesTitleText.Text = L("ChoiceScenario.Files.PanelTitle");
        UpdateCoreVoiceControls();
        ChoiceCustomOptionButton.Content = L("ChoiceScenario.CustomOption");
        ChoiceCustomInputHelpText.Text = L("ChoiceScenario.CustomInputHelp");
        ChoiceCustomSubmitButton.Content = L("ChoiceScenario.AcceptCustom");
        ChoiceGoFinalButton.Content = L("ChoiceScenario.GoFinal");
        ChoiceScenarioSummaryTitleText.Text = L("ChoiceScenario.TaskCardTitle");
        ExecutionRouteTitleText.Text = L("ExecutionRoute.Title");
        ExecutorPrepareButton.Content = L("Executor.Prepare");
        ExecutorContinueAvailableButton.Content = L("Executor.ContinueAvailable");
        ExecutorDownloadTitleText.Text = L("Executor.DownloadTitle");
        ExecutorCancelDownloadButton.Content = L("ChoiceScenario.Cancel");
        ExecutorConfirmDownloadButton.Content = L("Executor.DownloadAndRun");
        ExecutorResultTitleText.Text = L("Executor.ResultTitle");
        ExecutorCandidateChoiceTitleText.Text = L("Executor.ChoiceTitle");
        ExecutorThoughtTitleText.Text = L("Executor.ThoughtTitle");
        ExecutorCustomInputHelpText.Text = L("Executor.CustomInputHelp");
        ExecutorCustomSubmitButton.Content = L("ChoiceScenario.AcceptCustom");
        ExecutorRequestResultButton.Content = L("Executor.RequestResult");
        ExecutorFinishSessionButton.Content = L("Executor.FinishSession");
        ExecutorFinishSessionDockButton.Content = L("Executor.FinishSession");
        ExecutorLivePreviewTitleText.Text = L("Executor.LivePreviewTitle");
        ExecutorLivePreviewHintText.Text = L("Executor.LivePreviewHint");
        ExecutorStageRecommendationText.Text = L("Executor.StageRecommended");
        UpdateExecutorStageControls();
        UpdateExecutorVoiceControls();
        BackFromChoiceScenarioButton.Content = L("Settings.Back");
        CancelChoiceScenarioButton.Content = L("ChoiceScenario.Cancel");
        RefreshSessionFileCards();
        if (_currentChoiceScenarioStep?.TaskCard is { } localizedTaskCard)
        {
            RenderExecutionRoute(localizedTaskCard);
            if (_selectedExecutorCandidate is not null)
            {
                UpdateExecutorPreparationActions(localizedTaskCard);
            }
        }

        DownloadCoreModelButton.Content = L("CoreModel.Download");
        OpenSetupFromCoreModelButton.Content = L("CoreModel.OpenSetup");
        PostponeCoreModelButton.Content = L("CoreModel.Later");
        PauseCoreModelDownloadButton.Content = L("CoreModel.Pause");
        CancelCoreModelDownloadButton.Content = L("CoreModel.Cancel");
        UpdateCoreMemoryIndicator(_coreMemoryStatus);

        ApplyTheme();
        UpdatePrimaryActionButton();
        UpdateStorageSteps();
        if (_lastPassport is not null)
        {
            UpdateComputerPassportStep(_lastPassport);
            PassportSummaryText.Text = BuildPassportSummary(_lastPassport);
            PassportPathText.Text = LF("Setup.PassportPath", AppDataPaths.ComputerPassportPath);
        }

        if (WelcomePage.Visibility == Visibility.Visible)
        {
            UpdateWelcomeStatus();
        }
    }

    private void PopulateLanguageComboBox()
    {
        _isApplyingLanguageSelection = true;
        var languages = _localizationService.GetAvailableLanguages();
        LanguageComboBox.ItemsSource = languages;
        LanguageComboBox.SelectedItem = languages.FirstOrDefault(language =>
            string.Equals(language.Code, _localizationService.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));
        _isApplyingLanguageSelection = false;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLanguageSelection || LanguageComboBox.SelectedItem is not LanguageOption language)
        {
            return;
        }

        _appSettings.LanguageCode = language.Code;
        _appSettings.LanguageWasChosen = true;
        _appSettingsStore.Save(_appSettings);
        _localizationService.Load(language.Code);
        PopulateLanguageComboBox();
        ApplyLocalization();
        RefreshComponentCatalogUi();
        RefreshPreviousSessions();
        if (ImageAnalysisWorkspacePage.Visibility == Visibility.Visible)
        {
            StopImageAnalysisSpeechSession();
            RefreshImageAnalysisSpeechUi();
            BeginImageAnalysisRuntimePreparation();
        }
        StatusText.Text = L("Status.LanguageSaved");
    }

    private void LoadCoreVoiceSettingsIntoControls()
    {
        _isApplyingCoreVoiceSettings = true;
        try
        {
            _appSettings.CoreVoice ??= new CoreVoiceSettings();
            if (_appSettings.CoreVoice.Provider is not CoreVoiceSettings.EspeakProvider
                and not CoreVoiceSettings.RhVoiceProvider)
            {
                _appSettings.CoreVoice.Provider = CoreVoiceSettings.EspeakProvider;
            }

            PopulateCoreVoiceProviderComboBox();
            CoreVoiceEnabledCheckBox.IsChecked = _appSettings.CoreVoice.Enabled;
            CoreVoiceVolumeSlider.Value = Math.Clamp(_appSettings.CoreVoice.Volume, 0, 200);
            CoreVoiceRateSlider.Value = Math.Clamp(_appSettings.CoreVoice.Rate, 80, 240);
        }
        finally
        {
            _isApplyingCoreVoiceSettings = false;
        }
    }

    private void PopulateCoreVoiceProviderComboBox()
    {
        var wasApplying = _isApplyingCoreVoiceSettings;
        _isApplyingCoreVoiceSettings = true;
        try
        {
            var providers = new[]
            {
                new CoreVoiceProviderOption(CoreVoiceSettings.EspeakProvider, L("Settings.CoreVoiceProviderEspeak")),
                new CoreVoiceProviderOption(CoreVoiceSettings.RhVoiceProvider, L("Settings.CoreVoiceProviderRhVoice"))
            };
            CoreVoiceProviderComboBox.ItemsSource = providers;
            CoreVoiceProviderComboBox.SelectedItem = providers.First(provider =>
                string.Equals(provider.Id, _appSettings.CoreVoice.Provider, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _isApplyingCoreVoiceSettings = wasApplying;
        }
    }

    private void CoreVoiceProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingCoreVoiceSettings
            || CoreVoiceProviderComboBox.SelectedItem is not CoreVoiceProviderOption provider)
        {
            return;
        }

        CancelCoreSpeech(revealFullText: true, "voice_provider_changed");
        _appSettings.CoreVoice.Provider = provider.Id;
        _appSettingsStore.Save(_appSettings);
        UpdateCoreVoiceControls();
        StatusText.Text = LF("Status.CoreVoiceProviderSaved", provider.DisplayName);
    }

    private void CoreVoiceSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingCoreVoiceSettings)
        {
            return;
        }

        _appSettings.CoreVoice.Enabled = CoreVoiceEnabledCheckBox.IsChecked == true;
        _appSettingsStore.Save(_appSettings);
        if (!_appSettings.CoreVoice.Enabled)
        {
            CancelCoreSpeech(revealFullText: true, "settings_disabled");
        }

        UpdateCoreVoiceControls();
    }

    private void CoreVoiceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingCoreVoiceSettings)
        {
            return;
        }

        _appSettings.CoreVoice.Volume = (int)Math.Round(CoreVoiceVolumeSlider.Value);
        _appSettings.CoreVoice.Rate = (int)Math.Round(CoreVoiceRateSlider.Value);
        _appSettingsStore.Save(_appSettings);
        UpdateCoreVoiceControls();
    }

    private void LoadCoreAutonomySettingsIntoControls()
    {
        _isApplyingCoreAutonomySettings = true;
        try
        {
            _appSettings.CoreAutonomy ??= new CoreAutonomySettings();
            CoreAutonomyTimeSlider.Value =
                _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds;
            UpdateCoreAutonomyTimeText();
        }
        finally
        {
            _isApplyingCoreAutonomySettings = false;
        }
    }

    private void CoreAutonomyTimeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingCoreAutonomySettings)
        {
            return;
        }

        _appSettings.CoreAutonomy ??= new CoreAutonomySettings();
        _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds =
            (int)Math.Round(CoreAutonomyTimeSlider.Value);
        _appSettingsStore.Save(_appSettings);
        _executorWorkflowService.ConfigureAutonomy(
            _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds);
        UpdateCoreAutonomyTimeText();
        StatusText.Text = LF(
            "Status.CoreAutonomySaved",
            FormatAutonomyDuration(
                _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds));
    }

    private void UpdateCoreAutonomyTimeText()
    {
        if (CoreAutonomyTimeValueText is null)
        {
            return;
        }

        var seconds = _appSettings.CoreAutonomy?.MaximumIndependentSearchSeconds
            ?? CoreAutonomySettings.DefaultSeconds;
        CoreAutonomyTimeValueText.Text = FormatAutonomyDuration(seconds);
    }

    private void LoadModelDownloadSettingsIntoControls()
    {
        _isApplyingModelDownloadSettings = true;
        try
        {
            _appSettings.ModelDownloads ??= new ModelDownloadSettings();
            PopulateModelDownloadConnectionsComboBox();
            ApplyModelDownloadSettings();
        }
        finally
        {
            _isApplyingModelDownloadSettings = false;
        }
    }

    private void PopulateModelDownloadConnectionsComboBox()
    {
        if (ModelDownloadConnectionsComboBox is null)
        {
            return;
        }
        var wasApplying = _isApplyingModelDownloadSettings;
        _isApplyingModelDownloadSettings = true;
        try
        {
            var options = new[]
            {
                new ModelDownloadConnectionOption(0, L("Settings.ModelDownloadConnectionsAuto")),
                new ModelDownloadConnectionOption(1, L("Settings.ModelDownloadConnectionsSingle")),
                new ModelDownloadConnectionOption(2, LF("Settings.ModelDownloadConnectionsFixed", 2)),
                new ModelDownloadConnectionOption(4, LF("Settings.ModelDownloadConnectionsFixed", 4)),
                new ModelDownloadConnectionOption(8, LF("Settings.ModelDownloadConnectionsFixed", 8))
            };
            ModelDownloadConnectionsComboBox.ItemsSource = options;
            var selected = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0;
            ModelDownloadConnectionsComboBox.SelectedItem = options.First(option => option.Value == selected);
        }
        finally
        {
            _isApplyingModelDownloadSettings = wasApplying;
        }
    }

    private void ModelDownloadConnectionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingModelDownloadSettings
            || ModelDownloadConnectionsComboBox.SelectedItem is not ModelDownloadConnectionOption option)
        {
            return;
        }
        _appSettings.ModelDownloads ??= new ModelDownloadSettings();
        _appSettings.ModelDownloads.MaximumParallelConnections = option.Value;
        _appSettingsStore.Save(_appSettings);
        ApplyModelDownloadSettings();
        StatusText.Text = LF("Status.ModelDownloadConnectionsSaved", option.DisplayName);
    }

    private string FormatAutonomyDuration(int seconds)
    {
        if (seconds < 60)
        {
            return LF("Settings.CoreAutonomySeconds", seconds);
        }

        var minutes = seconds / 60;
        var remainder = seconds % 60;
        return remainder == 0
            ? LF("Settings.CoreAutonomyMinutes", minutes)
            : LF("Settings.CoreAutonomyMinutesSeconds", minutes, remainder);
    }

    private async void CoreVoiceTestButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCoreSpeech(revealFullText: true, "settings_test");
        CoreVoiceTestButton.IsEnabled = false;
        try
        {
            var request = new CoreSpeechRequest(
                [new CoreSpeechSegment("test", L("Settings.CoreVoiceTestPhrase"))],
                _appSettings.LanguageCode,
                _appSettings.CoreVoice,
                "settings_test");
            var progress = new Progress<CoreSpeechProgress>(_ => { });
            var result = await _coreSpeechCoordinator.PresentAsync(
                request,
                progress,
                _coreSessionLog,
                CancellationToken.None);
            StatusText.Text = result.Completed
                ? L("Status.CoreVoiceTestCompleted")
                : L("Status.CoreVoiceUnavailable");
        }
        finally
        {
            CoreVoiceTestButton.IsEnabled = true;
        }
    }

    private void UpdateCoreVoiceControls()
    {
        if (!IsInitialized)
        {
            return;
        }

        var enabled = _appSettings.CoreVoice.Enabled;
        CoreVoiceVolumeSlider.IsEnabled = enabled;
        CoreVoiceRateSlider.IsEnabled = enabled;
        CoreVoiceVolumeValueText.Text = _appSettings.CoreVoice.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CoreVoiceRateValueText.Text = _appSettings.CoreVoice.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CoreVoiceToggleButton.Content = enabled ? "🔊" : "🔇";
        CoreVoiceToggleButton.ToolTip = enabled
            ? L("ChoiceScenario.CoreVoiceDisable")
            : L("ChoiceScenario.CoreVoiceEnable");
        CoreVoiceProviderComboBox.IsEnabled = enabled;
        CoreVoiceProviderComboBox.ToolTip = string.Equals(
                _appSettings.CoreVoice.Provider,
                CoreVoiceSettings.RhVoiceProvider,
                StringComparison.OrdinalIgnoreCase)
            && !_coreSpeechCoordinator.IsRhVoiceAvailable
                ? L("Settings.CoreVoiceRhVoiceUnavailable")
                : null;
        CoreVoiceTestButton.ToolTip = _coreSpeechCoordinator.IsAvailable
            ? L("Settings.CoreVoiceTestTooltip")
            : L("Settings.CoreVoiceUnavailable");
    }

    private void InitializeAppData()
    {
        try
        {
            _appState = _appStateStore.LoadOrCreate();
            _storageSettings = _storageSettingsStore.LoadOrCreate();
            _userProfile = _userProfileStore.LoadOrCreate();
            LoadProfileIntoControls();
            UpdateProfileButtonState();
            StartCoreSessionLog();
            _ = InitializeUserContextAsync();
            var passport = _computerPassportService.RegeneratePassport();
            _lastPassport = passport;
            SavePassportState(passport);
            UpdateComputerPassportStep(passport);
            LoadStorageSettingsIntoControls();
            UpdateStorageSteps();
            UpdateWelcomeStatus();
            EvaluateCoreModelOnStartup();
        }
        catch
        {
            StatusText.Text = L("Status.PassportMissing");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateLifetime.Cancel();
        _updateHttp.Dispose();
        try
        {
            CancelCoreSpeech(revealFullText: false, "app_closed");
            _choiceScenarioCts?.Cancel();
            _imageAnalysisBundleOperationCts?.Cancel();
            _managedModelOperationCts?.Cancel();
            _catalogStartupCts.Cancel();
            _choiceScenarioRuntimeService?.Dispose();
            DisposeImageAnalysisLiteraryRuntime();
            PauseActiveSession("app_closed");
            CancelExecutorSession("app_closed");
            _executorWorkflowService.Dispose();
            _choiceScenarioLog?.Write("scenario_session_end", new { Reason = "app_closed" });
            _choiceScenarioLog?.Dispose();
            _coreSessionLog?.Write("session_end");
            _coreSessionLog?.Dispose();
            _coreSpeechCoordinator.Dispose();
            _executorSpeechCoordinator.Dispose();
            _catalogStartupCts.Dispose();
        }
        finally
        {
            EndImageInputSession();
            _imageAssets?.Dispose();
            _imageInputHttp?.Dispose();
            base.OnClosed(e);
        }
    }

    private void StartCoreSessionLog()
    {
        try
        {
            _coreSessionLog = JsonlSessionLog.CreateCore(_storageSettings);
            _coreSessionLog.Write("session_start", new
            {
                AppVersion = GetAppVersion(),
                _coreSessionLog.FilePath
            });
            _coreSessionLog.Write("context_snapshot", _userContextService.CreateSnapshot());
        }
        catch
        {
            _coreSessionLog = null;
        }
    }

    private async Task InitializeUserContextAsync()
    {
        try
        {
            await _userContextService.InitializeAsync(CancellationToken.None);
            _coreSessionLog?.Write("context_snapshot", _userContextService.CreateSnapshot());
        }
        catch
        {
            // User context must never block the app startup.
        }
    }

    private async Task RefreshModelCatalogOnStartupAsync()
    {
        await Task.Yield();
        try
        {
            _coreSessionLog?.Write("catalog_startup_sync_started", new
            {
                AppDataPaths.HuggingFaceCatalogPath,
                AppDataPaths.HuggingFaceCatalogSeedPath
            });
            var result = await _catalogStartupService.SynchronizeIfDueAsync(_catalogStartupCts.Token);
            _coreSessionLog?.Write("catalog_startup_sync_finished", result);
        }
        catch (OperationCanceledException)
        {
            _coreSessionLog?.Write("catalog_startup_sync_cancelled");
        }
        catch (Exception ex)
        {
            _coreSessionLog?.Write("catalog_startup_sync_error", new
            {
                ErrorType = ex.GetType().FullName,
                ex.Message
            });
        }
    }

    private void OpenSetupWindow(bool regeneratePassport)
    {
        try
        {
            var passport = regeneratePassport
                ? _computerPassportService.RegeneratePassport()
                : _computerPassportService.EnsurePassport();

            _lastPassport = passport;
            SavePassportState(passport);
            UpdateComputerPassportStep(passport);
            LoadStorageSettingsIntoControls();
            ShowSetupPage(passport);

            StatusText.Text = regeneratePassport
                ? L("Status.PassportRegenerated")
                : L("Status.SetupOpened");
        }
        catch
        {
            StatusText.Text = L("Status.SetupOpenFailed");
        }
    }

    private void BrowseModelsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(ModelsPathInput);
    }

    private void BrowseResultsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(ResultsPathInput);
    }

    private void AddModelsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        AddOrUpdateLocation(_storageSettings.Models, ModelsLocationList, ModelsPathInput, ModelsLocationLimitInput);
        RefreshStorageLists();
    }

    private void AddResultsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        AddOrUpdateLocation(_storageSettings.Results, ResultsLocationList, ResultsPathInput, ResultsLocationLimitInput);
        RefreshStorageLists();
    }

    private void RemoveModelsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedLocation(_storageSettings.Models, ModelsLocationList, ModelsPathInput, ModelsLocationLimitInput);
        RefreshStorageLists();
    }

    private void RemoveResultsLocationButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedLocation(_storageSettings.Results, ResultsLocationList, ResultsPathInput, ResultsLocationLimitInput);
        RefreshStorageLists();
    }

    private void MoveModelsLocationUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedLocation(_storageSettings.Models, ModelsLocationList, direction: -1);
        RefreshStorageLists();
    }

    private void MoveModelsLocationDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedLocation(_storageSettings.Models, ModelsLocationList, direction: 1);
        RefreshStorageLists();
    }

    private void MoveResultsLocationUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedLocation(_storageSettings.Results, ResultsLocationList, direction: -1);
        RefreshStorageLists();
    }

    private void MoveResultsLocationDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedLocation(_storageSettings.Results, ResultsLocationList, direction: 1);
        RefreshStorageLists();
    }

    private void ModelsLocationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FillLocationInputs(_storageSettings.Models, ModelsLocationList, ModelsPathInput, ModelsLocationLimitInput);
    }

    private void ResultsLocationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FillLocationInputs(_storageSettings.Results, ResultsLocationList, ResultsPathInput, ResultsLocationLimitInput);
    }

    private void SaveStorageSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveStorageSettingsFromControls();
        _storageSettingsStore.Save(_storageSettings);
        _appState.HasCompletedSetup = HasRequiredStorageSettings();
        _appStateStore.Save(_appState);
        UpdatePrimaryActionButton();
        UpdateStorageSteps();
        StatusText.Text = _appState.HasCompletedSetup
            ? LF("Status.StorageSavedComplete", AppDataPaths.StorageSettingsPath)
            : L("Status.StorageSavedIncomplete");

        if (_appState.HasCompletedSetup)
        {
            _isCoreModelPromptPostponed = false;
            EvaluateCoreModelAfterStorageSave();
        }
    }

    private void BackToStartButton_Click(object sender, RoutedEventArgs e)
    {
        HideImageAnalysisPages();
        SetupPage.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        UpdateWelcomeStatus();
    }

    private void BackFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var returnPage = _settingsReturnPage;
        var returnStatusText = _settingsReturnStatusText;
        _settingsReturnPage = null;
        _settingsReturnStatusText = null;

        HideImageAnalysisPages();
        HideStandardPages();

        if (returnPage is null)
        {
            WelcomePage.Visibility = Visibility.Visible;
            UpdateWelcomeStatus();
            return;
        }

        returnPage.Visibility = Visibility.Visible;
        if (returnStatusText is not null)
        {
            StatusText.Text = returnStatusText;
        }
    }

    private void BackFromProfileButton_Click(object sender, RoutedEventArgs e)
    {
        HideImageAnalysisPages();
        ProfilePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Visible;
        UpdateWelcomeStatus();
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        SaveProfileFromControls();
        _userContextService.UpdateProfile(_userProfile);
        UpdateProfileButtonState();
        StatusText.Text = _userProfile.IsComplete()
            ? L("Status.ProfileSavedComplete")
            : L("Status.ProfileSavedIncomplete");
    }

    private void FillProfileFromReminderButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoControls();
        ShowProfilePage();
        StatusText.Text = L("Status.ProfileIncomplete");
    }

    private void ContinueWithoutProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkStartPage();
        StatusText.Text = L("Status.WorkStartOpenedWithoutProfile");
    }

    private void BackFromProfileReminderButton_Click(object sender, RoutedEventArgs e)
    {
        HideImageAnalysisPages();
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Visible;
        UpdateWelcomeStatus();
    }

    private void BackFromWorkStartButton_Click(object sender, RoutedEventArgs e)
    {
        HideImageAnalysisPages();
        WorkStartPage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Visible;
        UpdateWelcomeStatus();
    }

    private void SelectReasoningModeButton_Click(object sender, RoutedEventArgs e)
    {
        StartChoiceScenario();
        StatusText.Text = L("Status.ChoiceScenarioOpened");
    }

    private void ChoiceOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choiceScenarioRequestInProgress
            || sender is not System.Windows.Controls.Button button
            || button.Tag is not ChoiceScenarioOption option)
        {
            return;
        }

        ChoiceCustomInputPanel.Visibility = Visibility.Collapsed;
        if (string.Equals(
            _currentChoiceScenarioStep?.StepType,
            ChoiceScenarioService.FileSetupStepType,
            StringComparison.Ordinal))
        {
            if (string.Equals(option.Id, ChoiceScenarioService.SelectFilesOptionId, StringComparison.Ordinal)
                && AddSessionFilesFromPicker() <= 0)
            {
                StatusText.Text = L("Status.ChoiceScenarioFileSelectionCancelled");
                return;
            }

            if (string.Equals(option.Id, ChoiceScenarioService.NoFilesOptionId, StringComparison.Ordinal))
            {
                _sessionFileManifestService.SetNoFilesPlanned(GetActiveFileManifest());
            }
            else if (!string.Equals(option.Id, ChoiceScenarioService.SelectFilesOptionId, StringComparison.Ordinal))
            {
                return;
            }

            RefreshSessionFileCards();
            ApplyFileManifestToCoreProfile();
            WriteFileManifestEvent("scenario_file_setup_completed");
            var budgetStep = _choiceScenarioService.CreateBudgetStep(L);
            _choiceScenarioState.AddStep(budgetStep, consumedAnswer: false);
            RenderChoiceScenarioStep(budgetStep);
            _choiceScenarioLog?.Write("scenario_parsed_step", budgetStep);
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false,
                pendingCoreRequestConsumesAnswer: false,
                pendingCoreRequestTrigger: string.Empty);
            StatusText.Text = L("Status.ChoiceScenarioFileSetupCompleted");
            return;
        }

        if (option.Id.StartsWith("budget_", StringComparison.Ordinal)
            && !string.Equals(_currentChoiceScenarioStep?.StepType, "budget_setup", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(_currentChoiceScenarioStep?.StepType, "budget_setup", StringComparison.Ordinal)
            && ChoiceScenarioStepBudget.TryCreate(option.Id, out var budget))
        {
            _choiceScenarioState.ConfigureStepBudget(budget);
            _choiceScenarioLog?.Write("scenario_step_budget_selected", new
            {
                budget.Mode,
                budget.MaximumSteps,
                budget.IsAutomatic
            });
            var domainStep = _choiceScenarioService.CreateDomainStartStep(L);
            _choiceScenarioState.AddStep(domainStep, consumedAnswer: false);
            RenderChoiceScenarioStep(domainStep);
            _choiceScenarioLog?.Write("scenario_parsed_step", domainStep);
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
            StatusText.Text = LF("Status.ChoiceScenarioBudgetSelected", option.Title);
            return;
        }

        if (!_choiceScenarioState.TryAddAnswer(option))
        {
            return;
        }

        _choiceScenarioLog?.Write("scenario_user_choice", _choiceScenarioState.Answers[^1]);
        _choiceScenarioLog?.Write("scenario_capability_profile_updated", new
        {
            Source = "user_choice",
            _choiceScenarioState.Answers[^1].DecisionDimension,
            _choiceScenarioState.Answers[^1].AppliedProfileEffects,
            Profile = _choiceScenarioState.CapabilityProfile
        });
        foreach (var effect in _choiceScenarioState.Answers[^1].AppliedProfileEffects
            .Where(effect => effect.Status is ChoiceDimensionStatuses.Resolved or ChoiceDimensionStatuses.NotApplicable))
        {
            _choiceScenarioLog?.Write("scenario_resolved_dimension", effect);
        }
        StatusText.Text = LF("Status.ChoiceScenarioSelected", option.Title);
        SaveActiveSessionCheckpoint(
            pendingCoreRequest: true,
            pendingCoreRequestFinal: false);
        _ = RequestNextChoiceScenarioStepAsync(requestFinal: false);
    }

    private void ChoiceCustomOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choiceScenarioRequestInProgress)
        {
            return;
        }

        if (sender is System.Windows.Controls.Button button)
        {
            ShowCustomChoiceMenu(button, executorContext: false);
        }
    }

    private void ShowCoreCustomTextInput()
    {
        ChoiceCustomInputPanel.Visibility = Visibility.Visible;
        ChoiceCustomInput.Focus();
        StatusText.Text = L("Status.ChoiceScenarioCustomInput");
    }

    private void ChoiceCustomSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choiceScenarioRequestInProgress)
        {
            return;
        }

        var customText = ChoiceCustomInput.Text.Trim();
        if (!IsValidCustomChoice(customText))
        {
            StatusText.Text = L("Status.ChoiceScenarioCustomTooLong");
            return;
        }

        var option = new ChoiceScenarioOption
        {
            Id = "custom",
            Title = customText,
            Description = L("ChoiceScenario.CustomDescription")
        };
        ChoiceCustomInput.Clear();
        ChoiceCustomInputPanel.Visibility = Visibility.Collapsed;
        ChoiceOptionButton_Click(new System.Windows.Controls.Button { Tag = option }, e);
    }

    private void BackFromChoiceScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choiceScenarioRequestInProgress)
        {
            return;
        }

        SaveActiveSessionCheckpoint();
        CancelExecutorSession("scenario_back");

        if (_choiceScenarioState.TryGoBack(out var previousStep) && previousStep is not null)
        {
            if (string.Equals(previousStep.StepType, "budget_setup", StringComparison.Ordinal))
            {
                _choiceScenarioState.ClearStepBudget();
            }

            RenderChoiceScenarioStep(previousStep);
            _choiceScenarioLog?.Write("scenario_back", new
            {
                RestoredStep = previousStep,
                AnswerCount = _choiceScenarioState.Answers.Count,
                CapabilityProfile = _choiceScenarioState.CapabilityProfile
            });
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
            StatusText.Text = L("Status.ChoiceScenarioBack");
            return;
        }

        PauseActiveSession("back_to_modes");
        _choiceScenarioLog?.Write("scenario_session_end", new { Reason = "back_to_modes" });
        _choiceScenarioLog?.Dispose();
        _choiceScenarioLog = null;
        ShowWorkStartPage();
        StatusText.Text = L("Status.WorkStartOpened");
    }

    private void CancelChoiceScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        _choiceScenarioCts?.Cancel();
        PauseActiveSession("cancelled");
        CancelExecutorSession("scenario_cancelled");
        _choiceScenarioLog?.Write("scenario_session_end", new { Reason = "cancelled" });
        _choiceScenarioLog?.Dispose();
        _choiceScenarioLog = null;
        ShowWorkStartPage();
        StatusText.Text = L("Status.ChoiceScenarioCancelled");
    }

    private void ChoiceGoFinalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choiceScenarioRequestInProgress)
        {
            return;
        }

        _ = RequestNextChoiceScenarioStepAsync(requestFinal: true);
    }

    private void PreviousWorkExpander_Expanded(object sender, RoutedEventArgs e)
    {
        RefreshPreviousSessions();
        StatusText.Text = L("Status.PreviousWorkExpanded");
    }

    private void PreviousWorkExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L("Status.WorkStartOpened");
    }

    private void RefreshPreviousSessions()
    {
        var selectedIds = _previousSessionCards
            .Where(card => card.IsSelected)
            .Select(card => card.Session.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _previousSessionCards.Clear();
        IReadOnlyList<ResumableScenarioSession> sessions;
        try
        {
            sessions = _sessionArchiveService.LoadAll(_storageSettings);
        }
        catch
        {
            sessions = [];
        }

        var culture = _appSettings.LanguageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");
        foreach (var session in sessions)
        {
            var canResume = session.Status != ResumableSessionStatuses.Unavailable;
            var requiresDownload = HasExecutorSelection(session)
                && FindInstalledExecutorArtifact(session) is null;
            _previousSessionCards.Add(new ResumableSessionCardViewModel
            {
                Session = session,
                DisplayTitle = canResume
                    ? string.IsNullOrWhiteSpace(session.CustomTitle)
                        ? L("ChoiceScenario.Title")
                        : session.CustomTitle
                    : L("ChoiceScenario.Title"),
                CreatedText = LF(
                    "WorkStart.CreatedAt",
                    session.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", culture)),
                UpdatedText = LF(
                    "WorkStart.UpdatedAt",
                    session.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", culture)),
                StatusText = GetPreviousSessionStatusText(session),
                PrimaryActionText = !canResume
                    ? L("WorkStart.Unavailable")
                    : requiresDownload
                        ? L("WorkStart.DownloadExecutor")
                        : L("WorkStart.Continue"),
                RenameTooltip = L("WorkStart.Rename"),
                RequiresExecutorDownload = requiresDownload,
                CanResume = canResume,
                IsSelected = selectedIds.Contains(session.SessionId)
            });
        }

        PreviousWorkHeaderText.Text = LF("WorkStart.PreviousWorkCount", sessions.Count);
        PreviousWorkEmptyText.Visibility = sessions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviousWorkScrollViewer.Visibility = sessions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdatePreviousWorkSelectionPanel();
    }

    private string GetPreviousSessionStatusText(ResumableScenarioSession session)
    {
        if (session.IsRunOpen)
        {
            return L("WorkStart.StatusInterrupted");
        }

        return session.Status switch
        {
            ResumableSessionStatuses.Completed => L("WorkStart.StatusCompleted"),
            ResumableSessionStatuses.Recovered => L("WorkStart.StatusRecovered"),
            ResumableSessionStatuses.Unavailable => L("WorkStart.StatusUnavailable"),
            _ => L("WorkStart.StatusPaused")
        };
    }

    private static bool HasExecutorSelection(ResumableScenarioSession session) =>
        !string.IsNullOrWhiteSpace(session.SelectedExecutorModel)
        || session.ExecutorArtifact is not null
        || session.Executor is not null;

    private ExecutorModelArtifact? FindInstalledExecutorArtifact(ResumableScenarioSession session)
    {
        var saved = session.Executor?.Artifact ?? session.ExecutorArtifact;
        if (saved is { IsInstalled: true }
            && !string.IsNullOrWhiteSpace(saved.InstalledPath)
            && File.Exists(saved.InstalledPath))
        {
            return saved;
        }

        var requestedNames = new[]
            {
                session.SelectedExecutorModel,
                saved?.RepoId,
                saved?.RequestedModel,
                session.Executor?.Handoff.ProgramFacts
                    .FirstOrDefault(item => item.Name == "selected_executor")?.Value
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        var discovered = _choiceModelDiscoveryService.Discover(_storageSettings)
            .FirstOrDefault(model =>
                string.Equals(model.Role, "executor", StringComparison.OrdinalIgnoreCase)
                && model.IsRunnable
                && requestedNames.Any(requested =>
                    string.Equals(model.Name, requested, StringComparison.OrdinalIgnoreCase)
                    || requested.Contains(model.Name, StringComparison.OrdinalIgnoreCase)
                    || model.Name.Contains(requested, StringComparison.OrdinalIgnoreCase)));
        if (discovered is null)
        {
            return null;
        }

        return new ExecutorModelArtifact
        {
            RequestedModel = saved?.RequestedModel ?? session.SelectedExecutorModel,
            RepoId = string.IsNullOrWhiteSpace(saved?.RepoId) ? discovered.Name : saved.RepoId,
            FileName = Path.GetFileName(discovered.Path),
            Quantization = saved?.Quantization ?? string.Empty,
            License = saved?.License ?? string.Empty,
            Architecture = saved?.Architecture ?? string.Empty,
            SizeBytes = discovered.SizeBytes,
            IsInstalled = true,
            InstalledPath = discovered.Path
        };
    }

    private void PreviousWorkSelectionCheckBox_Click(object sender, RoutedEventArgs e) =>
        UpdatePreviousWorkSelectionPanel();

    private void UpdatePreviousWorkSelectionPanel()
    {
        var selectedCount = _previousSessionCards.Count(card => card.IsSelected);
        PreviousWorkSelectionPanel.Visibility = selectedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviousWorkSelectionCountText.Text = LF("WorkStart.SelectedCount", selectedCount);
    }

    private void ClearPreviousWorkSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var card in _previousSessionCards)
        {
            card.IsSelected = false;
        }

        UpdatePreviousWorkSelectionPanel();
    }

    private void DeletePreviousWorkSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _previousSessionCards
            .Where(card => card.IsSelected)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (_activeResumableSession is { IsRunOpen: true } active
            && selected.Any(card => string.Equals(
                card.Session.SessionId,
                active.SessionId,
                StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = L("Status.PreviousWorkActiveDeleteBlocked");
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            LF("WorkStart.DeleteConfirmation", selected.Count),
            L("WorkStart.DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        try
        {
            _sessionArchiveService.Delete(
                _storageSettings,
                selected.Select(card => card.Session.SessionId));
            RefreshPreviousSessions();
            StatusText.Text = LF("Status.PreviousWorkDeleted", selected.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = LF("Status.PreviousWorkDeleteFailed", ex.Message);
        }
    }

    private void RenamePreviousWorkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ResumableSessionCardViewModel card })
        {
            return;
        }

        card.IsEditing = true;
        Dispatcher.BeginInvoke(() =>
        {
            var textBox = FindVisualDescendant<System.Windows.Controls.TextBox>(
                PreviousWorkItemsControl,
                control => ReferenceEquals(control.Tag, card));
            if (textBox is null)
            {
                return;
            }

            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void PreviousWorkTitleTextBox_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox
            {
                Tag: ResumableSessionCardViewModel card
            } textBox)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitPreviousWorkTitle(card, textBox.Text);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            card.DisplayTitle = card.Session.DisplayTitle;
            card.IsEditing = false;
            e.Handled = true;
        }
    }

    private void PreviousWorkTitleTextBox_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox
            {
                Tag: ResumableSessionCardViewModel { IsEditing: true } card
            } textBox)
        {
            CommitPreviousWorkTitle(card, textBox.Text);
        }
    }

    private void CommitPreviousWorkTitle(ResumableSessionCardViewModel card, string title)
    {
        var normalized = title.Trim();
        if (normalized.Length > 100)
        {
            normalized = normalized[..100].TrimEnd();
        }

        try
        {
            _sessionArchiveService.Rename(_storageSettings, card.Session, normalized);
            card.DisplayTitle = card.Session.DisplayTitle;
            card.IsEditing = false;
            RefreshPreviousSessions();
            StatusText.Text = L("Status.PreviousWorkRenamed");
        }
        catch (Exception ex)
        {
            card.DisplayTitle = card.Session.DisplayTitle;
            card.IsEditing = false;
            StatusText.Text = LF("Status.PreviousWorkRenameFailed", ex.Message);
        }
    }

    private async void PreviousWorkPrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: ResumableSessionCardViewModel card
            })
        {
            return;
        }

        var session = _sessionArchiveService.Load(_storageSettings, card.Session.SessionId);
        if (session is null || session.Status == ResumableSessionStatuses.Unavailable)
        {
            StatusText.Text = L("Status.PreviousWorkUnavailable");
            RefreshPreviousSessions();
            return;
        }

        await ResumeScenarioSessionAsync(session, card.RequiresExecutorDownload);
    }

    private async Task ResumeScenarioSessionAsync(
        ResumableScenarioSession session,
        bool requestExecutorDownload)
    {
        HideImageAnalysisPages();
        PauseActiveSession("another_session_opened");
        _choiceScenarioCts?.Cancel();
        CancelExecutorSession("restoring_archived_session");
        _choiceScenarioLog?.Write("scenario_session_end", new { Reason = "restored_another_session" });
        _choiceScenarioLog?.Dispose();
        _choiceScenarioLog = null;
        try
        {
            _sessionRestorationContext = _sessionArchiveService.BeginRestoredRun(
                _storageSettings,
                session);
            _activeResumableSession = session;
            _resumeExecutorAfterDownload = false;
            _choiceScenarioInvalidJsonCount = 0;
            _choiceScenarioRequestInProgress = false;
            _choiceScenarioState.Restore(session.Core);
            session.FileManifest ??= new SessionFileManifest();
            var fileAvailabilityChanged = _sessionFileManifestService.RefreshAvailability(
                session.FileManifest);
            ApplyFileManifestToCoreProfile();
            RefreshSessionFileCards();
            _choiceScenarioLog = ScenarioSessionLog.CreateUncertainty(
                _storageSettings,
                session.SessionId,
                session.CurrentRunId);
            session.CoreLogPath = _choiceScenarioLog.FilePath;
            _choiceScenarioLog.Write("scenario_session_restored", new
            {
                AppVersion = GetAppVersion(),
                session.SessionId,
                session.CurrentRunId,
                session.ResumeCount,
                _sessionRestorationContext.PreviousStopKind,
                _sessionRestorationContext.PreviousStopReason,
                _sessionRestorationContext.LostUncommittedTurn,
                FileAvailabilityChanged = fileAvailabilityChanged,
                FileManifest = _sessionFileManifestService.CreatePromptManifest(session.FileManifest),
                _choiceScenarioLog.FilePath
            });
            _choiceScenarioLog.Write("scenario_context_snapshot", _userContextService.CreateSnapshot());

            ChoiceCustomInput.Clear();
            ChoiceCustomInputPanel.Visibility = Visibility.Collapsed;
            ChoiceScenarioPreparationViewbox.Visibility = Visibility.Visible;
            ExecutorResultPanel.Visibility = Visibility.Collapsed;
            _executorPracticalLayoutActive = false;
            var restoredStep = _choiceScenarioState.CurrentStep
                ?? throw new InvalidOperationException("The archived session has no stable scenario step.");
            RenderChoiceScenarioStep(restoredStep);
            RestoreSavedExecutorSelection(session);
            WelcomePage.Visibility = Visibility.Collapsed;
            SetupPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            ProfilePage.Visibility = Visibility.Collapsed;
            ProfileReminderPage.Visibility = Visibility.Collapsed;
            WorkStartPage.Visibility = Visibility.Collapsed;
            ChoiceScenarioPage.Visibility = Visibility.Visible;
            SaveActiveSessionCheckpoint();

            if (session.Executor is { } executorCheckpoint)
            {
                executorCheckpoint.Handoff.FileManifest =
                    _sessionFileManifestService.CreatePromptManifest(session.FileManifest);
                var installedArtifact = FindInstalledExecutorArtifact(session);
                if (installedArtifact is not null)
                {
                    RestoreExecutorFromArchive(
                        installedArtifact,
                        executorCheckpoint,
                        _sessionRestorationContext);
                    return;
                }

                _resumeExecutorAfterDownload = true;
                _pendingExecutorArtifact = executorCheckpoint.Artifact;
                StatusText.Text = L("Status.PreviousWorkExecutorMissing");
                if (requestExecutorDownload)
                {
                    ExecutorPrepareButton_Click(
                        ExecutorPrepareButton,
                        new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }

                return;
            }

            if (session.Core.PendingCoreRequest)
            {
                CancelCoreSpeech(revealFullText: true, "restored_pending_request");
                await RequestNextChoiceScenarioStepAsync(
                    session.Core.PendingCoreRequestFinal,
                    session.Core.PendingCoreRequestConsumesAnswer,
                    string.IsNullOrWhiteSpace(session.Core.PendingCoreRequestTrigger)
                        ? "restored_request"
                        : session.Core.PendingCoreRequestTrigger);
                return;
            }

            if (requestExecutorDownload)
            {
                _resumeExecutorAfterDownload = false;
                ExecutorPrepareButton_Click(
                    ExecutorPrepareButton,
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                return;
            }

            StatusText.Text = L("Status.PreviousWorkRestored");
        }
        catch (Exception ex)
        {
            try
            {
                PauseActiveSession("restore_failed");
            }
            catch
            {
                // The original restore error is more useful to the user.
            }

            _choiceScenarioLog?.Write("scenario_restore_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            _choiceScenarioLog?.Dispose();
            _choiceScenarioLog = null;
            _activeResumableSession = null;
            _sessionRestorationContext = null;
            ShowWorkStartPage();
            StatusText.Text = LF("Status.PreviousWorkRestoreFailed", ex.Message);
        }
    }

    private void RestoreSavedExecutorSelection(ResumableScenarioSession session)
    {
        if (_currentChoiceScenarioStep?.TaskCard is not { } card)
        {
            return;
        }

        var requestedModel = session.SelectedExecutorModel;
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            requestedModel = session.Executor?.Artifact.RepoId
                ?? session.ExecutorArtifact?.RepoId
                ?? card.RecommendedExecutor;
        }

        var candidate = card.ExecutorCandidates.FirstOrDefault(item =>
            ModelNamesReferToSameExecutor(item.Model, requestedModel));
        if (candidate is null)
        {
            return;
        }

        candidate.Status = FindInstalledExecutorArtifact(session) is null
            ? ChoiceExecutorCandidateStatuses.NotInstalled
            : ChoiceExecutorCandidateStatuses.Installed;
        _selectedExecutorCandidate = candidate;
        card.RecommendedExecutor = candidate.Model;
        card.ExecutorStatus = candidate.Status;
        card.ExecutorRole = candidate.Role;
        card.ExecutorCapabilityClass = candidate.CapabilityClass;
        card.ExecutorReason = BuildExecutorVerifiedReason(candidate);
        RenderExecutionRoute(card);
        RenderExecutorCandidates(card);
        UpdateExecutorPreparationActions(card);
    }

    private static bool ModelNamesReferToSameExecutor(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void DownloadCoreModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingModelDownload == PendingModelDownload.Reranker)
        {
            await StartRerankerDownloadAsync();
            return;
        }

        await StartCoreModelDownloadAsync();
    }

    private void OpenSetupFromCoreModelButton_Click(object sender, RoutedEventArgs e)
    {
        HideCoreModelPrompt();
        OpenSetupWindow(regeneratePassport: false);
    }

    private void PostponeCoreModelButton_Click(object sender, RoutedEventArgs e)
    {
        _isCoreModelPromptPostponed = true;
        HideCoreModelPrompt();
        StatusText.Text = _pendingModelDownload == PendingModelDownload.Reranker
            ? L("Status.RerankerModelPostponed")
            : L("Status.CoreModelPostponed");
    }

    private void PauseCoreModelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _coreModelDownloadCts?.Cancel();
        StatusText.Text = L("Status.CoreModelPauseRequested");
    }

    private void CancelCoreModelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _coreModelDownloadCts?.Cancel();
        StatusText.Text = L("Status.CoreModelCancelRequested");
    }

    private void SavePassportState(ComputerPassport passport)
    {
        _appState.ComputerPassportLastUpdated = passport.CreatedAt;
        _appStateStore.Save(_appState);
        UpdatePrimaryActionButton();
    }

    private void UpdatePrimaryActionButton()
    {
        PrimaryActionButton.Content = _appState.HasCompletedSetup
            ? L("Home.StartWork")
            : L("Home.StartSetup");
        ReconfigureButton.Content = L("Home.Reconfigure");
    }

    private void UpdateWelcomeStatus()
    {
        StatusText.Text = _appState.HasCompletedSetup
            ? L("Status.PassportReadySetupComplete")
            : L("Status.PassportReadySetupIncomplete");
    }

    private void EvaluateCoreModelOnStartup()
    {
        var result = _coreModelManager.Check(_storageSettings);
        _lastCoreModelCheck = result;
        if (result.Availability != CoreModelAvailability.Installed && !_isCoreModelPromptPostponed)
        {
            ShowCoreModelPrompt(result);
            return;
        }

        if (result.Availability == CoreModelAvailability.Installed
            && !_toolModelManager.IsRerankerInstalled(_storageSettings)
            && !_isCoreModelPromptPostponed)
        {
            ShowRerankerModelPrompt();
        }
    }

    private void EvaluateCoreModelAfterStorageSave()
    {
        var result = _coreModelManager.Check(_storageSettings);
        _lastCoreModelCheck = result;
        if (result.Availability == CoreModelAvailability.Installed)
        {
            HideCoreModelPrompt();
            if (_toolModelManager.IsRerankerInstalled(_storageSettings))
            {
                StatusText.Text = L("Status.CoreModelReady");
            }
            else
            {
                ShowRerankerModelPrompt();
            }
            return;
        }

        ShowCoreModelPrompt(result);
    }

    private void ShowCoreModelPrompt(CoreModelCheckResult result)
    {
        _pendingModelDownload = PendingModelDownload.Core;
        _lastCoreModelCheck = result;
        CoreModelPromptPanel.Visibility = Visibility.Visible;
        CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
        DownloadCoreModelButton.IsEnabled = CanDownloadCoreModel(result);
        CoreModelPromptText.Text = BuildCoreModelPromptText(result);
        StatusText.Text = result.Availability switch
        {
            CoreModelAvailability.StorageNotConfigured => L("Status.CoreModelStorageNotConfigured"),
            CoreModelAvailability.ModelsFolderUnavailable => L("Status.CoreModelFolderUnavailable"),
            CoreModelAvailability.Partial => L("Status.CoreModelPartial"),
            CoreModelAvailability.Invalid => L("Status.CoreModelInvalid"),
            _ => L("Status.CoreModelMissing")
        };
    }

    private void ShowRerankerModelPrompt()
    {
        _pendingModelDownload = PendingModelDownload.Reranker;
        CoreModelPromptPanel.Visibility = Visibility.Visible;
        CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
        DownloadCoreModelButton.IsEnabled = HasModelsStorage();
        CoreModelPromptText.Text = LF(
            "ToolModel.RerankerMissingPrompt",
            ToolModelManager.RerankerDisplayName,
            FormatBytes(ToolModelManager.RerankerTotalBytes));
        StatusText.Text = L("Status.RerankerModelMissing");
    }

    private string BuildCoreModelPromptText(CoreModelCheckResult result)
    {
        if (!result.HasEnoughSpace)
        {
            return LF("CoreModel.NotEnoughSpacePrompt", FormatBytes(CoreModelManager.CoreModelTotalBytes), FormatBytes(CoreModelManager.RecommendedFreeBytes));
        }

        return result.Availability switch
        {
            CoreModelAvailability.StorageNotConfigured => L("CoreModel.StorageNotConfiguredPrompt"),
            CoreModelAvailability.ModelsFolderUnavailable => L("CoreModel.FolderUnavailablePrompt"),
            CoreModelAvailability.Partial => LF("CoreModel.PartialPrompt", FormatBytes(result.ExistingBytes), FormatBytes(CoreModelManager.CoreModelTotalBytes)),
            CoreModelAvailability.Invalid => L("CoreModel.InvalidPrompt"),
            _ => L("CoreModel.MissingPrompt")
        };
    }

    private static bool CanDownloadCoreModel(CoreModelCheckResult result)
    {
        return result.HasEnoughSpace
            && result.Availability is CoreModelAvailability.Missing
                or CoreModelAvailability.Partial
                or CoreModelAvailability.Invalid;
    }

    private bool HasModelsStorage()
    {
        return _storageSettings.Models.Locations.Any(location => !string.IsNullOrWhiteSpace(location.Path));
    }

    private void HideCoreModelPrompt()
    {
        CoreModelPromptPanel.Visibility = Visibility.Collapsed;
    }

    private async Task StartCoreModelDownloadAsync()
    {
        if (_coreModelDownloadCts is not null)
        {
            return;
        }

        var check = _coreModelManager.Check(_storageSettings);
        if (!CanDownloadCoreModel(check))
        {
            ShowCoreModelPrompt(check);
            return;
        }

        _coreModelDownloadCts = new CancellationTokenSource();
        DownloadCoreModelButton.IsEnabled = false;
        CoreModelPromptPanel.Visibility = Visibility.Collapsed;
        CoreModelDownloadPanel.Visibility = Visibility.Visible;
        CoreModelDownloadProgressBar.Value = 0;
        CoreModelDownloadTitleText.Text = LF(
            "CoreModel.DownloadProgress",
            0,
            FormatBytes(check.ExistingBytes),
            FormatBytes(CoreModelManager.CoreModelTotalBytes),
            FormatBytes(0) + L("Units.PerSecond"));
        StatusText.Text = L("Status.CoreModelDownloadStarted");

        var progress = new Progress<CoreModelDownloadProgress>(UpdateCoreModelDownloadProgress);

        var coreInstalled = false;
        try
        {
            await _coreModelManager.DownloadAsync(_storageSettings, progress, _coreModelDownloadCts.Token);
            coreInstalled = true;
            await _toolModelManager.EnsureRerankerDownloadedAsync(_storageSettings, progress, _coreModelDownloadCts.Token);
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            HideCoreModelPrompt();
            StatusText.Text = L("Status.CoreAndRerankerInstalled");
        }
        catch (OperationCanceledException)
        {
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            if (coreInstalled)
            {
                ShowRerankerModelPrompt();
            }
            else
            {
                var result = _coreModelManager.Check(_storageSettings);
                ShowCoreModelPrompt(result);
            }

            StatusText.Text = L("Status.CoreModelDownloadPaused");
        }
        catch (Exception)
        {
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            if (coreInstalled)
            {
                ShowRerankerModelPrompt();
                StatusText.Text = L("Status.RerankerModelDownloadFailed");
            }
            else
            {
                var result = _coreModelManager.Check(_storageSettings);
                ShowCoreModelPrompt(result);
                StatusText.Text = L("Status.CoreModelDownloadFailed");
            }
        }
        finally
        {
            _coreModelDownloadCts?.Dispose();
            _coreModelDownloadCts = null;
            DownloadCoreModelButton.IsEnabled = true;
        }
    }

    private async Task StartRerankerDownloadAsync()
    {
        if (_coreModelDownloadCts is not null)
        {
            return;
        }

        if (!HasModelsStorage())
        {
            ShowRerankerModelPrompt();
            return;
        }

        _coreModelDownloadCts = new CancellationTokenSource();
        DownloadCoreModelButton.IsEnabled = false;
        CoreModelPromptPanel.Visibility = Visibility.Collapsed;
        CoreModelDownloadPanel.Visibility = Visibility.Visible;
        CoreModelDownloadProgressBar.Value = 0;
        CoreModelDownloadTitleText.Text = LF(
            "ToolModel.RerankerDownloadProgress",
            0,
            FormatBytes(0),
            FormatBytes(ToolModelManager.RerankerTotalBytes),
            FormatBytes(0) + L("Units.PerSecond"));
        StatusText.Text = L("Status.RerankerModelDownloadStarted");

        var progress = new Progress<CoreModelDownloadProgress>(UpdateCoreModelDownloadProgress);

        try
        {
            await _toolModelManager.EnsureRerankerDownloadedAsync(_storageSettings, progress, _coreModelDownloadCts.Token);
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            HideCoreModelPrompt();
            StatusText.Text = L("Status.RerankerModelInstalled");
        }
        catch (OperationCanceledException)
        {
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            ShowRerankerModelPrompt();
            StatusText.Text = L("Status.CoreModelDownloadPaused");
        }
        catch (Exception)
        {
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            ShowRerankerModelPrompt();
            StatusText.Text = L("Status.RerankerModelDownloadFailed");
        }
        finally
        {
            _coreModelDownloadCts?.Dispose();
            _coreModelDownloadCts = null;
            DownloadCoreModelButton.IsEnabled = true;
        }
    }

    private void UpdateCoreModelDownloadProgress(CoreModelDownloadProgress progress)
    {
        if (progress.Stage == "verifying")
        {
            CoreModelDownloadTitleText.Text = L("CoreModel.Verifying");
            CoreModelDownloadProgressBar.Value = 100;
            return;
        }

        if (progress.Stage == "installed")
        {
            CoreModelDownloadTitleText.Text = L("CoreModel.Installed");
            CoreModelDownloadProgressBar.Value = 100;
            return;
        }

        if (progress.Stage == "verifying-reranker")
        {
            CoreModelDownloadTitleText.Text = L("ToolModel.RerankerVerifying");
            CoreModelDownloadProgressBar.Value = 100;
            return;
        }

        if (progress.Stage == "installed-reranker")
        {
            CoreModelDownloadTitleText.Text = L("ToolModel.RerankerInstalled");
            CoreModelDownloadProgressBar.Value = 100;
            return;
        }

        var percent = progress.TotalBytes <= 0
            ? 0
            : Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0, 100);

        CoreModelDownloadProgressBar.Value = percent;
        var progressKey = progress.Stage == "downloading-reranker"
            ? "ToolModel.RerankerDownloadProgress"
            : "CoreModel.DownloadProgress";
        CoreModelDownloadTitleText.Text = LF(
            progressKey,
            percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            FormatBytes(progress.DownloadedBytes),
            FormatBytes(progress.TotalBytes),
            FormatBytes(progress.BytesPerSecond) + L("Units.PerSecond"));
    }

    private bool HasRequiredStorageSettings()
    {
        return _storageSettings.Models.Locations.Count > 0
            && _storageSettings.Results.Locations.Count > 0;
    }

    private void ShowSetupPage(ComputerPassport passport)
    {
        HideImageAnalysisPages();
        PassportSummaryText.Text = BuildPassportSummary(passport);
        PassportPathText.Text = LF("Setup.PassportPath", AppDataPaths.ComputerPassportPath);
        WelcomePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Visible;
    }

    private void ShowSettingsPage()
    {
        if (SettingsPage.Visibility != Visibility.Visible)
        {
            _settingsReturnPage = FindVisiblePageBeforeSettings();
            _settingsReturnStatusText = StatusText.Text;
        }

        HideImageAnalysisPages();
        CancelCoreSpeech(revealFullText: false, "open_settings");
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        PopulateLanguageComboBox();
        RefreshComponentCatalogUi();
        if (ManagedModelsExpander.IsExpanded)
        {
            RefreshManagedModels();
        }
    }

    private FrameworkElement? FindVisiblePageBeforeSettings()
    {
        FrameworkElement[] pages =
        [
            LiteraryPage,
            ImageAnalysisWorkspacePage,
            ImageAnalysisBundleConfirmationPage,
            ImageAnalysisBundleSelectorPage,
            ChoiceScenarioPage,
            WorkStartPage,
            ProfileReminderPage,
            ProfilePage,
            SetupPage,
            WelcomePage
        ];

        return pages.FirstOrDefault(page => page.Visibility == Visibility.Visible);
    }

    private void ShowProfilePage()
    {
        HideImageAnalysisPages();
        CancelCoreSpeech(revealFullText: false, "open_profile");
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Visible;
    }

    private void ShowProfileReminderPage()
    {
        HideImageAnalysisPages();
        CancelCoreSpeech(revealFullText: false, "open_profile_reminder");
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Visible;
    }

    private void ShowWorkStartPage()
    {
        HideImageAnalysisPages();
        CancelCoreSpeech(revealFullText: false, "open_work_start");
        PreviousWorkExpander.IsExpanded = false;
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Visible;
        RefreshPreviousSessions();
    }

    private void StartChoiceScenario()
    {
        HideImageAnalysisPages();
        PauseActiveSession("new_scenario_started");
        _choiceScenarioLog?.Write("scenario_session_end", new { Reason = "restart" });
        _choiceScenarioLog?.Dispose();
        _choiceScenarioLog = null;
        _sessionRestorationContext = null;
        _resumeExecutorAfterDownload = false;
        _choiceScenarioInvalidJsonCount = 0;
        _choiceScenarioRequestInProgress = false;
        ChoiceScenarioPreparationViewbox.Visibility = Visibility.Visible;
        ExecutorResultPanel.Visibility = Visibility.Collapsed;
        _executorPracticalLayoutActive = false;
        ChoiceCustomInput.Clear();
        ChoiceCustomInputPanel.Visibility = Visibility.Collapsed;
        var startStep = _choiceScenarioService.CreateFileSetupStep(L);
        _choiceScenarioState.Reset(startStep);
        try
        {
            _activeResumableSession = _sessionArchiveService.Create(
                _storageSettings,
                L("ChoiceScenario.Title"),
                _choiceScenarioState.CreateCheckpoint());
            _choiceScenarioLog = ScenarioSessionLog.CreateUncertainty(
                _storageSettings,
                _activeResumableSession.SessionId,
                _activeResumableSession.CurrentRunId);
            _activeResumableSession.CoreLogPath = _choiceScenarioLog.FilePath;
            _sessionArchiveService.Save(_storageSettings, _activeResumableSession);
            RefreshSessionFileCards();
        }
        catch (Exception ex)
        {
            _activeResumableSession = null;
            _choiceScenarioLog?.Dispose();
            _choiceScenarioLog = null;
            StatusText.Text = LF("Status.SessionArchiveCreateFailed", ex.Message);
            return;
        }

        _choiceScenarioLog.Write("scenario_session_start", new
        {
            AppVersion = GetAppVersion(),
            _choiceScenarioLog.FilePath,
            _activeResumableSession.SessionId,
            _activeResumableSession.CurrentRunId
        });
        _choiceScenarioLog.Write("scenario_context_snapshot", _userContextService.CreateSnapshot());
        RenderChoiceScenarioStep(startStep);
        _choiceScenarioLog.Write("scenario_parsed_step", _currentChoiceScenarioStep);
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Visible;
    }

    private void SaveActiveSessionCheckpoint(
        bool? pendingCoreRequest = null,
        bool? pendingCoreRequestFinal = null,
        bool? pendingCoreRequestConsumesAnswer = null,
        string? pendingCoreRequestTrigger = null)
    {
        if (_activeResumableSession is null)
        {
            return;
        }

        try
        {
            var core = _choiceScenarioState.CreateCheckpoint();
            core.PendingCoreRequest = pendingCoreRequest
                ?? _activeResumableSession.Core.PendingCoreRequest;
            core.PendingCoreRequestFinal = pendingCoreRequestFinal
                ?? _activeResumableSession.Core.PendingCoreRequestFinal;
            core.PendingCoreRequestConsumesAnswer = pendingCoreRequestConsumesAnswer
                ?? _activeResumableSession.Core.PendingCoreRequestConsumesAnswer;
            core.PendingCoreRequestTrigger = pendingCoreRequestTrigger
                ?? _activeResumableSession.Core.PendingCoreRequestTrigger;
            _activeResumableSession.Core = core;
            if (_currentChoiceScenarioStep?.TaskCard is { } card)
            {
                _activeResumableSession.SelectedExecutorModel =
                    _selectedExecutorCandidate?.Model
                    ?? card.RecommendedExecutor;
            }

            if (_pendingExecutorArtifact is not null)
            {
                _activeResumableSession.ExecutorArtifact = _pendingExecutorArtifact;
            }

            var executorCheckpoint = _executorWorkflowService.CreateCheckpoint();
            if (executorCheckpoint is not null)
            {
                _activeResumableSession.Executor = executorCheckpoint;
                _activeResumableSession.ExecutorArtifact = executorCheckpoint.Artifact;
                _activeResumableSession.ExecutorHandoff = executorCheckpoint.Handoff;
                _activeResumableSession.ExecutorLogPath = _executorWorkflowService.ActiveLogPath;
            }

            _sessionArchiveService.Save(_storageSettings, _activeResumableSession);
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("session_checkpoint_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.SessionCheckpointFailed", ex.Message);
        }
    }

    private void ExecutorWorkflowService_CheckpointChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ExecutorWorkflowService_CheckpointChanged(sender, e));
            return;
        }

        try
        {
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("session_checkpoint_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.SessionCheckpointFailed", ex.Message);
        }
    }

    private void PauseActiveSession(string reason)
    {
        if (_activeResumableSession is not { IsRunOpen: true } session)
        {
            return;
        }

        try
        {
            SaveActiveSessionCheckpoint();
            _sessionArchiveService.MarkStopped(
                _storageSettings,
                session,
                ResumableSessionStopKinds.Normal,
                reason,
                ResumableSessionStatuses.Paused);
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("session_pause_checkpoint_failed", new
            {
                Reason = reason,
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
        }
    }

    private void CompleteActiveSessionArchive(string reason)
    {
        if (_activeResumableSession is null)
        {
            return;
        }

        try
        {
            SaveActiveSessionCheckpoint();
            _sessionArchiveService.MarkStopped(
                _storageSettings,
                _activeResumableSession,
                ResumableSessionStopKinds.Completed,
                reason,
                ResumableSessionStatuses.Completed);
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("session_completion_checkpoint_failed", new
            {
                Reason = reason,
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.SessionCheckpointFailed", ex.Message);
        }
    }

    private static string BuildCoreRestorationPrompt(SessionRestorationContext restoration) =>
        string.Join(
            Environment.NewLine,
            "[AI_HUB_SESSION_RESTORED]",
            $"Stable session id: {restoration.SessionId}.",
            $"Current restored run id: {restoration.RunId}.",
            $"Resume count: {restoration.ResumeCount}.",
            $"Original session created at: {restoration.OriginalCreatedAt:O}.",
            $"Restored at: {restoration.RestoredAt:O}.",
            $"Previous stop kind: {restoration.PreviousStopKind}.",
            $"Previous stop reason: {restoration.PreviousStopReason}.",
            $"Last stable stage: {restoration.LastStableStage}.",
            $"An uncommitted interrupted turn was lost: {restoration.LostUncommittedTurn}.",
            "This is a restored run loaded from the AI HUB archive, not the original live run and not a new task.",
            "Continue from the saved confirmed checkpoint. Do not restart the scenario, repeat completed questions or claim uninterrupted process memory.",
            restoration.LostUncommittedTurn
                ? "The interrupted unfinished request did not complete. Recreate only that next step from the stable checkpoint."
                : "All restored scenario steps in the checkpoint were fully committed by AI HUB.");

    private async Task RequestNextChoiceScenarioStepAsync(
        bool requestFinal,
        bool consumesAnswer = true,
        string requestTrigger = "user_choice")
    {
        if (_choiceScenarioRequestInProgress || _choiceScenarioCts is not null)
        {
            return;
        }

        var stepBudget = _choiceScenarioState.StepBudget;
        if (stepBudget is null)
        {
            return;
        }

        var mustReturnFinal = _choiceScenarioState.IsStepBudgetExhausted;
        var effectiveRequestFinal = requestFinal || mustReturnFinal;
        var answerCommitted = consumesAnswer && !requestFinal;
        _choiceScenarioRequestInProgress = true;
        _choiceScenarioCts = new CancellationTokenSource();
        SetChoiceScenarioInteractionEnabled(false);
        StartChoiceAiActivity();
        var streamProgress = CreateMatrixStreamProgress();
        ChoiceScenarioStatusText.Text = L("ChoiceScenario.CoreWorking");
        StatusText.Text = effectiveRequestFinal
            ? L("Status.ChoiceScenarioRequestFinal")
            : L("Status.ChoiceScenarioCoreThinking");

        try
        {
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: true,
                pendingCoreRequestFinal: effectiveRequestFinal,
                pendingCoreRequestConsumesAnswer: answerCommitted,
                pendingCoreRequestTrigger: requestFinal ? "manual_final" : requestTrigger);
            var model = _choiceModelDiscoveryService
                .Discover(_storageSettings)
                .FirstOrDefault(item => item.IsCoreModel && item.IsRunnable)
                ?? _choiceModelDiscoveryService.Discover(_storageSettings).FirstOrDefault(item => item.IsRunnable);

            if (model is null)
            {
                _choiceScenarioLog?.Write("scenario_core_unavailable", new { Reason = "No runnable model" });
                var fallbackStep = _choiceScenarioService.CreateFallbackStep(_choiceScenarioState.Answers, L);
                if (_choiceScenarioState.GetFingerprintCount(fallbackStep) > 0)
                {
                    if (answerCommitted)
                    {
                        _choiceScenarioState.RemoveLastAnswer();
                    }
                    ChoiceScenarioStatusText.Text = L("ChoiceScenario.CoreUnavailableStop");
                    StatusText.Text = L("Status.ChoiceScenarioCoreUnavailable");
                    SaveActiveSessionCheckpoint(
                        pendingCoreRequest: false,
                        pendingCoreRequestFinal: false);
                    return;
                }

                _choiceScenarioState.AddStep(fallbackStep, consumedAnswer: answerCommitted);
                RenderChoiceScenarioStep(fallbackStep);
                SaveActiveSessionCheckpoint(
                    pendingCoreRequest: false,
                    pendingCoreRequestFinal: false);
                StatusText.Text = L("Status.ChoiceScenarioCoreUnavailable");
                return;
            }

            _choiceScenarioRuntimeService ??= new LlamaServerRuntimeService(_userContextService);
            var inventory = _choiceInventoryService.Create(_storageSettings);
            var inventorySummary = string.Join(
                Environment.NewLine,
                inventory.Items.Select(item => $"{item.Role}: installed={item.IsInstalled}; runnable={item.IsRunnable}; name={item.Name}"));
            var systemPrompt = _choiceScenarioService.BuildSystemPrompt();
            if (_sessionRestorationContext is not null)
            {
                systemPrompt = string.Join(
                    Environment.NewLine,
                    systemPrompt,
                    string.Empty,
                    BuildCoreRestorationPrompt(_sessionRestorationContext));
            }
            var filePromptManifest = _sessionFileManifestService.CreatePromptManifest(
                GetActiveFileManifest());
            var userPrompt = _choiceScenarioService.BuildUserPrompt(
                _choiceScenarioState.Answers,
                requestFinal,
                mustReturnFinal,
                _userContextService.CreateSnapshot(),
                inventorySummary,
                _appSettings.LanguageCode,
                stepBudget,
                _choiceScenarioState.SubstantiveStepsUsed,
                _choiceScenarioState.StepsRemaining,
                _choiceScenarioState.CapabilityProfile,
                filePromptManifest,
                requestFinal ? "manual_final" : requestTrigger);
            _choiceScenarioLog?.Write("scenario_core_prompt", new
            {
                Model = model.Name,
                model.Path,
                RequestFinal = effectiveRequestFinal,
                ManualRequestFinal = requestFinal,
                RequestTrigger = requestFinal ? "manual_final" : requestTrigger,
                FileManifest = filePromptManifest,
                MustReturnFinal = mustReturnFinal,
                CapabilityProfile = _choiceScenarioState.CapabilityProfile,
                StepBudget = stepBudget,
                StepsUsed = _choiceScenarioState.SubstantiveStepsUsed,
                StepsRemaining = _choiceScenarioState.StepsRemaining,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt
            });

            var generation = await _choiceScenarioOrchestrator.GenerateAsync(
                _choiceScenarioRuntimeService,
                model,
                systemPrompt,
                userPrompt,
                _storageSettings,
                _choiceScenarioLog ?? new NullSessionEventLog(),
                _choiceScenarioCts.Token,
                effectiveRequestFinal,
                mustReturnFinal,
                _userProfile.WorkloadMode,
                _choiceScenarioState.CapabilityProfile,
                streamProgress,
                _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds,
                filePromptManifest);
            _choiceScenarioLog?.Write("scenario_core_raw_response", new
            {
                Model = model.Name,
                Text = generation.RawResponse,
                generation.RepairAttempts
            });

            if (generation.Step is { } step)
            {
                var repeatedStep = !step.IsFinal
                    && (_choiceScenarioState.GetFingerprintCount(step) > 0
                        || _choiceScenarioState.IsSemanticLoop(step));
                var subjectMatterOverreach = !step.IsFinal
                    && _choiceScenarioState.IsSubjectMatterOverreach(step);
                if (repeatedStep || subjectMatterOverreach)
                {
                    _choiceScenarioLog?.Write(
                        subjectMatterOverreach
                            ? "scenario_subject_matter_overreach"
                            : "scenario_repeated_step_blocked",
                        new
                        {
                            step.Question,
                            step.DecisionDimension,
                            step.SelectionImpact,
                            Fingerprint = ChoiceScenarioSessionState.CreateFingerprint(step)
                        });
                    var correctionPrompt = userPrompt + Environment.NewLine + Environment.NewLine
                        + (subjectMatterOverreach
                            ? "Предыдущий вопрос отклонён как повторное предметное углубление. Не уточняй содержание темы. Выбери другое неизвестное измерение профиля исполнителя: операцию, данные, контекст, инструменты, точность, скорость или приватность. Если профиль уже достаточен, сформируй final_task_card."
                            : "Запрещено повторять уже заданный вопрос, измерение или тот же набор вариантов. Выбери другое неизвестное измерение профиля исполнителя. Если данных достаточно, сформируй final_task_card.");
                    generation = await _choiceScenarioOrchestrator.GenerateAsync(
                        _choiceScenarioRuntimeService,
                        model,
                        systemPrompt,
                        correctionPrompt,
                        _storageSettings,
                        _choiceScenarioLog ?? new NullSessionEventLog(),
                        _choiceScenarioCts.Token,
                        effectiveRequestFinal,
                        mustReturnFinal,
                        _userProfile.WorkloadMode,
                        _choiceScenarioState.CapabilityProfile,
                        streamProgress,
                        _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds,
                        filePromptManifest);
                    step = generation.Step ?? step;
                    repeatedStep = !step.IsFinal
                        && (_choiceScenarioState.GetFingerprintCount(step) > 0
                            || _choiceScenarioState.IsSemanticLoop(step));
                    subjectMatterOverreach = !step.IsFinal
                        && _choiceScenarioState.IsSubjectMatterOverreach(step);
                    if (repeatedStep || subjectMatterOverreach)
                    {
                        _choiceScenarioLog?.Write("scenario_question_rejected_as_non_productive", new
                        {
                            step.Question,
                            step.DecisionDimension,
                            Reason = subjectMatterOverreach ? "subject_matter_overreach" : "repeated_step"
                        });
                        var forcedFinalPrompt = userPrompt + Environment.NewLine + Environment.NewLine
                            + "Два последовательных вопроса отклонены как непродуктивные. Новый question_step запрещён. Сформируй final_task_card по текущему capability profile, честно обозначь пробелы и поручай рабочей модели задать недостающие предметные вопросы.";
                        generation = await _choiceScenarioOrchestrator.GenerateAsync(
                            _choiceScenarioRuntimeService,
                            model,
                            systemPrompt,
                            forcedFinalPrompt,
                            _storageSettings,
                            _choiceScenarioLog ?? new NullSessionEventLog(),
                            _choiceScenarioCts.Token,
                            requestFinal: true,
                            mustReturnFinal: true,
                            workloadMode: _userProfile.WorkloadMode,
                            capabilityProfile: _choiceScenarioState.CapabilityProfile,
                            streamProgress: streamProgress,
                            autonomySeconds:
                                _appSettings.CoreAutonomy.MaximumIndependentSearchSeconds,
                            fileManifest: filePromptManifest);
                        step = generation.Step ?? step;
                        if (!step.IsFinal)
                        {
                            _choiceScenarioInvalidJsonCount++;
                            if (answerCommitted)
                            {
                                _choiceScenarioState.RemoveLastAnswer();
                            }
                            ChoiceScenarioStatusText.Text = L("ChoiceScenario.RepeatedStepError");
                            StatusText.Text = L("Status.ChoiceScenarioRepeatedStep");
                            SaveActiveSessionCheckpoint(
                                pendingCoreRequest: false,
                                pendingCoreRequestFinal: false);
                            return;
                        }
                    }
                }

                _choiceScenarioInvalidJsonCount = 0;
                _choiceScenarioState.AddStep(step, consumedAnswer: answerCommitted);
                RenderChoiceScenarioStep(step);
                _choiceScenarioLog?.Write("scenario_capability_profile_updated", _choiceScenarioState.CapabilityProfile);
                if (!step.IsFinal)
                {
                    _choiceScenarioLog?.Write("scenario_decision_dimension", new
                    {
                        step.DecisionDimension,
                        step.SelectionImpact,
                        ResolvedDimensions = _choiceScenarioState.CapabilityProfile.ResolvedDimensions
                    });
                }
                _choiceScenarioLog?.Write(step.IsFinal ? "scenario_final_task_card" : "scenario_parsed_step", step);
                SaveActiveSessionCheckpoint(
                    pendingCoreRequest: false,
                    pendingCoreRequestFinal: false,
                    pendingCoreRequestConsumesAnswer: false,
                    pendingCoreRequestTrigger: string.Empty);
                StatusText.Text = step.IsFinal
                    ? L("Status.ChoiceScenarioTaskCardReady")
                    : L("Status.ChoiceScenarioStepReady");
                return;
            }

            _choiceScenarioInvalidJsonCount++;
            if (answerCommitted)
            {
                _choiceScenarioState.RemoveLastAnswer();
            }
            _choiceScenarioLog?.Write("scenario_structure_error", new
            {
                Error = generation.Error,
                Raw = generation.RawResponse,
                Count = _choiceScenarioInvalidJsonCount
            });
            ChoiceScenarioStatusText.Text = L("ChoiceScenario.StructureError");
            StatusText.Text = L("Status.ChoiceScenarioStructureError");
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
        }
        catch (OperationCanceledException)
        {
            _choiceScenarioLog?.Write("scenario_request_cancelled");
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("scenario_core_unavailable", new
            {
                Reason = "Exception while requesting core step",
                ErrorType = ex.GetType().FullName,
                ex.Message
            });
            var fallbackStep = _choiceScenarioService.CreateFallbackStep(_choiceScenarioState.Answers, L);
            if (_choiceScenarioState.GetFingerprintCount(fallbackStep) > 0)
            {
                if (answerCommitted)
                {
                    _choiceScenarioState.RemoveLastAnswer();
                }
                ChoiceScenarioStatusText.Text = L("ChoiceScenario.CoreUnavailableStop");
                StatusText.Text = L("Status.ChoiceScenarioCoreUnavailable");
                SaveActiveSessionCheckpoint(
                    pendingCoreRequest: false,
                    pendingCoreRequestFinal: false,
                    pendingCoreRequestConsumesAnswer: false,
                    pendingCoreRequestTrigger: string.Empty);
                return;
            }

            _choiceScenarioState.AddStep(fallbackStep, consumedAnswer: answerCommitted);
            RenderChoiceScenarioStep(fallbackStep);
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false,
                pendingCoreRequestConsumesAnswer: false,
                pendingCoreRequestTrigger: string.Empty);
            StatusText.Text = L("Status.ChoiceScenarioCoreUnavailable");
        }
        finally
        {
            _choiceScenarioCts?.Dispose();
            _choiceScenarioCts = null;
            _choiceScenarioRequestInProgress = false;
            StopChoiceAiActivity();
            SetChoiceScenarioInteractionEnabled(true);
        }
    }

    private void StartChoiceAiActivity()
        => StartAiActivityOverlay();

    private void StartAiActivityOverlay(Media.Color? accentColor = null)
    {
        GlobalFileViewerButton.IsEnabled = false;
        ChoiceAiActivityPanel.Visibility = Visibility.Visible;
        if (accentColor is { } color)
        {
            ChoiceMatrixRain.Start(color);
        }
        else
        {
            ChoiceMatrixRain.Start();
        }
    }

    private void StopChoiceAiActivity()
        => StopAiActivityOverlay();

    private void StopAiActivityOverlay()
    {
        ChoiceMatrixRain.Stop();
        ChoiceAiActivityPanel.Visibility = Visibility.Collapsed;
        GlobalFileViewerButton.IsEnabled = true;
    }

    private void RenderChoiceScenarioStep(ChoiceScenarioStep step)
    {
        CancelCoreSpeech(revealFullText: false, "step_replaced");
        ChoiceScenarioPreparationViewbox.Visibility = Visibility.Visible;
        ExecutorResultPanel.Visibility = Visibility.Collapsed;
        _executorPracticalLayoutActive = false;
        _currentChoiceScenarioStep = step;
        ChoiceScenarioQuestionMeasureText.Text = step.Question;
        ChoiceScenarioCoreThoughtMeasureText.Text = step.CoreThought;
        AutomationProperties.SetName(ChoiceScenarioQuestionText, step.Question);
        AutomationProperties.SetName(ChoiceScenarioCoreThoughtText, step.CoreThought);
        ChoiceScenarioQuestionText.Text = step.Question;
        ChoiceScenarioCoreThoughtText.Text = step.CoreThought;
        var stepStatus = step.IsFinal
            ? L("ChoiceScenario.FinalStatus")
            : L("ChoiceScenario.StepStatus");
        if (!step.IsFinal
            && !string.Equals(step.StepType, "budget_setup", StringComparison.Ordinal)
            && _choiceScenarioState.StepBudget is { } budget)
        {
            var currentQuestion = Math.Min(budget.MaximumSteps, _choiceScenarioState.SubstantiveStepsUsed + 1);
            var progress = budget.IsAutomatic
                ? LF("ChoiceScenario.BudgetProgressAutomatic", currentQuestion, budget.MaximumSteps)
                : LF("ChoiceScenario.BudgetProgress", currentQuestion, budget.MaximumSteps);
            stepStatus += Environment.NewLine + progress;
        }

        ChoiceScenarioStatusText.Text = stepStatus;

        _choiceScenarioOptions.Clear();
        foreach (var option in step.Options.Take(6))
        {
            _choiceScenarioOptions.Add(option);
        }

        ChoiceCustomOptionButton.Visibility = step.AllowCustom ? Visibility.Visible : Visibility.Collapsed;
        ChoiceGoFinalButton.Visibility = !step.IsFinal && _choiceScenarioState.Answers.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChoiceCustomInputPanel.Visibility = Visibility.Collapsed;
        RenderChoiceScenarioSummary(step);
        if (!step.IsFinal
            && _appSettings.CoreVoice.Enabled
            && _coreSpeechCoordinator.IsAvailable
            && (!string.IsNullOrWhiteSpace(step.Question) || !string.IsNullOrWhiteSpace(step.CoreThought)))
        {
            StartChoiceScenarioSpeech(step);
        }
    }

    private void SetChoiceScenarioInteractionEnabled(bool enabled)
    {
        var answersEnabled = enabled && !_coreSpeechPresentationActive;
        ChoiceOptionsItemsControl.IsEnabled = answersEnabled;
        ChoiceOptionsItemsControl.Opacity = answersEnabled ? 1.0 : 0.58;
        ChoiceCustomOptionButton.IsEnabled = answersEnabled;
        ChoiceCustomSubmitButton.IsEnabled = answersEnabled;
        ChoiceGoFinalButton.IsEnabled = answersEnabled;
        ChoiceSessionFilesPanel.IsEnabled = answersEnabled;
        BackFromChoiceScenarioButton.IsEnabled = enabled;
        CancelChoiceScenarioButton.IsEnabled = enabled;
    }

    private void StartChoiceScenarioSpeech(ChoiceScenarioStep step)
    {
        _coreSpeechCts = new CancellationTokenSource();
        var presentationId = Interlocked.Increment(ref _coreSpeechPresentationId);
        _coreSpeechPresentationActive = true;
        ChoiceScenarioQuestionText.Text = string.Empty;
        ChoiceScenarioCoreThoughtText.Text = string.Empty;
        SetChoiceScenarioInteractionEnabled(!_choiceScenarioRequestInProgress);

        var request = new CoreSpeechRequest(
            [
                new CoreSpeechSegment("coreThought", step.CoreThought),
                new CoreSpeechSegment("question", step.Question)
            ],
            _appSettings.LanguageCode,
            _appSettings.CoreVoice,
            $"uncertainty:{step.StepType}");
        var progress = new Progress<CoreSpeechProgress>(value =>
        {
            if (presentationId != _coreSpeechPresentationId)
            {
                return;
            }

            ChoiceScenarioCoreThoughtText.Text = VisibleText(
                step.CoreThought,
                value.VisibleCharacters.GetValueOrDefault("coreThought"));
            ChoiceScenarioQuestionText.Text = VisibleText(
                step.Question,
                value.VisibleCharacters.GetValueOrDefault("question"));
        });

        _ = PresentChoiceScenarioSpeechAsync(step, request, progress, presentationId, _coreSpeechCts);
    }

    private async Task PresentChoiceScenarioSpeechAsync(
        ChoiceScenarioStep step,
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        long presentationId,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            var result = await _coreSpeechCoordinator.PresentAsync(
                request,
                progress,
                _choiceScenarioLog,
                cancellationSource.Token);
            if (presentationId != _coreSpeechPresentationId)
            {
                return;
            }

            if (!result.Completed && !result.Skipped)
            {
                _choiceScenarioLog?.Write("core_voice_disabled_for_session", new
                {
                    request.Source,
                    result.ErrorCode
                });
            }
        }
        finally
        {
            if (presentationId == _coreSpeechPresentationId)
            {
                ChoiceScenarioCoreThoughtText.Text = step.CoreThought;
                ChoiceScenarioQuestionText.Text = step.Question;
                _coreSpeechPresentationActive = false;
                _coreSpeechCts = null;
                SetChoiceScenarioInteractionEnabled(!_choiceScenarioRequestInProgress);
            }

            cancellationSource.Dispose();
        }
    }

    private void CancelCoreSpeech(bool revealFullText, string reason)
    {
        if (_coreSpeechCts is null && !_coreSpeechPresentationActive)
        {
            return;
        }

        Interlocked.Increment(ref _coreSpeechPresentationId);
        var cancellationSource = _coreSpeechCts;
        _coreSpeechCts = null;
        cancellationSource?.Cancel();
        _coreSpeechCoordinator.Cancel();
        _coreSpeechPresentationActive = false;
        if (revealFullText && _currentChoiceScenarioStep is { } step)
        {
            ChoiceScenarioCoreThoughtText.Text = step.CoreThought;
            ChoiceScenarioQuestionText.Text = step.Question;
        }

        _choiceScenarioLog?.Write("core_voice_cancelled", new { Reason = reason });
        SetChoiceScenarioInteractionEnabled(!_choiceScenarioRequestInProgress);
    }

    private static string VisibleText(string text, int visibleCharacters) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text[..Math.Clamp(visibleCharacters, 0, text.Length)];

    private void CoreVoiceToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.CoreVoice.Enabled = !_appSettings.CoreVoice.Enabled;
        _appSettingsStore.Save(_appSettings);
        _isApplyingCoreVoiceSettings = true;
        CoreVoiceEnabledCheckBox.IsChecked = _appSettings.CoreVoice.Enabled;
        _isApplyingCoreVoiceSettings = false;

        if (!_appSettings.CoreVoice.Enabled)
        {
            CancelCoreSpeech(revealFullText: true, "scenario_muted");
        }
        else if (_currentChoiceScenarioStep is { IsFinal: false } step)
        {
            StartChoiceScenarioSpeech(step);
        }

        UpdateCoreVoiceControls();
    }

    private void RenderChoiceScenarioSummary(ChoiceScenarioStep step)
    {
        if (!step.SummaryLines.Any() && step.TaskCard is null)
        {
            ChoiceScenarioSummaryPanel.Visibility = Visibility.Collapsed;
            ChoiceScenarioSummaryText.Text = string.Empty;
            ExecutorPrepareButton.Visibility = Visibility.Collapsed;
            ExecutorPreparationActionsPanel.Visibility = Visibility.Collapsed;
            ExecutorContinueAvailableButton.Visibility = Visibility.Collapsed;
            ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
            ExecutorResultPanel.Visibility = Visibility.Collapsed;
            ExecutorCandidateChoicePanel.Visibility = Visibility.Collapsed;
            ExecutionRoutePanel.Visibility = Visibility.Collapsed;
            _executorCandidateOptions.Clear();
            return;
        }

        var builder = new StringBuilder();
        foreach (var line in step.SummaryLines)
        {
            builder.AppendLine(line);
        }

        if (step.TaskCard is not null)
        {
            var card = step.TaskCard;
            builder.AppendLine();
            builder.AppendLine(LF("ChoiceScenario.Card.Goal", card.Goal));
            builder.AppendLine(LF("ChoiceScenario.Card.Internet", card.NeedsWeb ? L("Common.Yes") : L("Common.No")));
            if (card.RequiredTools.Count > 0)
            {
                builder.AppendLine(LF("ChoiceScenario.Card.RequiredTools", string.Join(", ", card.RequiredTools)));
            }
            if (card.CapabilityProfile.Dimensions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(L("ChoiceScenario.Card.CapabilityProfile"));
                foreach (var dimension in card.CapabilityProfile.Dimensions)
                {
                    var label = L($"ChoiceScenario.Dimension.{dimension.Dimension}");
                    var values = dimension.Values.Count == 0
                        ? dimension.Status
                        : string.Join(", ", dimension.Values);
                    builder.AppendLine($"{label}: {values}");
                }
            }
            builder.AppendLine();
            builder.AppendLine(L("ChoiceScenario.Card.Prompt"));
            builder.AppendLine(card.PromptForExecutor);
        }

        ChoiceScenarioSummaryPanel.Visibility = Visibility.Visible;
        ChoiceScenarioSummaryText.Text = builder.ToString().Trim();
        _selectedExecutorCandidate = null;
        RenderExecutionRoute(step.TaskCard);
        RenderExecutorCandidates(step.TaskCard);
        ExecutorPrepareButton.Visibility = Visibility.Collapsed;
        ExecutorPreparationActionsPanel.Visibility = Visibility.Collapsed;
        ExecutorContinueAvailableButton.Visibility = Visibility.Collapsed;
        ExecutorPrepareButton.IsEnabled = true;
        ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
        ExecutorResultPanel.Visibility = Visibility.Collapsed;
        ExecutorDownloadProgressBar.Value = 0;
        _pendingExecutorArtifact = null;
        _pendingExecutorComponentPlan = null;
    }

    private void RenderExecutorCandidates(ChoiceTaskCard? card)
    {
        _executorCandidateOptions.Clear();
        if (card is null || card.ExecutorCandidates.Count == 0)
        {
            ExecutorCandidateChoicePanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var candidate in card.ExecutorCandidates)
        {
            var selected = ReferenceEquals(candidate, _selectedExecutorCandidate);
            var recommendation = selected
                ? L("Executor.Selected")
                : candidate.IsRecommended
                    ? L("Executor.CorePreferred")
                    : string.Empty;
            _executorCandidateOptions.Add(new ChoiceExecutorCandidateDisplay(
                candidate,
                candidate.Model,
                BuildExecutorAvailabilityStatus(candidate),
                string.Equals(
                    _localizationService.CurrentLanguageCode,
                    "en",
                    StringComparison.OrdinalIgnoreCase)
                    ? candidate.SemanticDescriptionEn
                    : candidate.SemanticDescriptionRu,
                LF("Executor.Advantage", BuildExecutorVerifiedAdvantage(candidate)),
                LF("Executor.Limitation", BuildExecutorVerifiedLimitation(candidate)),
                recommendation,
                selected || (_selectedExecutorCandidate is null && candidate.IsRecommended)));
        }

        ExecutorCandidateItemsControl.IsEnabled = true;
        ExecutorCandidateChoicePanel.Visibility = Visibility.Visible;
    }

    private void RenderExecutionRoute(ChoiceTaskCard? card)
    {
        if (card?.ExecutionRoute is not { } route)
        {
            ExecutionRoutePanel.Visibility = Visibility.Collapsed;
            ExecutionRouteDetailsText.Text = string.Empty;
            return;
        }

        var builder = new StringBuilder();
        if (route.SourceFormats.Count > 0)
        {
            builder.AppendLine(LF(
                "ExecutionRoute.SourceFormats",
                string.Join(", ", route.SourceFormats)));
        }

        if (card.WorkPatterns.Selections.Count > 0)
        {
            builder.AppendLine(LF(
                "ExecutionRoute.PatternMatches",
                string.Join(
                    ", ",
                    card.WorkPatterns.Selections.Select(selection =>
                        $"{selection.PatternId} ({Math.Clamp(selection.MatchPercent, 0, 100)}%)"))));
        }

        builder.AppendLine(LF(
            "ExecutionRoute.Artifact",
            card.ArtifactContract.ArtifactKind,
            card.ArtifactContract.PreferredExtension));
        builder.AppendLine(LF(
            "ExecutionRoute.SelectedLevel",
            L($"ExecutionRoute.Level.{card.ExecutionBundle.SelectedRouteLevel}")));
        builder.AppendLine(LF(
            "ExecutionRoute.DownloadCount",
            card.ExecutionBundle.AcquisitionPlan.Items.Count));
        builder.AppendLine(LF(
            "ExecutionRoute.OutcomeCoverage",
            route.CoveredOutcomeActionCount,
            route.RequiredOutcomeActionCount,
            Math.Clamp(route.OutcomeCoveragePercent, 0, 100)));
        if (route.MissingOutcomeActionIds.Count > 0)
        {
            builder.AppendLine(LF(
                "ExecutionRoute.MissingOutcomeActions",
                string.Join(", ", route.MissingOutcomeActionIds)));
        }

        foreach (var requirement in route.Requirements)
        {
            var binding = route.Resolution.Bindings.FirstOrDefault(item =>
                string.Equals(
                    item.RequestedCapabilityId,
                    requirement.Request.Id,
                    StringComparison.OrdinalIgnoreCase));
            builder.AppendLine(LF(
                "ExecutionRoute.Stage",
                L($"ExecutionRoute.Layer.{requirement.Layer}"),
                requirement.Request.Id,
                L($"ExecutionRoute.Status.{binding?.Status ?? CapabilityBindingStatuses.UnknownCapability}")));
        }

        if (card.ExternalDiscovery.Searches.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(L("ExecutionRoute.ExternalCandidates"));
            foreach (var search in card.ExternalDiscovery.Searches)
            {
                if (search.Candidates.Count == 0)
                {
                    builder.AppendLine(LF(
                        "ExecutionRoute.ExternalNoCandidates",
                        search.CapabilityId));
                    continue;
                }

                foreach (var candidate in search.Candidates.Take(2))
                {
                    builder.AppendLine(LF(
                        "ExecutionRoute.ExternalCandidate",
                        search.CapabilityId,
                        candidate.Title,
                        candidate.Url));
                }
            }
            builder.AppendLine(L("ExecutionRoute.ExternalWarning"));
        }

        ExecutionRouteTitleText.Text = L("ExecutionRoute.Title");
        ExecutionRouteStatusText.Text = route.IsExecutable
            ? L("ExecutionRoute.Ready")
            : card is not null
              && CanStartExecutorBundle(card)
                ? L("ExecutionRoute.RequiresPreparation")
                : L("ExecutionRoute.Blocked");
        ExecutionRouteDetailsText.Text = builder.ToString().Trim();
        ExecutionRoutePanel.Visibility = Visibility.Visible;
    }

    private void ExecutorCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ChoiceExecutorCandidate candidate }
            || _currentChoiceScenarioStep?.TaskCard is not { } card)
        {
            return;
        }

        _selectedExecutorCandidate = candidate;
        card.RecommendedExecutor = candidate.Model;
        card.ExecutorStatus = candidate.Status;
        card.ExecutorRole = candidate.Role;
        card.ExecutorCapabilityClass = candidate.CapabilityClass;
        card.ExecutorReason = BuildExecutorVerifiedReason(candidate);
        RenderExecutorCandidates(card);
        UpdateExecutorPreparationActions(card);
        StatusText.Text = card.ExecutionRoute.IsExecutable
            ? LF("Status.ExecutorCandidateSelected", candidate.Model)
            : LF(
                "Status.ExecutorAvailableRoute",
                candidate.Model,
                string.Join(", ", card.ExecutionRoute.Resolution.Bindings
                    .Where(binding => binding.Required && !binding.IsExecutable)
                    .Select(binding => binding.RequestedCapabilityId)));
        _choiceScenarioLog?.Write("executor_candidate_selected", candidate);
        SaveActiveSessionCheckpoint(
            pendingCoreRequest: false,
            pendingCoreRequestFinal: false);
    }

    private string BuildExecutorVerifiedAdvantage(ChoiceExecutorCandidate candidate)
    {
        if (candidate.Status == ChoiceExecutorCandidateStatuses.Installed)
        {
            return LF(
                "Executor.VerifiedInstalledAdvantage",
                string.IsNullOrWhiteSpace(candidate.RuntimeBackend)
                    ? ExecutionCompatibilityService.LlamaRuntime
                    : candidate.RuntimeBackend);
        }

        return string.Equals(
            candidate.CatalogMatchScope,
            ExecutionCompatibilityService.TaskProfileMatch,
            StringComparison.OrdinalIgnoreCase)
            ? LF("Executor.VerifiedTaskMatchAdvantage", candidate.Family)
            : LF("Executor.VerifiedFallbackAdvantage", candidate.Family);
    }

    private string BuildExecutorAvailabilityStatus(ChoiceExecutorCandidate candidate)
    {
        var match = string.Join(
            Environment.NewLine,
            LF(
                "Executor.CoordinatorMatch",
                Math.Clamp(candidate.CoordinatorMatchPercent, 0, 100)),
            LF(
                "Executor.RouteCoverage",
                Math.Clamp(candidate.RouteCoveragePercent, 0, 100)));
        if (candidate.UnresolvedCapabilities.Count > 0)
        {
            var availability = candidate.Status == ChoiceExecutorCandidateStatuses.Installed
                ? L("Executor.StatusInstalledIncomplete")
                : L("Executor.StatusDownloadIncomplete");
            return $"{match}\n{availability}";
        }

        var readyStatus = candidate.Status == ChoiceExecutorCandidateStatuses.Installed
            ? L("Executor.StatusInstalled")
            : L("Executor.StatusDownload");
        return $"{match}\n{readyStatus}";
    }

    private string BuildExecutorVerifiedLimitation(ChoiceExecutorCandidate candidate)
    {
        if (candidate.UnresolvedCapabilities.Count > 0)
        {
            return LF(
                "Executor.VerifiedUnresolvedCapabilities",
                string.Join(", ", candidate.UnresolvedCapabilities));
        }

        if (candidate.MissingCapabilities.Count > 0)
        {
            return LF(
                "Executor.VerifiedMissingCapabilities",
                string.Join(", ", candidate.MissingCapabilities));
        }

        return L("Executor.VerifiedCoordinatorLimitation");
    }

    private string BuildExecutorVerifiedReason(ChoiceExecutorCandidate candidate) =>
        candidate.UnresolvedCapabilities.Count > 0
            ? L("Executor.VerifiedIncompleteReason")
            : candidate.Status == ChoiceExecutorCandidateStatuses.Installed
            ? L("Executor.VerifiedInstalledReason")
            : string.Equals(
                candidate.CatalogMatchScope,
                ExecutionCompatibilityService.TaskProfileMatch,
                StringComparison.OrdinalIgnoreCase)
                ? L("Executor.VerifiedTaskMatchReason")
                : L("Executor.VerifiedFallbackReason");

    private void UpdateExecutorPreparationActions(ChoiceTaskCard card)
    {
        var hasSelectedCandidate = _selectedExecutorCandidate is not null;
        var selectedInstalled = _selectedExecutorCandidate?.Status
            == ChoiceExecutorCandidateStatuses.Installed;
        var needsAcquisition = HasTrustedExecutorAcquisition(card);
        var needsExternalDiscovery = card.ExecutionRoute.HasBlockedRequirements
            && !needsAcquisition;
        ExecutorPrepareButton.Content = needsExternalDiscovery
            ? L("Executor.FindExternalComponents")
            : needsAcquisition
                ? L("Executor.DownloadAndImprove")
                : L("Executor.Prepare");
        ExecutorPrepareButton.IsEnabled = hasSelectedCandidate
            && (CanStartExecutorBundle(card) || needsExternalDiscovery);
        ExecutorContinueAvailableButton.Content = L("Executor.ContinueAvailable");
        ExecutorContinueAvailableButton.Visibility = hasSelectedCandidate
            && selectedInstalled
            && !card.ExecutionRoute.IsExecutable
                ? Visibility.Visible
                : Visibility.Collapsed;
        ExecutorContinueAvailableButton.IsEnabled = selectedInstalled
            && card.ExecutionBundle.DegradedRoute.IsStartable;
        ExecutorPreparationActionsPanel.Visibility = hasSelectedCandidate
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExecutorPrepareButton.Visibility = hasSelectedCandidate
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool HasTrustedExecutorAcquisition(ChoiceTaskCard card) =>
        card.ExecutionRoute.Resolution.Acquisition.Items.Any(item => !item.AlreadyAvailable)
        || card.ExecutionBundle.AcquisitionPlan.RequiresConfirmation;

    private void RevalidateExecutorExecutionBundle(
        ChoiceTaskCard card,
        string reason,
        string logEvent)
    {
        var promptManifest = _sessionFileManifestService.CreatePromptManifest(
            GetActiveFileManifest());
        var selectedPatterns = new WorkPatternCatalogService().ResolveSelected(
            card.WorkPatterns);
        card.ArtifactContract = new ArtifactContractBuilder().Build(
            selectedPatterns,
            promptManifest);
        card.OutcomeContract = new ExecutionOutcomeContractService().Build(
            card.Goal,
            card.CapabilityProfile,
            promptManifest,
            selectedPatterns,
            card.ArtifactContract);
        card.ExecutionRoute = new ExecutionRoutePlannerService().Build(
            card.CapabilityProfile,
            promptManifest,
            reason,
            selectedPatterns,
            card.ExecutionPlan,
            card.OutcomeContract);
        card.ExecutionBundle = new ExecutionBundlePlannerService().Build(
            card.WorkPatterns,
            card.ArtifactContract,
            card.ExecutionRoute);
        RenderExecutionRoute(card);
        _choiceScenarioLog?.Write(logEvent, new
        {
            card.WorkPatterns,
            card.ArtifactContract,
            card.OutcomeContract,
            card.ExecutionBundle
        });
    }

    private async void ExecutorPrepareButton_Click(object sender, RoutedEventArgs e)
    {
        var useAvailableComponents = _startExecutorWithAvailableComponents;
        _startExecutorWithAvailableComponents = false;
        var card = _currentChoiceScenarioStep?.TaskCard;
        if (card is null
            || _selectedExecutorCandidate is null
            || string.IsNullOrWhiteSpace(card.RecommendedExecutor))
        {
            StatusText.Text = L("Status.ExecutorCandidateRequired");
            return;
        }

        RevalidateExecutorExecutionBundle(
            card,
            "Revalidate the execution route before preparing the selected coordinator.",
            "executor_execution_bundle_revalidated");
        var trustedAcquisitionAvailable = HasTrustedExecutorAcquisition(card);
        if (useAvailableComponents)
        {
            SelectDegradedExecutorRoute(card);
        }
        else if (card.ExecutionRoute.HasBlockedRequirements
                 && !trustedAcquisitionAvailable)
        {
            ExecutorPrepareButton.IsEnabled = false;
            ExecutorContinueAvailableButton.IsEnabled = false;
            ExecutorCandidateItemsControl.IsEnabled = false;
            _executorCts?.Dispose();
            _executorCts = new CancellationTokenSource();
            await DiscoverMissingRouteComponentsAsync(card, _executorCts.Token);
            ExecutorCandidateItemsControl.IsEnabled = true;
            UpdateExecutorPreparationActions(card);
            return;
        }
        else if (!CanStartExecutorBundle(card)
                 && !trustedAcquisitionAvailable)
        {
            StatusText.Text = L("ExecutionRoute.Blocked");
            return;
        }

        ExecutorPrepareButton.IsEnabled = false;
        ExecutorContinueAvailableButton.IsEnabled = false;
        ExecutorCandidateItemsControl.IsEnabled = false;
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        StatusText.Text = L("Status.ExecutorResolving");
        try
        {
            _pendingExecutorArtifact = await _executorWorkflowService.ResolveAsync(
                card.RecommendedExecutor,
                _storageSettings,
                _executorCts.Token);
            _choiceScenarioLog?.Write("executor_artifact_resolved", _pendingExecutorArtifact);
            _pendingExecutorComponentPlan = card.ExecutionRoute.Resolution.Acquisition;
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
            if (_pendingExecutorArtifact.IsInstalled)
            {
                if (!useAvailableComponents
                    && !await EnsureExecutorComponentsReadyAsync(
                             _pendingExecutorComponentPlan,
                             confirmationAlreadyGranted: false,
                             _executorCts.Token))
                {
                    RestoreExecutorPreparationControls();
                    return;
                }
                RevalidateExecutorExecutionBundle(
                    card,
                    "Revalidate the execution route after preparing trusted components.",
                    "executor_execution_bundle_after_component_preparation");
                if (card.ExecutionRoute.HasBlockedRequirements)
                {
                    SelectDegradedExecutorRoute(card);
                }
                await RunExecutorAsync(_pendingExecutorArtifact, card);
                return;
            }

            var parameterCount = ModelHardwareCompatibilityService.TryReadParameterCountFromName(card.RecommendedExecutor);
            var compatibility = ModelHardwareCompatibilityService.Assess(
                parameterCount,
                _computerPassportService.EnsurePassport(),
                _userProfile.WorkloadMode);
            var modelDetails = LF(
                "Executor.DownloadDetails",
                _pendingExecutorArtifact.RepoId,
                _pendingExecutorArtifact.FileName,
                _pendingExecutorArtifact.Quantization,
                FormatBytes(_pendingExecutorArtifact.SizeBytes),
                string.IsNullOrWhiteSpace(_pendingExecutorArtifact.License)
                    ? L("Common.Unknown")
                    : _pendingExecutorArtifact.License,
                Path.Combine(
                    _storageSettings.Models.Locations.FirstOrDefault()?.Path ?? L("Common.Unknown"),
                    "Executors"),
                $"{compatibility.Status}: {compatibility.Reason}");
            ExecutorDownloadDetailsText.Text = modelDetails
                + BuildExecutorComponentPlanText(_pendingExecutorComponentPlan);
            ExecutorDownloadPanel.Visibility = Visibility.Visible;
            ExecutorConfirmDownloadButton.IsEnabled = true;
            ExecutorCancelDownloadButton.IsEnabled = true;
            StatusText.Text = L("Status.ExecutorConfirmationRequired");
            _choiceScenarioLog?.Write("executor_download_confirmation_requested", _pendingExecutorArtifact);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Status.ExecutorCancelled");
            ExecutorPrepareButton.IsEnabled = true;
            ExecutorContinueAvailableButton.IsEnabled = true;
            ExecutorCandidateItemsControl.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("executor_artifact_error", new { ex.Message, ErrorType = ex.GetType().FullName });
            StatusText.Text = LF("Status.ExecutorResolveFailed", ex.Message);
            ExecutorPrepareButton.IsEnabled = true;
            ExecutorContinueAvailableButton.IsEnabled = true;
            ExecutorCandidateItemsControl.IsEnabled = true;
        }
    }

    private void ExecutorContinueAvailableButton_Click(object sender, RoutedEventArgs e)
    {
        _startExecutorWithAvailableComponents = true;
        _choiceScenarioLog?.Write("executor_continue_available_selected", new
        {
            Model = _selectedExecutorCandidate?.Model,
            Route = ExecutionRouteLevels.Degraded
        });
        ExecutorPrepareButton_Click(sender, e);
    }

    private async void ExecutorConfirmDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingExecutorArtifact is null || _currentChoiceScenarioStep?.TaskCard is not { } card)
        {
            return;
        }

        RegisterPendingSandboxExecutorArtifact(_pendingExecutorArtifact, card);

        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        ExecutorConfirmDownloadButton.IsEnabled = false;
        ExecutorPrepareButton.IsEnabled = false;
        ExecutorContinueAvailableButton.IsEnabled = false;
        var progress = new Progress<ExecutorDownloadProgress>(UpdateExecutorDownloadProgress);
        try
        {
            _choiceScenarioLog?.Write("executor_download_started", _pendingExecutorArtifact);
            var installed = await _executorWorkflowService.InstallAsync(
                _pendingExecutorArtifact,
                _storageSettings,
                progress,
                _executorCts.Token);
            _choiceScenarioLog?.Write("executor_download_completed", installed);
            ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
            if (!await EnsureExecutorComponentsReadyAsync(
                    _pendingExecutorComponentPlan,
                    confirmationAlreadyGranted: true,
                    _executorCts.Token))
            {
                RestoreExecutorPreparationControls();
                return;
            }
            RevalidateExecutorExecutionBundle(
                card,
                "Revalidate the execution route after downloading the coordinator and trusted components.",
                "executor_execution_bundle_after_download");
            if (card.ExecutionRoute.HasBlockedRequirements)
            {
                SelectDegradedExecutorRoute(card);
            }
            if (_resumeExecutorAfterDownload
                && _activeResumableSession?.Executor is { } checkpoint
                && _sessionRestorationContext is { } restoration)
            {
                _resumeExecutorAfterDownload = false;
                RestoreExecutorFromArchive(installed, checkpoint, restoration);
                return;
            }

            await RunExecutorAsync(installed, card);
        }
        catch (OperationCanceledException)
        {
            _choiceScenarioLog?.Write("executor_download_cancelled");
            StatusText.Text = L("Status.ExecutorDownloadCancelled");
            ExecutorConfirmDownloadButton.IsEnabled = true;
            ExecutorPrepareButton.IsEnabled = true;
            ExecutorContinueAvailableButton.IsEnabled = true;
            ExecutorCandidateItemsControl.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _choiceScenarioLog?.Write("executor_download_error", new { ex.Message, ErrorType = ex.GetType().FullName });
            StatusText.Text = LF("Status.ExecutorDownloadFailed", ex.Message);
            ExecutorConfirmDownloadButton.IsEnabled = true;
            ExecutorPrepareButton.IsEnabled = true;
            ExecutorContinueAvailableButton.IsEnabled = true;
            ExecutorCandidateItemsControl.IsEnabled = true;
        }
    }

    private static bool CanStartExecutorBundle(ChoiceTaskCard card) =>
        card.ExecutionRoute.IsExecutable
        || card.ExecutionRoute.RequiresAcquisition
        || card.ExecutionBundle.DegradedRoute.IsStartable;

    private void RestoreExecutorPreparationControls()
    {
        ExecutorPrepareButton.IsEnabled = true;
        ExecutorContinueAvailableButton.IsEnabled = true;
        ExecutorCandidateItemsControl.IsEnabled = true;
    }

    private void SelectDegradedExecutorRoute(ChoiceTaskCard card)
    {
        card.ExecutionBundle.SelectedRouteLevel = ExecutionRouteLevels.Degraded;
        _choiceScenarioLog?.Write("executor_degraded_route_selected", new
        {
            card.ExecutionBundle.SelectedRouteLevel,
            card.ExecutionBundle.DegradedRoute.MissingCapabilities,
            card.ArtifactContract.EmergencyAcceptableResult
        });
        StatusText.Text = L("ExecutionRoute.RequiresPreparation");
    }

    private void ExecutorCancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _executorCts?.Cancel();
        ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
        ExecutorPrepareButton.IsEnabled = true;
        ExecutorContinueAvailableButton.IsEnabled = true;
        ExecutorCandidateItemsControl.IsEnabled = true;
        StatusText.Text = L("Status.ExecutorDownloadCancelled");
    }

    private void UpdateExecutorDownloadProgress(ExecutorDownloadProgress progress)
    {
        var percent = progress.TotalBytes <= 0
            ? 0
            : Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0, 100);
        ExecutorDownloadProgressBar.Value = percent;
        StatusText.Text = progress.Stage switch
        {
            "verifying" => L("Status.ExecutorVerifying"),
            "runtime_validation" => L("Status.ExecutorRuntimeValidation"),
            "installed" => L("Status.ExecutorInstalled"),
            _ => LF(
                "Status.ExecutorDownloading",
                FormatBytes(progress.DownloadedBytes),
                FormatBytes(progress.TotalBytes),
                FormatBytes(progress.BytesPerSecond) + L("Units.PerSecond"))
        };
    }

    private void RestoreExecutorFromArchive(
        ExecutorModelArtifact artifact,
        ExecutorSessionCheckpoint checkpoint,
        SessionRestorationContext restoration)
    {
        PrepareExecutorWorkspaceForRestoredRun();
        try
        {
            _pendingExecutorArtifact = artifact;
            if (_activeResumableSession is not null)
            {
                _activeResumableSession.SelectedExecutorModel = artifact.RepoId;
                _activeResumableSession.ExecutorArtifact = artifact;
            }

            var turn = _executorWorkflowService.Restore(
                checkpoint,
                artifact,
                GetActiveFileManifest(),
                _storageSettings,
                restoration);
            DisplayExecutorResponse(turn, speak: false);
            SaveActiveSessionCheckpoint(
                pendingCoreRequest: false,
                pendingCoreRequestFinal: false);
            StatusText.Text = L("Status.PreviousWorkExecutorRestored");
        }
        catch
        {
            ExecutorResultPanel.Visibility = Visibility.Collapsed;
            ChoiceScenarioPreparationViewbox.Visibility = Visibility.Visible;
            if (_currentChoiceScenarioStep?.TaskCard is { } card)
            {
                UpdateExecutorPreparationActions(card);
            }
            throw;
        }
        finally
        {
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private void PrepareExecutorWorkspaceForRestoredRun()
    {
        _executorResultWindow?.Close();
        _executorResultWindow = null;
        CloseSessionTreeWindow();
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        _choiceScenarioRuntimeService?.Stop();
        ExecutorCandidateChoicePanel.Visibility = Visibility.Collapsed;
        ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
        ExecutorPrepareButton.Visibility = Visibility.Collapsed;
        ExecutorPreparationActionsPanel.Visibility = Visibility.Collapsed;
        ExecutorContinueAvailableButton.Visibility = Visibility.Collapsed;
        ChoiceScenarioPreparationViewbox.Visibility = Visibility.Collapsed;
        ExecutorResultPanel.Visibility = Visibility.Visible;
        ExecutorLivePreviewPanel.Visibility = Visibility.Collapsed;
        ExecutorResponseDock.Visibility = Visibility.Collapsed;
        ExecutorThoughtPanel.Visibility = Visibility.Collapsed;
        ExecutorClarificationOptionsItemsControl.Visibility = Visibility.Collapsed;
        ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
        ExecutorRequestResultButton.Visibility = Visibility.Collapsed;
        ExecutorStageRecommendationText.Visibility = Visibility.Collapsed;
        _currentExecutorTurn = null;
        _executorSessionFinishedByUser = false;
        _executorFinalizationSuggested = false;
        _executorCurrentResultSummary = string.Empty;
        _executorCurrentStageId = ExecutorStageIds.TaskDefinition;
        _executorPracticalLayoutActive = false;
        ApplyExecutorWorkspaceLayout(animate: false);
        SetExecutorFinalizationSuggested(suggested: false, animate: false);
        UpdateExecutorStageControls();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
    }

    private async Task RunExecutorAsync(ExecutorModelArtifact artifact, ChoiceTaskCard card)
    {
        _executorResultWindow?.Close();
        _executorResultWindow = null;
        CloseSessionTreeWindow();
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        _choiceScenarioRuntimeService?.Stop();
        ExecutorCandidateChoicePanel.Visibility = Visibility.Collapsed;
        ExecutorDownloadPanel.Visibility = Visibility.Collapsed;
        ExecutorPrepareButton.Visibility = Visibility.Collapsed;
        ExecutorPreparationActionsPanel.Visibility = Visibility.Collapsed;
        ExecutorContinueAvailableButton.Visibility = Visibility.Collapsed;
        ChoiceScenarioPreparationViewbox.Visibility = Visibility.Collapsed;
        ExecutorResultPanel.Visibility = Visibility.Visible;
        ExecutorResultTitleText.Text = L("Executor.ResultTitle");
        ExecutorResultText.Text = L("Executor.Starting");
        ExecutorLivePreviewText.Text = string.Empty;
        ExecutorLivePreviewPanel.Visibility = Visibility.Collapsed;
        ExecutorResponseDock.Visibility = Visibility.Collapsed;
        ExecutorThoughtPanel.Visibility = Visibility.Collapsed;
        ExecutorClarificationOptionsItemsControl.Visibility = Visibility.Collapsed;
        ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
        ExecutorResponseDock.Visibility = Visibility.Collapsed;
        ExecutorRequestResultButton.Visibility = Visibility.Collapsed;
        ExecutorStageRecommendationText.Visibility = Visibility.Collapsed;
        _currentExecutorTurn = null;
        _executorSessionFinishedByUser = false;
        _executorFinalizationSuggested = false;
        _executorCurrentResultSummary = string.Empty;
        _executorCurrentStageId = ExecutorStageIds.TaskDefinition;
        _executorPracticalLayoutActive = false;
        ApplyExecutorWorkspaceLayout(animate: false);
        SetExecutorFinalizationSuggested(suggested: false, animate: false);
        UpdateExecutorStageControls();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorRunning");
        var streamProgress = CreateMatrixStreamProgress();
        var handoff = BuildExecutorHandoff(artifact, card);
        try
        {
            if (_activeResumableSession is not null)
            {
                _activeResumableSession.SelectedExecutorModel = artifact.RepoId;
                _activeResumableSession.ExecutorArtifact = artifact;
                _activeResumableSession.ExecutorHandoff = handoff;
                SaveActiveSessionCheckpoint(
                    pendingCoreRequest: false,
                    pendingCoreRequestFinal: false);
            }

            var result = await _executorWorkflowService.ExecuteAsync(
                artifact,
                handoff,
                GetActiveFileManifest(),
                _storageSettings,
                streamProgress,
                _executorCts.Token);
            DisplayExecutorResponse(result);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                _executorWorkflowService.Stop("start_cancelled");
                CloseSessionTreeWindow();
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_failed", new { ex.Message, ErrorType = ex.GetType().FullName });
            _executorWorkflowService.Stop("start_failed");
            CloseSessionTreeWindow();
            StatusText.Text = LF("Status.ExecutorFailed", ex.Message);
            ExecutorResultPanel.Visibility = Visibility.Collapsed;
            ChoiceScenarioPreparationViewbox.Visibility = Visibility.Visible;
            UpdateExecutorPreparationActions(card);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private async void ExecutorClarificationOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ChoiceScenarioOption option })
        {
            return;
        }

        if (string.Equals(option.Id, "executor_custom", StringComparison.Ordinal))
        {
            ShowCustomChoiceMenu(
                (System.Windows.Controls.Button)sender,
                executorContext: true);
            return;
        }

        if (option.IsExecutorAction)
        {
            await ContinueExecutorActionAsync(option);
            return;
        }

        await ContinueExecutorAsync(option.Title);
    }

    private async void ExecutorCustomSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = ExecutorCustomInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(answer))
        {
            StatusText.Text = L("Status.ExecutorCustomEmpty");
            return;
        }

        ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
        await ContinueExecutorAsync(answer);
    }

    private async void ExecutorNextStageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_executorWorkflowService.BriefConfirmed
            && _executorCurrentStageId == ExecutorStageIds.TaskDefinition)
        {
            await ConfirmExecutorBriefAsync();
        }
    }

    private async Task ContinueExecutorAsync(string answer)
    {
        CancelExecutorSpeech(revealFullText: true, "answer_selected");
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorRunning");
        try
        {
            var result = await _executorWorkflowService.ContinueAndRunAsync(
                answer,
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            DisplayExecutorResponse(result);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_failed", new { ex.Message, ErrorType = ex.GetType().FullName });
            StatusText.Text = LF("Status.ExecutorFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private async Task ContinueExecutorActionAsync(ChoiceScenarioOption option)
    {
        CancelExecutorSpeech(revealFullText: true, "action_selected");
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorRunning");
        try
        {
            var result = await _executorWorkflowService.ContinueApprovedActionAndRunAsync(
                new ExecutorTurnOption
                {
                    Title = option.Title,
                    Intent = option.Intent,
                    Action = option.Action,
                    TargetId = option.TargetId,
                    Effect = option.Effect,
                    IsRecommended = option.IsRecommended
                },
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            DisplayExecutorResponse(result);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write(
                "executor_action_failed",
                new
                {
                    option.Action,
                    option.TargetId,
                    ex.Message,
                    ErrorType = ex.GetType().FullName
                });
            StatusText.Text = LF("Status.ExecutorFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private async Task UpdateExecutorFileManifestAsync()
    {
        if (_activeResumableSession is null)
        {
            return;
        }

        CancelExecutorSpeech(revealFullText: true, "file_manifest_updated");
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorFilesUpdating");
        try
        {
            var result = await _executorWorkflowService.UpdateFileManifestAsync(
                _activeResumableSession.FileManifest,
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            DisplayExecutorResponse(result);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_file_manifest_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.ExecutorFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private async Task ConfirmExecutorBriefAsync()
    {
        CancelExecutorSpeech(revealFullText: true, "brief_confirmation");
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorBriefConfirming");
        try
        {
            var result = await _executorWorkflowService.ConfirmBriefAndRunAsync(
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            DisplayExecutorResponse(result);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_brief_confirmation_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.ExecutorFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private void DisplayExecutorResponse(ExecutorTurnResult turn, bool speak = true)
    {
        _currentExecutorTurn = turn;
        if (turn.Action == ExecutorTurnActions.RequestCapability)
        {
            ExecutorClarificationOptionsItemsControl.Visibility = Visibility.Collapsed;
            ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
            ExecutorResultPanel.Visibility = Visibility.Visible;
            ExecutorResultTitleText.Text = L("Components.ExecutorRequestTitle");
            ExecutorResultText.Text = turn.CapabilityReason;
            StatusText.Text = L("Components.ExecutorRequestStatus");
            Dispatcher.BeginInvoke(new Action(async () =>
                await HandleExecutorCapabilityRequestAsync(turn)));
            return;
        }
        if (ExecutorStageFlow.IsKnown(turn.StageId))
        {
            _executorCurrentStageId = turn.StageId;
        }

        var enteringPracticalStage = _executorCurrentStageId == ExecutorStageIds.PracticalClarification
            && !_executorPracticalLayoutActive;
        ApplyExecutorWorkspaceLayout(enteringPracticalStage);
        _executorClarificationOptions.Clear();
        ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
        ExecutorThoughtText.Text = turn.Thought;
        ExecutorThoughtPanel.Visibility = string.IsNullOrWhiteSpace(turn.Thought)
            ? Visibility.Collapsed
            : Visibility.Visible;
        var suggestsFinalization = string.Equals(
            turn.Action,
            ExecutorTurnActions.SuggestFinalization,
            StringComparison.Ordinal);
        var canFinalizeNow = suggestsFinalization || turn.CanFinalize;
        if (suggestsFinalization)
        {
            _executorClarificationOptions.Add(new ChoiceScenarioOption
            {
                Id = ExecutorContinueAfterReadyOptionId,
                Title = L("Executor.ContinueClarification")
            });
        }
        else
        {
            foreach (var option in turn.Options)
            {
                _executorClarificationOptions.Add(new ChoiceScenarioOption
                {
                    Id = "executor_response",
                    Title = option.Title,
                    Description = option.Effect,
                    Intent = option.Intent,
                    Action = option.Action,
                    TargetId = option.TargetId,
                    Effect = option.Effect,
                    IsRecommended = option.IsRecommended
                });
            }
        }

        _executorClarificationOptions.Add(new ChoiceScenarioOption
        {
            Id = "executor_custom",
            Title = L("ChoiceScenario.CustomOption")
        });

        ExecutorClarificationOptionsItemsControl.Visibility = _executorClarificationOptions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExecutorStageRecommendationText.Text = canFinalizeNow
            ? LF("Executor.ReadyToFinishReason", turn.CompletionReason)
            : L("Executor.StageRecommended");
        ExecutorStageRecommendationText.Visibility = canFinalizeNow
            || string.Equals(
                turn.Status,
                ExecutorTurnStatuses.StageReady,
                StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(turn.CurrentResultSummary))
        {
            _executorCurrentResultSummary = turn.CurrentResultSummary;
        }

        ExecutorLivePreviewText.Text = string.IsNullOrWhiteSpace(_executorCurrentResultSummary)
            ? L("Executor.LivePreviewWaiting")
            : _executorCurrentResultSummary;

        switch (turn.Status)
        {
            case ExecutorTurnStatuses.Working:
                ExecutorResultTitleText.Text = suggestsFinalization
                    ? L("Executor.ReadyToFinishTitle")
                    : L("Executor.WorkingTitle");
                ExecutorResultText.Text = suggestsFinalization
                    ? L("Executor.ReadyToFinishBody")
                    : turn.Question;
                StatusText.Text = canFinalizeNow
                    ? L("Status.ExecutorReadyToFinish")
                    : L("Status.ExecutorWorking");
                break;
            case ExecutorTurnStatuses.StageReady:
                ExecutorResultTitleText.Text = L("Executor.StageReadyTitle");
                ExecutorResultText.Text = string.IsNullOrWhiteSpace(turn.Question)
                    ? L("Executor.StageReadyBody")
                    : turn.Question;
                StatusText.Text = L("Status.ExecutorStageReady");
                break;
            case ExecutorTurnStatuses.Blocked:
                ExecutorResultTitleText.Text = L("Executor.BlockedTitle");
                ExecutorResultText.Text = FormatExecutorResult(turn);
                StatusText.Text = L("Status.ExecutorBlocked");
                break;
        }

        SetExecutorFinalizationSuggested(canFinalizeNow, animate: true);
        UpdateExecutorStageControls();
        ExecutorRequestResultButton.Visibility = _executorWorkflowService.BriefConfirmed
            && _executorCurrentStageId == ExecutorStageIds.PracticalClarification
            && !_executorSessionFinishedByUser
                ? Visibility.Visible
                : Visibility.Collapsed;
        ExecutorResponseDock.Visibility = _executorClarificationOptions.Count > 0
            || ExecutorNextStageButton.Visibility == Visibility.Visible
            || ExecutorFinishSessionDockButton.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        ExecutorResultPanel.Visibility = Visibility.Visible;
        if (speak
            && _executorVoiceEnabled
            && _executorSpeechCoordinator.IsAvailable
            && ShouldSpeakExecutorTurn(turn))
        {
            StartExecutorSpeech(turn);
        }
    }

    private ExecutorHandoffPackage BuildExecutorHandoff(ExecutorModelArtifact artifact, ChoiceTaskCard card)
    {
        var activeFileManifest = GetActiveFileManifest();
        var handoff = new ExecutorHandoffPackage
        {
            SuggestedDirection = card.Area,
            CapabilityProfile = card.CapabilityProfile.Clone(),
            Goal = card.Goal,
            Criteria = [.. card.Criteria],
            Constraints = [.. card.Constraints],
            NeedsWeb = card.NeedsWeb,
            RequiredTools = [.. card.RequiredTools],
            Prompt = card.PromptForExecutor,
            LanguageCode = _appSettings.LanguageCode,
            WorkloadMode = _userProfile.WorkloadMode,
            AnswerPreferences = _userProfile.AnswerPreferences,
            ParentCoreSessionId = _choiceScenarioLog?.SessionId ?? string.Empty,
            ParentRunId = _activeResumableSession?.CurrentRunId ?? string.Empty,
            WorkPatterns = card.WorkPatterns,
            ArtifactContract = card.ArtifactContract,
            OutcomeContract = card.OutcomeContract,
            ExecutionBundle = card.ExecutionBundle,
            FileManifest = _sessionFileManifestService.CreatePromptManifest(
                activeFileManifest,
                contentAccessAvailable: true),
            Unknowns =
            [
                "The exact subject or object of the user's work",
                "The concrete outcome the user expects",
                "Subject-specific constraints and source data"
            ]
        };
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "outcome_contract",
            Value = JsonSerializer.Serialize(card.OutcomeContract),
            Source = "program_execution_contract",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "selected_executor",
            Value = $"{artifact.RepoId}/{artifact.FileName}",
            Source = "program",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "language",
            Value = _appSettings.LanguageCode,
            Source = "program_settings",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "workload_mode",
            Value = _userProfile.WorkloadMode,
            Source = "user_profile",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "file_content_access",
            Value = activeFileManifest.Files.Any(file => file.IsAvailable)
                ? "available_via_safe_session_file_tools"
                : "no_available_session_files",
            Source = "program_file_manifest",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "available_file_capabilities",
            Value = string.Join(", ", _componentManager.GetAvailableCapabilities()),
            Source = "program_component_inventory",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "required_file_capabilities",
            Value = string.Join(", ", ComponentCapabilityMapper.FromProfile(card.CapabilityProfile)),
            Source = "program_component_plan",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "sandbox_work_patterns",
            Value = string.Join(
                ", ",
                card.WorkPatterns.Selections.Select(item =>
                    $"{item.PatternId}:{item.MatchPercent}%")),
            Source = "core_pattern_classification",
            IsAuthoritative = false
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "artifact_contract",
            Value = $"{card.ArtifactContract.ArtifactKind} {card.ArtifactContract.PreferredExtension} {card.ArtifactContract.MimeType}",
            Source = "program_artifact_contract",
            IsAuthoritative = true
        });
        handoff.ProgramFacts.Add(new ExecutorHandoffItem
        {
            Name = "selected_execution_route",
            Value = card.ExecutionBundle.SelectedRouteLevel,
            Source = "program_execution_bundle",
            IsAuthoritative = true
        });
        foreach (var answer in _choiceScenarioState.Answers)
        {
            handoff.UserSignals.Add(new ExecutorHandoffItem
            {
                Name = string.IsNullOrWhiteSpace(answer.DecisionDimension)
                    ? $"choice_{answer.StepNumber}"
                    : answer.DecisionDimension,
                Value = answer.OptionTitle,
                Source = "user_selection",
                IsAuthoritative = true
            });
        }

        AddCoreHypothesis(handoff, "suggested_direction", card.Area);
        AddCoreHypothesis(handoff, "provisional_goal", card.Goal);
        AddCoreHypothesis(handoff, "executor_reason", card.ExecutorReason);
        AddCoreHypothesis(handoff, "provisional_prompt", card.PromptForExecutor);
        if (ExecutorHandoffConsistencyPolicy.Normalize(handoff))
        {
            _choiceScenarioLog?.Write("executor_handoff_normalized", new
            {
                Reason = "offline_requirement_overrides_web",
                handoff.NeedsWeb,
                handoff.RequiredTools
            });
        }

        _choiceScenarioLog?.Write("executor_handoff_built", handoff);
        return handoff;
    }

    private static void AddCoreHypothesis(ExecutorHandoffPackage handoff, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            handoff.CoreHypotheses.Add(new ExecutorHandoffItem
            {
                Name = name,
                Value = value,
                Source = "core_inference",
                IsAuthoritative = false
            });
        }
    }

    private string FormatExecutorResult(ExecutorTurnResult turn)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(turn.Result))
        {
            builder.Append(turn.Result);
        }

        if (!string.IsNullOrWhiteSpace(turn.Question))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(turn.Question);
        }

        if (turn.Sources.Count > 0)
        {
            builder.AppendLine().AppendLine().AppendLine(L("Executor.SourcesTitle"));
            foreach (var source in turn.Sources)
            {
                builder.AppendLine("• " + source);
            }
        }

        if (turn.Warnings.Count > 0)
        {
            builder.AppendLine().AppendLine(L("Executor.WarningsTitle"));
            foreach (var warning in turn.Warnings)
            {
                builder.AppendLine("• " + warning);
            }
        }

        return builder.ToString().Trim();
    }

    private string GetExecutorVisibleBody(ExecutorTurnResult turn) => turn.Status switch
    {
        ExecutorTurnStatuses.Working when turn.Action == ExecutorTurnActions.SuggestFinalization =>
            string.Join(
                Environment.NewLine,
                L("Executor.ReadyToFinishBody"),
                LF("Executor.ReadyToFinishReason", turn.CompletionReason)),
        ExecutorTurnStatuses.Working => turn.Question,
        ExecutorTurnStatuses.StageReady => string.IsNullOrWhiteSpace(turn.Question)
            ? L("Executor.StageReadyBody")
            : turn.Question,
        _ => FormatExecutorResult(turn)
    };

    private static bool ShouldSpeakExecutorTurn(ExecutorTurnResult turn) =>
        turn.Status is ExecutorTurnStatuses.Working
            or ExecutorTurnStatuses.StageReady
            or ExecutorTurnStatuses.Blocked;

    private void UpdateExecutorStageControls()
    {
        if (!IsInitialized)
        {
            return;
        }

        var index = ExecutorStageFlow.GetIndex(_executorCurrentStageId);
        if (index < 0)
        {
            _executorCurrentStageId = ExecutorStageIds.TaskDefinition;
            index = 0;
        }

        ExecutorStageProgressText.Text = LF(
            "Executor.StageProgress",
            index + 1,
            ExecutorStageFlow.ActiveStageIds.Count);
        ExecutorStageTitleText.Text = GetExecutorStageTitle(_executorCurrentStageId);

        var canConfirmBrief = !_executorWorkflowService.BriefConfirmed
            && _executorCurrentStageId == ExecutorStageIds.TaskDefinition
            && _currentExecutorTurn?.Status == ExecutorTurnStatuses.StageReady
            && _currentExecutorTurn.Action == ExecutorTurnActions.ConfirmBrief;
        ExecutorNextStageButton.Content = L("Executor.ConfirmBrief");
        ExecutorNextStageButton.Visibility = canConfirmBrief
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string GetExecutorStageTitle(string stageId) => stageId switch
    {
        ExecutorStageIds.TaskDefinition => L("Executor.StageTaskDefinition"),
        ExecutorStageIds.PracticalClarification => L("Executor.StagePracticalClarification"),
        _ => L("Executor.StageTaskDefinition")
    };

    private void ExecutorResultPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ExecutorResultPanel.Visibility == Visibility.Visible)
        {
            UpdateExecutorOptionsLayout();
        }
    }

    private void ApplyExecutorWorkspaceLayout(bool animate)
    {
        var practical = _executorCurrentStageId == ExecutorStageIds.PracticalClarification;
        if (practical)
        {
            ExecutorResultPanel.MaxWidth = double.PositiveInfinity;
            ExecutorResultPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ExecutorResultPanel.VerticalAlignment = VerticalAlignment.Stretch;
            ExecutorWorkspaceContentRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ExecutorConversationPanel, 0);
            Grid.SetColumnSpan(ExecutorConversationPanel, 1);
            ExecutorConversationPanel.MaxWidth = double.PositiveInfinity;
            ExecutorConversationPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ExecutorConversationPanel.VerticalAlignment = VerticalAlignment.Stretch;
            ExecutorLivePreviewPanel.Visibility = Visibility.Visible;
            ExecutorResponseDock.MaxWidth = double.PositiveInfinity;
            ExecutorResponseDock.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            if (animate)
            {
                var duration = new Duration(TimeSpan.FromMilliseconds(260));
                ExecutorConversationTranslateTransform.BeginAnimation(
                    Media.TranslateTransform.XProperty,
                    new DoubleAnimation(36, 0, duration));
                ExecutorLivePreviewTranslateTransform.BeginAnimation(
                    Media.TranslateTransform.XProperty,
                    new DoubleAnimation(36, 0, duration));
                ExecutorLivePreviewPanel.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0, 1, duration));
            }
            else
            {
                ExecutorConversationTranslateTransform.X = 0;
                ExecutorLivePreviewTranslateTransform.X = 0;
                ExecutorLivePreviewPanel.Opacity = 1;
            }
        }
        else
        {
            ExecutorResultPanel.MaxWidth = 960;
            ExecutorResultPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ExecutorResultPanel.VerticalAlignment = VerticalAlignment.Center;
            ExecutorWorkspaceContentRow.Height = GridLength.Auto;
            Grid.SetColumn(ExecutorConversationPanel, 0);
            Grid.SetColumnSpan(ExecutorConversationPanel, 3);
            ExecutorConversationPanel.MaxWidth = double.PositiveInfinity;
            ExecutorConversationPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ExecutorConversationPanel.VerticalAlignment = VerticalAlignment.Stretch;
            ExecutorLivePreviewPanel.Visibility = Visibility.Collapsed;
            ExecutorLivePreviewPanel.Opacity = 0;
            ExecutorResponseDock.MaxWidth = double.PositiveInfinity;
            ExecutorResponseDock.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ExecutorConversationTranslateTransform.X = 0;
            ExecutorLivePreviewTranslateTransform.X = 36;
        }

        _executorPracticalLayoutActive = practical;
        UpdateExecutorOptionsLayout();
    }

    private void UpdateExecutorOptionsLayout()
    {
        var useWideLayout = _executorPracticalLayoutActive
            && ExecutorResultPanel.ActualWidth >= 1180;
        ExecutorClarificationOptionsItemsControl.ItemsPanel = (ItemsPanelTemplate)FindResource(
            useWideLayout
                ? "ExecutorOptionsWidePanel"
                : "ExecutorOptionsNarrowPanel");
    }

    private void StartExecutorSpeech(ExecutorTurnResult turn)
    {
        CancelExecutorSpeech(revealFullText: false, "replaced");
        _executorSpeechCts = new CancellationTokenSource();
        var presentationId = Interlocked.Increment(ref _executorSpeechPresentationId);
        _executorSpeechPresentationActive = true;
        ExecutorThoughtText.Text = string.Empty;
        ExecutorResultText.Text = string.Empty;
        SetExecutorInteractionEnabled(false);
        ExecutorFinishSessionButton.IsEnabled = !_executorSessionFinishedByUser;
        ExecutorFinishSessionDockButton.IsEnabled = !_executorSessionFinishedByUser;

        var settings = new CoreVoiceSettings
        {
            Enabled = true,
            Provider = CoreVoiceSettings.RhVoiceProvider,
            Volume = _appSettings.CoreVoice.Volume,
            Rate = _appSettings.CoreVoice.Rate,
            RussianVoice = _appSettings.CoreVoice.RussianVoice,
            EnglishVoice = _appSettings.CoreVoice.EnglishVoice
        };
        var request = new CoreSpeechRequest(
            [
                new CoreSpeechSegment("executorThought", turn.Thought),
                new CoreSpeechSegment("executorQuestion", turn.Question)
            ],
            _appSettings.LanguageCode,
            settings,
            "uncertainty:executor_clarification",
            SpeechRoles.UncertaintyExecutor);
        var progress = new Progress<CoreSpeechProgress>(value =>
        {
            if (presentationId != _executorSpeechPresentationId)
            {
                return;
            }

            ExecutorThoughtText.Text = VisibleText(
                turn.Thought,
                value.VisibleCharacters.GetValueOrDefault("executorThought"));
            ExecutorResultText.Text = VisibleText(
                turn.Question,
                value.VisibleCharacters.GetValueOrDefault("executorQuestion"));
        });
        _ = PresentExecutorSpeechAsync(turn, request, progress, presentationId, _executorSpeechCts);
    }

    private async Task PresentExecutorSpeechAsync(
        ExecutorTurnResult turn,
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        long presentationId,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            var result = await _executorSpeechCoordinator.PresentAsync(
                request,
                progress,
                _choiceScenarioLog,
                cancellationSource.Token);
            _executorWorkflowService.Write("executor_voice_result", new
            {
                result.Completed,
                result.Skipped,
                result.ErrorCode,
                RussianVoice = "Elena",
                EnglishVoice = "Bdl"
            });
        }
        finally
        {
            if (presentationId == _executorSpeechPresentationId)
            {
                ExecutorThoughtText.Text = turn.Thought;
                ExecutorResultText.Text = GetExecutorVisibleBody(turn);
                _executorSpeechPresentationActive = false;
                _executorSpeechCts = null;
                SetExecutorInteractionEnabled(true);
            }

            cancellationSource.Dispose();
        }
    }

    private void CancelExecutorSpeech(bool revealFullText, string reason)
    {
        if (_executorSpeechCts is null && !_executorSpeechPresentationActive)
        {
            return;
        }

        Interlocked.Increment(ref _executorSpeechPresentationId);
        var cancellationSource = _executorSpeechCts;
        _executorSpeechCts = null;
        cancellationSource?.Cancel();
        _executorSpeechCoordinator.Cancel();
        _executorSpeechPresentationActive = false;
        if (revealFullText && _currentExecutorTurn is { } turn)
        {
            ExecutorThoughtText.Text = turn.Thought;
            ExecutorResultText.Text = GetExecutorVisibleBody(turn);
        }

        _executorWorkflowService.Write("executor_voice_cancelled", new { Reason = reason });
        SetExecutorInteractionEnabled(true);
    }

    private void ExecutorVoiceToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _executorVoiceEnabled = !_executorVoiceEnabled;
        if (!_executorVoiceEnabled)
        {
            CancelExecutorSpeech(revealFullText: true, "session_muted");
        }
        else if (_currentExecutorTurn is { } turn && ShouldSpeakExecutorTurn(turn))
        {
            StartExecutorSpeech(turn);
        }

        UpdateExecutorVoiceControls();
        _executorWorkflowService.Write("executor_voice_toggled", new { Enabled = _executorVoiceEnabled });
    }

    private async void ExecutorRequestResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_executorWorkflowService.BriefConfirmed
            || _executorCurrentStageId == ExecutorStageIds.TaskDefinition
            || _executorSessionFinishedByUser)
        {
            return;
        }

        CancelExecutorSpeech(revealFullText: true, "result_snapshot");
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorSnapshotPreparing");
        try
        {
            var snapshot = await _executorWorkflowService.CreateResultSnapshotAsync(
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            ShowExecutorResultSnapshot(snapshot);
            _executorWorkflowService.Write("executor_snapshot_window_opened", new
            {
                snapshot.Id,
                snapshot.Version,
                snapshot.StageId
            });
            StatusText.Text = LF("Status.ExecutorSnapshotReady", snapshot.Version);
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                StatusText.Text = L("Status.ExecutorCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_snapshot_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            StatusText.Text = LF("Status.ExecutorSnapshotFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private void ShowExecutorResultSnapshot(
        ExecutorResultSnapshot snapshot,
        bool finalResult = false)
    {
        if (finalResult)
        {
            _executorResultWindow?.Close();
            _executorResultWindow = null;
        }

        var createdWindow = false;
        if (_executorResultWindow is null || !_executorResultWindow.IsLoaded)
        {
            _executorResultWindow = new ExecutorResultWindow(
                finalResult
                    ? L("Executor.FinalResultWindowTitle")
                    : L("Executor.ResultWindowTitle"),
                L("Executor.ResultWindowVersion"),
                finalResult
                    ? L("Executor.FinalResultWindowHint")
                    : L("Executor.ResultWindowHint"),
                L)
            {
                Owner = this
            };
            _executorResultWindow.Closed += (_, _) => _executorResultWindow = null;
            _executorResultWindow.Show();
            createdWindow = true;
        }

        if (createdWindow)
        {
            IEnumerable<ExecutorResultSnapshot> snapshots = finalResult
                ? [snapshot]
                : _executorWorkflowService.Snapshots;
            foreach (var savedSnapshot in snapshots)
            {
                _executorResultWindow.AddSnapshot(savedSnapshot);
            }
        }
        else
        {
            _executorResultWindow.AddSnapshot(snapshot);
        }

        if (_executorResultWindow.WindowState == WindowState.Minimized)
        {
            _executorResultWindow.WindowState = WindowState.Normal;
        }

        _executorResultWindow.Activate();
    }

    private void UpdateExecutorVoiceControls()
    {
        if (!IsInitialized)
        {
            return;
        }

        ExecutorVoiceToggleButton.Content = _executorVoiceEnabled ? "🔊" : "🔇";
        ExecutorVoiceToggleButton.ToolTip = _executorVoiceEnabled
            ? L("Executor.VoiceDisable")
            : L("Executor.VoiceEnable");
    }

    private async void ExecutorFinishSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_executorSessionFinishedByUser || _executorCts is { IsCancellationRequested: true })
        {
            return;
        }

        CancelExecutorSpeech(revealFullText: true, "finish_dialog");
        _executorWorkflowService.Write("executor_finish_dialog_opened", new
        {
            SuggestedByExecutor = _executorFinalizationSuggested,
            Stage = _executorCurrentStageId
        });
        var dialog = new ExecutorFinishDialog(
            L("Executor.FinishDialogTitle"),
            L("Executor.FinishDialogHeading"),
            L("Executor.FinishDialogDescription"),
            L("Executor.FormResult"),
            L("Executor.FinishWithoutResult"),
            L("Executor.OutputDialogHeading"),
            L("Executor.OutputDialogDescription"),
            L("Executor.ShowInApp"),
            L("Executor.ExportDocx"),
            L("Executor.DialogBack"))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Choice == ExecutorFinishChoice.None)
        {
            _executorWorkflowService.Write("executor_finish_dialog_cancelled");
            StatusText.Text = L("Status.ExecutorFinishCancelled");
            SetExecutorInteractionEnabled(true);
            return;
        }

        _executorWorkflowService.Write("executor_finish_choice_selected", new
        {
            Choice = dialog.Choice.ToString()
        });
        if (dialog.Choice == ExecutorFinishChoice.FinishWithoutResult)
        {
            CompleteExecutorSession(
                "user_finished_without_result",
                L("Status.ExecutorFinishedWithoutResult"));
            return;
        }

        string? exportPath = null;
        if (dialog.Choice == ExecutorFinishChoice.ExportDocx)
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("Executor.DocxDialogTitle"),
                Filter = L("Executor.DocxFilter"),
                AddExtension = true,
                DefaultExt = ".docx",
                FileName = $"{L("App.ProductName")}_result_{DateTime.Now:yyyyMMdd_HHmm}.docx",
                OverwritePrompt = true
            };
            if (saveDialog.ShowDialog(this) != true)
            {
                _executorWorkflowService.Write("executor_docx_export_cancelled");
                StatusText.Text = L("Status.ExecutorFinishCancelled");
                SetExecutorInteractionEnabled(true);
                return;
            }

            exportPath = saveDialog.FileName;
        }

        await FinalizeExecutorSessionAsync(dialog.Choice, exportPath);
    }

    private async Task FinalizeExecutorSessionAsync(
        ExecutorFinishChoice choice,
        string? exportPath)
    {
        _executorCts?.Dispose();
        _executorCts = new CancellationTokenSource();
        SetExecutorInteractionEnabled(false);
        BackFromChoiceScenarioButton.IsEnabled = false;
        StartChoiceAiActivity();
        StatusText.Text = L("Status.ExecutorFinalPreparing");
        try
        {
            var snapshot = await _executorWorkflowService.CreateFinalResultAsync(
                CreateMatrixStreamProgress(),
                _executorCts.Token);
            if (choice == ExecutorFinishChoice.ExportDocx)
            {
                if (string.IsNullOrWhiteSpace(exportPath))
                {
                    throw new InvalidOperationException("DOCX destination path is unavailable.");
                }

                await Task.Run(
                    () => ExecutorDocxExporter.Export(snapshot, exportPath),
                    _executorCts.Token);
                _executorWorkflowService.Write("executor_final_docx_exported", new
                {
                    snapshot.Id,
                    snapshot.Version,
                    Path = exportPath,
                    Bytes = new FileInfo(exportPath).Length
                });
                CompleteExecutorSession(
                    "user_exported_final_result",
                    LF("Status.ExecutorExported", exportPath));
            }
            else
            {
                ShowExecutorResultSnapshot(snapshot, finalResult: true);
                _executorWorkflowService.Write("executor_final_window_opened", new
                {
                    snapshot.Id,
                    snapshot.Version
                });
                CompleteExecutorSession(
                    "user_opened_final_result",
                    L("Status.ExecutorFinalReady"));
            }
        }
        catch (OperationCanceledException)
        {
            if (!_executorSessionFinishedByUser)
            {
                _executorWorkflowService.Write("executor_final_result_cancelled");
                StatusText.Text = L("Status.ExecutorFinishCancelled");
            }
        }
        catch (Exception ex)
        {
            _executorWorkflowService.Write("executor_final_result_failed", new
            {
                ex.Message,
                ErrorType = ex.GetType().FullName,
                Choice = choice.ToString()
            });
            StatusText.Text = choice == ExecutorFinishChoice.ExportDocx
                ? LF("Status.ExecutorExportFailed", ex.Message)
                : LF("Status.ExecutorFinalFailed", ex.Message);
        }
        finally
        {
            StopChoiceAiActivity();
            BackFromChoiceScenarioButton.IsEnabled = true;
            SetExecutorInteractionEnabled(true);
        }
    }

    private void CompleteExecutorSession(string reason, string status)
    {
        _executorSessionFinishedByUser = true;
        _executorWorkflowService.Write("executor_session_completed_by_user", new
        {
            Reason = reason,
            SuggestedByExecutor = _executorFinalizationSuggested
        });
        CompleteActiveSessionArchive(reason);
        CancelExecutorSession(reason);
        ExecutorClarificationOptionsItemsControl.Visibility = Visibility.Collapsed;
        ExecutorCustomInputPanel.Visibility = Visibility.Collapsed;
        ExecutorRequestResultButton.Visibility = Visibility.Collapsed;
        ExecutorResponseDock.Visibility = Visibility.Collapsed;
        ExecutorFinishSessionButton.IsEnabled = false;
        ExecutorFinishSessionButton.Visibility = Visibility.Collapsed;
        ExecutorFinishSessionDockButton.IsEnabled = false;
        ExecutorFinishSessionDockButton.Visibility = Visibility.Collapsed;
        SetExecutorInteractionEnabled(false);
        StatusText.Text = status;
    }

    private void SetExecutorInteractionEnabled(bool enabled)
    {
        var answersEnabled = enabled
            && !_executorSpeechPresentationActive
            && !_executorSessionFinishedByUser;
        ExecutorClarificationOptionsItemsControl.IsEnabled = answersEnabled;
        ExecutorCustomSubmitButton.IsEnabled = answersEnabled;
        ExecutorSessionFilesPanel.IsEnabled = answersEnabled;
        ExecutorRequestResultButton.IsEnabled = answersEnabled
            && _executorWorkflowService.BriefConfirmed
            && _executorCurrentStageId == ExecutorStageIds.PracticalClarification;
        var canConfirmBrief = !_executorWorkflowService.BriefConfirmed
            && _executorCurrentStageId == ExecutorStageIds.TaskDefinition
            && _currentExecutorTurn?.Status == ExecutorTurnStatuses.StageReady
            && _currentExecutorTurn.Action == ExecutorTurnActions.ConfirmBrief;
        ExecutorNextStageButton.IsEnabled = answersEnabled
            && canConfirmBrief;
        ExecutorFinishSessionButton.IsEnabled = answersEnabled;
        ExecutorFinishSessionDockButton.IsEnabled = answersEnabled;
    }

    private void SetExecutorFinalizationSuggested(bool suggested, bool animate)
    {
        if (_executorFinalizationSuggested == suggested && animate)
        {
            return;
        }

        _executorFinalizationSuggested = suggested;
        ExecutorFinishSessionButton.BeginAnimation(OpacityProperty, null);
        ExecutorFinishSessionDockButton.BeginAnimation(OpacityProperty, null);
        ExecutorFinishHeaderTranslateTransform.BeginAnimation(Media.TranslateTransform.XProperty, null);
        ExecutorFinishDockTranslateTransform.BeginAnimation(Media.TranslateTransform.XProperty, null);

        if (!animate)
        {
            ExecutorFinishSessionButton.Visibility = Visibility.Visible;
            ExecutorFinishSessionButton.Opacity = suggested ? 0 : 1;
            ExecutorFinishHeaderTranslateTransform.X = suggested ? -18 : 0;
            ExecutorFinishSessionDockButton.Visibility = suggested
                ? Visibility.Visible
                : Visibility.Collapsed;
            ExecutorFinishSessionDockButton.Opacity = suggested ? 1 : 0;
            ExecutorFinishDockTranslateTransform.X = suggested ? 0 : 36;
            return;
        }

        ExecutorFinishSessionButton.Visibility = Visibility.Visible;
        ExecutorFinishSessionDockButton.Visibility = Visibility.Visible;
        if (suggested)
        {
            ExecutorFinishSessionButton.Opacity = 0;
            ExecutorFinishHeaderTranslateTransform.X = -18;
            ExecutorFinishSessionDockButton.Opacity = 1;
            ExecutorFinishDockTranslateTransform.X = 0;
            var headerFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            headerFade.Completed += (_, _) =>
            {
                if (_executorFinalizationSuggested)
                {
                    ExecutorFinishSessionButton.Visibility = Visibility.Collapsed;
                }
            };
            ExecutorFinishSessionButton.BeginAnimation(OpacityProperty, headerFade);
            ExecutorFinishHeaderTranslateTransform.BeginAnimation(
                Media.TranslateTransform.XProperty,
                new DoubleAnimation(0, -18, TimeSpan.FromMilliseconds(220)));

            var dockOpacity = new DoubleAnimationUsingKeyFrames();
            dockOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            dockOpacity.KeyFrames.Add(new SplineDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
                new KeySpline(0.2, 0.8, 0.2, 1)));
            dockOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(
                0.8,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
            dockOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(540))));
            dockOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(
                0.84,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680))));
            dockOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));
            ExecutorFinishSessionDockButton.BeginAnimation(OpacityProperty, dockOpacity);
            ExecutorFinishDockTranslateTransform.BeginAnimation(
                Media.TranslateTransform.XProperty,
                new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(260)));
            return;
        }

        ExecutorFinishSessionButton.Opacity = 1;
        ExecutorFinishHeaderTranslateTransform.X = 0;
        ExecutorFinishSessionDockButton.Opacity = 0;
        ExecutorFinishDockTranslateTransform.X = 36;
        ExecutorFinishSessionButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        ExecutorFinishHeaderTranslateTransform.BeginAnimation(
            Media.TranslateTransform.XProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)));
        var dockFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        dockFade.Completed += (_, _) =>
        {
            if (!_executorFinalizationSuggested)
            {
                ExecutorFinishSessionDockButton.Visibility = Visibility.Collapsed;
            }
        };
        ExecutorFinishSessionDockButton.BeginAnimation(OpacityProperty, dockFade);
        ExecutorFinishDockTranslateTransform.BeginAnimation(
            Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, 36, TimeSpan.FromMilliseconds(180)));
    }

    private IProgress<ModelStreamChunk> CreateMatrixStreamProgress() =>
        new Progress<ModelStreamChunk>(chunk =>
        {
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                ChoiceMatrixRain.Feed(chunk.Text);
            }
        });

    private void CancelExecutorSession(string reason)
    {
        var hadActiveSession = _executorWorkflowService.HasActiveSession;
        CancelExecutorSpeech(revealFullText: true, reason);
        _executorCts?.Cancel();
        _executorCts?.Dispose();
        _executorCts = null;
        _executorWorkflowService.Stop(reason);
        if (hadActiveSession
            && reason is not "app_closed" and not "restoring_archived_session")
        {
            _executorWorkflowService.QueuePendingSemanticPassportGeneration(_storageSettings);
        }
        CloseSessionTreeWindow();
        StopChoiceAiActivity();
    }

    private void CloseSessionTreeWindow()
    {
        SessionTreeButton.Visibility = Visibility.Collapsed;
        SessionTreeButton.IsEnabled = false;
        _sessionTreeWindow?.Close();
        _sessionTreeWindow = null;
    }

    private static bool IsValidCustomChoice(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var words = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is >= 1 and <= 3;
    }

    private static string FormatInvariant(string format, params object[] args)
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
    }

    private void LoadStorageSettingsIntoControls()
    {
        ModelsTotalLimitInput.Text = FormatGb(_storageSettings.Models.TotalLimitGb);
        ModelsAllowOverflowCheckBox.IsChecked = _storageSettings.Models.AllowTemporaryOverflow;
        ModelsTemporaryOverflowInput.Text = FormatGb(_storageSettings.Models.TemporaryOverflowGb);

        ResultsTotalLimitInput.Text = FormatGb(_storageSettings.Results.TotalLimitGb);
        ResultsAllowOverflowCheckBox.IsChecked = _storageSettings.Results.AllowTemporaryOverflow;
        ResultsTemporaryOverflowInput.Text = FormatGb(_storageSettings.Results.TemporaryOverflowGb);

        RefreshStorageLists();
    }

    private void LoadProfileIntoControls()
    {
        ProfileDisplayNameInput.Text = _userProfile.DisplayName;
        ProfileCityInput.Text = _userProfile.Location.City;
        ProfileRegionInput.Text = _userProfile.Location.Region;
        ProfileCountryInput.Text = _userProfile.Location.Country;
        ProfileTimezoneInput.Text = string.IsNullOrWhiteSpace(_userProfile.Location.Timezone)
            ? TimeZoneInfo.Local.Id
            : _userProfile.Location.Timezone;

        PreferenceConciseCheckBox.IsChecked = _userProfile.AnswerPreferences.Concise;
        PreferenceDetailedCheckBox.IsChecked = _userProfile.AnswerPreferences.Detailed;
        PreferenceSimpleCheckBox.IsChecked = _userProfile.AnswerPreferences.SimpleLanguage;
        PreferenceStepsCheckBox.IsChecked = _userProfile.AnswerPreferences.StepByStep;
        PreferenceExamplesCheckBox.IsChecked = _userProfile.AnswerPreferences.Examples;
        PreferenceSourcesCheckBox.IsChecked = _userProfile.AnswerPreferences.SourcesWhenSearching;
        PreferenceRisksCheckBox.IsChecked = _userProfile.AnswerPreferences.WarnAboutRisks;
        SetSelectedWorkloadMode(_userProfile.WorkloadMode);
    }

    private void SaveProfileFromControls()
    {
        _userProfile.ProfileVersion = 1;
        _userProfile.DisplayName = ProfileDisplayNameInput.Text.Trim();
        _userProfile.Location.Mode = "manual";
        _userProfile.Location.Source = "manual";
        _userProfile.Location.City = ProfileCityInput.Text.Trim();
        _userProfile.Location.Region = ProfileRegionInput.Text.Trim();
        _userProfile.Location.Country = ProfileCountryInput.Text.Trim();
        _userProfile.Location.Timezone = ProfileTimezoneInput.Text.Trim();
        _userProfile.Location.UpdatedAt = DateTimeOffset.Now;
        _userProfile.AnswerPreferences.Concise = PreferenceConciseCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.Detailed = PreferenceDetailedCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.SimpleLanguage = PreferenceSimpleCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.StepByStep = PreferenceStepsCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.Examples = PreferenceExamplesCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.SourcesWhenSearching = PreferenceSourcesCheckBox.IsChecked == true;
        _userProfile.AnswerPreferences.WarnAboutRisks = PreferenceRisksCheckBox.IsChecked == true;
        _userProfile.WorkloadMode = GetSelectedWorkloadMode();
        _userProfileStore.Save(_userProfile);
    }

    private void UpdateProfileButtonState()
    {
        if (_userProfile.IsComplete())
        {
            _profileBlinkTimer.Stop();
            ProfileButton.Opacity = 1.0;
            return;
        }

        if (!_profileBlinkTimer.IsEnabled)
        {
            ProfileButton.Opacity = 1.0;
            _profileBlinkTimer.Start();
        }
    }

    private string GetSelectedWorkloadMode()
    {
        if (WorkloadLightRadio.IsChecked == true)
        {
            return UserWorkloadModes.Light;
        }

        if (WorkloadExtremeRadio.IsChecked == true)
        {
            return UserWorkloadModes.Extreme;
        }

        return UserWorkloadModes.Balanced;
    }

    private void SetSelectedWorkloadMode(string mode)
    {
        WorkloadLightRadio.IsChecked = string.Equals(mode, UserWorkloadModes.Light, StringComparison.OrdinalIgnoreCase);
        WorkloadExtremeRadio.IsChecked = string.Equals(mode, UserWorkloadModes.Extreme, StringComparison.OrdinalIgnoreCase);
        WorkloadBalancedRadio.IsChecked = WorkloadLightRadio.IsChecked != true && WorkloadExtremeRadio.IsChecked != true;
    }

    private void SaveStorageSettingsFromControls()
    {
        _storageSettings.Models.TotalLimitGb = ParseGb(ModelsTotalLimitInput.Text);
        _storageSettings.Models.AllowTemporaryOverflow = ModelsAllowOverflowCheckBox.IsChecked == true;
        _storageSettings.Models.TemporaryOverflowGb = ParseGb(ModelsTemporaryOverflowInput.Text);

        _storageSettings.Results.TotalLimitGb = ParseGb(ResultsTotalLimitInput.Text);
        _storageSettings.Results.AllowTemporaryOverflow = ResultsAllowOverflowCheckBox.IsChecked == true;
        _storageSettings.Results.TemporaryOverflowGb = ParseGb(ResultsTemporaryOverflowInput.Text);
    }

    private void RefreshStorageLists()
    {
        RefreshLocationList(ModelsLocationList, _storageSettings.Models);
        RefreshLocationList(ResultsLocationList, _storageSettings.Results);
    }

    private void RefreshLocationList(System.Windows.Controls.ListBox listBox, StorageCategorySettings category)
    {
        var selectedIndex = listBox.SelectedIndex;
        listBox.ItemsSource = category.Locations
            .Select((location, index) => LF("Storage.ListItem", index + 1, location.Path, FormatGbForText(location.LimitGb)))
            .ToList();

        if (selectedIndex >= 0 && selectedIndex < category.Locations.Count)
        {
            listBox.SelectedIndex = selectedIndex;
        }
    }

    private void BrowseFolderInto(System.Windows.Controls.TextBox pathInput)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = L("FolderDialog.Description"),
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(pathInput.Text))
        {
            dialog.SelectedPath = pathInput.Text.Trim();
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            pathInput.Text = dialog.SelectedPath;
        }
    }

    private void AddOrUpdateLocation(
        StorageCategorySettings category,
        System.Windows.Controls.ListBox listBox,
        System.Windows.Controls.TextBox pathInput,
        System.Windows.Controls.TextBox limitInput)
    {
        var path = pathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = L("Status.PathRequired");
            return;
        }

        var limitGb = ParseGb(limitInput.Text);
        var selectedIndex = listBox.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < category.Locations.Count)
        {
            category.Locations[selectedIndex].Path = path;
            category.Locations[selectedIndex].LimitGb = limitGb;
            return;
        }

        var existing = category.Locations.FirstOrDefault(location =>
            string.Equals(location.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.LimitGb = limitGb;
            return;
        }

        category.Locations.Add(new StorageLocationSettings
        {
            Path = path,
            LimitGb = limitGb
        });
    }

    private static void RemoveSelectedLocation(
        StorageCategorySettings category,
        System.Windows.Controls.ListBox listBox,
        System.Windows.Controls.TextBox pathInput,
        System.Windows.Controls.TextBox limitInput)
    {
        var selectedIndex = listBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= category.Locations.Count)
        {
            return;
        }

        category.Locations.RemoveAt(selectedIndex);
        pathInput.Clear();
        limitInput.Clear();
    }

    private static void MoveSelectedLocation(StorageCategorySettings category, System.Windows.Controls.ListBox listBox, int direction)
    {
        var selectedIndex = listBox.SelectedIndex;
        var newIndex = selectedIndex + direction;
        if (selectedIndex < 0 || newIndex < 0 || newIndex >= category.Locations.Count)
        {
            return;
        }

        (category.Locations[selectedIndex], category.Locations[newIndex]) =
            (category.Locations[newIndex], category.Locations[selectedIndex]);
        listBox.SelectedIndex = newIndex;
    }

    private static void FillLocationInputs(
        StorageCategorySettings category,
        System.Windows.Controls.ListBox listBox,
        System.Windows.Controls.TextBox pathInput,
        System.Windows.Controls.TextBox limitInput)
    {
        var selectedIndex = listBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= category.Locations.Count)
        {
            return;
        }

        var location = category.Locations[selectedIndex];
        pathInput.Text = location.Path;
        limitInput.Text = FormatGb(location.LimitGb);
    }

    private void UpdateStorageSteps()
    {
        ModelsStorageStepText.Text = BuildStorageSummary(_storageSettings.Models, L("Home.ModelsStorageEmpty"));
        ResultsStorageStepText.Text = BuildStorageSummary(_storageSettings.Results, L("Home.ResultsStorageEmpty"));
    }

    private string BuildStorageSummary(StorageCategorySettings category, string emptyText)
    {
        if (category.Locations.Count == 0)
        {
            return emptyText;
        }

        var defaultPath = category.Locations[0].Path;
        var additional = category.Locations.Skip(1).Take(2).Select(location => location.Path).ToList();
        var hiddenCount = Math.Max(0, category.Locations.Count - 1 - additional.Count);
        var additionalText = additional.Count == 0
            ? string.Empty
            : LF("Storage.SummaryAdditional", string.Join("; ", additional));

        if (hiddenCount > 0)
        {
            additionalText += LF("Storage.SummaryHidden", hiddenCount);
        }

        var overflowText = category.AllowTemporaryOverflow
            ? $"+{FormatGbForText(category.TemporaryOverflowGb)} {L("Units.Gb")}"
            : L("Storage.OverflowOff");

        return LF(
            "Storage.SummaryConfigured",
            category.Locations.Count,
            defaultPath,
            additionalText,
            FormatGbForText(category.TotalLimitGb),
            overflowText);
    }

    private static double ParseGb(string text)
    {
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, Math.Round(value, 2))
            : 0;
    }

    private static string FormatGb(double value)
    {
        return value <= 0 ? string.Empty : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatGbForText(double value)
    {
        return Math.Max(0, value).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string FormatBytes(double bytes)
    {
        string[] units = [L("Units.Bytes"), L("Units.Kb"), L("Units.Mb"), L("Units.Gb")];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private string BuildPassportSummary(ComputerPassport passport)
    {
        var drives = passport.Drives.Count == 0
            ? L("Passport.DrivesUnknown")
            : LF("Passport.DrivesFound", passport.Drives.Count);

        return string.Join(
            Environment.NewLine,
            LF("Passport.Analysis", passport.CreatedAt.ToString("dd.MM.yyyy HH:mm:ss")),
            LF("Passport.Computer", passport.MachineName),
            LF("Passport.Windows", passport.WindowsVersion),
            LF("Passport.Cpu", passport.CpuName),
            LF("Passport.LogicalProcessors", passport.LogicalProcessorCount),
            LF("Passport.Ram", FormatGbForText(passport.RamTotalGb)),
            BuildGpuSummary(passport),
            drives);
    }

    private void UpdateComputerPassportStep(ComputerPassport passport)
    {
        ComputerPassportStepText.Text = string.Join(
            Environment.NewLine,
            L("Passport.ScanComplete"),
            LF("Passport.Cpu", passport.CpuName),
            LF("Passport.LogicalProcessors", passport.LogicalProcessorCount),
            LF("Passport.Ram", FormatGbForText(passport.RamTotalGb)),
            BuildGpuSummary(passport),
            BuildDriveSummary(passport));
    }

    private string BuildGpuSummary(ComputerPassport passport)
    {
        if (passport.Gpus.Count == 0)
        {
            return L("Passport.GpuMissing");
        }

        var gpuNames = string.Join(", ", passport.Gpus.Select(gpu => gpu.Name));
        var vramTotal = passport.Gpus.Sum(gpu => gpu.VramGb);
        var vramText = vramTotal > 0 ? $"{FormatGbForText(vramTotal)} {L("Units.Gb")}" : "unknown";

        return LF("Passport.GpuFound", gpuNames, vramText);
    }

    private string BuildDriveSummary(ComputerPassport passport)
    {
        if (passport.Drives.Count == 0)
        {
            return L("Passport.DrivesMissing");
        }

        var totalFree = passport.Drives.Sum(drive => drive.FreeGb);
        return LF("Passport.DrivesFree", passport.Drives.Count, FormatGbForText(totalFree));
    }

    private void ApplySystemTitleBarTheme()
    {
        WindowTitleBarThemeService.Apply(this, _isDarkTheme);
    }

    private static bool IsWindowsAppThemeDark()
    {
        const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        var appsUseLightTheme = Registry.CurrentUser
            .OpenSubKey(personalizeKey)
            ?.GetValue("AppsUseLightTheme");

        return appsUseLightTheme is int value && value == 0;
    }

}
