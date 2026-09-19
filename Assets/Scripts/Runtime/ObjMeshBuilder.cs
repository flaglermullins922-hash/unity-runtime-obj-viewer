// ---------------------------------------------------------------------------
// ObjMeshBuilder.cs  —  把解析结果 ObjData 构建成 Unity 可渲染的 Mesh
//
// 关键点：Unity 的 Mesh 顶点是「位置/UV/法线」组合去重后的结果，与 OBJ 里的
//        v 行不是一一对应的。这里显式建立 corner -> vertex 的映射表，
//        并把映射关系保留下来（SourceVertexOf），方便反向追溯。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ObjViewer
{
    public static class ObjMeshBuilder
    {
        /// <summary>为一个子网格构建 Mesh。</summary>
        /// <param name="sourceVertexOf">输出：Mesh 第 i 个顶点来自 OBJ 里第几个 v 行（用于反向查询）。</param>
        public static Mesh BuildSubMesh(ObjData data, ObjSubMesh sub, out int[] sourceVertexOf)
        {
            var corners = sub.corners;
            int cornerCount = corners.Count;

            var vertices = new List<Vector3>(cornerCount);
            var uvs = new List<Vector2>(data.HasUVs ? cornerCount : 0);
            var normals = new List<Vector3>(data.HasNormals ? cornerCount : 0);
            var hasExplicitNormal = new List<bool>(data.HasNormals ? cornerCount : 0);
            var colors = new List<Color>(data.HasVertexColors ? cornerCount : 0);
            var triangles = new List<int>(cornerCount);
            var map = new Dictionary<FaceCorner, int>(cornerCount);
            var srcMap = new List<int>(cornerCount);
            bool anyMissingNormal = false;

            for (int i = 0; i < cornerCount; i++)
            {
                FaceCorner c = corners[i];

                int vi;
                if (!map.TryGetValue(c, out vi))
                {
                    vi = vertices.Count;
                    map.Add(c, vi);

                    Vector3 pos = (c.position >= 0 && c.position < data.positions.Count)
                        ? data.positions[c.position] : Vector3.zero;
                    vertices.Add(pos);
                    srcMap.Add(c.position);

                    if (data.HasUVs)
                        uvs.Add((c.uv >= 0 && c.uv < data.uvs.Count) ? data.uvs[c.uv] : Vector2.zero);

                    if (data.HasNormals)
                    {
                        bool ok = c.normal >= 0 && c.normal < data.normals.Count;
                        normals.Add(ok ? data.normals[c.normal] : Vector3.zero);
                        hasExplicitNormal.Add(ok);
                        if (!ok) anyMissingNormal = true;
                    }

                    if (data.HasVertexColors)
                        colors.Add((c.position >= 0 && c.position < data.vertexColors.Count)
                            ? data.vertexColors[c.position] : Color.white);
                }

                triangles.Add(vi);
            }

            var mesh = new Mesh { name = sub.objectName + " [" + sub.materialName + "]" };
            if (vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            if (uvs.Count == vertices.Count) mesh.SetUVs(0, uvs);
            if (colors.Count == vertices.Count) mesh.SetColors(colors);

            if (normals.Count == vertices.Count)
            {
                mesh.SetNormals(normals);

                // 有些 OBJ 只给了一部分面法线（本例 9598 条 vs 33974 个顶点）。
                // 缺失的顶点法线若留成 (0,0,0)，shader 里 normalize 会出 NaN。
                // 做法：整网格自动算法线，再把文件中原本写明的法线覆盖回去。
                if (anyMissingNormal)
                {
                    mesh.RecalculateNormals();
                    Vector3[] auto = mesh.normals;
                    for (int i = 0; i < auto.Length && i < hasExplicitNormal.Count; i++)
                        if (hasExplicitNormal[i]) auto[i] = normals[i];
                    mesh.SetNormals(auto);
                }
            }
            else
            {
                mesh.RecalculateNormals();
            }

            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            sourceVertexOf = srcMap.ToArray();
            return mesh;
        }

        /// <summary>构建「线框」Mesh：顶点沿用原 Mesh，索引用 Lines 拓扑画出每条三角边。</summary>
        public static Mesh BuildWireframe(Mesh source)
        {
            Vector3[] verts = source.vertices;
            int[] tris = source.triangles;

            var lines = new List<int>(tris.Length * 2);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                lines.Add(a); lines.Add(b);
                lines.Add(b); lines.Add(c);
                lines.Add(c); lines.Add(a);
            }

            var mesh = new Mesh { name = source.name + " (wireframe)" };
            if (verts.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetIndices(lines.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
