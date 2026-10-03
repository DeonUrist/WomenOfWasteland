using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WomenOfWasteland
{
    // A skinned model read from a .gltf (+ .bin / data URI) or .glb file, already converted to Unity's mesh space:
    // vertices are put into the skeleton's rest pose (sum of weight * jointWorld * inverseBind), then mirrored
    // on X (glTF is right-handed, Unity left-handed), v flipped, triangle winding reversed.
    // No UnityEngine types here, so the converter can be tested outside the game.
    internal sealed class SkinModel
    {
        public int VertexCount;
        public float[] Pos;      // 3 per vertex, Unity space
        public float[] Nrm;      // 3 per vertex (0 if the file has none)
        public float[] Uv;       // 2 per vertex, Unity convention
        public int[] Tris;       // Unity winding
        public int[] VJ;         // Influences per vertex: glTF joint index, -1 = none
        public float[] VW;
        public const int Influences = 8;
        public string[] Joints;      // joint node names
        public int[] JointParent;    // nearest ancestor that is a joint, -1 none
        public float[] JointPos;     // 3 per joint: rest position in Unity space
        public bool HasNormals;
        public string Info;
    }

    internal static class Gltf
    {
        // ---------- tiny affine matrix (row-major 4x4, doubles) ----------
        private sealed class M4
        {
            public readonly double[] m = new double[16];
            public static M4 Identity() { var r = new M4(); r.m[0] = r.m[5] = r.m[10] = r.m[15] = 1; return r; }
            public static M4 operator *(M4 a, M4 b)
            {
                var r = new M4();
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        double s = 0;
                        for (int k = 0; k < 4; k++) s += a.m[i * 4 + k] * b.m[k * 4 + j];
                        r.m[i * 4 + j] = s;
                    }
                return r;
            }
            public static M4 ColumnMajor(List<object> a)
            {
                var r = new M4();
                for (int c = 0; c < 4; c++)
                    for (int row = 0; row < 4; row++) r.m[row * 4 + c] = D(a[c * 4 + row]);
                return r;
            }
            public static M4 ColumnMajor(float[] a, int o)
            {
                var r = new M4();
                for (int c = 0; c < 4; c++)
                    for (int row = 0; row < 4; row++) r.m[row * 4 + c] = a[o + c * 4 + row];
                return r;
            }
            public static M4 Trs(double tx, double ty, double tz, double qx, double qy, double qz, double qw, double sx, double sy, double sz)
            {
                double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (n < 1e-12) { qx = qy = qz = 0; qw = 1; } else { qx /= n; qy /= n; qz /= n; qw /= n; }
                var r = new M4();
                double[] R =
                {
                    1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw),
                    2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw),
                    2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy),
                };
                double[] S = { sx, sy, sz };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++) r.m[i * 4 + j] = R[i * 3 + j] * S[j];
                r.m[3] = tx; r.m[7] = ty; r.m[11] = tz; r.m[15] = 1;
                return r;
            }
            public void Point(double x, double y, double z, out double ox, out double oy, out double oz)
            {
                ox = m[0] * x + m[1] * y + m[2] * z + m[3];
                oy = m[4] * x + m[5] * y + m[6] * z + m[7];
                oz = m[8] * x + m[9] * y + m[10] * z + m[11];
            }
            public void Vector(double x, double y, double z, out double ox, out double oy, out double oz)
            {
                ox = m[0] * x + m[1] * y + m[2] * z;
                oy = m[4] * x + m[5] * y + m[6] * z;
                oz = m[8] * x + m[9] * y + m[10] * z;
            }
        }

        // ---------- JSON helpers ----------
        private static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        private static List<object> Arr(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as List<object> : null;
        }
        private static bool Has(Dictionary<string, object> d, string k) { return d != null && d.ContainsKey(k) && d[k] != null; }
        private static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v is double) return (int)(double)v;
            return def;
        }
        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }
        private static double D(object o) { return o is double ? (double)o : 0.0; }

        // ---------- loading ----------
        public static SkinModel Load(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            string json = null;
            byte[] glbBin = null;
            if (file.Length >= 12 && BitConverter.ToUInt32(file, 0) == 0x46546C67)   // "glTF"
            {
                int off = 12;
                while (off + 8 <= file.Length)
                {
                    int len = BitConverter.ToInt32(file, off);
                    uint type = BitConverter.ToUInt32(file, off + 4);
                    off += 8;
                    if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(file, off, len);
                    else if (type == 0x004E4942) { glbBin = new byte[len]; Buffer.BlockCopy(file, off, glbBin, 0, len); }
                    off += len;
                }
                if (json == null) throw new InvalidDataException("GLB without a JSON chunk");
            }
            else json = Encoding.UTF8.GetString(file);

            var root = Obj(Json.Parse(json));
            string dir = Path.GetDirectoryName(path);

            // buffers
            var buffers = new List<byte[]>();
            var bufs = Arr(root, "buffers") ?? new List<object>();
            foreach (var b in bufs)
            {
                string uri = Str(Obj(b), "uri");
                if (uri == null) buffers.Add(glbBin);
                else if (uri.StartsWith("data:", StringComparison.Ordinal))
                    buffers.Add(Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1)));
                else buffers.Add(File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(uri))));
            }
            var views = Arr(root, "bufferViews") ?? new List<object>();
            var accessors = Arr(root, "accessors") ?? new List<object>();
            var nodes = Arr(root, "nodes") ?? new List<object>();
            var meshes = Arr(root, "meshes") ?? new List<object>();
            var skins = Arr(root, "skins") ?? new List<object>();

            // node hierarchy + rest world matrices
            int n = nodes.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = -1;
            for (int i = 0; i < n; i++)
            {
                var ch = Arr(Obj(nodes[i]), "children");
                if (ch != null) foreach (var c in ch) parent[(int)D(c)] = i;
            }
            var local = new M4[n];
            for (int i = 0; i < n; i++)
            {
                var nd = Obj(nodes[i]);
                var mat = Arr(nd, "matrix");
                if (mat != null && mat.Count == 16) { local[i] = M4.ColumnMajor(mat); continue; }
                var t = Arr(nd, "translation"); var r = Arr(nd, "rotation"); var s = Arr(nd, "scale");
                local[i] = M4.Trs(
                    t != null ? D(t[0]) : 0, t != null ? D(t[1]) : 0, t != null ? D(t[2]) : 0,
                    r != null ? D(r[0]) : 0, r != null ? D(r[1]) : 0, r != null ? D(r[2]) : 0, r != null ? D(r[3]) : 1,
                    s != null ? D(s[0]) : 1, s != null ? D(s[1]) : 1, s != null ? D(s[2]) : 1);
            }
            var world = new M4[n];
            for (int i = 0; i < n; i++) WorldOf(i, local, parent, world);

            // the skinned mesh node
            int meshNode = -1;
            for (int i = 0; i < n && meshNode < 0; i++)
                if (Has(Obj(nodes[i]), "mesh") && Has(Obj(nodes[i]), "skin")) meshNode = i;
            if (meshNode < 0) throw new InvalidDataException("no skinned mesh in the file (export with Skinning on, armature + mesh selected)");
            var mn = Obj(nodes[meshNode]);
            var skin = Obj(skins[Int(mn, "skin", 0)]);
            var mesh = Obj(meshes[Int(mn, "mesh", 0)]);

            // joints
            var jl = Arr(skin, "joints");
            int J = jl.Count;
            var jnode = new int[J];
            var isJoint = new Dictionary<int, int>();
            for (int j = 0; j < J; j++) { jnode[j] = (int)D(jl[j]); isJoint[jnode[j]] = j; }
            var skinM = new M4[J];
            float[] ibm = null;
            int ibmComps;
            if (Has(skin, "inverseBindMatrices")) ibm = ReadAccessor(accessors, views, buffers, Int(skin, "inverseBindMatrices", 0), out ibmComps);
            for (int j = 0; j < J; j++)
                skinM[j] = world[jnode[j]] * (ibm != null ? M4.ColumnMajor(ibm, j * 16) : M4.Identity());

            var model = new SkinModel();
            model.Joints = new string[J];
            model.JointParent = new int[J];
            model.JointPos = new float[J * 3];
            for (int j = 0; j < J; j++)
            {
                model.Joints[j] = Str(Obj(nodes[jnode[j]]), "name") ?? ("joint" + j);
                int p = parent[jnode[j]], pj = -1;
                while (p >= 0) { if (isJoint.TryGetValue(p, out pj)) break; p = parent[p]; pj = -1; }
                model.JointParent[j] = pj;
                var w = world[jnode[j]].m;
                model.JointPos[j * 3] = (float)-w[3];
                model.JointPos[j * 3 + 1] = (float)w[7];
                model.JointPos[j * 3 + 2] = (float)w[11];
            }

            // primitives
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>();
            var tris = new List<int>(); var vj = new List<int>(); var vw = new List<float>();
            bool hasN = true;
            int skipped = 0;
            foreach (var po in Arr(mesh, "primitives"))
            {
                var prim = Obj(po);
                if (Int(prim, "mode", 4) != 4) { skipped++; continue; }
                var at = Obj(prim["attributes"]);
                int c;
                float[] P = ReadAccessor(accessors, views, buffers, Int(at, "POSITION", -1), out c);
                int vc = P.Length / 3;
                float[] N = Has(at, "NORMAL") ? ReadAccessor(accessors, views, buffers, Int(at, "NORMAL", 0), out c) : null;
                float[] T = Has(at, "TEXCOORD_0") ? ReadAccessor(accessors, views, buffers, Int(at, "TEXCOORD_0", 0), out c) : null;
                float[][] Js = new float[2][]; float[][] Ws = new float[2][];
                for (int set = 0; set < 2; set++)
                {
                    if (Has(at, "JOINTS_" + set) && Has(at, "WEIGHTS_" + set))
                    {
                        Js[set] = ReadAccessor(accessors, views, buffers, Int(at, "JOINTS_" + set, 0), out c);
                        Ws[set] = ReadAccessor(accessors, views, buffers, Int(at, "WEIGHTS_" + set, 0), out c);
                    }
                }
                if (N == null) hasN = false;
                int baseV = pos.Count / 3;
                for (int v = 0; v < vc; v++)
                {
                    double px = P[v * 3], py = P[v * 3 + 1], pz = P[v * 3 + 2];
                    double nx = N != null ? N[v * 3] : 0, ny = N != null ? N[v * 3 + 1] : 0, nz = N != null ? N[v * 3 + 2] : 0;
                    double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0, tw = 0;
                    int k = 0;
                    for (int set = 0; set < 2; set++)
                    {
                        if (Js[set] == null) continue;
                        for (int q = 0; q < 4; q++)
                        {
                            float wgt = Ws[set][v * 4 + q];
                            int jj = (int)Js[set][v * 4 + q];
                            if (wgt <= 0f || jj < 0 || jj >= J) continue;
                            double x, y, z;
                            skinM[jj].Point(px, py, pz, out x, out y, out z);
                            ax += wgt * x; ay += wgt * y; az += wgt * z;
                            skinM[jj].Vector(nx, ny, nz, out x, out y, out z);
                            bx += wgt * x; by += wgt * y; bz += wgt * z;
                            tw += wgt;
                            if (k < SkinModel.Influences) { vj.Add(jj); vw.Add(wgt); k++; }
                        }
                    }
                    for (; k < SkinModel.Influences; k++) { vj.Add(-1); vw.Add(0f); }
                    if (tw > 1e-6) { ax /= tw; ay /= tw; az /= tw; }
                    else
                    {
                        world[meshNode].Point(px, py, pz, out ax, out ay, out az);
                        world[meshNode].Vector(nx, ny, nz, out bx, out by, out bz);
                    }
                    double nl = Math.Sqrt(bx * bx + by * by + bz * bz);
                    if (nl > 1e-9) { bx /= nl; by /= nl; bz /= nl; }
                    pos.Add((float)-ax); pos.Add((float)ay); pos.Add((float)az);
                    nrm.Add((float)-bx); nrm.Add((float)by); nrm.Add((float)bz);
                    uv.Add(T != null ? T[v * 2] : 0f); uv.Add(T != null ? 1f - T[v * 2 + 1] : 0f);
                }
                if (Has(prim, "indices"))
                {
                    float[] I = ReadAccessor(accessors, views, buffers, Int(prim, "indices", 0), out c);
                    for (int t = 0; t + 2 < I.Length; t += 3)
                    { tris.Add(baseV + (int)I[t]); tris.Add(baseV + (int)I[t + 2]); tris.Add(baseV + (int)I[t + 1]); }
                }
                else
                    for (int t = 0; t + 2 < vc; t += 3) { tris.Add(baseV + t); tris.Add(baseV + t + 2); tris.Add(baseV + t + 1); }
            }

            model.VertexCount = pos.Count / 3;
            model.Pos = pos.ToArray(); model.Nrm = nrm.ToArray(); model.Uv = uv.ToArray(); model.Tris = tris.ToArray();
            model.VJ = vj.ToArray(); model.VW = vw.ToArray();
            model.HasNormals = hasN;
            model.Info = model.VertexCount + " vertices, " + (model.Tris.Length / 3) + " triangles, " + J + " joints"
                         + (skipped > 0 ? ", " + skipped + " non-triangle primitive(s) skipped" : "");
            return model;
        }

        private static M4 WorldOf(int i, M4[] local, int[] parent, M4[] world)
        {
            if (world[i] != null) return world[i];
            world[i] = parent[i] < 0 ? local[i] : WorldOf(parent[i], local, parent, world) * local[i];
            return world[i];
        }

        private static float[] ReadAccessor(List<object> accessors, List<object> views, List<byte[]> buffers, int idx, out int comps)
        {
            if (idx < 0 || idx >= accessors.Count) throw new InvalidDataException("missing accessor " + idx);
            var a = Obj(accessors[idx]);
            int count = Int(a, "count", 0);
            string type = Str(a, "type") ?? "SCALAR";
            comps = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4
                  : type == "MAT2" ? 4 : type == "MAT3" ? 9 : type == "MAT4" ? 16 : 1;
            int ct = Int(a, "componentType", 5126);
            object nv;
            bool norm = a.TryGetValue("normalized", out nv) && nv is bool && (bool)nv;
            var res = new float[count * comps];
            if (Has(a, "sparse")) Plugin.Warn("glTF: sparse accessor " + idx + " not supported (sparse values ignored)");
            if (!Has(a, "bufferView")) return res;
            var bv = Obj(views[Int(a, "bufferView", 0)]);
            byte[] buf = buffers[Int(bv, "buffer", 0)];
            int cs = ct == 5120 || ct == 5121 ? 1 : ct == 5122 || ct == 5123 ? 2 : 4;
            int stride = Int(bv, "byteStride", 0);
            if (stride == 0) stride = cs * comps;
            int baseOff = Int(bv, "byteOffset", 0) + Int(a, "byteOffset", 0);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int o = baseOff + i * stride + c * cs;
                    float v;
                    switch (ct)
                    {
                        case 5120: v = (sbyte)buf[o]; if (norm) v = Math.Max(v / 127f, -1f); break;
                        case 5121: v = buf[o]; if (norm) v /= 255f; break;
                        case 5122: v = BitConverter.ToInt16(buf, o); if (norm) v = Math.Max(v / 32767f, -1f); break;
                        case 5123: v = BitConverter.ToUInt16(buf, o); if (norm) v /= 65535f; break;
                        case 5125: v = BitConverter.ToUInt32(buf, o); break;
                        default: v = BitConverter.ToSingle(buf, o); break;
                    }
                    res[i * comps + c] = v;
                }
            return res;
        }

        // ---------- skin weights for a target skeleton ----------
        // Maps every glTF joint to a bone of the target renderer by name ("mixamorig:Hips", or just "Hips"),
        // a joint with no match falls back to its nearest mapped parent joint, then to rootBone.
        // Result: 4 influences per vertex (strongest kept, normalised).
        public static void Weights(SkinModel m, string[] bones, int rootBone, out int[] idx4, out float[] w4, out List<string> unmapped)
        {
            var exact = new Dictionary<string, int>(StringComparer.Ordinal);
            var loose = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                if (!exact.ContainsKey(bones[i])) exact[bones[i]] = i;
                string s = Short(bones[i]);
                if (!loose.ContainsKey(s)) loose[s] = i;
            }
            int J = m.Joints.Length;
            var map = new int[J];
            unmapped = new List<string>();
            for (int j = 0; j < J; j++)
            {
                int b;
                if (exact.TryGetValue(m.Joints[j], out b) || loose.TryGetValue(Short(m.Joints[j]), out b)) map[j] = b;
                else { map[j] = -1; unmapped.Add(m.Joints[j]); }
            }
            for (int j = 0; j < J; j++)
            {
                if (map[j] >= 0) continue;
                int p = m.JointParent[j], guard = 0;
                while (p >= 0 && map[p] < 0 && guard++ < 256) p = m.JointParent[p];
                map[j] = p >= 0 && map[p] >= 0 ? map[p] : rootBone;
            }

            int n = m.VertexCount, K = SkinModel.Influences;
            idx4 = new int[n * 4];
            w4 = new float[n * 4];
            var bi = new int[K]; var bw = new float[K];
            for (int v = 0; v < n; v++)
            {
                int cnt = 0;
                for (int k = 0; k < K; k++)
                {
                    int j = m.VJ[v * K + k];
                    float w = m.VW[v * K + k];
                    if (j < 0 || w <= 0f) continue;
                    int b = map[j], at = -1;
                    for (int q = 0; q < cnt; q++) if (bi[q] == b) { at = q; break; }
                    if (at >= 0) bw[at] += w; else { bi[cnt] = b; bw[cnt] = w; cnt++; }
                }
                if (cnt == 0) { bi[0] = rootBone; bw[0] = 1f; cnt = 1; }
                for (int a = 1; a < cnt; a++)            // sort by weight, descending
                    for (int q = a; q > 0 && bw[q] > bw[q - 1]; q--)
                    {
                        float tw = bw[q]; bw[q] = bw[q - 1]; bw[q - 1] = tw;
                        int ti = bi[q]; bi[q] = bi[q - 1]; bi[q - 1] = ti;
                    }
                int keep = Math.Min(cnt, 4);
                float sum = 0f;
                for (int q = 0; q < keep; q++) sum += bw[q];
                for (int q = 0; q < 4; q++)
                {
                    idx4[v * 4 + q] = q < keep ? bi[q] : 0;
                    w4[v * 4 + q] = q < keep && sum > 0f ? bw[q] / sum : 0f;
                }
            }
        }

        private static string Short(string name)
        {
            int c = name.LastIndexOf(':');
            return c >= 0 ? name.Substring(c + 1) : name;
        }
    }
}
