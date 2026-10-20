using UnityEngine;

namespace SeptoMentor.Nav
{
    /// <summary>
    /// A CT volume in Hounsfield units. Voxel (i, j, k) sits at
    ///   LPS(mm) = origin + axes * (spacing * (i, j, k))
    /// "Model space" is the Unity convention used everywhere in the app (see
    /// tools/prepare_obj.py): metres, +X patient-left, +Y superior, +Z posterior,
    /// i.e. model = (L, S, P) / 1000. This keeps real-world handedness in left-handed Unity.
    /// </summary>
    public class CtVolume
    {
        public readonly int nx, ny, nz;
        public readonly short[] hu;
        public Vector3 spacing;                 // mm
        public Vector3 origin;                  // LPS mm of voxel (0,0,0)
        public Matrix4x4 axes = Matrix4x4.identity;   // columns: LPS directions of i, j, k
        public string description = "";

        Matrix4x4 _modelToIndex;
        bool _haveInv;   // geometry is fixed once a loader has filled the volume

        public CtVolume(int nx, int ny, int nz)
        {
            this.nx = nx; this.ny = ny; this.nz = nz;
            hu = new short[nx * ny * nz];
        }

        public int Index(int i, int j, int k) => i + nx * (j + ny * k);
        public short At(int i, int j, int k) => hu[Index(i, j, k)];

        public static Vector3 LpsToModel(Vector3 lps) => new Vector3(lps.x, lps.z, lps.y) * 0.001f;
        public static Vector3 ModelToLps(Vector3 m) => new Vector3(m.x, m.z, m.y) * 1000f;

        /// <summary>Voxel index (continuous) -> LPS mm.</summary>
        public Matrix4x4 IndexToLps
        {
            get
            {
                var m = axes * Matrix4x4.Scale(spacing);
                m.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1));
                return m;
            }
        }

        /// <summary>LPS mm -> model metres (a pure axis swap + scale).</summary>
        public static readonly Matrix4x4 LpsToModelM = new Matrix4x4(
            new Vector4(0.001f, 0, 0, 0), new Vector4(0, 0, 0.001f, 0), new Vector4(0, 0.001f, 0, 0), new Vector4(0, 0, 0, 1));

        public Matrix4x4 IndexToModel => LpsToModelM * IndexToLps;

        /// <summary>Model metres -> texture coordinates (voxel centres at (i + 0.5) / n).</summary>
        public Matrix4x4 ModelToUvw
        {
            get
            {
                var toIndex = IndexToModel.inverse;
                var toUvw = Matrix4x4.TRS(new Vector3(0.5f / nx, 0.5f / ny, 0.5f / nz), Quaternion.identity,
                                          new Vector3(1f / nx, 1f / ny, 1f / nz));
                return toUvw * toIndex;
            }
        }

        /// <summary>Model-space bounds of the voxel grid.</summary>
        public Bounds ModelBounds
        {
            get
            {
                var m = IndexToModel; var b = new Bounds(m.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
                for (int c = 1; c < 8; c++)
                    b.Encapsulate(m.MultiplyPoint3x4(new Vector3((c & 1) * (nx - 1), ((c >> 1) & 1) * (ny - 1), ((c >> 2) & 1) * (nz - 1))));
                return b;
            }
        }

        /// <summary>Trilinear HU at a model-space point; -1000 outside the grid.</summary>
        public float SampleModel(Vector3 model)
        {
            if (!_haveInv) { _modelToIndex = IndexToModel.inverse; _haveInv = true; }
            var p = _modelToIndex.MultiplyPoint3x4(model);
            int i = Mathf.FloorToInt(p.x), j = Mathf.FloorToInt(p.y), k = Mathf.FloorToInt(p.z);
            if (i < 0 || j < 0 || k < 0 || i >= nx - 1 || j >= ny - 1 || k >= nz - 1) return -1000f;
            float fx = p.x - i, fy = p.y - j, fz = p.z - k;
            float c00 = Mathf.Lerp(At(i, j, k), At(i + 1, j, k), fx), c10 = Mathf.Lerp(At(i, j + 1, k), At(i + 1, j + 1, k), fx);
            float c01 = Mathf.Lerp(At(i, j, k + 1), At(i + 1, j, k + 1), fx), c11 = Mathf.Lerp(At(i, j + 1, k + 1), At(i + 1, j + 1, k + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        /// <summary>Halves resolution along any axis longer than maxDim (box filter).</summary>
        public CtVolume Downsampled(int maxDim)
        {
            int fx = nx > maxDim ? 2 : 1, fy = ny > maxDim ? 2 : 1, fz = nz > maxDim ? 2 : 1;
            if (fx == 1 && fy == 1 && fz == 1) return this;
            var o = new CtVolume(nx / fx, ny / fy, nz / fz)
            {
                axes = axes, description = description,
                spacing = Vector3.Scale(spacing, new Vector3(fx, fy, fz)),
                origin = origin + axes.MultiplyVector(Vector3.Scale(spacing, new Vector3(fx - 1, fy - 1, fz - 1) * 0.5f)),
            };
            for (int k = 0; k < o.nz; k++)
            for (int j = 0; j < o.ny; j++)
            for (int i = 0; i < o.nx; i++)
            {
                int sum = 0;
                for (int c = 0; c < fz; c++) for (int b = 0; b < fy; b++) for (int a = 0; a < fx; a++)
                    sum += At(i * fx + a, j * fy + b, k * fz + c);
                o.hu[o.Index(i, j, k)] = (short)(sum / (fx * fy * fz));
            }
            return o.Downsampled(maxDim);
        }

        /// <summary>GPU copy for the slice shader. RHalf keeps raw HU so window/level is live.</summary>
        public Texture3D ToTexture()
        {
            bool half = SystemInfo.SupportsTextureFormat(TextureFormat.RHalf);
            var tex = new Texture3D(nx, ny, nz, half ? TextureFormat.RHalf : TextureFormat.R8, false)
            { name = "CT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (half)
            {
                var data = new ushort[hu.Length];
                for (int n = 0; n < hu.Length; n++) data[n] = Mathf.FloatToHalf(hu[n]);
                tex.SetPixelData(data, 0);
            }
            else
            {
                // Fallback: fixed bone window baked in (-1000..2000 HU -> 0..255); the shader is told via _Packed.
                var data = new byte[hu.Length];
                for (int n = 0; n < hu.Length; n++) data[n] = (byte)Mathf.Clamp((hu[n] + 1000) * 255 / 3000, 0, 255);
                tex.SetPixelData(data, 0);
            }
            tex.Apply(false, true);
            return tex;
        }

        public bool PackedTexture => !SystemInfo.SupportsTextureFormat(TextureFormat.RHalf);
    }
}
