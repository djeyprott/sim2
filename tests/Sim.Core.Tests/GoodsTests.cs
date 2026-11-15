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
