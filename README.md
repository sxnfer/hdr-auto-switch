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

## Competitive resolution

Some people play shooters on a smaller area than their monitor, say 25" of a 27" panel, with a low stretched resolution inside it. HDR Switch can do that per game: while the game runs, the desktop switches to a resolution of your choice, shown **1:1 in the middle of the screen with black borders**, so the monitor behaves like a smaller one. When the game closes, the screen goes back to its normal resolution. It is independent of HDR: a game can have HDR, a resolution, both or neither, and a game you set to "never HDR" can still have a resolution.

In the flyout, click the **monitor symbol** next to a game. The list shows **Off**, then every resolution your display offers at its current refresh rate, the ones with the screen's own aspect ratio first, each with how much of the screen it covers (for example `92.5 % · 24.6″`). Pick one and it is saved, and applied right away if the game is running. The status card and the tray icon's tooltip show the active resolution next to the HDR state, for example `2368 × 1332 – Counter-Strike 2`.

**One-time setup in NVIDIA Control Panel** (the resolution has to exist before HDR Switch can switch to it):

1. **Display › Change resolution › Customize… › Create Custom Resolution**. Enter the size you want, at your monitor's refresh rate (not 60 Hz), test and save it. For 25" on a 27" 2560×1440 panel that is **2368 × 1332** (exactly 16:9, 92.5 % of the diagonal).
2. **Display › Adjust desktop size and position**: keep the scaling you like for everything else, for example **Full screen**, so other games can stretch a low resolution over the whole screen. Perform scaling on **GPU**, and leave **"Override the scaling mode set by games and programs" unticked**.

While a game with a competitive resolution runs, HDR Switch switches NVIDIA's scaling to **No scaling** itself and puts your setting back when the game closes. That is what keeps the picture centred when the game uses *exclusive* fullscreen, which sets its own display mode. The driver saves this setting like Control Panel does, so if the PC loses power mid-game, HDR Switch puts it back the next time it starts. It only puts back a No scaling it set: if you change the setting yourself while the game runs, it leaves yours alone. On AMD or Intel graphics this step is skipped.

In the game, use the **same resolution as the competitive one** in exclusive fullscreen, or choose **borderless** or **fullscreen windowed** mode with whatever resolution you like, stretched or not. In borderless the game sizes its window from the desktop, which is now the smaller area, so a stretched 1280×960 fills exactly that area.

The switch happens the moment you press Play in Steam, or when the game's process appears, so the game sees the smaller desktop from the start. If you set a resolution while the game is already running, restart the game if it doesn't fit. Three seconds after the last game with a resolution closes, the display goes back to the mode saved in Windows display settings, scaling included. The change is never saved: a reboot or sign-out always comes back at your normal resolution, and if HDR Switch is killed mid-game, Windows drops the temporary mode with it (the next start switches again if the game is still running). As with HDR, it only undoes what it did: if you or Windows change the resolution while the game runs (display settings, sleep and resume), it leaves it alone.

**Known limitation:** a *lower* resolution than the competitive one in *exclusive* fullscreen, for example 1280×960, can't be stretched into the smaller area: with No scaling it shows as a small 1:1 box. For a stretched low resolution inside the area, use borderless or fullscreen windowed mode ([#1](https://github.com/sxnfer/hdr-auto-switch/issues/1)).

The profiles live in `%AppData%\HdrSwitch\resolutions.txt`, one game per line (`cs2.exe  2368x1332`, optionally `@240` for a refresh rate); the pencil in the resolution view opens it, and edits apply as soon as you save. If a resolution doesn't exist at the needed refresh rate, nothing is switched and a notification says what to create.

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
