using System;
using Godot;

namespace Viewer;

/// <summary>The controls drawn over the map: date, time controls, map mode, save and load, cell details.</summary>
public partial class Hud : CanvasLayer
{
    private static readonly string[] SpeedNames = { "Pause", "1 month/s", "1 year/s", "10 years/s", "Max" };

    private readonly Button[] _speedButtons = new Button[SpeedNames.Length];
    private Label _dateLabel = null!;
    private Label _statusLabel = null!;
    private Label _infoLabel = null!;
    private Button _saveButton = null!;
    private Button _loadButton = null!;

    /// <summary>Raised with the index of the chosen speed: 0 pause, 1 month/s, 2 year/s, 3 ten years/s, 4 maximum.</summary>
    public event Action<int>? SpeedChosen;

    /// <summary>Raised with 0 for terrain or 1 for elevation.</summary>
    public event Action<int>? MapModeChosen;

    public event Action? SaveRequested;

    public event Action? LoadRequested;

    public override void _Ready()
    {
        var topPanel = new PanelContainer();
        AddChild(topPanel);
        topPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);

        var margin = NewMargin(8);
        topPanel.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        margin.AddChild(row);

        _dateLabel = new Label { Text = "", CustomMinimumSize = new Vector2(230, 0) };
        row.AddChild(_dateLabel);
        row.AddChild(new VSeparator());

        for (int i = 0; i < SpeedNames.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = SpeedNames[i],
                ToggleMode = true,
                FocusMode = Control.FocusModeEnum.None,
            };
            button.Pressed += () => SpeedChosen?.Invoke(index);
            _speedButtons[i] = button;
            row.AddChild(button);
        }

        row.AddChild(new VSeparator());
        row.AddChild(new Label { Text = "Map:" });

        var mode = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        mode.AddItem("Terrain", 0);
        mode.AddItem("Elevation", 1);
        mode.ItemSelected += index => MapModeChosen?.Invoke((int)index);
        row.AddChild(mode);

        row.AddChild(new VSeparator());

        _saveButton = new Button { Text = "Save", FocusMode = Control.FocusModeEnum.None };
        _saveButton.Pressed += () => SaveRequested?.Invoke();
        row.AddChild(_saveButton);

        _loadButton = new Button { Text = "Load", FocusMode = Control.FocusModeEnum.None };
        _loadButton.Pressed += () => LoadRequested?.Invoke();
        row.AddChild(_loadButton);

        row.AddChild(new VSeparator());

        _statusLabel = new Label { Text = "" };
        row.AddChild(_statusLabel);

        var infoPanel = new PanelContainer();
        AddChild(infoPanel);
        infoPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 12);
        infoPanel.GrowVertical = Control.GrowDirection.Begin;
        infoPanel.MouseFilter = Control.MouseFilterEnum.Ignore;

        var infoMargin = NewMargin(10);
        infoMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        infoPanel.AddChild(infoMargin);

        _infoLabel = new Label
        {
            Text = "",
            CustomMinimumSize = new Vector2(260, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        infoMargin.AddChild(_infoLabel);

        ShowSpeed(0);
    }

    public void SetDate(string text) => _dateLabel.Text = text;

    public void SetStatus(string text) => _statusLabel.Text = text;

    public void SetInfo(string text) => _infoLabel.Text = text;

    /// <summary>Marks which speed button is active.</summary>
    public void ShowSpeed(int index)
    {
        for (int i = 0; i < _speedButtons.Length; i++)
        {
            _speedButtons[i].SetPressedNoSignal(i == index);
        }
    }

    /// <summary>Greys out save and load while one of them is in progress.</summary>
    public void SetBusy(bool busy)
    {
        _saveButton.Disabled = busy;
        _loadButton.Disabled = busy;
    }

    private static MarginContainer NewMargin(int size)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", size);
        margin.AddThemeConstantOverride("margin_right", size);
        margin.AddThemeConstantOverride("margin_top", size / 2);
        margin.AddThemeConstantOverride("margin_bottom", size / 2);
        return margin;
    }
}
