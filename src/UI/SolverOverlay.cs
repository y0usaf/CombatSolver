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
    private static SolverCoopModeButton? _coopMode;
    private static SolverExecuteTurnButton? _executeTurn;
    private static SolverStatusPanel? _panel;
    private static SolverQuickMenu? _menu;
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
        if (IsLive(_toggle))
            _toggle!.SyncState();
        if (IsLive(_coopMode))
            _coopMode!.SyncState();
        if (IsLive(_executeTurn))
            _executeTurn!.SyncState();
        if (QuickMenuVisible)
            _menu!.Sync();
    }

    private static bool IsLive(GodotObject? node)
        => node != null && GodotObject.IsInstanceValid(node);

    /// <summary>Flips the co-op Auto pace between Free play and Wait and stores the choice.</summary>
    public static void CycleCoopPlayMode()
        => SolverController.SetCoopPlayMode(
            SolverController.CoopFreePlayEnabled ? SolverCoopPlayMode.Wait : SolverCoopPlayMode.FreePlay);

    public static bool QuickMenuVisible
        => _menu != null && GodotObject.IsInstanceValid(_menu) && _menu.Visible;

    public static void ToggleQuickMenu()
    {
        if (_menu != null && GodotObject.IsInstanceValid(_menu))
            _menu.Toggle();
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
        if (_executeTurn != null && GodotObject.IsInstanceValid(_executeTurn))
            _executeTurn.QueueFree();
        if (_coopMode != null && GodotObject.IsInstanceValid(_coopMode))
            _coopMode.QueueFree();
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _toggle = null;
        _coopMode = null;
        _executeTurn = null;
        _panel = null;
        _menu = null;
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
            _coopMode?.SyncState();
            _executeTurn?.SyncState();
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
        _menu = new SolverQuickMenu { Name = "QuickMenu" };
        _layer.AddChild(_menu);
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
        if (_coopMode != null && GodotObject.IsInstanceValid(_coopMode))
            _coopMode.QueueFree();
        if (_executeTurn != null && GodotObject.IsInstanceValid(_executeTurn))
            _executeTurn.QueueFree();
        _coopMode = new SolverCoopModeButton { Name = "CombatSolverCoopMode" };
        _coopMode.AttachStyleSource(topBar, _font);
        parent.AddChild(_coopMode);
        parent.MoveChild(_coopMode, map.GetIndex());
        _executeTurn = new SolverExecuteTurnButton { Name = "CombatSolverExecuteTurn" };
        _executeTurn.AttachStyleSource(topBar, _font);
        parent.AddChild(_executeTurn);
        parent.MoveChild(_executeTurn, map.GetIndex());
        _toggle = new SolverAutoToggle { Name = "CombatSolverAutoToggle" };
        _toggle.AttachStyleSource(topBar, _font);
        parent.AddChild(_toggle);
        parent.MoveChild(_toggle, map.GetIndex());
        if (parent is not Container)
        {
            _toggle.Position = new Vector2(
                map.Position.X - _toggle.CustomMinimumSize.X - 6f,
                map.Position.Y);
            _executeTurn.Position = new Vector2(
                _toggle.Position.X - _executeTurn.CustomMinimumSize.X - 6f,
                map.Position.Y);
            _coopMode.Position = new Vector2(
                _executeTurn.Position.X - _coopMode.CustomMinimumSize.X - 6f,
                map.Position.Y);
        }
        _panel?.AttachStyleSource(topBar, _font);
        _menu?.AttachStyleSource(topBar, _font);
        _toggle.SyncState();
        _coopMode.SyncState();
        _executeTurn.SyncState();
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
