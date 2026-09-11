using Microsoft.Win32;

namespace Fm350Unlock.Service;

internal sealed class AtPortLocator
{
    private const string DeviceRoot = @"SYSTEM\CurrentControlSet\Enum\PCI\VID_8087&PID_0B62";

    public AtPort? FindStartedPort()
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DeviceRoot);
        if (root is null)
        {
            return null;
        }

        foreach (string instanceName in root.GetSubKeyNames())
        {
            using RegistryKey? instance = root.OpenSubKey(instanceName);
            string? friendlyName = instance?.GetValue("FriendlyName") as string;
            if (friendlyName is null ||
                !friendlyName.Contains("COM AT", StringComparison.OrdinalIgnoreCase) ||
                (!friendlyName.Contains("DW5931E", StringComparison.OrdinalIgnoreCase) &&
                 !friendlyName.Contains("FM350", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string deviceId = $@"PCI\VID_8087&PID_0B62\{instanceName}";
            if (!IsStarted(deviceId))
            {
                continue;
            }

            using RegistryKey? parameters = instance?.OpenSubKey("Device Parameters");
            string? portName = parameters?.GetValue("PortName") as string;
            if (!string.IsNullOrWhiteSpace(portName))
            {
                return new AtPort(portName, deviceId, friendlyName);
            }
        }

        return null;
    }

    private static bool IsStarted(string deviceId)
    {
        if (NativeMethods.LocateDevNode(out uint instance, deviceId, 0) != NativeMethods.CrSuccess)
        {
            return false;
        }

        return NativeMethods.GetDevNodeStatus(out uint status, out uint problem, instance, 0) ==
                   NativeMethods.CrSuccess &&
               problem == 0 &&
               (status & NativeMethods.DnStarted) != 0;
    }
}

internal sealed record AtPort(string Name, string DeviceId, string FriendlyName);
