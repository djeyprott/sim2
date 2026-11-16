# Milestone 3 spec: Technology (version 1.0, agreed)

Status: agreed by the owner 2026-11-16. Feature list is frozen. Parent: `design-doc.md` version 1.1.

Progress: steps 1 to 4 done 2026-11-16.

- Step 1 (speed): a 5,000-year headless run takes about 115 to 150 seconds on CI's machines, against 165 to 210 before (the 2-minute target is met on the faster machines, not yet on the slowest). Changes: climate's yearly rainfall factors and the needs and gathering rules no longer call Math.Exp and Math.Pow, which were about ten times slower on some build machines; food and luxuries still trade every season, but everyday goods and materials trade once a year in January, sending what four seasons would, with a cheap price path for the goods traded between Januaries; the price-gap measurement runs only in the exit tests; trade's ledger changes are batched. Trade went from about 55 to 107 seconds to about 40 to 57. Exit tests after the change: every milestone 2 test passes except herders outnumbering farmers on open grassland, which failed in 2 of 10 seeds (0.30 against 0.39 and 0.11 against 0.21 million); trading everyday goods yearly changed each run's course, and the result still hangs on when sheep and goats are first herded, which step 5 reworks.
- Step 2 (technology): `technologies.json` lists eight technologies for now (granaries, irrigation, milk and wool, the plough, the wheel, the sail, outrigger canoes, terraced fields); copper, bronze and horses come with their steps. `technology.json` holds how knowledge spreads and is lost. Each population group carries a share per technology (a column on the groups table), averaged when groups merge and carried by people who move between groups or cells. Each year, where a technology's conditions are met (occupation, river, dry climate, coast, terrain, animals kept, technologies required), a group may discover it, more readily with more people in reach (up to 20,000 in its cell and neighbours); shares grow within a group, move toward the other groups of its cell, its neighbours and its trade partners (faster with busier trade, measured by saved sales so a loaded run matches), and fall where fewer than 3,000 people in contact use it. First and independent discoveries (1,000 km from earlier ones) and losses are recorded. The viewer has a Technology map mode (share of people who know it, discovery places marked) and the tooltip lists what each group knows. Technology takes about 15 seconds of a 5,000-year run. First exit runs (6,000 years, untuned): all eight are discovered in most seeds, but far too early (the plough in years 1,500 to 2,900, about 7000 BCE, against about 4500 BCE in history); losses happen only with fewer than 3,000 people in contact; group shares stay valid. The reach measure counts any user from the first discovery, so independent discoveries inflate it; both are for step 8.
- Step 3 (farming and herding effects): each technology lists its effects in `technologies.json`, and a group gets them where at least half its people know the technology and its cell meets the technology's conditions (the plough needs cattle kept in the cell). Irrigation multiplies what dry river land feeds by 1.5, the plough by 1.25 and terraced fields (hills and mountains) by 1.3; the plough also lifts the open-grassland penalty, and farmers spreading to new land bring what they know; granaries halve grain spoilage in store; milk and wool lets pasture feed 1.4 times as many herders and doubles wool. A test sets knowledge directly in two copies of one world and checks each effect's size. Exit runs (6,000 years, discovery still untuned): ploughing cells hold 1.6 to 8.9 times the farmers per km² of like cells (the measure also catches that ploughing cells are older and fuller, not only the 1.25 yield), and some open grassland is ploughed in every seed (1 to 145 cells); population after 5,000 years stays under 40 million, and 39 to 43 million after 6,000 with real homelands. Two milestone 1 and 2 tests failed in one seed each: famine hit 1.94% of farming cell-years against at least 2% (granaries come thousands of years early, so stores grow), and herders against farmers on open grassland (the known failure for step 5); the first is for step 8, when discovery dates are tuned. Farming and herding take about a fifth longer than before.
- Step 4 (metals): six new goods: copper ore and tin, gathered within 80 km of listed real sources (23 copper districts, 12 tin sources), copper and bronze (smelted with charcoal; bronze one part tin to nine of copper), and copper and bronze tools. Copper working and bronze are technologies, discovered only where copper ore (or, for bronze, tin) can be gathered or is in store, and spreading without that limit once known. Recipes can now require a technology that some group in the settlement uses; a settlement wants a recipe's inputs only if it can follow it. Source settlements keep a little ore and tin in store to sell even before they can work it, so trade can carry it to those who can. Better goods can meet another good's need: a set of copper tools counts as 3 of stone, bronze as 5; people use the best they can afford first, and a settlement wants each kind for what the better ones in store do not cover, so metal tools are traded from the few places that make them. A test teaches copper working in one of two copies of a world and checks that only it makes copper and tools, and that wants follow the better tools in store. Also fixed: the technology system kept trade-partner values for a settlement whose trade links were rebuilt without it, so a loaded run could differ from an uninterrupted one. Exit runs (6,000 years, untuned): copper working is first found in every seed (mostly by the Yangtze and Zhongtiao copper in China, years 1,500 to 4,000), and 125 to 375 settlements hold metal tools at the end; bronze comes in only 4 of 10 seeds, late, so tin travels more than 1,000 km in only 2 (the tin exit test fails, for step 8). Herders against farmers on open grassland still fails in one seed (step 5); the famine-share test passes again. A 5,000-year run takes about 10 to 15% longer.

Change log:
- 1.0 (2026-11-16): agreed. The owner confirmed knowledge held by population groups and folding in the backlog item on when animals are first herded. Historical dates in the technology table checked against sources (see "Sources for the dates").
- Draft 2 (2026-11-16): the owner chose the Bronze Age as the end of the list, sea trade and settling islands both in this milestone, and that small isolated groups can lose knowledge. The owner asked whether population groups could hold knowledge; this draft proposes that in place of knowledge per cell (decision 2, awaiting the owner's confirmation).
- Draft 1 (2026-11-16): first draft.

## Purpose

Give the world technology you can watch arise and spread. When this milestone is done, places discover new ways of doing things (irrigation, the plough, copper and bronze, the wheel, sailing), each first in one or a few places, and the knowledge spreads to neighbours, along trade routes and with people who move. Each technology changes something you can see: more food from the same land, open grassland ploughed, metal tools traded from the few places with ore, carts and boats carrying goods further, and islands reached by sea. Runs differ in where and when things are discovered, but stay plausible.

## Decisions

1. **What a technology is.** A named piece of know-how, listed in `data/content/technologies.json` with what it needs before it can be discovered, how readily it is discovered, and what it changes. Technologies are not research projects: nobody chooses to research them; they are found where the conditions are right.
2. **Knowledge is held by population groups** (decided by the owner 2026-11-16). Each group carries, for each technology, the share of its people who know it, as one more condition in the population model: it is averaged by size when groups merge, carried along when people move, and passed between the groups of a cell (farmers to herders, for example) and to neighbouring cells. A technology is in use where a group's share is at least a half. So foragers living beside ploughing farmers do not plough until they take up farming, herders who move carry riding with them, and a small group that loses touch can forget. The design doc's "per culture or region" becomes: held by groups, so regions of shared know-how form on their own, and in milestone 5 it can also spread within a language.
3. **Discovery is local and needs the right conditions.** A technology can be discovered only where its prerequisites are known and its conditions are met (for example the plough needs farmers who keep cattle; bronze needs copper working and tin in store). The yearly chance grows with the number of people in reach, so dense farming regions discover more than thin foraging country.
4. **Knowledge spreads three ways:** to neighbouring cells with people, along trade routes (the more a link carries, the faster), and with people who move (farmers, herders and later seafarers take what they know with them).
5. **Small isolated groups can lose knowledge** (decided by the owner 2026-11-16). A group's share of a technology drifts down each year unless enough people in reach (its own size and the groups it is in contact with, in its cell, neighbouring cells and along trade links) keep it alive; below a set number of people in contact, the share falls. Large or well-connected populations never lose it. Losses are recorded, so a run can show, for example, an island people forgetting the sail.
6. **Scope ends with the Bronze Age** (decided by the owner 2026-11-16): the list stops before iron and writing. Exit runs extend to 7,000 years (3000 BCE), where the bronze-age technologies have had time to appear.
7. **Seafaring opens the sea in two stages** (sea trade and settling islands both, decided by the owner 2026-11-16). From the start, every coastal people can paddle along coasts and across short straits (people had reached Cyprus and fetched Melos obsidian by boat before 10,000 BCE). The sail lets trade cross wider seas cheaply. Outrigger canoes let people reach remote islands across open ocean. Sea trade and the settling of unreached islands both come in this milestone.
8. **Horses come in this milestone**, as the milestone 2 spec promised: wild on the western Eurasian steppe, first herded there, and later ridden.
9. **When animals are first herded is made more historical** (design doc backlog, folded in by the owner 2026-11-16). In milestone 2 sheep and goats were first herded anywhere from about 9900 to 7300 BCE and llamas as early as 9600 BCE, against about 8500 and 4000 BCE in history. An animal is first herded only where farming has stood nearby for some time (a set number of centuries per animal), so herding follows farming as it did in Southwest Asia and the Andes; eastern Sahel cattle keep a forager path, since they may have come before farming there.

## What gets built

### 1. Technologies

About a dozen, from the Neolithic to the Bronze Age. Dates and places are where they first appear in the real record, for comparison only; the simulation does not use them. Most are debated by some centuries; see "Sources for the dates".

| Technology | Needs | First in history (about) | What it changes |
|---|---|---|---|
| Granaries | farming | 9500 BCE, Jordan valley | Grain spoils more slowly in store |
| Irrigation | farming, a river | 6000 BCE, Mesopotamia | Higher yields in dry cells on rivers |
| Milk and wool (secondary products) | sheep, goats or cattle | milk 6500 BCE, Anatolia; wool 4000 BCE, Southwest Asia | Herds feed more people; more wool |
| Copper working | copper ore within reach | 5000 BCE, Balkans (Belovode); perhaps soon after 6000 BCE in northern Iraq | Copper and copper tools (new goods) |
| Bronze | copper working, tin in store | 3300 to 3000 BCE, Southwest Asia and the Balkans | Bronze and bronze tools; tin trade |
| The plough | farming, cattle | 5000 BCE, the Alps (plough marks); early 4th millennium BCE, northern Mesopotamia | Higher yields; ends the open-grassland penalty |
| The wheel | cattle, the plough | 3500 BCE, Mesopotamia and the steppe | Land carrying is cheaper |
| Horse herding | horses wild nearby | 3500 BCE, Botai (northern Kazakhstan) | Horses join the herd animals |
| Horse riding | horse herding | 2200 BCE, widespread from the Pontic-Caspian steppe (earlier riding debated) | Herders spread faster; land carrying is cheaper |
| The sail | boats, a coast | 5500 to 5000 BCE, the Gulf (Ubaid); 3500 BCE, the Nile | Sea trade crosses wider seas cheaply |
| Outrigger canoes | the sail | 1500 BCE, Island Southeast Asia (the Caribbean was reached by canoe about 4000 BCE) | People reach remote islands over open sea |
| Terraced fields | farming, hills or mountains | 3000 BCE, several places | Higher yields on hills |

Boats (coastal paddling) are known everywhere on coasts from the start and are not discovered.

### 2. Discovery

- Each year, for each technology a group does not know, if the group's occupation, its cell and its known technologies meet the conditions: chance = the technology's base chance × how well its conditions are met × people within reach (the cell and its neighbours), scaled so a dense farming region finds it in centuries rather than millennia. A discovering group starts with a small share that grows over the following years.
- The first discovery of each technology anywhere, and each later independent discovery at least 1,000 km from earlier ones, is recorded (technology, cell, date), like farming origins.

### 3. Spread

- **Within a cell:** each year a group's share moves toward that of the other groups in its cell that use the technology, where the group can use it (herders learn the wheel from farmers; foragers do not learn the plough).
- **Neighbours:** the same, more slowly, toward groups of the same or a related occupation in neighbouring cells, at a rate set per technology (some ideas travel more easily than others), and only where the conditions are met (no irrigation without a river; the plough only where cattle are kept).
- **Trade:** settlements' groups learn from the settlements they trade with, faster along busier links.
- **Migration:** people who move (farmers spreading, herders moving, famine refugees, settlers by sea) carry their shares with them, and they are averaged into the group they join.
- **Loss:** as in decision 5.

### 4. Effects

Effects change numbers the existing systems already use, through the shared world state:

- **Farming:** yield factors for irrigation (dry cells on rivers), the plough (all cells, and the grassland penalty lifted), terraced fields (hills and mountains); granaries lower grain spoilage.
- **Herding:** milk and wool raise herd density and wool per herder; horses become a herd animal (steppe homeland); riding speeds herders' spread.
- **Goods:** new goods copper ore, tin, copper, bronze, copper tools and bronze tools; copper ore and tin come from listed real sources (as obsidian); metal tools meet the everyday need for tools better than stone (one unit of metal tools counts as several of stone).
- **Trade:** the wheel and riding lower land carrying costs; the sail opens sea links (below).

### 5. Seafaring

- **Sea links:** coastal settlements link to other coastal settlements across the sea, with sea cells costed by the boats known: paddling allows short hops along coasts and across straits (about two sea cells); the sail allows crossings of several hundred kilometres at a lower cost per cell than land.
- **Settling by sea:** people in coastal cells can settle a coast or island within boat reach that has room. Islands marked unreached in milestone 1 (Madagascar, the Caribbean, the remote Pacific, Iceland, New Zealand) can be reached only with the sail or outrigger canoes, and only where they lie within range.
- Obsidian from Melos and Lipari, and later tin from overseas, can then reach the mainland.

### 6. Viewer

- **Technology map mode:** pick a technology; cells are coloured by the share of their people who know it, with where it was first discovered marked.
- The tooltip and pinned panel list each group's known technologies.
- Sea routes are drawn like land routes in the Trade and Goods modes.

### 7. Headless runner

- The statistics file adds, for each technology, the share of people who know it each year, and a further file lists every recorded discovery and loss (technology, place, date).

### 8. Speed first

- Before adding systems, trade (the slowest system: a third to a half of a run) and prices are made faster, so the 5,000-year run stays well under 5 minutes once technology, sea links and more goods are added. Target: under 2 minutes on CI's slower machines.

## Not in this milestone

- Iron, writing, the alphabet and later technologies (decision 6).
- Choosing what to research; research by states (milestone 4).
- Ideas tied to language or religion (milestone 5).
- Inventors and named people (milestone 6).
- Chariots and warfare effects (milestone 4).
- Disease.

## Exit tests

Automated, on the Earth map with real homelands, across 10 seeds over 7,000 years unless stated:

Discovery
- [ ] Every technology first seen before 3000 BCE in history is discovered somewhere in at least 7 of 10 seeds within 7,000 years. Horse riding and outrigger canoes, first seen later, may come or not.
- [ ] With real homelands, the first irrigation (history 6000 BCE), copper working (5000 BCE), the plough (4500 BCE, between the two dates above) and bronze (3300 BCE) each come within 1,500 years of that date in at least 6 of 10 seeds, and in a different place or year between seeds.
- [ ] The plough, bronze and the wheel are each discovered independently in at least 2 regions 1,000 km apart in at least half the seeds where they appear.

Spread
- [ ] A technology known in a region spreads: 1,000 years after its first discovery, it is known at least 1,000 km away in at least 8 of 10 seeds (for those that can spread that far).
- [ ] Spread follows trade: of two otherwise alike settlements, the one on busier routes learns new technologies sooner (measured as the average delay by route traffic).
- [ ] Knowledge is lost only by small or cut-off groups: no group of more than 10,000 people in contact with others loses a technology, and losses happen in at least some seeds.
- [ ] Group knowledge stays valid: every share is between 0 and 1, and merging and moving keep the total number of people who know a technology.

Effects
- [ ] Farming cells with the plough feed more people per km² than like cells without it, and open grassland is farmed again where the plough is known.
- [ ] Bronze tools are made only where copper and tin both reach, and tin travels at least 1,000 km from its sources in at least 6 of 10 seeds.
- [ ] After 7,000 years (3000 BCE), world population is between 15 and 60 million in every seed. Estimates for 3000 BCE run from about 15 to 45 million.

Herding
- [ ] With real homelands, sheep and goats are first herded within 1,500 years of 8500 BCE, and llamas not before 6000 BCE, in at least 7 of 10 seeds; the milestone 2 herding tests still pass.

Seafaring
- [ ] Sea trade links exist across the Aegean, the Gulf and the Red Sea once the sail is known there.
- [ ] Islands marked unreached stay empty until a people with the right boats lies within range, and some are settled by 7,000 years in at least half the seeds.

Soundness (as before)
- [ ] Goods ledger balances; no negative stocks; population valid; at most 8 groups a cell.
- [ ] Same seed gives the same checksum; save and load mid-run matches an uninterrupted run.
- [ ] A 5,000-year headless run takes under 5 minutes.
- [ ] Every milestone 1 and 2 exit test still passes (with the unreached-islands test changed to allow settling by sea).

By hand in the viewer:

- [ ] The technology map mode is readable, and you can watch a discovery spread out from where it was made.
- [ ] Sea routes appear once boats and sails allow them.
- [ ] Ploughed grassland and bronze-working regions look plausible.
- [ ] The frame rate stays smooth at every speed.

## Build order

Each step ends with a new development build you can download.

1. Speed: faster trade and prices
2. Technology list, knowledge held by groups, discovery, spread and loss; technology map mode
3. Farming and herding effects (granaries, irrigation, plough, terraces, milk and wool)
4. Metals: copper ore and tin sources, copper and bronze goods and recipes
5. Horses: horse herding and riding; first herding follows farming
6. Seafaring: sea links, the sail, settling islands, outrigger canoes
7. Statistics file, tooltip and viewer polish
8. Tuning against the exit tests, release v0.4

## Numbers that will need tuning

Base discovery chances, spread chances along borders and trade, effect sizes, metal source richness, sea carrying costs and boat ranges. They start as estimates in data files and are tuned in the last step.

## Sources for the dates

Checked 2026-11-16; dates are the earliest widely accepted evidence and are debated.

- Irrigation: canal irrigation at Choga Mami, eastern Iraq, about 6000 BCE (Oates; Helbaek 1972).
- Copper: smelting at Belovode, Serbia, about 5000 BCE, the earliest securely dated (Radivojević et al. 2010); possibly soon after 6000 BCE at Yarim Tepe, Iraq.
- The plough: plough marks at Sion, Switzerland, from the start of the 5th millennium BCE (van Willigen et al. 2024); in Mesopotamia thought to begin in the early 4th millennium BCE.
- Bronze: tin bronze in the Near East and the Balkans from about 3300 to 3000 BCE; arsenical bronze earlier. Tin sources include the Taurus mountains (Kestel), the Erzgebirge (mined from about 2500 BCE) and Southeast Asia.
- Horses: husbandry, milking and probably riding at Botai about 3500 BCE; the modern domestic line (DOM2) and widespread horse mobility from about 2200 BCE (Librado et al. 2024); riding by Yamnaya herders around 3000 BCE is argued but disputed.
- The sail: a painted disc showing a sailing boat and seagoing reed-boat remains at H3, As-Sabiyah, Kuwait, 5500 to 5000 BCE (Carter 2006); sails on the Nile by about 3500 BCE.
- Granaries (Dhra', Jordan, about 9500 BCE), milk (pottery residues in northwest Anatolia, 7th millennium BCE), the wheel (about 3500 BCE) and outrigger canoes (Austronesian voyaging, about 1500 BCE) are from general knowledge and were not re-checked.
