#if WINDOWS
using Nexus.Service.Devices.Detection.Native;
using Nexus.Service.QSeries;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>IOCTL_USB_HUB_CYCLE_PORT on one dongle half's own port (TX 0416:8040 or RX 0416:8041) after an exact identity check, with no pnputil fallback.</summary>
internal sealed class WindowsSlv3TxPortCycler : ISlv3TxPortCycler
{
    public bool TryResolve(Slv3PortInfo txPort, out string instanceId, out string target)
    {
        instanceId = "";
        if (txPort.Role is not (Slv3DongleRole.Tx or Slv3DongleRole.Rx))
        {
            target = $"{txPort.PortName} is not a dongle half";
            return false;
        }
        var id = CfgMgr32.GetInterfaceInstanceId(txPort.PortName);
        if (!Slv3TxPortIdentity.IsDongleInstanceId(txPort.Role, id))
        {
            target = id.Length == 0 ? $"no devnode for {txPort.PortName}" : $"{id} is not the dongle's {txPort.Role}";
            return false;
        }
        if (!CfgMgr32.TryGetUsbHubPort(id, out var hub, out var port))
        {
            target = $"no hub port for {id}";
            return false;
        }
        if (!Slv3TxPortIdentity.IsUsbEnumeratedInstanceId(hub))
        {
            target = $"{id} sits on {hub}, not on the USB enumerator";
            return false;
        }
        instanceId = id;
        target = $"{hub} port {port} ({id})";
        return true;
    }

    public bool TryCycle(string instanceId, out string detail)
    {
        if (!Slv3TxPortIdentity.IsTxInstanceId(instanceId) && !Slv3TxPortIdentity.IsRxInstanceId(instanceId))
        {
            detail = $"{instanceId} is not a 0416:8040 or 0416:8041 device";
            return false;
        }
        return QSeriesUsbPortCycle.TryCycle(instanceId, out detail);
    }
}
#endif
