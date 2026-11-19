using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace CombatSolver.NewSolver;

internal static class StateHash
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Compute(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player localPlayer,
        int knownTopRemaining)
    {
        ulong h = FnvOffset;
        Mix(ref h, (ulong)combat.RoundNumber);
        Mix(ref h, (ulong)combat.GetPlayerTurnNumber(localPlayer));
        Mix(ref h, (ulong)knownTopRemaining);
        Mix(ref h, (ulong)simulator.ShuffleEventCount);

        SimPlayerCombatState localState = simulator.State.GetPlayerCombatState(localPlayer);
        Mix(ref h, (ulong)localState.Energy);
        Mix(ref h, (ulong)localState.Stars);
        Mix(ref h, (ulong)localState.DrawPile.Cards.Count);
        Mix(ref h, (ulong)localState.DiscardPile.Cards.Count);
        Mix(ref h, (ulong)localState.ExhaustPile.Cards.Count);
        Mix(ref h, (ulong)combat.GetCardsPlayedThisTurn(localPlayer.Creature));

        ulong handXor = 0;
        ulong handSum = 0;
        foreach (PredictedCard card in localState.Hand.Cards)
        {
            ulong ch = CardHash(simulator, localState, card);
            handXor ^= ch;
            handSum += ch;
        }
        Mix(ref h, handXor);
        Mix(ref h, handSum);

        int topCount = Math.Min(knownTopRemaining, localState.DrawPile.Cards.Count);
        for (int i = 0; i < topCount; i++)
            Mix(ref h, CardHash(simulator, localState, localState.DrawPile.Cards[i]));

        foreach (OrbModel orb in localState.OrbQueue.Orbs)
        {
            MixString(ref h, orb.Id.Entry);
            Mix(ref h, (ulong)(int)OrbMirrors.GetPassiveValue(simulator, orb));
            Mix(ref h, (ulong)(int)OrbMirrors.GetEvokeValue(simulator, orb));
        }

        for (int slot = 0; slot < localPlayer.PotionSlots.Count; slot++)
        {
            if (combat.IsPotionAvailable(localPlayer, slot) && combat.GetPotionAtSlot(localPlayer, slot) is { } potion)
            {
                Mix(ref h, (ulong)(slot + 1));
                MixString(ref h, potion.Id.Entry);
            }
        }

        foreach (Player p in combat.Players)
        {
            Creature c = p.Creature;
            SimCreatureState cs = simulator.State.GetCreature(c);
            Mix(ref h, c.CombatId ?? 0);
            Mix(ref h, (ulong)cs.CurrentHp);
            Mix(ref h, (ulong)cs.MaxHp);
            Mix(ref h, (ulong)cs.Block);
            if (!ReferenceEquals(p, localPlayer))
            {
                SimPlayerCombatState ps = simulator.State.GetPlayerCombatState(p);
                Mix(ref h, (ulong)ps.Energy);
                Mix(ref h, (ulong)ps.Hand.Cards.Count);
                Mix(ref h, (ulong)ps.DrawPile.Cards.Count);
                Mix(ref h, (ulong)ps.DiscardPile.Cards.Count);
            }
            if (combat.GetOsty(p) is { } osty)
            {
                SimCreatureState os = simulator.State.GetCreature(osty);
                Mix(ref h, 0x0571UL);
                Mix(ref h, (ulong)os.CurrentHp);
                Mix(ref h, (ulong)combat.GetOstyMaxHp(simulator, p));
                Mix(ref h, (ulong)os.Block);
            }
        }

        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = simulator.State.GetCreature(enemy);
            Mix(ref h, enemy.CombatId ?? 0);
            Mix(ref h, (ulong)es.CurrentHp);
            Mix(ref h, (ulong)es.Block);
            Mix(ref h, es.IsAlive ? 1UL : 0UL);
        }

        ulong powerXor = 0;
        ulong powerSum = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (power.Amount == 0)
                continue;
            ulong ph = FnvOffset;
            Mix(ref ph, power.Owner?.CombatId ?? 0);
            MixString(ref ph, power.Id.Entry);
            Mix(ref ph, (ulong)power.Amount);
            powerXor ^= ph;
            powerSum += ph;
        }
        Mix(ref h, powerXor);
        Mix(ref h, powerSum);

        return h;
    }

    private static ulong CardHash(
        CombatPredictionSimulator simulator,
        SimPlayerCombatState state,
        PredictedCard card)
    {
        ulong h = FnvOffset;
        MixString(ref h, card.Preview.Id.Entry);
        Mix(ref h, (ulong)card.Preview.CurrentUpgradeLevel);
        Mix(ref h, (ulong)card.GetEnergyCostWithModifiers(simulator, state));
        Mix(ref h, (ulong)card.GetStarCostWithModifiers(simulator, state));
        Mix(ref h, card.Preview.IsDupe ? 1UL : 0UL);
        Mix(ref h, (ulong)card.Preview.BaseReplayCount);
        if (card.Preview.Enchantment is { } ench)
            MixString(ref h, ench.Id.Entry);
        return h;
    }

    private static void Mix(ref ulong h, ulong v)
    {
        h ^= v;
        h *= FnvPrime;
    }

    private static void MixString(ref ulong h, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            h ^= text[i];
            h *= FnvPrime;
        }
    }
}
