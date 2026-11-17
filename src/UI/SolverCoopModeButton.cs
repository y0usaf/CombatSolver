using Godot;
using MegaCrit.Sts2.Core.Assets;

namespace CombatSolver;

/// <summary>
/// Co-op Auto pace control: Free play acts at the solver's own pace, Wait is the old
/// "everybody else first" pace. The choice is stored in the player's settings.
/// </summary>
internal sealed partial class SolverCoopModeButton : Button
{
    private const string FreePlaySource = "自由行动";
    private const string WaitSource = "等待队友";
    private const string PlatePath = "res://images/packed/combat_ui/end_turn_button.png";
    private static readonly Vector2 MinimumSize = new(150f, 56f);
    private static readonly Color FreePlayTint = new(0.52f, 1f, 0.58f, 1f);
    private static readonly Color WaitTint = new(0.42f, 0.68f, 1f, 1f);
    private static readonly Color LabelColor = new(1f, 0.97f, 0.9f, 1f);
    private TextureRect? _plate;
    private Label? _label;
    private Font? _font;

    public SolverCoopModeButton()
    {
        Flat = true;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = MinimumSize;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        Pressed += OnPressed;
    }

    public override void _Ready()
    {
        BuildVisuals();
        SyncState();
    }

    public void AttachStyleSource(Control source, Font? font)
    {
        Theme = source.Theme;
        _font = font;
        ApplyFont();
    }

    public void SyncState()
    {
        bool freePlay = SolverController.CoopFreePlayEnabled;
        Disabled = SolverController.SolverDisabled;
        if (_plate != null)
            _plate.Modulate = freePlay ? FreePlayTint : WaitTint;
        if (_label != null)
        {
            _label.Text = SolverText.Get(freePlay ? FreePlaySource : WaitSource);
            _label.Modulate = Disabled ? new Color(0.7f, 0.7f, 0.7f, 1f) : Colors.White;
        }
        TooltipText = SolverText.Get(
            freePlay ? "Ctrl+F11 切换自动模式节奏：当前自由行动，立即出牌，不等队友"
                     : "Ctrl+F11 切换自动模式节奏：当前等待队友，队友结束回合后才出牌");
    }

    private void BuildVisuals()
    {
        if (_plate != null)
            return;
        _plate = new TextureRect
        {
            Name = "Plate",
            Texture = PreloadManager.Cache.GetTexture2D(PlatePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _plate.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_plate);
        _label = new Label
        {
            Name = "Label",
            Text = SolverText.Get(FreePlaySource),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.AddThemeFontSizeOverride("font_size", 15);
        _label.AddThemeConstantOverride("outline_size", 6);
        _label.AddThemeColorOverride("font_color", LabelColor);
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        AddChild(_label);
        ApplyFont();
    }

    private void ApplyFont()
    {
        if (_font == null || _label == null)
            return;
        _label.AddThemeFontOverride("font", _font);
    }

    private void OnPressed()
    {
        SolverOverlay.CycleCoopPlayMode();
        SyncState();
    }
}
