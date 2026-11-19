# CoreGlance

A small always-on desktop widget for Windows 10/11 that shows what your laptop is doing right now:
CPU load per thread (P-cores and E-cores labelled), clock speed, memory, both GPUs, temperatures,
fan speeds, CPU/GPU power draw, battery charge rate, disk, network, the busiest processes and a
one-line "what's the bottleneck" insight.

- **Small:** one ~100 KB exe, no installer framework, no runtime to download (it uses the .NET Framework 4.8 built into Windows).
- **Light:** about 0.1–0.3% CPU and ~10–20 MB of RAM at a 1 s refresh. The cost is shown at the bottom of the widget.
- **Accurate:** CPU, GPU, disk and network numbers come from Windows performance counters, the same source Task Manager uses.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This copies the exe to `%LOCALAPPDATA%\Programs\CoreGlance`, adds a Start Menu shortcut, starts it with
Windows and launches it. Use `-NoAutostart` to skip autostart, or `-EnableSensors` to turn on fan readings
in the same step (one UAC prompt).

Rebuild from source at any time with `.\build.ps1` (output: `dist\CoreGlance.exe`).
Remove everything with `.\uninstall.ps1` (add `-KeepSettings` to keep your layout).

## Using it

| Action | How |
| --- | --- |
| Move | Drag anywhere. It snaps to screen edges and remembers its position |
| Minimize to tray | The **–** button (top right), or Ctrl+Alt+Shift+M from anywhere |
| Bring back | Click the tray icon (it shows CPU and RAM bars), or Ctrl+Alt+Shift+M |
| Compact or full view | The **▢** button, or double-click to cycle Compact, Standard, Detailed |
| Thread detail | Hover a core bar: `CPU 5 · E-core 1 · 34% busy` |
| Top processes | Detailed view; click **CPU** or **MEM** to sort |
| Options | Right-click: always on top, lock position, click-through, opacity, refresh rate, hide over full-screen apps, start with Windows |

It hides itself while a full-screen app or video is playing and comes back afterwards
(turn this off under right-click > *Hide over full-screen apps*). Sampling pauses while the PC is locked.

## Temperatures, fans and power

| Reading | Source | Needs admin? |
| --- | --- | --- |
| Thermal zone temperature, throttling | Windows thermal-zone counters | No |
| CPU package power (W) | Intel/AMD RAPL energy counters | No |
| NVIDIA GPU temperature, power, clock | `nvml.dll` from the NVIDIA driver | No |
| Battery charge/discharge rate, time left | Windows battery WMI | No |
| CPU fan, GPU fan (RPM), CPU/GPU/system temperature | Acer firmware (`AcerGamingFunction`) | **Yes** |

To get fan speeds, right-click > **Enable temperatures & fans (admin)...**. It asks for UAC once, copies
the exe to `C:\Program Files\CoreGlance` (where a normal-user program can't replace it) and registers
a scheduled task named `CoreGlance`. From then on, the normal launcher starts the admin copy through
that task, with no UAC prompt each time. CoreGlance only *reads* sensors and never changes fan or power settings.

NVIDIA readings appear only while the dGPU is already awake. CoreGlance never wakes a sleeping
Optimus GPU, because that would drain the battery.

Fan RPM on non-Acer laptops would need that vendor's firmware interface or a kernel driver
(as NZXT CAM and LibreHardwareMonitor use). CoreGlance deliberately ships no driver.

## Files

- `src/`: C# source (`SysMonitor` counters, `Sensors` firmware/WMI/NVML thread, `WidgetForm` UI)
- `%APPDATA%\CoreGlance\settings.ini`: your settings; `error.log`: errors, if any
- `CoreGlance.exe --snapshot out.png`: renders the widget off-screen to a PNG (diagnostics)
