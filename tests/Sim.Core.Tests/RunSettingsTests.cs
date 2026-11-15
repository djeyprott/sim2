using Sim.Core.Engine;
using Xunit;

namespace Sim.Core.Tests;

public class RunSettingsTests
{
    /// <summary>Test-only system that records the settings it saw while initializing.</summary>
    private sealed class SettingsReader : ISimSystem
    {
        private WorldState _state = null!;

        public RunSettings? Seen { get; private set; }

        public string Name => "reader";

        public int IntervalMonths => 12;

        public void Register(WorldState state) => _state = state;

        public void Initialize(SimContext context) => Seen = RunSettings.ReadFrom(_state);

        public void Update(SimContext context)
        {
        }
    }

    [Fact]
    public void Settings_are_stored_and_visible_to_systems_while_initializing()
    {
        var reader = new SettingsReader();
        var settings = new RunSettings { Seed = 98765, CropHomelands = CropHomelands.Random };
        var sim = new Simulation(TestWorlds.Small(), settings, new ISimSystem[] { reader });

        Assert.Equal(98765UL, sim.Seed);
        Assert.Equal(98765UL, sim.Settings.Seed);
        Assert.Equal(CropHomelands.Random, sim.Settings.CropHomelands);
        Assert.Equal(CropHomelands.Random, reader.Seen!.CropHomelands);
        Assert.Equal("98765", sim.State.GetSetting(RunSettings.SeedKey));
        Assert.Equal("random", sim.State.GetSetting(RunSettings.CropHomelandsKey));
        Assert.Equal("Seed 98765, random crop homelands", sim.Settings.Describe());
    }

    [Fact]
    public void The_seed_only_constructor_uses_real_homelands()
    {
        var sim = new Simulation(TestWorlds.Small(), 5, new ISimSystem[] { new SettingsReader() });
        Assert.Equal(5UL, sim.Settings.Seed);
        Assert.Equal(CropHomelands.Real, sim.Settings.CropHomelands);
    }

    [Fact]
    public void Homelands_change_the_checksum()
    {
        var real = new Simulation(TestWorlds.Small(), new RunSettings { Seed = 1 }, new ISimSystem[] { new SettingsReader() });
        var random = new Simulation(TestWorlds.Small(), new RunSettings { Seed = 1, CropHomelands = CropHomelands.Random }, new ISimSystem[] { new SettingsReader() });
        Assert.NotEqual(real.StateChecksum(), random.StateChecksum());
    }

    [Fact]
    public void A_loaded_save_brings_its_settings()
    {
        var source = new Simulation(
            TestWorlds.Small(),
            new RunSettings { Seed = 31, CropHomelands = CropHomelands.Random },
            new ISimSystem[] { new SettingsReader(), new RandomWriteSystem("monthly", 1) });
        source.Run(25);
        using var save = new MemoryStream();
        source.Save(save);

        var target = new Simulation(TestWorlds.Small(), 7, new ISimSystem[] { new SettingsReader(), new RandomWriteSystem("monthly", 1) });
        target.Load(new MemoryStream(save.ToArray()));
        Assert.Equal(31UL, target.Settings.Seed);
        Assert.Equal(CropHomelands.Random, target.Settings.CropHomelands);
    }

    [Theory]
    [InlineData("real", CropHomelands.Real)]
    [InlineData("Random", CropHomelands.Random)]
    [InlineData(" RANDOM ", CropHomelands.Random)]
    public void Homelands_text_is_read_in_any_case(string text, CropHomelands expected)
    {
        Assert.True(RunSettings.TryParseHomelands(text, out CropHomelands homelands));
        Assert.Equal(expected, homelands);
        Assert.False(RunSettings.TryParseHomelands("sometimes", out _));
    }
}
