# Release and MSIX delivery

The app is a single-project MSIX WinUI application (`EnableMsixTooling=true`) targeting Windows 11 x64 with ARM64 retained in the project platform list. Package identity, visual assets, full-trust capability, version, publisher, and display metadata live in `src/MflManager.App/Package.appxmanifest`.

## Local package

1. Enable Windows Developer Mode.
2. Install WinApp CLI 0.6+ and the current WinUI templates.
3. Run the `winui-packaging` workflow to generate a development certificate, trust it locally, produce a Release x64 MSIX, and verify install/launch/uninstall.
4. Never commit `.pfx`, `.cer`, package output, SQLite files, or credentials.

Store distribution must replace the development publisher/identity with reserved Partner Center values and use Store signing. Enterprise distribution should use the organization's code-signing certificate and deployment policy.

## Release checklist

- `dotnet build` and all non-live tests pass in Release.
- `dotnet list package --vulnerable --include-transitive` has no known vulnerabilities.
- WinUI code review, keyboard/accessibility audit, three-theme visual pass, and one-process UI automation pass complete.
- Login, league-host switching, partial refresh, every owner mutation preview/confirmation, draft stale-state rejection, agent broker, and terminal lifecycle are tested.
- Package contents and logs pass the secret audit.
- Release notes identify any official MFL endpoint that remains capability-gated or unsupported by the current league.
