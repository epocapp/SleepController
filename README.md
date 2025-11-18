# SleepController

Simple WPF .NET 9 app that monitors user idle time and suspends the system after a configured period. It runs a pre-sleep command before suspending and a post-wake command after resume. The app lives in the tray and can start hidden.

How it works
- Monitors idle via Win32 GetLastInputInfo.
- Calls SetSuspendState to suspend the computer.
- Runs pre/post commands configured in the UI.

Build and run
- Requires .NET 9 SDK on Windows.
- Open a developer PowerShell and run: dotnet build
- To run for development (no elevation):

```powershell
dotnet run --project SleepController.csproj
```

- To run the built exe and get a UAC elevation prompt (recommended if you want the app to change system power settings):

```powershell
cd "C:\temp\New folder (2)\SleepController\bin\Debug\net9.0-windows"
# Run elevated and start hidden
Start-Process .\SleepController.exe -Verb RunAs --args "/h"
```

Command line parameter
- Pass `/h` to start hidden (show only tray icon). Without parameters the window is shown on startup.

Notes
- The app requests Administrator privileges via `app.manifest` (requestedExecutionLevel="requireAdministrator") so it can run `powercfg` to change system sleep settings. If you do not want the UAC prompt, remove `app.manifest` or change requestedExecutionLevel to `asInvoker`.
- `SetSuspendState` may require privileges on some systems; if suspend fails, check event logs or test with a manual suspend.
- I couldn't run the built exe elevated from inside `dotnet run` in the development environment here — that's expected. Build succeeded. Run the exe directly on your machine to test elevation and the tray behavior.
