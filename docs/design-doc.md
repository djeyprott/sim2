# sim2 design doc (version 0.5)

Status: top-level design agreed. Milestone 0 is complete and released as v0.1. Milestone 1 is in progress (`milestone-1-spec.md`).

Change log:
- 0.5 (2026-11-14): added the climate and crop decisions made for milestone 1, the .NET 10 move, the release process and new backlog items.
- 0.4 (2026-11-14): sphere grid, start date and coastlines confirmed.

## Goal

A desktop program in which you watch human civilization develop from the Neolithic Revolution into the future. Runs are plausible, not a replay of real history. The viewer observes; they do not play.

## Decisions

- **Stack**: Godot 4.6 with C# on .NET 10. The simulation core is a plain C# library that does not depend on the engine. Code lives at github.com/djeyprott/sim2.
- **Releases**: Windows executables built by GitHub Actions. A development build is replaced on every change; a numbered release is published once per milestone.
- **Clock**: one tick is one month. Each system declares how often it runs:
  - every month: war, trade and goods movement, historical figures
  - every season (3 months): farming
  - every year (12 months): population, technology, culture, climate
  - The order in which systems update within a tick is fixed and documented.
- **Start date**: 10,000 BCE. Everyone starts as a forager; farming emerges inside the simulation. Later start points come from saves of a run.
- **Map**: a grid on a sphere, 225,002 nearly equal cells of about 51 km each (hexagons plus 12 pentagons), about 65,000 of them land. Cells with no people in them do no per-tick work.
- **Coastlines**: modern and fixed.
- **Climate**: starts from Earth's present-day temperature and rainfall and then changes at random during a run, with slow worldwide warm and cool periods and regional wet and dry spells. It differs by seed and does not follow Earth's real past. Biomes and ice follow the current climate.
- **Crop homelands**: chosen when a run starts. Real homelands, or each crop placed at random in a region whose climate suits it.
- **Settlements**: at most one per cell.
- **World**: real Earth first, generated worlds last. The same grid serves both.
- **Run length**: open-ended. A run continues until the viewer stops it; technology plateaus at the last defined tier.
- **Organizations are a core concept**: states, tribes, religions, merchant houses and similar bodies are all "organizations" with members, positions, resources and goals. States (milestone 4) are built as one kind of organization so that figures (milestone 6) can plug in without rework.
- **Historical figures are actors, not labels**: each has a personality, personal goals and a position in one or more organizations, and pursues an agenda through those organizations, as in Dwarf Fortress. Expect thousands alive worldwide in later eras. Dead figures are kept as compact history records.
- **Milestone order**: as listed below.

## What the simulation tracks

- **Map cells**: terrain, rivers, lakes, climate, biome and natural resources.
- **Settlements**: villages and cities sit in cells and hold population, stockpiles and buildings.
- **Goods**: tracked as quantities per settlement stockpile. Every unit is produced, moved by trade, consumed or destroyed, so totals always balance.
- **Populations**: grow, starve, sicken and migrate.
- **Technology**: discovered locally and spread through contact.
- **Organizations**: states and other bodies that hold territory or resources, fight wars, split and collapse.
- **Culture**: languages, religions and ideas that spread and split.
- **Historical figures**: named individuals with personalities, family ties, goals and life events. Ordinary people stay as population numbers.

## Non-goals

- Player control of a nation or character.
- Reproducing real historical events or dates.
- Simulating every individual person.
- Tracking every individual item (only bulk goods; notable artifacts may come later).
- 3D graphics, multiplayer, modding support.

## Architecture rules

1. **The simulation core is separate from the display.** The core runs with no window at all, so it can be tested and run fast.
2. **Deterministic**: the same seed and settings always give the same history.
3. **Data-driven content**: goods, technologies, crops, biomes live in data files, not code.
4. **Save and load** from the first milestone.
5. **Each system only talks to others through the shared world state**, so a system can be added or reworked without rewriting the rest.

## Milestones

Each milestone ends with a working, watchable program. Work on the next one does not start until the current one meets its exit test.

| # | Milestone | Adds | State |
|---|-----------|------|-------|
| 0 | Skeleton | Earth map grid, headless core, monthly clock, time controls, save/load, map viewer, Windows releases | Complete, v0.1 |
| 1 | Land and people | Climate that changes, rivers and lakes, biomes, forager population, origin of farming, settlement founding, migration, famine | In progress |
| 2 | Goods and trade | Production, stockpiles, consumption, spoilage, trade routes | |
| 3 | Technology | Discovery, diffusion, effects on production and population, seafaring | |
| 4 | States and war | Organizations, polities, borders, conquest, collapse | |
| 5 | Culture | Language families, religions (as organizations), spread and schism | |
| 6 | Historical figures | Named actors with personalities and agendas, dynasties, life events, a readable chronicle | |
| 7 | Later eras | Industrial, modern and future systems | |
| 8 | Generated worlds | Procedural planets as an alternative to Earth | |

## Scope control

- New ideas go in the backlog below, not into the current milestone.
- A milestone's feature list is frozen once work on it starts. Only the owner can amend it, and amendments are recorded in the spec's change log.
- Before each milestone, a spec states what each new system does and does not do, and is agreed before code is written.

## Backlog

- Climate that follows Earth's real past (for example the green Sahara), as an option beside random climate. A published reconstruction in 1,000-year steps exists.
- Climate change caused by people in later eras (consider with milestone 7).
- Lower Ice Age sea level and changing coastlines.
- Herd animals and pastoralism (decide in the milestone 2 or 3 spec).
- Disease.
- The Aral Sea at its historical size, and small straits that are currently closed (for example Bali joined to Java).
- Globe view.
- Charts in the viewer.
