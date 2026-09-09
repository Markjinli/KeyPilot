using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>Creates the production user-session action pipeline with one shared recursion marker.</summary>
public static class WindowsActionExecutorFactory
{
    public static WindowsActionExecutor CreateDefault(InputInjectionMarker originMarker)
    {
        var inputBackend = new WindowsSendInputBackend();
        return new WindowsActionExecutor(
            inputBackend,
            new WindowsExternalActionBackend(),
            new SystemActionDelayScheduler(),
            new WindowsSystemControlBackend(inputBackend, CoreAudioEndpointVolume.Instance),
            originMarker,
            new WindowsViGEmXbox360Backend());
    }
}
