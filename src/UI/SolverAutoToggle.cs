using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class SolverAutoToggle : Button
{
    private const string LabelSource = "自动模式";
    private const string PlatePath = "res://images/packed/combat_ui/end_turn_button.png";
    private static readonly Vector2 MinimumSize = new(176f, 56f);
    private static readonly Color OffTint = new(0.42f, 0.68f, 1f, 1f);
    private static readonly Color OnTint = new(0.52f, 1f, 0.58f, 1f);
    private static readonly Color ErrorTint = new(1f, 0.42f, 0.42f, 1f);
    private static readonly Color LabelColor = new(1f, 0.97f, 0.9f, 1f);
    private TextureRect? _plate;
    private Label? _label;
    private Font? _font;

    public SolverAutoToggle()
    {
        ToggleMode = true;
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

    public void ToggleFromHotkey()
        => Apply(NGame.Instance, CombatManager.Instance.DebugOnlyGetState(), !IsOn());

    public void SyncState()
    {
        bool on = IsOn();
        SolverAutoStatusSnapshot status = SolverController.AutoStatus;
        SetPressedNoSignal(on);
        Disabled = SolverController.SolverDisabled || SolverController.IsMultiplayerSession;
        if (_plate != null)
            _plate.Modulate = !on ? OffTint : status.Failed ? ErrorTint : OnTint;
        if (_label != null)
        {
            _label.Text = SolverText.Get(LabelSource);
            _label.Modulate = Disabled ? new Color(0.7f, 0.7f, 0.7f, 1f) : Colors.White;
        }
        TooltipText = status.Failed
            ? SolverText.Format($"自动模式出错：{status.FailureReason}")
            : SolverText.Get("Ctrl+F9 切换自动模式，Ctrl+F10 查看求解器状态");
    }

    private bool IsOn()
        => CombatManager.Instance.IsInProgress
            ? SolverController.FullAutoEnabled
            : SolverController.AutoRequested;

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
        => Apply(NGame.Instance, CombatManager.Instance.DebugOnlyGetState(), ButtonPressed);

    private void Apply(NGame? host, CombatState? state, bool enabled)
    {
        if (SolverController.SolverDisabled || SolverController.IsMultiplayerSession)
        {
            SyncState();
            return;
        }
        if (host != null && state != null && CombatManager.Instance.IsInProgress)
            SolverController.SetFullAuto(host, state, enabled);
        SolverSettings.Update(SolverSettings.Current with { AutoPlay = enabled });
        SolverOverlay.RefreshControls();
        SyncState();
    }
}
