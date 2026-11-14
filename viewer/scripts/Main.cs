using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;
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
    private const ulong DefaultSeed = 1;
    private const float MaxZoom = 40f;
    private const float KeyPanPixelsPerSecond = 900f;
    private const float ClickSlopPixels = 5f;

    private static readonly double[] Speeds = { 0.0, 1.0, 12.0, 120.0, SimulationHost.MaxSpeed };

    private static readonly Color[] TerrainColours =
    {
        Color.Color8(24, 52, 110),   // Ocean
        Color.Color8(70, 120, 190),  // Coast
        Color.Color8(96, 150, 80),   // Lowland
        Color.Color8(176, 160, 100), // Hills
        Color.Color8(120, 96, 84),   // Mountains
    };

    private WorldMap? _map;
    private SimulationHost? _host;
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
    private int _hoverCell = -1;
    private string? _hoverText;
    private int _selectedCell = -1;
    private int _speedIndex;
    private int _speedBeforePause = 2;

    private Task? _pendingTask;
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

        _mapView.Build(_map);
        ApplyMapMode(0);

        var simulation = new Simulation(_map, DefaultSeed, SystemCatalog.CreateDefault());
        _host = new SimulationHost(simulation);

        _hud.SpeedChosen += SetSpeed;
        _hud.MapModeChosen += ApplyMapMode;
        RefreshInfo();
        _hud.SaveRequested += () => _saveDialog.PopupCentered(new Vector2I(900, 600));
        _hud.LoadRequested += () => _loadDialog.PopupCentered(new Vector2I(900, 600));

        _saveDialog = NewFileDialog(FileDialog.FileModeEnum.SaveFile, "Save the simulation");
        _saveDialog.FileSelected += OnSavePathChosen;
        _loadDialog = NewFileDialog(FileDialog.FileModeEnum.OpenFile, "Load a saved simulation");
        _loadDialog.FileSelected += OnLoadPathChosen;

        FitWholeMap();
        GetViewport().SizeChanged += OnViewportResized;

        if (OS.HasEnvironment("SIM2_SCREENSHOT"))
        {
            _screenshotPath = OS.GetEnvironment("SIM2_SCREENSHOT");
        }
    }

    public override void _ExitTree()
    {
        _host?.Dispose();
        _host = null;
    }

    public override void _Process(double delta)
    {
        if (_host == null || _map == null)
        {
            return;
        }

        SimFrame frame = _host.LatestFrame;
        _hud.SetDate(frame.Date.ToString());

        if (_host.Fault != null && _speedIndex != 0)
        {
            _speedIndex = 0;
            _hud.ShowSpeed(0);
            ShowMessage("The simulation stopped with an error: " + _host.Fault.Message, 60.0);
        }

        PollPendingTask();
        PanWithKeys((float)delta);
        UpdateHover();

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

        WorldMap map = _map;
        if (mode == 1)
        {
            _mapView.SetColours(cell => ElevationColour(map.IsLand(cell), map.ElevationMetres(cell)));
        }
        else
        {
            _mapView.SetColours(cell => TerrainColours[(int)map.Terrain(cell)]);
        }
    }

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
        _pendingTask = _host.SaveAsync(path);
        _hud.SetBusy(true);
        ShowMessage("Saving...", 600.0);
    }

    private void OnLoadPathChosen(string path)
    {
        if (_host == null || _pendingTask != null)
        {
            return;
        }

        SetSpeed(0);
        _pendingIsLoad = true;
        _pendingDoneMessage = "Loaded " + Path.GetFileName(path);
        _pendingTask = _host.LoadAsync(path);
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
        }
        else
        {
            string reason = _pendingTask.Exception?.GetBaseException().Message ?? "it was cancelled";
            ShowMessage((_pendingIsLoad ? "Could not load: " : "Could not save: ") + reason, 20.0);
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

    private void UpdateHover()
    {
        int cell = _mapView.CellAt(GetGlobalMousePosition());
        if (cell != _hoverCell)
        {
            _hoverCell = cell;
            _hoverText = cell >= 0 ? DescribeCell(cell) : null;
        }

        _hud.UpdateTooltip(_hoverText);
    }

    private void SelectCell(int cell)
    {
        _selectedCell = cell;
        _mapView.SetSelectedCell(cell);
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        _hud.SetInfo(_selectedCell >= 0
            ? "Selected\n" + DescribeCell(_selectedCell)
            : "Click a cell to keep its details here.");
    }

    private string DescribeCell(int cell)
    {
        WorldMap map = _map!;
        GeodesicGrid grid = map.Grid;
        double latitude = grid.Latitude(cell);
        double longitude = grid.Longitude(cell);
        string latText = Math.Abs(latitude).ToString("F2", CultureInfo.InvariantCulture) + (latitude >= 0 ? "° N" : "° S");
        string lonText = Math.Abs(longitude).ToString("F2", CultureInfo.InvariantCulture) + (longitude >= 0 ? "° E" : "° W");
        string area = grid.AreaKm2(cell).ToString("N0", CultureInfo.InvariantCulture);
        return $"Cell {cell}\n{latText}, {lonText}\nTerrain: {map.Terrain(cell)}\n"
            + $"Elevation: {map.ElevationMetres(cell)} m\nArea: {area} km²";
    }

    private void FitWholeMap()
    {
        Vector2 size = GetViewportRect().Size;
        _minZoom = Math.Max(0.01f, Math.Min(size.X / (2f * MapView.HalfWidth), size.Y / (2f * MapView.HalfHeight)));
        _zoom = _minZoom;
        _camera.Zoom = new Vector2(_zoom, _zoom);
        _camera.Position = Vector2.Zero;
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

    // Used by the automated build: exercises the viewer, saves a picture of it and quits.
    private void RunScreenshotScript(SimFrame frame)
    {
        _frame++;
        if (_frame == 10)
        {
            SetSpeed(3);
        }
        else if (_frame == 60)
        {
            // Look across the edge of the map (the Bering Strait) to show that it wraps,
            // and put the pointer in the middle so the tooltip appears.
            _camera.Position = MapView.MapPosition(64.0, 180.0);
            ZoomAt(MapView.MapPosition(64.0, 180.0), 5f);
            SelectCell(_map!.Grid.FindCell(66.0, -169.0));
            GetViewport().WarpMouse(GetViewportRect().Size / 2f);
        }
        else if (_frame == 90)
        {
            GD.Print($"Screenshot script: date {frame.Date}, tick {frame.Tick}, selected cell {_selectedCell}");
            Image image = GetViewport().GetTexture().GetImage();
            Error result = image.SavePng(_screenshotPath!);
            GD.Print($"Screenshot saved to {_screenshotPath}: {result}");
            GetTree().Quit(result == Error.Ok && frame.Tick > 0 ? 0 : 1);
        }
    }
}
