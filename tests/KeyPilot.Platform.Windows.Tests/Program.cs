using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;
using KeyPilot.Platform.Windows.Storage;

namespace KeyPilot.Platform.Windows.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "KeyPilot.Platform.Windows.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            await RunAsync("缺失配置返回默认方案", () => MissingConfigurationReturnsDefaultAsync(testRoot));
            await RunAsync("保存后可以完整读回", () => ConfigurationRoundTripsAsync(testRoot));
            await RunAsync("无效保存保留原文件并报告原因", () => InvalidSavePreservesFileAndReportsReasonAsync(testRoot));
            await RunAsync("替换配置保留上一份有效备份", () => ConfigurationReplacementKeepsBackupAsync(testRoot));
            await RunAsync("较旧保存不能覆盖最后一次关闭", LatestSaveCompletionCannotDisplaceNewerRequestAsync);
            await RunAsync("损坏配置不会被覆盖", () => InvalidConfigurationIsPreservedAsync(testRoot));
            await RunAsync("E0 扫描码区分右 Alt", RightAltIsResolvedAsync);
            await RunAsync("E0 回车区分数字键盘", NumpadEnterIsResolvedAsync);
            await RunAsync("E1 暂停键可以识别", PauseIsResolvedAsync);
            await RunAsync("稳定键盘 ID 可以还原 Set-1 代码", StableKeyboardIdsResolveToSet1Async);
            await RunAsync("未知 E0 码不会降级成普通字母", UnknownExtendedCodeIsNotMisclassifiedAsync);
            await RunAsync("无效键盘包会在发布前过滤", InvalidKeyboardPacketsAreFilteredAsync);
            await RunAsync("Raw Input 包长度会被严格校验", RawInputPacketLengthsAreValidatedAsync);
            await RunAsync("设备身份必须来自真实设备路径", DevicePathMustBeStableAsync);
            await RunAsync("故障订阅者不能突破原生边界", FaultSubscribersAreIsolatedAsync);
            await RunAsync("窗口资源只能由所属线程释放", OwnerThreadIsEnforcedAsync);
            await RunAsync("动作执行只经过注入端口", ExecutorUsesInjectedPortsAsync);
            await RunAsync("注入标记识别自身输入", InjectionMarkerRecognizesOwnInputAsync);
            await RunAsync("动作失败释放已按下输入", ExecutorReleasesInputAfterFailureAsync);
            await RunAsync("无效计划预检不产生副作用", InvalidPlanHasNoSideEffectsAsync);
            await RunAsync("Synthetic RAWKEYBOARD keeps marker without a physical identity", SyntheticKeyboardPacketReachesMarkerFilterAsync);
            await RunAsync("Shortcut capture normalizes modifiers and completes at two seconds", ShortcutCaptureSessionTests.NormalizesModifiersAndCompletesAtTwoSecondsAsync);
            await RunAsync("Shortcut capture rejects injected, modifier-only, and unbounded input", ShortcutCaptureSessionTests.RejectsInjectedPureModifierAndUnboundedInputAsync);
            await RunAsync("XInput maps all 14 standard buttons", XInputMapsAllStandardButtonsAsync);
            await RunAsync("SendInput preserves scan-code flags and marker", WindowsActionBackendTests.ScanCodeSendInputIsExactAsync);
            await RunAsync("SendInput preserves virtual-key release", WindowsActionBackendTests.VirtualKeySendInputIsExactAsync);
            await RunAsync("E1 Pause uses VK_PAUSE and never E0", WindowsActionBackendTests.E1PauseUsesVirtualKeyAsync);
            await RunAsync("SendInput rejects unsupported and cancelled input", WindowsActionBackendTests.UnsupportedSendInputNeverCallsNativeAsync);
            await RunAsync("Executor failure releases marked SendInput keys", WindowsActionBackendTests.ExecutorCleanupUsesMarkedSendInputAsync);
            await RunAsync("Executable arguments use ArgumentList", () => WindowsActionBackendTests.ExecutableArgumentsStaySeparatedAsync(testRoot));
            await RunAsync("External action paths expand environment variables", () => WindowsActionBackendTests.EnvironmentVariablePathsAreExpandedAsync(testRoot));
            await RunAsync("Associated files cannot receive arguments", () => WindowsActionBackendTests.FileOpenUsesAssociationWithoutArgumentsAsync(testRoot));
            await RunAsync("ShellExecute null process result is successful", WindowsActionBackendTests.ShellExecuteNullResultIsSuccessfulAsync);
            await RunAsync("URI opening accepts safe web and registered protocol addresses", WindowsActionBackendTests.UriOpenAcceptsOnlyHttpAndHttpsAsync);
            await RunAsync("Scripts use explicit Windows runners", () => WindowsActionBackendTests.ScriptsUseExplicitWindowsRunnersAsync(testRoot));
            await RunAsync("CMD command quoting handles spaces safely", () => WindowsActionBackendTests.BatchCommandWithSpacesIsBoundedAsync(testRoot));
            await RunAsync("Unsafe CMD metacharacters are rejected", () => WindowsActionBackendTests.UnsafeBatchTextIsRejectedAsync(testRoot));
            await RunAsync("Executor preflights every external action before dispatch", () => WindowsActionBackendTests.ExecutorPreflightsAllExternalActionsBeforeDispatchAsync(testRoot));
            await RunAsync("Macro delay is bounded and cancellable", WindowsActionBackendTests.DelaySchedulerIsBoundedAndCancellableAsync);
            await RunAsync("Windows media and relative volume use marked virtual keys", WindowsSystemControlBackendTests.MediaAndRelativeVolumeUseMarkedVirtualKeysAsync);
            await RunAsync("Windows exact volume uses Core Audio scalar", WindowsSystemControlBackendTests.ExactVolumeUsesCoreAudioScalarAsync);
            await RunAsync("Windows exact volume failures explain Core Audio capability", WindowsSystemControlBackendTests.ExactVolumeFailureExplainsCoreAudioCapabilityAsync);
            await RunAsync("Executor preflights and dispatches semantic system controls", WindowsSystemControlBackendTests.ExecutorPreflightsAndDispatchesSystemOperationsAsync);
            await RunAsync("Compatibility keyboard rules protect emergency keys", CompatibilityKeyboardSuppressionTests.RulesStayKeyboardOnlyAndProtectEmergencyKeysAsync);
            await RunAsync("Compatibility keyboard toggle quarantines held edges", CompatibilityKeyboardSuppressionTests.ToggleGenerationQuarantinesHeldEdgesAsync);
            await RunAsync("Compatibility Raw Input dedupe is bounded and generation-safe", CompatibilityKeyboardSuppressionTests.RawInputTokensSurviveGenerationChangesButStayBoundedAsync);
            await RunAsync("Compatibility suppression revisions change across equal-rule profiles", CompatibilityKeyboardSuppressionTests.RuntimeRevisionChangesEvenWhenRulesStayEqualAsync);
            await RunAsync("Compatibility commit replay remains FIFO", CompatibilityKeyboardSuppressionTests.CommitReplayStaysFifoWhileCompletionIsDetachedAsync);
            await RunAsync("Single-instance gate preserves the first owner", SingleInstanceGateTests.SecondOwnerCannotDisplaceFirstAsync);
            await RunAsync("XInput ignores reserved bits and reports edges", XInputReportsOnlyKnownEdgesAsync);
            await RunAsync("XInput polling is cancellable and isolates subscribers", XInputPollingIsSafeAsync);
            await RunAsync("XInput trigger thresholds are hysteretic", XInputVirtualControlTests.TriggerThresholdsAreHystereticAsync);
            await RunAsync("XInput stick directions support diagonals and reversal", XInputVirtualControlTests.StickDirectionsHandleDiagonalsAndReversalAsync);
            await RunAsync("XInput polling publishes analog and raw diagnostic state", XInputVirtualControlTests.PollingPublishesVirtualAndRawStateAsync);
            await RunAsync("XInput stick analysis reports cardinal angles and deadzone", XInputStickAnalyzerTests.CardinalAnglesAndDeadzoneAreReportedAsync);
            await RunAsync("XInput stick rotations cross zero from arbitrary starts", XInputStickAnalyzerTests.ArbitraryStartRotationsCrossZeroInBothDirectionsAsync);
            await RunAsync("XInput stick analysis emits one edge per complete turn", XInputStickAnalyzerTests.TwoTurnsEmitTwoIndependentEdgesAsync);
            await RunAsync("XInput stick rotations pause and resume through center", XInputStickAnalyzerTests.ReturningToCenterPausesAndResumesProgressAsync);
            await RunAsync("XInput stick reversal cannot create false completion", XInputStickAnalyzerTests.ReversalCannotCreateFalseCompletionAsync);
            await RunAsync("XInput stick small-radius movement cannot form gestures", XInputStickAnalyzerTests.SmallRadiusMovementNeverStartsGestureAsync);
            await RunAsync("XInput stick analysis estimates drift and angular resolution", XInputStickAnalyzerTests.CenterDriftAndEffectiveResolutionAreEstimatedAsync);
            await RunAsync("XInput stick state is isolated by user and stick", XInputStickAnalyzerTests.UsersAndSticksKeepIndependentStateAsync);
            await RunAsync("XInput stick disconnect resets analysis state", XInputStickAnalyzerTests.DisconnectClearsCalibrationProgressAndSequenceAsync);
            await RunAsync("Relative mouse SendInput is exact and marked", XInputMouseMotionTests.RelativeSendInputIsMarkedAndExactAsync);
            await RunAsync("Stick mouse motion uses radial curve, fractions, and screen coordinates", XInputMouseMotionTests.MotionUsesRadialCurveFractionsAndScreenCoordinatesAsync);
            await RunAsync("Stick mouse selects the configured stick and lowest active slot", XInputMouseMotionTests.LowestActiveSlotAndSelectedStickWinAsync);
            await RunAsync("Stick mouse revision switches quarantine held input", XInputMouseMotionTests.RevisionSwitchRequiresCenterAndRejectsLateFramesAsync);
            await RunAsync("Stick mouse stops on disconnect, disable, and stale capture", XInputMouseMotionTests.DisconnectDisableAndStaleCaptureStopMovementAsync);
            await RunAsync("XInput polling heartbeats repeat steady connected state", XInputMouseMotionTests.PollingHeartbeatRepeatsSteadyConnectedStateAsync);
            await RunAsync("Stick mouse output failure disables continuous retry", XInputMouseMotionTests.OutputFailureDisablesContinuousRetryAsync);
            await RunAsync("Input diagnostics stay explicit, bounded, and redacted", () => InputDiagnosticSessionTests.TraceIsExplicitBoundedAndRedactedAsync(testRoot));
            await RunAsync("Input diagnostics stop at hard count and byte limits", () => InputDiagnosticSessionTests.TraceStopsAtHardLimitsAsync(testRoot));
            await RunAsync("Unknown HID identity includes the complete report tuple", UnknownHidReportDifferTests.IdentityIncludesEveryReportFieldAsync);
            await RunAsync("Unknown HID baselines and byte deltas are deterministic", UnknownHidReportDifferTests.BaselineAndDeltaAreDeterministicAsync);
            await RunAsync("Unknown HID report identities keep independent baselines", UnknownHidReportDifferTests.ReportIdentitiesKeepIndependentBaselinesAsync);
            await RunAsync("Unknown HID report length and count limits are strict", UnknownHidReportDifferTests.LengthAndCountLimitsAreStrictAsync);
            await RunAsync("Unknown HID disconnect clears all device reports", UnknownHidReportDifferTests.DisconnectClearsEveryDeviceReportAsync);
            await RunAsync("Unknown HID bytes remain semantically opaque", UnknownHidReportDifferTests.OpaqueBytesNeverAcquireButtonSemanticsAsync);
            await RunAsync("Raw HID registration stays within the intended TLCs", RawInputHidGuardsTests.CaptureRegistrationSelectionIsNarrowAsync);
            await RunAsync("Raw Input registration enforces Win32 parameter rules", RawInputHidGuardsTests.RegistrationRequestsEnforceWin32ContractAsync);
            await RunAsync("Raw mouse button flags are fail-closed", RawInputMouseGuardsTests.TranslateButtonFlagsIsFailClosedAsync);
            await RunAsync("Low-level mouse hook translates middle/back/forward", RawInputMouseGuardsTests.LowLevelMouseHookTranslatesMiddleAndXButtonsAsync);
            await RunAsync("RAWMOUSE layout is 24 bytes", RawInputMouseGuardsTests.RawMouseLayoutIsTwentyFourBytesAsync);
            await RunAsync("Raw HID batches retain every report", RawInputHidGuardsTests.HidBatchLayoutSupportsMultipleReportsAsync);
            await RunAsync("Raw HID lengths are strictly bounded", RawInputHidGuardsTests.HidBatchLayoutRejectsUnsafeLengthsAsync);
            await RunAsync("Sony DualShock USB reports map Cross and PS", NativeFusionTests.SonyDs4UsbCrossAndPsParseAsync);
            await RunAsync("Sony DualSense USB battery parses without inventing values", NativeFusionTests.DualSenseUsbBatteryParsesAndShortReportsStayUnknownAsync);
            await RunAsync("Sony DualSense Bluetooth battery uses the 0x31 offset", NativeFusionTests.DualSenseBluetoothBatteryUsesOffsetTwoAsync);
            await RunAsync("Device battery status formats known percent only", NativeFusionTests.DeviceBatteryStatusFormatsWithoutInventingPercentAsync);
            await RunAsync("RC003 advertised BLE names are fail-closed", NativeFusionTests.Rc003AdvertisedNameIsFailClosedAsync);
            await RunAsync("RC003 battery GATT payload stays 0-100", NativeFusionTests.Rc003BatteryLevelParseRejectsOutOfRangeAsync);
            await RunAsync("RC003 identity is VID/PID fail-closed", NativeFusionTests.Rc003IdentityIsFailClosedAsync);
            await RunAsync("Virtual pad filter ignores slots after connect", NativeFusionTests.VirtualPadFilterIgnoresSlotsAfterConnectAsync);
            await RunAsync("Capture classifier recognizes DualSense, Xiaomi remote and CABLE Output", NativeFusionTests.CaptureClassifierRecognizesSonyAndCableAsync);
            await RunAsync("Connected microphones are device-centric", NativeFusionTests.ConnectedMicrophonesAreDeviceCentricAsync);
            await RunAsync("Capture catalog lists endpoints without IMMDeviceCollection casting", NativeFusionTests.CaptureCatalogListsActiveEndpointsWithoutComCastingAsync);
            await RunAsync("Microphone aliases round-trip and sanitize", NativeFusionTests.MicrophoneAliasesRoundTripAndSanitizeAsync);
            await RunAsync("IMA ADPCM and ATVV control stay bounded", NativeFusionTests.ImaAdpcmRoundTripSilenceStaysBoundedAsync);
            await RunAsync("Keyboard and HID share one packet cap", RawInputHidGuardsTests.RawInputPacketCapIsSharedAsync);
            await RunAsync("Opaque HID capture confirms a repeatable transition", OpaqueHidButtonCaptureSessionTests.RepeatableTransitionIsConfirmedAsync);
            await RunAsync("Opaque HID capture isolates physical reports", OpaqueHidButtonCaptureSessionTests.OtherReportsCannotConfirmCandidateAsync);
            await RunAsync("Opaque HID capture rejects unstable candidates", OpaqueHidButtonCaptureSessionTests.UnstableOrWideChangesAreRejectedAsync);
            await RunAsync("Opaque HID capture ignores stale reports and expires", OpaqueHidButtonCaptureSessionTests.StaleReportsAndTimeoutAreSafeAsync);
            await RunAsync("Opaque HID capture cancellation clears candidates", OpaqueHidButtonCaptureSessionTests.CancellationClearsCandidateAsync);
            await RunAsync("Opaque HID source retains exact hardware identity", OpaqueHidButtonCaptureSessionTests.FactoryCreatesExactOpaqueIdentityAsync);
            await RunAsync("Opaque HID source identity ignores transition direction", OpaqueHidButtonCaptureSessionTests.FactoryIdentityIsDirectionIndependentAsync);
            await RunAsync("Opaque HID runtime emits only captured press and release edges", OpaqueHidButtonCaptureSessionTests.RuntimeMatcherReportsOnlyCapturedEdgesAsync);
            await RunAsync("Foreground process reader resolves and trims the process name", WindowsForegroundProcessReaderTests.ResolvesAndTrimsForegroundProcessNameAsync);
            await RunAsync("Foreground process reader rejects invalid native owners", WindowsForegroundProcessReaderTests.RejectsMissingOrInvalidForegroundOwnersAsync);
            await RunAsync("Foreground process reader contains process races and failures", WindowsForegroundProcessReaderTests.ProcessRacesAndInvalidNamesFailSafelyAsync);
            await RunAsync("Foreground WinEvent watcher signals safely and unhooks once", WindowsForegroundWindowWatcherTests.HookSignalsChangesAndHasIdempotentLifetimeAsync);
            await RunAsync("Foreground WinEvent watcher tolerates installation failure", WindowsForegroundWindowWatcherTests.FailedHookRemainsStoppedAsync);
            await RunAsync("Application catalog lists visible root windows and skips tool windows", WindowsApplicationCatalogTests.ListsVisibleRootWindowsAndSkipsToolWindowsAsync);
            await RunAsync("Application catalog skips ApplicationFrameHost without a package family", WindowsApplicationCatalogTests.SkipsApplicationFrameHostWithoutPackageFamilyAsync);
            await RunAsync("Application catalog uses package family for packaged apps", WindowsApplicationCatalogTests.UsesPackageFamilyForPackagedAppsAsync);
            await RunAsync("Application catalog contains foreground read failures", WindowsApplicationCatalogTests.ForegroundReadFailuresStayEmptyAsync);
            await RunAsync("Workspace restores special slots and known inputs offline", WorkspaceConfigurationAdapterTests.RestoresSpecialSlotsAndKnownInputsOfflineAsync);
            await RunAsync("Workspace converts all six drawer actions", WorkspaceConfigurationAdapterTests.SixDrawerActionsRoundTripToCoreAsync);
            await RunAsync("Workspace round-trips mapping application conditions", WorkspaceConfigurationAdapterTests.MappingConditionRoundTripsThroughWorkspaceAsync);
            await RunAsync("Workspace round-trips media, volume, copied output, and naked URI", WorkspaceConfigurationAdapterTests.MediaVolumeCopiedOutputAndUriNormalizeAsync);
            await RunAsync("Workspace saves gamepad A URI mapping", WorkspaceConfigurationAdapterTests.GamepadUriMappingSerializesAsync);
            await RunAsync("Workspace saves mouse middle URI mapping", WorkspaceConfigurationAdapterTests.MouseUriMappingSerializesAsync);
            await RunAsync("Workspace saves and restores analog gamepad mappings", WorkspaceConfigurationAdapterTests.GamepadAnalogMappingSerializesAsync);
            await RunAsync("Workspace preserves advanced and detached mappings", WorkspaceConfigurationAdapterTests.AdvancedAndDetachedMappingsPassThroughUnchangedAsync);
            await RunAsync("Workspace round-trips chord and rotation sources", WorkspaceConfigurationAdapterTests.ChordAndRotationSourcesRoundTripAsync);
            await RunAsync("Mapping runtime rejects injected recursion", MappingExecutionCoordinatorTests.InjectedInputIsRejectedBeforeRecognitionAsync);
            await RunAsync("Raw Input repeat packets are explicit", MappingExecutionCoordinatorTests.RawDownPacketsBecomePressedThenRepeatedAsync);
            await RunAsync("Mapping runtime honors active profile and exact device", MappingExecutionCoordinatorTests.ActiveProfileAndExactDeviceAreHonoredAsync);
            await RunAsync("Mapping runtime ignores duplicate and repeated down", MappingExecutionCoordinatorTests.DuplicateDownAndRepeatFireOnlyOnceAsync);
            await RunAsync("Pass-through input rejects suppression-required mappings", MappingExecutionCoordinatorTests.PassThroughInputRejectsMappingsThatRequireSuppressionAsync);
            await RunAsync("Cross-backend chord executes once and resets safely", MappingExecutionCoordinatorTests.CrossBackendChordExecutesOnceAndResetClearsStateAsync);
            await RunAsync("Gamepad and HID pass-through reject suppression-required mappings", MappingExecutionCoordinatorTests.GamepadAndHidPassThroughAlsoRejectSuppressionAsync);
            await RunAsync("Mapping runtime advances long-press deadlines", MappingExecutionCoordinatorTests.LongPressDeadlineFiresWithoutAnotherInputAsync);
            await RunAsync("Mapping runtime expires double-press deadlines", MappingExecutionCoordinatorTests.DoublePressWindowExpiresOnItsDeadlineAsync);
            await RunAsync("Mapping runtime resets state on configuration replacement", MappingExecutionCoordinatorTests.ConfigurationReplacementResetsPendingTriggerStateAsync);
            await RunAsync("Held keys do not retrigger across profile replacement", MappingExecutionCoordinatorTests.HeldKeyDoesNotRetriggerAcrossProfileReplacementAsync);
            await RunAsync("Late suppressed input cannot cross the runtime revision fence", MappingExecutionCoordinatorTests.RuntimeRevisionRejectsLateSuppressedInputAsync);
            await RunAsync("Foreground context replacement cancels old actions", MappingExecutionCoordinatorTests.ForegroundContextReplacementCancelsOldActionsAsync);
            await RunAsync("Mapping runtime monotonicizes capture timestamps", MappingExecutionCoordinatorTests.OutOfOrderCaptureTimestampsAreMonotonicizedAsync);
            await RunAsync("Mapping runtime executes FIFO without blocking input", MappingExecutionCoordinatorTests.ActionsAreFifoWithoutBlockingInputSubmissionAsync);
            await RunAsync("Mapping runtime bounds a saturated action queue", MappingExecutionCoordinatorTests.SaturatedActionQueueDropsWithoutBlockingInputAsync);
            await RunAsync("Mapping runtime reports failures and continues", MappingExecutionCoordinatorTests.ExecutionFailureIsReportedAndWorkerContinuesAsync);
            await RunAsync("Mapping runtime shutdown cancels actions", MappingExecutionCoordinatorTests.StopCancelsRunningActionAndRejectsNewInputAsync);
            await RunAsync("Tracked mapping waits for action completion", MappingExecutionCoordinatorTests.TrackedInputWaitsForSuccessfulActionAsync);
            await RunAsync("Tracked long press includes deadline and action", MappingExecutionCoordinatorTests.TrackedLongPressIncludesDeadlineAndActionAsync);
            await RunAsync("Tracked input ignores another key's deadline", MappingExecutionCoordinatorTests.TrackedInputIgnoresAnotherKeysDeadlineAsync);
            await RunAsync("Tracked long wait provides verifiable liveness", MappingExecutionCoordinatorTests.TrackedLongWaitProvidesVerifiableLivenessAsync);
            await RunAsync("Tracked no-action input completes immediately", MappingExecutionCoordinatorTests.TrackedNoActionCompletesWithoutWaitingForFutureReleaseAsync);
            await RunAsync("Tracked mapping failures are not successful", MappingExecutionCoordinatorTests.TrackedPlanningAndExecutionFailuresAreNotSuccessfulAsync);
            await RunAsync("Driver rules retain safe protocol boundaries", DriverSuppressionTests.RuleBuilderHonorsProtocolBoundariesAsync);
            await RunAsync("Driver runtime maps phases and generations", DriverSuppressionTests.CoordinatorMapsPhasesAndGenerationsAsync);
            await RunAsync("Driver superseded policy cannot advance ACK", DriverSuppressionTests.SupersededPolicyCompletionCannotAcknowledgeAsync);
            await RunAsync("Driver heartbeat failure immediately fails open", DriverSuppressionTests.HeartbeatFailureImmediatelyFailsOpenAsync);
            await RunAsync("Driver read and dispatch failures immediately fail open", DriverSuppressionTests.ReadOrDispatchFailureImmediatelyFailsOpenAsync);
            await RunAsync("Driver failed actions immediately fail open", DriverSuppressionTests.FailedActionCompletionImmediatelyFailsOpenAsync);
            await RunAsync("Driver ACK cannot cross a completion gap", DriverSuppressionTests.AcknowledgementCannotCrossCompletionGapAsync);
            await RunAsync("Driver reconnect keeps generations monotonic", DriverSuppressionTests.ReconnectKeepsGenerationMonotonicAsync);
            await RunAsync("Driver connection errors have explicit states", DriverSuppressionTests.ConnectionErrorsHaveExplicitStatesAsync);
            await RunAsync("Driver tracking loss refuses a new lease", DriverSuppressionTests.InputTrackingLossRefusesLeaseAsync);
            await RunAsync("Broker keeps commit and completion watermarks separate", BrokerDriverSuppressionCoordinatorTests.CommitAndCompletionWatermarksStaySeparateAsync);
            await RunAsync("Broker superseded policy cannot advance ACK", BrokerDriverSuppressionCoordinatorTests.SupersededPolicyReportsFailOpenWithoutAckAsync);
            await RunAsync("Broker accepts repeated bounded-wait liveness", BrokerDriverSuppressionCoordinatorTests.BoundedWaitLivenessSurvivesRepeatedChallengesAsync);
            await RunAsync("Broker failed action closes IPC and fails open", BrokerDriverSuppressionCoordinatorTests.FailedActionClosesBrokerAndFailsOpenAsync);
            await RunAsync("Missing protected broker is explicitly fail-open", BrokerDriverSuppressionCoordinatorTests.MissingProtectedInstallationIsExplicitlyFailOpenAsync);
            await RunAsync("Disabled policy never starts and releases broker", BrokerDriverSuppressionCoordinatorTests.DisabledPolicyNeverConnectsAndReleasesActiveBrokerAsync);
            await RunAsync("Driver preflight prevents useless broker elevation", BrokerDriverSuppressionCoordinatorTests.DriverProbePreventsUselessBrokerLaunchAsync);
            await RunAsync("Broker failure retries only after explicit off and on", BrokerDriverSuppressionCoordinatorTests.FailureRequiresExplicitDisableBeforeRetryAsync);
            Console.WriteLine($"{_passed} passed, {_failed} failed.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            var expectedParent = Path.Combine(Path.GetTempPath(), "KeyPilot.Platform.Windows.Tests");
            var resolvedRoot = Path.GetFullPath(testRoot);
            if (resolvedRoot.StartsWith(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }

    private static async Task MissingConfigurationReturnsDefaultAsync(string root)
    {
        var path = Path.Combine(root, "missing", "config.json");
        var store = new LocalKeyPilotConfigurationStore(path);
        var configuration = await store.LoadAsync();

        Assert(configuration.Profiles.Count == 1, "默认配置应包含一个方案。");
        Assert(configuration.SpecialKeySlots.Count == 10, "默认配置应包含十个特殊键槽位。");
        Assert(!configuration.IsMappingEnabled, "首次启动和旧配置缺少开关时必须安全默认关闭映射。");
    }

    private static async Task ConfigurationRoundTripsAsync(string root)
    {
        var path = Path.Combine(root, "roundtrip", "config.json");
        var store = new LocalKeyPilotConfigurationStore(path);
        var configuration = KeyPilotConfiguration.CreateDefault("掌机默认配置");

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();

        Assert(File.Exists(path), "保存后配置文件应存在。");
        Assert(loaded.ActiveProfileId == configuration.ActiveProfileId, "活动方案 ID 应保持不变。");
        Assert(loaded.Profiles.Single().Name == "掌机默认配置", "方案名称应完整读回。");
    }

    private static async Task InvalidSavePreservesFileAndReportsReasonAsync(string root)
    {
        var path = Path.Combine(root, "invalid-save", "config.json");
        var store = new LocalKeyPilotConfigurationStore(path);
        await store.SaveAsync(KeyPilotConfiguration.CreateDefault("有效配置"));
        var original = await File.ReadAllTextAsync(path);

        var invalid = KeyPilotConfiguration.CreateDefault("无效配置");
        invalid.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "越界手柄按钮",
            SuppressOriginal = false,
            Source = new InputSource
            {
                Device = new InputDeviceSelector
                {
                    Kind = InputDeviceKind.Gamepad,
                    MatchMode = DeviceMatchMode.AnyOfKind
                },
                Control = new InputControlId
                {
                    Kind = InputControlKind.GamepadButton,
                    Code = 0x10000
                }
            },
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });

        try
        {
            await store.SaveAsync(invalid);
            throw new InvalidOperationException("无效配置不应保存成功。");
        }
        catch (KeyPilotConfigurationStoreException exception)
        {
            Assert(exception.Message.Contains("65535", StringComparison.Ordinal),
                "保存异常应包含具体的配置校验原因。");
        }

        Assert(await File.ReadAllTextAsync(path) == original, "无效保存不得覆盖原配置。");
    }

    private static async Task InvalidConfigurationIsPreservedAsync(string root)
    {
        var path = Path.Combine(root, "invalid", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string invalidJson = "{ this is intentionally invalid json";
        await File.WriteAllTextAsync(path, invalidJson);
        var store = new LocalKeyPilotConfigurationStore(path);

        try
        {
            await store.LoadAsync();
            throw new InvalidOperationException("损坏配置应抛出存储异常。");
        }
        catch (KeyPilotConfigurationStoreException exception)
        {
            Assert(exception.ConfigurationPath == Path.GetFullPath(path), "异常应包含配置路径。");
        }

        var unchanged = await File.ReadAllTextAsync(path);
        Assert(unchanged == invalidJson, "加载失败不得重写损坏配置。");
    }

    private static async Task ConfigurationReplacementKeepsBackupAsync(string root)
    {
        var path = Path.Combine(root, "backup", "config.json");
        var store = new LocalKeyPilotConfigurationStore(path);
        await store.SaveAsync(KeyPilotConfiguration.CreateDefault("上一份配置"));
        await store.SaveAsync(KeyPilotConfiguration.CreateDefault("当前配置"));

        Assert(File.Exists(store.BackupPath), "第二次原子替换后应保留上一份配置备份。");
        var backup = await new LocalKeyPilotConfigurationStore(store.BackupPath).LoadAsync();
        Assert(backup.Profiles.Single().Name == "上一份配置", "备份必须是替换前的有效配置。");
        var current = await store.LoadAsync();
        Assert(current.Profiles.Single().Name == "当前配置", "当前配置必须是最后一次保存的版本。");
    }

    private static Task LatestSaveCompletionCannotDisplaceNewerRequestAsync()
    {
        var revisions = new KeyPilot.App.Presentation.LatestSaveRevision();
        var enableRequest = revisions.BeginRequest();
        var disableRequest = revisions.BeginRequest();

        Assert(!revisions.IsLatest(enableRequest), "较旧的启用保存完成不得覆盖后来的关闭请求。");
        Assert(revisions.IsLatest(disableRequest), "最后一次关闭请求必须拥有提交运行状态的资格。");
        return Task.CompletedTask;
    }

    private static Task RightAltIsResolvedAsync()
    {
        var input = KeyboardPacket(makeCode: 0x38, virtualKey: 0xA5, flags: 0x0002);
        Assert(KeyboardKeyResolver.Resolve(input) == "AltRight", "E0 Alt 应解析为右 Alt。");
        return Task.CompletedTask;
    }

    private static Task NumpadEnterIsResolvedAsync()
    {
        var input = KeyboardPacket(makeCode: 0x1C, virtualKey: 0x0D, flags: 0x0002);
        Assert(KeyboardKeyResolver.Resolve(input) == "NumpadEnter", "E0 Enter 应解析为数字键盘回车。");
        return Task.CompletedTask;
    }

    private static Task PauseIsResolvedAsync()
    {
        var input = KeyboardPacket(makeCode: 0x45, virtualKey: 0x13, flags: 0x0004);
        Assert(KeyboardKeyResolver.Resolve(input) == "Pause", "E1 VK_PAUSE 应解析为 Pause。");
        return Task.CompletedTask;
    }

    private static Task StableKeyboardIdsResolveToSet1Async()
    {
        Assert(
            KeyboardKeyResolver.TryGetSet1Code("Space", out var space) &&
            space == new KeyboardKeyResolver.Set1KeyCode(0x39, 0),
            "Space must resolve to its base Set-1 code.");
        Assert(
            KeyboardKeyResolver.TryGetSet1Code("AltRight", out var rightAlt) &&
            rightAlt == new KeyboardKeyResolver.Set1KeyCode(0x38, 0x0002),
            "Right Alt must retain its E0 prefix.");
        Assert(
            KeyboardKeyResolver.TryGetSet1Code("Pause", out var pause) &&
            pause == new KeyboardKeyResolver.Set1KeyCode(0x45, 0x0004),
            "Pause must retain its E1 prefix.");
        Assert(!KeyboardKeyResolver.TryGetSet1Code("NotAKey", out _), "Unknown IDs must not be synthesized.");
        return Task.CompletedTask;
    }

    private static Task UnknownExtendedCodeIsNotMisclassifiedAsync()
    {
        var input = KeyboardPacket(makeCode: 0x20, virtualKey: 0xAD, flags: 0x0002);
        Assert(KeyboardKeyResolver.Resolve(input) is null, "未知 E0 0x20 不能被误判为字母 D。");
        return Task.CompletedTask;
    }

    private static Task InvalidKeyboardPacketsAreFilteredAsync()
    {
        Assert(
            !RawInputKeyboardGuards.ShouldPublishKeyboardPacket(makeCode: 0xFF, virtualKey: 0x41),
            "KEYBOARD_OVERRUN_MAKE_CODE 不得发布。");
        Assert(
            !RawInputKeyboardGuards.ShouldPublishKeyboardPacket(makeCode: 0x1E, virtualKey: 0xFF),
            "VK 0xFF 不得发布。");
        Assert(
            !RawInputKeyboardGuards.ShouldPublishKeyboardPacket(makeCode: 0x1E, virtualKey: 0x0100),
            "超出 8 位范围的 VK 不得发布。");
        Assert(
            !RawInputKeyboardGuards.ShouldPublishKeyboardPacket(makeCode: 0, virtualKey: 0xAD),
            "MakeCode 为零的包不能形成可抑制的稳定扫描码，必须过滤。");
        return Task.CompletedTask;
    }

    private static Task RawInputPacketLengthsAreValidatedAsync()
    {
        RawInputKeyboardGuards.ValidateRequestedSize(requestedSize: 40, headerSize: 24);
        RawInputKeyboardGuards.ValidateCopiedPacket(
            allocatedSize: 40,
            reportedSize: 40,
            copiedSize: 40,
            headerSize: 24);
        RawInputKeyboardGuards.ValidatePacketLayout(
            copiedSize: 40,
            declaredSize: 40,
            headerSize: 24,
            payloadSize: 16);

        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.ValidateRequestedSize(23, 24),
            "短于 RAWINPUTHEADER 的查询结果必须被拒绝。");
        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.ValidateCopiedPacket(40, 40, 23, 24),
            "短拷贝必须被拒绝。");
        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.ValidatePacketLayout(40, 39, 24, 16),
            "声明长度与复制长度不一致时必须被拒绝。");
        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.ValidatePacketLayout(32, 32, 24, 16),
            "键盘负载被截断时必须被拒绝。");
        return Task.CompletedTask;
    }

    private static Task SyntheticKeyboardPacketReachesMarkerFilterAsync()
    {
        var resolverCalls = 0;
        var timestamp = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        var synthetic = RawInputKeyboardGuards.CreateKeyboardEvent(
            device: 0,
            makeCode: 0x1E,
            virtualKey: 0x41,
            flags: 0,
            message: 0x0100,
            extraInformation: 0x4B50544C,
            timestamp,
            physicalDevicePathResolver: _ =>
            {
                resolverCalls++;
                throw new InvalidOperationException("A null hDevice must not be resolved as hardware.");
            });

        Assert(resolverCalls == 0, "Synthetic keyboard packets must not query a device interface path.");
        Assert(!synthetic.HasPhysicalDevice, "hDevice == NULL must remain explicitly non-physical.");
        Assert(
            synthetic.DevicePath == RawInputKeyboardGuards.SyntheticKeyboardDevicePath,
            "Synthetic keyboard packets must receive one stable, explicit synthetic identity.");
        Assert(
            synthetic.DevicePath.Contains("SYNTHETIC", StringComparison.Ordinal),
            "The synthetic identity must not resemble a physical device path.");
        Assert(
            synthetic.IsInjectedBy(new InputInjectionMarker(0x4B50544C)),
            "dwExtraInfo must reach the shared recursion-marker filter unchanged.");

        const string physicalPath = @"\\?\HID#VID_1234&PID_5678#INSTANCE";
        var physical = RawInputKeyboardGuards.CreateKeyboardEvent(
            device: (nint)7,
            makeCode: 0x1E,
            virtualKey: 0x41,
            flags: 0,
            message: 0x0100,
            extraInformation: 0,
            timestamp,
            physicalDevicePathResolver: device =>
            {
                resolverCalls++;
                Assert(device == (nint)7, "The physical resolver must receive the original handle.");
                return physicalPath;
            });
        Assert(resolverCalls == 1, "Physical keyboard identity must still use the device resolver.");
        Assert(physical.HasPhysicalDevice && physical.DevicePath == physicalPath, "Physical identity must be preserved.");

        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.RequirePhysicalDeviceHandle(0),
            "The keyboard-only synthetic exception must not relax the HID physical-handle guard.");
        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.RequireStableDevicePath(
                0,
                RawInputKeyboardGuards.SyntheticKeyboardDevicePath),
            "The synthetic identity must never pass as a physical interface path.");
        return Task.CompletedTask;
    }

    private static Task DevicePathMustBeStableAsync()
    {
        const string path = @"\\?\HID#VID_1234&PID_5678#INSTANCE";
        var actual = RawInputKeyboardGuards.RequireStableDevicePath((nint)1, path);
        Assert(actual == path, "真实设备路径必须原样保留。");
        Assert(!actual.StartsWith("HANDLE:", StringComparison.OrdinalIgnoreCase), "不得生成句柄伪身份。");

        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.RequireStableDevicePath(0, path),
            "空设备句柄不能冒充物理设备。");
        AssertThrows<InvalidDataException>(
            () => RawInputKeyboardGuards.RequireStableDevicePath((nint)1, string.Empty),
            "缺失设备路径时不能生成持久身份。");
        return Task.CompletedTask;
    }

    private static Task FaultSubscribersAreIsolatedAsync()
    {
        var laterSubscriberCalled = false;
        EventHandler<Exception> handlers = (_, _) => throw new InvalidOperationException("subscriber failure");
        handlers += (_, _) => laterSubscriberCalled = true;

        RawInputKeyboardGuards.InvokeCaptureFaultedSafely(
            new object(),
            handlers,
            new InvalidDataException("capture failure"));

        Assert(laterSubscriberCalled, "一个故障订阅者抛错后，后续订阅者仍应收到通知。");
        return Task.CompletedTask;
    }

    private static Task OwnerThreadIsEnforcedAsync()
    {
        RawInputKeyboardGuards.EnsureOwnerThread(ownerThreadId: 17, currentThreadId: 17);
        AssertThrows<InvalidOperationException>(
            () => RawInputKeyboardGuards.EnsureOwnerThread(ownerThreadId: 17, currentThreadId: 18),
            "跨线程释放 HWND 资源必须被拒绝。");
        return Task.CompletedTask;
    }

    private static async Task ExecutorUsesInjectedPortsAsync()
    {
        var ports = new RecordingActionPorts();
        var executor = new WindowsActionExecutor(
            ports,
            ports,
            ports,
            new InputInjectionMarker(0x4B50544C));
        var plan = new MappingActionPlanner().Plan(new MacroAction
        {
            Steps =
            {
                new SendKeyAction
                {
                    Target = KeyboardScanCode(0x39),
                    HoldMilliseconds = 12
                },
                new OpenUriAction { Uri = "https://example.invalid/" }
            }
        });

        await executor.ExecuteAsync(plan);

        AssertSequence(
            new[]
            {
                "inject:Down:57:4B50544C",
                "delay:12",
                "inject:Up:57:4B50544C",
                "uri:https://example.invalid/"
            },
            ports.Events);
    }

    private static Task InjectionMarkerRecognizesOwnInputAsync()
    {
        var marker = new InputInjectionMarker(0x10203040);
        Assert(
            KeyboardPacket(0x1E, 0x41, 0, marker.Value).IsInjectedBy(marker),
            "携带同一进程标记的包应被识别。");
        Assert(
            !KeyboardPacket(0x1E, 0x41, 0).IsInjectedBy(marker),
            "普通物理输入不得被误判为自身注入。");
        Assert(
            !KeyboardPacket(0x1E, 0x41, 0).IsInjectedBy(default),
            "默认零值标记不得把常见的零扩展信息误判为自身注入。");
        return Task.CompletedTask;
    }

    private static async Task ExecutorReleasesInputAfterFailureAsync()
    {
        var ports = new RecordingActionPorts { FailUri = true };
        var executor = new WindowsActionExecutor(
            ports,
            ports,
            ports,
            new InputInjectionMarker(0x55667788));
        var plan = new ActionPlan(new ActionPlanOperation[]
        {
            new InjectInputOperation(
                new ControlInjectionTarget(KeyboardScanCode(0x1D)),
                InputInjectionPhase.Down),
            new OpenUriOperation("https://failure.invalid/")
        });

        try
        {
            await executor.ExecuteAsync(plan);
            throw new InvalidOperationException("假后端故障应传播给调用方。");
        }
        catch (SyntheticBackendException)
        {
            // Expected from the injected fake; no real URI is opened.
        }

        AssertSequence(
            new[]
            {
                "inject:Down:29:55667788",
                "uri:https://failure.invalid/",
                "inject:Up:29:55667788"
            },
            ports.Events);
    }

    private static async Task InvalidPlanHasNoSideEffectsAsync()
    {
        var ports = new RecordingActionPorts();
        var executor = new WindowsActionExecutor(
            ports,
            ports,
            ports,
            new InputInjectionMarker(0xAABBCCDD));
        var plan = new ActionPlan(new ActionPlanOperation[]
        {
            new InjectInputOperation(
                new ControlInjectionTarget(KeyboardScanCode(0x1E)),
                InputInjectionPhase.Down),
            new OpenUriOperation("relative/path")
        });

        try
        {
            await executor.ExecuteAsync(plan);
            throw new InvalidOperationException("无效计划应在执行前被拒绝。");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith("Invalid action plan", StringComparison.Ordinal))
        {
            // Expected.
        }

        Assert(ports.Events.Count == 0, "预检失败前不得调用任何端口。");

        var unsupportedCapturedTarget = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Hid,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = "HID-DEVICE"
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.RawCode,
                Code = 1
            }
        };
        var unsupportedPlan = new ActionPlan(new ActionPlanOperation[]
        {
            new InjectInputOperation(
                new ControlInjectionTarget(KeyboardScanCode(0x1E)),
                InputInjectionPhase.Down),
            new InjectInputOperation(
                new CapturedInputInjectionTarget(unsupportedCapturedTarget),
                InputInjectionPhase.Down)
        });
        try
        {
            await executor.ExecuteAsync(unsupportedPlan);
            throw new InvalidOperationException("Unsupported captured HID must fail in preflight.");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith("Invalid action plan", StringComparison.Ordinal))
        {
            // Expected.
        }

        Assert(ports.Events.Count == 0, "Unsupported captured input must be rejected before earlier operations execute.");
    }

    private static Task XInputMapsAllStandardButtonsAsync()
    {
        const ushort allKnownButtons = 0xF3FF;
        var transitions = XInputButtonMapper.GetTransitions(0, allKnownButtons);

        Assert(transitions.Count == 14, "XInput must expose exactly fourteen standard digital buttons.");
        Assert(transitions.All(transition => transition.IsPressed), "Every zero-to-one edge must be pressed.");
        Assert(
            transitions.Select(transition => transition.Button).Distinct().Count() == 14,
            "Each standard XInput button must be returned once.");
        return Task.CompletedTask;
    }

    private static Task XInputReportsOnlyKnownEdgesAsync()
    {
        var previous = (ushort)((ushort)XInputButton.A | (ushort)XInputButton.LB | 0x0400);
        var current = (ushort)((ushort)XInputButton.B | (ushort)XInputButton.LB | 0x0800);
        var transitions = XInputButtonMapper.GetTransitions(previous, current);

        Assert(transitions.Count == 2, "Reserved and unchanged bits must not generate XInput edges.");
        Assert(
            transitions.Contains(new XInputButtonTransition(XInputButton.A, IsPressed: false)),
            "A must publish a release edge.");
        Assert(
            transitions.Contains(new XInputButtonTransition(XInputButton.B, IsPressed: true)),
            "B must publish a press edge.");
        return Task.CompletedTask;
    }

    private static Task XInputPollingIsSafeAsync()
    {
        var reader = new ScriptedXInputReader();
        using var source = new XInputGamepadSource(reader, TimeSpan.FromMilliseconds(1));
        using var cancellation = new CancellationTokenSource();
        using var disconnected = new ManualResetEventSlim();
        var buttonEvents = new List<string>();
        var connectionEvents = new List<string>();
        var faultSubscriberCalled = 0;

        source.ButtonChanged += (_, _) => throw new SyntheticBackendException();
        source.ButtonChanged += (_, args) =>
            buttonEvents.Add($"{args.UserIndex}:{args.Button}:{args.IsPressed}");
        source.ConnectionChanged += (_, _) => throw new SyntheticBackendException();
        source.ConnectionChanged += (_, args) =>
        {
            connectionEvents.Add($"{args.UserIndex}:{args.IsConnected}");
            if (args.UserIndex == 0 && !args.IsConnected)
            {
                disconnected.Set();
            }
        };
        source.CaptureFaulted += (_, _) => throw new SyntheticBackendException();
        source.CaptureFaulted += (_, _) => Interlocked.Increment(ref faultSubscriberCalled);

        source.Start(cancellation.Token);
        Assert(disconnected.Wait(TimeSpan.FromSeconds(2)), "The scripted disconnect event was not observed.");
        cancellation.Cancel();
        Assert(
            SpinWait.SpinUntil(() => !source.IsRunning, TimeSpan.FromSeconds(2)),
            "Cancellation must stop the XInput polling thread.");

        Assert(reader.AllSlotsWereRead, "Every poll cycle must inspect all four XInput user slots.");
        AssertSequence(
            new[]
            {
                "0:A:True",
                "0:A:False",
                "0:B:True",
                "0:B:False"
            },
            buttonEvents);
        AssertSequence(new[] { "0:True", "0:False" }, connectionEvents);
        Assert(faultSubscriberCalled >= 6, "Subscriber failures must be isolated and reported safely.");
        return Task.CompletedTask;
    }

    private static RawKeyboardEvent KeyboardPacket(
        ushort makeCode,
        ushort virtualKey,
        ushort flags,
        uint extraInformation = 0) =>
        new(
            DeviceHandle: 1,
            DevicePath: @"\\?\HID#VID_0000&PID_0000",
            MakeCode: makeCode,
            VirtualKey: virtualKey,
            Flags: flags,
            Message: 0x0100,
            ExtraInformation: extraInformation,
            Timestamp: DateTimeOffset.UtcNow);

    private static InputControlId KeyboardScanCode(int scanCode) => new()
    {
        Kind = InputControlKind.KeyboardScanCode,
        Code = scanCode
    };

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}: {exception.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertSequence(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"事件顺序不符：{string.Join(" | ", actual)}");
        }
    }
}

internal sealed class RecordingActionPorts :
    IInputInjectionBackend,
    IExternalActionBackend,
    IActionDelayScheduler
{
    public List<string> Events { get; } = new();

    public bool FailUri { get; init; }

    public void ValidateLaunchProgram(LaunchProgramOperation operation)
    {
    }

    public void ValidateUri(OpenUriOperation operation)
    {
    }

    public void ValidateScript(RunScriptOperation operation)
    {
    }

    public ValueTask InjectAsync(InputInjectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var code = request.Target switch
        {
            ControlInjectionTarget target => target.Control.Code,
            CapturedInputInjectionTarget target => target.Source.Control.Code,
            _ => -1
        };
        Events.Add($"inject:{request.Phase}:{code}:{request.OriginMarker.Value:X8}");
        return ValueTask.CompletedTask;
    }

    public ValueTask LaunchProgramAsync(
        LaunchProgramOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"program:{operation.FilePath}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenUriAsync(OpenUriOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"uri:{operation.Uri}");
        if (FailUri)
        {
            throw new SyntheticBackendException();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RunScriptAsync(
        RunScriptOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"script:{operation.ScriptPath}");
        return ValueTask.CompletedTask;
    }

    public ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"delay:{milliseconds}");
        return ValueTask.CompletedTask;
    }
}

internal sealed class SyntheticBackendException : Exception;

internal sealed class ScriptedXInputReader : IXInputStateReader
{
    private readonly int[] _reads = new int[XInputGamepadSource.UserSlotCount];
    private int _slotZeroStep;

    public bool AllSlotsWereRead => _reads.All(count => Volatile.Read(ref count) > 0);

    public XInputSnapshot Read(int userIndex)
    {
        Interlocked.Increment(ref _reads[userIndex]);
        if (userIndex != 0)
        {
            return XInputSnapshot.Disconnected;
        }

        return Interlocked.Increment(ref _slotZeroStep) switch
        {
            1 => XInputSnapshot.Disconnected,
            2 => new XInputSnapshot(true, 1, (ushort)XInputButton.A),
            3 => new XInputSnapshot(true, 2, (ushort)XInputButton.B),
            _ => XInputSnapshot.Disconnected
        };
    }
}
