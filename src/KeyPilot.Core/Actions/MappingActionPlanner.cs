using KeyPilot.Core.Input;

namespace KeyPilot.Core.Actions;

/// <summary>
/// Converts declarative mapping actions into a bounded, ordered plan. This class is pure: it
/// performs no delays, input injection, process launch, file access, or network access.
/// </summary>
public sealed class MappingActionPlanner
{
    public const int DefaultSpecialKeyHoldMilliseconds = 30;

    private const int MaximumDepth = 8;
    private const int MaximumOperations = 2_048;
    private const int MaximumHoldMilliseconds = 10_000;
    private const int MaximumDelayMilliseconds = 600_000;

    public ActionPlan Plan(
        MappingAction action,
        IEnumerable<SpecialKeySlot>? specialKeySlots = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var slots = BuildSlotIndex(specialKeySlots);
        var operations = new List<ActionPlanOperation>();
        var ancestors = new HashSet<MappingAction>(ReferenceEqualityComparer.Instance);
        Expand(action, "action", slots, operations, ancestors, depth: 0);
        return new ActionPlan(operations);
    }

    private static IReadOnlyDictionary<int, SpecialKeySlot> BuildSlotIndex(
        IEnumerable<SpecialKeySlot>? slots)
    {
        if (slots is null)
        {
            return new Dictionary<int, SpecialKeySlot>();
        }

        var result = new Dictionary<int, SpecialKeySlot>();
        foreach (var slot in slots)
        {
            if (slot is null || !result.TryAdd(slot.Number, slot))
            {
                throw new ActionPlanningException(
                    "specialKeySlots",
                    "slot entries must be non-null and have unique numbers.");
            }
        }

        return result;
    }

    private static void Expand(
        MappingAction action,
        string path,
        IReadOnlyDictionary<int, SpecialKeySlot> slots,
        ICollection<ActionPlanOperation> operations,
        ISet<MappingAction> ancestors,
        int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new ActionPlanningException(path, $"nesting exceeds {MaximumDepth} levels.");
        }

        if (!ancestors.Add(action))
        {
            throw new ActionPlanningException(path, "the macro contains a reference cycle.");
        }

        try
        {
            switch (action)
            {
                case SendKeyAction sendKey:
                    ExpandSendKey(sendKey, path, operations);
                    break;

                case ShortcutAction shortcut:
                    ExpandShortcut(shortcut, path, operations);
                    break;

                case LaunchProgramAction launch:
                    RequireText(launch.FilePath, $"{path}.filePath");
                    Add(
                        operations,
                        new LaunchProgramOperation(
                            launch.FilePath,
                            launch.Arguments,
                            launch.WorkingDirectory),
                        path);
                    break;

                case OpenUriAction openUri:
                    if (!System.Uri.TryCreate(openUri.Uri, UriKind.Absolute, out var parsedUri)
                        || parsedUri is null
                        || string.IsNullOrWhiteSpace(parsedUri.Scheme))
                    {
                        throw new ActionPlanningException($"{path}.uri", "an absolute URI is required.");
                    }

                    Add(operations, new OpenUriOperation(openUri.Uri), path);
                    break;

                case RunScriptAction script:
                    RequireText(script.ScriptPath, $"{path}.scriptPath");
                    Add(operations, new RunScriptOperation(script.ScriptPath, script.Arguments), path);
                    break;

                case MediaControlAction media:
                    if (!Enum.IsDefined(media.Command))
                    {
                        throw new ActionPlanningException(
                            $"{path}.command",
                            "the media command is unsupported.");
                    }

                    Add(operations, new MediaControlOperation(media.Command), path);
                    break;

                case VolumeControlAction volume:
                    ExpandVolumeControl(volume, path, operations);
                    break;

                case CopyInputToOutputAction copy:
                    ExpandCopiedOutput(copy, path, operations);
                    break;

                case EmitSpecialKeyAction special:
                    ExpandSpecialKey(special, path, slots, operations);
                    break;

                case DelayAction delay:
                    ValidateDelay(delay.Milliseconds, $"{path}.milliseconds");
                    Add(operations, new DelayOperation(delay.Milliseconds), path);
                    break;

                case MacroAction macro:
                    if (macro.Steps is null || macro.Steps.Count == 0)
                    {
                        throw new ActionPlanningException($"{path}.steps", "a macro needs at least one step.");
                    }

                    for (var index = 0; index < macro.Steps.Count; index++)
                    {
                        var step = macro.Steps[index]
                            ?? throw new ActionPlanningException(
                                $"{path}.steps[{index}]",
                                "a macro step cannot be null.");
                        Expand(step, $"{path}.steps[{index}]", slots, operations, ancestors, depth + 1);
                    }

                    break;

                default:
                    throw new ActionPlanningException(path, $"unsupported action type {action.GetType().Name}.");
            }
        }
        finally
        {
            ancestors.Remove(action);
        }
    }

    private static void ExpandSendKey(
        SendKeyAction action,
        string path,
        ICollection<ActionPlanOperation> operations)
    {
        if (action.Target is null)
        {
            throw new ActionPlanningException($"{path}.target", "an input target is required.");
        }

        var target = new ControlInjectionTarget(action.Target);
        switch (action.Transition)
        {
            case KeyTransition.Down:
                Add(operations, new InjectInputOperation(target, InputInjectionPhase.Down), path);
                break;
            case KeyTransition.Up:
                Add(operations, new InjectInputOperation(target, InputInjectionPhase.Up), path);
                break;
            case KeyTransition.Press:
                ValidateHold(action.HoldMilliseconds, $"{path}.holdMilliseconds");
                AddPress(operations, target, action.HoldMilliseconds, path);
                break;
            default:
                throw new ActionPlanningException($"{path}.transition", "the key transition is unsupported.");
        }
    }

    private static void ExpandShortcut(
        ShortcutAction action,
        string path,
        ICollection<ActionPlanOperation> operations)
    {
        if (action.Keys is null || action.Keys.Count == 0)
        {
            throw new ActionPlanningException($"{path}.keys", "a shortcut needs at least one key.");
        }

        ValidateHold(action.HoldMilliseconds, $"{path}.holdMilliseconds");
        var targets = new List<ControlInjectionTarget>(action.Keys.Count);
        for (var index = 0; index < action.Keys.Count; index++)
        {
            var key = action.Keys[index]
                ?? throw new ActionPlanningException($"{path}.keys[{index}]", "a key cannot be null.");
            targets.Add(new ControlInjectionTarget(key));
        }

        foreach (var target in targets)
        {
            Add(operations, new InjectInputOperation(target, InputInjectionPhase.Down), path);
        }

        Add(operations, new DelayOperation(action.HoldMilliseconds), path);

        // Releasing in reverse order mirrors a physical chord and limits stuck-modifier risk.
        for (var index = targets.Count - 1; index >= 0; index--)
        {
            Add(operations, new InjectInputOperation(targets[index], InputInjectionPhase.Up), path);
        }
    }

    private static void ExpandSpecialKey(
        EmitSpecialKeyAction action,
        string path,
        IReadOnlyDictionary<int, SpecialKeySlot> slots,
        ICollection<ActionPlanOperation> operations)
    {
        if (!slots.TryGetValue(action.SlotNumber, out var slot))
        {
            throw new ActionPlanningException(
                $"{path}.slotNumber",
                $"special-key slot {action.SlotNumber} does not exist.");
        }

        if (slot.Source is null)
        {
            throw new ActionPlanningException(
                $"{path}.slotNumber",
                $"special-key slot {action.SlotNumber} has not been captured.");
        }

        if (slot.Source.Control.Kind == InputControlKind.InputChord)
        {
            LogicalOutputTarget chordTarget;
            try
            {
                chordTarget = LogicalOutputTargetCodec.FromInputSource(slot.Source);
            }
            catch (LogicalOutputTargetCodecException exception)
            {
                throw new ActionPlanningException(
                    $"{path}.slotNumber",
                    $"the captured chord cannot be emitted: {exception.Message}");
            }

            ExpandLogicalOutput(
                chordTarget,
                DefaultSpecialKeyHoldMilliseconds,
                path,
                operations);
            return;
        }

        AddPress(
            operations,
            new CapturedInputInjectionTarget(slot.Source),
            DefaultSpecialKeyHoldMilliseconds,
            path);
    }

    private static void ExpandVolumeControl(
        VolumeControlAction action,
        string path,
        ICollection<ActionPlanOperation> operations)
    {
        if (!Enum.IsDefined(action.Command))
        {
            throw new ActionPlanningException(
                $"{path}.command",
                "the volume command is unsupported.");
        }

        if (action.Command == VolumeControlCommand.SetLevelPercent)
        {
            if (action.LevelPercent is < 0 or > 100 || !action.LevelPercent.HasValue)
            {
                throw new ActionPlanningException(
                    $"{path}.levelPercent",
                    "an exact volume level from 0 through 100 percent is required.");
            }
        }
        else if (action.LevelPercent.HasValue)
        {
            throw new ActionPlanningException(
                $"{path}.levelPercent",
                "a level is accepted only by the exact-volume command.");
        }

        Add(
            operations,
            new VolumeControlOperation(action.Command, action.LevelPercent),
            path);
    }

    private static void ExpandCopiedOutput(
        CopyInputToOutputAction action,
        string path,
        ICollection<ActionPlanOperation> operations)
    {
        LogicalOutputTarget target;
        try
        {
            target = LogicalOutputTargetCodec.Decode(action.EncodedTarget);
        }
        catch (LogicalOutputTargetCodecException exception)
        {
            throw new ActionPlanningException(
                $"{path}.encodedTarget",
                exception.Message);
        }

        ValidateHold(action.HoldMilliseconds, $"{path}.holdMilliseconds");
        ExpandLogicalOutput(target, action.HoldMilliseconds, path, operations);
    }

    private static void ExpandLogicalOutput(
        LogicalOutputTarget target,
        int holdMilliseconds,
        string path,
        ICollection<ActionPlanOperation> operations)
    {
        switch (target.Family)
        {
            case LogicalOutputFamily.Gamepad:
                break;
            case LogicalOutputFamily.Hid:
                throw new ActionPlanningException(
                    $"{path}.encodedTarget",
                    "HID output is unavailable because no virtual HID backend is installed.");
            case LogicalOutputFamily.Keyboard:
                break;
            default:
                throw new ActionPlanningException(
                    $"{path}.encodedTarget",
                    "the logical output family is unsupported.");
        }

        var targets = target.Controls
            .Select(control => new ControlInjectionTarget(control))
            .ToArray();
        foreach (var injectionTarget in targets)
        {
            Add(
                operations,
                new InjectInputOperation(injectionTarget, InputInjectionPhase.Down),
                path);
        }

        Add(operations, new DelayOperation(holdMilliseconds), path);
        for (var index = targets.Length - 1; index >= 0; index--)
        {
            Add(
                operations,
                new InjectInputOperation(targets[index], InputInjectionPhase.Up),
                path);
        }
    }

    private static void AddPress(
        ICollection<ActionPlanOperation> operations,
        InputInjectionTarget target,
        int holdMilliseconds,
        string path)
    {
        Add(operations, new InjectInputOperation(target, InputInjectionPhase.Down), path);
        Add(operations, new DelayOperation(holdMilliseconds), path);
        Add(operations, new InjectInputOperation(target, InputInjectionPhase.Up), path);
    }

    private static void Add(
        ICollection<ActionPlanOperation> operations,
        ActionPlanOperation operation,
        string path)
    {
        if (operations.Count >= MaximumOperations)
        {
            throw new ActionPlanningException(path, $"the expanded plan exceeds {MaximumOperations} operations.");
        }

        operations.Add(operation);
    }

    private static void ValidateHold(int milliseconds, string path)
    {
        if (milliseconds is < 1 or > MaximumHoldMilliseconds)
        {
            throw new ActionPlanningException(path, $"the hold must be between 1 and {MaximumHoldMilliseconds} ms.");
        }
    }

    private static void ValidateDelay(int milliseconds, string path)
    {
        if (milliseconds is < 0 or > MaximumDelayMilliseconds)
        {
            throw new ActionPlanningException(path, $"the delay must be between 0 and {MaximumDelayMilliseconds} ms.");
        }
    }

    private static void RequireText(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ActionPlanningException(path, "a non-empty value is required.");
        }
    }
}
