using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace SevenBoldPencil.TransparentSights
{
    public sealed class Patch_HousingLayer_SsaaOutput : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SSAAImpl), "OnRenderImage");

        [PatchPrefix]
        private static void Prefix(SSAAImpl __instance, RenderTexture __0, SSAAPropagator ____ssaaPropagator)
        {
            Plugin.Instance?.CompositeUpscaledHousing(__instance, __0, ____ssaaPropagator);
        }
    }
}
