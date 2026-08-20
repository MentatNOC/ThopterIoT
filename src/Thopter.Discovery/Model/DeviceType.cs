namespace Thopter.Discovery.Model;

/// <summary>
/// Coarse device classification produced by the offline, rule-based identifier.
/// Deliberately shallow - deep classification is not part of the open tool.
/// </summary>
public enum DeviceType
{
    Unknown = 0,
    Camera,
    Nvr,
    NetworkGear,
    Computer,
    Printer,
    Phone,
    IoT,
}
