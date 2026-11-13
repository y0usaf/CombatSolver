using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class SolverStatusPanel : PanelContainer
{
    private static readonly Color OnColor = new(0.55f, 1f, 0.6f, 1f);
    private static readonly Color OffColor = new(0.5f, 0.72f, 1f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private Label? _headline;
    private Label? _body;
    private Font? _font;
    private double _refreshTimer;

    public SolverStatusPanel()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Position = new Vector2(24f, 96f);
        Size = new Vector2(900f, 470f);
        Visible = false;
    }

    public override void _Ready() => BuildVisuals();

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

    public void AttachStyleSource(Control source, Font? font)
    {
        Theme = source.Theme;
        _font = font;
        ApplyFont();
    }

    public void Sync()
    {
        if (_headline == null || _body == null)
            return;
        SolverAutoStatusSnapshot status = SolverController.AutoStatus;
        string mode = status.Enabled ? SolverText.Get("开启") : SolverText.Get("关闭");
        string activity = CombatManager.Instance.IsInProgress ? Activity() : SolverText.Get("等待战斗");
        _headline.Text = status.Failed
            ? SolverText.Format($"自动模式：{mode}（故障） {activity}")
            : SolverText.Format($"自动模式：{mode} {activity}");
        _headline.Modulate = status.Failed
            ? ErrorColor
            : status.Enabled
                ? OnColor
                : OffColor;

        StringBuilder text = new();
        text.Append(SolverText.Get("上次决策")).Append(": ").AppendLine(status.LastDecision);
        if (status.Failed)
            text.Append(SolverText.Get("故障原因")).Append(": ").AppendLine(status.FailureReason);
        text.AppendLine();
        text.AppendLine(SolverText.Get("最近事件"));
        IReadOnlyList<SolverAutoEvent> events = status.Events;
        int first = Math.Max(0, events.Count - 10);
        for (int i = events.Count - 1; i >= first; i--)
        {
            SolverAutoEvent entry = events[i];
            string detail = entry.Detail.Length <= 180 ? entry.Detail : entry.Detail[..180] + "…";
            text.Append(entry.Time).Append(' ').Append(entry.Kind).Append(' ')
                .Append(detail).AppendLine();
        }
        _body.Text = text.ToString();
    }

    private static string Activity()
    {
        if (SolverController.IsDeploying)
            return SolverText.Get("部署中");
        if (SolverController.IsSearching)
            return SolverText.Get("搜索中");
        return SolverText.Get("空闲");
    }

    private void BuildVisuals()
    {
        if (_body != null)
            return;
        VBoxContainer column = new()
        {
            Name = "Column",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);
        _headline = new Label
        {
            Name = "Headline",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _headline.AddThemeFontSizeOverride("font_size", 20);
        column.AddChild(_headline);
        _body = new Label
        {
            Name = "Body",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _body.AddThemeFontSizeOverride("font_size", 14);
        column.AddChild(_body);
        ApplyFont();
    }

    private void ApplyFont()
    {
        if (_font == null)
            return;
        _headline?.AddThemeFontOverride("font", _font);
        _body?.AddThemeFontOverride("font", _font);
    }
}
