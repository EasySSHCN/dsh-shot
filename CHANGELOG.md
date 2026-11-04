# Changelog

All notable changes to `dsh-shot` are documented here.

## 0.1.1 — 2026-11-04

### Fixed

- **Compatibility with DSH `0.2.2-rc.1`:** the client bundle now registers under its **package name** (`dsh-shot`) to match the `client-modules` web loader's row-id contract. Previously the registration id differed from the package name, which made the loader's factory-registration check fail and **broke the entire web boot** of DSH on that version.
- The client `inject` list now also includes the `conversation` service, needed for draft attachment delivery.

## 0.1.0

Initial release.

- `screenshot` agent tool (region overlay by default, full-screen mode)
- `/shot` slash command, with optional `x,y,w,h` region
- Global hotkey (default `Ctrl+Alt+A`) via a PowerShell hotkey daemon
- Camera button in the conversation composer (client-side registration)
- Full-screen overlay: drag-select, hover window pre-select, annotation toolbar (rectangle/arrow/pen/mosaic/undo), copy-to-clipboard, translate/OCR preset captions
- Captures always land in the input draft; never auto-submits
- win32 only: PowerShell + .NET WinForms overlay compiled in-process via `Add-Type` (no shipped binaries)
