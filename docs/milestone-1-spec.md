# Milestone 1 spec: Land and people (version 1.1, agreed)

Status: agreed by the owner 2026-11-14. Feature list is frozen. Parent: `design-doc.md` version 0.6.

Progress: steps 1 to 6b done 2026-11-14; step 7 (farming) done 2026-11-15, all exit tests passing.

Change log:
- 1.1 (2026-11-14, agreed by owner): people are held in population groups, following the design doc's population model. Step 6b moves foragers onto groups; farming turns foragers into farmers by moving people between groups; famine uses each group's food condition.
- 1.0 (2026-11-14): agreed and frozen.
- Draft 2 (2026-11-14): climate now changes at random during a run; crop homelands can be real or random; a new-run screen is added to choose these.
- Draft 1 (2026-11-14): first draft.

## Purpose

Put a living world on the map. When this milestone is done, you start a run in 10,000 BCE with foragers spread across the continents, and watch climate shift, people move with it, farming begin in a few places, spread outward, grow villages, and suffer famines. Each run differs in its climate and in where and when farming starts.

## Confirmed decisions

1. **Climate changes at random.** Each cell starts from Earth's present-day temperature and rainfall. During a run, climate drifts and swings around those values: slow worldwide warm and cool periods, and regional wet and dry spells lasting decades to centuries. It is different for every seed and the same for the same seed. It does not follow Earth's real past, so there is no scripted green Sahara. The owner confirmed on 2026-11-14 that the modern baseline is kept even for the opening millennia, although 10,000 BCE was colder in the north and parts of Canada and Scandinavia were still under ice.
2. **Crop homelands are a choice made when a run starts.** On the Earth map, "real" puts each crop group where it grew wild in reality (for example wheat and barley in Southwest Asia, rice in China, maize in Mesoamerica). "Random" places each crop group in a randomly chosen region whose climate suits it. Either way, farming can only be invented where a crop grows wild, and only spreads where the climate suits that crop.
3. **One settlement per cell at most.** A cell (about 51 km across) holds a rural population and, once dense enough, one named settlement that stands for its main village or town.
4. **The world starts already populated by foragers**, at a level each cell's land can support, except for remote islands that people had not reached by 10,000 BCE (for example Madagascar, New Zealand, Iceland and Polynesia). Those stay empty until seafaring arrives in milestone 3.
5. **No herd animals yet.** Herding and pack animals are decided in the milestone 2 or 3 spec.

## What gets built

### 1. Housekeeping

- Move the whole project from .NET 8 to .NET 10.

### 2. Environment in the world file

The import job adds these per cell, and the world file format moves to version 2.

- **Baseline temperature and rainfall** for each month, from the TerraClimate 1981-2010 averages, which are in the public domain.
- **Lakes**: cells that are mostly lake become water. This fixes the Caspian Sea and the Great Lakes. Source: Natural Earth, which is in the public domain. Reservoirs are left out. The Caspian Sea is not in that lake data and is added by a hand-written entry.
- **Rivers**: each land cell records the size of the largest river crossing it (none, minor, major). Source: Natural Earth. Its smallest rivers are left out because they only cover North America and Europe.

### 3. Saved state that is not per cell

- A new kind of saved state for things that are not cells: tables with a stable id per entry, and run settings. It is saved, loaded and checksummed like cell state.
- First users: climate regions, run settings, population groups and settlements. States and figures will reuse it later.

### 4. Changing climate

- The world is covered by about 160 climate regions, each roughly 1,800 km across. Each region carries a temperature shift and a rainfall factor that wander slowly and at random, on both a decades scale and a centuries scale. A worldwide warm or cool trend is added on top.
- A cell's current climate is its baseline adjusted by a blend of the nearest regions, so changes are smooth across the map with no visible region borders.
- Updated once a year. Limits keep it believable: the worldwide temperature shift stays within about 2 degrees C either way, and a cell's rainfall stays between about half and one and a half times its baseline.
- **Biome** is worked out from each cell's current climate (ten biomes, rules in `data/content/biomes.json` following the Koppen dryness threshold), so biomes move: deserts advance and retreat, forest gives way to grassland. One of about ten types (for example tundra, forest, grassland, desert, rainforest). The biome list and its rules live in a data file. Recalculated every ten years.
- **Ice**: cells whose current climate is too cold for any thaw are ice. Coastlines and sea level do not change.

### 5. New-run settings

- The viewer gets a start screen with: seed, crop homelands (real or random), and a button to start. Loading a save is also offered there.
- The headless runner gets matching options.
- Settings are stored in the save and shown in the viewer.

### 6. Foragers

- Foragers are a population group (occupation "forager") in each cell, not a separate per-cell number. In milestone 1 a group's only traits are its occupation, and its only condition is food: the share of its food needs met this year.
- Each cell holds at most 8 groups; groups in a cell with the same traits are always merged.

- Each land cell has a forager population and a limit on how many foragers it can feed, set by its current biome, rivers and coast. The limit moves as climate changes. Rivers add a fixed number of people per km² whatever the biome, so a river through a desert supports many more people than the desert alone. Numbers are in `data/content/foragers.json`.
- Which land is reached at the start: the landmasses holding a list of seed places (the continents and large islands people had reached), plus any landmass within two sea cells (about 100 km) of a reached one, except a list of unreached landmasses (Madagascar, New Zealand, Iceland, Greenland, Antarctica, and the larger Caribbean islands). Remote Pacific islands are unreached because they are too far by sea.
- Yearly: populations grow toward the limit, shrink when the limit falls, and move into neighbouring land cells that have room. This is how climate drives migration.
- The starting population of the world is between 2 and 6 million.

### 7. Farming

- **Crops**: a data file lists about eight crop groups, each with its real homeland and the climate it tolerates.
- **Invention**: each year, a cell where a crop grows wild has a small chance of inventing farming. The chance rises with forager density.
- **Yield**: a farming cell's food depends on how well its current climate suits its best crop, and on rivers. A region that dries out can stop supporting its crop.
- **Spread**: farming passes to neighbouring cells where the crop can grow, by farmers moving in and by foragers taking it up.
- **Harvest**: computed once a year at the harvest season of the cell's hemisphere, with random good and bad years on top of the slower climate swings.
- Farmers are a separate group from foragers in the same cell. Foragers taking up farming move from the forager group to the farmer group, so farming displaces foraging gradually. Farmland feeds many times more people than foraging.

### 8. Famine

- When a bad harvest or a drying climate leaves a group with less food than it needs, its food condition falls, and its people die or leave for neighbouring cells. There are no food stores yet; those come with goods in milestone 2.

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
- [x] The Caspian Sea and Lake Superior are water.
- [x] At the start of a run the Sahara is desert, the Amazon is rainforest and Greenland's interior is ice.
- [x] Over 5,000 years the worldwide temperature shift stays within 2.5 degrees C and no cell's rainfall leaves the range of 0.4 to 1.6 times its baseline. (Measured: largest world shift 0.6 to 1.5 degrees C; rainfall 0.73 to 1.38 times baseline.)
- [x] Climate differs between seeds and is identical for the same seed.
- [x] Between 2% and 25% of land cells change biome at least once in 5,000 years. (Measured: 19% to 23%. At any one time about 3% to 4% of land is in a different biome from the start.)

Foragers
- [x] Starting world population is between 2 and 6 million. (Measured: 4.37 million.)
- [x] With farming switched off, world population stays within 20% of what the land can feed at that time, for 5,000 years. (Measured with farming switched off: 98.6% to 100.3%.)
- [x] Islands marked as unreached stay empty.

Farming
- [x] Farming is invented independently in at least 3 separate regions within 6,000 years in at least 8 of 10 seeds. (Measured: 6 to 8 regions in every seed.)
- [x] The place and date of the first invention differ between seeds.
- [ ] With random crop homelands, the same test passes and at least one origin lies more than 2,000 km from every real homeland in at least 8 of 10 seeds. (Measured: 6 to 8 regions; farthest origin 2,960 to 4,731 km from a real homeland.)
- [x] Farming spreads at an average of 0.3 to 3 km per year. (Measured: 0.48 to 0.96 km a year.)
- [x] After 5,000 years, world population is between 5 and 40 million in every seed. (Measured with real homelands: 18.6 to 30.4 million. Random homelands give 22.9 to 43.7 million; to be retuned with famine in step 8 or 10.)
- [x] No farming ever appears in ice, tundra or desert cells without a major river. (Farming also ends at once where the climate turns against its crop; the farmers go back to foraging.)

Soundness
- [x] Population is never negative and never an invalid number in any cell. (Holds so far with foragers and farmers; to be rechecked with famine.)
- [x] No cell ever holds more than 8 groups, and no two groups in a cell have the same traits. (Holds with foragers and farmers: up to 2 groups in a cell so far.)
- [x] Same seed and settings give the same checksum; save and load mid-run gives the same result as an uninterrupted run.
- [x] A 5,000-year headless run on the build machine takes under 5 minutes. (With climate, foragers and farming: 18 to 52 seconds, varying with the build machine.)

By hand in the viewer:

- [ ] The start screen sets the seed and crop homelands, and a loaded save shows the settings it was started with.
- [ ] Each new map mode is readable and matches the tooltip.
- [ ] You can see a region dry out or cool over centuries, its biome change, and people leave it.
- [ ] You can watch farming start, spread and found settlements, and tell which crop is where.
- [ ] A famine is visible as a drop in population that later recovers.
- [ ] The frame rate stays smooth at every speed.

## Build order

Each step ends with a new development build you can download.

1. Move to .NET 10 (done)
2. Baseline climate, lakes and rivers in the world file, with their map modes (done)
3. Saved state that is not per cell (done)
4. Changing climate, biomes and ice, with their map modes (done)
5. New-run settings and start screen (done)
6. Foragers and the population map mode (done, as a per-cell number)
6b. Population groups: move foragers onto groups, with merging and the per-cell limit (done)
7. Farming: crops, invention, yield, spread, harvest (done)
8. Famine
9. Settlements
10. Statistics file, tuning against the exit tests, release v0.2

## Numbers that will need tuning

How fast and how far climate swings, growth rates, feeding limits per biome, the chance of inventing farming, spread rates and the settlement threshold all start as estimates. Step 10 adjusts them until the exit tests pass. They live in data files, not code.
