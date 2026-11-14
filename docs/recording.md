# Recording with OBS

[← Back to the README](../README.md)


An HDR game recorded by OBS with its usual SDR settings comes out washed out: OBS has to squeeze the HDR picture into SDR. Turn on **SDR while recording** in the flyout and, while OBS Studio or Streamlabs runs, nothing switches HDR on, so your recordings keep their normal colours. If a game already has HDR on when OBS starts, HDR goes back to SDR straight away; when OBS closes and the game is still running, HDR comes back on. HDR you turned on yourself is left alone. It is off until you turn it on.

Some games only offer their HDR option if HDR was on when they started (Dying Light: The Beast, for example), so while OBS is open they run in SDR until you restart them. Want HDR footage instead? Leave the tile off and set OBS to record in HDR: **Settings › Advanced**, Color Format **P010** and Color Space **Rec. 2100 (PQ)**, with a 10-bit HEVC or AV1 encoder. Those files only look right on an HDR screen or YouTube HDR.

