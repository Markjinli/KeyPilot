using System.Security.Cryptography;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Process-local value written into the native injected-input extra-information field. It is a
/// recursion guard, not an authentication or security boundary.
/// </summary>
public readonly record struct InputInjectionMarker
{
    public InputInjectionMarker(uint value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The injection marker cannot be zero.");
        }

        Value = value;
    }

    public uint Value { get; }

    public static InputInjectionMarker CreateProcessLocal()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        }
        while (value == 0);

        return new InputInjectionMarker(value);
    }

    // default(InputInjectionMarker) must never classify the common zero extra-info value as ours.
    public bool Matches(uint extraInformation) => Value != 0 && extraInformation == Value;
}
