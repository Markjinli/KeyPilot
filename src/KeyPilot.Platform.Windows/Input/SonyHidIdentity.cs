namespace KeyPilot.Platform.Windows.Input;

/// <summary>First-party Sony USB IDs used to decide whether a HID collection is a DualShock/DualSense.</summary>
public static class SonyHidIdentity
{
    public const ushort VendorId = 0x054C;
    public const ushort DualShock4Fat = 0x05C4;
    public const ushort DualShock4Slim = 0x09CC;
    public const ushort DualSense = 0x0CE6;
    public const ushort DualSenseEdge = 0x0DF2;
    public const ushort GenericDesktopPage = 0x01;
    public const ushort GamePadUsage = 0x05;

    public const int PsButton = 0x0400;
    public const int TouchpadClick = 0x0800;

    public static bool IsSonyPad(uint vendorId, uint productId) =>
        vendorId == VendorId && IsSonyProduct(productId);

    public static bool IsSonyProduct(uint productId) =>
        productId is DualShock4Fat or DualShock4Slim or DualSense or DualSenseEdge;

    public static bool IsDualSense(uint productId) =>
        productId is DualSense or DualSenseEdge;

    public static string DisplayName(uint productId) => productId switch
    {
        DualShock4Fat or DualShock4Slim => "DualShock 4",
        DualSense => "DualSense",
        DualSenseEdge => "DualSense Edge",
        _ => "Sony 手柄"
    };
}
