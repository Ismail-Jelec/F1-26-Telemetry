# F1 26 Telemetry

A live console dashboard and lap logger for **F1 26** (the F1 25 2026 Season Pack), written in C# with no NuGet packages.

## Requirements

- .NET 10 SDK (`dotnet --version` should print 10.x)
- Windows Terminal is recommended (it shows the colours best)

## Game settings

In F1 26, open **Settings → Telemetry Settings** and set:

| Setting            | Value                                    |
|--------------------|------------------------------------------|
| UDP Telemetry      | On                                       |
| UDP Broadcast Mode | Off                                      |
| UDP IP Address     | 127.0.0.1 (or your PC's IP if you play on console) |
| UDP Port           | 20777 (SimHub's port)                    |
| UDP Send Rate      | 60Hz                                     |
| UDP Format         | **2026**                                 |

This app listens on **20999**, so it can run alongside SimHub. The game sends to SimHub on 20777, and SimHub forwards a copy to this app:

1. In SimHub, open the F1 game's settings and turn on UDP forwarding.
2. Add the target `127.0.0.1:20999`.

Without SimHub, set the game's UDP Port to 20999 instead.

## Run

```bash
cd F126Telemetry
dotnet run                     # listen on 20999 and log laps
dotnet run -- --demo           # try it with fake data, no game needed
dotnet run -- --port 21000     # if something else already uses 20999
dotnet run -- --no-log         # dashboard only
dotnet run -- --log-dir C:\f1laps
```

The first time you run it, Windows may ask whether to let it through the firewall. Allow private networks.

## What you get

**Live dashboard:** speed, gear (with the suggested gear), RPM, rev bar, throttle, brake and steering bars, ERS store, deployment and harvest, active aero, overtake mode, DRS, fuel, tyre compound and age, brake bias, and tyre surface, inner, brake and pressure readings for each corner, with colour-coded temperatures.

**Lap logging:** each session gets its own folder in `laps/`:

```
laps/2026-11-12_20-15-03_Silverstone_Time_Trial/
  laps.csv       one row per lap: time, sectors, valid, sample count
  lap_02.csv     every telemetry frame of that lap
  lap_03.csv
```

Each `lap_XX.csv` has one row per frame (about 60 a second). Its columns are lap distance, lap time, speed, throttle, brake, steer, gear, RPM, DRS, ERS store, deploy mode, fuel, tyre surface temps, active aero, overtake and sector. `lap_distance_m` is the column to use for overlaying two laps in Excel or Python. Decimals use `.`, so set Excel's import to "." if your Windows is set to Swedish.

When you use a flashback in the middle of a lap, the rewound part is cut so the lap stays clean. A lap you restart is thrown away.

## Code tour

| File | What it does |
|------|--------------|
| `Packets.cs` | Packet layouts for UDP format 2026 (24 cars, the new Car Telemetry 2 packet) and a small little-endian reader |
| `TelemetryHub.cs` | Decodes each datagram and keeps the player car's latest state |
| `Dashboard.cs` | Draws the console UI with ANSI colours at 10 fps |
| `LapLogger.cs` | Buffers samples, trims flashbacks and writes the CSVs |
| `DemoSender.cs` | Fake packet generator used by `--demo` |
| `Names.cs` | Track, session, compound and weather lookups |

## Notes

- Layouts come from EA's *Data Output from F1 25: 2026 Season Pack* spec. If a game patch changes the sizes, mismatched packets are skipped and counted under "dropped" instead of crashing the app.
- New 2026 track IDs that aren't in `Names.cs` yet show up as `Track <id>`. Add them as you meet them.
- The active-aero mode is shown as its raw number until we confirm which number is which mode in the game.
