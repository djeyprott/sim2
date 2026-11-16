using Sim.Core.Content;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Xunit;

namespace Sim.Core.Tests;

public class GoodsTests
{
    [Fact]
    public void The_goods_file_loads_with_tiers_and_monthly_spoilage()
    {
        GoodSet goods = GoodSet.LoadDefault();
        Assert.InRange(goods.Count, 10, 30);
        Good grain = goods.Get(goods.IndexOf(GoodSet.Grain));
        Assert.True(grain.Food);
        Assert.Equal(NeedTier.Life, grain.NeedTier);
        Assert.Equal(grain.Spoilage, 1.0 - Math.Pow(1.0 - grain.MonthlySpoilage, 12.0), 9);
        Assert.Equal(NeedTier.Luxury, goods.Get(goods.IndexOf("Obsidian")).NeedTier);
        Assert.Equal("wild_food", goods.Get(goods.IndexOf(GoodSet.WildFood)).Slug);
    }

    [Fact]
    public void Bad_goods_files_are_rejected()
    {
        string text = ContentFiles.ReadText("goods.json");
        Assert.Throws<InvalidDataException>(() => GoodSet.Parse(text.Replace("\"tier\": \"life\"", "\"tier\": \"food\"")));
        Assert.Throws<InvalidDataException>(() => GoodSet.Parse(text.Replace("\"name\": \"Grain\"", "\"name\": \"Barley\"")));
        Assert.Throws<InvalidDataException>(() => GoodSet.Parse(text.Replace("\"spoilage\": 0.10", "\"spoilage\": 1.5")));
    }
}

public class ResourceTests : IClassFixture<EarthWorldFixture>
{
    private readonly Sim.Core.Map.WorldMap _world;

    public ResourceTests(EarthWorldFixture fixture)
    {
        _world = fixture.World;
    }

    [Fact]
    public void Resources_are_where_the_rules_put_them()
    {
        var sim = new Sim.Core.Engine.Simulation(_world, new Sim.Core.Engine.RunSettings { Seed = 1 }, Sim.Core.Engine.SystemCatalog.CreateDefault());
        ResourceSystem resources = sim.Systems.OfType<ResourceSystem>().Single();
        int Rule(string good) => resources.Rules.Goods.FindIndex(r => r.Good == good);
        int Cell(double lat, double lon) => _world.Grid.FindCell(lat, lon);

        Assert.Equal(1.0, resources.Richness(Rule("Obsidian"), Cell(38.25, 34.55)));
        Assert.Equal(0.0, resources.Richness(Rule("Obsidian"), Cell(48.85, 2.35)));
        Assert.Equal(1.0, resources.Richness(Rule("Salt"), Cell(47.56, 13.65)));
        Assert.Equal(0.0, resources.Richness(Rule("Salt"), Cell(50.0, 10.0)));
        Assert.True(resources.Richness(Rule("Flint"), Cell(46.5, 9.0)) >= 1.0, "The Alps should have flint.");
        Assert.True(resources.Richness(Rule("Wood"), Cell(-3.0, -60.0)) >= 1.0, "The Amazon should have wood.");
        Assert.True(resources.Richness(Rule("Wood"), Cell(23.0, 10.0)) < 0.1, "The Sahara should have almost no wood.");

        int obsidianCells = Enumerable.Range(0, _world.CellCount).Count(c => resources.Richness(Rule("Obsidian"), c) > 0.0);
        Assert.InRange(obsidianCells, 18, 400);
    }

    [Fact]
    public void Bad_resource_files_are_rejected()
    {
        string text = ContentFiles.ReadText("resources.json");
        Assert.Throws<InvalidDataException>(() => ResourceRules.Parse(text.Replace("\"hills\": 1.0, \"mountains\": 1.0", "\"hills\": 1.0, \"plateau\": 1.0")));
        Assert.Throws<InvalidDataException>(() => ResourceRules.Parse(text.Replace("\"river\": 1.0", "\"river\": 2.0")));
    }
}

public class PriceTests
{
    [Fact]
    public void Prices_rise_with_scarcity_and_stay_within_bounds()
    {
        PriceParameters p = PriceParameters.LoadDefault();
        Assert.Equal(2.0, Prices.PriceOf(2.0, 100.0, 100.0, p), 9);
        Assert.True(Prices.PriceOf(1.0, 100.0, 50.0, p) > Prices.PriceOf(1.0, 100.0, 100.0, p));
        Assert.True(Prices.PriceOf(1.0, 100.0, 200.0, p) < 1.0);
        Assert.Equal(p.MaxFactor, Prices.PriceOf(1.0, 100.0, 0.0, p), 9);
        Assert.Equal(p.MinFactor, Prices.PriceOf(1.0, 1.0, 1e9, p), 9);
        Assert.Equal(p.MinFactor, Prices.PriceOf(1.0, 0.0, 10.0, p), 9);
    }
}

public class CraftRuleTests
{
    [Fact]
    public void Recipes_and_needs_load()
    {
        GoodSet goods = GoodSet.LoadDefault();
        CraftRules rules = CraftRules.LoadDefault(goods);
        Assert.Contains(goods.IndexOf("Pottery"), rules.Outputs);
        Assert.Equal(2, rules.Recipes.Count(r => r.Output == goods.IndexOf("Cloth")));
        Assert.True(rules.NeedPerPerson[goods.IndexOf("Stone tools")] > 0.0);
        Assert.Equal(0.0, rules.NeedPerPerson[goods.IndexOf("Grain")]);
    }

    [Fact]
    public void A_need_for_a_material_is_rejected()
    {
        GoodSet goods = GoodSet.LoadDefault();
        string needs = ContentFiles.ReadText("needs.json").Replace("\"Pottery\": 0.1", "\"Clay\": 0.1");
        Assert.Throws<InvalidDataException>(() => CraftRules.Parse(ContentFiles.ReadText("recipes.json"), needs, goods));
    }

    [Fact]
    public void Metal_tools_stand_in_for_stone_and_metal_recipes_need_known_technologies()
    {
        GoodSet goods = GoodSet.LoadDefault();
        CraftRules rules = CraftRules.LoadDefault(goods);
        int stone = goods.IndexOf("Stone tools");
        Assert.Equal(new[] { goods.IndexOf("Bronze tools"), goods.IndexOf("Copper tools"), stone }, rules.NeedGoods[stone]);
        Assert.Equal(3.0, rules.Counts(goods.IndexOf("Copper tools")));
        Assert.Equal(1.0, rules.Counts(stone));

        // Every recipe's technology exists, and metal recipes need one.
        var technologies = Sim.Core.Technologies.TechnologySet.LoadDefault(Sim.Core.People.OccupationSet.LoadDefault());
        Assert.All(rules.Recipes.Where(r => r.Requires.Length > 0), r => technologies.IndexOf(r.Requires));
        Assert.Equal("Bronze", rules.Recipes.Single(r => r.Output == goods.IndexOf("Bronze")).Requires);

        // A substitute that is itself a need, or one for a material, is rejected; so is an input listed after its output.
        string needs = ContentFiles.ReadText("needs.json");
        string recipes = ContentFiles.ReadText("recipes.json");
        Assert.Throws<InvalidDataException>(() => CraftRules.Parse(recipes, needs.Replace("\"Copper tools\": { \"for\": \"Stone tools\"", "\"Pottery\": { \"for\": \"Stone tools\""), goods));
        Assert.Throws<InvalidDataException>(() => CraftRules.Parse(recipes, needs.Replace("\"for\": \"Stone tools\", \"counts\": 3.0", "\"for\": \"Flint\", \"counts\": 3.0"), goods));
        Assert.Throws<InvalidDataException>(() => CraftRules.Parse(recipes.Replace("\"inputs\": { \"Copper\": 0.5 }", "\"inputs\": { \"Bronze tools\": 0.5 }"), needs, goods));
    }
}

public class TradeRuleTests
{
    [Fact]
    public void Trade_settings_load_and_bad_ones_are_rejected()
    {
        TradeParameters p = TradeParameters.LoadDefault();
        Assert.True(p.MaxLinkCost > 0.0 && p.LinksPerSettlement > 0);
        string text = ContentFiles.ReadText("trade.json");
        Assert.Throws<InvalidDataException>(() => TradeParameters.Parse(text.Replace("\"sendShare\": 0.15", "\"sendShare\": 1.5")));
        Assert.Throws<InvalidDataException>(() => TradeParameters.Parse(text.Replace("\"hills\": 1.6, ", "")));
    }
}

public class LivestockTests
{
    [Fact]
    public void Livestock_loads_with_every_homeland_numbered_and_bad_files_are_rejected()
    {
        LivestockSet livestock = LivestockSet.LoadDefault();
        Assert.Contains(livestock.Animals, a => a.Name == "Sheep and goats" && a.Herded && a.Wool);
        Assert.Contains(livestock.Animals, a => a.Name == "Pigs" && !a.Herded);
        Assert.Equal(2, livestock.Get(livestock.IndexOf("Cattle")).HomelandCount);
        for (int i = 0; i < livestock.Homelands.Count; i++)
        {
            Assert.Equal(i, livestock.Homelands[i].Index);
            Animal animal = livestock.Get(livestock.Homelands[i].Animal);
            Assert.InRange(i, animal.FirstHomeland, animal.FirstHomeland + animal.HomelandCount - 1);
        }

        string text = ContentFiles.ReadText("livestock.json");
        Assert.Throws<InvalidDataException>(() => LivestockSet.Parse(text.Replace("\"warmest\": [10, 15, 34, 42]", "\"warmest\": [15, 10, 34, 42]")));
        Assert.Throws<InvalidDataException>(() => LivestockSet.Parse(text.Replace("\"homelands\": [ { \"lat\": 36.0, \"lon\": 45.0, \"radiusKm\": 600 } ]", "\"homelands\": [ ]")));

        HerdingParameters p = HerdingParameters.LoadDefault();
        Assert.True(p.HerdDensity > 0.0 && p.Pasture["Grassland"] >= p.MinPasture);
        string herding = ContentFiles.ReadText("herding.json");
        Assert.Throws<InvalidDataException>(() => HerdingParameters.Parse(herding.Replace("\"deathShare\": 0.2", "\"deathShare\": 0.9")));
        Assert.Throws<InvalidDataException>(() => HerdingParameters.Parse(herding.Replace("\"Grassland\": 1.0", "\"Grassland\": 1.5")));
    }
}

public class TechnologyFileTests
{
    [Fact]
    public void Technologies_load_and_bad_files_are_rejected()
    {
        var occupations = Sim.Core.People.OccupationSet.LoadDefault();
        var set = Sim.Core.Technologies.TechnologySet.LoadDefault(occupations);
        var wheel = set.Get(set.IndexOf("The wheel"));
        Assert.Contains(set.IndexOf("The plough"), wheel.Requires);
        Assert.False(set.Get(set.IndexOf("Granaries")).CanUse[occupations.IndexOf("Forager")]);

        string text = ContentFiles.ReadText("technologies.json");
        // A requirement listed after the technology that needs it.
        Assert.Throws<InvalidDataException>(() => Sim.Core.Technologies.TechnologySet.Parse(
            text.Replace("\"requires\": [ \"The sail\" ]", "\"requires\": [ \"Terraced fields\" ]"), occupations));
        Assert.Throws<InvalidDataException>(() => Sim.Core.Technologies.TechnologySet.Parse(
            text.Replace("\"terrain\": [ \"hills\", \"mountains\" ]", "\"terrain\": [ \"ocean\" ]"), occupations));
        Assert.Throws<KeyNotFoundException>(() => Sim.Core.Technologies.TechnologySet.Parse(
            text.Replace("\"occupations\": [ \"Farmer\" ],\n      \"chance\": 0.0002", "\"occupations\": [ \"Smith\" ],\n      \"chance\": 0.0002"), occupations));

        // Effects: known names only, and a lifted penalty share of at most 1.
        Assert.Equal(1.25, set.Get(set.IndexOf("The plough")).Effects["farmYield"]);
        Assert.Equal(0.5, set.Get(set.IndexOf("Granaries")).Effects["grainSpoilage"]);
        Assert.Throws<InvalidDataException>(() => Sim.Core.Technologies.TechnologySet.Parse(
            text.Replace("\"grainSpoilage\": 0.5", "\"grainSpoilige\": 0.5"), occupations));
        Assert.Throws<InvalidDataException>(() => Sim.Core.Technologies.TechnologySet.Parse(
            text.Replace("\"biomePenaltyLift\": 1.0", "\"biomePenaltyLift\": 1.5"), occupations));

        var p = Sim.Core.Technologies.TechnologyParameters.LoadDefault();
        Assert.InRange(p.InUse, 0.01, 1.0);
        Assert.Throws<InvalidDataException>(() => Sim.Core.Technologies.TechnologyParameters.Parse(
            ContentFiles.ReadText("technology.json").Replace("\"lossRate\": 0.03", "\"lossRate\": 3")));
    }
}
