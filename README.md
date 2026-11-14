# Display Switch

*Formerly HDR Switch.* Per-game display settings for Windows: HDR, a competitive resolution and NVIDIA scaling switch when a game starts and switch back when it closes.

Windows HDR turns on by itself when you start a game, and goes back to SDR a few seconds after you quit. No setup, no launch options: it finds your games and gets out of the way.

## Download and use

1. Download **HdrSwitch.exe** from the [latest release](https://github.com/sxnfer/game-display-switch/releases/latest).
2. Put it in a folder that will stay put, for example `C:\Tools\Display Switch`.
3. Double-click it. Windows will probably say **"Windows protected your PC"**, because the app isn't code-signed. Click **More info › Run anyway**. Every release is built by GitHub from the code in this repository, and comes with its SHA256 checksum (`SHA256SUMS.txt`).
4. An **H** icon appears in the tray (click the **^** near the clock if you don't see it). Click it and turn on **Start with Windows**.

That's it. It finds your games on its own (Steam, Epic, GOG, Ubisoft Connect, and game folders such as `XboxGames` or `Games` on every drive).

Needs Windows 10 or 11 and a monitor with HDR available in **Settings › System › Display**. Nothing else to install.

## The flyout

Click the icon (left or right button) and a small panel slides in by the taskbar:

| | |
|---|---|
| **Status** | `HDR on` with the game's name, `Waiting for a game`, or `No HDR display`, with an amber dot while HDR is on. A setup hint (for example about a missing resolution) stays here until the game closes. An **Update to …** button appears here when there is one. |
| **Tiles** | **Start with Windows**, **SDR while recording** (see below) and **Island** (see below). A tile is white and says **On** while it is on, dark and **Off** otherwise. |
| **Games** | Every game it found, each with a switch: amber means HDR turns on for it. Switching one off keeps it listed, so a slip is one click to undo. The two round buttons in the header **look for games** again and **add a program** it missed, from a list of the programs that are running. |
| **Bottom bar** | The pencil opens `games.txt`, **Exit** quits. |

Click anywhere else, or press Esc, to close it. The icon is amber while HDR is on and grey in SDR. Amber always means HDR; the Windows accent colour isn't used.

## The island

When something happens, a small pill drops in at the top-centre of the screen and leaves again after a few seconds:

| | |
|---|---|
| **A game starts** | The game's name with `HDR on`, and its competitive resolution if it has one. It then shrinks to a dot and `HDR on`. |
| **A game closes** | `SDR in 3s`, counting down, then `SDR`. Starting a game again in that time cancels it. |
| **Recording** | `OBS Studio · SDR` with a red dot beside it, when **SDR while recording** takes HDR off. |
| **Updates and scans** | A new version, a finished update, the games a scan found. |

It never takes focus and has no taskbar or Alt+Tab entry. It doesn't show over a game in exclusive fullscreen, over any other fullscreen window, or in presentation mode: it waits up to 10 seconds for the screen to be free, and what still matters then arrives as an ordinary Windows notification. While a game runs, clicks go straight through it; otherwise resting the mouse on it keeps it there and a click opens the flyout. Longer setup hints (a resolution that has to be created in NVIDIA Control Panel, say) are always ordinary notifications, and stay in the flyout's status card.

Turn it off with the **Island** tile; everything it would have said then comes as a notification, as in earlier versions. Movement follows the Windows setting **Accessibility › Visual effects › Animation effects**: with that off, the island and the flyout appear and change without animation. `HdrSwitch.exe --island-demo` shows every state in turn without switching anything, also next to a running copy.

The island's shapes and motion are inspired by Apple's Dynamic Island and by the open-source React project islandcn (MIT); no code is taken from it.

## Updates

Display Switch checks this repository once a day. When there is a new version, an **Update to …** button appears at the top of the flyout. One click downloads it, checks it against the release's SHA256 checksum, and restarts the app. The update check is the only time the app goes online.

## How it works

- **Steam games**: Steam marks a game as running in the registry the moment you press Play. Display Switch is told about that change by Windows, so HDR is on before the game even starts. That matters for games such as Dying Light: The Beast, which only offer their HDR option if Windows HDR is already on when they start.
- **Other games**: once a second it takes a lightweight list of running programs and compares it with your game list, catching games while they are still loading.
- When the last game closes, it waits 3 seconds and switches back to SDR.
- It only switches back HDR **it** turned on. HDR that you, a game or another tool turned on is left alone.
- Idle cost: about 0.2% of one CPU core and ~20 MB of memory.

Your game list lives in `%AppData%\HdrSwitch\games.txt` (one exe per line; a `-` in front means "never").

## Recording with OBS

An HDR game recorded by OBS with its usual SDR settings comes out washed out: OBS has to squeeze the HDR picture into SDR. Turn on **SDR while recording** in the flyout and, while OBS Studio or Streamlabs runs, nothing switches HDR on, so your recordings keep their normal colours. If a game already has HDR on when OBS starts, HDR goes back to SDR straight away; when OBS closes and the game is still running, HDR comes back on. HDR you turned on yourself is left alone. It is off until you turn it on.

Some games only offer their HDR option if HDR was on when they started (Dying Light: The Beast, for example), so while OBS is open they run in SDR until you restart them. Want HDR footage instead? Leave the tile off and set OBS to record in HDR: **Settings › Advanced**, Color Format **P010** and Color Space **Rec. 2100 (PQ)**, with a 10-bit HEVC or AV1 encoder. Those files only look right on an HDR screen or YouTube HDR.

## Competitive resolution

Some people play shooters on a smaller area than their monitor, say 25" of a 27" panel, with a low stretched resolution inside it. Display Switch can do that per game: while the game runs, the desktop switches to a resolution of your choice, shown **1:1 in the middle of the screen with black borders**, so the monitor behaves like a smaller one. When the game closes, the screen goes back to its normal resolution. It is independent of HDR: a game can have HDR, a resolution, both or neither, and a game you set to "never HDR" can still have a resolution.

In the flyout, click the **monitor symbol** next to a game. The list shows **Off** (the whole screen), then every resolution your display offers at its current refresh rate, the ones with the screen's own aspect ratio first, each with how much of the screen it covers (for example `92.5 % · 24.6″`). Pick one and it is saved, and applied right away if the game is running. The status card and the tray icon's tooltip show the active resolution next to the HDR state, for example `2368 × 1332 – Counter-Strike 2`.

**One-time setup in NVIDIA Control Panel** (the resolution has to exist before Display Switch can switch to it):

1. **Display › Change resolution › Customize… › Create Custom Resolution**. Enter the size you want, at your monitor's refresh rate (not 60 Hz), test and save it. For 25" on a 27" 2560×1440 panel that is **2368 × 1332** (exactly 16:9, 92.5 % of the diagonal).
2. **Display › Adjust desktop size and position**: keep the scaling you like for everything else, for example **Full screen**, so other games can stretch a low resolution over the whole screen. Perform scaling on **GPU**, and leave **"Override the scaling mode set by games and programs" unticked**.

While a game with a competitive resolution runs, Display Switch switches NVIDIA's scaling to **No scaling** itself and puts your setting back when the game closes. That is what keeps the picture centred when the game uses *exclusive* fullscreen, which sets its own display mode. The driver saves this setting like Control Panel does, so if the PC loses power mid-game, Display Switch puts it back the next time it starts. It only puts back a No scaling it set: if you change the setting yourself while the game runs, it leaves yours alone. On AMD or Intel graphics this step is skipped.

In the game, use the **same resolution as the competitive one** in exclusive fullscreen, or borderless (fullscreen windowed) mode, which always draws at the desktop's size and so fills the area. For a lower resolution stretched over the area, see the next part.

The switch happens the moment you press Play in Steam, or when the game's process appears, so the game sees the smaller desktop from the start. If you set a resolution while the game is already running, restart the game if it doesn't fit. Three seconds after the last game with a resolution closes, the display goes back to the mode saved in Windows display settings, scaling included. The change is never saved: a reboot or sign-out always comes back at your normal resolution, and if Display Switch is killed mid-game, Windows drops the temporary mode with it (the next start switches again if the game is still running). As with HDR, it only undoes what it did: if you or Windows change the resolution while the game runs (display settings, sleep and resume), it leaves it alone.

**A stretched resolution inside the area.** A game can also run at a *lower* resolution of its own, stretched over the competitive one: 4:3 at 1776 × 1332 across a 2368 × 1332 area, say. In the game's resolution view, click **Game resolution** and pick one; the list shows what fits inside the competitive resolution. While the game runs, the desktop switches to that resolution and the GPU stretches it over the area, with the same black borders around it. The monitor still gets its native signal, and the stretching is done by the scaler in the GPU's display output, the one "Full screen" scaling uses, only aimed at a smaller rectangle. Nothing is added to the display path. Three things follow from how it works:

- **The game has to be in borderless mode** (*Fullscreen Windowed* in Counter-Strike 2, *Windowed Fullscreen* in Valorant). There it draws at the desktop's resolution and asks Windows for nothing. A game in *exclusive* fullscreen sets a display mode of its own, and Windows then goes back to the driver's scaling: a 1:1 box or the whole panel. Display Switch doesn't fight over the display: it leaves the game's mode alone and says so, in a notification and as *not stretched* in the flyout. Switch the game to borderless and start it again. A borderless game that covers the screen is normally sent straight to the display on Windows 11, as exclusive fullscreen is; anything drawn over it (a notification, an overlay) goes through Windows' compositor while it shows.
- **The resolution has to fit inside the competitive one.** The GPU's scaler only enlarges. A resolution that would have to shrink (1920 × 1440 into 2368 × 1332) is done by Windows' compositor instead, which costs latency, so it is neither offered nor applied. The largest 4:3 inside 2368 × 1332 is 1776 × 1332.
- **Everything is stretched while the game runs**, the desktop and the windows you Alt+Tab to included, because the desktop itself is at the game's resolution.

Unlike the plain competitive resolution, Windows doesn't drop this mode by itself if Display Switch is killed mid-game: Display Switch puts the screen back the next time it starts, and so does a reboot or sign-out, since the mode is never saved. NVIDIA's scaling setting isn't touched for these games. This was developed and tested on an NVIDIA card; another graphics driver may refuse it, in which case nothing is switched and a notification says so ([#1](https://github.com/sxnfer/game-display-switch/issues/1)).

The profiles live in `%AppData%\HdrSwitch\resolutions.txt`, one game per line (`cs2.exe  2368x1332`, optionally `@240` for a refresh rate and `stretch 1776x1332` for the game's own resolution); the pencil in the resolution view opens it, and edits apply as soon as you save. If a resolution doesn't exist at the needed refresh rate, nothing is switched and a notification says what to create.

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
