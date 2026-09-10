using System;
using ClientPlugin.Aurora;
using HarmonyLib;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Diagnostics;

namespace ClientPlugin.Patches;

/// <summary>
/// Per-frame game-thread hook: Session.Update runs every frame for the client and the
/// server session while a world is loaded, which is exactly when there are planets.
/// </summary>
[HarmonyPatch(typeof(Session), nameof(Session.Update), typeof(bool))]
public static class SessionUpdatePatch
{
    private static bool failed;

    // ReSharper disable once InconsistentNaming
    public static void Postfix(Session __instance)
    {
        if (failed)
            return;
        try
        {
            PlanetOrientationSampler.Update(__instance);
        }
        catch (Exception e)
        {
            failed = true;
            PlanetOrientationSampler.Clear();
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] Planet sampling failed, using the world axis for this session: {e}");
        }
    }
}
