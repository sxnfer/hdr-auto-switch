# HDR Switch

Windows HDR turns on by itself when you start a game, and goes back to SDR a few seconds after you quit. No setup, no launch options: it finds your games and gets out of the way.

## Download and use

1. Download **HdrSwitch.exe** from the [latest release](https://github.com/sxnfer/hdr-auto-switch/releases/latest).
2. Put it in a folder that will stay put, for example `C:\Tools\HDR Switch`.
3. Double-click it. Windows will probably say **"Windows protected your PC"**, because the app isn't code-signed. Click **More info › Run anyway**. Every release is built by GitHub from the code in this repository, and comes with its SHA256 checksum (`SHA256SUMS.txt`).
4. An **H** icon appears in the tray (click the **^** near the clock if you don't see it). Click it and turn on **Start with Windows**.

That's it. It finds your games on its own (Steam, Epic, GOG, Ubisoft Connect, and game folders such as `XboxGames` or `Games` on every drive).

Needs Windows 10 or 11 and a monitor with HDR available in **Settings › System › Display**. Nothing else to install.

## The flyout

Click the icon (left or right button) and a small panel opens by the taskbar, in the style of the Windows 11 quick settings:

| | |
|---|---|
| **Status** | `HDR on` with the game's name, `Waiting for a game`, or `No HDR display found`. An **Update to version …** button appears here when there is one. |
| **Tiles** | **Start with Windows** (lit when on), **Look for games** to scan again, **Add a program** for anything it missed – it lists the programs that are running. |
| **Games** | Every game it found, each with a switch. Switching one off keeps it listed, so a slip is one click to undo. |
| **Bottom bar** | The pencil opens `games.txt`, **Exit** quits. |

Click anywhere else, or press Esc, to close it. The icon is orange while HDR is on and grey in SDR.

## Updates

HDR Switch checks this repository once a day. When there is a new version, an **Update to version …** button appears at the top of the flyout. One click downloads it, checks it against the release's SHA256 checksum, and restarts the app. The update check is the only time the app goes online.

## How it works

- **Steam games**: Steam marks a game as running in the registry the moment you press Play. HDR Switch is told about that change by Windows, so HDR is on before the game even starts. That matters for games such as Dying Light: The Beast, which only offer their HDR option if Windows HDR is already on when they start.
- **Other games**: once a second it takes a lightweight list of running programs and compares it with your game list, catching games while they are still loading.
- When the last game closes, it waits 3 seconds and switches back to SDR.
- It only switches back HDR **it** turned on. HDR that you, a game or another tool turned on is left alone.
- Idle cost: about 0.2% of one CPU core and ~20 MB of memory.

Your game list lives in `%AppData%\HdrSwitch\games.txt` (one exe per line; a `-` in front means "never").

## Uninstall

Turn off **Start with Windows**, choose **Exit**, then delete the folder with `HdrSwitch.exe` and `%AppData%\HdrSwitch`.

## Building it yourself

Run `build.cmd` (or `build.cmd 1.2.0` for a versioned build). It uses the C# compiler that ships with Windows (.NET Framework 4.8).

## Publishing a new version

Commit your changes, then tag and push:

```
git tag v1.2.0
git push origin main v1.2.0
```

GitHub Actions builds the exe and publishes the release; everyone's copy offers the update within a day.
