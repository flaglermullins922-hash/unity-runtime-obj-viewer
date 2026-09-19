// ---------------------------------------------------------------------------
// ObjParsing.cs  —  OBJ / MTL 文本解析层（纯数据，不依赖任何渲染逻辑）
//
// 设计目标：把 .obj 文本完整翻译成内存中的「顶点表 + 子网格 + 三角面」结构，
//          解析结果对上层完全公开，方便后续做顶点级功能扩展。
//
// 支持：
//   v / vn / vt            顶点、法线、UV
//   v x y z r g b          带顶点颜色的顶点（非标准但常见）
//   f 的四种写法            v | v/vt | v//vn | v/vt/vn
//   负数索引               -1 表示"相对于当前已读顶点数"的倒数第一个
//   n 边形面               自动扇形三角化（quad / ngon）
//   o / g / usemtl         按对象名 + 材质名切分子网格
//   mtllib / MTL 的 Kd/Ka/Ks/Ns/d  材质漫反射色
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ObjViewer
{
    /// <summary>OBJ 面里的一个角点。索引均为 0 起算；-1 表示该文件没提供这一项。</summary>
    public struct FaceCorner : IEquatable<FaceCorner>
    {
        public int position;
        public int uv;
        public int normal;

        public FaceCorner(int p, int u, int n) { position = p; uv = u; normal = n; }

        public bool Equals(FaceCorner other)
            => position == other.position && uv == other.uv && normal == other.normal;

        public override bool Equals(object obj) => obj is FaceCorner other && Equals(other);

        public override int GetHashCode() => (position * 397) ^ (uv * 31) ^ normal;
    }

    /// <summary>一个子网格 = 同一材质下的所有面。三角面数 = corners.Count / 3。</summary>
    public sealed class ObjSubMesh
    {
        public string objectName = "default";
        public string materialName = "default";
        public readonly List<FaceCorner> corners = new List<FaceCorner>();
        public int TriangleCount => corners.Count / 3;
    }

    /// <summary>从 .mtl 读出的材质信息（KD 漫反射色就是模型本来的颜色）。</summary>
    public sealed class ObjMaterialInfo
    {
        public string name = "default";
        public Color diffuse = Color.white;   // Kd
        public Color ambient = Color.black;   // Ka
        public Color specular = Color.black;  // Ks
        public float shininess = 10f;         // Ns
        public float alpha = 1f;              // d / Tr
        public string diffuseMap;             // map_Kd
    }

    /// <summary>整个 OBJ 文件的解析结果。</summary>
    public sealed class ObjData
    {
        /// <summary>OBJ 里 v 行的原始顶点，一行一个，顺序一致（未被 Unity 展开/去重）。</summary>
        public readonly List<Vector3> positions = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<Vector3> normals = new List<Vector3>();
        /// <summary>若 v 行带 r g b，这里会有对应顶点色。</summary>
        public readonly List<Color> vertexColors = new List<Color>();
        public readonly List<ObjSubMesh> subMeshes = new List<ObjSubMesh>();
        public readonly List<ObjMaterialInfo> materials = new List<ObjMaterialInfo>();

        public string mtlLibName;
        public string sourcePath;

        public bool HasUVs => uvs.Count > 0;
        public bool HasNormals => normals.Count > 0;
        public bool HasVertexColors => vertexColors.Count > 0;
        public int SourceVertexCount => positions.Count;

        public int TriangleCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < subMeshes.Count; i++) n += subMeshes[i].TriangleCount;
                return n;
            }
        }

        public ObjMaterialInfo FindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < materials.Count; i++)
                if (string.Equals(materials[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return materials[i];
            return null;
        }
    }

    public static class ObjParser
    {
        static readonly char[] SplitChars = { ' ', '\t' };

        // ---------------------------------------------------------------- OBJ
        public static ObjData ParseFile(string path)
        {
            using (var reader = new StreamReader(path))
                return Parse(reader, path);
        }

        public static ObjData Parse(string text, string sourcePath = null)
        {
            using (var reader = new StringReader(text))
                return Parse(reader, sourcePath);
        }

        public static ObjData Parse(TextReader reader, string sourcePath = null)
        {
            var data = new ObjData { sourcePath = sourcePath };

            string objName = "default";
            string matName = "default";
            ObjSubMesh current = null;
            var cornerBuffer = new FaceCorner[128];

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                int i = 0, len = line.Length;

                // 跳过前导空白
                while (i < len && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= len || line[i] == '#') continue;

                // 取出关键字
                int keyStart = i;
                while (i < len && line[i] != ' ' && line[i] != '\t') i++;
                string key = line.Substring(keyStart, i - keyStart);

                switch (key)
                {
                    case "v":
                    {
                        var f = new float[6];
                        int n = ReadFloats(line, i, f, 6);
                        if (n >= 3)
                        {
                            data.positions.Add(new Vector3(f[0], f[1], f[2]));
                            if (n >= 6) data.vertexColors.Add(new Color(f[3], f[4], f[5], 1f));
                        }
                        break;
                    }
                    case "vn":
                    {
                        var f = new float[3];
                        if (ReadFloats(line, i, f, 3) == 3) data.normals.Add(new Vector3(f[0], f[1], f[2]));
                        break;
                    }
                    case "vt":
                    {
                        var f = new float[2];
                        if (ReadFloats(line, i, f, 2) >= 1) data.uvs.Add(new Vector2(f[0], f[1]));
                        break;
                    }
                    case "f":
                    {
                        // 子网格按 (对象名, 材质名) 切分；第一次遇到该组合时创建
                        if (current == null || current.objectName != objName || current.materialName != matName)
                            current = GetOrCreateSubMesh(data, objName, matName);

                        ParseFace(line, i, data, current, cornerBuffer);
                        break;
                    }
                    case "o":
                    case "g":
                        objName = line.Substring(i).Trim();
                        if (objName.Length == 0) objName = "default";
                        break;
                    case "usemtl":
                        matName = line.Substring(i).Trim();
                        if (matName.Length == 0) matName = "default";
                        break;
                    case "mtllib":
                        data.mtlLibName = line.Substring(i).Trim();
                        break;
                }
            }

            return data;
        }

        static ObjSubMesh GetOrCreateSubMesh(ObjData data, string objName, string matName)
        {
            var sm = new ObjSubMesh { objectName = objName, materialName = matName };
            data.subMeshes.Add(sm);
            return sm;
        }

        // ------------------------------------------------------------- face
        /// <summary>解析一行 f：支持多边形、四种索引写法、负数索引。按扇形三角化写入 corners。</summary>
        static void ParseFace(string line, int start, ObjData data, ObjSubMesh target, FaceCorner[] buf)
        {
            int pCount = data.positions.Count;
            int tCount = data.uvs.Count;
            int nCount = data.normals.Count;

            int count = 0;
            int i = start, len = line.Length;

            while (i < len)
            {
                while (i < len && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= len) break;

                int st = i;
                while (i < len && line[i] != ' ' && line[i] != '\t') i++;

                if (count >= buf.Length) Array.Resize(ref buf, buf.Length * 2);
                buf[count++] = ParseCorner(line, st, i, pCount, tCount, nCount);
            }

            if (count < 3) return;

            // 扇形三角化： (0,1,2) (0,2,3) (0,3,4) ...
            for (int k = 1; k + 1 < count; k++)
            {
                target.corners.Add(buf[0]);
                target.corners.Add(buf[k]);
                target.corners.Add(buf[k + 1]);
            }
        }

        /// <summary>解析单个角点 token，形如 "v" / "v/vt" / "v//vn" / "v/vt/vn"。</summary>
        static FaceCorner ParseCorner(string line, int start, int end, int pCount, int tCount, int nCount)
        {
            int p = -1, t = -1, n = -1;
            int field = 0;
            int i = start;

            while (field < 3)
            {
                int st = i;
                while (i < end && line[i] != '/') i++;

                if (i > st)
                {
                    int raw;
                    if (int.TryParse(line.Substring(st, i - st), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out raw))
                    {
                        // OBJ 索引 1 起算；负数表示相对当前计数
                        int resolved = raw > 0 ? raw - 1 : (raw < 0 ? (field == 0 ? pCount : field == 1 ? tCount : nCount) + raw : -1);
                        if (field == 0) p = resolved;
                        else if (field == 1) t = resolved;
                        else n = resolved;
                    }
                }

                field++;
                if (i >= end) break;
                i++; // 跳过 '/'
            }

            return new FaceCorner(p, t, n);
        }

        // -------------------------------------------------------------- MTL
        public static List<ObjMaterialInfo> ParseMtlFile(string path)
        {
            using (var reader = new StreamReader(path))
                return ParseMtl(reader);
        }

        public static List<ObjMaterialInfo> ParseMtl(TextReader reader)
        {
            var list = new List<ObjMaterialInfo>();
            ObjMaterialInfo cur = null;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] tok = line.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length == 0) continue;

                switch (tok[0])
                {
                    case "newmtl":
                        cur = new ObjMaterialInfo { name = tok.Length > 1 ? tok[1] : "default" };
                        list.Add(cur);
                        break;
                    case "Kd":
                        if (cur != null && tok.Length >= 4)
                            cur.diffuse = new Color(P(tok[1]), P(tok[2]), P(tok[3]), 1f);
                        break;
                    case "Ka":
                        if (cur != null && tok.Length >= 4)
                            cur.ambient = new Color(P(tok[1]), P(tok[2]), P(tok[3]), 1f);
                        break;
                    case "Ks":
                        if (cur != null && tok.Length >= 4)
                            cur.specular = new Color(P(tok[1]), P(tok[2]), P(tok[3]), 1f);
                        break;
                    case "Ns":
                        if (cur != null && tok.Length >= 2) cur.shininess = P(tok[1]);
                        break;
                    case "d":
                        if (cur != null && tok.Length >= 2) cur.alpha = P(tok[1]);
                        break;
                    case "map_Kd":
                        if (cur != null && tok.Length >= 2) cur.diffuseMap = tok[tok.Length - 1];
                        break;
                }
            }

            return list;
        }

        static float P(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        // ------------------------------------------------------------ helper
        /// <summary>从 line 的 start 位置起解析最多 count 个 float（手写，避免 Split 的 GC）。</summary>
        static int ReadFloats(string line, int start, float[] outValues, int count)
        {
            int idx = start, len = line.Length, field = 0;
            while (field < count && idx < len)
            {
                while (idx < len && (line[idx] == ' ' || line[idx] == '\t')) idx++;
                if (idx >= len) break;

                int st = idx;
                while (idx < len && line[idx] != ' ' && line[idx] != '\t') idx++;

                float v;
                if (!float.TryParse(line.Substring(st, idx - st), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v))
                    break;
                outValues[field++] = v;
            }
            return field;
        }
    }
}
