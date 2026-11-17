using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SevenBoldPencil.TransparentSights
{
    internal sealed class HousingOrientationProbe : IDisposable
    {
        private readonly RenderTexture marker;
        private readonly RenderTexture samples;
        private readonly Mesh triangle;
        private readonly Mesh readout;
        private readonly Material copy;
        private readonly Vector3[] vertices = new Vector3[3];
        private bool recorded;

        public HousingOrientationProbe(Shader blend)
        {
            marker = CreateTexture(8);
            samples = CreateTexture(2, 1);
            copy = new Material(blend) { hideFlags = HideFlags.HideAndDontSave };
            copy.SetInt("_SrcFactor", (int)BlendMode.One);
            copy.SetInt("_DstFactor", (int)BlendMode.Zero);
            copy.SetInt("_Operation", (int)BlendOp.Add);
            copy.SetColor("_Color", Color.white);
            copy.SetTexture("_MainTex", marker);

            triangle = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            triangle.vertices = vertices;
            triangle.triangles = new[] { 0, 1, 2, 0, 2, 1 };
            readout = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            readout.vertices = new[]
            {
                new Vector3(-1, -1), new Vector3(0, -1), new Vector3(0, 1), new Vector3(-1, 1),
                new Vector3(0, -1), new Vector3(1, -1), new Vector3(1, 1), new Vector3(0, 1)
            };
            var lowV = new Vector2(0.5f, 0.25f);
            var highV = new Vector2(0.5f, 0.75f);
            readout.uv = new[] { lowV, lowV, lowV, lowV, highV, highV, highV, highV };
            readout.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        }

        private static RenderTexture CreateTexture(int width, int height = 8) => new(width, height, 0, RenderTextureFormat.ARGB32)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        public void Record(CommandBuffer commands, Matrix4x4 view, Matrix4x4 cropProjection, Material coverage,
            Action<bool?> onResult)
        {
            if (recorded)
            {
                return;
            }
            recorded = true;

            // Place a marker in the upper half using Unity's logical projection. Read it with
            // the same UV sampler as the final composite, so readback row order is irrelevant.
            var clipToWorld = (cropProjection * view).inverse;
            vertices[0] = Unproject(clipToWorld, -0.35f, 0.15f);
            vertices[1] = Unproject(clipToWorld, 0.35f, 0.15f);
            vertices[2] = Unproject(clipToWorld, 0f, 0.92f);
            triangle.vertices = vertices;
            commands.SetRenderTarget(marker);
            commands.SetViewport(new Rect(0, 0, marker.width, marker.height));
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.DrawMesh(triangle, Matrix4x4.identity, coverage, 0, 0);
            commands.SetRenderTarget(samples);
            commands.SetViewport(new Rect(0, 0, samples.width, samples.height));
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.DrawMesh(readout, Matrix4x4.identity, copy, 0, 1);
            commands.RequestAsyncReadback(samples, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError)
                {
                    onResult(null);
                    return;
                }
                var pixels = request.GetData<Color32>();
                var lowerVIsTop = pixels[0].a > 127;
                var higherVIsTop = pixels[1].a > 127;
                onResult(lowerVIsTop == higherVIsTop ? null : lowerVIsTop);
            });
        }

        private static Vector3 Unproject(Matrix4x4 clipToWorld, float x, float y)
        {
            var point = clipToWorld * new Vector4(x, y, 0f, 1f);
            return new Vector3(point.x, point.y, point.z) / point.w;
        }

        public void Dispose()
        {
            Object.Destroy(triangle);
            Object.Destroy(readout);
            Object.Destroy(copy);
            Object.Destroy(marker);
            Object.Destroy(samples);
        }
    }
}
