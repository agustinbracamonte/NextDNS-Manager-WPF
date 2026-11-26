# Restricted launcher for the broker smoke test

`RestrictedLauncher` is a test-only helper for GitHub-hosted Windows runners, where the job token is an administrator token and UAC is disabled. It launches only `BrokerProbe.exe --status` from a restricted version of the current token; it does not change the installer, service, or production authorization rules.

The launcher creates a LUA token with maximum privileges disabled and the built-in Administrators SID disabled. Before launching the probe, and again after process creation, it checks that the token has the same user SID and session as the runner, keeps the Interactive SID enabled, and does not enable the Network SID or Administrators SID. It then requires `BrokerProbe` to print `Admin: false` and `Success: true` and exit with code 0. Any failed check fails the command. Standard output and error are captured in a unique temporary log file, and the child has a 120-second timeout.

Build and run the smoke check after installing NextDNS Manager and building `BrokerProbe`:

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'NextDNSManager\dotnet-sdk\dotnet.exe'
& $dotnet build tests/BrokerProbe/BrokerProbe.csproj -c Release
& $dotnet run --project tests/RestrictedLauncher/RestrictedLauncher.csproj -c Release -- tests/BrokerProbe/bin/Release/net10.0-windows/BrokerProbe.exe
```

`BrokerProbe --status` sends a read-only request to the installed broker. Do not run this launcher in local development unless a status query against the current user's installed service is intended; it is designed for the installer smoke workflow.
