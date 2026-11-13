using Godot;

namespace CombatSolver;

internal sealed partial class SolverOverlayHost : Node
{
    public override void _Ready()
        => ProcessMode = ProcessModeEnum.Always;

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } key)
            return;
        switch (key.Keycode)
        {
            case Key.F9:
                SolverOverlay.ToggleAuto();
                break;
            case Key.F10:
                SolverOverlay.TogglePanel();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
        => SolverOverlay.Pump();
}
