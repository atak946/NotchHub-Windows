# Development

## Build and run

Requires Windows 10 2004 or later and the .NET 10 SDK (or newer).

```powershell
dotnet build Notch.csproj -c Release
.\bin\Release\net10.0-windows10.0.19041.0\Notch.exe
```

Quit a running copy from the tray icon before building, otherwise the files are locked.

The multi-file build output is a development version: it cannot install or update itself.

CI also publishes the single-file build, compiles the installer and installs it silently.

If a change affects the elevated FPS helper (its code or the TraceEvent package), raise `MinHelperVersion` in
`Services/FpsHelper.cs` to the release that ships it, so users are offered **Güncelle**.

## Releases

Releases are built locally and published to GitHub Releases. The installer needs Inno Setup 6
(`winget install JRSoftware.InnoSetup`); its script is `tools/setup.iss`.

```powershell
# 1. Write and commit docs/release-notes/<version>.md
# 2. Publish
powershell -ExecutionPolicy Bypass -File tools\release.ps1 1.4.0
```

Add `-DryRun` to only build and sign into `dist\`. Publishing needs a clean git tree and `gh auth login`.

Each release has four assets:

| File | Purpose |
|---|---|
| `NotchSetup.exe` | Per-user installer containing the same executable |
| `Notch.exe` | Self-contained single-file build |
| `Notch.exe.sig` | ECDSA P-256 / SHA-256 signature of the executable |
| `version.txt` | Version number the app compares against |

## Update signing key

`tools/sign.cs` signs with `%USERPROFILE%\.notch\update-signing-key.pem` (or the path given with `-Key`).
The matching public key is `UpdatePublicKey` in `Services/Installer.cs`.

- **Back the private key up.** Without it, installed copies cannot be updated.
- Never commit it.
- In a fork, generate your own key: run
  `dotnet run --file tools/sign.cs -- <any file> <new-key.pem>`, copy the printed `PUBLIC` value into
  `Installer.cs`, and point `DefaultUpdateSource` at your repository.
