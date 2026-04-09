# Spacetorian

Spacetorian is a fork of Monitorian focused on remote brightness workflows and fork-specific UX updates.

## Official Upstream

- Original project: https://github.com/emoacht/Monitorian
- Original author: emoacht

Spacetorian keeps the original Monitorian base and adds fork-specific behavior. For full base-feature documentation of Monitorian, use the upstream repository.

## This Fork's Repository

- https://github.com/Sewwdy/spacetorian

## What Is Different From Monitorian

- Added **Viewer Client** (`ViewerClient/SpacetorianViewerClient.exe`) for remote brightness control from another PC.
- Viewer Client supports persisted connection settings (Main PC IP and Viewer Name).
- Viewer Client is single-instance and focuses an existing running window when launched again.
- Viewer Client tray behavior:
  - left click opens/focuses the connection window
  - startup opens the connection window immediately (not silent tray-only)
- Viewer Client connection UX is fully in-app:
  - inline connect status and errors (no system popup dependency)
  - reconnect window with periodic retry loop
  - auto-reconnect every 10 seconds after disconnect
  - closing reconnect window exits Viewer Client
- Main client includes identification for Viewer Client-connected devices.

## Build Notes

Main app solution:

```powershell
dotnet build .\Source\Monitorian.sln -c Release
```

Viewer Client executable:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe" /nologo /langversion:latest /target:winexe /out:"C:\Projects\spacetorian\ViewerClient\SpacetorianViewerClient.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll "C:\Projects\spacetorian\ViewerClient\Program.cs"
```

## Detection Of External Monitors

The base monitor-detection behavior comes from Monitorian.
Use the upstream documentation for deeper diagnostics:

- https://github.com/emoacht/Monitorian#detection-of-external-monitors

## Reporting

For `probe.log` and `operation.log` workflows, use the upstream reporting guide:

- https://github.com/emoacht/Monitorian#reporting

## Globalization

Localization model is inherited from Monitorian:

- https://github.com/emoacht/Monitorian#globalization

## License And Attribution

Spacetorian is distributed under MIT.

- Original copyright: emoacht
- Fork modifications: Sewwdy

See `LICENSE.txt` for repository-level terms and `Source/Monitorian/Resources/License.txt` for in-app license text.
