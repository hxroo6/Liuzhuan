# Audio host transaction checks

This independent source-link runner compiles the production `MicrophoneHost`, `MicrophonePreferences` and `FollowDemandPolicy` with synthetic devices and transport. It does not reference the main WPF project or enumerate real audio hardware. The optional argument specifies a directory for synthetic preference files; no cleanup of user data is performed.

```powershell
F:/dotnet10/dotnet.exe run --project scripts/AudioHostChecks/AudioHostChecks.csproj -c Release -- F:/appdata/Temp/liuzhuan-audio-host-checks
```

The checks cover first-scan startup, stop invalidation, recovery racing with device enumeration/start/retry, explicit re-enable, validation and persistence failure, revocation of observing/automatic-follow state, and explicit phone repair racing with manual forwarding. Repair refuses to interrupt an active forwarding request and cannot replace the server between an idle check and a concurrent start. These checks do not establish real-device audio quality, speaker availability, successful dictation or power consumption.
