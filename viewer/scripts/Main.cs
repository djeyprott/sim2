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
using Sim.Core.Hosting;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;

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
    private SettlementSystem? _settlementSystem;
    private Task<List<SettlementInfo>>? _settlementTask;
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
        _settlementSystem = systems.OfType<SettlementSystem>().FirstOrDefault();
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
                worldInfo = "World " + summary[0].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " °C";
            }

            if (!double.IsNaN(summary[1]))
            {
                worldInfo += "   " + (summary[1] / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " M people";
            }

            if (summary.Length >= 3 && !double.IsNaN(summary[2]))
            {
                worldInfo += ", " + (summary[2] / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " M farmers";
            }

            if (summary.Length >= 4 && !double.IsNaN(summary[3]))
            {
                worldInfo += "   Food in store " + (summary[3] / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " M";
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

        if (mode == PopulationMode && _foragers != null)
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
        }
    }

    private Color Neutral(int cell) => _map!.IsLake(cell) ? LakeNeutral : SeaNeutral;

    private void ShowLegend(int mode)
    {
        var entries = new List<(Color, string)>();
        string title;
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

    // Stops are powers of ten of people per 100 km².
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
                List<SettlementInfo> list = _settlementTask.Result;
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
            }

            _settlementTask = null;
        }

        SettlementSystem? settlements = _settlementSystem;
        if (_host != null && settlements != null && _settlementAge >= SettlementRefreshSeconds && frame.Tick != _settlementTick)
        {
            _settlementTick = frame.Tick;
            _settlementAge = 0.0;
            _settlementTask = _host.InvokeAsync(_ => settlements.All());
        }
    }

    // Runs on the simulation thread: the cell's settlement, if any.
    private static string DescribeSettlement(Simulation sim, int cell, SettlementSystem? settlements)
    {
        if (settlements?.InCell(cell) is not { } s)
        {
            return "";
        }

        var founded = new GameDate(s.Founded);
        string text = $"\nSettlement: {s.Name}, founded {founded.Year} {(founded.IsBce ? "BCE" : "CE")}, "
            + $"{s.Population.ToString("N0", CultureInfo.InvariantCulture)} people";
        if (sim.State.HasField(GrainStockField))
        {
            double grain = sim.State.DoubleField(GrainStockField)[cell];
            text += $"\n  Grain in store: {grain.ToString("N0", CultureInfo.InvariantCulture)} person-years";
        }

        return text;
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
            SettlementSystem? settlementsForCell = _settlementSystem;
            live.Pending = _host.InvokeAsync(sim => new CellNow(
                sim.State.DoubleField(ClimateSystem.TemperatureShiftField)[target],
                sim.State.DoubleField(ClimateSystem.RainfallFactorField)[target],
                sim.State.IntField(ClimateSystem.BiomeField)[target],
                foragers != null ? DescribeSettlement(sim, target, settlementsForCell) + DescribeGroups(sim, target, occupations)
                    + DescribeFarming(sim, target, farmingForCell) : null,
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
                : $"\nLand can feed {people.Capacity.ToString("N0", CultureInfo.InvariantCulture)} foragers"
                    + (people.Groups.Length > 0 ? "\nGroups:" + people.Groups : "\nGroups: none");
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

                // Close in on the Fertile Crescent to show settlement names.
                SetMapMode(PopulationMode);
                SelectCell(-1);
                _camera.Position = MapView.MapPosition(36.5, 40.0);
                ZoomAt(MapView.MapPosition(36.5, 40.0), 5f);
                GetViewport().WarpMouse(GetViewportRect().Size / 2f);
                break;
            case 225:
                bool savedSettlements = SaveScreenshot("-settlements");
                GetTree().Quit(_screenshotOk && savedSettlements ? 0 : 1);
                break;
        }
    }

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
