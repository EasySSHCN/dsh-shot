<p align="center">
  <img src="icon.svg" width="96" alt="dsh-shot logo">
</p>

# dsh-shot

[中文](README.zh.md) | English

**Screenshot capture for DeepSeek Harness (DSH).** Press a global hotkey, click the camera button in the composer, or type `/shot` — a full-screen overlay lets you drag a region, and the captured PNG lands in the current conversation as a draft attachment. The agent can also call a `screenshot` tool to look at your screen on request.

## Features

- **Global hotkey** (default `Ctrl+Alt+A`) works system-wide, even when the browser is not focused
- **Camera button** in the conversation composer triggers capture directly — the shot is inserted into the input draft, press Enter to send
- **`/shot` slash command** with an optional region: `/shot 100,100,800,600`
- **`screenshot` agent tool** — the model can capture your screen (`region` with drag overlay by default, or `screen` for the whole desktop) when you ask it to look at something
- **Translate / OCR actions** in the hotkey flow attach a preset caption ("translate the text in this screenshot" / "extract all text") automatically
- **Copy to clipboard** mode captures without touching any session
- Never auto-submits: captured images always wait in the draft until **you** press Enter

## Requirements

- Windows 10/11 (capture runs through a PowerShell + WinForms overlay; other platforms are not supported yet)
- DSH: **tested on `0.2.2-rc.1`**; `0.2.1+` declared in `engines` (requires the client-modules web plugin loader). Older or unreleased builds may differ.
- No external dependencies, no compiled binaries: the overlay window is compiled in-process via PowerShell `Add-Type`, which also avoids "fresh unsigned binary" antivirus heuristics

## Install

Install from npm with the DSH CLI:

```sh
dsh plugin --profile <your-profile> add dsh-shot
```

To install a local copy or a GitHub tarball instead:

```sh
# local directory
dsh plugin --profile <your-profile> add file:/path/to/dsh-shot-plugin

# tarball (npm-style URL)
dsh plugin --profile <your-profile> add https://github.com/<owner>/<repo>/archive/refs/tags/v0.1.1.tar.gz
```

In DSH Desktop, use the in-app plugin manager (Settings → Plugins) and search for `dsh-shot`. Restart the session page after installing if the camera button does not appear.

## Usage

| Action | Result |
|---|---|
| `Ctrl+Alt+A` (global) | Region overlay → drag + Enter → shot in the last active conversation draft |
| Camera button in composer | Same overlay and draft flow as the hotkey |
| `/shot` | Region overlay → shot in the draft |
| `/shot x,y,w,h` | Skips the overlay, captures that rectangle |
| Agent `screenshot` tool | Model captures your screen when you ask it to look at your screen/window/dialog |

Hotkey actions available from the overlay: **session** (draft in conversation), **translate**, **OCR**, **copy** (clipboard only).

## Configuration

Set a custom hotkey through the profile patch (`cordis.patch.yml`):

```yaml
- id: dsh-shot
  name: dsh-shot
  config:
    hotkey: Ctrl+Alt+A   # any RegisterHotKey-style chord, e.g. Ctrl+Shift+S
```

## Privacy & security

- Capture runs **entirely locally**: the PNG goes to your temp directory and into the conversation you choose. Nothing is uploaded by this plugin.
- If the hotkey daemon exits unexpectedly it stays disabled until the plugin reloads (no silent auto-restart), and the reason is logged to the console.

## Troubleshooting

- **Hotkey does nothing** — another app may own `Ctrl+Alt+A`. Change it via the config above, then reload the plugin.
- **Antivirus blocks the overlay** — the overlay is PowerShell hosting in-process-compiled WinForms code; whitelist the `helper/*.ps1` scripts if your AV flags them.
- **Camera button missing** — refresh the page; the client half registers into `conversation.input.left`.

## Changelog

See [CHANGELOG.md](CHANGELOG.md). Version `0.1.1` fixes web-boot failure on DSH `0.2.2-rc.1` (the client bundle now registers under its package name, matching the client-modules loader's row-id contract).

## License

[MIT](LICENSE)
