using Godot;

namespace CombatSolver;

internal sealed partial class SolverQuickMenu : PanelContainer
{
    private const int AutoRow = 0;
    private const int FreePlayRow = 1;
    private const int RunTurnRow = 2;
    private const int SpeedRow = 3;
    private const int RowCount = 4;
    private static readonly Color OnColor = new(0.55f, 1f, 0.6f, 1f);
    private static readonly Color OffColor = new(0.5f, 0.72f, 1f, 1f);
    private static readonly Color ActionColor = new(1f, 0.82f, 0.45f, 1f);
    private static readonly Color DisabledColor = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly SolverDeploymentFastMode[] SpeedCycle =
    [
        SolverDeploymentFastMode.FollowGame,
        SolverDeploymentFastMode.Normal,
        SolverDeploymentFastMode.Fast,
        SolverDeploymentFastMode.Instant,
    ];
    private readonly Button[] _rows = new Button[RowCount];
    private Label? _title;
    private Label? _hint;
    private Font? _font;
    private StyleBoxFlat? _selectedStyle;
    private StyleBoxFlat? _idleStyle;
    private int _selected;
    private double _refreshTimer;

    public SolverQuickMenu()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        CustomMinimumSize = new Vector2(340f, 0f);
    }

    public override void _Ready()
    {
        BuildVisuals();
        SetAnchorsPreset(LayoutPreset.TopRight);
        GrowHorizontal = GrowDirection.Begin;
        GrowVertical = GrowDirection.End;
        OffsetRight = -24f;
        OffsetLeft = -24f - CustomMinimumSize.X;
        OffsetTop = 96f;
        OffsetBottom = 96f;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        _refreshTimer -= delta;
        if (_refreshTimer > 0d)
            return;
        _refreshTimer = 0.25d;
        Sync();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!Visible || inputEvent is not InputEventKey { Pressed: true } key
            || key.CtrlPressed || key.AltPressed || key.MetaPressed)
            return;
        switch (key.Keycode)
        {
            case Key.Up:
                Select(_selected - 1);
                break;
            case Key.Down:
                Select(_selected + 1);
                break;
            case Key.Left when _selected == SpeedRow:
                CycleSpeed(-1);
                break;
            case Key.Right when _selected == SpeedRow:
                CycleSpeed(1);
                break;
            case Key.Enter or Key.KpEnter or Key.Space when !key.Echo:
                Activate(_selected);
                break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4 when !key.Echo:
                Select((int)(key.Keycode - Key.Key1));
                Activate(_selected);
                break;
            case Key.Escape when !key.Echo:
                Close();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public void AttachStyleSource(Control source, Font? font)
    {
        Theme = source.Theme;
        _font = font;
        ApplyFont();
    }

    public void Toggle()
    {
        if (Visible)
        {
            Close();
            return;
        }
        Visible = true;
        Select(AutoRow);
        Sync();
        Entry.Logger.Info("[CombatSolver/Test] QUICK_MENU visible=true");
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        Entry.Logger.Info("[CombatSolver/Test] QUICK_MENU visible=false");
    }

    public void Sync()
    {
        if (_rows[AutoRow] == null)
            return;
        bool disabled = SolverController.SolverDisabled;
        bool auto = SolverAutoToggle.IsOn();
        SetRow(AutoRow, SolverText.Format($"自动模式：{OnOff(auto)}"), auto ? OnColor : OffColor, disabled);
        bool freePlay = SolverController.CoopFreePlayEnabled;
        SetRow(FreePlayRow,
            SolverText.Format($"自由行动：{(freePlay ? SolverText.Get("开启") : SolverText.Get("关闭（等待队友）"))}"),
            freePlay ? OnColor : OffColor, disabled);
        SetRow(RunTurnRow, SolverText.Get("执行本回合"), ActionColor,
            disabled || !SolverExecuteTurnButton.IsReady());
        SolverDeploymentFastMode speed = SolverSettings.Current.DeploymentFastMode;
        SetRow(SpeedRow, SolverText.Format($"速度：{SpeedName(speed)}"),
            speed == SolverDeploymentFastMode.FollowGame ? OffColor : OnColor, disabled);
        for (int i = 0; i < RowCount; i++)
            _rows[i].AddThemeStyleboxOverride("normal", i == _selected ? _selectedStyle! : _idleStyle!);
    }

    private static string OnOff(bool on) => SolverText.Get(on ? "开启" : "关闭");

    private static string SpeedName(SolverDeploymentFastMode mode)
    {
        string name = SolverText.Get(mode switch
        {
            SolverDeploymentFastMode.FollowGame => "跟随游戏",
            SolverDeploymentFastMode.Normal => "普通",
            SolverDeploymentFastMode.Fast => "快速",
            SolverDeploymentFastMode.Instant => "瞬间",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        });
        return mode == SolverDeploymentFastMode.Instant && SolverController.IsMultiplayerSession
            ? SolverText.Format($"{name}（联机不生效）")
            : name;
    }

    private void SetRow(int row, string text, Color color, bool disabled)
    {
        Button button = _rows[row];
        button.Text = $"{row + 1}  {text}";
        button.Disabled = disabled;
        button.AddThemeColorOverride("font_color", disabled ? DisabledColor : color);
        button.AddThemeColorOverride("font_hover_color", disabled ? DisabledColor : color);
        button.AddThemeColorOverride("font_pressed_color", color);
        button.AddThemeColorOverride("font_focus_color", color);
        button.AddThemeColorOverride("font_disabled_color", DisabledColor);
    }

    private void Select(int row)
    {
        _selected = (row % RowCount + RowCount) % RowCount;
        Sync();
    }

    private void Activate(int row)
    {
        if (SolverController.SolverDisabled || _rows[row].Disabled)
            return;
        switch (row)
        {
            case AutoRow:
                SolverAutoToggle.SetAuto(!SolverAutoToggle.IsOn());
                break;
            case FreePlayRow:
                SolverOverlay.CycleCoopPlayMode();
                break;
            case RunTurnRow:
                Close();
                SolverController.ExecuteSingleTurn();
                SolverOverlay.RefreshControls();
                return;
            case SpeedRow:
                CycleSpeed(1);
                return;
        }
        Sync();
    }

    private void CycleSpeed(int step)
    {
        if (SolverController.SolverDisabled)
            return;
        int current = Array.IndexOf(SpeedCycle, SolverSettings.Current.DeploymentFastMode);
        SolverController.SetDeploymentFastMode(SpeedCycle[(current + step + SpeedCycle.Length) % SpeedCycle.Length]);
        Sync();
    }

    private void BuildVisuals()
    {
        if (_title != null)
            return;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.1f, 0.94f),
            BorderColor = new Color(1f, 0.82f, 0.45f, 0.55f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12,
            ContentMarginTop = 10,
            ContentMarginRight = 12,
            ContentMarginBottom = 10,
        });
        _selectedStyle = new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.14f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        _idleStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        VBoxContainer column = new() { Name = "Column", MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 2);
        AddChild(column);
        _title = new Label { Name = "Title", Text = "CombatSolver", MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontSizeOverride("font_size", 16);
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.45f, 1f));
        column.AddChild(_title);
        for (int i = 0; i < RowCount; i++)
        {
            int row = i;
            Button button = new()
            {
                Name = $"Row{row + 1}",
                FocusMode = FocusModeEnum.None,
                Alignment = HorizontalAlignment.Left,
                MouseDefaultCursorShape = CursorShape.PointingHand,
            };
            button.AddThemeFontSizeOverride("font_size", 18);
            button.AddThemeStyleboxOverride("hover", _selectedStyle);
            button.AddThemeStyleboxOverride("pressed", _selectedStyle);
            button.AddThemeStyleboxOverride("disabled", _idleStyle);
            button.MouseEntered += () => Select(row);
            button.Pressed += () => Activate(row);
            if (row == SpeedRow)
                button.GuiInput += inputEvent => OnSpeedGuiInput(inputEvent);
            _rows[row] = button;
            column.AddChild(button);
        }
        _hint = new Label
        {
            Name = "Hint",
            Text = SolverText.Get("方向键选择，回车或数字键执行，左右键调速度，Esc 关闭"),
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f, 1f));
        column.AddChild(_hint);
        ApplyFont();
        Sync();
    }

    private void OnSpeedGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            return;
        CycleSpeed(-1);
        _rows[SpeedRow].AcceptEvent();
    }

    private void ApplyFont()
    {
        if (_font == null || _title == null)
            return;
        _title.AddThemeFontOverride("font", _font);
        _hint?.AddThemeFontOverride("font", _font);
        foreach (Button button in _rows)
            button?.AddThemeFontOverride("font", _font);
    }
}
