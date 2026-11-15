using Sim.Core.Content;
using Sim.Core.Goods;
using Xunit;

namespace Sim.Core.Tests;

public class GoodsTests
{
    [Fact]
    public void The_goods_file_loads_with_tiers_and_monthly_spoilage()
    {
        GoodSet goods = GoodSet.LoadDefault();
        Assert.InRange(goods.Count, 10, 20);
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
}
