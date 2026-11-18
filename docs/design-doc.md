# sim2 design doc (version 1.4)

Status: top-level design agreed. Milestone 0 is complete and released as v0.1. Milestone 1 is complete and released as v0.2 (`milestone-1-spec.md`). Milestone 2 is complete and released as v0.3 (`milestone-2-spec.md`). Milestone 3 is complete and released as v0.4, with fixes as v0.4.1 (`milestone-3-spec.md`). Milestone 4 (states and war) is next; its spec is in draft (`milestone-4-spec.md`).

Change log:
- 1.4 (2026-11-17): milestone 4 spec in draft; backlog item on tribes and nomads.
- 1.3 (2026-11-17): milestone 3 fixes released as v0.4.1 (spec 1.1): metal know-how and horse herding kept near their goods or animals, no cattle in tropical forest, the plough with wheat and barley; backlog items on copper working in Europe and long-distance tin.
- 1.2 (2026-11-17): milestone 3 complete, released as v0.4; backlog items on the sail around the Gulf and the Red Sea, the timing of horses, and Austronesian voyaging.
- 1.1 (2026-11-16): milestone 3 spec agreed: technology is held by population groups; the backlog items on when animals are first herded and on the speed of trade move into milestone 3.
- 1.0 (2026-11-16): milestone 2 complete, released as v0.3; backlog items on when animals are first herded and on the speed of trade.
- 0.9 (2026-11-15): milestone 2 spec agreed: herding comes in milestone 2, so the herding backlog item moves into it.
- 0.8 (2026-11-15): milestone 1 complete, released as v0.2.
- 0.7 (2026-11-15): backlog item on farming open grassland before the plough.
- 0.6 (2026-11-14): population model agreed: population groups in the manner of Victoria 2's pops.
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
- **Populations**: population groups (see the population model) that grow, starve, sicken, migrate and change occupation, language and religion.
- **Technology**: discovered locally, held by population groups and spread through contact, trade and migration.
- **Organizations**: states and other bodies that hold territory or resources, fight wars, split and collapse.
- **Culture**: languages, religions and ideas that spread and split.
- **Historical figures**: named individuals with personalities, family ties, goals and life events. Ordinary people stay as population numbers.

## Population model

People are held in **population groups**, in the manner of Victoria 2's pops. Agreed 2026-11-14.

- **Groups:** each land cell holds a short list of groups, at most 8. A group has a size, a few traits and some conditions.
- **Traits** split people into different groups:
  - **Occupation**, such as forager, farmer, later herder, craftsman, trader, soldier or priest. Occupations live in a data file and become available by era and technology; milestone 1 has only forager and farmer. Each occupation belongs to a class (lower, middle or upper) and lives in the countryside or the settlement, so class and town-or-country are not separate traits.
  - **Language** (milestone 5).
  - **Religion** (milestone 5).
- **Conditions** are numbers each group carries, averaged by size when groups merge:
  - **Needs met**, in tiers as in Victoria 2: life needs, everyday needs and luxuries. In milestone 1 the only need is food. Needs drive growth, migration, occupation change and later unrest.
  - Later: savings, literacy, unrest, and political views as a percentage mix.
- **Merging:** groups in the same cell with identical traits are always one group. When a cell would go over the limit, its smallest group joins the most similar group.
- **How people change group:** birth and death within a group; changing occupation (for example foragers taking up farming); moving to another cell, keeping their traits; changing language (assimilation); changing religion (conversion).
- **Knowledge:** from milestone 3, each group carries the share of its people who know each technology, averaged by size when groups merge (agreed 2026-11-16).
- **Held elsewhere, not on groups:** customs and values belong to a language's culture or a religion; which state a group belongs to comes from who rules its cell.
- **Not tracked:** individual ordinary people, households and families, ages and sexes, and ancestry separate from language. Named historical figures are a separate system, linked to the groups and organisations they come from.
- **Storage:** groups are rows in a world-state table with stable ids, with a per-cell index. Speed is measured from the start against the 5-minute limit for a 5,000-year run.

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
| 1 | Land and people | Climate that changes, rivers and lakes, biomes, forager population, origin of farming, settlement founding, migration, famine | Complete, v0.2 |
| 2 | Goods and trade | Production, stockpiles, consumption, spoilage, trade routes, herding | Complete, v0.3 |
| 3 | Technology | Discovery, diffusion, effects on production and population, seafaring | Complete, v0.4.1 |
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
- End-of-Ice-Age start: a colder north and remaining ice sheets (Canada, Scandinavia) that melt back over the first few thousand years. Considered for milestone 1 and declined by the owner on 2026-11-14 in favour of the modern baseline.
- Climate change caused by people in later eras (consider with milestone 7).
- Lower Ice Age sea level and changing coastlines.
- Open grassland is hard to farm before the plough. Steppe, prairie and pampas have tough root mats and unreliable rain, so early farmers kept to forest edges and river valleys while herders held the open grassland (the Pontic steppe was herding country from about 4500 BCE and was ploughed at scale only in the 18th and 19th centuries). Milestone 1 applies a grassland yield factor of 0.5 away from rivers; milestone 2 adds herders who outcompete early farmers there; milestone 3's plough lifts the penalty where farmers who keep cattle know it.
- Disease.
- The Aral Sea at its historical size, and small straits that are currently closed (for example Bali joined to Java).
- The sail around the Gulf and the Red Sea. In milestone 3's runs the sail is found elsewhere first and never reaches either sea, though it was first seen on the Gulf (Ubaid boats, 5500 to 5000 BCE); the Gulf and Red Sea coasts hold few farming settlements in the simulation.
- Horses are first herded about 1,500 years early (5800 to 4800 BCE against Botai's 3500 BCE) and riding comes about 2,000 years early; tuned in milestone 3 only so that horse herding is found in most runs.
- Austronesian voyaging: outrigger canoes (history 1500 BCE) need 7,000 years of farming and so do not come before 3000 BCE; remote Oceania stays unsettled until a later era's runs.
- Copper working in Europe. Since v0.4.1 it is kept near copper and no longer spreads from Anatolia, and Europe's copper districts are farmed too late to find it before 3000 BCE (history: the Balkans by about 5000 BCE). A per-place time to discovery, or know-how carried along trade routes without being used, could fix it.
- Long-distance tin. Tin travels only as far as the lands that work bronze (under 1,000 km), so Afghan and Central Asian tin never reaches Mesopotamia; it needs merchants or goods traded for resale.
- Tribes and nomads: herders and foragers forming tribal confederations that raid settled states and sometimes conquer them (the steppe peoples). Left out of milestone 4 by the owner on 2026-11-17; consider with milestone 5 or 6.
- Globe view.
- Charts in the viewer.
