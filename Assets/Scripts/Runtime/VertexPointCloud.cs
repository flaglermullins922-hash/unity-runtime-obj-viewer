// ---------------------------------------------------------------------------
// VertexPointCloud.cs  —  把模型的每一个顶点画成一个小方块（顶点可视化的直观证明）
//
// 用法上它是"可选图层"：按空格打开，就能亲眼看到"顶点确实被程序拿到了"。
// 实现：从 ObjModel 读出顶点数组 → 每个顶点一个矩阵 → Graphics.DrawMeshInstanced
//       （八面体小方块 + 按世界高度着色的实例化 shader）
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ObjViewer
{
    public sealed class VertexPointCloud : MonoBehaviour
    {
        public ObjModel model;
        public bool visible = false;
        public int maxPoints = 20000;
        public float pointScale = 1f;

        Mesh _shape;             // 八面体小方块
        Material _material;
        readonly List<Vector3> _points = new List<Vector3>();
        Matrix4x4[] _matrices = new Matrix4x4[1023];   // DrawMeshInstanced 单批上限
        float _minY, _maxY;
        int _lastVertexCount = -1;

        static readonly int ColorAId = Shader.PropertyToID("_ColorA");
        static readonly int ColorBId = Shader.PropertyToID("_ColorB");
        static readonly int HeightMinId = Shader.PropertyToID("_HeightMin");
        static readonly int HeightMaxId = Shader.PropertyToID("_HeightMax");

        void Awake()
        {
            EnsureInit();
        }

        /// <summary>惰性初始化：编辑器批处理/非 Play 模式下 Awake 不会被调用，所以每个入口都先确保资源就绪。</summary>
        void EnsureInit()
        {
            if (_shape != null && _material != null) return;
            _shape = CreateOctahedron();
            Shader sh = Shader.Find("ObjViewer/VertexPoint");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            _material = new Material(sh) { name = "VertexPointMat" };
            _material.enableInstancing = true;          // DrawMeshInstanced 的硬性要求
            _material.SetColor(ColorAId, new Color(0.15f, 0.85f, 1f));
            _material.SetColor(ColorBId, new Color(1f, 0.5f, 0.15f));
        }

        /// <summary>顶点数变化后重新采样。</summary>
        public void Refresh()
        {
            if (model == null || !model.IsLoaded) return;
            EnsureInit();

            model.CopyCurrentVerticesTo(_points);
            _minY = model.ModelBounds.min.y;
            _maxY = model.ModelBounds.max.y;

            _material.SetFloat(HeightMinId, _minY);
            _material.SetFloat(HeightMaxId, _maxY);

            // 模型局部空间 -> 世界空间（模型根节点可能被移动过）
            Matrix4x4 localToWorld = model.transform.localToWorldMatrix;
            for (int i = 0; i < _points.Count; i++)
                _points[i] = localToWorld.MultiplyPoint3x4(_points[i]);

            _lastVertexCount = _points.Count;
        }

        void LateUpdate()
        {
            DrawNow();
        }

        /// <summary>立刻提交一次实例化绘制（Play 模式由 LateUpdate 调用，批处理自测可手动调用）。</summary>
        public void DrawNow()
        {
            if (!visible || model == null || !model.IsLoaded) return;
            EnsureInit();

            // 顶点被顶点动画改动过就重新采样
            if (_lastVertexCount != model.MeshVertexCount && _lastVertexCount != -1)
                Refresh();
            else if (_lastVertexCount == -1)
                Refresh();

            if (model.breatheAnimation) Refresh();

            int total = _points.Count;
            if (total == 0) return;

            int stride = Mathf.Max(1, Mathf.CeilToInt((float)total / Mathf.Max(1, maxPoints)));
            float size = Mathf.Max(1e-4f, (model.ModelBounds.extents.magnitude * 0.004f)) * pointScale;

            int batch = 0, drawn = 0;
            for (int i = 0; i < total; i += stride)
            {
                _matrices[batch++] = Matrix4x4.TRS(_points[i], Quaternion.identity, Vector3.one * size);
                if (batch == 1023)
                {
                    Graphics.DrawMeshInstanced(_shape, 0, _material, _matrices, batch);
                    drawn += batch;
                    batch = 0;
                }
            }
            if (batch > 0)
            {
                Graphics.DrawMeshInstanced(_shape, 0, _material, _matrices, batch);
                drawn += batch;
            }
        }

        /// <summary>程序化生成一个 6 顶点 8 面的八面体，当"点"用。</summary>
        static Mesh CreateOctahedron()
        {
            var m = new Mesh { name = "OctahedronPoint" };
            m.vertices = new[]
            {
                new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
                new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
            };
            m.triangles = new[]
            {
                0,2,4,  2,1,4,  1,3,4,  3,0,4,
                2,0,5,  1,2,5,  3,1,5,  0,3,5,
            };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_shape != null) Destroy(_shape);
        }
    }
}
