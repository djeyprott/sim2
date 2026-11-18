# Milestone 4 spec: States and war (version 1.0, agreed)

Status: agreed by the owner 2026-11-18. Feature list is frozen. Parent: `design-doc.md` version 1.5.

Progress: step 1 done 2026-11-18.

- Step 1 (organizations, ruled cells, political map): a polity system (`polities.json`, last in the update order) holds the organizations table (kind, name, founded, ended, capital, level, and the temperament: aggression, greed and expansion, each drawn as exp(0.35 × a normal draw) between 0.4 and 2.5), the positions table (Ruler, Heir and General for each polity, held by nobody until milestone 6) and a per-cell field for the ruling polity. A polity is named after its capital settlement for now; names by region are for milestone 5's languages. A founded polity rules its capital and the land around it; an ended one keeps its row as history and rules nothing. Nothing founds polities yet (step 2). The viewer has a Political map mode (each polity in its own colour, states stronger than chiefdoms, borders darker, capitals white) and the tooltip names the ruler, its level, founding date, size and temperament. A test founds a polity at a settlement, checks its cells, positions and temperament, and that it is saved, loaded and ended correctly.

Change log:
- 1.0 (2026-11-18): agreed by the owner as draft 2 stood.
- Draft 2 (2026-11-17): the owner chose exit runs of 9,000 years, rules plus a temperament per polity, tribes and nomads later, and shared rule speeding the spread of knowledge.
- Draft 1 (2026-11-17): first draft.

## Purpose

Give the world political powers you can watch rise, fight and fall. When this milestone is done, chiefdoms grow out of dense farming country, some become states that rule many settlements from a capital, they take tribute, raise soldiers, go to war over land, and break apart when they grow too large, lose wars or starve. Each run's political map differs, but the first states arise where and roughly when they did: in long-farmed, crowded river valleys and plains.

## Decisions

1. **Organizations come first** (design doc). A general organizations table holds every organization: kind, name, founding date, members, positions, resources and goals. Polities (chiefdoms and states) are the only kind built in this milestone; tribes, religions and merchant houses reuse it later. Positions (ruler, heir, general) are listed but left empty until named people arrive in milestone 6; until then a polity acts by simple rules shaped by its temperament (decision 11).
2. **Territory is held cell by cell.** Each land cell is ruled by at most one polity (a per-cell field). Which polity a population group belongs to comes from who rules its cell, as the population model says. Cells with nobody in them can be ruled, so borders are not full of holes, but only within reach of the polity's settlements.
3. **Polities grow out of farming.** A chiefdom forms where a settlement is large, its farmers have a surplus, and farming has stood nearby for a long time; it rules the settlement's cell and its neighbours. It grows by drawing in nearby unruled settlements (peacefully, by weight of numbers and trade) and by conquest. A chiefdom becomes a state once it rules enough people and settlements; a state that falls below that becomes a chiefdom again. Foragers and herders do not form polities in this milestone (decided by the owner 2026-11-17); tribal confederations and nomad raids are in the design doc's backlog.
4. **Rule costs and pays.** A polity takes tribute: a share of its farmers' grain and of its settlements' goods goes to its capital's store. Tribute feeds a new upper-class occupation, the **elite** (rulers, chiefs and their households, in the capital), and a new **soldier** occupation. The more a polity takes, the more its people's needs go unmet, which raises **unrest** (as milestone 2 promised).
5. **Reach is limited.** A polity's hold on a cell weakens with distance from its capital, measured as trade-link cost (so rivers, the wheel and riding extend it, and mountains shorten it). Far cells pay less tribute and are the first to break away. This is what keeps empires from covering continents in a Bronze Age world.
6. **War is abstract.** No armies move across the map. A polity may declare war on a neighbour when it is stronger, short of land or food, or the neighbour holds goods it wants. Each year of war, the two sides' strength (soldiers, bronze and copper weapons in store, chariots, fortified settlements, distance from home) decides who takes border cells, with chance. Battles kill soldiers and some farmers, burn stores and can raze settlements. Wars end in peace when one side is exhausted or both are; a polity that loses its capital is conquered.
7. **Polities collapse.** A polity breaks apart when unrest is high, after a lost war, in a long famine, or when its reach is stretched too far. Its far provinces become new chiefdoms or states (successors), or fall back to no rule. Every founding, war, peace, conquest and collapse is recorded.
8. **Technology matters, and states do not choose research** (milestone 3 deferred "research by states" here). States do not research: knowledge spreads and is found as before. Instead, being ruled by one polity speeds the spread of knowledge between its settlements: shared rule acts like a trade link (decided by the owner 2026-11-17).
9. **Two new technologies for war:** **fortifications** (walled settlements; found in large settlements under threat; defenders count stronger) and **chariots** (needs the wheel and horse riding; promised by milestone 3; attackers count stronger on open land). Bronze and copper weapons are a new good, **weapons**, made from bronze or copper by craftsmen and wanted by soldiers.
10. **Run length grows to 9,000 years** (decided by the owner 2026-11-17). The first states arose about 3500 to 3100 BCE and the first empires about 2300 BCE, after milestone 3's 3000 BCE end, so exit runs extend to 1000 BCE, with technology still ending at the Bronze Age. The 5-minute limit stays on a 5,000-year run.
11. **Each polity has a temperament** (decided by the owner 2026-11-17). Drawn at founding from the run's random streams: how aggressive (how readily it goes to war), how grasping (how much tribute it takes) and how expansive (how readily it draws in its neighbours), each a number around 1 that scales its rules. Successors draw their own. So neighbouring polities behave visibly differently until named rulers take over in milestone 6.

## What gets built

### 1. Organizations

- A `WorldState` table of organizations with stable ids: kind, name (from a simple name generator by region), founded, ended, capital cell, polity level (chiefdom or state), and resources (the capital settlement's store stands for the treasury).
- A positions table (organization, position name, holder: none until milestone 6).
- Each polity's temperament (aggression, greed, expansion), saved with it.
- A per-cell field: ruling polity (none, or an organization id).

### 2. Forming and growing

- Each year, a settlement not yet ruled may found a chiefdom if: its people number at least a set size (about 3,000 to start), its store holds a surplus of grain, and farming has stood in its cell for a set number of years. The chance grows with size and crowding (people within reach).
- A polity draws in unruled settlements within its reach each year with a chance that grows with its strength and their trade with it, and claims the empty cells between them.
- Chiefdom to state: at least about 20,000 people ruled and 5 settlements (numbers to tune); back to chiefdom below them.

### 3. Tribute, elite and soldiers

- Each year, settlements pay a share of their grain and other goods to the capital, less with distance (reach, decision 5). The capital's store feeds the elite and soldiers.
- Elite: a share of the capital's people, upper class, living in the settlement; their luxury needs drive trade in luxuries and metal goods to the capital.
- Soldiers: raised from farmers in proportion to what tribute can feed and how threatened the polity is; they go back to farming in peace.
- Unrest: a condition on population groups, rising with unmet needs and heavy tribute and falling slowly; high unrest in a cell lowers its tribute and makes it more likely to break away.

### 4. War and peace

- Declaring war: once a year, each polity weighs each neighbour (relative strength, its own food and land shortage, goods the neighbour holds). The strongest case above a threshold leads to war.
- Each year of war: the border cells between them are contested; the side with the greater local strength takes some with chance. Losses: soldiers on both sides, farmers in contested cells, stores in taken settlements, settlements razed with a small chance.
- Peace: when war exhaustion (losses against size) passes a set level for either side, or after a set number of years. Taking the enemy's capital ends the war and its polity.
- Strength: soldiers, times a factor for weapons in store (bronze better than copper), chariots on open land, fortifications for defenders, less with distance from the capital.

### 5. Collapse

- Each year a polity's cohesion is worked out from unrest, famine, war exhaustion and how stretched its reach is; below a threshold it may split: provinces far from the capital break off as successor polities (or fall back to no rule if too small).
- A polity whose capital is lost or abandoned ends; its other settlements become successors.

### 6. Technology

- Fortifications and chariots added to `technologies.json`, with effects on war strength (two new effect names).
- Weapons: a new good with recipes from copper and from bronze; soldiers' need for weapons.
- Rule shares knowledge: settlements of one polity count as linked for knowledge spread.

### 7. Viewer

- **Political map mode:** each polity in its own colour, borders drawn, capitals marked; chiefdoms paler than states; wars shown on contested borders.
- The tooltip and pinned panel show who rules the cell, the polity's level, capital, size, age, tribute and unrest, and any war it is in.

### 8. Headless runner

- The yearly statistics file adds the number of chiefdoms and states, people ruled, the largest polity's people and area, and wars under way.
- A further file lists every founding, level change, war, peace, conquest and collapse (with places and dates); the regional file adds people ruled and polities per region.

## Not in this milestone

- Named rulers, dynasties and succession (milestone 6): positions stay empty.
- Languages and religions, and wars over them (milestone 5).
- Diplomacy beyond war and peace: alliances, vassals, marriages (consider with milestone 6).
- Armies that move across the map; sieges as events; naval war.
- Iron, writing and later technologies (later eras); states choosing research.
- Tribes of herders and foragers, confederations and nomad raids on settled states (design doc backlog).

## Exit tests

Automated, on the Earth map with real homelands, across 10 seeds over 9,000 years (to 1000 BCE) unless stated:

Formation
- [ ] The first chiefdom forms within 1,500 years of 5000 BCE, and the first state within 1,000 years of 3300 BCE, in at least 6 of 10 seeds; first states form in long-farmed river valleys or plains (Mesopotamia, the Nile, the Indus, the Yellow River or their like with random homelands).
- [ ] States arise independently in at least 2 regions 1,000 km apart in at least 7 of 10 seeds.
- [ ] No polity forms before farming in its region, and none in foraging or herding country.

Size and change
- [ ] At the end, the largest polity rules between 0.5 and 5 million km² and no more than a quarter of the world's people in every seed (the largest Bronze Age empires were under 1 million km²).
- [ ] Polities rise and fall: at least a set number of collapses and conquests per seed, and the median state lasts between 100 and 1,000 years.
- [ ] Borders are steady: in peace, fewer than a set share of ruled cells change hands each year.

War
- [ ] Wars happen in every seed with states; the stronger side wins most wars; fortifications and chariots each raise the share of wars won by their users.
- [ ] War costs people: deaths in war are recorded, and a polity at war grows more slowly than at peace.
- [ ] Temperament shows: the most aggressive third of polities start at least twice as many wars per year as the least aggressive third.

Rule
- [ ] Tribute flows to capitals: capitals' stores hold more per person than other settlements, and the elite live only in capitals.
- [ ] Unrest rises with unmet needs and heavy tribute, and polities with high unrest break apart more often.

Soundness (as before)
- [ ] Goods ledger balances; no negative stocks; population valid; at most 8 groups a cell.
- [ ] Same seed gives the same checksum; save and load mid-run matches an uninterrupted run.
- [ ] A 5,000-year headless run takes under 5 minutes.
- [ ] Every milestone 1 to 3 exit test still passes.

By hand in the viewer:
- [ ] The political map is readable, and you can watch states rise, fight and fall.
- [ ] Capitals, borders and wars are clear, and the tooltip explains who rules a place.
- [ ] The frame rate stays smooth at every speed.

## Build order

Each step ends with a new development build you can download.

1. Organizations table and ruled cells; political map mode
2. Chiefdoms and states forming and growing
3. Tribute, elite, soldiers and unrest
4. War and peace; weapons, fortifications and chariots
5. Collapse and successor polities
6. Statistics, events file and viewer polish
7. Tuning against the exit tests (with the 9,000-year runs), release v0.5

## Numbers that will need tuning

Founding size and chance, the state threshold, tribute shares, reach costs, soldier shares, war thresholds and strength factors, exhaustion, cohesion and split rules. They start as estimates in a new `data/content/polities.json` and are tuned in the last step.
