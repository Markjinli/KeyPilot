using System.Runtime.InteropServices;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Uses standard Windows media virtual keys for relative commands and Core Audio for an exact
/// default-render-endpoint volume level.
/// </summary>
public sealed class WindowsSystemControlBackend : ISystemControlBackend
{
    internal const int VirtualKeyMediaNextTrack = 0xB0;
    internal const int VirtualKeyMediaPreviousTrack = 0xB1;
    internal const int VirtualKeyMediaStop = 0xB2;
    internal const int VirtualKeyMediaPlayPause = 0xB3;
    internal const int VirtualKeyVolumeMute = 0xAD;
    internal const int VirtualKeyVolumeDown = 0xAE;
    internal const int VirtualKeyVolumeUp = 0xAF;

    private readonly IInputInjectionBackend _inputBackend;
    private readonly ICoreAudioEndpointVolume _endpointVolume;

    public WindowsSystemControlBackend()
        : this(new WindowsSendInputBackend(), CoreAudioEndpointVolume.Instance)
    {
    }

    internal WindowsSystemControlBackend(
        IInputInjectionBackend inputBackend,
        ICoreAudioEndpointVolume endpointVolume)
    {
        _inputBackend = inputBackend ?? throw new ArgumentNullException(nameof(inputBackend));
        _endpointVolume = endpointVolume ?? throw new ArgumentNullException(nameof(endpointVolume));
    }

    public void ValidateMediaControl(MediaControlOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(operation.Command))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                "The media-control command is unsupported.");
        }
    }

    public void ValidateVolumeControl(VolumeControlOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(operation.Command))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                "The volume-control command is unsupported.");
        }

        if (operation.Command == VolumeControlCommand.SetLevelPercent)
        {
            if (!operation.LevelPercent.HasValue || operation.LevelPercent is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    "An exact system volume from 0 through 100 percent is required.");
            }
        }
        else if (operation.LevelPercent.HasValue)
        {
            throw new ArgumentException(
                "A volume level is accepted only by SetLevelPercent.",
                nameof(operation));
        }
    }

    public ValueTask ExecuteMediaControlAsync(
        MediaControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken)
    {
        ValidateMediaControl(operation);
        var virtualKey = operation.Command switch
        {
            MediaControlCommand.PlayPause => VirtualKeyMediaPlayPause,
            MediaControlCommand.Stop => VirtualKeyMediaStop,
            MediaControlCommand.PreviousTrack => VirtualKeyMediaPreviousTrack,
            MediaControlCommand.NextTrack => VirtualKeyMediaNextTrack,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return PressVirtualKeyAsync(virtualKey, originMarker, cancellationToken);
    }

    public ValueTask ExecuteVolumeControlAsync(
        VolumeControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken)
    {
        ValidateVolumeControl(operation);
        return operation.Command switch
        {
            VolumeControlCommand.Increase =>
                PressVirtualKeyAsync(VirtualKeyVolumeUp, originMarker, cancellationToken),
            VolumeControlCommand.Decrease =>
                PressVirtualKeyAsync(VirtualKeyVolumeDown, originMarker, cancellationToken),
            VolumeControlCommand.ToggleMute =>
                PressVirtualKeyAsync(VirtualKeyVolumeMute, originMarker, cancellationToken),
            VolumeControlCommand.SetLevelPercent =>
                SetExactVolumeAsync(operation.LevelPercent!.Value, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private async ValueTask PressVirtualKeyAsync(
        int virtualKey,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken)
    {
        if (originMarker.Value == 0)
        {
            throw new ArgumentException(
                "A non-zero injection marker is required.",
                nameof(originMarker));
        }

        var target = new ControlInjectionTarget(new InputControlId
        {
            Kind = InputControlKind.VirtualKey,
            Code = virtualKey
        });
        await _inputBackend.InjectAsync(
                new InputInjectionRequest(target, InputInjectionPhase.Down, originMarker),
                cancellationToken)
            .ConfigureAwait(false);

        var released = false;
        try
        {
            await _inputBackend.InjectAsync(
                    new InputInjectionRequest(target, InputInjectionPhase.Up, originMarker),
                    cancellationToken)
                .ConfigureAwait(false);
            released = true;
        }
        finally
        {
            if (!released)
            {
                try
                {
                    await _inputBackend.InjectAsync(
                            new InputInjectionRequest(
                                target,
                                InputInjectionPhase.Up,
                                originMarker),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original media-key failure after one best-effort release.
                }
            }
        }
    }

    private ValueTask SetExactVolumeAsync(
        int levelPercent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _endpointVolume.SetMasterVolumeScalar(levelPercent / 100f);
            return ValueTask.CompletedTask;
        }
        catch (Exception exception) when (exception is COMException
                                              or InvalidCastException
                                              or InvalidOperationException
                                              or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                $"Unable to set system volume to {levelPercent}% through the Windows Core Audio " +
                $"default render endpoint: {exception.Message}",
                exception);
        }
    }
}

internal interface ICoreAudioEndpointVolume
{
    void SetMasterVolumeScalar(float scalar);
}

internal sealed class CoreAudioEndpointVolume : ICoreAudioEndpointVolume
{
    private static readonly Guid DeviceEnumeratorClassId =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioEndpointVolumeInterfaceId =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const uint ClsContextAll = 23;

    public static CoreAudioEndpointVolume Instance { get; } = new();

    private CoreAudioEndpointVolume()
    {
    }

    public void SetMasterVolumeScalar(float scalar)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Core Audio is required.");
        }

        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                DeviceEnumeratorClassId,
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    "The Windows multimedia device enumerator is unavailable.");
            enumeratorObject = Activator.CreateInstance(enumeratorType)
                ?? throw new InvalidOperationException(
                    "The Windows multimedia device enumerator could not be created.");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            ThrowForHResult(
                enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.Render,
                    ERole.Multimedia,
                    out device),
                "The default Windows render endpoint is unavailable.");

            var endpointInterface = AudioEndpointVolumeInterfaceId;
            ThrowForHResult(
                device.Activate(
                    ref endpointInterface,
                    ClsContextAll,
                    IntPtr.Zero,
                    out endpointObject),
                "The default endpoint does not expose volume control.");
            var endpointVolume = (IAudioEndpointVolume)endpointObject;
            var eventContext = Guid.Empty;
            ThrowForHResult(
                endpointVolume.SetMasterVolumeLevelScalar(scalar, ref eventContext),
                "Windows rejected the requested endpoint volume.");
        }
        finally
        {
            ReleaseComObject(endpointObject);
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static void ThrowForHResult(int result, string message)
    {
        if (result < 0)
        {
            throw new COMException(message, result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfaceObject);

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    }
}
