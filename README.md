# sim2

A human history simulator. You watch civilization develop on Earth from 10,000 BCE into the future.

## Status

Milestone 0 (Skeleton) is in progress.

| Build step | State |
|---|---|
| 1. Grid and its tests | Done |
| 2. Earth import tool and world file | Not started |
| 3. Clock, scheduler, random streams | Not started |
| 4. World state, save and load, checksum | Not started |
| 5. Headless runner | Not started |
| 6. Viewer | Not started |

## Layout

- `src/Sim.Core`: the simulation core, a C# library with no Godot dependency.
- `tests/Sim.Core.Tests`: automated tests for the core.

## Running the tests

Requires the .NET 8 SDK or later.

```
dotnet test sim2.sln
```

Tests also run on GitHub Actions on every push.
