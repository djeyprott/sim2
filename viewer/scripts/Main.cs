using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Geo;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.Hosting;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Polities;
using Sim.Core.Sea;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

namespace Viewer;

/// <summary>
/// Entry point of the viewer. Loads the world, starts the simulation on its own thread,
/// and connects the map, the camera and the controls. It only displays: all simulation
/// logic lives in Sim.Core.
/// </summary>
public partial class Main : Node2D
{
    private const float MaxZoom = 40f;
    private const double SettlementRefreshSeconds = 1.0;
    private const string GrainStockField = Stockpiles.StockFieldPrefix + "grain";
    private static readonly GoodSet ViewerGoods = GoodSet.LoadDefault();
    private static readonly TechnologySet ViewerTechnologies = TechnologySet.LoadDefault(OccupationSet.LoadDefault());
    private static readonly CraftRules ViewerCraftRules = CraftRules.LoadDefault(ViewerGoods);
    private static readonly PriceParameters ViewerPriceParameters = PriceParameters.LoadDefault();
    private const float KeyPanPixelsPerSecond = 900f;
    private const float ClickSlopPixels = 5f;

    private const int TerrainMode = 0;
    private const int ElevationMode = 1;
    private const int BiomeMode = 2;
    private const int TemperatureMode = 3;
    private const int RainfallMode = 4;
    private const int TemperatureChangeMode = 5;
    private const int RainfallChangeMode = 6;
    private const int PopulationMode = 7;
    private const int FarmingMode = 8;
    private const int TradeMode = 9;
    private const int HerdingMode = 10;
    private const int GoodsMode = 11;
    private const int TechnologyMode = 12;
    private const int PoliticalMode = 13;
    private int _techIndex;
    private TechnologySystem? _technology;
    private Color[] _techColours = Array.Empty<Color>();

    // The goods map mode's choices: which good, and whether to show the amount held or the price.
    private int _goodIndex = ViewerGoods.IndexOf(GoodSet.Grain);
    // Goods map: 0 amount held, 1 price, 2 made by craftsmen at their last January's work.
    private int _goodMeasure;
    private const int GoodPrice = 1;
    private const int GoodMade = 2;

    private const double RecolourSeconds = 0.1;
    private const double CellRefreshSeconds = 0.25;

    private static readonly double[] Speeds = { 0.0, 1.0, 12.0, 120.0, SimulationHost.MaxSpeed };

    private static readonly Color[] TerrainColours =
    {
        Color.Color8(24, 52, 110),   // Ocean
        Color.Color8(70, 120, 190),  // Coast
        Color.Color8(96, 150, 80),   // Lowland
        Color.Color8(176, 160, 100), // Hills
        Color.Color8(120, 96, 84),   // Mountains
        Color.Color8(90, 170, 220),  // Lake
    };

    private static readonly Color SeaNeutral = Color.Color8(30, 40, 62);
    private static readonly Color LakeNeutral = Color.Color8(52, 66, 96);

    private WorldMap? _map;
    private SimulationHost? _host;

    // The climate system belongs to the simulation thread. The viewer only uses its biome list
    // directly; anything else is read through the host.
    private ClimateSystem? _climate;
    private ForagerSystem? _foragers;
    private FarmingSystem? _farming;
    private HerdingSystem? _herding;
    private PolitySystem? _polities;
    private Color[] _animalColours = Array.Empty<Color>();
    private SettlementSystem? _settlementSystem;
    private ResourceSystem? _resourceSystem;
    private Task<(List<SettlementInfo> Settlements, List<TradeRoute> Routes)>? _settlementTask;
    private TradeSystem? _tradeSystem;

    // The good whose routes were last asked for (-1: all goods).
    private int _routeGood = -1;
    private double _settlementAge = double.MaxValue;
    private long _settlementTick = -1;
    private Color[] _cropColours = Array.Empty<Color>();
    private readonly OccupationSet _occupations = OccupationSet.LoadDefault();
    private double[] _areaKm2 = Array.Empty<double>();
    private Color[] _biomeColours = Array.Empty<Color>();
    private double[] _baselineMean = Array.Empty<double>();
    private double[] _baselineRain = Array.Empty<double>();
    private int _mode;
    private SimFrame? _colouredFrame;
    private double _sinceRecolour;
    private LiveCell _hover = new LiveCell();
    private LiveCell _selected = new LiveCell();
    private StartScreen _startScreen = null!;
    private string _runDescription = "";
    private MapView _mapView = null!;
    private Hud _hud = null!;
    private Camera2D _camera = null!;
    private FileDialog _saveDialog = null!;
    private FileDialog _loadDialog = null!;

    private float _zoom = 1f;
    private bool _screenshotOk;
    private int _frameWaits;
    private float _minZoom = 0.1f;
    private bool _dragging;
    private Vector2 _pressPosition;
    private bool _movedSincePress;
    private int _selectedCell = -1;
    private int _speedIndex;
    private int _speedBeforePause = 2;

    private Task<string?>? _pendingTask;
    private string _pendingDoneMessage = "";
    private bool _pendingIsLoad;
    private string _message = "";
    private double _messageSecondsLeft;

    private string? _screenshotPath;
    private int _frame;

    public override void _Ready()
    {
        _hud = new Hud();
        AddChild(_hud);

        _mapView = new MapView();
        AddChild(_mapView);

        _camera = new Camera2D();
        AddChild(_camera);
        _camera.MakeCurrent();

        string? worldPath = FindWorldFile();
        if (worldPath == null)
        {
            _hud.SetStatus("Could not find earth.world next to the program.");
            GD.PrintErr("earth.world not found.");
            return;
        }

        try
        {
            _map = WorldMap.Load(worldPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _hud.SetStatus("Could not read earth.world: " + error.Message);
            GD.PrintErr("Failed to load world: " + error.Message);
            return;
        }

        GD.Print($"Loaded world '{_map.Name}' with {_map.CellCount} cells from {worldPath}");

        string? worldFolder = Path.GetDirectoryName(worldPath);
        _mapView.Build(_map, worldFolder == null ? null : Path.Combine(worldFolder, "earth-rivers.bin"));

        _areaKm2 = new double[_map.CellCount];
        for (int cell = 0; cell < _map.CellCount; cell++)
        {
            _areaKm2[cell] = _map.Grid.AreaKm2(cell);
        }

        _baselineMean = new double[_map.CellCount];
        _baselineRain = new double[_map.CellCount];
        for (int cell = 0; cell < _map.CellCount; cell++)
        {
            if (_map.HasClimate(cell))
            {
                _baselineMean[cell] = _map.MeanTemperatureC(cell);
                _baselineRain[cell] = _map.AnnualRainfallMm(cell);
            }
        }

        ApplyMapMode(TerrainMode);
        _hud.SpeedChosen += SetSpeed;
        _hud.MapModeChosen += ApplyMapMode;
        _hud.GoodChosen += index =>
        {
            if (_mode == GoodsMode)
            {
                _goodIndex = index;
                ApplyMapMode(GoodsMode);
            }
            else if (_mode == TechnologyMode)
            {
                _techIndex = index;
                ApplyMapMode(TechnologyMode);
            }
        };
        _hud.GoodMeasureChosen += measure =>
        {
            _goodMeasure = measure;
            if (_mode == GoodsMode)
            {
                ApplyMapMode(GoodsMode);
            }
        };
        _hud.SaveRequested += () => _saveDialog.PopupCentered(new Vector2I(900, 600));
        _hud.LoadRequested += () => _loadDialog.PopupCentered(new Vector2I(900, 600));

        _saveDialog = NewFileDialog(FileDialog.FileModeEnum.SaveFile, "Save the simulation");
        _saveDialog.FileSelected += OnSavePathChosen;
        _loadDialog = NewFileDialog(FileDialog.FileModeEnum.OpenFile, "Load a saved simulation");
        _loadDialog.FileSelected += OnLoadPathChosen;

        _startScreen = new StartScreen();
        AddChild(_startScreen);
        _startScreen.StartRequested += StartRun;
        _startScreen.LoadRequested += () => _loadDialog.PopupCentered(new Vector2I(900, 600));
        _startScreen.CancelRequested += () => _startScreen.Close();
        _hud.NewRunRequested += OpenStartScreen;

        FitWholeMap();
        GetViewport().SizeChanged += OnViewportResized;

        if (OS.HasEnvironment("SIM2_SCREENSHOT"))
        {
            _screenshotPath = OS.GetEnvironment("SIM2_SCREENSHOT");
        }

        _startScreen.Open(canCancel: false);
    }

    private void OpenStartScreen()
    {
        SetSpeed(0);
        _startScreen.Open(canCancel: _host != null);
    }

    private void StartRun(RunSettings settings)
    {
        InstallSimulation(settings);
        _startScreen.Close();
        ShowMessage("Started a new run", 4.0);
    }

    // Replaces the running simulation, if any, with a new one built from the settings.
    private void InstallSimulation(RunSettings settings)
    {
        IReadOnlyList<ISimSystem> systems = SystemCatalog.CreateDefault();
        var simulation = new Simulation(_map!, settings, systems);
        _runDescription = simulation.Settings.Describe();

        _host?.Dispose();
        _climate = systems.OfType<ClimateSystem>().FirstOrDefault();
        _foragers = systems.OfType<ForagerSystem>().FirstOrDefault();
        _farming = systems.OfType<FarmingSystem>().FirstOrDefault();
        _herding = systems.OfType<HerdingSystem>().FirstOrDefault();
        _technology = systems.OfType<TechnologySystem>().FirstOrDefault();
        _polities = systems.OfType<PolitySystem>().FirstOrDefault();
        _techColours = _technology == null
            ? Array.Empty<Color>()
            : _technology.Technologies.Technologies.Select(t => Color.FromHtml(t.Colour)).ToArray();
        _animalColours = _herding == null
            ? Array.Empty<Color>()
            : _herding.Livestock.Animals.Select(a => Color.FromHtml(a.Colour)).ToArray();
        _settlementSystem = systems.OfType<SettlementSystem>().FirstOrDefault();
        _resourceSystem = systems.OfType<ResourceSystem>().FirstOrDefault();
        _tradeSystem = systems.OfType<TradeSystem>().FirstOrDefault();
        _settlementTask = null;
        _settlementAge = double.MaxValue;
        _settlementTick = -1;
        _mapView.SetSettlements(Array.Empty<Vector2>(), Array.Empty<string>(), Array.Empty<float>());
        _cropColours = _farming == null
            ? Array.Empty<Color>()
            : _farming.Crops.Crops.Select(c => Color.FromHtml(c.Colour)).ToArray();
        _biomeColours = _climate == null
            ? Array.Empty<Color>()
            : _climate.Biomes.Biomes.Select(b => Color.FromHtml(b.Colour)).ToArray();

        _host = new SimulationHost(simulation);
        ClimateSystem? climate = _climate;
        ForagerSystem? foragers = _foragers;
        FarmingSystem? farmingSystem = _farming;
        _host.SetSummaryExtractor(sim => new[]
        {
            climate?.WorldTemperatureShift() ?? double.NaN,
            foragers != null ? new PopulationGroups(sim.State).WorldTotal() : double.NaN,
            farmingSystem?.WorldFarmers() ?? double.NaN,
            sim.State.HasField(GrainStockField) ? sim.State.DoubleField(GrainStockField).Sum() : double.NaN,
        });

        _hover = new LiveCell();
        _selected = new LiveCell();
        _speedIndex = 0;
        _hud.ShowSpeed(0);
        _pendingTask = null;
        _hud.SetBusy(false);
        ApplyMapMode(_mode);
    }

    public override void _ExitTree()
    {
        _host?.Dispose();
        _host = null;
    }

    public override void _Process(double delta)
    {
        if (_map == null)
        {
            return;
        }

        if (_host == null)
        {
            if (_screenshotPath != null)
            {
                RunScreenshotScript(null);
            }

            return;
        }

        SimFrame frame = _host.LatestFrame;
        _hud.SetDate(frame.Date.ToString());
        string worldInfo = "";
        if (frame.Summary is { Length: >= 2 } summary)
        {
            if (!double.IsNaN(summary[0]))
            {
                worldInfo = "Δ " + summary[0].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " °C";
            }

            if (!double.IsNaN(summary[1]))
            {
                worldInfo += "   " + (summary[1] / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " M people";
            }

            if (summary.Length >= 3 && !double.IsNaN(summary[2]))
            {
                worldInfo += ", " + (summary[2] / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " M farmers";
            }

            if (summary.Length >= 4 && !double.IsNaN(summary[3]))
            {
                worldInfo += ", " + (summary[3] / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " M food";
            }
        }

        _hud.SetWorldInfo(worldInfo);

        if (_host.Fault != null && _speedIndex != 0)
        {
            _speedIndex = 0;
            _hud.ShowSpeed(0);
            ShowMessage("The simulation stopped with an error: " + _host.Fault.Message, 60.0);
        }

        PollPendingTask();
        PanWithKeys((float)delta);
        RecolourIfNeeded(frame, delta);
        UpdateCellDetails(delta);

        float halfViewWidth = GetViewportRect().Size.X / (2f * _zoom);
        _mapView.ShowCopiesInView(_camera.Position.X - halfViewWidth, _camera.Position.X + halfViewWidth);
        Vector2 viewSize = GetViewportRect().Size / _zoom;
        _mapView.SetSettlementView(_zoom, new Rect2(_camera.Position - viewSize / 2f, viewSize));
        PollSettlements(frame, delta);
        UpdateStatus(frame, delta);

        if (_screenshotPath != null)
        {
            RunScreenshotScript(frame);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_map == null)
        {
            return;
        }

        if (@event is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.WheelUp && button.Pressed)
            {
                ZoomAt(GetGlobalMousePosition(), 1.2f);
            }
            else if (button.ButtonIndex == MouseButton.WheelDown && button.Pressed)
            {
                ZoomAt(GetGlobalMousePosition(), 1f / 1.2f);
            }
            else if (button.ButtonIndex == MouseButton.Left || button.ButtonIndex == MouseButton.Right
                || button.ButtonIndex == MouseButton.Middle)
            {
                if (button.Pressed)
                {
                    _dragging = true;
                    _movedSincePress = false;
                    _pressPosition = button.Position;
                }
                else
                {
                    bool wasClick = _dragging && !_movedSincePress && button.ButtonIndex == MouseButton.Left;
                    _dragging = false;
                    if (wasClick)
                    {
                        SelectCell(_mapView.CellAt(GetGlobalMousePosition()));
                    }
                }
            }
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            if (motion.Position.DistanceTo(_pressPosition) > ClickSlopPixels)
            {
                _movedSincePress = true;
            }

            if (_movedSincePress)
            {
                _camera.Position -= motion.Relative / _zoom;
                ClampCamera();
            }
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.Space)
            {
                SetSpeed(_speedIndex == 0 ? _speedBeforePause : 0);
            }
            else if (key.Keycode == Key.Escape)
            {
                SelectCell(-1);
            }
            else if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key5)
            {
                SetSpeed((int)(key.Keycode - Key.Key1));
            }
        }
    }

    private static string? FindWorldFile()
    {
        if (OS.HasEnvironment("SIM2_WORLD"))
        {
            string fromEnvironment = OS.GetEnvironment("SIM2_WORLD");
            if (File.Exists(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        string? programFolder = Path.GetDirectoryName(OS.GetExecutablePath());
        if (programFolder != null)
        {
            string besideProgram = Path.Combine(programFolder, "earth.world");
            if (File.Exists(besideProgram))
            {
                return besideProgram;
            }
        }

        // Running from the editor: use the copy in the repository.
        string inRepository = Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "data", "earth.world");
        return File.Exists(inRepository) ? Path.GetFullPath(inRepository) : null;
    }

    private FileDialog NewFileDialog(FileDialog.FileModeEnum mode, string title)
    {
        var dialog = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Title = title,
        };
        dialog.AddFilter("*.sav", "sim2 saves");
        string documents = OS.GetSystemDir(OS.SystemDir.Documents);
        if (!string.IsNullOrEmpty(documents))
        {
            dialog.CurrentDir = documents;
        }

        AddChild(dialog);
        return dialog;
    }

    private void SetSpeed(int index)
    {
        if (_host == null || index < 0 || index >= Speeds.Length)
        {
            return;
        }

        if (_host.Fault != null && index != 0)
        {
            index = 0;
        }

        if (index != 0)
        {
            _speedBeforePause = index;
        }

        _speedIndex = index;
        _host.TicksPerSecond = Speeds[index];
        _hud.ShowSpeed(index);
    }

    private void ApplyMapMode(int mode)
    {
        if (_map == null)
        {
            return;
        }

        _mode = mode;
        _mapView.SetRoutesVisible(mode == TradeMode || mode == GoodsMode, mode == GoodsMode ? GoodRouteColour : TradeRouteColour);
        if (mode == GoodsMode)
        {
            _hud.ShowPicker(true, "Good:", ViewerGoods.Goods.Select(g => g.Name).ToList(), _goodIndex, true, _goodMeasure);
        }
        else if (mode == TechnologyMode && _technology != null)
        {
            _hud.ShowPicker(true, "Technology:", _technology.Technologies.Technologies.Select(t => t.Name).ToList(), _techIndex);
        }
        else
        {
            _hud.ShowPicker(false);
        }
        _colouredFrame = null;
        _sinceRecolour = RecolourSeconds;

        Func<Simulation, double[]?>? extractor = null;
        if (_climate != null)
        {
            extractor = mode switch
            {
                BiomeMode => ExtractBiomes,
                TemperatureMode or TemperatureChangeMode => sim => (double[])sim.State.DoubleField(ClimateSystem.TemperatureShiftField).Clone(),
                RainfallMode or RainfallChangeMode => sim => (double[])sim.State.DoubleField(ClimateSystem.RainfallFactorField).Clone(),
                _ => null,
            };
        }

        if (mode == FarmingMode && _farming != null)
        {
            int cellCount = _map.CellCount;
            extractor = sim => ExtractFarming(sim, cellCount);
        }

        if (mode == GoodsMode && _settlementSystem != null)
        {
            int cellCount = _map.CellCount;
            int good = _goodIndex;
            int measure = _goodMeasure;
            extractor = sim => ExtractGoods(sim, good, measure, cellCount);
        }

        if (mode == TechnologyMode && _technology != null)
        {
            int cellCount = _map.CellCount;
            int tech = _techIndex;
            string column = PopulationGroups.KnowledgeColumnPrefix + _technology.Technologies.Get(tech).Slug;
            TechnologySystem technology = _technology;
            extractor = sim => ExtractTechnology(sim, technology, tech, column, cellCount);
        }

        if (mode == PoliticalMode && _polities != null)
        {
            int cellCount = _map.CellCount;
            PolitySystem polities = _polities;
            extractor = sim => ExtractPolitical(sim, polities, cellCount);
        }

        if (mode == HerdingMode && _herding != null)
        {
            int cellCount = _map.CellCount;
            HerdingSystem herding = _herding;
            extractor = sim => ExtractHerding(sim, herding, cellCount);
        }

        if ((mode == PopulationMode || mode == TradeMode) && _foragers != null)
        {
            double[] area = _areaKm2;
            extractor = sim =>
            {
                var people = new double[area.Length];
                new PopulationGroups(sim.State).FillCellTotals(people);
                var perHundredKm2 = new double[people.Length];
                for (int cell = 0; cell < people.Length; cell++)
                {
                    perHundredKm2[cell] = people[cell] / area[cell] * 100.0;
                }

                return perHundredKm2;
            };
        }

        _host?.SetValueExtractor(extractor, mode);

        WorldMap map = _map;
        if (extractor == null)
        {
            // Modes that never change during a run are coloured once.
            switch (mode)
            {
                case ElevationMode:
                    _mapView.SetColours(cell => map.IsLake(cell)
                        ? TerrainColours[(int)TerrainClass.Lake]
                        : ElevationColour(map.IsLand(cell), map.ElevationMetres(cell)));
                    break;
                case TemperatureMode:
                    _mapView.SetColours(cell => map.IsLand(cell) ? TemperatureColour(_baselineMean[cell]) : Neutral(cell));
                    break;
                case RainfallMode:
                    _mapView.SetColours(cell => map.IsLand(cell) ? RainfallColour(_baselineRain[cell]) : Neutral(cell));
                    break;
                default:
                    _mapView.SetColours(cell => TerrainColours[(int)map.Terrain(cell)]);
                    break;
            }
        }

        ShowLegend(mode);
    }

    // Per cell: a farmed crop's index plus the farmers' share of the cell's people (0 to 0.998);
    // or, where nothing is farmed, minus one minus the index of a crop that grows wild there;
    // or -100 for neither.
    private static double[] ExtractFarming(Simulation sim, int cellCount)
    {
        int[] crop = sim.State.IntField(FarmingSystem.CropField);
        int[] wild = sim.State.IntField(FarmingSystem.WildCropsField);
        var groups = new PopulationGroups(sim.State);
        var totals = new double[cellCount];
        groups.FillCellTotals(totals);
        var farmers = new double[cellCount];
        groups.FillCellTotals(farmers, FarmerOccupation);
        var values = new double[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            if (crop[cell] > 0)
            {
                double share = totals[cell] > 0.0 ? farmers[cell] / totals[cell] : 0.0;
                values[cell] = crop[cell] - 1 + 0.001 + 0.998 * Math.Clamp(share, 0.0, 1.0);
            }
            else if (wild[cell] != 0)
            {
                values[cell] = -1 - System.Numerics.BitOperations.TrailingZeroCount(wild[cell]);
            }
            else
            {
                values[cell] = -100.0;
            }
        }

        return values;
    }

    // Per settlement cell: how much of the good it holds (with what is on its way) per person, its
    // price as a multiple of the good's base value, or how much its craftsmen made at their last
    // January's work per person; -1 for cells without a settlement.
    private static double[] ExtractGoods(Simulation sim, int good, int measure, int cellCount)
    {
        var values = new double[cellCount];
        Array.Fill(values, -1.0);
        EntityTable settlements = sim.State.Table(SettlementSystem.TableName);
        IntColumn settlementCell = settlements.IntColumn("cell");
        var stockpiles = new Stockpiles(sim.State, ViewerGoods);
        var groups = new PopulationGroups(sim.State);
        string madeField = CraftSystem.MadeFieldPrefix + ViewerGoods.Get(good).Slug;
        double[]? made = measure == GoodMade && sim.State.HasField(madeField) ? sim.State.DoubleField(madeField) : null;
        Prices? prices = measure == GoodPrice ? new Prices(sim.State, sim.Map, ViewerPriceParameters, ViewerCraftRules, OccupationSet.LoadDefault()) : null;
        var wanted = new double[ViewerGoods.Count];
        var prices1 = new double[ViewerGoods.Count];
        double value = ViewerGoods.Get(good).Value;
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (!settlements.IsAlive(id))
            {
                continue;
            }

            int cell = settlementCell[id];
            if (prices != null)
            {
                prices.Work(cell, sim.Tick, wanted, prices1);
                values[cell] = prices1[good] / value;
            }
            else
            {
                double people = groups.CellTotal(cell);
                double amount = measure == GoodMade ? made?[cell] ?? 0.0 : stockpiles.Stock(cell, good) + stockpiles.Incoming(cell, good);
                values[cell] = people > 0.0 ? amount / people : 0.0;
            }
        }

        return values;
    }

    // Per cell: the share of its people who know the technology (0 to 1), -1 where nobody lives,
    // and 2 where it was first discovered or independently discovered again.
    private static double[] ExtractTechnology(Simulation sim, TechnologySystem technology, int tech, string column, int cellCount)
    {
        var values = new double[cellCount];
        var people = new double[cellCount];
        var knowing = new double[cellCount];
        EntityTable table = sim.State.Table(PopulationGroups.TableName);
        IntColumn cellOf = table.IntColumn("cell");
        DoubleColumn size = table.DoubleColumn("size");
        DoubleColumn share = table.DoubleColumn(column);
        for (int g = 0; g < table.IdCount; g++)
        {
            if (table.IsAlive(g))
            {
                people[cellOf[g]] += size[g];
                knowing[cellOf[g]] += size[g] * share[g];
            }
        }

        for (int cell = 0; cell < cellCount; cell++)
        {
            values[cell] = people[cell] > 0.0 ? knowing[cell] / people[cell] : -1.0;
        }

        foreach (TechnologyEvent e in technology.Events())
        {
            if (e.Technology == tech && e.Kind != TechnologyEventKind.Lost)
            {
                values[e.Cell] = 2.0;
            }
        }

        return values;
    }

    // Per cell: -1 where no polity rules; otherwise the ruling polity's id times 8, plus 4 where
    // it borders the polity it is at war with, plus 2 for its capital, plus 1 when it is a state.
    private static double[] ExtractPolitical(Simulation sim, PolitySystem polities, int cellCount)
    {
        var values = new double[cellCount];
        var states = new HashSet<int>();
        var capitals = new HashSet<int>();
        var enemyOf = new Dictionary<int, int>();
        foreach (PolityInfo p in polities.Standing())
        {
            if (p.Level == PolityLevel.State)
            {
                states.Add(p.Id);
            }

            capitals.Add(p.Capital);
            int war = polities.WarOf(p.Id);
            if (war >= 0)
            {
                WarInfo w = polities.War(war);
                enemyOf[p.Id] = w.Attacker == p.Id ? w.Defender : w.Attacker;
            }
        }

        for (int cell = 0; cell < cellCount; cell++)
        {
            int ruler = polities.RulerOf(cell);
            if (ruler < 0)
            {
                values[cell] = -1.0;
                continue;
            }

            bool front = false;
            if (enemyOf.TryGetValue(ruler, out int enemy))
            {
                foreach (int other in sim.Map.Grid.Neighbours(cell))
                {
                    front |= polities.RulerOf(other) == enemy;
                }
            }

            values[cell] = ruler * 8.0 + (front ? 4.0 : 0.0) + (capitals.Contains(cell) ? 2.0 : 0.0) + (states.Contains(ruler) ? 1.0 : 0.0);
        }

        return values;
    }

    // A polity's own colour: hues spread by the golden angle so neighbouring ids differ.
    private static Color PolityColour(int polity, bool state)
    {
        float hue = (float)((polity * 0.618033988749895) % 1.0);
        return Color.FromHsv(hue, state ? 0.75f : 0.4f, state ? 0.85f : 0.75f);
    }

    // Per cell: the main animal's index plus the herders' share of the cell's people (0 to 0.998);
    // or, where animals are kept only by farmers, minus one minus the main animal's index; or -100.
    private static double[] ExtractHerding(Simulation sim, HerdingSystem herding, int cellCount)
    {
        int[] animals = sim.State.IntField(HerdingSystem.AnimalsField);
        var groups = new PopulationGroups(sim.State);
        var totals = new double[cellCount];
        groups.FillCellTotals(totals);
        var herders = new double[cellCount];
        groups.FillCellTotals(herders, HerderOccupation);
        var values = new double[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            int mask = animals[cell];
            if (mask == 0)
            {
                values[cell] = -100.0;
                continue;
            }

            int main = herding.BestHerded(cell, mask).Animal;
            if (main < 0)
            {
                main = System.Numerics.BitOperations.TrailingZeroCount(mask);
            }

            if (herders[cell] > 0.0)
            {
                double share = totals[cell] > 0.0 ? herders[cell] / totals[cell] : 0.0;
                values[cell] = main + 0.001 + 0.998 * Math.Clamp(share, 0.0, 1.0);
            }
            else
            {
                values[cell] = -1 - main;
            }
        }

        return values;
    }

    private static double[] ExtractBiomes(Simulation sim)
    {
        int[] biomes = sim.State.IntField(ClimateSystem.BiomeField);
        var values = new double[biomes.Length];
        for (int cell = 0; cell < biomes.Length; cell++)
        {
            values[cell] = biomes[cell];
        }

        return values;
    }

    // Recolours the map from the latest frame when the current mode changes during a run.
    private void RecolourIfNeeded(SimFrame frame, double delta)
    {
        _sinceRecolour += delta;
        double[]? values = frame.Values;
        if (values == null || frame.ValuesTag != _mode || ReferenceEquals(frame, _colouredFrame) || _sinceRecolour < RecolourSeconds)
        {
            return;
        }

        _colouredFrame = frame;
        _sinceRecolour = 0.0;
        WorldMap map = _map!;
        switch (_mode)
        {
            case BiomeMode:
                _mapView.SetColours(cell =>
                {
                    int biome = (int)values[cell];
                    return biome >= 0 && biome < _biomeColours.Length ? _biomeColours[biome] : TerrainColours[(int)map.Terrain(cell)];
                });
                break;
            case TemperatureMode:
                _mapView.SetColours(cell => map.IsLand(cell) ? TemperatureColour(_baselineMean[cell] + values[cell]) : Neutral(cell));
                break;
            case RainfallMode:
                _mapView.SetColours(cell => map.IsLand(cell) ? RainfallColour(_baselineRain[cell] * values[cell]) : Neutral(cell));
                break;
            case TemperatureChangeMode:
                _mapView.SetColours(cell => map.IsLand(cell) ? Gradient(values[cell], TemperatureChangeStops) : Neutral(cell));
                break;
            case RainfallChangeMode:
                _mapView.SetColours(cell => map.IsLand(cell) ? Gradient(values[cell], RainfallChangeStops) : Neutral(cell));
                break;
            case FarmingMode:
                _mapView.SetColours(cell =>
                {
                    if (!map.IsLand(cell))
                    {
                        return Neutral(cell);
                    }

                    double v = values[cell];
                    if (v >= 0.0)
                    {
                        int crop = (int)v;
                        double share = v - crop;
                        return crop < _cropColours.Length ? NoFarming.Lerp(_cropColours[crop], (float)(0.45 + 0.55 * share)) : NoFarming;
                    }

                    if (v > -99.0)
                    {
                        int crop = (int)(-v) - 1;
                        return crop < _cropColours.Length ? NoFarming.Lerp(_cropColours[crop], 0.22f) : NoFarming;
                    }

                    return NoFarming;
                });
                break;
            case PopulationMode:
                _mapView.SetColours(cell => !map.IsLand(cell) ? Neutral(cell)
                    : values[cell] <= 0.0 ? EmptyLand
                    : Gradient(Math.Log10(values[cell]), PopulationStops));
                break;
            case PoliticalMode:
                _mapView.SetColours(cell =>
                {
                    if (!map.IsLand(cell))
                    {
                        return Neutral(cell);
                    }

                    double v = values[cell];
                    if (v < 0.0)
                    {
                        return NoFarming;
                    }

                    int code = (int)v;
                    int polity = code / 8;
                    if ((code & 2) != 0)
                    {
                        return CapitalColour;
                    }

                    if ((code & 4) != 0)
                    {
                        return WarFrontColour;
                    }

                    Color colour = PolityColour(polity, (code & 1) != 0);
                    foreach (int other in map.Grid.Neighbours(cell))
                    {
                        if (map.IsLand(other) && (values[other] < 0.0 || (int)values[other] / 8 != polity))
                        {
                            return colour.Darkened(0.45f);
                        }
                    }

                    return colour;
                });
                break;
            case TechnologyMode:
                Color techColour = _techIndex < _techColours.Length ? _techColours[_techIndex] : Colors.White;
                _mapView.SetColours(cell =>
                {
                    if (!map.IsLand(cell))
                    {
                        return Neutral(cell);
                    }

                    double v = values[cell];
                    return v < 0.0 ? EmptyLand
                        : v > 1.5 ? DiscoveryColour
                        : NoFarming.Lerp(techColour, (float)(v <= 0.0 ? 0.0 : 0.25 + 0.75 * v));
                });
                break;
            case GoodsMode:
                bool showPrice = _goodMeasure == GoodPrice;
                _mapView.SetColours(cell =>
                {
                    if (!map.IsLand(cell))
                    {
                        return Neutral(cell);
                    }

                    double v = values[cell];
                    if (v < 0.0)
                    {
                        return NoFarming;
                    }

                    if (showPrice)
                    {
                        return Gradient(Math.Log10(Math.Max(v, 1e-6)), PriceStops);
                    }

                    return v <= 0.0 ? NoneHeld : Gradient(Math.Log10(v), HeldStops);
                });
                break;
            case HerdingMode:
                _mapView.SetColours(cell =>
                {
                    if (!map.IsLand(cell))
                    {
                        return Neutral(cell);
                    }

                    double v = values[cell];
                    if (v >= 0.0)
                    {
                        int animal = (int)v;
                        double share = v - animal;
                        return animal < _animalColours.Length ? NoFarming.Lerp(_animalColours[animal], (float)(0.45 + 0.55 * share)) : NoFarming;
                    }

                    if (v > -99.0)
                    {
                        int animal = (int)(-v) - 1;
                        return animal < _animalColours.Length ? NoFarming.Lerp(_animalColours[animal], 0.22f) : NoFarming;
                    }

                    return NoFarming;
                });
                break;
            case TradeMode:
                // Population, faded, under the trade routes.
                _mapView.SetColours(cell => !map.IsLand(cell) ? Neutral(cell)
                    : values[cell] <= 0.0 ? EmptyLand
                    : EmptyLand.Lerp(Gradient(Math.Log10(values[cell]), PopulationStops), 0.35f));
                break;
        }
    }

    private Color Neutral(int cell) => _map!.IsLake(cell) ? LakeNeutral : SeaNeutral;

    private void ShowLegend(int mode)
    {
        var entries = new List<(Color, string)>();
        string title = "";
        switch (mode)
        {
            case ElevationMode:
                title = "Elevation";
                entries.Add((ElevationColour(false, -5000), "Deep sea"));
                entries.Add((ElevationColour(false, -200), "Shallow sea"));
                entries.Add((ElevationColour(true, 0), "0 m"));
                entries.Add((ElevationColour(true, 500), "500 m"));
                entries.Add((ElevationColour(true, 2000), "2,000 m"));
                entries.Add((ElevationColour(true, 5500), "5,500 m"));
                break;
            case BiomeMode:
                title = "Biome";
                if (_climate != null)
                {
                    entries.AddRange(_climate.Biomes.Biomes.Select(b => (_biomeColours[b.Index], b.Name)));
                }

                break;
            case TemperatureMode:
                title = "Mean temperature";
                entries.AddRange(TemperatureStops.Select(s => (s.Colour, s.At.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " °C")));
                break;
            case RainfallMode:
                title = "Rainfall a year";
                entries.AddRange(RainfallStops.Select(s => (s.Colour, s.At.ToString("N0", CultureInfo.InvariantCulture) + " mm")));
                break;
            case TemperatureChangeMode:
                title = "Temperature vs present day";
                entries.AddRange(TemperatureChangeStops.Select(s => (s.Colour, s.At.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) + " °C")));
                break;
            case FarmingMode:
                title = "Farming (brighter: more of the people farm)";
                if (_farming != null)
                {
                    entries.AddRange(_farming.Crops.Crops.Select(c => (_cropColours[c.Index], c.Name)));
                    entries.Add((NoFarming.Lerp(_cropColours[0], 0.22f), "Grows wild, not yet farmed"));
                }

                entries.Add((NoFarming, "No farming"));
                break;
            case PopulationMode:
                title = "People per 100 km²";
                entries.Add((EmptyLand, "None"));
                entries.AddRange(PopulationStops.Select(s => (s.Colour, Math.Pow(10.0, s.At).ToString("0.##", CultureInfo.InvariantCulture))));
                break;
            case PoliticalMode:
                title = "Who rules: each polity in its own colour, its border darker, war fronts red";
                entries.Add((PolityColour(1, true), "A state"));
                entries.Add((PolityColour(1, false), "A chiefdom (paler)"));
                entries.Add((PolityColour(1, true).Darkened(0.45f), "Border"));
                entries.Add((CapitalColour, "Capital"));
                entries.Add((NoFarming, "Ruled by nobody"));
                break;
            case TechnologyMode:
                if (_technology != null)
                {
                    Technology t = _technology.Technologies.Get(_techIndex);
                    Color colour = _techIndex < _techColours.Length ? _techColours[_techIndex] : Colors.White;
                    title = $"{t.Name}: share of people who know it (first seen {t.History})";
                    entries.Add((NoFarming, "Nobody"));
                    entries.Add((NoFarming.Lerp(colour, 0.25f), "A few"));
                    entries.Add((NoFarming.Lerp(colour, 0.625f), "Half"));
                    entries.Add((colour, "Everyone"));
                    entries.Add((DiscoveryColour, "Discovered here"));
                    entries.Add((EmptyLand, "No people"));
                }

                break;
            case GoodsMode:
                string goodName = ViewerGoods.Get(_goodIndex).Name;
                if (_goodMeasure == GoodPrice)
                {
                    title = $"{goodName}: price in settlements, against its usual value";
                    entries.AddRange(PriceStops.Select(s => (s.Colour, Math.Pow(10.0, s.At).ToString("0.##", CultureInfo.InvariantCulture) + " times")));
                }
                else
                {
                    title = _goodMeasure == GoodMade
                        ? $"{goodName} made per person by craftsmen last January ({ViewerGoods.Get(_goodIndex).Unit})"
                        : $"{goodName} held per person in settlements ({ViewerGoods.Get(_goodIndex).Unit})";
                    entries.Add((NoneHeld, "None"));
                    entries.AddRange(HeldStops.Select(s => (s.Colour, Math.Pow(10.0, s.At).ToString("0.####", CultureInfo.InvariantCulture))));
                }

                entries.Add((NoFarming, "No settlement"));
                entries.Add((new Color(GoodRouteColour, 0.3f), $"Route carrying {goodName.ToLowerInvariant()}: 0.02 food a season"));
                entries.Add((new Color(GoodRouteColour, 0.75f), "0.2 or more (thicker)"));
                entries.Add((GoodRouteColour, "2 or more (thickest)"));
                break;
            case HerdingMode:
                title = "Herding (brighter: more of the people herd)";
                if (_herding != null)
                {
                    entries.AddRange(_herding.Livestock.Animals.Select(a => (_animalColours[a.Index], a.Name)));
                    entries.Add((NoFarming.Lerp(_animalColours[0], 0.22f), "Kept only by farmers"));
                }

                entries.Add((NoFarming, "No animals"));
                break;
            case TradeMode:
                title = "Trade routes (food value carried a season)";
                entries.Add((new Color(TradeRouteColour, 0.3f), "0.2 or more"));
                entries.Add((new Color(TradeRouteColour, 0.75f), "2 or more (thicker)"));
                entries.Add((TradeRouteColour, "20 or more (thickest)"));
                break;
            case RainfallChangeMode:
                title = "Rainfall vs present day";
                entries.AddRange(RainfallChangeStops.Select(s => (s.Colour, (s.At * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%")));
                break;
            default:
                title = "Terrain";
                string[] names = { "Ocean", "Coast", "Lowland", "Hills", "Mountains", "Lake" };
                for (int i = 0; i < names.Length; i++)
                {
                    entries.Add((TerrainColours[i], names[i]));
                }

                break;
        }

        _hud.SetLegend(title, entries);
    }

    // Blends through a list of (value, colour) stops.
    private static Color Gradient(double value, (double At, Color Colour)[] stops)
    {
        if (value <= stops[0].At)
        {
            return stops[0].Colour;
        }

        for (int i = 1; i < stops.Length; i++)
        {
            if (value <= stops[i].At)
            {
                float t = (float)((value - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
                return stops[i - 1].Colour.Lerp(stops[i].Colour, t);
            }
        }

        return stops[stops.Length - 1].Colour;
    }

    private static readonly (double At, Color Colour)[] TemperatureStops =
    {
        (-40.0, Color.Color8(70, 40, 130)),
        (-20.0, Color.Color8(60, 100, 200)),
        (0.0, Color.Color8(170, 215, 235)),
        (10.0, Color.Color8(120, 185, 110)),
        (20.0, Color.Color8(240, 215, 90)),
        (30.0, Color.Color8(200, 50, 40)),
    };

    private static readonly (double At, Color Colour)[] RainfallStops =
    {
        (0.0, Color.Color8(225, 200, 150)),
        (250.0, Color.Color8(215, 205, 120)),
        (750.0, Color.Color8(130, 190, 100)),
        (1500.0, Color.Color8(40, 140, 110)),
        (3000.0, Color.Color8(30, 70, 160)),
        (6000.0, Color.Color8(70, 20, 110)),
    };

    private static readonly (double At, Color Colour)[] TemperatureChangeStops =
    {
        (-2.0, Color.Color8(40, 70, 170)),
        (-1.0, Color.Color8(130, 170, 230)),
        (0.0, Color.Color8(225, 225, 225)),
        (1.0, Color.Color8(240, 170, 120)),
        (2.0, Color.Color8(180, 40, 40)),
    };

    private static readonly (double At, Color Colour)[] RainfallChangeStops =
    {
        (0.5, Color.Color8(150, 90, 40)),
        (0.75, Color.Color8(215, 180, 120)),
        (1.0, Color.Color8(225, 225, 225)),
        (1.25, Color.Color8(120, 190, 170)),
        (1.5, Color.Color8(30, 110, 140)),
    };

    private static readonly Color EmptyLand = Color.Color8(70, 70, 74);
    private static readonly Color NoFarming = Color.Color8(62, 64, 68);
    private static readonly int FarmerOccupation = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
    private static readonly int HerderOccupation = OccupationSet.LoadDefault().IndexOf(OccupationSet.Herder);

    // Stops are powers of ten of people per 100 km².
    private static readonly Color NoneHeld = Color.Color8(24, 24, 30);
    private static readonly Color DiscoveryColour = Color.Color8(255, 60, 200);
    private static readonly Color CapitalColour = Color.Color8(255, 255, 255);
    private static readonly Color WarFrontColour = Color.Color8(220, 30, 30);

    // Amount held per person, as a power of ten: 0.0001 to 10.
    private static readonly (double At, Color Colour)[] HeldStops =
    {
        (-4.0, Color.Color8(80, 55, 130)),
        (-3.0, Color.Color8(130, 65, 160)),
        (-2.0, Color.Color8(195, 80, 125)),
        (-1.0, Color.Color8(240, 135, 70)),
        (0.0, Color.Color8(255, 215, 110)),
        (1.0, Color.Color8(255, 255, 220)),
    };

    // Price against the good's base value, as a power of ten: a fifth (cheap, blue) to five times (dear, red).
    private static readonly (double At, Color Colour)[] PriceStops =
    {
        (-0.69897, Color.Color8(60, 110, 200)),
        (-0.30103, Color.Color8(130, 170, 220)),
        (0.0, Color.Color8(225, 225, 215)),
        (0.30103, Color.Color8(230, 150, 110)),
        (0.69897, Color.Color8(200, 60, 50)),
    };

    private static readonly (double At, Color Colour)[] PopulationStops =
    {
        (-1.0, Color.Color8(60, 40, 90)),
        (0.0, Color.Color8(110, 60, 140)),
        (0.47712125471966244, Color.Color8(180, 80, 120)), // 3
        (1.0, Color.Color8(235, 130, 70)),
        (1.4771212547196624, Color.Color8(250, 210, 90)), // 30
        (2.0, Color.Color8(255, 250, 200)),
    };

    private static Color TemperatureColour(double meanC) => Gradient(meanC, TemperatureStops);

    private static Color RainfallColour(double annualMm) => Gradient(annualMm, RainfallStops);

    private static Color ElevationColour(bool land, int metres)
    {
        if (!land)
        {
            float depth = Math.Clamp(-metres / 6000f, 0f, 1f);
            return new Color(0.42f, 0.62f, 0.85f).Lerp(new Color(0.03f, 0.08f, 0.30f), depth);
        }

        if (metres < 0)
        {
            return new Color(0.30f, 0.45f, 0.30f);
        }

        if (metres < 500)
        {
            return new Color(0.36f, 0.60f, 0.33f).Lerp(new Color(0.78f, 0.78f, 0.45f), metres / 500f);
        }

        if (metres < 2000)
        {
            return new Color(0.78f, 0.78f, 0.45f).Lerp(new Color(0.60f, 0.42f, 0.28f), (metres - 500) / 1500f);
        }

        float high = Math.Clamp((metres - 2000) / 3500f, 0f, 1f);
        return new Color(0.60f, 0.42f, 0.28f).Lerp(new Color(0.97f, 0.97f, 0.97f), high);
    }

    private void OnSavePathChosen(string path)
    {
        if (_host == null || _pendingTask != null)
        {
            return;
        }

        if (!path.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
        {
            path += ".sav";
        }

        _pendingIsLoad = false;
        _pendingDoneMessage = "Saved " + Path.GetFileName(path);
        string target = path;
        _pendingTask = _host.InvokeAsync<string?>(sim =>
        {
            sim.Save(target);
            return null;
        });
        _hud.SetBusy(true);
        ShowMessage("Saving...", 600.0);
    }

    private void OnLoadPathChosen(string path)
    {
        if (_pendingTask != null)
        {
            return;
        }

        // From the start screen with no run yet, load into a fresh simulation.
        // A save that does not load leaves the simulation it was loaded into unchanged.
        if (_host == null)
        {
            InstallSimulation(new RunSettings());
        }

        SetSpeed(0);
        _pendingIsLoad = true;
        _pendingDoneMessage = "Loaded " + Path.GetFileName(path);
        string source = path;
        _pendingTask = _host!.InvokeAsync<string?>(sim =>
        {
            sim.Load(source);
            return sim.Settings.Describe();
        });
        _hud.SetBusy(true);
        ShowMessage("Loading...", 600.0);
    }

    private void PollPendingTask()
    {
        if (_pendingTask == null || !_pendingTask.IsCompleted)
        {
            return;
        }

        if (_pendingTask.IsCompletedSuccessfully)
        {
            ShowMessage(_pendingDoneMessage, 6.0);
            if (_pendingIsLoad && _pendingTask.Result is string description)
            {
                _runDescription = description;
                _startScreen.Close();
            }
        }
        else
        {
            string reason = _pendingTask.Exception?.GetBaseException().Message ?? "it was cancelled";
            ShowMessage((_pendingIsLoad ? "Could not load: " : "Could not save: ") + reason, 20.0);
            if (_pendingIsLoad && _startScreen.Visible)
            {
                _startScreen.ShowError("Could not load: " + reason);
            }
        }

        _pendingTask = null;
        _hud.SetBusy(false);
    }

    private void ShowMessage(string text, double seconds)
    {
        _message = text;
        _messageSecondsLeft = seconds;
    }

    private void UpdateStatus(SimFrame frame, double delta)
    {
        if (_messageSecondsLeft > 0.0)
        {
            _messageSecondsLeft -= delta;
            _hud.SetStatus(_message);
            return;
        }

        string rate = frame.MeasuredTicksPerSecond >= 1000.0
            ? (frame.MeasuredTicksPerSecond / 12.0).ToString("N0", CultureInfo.InvariantCulture) + " years/s"
            : frame.MeasuredTicksPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " months/s";
        _hud.SetStatus($"{rate}   {Engine.GetFramesPerSecond():F0} fps");
    }

    private void PanWithKeys(float delta)
    {
        var direction = Vector2.Zero;
        if (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.A))
        {
            direction.X -= 1f;
        }

        if (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.D))
        {
            direction.X += 1f;
        }

        if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.W))
        {
            direction.Y -= 1f;
        }

        if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.S))
        {
            direction.Y += 1f;
        }

        if (direction != Vector2.Zero)
        {
            _camera.Position += direction * (KeyPanPixelsPerSecond * delta / _zoom);
            ClampCamera();
        }
    }

    // Keeps the details of the cell under the pointer and the selected cell up to date.
    private void UpdateCellDetails(double delta)
    {
        int hoverCell = _mapView.CellAt(GetGlobalMousePosition());
        Track(_hover, hoverCell, delta);
        Track(_selected, _selectedCell, delta);

        _hud.UpdateTooltip(hoverCell >= 0 ? DescribeCell(hoverCell, _hover.Latest) : null);
        _hud.SetInfo(_runDescription + "\n\n" + (_selectedCell >= 0
            ? "Selected\n" + DescribeCell(_selectedCell, _selected.Latest)
            : "Click a cell to keep its details here."));
    }

    // Asks the simulation for its settlements about once a second while the date moves, and draws them.
    private void PollSettlements(SimFrame frame, double delta)
    {
        _settlementAge += delta;
        if (_settlementTask != null)
        {
            if (!_settlementTask.IsCompleted)
            {
                return;
            }

            if (_settlementTask.IsCompletedSuccessfully)
            {
                (List<SettlementInfo> list, List<TradeRoute> routes) = _settlementTask.Result;
                GeodesicGrid grid = _map!.Grid;
                var positions = new Vector2[list.Count];
                var names = new string[list.Count];
                var populations = new float[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    int cell = list[i].Cell;
                    positions[i] = MapView.MapPosition(grid.Latitude(cell), grid.Longitude(cell));
                    names[i] = list[i].Name;
                    populations[i] = (float)list[i].Population;
                }

                _mapView.SetSettlements(positions, names, populations);

                var ends = new Vector2[routes.Count * 2];
                var values = new float[routes.Count];
                for (int i = 0; i < routes.Count; i++)
                {
                    ends[2 * i] = MapView.MapPosition(grid.Latitude(routes[i].CellA), grid.Longitude(routes[i].CellA));
                    ends[2 * i + 1] = MapView.MapPosition(grid.Latitude(routes[i].CellB), grid.Longitude(routes[i].CellB));
                    values[i] = (float)routes[i].Value;
                }

                _mapView.SetRoutes(ends, values);
            }

            _settlementTask = null;
        }

        SettlementSystem? settlements = _settlementSystem;
        TradeSystem? trade = _tradeSystem;
        int routeGood = _mode == GoodsMode ? _goodIndex : -1;
        if (_host != null && settlements != null && (_settlementAge >= SettlementRefreshSeconds || routeGood != _routeGood)
            && (frame.Tick != _settlementTick || routeGood != _routeGood))
        {
            _settlementTick = frame.Tick;
            _settlementAge = 0.0;
            _routeGood = routeGood;
            _settlementTask = _host.InvokeAsync(_ => (settlements.All(), trade == null ? new List<TradeRoute>()
                : routeGood < 0 ? trade.Routes()
                // One good's routes carry far less; scaled tenfold so the same line widths apply.
                : trade.Routes(routeGood).Select(r => r with { Value = 10.0 * r.Value }).ToList()));
        }
    }

    // Runs on the simulation thread: the cell's settlement, if any.
    // Used only on the simulation thread, by the tooltip.
    private static Prices? _tooltipPrices;
    private static Simulation? _tooltipPricesFor;

    // Runs on the simulation thread: how far the settlement's boats reach, and when land settled by
    // sea was first reached.
    private static string DescribeSea(Simulation sim, int cell)
    {
        string text = "";
        int settled = sim.State.IntField(SettlementSystem.SettlementField)[cell];
        if (settled > 0 && sim.State.HasField(TradeSystem.BoatRangeField) && SeaMap.Of(sim.Map).Shore[cell])
        {
            int range = sim.State.IntField(TradeSystem.BoatRangeField)[cell];
            if (range > 0)
            {
                text += $"\nBoats cross up to {range} sea cells (about {range * 50} km)";
            }
        }

        SeafaringSystem? seafaring = sim.Systems.OfType<SeafaringSystem>().SingleOrDefault();
        if (seafaring != null && sim.Map.IsLand(cell))
        {
            int landmass = SeaMap.Of(sim.Map).Landmass[cell];
            foreach (Landing landing in seafaring.Landings())
            {
                if (landing.Landmass == landmass)
                {
                    var date = new GameDate(landing.Tick);
                    text += $"\nReached by sea in {date.Year} {(date.IsBce ? "BCE" : "CE")}, "
                        + $"from {sim.Map.Grid.DistanceKm(landing.From, landing.Cell).ToString("N0", CultureInfo.InvariantCulture)} km away";
                    break;
                }
            }
        }

        return text;
    }

    // Runs on the simulation thread: who rules the cell.
    private static string DescribePolity(Simulation sim, int cell, PolitySystem? polities)
    {
        if (polities == null)
        {
            return "";
        }

        int ruler = polities.RulerOf(cell);
        if (ruler < 0)
        {
            return "\nRuled by nobody";
        }

        PolityInfo p = polities.Info(ruler);
        var founded = new GameDate(p.Founded);
        string level = p.Level == PolityLevel.State ? "state" : "chiefdom";
        int cells = 0;
        for (int c = 0; c < sim.Map.CellCount; c++)
        {
            cells += polities.RulerOf(c) == ruler ? 1 : 0;
        }

        string tribute = p.Capital == cell
            ? $"\n  Capital: takes {polities.Tribute(ruler).ToString("N0", CultureInfo.InvariantCulture)} grain a year in tribute"
            : polities.Taken(cell) > 0.0 ? $"\n  Gives {(100.0 * polities.Taken(cell)).ToString("0", CultureInfo.InvariantCulture)}% of its store a year in tribute" : "";
        return $"\nRuled by {p.Name} ({level}), founded {founded.Year} {(founded.IsBce ? "BCE" : "CE")}, {cells} cells"
            + (p.Capital == cell ? ", capital here" : "")
            + tribute
            + DescribeWar(polities, ruler)
            + $"\n  Unrest {(100.0 * polities.CellUnrest(cell)).ToString("0", CultureInfo.InvariantCulture)}%"
            + $"\n  Temperament: aggression {p.Aggression.ToString("0.00", CultureInfo.InvariantCulture)}, "
            + $"greed {p.Greed.ToString("0.00", CultureInfo.InvariantCulture)}, expansion {p.Expansion.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    // The war a polity is in, if any, and how it goes.
    private static string DescribeWar(PolitySystem polities, int polity)
    {
        int war = polities.WarOf(polity);
        if (war < 0)
        {
            return "";
        }

        WarInfo w = polities.War(war);
        int enemy = w.Attacker == polity ? w.Defender : w.Attacker;
        var started = new GameDate(w.Started);
        int took = w.Attacker == polity ? w.AttackerTook : w.DefenderTook;
        int lost = w.Attacker == polity ? w.DefenderTook : w.AttackerTook;
        double deaths = w.AttackerDeaths + w.DefenderDeaths;
        return $"\n  At war with {polities.Info(enemy).Name} since {started.Year} {(started.IsBce ? "BCE" : "CE")}"
            + (w.Attacker == polity ? " (attacking)" : " (defending)")
            + $": took {took} cells, lost {lost}, {deaths.ToString("N0", CultureInfo.InvariantCulture)} dead";
    }

    private static string DescribeSettlement(Simulation sim, int cell, SettlementSystem? settlements)
    {
        if (settlements?.InCell(cell) is not { } s)
        {
            return "";
        }

        var founded = new GameDate(s.Founded);
        string text = $"\nSettlement: {s.Name}, founded {founded.Year} {(founded.IsBce ? "BCE" : "CE")}, "
            + $"{s.Population.ToString("N0", CultureInfo.InvariantCulture)} people";
        var stock = new List<string>();
        foreach (Good good in ViewerGoods.Goods)
        {
            string field = Stockpiles.StockFieldPrefix + good.Slug;
            double amount = sim.State.HasField(field) ? sim.State.DoubleField(field)[cell] : 0.0;
            if (amount >= 1.0)
            {
                stock.Add($"{good.Name.ToLowerInvariant()} {amount.ToString("N0", CultureInfo.InvariantCulture)}");
            }
        }

        if (stock.Count > 0)
        {
            text += "\n  In store: " + Wrap(stock);
        }

        // What the craftsmen made at their last January's work (bronze is made into tools at once,
        // so it never shows in store).
        var made = new List<string>();
        foreach (Good good in ViewerGoods.Goods)
        {
            string field = CraftSystem.MadeFieldPrefix + good.Slug;
            double amount = sim.State.HasField(field) ? sim.State.DoubleField(field)[cell] : 0.0;
            if (amount >= 0.05)
            {
                made.Add($"{good.Name.ToLowerInvariant()} {amount.ToString(amount >= 10.0 ? "N0" : "0.#", CultureInfo.InvariantCulture)}");
            }
        }

        if (made.Count > 0)
        {
            text += "\n  Made last January: " + Wrap(made);
        }

        // Prices in food, for the goods the settlement holds or wants (one price reckoner per run:
        // making one works out the technology conditions over the whole map).
        if (!ReferenceEquals(_tooltipPricesFor, sim))
        {
            _tooltipPrices = new Prices(sim.State, sim.Map, ViewerPriceParameters, ViewerCraftRules, OccupationSet.LoadDefault());
            _tooltipPricesFor = sim;
        }

        Prices prices = _tooltipPrices!;
        var wanted = new double[ViewerGoods.Count];
        var price = new double[ViewerGoods.Count];
        prices.Work(cell, sim.Tick, wanted, price);
        var priced = new List<string>();
        foreach (Good good in ViewerGoods.Goods)
        {
            string field = Stockpiles.StockFieldPrefix + good.Slug;
            double amount = sim.State.HasField(field) ? sim.State.DoubleField(field)[cell] : 0.0;
            if (wanted[good.Index] > 0.0 || amount >= 1.0)
            {
                priced.Add($"{good.Name.ToLowerInvariant()} {price[good.Index].ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }

        if (priced.Count > 0)
        {
            text += "\n  Prices in food: " + Wrap(priced);
        }

        return text;
    }

    // Joins a list, five items to a line, with later lines indented under the first.
    private static string Wrap(List<string> items)
    {
        var lines = new List<string>();
        for (int i = 0; i < items.Count; i += 5)
        {
            lines.Add(string.Join(", ", items.Skip(i).Take(5)));
        }

        return string.Join(",\n    ", lines);
    }

    // Runs on the simulation thread: what can be gathered in the cell.
    private static string DescribeResources(int cell, ResourceSystem? resources)
    {
        if (resources == null)
        {
            return "";
        }

        List<string> names = resources.ResourcesIn(cell)
            .Select(r => r.Richness >= 0.7 ? r.Good.ToLowerInvariant() : $"some {r.Good.ToLowerInvariant()}")
            .ToList();
        return names.Count == 0 ? "" : "\nResources: " + string.Join(", ", names);
    }

    // Asks the simulation for a cell's current climate every so often, without waiting for the answer.
    private void Track(LiveCell live, int cell, double delta)
    {
        if (cell != live.Cell)
        {
            live.Cell = cell;
            live.Latest = null;
            live.Age = double.MaxValue;
        }
        else
        {
            live.Age += delta;
        }

        if (live.Pending != null && live.Pending.IsCompleted)
        {
            if (live.Pending.IsCompletedSuccessfully && live.PendingCell == live.Cell)
            {
                live.Latest = live.Pending.Result;
            }

            live.Pending = null;
        }

        if (live.Pending == null && live.Cell >= 0 && live.Age >= CellRefreshSeconds
            && _host != null && _climate != null && _map!.IsLand(live.Cell))
        {
            int target = live.Cell;
            live.PendingCell = target;
            ForagerSystem? foragers = _foragers;
            OccupationSet occupations = _occupations;
            FarmingSystem? farmingForCell = _farming;
            HerdingSystem? herdingForCell = _herding;
            SettlementSystem? settlementsForCell = _settlementSystem;
            ResourceSystem? resourcesForCell = _resourceSystem;
            PolitySystem? politiesForCell = _polities;
            live.Pending = _host.InvokeAsync(sim => new CellNow(
                sim.State.DoubleField(ClimateSystem.TemperatureShiftField)[target],
                sim.State.DoubleField(ClimateSystem.RainfallFactorField)[target],
                sim.State.IntField(ClimateSystem.BiomeField)[target],
                foragers != null ? DescribePolity(sim, target, politiesForCell) + DescribeSettlement(sim, target, settlementsForCell) + DescribeSea(sim, target) + LabelGroups(DescribeGroups(sim, target, occupations))
                    + DescribeFarming(sim, target, farmingForCell) + DescribeHerding(sim, target, herdingForCell) + DescribeResources(target, resourcesForCell) : null,
                foragers != null ? sim.State.DoubleField(ForagerSystem.CapacityField)[target] : double.NaN,
                foragers?.IsReachable(target) ?? true));
            live.Age = 0.0;
        }
    }

    private void SelectCell(int cell)
    {
        _selectedCell = cell;
        _mapView.SetSelectedCell(cell);
    }

    private string DescribeCell(int cell, CellNow? now)
    {
        WorldMap map = _map!;
        GeodesicGrid grid = map.Grid;
        double latitude = grid.Latitude(cell);
        double longitude = grid.Longitude(cell);
        string latText = Math.Abs(latitude).ToString("F2", CultureInfo.InvariantCulture) + (latitude >= 0 ? "° N" : "° S");
        string lonText = Math.Abs(longitude).ToString("F2", CultureInfo.InvariantCulture) + (longitude >= 0 ? "° E" : "° W");
        string area = grid.AreaKm2(cell).ToString("N0", CultureInfo.InvariantCulture);
        string text = $"Cell {cell}\n{latText}, {lonText}\nTerrain: {map.Terrain(cell)}\n"
            + $"Elevation: {map.ElevationMetres(cell)} m\nArea: {area} km²";
        if (map.River(cell) != RiverSize.None)
        {
            text += $"\nRiver: {map.River(cell)}";
        }

        if (!map.HasClimate(cell))
        {
            return text;
        }

        bool live = map.IsLand(cell) && _climate != null;
        double shift = now?.TemperatureShift ?? 0.0;
        double factor = now?.RainfallFactor ?? 1.0;
        if (live)
        {
            string biome = now is { } n && n.Biome >= 0 && n.Biome < _climate!.Biomes.Count ? _climate.Biomes.Get(n.Biome).Name : "…";
            text += $"\nBiome: {biome}";
        }

        string coldest = (Math.Min(map.TemperatureC(cell, 0), map.TemperatureC(cell, 6)) + shift).ToString("F0", CultureInfo.InvariantCulture);
        string warmest = (Math.Max(map.TemperatureC(cell, 0), map.TemperatureC(cell, 6)) + shift).ToString("F0", CultureInfo.InvariantCulture);
        string mean = (_baselineMean[cell] + shift).ToString("F1", CultureInfo.InvariantCulture);
        string rain = (_baselineRain[cell] * factor).ToString("N0", CultureInfo.InvariantCulture);
        text += $"\nTemperature: {mean} °C (Jan/Jul {coldest} to {warmest})\nRainfall: {rain} mm a year";
        if (live && now != null)
        {
            text += $"\nVs present day: {shift.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} °C, "
                + $"{(factor * 100.0).ToString("0", CultureInfo.InvariantCulture)}% rain";
        }

        if (live && now is { } people && people.Groups != null)
        {
            text += !people.Reachable
                ? "\nNo people: not reached yet"
                : $"\nLand can feed {people.Capacity.ToString("N0", CultureInfo.InvariantCulture)} foragers" + people.Groups;
        }

        return text;
    }

    // Runs on the simulation thread: the animals kept in the cell and what its pasture can feed.
    private static string DescribeHerding(Simulation sim, int cell, HerdingSystem? herding)
    {
        if (herding == null)
        {
            return "";
        }

        int mask = sim.State.IntField(HerdingSystem.AnimalsField)[cell];
        if (mask == 0)
        {
            return "";
        }

        var names = herding.Livestock.Animals.Where(a => (mask & (1 << a.Index)) != 0).Select(a => a.Name);
        string text = "\nAnimals: " + string.Join(", ", names);
        double capacity = sim.State.DoubleField(HerdingSystem.CapacityField)[cell];
        if (capacity > 0.0)
        {
            text += $"\nPasture can feed {capacity.ToString("N0", CultureInfo.InvariantCulture)} herders";
        }

        return text;
    }

    // Runs on the simulation thread: what is farmed in the cell, and what grows wild there.
    private static string DescribeFarming(Simulation sim, int cell, FarmingSystem? farming)
    {
        if (farming == null)
        {
            return "";
        }

        string text = "";
        int crop = sim.State.IntField(FarmingSystem.CropField)[cell] - 1;
        if (crop >= 0)
        {
            var since = new GameDate(Math.Max(0, sim.State.IntField(FarmingSystem.SinceField)[cell] - 1));
            double capacity = sim.State.DoubleField(FarmingSystem.CapacityField)[cell];
            text += $"\nFarming {farming.Crops.Get(crop).Name} since {since.Year} {(since.IsBce ? "BCE" : "CE")}"
                + $"\nFarmland can feed {capacity.ToString("N0", CultureInfo.InvariantCulture)}";
        }

        int faminePlusOne = sim.State.IntField(FarmingSystem.LastFamineField)[cell];
        if (faminePlusOne > 0)
        {
            var famine = new GameDate(faminePlusOne - 1);
            text += $"\nLast famine: {famine.Year} {(famine.IsBce ? "BCE" : "CE")}";
        }

        int wild = sim.State.IntField(FarmingSystem.WildCropsField)[cell];
        if (wild != 0)
        {
            var names = farming.Crops.Crops.Where(c => (wild & (1 << c.Index)) != 0).Select(c => c.Name);
            text += "\nGrows wild: " + string.Join(", ", names);
        }

        return text;
    }

    private static string LabelGroups(string groups) => groups.Length > 0 ? "\nGroups:" + groups : "\nGroups: none";

    // Runs on the simulation thread: one line per population group in the cell.
    private static string DescribeGroups(Simulation sim, int cell, OccupationSet occupations)
    {
        var groups = new PopulationGroups(sim.State);
        var text = new System.Text.StringBuilder();
        foreach (int group in groups.InCell(cell))
        {
            string name = occupations.Get(groups.Occupation(group)).Name;
            text.Append($"\n  {name}: {groups.Size(group).ToString("N0", CultureInfo.InvariantCulture)}, "
                + $"food {(groups.Food(group) * 100.0).ToString("0", CultureInfo.InvariantCulture)}%");
            if (groups.Everyday(group) > 0.0 || groups.Luxury(group) > 0.0)
            {
                text.Append($", everyday {(groups.Everyday(group) * 100.0).ToString("0", CultureInfo.InvariantCulture)}%"
                    + $", luxury {(groups.Luxury(group) * 100.0).ToString("0", CultureInfo.InvariantCulture)}%");
            }

            // Technologies the group uses, and any it is still learning or forgetting.
            var known = new List<string>();
            foreach (Technology t in ViewerTechnologies.Technologies)
            {
                string column = PopulationGroups.KnowledgeColumnPrefix + t.Slug;
                EntityTable table = sim.State.Table(PopulationGroups.TableName);
                if (!table.ColumnNames.Contains(column))
                {
                    continue;
                }

                double share = table.DoubleColumn(column)[group];
                if (share >= 0.5)
                {
                    known.Add(t.Name.ToLowerInvariant());
                }
                else if (share >= 0.005)
                {
                    known.Add($"{t.Name.ToLowerInvariant()} ({(share * 100.0).ToString("0", CultureInfo.InvariantCulture)}%)");
                }
            }

            if (known.Count > 0)
            {
                text.Append("\n    knows " + string.Join(", ", known));
            }
        }

        return text.ToString();
    }

    private void FitWholeMap()
    {
        Vector2 size = GetViewportRect().Size;
        _minZoom = Math.Max(0.01f, Math.Min(size.X / (2f * MapView.HalfWidth), size.Y / (2f * MapView.HalfHeight)));
        _zoom = _minZoom;
        _camera.Zoom = new Vector2(_zoom, _zoom);
        _camera.Position = Vector2.Zero;
        _mapView.SetZoom(_zoom);
    }

    private void OnViewportResized()
    {
        Vector2 size = GetViewportRect().Size;
        _minZoom = Math.Max(0.01f, Math.Min(size.X / (2f * MapView.HalfWidth), size.Y / (2f * MapView.HalfHeight)));
        if (_zoom < _minZoom)
        {
            _zoom = _minZoom;
            _camera.Zoom = new Vector2(_zoom, _zoom);
        }

        ClampCamera();
    }

    private void ZoomAt(Vector2 mapPoint, float factor)
    {
        float newZoom = Math.Clamp(_zoom * factor, _minZoom, MaxZoom);
        if (newZoom == _zoom)
        {
            return;
        }

        // Keep the point under the pointer fixed on screen.
        _camera.Position = mapPoint + (_camera.Position - mapPoint) * (_zoom / newZoom);
        _zoom = newZoom;
        _camera.Zoom = new Vector2(_zoom, _zoom);
        _mapView.SetZoom(_zoom);
        ClampCamera();
    }

    private void ClampCamera()
    {
        // The map wraps around left to right, and stops at the top and bottom.
        Vector2 half = GetViewportRect().Size / (2f * _zoom);
        float limitY = MapView.HalfHeight - half.Y;
        Vector2 position = _camera.Position;
        position.X = MapView.WrapX(position.X);
        position.Y = limitY > 0f ? Math.Clamp(position.Y, -limitY, limitY) : 0f;
        _camera.Position = position;
    }

    // Used by the automated build: exercises the viewer, saves pictures of it and quits.
    private void RunScreenshotScript(SimFrame? frame)
    {
        _frame++;
        switch (_frame)
        {
            case 5:
                SaveScreenshot("-start");
                StartRun(new RunSettings { Seed = 42, CropHomelands = CropHomelands.Real });
                break;
            case 10:
                SetSpeed(4);
                break;
            case 90:
                // Run until 3,000 years have passed, so farming and settlements have had time to grow.
                if (frame != null && frame.Tick < 3000L * 12 && _frameWaits++ < 3000)
                {
                    _frame--;
                    break;
                }

                SetSpeed(0);
                SetMapMode(BiomeMode);
                break;
            case 105:
                SaveScreenshot("");
                SetMapMode(TemperatureChangeMode);
                break;
            case 120:
                SaveScreenshot("-temperature-change");
                SetMapMode(RainfallChangeMode);
                break;
            case 135:
                SaveScreenshot("-rainfall-change");
                SetMapMode(PopulationMode);
                break;
            case 150:
                SaveScreenshot("-population");
                SetMapMode(FarmingMode);
                break;
            case 165:
                SaveScreenshot("-farming");

                // Zoom in on Southwest Asia, with the pointer in the middle for the tooltip.
                _camera.Position = MapView.MapPosition(33.0, 40.0);
                ZoomAt(MapView.MapPosition(33.0, 40.0), 4f);
                SelectCell(_map!.Grid.FindCell(30.6, 31.2));
                GetViewport().WarpMouse(GetViewportRect().Size / 2f);
                break;
            case 195:
                GD.Print($"Screenshot script: {_runDescription}, date {frame?.Date}, tick {frame?.Tick}, selected cell {_selectedCell}");
                _screenshotOk = SaveScreenshot("-detail") && frame != null && frame.Tick > 0;

                // Southwest Asia's trade routes.
                SetMapMode(TradeMode);
                SelectCell(-1);
                _camera.Position = MapView.MapPosition(36.5, 40.0);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 2f);
                GetViewport().WarpMouse(new Vector2(GetViewportRect().Size.X / 2f, 12f));
                break;
            case 215:
                _screenshotOk &= SaveScreenshot("-trade");

                // Herding, zoomed out over Southwest Asia and the steppe.
                SetMapMode(HerdingMode);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 0.5f);
                break;
            case 230:
                _screenshotOk &= SaveScreenshot("-herding");

                // How far obsidian has spread from its sources.
                _goodIndex = ViewerGoods.IndexOf("Obsidian");
                _goodMeasure = 0;
                SetMapMode(GoodsMode);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 2f);
                break;
            case 245:
                _screenshotOk &= SaveScreenshot("-goods");

                // Who knows of granaries, zoomed out a little.
                _techIndex = 0;
                SetMapMode(TechnologyMode);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 0.5f);
                break;
            case 260:
                _screenshotOk &= SaveScreenshot("-technology");

                // Close in on the Fertile Crescent to show settlement names.
                SetMapMode(PopulationMode);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 5f);
                GetViewport().WarpMouse(GetViewportRect().Size / 2f);
                break;
            case 285:
                bool savedSettlements = SaveScreenshot("-settlements");
                GetTree().Quit(_screenshotOk && savedSettlements ? 0 : 1);
                break;
        }
    }

    private static readonly Color TradeRouteColour = new Color(0.95f, 0.55f, 0.15f);

    // Over the goods map's purple-to-yellow colours, routes are drawn in cyan.
    private static readonly Color GoodRouteColour = new Color(0.35f, 0.9f, 1.0f);

    private void SetMapMode(int mode)
    {
        ApplyMapMode(mode);
        _hud.ShowMapMode(mode);
    }

    private bool SaveScreenshot(string suffix)
    {
        string path = Path.ChangeExtension(_screenshotPath!, null) + suffix + ".png";
        Image image = GetViewport().GetTexture().GetImage();
        Error result = image.SavePng(path);
        GD.Print($"Screenshot saved to {path}: {result}");
        return result == Error.Ok;
    }

    private readonly record struct CellNow(
        double TemperatureShift,
        double RainfallFactor,
        int Biome,
        string? Groups,
        double Capacity,
        bool Reachable);

    private sealed class LiveCell
    {
        public int Cell = -1;
        public CellNow? Latest;
        public Task<CellNow>? Pending;
        public int PendingCell = -1;
        public double Age;
    }
}
