using KeyPilot.DriverClient;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.18362")]

namespace KeyPilot.DriverPolicyModelTests;

internal readonly record struct Decision(bool Suppress, ulong Generation, ulong RuleId, string Phase, ulong Sequence);
internal readonly record struct Rule(
    ushort MakeCode,
    bool Suppress,
    ulong RuleId,
    ushort RequiredFlags = 0,
    ushort IgnoredFlags = 0x0006);
internal readonly record struct Key(ushort MakeCode, ushort PrefixFlags);
internal readonly record struct Press(ulong Lease, ulong Generation, ulong RuleId, bool Suppress);

internal sealed class EmergencyBypassFleet
{
    public int ActiveDeviceCount { get; private set; }

    public EmergencyDeviceModel CreateDevice() => new(this);

    internal void Latch() => ActiveDeviceCount++;

    internal void ResetLatched()
    {
        if (ActiveDeviceCount <= 0) throw new InvalidOperationException("Emergency count underflow.");
        ActiveDeviceCount--;
    }
}

internal sealed class EmergencyDeviceModel(EmergencyBypassFleet fleet)
{
    private bool _armed;
    private bool _latched;

    public void Arm() => _armed = true;

    public void FireTimer()
    {
        if (!_armed || _latched) return;
        _armed = false;
        _latched = true;
        fleet.Latch();
    }

    public void D0Exit() => Reset();

    public void Cleanup() => Reset();

    private void Reset()
    {
        _armed = false;
        if (!_latched) return;
        _latched = false;
        fleet.ResetLatched();
    }
}

internal sealed class FailOpenPolicyModel
{
    private readonly int _eventCapacity;
    private readonly Dictionary<Key, Rule> _rules = new();
    private readonly Dictionary<Key, Press> _presses = new();
    private ulong _lease;
    private ulong _deadline;
    private ulong _progressDeadline;
    private ulong _duration;
    private ulong _generation;
    private ulong _nextSequence;
    private ulong _deliveredSequence;
    private ulong _acknowledgedSequence;
    private ulong _emergencyDeadline;
    private int _events;
    private bool _leftCtrlDown;
    private bool _leftShiftDown;
    private bool _f12Down;

    public FailOpenPolicyModel(int eventCapacity)
    {
        _eventCapacity = eventCapacity;
    }

    public bool IsFailOpen => _lease == 0;
    public bool Overflowed { get; private set; }
    public bool TrackingLost { get; private set; }
    public bool ProgressTimedOut { get; private set; }
    public bool EmergencyBypassActive { get; private set; }

    public bool Acquire(ulong lease, ulong now, ulong duration)
    {
        if (TrackingLost || EmergencyBypassActive)
        {
            FailOpen();
            return false;
        }
        _lease = lease;
        _deadline = now + duration;
        _progressDeadline = 0;
        _duration = duration;
        _generation = 0;
        _nextSequence = 0;
        _deliveredSequence = 0;
        _acknowledgedSequence = 0;
        _rules.Clear();
        _events = 0;
        Overflowed = false;
        ProgressTimedOut = false;
        return true;
    }

    public bool Replace(ulong lease, ulong generation, params Rule[] rules)
    {
        if (_lease == 0 || lease != _lease || generation <= _generation || rules.Length > 512 ||
            rules.Any(rule => rule.MakeCode == 0 || rule.RuleId == 0 || !rule.Suppress) ||
            rules.Any(rule => !HasExactPrefix(rule)) ||
            rules.Any(rule => rule.Suppress &&
                ((rule.MakeCode is 0x001d or 0x0038) && rule.RequiredFlags == 0 ||
                 (rule.MakeCode is 0x002a or 0x0058) && rule.RequiredFlags == 0 ||
                 rule.MakeCode == 0x0053 && rule.RequiredFlags == 0x0002)) ||
            rules.Select(rule => rule.RuleId).Distinct().Count() != rules.Length ||
            rules.Select(rule => new Key(rule.MakeCode, rule.RequiredFlags)).Distinct().Count() != rules.Length)
        {
            FailOpen();
            return false;
        }

        _rules.Clear();
        foreach (var rule in rules)
        {
            _rules.Add(new Key(rule.MakeCode, rule.RequiredFlags), rule);
        }
        _generation = generation;
        return true;
    }

    public bool Heartbeat(ulong lease, ulong generation, ulong acknowledgedSequence, ulong now)
    {
        if (!ValidateLease(now) || lease != _lease || generation != _generation ||
            acknowledgedSequence < _acknowledgedSequence || acknowledgedSequence > _deliveredSequence)
        {
            FailOpen();
            return false;
        }
        if (acknowledgedSequence > _acknowledgedSequence)
        {
            _acknowledgedSequence = acknowledgedSequence;
            _progressDeadline = _acknowledgedSequence == _nextSequence ? 0 : now + _duration;
        }
        else if (_acknowledgedSequence == _nextSequence)
        {
            _progressDeadline = 0;
        }
        _deadline = now + _duration;
        return true;
    }

    public void ReadAllEvents(ulong now)
    {
        if (!ValidateLease(now))
        {
            return;
        }
        _deliveredSequence = _nextSequence;
        _events = 0;
    }

    public void RecoverDevice()
    {
        TrackingLost = false;
        _presses.Clear();
        ResetEmergencyChord();
    }

    public void SuspendDevice()
    {
        TrackingLost = true;
        _presses.Clear();
        ResetEmergencyChord();
        FailOpen();
    }

    public void AdvanceEmergencyTimer(ulong now)
    {
        if (_emergencyDeadline != 0 && now >= _emergencyDeadline &&
            _leftCtrlDown && _leftShiftDown && _f12Down)
        {
            _emergencyDeadline = 0;
            EmergencyBypassActive = true;
            FailOpen();
        }
    }

    public Decision Input(ushort makeCode, bool isUp, ulong now, ushort prefixFlags = 0)
    {
        var key = new Key(makeCode, (ushort)(prefixFlags & 0x0006));
        AdvanceEmergencyTimer(now);
        if (UpdateEmergencyChord(makeCode, isUp, prefixFlags, now))
        {
            return default;
        }
        if (makeCode == 0x00ff)
        {
            _presses.Clear();
            TrackingLost = true;
            FailOpen();
            return default;
        }
        if (!ValidateLease(now))
        {
            if (isUp)
            {
                _presses.Remove(key);
            }
            else
            {
                _presses.TryAdd(key, new Press(0, 0, 0, false));
            }
            return default;
        }

        if (isUp)
        {
            if (!_presses.Remove(key, out var released) || released.Lease != _lease || released.RuleId == 0)
            {
                return default;
            }
            return QueueOrFail(released.Suppress, released.Generation, released.RuleId, "up", now);
        }

        if (_presses.TryGetValue(key, out var repeated))
        {
            if (repeated.Lease != _lease || repeated.RuleId == 0)
            {
                return default;
            }
            return QueueOrFail(repeated.Suppress, repeated.Generation, repeated.RuleId, "repeat", now);
        }

        if (!_rules.TryGetValue(key, out var rule))
        {
            _presses.Add(key, new Press(_lease, 0, 0, false));
            return default;
        }
        var press = new Press(_lease, _generation, rule.RuleId, rule.Suppress);
        _presses.Add(key, press);
        return QueueOrFail(press.Suppress, press.Generation, press.RuleId, "down", now);
    }

    public void Disconnect() => FailOpen();

    private bool UpdateEmergencyChord(ushort makeCode, bool isUp, ushort prefixFlags, ulong now)
    {
        if ((prefixFlags & 0x0006) != 0 || makeCode is not (0x001d or 0x002a or 0x0058))
        {
            return false;
        }

        if (makeCode == 0x001d) _leftCtrlDown = !isUp;
        if (makeCode == 0x002a) _leftShiftDown = !isUp;
        if (makeCode == 0x0058) _f12Down = !isUp;
        if (_leftCtrlDown && _leftShiftDown && _f12Down)
        {
            if (_emergencyDeadline == 0 && !EmergencyBypassActive)
            {
                _emergencyDeadline = now + 2000;
            }
        }
        else
        {
            _emergencyDeadline = 0;
            EmergencyBypassActive = false;
        }
        return true;
    }

    private void ResetEmergencyChord()
    {
        _leftCtrlDown = false;
        _leftShiftDown = false;
        _f12Down = false;
        _emergencyDeadline = 0;
        EmergencyBypassActive = false;
    }

    private static bool HasExactPrefix(Rule rule) =>
        rule.RequiredFlags == 0 && rule.IgnoredFlags == 0x0006 ||
        rule.RequiredFlags == 0x0002 && rule.IgnoredFlags == 0x0004 ||
        rule.RequiredFlags == 0x0004 && rule.IgnoredFlags == 0x0002;

    private Decision QueueOrFail(bool suppress, ulong generation, ulong ruleId, string phase, ulong now)
    {
        if (_events == _eventCapacity)
        {
            Overflowed = true;
            FailOpen();
            return default;
        }
        _events++;
        var sequence = ++_nextSequence;
        if (_progressDeadline == 0)
        {
            _progressDeadline = now + _duration;
        }
        return new Decision(suppress, generation, ruleId, phase, sequence);
    }

    private bool ValidateLease(ulong now)
    {
        if (_lease == 0 || now >= _deadline || (_progressDeadline != 0 && now >= _progressDeadline))
        {
            ProgressTimedOut = _progressDeadline != 0 && now >= _progressDeadline;
            FailOpen();
            return false;
        }
        return true;
    }

    private void FailOpen()
    {
        _lease = 0;
        _deadline = 0;
        _progressDeadline = 0;
        _rules.Clear();
    }
}

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Run("no lease always passes", () =>
        {
            var model = new FailOpenPolicyModel(8);
            Equal(default, model.Input(0x1E, false, 1));
        });

        Run("valid lease and snapshot suppress paired down/up", () =>
        {
            var model = Ready();
            var down = model.Input(0x1E, false, 2);
            var up = model.Input(0x1E, true, 3);
            True(down.Suppress && up.Suppress);
            Equal(down.Generation, up.Generation);
            Equal(down.RuleId, up.RuleId);
        });

        Run("replacement keeps the pressed key generation", () =>
        {
            var model = Ready();
            var down = model.Input(0x1E, false, 2);
            True(model.Replace(10, 2, new Rule(0x1E, true, 200)));
            var up = model.Input(0x1E, true, 3);
            Equal(1UL, down.Generation);
            Equal(1UL, up.Generation);
            Equal(100UL, up.RuleId);
            True(up.Suppress);
        });

        Run("lease timeout clears suppression", () =>
        {
            var model = Ready();
            True(model.Input(0x1E, false, 2).Suppress);
            Equal(default, model.Input(0x1E, true, 100));
            True(model.IsFailOpen);
        });

        Run("bad heartbeat immediately fails open", () =>
        {
            var model = Ready();
            True(!model.Heartbeat(10, 999, 0, 2));
            Equal(default, model.Input(0x1E, false, 3));
        });

        Run("malformed or non-monotonic replacement fails open", () =>
        {
            var model = Ready();
            True(!model.Replace(10, 1, new Rule(0x1E, true, 999)));
            True(model.IsFailOpen);
        });

        Run("queue overflow fails open and passes current input", () =>
        {
            var model = new FailOpenPolicyModel(1);
            model.Acquire(10, 1, 50);
            True(model.Replace(10, 1, new Rule(0x1E, true, 100)));
            True(model.Input(0x1E, false, 2).Suppress);
            Equal(default, model.Input(0x1E, false, 3));
            True(model.Overflowed && model.IsFailOpen);
        });

        Run("client disconnect immediately fails open", () =>
        {
            var model = Ready();
            model.Disconnect();
            Equal(default, model.Input(0x1E, false, 2));
        });

        Run("acquiring a lease while a key is held cannot suppress mid-press", () =>
        {
            var model = new FailOpenPolicyModel(8);
            Equal(default, model.Input(0x1E, false, 1));
            model.Acquire(10, 2, 50);
            True(model.Replace(10, 1, new Rule(0x1E, true, 100)));
            Equal(default, model.Input(0x1E, false, 3));
            Equal(default, model.Input(0x1E, true, 4));
            True(model.Input(0x1E, false, 5).Suppress);
        });

        Run("a rule added while an unmapped key is held waits for next down", () =>
        {
            var model = new FailOpenPolicyModel(8);
            model.Acquire(10, 1, 50);
            True(model.Replace(10, 1, new Rule(0x30, true, 200)));
            Equal(default, model.Input(0x1E, false, 2));
            True(model.Replace(10, 2, new Rule(0x1E, true, 100)));
            Equal(default, model.Input(0x1E, false, 3));
            Equal(default, model.Input(0x1E, true, 4));
            True(model.Input(0x1E, false, 5).Suppress);
        });

        Run("SAS and left emergency chord keys are protected while right modifiers stay mappable", () =>
        {
            var left = new FailOpenPolicyModel(8);
            left.Acquire(10, 1, 50);
            True(!left.Replace(10, 1, new Rule(0x001d, true, 100)));
            True(left.IsFailOpen);

            var leftShift = new FailOpenPolicyModel(8);
            leftShift.Acquire(10, 1, 50);
            True(!leftShift.Replace(10, 1, new Rule(0x002a, true, 100)));

            var f12 = new FailOpenPolicyModel(8);
            f12.Acquire(10, 1, 50);
            True(!f12.Replace(10, 1, new Rule(0x0058, true, 100)));

            var right = new FailOpenPolicyModel(8);
            right.Acquire(10, 1, 50);
            True(right.Replace(10, 1,
                new Rule(0x001d, true, 100, 0x0002, 0x0004),
                new Rule(0x0038, true, 101, 0x0002, 0x0004)));
        });

        Run("normal E0 and E1 rules with one make code never cross-match", () =>
        {
            var model = new FailOpenPolicyModel(16);
            model.Acquire(10, 1, 50);
            True(model.Replace(10, 1,
                new Rule(0x0045, true, 100),
                new Rule(0x0045, true, 200, 0x0002, 0x0004),
                new Rule(0x0045, true, 300, 0x0004, 0x0002)));
            Equal(100UL, model.Input(0x0045, false, 2).RuleId);
            Equal(200UL, model.Input(0x0045, false, 3, 0x0002).RuleId);
            Equal(300UL, model.Input(0x0045, false, 4, 0x0004).RuleId);
        });

        Run("protocol model fails open on wildcard prefix or observation rule", () =>
        {
            var wildcard = new FailOpenPolicyModel(8);
            wildcard.Acquire(10, 1, 50);
            True(!wildcard.Replace(10, 1, new Rule(0x0045, true, 100, 0, 0)));
            True(wildcard.IsFailOpen);

            var observation = new FailOpenPolicyModel(8);
            observation.Acquire(10, 1, 50);
            True(!observation.Replace(10, 1, new Rule(0x0045, false, 100)));
            True(observation.IsFailOpen);
        });

        Run("rule factories produce exact normal E0 and E1 predicates", () =>
        {
            var deviceHash = Enumerable.Range(1, KeyPilotDriverClient.DeviceHashLength)
                .Select(value => checked((byte)value)).ToArray();
            var normal = KeyboardRule.ForEveryKeyboard(0x0045, KeyboardScanPrefix.None, 100);
            var e0 = KeyboardRule.ForEveryKeyboard(0x0045, KeyboardScanPrefix.E0, 200);
            var e1 = KeyboardRule.ForDevice(deviceHash, 0x0045, KeyboardScanPrefix.E1, 300);

            Equal((ushort)0, normal.RequiredFlags);
            Equal((ushort)0x0006, normal.IgnoredFlags);
            Equal((ushort)0x0002, e0.RequiredFlags);
            Equal((ushort)0x0004, e0.IgnoredFlags);
            Equal((ushort)0x0004, e1.RequiredFlags);
            Equal((ushort)0x0002, e1.IgnoredFlags);
            Equal(KeyboardRuleFlags.SuppressOriginal, normal.Flags);
            True(normal.DeviceHash.Span.ToArray().All(value => value == 0));
            True(e1.DeviceHash.Span.SequenceEqual(deviceHash));
            KeyPilotDriverClient.ValidateRules(new[] { normal, e0, e1 });
            Throws<ArgumentOutOfRangeException>(() =>
                KeyboardRule.ForEveryKeyboard(0x0045, (KeyboardScanPrefix)99, 400));
        });

        Run("public validator rejects hand-built wildcard and observation rules", () =>
        {
            var wildcard = new KeyboardRule(
                new byte[KeyPilotDriverClient.DeviceHashLength], 0x0045, 0, 0,
                KeyboardRuleFlags.SuppressOriginal, 100);
            Throws<ArgumentException>(() => KeyPilotDriverClient.ValidateRules(new[] { wildcard }));

            var observation = new KeyboardRule(
                new byte[KeyPilotDriverClient.DeviceHashLength], 0x0045, 0, 0x0006,
                KeyboardRuleFlags.None, 100);
            Throws<ArgumentException>(() => KeyPilotDriverClient.ValidateRules(new[] { observation }));

            var tooMany = Enumerable.Range(1, KeyPilotDriverClient.MaximumRules + 1)
                .Select(index => KeyboardRule.ForEveryKeyboard(
                    checked((ushort)index), KeyboardScanPrefix.None, checked((ulong)index)))
                .ToArray();
            Throws<ArgumentOutOfRangeException>(() => KeyPilotDriverClient.ValidateRules(tooMany));
        });

        Run("timer-only heartbeat cannot extend stalled dispatch progress", () =>
        {
            var model = Ready();
            Equal(1UL, model.Input(0x1E, false, 2).Sequence);
            model.ReadAllEvents(3);
            True(model.Heartbeat(10, 1, 0, 20));
            True(model.Heartbeat(10, 1, 0, 40));
            Equal(default, model.Input(0x1E, false, 52));
            True(model.IsFailOpen && model.ProgressTimedOut);

            var forged = Ready();
            forged.Input(0x1E, false, 2);
            True(!forged.Heartbeat(10, 1, 1, 3));
            True(forged.IsFailOpen);
        });

        Run("completed dispatch ACK clears the progress deadline", () =>
        {
            var model = Ready();
            var down = model.Input(0x1E, false, 2);
            model.ReadAllEvents(3);
            True(model.Heartbeat(10, 1, down.Sequence, 20));
            True(!model.IsFailOpen);
            True(model.Input(0x1E, false, 60).Suppress);
        });

        Run("keyboard overrun fails open and blocks lease until device recovery", () =>
        {
            var model = Ready();
            Equal(default, model.Input(0x00ff, false, 3));
            True(model.IsFailOpen && model.TrackingLost);
            True(!model.Acquire(11, 4, 50));
            model.RecoverDevice();
            True(model.Acquire(11, 5, 50));
        });

        Run("D0 exit blocks leases until the matching D0 entry", () =>
        {
            var model = Ready();
            model.SuspendDevice();
            True(model.IsFailOpen && model.TrackingLost);
            True(!model.Acquire(11, 3, 50));
            model.RecoverDevice();
            True(model.Acquire(11, 4, 50));
        });

        Run("device instance ID hash matches the kernel byte contract", () =>
        {
            var hash = KeyPilotDeviceHash.ComputeDeviceInstanceIdHash(
                @"HID\VID_1234&PID_ABCD\7&ABCDEF&0&0000");
            Equal("1E5E15D1547A1A14610046005CB381B0", Convert.ToHexString(hash));
            Throws<ArgumentException>(() => KeyPilotDeviceHash.ComputeDeviceInstanceIdHash("HID\0BAD"));
        });

        Run("left Ctrl plus left Shift plus F12 hold activates fail-open bypass", () =>
        {
            var model = new FailOpenPolicyModel(8);
            True(model.Acquire(10, 1, 5000));
            Equal(default, model.Input(0x001d, false, 2));
            Equal(default, model.Input(0x002a, false, 3));
            Equal(default, model.Input(0x0058, false, 4));
            model.AdvanceEmergencyTimer(2003);
            True(!model.EmergencyBypassActive && !model.IsFailOpen);
            model.AdvanceEmergencyTimer(2004);
            True(model.EmergencyBypassActive && model.IsFailOpen);
            True(!model.Acquire(11, 2005, 5000));
            Equal(default, model.Input(0x0058, true, 2006));
            True(!model.EmergencyBypassActive);
            True(model.Acquire(11, 2007, 5000));
        });

        Run("emergency timer races cannot leak the multi-device bypass count", () =>
        {
            var fleet = new EmergencyBypassFleet();
            var first = fleet.CreateDevice();
            var second = fleet.CreateDevice();
            first.Arm();
            second.Arm();
            first.FireTimer();
            second.FireTimer();
            Equal(2, fleet.ActiveDeviceCount);

            first.D0Exit();
            first.FireTimer();
            Equal(1, fleet.ActiveDeviceCount);

            second.Cleanup();
            second.FireTimer();
            Equal(0, fleet.ActiveDeviceCount);
        });

        Console.WriteLine($"Policy model and ABI helpers: {_passed}/22 passed.");
    }

    private static FailOpenPolicyModel Ready()
    {
        var model = new FailOpenPolicyModel(8);
        model.Acquire(10, 1, 50);
        True(model.Replace(10, 1, new Rule(0x1E, true, 100)));
        return model;
    }

    private static void Run(string name, Action body)
    {
        body();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Assertion failed.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
        }
    }

    private static void Throws<TException>(Action body) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
