# sim2

A human history simulator. You watch civilization develop on Earth from 10,000 BCE into the future.

## Status

Milestone 0 (Skeleton) is in progress.

| Build step | State |
|---|---|
| 1. Grid and its tests | Done |
| 2. Earth import tool and world file | Done |
| 3. Clock, scheduler, random streams | Done |
| 4. World state, save and load, checksum | Done |
| 5. Headless runner | Done |
| 6. Viewer | Built, awaiting hands-on check on Windows |
| 7. Release build | Done |

![Terrain classes of the imported Earth map](docs/earth-preview.png)

The imported Earth map: ocean, coast, lowland, hills and mountains. Lakes and ice arrive with milestone 1.

![The viewer](docs/viewer-screenshot.png)

## Download

Get `sim2-windows.zip` from the [Releases page](https://github.com/djeyprott/sim2/releases), unzip it and run `sim2.exe`. Windows will warn that the program is unsigned: choose "More info", then "Run anyway".

Controls: drag to move, mouse wheel to zoom, click a cell to select it, Space to pause or resume, keys 1 to 5 to set the speed.

## Layout

- `src/Sim.Core`: the simulation core, a C# library with no Godot dependency.
- `src/Sim.Tools.EarthImport`: builds `data/earth.world` from elevation data. Runs in the "Earth import" GitHub Actions job.
- `src/Sim.Headless`: runs the simulation with no window. See below.
- `viewer`: the Godot 4.6 project that displays the world. It only displays; all logic is in `Sim.Core`.
- `tests/Sim.Core.Tests`: automated tests for the core.
- `data/earth.world`: the Earth map the simulator loads. `data/earth-overrides.txt` lists hand fixes for narrow straits and land bridges.

## Running the tests

Requires the .NET 8 SDK or later.

```
dotnet test sim2.sln
```

Tests also run on GitHub Actions on every push.

## Running without a window

```
dotnet run --project src/Sim.Headless -c Release -- --world data/earth.world --seed 1 --years 1000 --save run.sav
```

Options: `--world` (map file), `--seed` (whole number, default 1), `--years` (default 100), `--load` (continue from a save), `--save` (write a save at the end). It prints the final date, ticks per second and the state checksum.

## Data

Elevation comes from the NOAA NCEI ETOPO 2022 Global Relief Model, 60 arc-second ice surface version (doi:10.25921/fd45-gt74).
