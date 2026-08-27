# Camera Kill Switch

A software "master switch" that lets users turn their device camera fully
**off** (blocked for every app) and back **on** from a system-tray icon — an ideal
software replacement for the stickers people put over their webcams.

- **Green tray icon** = camera enabled
- **Red tray icon** = camera disabled (Zoom, Teams, browsers — everything is blocked)
- One click to toggle. No admin prompt after the one-time setup.

## Install (end users)

1. Download the latest **`CameraKillSwitch-Setup.exe`** from the
   [Releases](../../releases) page.
2. Double-click it and follow the short wizard (it needs admin once, to install).
3. A camera icon appears in the system tray. **Left-click it to toggle** the
   camera on/off; the app window has an **Enable / Disable Camera** button too.

The first toggle shows **one** UAC prompt (it registers the elevated helper task);
every toggle after that is silent. Uninstall anytime from **Settings → Apps →
Camera Kill Switch** — it re-enables the camera and cleans up after itself.

## Why disabling the *device* (not the privacy setting)

Windows' built-in "Camera access" privacy toggle only blocks **Microsoft Store
apps**. Classic desktop apps (Zoom, Teams desktop, OBS, browsers) can still use
the camera. To block *everything*, this tool disables the camera **device**
itself via Plug-and-Play — the same effect as unplugging it.

Disabling a device needs admin rights, so the design splits into two parts:

1. A **one-time setup** (the first toggle, one UAC prompt) registers an elevated
   background task, pre-authorized so standard users may run it.
2. After that, any **standard user** flips the switch freely — they only
   *trigger* the pre-authorized task, so Windows shows no UAC prompt.