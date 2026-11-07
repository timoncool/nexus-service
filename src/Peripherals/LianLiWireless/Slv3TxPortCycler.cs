using System;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Cycles the USB hub port the SLV3 TX is attached to. Resolve names the target or the
/// reason it is refused; TryCycle acts on the resolved instance id only.
/// </summary>
internal interface ISlv3TxPortCycler
{
    bool TryResolve(Slv3PortInfo txPort, out string instanceId, out string target);

    bool TryCycle(string instanceId, out string detail);
}

internal static class Slv3TxPortIdentity
{
    private const string TxInstancePrefix = @"USB\VID_0416&PID_8040\";
    private const string RxInstancePrefix = @"USB\VID_0416&PID_8041\";

    /// <summary>
    /// True only for the plain TX device itself: not an interface of a composite device,
    /// not the RX, not a hub, not the WCH alias.
    /// </summary>
    internal static bool IsTxInstanceId(string instanceId) => IsPlainDevice(instanceId, TxInstancePrefix);

    /// <summary>The same check for the RX half (0416:8041), which a deaf RX needs cycled.</summary>
    internal static bool IsRxInstanceId(string instanceId) => IsPlainDevice(instanceId, RxInstancePrefix);

    internal static bool IsDongleInstanceId(Slv3DongleRole role, string instanceId) =>
        role == Slv3DongleRole.Tx ? IsTxInstanceId(instanceId) : role == Slv3DongleRole.Rx && IsRxInstanceId(instanceId);

    private static bool IsPlainDevice(string instanceId, string prefix)
    {
        if (!instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var serial = instanceId.AsSpan(prefix.Length);
        return serial.Length > 0
            && serial.IndexOf('\\') < 0;
    }

    /// <summary>
    /// Coarse sanity check that the parent devnode is on the USB enumerator. The real hub
    /// check is the hub-interface lookup in <c>QSeriesUsbPortCycle.TryCycle</c>.
    /// </summary>
    internal static bool IsUsbEnumeratedInstanceId(string instanceId) =>
        instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
}
