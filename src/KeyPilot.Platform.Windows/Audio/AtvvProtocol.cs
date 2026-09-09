using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Audio;

/// <summary>
/// Android TV Voice-over-BLE constants and IMA/DVI 4-bit ADPCM. The GATT UUIDs, opcodes, and
/// IMA step/index tables are interoperability facts (the values the RC003 actually speaks / the
/// public IMA ADPCM specification), not a copy of any GPL implementation.
/// </summary>
public static class AtvvProtocol
{
    public static readonly Guid VoiceService = Guid.Parse("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid VoiceTx = Guid.Parse("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid VoiceAudio = Guid.Parse("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid VoiceControl = Guid.Parse("AB5E0004-5A21-4F05-BC7D-AF01F617B664");

    public const byte OpcodeAudioStop = 0x00;
    public const byte OpcodeAudioStart = 0x04;
    public const byte OpcodeMicButton = 0x08;
    public const byte OpcodeAudioSync = 0x0A;
    public const byte OpcodeCaps = 0x0B;

    public const int DefaultFrameSize = 120;
    public const int SampleRateHz = 16000;
    public const int LateAudioGuardMilliseconds = 300;
    public const double DefaultGainDb = 10.0;

    public static readonly byte[] GetCapabilitiesV10 = [0x0A, 0x01, 0x00, 0x00, 0x03, 0x03];

    public static byte[] MicOpenCommand(int version) =>
        version >= 0x0100 ? [0x0C, 0x00] : [0x0C, 0x00, 0x00];

    public static byte[] MicCloseCommand(int version, int sessionId) =>
        version >= 0x0100 ? [0x0D, (byte)(sessionId & 0xFF)] : [0x0D];
}

public sealed class ImaAdpcmDecoder
{
    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
        143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449,
        494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
        1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
        4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623,
        27086, 29794, 32767
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    private int _predictor;
    private int _stepIndex;

    public void Reset(int predictor = 0, int stepIndex = 0)
    {
        _predictor = Math.Clamp(predictor, -32768, 32767);
        _stepIndex = Math.Clamp(stepIndex, 0, 88);
    }

    public short[] Decode(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length * 2];
        var written = 0;
        foreach (var value in data)
        {
            samples[written++] = DecodeNibble(value >> 4);
            samples[written++] = DecodeNibble(value & 0x0F);
        }

        return samples;
    }

    private short DecodeNibble(int nibble)
    {
        var step = StepTable[_stepIndex];
        var difference = step >> 3;
        if ((nibble & 1) != 0)
        {
            difference += step >> 2;
        }

        if ((nibble & 2) != 0)
        {
            difference += step >> 1;
        }

        if ((nibble & 4) != 0)
        {
            difference += step;
        }

        _predictor = Math.Clamp(
            (nibble & 8) != 0 ? _predictor - difference : _predictor + difference,
            -32768,
            32767);
        _stepIndex = Math.Clamp(_stepIndex + IndexTable[nibble & 7], 0, 88);
        return (short)_predictor;
    }
}

public sealed class DcHighPassFilter
{
    private readonly double _alpha;
    private double _previousInput;
    private double _previousOutput;
    private bool _initialized;

    public DcHighPassFilter(int sampleRateHz = AtvvProtocol.SampleRateHz, double cutoffHz = 20.0)
    {
        if (sampleRateHz <= 0 || cutoffHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        _alpha = Math.Exp(-2.0 * Math.PI * cutoffHz / sampleRateHz);
    }

    public void Reset()
    {
        _previousInput = 0;
        _previousOutput = 0;
        _initialized = false;
    }

    public short[] Process(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return [];
        }

        if (!_initialized)
        {
            _previousInput = samples[0];
            _initialized = true;
        }

        var filtered = new short[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var current = (double)samples[index];
            var output = current - _previousInput + _alpha * _previousOutput;
            _previousInput = current;
            _previousOutput = output;
            filtered[index] = (short)Math.Clamp((int)Math.Round(output), -32768, 32767);
        }

        return filtered;
    }
}

public static class AtvvPcmPostprocessor
{
    public static short[] Process(ReadOnlySpan<short> samples, double gainDb = AtvvProtocol.DefaultGainDb)
    {
        if (samples.IsEmpty)
        {
            return [];
        }

        var filtered = samples.ToArray();
        if (filtered.Length >= 3)
        {
            for (var index = 1; index < filtered.Length - 1; index++)
            {
                filtered[index] = (short)((samples[index - 1] + (2 * samples[index]) + samples[index + 1]) >> 2);
            }
        }

        var finiteGain = double.IsFinite(gainDb) ? Math.Clamp(gainDb, -24.0, 24.0) : 0.0;
        var gain = Math.Pow(10.0, finiteGain / 20.0);
        for (var index = 0; index < filtered.Length; index++)
        {
            filtered[index] = (short)Math.Clamp((int)Math.Round(filtered[index] * gain), -32768, 32767);
        }

        return filtered;
    }
}

public sealed class AtvvSession
{
    private readonly ImaAdpcmDecoder _decoder = new();
    private readonly DcHighPassFilter _dcFilter = new();
    private readonly List<byte> _buffer = [];
    private int _version;
    private int _frameSize = AtvvProtocol.DefaultFrameSize;
    private int _sessionId;
    private bool _micOpen;
    private long? _lastMicOffMs;
    private (int Predictor, int StepIndex)? _pendingSync;

    public bool MicOpen => _micOpen;

    public int Version => _version;

    public int SessionId => _sessionId;

    public AtvvControlEvent HandleControl(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return AtvvControlEvent.Unknown;
        }

        switch (payload[0])
        {
            case AtvvProtocol.OpcodeCaps:
                if (payload.Length >= 3)
                {
                    _version = (payload[1] << 8) | payload[2];
                }

                if (payload.Length >= 7)
                {
                    var frameSize = (payload[5] << 8) | payload[6];
                    if (frameSize > 0)
                    {
                        _frameSize = frameSize;
                    }
                }

                return AtvvControlEvent.Caps;
            case AtvvProtocol.OpcodeMicButton:
                return AtvvControlEvent.MicButton;
            case AtvvProtocol.OpcodeAudioStart:
                _decoder.Reset();
                _dcFilter.Reset();
                _buffer.Clear();
                _pendingSync = null;
                _micOpen = true;
                if (payload.Length >= 4)
                {
                    _sessionId = payload[3];
                }

                return AtvvControlEvent.AudioStart;
            case AtvvProtocol.OpcodeAudioStop:
                _micOpen = false;
                _lastMicOffMs = Environment.TickCount64;
                _buffer.Clear();
                return AtvvControlEvent.AudioStop;
            case AtvvProtocol.OpcodeAudioSync when payload.Length >= 7:
                var predictor = (short)((payload[4] << 8) | payload[5]);
                _pendingSync = (predictor, payload[6]);
                return AtvvControlEvent.AudioSync;
            default:
                return AtvvControlEvent.Unknown;
        }
    }

    public short[] HandleAudio(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        if (!_micOpen)
        {
            if (_lastMicOffMs is { } stoppedAt &&
                Environment.TickCount64 - stoppedAt < AtvvProtocol.LateAudioGuardMilliseconds)
            {
                return [];
            }

            return [];
        }

        _buffer.AddRange(payload.ToArray());
        if (_buffer.Count < _frameSize)
        {
            return [];
        }

        var frames = _buffer.Count / _frameSize;
        var samples = new List<short>(frames * _frameSize * 2);
        for (var frame = 0; frame < frames; frame++)
        {
            if (_pendingSync is { } sync)
            {
                _decoder.Reset(sync.Predictor, sync.StepIndex);
                _dcFilter.Reset();
                _pendingSync = null;
            }

            var offset = frame * _frameSize;
            var decoded = _decoder.Decode(CollectionsMarshal.AsSpan(_buffer).Slice(offset, _frameSize));
            var centered = _dcFilter.Process(decoded);
            samples.AddRange(AtvvPcmPostprocessor.Process(centered));
        }

        _buffer.RemoveRange(0, frames * _frameSize);
        return [.. samples];
    }

    public byte[] MicOpenCommand() => AtvvProtocol.MicOpenCommand(_version);

    public byte[] MicCloseCommand() => AtvvProtocol.MicCloseCommand(_version, _sessionId);
}

public enum AtvvControlEvent
{
    Unknown,
    Caps,
    MicButton,
    AudioStart,
    AudioStop,
    AudioSync
}
