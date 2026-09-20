using KeyPilot.App.Presentation;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Diagnostics;
using KeyPilot.Core.Input;
using KeyPilot.Core.Triggers;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Audio;
using KeyPilot.Platform.Windows.Foreground;
using KeyPilot.Platform.Windows.Input;
using KeyPilot.Platform.Windows.Storage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace KeyPilot.App;

public sealed partial class MainWindow : Window
{
    private const int MaximumOpaqueHidChangedBytes = 64;

    private enum ConfigurationSaveOutcome
    {
        Succeeded,
        Failed,
        Superseded
    }

    private readonly record struct ConfigurationSaveResult(
        ConfigurationSaveOutcome Outcome,
        long Revision);

    private sealed record ApplicationBindingEditor(
        Grid Root,
        TextBox ProcessNameTextBox,
        ComboBox ProfileComboBox);

    private static readonly IReadOnlyDictionary<string, string> ActionPlaceholders =
        new Dictionary<string, string>
        {
            ["快捷键"] = "例如：Ctrl + Alt + =",
            ["按键 / 组合键"] = "粘贴从主页复制的 KeyPilot 按键信息",
            ["媒体控制"] = "请选择媒体动作",
            ["音量控制"] = "0–100",
            ["应用 / 文件"] = @"例如：C:\Tools\ControlCenter.exe",
            ["网址"] = "例如：example.com、steam:// 或 mailto:",
            ["脚本 / 命令"] = @"例如：D:\Scripts\performance-mode.bat",
            ["手柄按键"] = "例如：Gamepad Y"
        };

    private static readonly string[] MediaActionValues =
        ["播放 / 暂停", "停止", "上一首", "下一首"];

    private static readonly string[] VolumeActionValues =
        ["音量增加", "音量降低", "静音切换", "设置音量百分比…"];

    private readonly Dictionary<string, InputNodeVisual> _nodeVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly InputProcessLog _processLog = new();
    private InputProcessLogFilter _processLogFilter = InputProcessLogFilter.All;
    private bool _processLogExpanded = true;
    private bool _processLogFollowTail = true;
    private readonly HashSet<string> _pressedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pulseVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _connectedGamepadSlots = [];
    private readonly HashSet<string> _knownHidDevicePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressionUnavailableWarnings = new(StringComparer.Ordinal);
    private readonly UnknownHidReportDiffer _hidReportDiffer = new();
    private readonly OpaqueHidButtonCaptureSession _hidCaptureSession =
        new(MaximumOpaqueHidChangedBytes);
    private readonly InputPatternCaptureSession _chordCaptureSession = new();
    private readonly IKeyPilotConfigurationStore _configurationStore =
        new LocalKeyPilotConfigurationStore();
    private readonly SemaphoreSlim _configurationSaveGate = new(1, 1);
    private readonly LatestSaveRevision _configurationSaveRevision = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly InputInjectionMarker _inputInjectionMarker;
    private readonly XInputMouseMotionController _xInputMouseMotion;
    private readonly MappingExecutionCoordinator _mappingExecution;
    private readonly CaptureIngestCoordinator _ingest;
    private readonly VirtualGamepadExclusionFilter _virtualPadFilter = new();
    private readonly Rc003VoiceBridge _rc003Voice = new();
    private readonly Rc003BatteryReader _rc003Battery = new();
    private readonly MicrophoneAliasStore _microphoneAliases = new();
    private bool _rc003HidConnected;
    private byte? _rc003BatteryPercent;
    private int _rc003VoiceStarting;
    private int _rc003BatteryStarting;
    private string? _sonyStatusName;
    private readonly Dictionary<string, SonyHidPadState> _sonyPadStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDriverSuppressionCoordinator _driverSuppression;
    private readonly LowLevelKeyboardSuppressionSource _compatibilitySuppression;
    private readonly InputEventPhaseTracker _inputPhaseTracker = new();
    private readonly InputDiagnosticSession _inputDiagnosticSession = new();
    private readonly XInputStickAnalyzer _stickAnalyzer = new();
    private readonly ShortcutCaptureSession _shortcutCaptureSession = new();
    private readonly Dictionary<(int UserIndex, XInputStick Stick), XInputStickAnalysis> _stickAnalyses = [];
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _hidCaptureTimer;
    private readonly DispatcherTimer _inputDiagnosticTimer;
    private readonly DispatcherTimer _actionCaptureTimer;
    private readonly DispatcherTimer _foregroundProcessTimer;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly DispatcherTimer _conditionCaptureTimer;
    private readonly WindowsForegroundProcessReader _foregroundProcessReader = new();
    private readonly WindowsForegroundWindowWatcher _foregroundWindowWatcher = new();
    private readonly WindowsApplicationCatalog _applicationCatalog = new();
    private readonly List<ApplicationBindingEditor> _applicationBindingEditors = [];
    private readonly List<MappingProfile> _profileSettingsDraft = [];
    private readonly List<(string ThemeId, Border Preview)> _appearanceThemeSwatches = [];
    private RawInputKeyboardSource? _rawKeyboard;
    private LowLevelMouseButtonSource? _mouseButtons;
    private XInputGamepadSource? _xInputGamepad;
    private InputNodeState? _selectedNode;
    private InputNodeState? _editingNode;
    private InputNodeState? _armedSpecialSlot;
    private InputNodeState? _armedChordSlot;
    private Button? _selectedActionButton;
    private Button? _drawerSourceButton;
    private enum WorkspacePage
    {
        Capture,
        Mapping
    }

    private WorkspacePage _workspacePage;
    private bool _mappingMode;
    private bool _captureEnabled = true;
    private bool _suspendWorkspaceCapture = true;
    private bool _isClosing;
    private bool _exitRequested;
    private bool _hidingToTray;
    private TrayIconHost? _trayIcon;
    private bool _configurationWriteBlocked;
    private bool _updatingMasterToggle;
    private bool _updatingProfileSettings;
    private bool _savingProfileSettings;
    private Guid? _editedStickMouseProfileId;
    private bool _runtimeMappingsEnabled;
    private bool _driverSuppressionActive;
    private InputBackendHealth _rawInputHealth = InputBackendHealth.Pending;
    private InputBackendHealth _xInputHealth = InputBackendHealth.Pending;
    private string _rawInputStatus = "Raw Input 等待初始化";
    private string _xInputStatus = "XInput 等待初始化";
    private int _errorCount;
    private long _inputSequence;
    private long _runtimeRevision;
    private long _lastStickUiEnqueueTicks;
    private Task? _configurationLoadTask;
    private KeyPilotConfiguration _configuration =
        KeyPilotConfiguration.CreateDefault("掌机默认配置");
    private KeyPilotConfiguration _runtimeConfiguration =
        KeyPilotConfiguration.CreateDefault("掌机默认配置");
    private KeyPilotConfiguration? _lastDurableConfiguration;
    private WorkspaceConfigurationAdapter.RestoreResult? _workspaceRestore;
    private string _activeProfileName = "掌机默认配置";
    private string? _currentForegroundProcessName;
    private string? _lastExternalForegroundProcessName;
    private ApplicationIdentity? _currentForegroundIdentity;
    private string? _currentForegroundPackageFamily;
    private IReadOnlyList<string> _runningProcessNames = [];
    private IReadOnlyList<string> _runningPackageFamilyNames = [];
    private string _applicationContextSignature = string.Empty;
    private readonly List<ApplicationIdentity> _conditionApplications = [];
    private MappingConditionKind _conditionKind = MappingConditionKind.Always;
    private bool _updatingConditionKind;
    private bool _capturingConditionApplication;
    private DateTimeOffset _conditionCaptureDeadlineUtc;
    private string? _conditionCaptureBaseline;

    public MainWindow()
    {
        InitializeComponent();
        TrySetWindowIcon();
        InitializeTrayIcon();
        VersionText.Text = GetDisplayVersion();
        _inputInjectionMarker = InputInjectionMarker.CreateProcessLocal();
        _xInputMouseMotion = new XInputMouseMotionController(_inputInjectionMarker);
        _xInputMouseMotion.Faulted += XInputMouseMotion_Faulted;
        var actionExecutor = WindowsActionExecutorFactory.CreateDefault(_inputInjectionMarker);
        _mappingExecution = new MappingExecutionCoordinator(
            new MappingActionPlanner(),
            actionExecutor.ExecuteAsync);
        _mappingExecution.Faulted += MappingExecution_Faulted;
        _ingest = new CaptureIngestCoordinator(
            _inputPhaseTracker,
            _mappingExecution,
            () => _runtimeConfiguration,
            ObserveChordCapture,
            WarnSuppressionUnavailable);
        _driverSuppression = new BrokerDriverSuppressionCoordinator();
        _driverSuppression.InputReceived += DriverSuppression_InputReceived;
        _driverSuppression.StatusChanged += DriverSuppression_StatusChanged;
        _compatibilitySuppression = new LowLevelKeyboardSuppressionSource(_inputInjectionMarker);
        _compatibilitySuppression.InputReceived += CompatibilitySuppression_InputReceived;
        _compatibilitySuppression.StatusChanged += CompatibilitySuppression_StatusChanged;
        ApplyRuntimeConfiguration(_configuration);
        BuildKeyboard();
        BuildGamepad();
        BuildSpecialSlots();
        BuildRemoteButtons();
        BuildMouse();
        BuildAppearanceThemeSwatches();
        ApplyAppearanceTheme(_configuration.AppearanceThemeId);
        _rc003Battery.BatteryChanged += Rc003Battery_BatteryChanged;
        RefreshConnectedMicrophones();
        _workspaceRestore = WorkspaceConfigurationAdapter.Restore(
            _configuration,
            WorkspaceNodes());
        _activeProfileName = _workspaceRestore.ActiveProfileName;

        _selectedActionButton = ShortcutActionButton;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastInfoBar.IsOpen = false;
        };
        _hidCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hidCaptureTimer.Tick += (_, _) =>
        {
            if (_armedSpecialSlot is not null && _hidCaptureSession.TryExpire(DateTimeOffset.UtcNow))
            {
                CancelArmedHidCapture("HID 采集已超时，请重新选择槽位", InfoBarSeverity.Warning);
            }

            if (_armedChordSlot is not null)
            {
                var patternState = _chordCaptureSession.TryComplete(DateTimeOffset.UtcNow);
                if (patternState == InputPatternCaptureState.Completed &&
                    _chordCaptureSession.CompletedSource is { } completedPattern)
                {
                    CompleteChordCapture(completedPattern);
                }
                else if (patternState == InputPatternCaptureState.Rejected)
                {
                    CancelChordCapture(
                        "2 秒录制无效：请完整按下并释放按键，且不要超过 32 个边沿",
                        InfoBarSeverity.Warning);
                }
            }
        };
        _inputDiagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _inputDiagnosticTimer.Tick += (_, _) => UpdateInputDiagnosticCountdown();
        _actionCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _actionCaptureTimer.Tick += (_, _) => UpdateShortcutCapture();
        _foregroundProcessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _foregroundProcessTimer.Tick += (_, _) => UpdateForegroundProcess();
        _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _historySaveTimer.Tick += HistorySaveTimer_Tick;
        _conditionCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _conditionCaptureTimer.Tick += (_, _) => UpdateConditionCapture();
        _foregroundWindowWatcher.ForegroundChanged += ForegroundWindowWatcher_ForegroundChanged;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        SetMode(mappingMode: false);
        ResizeToUsefulBounds();
    }

    private static Brush BrushResource(string key) => (Brush)Application.Current.Resources[key];

    private void BuildAppearanceThemeSwatches()
    {
        AppearanceThemeSwatchHost.Children.Clear();
        AppearanceThemeSwatchHost.ColumnDefinitions.Clear();
        AppearanceThemeSwatchHost.RowDefinitions.Clear();
        _appearanceThemeSwatches.Clear();

        const int columns = 4;
        var themes = AppearanceThemeCatalog.All;
        var rows = (themes.Count + columns - 1) / columns;
        for (var column = 0; column < columns; column++)
        {
            AppearanceThemeSwatchHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        for (var row = 0; row < rows; row++)
        {
            AppearanceThemeSwatchHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var index = 0; index < themes.Count; index++)
        {
            var theme = themes[index];
            var preview = new Border
            {
                Height = 26,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(AppearanceThemeApplier.Parse(theme.Background)),
                BorderThickness = new Thickness(1),
                Child = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(AppearanceThemeApplier.Parse(theme.Accent)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var button = new Button
            {
                Padding = new Thickness(0),
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = theme.Id,
                Content = preview
            };
            ToolTipService.SetToolTip(button, $"{theme.DisplayName} · {theme.Description}");
            button.Click += AppearanceTheme_Click;
            Grid.SetRow(button, index / columns);
            Grid.SetColumn(button, index % columns);
            AppearanceThemeSwatchHost.Children.Add(button);
            _appearanceThemeSwatches.Add((theme.Id, preview));
        }

        RefreshAppearanceThemeSwatches(_configuration.AppearanceThemeId);
    }

    private void ApplyAppearanceTheme(string? themeId)
    {
        var theme = AppearanceThemeCatalog.Resolve(themeId);
        AppearanceThemeApplier.Apply(theme, RootGrid);
        RefreshAppearanceThemeSwatches(theme.Id);
    }

    private void RefreshAppearanceThemeSwatches(string selectedId)
    {
        foreach (var (themeId, preview) in _appearanceThemeSwatches)
        {
            var selected = string.Equals(themeId, selectedId, StringComparison.OrdinalIgnoreCase);
            preview.BorderBrush = selected
                ? new SolidColorBrush(AppearanceThemeApplier.Parse(
                    AppearanceThemeCatalog.Resolve(themeId).Accent))
                : BrushResource("KpLineBrush");
            preview.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private async void AppearanceTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_suspendWorkspaceCapture || _isClosing || sender is not Button { Tag: string themeId })
        {
            return;
        }

        if (string.Equals(_configuration.AppearanceThemeId, themeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _configuration = _configuration with { AppearanceThemeId = themeId };
        ApplyAppearanceTheme(themeId);
        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (!IsCurrentSaveResult(saveResult))
        {
            return;
        }

        if (saveResult.Outcome == ConfigurationSaveOutcome.Failed)
        {
            ApplyAppearanceTheme(_configuration.AppearanceThemeId);
            return;
        }

        var theme = AppearanceThemeCatalog.Resolve(themeId);
        ShowToast($"已切换外观：{theme.DisplayName}");
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "KeyPilot.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception exception)
        {
            // Icon decoration is non-critical; record it without weakening application startup.
            RuntimeDiagnostics.Write("WindowIcon", exception.Message, exception);
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIconHost();
            _trayIcon.OpenRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
            _trayIcon.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitFromTray);
            _trayIcon.Show(
                System.IO.Path.Combine(AppContext.BaseDirectory, "KeyPilot.ico"),
                "KeyPilot · 单击打开工作台");
            AppWindow.Closing += AppWindow_Closing;
            AppWindow.Changed += AppWindow_Changed;
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write("Tray", exception.Message, exception);
        }
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _isClosing)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (_exitRequested || _isClosing || _hidingToTray || !args.DidPresenterChange)
        {
            return;
        }

        if (sender.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        if (_exitRequested || _isClosing)
        {
            return;
        }

        _hidingToTray = true;
        try
        {
            AppWindow.Hide();
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write("Tray", exception.Message, exception);
        }
        finally
        {
            _hidingToTray = false;
        }
    }

    private void RestoreFromTray()
    {
        if (_exitRequested || _isClosing)
        {
            return;
        }

        try
        {
            AppWindow.Show();
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Restore();
            }

            Activate();
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write("Tray", exception.Message, exception);
        }
    }

    private void ExitFromTray()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _trayIcon?.Hide();
        Close();
    }

    private static string GetDisplayVersion()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            var productVersion = string.IsNullOrWhiteSpace(processPath)
                ? null
                : FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                var metadataIndex = productVersion.IndexOf('+');
                return metadataIndex > 0 ? productVersion[..metadataIndex] : productVersion;
            }
        }
        catch
        {
            // Version text is cosmetic and must never affect application startup.
        }

        return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
    }

    private void UpdateForegroundProcess()
    {
        if (_isClosing)
        {
            return;
        }

        var processName = _foregroundProcessReader.TryGetForegroundProcessName(out var detected)
            ? detected
            : null;
        var identity = TryReadForegroundIdentity(processName);
        var running = _applicationCatalog.ListRunningWindowedApplications();
        var runningProcessNames = running
            .Select(item => item.NormalizedProcessName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runningFamilies = running
            .Select(item => item.NormalizedPackageFamilyName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var signature = BuildApplicationContextSignature(
            processName,
            identity?.PackageFamilyName,
            runningProcessNames,
            runningFamilies);
        var processChanged = !string.Equals(
            _currentForegroundProcessName,
            processName,
            StringComparison.OrdinalIgnoreCase);
        var contextChanged = !string.Equals(
            _applicationContextSignature,
            signature,
            StringComparison.Ordinal);

        if ((processChanged || contextChanged) && !_savingProfileSettings)
        {
            var armProcessName = IsKeyPilotProcess(processName) ? null : processName;
            RefreshEffectiveRuntimeConfiguration(
                force: false,
                executionContextChanged: true,
                new MappingApplicationContext
                {
                    ForegroundProcessName = armProcessName,
                    ForegroundPackageFamilyName = armProcessName is null
                        ? null
                        : identity?.PackageFamilyName,
                    RunningProcessNames = runningProcessNames,
                    RunningPackageFamilyNames = runningFamilies
                });
        }

        _currentForegroundProcessName = processName;
        _currentForegroundIdentity = identity;
        _currentForegroundPackageFamily = identity?.PackageFamilyName;
        _runningProcessNames = runningProcessNames;
        _runningPackageFamilyNames = runningFamilies;
        _applicationContextSignature = signature;

        if (identity is not null &&
            !IsKeyPilotProcess(identity.ProcessName) &&
            !IsKeyPilotProcess(processName))
        {
            _lastExternalForegroundProcessName = identity.ProcessName;
            RememberForegroundApplication(identity);
        }
        else if (!string.IsNullOrWhiteSpace(processName) &&
                 !IsKeyPilotProcess(processName) &&
                 !IsApplicationFrameHost(processName))
        {
            _lastExternalForegroundProcessName = processName;
        }

        ObserveConditionCapture(identity, processName);

        if (ProfileSettingsDrawer.Visibility == Visibility.Visible)
        {
            UpdateDetectedProcessEditor();
        }

        if (MappingDrawer.Visibility == Visibility.Visible)
        {
            RefreshConditionPickerLists();
        }

        UpdateForegroundStatusText();
    }

    private ApplicationIdentity? TryReadForegroundIdentity(string? processName)
    {
        if (_applicationCatalog.TryGetForegroundIdentity(out var identity) &&
            identity.HasMatchKey &&
            !IsKeyPilotProcess(identity.ProcessName))
        {
            return identity;
        }

        if (string.IsNullOrWhiteSpace(processName) ||
            IsKeyPilotProcess(processName) ||
            IsApplicationFrameHost(processName))
        {
            return null;
        }

        return new ApplicationIdentity
        {
            ProcessName = ApplicationProfileResolver.NormalizeProcessName(processName),
            DisplayName = DisplayProcessName(processName)
        };
    }

    private static string BuildApplicationContextSignature(
        string? processName,
        string? packageFamilyName,
        IReadOnlyList<string> runningProcessNames,
        IReadOnlyList<string> runningPackageFamilyNames) =>
        string.Join(
            "|",
            ApplicationProfileResolver.NormalizeProcessName(processName),
            packageFamilyName?.Trim() ?? string.Empty,
            string.Join(",", runningProcessNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", runningPackageFamilyNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));

    private void RememberForegroundApplication(ApplicationIdentity identity)
    {
        var next = RecentApplicationCatalog.RememberForeground(
            _configuration.RecentForegroundApplications,
            identity,
            DateTimeOffset.UtcNow)
            .ToList();
        if (AreRecentApplicationsEqual(_configuration.RecentForegroundApplications, next))
        {
            return;
        }

        _configuration = _configuration with { RecentForegroundApplications = next };
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private static bool AreRecentApplicationsEqual(
        IReadOnlyList<RecentApplicationEntry> left,
        IReadOnlyList<RecentApplicationEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftApp = left[index].Application;
            var rightApp = right[index].Application;
            if (!leftApp.Matches(rightApp) ||
                !string.Equals(leftApp.DisplayName, rightApp.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(
                    leftApp.NormalizedPackageFamilyName,
                    rightApp.NormalizedPackageFamilyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async void HistorySaveTimer_Tick(object? sender, object e)
    {
        _historySaveTimer.Stop();
        if (_isClosing ||
            _configurationWriteBlocked ||
            _savingProfileSettings ||
            _workspaceRestore is null ||
            _configurationLoadTask is { IsCompleted: false } ||
            ProfileSettingsDrawer.Visibility == Visibility.Visible ||
            MappingDrawer.Visibility == Visibility.Visible)
        {
            return;
        }

        await PersistWorkspaceConfigurationAsync();
    }

    private void UpdateForegroundStatusText()
    {
        if (FooterForegroundText is null)
        {
            return;
        }

        if (_currentForegroundIdentity is { } identity &&
            !IsKeyPilotProcess(identity.ProcessName))
        {
            FooterForegroundText.Text = identity.EffectiveDisplayName;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentForegroundProcessName) &&
            !IsKeyPilotProcess(_currentForegroundProcessName))
        {
            FooterForegroundText.Text = DisplayProcessName(_currentForegroundProcessName);
            return;
        }

        FooterForegroundText.Text = "KeyPilot";
    }

    private void ForegroundWindowWatcher_ForegroundChanged(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(UpdateForegroundProcess);
    }

    private bool IsObservedForegroundContextCurrent()
    {
        if (!_foregroundProcessReader.TryGetForegroundProcessName(out var observed))
        {
            return false;
        }

        var expected = Volatile.Read(ref _currentForegroundProcessName);
        return string.Equals(
            ApplicationProfileResolver.NormalizeProcessName(observed),
            ApplicationProfileResolver.NormalizeProcessName(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyPilotProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var ownPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(ownPath))
        {
            return false;
        }

        var ownName = ApplicationProfileResolver.NormalizeProcessName(ownPath);
        var otherName = ApplicationProfileResolver.NormalizeProcessName(processName);
        return ownName.Length > 0 &&
            string.Equals(ownName, otherName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicationFrameHost(string? processName) =>
        string.Equals(
            ApplicationProfileResolver.NormalizeProcessName(processName),
            ApplicationIdentity.ApplicationFrameHostProcessName,
            StringComparison.OrdinalIgnoreCase);

    private static string DisplayProcessName(string processName)
    {
        var fileName = System.IO.Path.GetFileName(processName.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"{fileName}.exe";
    }

    private void BuildKeyboard()
    {
        foreach (var definition in KeyboardLayout.FunctionRow)
        {
            if (definition is null)
            {
                FunctionRowPanel.Children.Add(new Border { Width = 18 });
                continue;
            }

            FunctionRowPanel.Children.Add(CreateStandardNodeButton(
                CreateKeyboardState(definition),
                width: Math.Max(39, definition.Units * 39),
                height: 35));
        }

        foreach (var rowDefinitions in KeyboardLayout.MainRows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (var definition in rowDefinitions)
            {
                row.Children.Add(CreateStandardNodeButton(
                    CreateKeyboardState(definition),
                    width: Math.Max(39, definition.Units * 39),
                    height: 35));
            }

            KeyboardMainPanel.Children.Add(row);
        }

        BuildNavigationKeys();
        BuildNumpadKeys();
    }

    private void BuildNavigationKeys()
    {
        for (var column = 0; column < 3; column++)
        {
            NavigationGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var row = 0; row < 6; row++)
        {
            NavigationGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        }

        NavigationGrid.ColumnSpacing = 5;
        NavigationGrid.RowSpacing = 5;

        var positions = new (int Row, int Column)[]
        {
            (0, 0), (0, 1), (0, 2),
            (1, 0), (1, 1), (1, 2),
            (4, 1), (5, 0), (5, 1), (5, 2)
        };

        for (var index = 0; index < KeyboardLayout.Navigation.Count; index++)
        {
            var button = CreateStandardNodeButton(CreateKeyboardState(KeyboardLayout.Navigation[index]), double.NaN, 35);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(button, positions[index].Row);
            Grid.SetColumn(button, positions[index].Column);
            NavigationGrid.Children.Add(button);
        }
    }

    private void BuildNumpadKeys()
    {
        for (var column = 0; column < 4; column++)
        {
            NumpadGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var row = 0; row < 5; row++)
        {
            NumpadGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        }

        NumpadGrid.ColumnSpacing = 5;
        NumpadGrid.RowSpacing = 5;

        var byId = KeyboardLayout.Numpad.ToDictionary(definition => definition.Id);
        AddNumpad(byId["NumLock"], 0, 0);
        AddNumpad(byId["NumpadDivide"], 0, 1);
        AddNumpad(byId["NumpadMultiply"], 0, 2);
        AddNumpad(byId["NumpadSubtract"], 0, 3);
        AddNumpad(byId["Numpad7"], 1, 0);
        AddNumpad(byId["Numpad8"], 1, 1);
        AddNumpad(byId["Numpad9"], 1, 2);
        AddNumpad(byId["NumpadAdd"], 1, 3, rowSpan: 2);
        AddNumpad(byId["Numpad4"], 2, 0);
        AddNumpad(byId["Numpad5"], 2, 1);
        AddNumpad(byId["Numpad6"], 2, 2);
        AddNumpad(byId["Numpad1"], 3, 0);
        AddNumpad(byId["Numpad2"], 3, 1);
        AddNumpad(byId["Numpad3"], 3, 2);
        AddNumpad(byId["NumpadEnter"], 3, 3, rowSpan: 2);
        AddNumpad(byId["Numpad0"], 4, 0, columnSpan: 2);
        AddNumpad(byId["NumpadDecimal"], 4, 2);
    }

    private void AddNumpad(KeyDefinition definition, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        var button = CreateStandardNodeButton(CreateKeyboardState(definition), double.NaN, double.NaN);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        Grid.SetRowSpan(button, rowSpan);
        Grid.SetColumnSpan(button, columnSpan);
        NumpadGrid.Children.Add(button);
    }

    private static InputNodeState CreateKeyboardState(KeyDefinition definition)
    {
        var source = BuildDefaultKeyboardSource(definition.Id);
        return new InputNodeState
        {
            Id = definition.Id,
            Label = definition.Label,
            FriendlyName = FriendlyKeyboardName(definition.Id, definition.Label),
            Kind = InputNodeKind.Keyboard,
            RawCode = definition.Id,
            CaptureIdentity = source.CanonicalKey,
            CapturedSource = source,
            Location = KeyboardLocation(definition.Id)
        };
    }

    private void BuildGamepad()
    {
        AddGamepadNode("LB", 25, 8, 68, 25, rounded: false);
        AddGamepadNode("RB", 217, 8, 68, 25, rounded: false);
        AddGamepadVirtualNode("LT", 25, 36, 44, 18, XInputVirtualControl.LeftTrigger);
        AddGamepadVirtualNode("RT", 241, 36, 44, 18, XInputVirtualControl.RightTrigger);
        AddGamepadNode("View", 116, 40, 31, 18, rounded: false);
        AddGamepadNode("Menu", 163, 40, 31, 18, rounded: false);
        AddGamepadNode("↑", 48, 55, 23, 23, rounded: false, id: "DPadUp");
        AddGamepadNode("←", 28, 75, 23, 23, rounded: false, id: "DPadLeft");
        AddGamepadNode("→", 68, 75, 23, 23, rounded: false, id: "DPadRight");
        AddGamepadNode("↓", 48, 95, 23, 23, rounded: false, id: "DPadDown");
        AddGamepadNode("Y", 233, 55, 27, 27, rounded: true);
        AddGamepadNode("X", 210, 78, 27, 27, rounded: true);
        AddGamepadNode("B", 256, 78, 27, 27, rounded: true);
        AddGamepadNode("A", 233, 101, 27, 27, rounded: true);
        AddGamepadNode("PS", 139, 38, 22, 22, rounded: true, id: "PS", buttonCode: SonyHidIdentity.PsButton);
        AddGamepadNode("TP", 108, 86, 94, 16, rounded: false, id: "Touchpad", buttonCode: SonyHidIdentity.TouchpadClick);

        AddGamepadVirtualNode("L↑", 123, 121, 26, 18, XInputVirtualControl.LeftStickUp);
        AddGamepadVirtualNode("L←", 103, 141, 20, 24, XInputVirtualControl.LeftStickLeft);
        AddGamepadNode("L3", 124, 140, 24, 26, rounded: true, id: "LStick");
        AddGamepadVirtualNode("L→", 149, 141, 20, 24, XInputVirtualControl.LeftStickRight);
        AddGamepadVirtualNode("L↓", 123, 167, 26, 18, XInputVirtualControl.LeftStickDown);

        AddGamepadVirtualNode("R↑", 189, 121, 26, 18, XInputVirtualControl.RightStickUp);
        AddGamepadVirtualNode("R←", 169, 141, 20, 24, XInputVirtualControl.RightStickLeft);
        AddGamepadNode("R3", 190, 140, 24, 26, rounded: true, id: "RStick");
        AddGamepadVirtualNode("R→", 215, 141, 20, 24, XInputVirtualControl.RightStickRight);
        AddGamepadVirtualNode("R↓", 189, 167, 26, 18, XInputVirtualControl.RightStickDown);

        AddGamepadRotationNode("L↺", 61, 191, 42, 18, GamepadRotationControl.LeftCounterClockwise);
        AddGamepadRotationNode("L↻", 107, 191, 42, 18, GamepadRotationControl.LeftClockwise);
        AddGamepadRotationNode("R↺", 161, 191, 42, 18, GamepadRotationControl.RightCounterClockwise);
        AddGamepadRotationNode("R↻", 207, 191, 42, 18, GamepadRotationControl.RightClockwise);
    }

    private void AddGamepadNode(
        string label,
        double left,
        double top,
        double width,
        double height,
        bool rounded,
        string? id = null,
        int? buttonCode = null)
    {
        var state = new InputNodeState
        {
            Id = id ?? label,
            Label = label,
            FriendlyName = $"手柄 {label}",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            Location = "Gamepad"
        };
        state.CapturedSource = buttonCode is int code
            ? BuildGamepadButtonSource(code)
            : BuildDefaultXInputSource(state.Id);
        state.CaptureIdentity = state.CapturedSource.CanonicalKey;
        var button = CreateStandardNodeButton(state, width, height);
        button.CornerRadius = rounded ? new CornerRadius(width / 2) : new CornerRadius(6);
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, top);
        GamepadSurface.Children.Add(button);
    }

    private void AddGamepadVirtualNode(
        string label,
        double left,
        double top,
        double width,
        double height,
        XInputVirtualControl control)
    {
        var state = new InputNodeState
        {
            Id = control.ToString(),
            Label = label,
            FriendlyName = $"手柄 {label}",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            Location = "Gamepad Analog"
        };
        state.CapturedSource = BuildDefaultXInputVirtualSource(control);
        state.CaptureIdentity = state.CapturedSource.CanonicalKey;
        var button = CreateStandardNodeButton(state, width, height);
        button.CornerRadius = new CornerRadius(5);
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, top);
        GamepadSurface.Children.Add(button);
    }

    private void AddGamepadRotationNode(
        string label,
        double left,
        double top,
        double width,
        double height,
        GamepadRotationControl control)
    {
        var state = new InputNodeState
        {
            Id = control.ToString(),
            Label = label,
            FriendlyName = $"手柄 {label} 一圈",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            Location = "Gamepad Rotation"
        };
        state.CapturedSource = BuildDefaultXInputRotationSource(control);
        state.CaptureIdentity = state.CapturedSource.CanonicalKey;
        var button = CreateStandardNodeButton(state, width, height);
        button.CornerRadius = new CornerRadius(5);
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, top);
        GamepadSurface.Children.Add(button);
    }

    private void BuildSpecialSlots()
    {
        for (var column = 0; column < 5; column++)
        {
            SpecialSlotsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        SpecialSlotsGrid.RowDefinitions.Add(new RowDefinition());
        SpecialSlotsGrid.RowDefinitions.Add(new RowDefinition());

        for (var number = 1; number <= 10; number++)
        {
            var state = new InputNodeState
            {
                Id = number.ToString(),
                Label = $"SPECIAL {number:00}",
                FriendlyName = "等待采集",
                Kind = InputNodeKind.Special,
                RawCode = string.Empty,
                Location = "HID"
            };
            var button = CreateSpecialNodeButton(state, number);
            Grid.SetRow(button, (number - 1) / 5);
            Grid.SetColumn(button, (number - 1) % 5);
            SpecialSlotsGrid.Children.Add(button);
        }
    }

    private void BuildRemoteButtons()
    {
        const double bodyWidth = 220;
        const double bodyHeight = 446;
        foreach (var hotspot in Rc003DeviceIdentity.Hotspots)
        {
            Rc003DeviceIdentity.TryGetButton(hotspot.ButtonId, out var label, out var usage, out var degraded);
            var state = new InputNodeState
            {
                Id = hotspot.ButtonId,
                Label = label,
                FriendlyName = degraded ? $"遥控器 {label}（系统可能丢弃）" : $"遥控器 {label}",
                Kind = InputNodeKind.Remote,
                RawCode = $"HID 0x07/0x{usage:X4}",
                Location = "RC003",
                CapturedSource = Rc003DeviceIdentity.CreateSource(hotspot.ButtonId)
            };
            state.CaptureIdentity = state.CapturedSource.CanonicalKey;
            var width = Math.Max(28, hotspot.Width * bodyWidth);
            var height = Math.Max(24, hotspot.Height * bodyHeight);
            var button = CreateStandardNodeButton(state, width, height);
            button.Opacity = degraded ? 0.7 : 0.95;
            button.CornerRadius = hotspot.ButtonId is "ok" or "up" or "down" or "left" or "right"
                ? new CornerRadius(Math.Min(width, height) / 2)
                : new CornerRadius(8);
            Canvas.SetLeft(button, hotspot.X * bodyWidth);
            Canvas.SetTop(button, hotspot.Y * bodyHeight);
            RemoteSurface.Children.Add(button);

            var legend = new TextBlock
            {
                Text = degraded ? $"{label} · 系统可能丢弃" : label,
                FontSize = 10,
                Foreground = BrushResource("KpMutedBrush")
            };
            RemoteLegendPanel.Children.Add(legend);
        }
    }

    private void BuildMouse()
    {
        (string Id, string Label, ushort VirtualKey)[] buttons =
        [
            ("Middle", "中键", 0x04),
            ("XButton1", "后退", 0x05),
            ("XButton2", "前进", 0x06)
        ];
        foreach (var (id, label, virtualKey) in buttons)
        {
            var state = new InputNodeState
            {
                Id = id,
                Label = label,
                FriendlyName = $"鼠标 {label}",
                Kind = InputNodeKind.Mouse,
                RawCode = $"VK 0x{virtualKey:X2}",
                Location = "Mouse",
                CapturedSource = BuildDefaultMouseSource(virtualKey)
            };
            state.CaptureIdentity = state.CapturedSource.CanonicalKey;
            var button = CreateStandardNodeButton(state, 88, 44);
            button.CornerRadius = new CornerRadius(8);
            MouseButtonsPanel.Children.Add(button);
        }
    }

    private void RefreshCaptureEndpoints_Click(object sender, RoutedEventArgs e) => RefreshConnectedMicrophones();

    private void RefreshConnectedMicrophones()
    {
        IReadOnlyList<WindowsCaptureEndpoint> endpoints = [];
        string? scanError = null;
        try
        {
            endpoints = WindowsAudioCaptureCatalog.ListActive();
        }
        catch (Exception exception)
        {
            scanError = exception.Message;
        }

        try
        {
            var microphones = ConnectedMicrophoneCatalog.List(
                endpoints,
                _rc003HidConnected || _rc003Voice.IsRunning,
                _microphoneAliases.Snapshot());

            ConnectedMicrophonePanel.Children.Clear();
            foreach (var microphone in microphones)
            {
                ConnectedMicrophonePanel.Children.Add(CreateConnectedMicrophoneRow(microphone));
            }

            MicEmptyText.Visibility = microphones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MicBackendStatusText.Text = microphones.Count == 0
                ? "等待设备连接"
                : _rc003Voice.IsRunning
                    ? $"{microphones.Count} 台已连接 · 语音桥运行中"
                    : $"{microphones.Count} 台已连接";
            MicBackendStatusText.Foreground = microphones.Count == 0
                ? BrushResource("KpAmberBrush")
                : BrushResource("KpMintBrush");
            if (scanError is not null)
            {
                MicDetailText.Text = scanError;
            }
            else if (!string.IsNullOrWhiteSpace(_rc003Voice.Status) && _rc003Voice.IsRunning)
            {
                MicDetailText.Text = _rc003Voice.Status;
            }
        }
        catch (Exception exception)
        {
            MicBackendStatusText.Text = "扫描失败";
            MicDetailText.Text = exception.Message;
            MicBackendStatusText.Foreground = BrushResource("KpAmberBrush");
        }
    }

    private Border CreateConnectedMicrophoneRow(ConnectedMicrophone microphone)
    {
        var nameBox = new TextBox
        {
            Text = string.Equals(microphone.DisplayName, microphone.DeviceName, StringComparison.Ordinal)
                ? string.Empty
                : microphone.DisplayName,
            PlaceholderText = $"显示名称，默认 {microphone.DeviceName}",
            MinHeight = 32
        };
        nameBox.LostFocus += (_, _) => CommitMicrophoneAlias(microphone.Id, nameBox.Text);
        nameBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                CommitMicrophoneAlias(microphone.Id, nameBox.Text);
                args.Handled = true;
            }
        };

        var setButton = new Button
        {
            Content = microphone.IsWindowsDefault ? "已是当前麦克风" : "设为麦克风",
            Style = (Style)Application.Current.Resources["KpButtonStyle"],
            Padding = new Thickness(12, 6, 12, 6),
            Tag = microphone
        };
        if (!microphone.IsWindowsDefault)
        {
            setButton.Background = BrushResource("KpMintBrush");
            setButton.BorderBrush = BrushResource("KpMintBrush");
            setButton.Foreground = BrushResource("KpOnAccentBrush");
        }

        setButton.Click += SetConnectedMicrophone_Click;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        buttons.Children.Add(setButton);
        if (microphone.Kind == ConnectedMicrophoneKind.Rc003 && _rc003Voice.IsRunning)
        {
            var stopButton = new Button
            {
                Content = "停止语音",
                Style = (Style)Application.Current.Resources["KpButtonStyle"],
                Padding = new Thickness(12, 6, 12, 6)
            };
            stopButton.Click += StopRc003Voice_Click;
            buttons.Children.Add(stopButton);
        }

        var textColumn = new StackPanel { Spacing = 6 };
        textColumn.Children.Add(new TextBlock
        {
            Text = microphone.DeviceName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = microphone.Status,
            FontSize = 8,
            Foreground = BrushResource("KpMintBrush")
        });
        textColumn.Children.Add(nameBox);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(textColumn);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = BrushResource("KpPanelSecondaryBrush"),
            BorderBrush = BrushResource("KpLineBrush"),
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    private void CommitMicrophoneAlias(string id, string? alias)
    {
        try
        {
            _microphoneAliases.Set(id, alias);
        }
        catch (Exception exception)
        {
            ShowToast($"无法保存名称：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void SetConnectedMicrophone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConnectedMicrophone microphone })
        {
            return;
        }

        try
        {
            if (microphone.Kind == ConnectedMicrophoneKind.Rc003 &&
                microphone.RequiresVoiceBridge &&
                !_rc003Voice.IsRunning)
            {
                await _rc003Voice.StartAsync();
            }

            var endpoints = WindowsAudioCaptureCatalog.ListActive();
            var current = ConnectedMicrophoneCatalog.List(
                    endpoints,
                    _rc003HidConnected || _rc003Voice.IsRunning,
                    _microphoneAliases.Snapshot())
                .FirstOrDefault(item => string.Equals(item.Id, microphone.Id, StringComparison.OrdinalIgnoreCase));
            var endpointId = current?.CaptureEndpointId ?? microphone.CaptureEndpointId;
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                ShowToast(
                    microphone.Kind == ConnectedMicrophoneKind.Rc003
                        ? "这台遥控器已连接，但还不能被 Windows 选为录音设备。请安装 VB-CABLE 后再设为麦克风。"
                        : "这台设备还没有出现在 Windows 录音设备里。",
                    InfoBarSeverity.Warning);
                RefreshConnectedMicrophones();
                return;
            }

            WindowsAudioCaptureCatalog.SetDefaultCapture(endpointId);
            ShowToast($"已把 {current?.DisplayName ?? microphone.DisplayName} 设为麦克风");
            MicDetailText.Text = _rc003Voice.IsRunning ? _rc003Voice.Status : MicDetailText.Text;
            RefreshConnectedMicrophones();
        }
        catch (Exception exception)
        {
            MicDetailText.Text = exception.Message;
            ShowToast($"无法设为麦克风：{exception.Message}", InfoBarSeverity.Error);
            RefreshConnectedMicrophones();
        }
    }

    private void StopRc003Voice_Click(object sender, RoutedEventArgs e)
    {
        _rc003Voice.Stop();
        MicDetailText.Text = _rc003Voice.Status;
        RefreshConnectedMicrophones();
    }

    private void Rc003Battery_BatteryChanged(byte? percent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing)
            {
                return;
            }

            _rc003BatteryPercent = percent;
            UpdateRemoteBackendStatus();
        });
    }

    private async Task EnsureRc003VoiceRunningAsync()
    {
        if (_rc003Voice.IsRunning || Interlocked.Exchange(ref _rc003VoiceStarting, 1) == 1)
        {
            return;
        }

        try
        {
            await _rc003Voice.StartAsync();
            TrySetDefaultCableCapture();
        }
        catch (Exception exception)
        {
            MicDetailText.Text = exception.Message;
            ShowToast($"无法启动遥控器麦克风：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _rc003VoiceStarting, 0);
            RefreshConnectedMicrophones();
        }
    }

    private async Task EnsureRc003BatteryRunningAsync()
    {
        if (_rc003Battery.IsRunning || Interlocked.Exchange(ref _rc003BatteryStarting, 1) == 1)
        {
            return;
        }

        try
        {
            await _rc003Battery.StartAsync();
        }
        catch (Exception)
        {
            _rc003BatteryPercent = null;
        }
        finally
        {
            Interlocked.Exchange(ref _rc003BatteryStarting, 0);
            UpdateRemoteBackendStatus();
        }
    }

    private void TrySetDefaultCableCapture()
    {
        try
        {
            var cable = WindowsAudioCaptureCatalog.FindCableOutput(WindowsAudioCaptureCatalog.ListActive());
            if (cable is not null)
            {
                WindowsAudioCaptureCatalog.SetDefaultCapture(cable.Id);
            }
        }
        catch (Exception exception)
        {
            ShowToast($"语音桥已接通，但无法设为默认麦克风：{exception.Message}", InfoBarSeverity.Warning);
        }
    }

    private void UpdateGamepadBackendStatus()
    {
        if (_sonyPadStates.Count > 0 && !string.IsNullOrWhiteSpace(_sonyStatusName))
        {
            var state = _sonyPadStates.Values.First();
            GamepadBackendStatusText.Text = DeviceBatteryStatus.Format(
                $"{_sonyStatusName} 已连接",
                state.BatteryPercent,
                state.Charging);
            GamepadBackendStatusText.Foreground = BrushForBattery(state.BatteryPercent, connected: true);
            return;
        }

        GamepadBackendStatusText.Text = _connectedGamepadSlots.Count == 0
            ? "正在等待手柄"
            : _connectedGamepadSlots.Count == 1
                ? $"USER {_connectedGamepadSlots.Min() + 1} 已连接"
                : $"{_connectedGamepadSlots.Count} 个手柄已连接";
        GamepadBackendStatusText.Foreground = _connectedGamepadSlots.Count == 0
            ? BrushResource("KpAmberBrush")
            : BrushResource("KpMintBrush");
    }

    private void UpdateRemoteBackendStatus()
    {
        if (!_rc003HidConnected && !_rc003Voice.IsRunning)
        {
            RemoteBackendStatusText.Text = "等待遥控器 HID";
            RemoteBackendStatusText.Foreground = BrushResource("KpAmberBrush");
            return;
        }

        RemoteBackendStatusText.Text = DeviceBatteryStatus.Format("RC003 已连接", _rc003BatteryPercent, charging: null);
        RemoteBackendStatusText.Foreground = BrushForBattery(_rc003BatteryPercent, connected: true);
    }

    private Brush BrushForBattery(byte? percent, bool connected)
    {
        if (!connected)
        {
            return BrushResource("KpAmberBrush");
        }

        return DeviceBatteryStatus.Tone(percent) switch
        {
            DeviceBatteryTone.Critical => BrushResource("KpRedBrush"),
            DeviceBatteryTone.Low => BrushResource("KpAmberBrush"),
            _ => BrushResource("KpMintBrush")
        };
    }

    private Button CreateStandardNodeButton(InputNodeState state, double width, double height)
    {
        var label = new TextBlock
        {
            Text = state.Label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = state.Kind == InputNodeKind.Gamepad ? 8 : 8,
            FontWeight = FontWeights.SemiBold
        };
        var target = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            FontSize = 7,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var mappingDot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Visibility = Visibility.Collapsed,
            Fill = BrushResource("KpMintBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 1)
        };
        var content = new Grid { MinWidth = 0 };
        content.Children.Add(label);
        content.Children.Add(target);
        content.Children.Add(mappingDot);

        var button = new Button
        {
            Width = width,
            Height = height,
            MinWidth = 0,
            Padding = new Thickness(4, 1, 4, 3),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = state
        };

        var visual = new InputNodeVisual(state, button, label, target, mappingDot, null);
        RegisterNodeVisual(visual);
        return button;
    }

    private Button CreateSpecialNodeButton(InputNodeState state, int number)
    {
        var header = new TextBlock
        {
            Text = $"SPECIAL {number:00}          HID",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 8,
            Foreground = BrushResource("KpDimBrush")
        };
        var label = new TextBlock
        {
            Text = state.FriendlyName,
            Margin = new Thickness(0, 9, 0, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var raw = new TextBlock
        {
            Text = "等待真实 HID 或未知扫描码",
            Margin = new Thickness(0, 7, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 7,
            Foreground = BrushResource("KpDimBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var target = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 7, 0, 0),
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = BrushResource("KpOnAccentBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var mappingDot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Visibility = Visibility.Collapsed,
            Fill = BrushResource("KpMintBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var content = new Grid();
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(label);
        stack.Children.Add(raw);
        stack.Children.Add(target);
        content.Children.Add(stack);
        content.Children.Add(mappingDot);

        var button = new Button
        {
            MinHeight = 91,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(9),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = state
        };

        var visual = new InputNodeVisual(state, button, label, target, mappingDot, raw);
        RegisterNodeVisual(visual);
        return button;
    }

    private void RegisterNodeVisual(InputNodeVisual visual)
    {
        _nodeVisuals.Add(visual.State.StableId, visual);
        visual.Button.Click += InputNode_Click;
        visual.Button.RightTapped += InputNode_RightTapped;
        visual.Button.ContextFlyout = CreateNodeMenu(visual.State);
        RenderNode(visual);
    }

    private MenuFlyout CreateNodeMenu(InputNodeState state)
    {
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "创建或编辑映射" };
        var inspect = new MenuFlyoutItem { Text = "测试并查看原始代码" };
        var copy = new MenuFlyoutItem { Text = "复制按键信息" };
        var rename = new MenuFlyoutItem { Text = "重命名特殊键" };
        var recordChord = new MenuFlyoutItem { Text = "录制 2 秒按键过程" };
        var clear = new MenuFlyoutItem { Text = "清除此按键的映射" };

        edit.Click += (_, _) => OpenMapping(state);
        inspect.Click += (_, _) =>
        {
            SelectNode(state, updateCaptureStatus: true);
            ShowToast(string.IsNullOrWhiteSpace(state.RawCode) ? "该槽位尚未采集输入" : state.RawCode);
        };
        copy.Click += (_, _) => CopyInputTargetToClipboard(state.CapturedSource);
        rename.Click += async (_, _) => await RenameSpecialKeyAsync(state);
        recordChord.Click += (_, _) => ArmChordCapture(state);
        clear.Click += async (_, _) =>
        {
            state.Mapping = null;
            UpdateMappingCount();
            RenderAllNodes();
            SelectNode(state, updateCaptureStatus: false);
            var saveResult = await PersistWorkspaceConfigurationAsync();
            if (!IsCurrentSaveResult(saveResult))
            {
                return;
            }

            if (saveResult.Outcome == ConfigurationSaveOutcome.Succeeded)
            {
                ShowToast("映射已清除并写入本地配置");
            }
        };

        menu.Items.Add(edit);
        menu.Items.Add(inspect);
        menu.Items.Add(copy);
        menu.Items.Add(rename);
        menu.Items.Add(recordChord);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);
        menu.Opening += (_, _) =>
        {
            edit.Text = state.Mapping is null ? "为此按键创建映射" : "编辑此按键的映射";
            edit.IsEnabled = (state.Kind != InputNodeKind.Special || !string.IsNullOrWhiteSpace(state.CaptureIdentity)) &&
                state.Mapping?.IsReadOnlyPassthrough != true;
            copy.IsEnabled = state.CapturedSource is not null;
            rename.Visibility = state.Kind == InputNodeKind.Special && !string.IsNullOrWhiteSpace(state.RawCode)
                ? Visibility.Visible
                : Visibility.Collapsed;
            recordChord.Visibility = state.Kind == InputNodeKind.Special
                ? Visibility.Visible
                : Visibility.Collapsed;
            clear.IsEnabled = state.Mapping is not null;
        };
        return menu;
    }

    private void InputNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InputNodeState state })
        {
            return;
        }

        if (!_captureEnabled)
        {
            ShowToast("采集已暂停；仍可右键编辑映射", InfoBarSeverity.Informational);
            return;
        }

        if (state.Kind == InputNodeKind.Special && string.IsNullOrWhiteSpace(state.CaptureIdentity))
        {
            ArmSpecialSlot(state);
            return;
        }

        if (_armedSpecialSlot is not null && !ReferenceEquals(_armedSpecialSlot, state))
        {
            CancelArmedHidCapture();
        }

        if (_armedChordSlot is not null && !ReferenceEquals(_armedChordSlot, state))
        {
            CancelChordCapture();
        }

        if (state.Kind is InputNodeKind.Gamepad or InputNodeKind.Mouse)
        {
            ShowToast(
                state.Kind == InputNodeKind.Mouse
                    ? "请按实体鼠标中键、后退或前进；点击图形不会模拟输入"
                    : "请按实体手柄按键；点击图形不会模拟输入",
                InfoBarSeverity.Informational);
            return;
        }

        SelectNode(state, updateCaptureStatus: true);
        _ = PulseNodeAsync(state);
    }

    private void InputNode_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Button { Tag: InputNodeState state })
        {
            SelectNode(state, updateCaptureStatus: false);
        }
    }

    private InputNodeState[] WorkspaceNodes() => _nodeVisuals.Values
        .Select(visual => visual.State)
        .ToArray();

    private bool IsAnyDrawerOpen =>
        MappingDrawer.Visibility == Visibility.Visible ||
        ProfileSettingsDrawer.Visibility == Visibility.Visible;

    private async Task LoadWorkspaceConfigurationAsync()
    {
        try
        {
            var loaded = await _configurationStore.LoadAsync(_lifetimeCancellation.Token);
            if (_isClosing)
            {
                return;
            }

            _configuration = loaded;
            _lastDurableConfiguration = loaded;
            ApplyAppearanceTheme(loaded.AppearanceThemeId);
            _workspaceRestore = WorkspaceConfigurationAdapter.Restore(loaded, WorkspaceNodes());
            _activeProfileName = _workspaceRestore.ActiveProfileName;
            _configurationWriteBlocked = false;
            ProfileNameText.Text = _activeProfileName;
            _updatingMasterToggle = true;
            MasterToggle.IsOn = loaded.IsMappingEnabled;
            _updatingMasterToggle = false;
            RefreshEffectiveRuntimeConfiguration(force: true);
            UpdateMasterStatus();
            if (_workspacePage is WorkspacePage.Capture or WorkspacePage.Mapping)
            {
                SetMode(_mappingMode);
            }
            RenderAllNodes();
            UpdateMappingCount();
            LastOperationText.Text = "已恢复本地配置";
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            // Normal shutdown while the small local file is being read.
        }
        catch (Exception exception)
        {
            // Never let an invalid/unreadable file be replaced by the in-memory default.
            _configurationWriteBlocked = true;
            ReportError($"配置加载失败，原文件保持不变：{exception.Message}");
        }
        finally
        {
            if (!_isClosing)
            {
                _suspendWorkspaceCapture = false;
            }
        }
    }

    private async Task<ConfigurationSaveResult> PersistWorkspaceConfigurationAsync(
        Guid? activeProfileIdOverride = null)
    {
        var requestedRevision = _configurationSaveRevision.BeginRequest();
        if (_configurationLoadTask is { IsCompleted: false })
        {
            await _configurationLoadTask;
        }

        var requestedMappingEnabled = _configuration.IsMappingEnabled;
        var runtimeWasEnabled = _runtimeMappingsEnabled;
        if (_configurationWriteBlocked)
        {
            if (!_configurationSaveRevision.IsLatest(requestedRevision))
            {
                return new(ConfigurationSaveOutcome.Superseded, requestedRevision);
            }

            ReconcileConfigurationAfterSaveFailure(requestedMappingEnabled, runtimeWasEnabled);
            ShowToast("配置文件加载失败，已阻止覆盖原文件", InfoBarSeverity.Error);
            return new(ConfigurationSaveOutcome.Failed, requestedRevision);
        }

        var gateEntered = false;
        KeyPilotConfiguration? candidate = null;
        try
        {
            if (_workspaceRestore is null)
            {
                throw new InvalidOperationException("配置工作区尚未初始化。");
            }

            // Bind this request to the exact UI/configuration state that initiated it. A queued,
            // older completion must never capture or overwrite a later toggle or mapping edit.
            candidate = WorkspaceConfigurationAdapter.Capture(
                _configuration,
                _workspaceRestore,
                WorkspaceNodes());
            if (activeProfileIdOverride is Guid requestedActiveProfileId)
            {
                if (!candidate.Profiles.Any(profile => profile.Id == requestedActiveProfileId))
                {
                    throw new InvalidOperationException("The requested active profile no longer exists.");
                }

                candidate = candidate with { ActiveProfileId = requestedActiveProfileId };
            }
            requestedMappingEnabled = candidate.IsMappingEnabled;

            await _configurationSaveGate.WaitAsync(_lifetimeCancellation.Token);
            gateEntered = true;
            if (!_configurationSaveRevision.IsLatest(requestedRevision))
            {
                return new(ConfigurationSaveOutcome.Superseded, requestedRevision);
            }

            await _configurationStore.SaveAsync(candidate, _lifetimeCancellation.Token);
            // Every completed write is durable even when a newer UI request superseded it.
            // Remember it so a later failed request can reconcile to what is actually on disk.
            _lastDurableConfiguration = candidate;
            if (!_configurationSaveRevision.IsLatest(requestedRevision))
            {
                // A newer UI mutation is already queued. Never let this older completion replace
                // its in-memory desired state; the newer request will commit the final snapshot.
                return new(ConfigurationSaveOutcome.Superseded, requestedRevision);
            }
            _configuration = candidate;
            RefreshEffectiveRuntimeConfiguration(force: true);
            return new(ConfigurationSaveOutcome.Succeeded, requestedRevision);
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            return new(ConfigurationSaveOutcome.Failed, requestedRevision);
        }
        catch (Exception exception)
        {
            if (!_configurationSaveRevision.IsLatest(requestedRevision))
            {
                return new(ConfigurationSaveOutcome.Superseded, requestedRevision);
            }

            ReconcileConfigurationAfterSaveFailure(requestedMappingEnabled, runtimeWasEnabled);
            ReportError(
                $"配置保存失败，原文件保持不变：{exception.Message}",
                "Configuration",
                exception);
            return new(ConfigurationSaveOutcome.Failed, requestedRevision);
        }
        finally
        {
            if (gateEntered)
            {
                _configurationSaveGate.Release();
            }
        }
    }

    private bool IsCurrentSaveResult(ConfigurationSaveResult result) =>
        result.Outcome != ConfigurationSaveOutcome.Superseded &&
        _configurationSaveRevision.IsLatest(result.Revision);

    private void ReconcileConfigurationAfterSaveFailure(
        bool requestedMappingEnabled,
        bool runtimeWasEnabled)
    {
        // With no proven durable snapshot, remain Off. An explicit Off request always wins even
        // when the disk still contains an older enabled snapshot.
        var fallback = _lastDurableConfiguration ??
            (_configuration with { IsMappingEnabled = false });
        // A failed On request may not inherit an older durable On bit when this session was
        // already Off. Preserve only the runtime state that existed before this exact request.
        fallback = fallback with
        {
            IsMappingEnabled = requestedMappingEnabled && runtimeWasEnabled
        };

        _configuration = fallback;
        ApplyAppearanceTheme(fallback.AppearanceThemeId);
        if (_workspaceRestore is not null)
        {
            // Roll the whole mutable workspace back, not only the caller's local field. This
            // prevents an older superseded edit from surviving a later failed save and being
            // resurrected by the next successful capture.
            _workspaceRestore = WorkspaceConfigurationAdapter.Restore(fallback, WorkspaceNodes());
            _activeProfileName = _workspaceRestore.ActiveProfileName;
            ProfileNameText.Text = _activeProfileName;
            FooterProfileNameText.Text = _activeProfileName;
            RenderAllNodes();
            UpdateMappingCount();
            if (_selectedNode is not null)
            {
                SelectNode(_selectedNode, updateCaptureStatus: false);
            }
        }
        RefreshEffectiveRuntimeConfiguration(force: true);
        _updatingMasterToggle = true;
        MasterToggle.IsOn = fallback.IsMappingEnabled;
        _updatingMasterToggle = false;
        UpdateMasterStatus();
    }

    private void ApplyRuntimeConfiguration(
        KeyPilotConfiguration configuration,
        bool cancelPendingActions = false)
    {
        var runtimeRevision = Interlocked.Increment(ref _runtimeRevision);
        _runtimeConfiguration = configuration;
        _mappingExecution.TryApplyConfiguration(
            configuration,
            cancelPendingActions,
            runtimeRevision);
        _driverSuppression.ApplyConfiguration(configuration, runtimeRevision);
        ApplyCompatibilityConfiguration(configuration);
        _runtimeMappingsEnabled = configuration.IsMappingEnabled;
        var activeProfile = configuration.ActiveProfileId is Guid activeProfileId
            ? configuration.Profiles.FirstOrDefault(profile => profile.Id == activeProfileId)
            : null;
        activeProfile ??= configuration.Profiles.FirstOrDefault(profile => profile.IsEnabled)
            ?? configuration.Profiles.FirstOrDefault();
        _xInputMouseMotion.ApplySettings(
            activeProfile?.StickMouse ?? new StickMouseSettings(),
            configuration.IsMappingEnabled && activeProfile is { IsEnabled: true },
            runtimeRevision);
        UpdateGamepadSubtitle();
    }

    private void RefreshEffectiveRuntimeConfiguration(
        bool force,
        bool executionContextChanged = false,
        MappingApplicationContext? applicationContext = null)
    {
        var processName = applicationContext?.ForegroundProcessName ?? _currentForegroundProcessName;
        if (IsKeyPilotProcess(processName))
        {
            processName = null;
        }

        var effectiveProfileId = ApplicationProfileResolver.ResolveProfileId(
            _configuration,
            processName);
        var effective = _configuration with { ActiveProfileId = effectiveProfileId };
        var context = applicationContext ?? new MappingApplicationContext
        {
            ForegroundProcessName = processName,
            ForegroundPackageFamilyName = processName is null ? null : _currentForegroundPackageFamily,
            RunningProcessNames = _runningProcessNames,
            RunningPackageFamilyNames = _runningPackageFamilyNames
        };
        if (processName is null)
        {
            context = context with
            {
                ForegroundProcessName = null,
                ForegroundPackageFamilyName = null
            };
        }

        effective = MappingConditionMatcher.Arm(effective, context);
        if (_armedChordSlot is not null ||
            _shortcutCaptureSession.State == ShortcutCaptureState.Armed)
        {
            // Recording temporarily owns physical input. A foreground-process transition may
            // change the selected profile, but it must never re-enable actions or suppression.
            effective = effective with { IsMappingEnabled = false };
        }

        var profileChanged = effective.ActiveProfileId != _runtimeConfiguration.ActiveProfileId;
        var enabledStateChanged =
            effective.IsMappingEnabled != _runtimeConfiguration.IsMappingEnabled;
        if (force || profileChanged || enabledStateChanged || executionContextChanged)
        {
            ApplyRuntimeConfiguration(
                effective,
                cancelPendingActions: profileChanged || executionContextChanged);
        }

        var profile = effective.ActiveProfileId is Guid activeId
            ? effective.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= effective.Profiles.FirstOrDefault(candidate => candidate.IsEnabled)
            ?? effective.Profiles.FirstOrDefault();
        FooterProfileNameText.Text = profile?.Name ?? "无可用方案";
        ProfileSourceText.Text = FindMatchingApplicationBinding(processName) is not null
                ? $"{DisplayProcessName(processName!)} · 自动生效"
                : _configuration.IsAutomaticProfileSwitchingEnabled
                    ? "未匹配时使用默认方案"
                    : "未启用应用自动切换";
    }

    private ApplicationProfileBinding? FindMatchingApplicationBinding(string? processName)
    {
        if (!_configuration.IsAutomaticProfileSwitchingEnabled)
        {
            return null;
        }

        var normalized = ApplicationProfileResolver.NormalizeProcessName(processName);
        if (normalized.Length == 0)
        {
            return null;
        }

        return _configuration.ApplicationProfileBindings.FirstOrDefault(binding =>
            binding is not null &&
            _configuration.Profiles.Any(profile => profile.Id == binding.ProfileId) &&
            string.Equals(
                ApplicationProfileResolver.NormalizeProcessName(binding.ProcessName),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistWorkspaceChangeAsync(string successMessage)
    {
        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (IsCurrentSaveResult(saveResult) &&
            saveResult.Outcome == ConfigurationSaveOutcome.Succeeded)
        {
            ShowToast(successMessage);
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        _configurationLoadTask ??= LoadWorkspaceConfigurationAsync();
        _compatibilitySuppression.Start();
        _driverSuppression.Start();
        _foregroundWindowWatcher.Start();
        // Activation is a safety boundary: returning to KeyPilot must invalidate an external
        // application's profile immediately instead of waiting for the polling fallback.
        UpdateForegroundProcess();
        if (!_foregroundProcessTimer.IsEnabled)
        {
            _foregroundProcessTimer.Start();
        }

        if (_rawKeyboard is null)
        {
            try
            {
                var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _rawKeyboard = new RawInputKeyboardSource(windowHandle);
                _rawKeyboard.InputReceived += RawKeyboard_InputReceived;
                _rawKeyboard.MouseButtonReceived += RawMouse_ButtonReceived;
                _rawKeyboard.HidReportsReceived += RawInput_HidReportsReceived;
                _rawKeyboard.DevicesChanged += RawInput_DevicesChanged;
                _rawKeyboard.CaptureFaulted += RawInput_CaptureFaulted;
                RefreshHidDeviceStatus(showToast: false);
                _rawInputHealth = InputBackendHealth.Healthy;
                _rawInputStatus = "Raw Input 键盘 / 鼠标 / HID 已连接";
                UpdateInputServiceStatus();
            }
            catch (Exception exception)
            {
                if (_rawKeyboard is not null)
                {
                    _rawKeyboard.InputReceived -= RawKeyboard_InputReceived;
                    _rawKeyboard.MouseButtonReceived -= RawMouse_ButtonReceived;
                    _rawKeyboard.HidReportsReceived -= RawInput_HidReportsReceived;
                    _rawKeyboard.DevicesChanged -= RawInput_DevicesChanged;
                    _rawKeyboard.CaptureFaulted -= RawInput_CaptureFaulted;
                    _rawKeyboard.Dispose();
                    _rawKeyboard = null;
                }

                _rawInputHealth = InputBackendHealth.Failed;
                _rawInputStatus = $"Raw Input 异常：{exception.Message}";
                UpdateInputServiceStatus();
                ReportError($"Raw Input 初始化失败：{exception.Message}", "RawInput", exception);
            }
        }

        if (_mouseButtons is null)
        {
            try
            {
                _mouseButtons = new LowLevelMouseButtonSource();
                _mouseButtons.ButtonReceived += RawMouse_ButtonReceived;
                _mouseButtons.CaptureFaulted += MouseButtons_CaptureFaulted;
                _mouseButtons.Start();
            }
            catch (Exception exception)
            {
                _mouseButtons?.Dispose();
                _mouseButtons = null;
                ReportError($"鼠标按键采集启动失败：{exception.Message}", "Mouse", exception);
            }
        }

        if (_xInputGamepad is null)
        {
            try
            {
                _xInputGamepad = new XInputGamepadSource();
                _xInputGamepad.ButtonChanged += XInputGamepad_ButtonChanged;
                _xInputGamepad.VirtualControlChanged += XInputGamepad_VirtualControlChanged;
                _xInputGamepad.StateSampled += XInputGamepad_StateSampled;
                _xInputGamepad.RawStateChanged += XInputGamepad_RawStateChanged;
                _xInputGamepad.ConnectionChanged += XInputGamepad_ConnectionChanged;
                _xInputGamepad.CaptureFaulted += XInputGamepad_CaptureFaulted;
                _xInputGamepad.Start();
                _xInputHealth = InputBackendHealth.Healthy;
                _xInputStatus = "XInput 手柄已连接";
                UpdateInputServiceStatus();
            }
            catch (Exception exception)
            {
                if (_xInputGamepad is not null)
                {
                    _xInputGamepad.ButtonChanged -= XInputGamepad_ButtonChanged;
                    _xInputGamepad.VirtualControlChanged -= XInputGamepad_VirtualControlChanged;
                    _xInputGamepad.StateSampled -= XInputGamepad_StateSampled;
                    _xInputGamepad.RawStateChanged -= XInputGamepad_RawStateChanged;
                    _xInputGamepad.ConnectionChanged -= XInputGamepad_ConnectionChanged;
                    _xInputGamepad.CaptureFaulted -= XInputGamepad_CaptureFaulted;
                    _xInputGamepad.Dispose();
                }

                _xInputGamepad = null;
                _xInputHealth = InputBackendHealth.Failed;
                _xInputStatus = $"XInput 异常：{exception.Message}";
                UpdateInputServiceStatus();
                ReportError($"XInput 初始化失败：{exception.Message}", "XInput", exception);
            }
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
        try
        {
            _rc003Battery.BatteryChanged -= Rc003Battery_BatteryChanged;
            _rc003Battery.Dispose();
            _rc003Voice.Dispose();
            _xInputMouseMotion.Faulted -= XInputMouseMotion_Faulted;
            _xInputMouseMotion.Stop();
            _compatibilitySuppression.InputReceived -= CompatibilitySuppression_InputReceived;
            _compatibilitySuppression.StatusChanged -= CompatibilitySuppression_StatusChanged;
            _compatibilitySuppression.Dispose();
            _driverSuppression.InputReceived -= DriverSuppression_InputReceived;
            _driverSuppression.StatusChanged -= DriverSuppression_StatusChanged;
            _driverSuppression.Stop();
            _driverSuppression.Dispose();
            _mappingExecution.Faulted -= MappingExecution_Faulted;
            _mappingExecution.Stop();
            _lifetimeCancellation.Cancel();
            _toastTimer.Stop();
            _hidCaptureTimer.Stop();
            _inputDiagnosticTimer.Stop();
            _actionCaptureTimer.Stop();
            _foregroundProcessTimer.Stop();
            _historySaveTimer.Stop();
            _conditionCaptureTimer.Stop();
            _foregroundWindowWatcher.ForegroundChanged -= ForegroundWindowWatcher_ForegroundChanged;
            _foregroundWindowWatcher.Dispose();
            if (_inputDiagnosticSession.IsActive)
            {
                var diagnosticResult = _inputDiagnosticSession.StopAndSave(
                    "application-closed",
                    DateTimeOffset.UtcNow);
                if (!diagnosticResult.Saved && !string.IsNullOrWhiteSpace(diagnosticResult.Error))
                {
                    RuntimeDiagnostics.Write(
                        "InputDiagnostic",
                        $"输入诊断保存失败：{diagnosticResult.Error}");
                }
            }

            _hidCaptureSession.Cancel();
            _armedSpecialSlot = null;
            _chordCaptureSession.Cancel();
            _armedChordSlot = null;
            _shortcutCaptureSession.Cancel();
            if (_rawKeyboard is not null)
            {
                _rawKeyboard.InputReceived -= RawKeyboard_InputReceived;
                _rawKeyboard.MouseButtonReceived -= RawMouse_ButtonReceived;
                _rawKeyboard.HidReportsReceived -= RawInput_HidReportsReceived;
                _rawKeyboard.DevicesChanged -= RawInput_DevicesChanged;
                _rawKeyboard.CaptureFaulted -= RawInput_CaptureFaulted;
                _rawKeyboard.Dispose();
            }

            _rawKeyboard = null;
            if (_mouseButtons is not null)
            {
                _mouseButtons.ButtonReceived -= RawMouse_ButtonReceived;
                _mouseButtons.CaptureFaulted -= MouseButtons_CaptureFaulted;
                _mouseButtons.Dispose();
                _mouseButtons = null;
            }
            _hidReportDiffer.Clear();
            _knownHidDevicePaths.Clear();
            if (_xInputGamepad is not null)
            {
                _xInputGamepad.ButtonChanged -= XInputGamepad_ButtonChanged;
                _xInputGamepad.VirtualControlChanged -= XInputGamepad_VirtualControlChanged;
                _xInputGamepad.StateSampled -= XInputGamepad_StateSampled;
                _xInputGamepad.RawStateChanged -= XInputGamepad_RawStateChanged;
                _xInputGamepad.ConnectionChanged -= XInputGamepad_ConnectionChanged;
                _xInputGamepad.CaptureFaulted -= XInputGamepad_CaptureFaulted;
                _xInputGamepad.Dispose();
            }

            _xInputGamepad = null;
            await _mappingExecution.DisposeAsync();
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write("Shutdown", "关闭资源时发生异常。", exception);
        }
        finally
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _xInputMouseMotion.Dispose();
            // WinUI 3 does not guarantee that closing an unpackaged window ends the application
            // lifetime. Exit only after owned workers and native hooks have been released.
            if (Application.Current is App app)
            {
                app.ReleaseSingleInstance();
            }
            Application.Current.Exit();
        }
    }

    private void RawInput_CaptureFaulted(object? sender, Exception exception)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    _rawInputHealth = InputBackendHealth.Failed;
                    _rawInputStatus = $"Raw Input 异常：{exception.Message}";
                    UpdateInputServiceStatus();
                    ReportError(exception.Message, "RawInput", exception);
                }
            });
        }
    }

    private void MouseButtons_CaptureFaulted(object? sender, Exception exception)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    ReportError($"鼠标按键采集异常：{exception.Message}", "Mouse", exception);
                }
            });
        }
    }

    private void RawKeyboard_InputReceived(object? sender, RawKeyboardEvent input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleRawKeyboard(input);
                }
            });
        }
    }

    private void RawMouse_ButtonReceived(object? sender, RawMouseEvent input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleRawMouse(input);
                }
            });
        }
    }

    private void RawInput_HidReportsReceived(object? sender, RawHidReportBatch input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleRawHidReports(input);
                }
            });
        }
    }

    private void RawInput_DevicesChanged(object? sender, EventArgs args)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                _hidReportDiffer.Clear();
                _inputPhaseTracker.Reset();
                _mappingExecution.TryResetInputState();
                CancelArmedHidCapture();
                CancelChordCapture();
                RefreshHidDeviceStatus(showToast: true);
            });
        }
    }

    private void XInputGamepad_ButtonChanged(object? sender, XInputButtonChangedEventArgs input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleXInputButton(input);
                }
            });
        }
    }

    private void XInputGamepad_VirtualControlChanged(
        object? sender,
        XInputVirtualControlChangedEventArgs input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleXInputVirtualControl(input);
                }
            });
        }
    }

    private void XInputGamepad_RawStateChanged(object? sender, XInputRawStateChangedEventArgs input)
    {
        if (_isClosing || !_inputDiagnosticSession.IsActive)
        {
            return;
        }

        _inputDiagnosticSession.ObserveXInputState(
            input.UserIndex,
            input.IsConnected,
            input.PacketNumber,
            input.Buttons,
            input.LeftTrigger,
            input.RightTrigger,
            input.ThumbLX,
            input.ThumbLY,
            input.ThumbRX,
            input.ThumbRY,
            input.TimestampUtc);
    }

    private void XInputGamepad_StateSampled(object? sender, XInputRawStateChangedEventArgs input)
    {
        if (_isClosing)
        {
            return;
        }

        var analysis = _stickAnalyzer.Process(input);
        _xInputMouseMotion.Observe(analysis, Volatile.Read(ref _runtimeRevision));

        var sampleTicks = input.TimestampUtc.UtcDateTime.Ticks;
        var priorTicks = Interlocked.Read(ref _lastStickUiEnqueueTicks);
        var hasRotationEdge = analysis.Left.RotationEdge.HasValue || analysis.Right.RotationEdge.HasValue;
        if (!hasRotationEdge && sampleTicks - priorTicks < TimeSpan.FromMilliseconds(33).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastStickUiEnqueueTicks, sampleTicks);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing)
            {
                HandleXInputStickAnalysis(analysis);
            }
        });
    }

    private void HandleXInputStickAnalysis(XInputStickAnalysisFrame frame)
    {
        if (!frame.IsConnected)
        {
            _stickAnalyses.Remove((frame.UserIndex, XInputStick.Left));
            _stickAnalyses.Remove((frame.UserIndex, XInputStick.Right));
            UpdateStickAnalysisText();
            UpdateGamepadSubtitle();
            return;
        }

        _stickAnalyses[(frame.UserIndex, XInputStick.Left)] = frame.Left;
        _stickAnalyses[(frame.UserIndex, XInputStick.Right)] = frame.Right;
        UpdateStickAnalysisText();
        UpdateGamepadSubtitle(frame);

        if (frame.Left.RotationEdge is { } leftEdge)
        {
            HandleXInputRotation(leftEdge, frame.Left);
        }

        if (frame.Right.RotationEdge is { } rightEdge)
        {
            HandleXInputRotation(rightEdge, frame.Right);
        }
    }

    private void UpdateGamepadSubtitle(XInputStickAnalysisFrame? frame = null)
    {
        var profile = _runtimeConfiguration.ActiveProfileId is Guid activeId
            ? _runtimeConfiguration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= _runtimeConfiguration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled)
            ?? _runtimeConfiguration.Profiles.FirstOrDefault();
        var settings = profile?.StickMouse;
        if (!_runtimeConfiguration.IsMappingEnabled ||
            profile is null ||
            !profile.IsEnabled ||
            settings is null ||
            !settings.IsEnabled)
        {
            GamepadSubtitleText.Text = "XInput 1.4 · 只读采集";
            return;
        }

        var sourceLabel = settings.Source == StickMouseSource.Right ? "右摇杆" : "左摇杆";
        if (frame is null || !frame.IsConnected)
        {
            GamepadSubtitleText.Text = $"XInput 1.4 · {sourceLabel}模拟鼠标 · 等待输入";
            return;
        }

        var stick = settings.Source == StickMouseSource.Right ? frame.Right : frame.Left;
        var normalizedMagnitude = Math.Clamp(
            (stick.Radius - settings.Deadzone) / (1d - settings.Deadzone),
            0d,
            1d);
        GamepadSubtitleText.Text = normalizedMagnitude <= 0
            ? $"XInput 1.4 · {sourceLabel}模拟鼠标 · 中心停止"
            : $"XInput 1.4 · {sourceLabel}模拟鼠标 · {normalizedMagnitude:P0}";
    }

    private void UpdateStickAnalysisText()
    {
        if (_stickAnalyses.Count == 0)
        {
            StickAnalysisText.Text = "已检测 0 个摇杆 · 转动摇杆后自动估算角度与精度";
            ToolTipService.SetToolTip(StickAnalysisText, null);
            return;
        }

        var userIndex = _stickAnalyses.Keys.Min(key => key.UserIndex);
        if (!_stickAnalyses.TryGetValue((userIndex, XInputStick.Left), out var left) ||
            !_stickAnalyses.TryGetValue((userIndex, XInputStick.Right), out var right))
        {
            return;
        }

        var detected = _stickAnalyses.Values.Count(value => value.IsConnected);
        StickAnalysisText.Text =
            $"已检测 {detected} 个摇杆 · L {FormatStickAngle(left)} / {FormatStickResolution(left)} · " +
            $"R {FormatStickAngle(right)} / {FormatStickResolution(right)}";
        ToolTipService.SetToolTip(
            StickAnalysisText,
            $"XInput User {userIndex + 1}\n" +
            $"左摇杆：角度 {FormatStickAngle(left)}，有效精度 {FormatStickResolution(left)}，" +
            $"死区 {left.EffectiveDeadzoneRadius:P0}，中心漂移 {left.CenterDriftRadius:P1}\n" +
            $"右摇杆：角度 {FormatStickAngle(right)}，有效精度 {FormatStickResolution(right)}，" +
            $"死区 {right.EffectiveDeadzoneRadius:P0}，中心漂移 {right.CenterDriftRadius:P1}\n" +
            "精度是运行时观察到的有效角分辨率估算，不代表厂商标称 ADC 位数。");
    }

    private static string FormatStickAngle(XInputStickAnalysis analysis) =>
        analysis.AngleDegrees is { } angle ? $"{angle:0}°" : "中心";

    private static string FormatStickResolution(XInputStickAnalysis analysis) =>
        analysis.EffectiveAngularResolutionDegrees is { } resolution
            ? $"精度≈{resolution:0.0}°"
            : "精度校准中";

    private void HandleXInputRotation(
        XInputStickRotationEdge edge,
        XInputStickAnalysis analysis)
    {
        var control = (edge.Stick, edge.Direction) switch
        {
            (XInputStick.Left, XInputStickRotationDirection.Clockwise) =>
                GamepadRotationControl.LeftClockwise,
            (XInputStick.Left, XInputStickRotationDirection.CounterClockwise) =>
                GamepadRotationControl.LeftCounterClockwise,
            (XInputStick.Right, XInputStickRotationDirection.Clockwise) =>
                GamepadRotationControl.RightClockwise,
            _ => GamepadRotationControl.RightCounterClockwise
        };
        var source = BuildXInputRotationSource(edge.UserIndex, control);
        var pressed = new InputEvent
        {
            Source = source,
            Phase = InputEventPhase.Pressed,
            TimestampUtc = edge.TimestampUtc,
            SequenceNumber = Interlocked.Increment(ref _inputSequence)
        };
        var released = new InputEvent
        {
            Source = source,
            Phase = InputEventPhase.Released,
            TimestampUtc = edge.TimestampUtc,
            SequenceNumber = Interlocked.Increment(ref _inputSequence)
        };
        if (!ObserveChordCapture(pressed))
        {
            _mappingExecution.TrySubmitPassThrough(pressed);
            _mappingExecution.TrySubmitPassThrough(released);
        }
        else
        {
            ObserveChordCapture(released);
        }

        RecordInputProcess(pressed, null, originalWasSuppressed: false);
        RecordInputProcess(released, null, originalWasSuppressed: false);

        if (!_nodeVisuals.TryGetValue($"gamepad:{control}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = $"XInput User {edge.UserIndex + 1}";
        visual.State.CapturedSource = source;
        visual.State.CaptureIdentity = source.CanonicalKey;
        visual.State.RawCode =
            $"XInput User {edge.UserIndex + 1} · 360° {edge.Direction} · " +
            $"有效精度 {FormatStickResolution(analysis)}";
        visual.State.Location = $"XInput User {edge.UserIndex + 1}";

        if (_captureEnabled && !_suspendWorkspaceCapture && !IsAnyDrawerOpen)
        {
            SelectNode(visual.State, updateCaptureStatus: true);
            RecentSourceText.Text = "摇杆整圈手势 · XINPUT 1.4";
            RecentVirtualKeyText.Text = control.ToString();
            RecentCodeText.Text = "360°";
            RecentLocationText.Text = visual.State.Location;
            RecentStateText.Text = "ROTATION";
            LastOperationText.Text = $"检测：{visual.State.Label}";
            _ = PulseNodeAsync(visual.State);
        }
    }

    private void XInputGamepad_ConnectionChanged(object? sender, XInputConnectionChangedEventArgs input)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    HandleXInputConnection(input);
                }
            });
        }
    }

    private void XInputGamepad_CaptureFaulted(object? sender, Exception exception)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    _xInputHealth = InputBackendHealth.Failed;
                    _xInputStatus = $"XInput 异常：{exception.Message}";
                    UpdateInputServiceStatus();
                    ReportError($"XInput：{exception.Message}", "XInput", exception);
                }
            });
        }
    }

    private void XInputMouseMotion_Faulted(object? sender, Exception exception)
    {
        if (!_isClosing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing)
                {
                    ReportError($"摇杆模拟鼠标：{exception.Message}", "StickMouse", exception);
                }
            });
        }
    }

    private void HandleXInputConnection(XInputConnectionChangedEventArgs input)
    {
        _virtualPadFilter.ObservePhysicalConnection(input.UserIndex, input.IsConnected);
        if (input.IsConnected)
        {
            _connectedGamepadSlots.Add(input.UserIndex);
        }
        else
        {
            _xInputMouseMotion.ResetUser(input.UserIndex);
            _connectedGamepadSlots.Remove(input.UserIndex);
            _inputPhaseTracker.Reset();
            _mappingExecution.TryResetInputState();
            _stickAnalyzer.Reset(input.UserIndex);
            _stickAnalyses.Remove((input.UserIndex, XInputStick.Left));
            _stickAnalyses.Remove((input.UserIndex, XInputStick.Right));
            UpdateStickAnalysisText();
        }

        UpdateGamepadBackendStatus();
    }

    private void HandleXInputButton(XInputButtonChangedEventArgs input)
    {
        if (_virtualPadFilter.ShouldIgnore(input.UserIndex))
        {
            return;
        }

        var source = BuildXInputSource(input);
        var observed = _ingest.Observe(source, input.IsPressed, input.TimestampUtc, "XInput 手柄");
        RecordInputProcess(observed, input.Button.ToString(), originalWasSuppressed: false);

        if (!_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
        {
            return;
        }

        if (!_nodeVisuals.TryGetValue($"gamepad:{input.Button}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = $"XInput User {input.UserIndex + 1}";
        visual.State.CapturedSource = BuildXInputSource(input);
        visual.State.CaptureIdentity = visual.State.CapturedSource.CanonicalKey;
        visual.State.RawCode = $"XInput User {input.UserIndex + 1} · Button 0x{(ushort)input.Button:X4} · Packet {input.PacketNumber}";
        visual.State.Location = $"XInput User {input.UserIndex + 1}";
        SelectNode(visual.State, updateCaptureStatus: true);
        RecentSourceText.Text = "手柄 · XINPUT 1.4";
        RecentVirtualKeyText.Text = input.Button.ToString();
        RecentCodeText.Text = $"0x{(ushort)input.Button:X4}";
        RecentLocationText.Text = visual.State.Location;
        RecentStateText.Text = input.IsPressed ? "BUTTON DOWN" : "BUTTON UP";
        LastOperationText.Text = $"检测：{visual.State.Label}";

        if (input.IsPressed)
        {
            _ = PulseNodeAsync(visual.State);
        }
    }

    private void HandleRawMouse(RawMouseEvent input)
    {
        if (input.IsInjectedBy(_inputInjectionMarker))
        {
            return;
        }

        if (!TryGetMouseButtonId(input.VirtualKey, out var buttonId))
        {
            return;
        }

        var source = BuildMouseSource(input);
        var observed = _ingest.Observe(source, input.IsPressed, input.Timestamp, "鼠标 Raw Input");
        var mouseLabel = _nodeVisuals.TryGetValue($"mouse:{buttonId}", out var mouseNode)
            ? mouseNode.State.Label
            : buttonId;
        RecordInputProcess(observed, mouseLabel, originalWasSuppressed: false);

        MouseBackendStatusText.Text = "已检测到鼠标按键";
        MouseBackendStatusText.Foreground = BrushResource("KpMintBrush");

        if (!_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
        {
            return;
        }

        if (!_nodeVisuals.TryGetValue($"mouse:{buttonId}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = input.DevicePath;
        visual.State.RawCode = $"VK 0x{input.VirtualKey:X2} · Flags 0x{input.ButtonFlags:X4}";
        visual.State.Location = input.DevicePath;
        SelectNode(visual.State, updateCaptureStatus: true);
        RecentSourceText.Text = "鼠标 · RAW INPUT";
        RecentVirtualKeyText.Text = $"0x{input.VirtualKey:X2}";
        RecentCodeText.Text = visual.State.RawCode;
        RecentLocationText.Text = visual.State.Location;
        RecentStateText.Text = input.IsPressed ? "BUTTON DOWN" : "BUTTON UP";
        LastOperationText.Text = $"检测：{visual.State.Label}";

        if (input.IsPressed)
        {
            _ = PulseNodeAsync(visual.State);
        }
    }

    private void HandleXInputVirtualControl(XInputVirtualControlChangedEventArgs input)
    {
        if (_virtualPadFilter.ShouldIgnore(input.UserIndex))
        {
            return;
        }

        var source = BuildXInputSource(input);
        var observed = _ingest.Observe(source, input.IsPressed, input.TimestampUtc, "XInput 模拟输入");
        RecordInputProcess(observed, input.Control.ToString(), originalWasSuppressed: false);

        if (!_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
        {
            return;
        }

        if (!_nodeVisuals.TryGetValue($"gamepad:{input.Control}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = $"XInput User {input.UserIndex + 1}";
        visual.State.CapturedSource = source;
        visual.State.CaptureIdentity = source.CanonicalKey;
        visual.State.RawCode =
            $"XInput User {input.UserIndex + 1} · Analog {(int)input.Control} · Packet {input.PacketNumber}";
        visual.State.Location = $"XInput User {input.UserIndex + 1}";
        SelectNode(visual.State, updateCaptureStatus: true);
        RecentSourceText.Text = "手柄模拟输入 · XINPUT 1.4";
        RecentVirtualKeyText.Text = input.Control.ToString();
        RecentCodeText.Text = $"ANALOG {(int)input.Control}";
        RecentLocationText.Text = visual.State.Location;
        RecentStateText.Text = input.IsPressed ? "THRESHOLD DOWN" : "THRESHOLD UP";
        LastOperationText.Text = $"检测：{visual.State.Label}";

        if (input.IsPressed)
        {
            _ = PulseNodeAsync(visual.State);
        }
    }

    private void HandleRawKeyboard(RawKeyboardEvent input)
    {
        if (_inputDiagnosticSession.IsActive)
        {
            _inputDiagnosticSession.ObserveKeyboard(input);
        }

        if (input.IsInjectedBy(_inputInjectionMarker))
        {
            return;
        }

        if (Rc003DeviceIdentity.TryMapKeyboardEvent(input, out var remoteButtonId))
        {
            HandleRc003Button(input, remoteButtonId);
            return;
        }

        if (_shortcutCaptureSession.State == ShortcutCaptureState.Armed)
        {
            _shortcutCaptureSession.Observe(input);
            ActionCaptureStatusText.Text = string.IsNullOrWhiteSpace(_shortcutCaptureSession.CurrentText)
                ? "正在录制；请按下要输出的快捷键"
                : $"已捕获：{_shortcutCaptureSession.CurrentText}";
            return;
        }

        var source = BuildKeyboardSource(input);
        var phase = _inputPhaseTracker.Observe(source, input.IsKeyDown);
        var normalizedInput = new InputEvent
        {
            Source = source,
            Phase = phase,
            TimestampUtc = input.Timestamp,
            SequenceNumber = Interlocked.Increment(ref _inputSequence)
        };
        var chordCaptureOwnsInput = ObserveChordCapture(normalizedInput);
        var compatibilitySuppressed = _compatibilitySuppression.TryConsumeSuppressed(
            source,
            input.IsKeyDown);
        var originalWasSuppressed = compatibilitySuppressed || _driverSuppression.IsSuppressing(source);
        if (!chordCaptureOwnsInput && !originalWasSuppressed)
        {
            if (_armedChordSlot is null && phase == InputEventPhase.Pressed &&
                MappingDispatchPolicy.HasEnabledSuppressedMapping(_runtimeConfiguration, source))
            {
                WarnSuppressionUnavailable(source, "键盘 Raw Input");
            }

            _mappingExecution.TrySubmitPassThrough(normalizedInput);
        }

        if (!originalWasSuppressed)
        {
            RecordInputProcess(
                normalizedInput,
                KeyboardKeyResolver.Resolve(input),
                originalWasSuppressed: false);
        }

        if (!_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
        {
            return;
        }

        var id = KeyboardKeyResolver.Resolve(input);
        if (id is null)
        {
            HandleUnknownKeyboardInput(input);
            return;
        }

        if (!_nodeVisuals.TryGetValue($"keyboard:{id}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = string.IsNullOrWhiteSpace(input.DevicePath) ? null : input.DevicePath;
        // Common keyboard tiles intentionally remain logical AnyOfKind controls. Exact-device
        // capture is reserved for OEM/special slots; the observed path remains available here for
        // diagnostics and protocol-v2 exact-device hashing when such a slot is used.
        visual.State.CapturedSource = BuildDefaultKeyboardSource(id);
        visual.State.CaptureIdentity = visual.State.CapturedSource.CanonicalKey;
        visual.State.RawCode = $"{input.RawCode} · {CompactDevicePath(input.DevicePath)}";
        visual.State.Location = KeyboardLocation(id);
        SelectNode(visual.State, updateCaptureStatus: true, input);
        KeyboardLastText.Text = $"最近：{visual.State.Label} · Scan 0x{input.MakeCode:X2}";
        if (input.IsKeyDown)
        {
            _ = PulseNodeAsync(visual.State);
        }
    }

    private void HandleUnknownKeyboardInput(RawKeyboardEvent input)
    {
        var source = BuildKeyboardSource(input);
        var identity = source.CanonicalKey;
        var existing = _nodeVisuals.Values.FirstOrDefault(visual =>
            visual.State.Kind == InputNodeKind.Special &&
            string.Equals(visual.State.CaptureIdentity, identity, StringComparison.OrdinalIgnoreCase));

        if (existing is null && input.IsKeyDown && _armedChordSlot is null)
        {
            existing = _armedSpecialSlot is not null && string.IsNullOrWhiteSpace(_armedSpecialSlot.CaptureIdentity)
                ? _nodeVisuals[_armedSpecialSlot.StableId]
                : _nodeVisuals.Values.FirstOrDefault(visual =>
                    visual.State.Kind == InputNodeKind.Special && string.IsNullOrWhiteSpace(visual.State.RawCode));
            if (existing is null)
            {
                ShowToast("10 个特殊键槽位已满");
                return;
            }

            existing.State.FriendlyName = input.VirtualKey == 0x85 && input.MakeCode == 0x6D
                ? "F22 / OEM 自定义键"
                : "未命名特殊键";
            existing.State.CaptureIdentity = identity;
            existing.State.DevicePath = string.IsNullOrWhiteSpace(input.DevicePath) ? null : input.DevicePath;
            existing.State.CapturedSource = source;
            existing.State.RawCode = $"{CompactDevicePath(input.DevicePath)} · Scan 0x{input.MakeCode:X2} · VK 0x{input.VirtualKey:X2} · Prefix 0x{input.Flags & 0x0006:X2}";
            existing.State.Location = "Keyboard / OEM";
            CancelArmedHidCapture();
            RenderNode(existing);
            ShowToast($"已捕获 {existing.State.Label}，可稍后右键命名");
            _ = PersistWorkspaceChangeAsync("特殊键槽位已写入本地配置");
        }

        if (existing is null)
        {
            return;
        }

        SelectNode(existing.State, updateCaptureStatus: true, input);
        if (input.IsKeyDown)
        {
            _ = PulseNodeAsync(existing.State);
        }
    }

    private void HandleRawHidReports(RawHidReportBatch batch)
    {
        var canPresent = _captureEnabled &&
            !_suspendWorkspaceCapture &&
            !IsAnyDrawerOpen;

        if (batch.ReportLength > UnknownHidReportDiffer.DefaultMaximumReportLength)
        {
            if (canPresent && _armedSpecialSlot is not null)
            {
                ShowToast($"该 HID 报告有 {batch.ReportLength} 字节，超出安全分析上限", InfoBarSeverity.Warning);
            }

            return;
        }

        if (_inputDiagnosticSession.IsActive)
        {
            _inputDiagnosticSession.ObserveHidDescriptor(
                batch.Device,
                batch.ReportLength,
                batch.Timestamp);
        }

        foreach (var report in batch.Reports)
        {
            try
            {
                if (HandleSonyHidReport(batch.Device, report, batch.Timestamp, canPresent))
                {
                    continue;
                }

                // Report ID is intentionally left unknown until descriptor parsing is available.
                var identity = new UnknownHidReportIdentity(
                    batch.Device.DevicePath,
                    batch.Device.UsagePage,
                    batch.Device.Usage,
                    reportId: 0,
                    batch.ReportLength);
                var change = _hidReportDiffer.Observe(identity, report, batch.Timestamp);
                if (change is null)
                {
                    continue;
                }

                if (_inputDiagnosticSession.IsActive)
                {
                    _inputDiagnosticSession.ObserveHidChange(batch.Device, change);
                }

                SubmitOpaqueHidMappings(batch.Device, change);
                if (!canPresent)
                {
                    continue;
                }

                if (change.ByteChanges.Count > MaximumOpaqueHidChangedBytes)
                {
                    if (_armedSpecialSlot is not null)
                    {
                        var rejected = _hidCaptureSession.Observe(change);
                        UpdateHidCaptureProgress(rejected);
                    }

                    continue;
                }

                var source = OpaqueHidInputSourceFactory.Create(batch.Device, change);
                var captureIdentity = source.CanonicalKey;
                var existing = _nodeVisuals.Values.FirstOrDefault(visual =>
                    visual.State.Kind == InputNodeKind.Special &&
                    string.Equals(visual.State.CaptureIdentity, captureIdentity, StringComparison.Ordinal));

                if (existing is not null)
                {
                    PresentOpaqueHidChange(existing.State, batch.Device, change);
                }

                if (_armedSpecialSlot is null)
                {
                    continue;
                }

                var progress = _hidCaptureSession.Observe(change);
                if (progress.Progress != OpaqueHidCaptureProgress.Confirmed || progress.ConfirmedChange is null)
                {
                    UpdateHidCaptureProgress(progress);
                    continue;
                }

                var confirmedSource = OpaqueHidInputSourceFactory.Create(batch.Device, progress.ConfirmedChange);
                var confirmedIdentity = confirmedSource.CanonicalKey;
                var duplicate = _nodeVisuals.Values.FirstOrDefault(visual =>
                    visual.State.Kind == InputNodeKind.Special &&
                    string.Equals(visual.State.CaptureIdentity, confirmedIdentity, StringComparison.Ordinal));
                if (duplicate is not null)
                {
                    CancelArmedHidCapture(
                        $"这个输入已经保存在 {duplicate.State.Label}",
                        InfoBarSeverity.Warning);
                    SelectNode(duplicate.State, updateCaptureStatus: false);
                    _ = PulseNodeAsync(duplicate.State);
                    continue;
                }

                var capturedSlot = _armedSpecialSlot;
                capturedSlot.FriendlyName = "未命名特殊键";
                capturedSlot.CaptureIdentity = confirmedIdentity;
                capturedSlot.DevicePath = batch.Device.DevicePath;
                capturedSlot.CapturedSource = confirmedSource;
                capturedSlot.RawCode = FormatOpaqueHidCode(batch.Device, progress.ConfirmedChange);
                capturedSlot.Location = $"HID {batch.Device.UsagePage:X4}/{batch.Device.Usage:X4}";
                CancelArmedHidCapture();
                RenderNode(_nodeVisuals[capturedSlot.StableId]);
                PresentOpaqueHidChange(capturedSlot, batch.Device, change);
                ShowToast($"已确认并捕获 {capturedSlot.Label}，可稍后右键命名");
                _ = PersistWorkspaceChangeAsync("特殊键槽位已写入本地配置");
            }
            catch (Exception exception)
            {
                ReportError($"HID 报告分析失败：{exception.Message}");
                return;
            }
        }
    }

    private void SubmitOpaqueHidMappings(
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change)
    {
        if (!_runtimeConfiguration.IsMappingEnabled && _armedChordSlot is null)
        {
            return;
        }

        var capturedSources = EnumerateConfiguredAtomicSources()
            .DistinctBy(source => source.CanonicalKey);
        foreach (var source in capturedSources)
        {
            if (!OpaqueHidInputMatcher.TryMatch(source, device, change, out var phase))
            {
                continue;
            }

            if (_armedChordSlot is null && phase == InputEventPhase.Pressed &&
                MappingDispatchPolicy.HasEnabledSuppressedMapping(_runtimeConfiguration, source))
            {
                WarnSuppressionUnavailable(source, "厂商 HID");
            }

            var normalizedInput = new InputEvent
            {
                Source = source,
                Phase = phase,
                TimestampUtc = change.TimestampUtc,
                SequenceNumber = Interlocked.Increment(ref _inputSequence)
            };
            if (!ObserveChordCapture(normalizedInput) && _runtimeConfiguration.IsMappingEnabled)
            {
                _mappingExecution.TrySubmitPassThrough(normalizedInput);
            }

            RecordInputProcess(normalizedInput, null, originalWasSuppressed: false);
        }
    }

    private IEnumerable<InputSource> EnumerateConfiguredAtomicSources()
    {
        var slotSources = _nodeVisuals.Values
            .Where(visual => visual.State.Kind == InputNodeKind.Special)
            .Select(visual => visual.State.CapturedSource)
            .Where(source => source is not null)
            .Cast<InputSource>();
        var profile = _runtimeConfiguration.ActiveProfileId is Guid activeId
            ? _runtimeConfiguration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= _runtimeConfiguration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled);
        var mappingSources = profile?.Mappings
            .Where(mapping => mapping is { IsEnabled: true })
            .Select(mapping => mapping.Source)
            ?? Enumerable.Empty<InputSource>();
        return slotSources.Concat(mappingSources).SelectMany(ExpandAtomicSource);
    }

    private static IEnumerable<InputSource> ExpandAtomicSource(InputSource source) =>
        source.Control.Kind == InputControlKind.InputChord && source.ChordMembers is { Count: > 0 }
            ? source.ChordMembers.SelectMany(ExpandAtomicSource)
            : source.Control.Kind == InputControlKind.InputSequence && source.PatternSteps is { Count: > 0 }
                ? source.PatternSteps.Select(step => step.Source).SelectMany(ExpandAtomicSource)
            : [source];

    private void ArmSpecialSlot(InputNodeState state)
    {
        CancelChordCapture();
        _armedSpecialSlot = state;
        _hidCaptureSession.Arm(DateTimeOffset.UtcNow);
        _hidCaptureTimer.Stop();
        _hidCaptureTimer.Start();
        SelectNode(state, updateCaptureStatus: true);
        RenderAllNodes();
        HidBackendStatusText.Text = $"{state.Label} · 等待第 1 次";
        HidBackendStatusText.Foreground = BrushResource("KpAmberBrush");
        ShowToast("松开其他控件，然后将实体特殊键完整按下并松开两次", InfoBarSeverity.Informational);
    }

    private void UpdateHidCaptureProgress(OpaqueHidCaptureResult result)
    {
        if (_armedSpecialSlot is null)
        {
            return;
        }

        switch (result.Progress)
        {
            case OpaqueHidCaptureProgress.CandidateDetected:
                HidBackendStatusText.Text = $"{_armedSpecialSlot.Label} · 请松开";
                break;
            case OpaqueHidCaptureProgress.BaselineRestored:
                HidBackendStatusText.Text = $"{_armedSpecialSlot.Label} · 再按一次";
                break;
            case OpaqueHidCaptureProgress.CandidateRejected:
                HidBackendStatusText.Text = $"{_armedSpecialSlot.Label} · 变化不稳定";
                ShowToast("变化不稳定或字节过多，请松开其他控件后重新按两次", InfoBarSeverity.Warning);
                break;
            case OpaqueHidCaptureProgress.TimedOut:
                CancelArmedHidCapture("HID 采集已超时，请重新选择槽位", InfoBarSeverity.Warning);
                break;
        }
    }

    private void CancelArmedHidCapture(
        string? message = null,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _armedSpecialSlot = null;
        _hidCaptureSession.Cancel();
        _hidCaptureTimer.Stop();
        if (!_isClosing)
        {
            RefreshHidDeviceStatus(showToast: false);
            RenderAllNodes();
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowToast(message, severity);
            }
        }
    }

    private void ArmChordCapture(InputNodeState state)
    {
        if (state.Kind != InputNodeKind.Special)
        {
            return;
        }

        CancelArmedHidCapture();
        _armedChordSlot = state;
        _chordCaptureSession.Arm(DateTimeOffset.UtcNow);
        // Recording owns physical edges. Temporarily release suppression and mapping execution so
        // testing a candidate chord cannot launch an existing action or strand a swallowed key.
        ApplyRuntimeConfiguration(_runtimeConfiguration with { IsMappingEnabled = false });
        _hidCaptureTimer.Stop();
        _hidCaptureTimer.Start();
        SelectNode(state, updateCaptureStatus: true);
        RenderAllNodes();
        HidBackendStatusText.Text = $"{state.Label} · 2 秒录制 0/32";
        HidBackendStatusText.Foreground = BrushResource("KpAmberBrush");
        ShowToast(
            "录制已开始：2 秒内完整按下并释放；同时按住会识别为组合，先后操作会识别为序列",
            InfoBarSeverity.Informational);
    }

    private void CancelChordCapture(
        string? message = null,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_armedChordSlot is null)
        {
            return;
        }

        _armedChordSlot = null;
        _chordCaptureSession.Cancel();
        _hidCaptureTimer.Stop();
        if (!_isClosing)
        {
            RefreshEffectiveRuntimeConfiguration(force: true);
            RefreshHidDeviceStatus(showToast: false);
            RenderAllNodes();
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowToast(message, severity);
            }
        }
    }

    /// <summary>Returns true while an armed combination recorder owns this atomic edge.</summary>
    private bool ObserveChordCapture(InputEvent inputEvent)
    {
        if (_armedChordSlot is null)
        {
            return false;
        }

        var state = _chordCaptureSession.Observe(inputEvent);
        HidBackendStatusText.Text =
            $"{_armedChordSlot.Label} · 2 秒录制 {_chordCaptureSession.RecordedEdgeCount}/32";
        if (state != InputPatternCaptureState.Completed ||
            _chordCaptureSession.CompletedSource is not { } completedSource)
        {
            return true;
        }

        CompleteChordCapture(completedSource);
        return true;
    }

    private void CompleteChordCapture(InputSource completedSource)
    {
        var slot = _armedChordSlot;
        if (slot is null)
        {
            return;
        }

        var isChord = completedSource.Control.Kind == InputControlKind.InputChord;
        var members = isChord
            ? completedSource.ChordMembers ?? []
            : completedSource.PatternSteps?
                .Select(step => step.Source)
                .DistinctBy(source => source.CanonicalKey)
                .ToList() ?? [];
        var memberNames = members.Select(DescribeChordMember).ToArray();
        var sequenceDescription = isChord
            ? string.Join(" + ", memberNames)
            : string.Join(
                " → ",
                completedSource.PatternSteps!.Select(step =>
                    $"{DescribeChordMember(step.Source)}{(step.Phase == InputEventPhase.Pressed ? "↓" : "↑")}"));
        slot.CapturedSource = completedSource;
        slot.CaptureIdentity = completedSource.CanonicalKey;
        slot.DevicePath = CommonExactDevicePath(members);
        slot.FriendlyName = isChord
            ? $"组合：{sequenceDescription}"
            : $"序列：{sequenceDescription}";
        slot.RawCode = isChord
            ? $"PATTERN CHORD · {members.Count} 个成员 · 2 秒窗口 · {sequenceDescription}"
            : $"PATTERN SEQUENCE · {completedSource.PatternSteps!.Count} 个边沿 · 2 秒窗口 · {sequenceDescription}";
        slot.Location = isChord ? "Input Chord" : "Input Sequence";
        if (slot.Mapping is { } mapping)
        {
            mapping.Source = completedSource;
            mapping.SourceCaptureIdentity = completedSource.CanonicalKey;
            mapping.SourceDevicePath = slot.DevicePath;
            mapping.BlockOriginal = false;
        }

        _armedChordSlot = null;
        _hidCaptureTimer.Stop();
        RefreshEffectiveRuntimeConfiguration(force: true);
        RenderAllNodes();
        SelectNode(slot, updateCaptureStatus: true);
        ShowToast(
            $"已录制 {slot.Label}：{sequenceDescription}；输入模式采用安全透传，不屏蔽原键",
            InfoBarSeverity.Success);
        _ = PersistWorkspaceChangeAsync("组合键槽位已写入本地配置");
    }

    private string DescribeChordMember(InputSource source)
    {
        if (source.Control.Kind == InputControlKind.KeyboardScanCode)
        {
            var prefix = source.Control.RawQualifier?.Contains(
                    "PREFIX=0004",
                    StringComparison.OrdinalIgnoreCase) == true
                ? (ushort)0x0004
                : source.Control.IsExtended
                    ? (ushort)0x0002
                    : (ushort)0;
            if (KeyboardKeyResolver.TryResolveSet1Code(
                    checked((ushort)source.Control.Code),
                    prefix,
                    out var stableId))
            {
                return stableId switch
                {
                    "ControlLeft" => "Ctrl",
                    "ControlRight" => "右 Ctrl",
                    "ShiftLeft" => "Shift",
                    "ShiftRight" => "右 Shift",
                    "AltLeft" => "Alt",
                    "AltRight" => "右 Alt",
                    _ when stableId.StartsWith("Digit", StringComparison.Ordinal) => stableId[5..],
                    _ when stableId.StartsWith("Key", StringComparison.Ordinal) => stableId[3..],
                    _ => stableId
                };
            }

            if (source.Control.Code == 0x6D)
            {
                return "F22";
            }

            return $"Scan 0x{source.Control.Code:X2}";
        }

        if (source.Device.Kind == InputDeviceKind.Gamepad)
        {
            return source.Control.Kind switch
            {
                InputControlKind.GamepadButton =>
                    Enum.IsDefined(typeof(XInputButton), (ushort)source.Control.Code)
                        ? ((XInputButton)(ushort)source.Control.Code).ToString()
                        : $"Pad 0x{source.Control.Code:X}",
                InputControlKind.GamepadAxisDirection =>
                    ((XInputVirtualControl)source.Control.Code).ToString(),
                InputControlKind.GamepadRotation =>
                    ((GamepadRotationControl)source.Control.Code).ToString(),
                _ => $"Pad 0x{source.Control.Code:X}"
            };
        }

        var special = _nodeVisuals.Values.FirstOrDefault(visual =>
            visual.State.Kind == InputNodeKind.Special &&
            visual.State.CapturedSource is not null &&
            string.Equals(
                visual.State.CapturedSource.CanonicalKey,
                source.CanonicalKey,
                StringComparison.Ordinal));
        return special?.State.FriendlyName ?? $"{source.Device.Kind} 0x{source.Control.Code:X}";
    }

    private static string? CommonExactDevicePath(IReadOnlyList<InputSource> members)
    {
        var paths = members
            .Where(member => member.Device.MatchMode == DeviceMatchMode.ExactDevice)
            .Select(member => member.Device.DeviceId)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length == 1 ? paths[0] : null;
    }

    private void PresentOpaqueHidChange(
        InputNodeState state,
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change)
    {
        SelectNode(state, updateCaptureStatus: true);
        RecentSourceText.Text = "特殊按键 · RAW HID（待校准）";
        RecentVirtualKeyText.Text = $"UP {device.UsagePage:X4}";
        RecentCodeText.Text = FormatHidDelta(change);
        RecentLocationText.Text = state.Location;
        RecentStateText.Text = "REPORT CHANGE";
        LastOperationText.Text = $"检测：{state.Label}";
        if (state.CapturedSource is { } hidSource)
        {
            RecordInputProcess(
                new InputEvent
                {
                    Source = hidSource,
                    Phase = InputEventPhase.Pressed,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    SequenceNumber = Interlocked.Increment(ref _inputSequence)
                },
                state.Label,
                originalWasSuppressed: false);
        }

        _ = PulseNodeAsync(state);
    }

    private void RefreshHidDeviceStatus(bool showToast)
    {
        var devices = _rawKeyboard?.HidDevices ?? Array.Empty<RawHidDeviceDescriptor>();
        var currentPaths = devices
            .Select(device => device.DevicePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removedPath in _knownHidDevicePaths.Where(path => !currentPaths.Contains(path)).ToArray())
        {
            _hidReportDiffer.RemoveDevice(removedPath);
            _sonyPadStates.Remove(removedPath);
        }

        _knownHidDevicePaths.Clear();
        _knownHidDevicePaths.UnionWith(currentPaths);
        if (_sonyPadStates.Count == 0)
        {
            _sonyStatusName = null;
        }

        UpdateGamepadBackendStatus();

        var rc003Present = devices.Any(device => Rc003DeviceIdentity.IsRc003(device.VendorId, device.ProductId))
            || currentPaths.Any(Rc003DeviceIdentity.IsRc003)
            || (_rawKeyboard?.HasDevicePath(Rc003DeviceIdentity.IsRc003) ?? false);
        if (rc003Present != _rc003HidConnected)
        {
            _rc003HidConnected = rc003Present;
            if (rc003Present)
            {
                UpdateRemoteBackendStatus();
                _ = EnsureRc003VoiceRunningAsync();
                _ = EnsureRc003BatteryRunningAsync();
            }
            else
            {
                _rc003Voice.Stop();
                _rc003Battery.Stop();
                _rc003BatteryPercent = null;
                UpdateRemoteBackendStatus();
            }

            RefreshConnectedMicrophones();
        }

        if (_armedSpecialSlot is null)
        {
            HidBackendStatusText.Text = devices.Count == 0
                ? "未发现 HID 集合"
                : $"{currentPaths.Count} 设备 · {devices.Count} 集合";
            HidBackendStatusText.Foreground = devices.Count == 0
                ? BrushResource("KpMutedBrush")
                : BrushResource("KpMintBrush");
        }

        if (showToast)
        {
            ShowToast("输入设备列表已更新", InfoBarSeverity.Informational);
        }
    }

    private void SelectNode(
        InputNodeState state,
        bool updateCaptureStatus,
        RawKeyboardEvent? input = null)
    {
        _selectedNode = state;
        CopyDetectedInputButton.IsEnabled = state.CapturedSource is not null;
        RenderAllNodes();

        SelectedSourceText.Text = state.FriendlyName;
        SelectedSourceCodeText.Text = string.IsNullOrWhiteSpace(state.RawCode) ? state.StableId : state.RawCode;
        if (state.Mapping is null)
        {
            SelectedTargetText.Text = "尚未设置映射";
            SelectedActionTypeText.Text = "在此按键上右键创建映射";
            SelectedTriggerText.Text = "—";
            SelectedBlockText.Text = "—";
        }
        else
        {
            SelectedTargetText.Text = DisplayMappingTarget(state.Mapping);
            SelectedActionTypeText.Text = state.Mapping.ActionType;
            SelectedTriggerText.Text = state.Mapping.Condition.IsRestricted
                ? $"{state.Mapping.Trigger} · {state.Mapping.Condition.Summary}"
                : state.Mapping.Trigger;
            SelectedBlockText.Text = state.Mapping.BlockOriginal ? "计划屏蔽" : "保留原输入";
        }

        if (!updateCaptureStatus)
        {
            return;
        }

        RecentSourceText.Text = state.Kind switch
        {
            InputNodeKind.Keyboard => "常规键盘 · RAW INPUT",
            InputNodeKind.Gamepad => "手柄 · 等待后端",
            InputNodeKind.Remote => "遥控器 · RC003",
            InputNodeKind.Mouse => "鼠标 · RAW INPUT",
            _ => "特殊按键 · 原始输入"
        };
        RecentKeyText.Text = state.Label;
        RecentNameText.Text = state.FriendlyName + (state.Mapping is null ? " · 未映射" : " · 已映射");
        RecentVirtualKeyText.Text = input is null ? state.Kind.ToString().ToUpperInvariant() : $"0x{input.VirtualKey:X2}";
        RecentCodeText.Text = input is null ? state.RawCode : $"0x{input.MakeCode:X2}";
        RecentLocationText.Text = state.Location;
        RecentStateText.Text = input is null ? "TEST" : input.IsKeyDown ? "KEY DOWN" : "KEY UP";
        LastOperationText.Text = $"检测：{state.Label}";
    }

    private async Task PulseNodeAsync(InputNodeState state)
    {
        if (_isClosing || !_nodeVisuals.TryGetValue(state.StableId, out var visual))
        {
            return;
        }

        var pulseVersion = _pulseVersions.GetValueOrDefault(state.StableId) + 1;
        _pulseVersions[state.StableId] = pulseVersion;
        _pressedNodeIds.Add(state.StableId);
        RenderNode(visual);
        await Task.Delay(220);
        if (_isClosing || _pulseVersions.GetValueOrDefault(state.StableId) != pulseVersion)
        {
            return;
        }

        _pulseVersions.Remove(state.StableId);
        _pressedNodeIds.Remove(state.StableId);
        RenderNode(visual);
    }

    private void RecordInputProcess(InputEvent inputEvent, string? keyLabel, bool originalWasSuppressed)
    {
        if (_isClosing || inputEvent.IsInjected)
        {
            return;
        }

        if (_shortcutCaptureSession.State == ShortcutCaptureState.Armed)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(keyLabel)
            ? ResolveProcessLogLabel(inputEvent.Source, null)
            : keyLabel;
        var entry = _processLog.TryRecord(
            inputEvent,
            label,
            _runtimeConfiguration,
            originalWasSuppressed,
            DateTimeOffset.Now);
        if (entry is null)
        {
            return;
        }

        AppendProcessLogEntry(entry);
        RenderHistory();
    }

    private string ResolveProcessLogLabel(InputSource source, string? fallback)
    {
        foreach (var visual in _nodeVisuals.Values)
        {
            if (visual.State.CapturedSource is { } captured &&
                MappingTriggerStateMachine.MatchesSource(captured, source))
            {
                return string.IsNullOrWhiteSpace(visual.State.Label)
                    ? visual.State.FriendlyName
                    : visual.State.Label;
            }

            if (string.Equals(visual.State.CaptureIdentity, source.CanonicalKey, StringComparison.Ordinal))
            {
                return visual.State.Label;
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? "未知" : fallback;
    }

    private void AppendProcessLogEntry(InputProcessLogEntry entry)
    {
        ProcessLogEmptyText.Visibility = Visibility.Collapsed;
        var existing = ProcessLogRows.Children
            .OfType<FrameworkElement>()
            .LastOrDefault(child => child.Tag is Guid id && id == entry.Id);
        if (existing is Grid existingGrid)
        {
            UpdateProcessLogRow(existingGrid, entry);
        }
        else if (InputProcessLogClassifier.MatchesFilter(entry, _processLogFilter))
        {
            ProcessLogRows.Children.Add(CreateProcessLogRow(entry));
            TrimProcessLogRows();
        }

        ProcessLogCountText.Text = _processLog.Count.ToString();
        if (_processLogFollowTail)
        {
            ProcessLogScroll.UpdateLayout();
            ProcessLogScroll.ChangeView(null, ProcessLogScroll.ScrollableHeight, null, true);
        }
    }

    private void TrimProcessLogRows()
    {
        while (ProcessLogRows.Children.Count > InputProcessLog.Capacity + 1)
        {
            var extra = ProcessLogRows.Children[0];
            if (ReferenceEquals(extra, ProcessLogEmptyText))
            {
                if (ProcessLogRows.Children.Count < 2)
                {
                    break;
                }

                extra = ProcessLogRows.Children[1];
            }

            ProcessLogRows.Children.Remove(extra);
        }
    }

    private void RebuildProcessLogRows()
    {
        var keepEmpty = ProcessLogEmptyText;
        ProcessLogRows.Children.Clear();
        ProcessLogRows.Children.Add(keepEmpty);
        var entries = _processLog.Snapshot()
            .Where(entry => InputProcessLogClassifier.MatchesFilter(entry, _processLogFilter))
            .ToArray();
        keepEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries)
        {
            ProcessLogRows.Children.Add(CreateProcessLogRow(entry));
        }

        ProcessLogCountText.Text = _processLog.Count.ToString();
        if (_processLogFollowTail)
        {
            ProcessLogScroll.UpdateLayout();
            ProcessLogScroll.ChangeView(null, ProcessLogScroll.ScrollableHeight, null, true);
        }
    }

    private Grid CreateProcessLogRow(InputProcessLogEntry entry)
    {
        var row = new Grid
        {
            Tag = entry.Id,
            Padding = new Thickness(8, 3, 8, 3),
            ColumnSpacing = 8
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        for (var i = 0; i < 7; i++)
        {
            row.Children.Add(new TextBlock
            {
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn((FrameworkElement)row.Children[i], i);
        }

        ((TextBlock)row.Children[0]).FontFamily = new FontFamily("Consolas");
        ((TextBlock)row.Children[1]).FontFamily = new FontFamily("Consolas");
        UpdateProcessLogRow(row, entry);
        return row;
    }

    private void UpdateProcessLogRow(Grid row, InputProcessLogEntry entry)
    {
        var accent = ProcessLogAccent(entry.Disposition);
        var texts = row.Children.OfType<TextBlock>().ToArray();
        texts[0].Text = entry.TimestampLocal.ToString("HH:mm:ss.fff");
        texts[0].Foreground = BrushResource("KpMutedBrush");
        texts[1].Text = InputProcessLogClassifier.PhaseGlyph(entry.Phase);
        texts[1].Foreground = accent;
        texts[2].Text = entry.DeviceLabel;
        texts[2].Foreground = BrushResource("KpMutedBrush");
        texts[3].Text = entry.KeyLabel + InputProcessLogClassifier.FormatRepeatSuffix(entry.RepeatCount);
        texts[3].Foreground = BrushResource("KpTextBrush");
        texts[4].Text = entry.DispositionLabel;
        texts[4].Foreground = accent;
        texts[5].Text = entry.ActionSummary;
        texts[5].Foreground = BrushResource("KpMutedBrush");
        texts[6].Text = entry.ResultLabel;
        texts[6].Foreground = accent;
    }

    private Brush ProcessLogAccent(InputProcessDisposition disposition) => disposition switch
    {
        InputProcessDisposition.InterceptAndRewrite => BrushResource("KpMintBrush"),
        InputProcessDisposition.CannotInterceptOverlay => BrushResource("KpAmberBrush"),
        InputProcessDisposition.CannotInterceptRefused => BrushResource("KpAmberBrush"),
        InputProcessDisposition.KeepOriginalOverlay => BrushResource("KpBlueBrush"),
        InputProcessDisposition.CaptureOnly => BrushResource("KpDimBrush"),
        _ => BrushResource("KpMutedBrush")
    };

    private void RenderHistory()
    {
        RecentHistoryPanel.Children.Clear();
        var latest = _processLog.Latest();
        if (latest is null)
        {
            RecentHistoryPanel.Children.Add(new TextBlock
            {
                Text = "暂无记录",
                FontSize = 9,
                Foreground = BrushResource("KpMutedBrush")
            });
            ProcessLogLatestText.Text = "按下按键后，这里会记下拦截、叠加或放行。";
            return;
        }

        RecentHistoryPanel.Children.Add(new TextBlock
        {
            Text = $"{latest.KeyLabel} · {latest.DispositionLabel}",
            FontSize = 8,
            Foreground = ProcessLogAccent(latest.Disposition),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        ProcessLogLatestText.Text =
            $"{InputProcessLogClassifier.PhaseGlyph(latest.Phase)} {latest.DeviceLabel} {latest.KeyLabel}  {latest.DispositionLabel}";
    }

    private void ProcessLogFilter_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        _processLogFilter = tag switch
        {
            "Rewritten" => InputProcessLogFilter.Rewritten,
            "Uninterceptable" => InputProcessLogFilter.Uninterceptable,
            _ => InputProcessLogFilter.All
        };
        StyleProcessLogFilterButtons();
        RebuildProcessLogRows();
    }

    private void StyleProcessLogFilterButtons()
    {
        StyleProcessLogFilterButton(ProcessLogFilterAllButton, _processLogFilter == InputProcessLogFilter.All);
        StyleProcessLogFilterButton(
            ProcessLogFilterRewrittenButton,
            _processLogFilter == InputProcessLogFilter.Rewritten);
        StyleProcessLogFilterButton(
            ProcessLogFilterUninterceptableButton,
            _processLogFilter == InputProcessLogFilter.Uninterceptable);
    }

    private void StyleProcessLogFilterButton(Button button, bool selected)
    {
        button.Background = selected ? BrushResource("KpPanelSecondaryBrush") : new SolidColorBrush(Colors.Transparent);
        button.Foreground = selected ? BrushResource("KpTextBrush") : BrushResource("KpMutedBrush");
    }

    private void ProcessLogCollapseRepeats_Click(object sender, RoutedEventArgs e)
    {
        _processLog.CollapseRepeats = !_processLog.CollapseRepeats;
        ProcessLogCollapseRepeatsButton.Background = _processLog.CollapseRepeats
            ? BrushResource("KpAccentFillBrush")
            : BrushResource("KpPanelBrush");
        ProcessLogCollapseRepeatsButton.BorderBrush = _processLog.CollapseRepeats
            ? BrushResource("KpMintBrush")
            : BrushResource("KpLineBrush");
        ProcessLogCollapseRepeatsButton.Foreground = BrushResource("KpTextBrush");
    }

    private void ProcessLogToggle_Click(object sender, RoutedEventArgs e)
    {
        _processLogExpanded = !_processLogExpanded;
        ProcessLogBody.Visibility = _processLogExpanded ? Visibility.Visible : Visibility.Collapsed;
        ProcessLogPanel.Height = _processLogExpanded ? 188 : 36;
        ProcessLogToggleButton.Content = _processLogExpanded ? "收起" : "展开";
    }

    private void ProcessLogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        _processLogFollowTail = ProcessLogScroll.VerticalOffset >= ProcessLogScroll.ScrollableHeight - 24;
    }

    private void SetMode(bool mappingMode)
    {
        if (_armedSpecialSlot is not null)
        {
            CancelArmedHidCapture();
        }

        if (_armedChordSlot is not null)
        {
            CancelChordCapture();
        }

        _mappingMode = mappingMode;
        BreadcrumbModeText.Text = mappingMode ? $"映射方案 · {_activeProfileName}" : "采集与测试";
        PageTitleText.Text = mappingMode ? $"映射方案 · {_activeProfileName}" : "采集与测试";
        PageDescriptionText.Text = mappingMode
            ? "布局与采集模式完全一致；已映射按键被突出显示，未映射按键适当弱化。"
            : "按下设备按键查看真实 Raw Input；在任意按键上右键创建映射。";

        CaptureStatusPanel.Visibility = mappingMode ? Visibility.Collapsed : Visibility.Visible;
        MappingStatusPanel.Visibility = mappingMode ? Visibility.Visible : Visibility.Collapsed;
        CaptureToggleButton.Visibility = mappingMode ? Visibility.Collapsed : Visibility.Visible;

        CaptureModeButton.Background = mappingMode ? new SolidColorBrush(Colors.Transparent) : BrushResource("KpPanelSecondaryBrush");
        CaptureModeButton.Foreground = mappingMode ? BrushResource("KpMutedBrush") : BrushResource("KpTextBrush");
        MappingModeButton.Background = mappingMode ? BrushResource("KpPanelSecondaryBrush") : new SolidColorBrush(Colors.Transparent);
        MappingModeButton.Foreground = mappingMode ? BrushResource("KpTextBrush") : BrushResource("KpMutedBrush");

        CaptureNavButton.Background = mappingMode ? new SolidColorBrush(Colors.Transparent) : BrushResource("KpAccentSoftBrush");
        CaptureNavButton.BorderBrush = mappingMode ? new SolidColorBrush(Colors.Transparent) : BrushResource("KpAccentSoftBorderBrush");
        MappingNavButton.Background = mappingMode ? BrushResource("KpAccentSoftBrush") : new SolidColorBrush(Colors.Transparent);
        MappingNavButton.BorderBrush = mappingMode ? BrushResource("KpAccentSoftBorderBrush") : new SolidColorBrush(Colors.Transparent);

        if (mappingMode && _selectedNode is null && _nodeVisuals.TryGetValue("keyboard:Space", out var space))
        {
            SelectNode(space.State, updateCaptureStatus: false);
        }
        else
        {
            RenderAllNodes();
        }
    }

    private void RenderAllNodes()
    {
        foreach (var visual in _nodeVisuals.Values)
        {
            RenderNode(visual);
        }
    }

    private void RenderNode(InputNodeVisual visual)
    {
        var mapped = visual.State.Mapping is not null;
        var selected = _mappingMode && ReferenceEquals(visual.State, _selectedNode);
        var pressed = _pressedNodeIds.Contains(visual.State.StableId);
        var armed = ReferenceEquals(visual.State, _armedSpecialSlot) ||
            ReferenceEquals(visual.State, _armedChordSlot);

        visual.Button.Opacity = _mappingMode && !mapped ? 0.4 : 1;
        visual.Button.Background = pressed || (_mappingMode && mapped)
            ? BrushResource("KpMintBrush")
            : BrushResource("KpPanelSecondaryBrush");
        visual.Button.Foreground = pressed || (_mappingMode && mapped)
            ? BrushResource("KpOnAccentBrush")
            : BrushResource("KpMutedBrush");
        visual.Button.BorderBrush = armed
            ? BrushResource("KpAmberBrush")
            : selected
            ? BrushResource("KpSelectionBrush")
            : mapped
                ? BrushResource("KpMintBrush")
                : BrushResource("KpLineBrush");
        visual.Button.BorderThickness = selected || armed ? new Thickness(2) : new Thickness(1);
        visual.MappingDot.Visibility = !_mappingMode && mapped ? Visibility.Visible : Visibility.Collapsed;
        visual.TargetText.Visibility = _mappingMode && mapped ? Visibility.Visible : Visibility.Collapsed;
        visual.TargetText.Text = mapped ? $"→ {DisplayMappingTarget(visual.State.Mapping!)}" : string.Empty;

        if (visual.State.Kind == InputNodeKind.Special)
        {
            visual.LabelText.Text = visual.State.FriendlyName;
            if (visual.SecondaryText is not null)
            {
                visual.SecondaryText.Text = string.IsNullOrWhiteSpace(visual.State.RawCode)
                    ? armed ? "已就绪 · 请按实体特殊键" : "点击槽位后采集 HID"
                    : visual.State.RawCode;
                visual.SecondaryText.Visibility = _mappingMode && mapped ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void OpenMapping(InputNodeState state)
    {
        if (state.Mapping?.IsReadOnlyPassthrough == true)
        {
            ShowToast("这是配置中的高级动作；当前界面会原样保留，但不会降级改写", InfoBarSeverity.Informational);
            return;
        }

        if (state.Kind == InputNodeKind.Special && string.IsNullOrWhiteSpace(state.CaptureIdentity))
        {
            ShowToast("请先按下实体特殊键，让它占用此槽位", InfoBarSeverity.Informational);
            return;
        }

        CancelArmedHidCapture();

        _editingNode = state;
        _drawerSourceButton = _nodeVisuals.TryGetValue(state.StableId, out var sourceVisual)
            ? sourceVisual.Button
            : null;
        SelectNode(state, updateCaptureStatus: false);
        MappingDrawerTitle.Text = state.Mapping is null ? "新建映射" : "编辑映射";
        DrawerSourceNameText.Text = $"{state.FriendlyName} · {state.Label}";
        DrawerSourceCodeText.Text = string.IsNullOrWhiteSpace(state.RawCode) ? state.StableId : state.RawCode;

        var actionType = state.Mapping?.ActionType ?? "快捷键";
        var actionButton = FindActionButton(actionType) ?? ShortcutActionButton;
        ActionValueTextBox.Text = state.Mapping?.ActionValue ?? string.Empty;
        SelectActionType(actionButton, clearValue: false);
        var canSuppressOriginal = MappingDispatchPolicy.CanRequestOriginalSuppression(state.CapturedSource);
        BlockOriginalToggle.IsEnabled = canSuppressOriginal;
        BlockOriginalToggle.IsOn = canSuppressOriginal && (state.Mapping?.BlockOriginal ?? true);
        BlockOriginalHintText.Text = canSuppressOriginal
            ? "驱动阶段才会真正生效"
            : state.CapturedSource?.Control.Kind is InputControlKind.InputChord or InputControlKind.InputSequence
                ? "2 秒录制的组合/序列为避免误吞原键，始终保持安全透传"
                : "当前输入类型不支持屏蔽，将保留原始输入";
        var sequenceSource = state.CapturedSource?.Control.Kind == InputControlKind.InputSequence;
        foreach (var item in TriggerComboBox.Items.OfType<ComboBoxItem>())
        {
            item.IsEnabled = !sequenceSource ||
                !string.Equals(item.Content?.ToString(), "长按 600ms", StringComparison.Ordinal);
        }
        SetSelectedTrigger(state.Mapping?.Trigger ?? "单击");
        LoadConditionEditor(state.Mapping?.Condition ?? new());
        if (sequenceSource && string.Equals(SelectedTrigger(), "长按 600ms", StringComparison.Ordinal))
        {
            TriggerComboBox.SelectedIndex = 0;
        }

        MappingScrim.Visibility = Visibility.Visible;
        MappingDrawer.Visibility = Visibility.Visible;
        if (ActionPresetComboBox.Visibility == Visibility.Visible)
        {
            ActionPresetComboBox.Focus(FocusState.Programmatic);
        }
        else
        {
            ActionValueTextBox.Focus(FocusState.Programmatic);
        }
    }

    private Button? FindActionButton(string actionType)
    {
        return AllActionButtons().FirstOrDefault(button => string.Equals(button.Tag?.ToString(), actionType, StringComparison.Ordinal));
    }

    private IEnumerable<Button> AllActionButtons()
    {
        yield return ShortcutActionButton;
        yield return SpecialKeyActionButton;
        yield return MediaActionButton;
        yield return VolumeActionButton;
        yield return LaunchActionButton;
        yield return UriActionButton;
        yield return ScriptActionButton;
        yield return GamepadActionButton;
    }

    private void ActionType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            if (_shortcutCaptureSession.State == ShortcutCaptureState.Armed)
            {
                FinishShortcutCapture(cancelled: true);
            }
            SelectActionType(button, clearValue: true);
        }
    }

    private void RecordShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcutCaptureSession.State == ShortcutCaptureState.Armed)
        {
            FinishShortcutCapture(cancelled: true, "快捷键录制已取消");
            return;
        }

        CancelArmedHidCapture();
        CancelChordCapture();
        _shortcutCaptureSession.Arm(DateTimeOffset.UtcNow);
        ApplyRuntimeConfiguration(_runtimeConfiguration with { IsMappingEnabled = false });
        ActionValueTextBox.Text = string.Empty;
        ActionCaptureStatusText.Text = "正在录制；2 秒内按下要输出的键盘快捷键";
        RecordShortcutButton.Content = "取消录制";
        _actionCaptureTimer.Stop();
        _actionCaptureTimer.Start();
        ShowToast("快捷键录制已开始；映射执行与原键抑制已临时暂停", InfoBarSeverity.Informational);
    }

    private void UpdateShortcutCapture()
    {
        if (_shortcutCaptureSession.State != ShortcutCaptureState.Armed)
        {
            _actionCaptureTimer.Stop();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var deadline = _shortcutCaptureSession.DeadlineUtc;
        if (deadline.HasValue)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((deadline.Value - now).TotalMilliseconds / 100.0));
            RecordShortcutButton.Content = $"取消 · {remaining / 10.0:0.0}s";
        }

        if (!_shortcutCaptureSession.TryComplete(now))
        {
            return;
        }

        FinishShortcutCapture(cancelled: false);
    }

    private void FinishShortcutCapture(bool cancelled, string? cancelledMessage = null)
    {
        _actionCaptureTimer.Stop();
        if (cancelled)
        {
            _shortcutCaptureSession.Cancel();
        }

        if (!_isClosing)
        {
            RefreshEffectiveRuntimeConfiguration(force: true);
        }

        RecordShortcutButton.Content = "录制 2 秒";
        if (cancelled)
        {
            ActionCaptureStatusText.Text = "点击录制后，2 秒内按下要输出的键盘快捷键";
            if (!string.IsNullOrWhiteSpace(cancelledMessage))
            {
                ShowToast(cancelledMessage, InfoBarSeverity.Informational);
            }
            return;
        }

        if (_shortcutCaptureSession.State == ShortcutCaptureState.Completed &&
            !string.IsNullOrWhiteSpace(_shortcutCaptureSession.CompletedText))
        {
            ActionValueTextBox.Text = _shortcutCaptureSession.CompletedText;
            ActionCaptureStatusText.Text = $"已录制：{_shortcutCaptureSession.CompletedText}";
            ShowToast("快捷键已录制，可继续编辑或保存");
            return;
        }

        var error = _shortcutCaptureSession.Error ?? "快捷键录制失败，请重试";
        ActionCaptureStatusText.Text = error;
        ShowToast(error, InfoBarSeverity.Warning);
    }

    private void SelectActionType(Button button, bool clearValue)
    {
        _selectedActionButton = button;
        foreach (var item in AllActionButtons())
        {
            var selected = ReferenceEquals(item, button);
            item.BorderBrush = selected ? BrushResource("KpMintBrush") : BrushResource("KpLineBrush");
            item.Background = selected
                ? BrushResource("KpAccentFillBrush")
                : BrushResource("KpPanelBrush");
            item.Foreground = selected ? BrushResource("KpMintBrush") : BrushResource("KpMutedBrush");
        }

        var type = button.Tag?.ToString() ?? "快捷键";
        ActionValueTextBox.PlaceholderText = ActionPlaceholders[type];
        if (clearValue)
        {
            ActionValueTextBox.Text = string.Empty;
        }

        ConfigureActionEditor(type);
    }

    private void ConfigureActionEditor(string actionType)
    {
        var isShortcut = string.Equals(actionType, "快捷键", StringComparison.Ordinal);
        var isCopiedInput = string.Equals(actionType, "按键 / 组合键", StringComparison.Ordinal);
        var isMedia = string.Equals(actionType, "媒体控制", StringComparison.Ordinal);
        var isVolume = string.Equals(actionType, "音量控制", StringComparison.Ordinal);

        RecordShortcutButton.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;
        PasteInputTargetButton.Visibility = isCopiedInput ? Visibility.Visible : Visibility.Collapsed;
        ActionPresetComboBox.Visibility = isMedia || isVolume
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActionCaptureStatusText.Visibility = isShortcut || isCopiedInput
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActionCaptureStatusText.Text = isShortcut
            ? "点击录制后，2 秒内按下要输出的键盘快捷键"
            : "请粘贴主页“复制按键信息”生成的版本化 KeyPilot 数据";

        if (!isMedia && !isVolume)
        {
            ActionValueEditorGrid.Visibility = Visibility.Visible;
            return;
        }

        var existingValue = ActionValueTextBox.Text.Trim();
        ActionPresetComboBox.Items.Clear();
        var values = isMedia ? MediaActionValues : VolumeActionValues;
        foreach (var value in values)
        {
            ActionPresetComboBox.Items.Add(new ComboBoxItem { Content = value });
        }

        var selectedIndex = Array.FindIndex(
            values,
            value => string.Equals(value, existingValue, StringComparison.Ordinal));
        if (isVolume && existingValue.StartsWith("设置音量 ", StringComparison.Ordinal))
        {
            selectedIndex = VolumeActionValues.Length - 1;
            ActionValueTextBox.Text = existingValue[5..].Trim().TrimEnd('%');
        }

        ActionPresetComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        UpdatePresetValueEditor();
    }

    private void ActionPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePresetValueEditor();

    private void UpdatePresetValueEditor()
    {
        var actionType = _selectedActionButton?.Tag?.ToString();
        var preset = (ActionPresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.Equals(actionType, "媒体控制", StringComparison.Ordinal))
        {
            ActionValueEditorGrid.Visibility = Visibility.Collapsed;
            ActionValueTextBox.Text = preset ?? string.Empty;
            return;
        }

        if (!string.Equals(actionType, "音量控制", StringComparison.Ordinal))
        {
            return;
        }

        var isExactLevel = string.Equals(preset, VolumeActionValues[^1], StringComparison.Ordinal);
        ActionValueEditorGrid.Visibility = isExactLevel ? Visibility.Visible : Visibility.Collapsed;
        RecordShortcutButton.Visibility = Visibility.Collapsed;
        PasteInputTargetButton.Visibility = Visibility.Collapsed;
        if (!isExactLevel)
        {
            ActionValueTextBox.Text = preset ?? string.Empty;
        }
        else
        {
            ActionValueTextBox.PlaceholderText = "输入 0–100，例如 37";
            if (!int.TryParse(ActionValueTextBox.Text.Trim().TrimEnd('%'), out _))
            {
                ActionValueTextBox.Text = string.Empty;
            }
        }
    }

    private string CurrentActionValue()
    {
        var actionType = _selectedActionButton?.Tag?.ToString();
        var preset = (ActionPresetComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.Equals(actionType, "媒体控制", StringComparison.Ordinal))
        {
            return preset?.Trim() ?? string.Empty;
        }

        if (string.Equals(actionType, "音量控制", StringComparison.Ordinal) &&
            string.Equals(preset, VolumeActionValues[^1], StringComparison.Ordinal))
        {
            return $"设置音量 {ActionValueTextBox.Text.Trim().TrimEnd('%')}%";
        }

        return ActionValueTextBox.Text.Trim();
    }

    private async void SaveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (_editingNode is null)
        {
            return;
        }

        var value = CurrentActionValue();
        var actionType = _selectedActionButton?.Tag?.ToString() ?? "快捷键";
        if (!TryValidateAction(actionType, value, out var validationMessage))
        {
            ShowToast(validationMessage, InfoBarSeverity.Warning);
            return;
        }

        if (_conditionKind != MappingConditionKind.Always && _conditionApplications.Count == 0)
        {
            ShowToast("限制生效范围时请至少选择一个软件。", InfoBarSeverity.Warning);
            return;
        }

        var actionTarget = ResolveActionTarget(actionType, value);
        var editingNode = _editingNode;
        var previous = editingNode.Mapping;
        MappingAction? action;
        if (previous is not null &&
            !previous.IsReadOnlyPassthrough &&
            previous.Action is not null &&
            string.Equals(previous.ActionType, actionType, StringComparison.Ordinal) &&
            string.Equals(previous.ActionValue, value, StringComparison.Ordinal))
        {
            action = previous.Action;
        }
        else if (!WorkspaceConfigurationAdapter.TryCreateAction(
            actionType,
            value,
            WorkspaceNodes(),
            out action,
            out var actionError))
        {
            ShowToast(actionError, InfoBarSeverity.Warning);
            return;
        }

        editingNode.Mapping = new MappingDraft
        {
            PersistedId = previous?.PersistedId,
            OriginalMapping = previous?.OriginalMapping,
            SourceStableId = editingNode.StableId,
            SourceCaptureIdentity = previous?.SourceCaptureIdentity ?? editingNode.CaptureIdentity,
            SourceDevicePath = previous?.SourceDevicePath ?? editingNode.DevicePath,
            Source = previous?.Source ?? editingNode.CapturedSource,
            ActionType = actionType,
            ActionValue = value,
            TargetStableId = actionTarget?.StableId,
            TargetCaptureIdentity = actionTarget?.CaptureIdentity,
            TargetSource = actionTarget?.CapturedSource,
            Action = action,
            Trigger = SelectedTrigger(),
            Condition = CaptureConditionEditor(),
            BlockOriginal = BlockOriginalToggle.IsOn
        };
        LastOperationText.Text = $"设置：{editingNode.Label} → {value}";
        UpdateMappingCount();
        SelectNode(editingNode, updateCaptureStatus: false);
        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (!IsCurrentSaveResult(saveResult))
        {
            return;
        }

        if (saveResult.Outcome == ConfigurationSaveOutcome.Failed)
        {
            return;
        }

        CloseMappingDrawer();
        ShowToast("映射已写入本地配置");
    }

    private string SelectedTrigger() =>
        (TriggerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "单击";

    private void SetSelectedTrigger(string trigger)
    {
        for (var index = 0; index < TriggerComboBox.Items.Count; index++)
        {
            if (TriggerComboBox.Items[index] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), trigger, StringComparison.Ordinal))
            {
                TriggerComboBox.SelectedIndex = index;
                return;
            }
        }

        TriggerComboBox.SelectedIndex = 0;
    }

    private void UpdateMappingCount()
    {
        var visible = _nodeVisuals.Values.Count(visual => visual.State.Mapping is not null);
        var detached = _workspaceRestore?.DetachedMappings.Count ?? 0;
        MappingCountText.Text = (visible + detached).ToString();
    }

    private async Task RenameSpecialKeyAsync(InputNodeState state)
    {
        if (state.Kind != InputNodeKind.Special || string.IsNullOrWhiteSpace(state.RawCode))
        {
            return;
        }

        var nameBox = new TextBox
        {
            Text = state.FriendlyName == "未命名特殊键" ? string.Empty : state.FriendlyName,
            PlaceholderText = "例如：掌机控制面板键"
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "名称只影响显示，原始代码和设备身份保持不变。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushResource("KpMutedBrush")
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock
        {
            Text = state.RawCode,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushResource("KpDimBrush")
        });

        var dialog = new ContentDialog
        {
            Title = "重命名特殊按键",
            Content = panel,
            PrimaryButtonText = "保存名称",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        dialog.Closing += (_, args) =>
        {
            if (args.Result == ContentDialogResult.Primary && string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                ShowToast("名称不能为空", InfoBarSeverity.Warning);
            }
        };

        _suspendWorkspaceCapture = true;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _suspendWorkspaceCapture = false;
        }

        DispatcherQueue.TryEnqueue(() => _nodeVisuals[state.StableId].Button.Focus(FocusState.Programmatic));

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        state.FriendlyName = name;
        RenderNode(_nodeVisuals[state.StableId]);
        SelectNode(state, updateCaptureStatus: false);
        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (!IsCurrentSaveResult(saveResult))
        {
            return;
        }

        if (saveResult.Outcome == ConfigurationSaveOutcome.Succeeded)
        {
            ShowToast("特殊按键名称已写入本地配置");
        }
    }

    private void CaptureMode_Click(object sender, RoutedEventArgs e) => ShowWorkspace(WorkspacePage.Capture);

    private void MappingMode_Click(object sender, RoutedEventArgs e) => ShowWorkspace(WorkspacePage.Mapping);

    private void ShowWorkspace(WorkspacePage page)
    {
        _workspacePage = page;
        SetMode(page == WorkspacePage.Mapping);
    }

    private void InputDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_inputDiagnosticSession.IsActive)
        {
            FinishInputDiagnostic("stopped-by-user", showToast: true);
            return;
        }

        _inputDiagnosticSession.Start(DateTimeOffset.UtcNow, GetDisplayVersion());
        _inputDiagnosticTimer.Start();
        UpdateInputDiagnosticCountdown();
        ShowToast(
            "输入诊断已开始：10 秒内只操作待测按键；日志不会记录输入文字或完整设备路径",
            InfoBarSeverity.Informational);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _processLog.Clear();
        RebuildProcessLogRows();
        RenderHistory();
        ShowToast("按键过程已清空");
    }

    private void CopyDetectedInput_Click(object sender, RoutedEventArgs e) =>
        CopyInputTargetToClipboard(_selectedNode?.CapturedSource);

    private void CopyInputTargetToClipboard(InputSource? source)
    {
        if (!LogicalOutputTargetCodec.TryFromInputSource(source, out var target, out var error) ||
            target is null)
        {
            ShowToast(
                $"该输入目前只能作为来源，不能作为输出目标：{error}",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var encoded = LogicalOutputTargetCodec.Encode(target);
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy
            };
            package.SetText(encoded);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowToast($"已复制：{DescribeLogicalOutputTarget(target)}");
        }
        catch (Exception exception)
        {
            ReportError($"复制按键信息失败：{exception.Message}", "Clipboard", exception);
        }
    }

    private async void PasteInputTarget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                ShowToast("剪贴板中没有文字按键信息", InfoBarSeverity.Warning);
                return;
            }

            var encoded = (await content.GetTextAsync()).Trim();
            if (!LogicalOutputTargetCodec.TryDecode(encoded, out var target, out var error) ||
                target is null)
            {
                ShowToast($"这不是有效的 KeyPilot 按键信息：{error}", InfoBarSeverity.Warning);
                return;
            }

            ActionValueTextBox.Text = encoded;
            ActionCaptureStatusText.Text = $"已粘贴：{DescribeLogicalOutputTarget(target)}";
            ShowToast("按键信息已粘贴；保存前会再次检查输出后端");
        }
        catch (Exception exception)
        {
            ReportError($"读取剪贴板失败：{exception.Message}", "Clipboard", exception);
        }
    }

    private static string DescribeLogicalOutputTarget(LogicalOutputTarget target)
    {
        var controls = target.Controls.Select(DescribeLogicalOutputControl);
        var body = string.Join(" + ", controls);
        return target.Family switch
        {
            LogicalOutputFamily.Keyboard => $"键盘 {body}",
            LogicalOutputFamily.Gamepad => $"手柄 {body}（当前仅可复制，尚无输出后端）",
            _ => $"HID {body}（当前仅可复制，尚无输出后端）"
        };
    }

    private static string DisplayMappingTarget(MappingDraft draft)
    {
        if ((draft.Action is CopyInputToOutputAction ||
             string.Equals(draft.ActionType, "按键 / 组合键", StringComparison.Ordinal)) &&
            LogicalOutputTargetCodec.TryDecode(draft.ActionValue, out var target, out _) &&
            target is not null)
        {
            return DescribeLogicalOutputTarget(target);
        }

        return draft.ActionValue;
    }

    private static string DescribeLogicalOutputControl(InputControlId control)
    {
        if (control.Kind == InputControlKind.KeyboardScanCode &&
            control.Code is > 0 and <= ushort.MaxValue)
        {
            var prefix = control.RawQualifier?.Contains("PREFIX=0004", StringComparison.OrdinalIgnoreCase) == true
                ? (ushort)0x0004
                : control.IsExtended
                    ? (ushort)0x0002
                    : (ushort)0;
            if (KeyboardKeyResolver.TryResolveSet1Code((ushort)control.Code, prefix, out var stableId))
            {
                return stableId switch
                {
                    "ControlLeft" or "ControlRight" => "Ctrl",
                    "AltLeft" or "AltRight" => "Alt",
                    "ShiftLeft" or "ShiftRight" => "Shift",
                    "MetaLeft" or "MetaRight" => "Win",
                    _ when stableId.StartsWith("Key", StringComparison.Ordinal) => stableId[3..],
                    _ when stableId.StartsWith("Digit", StringComparison.Ordinal) => stableId[5..],
                    _ => stableId
                };
            }

            return control.Code == 0x6D ? "F22 / Scan 0x6D" : $"Scan 0x{control.Code:X2}";
        }

        return control.Kind switch
        {
            InputControlKind.VirtualKey => $"VK 0x{control.Code:X2}",
            InputControlKind.GamepadButton => $"Button 0x{control.Code:X4}",
            InputControlKind.GamepadAxisDirection => $"Analog {control.Code}",
            InputControlKind.GamepadRotation => $"Rotation {control.Code}",
            InputControlKind.HidUsage => $"Usage {control.UsagePage:X4}/{control.Usage:X4}",
            _ => $"{control.Kind} {control.Code}"
        };
    }

    private void UpdateInputDiagnosticCountdown()
    {
        if (!_inputDiagnosticSession.IsActive)
        {
            _inputDiagnosticTimer.Stop();
            InputDiagnosticButton.Content = "诊断 10 秒";
            return;
        }

        var remaining = _inputDiagnosticSession.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            FinishInputDiagnostic("duration-complete", showToast: true);
            return;
        }

        InputDiagnosticButton.Content = $"停止诊断 {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
    }

    private void FinishInputDiagnostic(string reason, bool showToast)
    {
        _inputDiagnosticTimer.Stop();
        var result = _inputDiagnosticSession.StopAndSave(reason, DateTimeOffset.UtcNow);
        InputDiagnosticButton.Content = "诊断 10 秒";
        if (!result.Saved)
        {
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                ReportError($"输入诊断保存失败：{result.Error}", "InputDiagnostic");
            }

            return;
        }

        if (showToast)
        {
            var suffix = result.Truncated ? "（已达到记录上限）" : string.Empty;
            ShowToast($"输入诊断已保存：{result.EventCount} 条{suffix} · {result.Path}");
        }
    }

    private void CaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        _captureEnabled = !_captureEnabled;
        if (!_captureEnabled)
        {
            CancelArmedHidCapture();
        }
        CaptureToggleButton.Content = _captureEnabled ? "● 正在采集" : "▶ 开始采集";
        CaptureToggleButton.Background = _captureEnabled ? BrushResource("KpMintBrush") : BrushResource("KpPanelBrush");
        CaptureToggleButton.Foreground = _captureEnabled ? BrushResource("KpOnAccentBrush") : BrushResource("KpTextBrush");
        ShowToast(_captureEnabled ? "按键采集已开启" : "按键采集已暂停");
    }

    private async void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingMasterToggle || _suspendWorkspaceCapture || _isClosing)
        {
            return;
        }

        var requestedEnabled = MasterToggle.IsOn;
        _configuration = _configuration with { IsMappingEnabled = requestedEnabled };
        if (!requestedEnabled)
        {
            // Turning mappings off must not wait for storage and must never be rolled back to On.
            RefreshEffectiveRuntimeConfiguration(force: true);
            UpdateMasterStatus();
        }

        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (!IsCurrentSaveResult(saveResult))
        {
            return;
        }

        if (saveResult.Outcome == ConfigurationSaveOutcome.Failed)
        {
            return;
        }

        UpdateMasterStatus();
        ShowToast(MasterToggle.IsOn
            ? _driverSuppressionActive
                ? "全局映射已启用 · 内核抑制"
                : "全局映射已启用 · 仅键盘兼容抑制"
            : "全局映射已关闭");
    }

    private void CloseMapping_Click(object sender, RoutedEventArgs e) => CloseMappingDrawer();

    private void ConditionKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingConditionKind || ConditionRestrictedPanel is null)
        {
            return;
        }

        _conditionKind = SelectedConditionKind();
        UpdateConditionEditorVisibility();
    }

    private void ToggleConditionPicker_Click(object sender, RoutedEventArgs e)
    {
        var open = ConditionPickerPanel.Visibility != Visibility.Visible;
        ConditionPickerPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToggleConditionPickerButton.Content = open ? "收起选择器" : "选择软件";
        if (open)
        {
            RefreshConditionPickerLists();
        }
    }

    private void UseCurrentForeground_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectableForegroundIdentity() is not { } identity)
        {
            ShowToast("尚未检测到其他前台软件。", InfoBarSeverity.Warning);
            return;
        }

        AddConditionApplication(identity);
    }

    private void CaptureConditionApp_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingConditionApplication)
        {
            CancelConditionCapture("已取消捕捉软件");
            return;
        }

        _capturingConditionApplication = true;
        _conditionCaptureBaseline = ApplicationProfileResolver.NormalizeProcessName(
            _currentForegroundProcessName);
        _conditionCaptureDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(8);
        CaptureConditionAppButton.Content = "取消捕捉";
        ConditionHintText.Text = "请在 8 秒内切到目标软件，再回到 KeyPilot。";
        _conditionCaptureTimer.Start();
        ShowToast("请切换到目标软件；检测到后会自动加入生效范围", InfoBarSeverity.Informational);
    }

    private void AddManualConditionApp_Click(object sender, RoutedEventArgs e)
    {
        var processName = ApplicationProfileResolver.NormalizeProcessName(ConditionManualTextBox.Text);
        if (processName.Length == 0)
        {
            ShowToast("请填写进程名称，例如 chrome。", InfoBarSeverity.Warning);
            return;
        }

        AddConditionApplication(new ApplicationIdentity
        {
            ProcessName = processName,
            DisplayName = DisplayProcessName(processName)
        });
        ConditionManualTextBox.Text = string.Empty;
    }

    private void LoadConditionEditor(MappingCondition condition)
    {
        CancelConditionCapture();
        _conditionKind = condition.Kind;
        _conditionApplications.Clear();
        foreach (var application in condition.Applications ?? [])
        {
            if (application is not null && application.HasMatchKey)
            {
                _conditionApplications.Add(application);
            }
        }

        _updatingConditionKind = true;
        SetSelectedConditionKind(_conditionKind);
        _updatingConditionKind = false;
        ConditionPickerPanel.Visibility = Visibility.Collapsed;
        ToggleConditionPickerButton.Content = "选择软件";
        RebuildConditionSelectedApps();
        UpdateConditionEditorVisibility();
        RefreshConditionPickerLists();
    }

    private MappingCondition CaptureConditionEditor()
    {
        var applications = _conditionKind == MappingConditionKind.Always
            ? []
            : _conditionApplications
                .Select(application => RecentApplicationCatalog.Merge(application, null))
                .ToList();
        foreach (var application in applications)
        {
            _configuration = _configuration with
            {
                KnownApplications = RecentApplicationCatalog.RememberKnown(
                    _configuration.KnownApplications,
                    application).ToList()
            };
        }

        return new MappingCondition
        {
            Kind = _conditionKind,
            Applications = applications
        };
    }

    private void UpdateConditionEditorVisibility()
    {
        if (ConditionRestrictedPanel is null || ConditionHintText is null)
        {
            return;
        }

        var restricted = _conditionKind != MappingConditionKind.Always;
        ConditionRestrictedPanel.Visibility = restricted ? Visibility.Visible : Visibility.Collapsed;
        ConditionHintText.Text = _capturingConditionApplication
            ? "请在 8 秒内切到目标软件，再回到 KeyPilot。"
            : restricted
                ? "条件不满足时，这条映射不会触发，原按键会照常发给前台软件。"
                : "不限制软件时，这条映射在任何前台都生效。";
        if (!restricted)
        {
            if (ConditionPickerPanel is not null)
            {
                ConditionPickerPanel.Visibility = Visibility.Collapsed;
            }

            if (ToggleConditionPickerButton is not null)
            {
                ToggleConditionPickerButton.Content = "选择软件";
            }

            CancelConditionCapture();
        }
    }

    private void RebuildConditionSelectedApps()
    {
        ConditionSelectedAppsPanel.Children.Clear();
        if (_conditionApplications.Count == 0)
        {
            ConditionSelectedAppsPanel.Children.Add(new TextBlock
            {
                Text = "还没有选择软件。",
                FontSize = 9,
                Foreground = BrushResource("KpMutedBrush")
            });
            return;
        }

        foreach (var application in _conditionApplications.ToList())
        {
            ConditionSelectedAppsPanel.Children.Add(
                CreateApplicationChoiceRow(application, "移除", () =>
                {
                    _conditionApplications.RemoveAll(candidate => candidate.Matches(application));
                    RebuildConditionSelectedApps();
                }));
        }
    }

    private void RefreshConditionPickerLists()
    {
        if (MappingDrawer.Visibility != Visibility.Visible ||
            ConditionPickerPanel.Visibility != Visibility.Visible)
        {
            UpdateConditionCurrentForegroundRow();
            return;
        }

        UpdateConditionCurrentForegroundRow();
        RebuildApplicationChoiceList(
            ConditionRecentPanel,
            VisibleRecentApplications(),
            "还没有最近前台记录。切到目标软件后再回来即可。");
        RebuildApplicationChoiceList(
            ConditionRunningPanel,
            VisibleRunningApplications(),
            "没有检测到带窗口的正在运行软件。");
    }

    private void UpdateConditionCurrentForegroundRow()
    {
        var identity = TryGetSelectableForegroundIdentity();
        ConditionCurrentForegroundText.Text = identity is null
            ? "尚未检测到其他前台软件"
            : identity.EffectiveDisplayName;
        UseCurrentForegroundButton.IsEnabled = identity is not null;
    }

    private void RebuildApplicationChoiceList(
        StackPanel host,
        IReadOnlyList<ApplicationIdentity> applications,
        string emptyText)
    {
        host.Children.Clear();
        if (applications.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = emptyText,
                FontSize = 8,
                Foreground = BrushResource("KpMutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var application in applications.Take(8))
        {
            host.Children.Add(CreateApplicationChoiceRow(application, "加入", () =>
                AddConditionApplication(application)));
        }
    }

    private UIElement CreateApplicationChoiceRow(
        ApplicationIdentity application,
        string actionLabel,
        Action onClick)
    {
        var root = new Grid { ColumnSpacing = 8 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = application.EffectiveDisplayName,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var subtitle = new TextBlock
        {
            Text = DisplayProcessName(application.ProcessName),
            FontSize = 8,
            Foreground = BrushResource("KpMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var labels = new StackPanel();
        labels.Children.Add(title);
        labels.Children.Add(subtitle);
        var button = new Button
        {
            Content = actionLabel,
            Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["KpButtonStyle"]
        };
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, 1);
        root.Children.Add(labels);
        root.Children.Add(button);
        return root;
    }

    private void AddConditionApplication(ApplicationIdentity identity)
    {
        if (!identity.HasMatchKey)
        {
            return;
        }

        var merged = RecentApplicationCatalog.Merge(identity, null);
        var existing = _conditionApplications.FindIndex(candidate => candidate.Matches(merged));
        if (existing >= 0)
        {
            _conditionApplications[existing] = RecentApplicationCatalog.Merge(
                merged,
                _conditionApplications[existing]);
        }
        else
        {
            if (_conditionApplications.Count >= MappingCondition.MaximumApplications)
            {
                ShowToast($"一条映射最多选择 {MappingCondition.MaximumApplications} 个软件。", InfoBarSeverity.Warning);
                return;
            }

            _conditionApplications.Add(merged);
        }

        RebuildConditionSelectedApps();
        ShowToast($"已加入 {merged.EffectiveDisplayName}");
    }

    private IReadOnlyList<ApplicationIdentity> VisibleRecentApplications()
    {
        var known = _configuration.KnownApplications ?? [];
        return _configuration.RecentForegroundApplications
            .Select(entry => entry.Application)
            .Concat(known)
            .Where(application => application is not null &&
                application.HasMatchKey &&
                !IsKeyPilotProcess(application.ProcessName))
            .Select(application => application)
            .DistinctBy(application => application.NormalizedPackageFamilyName.Length > 0
                ? $"pfn:{application.NormalizedPackageFamilyName}"
                : $"exe:{application.NormalizedProcessName}")
            .Take(12)
            .ToList();
    }

    private IReadOnlyList<ApplicationIdentity> VisibleRunningApplications() =>
        _applicationCatalog.ListRunningWindowedApplications()
            .Where(application => !IsKeyPilotProcess(application.ProcessName))
            .Take(12)
            .ToList();

    private ApplicationIdentity? TryGetSelectableForegroundIdentity()
    {
        if (_currentForegroundIdentity is { } identity &&
            identity.HasMatchKey &&
            !IsKeyPilotProcess(identity.ProcessName))
        {
            return identity;
        }

        if (!string.IsNullOrWhiteSpace(_lastExternalForegroundProcessName) &&
            !IsKeyPilotProcess(_lastExternalForegroundProcessName))
        {
            return new ApplicationIdentity
            {
                ProcessName = ApplicationProfileResolver.NormalizeProcessName(
                    _lastExternalForegroundProcessName),
                DisplayName = DisplayProcessName(_lastExternalForegroundProcessName)
            };
        }

        return null;
    }

    private MappingConditionKind SelectedConditionKind()
    {
        if (ConditionKindComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<MappingConditionKind>(tag, out var kind))
        {
            return kind;
        }

        return MappingConditionKind.Always;
    }

    private void SetSelectedConditionKind(MappingConditionKind kind)
    {
        for (var index = 0; index < ConditionKindComboBox.Items.Count; index++)
        {
            if (ConditionKindComboBox.Items[index] is ComboBoxItem { Tag: string tag } &&
                string.Equals(tag, kind.ToString(), StringComparison.Ordinal))
            {
                ConditionKindComboBox.SelectedIndex = index;
                return;
            }
        }

        ConditionKindComboBox.SelectedIndex = 0;
    }

    private void ObserveConditionCapture(ApplicationIdentity? identity, string? processName)
    {
        if (!_capturingConditionApplication)
        {
            return;
        }

        var normalized = ApplicationProfileResolver.NormalizeProcessName(
            identity?.ProcessName ?? processName);
        if (normalized.Length == 0 ||
            IsKeyPilotProcess(normalized) ||
            string.Equals(normalized, _conditionCaptureBaseline, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (identity is not { HasMatchKey: true })
        {
            if (IsApplicationFrameHost(normalized))
            {
                return;
            }

            identity = new ApplicationIdentity
            {
                ProcessName = normalized,
                DisplayName = DisplayProcessName(normalized)
            };
        }

        AddConditionApplication(identity);
        CancelConditionCapture($"已捕捉 {identity.EffectiveDisplayName}");
    }

    private void UpdateConditionCapture()
    {
        if (!_capturingConditionApplication)
        {
            _conditionCaptureTimer.Stop();
            return;
        }

        if (DateTimeOffset.UtcNow < _conditionCaptureDeadlineUtc)
        {
            return;
        }

        CancelConditionCapture("没有捕捉到其他软件");
    }

    private void CancelConditionCapture(string? message = null)
    {
        var wasCapturing = _capturingConditionApplication;
        _capturingConditionApplication = false;
        _conditionCaptureTimer.Stop();
        CaptureConditionAppButton.Content = "捕捉软件：切到目标窗口";
        if (wasCapturing)
        {
            UpdateConditionEditorVisibility();
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowToast(message);
            }
        }
    }

    private void MappingScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_capturingConditionApplication)
        {
            return;
        }

        if (ProfileSettingsDrawer.Visibility == Visibility.Visible)
        {
            CloseProfileSettingsDrawer();
        }
        else
        {
            CloseMappingDrawer();
        }
    }

    private void ReselectSource_Click(object sender, RoutedEventArgs e)
    {
        CloseMappingDrawer();
        ShowToast("请在面板中右键选择新的来源按键");
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        if (ProfileSettingsDrawer.Visibility == Visibility.Visible)
        {
            CloseProfileSettingsDrawer();
            e.Handled = true;
        }
        else if (MappingDrawer.Visibility == Visibility.Visible)
        {
            if (_capturingConditionApplication)
            {
                CancelConditionCapture("已取消捕捉软件");
            }
            else
            {
                CloseMappingDrawer();
            }

            e.Handled = true;
        }
    }

    private void CloseMappingDrawer()
    {
        if (_shortcutCaptureSession.State == ShortcutCaptureState.Armed)
        {
            FinishShortcutCapture(cancelled: true);
        }

        CancelConditionCapture();
        ConditionPickerPanel.Visibility = Visibility.Collapsed;
        ToggleConditionPickerButton.Content = "选择软件";
        var sourceButton = _drawerSourceButton;
        MappingDrawer.Visibility = Visibility.Collapsed;
        MappingScrim.Visibility = Visibility.Collapsed;
        _editingNode = null;
        _drawerSourceButton = null;
        if (sourceButton is not null)
        {
            DispatcherQueue.TryEnqueue(() => sourceButton.Focus(FocusState.Programmatic));
        }
    }

    private async void OpenProfileSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationLoadTask is { IsCompleted: false })
        {
            await _configurationLoadTask;
        }

        if (_isClosing)
        {
            return;
        }

        if (MappingDrawer.Visibility == Visibility.Visible)
        {
            CloseMappingDrawer();
        }

        CancelArmedHidCapture();
        CancelChordCapture();
        _editedStickMouseProfileId = null;
        _profileSettingsDraft.Clear();
        _profileSettingsDraft.AddRange(_configuration.Profiles.Select(profile => profile with
        {
            Mappings = profile.Mappings.ToList()
        }));

        _updatingProfileSettings = true;
        AutomaticProfileSwitchingToggle.IsOn = _configuration.IsAutomaticProfileSwitchingEnabled;
        PopulateManagedProfileChoices(_configuration.ActiveProfileId);
        RebuildApplicationBindingEditors(_configuration.ApplicationProfileBindings);
        _updatingProfileSettings = false;
        UpdateManagedProfileNameEditor();
        UpdateDetectedProcessEditor();
        MappingScrim.Visibility = Visibility.Visible;
        ProfileSettingsDrawer.Visibility = Visibility.Visible;
    }

    private void CloseProfileSettings_Click(object sender, RoutedEventArgs e) =>
        CloseProfileSettingsDrawer();

    private void CloseProfileSettingsDrawer()
    {
        if (_savingProfileSettings)
        {
            return;
        }

        ProfileSettingsDrawer.Visibility = Visibility.Collapsed;
        if (MappingDrawer.Visibility != Visibility.Visible)
        {
            MappingScrim.Visibility = Visibility.Collapsed;
        }

        _applicationBindingEditors.Clear();
        ApplicationBindingRowsPanel.Children.Clear();
        _profileSettingsDraft.Clear();
        _editedStickMouseProfileId = null;
    }

    private void PopulateManagedProfileChoices(Guid? selectedProfileId)
    {
        _updatingProfileSettings = true;
        ManagedProfileComboBox.Items.Clear();
        foreach (var profile in _profileSettingsDraft)
        {
            ManagedProfileComboBox.Items.Add(CreateProfileComboBoxItem(profile));
        }

        ManagedProfileComboBox.SelectedItem = ManagedProfileComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is Guid id && id == selectedProfileId);
        if (ManagedProfileComboBox.SelectedIndex < 0 && ManagedProfileComboBox.Items.Count > 0)
        {
            ManagedProfileComboBox.SelectedIndex = 0;
        }

        _updatingProfileSettings = false;
        UpdateManagedProfileNameEditor();
    }

    private static ComboBoxItem CreateProfileComboBoxItem(MappingProfile profile) => new()
    {
        Content = profile.Name,
        Tag = profile.Id
    };

    private Guid? SelectedManagedProfileId() =>
        ManagedProfileComboBox.SelectedItem is ComboBoxItem { Tag: Guid id }
            ? id
            : null;

    private void ManagedProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingProfileSettings)
        {
            CaptureStickMouseSettingsEditor();
            UpdateManagedProfileNameEditor();
        }
    }

    private void UpdateManagedProfileNameEditor()
    {
        var selectedId = SelectedManagedProfileId();
        ManagedProfileNameTextBox.Text = selectedId is Guid id
            ? _profileSettingsDraft.FirstOrDefault(profile => profile.Id == id)?.Name ?? string.Empty
            : string.Empty;
        LoadStickMouseSettingsEditor(selectedId);
    }

    private void LoadStickMouseSettingsEditor(Guid? profileId)
    {
        var previousUpdating = _updatingProfileSettings;
        _updatingProfileSettings = true;
        try
        {
            var settings = profileId is Guid id
                ? _profileSettingsDraft.FirstOrDefault(profile => profile.Id == id)?.StickMouse
                : null;
            settings ??= new StickMouseSettings();

            StickMouseToggle.IsOn = settings.IsEnabled;
            StickMouseSourceComboBox.SelectedIndex = settings.Source == StickMouseSource.Right ? 1 : 0;
            StickMouseSpeedNumberBox.Value = Math.Clamp(
                settings.SpeedPixelsPerSecond,
                StickMouseSettings.MinimumSpeedPixelsPerSecond,
                StickMouseSettings.MaximumSpeedPixelsPerSecond);
            StickMouseDeadzoneNumberBox.Value = Math.Clamp(
                settings.Deadzone,
                StickMouseSettings.MinimumDeadzone,
                StickMouseSettings.MaximumDeadzone) * 100d;
            _editedStickMouseProfileId = profileId;
            UpdateStickMouseSettingsEnabledState();
        }
        finally
        {
            _updatingProfileSettings = previousUpdating;
        }
    }

    private void CaptureStickMouseSettingsEditor()
    {
        if (_updatingProfileSettings || _editedStickMouseProfileId is not Guid profileId)
        {
            return;
        }

        var index = _profileSettingsDraft.FindIndex(profile => profile.Id == profileId);
        if (index < 0)
        {
            return;
        }

        var profile = _profileSettingsDraft[index];
        var settings = profile.StickMouse ?? new StickMouseSettings();
        var speedValue = double.IsFinite(StickMouseSpeedNumberBox.Value)
            ? StickMouseSpeedNumberBox.Value
            : StickMouseSettings.DefaultSpeedPixelsPerSecond;
        var deadzonePercent = double.IsFinite(StickMouseDeadzoneNumberBox.Value)
            ? StickMouseDeadzoneNumberBox.Value
            : StickMouseSettings.DefaultDeadzone * 100d;
        _profileSettingsDraft[index] = profile with
        {
            StickMouse = settings with
            {
                IsEnabled = StickMouseToggle.IsOn,
                Source = StickMouseSourceComboBox.SelectedIndex == 1
                    ? StickMouseSource.Right
                    : StickMouseSource.Left,
                SpeedPixelsPerSecond = (int)Math.Round(Math.Clamp(
                    speedValue,
                    StickMouseSettings.MinimumSpeedPixelsPerSecond,
                    StickMouseSettings.MaximumSpeedPixelsPerSecond)),
                Deadzone = Math.Clamp(
                    deadzonePercent / 100d,
                    StickMouseSettings.MinimumDeadzone,
                    StickMouseSettings.MaximumDeadzone)
            }
        };
    }

    private void StickMouseToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateStickMouseSettingsEnabledState();

    private void UpdateStickMouseSettingsEnabledState()
    {
        StickMouseSourceComboBox.IsEnabled = StickMouseToggle.IsOn;
        StickMouseSpeedNumberBox.IsEnabled = StickMouseToggle.IsOn;
        StickMouseDeadzoneNumberBox.IsEnabled = StickMouseToggle.IsOn;
        StickMouseSettingsPanel.Opacity = StickMouseToggle.IsOn ? 1 : 0.56;
    }

    private void RenameManagedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyManagedProfileName(out var error))
        {
            ShowToast(error, InfoBarSeverity.Warning);
            return;
        }

        ShowToast("方案名称已更新；点击“保存设置”后写入本地配置");
    }

    private bool TryApplyManagedProfileName(out string error)
    {
        error = string.Empty;
        if (SelectedManagedProfileId() is not Guid selectedId)
        {
            error = "请先选择一个方案。";
            return false;
        }

        var name = ManagedProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "方案名称不能为空。";
            return false;
        }

        if (_profileSettingsDraft.Any(profile =>
                profile.Id != selectedId &&
                string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"已经存在名为“{name}”的方案。";
            return false;
        }

        var index = _profileSettingsDraft.FindIndex(profile => profile.Id == selectedId);
        if (index < 0)
        {
            error = "所选方案已不存在。";
            return false;
        }

        _profileSettingsDraft[index] = _profileSettingsDraft[index] with { Name = name };
        if (ManagedProfileComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            selectedItem.Content = name;
        }

        foreach (var editor in _applicationBindingEditors)
        {
            var item = editor.ProfileComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => candidate.Tag is Guid id && id == selectedId);
            if (item is not null)
            {
                item.Content = name;
            }
        }

        return true;
    }

    private void DuplicateManagedProfile_Click(object sender, RoutedEventArgs e)
    {
        CaptureStickMouseSettingsEditor();
        if (!TryApplyManagedProfileName(out var error))
        {
            ShowToast(error, InfoBarSeverity.Warning);
            return;
        }

        if (SelectedManagedProfileId() is not Guid selectedId ||
            _profileSettingsDraft.FirstOrDefault(profile => profile.Id == selectedId) is not { } source)
        {
            ShowToast("请先选择要复制的方案。", InfoBarSeverity.Warning);
            return;
        }

        var copy = source with
        {
            Id = Guid.NewGuid(),
            Name = NextProfileCopyName(source.Name),
            Mappings = source.Mappings.ToList()
        };
        _profileSettingsDraft.Add(copy);
        foreach (var editor in _applicationBindingEditors)
        {
            editor.ProfileComboBox.Items.Add(CreateProfileComboBoxItem(copy));
        }

        PopulateManagedProfileChoices(copy.Id);
        ShowToast($"已创建“{copy.Name}”；可为它设置不同映射后再绑定软件");
    }

    private string NextProfileCopyName(string sourceName)
    {
        var baseName = $"{sourceName} 副本";
        var name = baseName;
        for (var suffix = 2; _profileSettingsDraft.Any(profile =>
                 string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            name = $"{baseName} {suffix}";
        }

        return name;
    }

    private async void ActivateManagedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedManagedProfileId() is not Guid selectedId)
        {
            ShowToast("请先选择要编辑的方案。", InfoBarSeverity.Warning);
            return;
        }

        if (!await SaveProfileSettingsDraftAsync(selectedId))
        {
            return;
        }

        CloseProfileSettingsDrawer();
        ShowToast($"已切换到“{_activeProfileName}”，现在可以编辑它的按键映射");
    }

    private void AddApplicationBinding_Click(object sender, RoutedEventArgs e) =>
        AddApplicationBindingEditor(null);

    private void AddDetectedProcess_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastExternalForegroundProcessName))
        {
            ShowToast("尚未检测到其他前台软件。", InfoBarSeverity.Warning);
            return;
        }

        var normalized = NormalizeProcessNameForComparison(_lastExternalForegroundProcessName);
        var existing = _applicationBindingEditors.FirstOrDefault(editor =>
            string.Equals(
                NormalizeProcessNameForComparison(editor.ProcessNameTextBox.Text),
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.ProcessNameTextBox.Focus(FocusState.Programmatic);
            ShowToast("这个软件已经在规则列表中。", InfoBarSeverity.Informational);
            return;
        }

        var profileId = SelectedManagedProfileId()
            ?? _configuration.ActiveProfileId
            ?? _profileSettingsDraft.First().Id;
        AddApplicationBindingEditor(new ApplicationProfileBinding
        {
            ProcessName = DisplayProcessName(_lastExternalForegroundProcessName),
            ProfileId = profileId
        });
        AutomaticProfileSwitchingToggle.IsOn = true;
    }

    private void AddApplicationBindingEditor(ApplicationProfileBinding? binding)
    {
        var root = new Grid
        {
            ColumnSpacing = 7,
            Margin = new Thickness(0, 2, 0, 2)
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var processName = new TextBox
        {
            PlaceholderText = "例如 chrome.exe",
            Text = binding?.ProcessName ?? string.Empty
        };
        var arrow = new TextBlock
        {
            Text = "→",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushResource("KpMutedBrush")
        };
        Grid.SetColumn(arrow, 1);
        var profile = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var candidate in _profileSettingsDraft)
        {
            profile.Items.Add(CreateProfileComboBoxItem(candidate));
        }

        profile.SelectedItem = profile.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is Guid id && id == binding?.ProfileId);
        if (profile.SelectedIndex < 0 && profile.Items.Count > 0)
        {
            profile.SelectedIndex = 0;
        }
        Grid.SetColumn(profile, 2);

        var remove = new Button
        {
            Content = "删除",
            Padding = new Thickness(9, 5, 9, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(remove, 3);
        root.Children.Add(processName);
        root.Children.Add(arrow);
        root.Children.Add(profile);
        root.Children.Add(remove);

        var editor = new ApplicationBindingEditor(root, processName, profile);
        remove.Click += (_, _) => RemoveApplicationBindingEditor(editor);
        _applicationBindingEditors.Add(editor);
        ApplicationBindingRowsPanel.Children.Add(root);
        UpdateApplicationBindingsEmptyState();
        processName.Focus(FocusState.Programmatic);
    }

    private void RemoveApplicationBindingEditor(ApplicationBindingEditor editor)
    {
        _applicationBindingEditors.Remove(editor);
        ApplicationBindingRowsPanel.Children.Remove(editor.Root);
        UpdateApplicationBindingsEmptyState();
    }

    private void RebuildApplicationBindingEditors(
        IEnumerable<ApplicationProfileBinding> bindings)
    {
        _applicationBindingEditors.Clear();
        ApplicationBindingRowsPanel.Children.Clear();
        foreach (var binding in bindings)
        {
            AddApplicationBindingEditor(binding);
        }

        UpdateApplicationBindingsEmptyState();
    }

    private void UpdateApplicationBindingsEmptyState() =>
        ApplicationBindingsEmptyText.Visibility = _applicationBindingEditors.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateDetectedProcessEditor()
    {
        var identity = TryGetSelectableForegroundIdentity();
        var hasProcess = identity is not null;
        DetectedProcessNameText.Text = hasProcess
            ? identity!.EffectiveDisplayName
            : "尚未检测到其他前台软件";
        AddDetectedProcessButton.IsEnabled = hasProcess;
        RebuildProfileRecentApplications(identity);
    }

    private void RebuildProfileRecentApplications(ApplicationIdentity? current)
    {
        RecentForegroundAppsPanel.Children.Clear();
        var recent = VisibleRecentApplications()
            .Where(application => current is null || !application.Matches(current))
            .Take(6)
            .ToList();
        if (recent.Count == 0)
        {
            return;
        }

        RecentForegroundAppsPanel.Children.Add(new TextBlock
        {
            Text = "历史前台",
            FontSize = 8,
            Foreground = BrushResource("KpMutedBrush")
        });
        foreach (var application in recent)
        {
            RecentForegroundAppsPanel.Children.Add(
                CreateApplicationChoiceRow(application, "添加到规则", () =>
                    AddApplicationBindingEditor(new ApplicationProfileBinding
                    {
                        ProcessName = DisplayProcessName(application.ProcessName),
                        ProfileId = SelectedManagedProfileId()
                            ?? _configuration.ActiveProfileId
                            ?? _profileSettingsDraft.First().Id
                    })));
        }
    }

    private async void SaveProfileSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveProfileSettingsDraftAsync())
        {
            return;
        }

        CloseProfileSettingsDrawer();
        ShowToast("应用对应规则已保存，前台软件变化时会自动切换方案");
    }

    private async Task<bool> SaveProfileSettingsDraftAsync(Guid? activeProfileIdOverride = null)
    {
        if (_savingProfileSettings)
        {
            return false;
        }

        _savingProfileSettings = true;
        SetProfileSettingsBusy(isBusy: true);
        try
        {
            CaptureStickMouseSettingsEditor();
            if (!TryApplyManagedProfileName(out var profileError))
            {
                ShowToast(profileError, InfoBarSeverity.Warning);
                return false;
            }

            if (!TryCaptureApplicationBindings(out var bindings, out var bindingError))
            {
                ShowToast(bindingError, InfoBarSeverity.Warning);
                return false;
            }

            _configuration = _configuration with
            {
                Profiles = _profileSettingsDraft.ToList(),
                IsAutomaticProfileSwitchingEnabled = AutomaticProfileSwitchingToggle.IsOn,
                ApplicationProfileBindings = bindings
            };
            var saveResult = await PersistWorkspaceConfigurationAsync(activeProfileIdOverride);
            if (!IsCurrentSaveResult(saveResult) ||
                saveResult.Outcome != ConfigurationSaveOutcome.Succeeded)
            {
                return false;
            }

            RestoreWorkspaceFromConfiguration();
            return true;
        }
        finally
        {
            _savingProfileSettings = false;
            SetProfileSettingsBusy(isBusy: false);
        }
    }

    private void SetProfileSettingsBusy(bool isBusy)
    {
        SaveProfileSettingsButton.Content = isBusy ? "正在保存…" : "保存设置";
        ProfileSettingsDrawer.IsHitTestVisible = !isBusy;
        ProfileSettingsDrawer.Opacity = isBusy ? 0.72 : 1;
    }

    private bool TryCaptureApplicationBindings(
        out List<ApplicationProfileBinding> bindings,
        out string error)
    {
        bindings = [];
        error = string.Empty;
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in _applicationBindingEditors)
        {
            var processName = editor.ProcessNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(processName))
            {
                error = "每条规则都必须填写进程名称。";
                return false;
            }

            var normalized = NormalizeProcessNameForComparison(processName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = $"无法识别进程名称“{processName}”。";
                return false;
            }

            if (!processNames.Add(normalized))
            {
                error = $"进程“{DisplayProcessName(processName)}”设置了重复规则。";
                return false;
            }

            if (editor.ProfileComboBox.SelectedItem is not ComboBoxItem { Tag: Guid profileId })
            {
                error = $"请为“{DisplayProcessName(processName)}”选择一个方案。";
                return false;
            }

            bindings.Add(new ApplicationProfileBinding
            {
                ProcessName = processName,
                ProfileId = profileId
            });
        }

        return true;
    }

    private static string NormalizeProcessNameForComparison(string processName)
    {
        try
        {
            return ApplicationProfileResolver.NormalizeProcessName(processName);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private void RestoreWorkspaceFromConfiguration()
    {
        _workspaceRestore = WorkspaceConfigurationAdapter.Restore(
            _configuration,
            WorkspaceNodes());
        _activeProfileName = _workspaceRestore.ActiveProfileName;
        ProfileNameText.Text = _activeProfileName;
        if (_workspacePage is WorkspacePage.Capture or WorkspacePage.Mapping)
        {
            SetMode(_mappingMode);
        }
        RenderAllNodes();
        UpdateMappingCount();
        if (_selectedNode is not null)
        {
            SelectNode(_selectedNode, updateCaptureStatus: false);
        }

        RefreshEffectiveRuntimeConfiguration(force: true);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MappingDrawer.Width = Math.Min(450, Math.Max(1, e.NewSize.Width * 0.95));
        ProfileSettingsDrawer.Width = Math.Min(520, Math.Max(1, e.NewSize.Width * 0.95));

        var stackLowerCards = e.NewSize.Width < 1120;
        Grid.SetColumn(SpecialCard, stackLowerCards ? 0 : 1);
        Grid.SetRow(SpecialCard, stackLowerCards ? 1 : 0);
        Grid.SetColumnSpan(SpecialCard, 1);
        SpecialCard.Margin = new Thickness(0);
        Grid.SetColumn(RemoteCard, 0);
        Grid.SetRow(RemoteCard, stackLowerCards ? 2 : 1);
        Grid.SetColumnSpan(RemoteCard, stackLowerCards ? 1 : 2);
        Grid.SetColumn(MicCard, 0);
        Grid.SetRow(MicCard, stackLowerCards ? 3 : 2);
        Grid.SetColumnSpan(MicCard, stackLowerCards ? 1 : 2);
        Grid.SetColumn(MouseCard, 0);
        Grid.SetRow(MouseCard, stackLowerCards ? 4 : 3);
        Grid.SetColumnSpan(MouseCard, stackLowerCards ? 1 : 2);
        LowerGrid.ColumnDefinitions[0].Width = stackLowerCards
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0.78, GridUnitType.Star);
        LowerGrid.ColumnDefinitions[1].Width = stackLowerCards
            ? new GridLength(0)
            : new GridLength(1.22, GridUnitType.Star);

        SidebarColumn.Width = e.NewSize.Width < 760 ? new GridLength(0) : new GridLength(212);
        CompactNavBar.Visibility = e.NewSize.Width < 760 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var visual in _nodeVisuals.Values)
        {
            visual.Button.ContextFlyout?.Hide();
        }
    }

    private bool TryValidateAction(string actionType, string value, out string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "请填写要执行的动作";
            return false;
        }

        switch (actionType)
        {
            case "快捷键":
                if (!WorkspaceConfigurationAdapter.TryCreateAction(
                    WorkspaceConfigurationAdapter.ShortcutActionType,
                    value,
                    WorkspaceNodes(),
                    out _,
                    out var shortcutError))
                {
                    message = shortcutError;
                    return false;
                }
                break;

            case "网址":
                if (!ExternalUriNormalizer.TryNormalize(value, out var normalizedUri, out var uriError))
                {
                    message = $"请输入域名或安全的已注册协议地址：{uriError}";
                    return false;
                }
                try
                {
                    new WindowsExternalActionBackend().ValidateUri(new OpenUriOperation(normalizedUri));
                }
                catch (Exception exception) when (exception is ArgumentException
                                                        or NotSupportedException
                                                        or InvalidOperationException)
                {
                    message = $"该自定义协议尚未在 Windows 注册：{exception.Message}";
                    return false;
                }
                break;

            case "应用 / 文件":
                var expandedPath = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                if (!System.IO.Path.IsPathFullyQualified(expandedPath) ||
                    !File.Exists(expandedPath))
                {
                    message = "请选择当前设备上真实存在的应用或文件";
                    return false;
                }
                if (new[] { ".bat", ".cmd", ".ps1" }.Contains(
                        System.IO.Path.GetExtension(expandedPath),
                        StringComparer.OrdinalIgnoreCase))
                {
                    message = "BAT、CMD 和 PowerShell 文件请使用“脚本 / 命令”动作。";
                    return false;
                }
                break;

            case "脚本 / 命令":
                var scriptPath = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                var allowedExtensions = new[] { ".bat", ".cmd", ".ps1", ".exe" };
                if (!System.IO.Path.IsPathFullyQualified(scriptPath) ||
                    !File.Exists(scriptPath) ||
                    !allowedExtensions.Contains(System.IO.Path.GetExtension(scriptPath), StringComparer.OrdinalIgnoreCase))
                {
                    message = "脚本必须是完整路径、真实存在，且类型为 .bat、.cmd、.ps1 或 .exe。";
                    return false;
                }
                break;

            case "按键 / 组合键":
            case "特殊按键":
            case "媒体控制":
            case "音量控制":
                if (!WorkspaceConfigurationAdapter.TryCreateAction(
                        actionType,
                        value,
                        WorkspaceNodes(),
                        out _,
                        out var typedActionError))
                {
                    message = typedActionError;
                    return false;
                }
                break;

            case "手柄按键":
                if (!WorkspaceConfigurationAdapter.TryCreateAction(
                        actionType,
                        value,
                        WorkspaceNodes(),
                        out _,
                        out var gamepadActionError))
                {
                    message = gamepadActionError;
                    return false;
                }
                break;
        }

        message = string.Empty;
        return true;
    }

    private static string[] ParseShortcutTokens(string value)
    {
        var normalized = value.Trim();
        var endsWithPlusKey = Regex.IsMatch(normalized, @"\+\s*\+$");
        if (endsWithPlusKey)
        {
            normalized = Regex.Replace(normalized, @"\s*\+\s*\+$", string.Empty);
        }

        var tokens = string.IsNullOrWhiteSpace(normalized)
            ? new List<string>()
            : Regex.Split(normalized, @"\s*\+\s*")
                .Select(token => token.Trim())
                .ToList();
        if (endsWithPlusKey)
        {
            tokens.Add("+");
        }

        return tokens.ToArray();
    }

    private IEnumerable<InputNodeState> FindSpecialTargets(string value) =>
        _nodeVisuals.Values
            .Select(visual => visual.State)
            .Where(state =>
                state.Kind == InputNodeKind.Special &&
                !string.IsNullOrWhiteSpace(state.CaptureIdentity) &&
                (string.Equals(state.Label, value, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state.Id, value, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state.FriendlyName, value, StringComparison.OrdinalIgnoreCase)));

    private IEnumerable<InputNodeState> FindGamepadTargets(string value)
    {
        var requested = value.StartsWith("Gamepad ", StringComparison.OrdinalIgnoreCase)
            ? value[8..].Trim()
            : value.Trim();
        return _nodeVisuals.Values
            .Select(visual => visual.State)
            .Where(state =>
                state.Kind == InputNodeKind.Gamepad &&
                (string.Equals(state.Id, requested, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state.Label, requested, StringComparison.OrdinalIgnoreCase)));
    }

    private InputNodeState? ResolveActionTarget(string actionType, string value) => actionType switch
    {
        "按键 / 组合键" or "特殊按键" => FindSpecialTargets(value).SingleOrDefault(),
        "手柄按键" => FindGamepadTargets(value).SingleOrDefault(),
        _ => null
    };

    private static bool IsSupportedShortcutToken(string token)
    {
        if (token.Length == 1)
        {
            return !char.IsWhiteSpace(token[0]);
        }

        return Regex.IsMatch(
            token,
            @"^(Ctrl|Control|Alt|Shift|Win|Windows|Meta|Esc|Escape|Tab|Enter|Return|Space|Backspace|Delete|Insert|Home|End|PageUp|PageDown|Up|Down|Left|Right|CapsLock|NumLock|ScrollLock|PrintScreen|Pause|Plus|Minus|Equals|F(?:[1-9]|1[0-9]|2[0-4])|Numpad[0-9])$",
            RegexOptions.IgnoreCase);
    }

    private static InputSource BuildKeyboardSource(RawKeyboardEvent input)
    {
        var prefix = input.Flags & 0x0006;
        var hasDevicePath = !string.IsNullOrWhiteSpace(input.DevicePath);
        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = hasDevicePath ? DeviceMatchMode.ExactDevice : DeviceMatchMode.AnyOfKind,
                DeviceId = hasDevicePath ? input.DevicePath : null
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = input.MakeCode,
                IsExtended = prefix != 0,
                RawQualifier = $"RAWKEYBOARD-V1;PREFIX={prefix:X4}"
            }
        };
    }

    private static InputSource BuildDefaultKeyboardSource(string stableId)
    {
        if (!KeyboardKeyResolver.TryGetSet1Code(stableId, out var code))
        {
            throw new InvalidOperationException($"键盘布局包含未知的 Set-1 按键：{stableId}");
        }

        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = code.MakeCode,
                IsExtended = code.Prefix != 0,
                RawQualifier = $"RAWKEYBOARD-V1;PREFIX={code.Prefix:X4}"
            }
        };
    }

    private static InputSource BuildGamepadButtonSource(int code) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadButton,
            Code = code
        }
    };

    private static InputSource BuildDefaultXInputSource(string stableId)
    {
        if (!Enum.TryParse<XInputButton>(stableId, ignoreCase: false, out var button))
        {
            throw new InvalidOperationException($"手柄布局包含未知的 XInput 按键：{stableId}");
        }

        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Gamepad,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.GamepadButton,
                Code = (ushort)button
            }
        };
    }

    private static InputSource BuildDefaultMouseSource(ushort virtualKey) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Mouse,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.VirtualKey,
            Code = virtualKey
        }
    };

    private static InputSource BuildMouseSource(RawMouseEvent input)
    {
        var hasPath = !string.IsNullOrWhiteSpace(input.DevicePath);
        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Mouse,
                MatchMode = hasPath ? DeviceMatchMode.ExactDevice : DeviceMatchMode.AnyOfKind,
                DeviceId = hasPath ? input.DevicePath : null
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.VirtualKey,
                Code = input.VirtualKey
            }
        };
    }

    private static bool TryGetMouseButtonId(ushort virtualKey, out string buttonId)
    {
        buttonId = virtualKey switch
        {
            0x04 => "Middle",
            0x05 => "XButton1",
            0x06 => "XButton2",
            _ => string.Empty
        };
        return buttonId.Length > 0;
    }

    private static InputSource BuildDefaultXInputVirtualSource(XInputVirtualControl control) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadAxisDirection,
            Code = (int)control
        }
    };

    private static InputSource BuildDefaultXInputRotationSource(GamepadRotationControl control) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadRotation,
            Code = (int)control
        }
    };

    private static InputSource BuildXInputSource(XInputButtonChangedEventArgs input) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.ExactDevice,
            DeviceId = $"XINPUT:USER:{input.UserIndex}"
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadButton,
            Code = (ushort)input.Button
        }
    };

    private static InputSource BuildXInputSource(XInputVirtualControlChangedEventArgs input) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.ExactDevice,
            DeviceId = $"XINPUT:USER:{input.UserIndex}"
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadAxisDirection,
            Code = (int)input.Control
        }
    };

    private static InputSource BuildXInputRotationSource(
        int userIndex,
        GamepadRotationControl control) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.ExactDevice,
            DeviceId = $"XINPUT:USER:{userIndex}"
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadRotation,
            Code = (int)control
        }
    };

    private void ApplyCompatibilityConfiguration(KeyPilotConfiguration configuration)
    {
        var profile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= configuration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled);
        var sources = configuration.IsMappingEnabled && profile?.IsEnabled == true
            ? profile.Mappings
                .Where(mapping => mapping is { IsEnabled: true, SuppressOriginal: true })
                .Select(mapping => mapping.Source)
            : Array.Empty<InputSource>();
        _compatibilitySuppression.ApplySources(
            sources,
            Volatile.Read(ref _runtimeRevision));
        _compatibilitySuppression.SetEnabled(configuration.IsMappingEnabled && !_driverSuppressionActive);
    }

    private void CompatibilitySuppression_InputReceived(
        object? sender,
        CompatibilityKeyboardInputEventArgs args)
    {
        if (_isClosing ||
            !_runtimeConfiguration.IsMappingEnabled ||
            args.RuntimeRevision != Volatile.Read(ref _runtimeRevision) ||
            !IsObservedForegroundContextCurrent())
        {
            return;
        }

        var inputEvent = new InputEvent
        {
            Source = args.Source,
            Phase = args.Phase,
            TimestampUtc = args.TimestampUtc,
            SequenceNumber = Interlocked.Increment(ref _inputSequence)
        };
        var tracking = _mappingExecution.SubmitTracked(inputEvent, args.RuntimeRevision);
        args.Accept(
            CompatibilityInputCommittedAsync(tracking.Committed),
            CompatibilityInputCompletedAsync(tracking.Completion));
        DispatcherQueue.TryEnqueue(() =>
            RecordInputProcess(inputEvent, null, originalWasSuppressed: true));
    }

    private static async Task<bool> CompatibilityInputCommittedAsync(Task<MappingInputCommit> commitTask)
    {
        try
        {
            return (await commitTask.ConfigureAwait(false)).Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CompatibilityInputCompletedAsync(
        Task<MappingInputCompletion> completionTask)
    {
        try
        {
            return (await completionTask.ConfigureAwait(false)).Success;
        }
        catch
        {
            return false;
        }
    }

    private void CompatibilitySuppression_StatusChanged(
        object? sender,
        CompatibilityKeyboardSuppressionStatus status)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing)
            {
                return;
            }

            UpdateMasterStatus(status);
            if (status.State == CompatibilityKeyboardSuppressionState.Failed &&
                _configuration.IsMappingEnabled && !_driverSuppressionActive)
            {
                _ = DisableMappingsAfterCompatibilityFailureAsync(status.Message);
            }
        });
    }

    private async Task DisableMappingsAfterCompatibilityFailureAsync(string reason)
    {
        if (_isClosing || _driverSuppressionActive || !_configuration.IsMappingEnabled)
        {
            return;
        }

        _configuration = _configuration with { IsMappingEnabled = false };
        _updatingMasterToggle = true;
        MasterToggle.IsOn = false;
        _updatingMasterToggle = false;
        // Stop every runtime immediately; disk I/O must never reopen an unsafe session.
        RefreshEffectiveRuntimeConfiguration(force: true);
        var saveResult = await PersistWorkspaceConfigurationAsync();
        if (!IsCurrentSaveResult(saveResult))
        {
            return;
        }

        ReportError(saveResult.Outcome == ConfigurationSaveOutcome.Succeeded
            ? $"{reason} 全局映射已安全关闭，避免原按键与宏同时触发。"
            : $"{reason} 本次运行已强制关闭全局映射，但磁盘配置未能更新；修复配置保存错误前请勿重启后直接使用映射。");
        UpdateMasterStatus();
    }

    private void UpdateMasterStatus(CompatibilityKeyboardSuppressionStatus? compatibilityStatus = null)
    {
        if (!_configuration.IsMappingEnabled)
        {
            MasterStateText.Text = "已关闭 · 不执行映射";
            MasterStateText.Foreground = BrushResource("KpDimBrush");
            return;
        }

        if (_driverSuppressionActive)
        {
            MasterStateText.Text = "已启用 · 内核抑制";
            MasterStateText.Foreground = BrushResource("KpMintBrush");
            return;
        }

        if (_compatibilitySuppression.IsEnabled)
        {
            MasterStateText.Text = "已启用 · 兼容抑制（仅键盘）";
            MasterStateText.Foreground = BrushResource("KpAmberBrush");
            return;
        }

        MasterStateText.Text = compatibilityStatus?.Message ?? "正在准备兼容抑制；当前 fail-open";
        MasterStateText.Foreground = compatibilityStatus?.State == CompatibilityKeyboardSuppressionState.Failed
            ? BrushResource("KpRedBrush")
            : BrushResource("KpAmberBrush");
    }

    private static string FormatOpaqueHidCode(
        RawHidDeviceDescriptor device,
        UnknownHidReportChange change) =>
        $"VID_{device.VendorId:X4}&PID_{device.ProductId:X4} · UP {device.UsagePage:X4}/{device.Usage:X4} · {FormatHidDelta(change)}";

    private static string FormatHidDelta(UnknownHidReportChange change)
    {
        const int shownChangeCount = 4;
        var shown = change.ByteChanges
            .Take(shownChangeCount)
            .Select(item => $"{item.Offset:X2}:{item.PreviousValue:X2}→{item.CurrentValue:X2}");
        var suffix = change.ByteChanges.Count > shownChangeCount
            ? $" +{change.ByteChanges.Count - shownChangeCount}"
            : string.Empty;
        return $"Δ {string.Join(' ', shown)}{suffix}";
    }

    private void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        ToastInfoBar.Message = message;
        ToastInfoBar.Severity = severity;
        ToastInfoBar.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateInputServiceStatus()
    {
        var statuses = new[] { _rawInputStatus, _xInputStatus };
        ServiceStatusText.Text = string.Join(" · ", statuses);

        if (_rawInputHealth == InputBackendHealth.Failed ||
            _xInputHealth == InputBackendHealth.Failed)
        {
            ServiceDot.Fill = BrushResource("KpRedBrush");
            FooterServiceText.Text = "输入后端部分异常 · 详见 runtime.log";
            return;
        }

        if (_rawInputHealth == InputBackendHealth.Healthy &&
            _xInputHealth == InputBackendHealth.Healthy)
        {
            ServiceDot.Fill = BrushResource("KpMintBrush");
            FooterServiceText.Text = "键盘、HID 与手柄只读采集中";
            return;
        }

        ServiceDot.Fill = BrushResource("KpAmberBrush");
        FooterServiceText.Text = "输入后端正在初始化";
    }

    private void ReportError(string message, string component = "Runtime", Exception? exception = null)
    {
        _errorCount++;
        ErrorCountText.Text = _errorCount.ToString();
        RuntimeDiagnostics.Write(component, message, exception);
        ShowToast(message, InfoBarSeverity.Error);
    }

    private void HandleRc003Button(RawKeyboardEvent input, string buttonId)
    {
        var source = Rc003DeviceIdentity.CreateLiveSource(buttonId, input.DevicePath);
        var observed = _ingest.Observe(source, input.IsKeyDown, input.Timestamp, "RC003");
        RecordInputProcess(observed, buttonId, originalWasSuppressed: false);
        if (!_rc003HidConnected)
        {
            _rc003HidConnected = true;
            _ = EnsureRc003VoiceRunningAsync();
            _ = EnsureRc003BatteryRunningAsync();
            RefreshConnectedMicrophones();
        }

        UpdateRemoteBackendStatus();
        if (!_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
        {
            return;
        }

        if (!_nodeVisuals.TryGetValue($"remote:{buttonId}", out var visual))
        {
            return;
        }

        visual.State.DevicePath = input.DevicePath;
        visual.State.CapturedSource = source;
        visual.State.CaptureIdentity = source.CanonicalKey;
        visual.State.Location = "RC003 HID";
        SelectNode(visual.State, updateCaptureStatus: true);
        RecentSourceText.Text = "遥控器 · RC003";
        RecentVirtualKeyText.Text = $"VK 0x{input.VirtualKey:X2}";
        RecentCodeText.Text = visual.State.RawCode;
        RecentLocationText.Text = visual.State.Location;
        RecentStateText.Text = input.IsKeyDown ? "REMOTE DOWN" : "REMOTE UP";
        LastOperationText.Text = $"检测：{visual.State.FriendlyName}";
        if (input.IsKeyDown)
        {
            _ = PulseNodeAsync(visual.State);
        }
    }

    private bool HandleSonyHidReport(
        RawHidDeviceDescriptor device,
        byte[] report,
        DateTimeOffset timestamp,
        bool canPresent)
    {
        if (!SonyHidIdentity.IsSonyPad(device.VendorId, device.ProductId))
        {
            return false;
        }

        if (!SonyHidReportParser.TryParse(device.ProductId, report, out var state))
        {
            return false;
        }

        var firstSight = !_sonyPadStates.ContainsKey(device.DevicePath);
        _sonyPadStates.TryGetValue(device.DevicePath, out var previous);
        var batteryChanged = state.BatteryPercent != previous.BatteryPercent ||
            state.Charging != previous.Charging;
        _sonyPadStates[device.DevicePath] = state;
        _sonyStatusName = SonyHidIdentity.DisplayName(device.ProductId);
        GamepadSubtitleText.Text = $"{_sonyStatusName} HID · 只读采集";
        if (firstSight || batteryChanged)
        {
            UpdateGamepadBackendStatus();
        }
        if (firstSight)
        {
            RefreshConnectedMicrophones();
        }

        foreach (var transition in SonyHidReportParser.ButtonTransitions(previous.Buttons, state.Buttons))
        {
            var source = new InputSource
            {
                Device = new InputDeviceSelector
                {
                    Kind = InputDeviceKind.Gamepad,
                    MatchMode = DeviceMatchMode.ExactDevice,
                    DeviceId = device.DevicePath,
                    VendorId = (ushort)device.VendorId,
                    ProductId = (ushort)device.ProductId
                },
                Control = new InputControlId
                {
                    Kind = InputControlKind.GamepadButton,
                    Code = (ushort)transition.Button
                }
            };
            var observed = _ingest.Observe(source, transition.IsPressed, timestamp, "DualShock HID");
            RecordInputProcess(observed, GamepadNodeId((ushort)transition.Button), originalWasSuppressed: false);
            if (!canPresent || !_captureEnabled || _suspendWorkspaceCapture || IsAnyDrawerOpen)
            {
                continue;
            }

            var nodeId = GamepadNodeId((ushort)transition.Button);
            if (!_nodeVisuals.TryGetValue($"gamepad:{nodeId}", out var visual))
            {
                continue;
            }

            visual.State.DevicePath = device.DevicePath;
            visual.State.Location = SonyHidIdentity.DisplayName(device.ProductId);
            SelectNode(visual.State, updateCaptureStatus: true);
            RecentSourceText.Text = visual.State.Location + " · HID";
            RecentVirtualKeyText.Text = visual.State.Label;
            RecentCodeText.Text = $"0x{(ushort)transition.Button:X4}";
            RecentStateText.Text = transition.IsPressed ? "BUTTON DOWN" : "BUTTON UP";
            LastOperationText.Text = $"检测：{visual.State.Label}";
            if (transition.IsPressed)
            {
                _ = PulseNodeAsync(visual.State);
            }
        }

        return true;
    }

    private static string GamepadNodeId(ushort button) => button switch
    {
        SonyHidIdentity.PsButton => "PS",
        SonyHidIdentity.TouchpadClick => "Touchpad",
        _ => ((XInputButton)button).ToString()
    };

    private void WarnSuppressionUnavailable(InputSource source, string backend)
    {
        if (backend.StartsWith("键盘", StringComparison.Ordinal) &&
            (_compatibilitySuppression.IsEnabled || _driverSuppressionActive))
        {
            return;
        }

        var warningKey = $"{backend}/{source.CanonicalKey}";
        if (!_suppressionUnavailableWarnings.Add(warningKey))
        {
            return;
        }

        var message = $"{backend} 当前不能屏蔽这个原始输入；需要屏蔽原键的映射已拒绝执行。请关闭“屏蔽原按键”后再使用。";
        RuntimeDiagnostics.Write("Suppression", message);
        ShowToast(message, InfoBarSeverity.Warning);
    }

    private void DriverSuppression_InputReceived(
        object? sender,
        DriverSuppressionInputEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        if (args.RuntimeRevision != Volatile.Read(ref _runtimeRevision) ||
            !IsObservedForegroundContextCurrent())
        {
            var exception = new InvalidOperationException(
                "The suppressed driver input belongs to an obsolete foreground context.");
            _ = ReportDriverInputCommitAsync(
                args,
                Task.FromResult(new MappingInputCommit(false, 0, exception)));
            _ = ReportDriverInputCompletionAsync(
                args,
                Task.FromResult(new MappingInputCompletion(false, 0, exception)));
            return;
        }

        var inputEvent = args.InputEvent with
        {
            SequenceNumber = Interlocked.Increment(ref _inputSequence)
        };
        var tracking = _mappingExecution.SubmitTracked(inputEvent, args.RuntimeRevision);
        _ = ReportDriverInputCommitAsync(args, tracking.Committed);
        _ = ReportDriverInputCompletionAsync(args, tracking.Completion);
        DispatcherQueue.TryEnqueue(() =>
            RecordInputProcess(inputEvent, null, originalWasSuppressed: true));
    }

    private async Task ReportDriverInputCommitAsync(
        DriverSuppressionInputEventArgs input,
        Task<MappingInputCommit> commitTask)
    {
        MappingInputCommit commit;
        try
        {
            commit = await commitTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            commit = new MappingInputCommit(false, 0, exception);
        }

        _driverSuppression.ReportInputCommitted(
            input.ConnectionEpoch,
            input.DriverSequence,
            commit,
            input.PolicyRevision);
    }

    private async Task ReportDriverInputCompletionAsync(
        DriverSuppressionInputEventArgs input,
        Task<MappingInputCompletion> completionTask)
    {
        MappingInputCompletion completion;
        try
        {
            completion = await completionTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion = new MappingInputCompletion(false, 0, exception);
        }

        _driverSuppression.ReportInputCompletion(
            input.ConnectionEpoch,
            input.DriverSequence,
            completion,
            input.PolicyRevision);
    }

    private void DriverSuppression_StatusChanged(
        object? sender,
        DriverSuppressionStatus status)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing)
            {
                return;
            }

            _driverSuppressionActive = status.State == DriverSuppressionState.Active;
            ApplyCompatibilityConfiguration(_runtimeConfiguration);
            UpdateMasterStatus();
            if (status.State == DriverSuppressionState.FailOpen)
            {
                RuntimeDiagnostics.Write("DriverSuppression", status.Message);
            }
        });
    }

    private void MappingExecution_Faulted(
        object? sender,
        MappingExecutionFaultEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing)
            {
                return;
            }

            var mappingName = string.IsNullOrWhiteSpace(args.Mapping?.Name)
                ? "映射动作"
                : $"映射“{args.Mapping.Name}”";
            var stage = args.Stage switch
            {
                MappingExecutionStage.InputRecognition => "输入识别",
                MappingExecutionStage.ActionPlanning => "动作规划",
                _ => "动作执行"
            };
            var message = $"{mappingName}{stage}失败：{args.Exception.Message}";
            if (args.Stage == MappingExecutionStage.InputRecognition)
            {
                ReportError(message);
                return;
            }

            _errorCount++;
            ErrorCountText.Text = _errorCount.ToString();
            LastOperationText.Text = message;
            RuntimeDiagnostics.Write("Mapping", message, args.Exception);
            ShowToast(message, InfoBarSeverity.Error);
        });
    }

    private void ResizeToUsefulBounds()
    {
        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            var dpi = GetDpiForWindow(windowHandle);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var scale = dpi / 96.0;
            var width = (int)Math.Min(Math.Round(1280 * scale), work.Width * 0.94);
            var height = (int)Math.Min(Math.Round(860 * scale), work.Height * 0.94);
            width = Math.Max(width, (int)Math.Round(960 * scale));
            height = Math.Max(height, (int)Math.Round(640 * scale));
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
            var x = work.X + Math.Max(0, (work.Width - width) / 2);
            var y = work.Y + Math.Max(0, (work.Height - height) / 2);
            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            // Window sizing is cosmetic; input capture can still initialize after activation.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static string CompactDevicePath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return "设备路径待解析";
        }

        var match = Regex.Match(devicePath, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : devicePath;
    }

    private static string KeyboardLocation(string id)
    {
        if (id.EndsWith("Left", StringComparison.Ordinal))
        {
            return "Left";
        }

        if (id.EndsWith("Right", StringComparison.Ordinal))
        {
            return "Right";
        }

        return id.StartsWith("Numpad", StringComparison.Ordinal) ? "Numpad" : "Standard";
    }

    private static string FriendlyKeyboardName(string id, string label) => id switch
    {
        "Space" => "空格键",
        "Escape" => "Esc",
        "Tab" => "Tab 键",
        "Enter" => "回车键",
        "ControlLeft" => "左 Ctrl",
        "ControlRight" => "右 Ctrl",
        "AltLeft" => "左 Alt",
        "AltRight" => "右 Alt",
        "ShiftLeft" => "左 Shift",
        "ShiftRight" => "右 Shift",
        "MetaLeft" => "左 Win",
        "MetaRight" => "右 Win",
        "PageDown" => "Page Down",
        "PageUp" => "Page Up",
        _ when id.StartsWith("Key", StringComparison.Ordinal) => $"字母 {id[3..]}",
        _ when id.StartsWith("Digit", StringComparison.Ordinal) => $"数字 {id[5..]}",
        _ => label
    };

    private sealed record InputNodeVisual(
        InputNodeState State,
        Button Button,
        TextBlock LabelText,
        TextBlock TargetText,
        Ellipse MappingDot,
        TextBlock? SecondaryText);

    private enum InputBackendHealth
    {
        Pending,
        Healthy,
        Failed
    }
}
