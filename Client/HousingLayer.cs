using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SevenBoldPencil.TransparentSights
{
    internal sealed class HousingLayer : IDisposable
    {
        private readonly record struct Surface(MeshRenderer Renderer, MeshFilter Filter, Material Material, int Slot, int Pass);
        private const CameraEvent Event = CameraEvent.BeforeImageEffects;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int Offsets = Shader.PropertyToID("offsets");
        private static readonly int Tint = Shader.PropertyToID("_Color");
        private static readonly Vector2[] UprightUv = { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        private static readonly Vector2[] FlippedUv = { Vector2.up, Vector2.one, Vector2.right, Vector2.zero };

        private readonly ManualLogSource logger;
        private readonly Material invisible;
        private readonly Material coverage;
        private readonly Material mergeCoverage;
        private readonly Material horizontal;
        private readonly Material vertical;
        private readonly Material composite;
        private readonly List<Surface> surfaces = new();
        private readonly List<Surface> visibleSurfaces = new();
        private readonly List<(Renderer Renderer, Material[] Materials)> savedMaterials = new();
        private readonly Vector3[] vertices = new Vector3[4];
        private Camera camera;
        private CommandBuffer commands;
        private CommandBuffer outputCommands;
        private SSAAImpl upscaler;
        private Mesh quad;
        private Mesh fullQuad;
        private RenderTexture layer;
        private RenderTexture scratch;
        private float radius;
        private float opacity;
        private bool layerReady;
        private bool captureProbed;
        private bool failedCapture;
        private bool outputLogged;
        private bool flipCaptureUv;
        private bool? measuredCaptureFlip;
        private HousingOrientationProbe orientationProbe;
        private int attachmentId;
        private Rect layerRegion;

        public HousingLayer(ManualLogSource logger)
        {
            this.logger = logger;
            var flat = Shader.Find("Hidden/Internal-Colored");
            var mask = Shader.Find("Hidden/ScopeMask");
            var blur = Shader.Find("Hidden/SeparableBlurPlus");
            var blend = Shader.Find("Hidden/ColorBlend");
            if (!Supported(flat) || !Supported(mask) || !Supported(blur) || !Supported(blend) || blend.passCount < 2 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                logger.LogWarning("Separate housing blur is unavailable on this renderer.");
                return;
            }

            invisible = MakeMaterial(flat);
            invisible.SetInt("_ZTest", (int)CompareFunction.Never);
            invisible.SetInt("_ZWrite", 0);

            // The opaque forward pass can output alpha zero. ScopeMask writes constant alpha,
            // independent of the mesh's vertex colors and its lighting shader.
            coverage = MakeMaterial(mask);
            coverage.SetColor(Tint, new Color(0f, 0f, 0f, 1f));
            mergeCoverage = MakeMaterial(blend);
            mergeCoverage.SetInt("_SrcFactor", (int)BlendMode.OneMinusDstAlpha);
            mergeCoverage.SetInt("_DstFactor", (int)BlendMode.One);
            mergeCoverage.SetInt("_Operation", (int)BlendOp.Add);
            mergeCoverage.SetColor(Tint, Color.white);

            horizontal = MakeMaterial(blur);
            vertical = MakeMaterial(blur);
            composite = MakeMaterial(blend);
            composite.SetInt("_SrcFactor", (int)BlendMode.One);
            composite.SetInt("_DstFactor", (int)BlendMode.OneMinusSrcAlpha);
            composite.SetInt("_Operation", (int)BlendOp.Add);
        }

        private static bool IsOpticalSurface(string name) =>
            name.IndexOf("lens", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("linza", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("reticle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("collimator", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool Supported(Shader shader) => shader && shader.isSupported;
        private static Material MakeMaterial(Shader shader) => new(shader) { hideFlags = HideFlags.HideAndDontSave };
        public bool Attach(Camera target, string sightName, List<PatchedRenderer> renderers, float blurRadius, float visibility)
        {
            Detach();
            if (failedCapture || !composite || !target || target.stereoEnabled)
            {
                return false;
            }
            foreach (var entry in renderers)
            {
                if (entry.Renderer is not MeshRenderer renderer || !renderer ||
                    IsOpticalSurface(renderer.name) ||
                    !renderer.TryGetComponent<MeshFilter>(out var filter) || !filter.sharedMesh)
                {
                    continue;
                }
                var slots = Mathf.Min(filter.sharedMesh.subMeshCount, entry.Patched.Length);
                for (var slot = 0; slot < slots; slot++)
                {
                    var original = entry.Original[slot];
                    if (!original || !original.shader || entry.Patched[slot] == original ||
                        IsOpticalSurface(original.name) || IsOpticalSurface(original.shader.name))
                    {
                        continue;
                    }
                    var pass = original.FindPass("FORWARD");
                    if (pass >= 0)
                    {
                        surfaces.Add(new Surface(renderer, filter, original, slot, pass));
                    }
                }
            }
            if (surfaces.Count == 0)
            {
                logger.LogInfo($"Separate housing blur skipped for {sightName}: no compatible housing material.");
                return false;
            }
            radius = blurRadius;
            opacity = visibility;
            camera = target;
            upscaler = target.GetComponent<SSAAImpl>();
            commands = new CommandBuffer { name = "TransparentSights housing layer" };
            outputCommands = new CommandBuffer { name = "TransparentSights housing composite" };
            quad = new Mesh { name = "TransparentSights housing composite", hideFlags = HideFlags.HideAndDontSave };
            quad.vertices = vertices;
            quad.uv = UprightUv;
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            fullQuad = Object.Instantiate(quad);
            fullQuad.hideFlags = HideFlags.HideAndDontSave;
            fullQuad.vertices = new[]
            {
                new Vector3(-1f, -1f, 0.5f), new Vector3(1f, -1f, 0.5f),
                new Vector3(1f, 1f, 0.5f), new Vector3(-1f, 1f, 0.5f)
            };
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                fullQuad.uv = FlippedUv;
            }
            camera.AddCommandBuffer(Event, commands);
            Camera.onPreCull += Prepare;
            Camera.onPreRender += Record;
            Camera.onPostRender += RestoreVisibleMaterials;
            logger.LogDebug($"Separate housing blur attached for {sightName} ({surfaces.Count} housing surfaces).");
            return true;
        }

        public void SetAppearance(float blurRadius, float visibility)
        {
            radius = blurRadius;
            opacity = visibility;
        }

        private bool CollectVisibleSurfaces(Camera target)
        {
            visibleSurfaces.Clear();
            foreach (var surface in surfaces)
            {
                var renderer = surface.Renderer;
                if (!renderer || !surface.Filter || !surface.Filter.sharedMesh || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff ||
                    (target.cullingMask & (1 << renderer.gameObject.layer)) == 0 || HasHigherDetail(surface))
                {
                    continue;
                }
                visibleSurfaces.Add(surface);
            }
            return visibleSurfaces.Count != 0;
        }

        private bool HasHigherDetail(Surface surface)
        {
            // The selected sight is close to the eye. Avoid drawing the same shell from several LODs.
            var name = surface.Renderer.name;
            var suffix = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (suffix < 0 || !int.TryParse(name.Substring(suffix + 4), out var level) || level == 0)
            {
                return false;
            }
            var detailedName = name.Substring(0, suffix) + "_LOD0";
            return surfaces.Exists(other => other.Renderer && other.Renderer.enabled &&
                other.Renderer.gameObject.activeInHierarchy && other.Renderer.transform.parent == surface.Renderer.transform.parent &&
                string.Equals(other.Renderer.name, detailedName, StringComparison.OrdinalIgnoreCase));
        }

        private void Prepare(Camera target)
        {
            if (target != camera)
            {
                return;
            }
            commands.Clear();
            layerReady = false;
            if (!CollectVisibleSurfaces(target) ||
                !TryGetRegion(target, target.worldToCameraMatrix, target.projectionMatrix, out _))
            {
                return;
            }

            try
            {
                foreach (var surface in surfaces)
                {
                    var renderer = surface.Renderer;
                    if (!renderer)
                    {
                        continue;
                    }
                    var materials = renderer.sharedMaterials;
                    if (surface.Slot >= materials.Length)
                    {
                        continue;
                    }
                    if (!savedMaterials.Exists(entry => entry.Renderer == renderer))
                    {
                        savedMaterials.Add((renderer, (Material[])materials.Clone()));
                    }
                    materials[surface.Slot] = invisible;
                    renderer.sharedMaterials = materials;
                }
            }
            catch (Exception error)
            {
                commands.Clear();
                RestoreVisibleMaterials(target);
                logger.LogWarning($"Separate housing blur failed: {error.Message}");
                Detach();
            }
        }

        private void Record(Camera target)
        {
            if (target != camera || savedMaterials.Count == 0 || opacity <= 0f)
            {
                return;
            }
            var view = target.worldToCameraMatrix;
            var projection = target.projectionMatrix;
            if (!CollectVisibleSurfaces(target) || !TryGetRegion(target, view, projection, out var region))
            {
                RestoreVisibleMaterials(target);
                return;
            }
            try
            {
                RecordLayer(target, region, view, projection);
                layerRegion = region;
                layerReady = true;
                if (!upscaler || !upscaler.isActiveAndEnabled)
                {
                    var cameraProjection = GL.GetGPUProjectionMatrix(projection,
                        target.targetTexture || target.forceIntoRenderTexture || target.activeTexture);
                    RecordComposite(commands, BuiltinRenderTextureType.CameraTarget,
                        target.scaledPixelWidth, target.scaledPixelHeight, cameraProjection.m11 < 0f,
                        measuredCaptureFlip ?? flipCaptureUv);
                }
            }
            catch (Exception error)
            {
                commands.Clear();
                logger.LogWarning($"Separate housing capture failed: {error.Message}");
                Detach();
            }
        }

        private bool TryGetRegion(Camera target, Matrix4x4 view, Matrix4x4 projection, out Rect region)
        {
            var min = new Vector2(1f, 1f);
            var max = Vector2.zero;
            Renderer previous = null;
            foreach (var surface in visibleSurfaces)
            {
                if (previous == surface.Renderer)
                {
                    continue;
                }
                previous = surface.Renderer;
                var bounds = surface.Filter.sharedMesh.bounds;
                var localToEye = view * surface.Renderer.localToWorldMatrix;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                    var eye = localToEye.MultiplyPoint3x4(corner);
                    if (-eye.z <= target.nearClipPlane)
                    {
                        region = default;
                        return false;
                    }
                    var clip = projection * new Vector4(eye.x, eye.y, eye.z, 1f);
                    var uv = new Vector2(clip.x, clip.y) * (0.5f / clip.w) + Vector2.one * 0.5f;
                    min = Vector2.Min(min, uv);
                    max = Vector2.Max(max, uv);
                }
            }
            var pixels = new Vector2(Mathf.Max(1, target.pixelWidth), Mathf.Max(1, target.pixelHeight));
            var pad = radius + 6f;
            region = Rect.MinMaxRect(Mathf.Clamp01(min.x - pad / pixels.x), Mathf.Clamp01(min.y - pad / pixels.y),
                Mathf.Clamp01(max.x + pad / pixels.x), Mathf.Clamp01(max.y + pad / pixels.y));
            return region.width > 0.001f && region.height > 0.001f;
        }

        private void RecordLayer(Camera target, Rect region, Matrix4x4 view, Matrix4x4 projection)
        {
            var pixelWidth = region.width * target.pixelWidth;
            var pixelHeight = region.height * target.pixelHeight;
            EnsureTargets(Mathf.Clamp(Mathf.CeilToInt(pixelWidth / 64f) * 32, 32, 1024),
                Mathf.Clamp(Mathf.CeilToInt(pixelHeight / 64f) * 32, 32, 1024));

            var crop = Matrix4x4.identity;
            crop.m00 = 1f / region.width;
            crop.m11 = 1f / region.height;
            crop.m03 = (1f - 2f * region.center.x) / region.width;
            crop.m13 = (1f - 2f * region.center.y) / region.height;
            var captureProjection = GL.GetGPUProjectionMatrix(crop * projection, true);
            flipCaptureUv = SystemInfo.graphicsUVStartsAtTop != (captureProjection.m11 < 0f);
            commands.SetRenderTarget(layer);
            commands.SetViewport(new Rect(0, 0, layer.width, layer.height));
            commands.ClearRenderTarget(true, true, Color.clear);
            commands.SetViewProjectionMatrices(view, captureProjection);
            foreach (var surface in visibleSurfaces)
            {
                commands.DrawMesh(surface.Filter.sharedMesh, surface.Renderer.localToWorldMatrix, surface.Material, surface.Slot, surface.Pass);
            }

            commands.SetRenderTarget(scratch);
            commands.SetViewport(new Rect(0, 0, scratch.width, scratch.height));
            commands.ClearRenderTarget(false, true, Color.clear);
            foreach (var surface in visibleSurfaces)
            {
                commands.DrawMesh(surface.Filter.sharedMesh, surface.Renderer.localToWorldMatrix, coverage, surface.Slot, 0);
            }
            if (SystemInfo.supportsAsyncGPUReadback && !measuredCaptureFlip.HasValue)
            {
                orientationProbe ??= new HousingOrientationProbe(composite.shader);
                orientationProbe.Record(commands, view, crop * projection, coverage, MeasureCaptureOrientation);
            }

            // Match each destination row to the same row in the coverage texture.
            mergeCoverage.SetTexture(MainTex, scratch);
            commands.SetRenderTarget(layer, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            commands.SetViewport(new Rect(0, 0, layer.width, layer.height));
            commands.DrawMesh(fullQuad, Matrix4x4.identity, mergeCoverage, 0, 1);

            if (radius > 0f)
            {
                const float lastTap = 6.5f;
                horizontal.SetVector(Offsets, new Vector4(radius / (lastTap * pixelWidth), 0f));
                vertical.SetVector(Offsets, new Vector4(0f, radius / (lastTap * pixelHeight)));
                commands.Blit(layer, scratch, horizontal, 0);
                commands.Blit(scratch, layer, vertical, 0);
            }

            commands.SetViewProjectionMatrices(view, GL.GetGPUProjectionMatrix(projection,
                target.targetTexture || target.forceIntoRenderTexture || target.activeTexture));
            if (!captureProbed && SystemInfo.supportsAsyncGPUReadback)
            {
                captureProbed = true;
                var generation = attachmentId;
                commands.RequestAsyncReadback(layer, 0, TextureFormat.RGBA32, request => CheckCapture(request, generation));
            }
        }

        private void MeasureCaptureOrientation(bool? flipped)
        {
            measuredCaptureFlip = flipped;
            logger.LogInfo(flipped.HasValue
                ? $"Housing layer orientation probe: crop flip V is {flipped.Value}, projection predicted {flipCaptureUv}."
                : "Housing layer orientation probe could not determine the crop orientation.");
        }

        private void CheckCapture(AsyncGPUReadbackRequest request, int generation)
        {
            if (request.hasError)
            {
                logger.LogDebug("Separate housing blur could not read the cropped texture for diagnosis.");
                return;
            }
            var data = request.GetData<Color32>();
            byte peakAlpha = 0;
            byte peakColor = 0;
            for (var i = 0; i < data.Length; i++)
            {
                var pixel = data[i];
                peakAlpha = (byte)Mathf.Max(peakAlpha, pixel.a);
                peakColor = (byte)Mathf.Max(peakColor, Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)));
                if (peakAlpha == byte.MaxValue && peakColor == byte.MaxValue)
                {
                    break;
                }
            }
            logger.LogInfo($"Separate housing blur captured RGB {peakColor}/255, alpha {peakAlpha}/255.");
            if (peakAlpha == 0 && generation == attachmentId)
            {
                logger.LogWarning("Separate housing blur captured empty coverage. Restoring the original sight.");
                failedCapture = true;
                Detach();
            }
        }

        public void CompositeUpscaled(SSAAImpl instance, RenderTexture source, SSAAPropagator propagator)
        {
            if (upscaler != instance || !layerReady || opacity <= 0f)
            {
                return;
            }
            RenderTexture current = null;
            var propagated = propagator && propagator.GetSourceDestination(out current, out _) && current;
            if (propagated)
            {
                source = current;
            }
            if (!source)
            {
                return;
            }

            outputCommands.Clear();
            var flipOutputY = SystemInfo.graphicsUVStartsAtTop != instance.FlippedV;
            var flippedCrop = measuredCaptureFlip ?? flipCaptureUv;
            if (propagated && SystemInfo.graphicsUVStartsAtTop && !instance.FlippedV)
            {
                // This route was mirrored with the original projection-based mapping.
                // If the crop probe validates that mapping, correct the mirror in frame placement.
                if (measuredCaptureFlip == flipCaptureUv)
                {
                    flipOutputY = !flipOutputY;
                }
                flippedCrop = measuredCaptureFlip ?? !flipCaptureUv;
            }
            RecordComposite(outputCommands, source, source.width, source.height, flipOutputY, flippedCrop);
            var previous = RenderTexture.active;
            try
            {
                Graphics.ExecuteCommandBuffer(outputCommands);
            }
            catch (Exception error)
            {
                logger.LogWarning($"Separate housing composite failed: {error.Message}");
                failedCapture = true;
                Detach();
                return;
            }
            finally
            {
                RenderTexture.active = previous;
            }
            if (!outputLogged)
            {
                logger.LogInfo($"Separate housing blur composed into {(propagated ? "Tarkov's upscaled frame" : "the camera image")} at {source.width}x{source.height} (present flip V: {instance.FlippedV}).");
                outputLogged = true;
            }
        }

        private void RecordComposite(CommandBuffer buffer, RenderTargetIdentifier destination, int width, int height,
            bool flipY, bool flipV)
        {
            var ySign = flipY ? -1f : 1f;
            var left = 2f * layerRegion.xMin - 1f;
            var right = 2f * layerRegion.xMax - 1f;
            var bottom = (2f * layerRegion.yMin - 1f) * ySign;
            var top = (2f * layerRegion.yMax - 1f) * ySign;
            vertices[0] = new Vector3(left, bottom, 0.5f);
            vertices[1] = new Vector3(right, bottom, 0.5f);
            vertices[2] = new Vector3(right, top, 0.5f);
            vertices[3] = new Vector3(left, top, 0.5f);
            quad.vertices = vertices;
            quad.uv = flipV ? FlippedUv : UprightUv;
            composite.SetTexture(MainTex, layer);
            composite.SetVector(Tint, Vector4.one * opacity);
            buffer.SetRenderTarget(destination, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            buffer.SetViewport(new Rect(0, 0, width, height));
            buffer.DrawMesh(quad, Matrix4x4.identity, composite, 0, 1);
        }

        private void EnsureTargets(int width, int height)
        {
            if (layer && layer.width == width && layer.height == height)
            {
                return;
            }
            ReleaseTargets();
            layer = MakeTarget(width, height, 24);
            scratch = MakeTarget(width, height, 0);
        }

        private static RenderTexture MakeTarget(int width, int height, int depth)
        {
            var target = new RenderTexture(width, height, depth, RenderTextureFormat.ARGBHalf)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!target.Create())
            {
                Object.Destroy(target);
                throw new InvalidOperationException("Could not allocate the cropped housing texture.");
            }
            return target;
        }

        private void RestoreVisibleMaterials(Camera target)
        {
            if (target != camera)
            {
                return;
            }
            foreach (var (renderer, materials) in savedMaterials)
            {
                if (renderer)
                {
                    renderer.sharedMaterials = materials;
                }
            }
            savedMaterials.Clear();
        }

        public void Detach()
        {
            if (commands != null)
            {
                RestoreVisibleMaterials(camera);
                Camera.onPreCull -= Prepare;
                Camera.onPreRender -= Record;
                Camera.onPostRender -= RestoreVisibleMaterials;
                if (camera)
                {
                    camera.RemoveCommandBuffer(Event, commands);
                }
                commands.Release();
                commands = null;
                outputCommands.Release();
                outputCommands = null;
                Object.Destroy(quad);
                quad = null;
                Object.Destroy(fullQuad);
                fullQuad = null;
            }
            surfaces.Clear();
            visibleSurfaces.Clear();
            camera = null;
            upscaler = null;
            layerReady = false;
            attachmentId++;
            ReleaseTargets();
        }

        private void ReleaseTargets()
        {
            if (layer)
            {
                layer.Release();
                Object.Destroy(layer);
                layer = null;
            }
            if (scratch)
            {
                scratch.Release();
                Object.Destroy(scratch);
                scratch = null;
            }
        }

        public void Dispose()
        {
            Detach();
            Object.Destroy(invisible);
            Object.Destroy(coverage);
            Object.Destroy(mergeCoverage);
            Object.Destroy(horizontal);
            Object.Destroy(vertical);
            Object.Destroy(composite);
            orientationProbe?.Dispose();
        }
    }
}
