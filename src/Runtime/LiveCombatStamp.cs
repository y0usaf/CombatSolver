using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

/// <summary>
/// 搜索开始时的完整可见状态文本，用于拒绝已经过期的后台结果；不做摘要或哈希。
/// </summary>
internal sealed record LiveCombatStamp(string StateText)
{
    public static LiveCombatStamp Capture(CombatState state)
        => new(ContinuationStamp.CaptureLive(state).StateText + TeammateReadiness(state));

    public static LiveCombatStamp FromContinuation(ContinuationStamp continuation, CombatState state)
        => new(continuation.StateText + TeammateReadiness(state));

    private static string TeammateReadiness(CombatState state)
    {
        if (state.Players.Count <= 1 || !CombatManager.Instance.IsInProgress)
            return "";
        Player? me = LocalContext.GetMe(state);
        StringBuilder text = new(";coop_ready=");
        foreach (Player player in state.Players.OrderBy(player => player.NetId))
        {
            if (ReferenceEquals(player, me))
                continue;
            bool ready = CombatManager.Instance.IsPlayerReadyToEndTurn(player);
            text.Append(player.NetId).Append(':').Append(ready ? '1' : '0');
            if (!ready && player.PlayerCombatState is { } open)
            {
                text.Append(":e=").Append(open.Energy).Append(":s=").Append(open.Stars);
                foreach (CardModel card in open.Hand.Cards)
                {
                    if (card is Mimic or BodySlam)
                    {
                        text.Append(':').Append(card.Id.Entry).Append('+').Append(card.CurrentUpgradeLevel)
                            .Append('@').Append(card.EnergyCost.GetWithModifiers(CostModifiers.All));
                    }
                }
            }
            text.Append(',');
        }
        return text.ToString();
    }
}
