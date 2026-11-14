# How it works

[← Back to the README](../README.md)


- **Steam games**: Steam marks a game as running in the registry the moment you press Play. Display Switch is told about that change by Windows, so HDR is on before the game even starts. That matters for games such as Dying Light: The Beast, which only offer their HDR option if Windows HDR is already on when they start.
- **Other games**: once a second it takes a lightweight list of running programs and compares it with your game list, catching games while they are still loading.
- When the last game closes, it waits 3 seconds and switches back to SDR.
- It only switches back HDR **it** turned on. HDR that you, a game or another tool turned on is left alone.
- Idle cost: about 0.2% of one CPU core and ~20 MB of memory.

Your game list lives in `%AppData%\HdrSwitch\games.txt` (one exe per line; a `-` in front means "never").

## Building it yourself


Run `build.cmd` (or `build.cmd 1.2.0` for a versioned build). It uses the C# compiler that ships with Windows (.NET Framework 4.8).

## Publishing a new version

Commit your changes, then tag and push:

```
git tag v1.2.0
git push origin main v1.2.0
```

GitHub Actions builds the exe and publishes the release; everyone's copy offers the update within a day.

