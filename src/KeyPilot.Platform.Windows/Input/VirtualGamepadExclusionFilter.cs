namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Drops XInput slots that appeared after KeyPilot connected its own virtual pad, so the virtual
/// device cannot re-enter <see cref="XInputGamepadSource"/> and recurse into mappings.
/// </summary>
public sealed class VirtualGamepadExclusionFilter
{
    private readonly HashSet<int> _physicalSlots = [];
    private readonly object _gate = new();
    private bool _virtualOutputActive;

    public void ObservePhysicalConnection(int userIndex, bool connected)
    {
        lock (_gate)
        {
            if (_virtualOutputActive)
            {
                return;
            }

            if (connected)
            {
                _physicalSlots.Add(userIndex);
            }
            else
            {
                _physicalSlots.Remove(userIndex);
            }
        }
    }

    public void SetVirtualOutputActive(bool active)
    {
        lock (_gate)
        {
            _virtualOutputActive = active;
            if (!active)
            {
                return;
            }
        }
    }

    public bool ShouldIgnore(int userIndex)
    {
        lock (_gate)
        {
            return _virtualOutputActive && !_physicalSlots.Contains(userIndex);
        }
    }
}
