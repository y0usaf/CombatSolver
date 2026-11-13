using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static class SolverOverlay
{
    private const int OverlayLayer = 120;
    private const int SyncIntervalFrames = 15;
    private const int AttachIntervalFrames = 60;
    private static CanvasLayer? _layer;
    private static SolverAutoToggle? _toggle;
    private static SolverStatusPanel? _panel;
    private static Font? _font;
    private static int _framesUntilSync;
    private static int _framesUntilAttach;

    public static bool IsVisible
        => CombatManager.Instance.IsInProgress && IsToggleAlive;

    public static void ShowDisabled(NGame host)
    {
        EnsureLayer(host);
        RefreshControls();
    }

    public static void RefreshControls()
    {
        NGame? host = NGame.Instance;
        if (host == null)
        {
            Hide();
            return;
        }
        EnsureLayer(host);
        if (!IsToggleAlive)
            TryAttachTopBar(host);
        _toggle?.SyncState();
    }

    public static void ToggleAuto()
    {
        RefreshControls();
        _toggle?.ToggleFromHotkey();
    }

    public static void TogglePanel()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            return;
        _panel.Visible = !_panel.Visible;
        if (_panel.Visible)
            _panel.Sync();
    }

    public static void Hide()
    {
        if (_toggle != null && GodotObject.IsInstanceValid(_toggle))
            _toggle.QueueFree();
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _toggle = null;
        _panel = null;
        _layer = null;
    }

    internal static void Pump()
    {
        if (IsToggleAlive)
        {
            if (--_framesUntilSync > 0)
                return;
            _framesUntilSync = SyncIntervalFrames;
            _toggle!.SyncState();
            return;
        }
        if (--_framesUntilAttach > 0)
            return;
        _framesUntilAttach = AttachIntervalFrames;
        if (!RunManager.Instance.IsInProgress || NGame.Instance is not { } host)
            return;
        EnsureLayer(host);
        TryAttachTopBar(host);
    }

    private static bool IsToggleAlive
        => _toggle != null
           && GodotObject.IsInstanceValid(_toggle)
           && _toggle.IsInsideTree();

    private static void EnsureLayer(NGame host)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            return;
        _layer = new CanvasLayer { Name = "CombatSolverOverlay", Layer = OverlayLayer };
        _layer.AddChild(new SolverOverlayHost { Name = "Hotkeys" });
        _panel = new SolverStatusPanel { Name = "StatusPanel" };
        _layer.AddChild(_panel);
        host.AddChild(_layer);
        _framesUntilSync = SyncIntervalFrames;
        _framesUntilAttach = AttachIntervalFrames;
    }

    private static void TryAttachTopBar(NGame host)
    {
        if (FindTopBar(host) is not { } topBar)
            return;
        NTopBarMapButton? map = topBar.Map;
        if (map == null || !GodotObject.IsInstanceValid(map) || map.GetParent() is not { } parent)
            return;
        if (_toggle != null && GodotObject.IsInstanceValid(_toggle))
            _toggle.QueueFree();
        _font ??= ResolveFont(host, topBar);
        _toggle = new SolverAutoToggle { Name = "CombatSolverAutoToggle" };
        _toggle.AttachStyleSource(topBar, _font);
        parent.AddChild(_toggle);
        parent.MoveChild(_toggle, map.GetIndex());
        if (parent is not Container)
        {
            _toggle.Position = new Vector2(
                map.Position.X - _toggle.CustomMinimumSize.X - 6f,
                map.Position.Y);
        }
        _panel?.AttachStyleSource(topBar, _font);
        _toggle.SyncState();
    }

    private static NTopBar? FindTopBar(Node root)
    {
        if (root is NTopBar topBar)
            return topBar;
        foreach (Node child in root.GetChildren())
        {
            if (FindTopBar(child) is { } found)
                return found;
        }
        return null;
    }

    private static NEndTurnButton? FindEndTurnButton(Node root)
    {
        if (root is NEndTurnButton match)
            return match;
        foreach (Node child in root.GetChildren())
        {
            if (FindEndTurnButton(child) is { } found)
                return found;
        }
        return null;
    }

    private static Font ResolveFont(NGame host, Control topBar)
        => (FindEndTurnButton(host)?.GetNodeOrNull<Label>("Visuals/Label")?.GetThemeFont("font"))
           ?? topBar.GetThemeFont("font", "Label")
           ?? ThemeDB.FallbackFont;
}
