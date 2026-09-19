// ---------------------------------------------------------------------------
// VertexAccessExamples.cs  —  顶点访问示例集（给"未来功能扩展"当模板用）
//
// 本文件不参与渲染，纯粹演示：拿到 ObjModel 之后，顶点数据可以怎么用。
// 作业要求「选择的方法应该能够访问到模型顶点，便于未来增加其他功能」，
// 下面每个方法都是一个可以立刻接上去的功能入口。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ObjViewer
{
    public static class VertexAccessExamples
    {
        /// <summary>① 遍历全部顶点，找出最高/最低点（例：碰撞盒、站位点）。</summary>
        public static void FindExtremes(ObjModel model, out Vector3 highest, out Vector3 lowest)
        {
            highest = Vector3.zero;
            lowest = Vector3.zero;
            float maxY = float.MinValue, minY = float.MaxValue;

            for (int p = 0; p < model.Parts.Length; p++)
            {
                Vector3[] verts = model.Parts[p].mesh.vertices;   // ← 直接读顶点数组
                for (int i = 0; i < verts.Length; i++)
                {
                    if (verts[i].y > maxY) { maxY = verts[i].y; highest = verts[i]; }
                    if (verts[i].y < minY) { minY = verts[i].y; lowest = verts[i]; }
                }
            }
        }

        /// <summary>② 整体缩放顶点（例：统一不同来源模型的尺寸）。</summary>
        public static void ScaleVertices(ObjModel model, float factor)
        {
            Vector3[] all = model.GetAllVertices();
            Vector3 center = model.ComputeVertexCentroid();     // 以顶点重心为缩放中心
            for (int i = 0; i < all.Length; i++)
                all[i] = center + (all[i] - center) * factor;

            model.SetAllVertices(all);                          // ← 写回顶点
            model.RecalculateNormals();
        }

        /// <summary>③ 按顶点高度写入顶点色（例：热力图、地形着色）。</summary>
        public static void ApplyHeightHeatmap(ObjModel model)
        {
            Vector3[] verts = model.GetAllVertices();
            Color[] colors = model.GetAllColors();

            float minY = model.ModelBounds.min.y;
            float span = Mathf.Max(1e-4f, model.ModelBounds.size.y);

            for (int i = 0; i < verts.Length; i++)
            {
                float t = Mathf.Clamp01((verts[i].y - minY) / span);
                colors[i] = ObjModel.Gradient(t);
            }

            model.SetAllColors(colors);                         // ← 写回顶点色
        }

        /// <summary>④ 顶点抖动 / 噪声位移（例：爆炸、受击形变）。</summary>
        public static void JitterVertices(ObjModel model, float amount, int seed = 12345)
        {
            var rnd = new System.Random(seed);
            Vector3[] verts = model.GetAllVertices();
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] += new Vector3(
                    (float)(rnd.NextDouble() - 0.5) * amount,
                    (float)(rnd.NextDouble() - 0.5) * amount,
                    (float)(rnd.NextDouble() - 0.5) * amount);
            }
            model.SetAllVertices(verts);
        }

        /// <summary>⑤ 从顶点反推网格信息：把每个 Mesh 顶点映射回 OBJ 的 v 行号。</summary>
        public static string DescribeSourceMapping(ObjModel model, int maxRows = 5)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Mesh顶点 -> OBJ v 行 的映射（前 " + maxRows + " 条）：");
            for (int p = 0; p < model.Parts.Length && p < maxRows; p++)
            {
                ObjMeshPart part = model.Parts[p];
                sb.AppendFormat("  子网格[{0}] {1,-34} Mesh顶点={2,7}  来自 v[{3}..]\n",
                    p, part.source.objectName, part.mesh.vertexCount, part.sourceVertexOf[0]);
            }
            return sb.ToString();
        }

        /// <summary>⑥ 统计每个子网格的信息（例：UI 列表、渲染批次分析）。</summary>
        public static List<string> ListSubMeshes(ObjModel model)
        {
            var rows = new List<string>();
            for (int i = 0; i < model.Parts.Length; i++)
            {
                ObjMeshPart p = model.Parts[i];
                rows.Add(string.Format("{0,2}. {1,-32} 材质 {2,-28} 三角面 {3,6:N0}",
                    i, p.source.objectName, p.source.materialName, p.source.TriangleCount));
            }
            return rows;
        }

        /// <summary>⑦ 在模型表面按顶点做一次随机撒点（例：粒子发射器、贴花位置）。</summary>
        public static List<Vector3> ScatterOnVertices(ObjModel model, int count, int seed = 7)
        {
            var rnd = new System.Random(seed);
            var result = new List<Vector3>(count);
            Vector3[] all = model.GetAllVertices();
            if (all.Length == 0) return result;

            for (int i = 0; i < count; i++)
                result.Add(all[rnd.Next(all.Length)]);
            return result;
        }
    }
}
