using Godot;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private OptionButton _solverSpeed = null!;

    private Control CreatePerformancePage()
    {
        VBoxContainer content = CreatePageContent("PerformanceSettingsPage");
        GridContainer speedGrid = CreateSettingsGrid();
        _solverSpeed = CreateOptionInput(260);
        _solverSpeed.AddItem(SolverText.Get("快速"), (int)SolverSpeed.Fast);
        _solverSpeed.AddItem(SolverText.Get("标准（默认）"), (int)SolverSpeed.Normal);
        _solverSpeed.AddItem(SolverText.Get("深入"), (int)SolverSpeed.Thorough);
        _solverSpeed.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverSettings.Update(SolverSettings.Current with { Speed = (SolverSpeed)_solverSpeed.GetItemId((int)index) });
            SetStatus(SolverText.Get("计算速度已保存，下次计算生效"), SolverUiTokens.Palette.Success);
        };
        AddBasicRow(
            speedGrid,
            SolverText.Get("计算速度"),
            _solverSpeed,
            SolverText.Get("每次决策抽取的隐藏信息样本数与每个样本的搜索量。快速用时最短；深入更慢，但比较更多候选。其余参数按此档位和局面自动调整。"));
        AddSettingsSection(content, SolverText.Get("计算速度"),
            SolverText.Get("求解器只有这一个计算量设置，下次计算生效。"), speedGrid);
        return CreatePageScroll(content);
    }

    private void ReloadPerformancePage(SolverSettingsData data)
        => _solverSpeed.Selected = _solverSpeed.GetItemIndex((int)data.Speed);
}
