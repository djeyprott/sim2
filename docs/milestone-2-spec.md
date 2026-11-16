# Milestone 2 spec: Goods and trade (version 1.3, agreed; 1.2 awaits the owner's confirmation)

Status: agreed by the owner 2026-11-15; amended 2026-11-15 (1.1) and 2026-11-16 (1.2, 1.3). Feature list is frozen. Parent: `design-doc.md` version 0.9.

Progress: steps 1 to 7 done 2026-11-15; step 8's tuning done 2026-11-16, and every automated exit test passes. Release v0.3 waits for the owner to confirm change 1.2 and check the viewer by hand.

- Step 1: goods, the ledger and stockpiles; harvests go into settlement stores and farmers eat from them monthly. The ledger balances to about 1e-12 in every seed.
- Step 2: wood, flint, clay, fibre, salt and obsidian are gathered by the farmers of each settlement, up to a stock per person until prices take over in step 4. Obsidian comes from 18 real sources; salt from 16 real deposits plus dry coasts and salt-lake shores.
- Step 3: craftsmen (a new occupation living in settlements, up to a tenth of a settlement's store-fed people) make stone tools, pottery, cloth and ornaments by recipes. Farmers and craftsmen use up everyday and luxury goods; needs met adds up to 15% to farmers' growth. With craftsmen eating from the store, farmers now grow only while the store holds half a year's food (was a quarter).
- Step 4: prices in food, worked out when needed (never stored): a good's base value (goods.json) times (wanted / in store) ^ 0.7, between a fifth and five times the base (prices.json). A settlement wants food to the next harvest plus half a year, a year of needs plus the craft reserve, and the materials its craftsmen would use. Craftsmen take up only work that pays more than the grain they eat, by the best-paying recipe; farmers gather only what is wanted and worth their time (this replaced step 2's stock-per-person cap). The tooltip shows prices.
- Step 5: each settlement links to the six it can reach most cheaply over land (hills and mountains cost more, rivers less); links are rebuilt every five Januaries around settlements founded or abandoned, from a saved record so loaded runs match. Each season (every three months, for speed) goods move along links from the cheaper end to the dearer end when the gap beats the carrying cost, arriving the next season and counted as in transit meanwhile. To support trade, prices became linear in scarcity (elasticity 1), people use dear goods more sparingly, and a settlement keeps extra stock of non-food goods it has sold lately (0.75 times its sales record). The viewer has a Trade map mode: busier routes are brighter and thicker. Exit tests (10 seeds, real and random homelands): price gaps along links shrink for every traded good; every linked region has routes, and 90% to 100% of settlements are on one. Obsidian reaches only about 190 km from its sources against the 500 km test: sellers stocking harder for resale (salesWanted 2) took it to about 310 km but widened price gaps for clay, salt and obsidian. That, and stores halving famine deaths (0.40 to 0.57 with real homelands, trade did not change it), are left for tuning in step 8.
- Step 6: herding. `livestock.json` lists sheep and goats (Zagros), cattle (eastern Anatolia and the eastern Sahel), pigs (eastern Anatolia and the Yellow River) and llamas and alpacas (Andes); `herding.json` holds the numbers. People in a cell where an animal lives wild first take it up there, once per homeland. Herders (a new occupation) live on pasture (grassland and savanna in full, Mediterranean scrub, desert and tundra in part) and eat their herds' meat and milk; they grow toward what the pasture feeds on the land the cell's farmers leave, and spread to neighbouring pasture. Foragers in cells with animals take up herding; farmers do too where herds feed more people than crops (open grassland away from rivers), and farming does not spread into land herders hold that way. Pigs are kept only by farmers, and every animal passes from village to village among farmers. Herders and farmers put wool and hides into nearby settlement stores. The viewer has a Herding map mode, and the tooltip lists the animals kept and what the pasture can feed. Exit tests (real homelands, 10 seeds): herding is first taken up in 4 or 5 separate regions within 6,000 years; after 5,000 years 0.8 to 1.5 million herders live on open grassland against 0.25 to 0.41 million farmers, out of 4.6 to 6.9 million herders in all. Left for tuning: when each animal is first herded varies a lot (llamas as early as 9900 BCE, against about 4000 BCE in history), and herders reach the steppe east of the Caspian by about 7000 BCE, some 2,500 years early.
- Step 7: a Goods map mode: choose a good, and whether to see how much each settlement holds per person (with goods on their way) or its price against the good's usual value. The tooltip and the pinned cell panel already listed a settlement's store, prices and each group's needs met (steps 1 to 4), and the top bar world food in store (step 1). Choosing Trade from the menu now draws the routes; before, only the screenshot script did. The statistics file adds herders and craftsmen, and for each good what was produced, consumed, spoiled, lost and traded each year and what is in store and in transit (the ledger now keeps a running total of goods traded); a third file per run lists where and when each animal was first herded. In the statistics runs, only about 60 loads of obsidian a year are traded in the whole world by 5000 BCE, which bears on obsidian's reach (step 8).
- Step 8 (tuning), changes: farmers gather a good while it pays, until its price falls to what their time is worth but never past the stock at which the price bottoms out (five times what is wanted), so sources of valuable goods keep stock for trade; people use an everyday or luxury good in proportion to the square of its value over its price (`needElasticity` 2); luxuries may cost up to 20 times their value (`luxuryMaxFactor`), so a rare good can still be traded on past its frontier; trade needs a 5% margin and skips sends worth under 0.05 food; animals are first kept readily where farmers live in or beside a homeland cell, and by foragers on pasture only at a fiftieth of that rate (`foragerInventionFactor`); herders and animals spread at 0.02 a year (was 0.05); settled farmers grow only while their store held three quarters of a year's food at harvest (was half). The price-gap test now compares the gap along each link that carried a good in January before and straight after the trade (before, all links counted, so a good sent one step down a chain could widen the next gap).
- Step 8 results (exit tests, 10 seeds each, all passing): real homelands: obsidian reaches 605 to 661 km from its nearest source; shortages in 2.5% to 3.1% of farming cell-years; like for like, stores leave 0.30 to 0.42 of the famine deaths; herding first taken up in 3 to 5 separate regions within 6,000 years; after 5,000 years 0.45 to 1.26 million herders on open grassland against 0.23 to 0.44 million farmers; population 18.6 to 29.5 million; price gaps shrink for every traded good. Random homelands: population 23.0 to 42.0 million (the 40 million test applies to real homelands only). Still unhistorical, left for later: when animals are first herded varies a lot between seeds. Sheep and goats came between 9930 and 7270 BCE (history: about 8500 BCE); llamas, in the 6 of 10 runs that herded them within 6,000 years, between 9625 and 5630 BCE (history: about 4000 BCE).
- Results after step 4 (real homelands, 10 seeds): shortages in 4.0% to 4.5% of farming cell-years; famine deaths with a store are 0.47 to 0.68 of those without, short of the halving test in most seeds, which trade (step 5) and tuning (step 8) are expected to close; severe famines under 0.1%; population 18.1 to 26.1 million after 5,000 years.
- A 5,000-year headless run takes 55 to 95 seconds depending on the build machine (90 seconds after step 4, climate the largest part; 136 to 161 seconds with trade, of which trade 42 to 69; 115 to 210 seconds after step 8, with trade 54 to 107); the runner prints each system's time. Each exit-test suite takes 15 to 40 minutes and runs only when started by hand. The repository was made public on 2026-11-15 so GitHub Actions stays free.

Change log:
- 1.3 (2026-11-16, added by the owner): the goods map mode also draws the trade routes of the chosen good, thicker for routes that carry more of it.
- 1.2 (2026-11-16, the owner said to go ahead with the planned fix; awaits confirmation before release): the test that stores at least halve famine deaths compares like with like. Cells with stores are mostly long-settled cells near what their land feeds, and cells without are mostly young or small ones with room to spare, so the raw rates compared different kinds of cells (stores left 0.44 to 0.82 of deaths overall, but 0.30 to 0.50 within each band of fullness). Each band of fullness (farmers over what the land feeds: under 0.6, 0.6 to 0.85, 0.85 to 1, 1 or more) now has its store death rate weighed by the farmer-years without a store in that band.
- 1.1 (2026-11-15, agreed by the owner): famine numbers grounded in pre-modern records. The test that stores halve how often famine strikes is replaced: shortages should hit 2% to 5% of farming cell-years, stores should at least halve famine deaths, and severe famines should be rare. Famine kills less: a fifth of the shortfall dies instead of half. See "Famine numbers" below.
- 1.0 (2026-11-15): agreed and frozen.
- Draft 2 (2026-11-15): the owner settled the four open decisions: herding comes in this milestone; local prices measured in food; trade over land and rivers only; about ten goods.
- Draft 1 (2026-11-15): first draft.

## Purpose

Give the world an economy you can watch. When this milestone is done, harvests fill settlement stores and carry villages through bad years, craftsmen turn raw materials into pots, tools and cloth, and goods travel from settlement to settlement along trade routes that you can see on the map. Rare goods such as obsidian and salt spread hundreds of kilometres from the few places they come from. Every unit of every good is accounted for from the moment it is made until it is eaten, used up, spoiled or lost.

## Decisions

1. **Goods live in settlement stockpiles.** A settlement's stockpile holds quantities of each good. Cells without a settlement keep no stores: their people eat what they produce that year, and anything left over is lost. This makes settlements matter: they are where food is stored and where trade happens.
2. **Goods are bulk quantities, not items.** A good is a name, a unit (for example tonnes), a spoilage rate and a weight for transport. The list lives in `data/content/goods.json`.
3. **Every unit is accounted for.** For each good the world keeps a ledger: produced, consumed, spoiled, lost (for example in an abandoned settlement), in stores and in transit. Produced always equals the sum of the rest.
4. **Needs come in tiers**, as in the population model. Life needs: food. Everyday needs: tools, pottery and cloth. Luxuries: ornaments, obsidian and salt. A group's needs met is a condition it carries. Unmet life needs cause famine, as now. Everyday and luxury needs met raise growth a little and drive demand. They do not yet cause unrest (unrest comes with states in milestone 4).
5. **Local prices, no money** (decided by the owner 2026-11-15). Each settlement has a price for each good, measured in food, which rises when the good is scarce there and falls when it is plentiful. Trade moves goods from where they are cheap to where they are dear, if the price gap beats the cost of carrying them. Coins and money come later.
6. **Trade runs over land and rivers** (decided by the owner 2026-11-15). Each settlement links to nearby settlements within a few days' travel. Carrying goods costs more over mountains and less along rivers. Goods can pass along a chain of links, so obsidian can travel a long way by being traded on from village to village. Sea and coastal trade arrive with seafaring in milestone 3.
7. **Herding comes in this milestone** (decided by the owner 2026-11-15). The milestone 1 spec left herd animals to the milestone 2 or 3 spec. It comes now because livestock gives goods (meat, milk, wool, hides) and lets herders hold the open grassland that farmers cannot use well (design doc backlog).
8. **Settlement threshold.** Unchanged at 1,000 farmers unless the owner decides otherwise after watching runs. Because stores live in settlements, a higher threshold would also mean fewer places that can store food.

## What gets built

### 1. Goods

- `data/content/goods.json` lists about ten goods for the Neolithic (decided by the owner 2026-11-15): grain (all staple crops), wild food, wood, flint and stone tools, clay and pottery, fibre and cloth, salt, obsidian and ornaments, plus the livestock products of herding: meat and milk, wool, hides.
- Each good has a unit, a yearly spoilage rate (grain loses about a tenth a year in store; stone does not spoil), a carrying weight and its needs tier, if any.

### 2. Natural resources

- Some goods come only from particular places. Flint and stone come from hills and mountains; clay from river valleys and lowland; salt from coasts, salt lakes and listed inland deposits; obsidian only from a hand-written list of real sources (for example central Anatolia, Melos, Lipari, the Ethiopian Rift and central Mexico).
- The world file is not changed; resources are worked out at the start of a run from terrain, rivers and the list in `data/content/resources.json`.

### 3. Stockpiles

- Each settlement gets a stockpile with a quantity per good, saved and checksummed like everything else.
- When a settlement is abandoned, its stockpile is lost and recorded as lost in the ledger.

### 4. Production

- **Farmers** put their harvest into the settlement's grain store instead of eating it at once.
- **Foragers** produce wild food and eat it the same year. They keep no stores and do not trade in this milestone.
- **Craftsmen** are a new occupation that lives in settlements. They turn inputs into outputs by recipes in `data/content/recipes.json` (for example clay and wood into pottery, fibre into cloth, flint into tools). The share of a settlement's people who work as craftsmen rises and falls with the prices of what they make.
- **Gathering of materials** (wood, clay, flint, salt, obsidian) is done by the farmers and herders of the cell, as a small share of their time, where the resource is present.
- **Herders**: a new occupation that keeps livestock on grassland and savanna, as described in section 8.

### 5. Consumption and food stores

- Each month every group eats from its settlement's food store. A group in a cell without a settlement eats from its own harvest, as in milestone 1.
- Famine now happens when the store runs out, not when one harvest is short. Stores spoil at each good's rate.
- Famine kills a fifth of the shortfall over a year and sends three tenths away: an ordinary famine (food at 75% to 85% of needs) kills 3% to 5%, and a whole year without food kills a fifth (see "Famine numbers").
- Everyday and luxury goods are used up at a yearly rate per person. The share of these needs met is recorded on each group.

### 6. Prices and trade

- Each month each settlement works out a price for each good from its stock and its people's needs.
- **Trade links**: each settlement links to settlements within about 150 km by land (more along rivers). A link's cost depends on distance, terrain (mountains cost more) and rivers (cheaper). Links are rebuilt when settlements are founded or abandoned.
- Each month goods move along links from the cheaper end to the dearer end while the price gap is larger than the carrying cost. The amount moved per month is limited, so goods flow gradually and travel far only by being passed on.
- Goods on the way are recorded as in transit in the ledger.
- A **trade route** is a link that carries goods steadily. Routes are what the viewer draws.

### 7. Needs and growth

- Everyday and luxury needs met add a small bonus to growth. Food remains what limits population.
- Needs met is stored per group, averaged by size when groups merge, as the population model sets out.

### 8. Herding

- **Livestock**: a data file lists herd animals with real homelands, like crops: sheep and goats (Southwest Asia), cattle (Southwest Asia and the Sahel), pigs (Southwest Asia and China), llamas and alpacas (Andes). Horses are left to milestone 3.
- Herding is first taken up where an animal lives wild, and spreads like farming, by herders moving and by foragers and farmers taking it up.
- Herders are an occupation. A cell's herders are fed by its grassland, savanna or scrub, and produce meat and milk, wool and hides. Farmers in the same cell may keep some animals too.
- On open grassland, herders do better than farmers, so herders come to hold the steppe, prairie and pampas while farmers keep to forest edges and river valleys.
- Real or random homelands follow the same run setting as crops.

### 9. Viewer

- **Goods map mode**: pick a good; settlements are coloured by how much they hold, or by its price there, and the routes that carry that good are drawn over them (1.3).
- **Trade map mode**: trade routes drawn as lines, thicker for more goods carried.
- A **herding map mode** showing which animals are kept where.
- The tooltip and the pinned cell panel list a settlement's stockpile and prices, and each group's needs met.
- The top bar adds world food in store.

### 10. Headless runner

- The statistics file adds, for each good: produced, consumed, spoiled, lost, in store and in transit, and the total traded that year. It also adds herders and where each animal was first herded.

## Not in this milestone

- Money, coins and markets as places (later).
- Metals and metalworking (milestone 3, technology).
- Sea and coastal trade, boats (milestone 3, with seafaring).
- Taxes, tribute and trade controlled by states (milestone 4).
- Merchants as named people (milestone 6).
- Horses, riding and wheeled transport (milestone 3).
- Disease.
- Charts in the viewer.

## Exit tests

Automated, on the Earth map with real homelands, across 10 seeds unless stated:

Goods
- [x] For every good, every year, produced equals consumed plus spoiled plus lost plus in store plus in transit, within a rounding error of one part in a million.
- [x] No stockpile ever holds a negative or invalid amount.
- [x] Obsidian is found in stores more than 500 km from its nearest source in at least 8 of 10 seeds by 5,000 years.

Food and famine
- [x] Shortages (a month with food below 85% of needs) hit between 2% and 5% of farming cell-years, about once every 20 to 50 years in a cell.
- [x] Famine deaths per farmer-year in cells that eat from a store are at most half those in cells without one, compared within bands of how full the cells are (1.2).
- [x] Severe famines are rare: in fewer than one in ten cell-years with famine deaths do those deaths pass 15% of the cell's farmers.
- [x] After 5,000 years, world population is between 5 and 40 million in every seed.

Trade
- [x] Goods flow from cheaper to dearer settlements: over a run, the average price gap between linked settlements shrinks for every good that is traded.
- [x] Trade routes exist in every region where farming has spread.

Herding
- [x] Herding is first taken up in at least 2 separate regions within 6,000 years in at least 8 of 10 seeds.
- [x] After 5,000 years, herders hold more of the open grassland that people live on than farmers do.

Soundness (as in milestone 1)
- [x] Population is never negative or invalid; no cell holds more than 8 groups.
- [x] Same seed and settings give the same checksum; save and load mid-run matches an uninterrupted run.
- [x] A 5,000-year headless run takes under 5 minutes.
- [x] Every milestone 1 exit test still passes.

By hand in the viewer:

- [ ] The goods map mode is readable, and you can follow a rare good spreading out from its source over centuries.
- [ ] Trade routes are visible and grow as settlements fill a region.
- [ ] A bad harvest in one area is softened by stores, and you can see grain move in from neighbours.
- [ ] The tooltip shows a settlement's stockpile and prices, and groups' needs met.
- [ ] Herders spread over the steppe and farmers keep to the valleys and forest edges.
- [ ] The frame rate stays smooth at every speed.

## Build order

Each step ends with a new development build you can download.

1. Goods list, ledger and stockpiles; harvests go into stores; food eaten from stores (done)
2. Natural resources and gathering (done)
3. Craftsmen and recipes; everyday and luxury needs (done)
4. Prices (done)
5. Trade links and trade; trade routes in the viewer (done)
6. Herding (done)
7. Goods map mode, tooltip and statistics file (done)
8. Tuning against the exit tests (done), release v0.3 (waits for the owner)

## Famine numbers

Agreed by the owner 2026-11-15, from pre-modern records. There are no counts for the Neolithic itself; these come from medieval and early modern Europe and Japan, at the scale of regions or countries rather than single cells.

- **How often:** Carolingian Europe (750 to 950) had a famine at least once every 20 years and a lesser shortage at least once every 17 years (Newfield). Tokugawa Japan recorded 154 famines in about 265 years, 21 of them widespread and serious. Early farmers were probably no better off: their skeletons show more signs of childhood stress than earlier foragers'. Shortages in 2% to 5% of farming cell-years (once every 20 to 50 years per cell) sit in this range. Milestone 1 already gave about 3%, so stores are not expected to cut how often shortages strike; they are expected to cut deaths.
- **How deadly:** an ordinary European famine killed 2% to 5% of the population. Severe ones killed 15% to 25% (Finland 1696 to 1697, a quarter; Estonia and Latvia, a fifth; Ireland 1740 to 1741, 15% to 20%). The worst, Bengal 1770, killed a third. Before 1800, crises caused less than 5% of all deaths in England (Fogel). The worst famines followed back-to-back bad harvests (Campbell and Ó Gráda).
- **Causes:** before about 1710, famine's deeper cause was population pressing on resources; the trigger was almost always the weather (Alfani and Ó Gráda). The simulation works the same way.

## Numbers that will need tuning

Spoilage rates, recipe amounts, needs per person, the share of time spent gathering, link reach and carrying costs, how fast prices react, and herding densities. They start as estimates in data files and are tuned in the last step.
