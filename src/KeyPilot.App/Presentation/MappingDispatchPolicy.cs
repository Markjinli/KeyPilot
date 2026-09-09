using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Triggers;

namespace KeyPilot.App.Presentation;

internal static class MappingDispatchPolicy
{
    public static bool CanRequestOriginalSuppression(InputSource? source) =>
        source is
        {
            Device.Kind: InputDeviceKind.Keyboard,
            Control.Kind: InputControlKind.KeyboardScanCode
        };

    public static bool HasEnabledSuppressedMapping(
        KeyPilotConfiguration configuration,
        InputSource source)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        if (!configuration.IsMappingEnabled)
        {
            return false;
        }

        var profile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= configuration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled);
        return profile?.IsEnabled == true && profile.Mappings.Any(mapping =>
            mapping is { IsEnabled: true, SuppressOriginal: true } &&
            MappingTriggerStateMachine.MatchesSource(mapping.Source, source));
    }
}
