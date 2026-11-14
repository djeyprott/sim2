# sim2 working rules

Human history simulator: Godot 4 with C#, watch-only, Earth from 10,000 BCE, open-ended runs.
The design doc and milestone specs live in the claude.ai Project "sim2" (`design-doc.md`, `milestone-0-spec.md`). They are the source of truth.

## Scope rules

- Build only what the current milestone spec lists. Its feature list is frozen.
- New ideas go to the backlog in the design doc, not into the code.
- Each milestone gets an agreed spec before any code is written.

## Architecture rules

- `Sim.Core` never references Godot. The viewer only displays.
- Deterministic: same build, machine and seed give the same history. Single-threaded core. Never rely on the iteration order of `Dictionary` or `HashSet`. Use the core's own random streams, never `System.Random`, inside the core.
- One tick is one month. Systems declare an interval of 1, 3 or 12 months and a fixed update order.
- State lives in flat arrays indexed by cell id.
- Content (goods, technologies, terrain types) lives in data files, not code.

## Grid

- `GeodesicGrid.Create(GeodesicGrid.EarthFrequency)` gives 225,002 cells about 51 km across: hexagons plus 12 pentagons. Cell ids are stable for a given frequency.
- Cell areas differ by up to about 12% from the mean. Always use `AreaKm2(cell)`, never assume equal areas.

## Workflow

- The cloud workspace has no .NET SDK. GitHub Actions (`.github/workflows/ci.yml`) is the build and test check: push, then read the run result with `gh run list` and `gh run view`.
- Target framework is `net8.0` for Godot compatibility.
