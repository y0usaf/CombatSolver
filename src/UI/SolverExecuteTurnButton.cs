using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

/// <summary>
/// One-shot version of Auto: solve and play the current turn now, then stop.
/// In co-op this is also the way out when every player is waiting for someone else.
/// </summary>
internal sealed partial class SolverExecuteTurnButton : Button
{
    private const string LabelSource = "执行本回合";
    private const string PlatePath = "res://images/packed/combat_ui/end_turn_button.png";
    private static readonly Vector2 MinimumSize = new(150f, 56f);
    private static readonly Color ReadyTint = new(1f, 0.78f, 0.35f, 1f);
    private static readonly Color BusyTint = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Color LabelColor = new(1f, 0.97f, 0.9f, 1f);
    private TextureRect? _plate;
    private Label? _label;
    private Font? _font;

    public SolverExecuteTurnButton()
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
        bool ready = IsReady();
        Disabled = SolverController.SolverDisabled || !ready;
        if (_plate != null)
            _plate.Modulate = ready ? ReadyTint : BusyTint;
        if (_label != null)
        {
            _label.Text = SolverText.Get(LabelSource);
            _label.Modulate = Disabled ? new Color(0.7f, 0.7f, 0.7f, 1f) : Colors.White;
        }
        TooltipText = SolverText.Get("Ctrl+F8 立即求解并执行当前回合，然后停止自动模式");
    }

    private static bool IsReady()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        return CombatManager.Instance.IsInProgress
               && state != null
               && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
               && !SolverController.IsDeploying
               && !SolverController.IsApplyingCurrentTurn;
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
            Text = SolverText.Get(LabelSource),
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
        SolverController.ExecuteSingleTurn();
        SyncState();
    }
}
