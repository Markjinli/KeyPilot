using KeyPilot.Platform.Windows.Audio;
using KeyPilot.Platform.Windows.Input;
using KeyPilot.Platform.Windows.Storage;

namespace KeyPilot.Platform.Windows.Tests;

internal static class NativeFusionTests
{
    internal static Task SonyDs4UsbCrossAndPsParseAsync()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = 128;
        report[2] = 128;
        report[3] = 128;
        report[4] = 128;
        report[5] = 0x28; // hat center + Cross
        report[7] = 0x01; // PS
        report[8] = 0;
        report[9] = 0;

        Assert(SonyHidReportParser.TryParse(SonyHidIdentity.DualShock4Slim, report, out var state),
            "A public DS4 USB report must parse.");
        Assert((state.Buttons & (ushort)XInputButton.A) != 0, "Cross must map to XInput A.");
        Assert((state.Buttons & SonyHidIdentity.PsButton) != 0, "PS must set the Guide-equivalent bit.");
        var edges = SonyHidReportParser.ButtonTransitions(0, state.Buttons);
        Assert(edges.Any(edge => edge.Button == XInputButton.A && edge.IsPressed),
            "Cross press must be a button edge.");
        Assert(edges.Any(edge => (ushort)edge.Button == SonyHidIdentity.PsButton && edge.IsPressed),
            "PS press must be a button edge.");
        return Task.CompletedTask;
    }

    internal static Task DualSenseUsbBatteryParsesAndShortReportsStayUnknownAsync()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = 128;
        report[2] = 128;
        report[3] = 128;
        report[4] = 128;
        report[8] = 0x08; // hat center
        report[53] = 0x05;
        Assert(SonyHidReportParser.TryParse(SonyHidIdentity.DualSense, report, out var state),
            "A public DualSense USB report must parse.");
        Assert(state.BatteryPercent == 62, "Nibble 5 of 8 must be 62%.");
        Assert(state.Charging == false, "Charging bit unset must be not charging.");

        report[53] = 0x25;
        report[54] = 0x08;
        Assert(SonyHidReportParser.TryParse(SonyHidIdentity.DualSense, report, out var full),
            "A full DualSense USB report must still parse.");
        Assert(full.BatteryPercent == 100, "Full bit must report 100%.");
        Assert(full.Charging == true, "Charging bit 0x08 must be charging.");

        var shortReport = new byte[11];
        shortReport[0] = 0x01;
        shortReport[8] = 0x08;
        Assert(SonyHidReportParser.TryParse(SonyHidIdentity.DualSense, shortReport, out var truncated),
            "A short DualSense report must still parse buttons.");
        Assert(truncated.BatteryPercent is null, "Short reports must not invent a battery percent.");
        Assert(truncated.Charging is null, "Short reports must not invent charging.");
        return Task.CompletedTask;
    }

    internal static Task DualSenseBluetoothBatteryUsesOffsetTwoAsync()
    {
        var report = new byte[78];
        report[0] = 0x31;
        report[2] = 128;
        report[3] = 128;
        report[4] = 128;
        report[5] = 128;
        report[9] = 0x08;
        report[54] = 0x04;
        Assert(SonyHidReportParser.TryParse(SonyHidIdentity.DualSense, report, out var state),
            "A public DualSense BT 0x31 report must parse.");
        Assert(state.BatteryPercent == 50, "BT nibble 4 of 8 must be 50%.");
        return Task.CompletedTask;
    }

    internal static Task DeviceBatteryStatusFormatsWithoutInventingPercentAsync()
    {
        Assert(
            DeviceBatteryStatus.Format("DualSense 已连接", 72, false) == "DualSense 已连接 · 72%",
            "Known percent must append.");
        Assert(
            DeviceBatteryStatus.Format("DualSense 已连接", 72, true) == "DualSense 已连接 · 充电 72%",
            "Charging must be labeled.");
        Assert(
            DeviceBatteryStatus.Format("RC003 已连接", null, null) == "RC003 已连接",
            "Unknown battery must not invent a percent.");
        Assert(DeviceBatteryStatus.Tone(null) == DeviceBatteryTone.Unknown, "Null is unknown.");
        Assert(DeviceBatteryStatus.Tone(14) == DeviceBatteryTone.Critical, "Below 15 is critical.");
        Assert(DeviceBatteryStatus.Tone(15) == DeviceBatteryTone.Low, "15-49 is low.");
        Assert(DeviceBatteryStatus.Tone(50) == DeviceBatteryTone.Ok, "50 and above is ok.");
        return Task.CompletedTask;
    }

    internal static Task Rc003AdvertisedNameIsFailClosedAsync()
    {
        Assert(Rc003DeviceIdentity.IsAdvertisedName("MI RC"), "mi rc must match.");
        Assert(Rc003DeviceIdentity.IsAdvertisedName("小米遥控器 2 Pro"), "Product name must match.");
        Assert(!Rc003DeviceIdentity.IsAdvertisedName("cc pro"), "Unrelated BLE names must not match.");
        Assert(!Rc003DeviceIdentity.IsAdvertisedName(" "), "Blank names must not match.");
        return Task.CompletedTask;
    }

    internal static Task Rc003BatteryLevelParseRejectsOutOfRangeAsync()
    {
        Assert(Rc003BatteryReader.ParseLevel([41]) == 41, "0-100 must pass through.");
        Assert(Rc003BatteryReader.ParseLevel([100]) == 100, "100 is valid.");
        Assert(Rc003BatteryReader.ParseLevel([101]) is null, "Above 100 is unknown.");
        Assert(Rc003BatteryReader.ParseLevel([]) is null, "Empty payload is unknown.");
        return Task.CompletedTask;
    }

    internal static Task Rc003IdentityIsFailClosedAsync()
    {
        Assert(Rc003DeviceIdentity.IsRc003(@"\\?\HID#VID_2717&PID_32B8#7&123"),
            "Classic USB HID path must match RC003.");
        Assert(Rc003DeviceIdentity.IsRc003(@"\\?\HID#VID&012717_PID&32B8#rev"),
            "Packed Bluetooth HID path must match RC003.");
        Assert(!Rc003DeviceIdentity.IsRc003(@"\\?\HID#VID_2717&PID_0001#x"),
            "Same vendor with another product must not match.");
        Assert(!Rc003DeviceIdentity.IsRc003(@"\\?\HID#VID_054C&PID_09CC#x"),
            "A DualShock path must not match RC003.");
        var source = Rc003DeviceIdentity.CreateSource(Rc003DeviceIdentity.Ok);
        Assert(source.Device.VendorId == Rc003DeviceIdentity.VendorId, "OK source must keep VID.");
        Assert(source.Control.Usage == 0x0028, "OK must be HID Return usage.");
        return Task.CompletedTask;
    }

    internal static Task VirtualPadFilterIgnoresSlotsAfterConnectAsync()
    {
        var filter = new VirtualGamepadExclusionFilter();
        filter.ObservePhysicalConnection(0, connected: true);
        Assert(!filter.ShouldIgnore(0), "A physical slot connected first must remain visible.");
        filter.SetVirtualOutputActive(true);
        Assert(!filter.ShouldIgnore(0), "The original physical slot must not be dropped.");
        Assert(filter.ShouldIgnore(1), "A slot that appears after virtual output must be ignored.");
        filter.SetVirtualOutputActive(false);
        Assert(!filter.ShouldIgnore(1), "Disconnecting virtual output must stop exclusion.");
        return Task.CompletedTask;
    }

    internal static Task CaptureClassifierRecognizesSonyAndCableAsync()
    {
        Assert(
            WindowsCaptureEndpointClassifier.Classify(
                "Headset Microphone (DualSense Wireless Controller)",
                @"USB\VID_054C&PID_0CE6&MI_01\6&abc") == WindowsCaptureKind.DualSense,
            "DualSense USB audio must classify as the PS5 pad microphone.");
        Assert(
            WindowsCaptureEndpointClassifier.Classify(
                "Headset Microphone (Wireless Controller)",
                @"USB\VID_054C&PID_09CC&MI_01\7&def") == WindowsCaptureKind.DualShockHeadset,
            "DualShock USB headset capture must classify as the PS4 pad microphone.");
        Assert(
            WindowsCaptureEndpointClassifier.Classify(
                "CABLE Output (VB-Audio Virtual Cable)",
                null) == WindowsCaptureKind.CableOutput,
            "VB-CABLE Output is the Windows endpoint behind the Xiaomi remote, not the device.");
        Assert(
            WindowsCaptureEndpointClassifier.Classify(
                "mi rc",
                @"BTHENUM\VID_2717&PID_32B8") == WindowsCaptureKind.XiaomiRemote,
            "A Xiaomi RC003 capture endpoint must classify as the remote microphone.");
        Assert(
            WindowsCaptureEndpointClassifier.Classify("Microphone Array (Realtek)", @"PCI\VEN_10EC") ==
            WindowsCaptureKind.Other,
            "Built-in arrays must stay unclassified.");
        Assert(WindowsCaptureEndpointClassifier.IsCableInput("CABLE Input (VB-Audio Virtual Cable)"),
            "CABLE Input is the render side RC003 PCM must hit.");
        return Task.CompletedTask;
    }

    internal static Task ConnectedMicrophonesAreDeviceCentricAsync()
    {
        var dualSense = new WindowsCaptureEndpoint(
            "ds-1",
            "Headset Microphone (DualSense Wireless Controller)",
            @"USB\VID_054C&PID_0CE6&MI_01",
            WindowsCaptureKind.DualSense,
            false,
            false);
        var cable = new WindowsCaptureEndpoint(
            "cable-1",
            "CABLE Output (VB-Audio Virtual Cable)",
            null,
            WindowsCaptureKind.CableOutput,
            true,
            false);
        var realtek = new WindowsCaptureEndpoint(
            "onboard-1",
            "Microphone Array (Realtek)",
            @"PCI\VEN_10EC",
            WindowsCaptureKind.Other,
            false,
            false);

        var idle = ConnectedMicrophoneCatalog.List([cable, realtek], rc003Connected: false);
        Assert(idle.Count == 0, "CABLE Output and laptop arrays must not appear as connected-device microphones.");

        var remote = ConnectedMicrophoneCatalog.List(
            [cable, realtek],
            rc003Connected: true,
            new Dictionary<string, string> { [ConnectedMicrophoneCatalog.Rc003Id] = "客厅遥控器" });
        Assert(remote.Count == 1, "A connected Xiaomi remote is one real microphone.");
        Assert(remote[0].Kind == ConnectedMicrophoneKind.Rc003, "The row must be the Xiaomi remote, not CABLE.");
        Assert(remote[0].DeviceName == "小米遥控器 2 Pro", "The device identity is the remote.");
        Assert(remote[0].DisplayName == "客厅遥控器", "A manual alias must replace the default label.");
        Assert(remote[0].CaptureEndpointId == "cable-1", "CABLE Output is only the Windows endpoint behind the remote.");
        Assert(remote[0].IsWindowsDefault, "Default capture on CABLE means the remote is the current microphone.");
        Assert(!remote[0].DeviceName.Contains("CABLE", StringComparison.OrdinalIgnoreCase),
            "CABLE must never be the device name.");

        var nativeRemote = new WindowsCaptureEndpoint(
            "xm-1",
            "mi rc",
            @"BTHENUM\VID_2717&PID_32B8",
            WindowsCaptureKind.XiaomiRemote,
            false,
            false);
        var native = ConnectedMicrophoneCatalog.List([nativeRemote, cable], rc003Connected: false);
        Assert(native.Count == 1, "A native Xiaomi capture endpoint is enough to list the remote.");
        Assert(native[0].CaptureEndpointId == "xm-1", "A native Xiaomi endpoint must win over CABLE.");
        Assert(!native[0].RequiresVoiceBridge, "A native endpoint does not need the ATVV bridge.");

        var pads = ConnectedMicrophoneCatalog.List([dualSense, realtek], rc003Connected: false);
        Assert(pads.Count == 1, "A DualSense USB mic is listed without the laptop array.");
        Assert(pads[0].Kind == ConnectedMicrophoneKind.DualSense, "The DualSense row must keep its kind.");
        Assert(!pads[0].RequiresVoiceBridge, "DualSense USB audio is already a Windows capture device.");

        var hidOnly = ConnectedMicrophoneCatalog.List([], rc003Connected: true);
        Assert(hidOnly.Count == 1, "A connected Xiaomi remote must still appear if WASAPI listing fails.");
        Assert(hidOnly[0].CaptureEndpointId is null, "Without an endpoint the remote cannot yet be the Windows default.");
        return Task.CompletedTask;
    }

    internal static Task CaptureCatalogListsActiveEndpointsWithoutComCastingAsync()
    {
        var endpoints = WindowsAudioCaptureCatalog.ListActive();
        Assert(endpoints.Count >= 0, "Listing capture endpoints must not throw.");
        foreach (var endpoint in endpoints)
        {
            Assert(!string.IsNullOrWhiteSpace(endpoint.Id), "Every capture endpoint must have an id.");
            Assert(!string.IsNullOrWhiteSpace(endpoint.Name), "Every capture endpoint must have a name.");
        }

        var render = WindowsAudioCaptureCatalog.ListActiveRender();
        Assert(render.Count >= 0, "Listing render endpoints must not throw.");
        return Task.CompletedTask;
    }

    internal static Task MicrophoneAliasesRoundTripAndSanitizeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "KeyPilot.MicAlias." + Guid.NewGuid().ToString("N"), "aliases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new MicrophoneAliasStore(path);
        store.Set("rc003", "  客厅遥控器  ");
        Assert(store.Snapshot()["rc003"] == "客厅遥控器", "Aliases must trim and drop control characters.");
        store.Set("rc003", "   ");
        Assert(!store.Snapshot().ContainsKey("rc003"), "Clearing an alias must remove it.");
        store.Set("dualsense", new string('麦', 80));
        Assert(store.Snapshot()["dualsense"].Length == MicrophoneAliasStore.MaximumLength,
            "Aliases must be bounded.");
        var reloaded = new MicrophoneAliasStore(path);
        Assert(reloaded.Snapshot()["dualsense"].Length == MicrophoneAliasStore.MaximumLength,
            "Aliases must survive a process restart.");
        return Task.CompletedTask;
    }

    internal static Task ImaAdpcmRoundTripSilenceStaysBoundedAsync()
    {
        var decoder = new ImaAdpcmDecoder();
        decoder.Reset();
        var silent = decoder.Decode([0x00, 0x00, 0x00, 0x00]);
        Assert(silent.Length == 8, "Each ADPCM byte yields two PCM samples.");
        Assert(silent.All(sample => sample is >= -64 and <= 64), "Zero nibbles must stay near silence.");
        var session = new AtvvSession();
        Assert(session.HandleAudio([0x00, 0x00]) is { Length: 0 },
            "Audio before AUDIO_START must be dropped.");
        Assert(session.HandleControl([AtvvProtocol.OpcodeMicButton]) == AtvvControlEvent.MicButton,
            "Mic button opcode must be recognized.");
        CollectionAssertEqual(
            AtvvProtocol.MicOpenCommand(0),
            [0x0C, 0x00, 0x00],
            "Host MIC_OPEN before caps uses the three-byte form.");
        CollectionAssertEqual(
            AtvvProtocol.MicOpenCommand(0x0100),
            [0x0C, 0x00],
            "Host MIC_OPEN after v1.0 uses the two-byte form.");
        Assert(session.HandleControl([AtvvProtocol.OpcodeAudioStart, 0, 0, 3]) == AtvvControlEvent.AudioStart,
            "Audio start must open the session.");
        Assert(session.MicOpen, "Audio start must mark the microphone open.");
        Assert(session.HandleControl([AtvvProtocol.OpcodeAudioStop]) == AtvvControlEvent.AudioStop,
            "Audio stop must close the session.");
        Assert(!session.MicOpen, "Audio stop must mark the microphone closed.");
        Assert(session.HandleAudio(new byte[AtvvProtocol.DefaultFrameSize]) is { Length: 0 },
            "Leftover audio in the late-stop guard must be discarded.");

        session.HandleControl([AtvvProtocol.OpcodeAudioStart, 0, 0, 3]);
        Assert(session.HandleAudio(new byte[60]) is { Length: 0 },
            "A partial ATVV frame must wait for the rest of the 120-byte block.");
        var assembled = session.HandleAudio(new byte[60]);
        Assert(assembled.Length == 240, "One 120-byte frame must decode to 240 PCM samples.");
        Assert(AtvvPcmPostprocessor.Process([1, 2, 3, 4]).Length == 4,
            "The 3-tap smoother must keep the sample count.");
        Assert(session.HandleControl([AtvvProtocol.OpcodeAudioSync, 0, 0, 0, 0, 0, 0]) ==
            AtvvControlEvent.AudioSync,
            "AUDIO_SYNC must be recognized.");
        return Task.CompletedTask;
    }

    private static void CollectionAssertEqual(byte[] actual, byte[] expected, string message)
    {
        Assert(actual.SequenceEqual(expected), message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
