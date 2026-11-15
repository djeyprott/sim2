using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Hosting;
using Sim.Core.Map;

namespace Viewer;

/// <summary>
/// Entry point of the viewer. Loads the world, starts the simulation on its own thread,
/// and connects the map, the camera and the controls. It only displays: all simulation
/// logic lives in Sim.Core.
/// </summary>
public partial class Main : Node2D
{
    private const float MaxZoom = 40f;
    private const float KeyPanPixelsPerSecond = 900f;
    private const float ClickSlopPixels = 5f;

    private const int TerrainMode = 0;
    private const int ElevationMode = 1;
    private const int BiomeMode = 2;
    private const int TemperatureMode = 3;
    private const int RainfallMode = 4;
    private const int TemperatureChangeMode = 5;
    private const int RainfallChangeMode = 6;

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
        _biomeColours = _climate == null
            ? Array.Empty<Color>()
            : _climate.Biomes.Biomes.Select(b => Color.FromHtml(b.Colour)).ToArray();

        _host = new SimulationHost(simulation);
        ClimateSystem? climate = _climate;
        if (climate != null)
        {
            _host.SetSummaryExtractor(_ => new[] { climate.WorldTemperatureShift() });
        }

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
        _hud.SetWorldInfo(frame.Summary is { Length: > 0 } summary
            ? "World " + summary[0].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " °C"
            : "");

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
            live.Pending = _host.InvokeAsync(sim => new CellNow(
                sim.State.DoubleField(ClimateSystem.TemperatureShiftField)[target],
                sim.State.DoubleField(ClimateSystem.RainfallFactorField)[target],
                sim.State.IntField(ClimateSystem.BiomeField)[target]));
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

        return text;
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
            case 40:
                SetSpeed(0);
                SetMapMode(BiomeMode);
                break;
            case 55:
                SaveScreenshot("");
                SetMapMode(TemperatureChangeMode);
                break;
            case 70:
                SaveScreenshot("-temperature-change");
                SetMapMode(RainfallChangeMode);
                break;
            case 85:
                SaveScreenshot("-rainfall-change");
                SetMapMode(TemperatureMode);
                break;
            case 100:
                SaveScreenshot("-temperature");
                SetMapMode(BiomeMode);

                // Zoom in on northeast Africa and the Middle East, with the pointer in the middle for the tooltip.
                _camera.Position = MapView.MapPosition(24.0, 34.0);
                ZoomAt(MapView.MapPosition(24.0, 34.0), 4f);
                SelectCell(_map!.Grid.FindCell(30.6, 31.2));
                GetViewport().WarpMouse(GetViewportRect().Size / 2f);
                break;
            case 130:
                GD.Print($"Screenshot script: {_runDescription}, date {frame?.Date}, tick {frame?.Tick}, selected cell {_selectedCell}");
                bool saved = SaveScreenshot("-detail");
                GetTree().Quit(saved && frame != null && frame.Tick > 0 ? 0 : 1);
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

    private readonly record struct CellNow(double TemperatureShift, double RainfallFactor, int Biome);

    private sealed class LiveCell
    {
        public int Cell = -1;
        public CellNow? Latest;
        public Task<CellNow>? Pending;
        public int PendingCell = -1;
        public double Age;
    }
}
