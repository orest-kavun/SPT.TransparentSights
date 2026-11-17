using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT.BlitDebug;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityStandardAssets.ImageEffects;

namespace SevenBoldPencil.TransparentSights
{
    public sealed class Patch_DepthOfField_ForegroundMask : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(DepthOfField), "WriteCoc", new[] { typeof(RenderTexture), typeof(bool) });
        }

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var originalBlit = AccessTools.Method(typeof(DebugGraphics), nameof(DebugGraphics.Blit),
                new[] { typeof(Texture), typeof(RenderTexture), typeof(Material), typeof(int) });
            var maskBlit = -1;
            for (var i = 1; i < code.Count; i++)
            {
                if (code[i - 1].opcode != OpCodes.Ldc_I4_4 || !code[i].Calls(originalBlit))
                {
                    continue;
                }
                if (maskBlit >= 0)
                {
                    Plugin.Instance.LoggerInstance.LogWarning("Multiple foreground-mask blits found. Using the original mask.");
                    return code;
                }
                maskBlit = i;
            }
            if (maskBlit < 0)
            {
                Plugin.Instance.LoggerInstance.LogWarning("Foreground-mask blit not found. Using the original mask.");
                return code;
            }

            var filteredBlit = AccessTools.Method(typeof(Patch_DepthOfField_ForegroundMask), nameof(BlitMask));
            ReplaceBlit(code, maskBlit, filteredBlit);
            return code;
        }

        private static void ReplaceBlit(List<CodeInstruction> code, int index, MethodInfo replacement)
        {
            code[index].opcode = OpCodes.Ldarg_0;
            code[index].operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, replacement));
        }

        private static void BlitMask(Texture source, RenderTexture destination, Material material, int pass, DepthOfField effect)
        {
            if (Plugin.Instance?.ShouldFilterForegroundMask(effect) != true ||
                (source.width == destination.width && source.height == destination.height))
            {
                DebugGraphics.Blit(source, destination, material, pass);
                return;
            }

            var descriptor = destination.descriptor;
            descriptor.width = source.width;
            descriptor.height = source.height;
            var fullMask = RenderTexture.GetTemporary(descriptor);
            try
            {
                fullMask.filterMode = FilterMode.Bilinear;
                fullMask.wrapMode = TextureWrapMode.Clamp;
                DebugGraphics.Blit(source, fullMask, material, pass);
                Graphics.Blit(fullMask, destination);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(fullMask);
            }
        }
    }
}
