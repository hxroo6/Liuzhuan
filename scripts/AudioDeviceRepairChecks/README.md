# Safe virtual-device repair checks

This independent source-link runner verifies production candidate selection without loading the main WPF application. Synthetic checks never enumerate or open real devices and never read or change user settings.

```powershell
F:/dotnet10/dotnet.exe run --project scripts/AudioDeviceRepairChecks/AudioDeviceRepairChecks.csproj -c Release
```

The optional `--scan` argument performs a **read-only** Core Audio/PnP inventory and prints the suggested pair. It does not read saved preferences, record, play, activate a stream, install a driver, or change Windows defaults or volume. Its output contains local endpoint identities and must stay out of shared diagnostics and source control.

```powershell
F:/dotnet10/dotnet.exe run --project scripts/AudioDeviceRepairChecks/AudioDeviceRepairChecks.csproj -c Release -- --scan
```

Automatic candidate selection requires the exact `VBAudioVACWDM` Windows matching device ID, a common nonempty PnP parent and controller, the CABLE Output recording role, and one unambiguous pair. The 16-channel endpoint is excluded by both display name and device description. A generic “Speakers” label alone never establishes a virtual device. Applying a suggestion additionally requires an explicit authenticated action and the host's serialized idle check; identifying a pair does not prove audible output or successful dictation.

Reference APIs (implementation is original): [device properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties), [matching device ID](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-matchingdeviceid), [PnP parent](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-parent), [retrieving device relations](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-device-relations).
