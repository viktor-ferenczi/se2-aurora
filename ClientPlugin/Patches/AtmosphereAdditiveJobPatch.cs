using ClientPlugin.Aurora;
using HarmonyLib;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.LightingStage;
using Keen.VRage.Render12.Resources.Views;

namespace ClientPlugin.Patches;

/// <summary>
/// Draws the aurora right after the atmosphere, into the same additive buffer. The volume
/// rendering composite that follows adds that buffer to the light buffer, so the aurora
/// gets the same cloud occlusion and fog treatment as the atmosphere glow. Only the main
/// view runs this pass; the command list may be a split one recorded off the render thread.
/// </summary>
[HarmonyPatch(typeof(AtmosphereAdditiveJob), nameof(AtmosphereAdditiveJob.DoWork))]
public static class AtmosphereAdditiveJobPatch
{
    // ReSharper disable once InconsistentNaming
    public static void Postfix(DirectCommandList commandList, IRenderTargetView rtView)
    {
        // AuroraRenderer.Draw never throws; it disables itself on the first error.
        AuroraRenderer.Draw(commandList, rtView);
    }
}
