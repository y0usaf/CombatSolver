using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class MultiplayerHandshakeModListPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_multiplayer_handshake_mod_list";
    public static string Description => "Omit CombatSolver and STS2-RitsuLib from outgoing multiplayer handshake mod lists";

    public static ModPatchTarget[] GetTargets()
    {
        MethodInfo? method = typeof(PeerVersionInfo).GetMethod(
            nameof(PeerVersionInfo.LocalDefault),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (method == null || method.ReturnType != typeof(PeerVersionInfo))
            return [];
        return [new(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault), Type.EmptyTypes)];
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(ref PeerVersionInfo __result)
        => __result = Sanitize(__result);

    internal static PeerVersionInfo Sanitize(PeerVersionInfo info)
    {
        if (info.otherMods is { Count: > 0 } otherMods)
        {
            List<string> filtered = otherMods.Where(entry => !IsOmittedModEntry(entry)).ToList();
            info.otherMods = filtered.Count > 0 ? filtered : null;
        }
        else if (info.otherMods is { Count: 0 })
        {
            info.otherMods = null;
        }

        if (info.otherMods == null && info.gameplayAffectingMods is { Count: 0 })
            info.gameplayAffectingMods = null;

        return info;
    }

    internal static bool IsOmittedModEntry(string? entry)
    {
        if (string.IsNullOrEmpty(entry))
            return false;
        return MatchesModId(entry, Entry.ModId) || MatchesModId(entry, "STS2-RitsuLib");
    }

    private static bool MatchesModId(string entry, string modId)
        => string.Equals(entry, modId, StringComparison.OrdinalIgnoreCase)
           || (entry.StartsWith(modId, StringComparison.OrdinalIgnoreCase)
               && entry.Length > modId.Length
               && entry[modId.Length] == '-');
}
