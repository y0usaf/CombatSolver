using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class SolverAutoToggle : Button
{
    private const string AutoOnLabel = "自动：开";
    private const string AutoOffLabel = "自动：关";
    private static readonly Vector2 MinimumSize = new(112f, 32f);
    private static readonly Color OffModulate = new(0.62f, 0.66f, 0.72f, 0.92f);
    private static readonly Color OnModulate = Colors.White;

    private Control? _anchor;
    private bool _appliedAnchorTheme;
    private bool _hasAnchorRect;
    private Rect2 _lastAnchorRect;
    private Vector2 _lastViewportSize;

    public SolverAutoToggle()
    {
        ToggleMode = true;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = MinimumSize;
        Size = MinimumSize;
        AddThemeFontSizeOverride("font_size", 15);
        AddThemeConstantOverride("outline_size", 3);
        AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        Pressed += OnPressed;
    }

    public bool HasAnchor => _anchor is { } anchor && GodotObject.IsInstanceValid(anchor);

    public void ApplyAnchorTheme(Control anchor)
    {
        _anchor = anchor;
        if (_appliedAnchorTheme)
            return;
        _appliedAnchorTheme = true;
        Theme = anchor.Theme;
        AddThemeFontSizeOverride(
            "font_size", Math.Clamp(anchor.GetThemeFontSize("font_size"), 12, 18));
        AddThemeConstantOverride("outline_size", anchor.GetThemeConstant("outline_size"));
        AddThemeColorOverride("font_outline_color", anchor.GetThemeColor("font_outline_color"));
    }

    public void SyncState()
    {
        SetPressedNoSignal(SolverController.FullAutoEnabled);
        Disabled = SolverController.SolverDisabled || SolverController.IsMultiplayerSession;
        Text = SolverText.Get(ButtonPressed ? AutoOnLabel : AutoOffLabel);
        Modulate = ButtonPressed ? OnModulate : OffModulate;
        Position = ResolvePosition(GetViewportRect().Size, force: true);
    }

    public override void _Process(double delta)
        => Position = ResolvePosition(GetViewportRect().Size, force: false);

    private Vector2 ResolvePosition(Vector2 viewportSize, bool force)
    {
        Vector2 target;
        if (_anchor is { } anchor && GodotObject.IsInstanceValid(anchor))
        {
            Rect2 rect = anchor.GetGlobalRect();
            if (rect.Size.X > 0f && rect.Size.Y > 0f)
            {
                target = new Vector2(rect.Position.X + rect.Size.X - Size.X, rect.Position.Y - Size.Y - 10f);
                if (!force && rect == _lastAnchorRect)
                    return Position;
                _lastAnchorRect = rect;
                _hasAnchorRect = true;
                return target;
            }
            if (_hasAnchorRect || !force)
                return Position;
            target = FallbackPosition(viewportSize);
        }
        else
        {
            if (!force && viewportSize == _lastViewportSize)
                return Position;
            target = FallbackPosition(viewportSize);
        }
        _lastViewportSize = viewportSize;
        return target;
    }

    private Vector2 FallbackPosition(Vector2 viewportSize)
        => new(viewportSize.X - Size.X - 24f, viewportSize.Y - Size.Y - 136f);

    private void OnPressed()
    {
        if (NGame.Instance is not { } host)
            return;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
            return;
        SolverController.SetFullAuto(host, state, ButtonPressed);
        SolverSettings.Update(SolverSettings.Current with { AutoPlay = SolverController.FullAutoEnabled });
        SyncState();
    }
}
