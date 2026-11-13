using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CombatSolver;

internal static class SolverOverlay
{
    private const int OverlayLayer = 120;
    private static CanvasLayer? _layer;
    private static SolverAutoToggle? _toggle;

    public static bool IsVisible
        => _toggle != null && GodotObject.IsInstanceValid(_toggle) && _toggle.Visible;

    public static void ShowDisabled(NGame host)
    {
        EnsureCreated(host);
        _toggle!.SyncState();
    }

    public static void RefreshControls()
    {
        NGame? host = NGame.Instance;
        if (host == null || !CombatManager.Instance.IsInProgress)
        {
            Hide();
            return;
        }
        EnsureCreated(host);
        _toggle!.SyncState();
    }

    public static void Hide()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _layer = null;
        _toggle = null;
    }

    private static void EnsureCreated(NGame host)
    {
        if (_layer != null
            && GodotObject.IsInstanceValid(_layer)
            && _toggle != null
            && GodotObject.IsInstanceValid(_toggle))
        {
            if (!_toggle.HasAnchor && FindEndTurnButton(host) is { } lateAnchor)
                _toggle.ApplyAnchorTheme(lateAnchor);
            return;
        }
        Hide();
        _toggle = new SolverAutoToggle { Name = "AutoToggle" };
        if (FindEndTurnButton(host) is { } anchor)
            _toggle.ApplyAnchorTheme(anchor);
        _layer = new CanvasLayer { Name = "CombatSolverAutoToggle", Layer = OverlayLayer };
        _layer.AddChild(_toggle);
        host.AddChild(_layer);
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
}
