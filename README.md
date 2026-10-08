# ComTray

A notification-area app that shows which COM ports are present, and lets you act on them
without opening Device Manager.

```
3 COM ports
──────────────────────────────────────────
COM1   —   Communications Port           ▸
COM3   —   Bench PSU                     ▸
COM7   —   Arduino Uno                   ▸    PuTTY at 115200      (bold = default)
Copy all as text                              Arduino IDE
──────────────────────────────────────────    PlatformIO monitor
Open Device Manager                           Device properties
Edit actions.json                             Restart this device
Refresh now                                   ─────────────────────
✓ Notify on connect and disconnect            Baud: 115200         ▸
  Start with Windows                          Copy COM7
──────────────────────────────────────────    Copy as Python
Exit                                          Copy hardware ID
                                              Name this device...
```

## Actions

Everything in the top half of a port's submenu comes from `actions.json`. Nothing is hardcoded,
so adding a tool is a config edit rather than a rebuild.

```jsonc
{
  "name": "PuTTY at {baud}",
  "command": "putty.exe",
  "args": "-serial {port} -sercfg {baud},8,n,1,N",
  "match": ["*"],
  "default": true
}
```

| Field | Meaning |
| --- | --- |
| `name` | Menu text. Placeholders work here too. |
| `command` | Executable. `%LOCALAPPDATA%`-style variables are expanded. Resolved through ShellExecute, so anything with an App Paths entry (PuTTY, Tera Term) works unqualified. |
| `args` | Command line. |
| `match` | Substrings tested against the device instance ID. `["*"]` matches everything. |
| `admin` | `true` requests elevation via UAC. |
| `default` | `true` renders the item in bold as the primary action. |

Placeholders: `{port}` `{portnum}` `{baud}` `{vid}` `{pid}` `{serial}` `{instance}` `{desc}` `{nickname}`

### Matching on hardware, not port number

`match` tests the device instance ID — `USB\VID_2341&PID_0043\85735313235351E0` — so a rule fires
based on what the device *is*. Windows reshuffling COM numbers doesn't break anything.

Common vendor IDs:

| Token | Device |
| --- | --- |
| `VID_2341`, `VID_2A03` | Arduino |
| `VID_1A86` | WCH CH340 / CH341 |
| `VID_10C4` | Silicon Labs CP210x |
| `VID_0403` | FTDI |
| `VID_303A` | Espressif native USB |
| `VID_0483` | ST-Link |
| `VID_2E8A` | Raspberry Pi Pico |

You can narrow further: `["VID_0403&PID_6001"]` for one FTDI model, or paste a full instance ID
to target one physical adapter and nothing else.

### Two shipped rules worth knowing about

Both are just config entries, but they're the ones that save the most clicking:

```jsonc
// Jumps straight to this device's properties dialog, skipping the Device Manager tree
{ "command": "rundll32.exe",
  "args": "devmgr.dll,DeviceProperties_RunDLL /MachineName \"\" /DeviceID \"{instance}\"" }

// Disable + re-enable without unplugging. The "my board stopped enumerating" button.
{ "command": "pnputil.exe", "args": "/restart-device \"{instance}\"", "admin": true }
```

`DeviceProperties_RunDLL` is undocumented. It has worked since XP, but it isn't contractual.
`pnputil /restart-device` needs Windows 10 2004 or newer and elevation.

### File location

Portable first, then per-user:

1. `actions.json` next to `ComTray.exe`
2. `%APPDATA%\ComTray\actions.json` — created on first run

The file is re-read every time the menu opens, so edits apply immediately. JSONC comments and
trailing commas are allowed. A syntax error shows a toast once and falls back to no actions.

## Per-device memory

Baud rate and nickname are stored against `VID:PID:SerialNumber`, not the port number. Name a
CP210x "Bench PSU" and set it to 9600, and it comes back as "Bench PSU" at 9600 in any USB socket,
on any COM number. Devices without a real serial number (many CH340 clones) fall back to the full
instance ID, which encodes the physical USB port — the setting then follows the socket instead.

Stored under `HKCU\Software\ComTray\Nicknames` and `\Baud`.

## Build

```powershell
winget install Microsoft.DotNet.SDK.8
dotnet publish -c Release
```

Output: `bin\Release\net8.0-windows\win-x64\publish\ComTray.exe`

Add `-p:SelfContained=true` for a standalone ~70 MB exe on machines without the .NET runtime.
Change `<RuntimeIdentifier>` for `win-x86` or `win-arm64`.

## How detection works

| Source | Used for | Cost |
| --- | --- | --- |
| `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` | Which ports exist | microseconds |
| WMI `Win32_PnPEntity` | Friendly name, hardware ID | ~200 ms |

WMI is queried only when the port set changes. Updates are driven by `WM_DEVICECHANGE`, with a
5-second registry poll as a fallback. Idle cost is effectively zero.

## Deliberately not included

**"Is this port in use?"** — determining it means opening the port, and opening a port toggles DTR,
which resets an Arduino. The alternative is enumerating kernel handles via
`NtQuerySystemInformation`, which is undocumented and needs elevation. Neither belongs in a
background app that runs all day.

**Serial logging** — a tray app holding your port open is a good way to spend twenty minutes
wondering why the IDE can't upload. If you want capture, add a rule that launches a real logger.
