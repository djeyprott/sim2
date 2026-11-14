# Milestone 1 spec: Land and people (version 1.0, agreed)

Status: agreed by the owner 2026-11-14. Feature list is frozen. Parent: `design-doc.md` version 0.4.

Progress: step 1 in progress.

Change log:
- 1.0 (2026-11-14): agreed and frozen.
- Draft 2 (2026-11-14): climate now changes at random during a run; crop homelands can be real or random; a new-run screen is added to choose these.
- Draft 1 (2026-11-14): first draft.

## Purpose

Put a living world on the map. When this milestone is done, you start a run in 10,000 BCE with foragers spread across the continents, and watch climate shift, people move with it, farming begin in a few places, spread outward, grow villages, and suffer famines. Each run differs in its climate and in where and when farming starts.

## Confirmed decisions

1. **Climate changes at random.** Each cell starts from Earth's present-day temperature and rainfall. During a run, climate drifts and swings around those values: slow worldwide warm and cool periods, and regional wet and dry spells lasting decades to centuries. It is different for every seed and the same for the same seed. It does not follow Earth's real past, so there is no scripted green Sahara.
2. **Crop homelands are a choice made when a run starts.** On the Earth map, "real" puts each crop group where it grew wild in reality (for example wheat and barley in Southwest Asia, rice in China, maize in Mesoamerica). "Random" places each crop group in a randomly chosen region whose climate suits it. Either way, farming can only be invented where a crop grows wild, and only spreads where the climate suits that crop.
3. **One settlement per cell at most.** A cell (about 51 km across) holds a rural population and, once dense enough, one named settlement that stands for its main village or town.
4. **The world starts already populated by foragers**, at a level each cell's land can support, except for remote islands that people had not reached by 10,000 BCE (for example Madagascar, New Zealand, Iceland and Polynesia). Those stay empty until seafaring arrives in milestone 3.
5. **No herd animals yet.** Herding and pack animals are decided in the milestone 2 or 3 spec.

## What gets built

### 1. Housekeeping

- Move the whole project from .NET 8 to .NET 10.

### 2. Environment in the world file

The import job adds these per cell, and the world file format moves to version 2.

- **Baseline temperature and rainfall** for each month, from a public present-day climate dataset. Source to be chosen from datasets whose licence allows redistribution; licence checked before import.
- **Lakes**: cells that are mostly lake become water. This fixes the Caspian Sea and the Great Lakes. Source: Natural Earth.
- **Rivers**: each land cell records the size of the largest river crossing it (none, minor, major). Source: Natural Earth.

### 3. Saved state that is not per cell

- A new kind of saved state for things that are not cells: tables with a stable id per entry, and run settings. It is saved, loaded and checksummed like cell state.
- First users: climate regions, run settings and settlements. States and figures will reuse it later.

### 4. Changing climate

- The world is covered by about 160 climate regions, each roughly 1,800 km across. Each region carries a temperature shift and a rainfall factor that wander slowly and at random, on both a decades scale and a centuries scale. A worldwide warm or cool trend is added on top.
- A cell's current climate is its baseline adjusted by a blend of the nearest regions, so changes are smooth across the map with no visible region borders.
- Updated once a year. Limits keep it believable: the worldwide temperature shift stays within about 2 degrees C either way, and a cell's rainfall stays between about half and one and a half times its baseline.
- **Biome** is worked out from each cell's current climate, so biomes move: deserts advance and retreat, forest gives way to grassland. One of about ten types (for example tundra, forest, grassland, desert, rainforest). The biome list and its rules live in a data file. Recalculated every ten years.
- **Ice**: cells whose current climate is too cold for any thaw are ice. Coastlines and sea level do not change.

### 5. New-run settings

- The viewer gets a start screen with: seed, crop homelands (real or random), and a button to start. Loading a save is also offered there.
- The headless runner gets matching options.
- Settings are stored in the save and shown in the viewer.

### 6. Foragers

- Each land cell has a forager population and a limit on how many foragers it can feed, set by its current biome, rivers and coast. The limit moves as climate changes.
- Yearly: populations grow toward the limit, shrink when the limit falls, and move into neighbouring land cells that have room. This is how climate drives migration.
- The starting population of the world is between 2 and 6 million.

### 7. Farming

- **Crops**: a data file lists about eight crop groups, each with its real homeland and the climate it tolerates.
- **Invention**: each year, a cell where a crop grows wild has a small chance of inventing farming. The chance rises with forager density.
- **Yield**: a farming cell's food depends on how well its current climate suits its best crop, and on rivers. A region that dries out can stop supporting its crop.
- **Spread**: farming passes to neighbouring cells where the crop can grow, by farmers moving in and by foragers taking it up.
- **Harvest**: computed once a year at the harvest season of the cell's hemisphere, with random good and bad years on top of the slower climate swings.
- Each cell tracks farmers separately from foragers. Farmland feeds many times more people than foraging.

### 8. Famine

- When a bad harvest or a drying climate leaves a cell with more people than food, people die or leave for neighbouring cells. There are no food stores yet; those come with goods in milestone 2.

### 9. Settlements

- A settlement is founded when a cell's farmer population passes a threshold. It gets a generated name, a founding date and a population that is part of the cell's total.
- Settlements grow, shrink, and are abandoned if their cell empties.
- Names come from a simple placeholder generator until languages arrive in milestone 5.

### 10. Viewer

- New map modes: biome, temperature, rainfall, climate change (warmer or cooler, wetter or drier than baseline), population density, and farming (which crop, and farmer share).
- Rivers and lakes drawn on the map. Settlements drawn as dots, with names when zoomed in.
- The tooltip adds biome, climate, river, population (foragers and farmers), crop and settlement.
- World population and the worldwide temperature shift shown in the top bar.

### 11. Headless runner

- Can write a yearly statistics file: world population, forager and farmer totals, number of farming cells, number of settlements, worldwide temperature shift, share of land in each biome, and where and when each farming origin happened.

## Not in this milestone

- Climate that follows Earth's real past, climate change caused by people, changing sea level and coastlines (backlog).
- Goods, food stores, trade (milestone 2).
- Technology beyond the single step from foraging to farming; seafaring (milestone 3).
- States, borders, war (milestone 4). Languages, religions (milestone 5). Named people (milestone 6).
- Herd animals and pastoralism.
- Disease.
- Charts in the viewer (the statistics file covers measurement for now).
- Generated worlds.

## Exit tests

Automated, on the Earth map with real crop homelands, across 10 different seeds unless stated:

Map and climate
- [ ] The Caspian Sea and Lake Superior are water.
- [ ] At the start of a run the Sahara is desert, the Amazon is rainforest and Greenland's interior is ice.
- [ ] Over 5,000 years the worldwide temperature shift stays within 2.5 degrees C and no cell's rainfall leaves the range of 0.4 to 1.6 times its baseline.
- [ ] Climate differs between seeds and is identical for the same seed.
- [ ] Between 2% and 25% of land cells change biome at least once in 5,000 years.

Foragers
- [ ] Starting world population is between 2 and 6 million.
- [ ] With farming switched off, world population stays within 20% of what the land can feed at that time, for 5,000 years.
- [ ] Islands marked as unreached stay empty.

Farming
- [ ] Farming is invented independently in at least 3 separate regions within 6,000 years in at least 8 of 10 seeds.
- [ ] The place and date of the first invention differ between seeds.
- [ ] With random crop homelands, the same test passes and at least one origin lies more than 2,000 km from every real homeland in at least 8 of 10 seeds.
- [ ] Farming spreads at an average of 0.3 to 3 km per year.
- [ ] After 5,000 years, world population is between 5 and 40 million in every seed.
- [ ] No farming ever appears in ice, tundra or desert cells without a major river.

Soundness
- [ ] Population is never negative and never an invalid number in any cell.
- [ ] Same seed and settings give the same checksum; save and load mid-run gives the same result as an uninterrupted run.
- [ ] A 5,000-year headless run on the build machine takes under 5 minutes.

By hand in the viewer:

- [ ] The start screen sets the seed and crop homelands, and a loaded save shows the settings it was started with.
- [ ] Each new map mode is readable and matches the tooltip.
- [ ] You can see a region dry out or cool over centuries, its biome change, and people leave it.
- [ ] You can watch farming start, spread and found settlements, and tell which crop is where.
- [ ] A famine is visible as a drop in population that later recovers.
- [ ] The frame rate stays smooth at every speed.

## Build order

Each step ends with a new development build you can download.

1. Move to .NET 10
2. Baseline climate, lakes and rivers in the world file, with their map modes
3. Saved state that is not per cell
4. Changing climate, biomes and ice, with their map modes
5. New-run settings and start screen
6. Foragers and the population map mode
7. Farming: crops, invention, yield, spread, harvest
8. Famine
9. Settlements
10. Statistics file, tuning against the exit tests, release v0.2

## Numbers that will need tuning

How fast and how far climate swings, growth rates, feeding limits per biome, the chance of inventing farming, spread rates and the settlement threshold all start as estimates. Step 10 adjusts them until the exit tests pass. They live in data files, not code.
