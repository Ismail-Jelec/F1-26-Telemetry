# F1 26 Telemetry

Two apps that share the same packet code:

| Folder | What it is |
|--------|-----------|
| `F126Overlay`   | **In-game overlays** (lap time log, gear/throttle/brake trace) with a control panel to switch them on and off |
| `F126Telemetry` | Console dashboard plus lap CSV logging |

Both listen on UDP **20999** (SimHub forwards there from 20777).

## Overlays

```
cd F126Overlay
dotnet run
```

Or open `F1-26-Telemetry.slnx` in Visual Studio, set **F126Overlay** as the startup project and press ▶.

The control panel (Overlay Studio) has three tabs:
- **Lap time log** and **Inputs**: show or hide each overlay, and set its size, opacity, background opacity, colours and content (number of lap rows, which columns show, trace length, speed, pedal bars). Changes apply live.
- **General**: lock or unlock the overlays (when unlocked you can drag them; they show a dashed outline), save laps to CSV, run demo data, and reset everything.

Positions and settings are saved in `%AppData%\F126Overlay\settings.json`. Lap CSVs go to `Documents\F126Telemetry\laps`.

**Play F1 26 in Borderless or Windowed mode.** Windows can't draw overlays on top of exclusive fullscreen.

### Lap time log
- **Personal session best:** your fastest valid lap this session
- **Session best (purple):** the fastest valid lap by any car this session
- **Lap rows:** your last 8 laps, each with its gap to your PB (red if slower, `PB` in green for your best lap, `INVALID` for invalid laps) and the fuel left at the end of the lap. Fuel is in kg, the unit the game reports.
