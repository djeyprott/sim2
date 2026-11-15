using System;
using System.Collections.Generic;
using Godot;

namespace Viewer;

/// <summary>The controls drawn over the map: date, time controls, map mode, save and load, cell details.</summary>
public partial class Hud : CanvasLayer
{
    /// <summary>Map modes in the order of their index.</summary>
    public static readonly string[] MapModeNames =
    {
        "Terrain", "Elevation", "Biome", "Temperature", "Rainfall", "Temperature change", "Rainfall change",
    };

    private static readonly string[] SpeedNames = { "Pause", "1 month/s", "1 year/s", "10 years/s", "Max" };

    private readonly Button[] _speedButtons = new Button[SpeedNames.Length];
    private OptionButton _modeButton = null!;
    private Label _dateLabel = null!;
    private Label _worldLabel = null!;
    private PanelContainer _legendPanel = null!;
    private VBoxContainer _legendList = null!;
    private Label _statusLabel = null!;
    private Label _infoLabel = null!;
    private PanelContainer _tooltipPanel = null!;
    private Label _tooltipLabel = null!;
    private Button _saveButton = null!;
    private Button _loadButton = null!;

    /// <summary>Raised with the index of the chosen speed: 0 pause, 1 month/s, 2 year/s, 3 ten years/s, 4 maximum.</summary>
    public event Action<int>? SpeedChosen;

    /// <summary>Raised with the index of the chosen map mode, as in <see cref="MapModeNames"/>.</summary>
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

        _dateLabel = new Label { Text = "", CustomMinimumSize = new Vector2(190, 0) };
        row.AddChild(_dateLabel);
        _worldLabel = new Label { Text = "", CustomMinimumSize = new Vector2(150, 0) };
        row.AddChild(_worldLabel);
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

        _modeButton = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        for (int i = 0; i < MapModeNames.Length; i++)
        {
            _modeButton.AddItem(MapModeNames[i], i);
        }

        _modeButton.ItemSelected += index => MapModeChosen?.Invoke((int)index);
        row.AddChild(_modeButton);

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

        _legendPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        AddChild(_legendPanel);

        var legendMargin = NewMargin(10);
        legendMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        _legendPanel.AddChild(legendMargin);

        _legendList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _legendList.AddThemeConstantOverride("separation", 2);
        legendMargin.AddChild(_legendList);

        _tooltipPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        AddChild(_tooltipPanel);

        var tooltipMargin = NewMargin(10);
        tooltipMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tooltipPanel.AddChild(tooltipMargin);

        _tooltipLabel = new Label { Text = "", MouseFilter = Control.MouseFilterEnum.Ignore };
        tooltipMargin.AddChild(_tooltipLabel);

        ShowSpeed(0);
    }

    /// <summary>
    /// Shows a small panel of text beside the pointer, or hides it when given null.
    /// Call every frame so it follows the pointer.
    /// </summary>
    public void UpdateTooltip(string? text)
    {
        // Hide it while the pointer is over a button or panel.
        if (text == null || GetViewport().GuiGetHoveredControl() != null)
        {
            _tooltipPanel.Visible = false;
            return;
        }

        if (_tooltipLabel.Text != text)
        {
            _tooltipLabel.Text = text;
            _tooltipPanel.ResetSize();
        }

        Vector2 pointer = GetViewport().GetMousePosition();
        Vector2 screen = GetViewport().GetVisibleRect().Size;
        Vector2 size = _tooltipPanel.GetCombinedMinimumSize();
        Vector2 position = pointer + new Vector2(18f, 18f);
        if (position.X + size.X > screen.X)
        {
            position.X = pointer.X - size.X - 12f;
        }

        if (position.Y + size.Y > screen.Y)
        {
            position.Y = pointer.Y - size.Y - 12f;
        }

        _tooltipPanel.Position = new Vector2(Math.Max(0f, position.X), Math.Max(0f, position.Y));
        _tooltipPanel.Visible = true;
    }

    public void SetDate(string text)
    {
        if (_dateLabel.Text != text)
        {
            _dateLabel.Text = text;
        }
    }

    public override void _Process(double delta)
    {
        // Keep the legend in the bottom-right corner as its contents and the window change size.
        if (_legendPanel.Visible)
        {
            Vector2 screen = GetViewport().GetVisibleRect().Size;
            Vector2 size = _legendPanel.GetCombinedMinimumSize();
            _legendPanel.Size = size;
            _legendPanel.Position = screen - size - new Vector2(12f, 12f);
        }
    }

    /// <summary>Whole-world figures shown beside the date.</summary>
    public void SetWorldInfo(string text)
    {
        if (_worldLabel.Text != text)
        {
            _worldLabel.Text = text;
        }
    }

    /// <summary>Shows a key for the map in the bottom-right corner, or hides it when given no entries.</summary>
    public void SetLegend(string title, IReadOnlyList<(Color Colour, string Label)> entries)
    {
        foreach (Node child in _legendList.GetChildren())
        {
            _legendList.RemoveChild(child);
            child.QueueFree();
        }

        _legendList.AddChild(new Label { Text = title, MouseFilter = Control.MouseFilterEnum.Ignore });
        foreach ((Color colour, string label) in entries)
        {
            var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            line.AddThemeConstantOverride("separation", 8);
            line.AddChild(new ColorRect
            {
                Color = colour,
                CustomMinimumSize = new Vector2(18, 14),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            line.AddChild(new Label { Text = label, MouseFilter = Control.MouseFilterEnum.Ignore });
            _legendList.AddChild(line);
        }

        _legendPanel.Visible = entries.Count > 0;
    }

    public void SetStatus(string text) => _statusLabel.Text = text;

    public void SetInfo(string text)
    {
        if (_infoLabel.Text != text)
        {
            _infoLabel.Text = text;
        }
    }

    /// <summary>Shows a map mode as chosen without raising the event.</summary>
    public void ShowMapMode(int index) => _modeButton.Selected = index;

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
