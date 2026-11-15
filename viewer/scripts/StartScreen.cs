using System;
using System.Globalization;
using Godot;
using Sim.Core.Engine;

namespace Viewer;

/// <summary>The screen for starting a new run: seed, crop homelands, start or load a save.</summary>
public partial class StartScreen : CanvasLayer
{
    private LineEdit _seed = null!;
    private OptionButton _homelands = null!;
    private Label _error = null!;
    private Button _cancel = null!;

    /// <summary>Raised with the chosen settings when Start is pressed and the seed is valid.</summary>
    public event Action<RunSettings>? StartRequested;

    public event Action? LoadRequested;

    public event Action? CancelRequested;

    public override void _Ready()
    {
        Layer = 10;

        // Dims the map and keeps the mouse off it while the screen is open.
        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 24);
        }

        panel.AddChild(margin);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        box.AddThemeConstantOverride("separation", 14);
        margin.AddChild(box);

        var title = new Label { Text = "New run" };
        title.AddThemeFontSizeOverride("font_size", 26);
        box.AddChild(title);

        box.AddChild(WrappedLabel(
            "Every run starts in January 10,000 BCE, with foragers spread across the continents. "
            + "The same seed and choices always give the same history."));

        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        seedRow.AddChild(new Label { Text = "Seed", CustomMinimumSize = new Vector2(150, 0) });
        _seed = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "A whole number" };
        _seed.TextSubmitted += _ => OnStartPressed();
        seedRow.AddChild(_seed);
        var newSeed = new Button { Text = "New seed" };
        newSeed.Pressed += () => _seed.Text = SuggestSeed().ToString(CultureInfo.InvariantCulture);
        seedRow.AddChild(newSeed);
        box.AddChild(seedRow);

        var homelandsRow = new HBoxContainer();
        homelandsRow.AddThemeConstantOverride("separation", 8);
        homelandsRow.AddChild(new Label { Text = "Crop homelands", CustomMinimumSize = new Vector2(150, 0) });
        _homelands = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _homelands.AddItem("Real", (int)CropHomelands.Real);
        _homelands.AddItem("Random", (int)CropHomelands.Random);
        homelandsRow.AddChild(_homelands);
        box.AddChild(homelandsRow);

        Label hint = WrappedLabel(
            "Real: each crop, such as wheat, rice or maize, can only be first farmed where it grew wild on Earth. "
            + "Random: each crop starts in a random region whose climate suits it.");
        hint.Modulate = new Color(1f, 1f, 1f, 0.75f);
        box.AddChild(hint);

        _error = new Label { Text = "", Modulate = new Color(1f, 0.55f, 0.5f), Visible = false };
        box.AddChild(_error);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        _cancel = new Button { Text = "Cancel", Visible = false };
        _cancel.Pressed += () => CancelRequested?.Invoke();
        buttons.AddChild(_cancel);
        var load = new Button { Text = "Load a save..." };
        load.Pressed += () => LoadRequested?.Invoke();
        buttons.AddChild(load);
        var start = new Button { Text = "Start" };
        start.Pressed += OnStartPressed;
        buttons.AddChild(start);
        box.AddChild(buttons);

        Visible = false;
    }

    public static ulong SuggestSeed() => (ulong)System.Random.Shared.NextInt64(1, 1_000_000_000);

    /// <summary>Shows the screen. Cancel is offered only when there is a run to go back to.</summary>
    public void Open(bool canCancel)
    {
        _seed.Text = SuggestSeed().ToString(CultureInfo.InvariantCulture);
        _cancel.Visible = canCancel;
        ShowError(null);
        Visible = true;
    }

    public void Close() => Visible = false;

    public void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visible = !string.IsNullOrEmpty(text);
    }

    private void OnStartPressed()
    {
        if (!ulong.TryParse(_seed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
        {
            ShowError("The seed must be a whole number, with no other characters.");
            return;
        }

        var homelands = (CropHomelands)_homelands.GetSelectedId();
        StartRequested?.Invoke(new RunSettings { Seed = seed, CropHomelands = homelands });
    }

    private static Label WrappedLabel(string text) => new Label
    {
        Text = text,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(520, 0),
    };
}
