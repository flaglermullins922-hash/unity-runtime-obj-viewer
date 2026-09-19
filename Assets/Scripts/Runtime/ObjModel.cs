// ---------------------------------------------------------------------------
// ObjModel.cs  —  ★核心★ 运行时 OBJ 模型：加载 / 渲染 / 顶点数据访问
//
// 为什么不用 Unity 编辑器的 OBJ 导入？
//   编辑器导入会把 OBJ 烘焙成一个只读的 Mesh 资产，虽然也能读 mesh.vertices，
//   但顶点数据在导入那一刻就被 Unity 加工过（去重、缩放、坐标系转换），
//   而且无法在运行时切换模型、无法在运行时重建几何。
//   本项目在「运行时」自己解析、自己建 Mesh，于是：
//     · Mesh 完全可读写（isReadable 天然为 true）
//     · 同时保留 OBJ 原始的 v 行顶点表（SourceVertices）和 Unity 展开后的顶点表
//     · 随时可以改顶点、重算法线、换模型 —— 这就是后续功能扩展的接口
//
// 顶点访问 API 一览（作业要求 2）：
//   Data              解析出来的完整原始数据（顶点/UV/法线/子网格/材质）
//   SourceVertices    OBJ 文件里 v 行的原始顶点（一比一，未去重）
//   Parts             每个子网格（Mesh + Renderer + Material + 顶点映射）
//   GetVertices(i)    第 i 个子网格的 Unity 顶点数组（可直接读写底层数据）
//   GetAllVertices()  合并所有子网格顶点的快照
//   SetAllVertices()  把顶点数组写回 Mesh（未来功能：顶点动画/编辑/变形）
//   GetAllColors()/SetAllColors()  顶点色读写
//   Loaded            加载完成事件
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace ObjViewer
{
    /// <summary>一个子网格对应的运行时渲染资源。</summary>
    public sealed class ObjMeshPart
    {
        public ObjSubMesh source;
        public Mesh mesh;            // 三角面 Mesh（可读写）
        public Mesh wireMesh;        // 线框 Mesh（Lines 拓扑）
        public MeshFilter filter;
        public MeshRenderer renderer;
        public Material material;
        public Color baseColor = Color.white;

        public Vector3[] restVertices;     // 初始顶点（做顶点动画时的基准）
        public Vector3[] restNormals;      // 初始法线
        public Color[] restColors;         // 初始顶点色
        public int[] sourceVertexOf;       // mesh 第 i 个顶点来自 OBJ 第几个 v 行

        public GameObject surfaceGO;       // 三角面显示用
        public GameObject wireGO;          // 线框显示用
    }

    [DisallowMultipleComponent]
    public sealed class ObjModel : MonoBehaviour
    {
        public enum ShadingMode
        {
            MaterialColor = 0,   // 材质/调色板颜色
            HeightGradient = 1,  // 按顶点高度渐变
            NormalColor = 2,     // 把法线当颜色（检查数据是否合理）
            Wireframe = 3        // 线框
        }

        [Header("加载设置")]
        [Tooltip("StreamingAssets/models 下的文件名")]
        public string modelFile = "Tiger-class.obj";
        public bool loadOnStart = true;

        [Header("显示设置")]
        public ShadingMode shadingMode = ShadingMode.MaterialColor;
        public bool showVertexPoints = false;
        public float vertexPointScale = 1f;
        public int vertexPointMaxCount = 20000;   // 顶点云最多显示多少个（抽样）
        [Range(0f, 1f)] public float breatheAmplitude = 0.02f;
        public bool breatheAnimation = false;

        // ---------------------------------------------------------- 运行状态
        public ObjData Data { get; private set; }
        public ObjMeshPart[] Parts { get; private set; } = new ObjMeshPart[0];
        public bool IsLoaded { get; private set; }
        public float LoadMilliseconds { get; private set; }
        public string LoadedFrom { get; private set; }
        public string LoadError { get; private set; }

        /// <summary>模型包围盒（世界空间），相机取景和高度渐变都用它。</summary>
        public Bounds ModelBounds { get; private set; }

        /// <summary>全部子网格的三角面总数。</summary>
        public int TriangleCount => Data != null ? Data.TriangleCount : 0;

        /// <summary>OBJ 文件里 v 行的数量（原始顶点数）。</summary>
        public int SourceVertexCount => Data != null ? Data.positions.Count : 0;

        /// <summary>Unity Mesh 展开后的顶点总数（所有子网格之和）。</summary>
        public int MeshVertexCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Parts.Length; i++) n += Parts[i].mesh.vertexCount;
                return n;
            }
        }

        public event Action<ObjModel> Loaded;

        // 无 .mtl 时给子网格分配的调色板（科技感配色，避免整船惨白）
        static readonly Color[] Palette =
        {
            new Color(0.78f, 0.80f, 0.84f), new Color(0.55f, 0.58f, 0.63f),
            new Color(0.35f, 0.42f, 0.52f), new Color(0.72f, 0.62f, 0.48f),
            new Color(0.45f, 0.50f, 0.58f), new Color(0.62f, 0.66f, 0.72f),
            new Color(0.28f, 0.34f, 0.42f), new Color(0.85f, 0.72f, 0.50f),
        };

        static readonly int ColorId = Shader.PropertyToID("_Color");

        // =====================================================================
        //  加载
        // =====================================================================

        void Start()
        {
            if (loadOnStart && !IsLoaded) Load(modelFile);
        }

        /// <summary>从 StreamingAssets/models 目录加载。</summary>
        public void Load(string fileName)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "models", fileName);
            LoadFromFile(path);
        }

        public void LoadFromFile(string absolutePath)
        {
            Clear();
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(absolutePath))
                    throw new FileNotFoundException("找不到模型文件: " + absolutePath);

                Data = ObjParser.ParseFile(absolutePath);
                Data.sourcePath = absolutePath;

                // 顺带找同名 .mtl，拿到模型本来的颜色
                string mtlPath = Path.ChangeExtension(absolutePath, ".mtl");
                if (File.Exists(mtlPath))
                {
                    foreach (var m in ObjParser.ParseMtlFile(mtlPath)) Data.materials.Add(m);
                }

                BuildRenderers();
                sw.Stop();
                LoadMilliseconds = sw.ElapsedMilliseconds;
                LoadedFrom = absolutePath;
                LoadError = null;
                IsLoaded = true;
                ApplyShadingMode();
                UnityEngine.Debug.Log(string.Format(
                    "[ObjModel] 加载完成: {0}  顶点(v行)={1}  Mesh顶点={2}  三角面={3}  子网格={4}  耗时={5} ms",
                    Path.GetFileName(absolutePath), SourceVertexCount, MeshVertexCount,
                    TriangleCount, Parts.Length, LoadMilliseconds));

                // 事件回调单独保护：回调里的异常不应该被误报成「加载失败」
                try
                {
                    Loaded?.Invoke(this);
                }
                catch (Exception cbEx)
                {
                    UnityEngine.Debug.LogError("[ObjModel] Loaded 事件回调中发生异常: " + cbEx);
                }
            }
            catch (Exception e)
            {
                sw.Stop();
                LoadError = e.Message;
                UnityEngine.Debug.LogError("[ObjModel] 加载失败: " + e);
            }
        }

        public void LoadFromText(string objText, string displayName = "inline.obj")
        {
            Clear();
            var sw = Stopwatch.StartNew();
            Data = ObjParser.Parse(objText, displayName);
            BuildRenderers();
            sw.Stop();
            LoadMilliseconds = sw.ElapsedMilliseconds;
            LoadedFrom = displayName;
            LoadError = null;
            IsLoaded = true;
            ApplyShadingMode();
            Loaded?.Invoke(this);
        }

        /// <summary>用解析结果构建所有渲染对象（模型根节点就是本 GameObject）。</summary>
        void BuildRenderers()
        {
            var parts = new List<ObjMeshPart>(Data.subMeshes.Count);
            var overallBounds = new Bounds();
            bool hasBounds = false;

            Shader surfaceShader = Shader.Find("ObjViewer/SurfaceLambert");
            Shader wireShader = Shader.Find("ObjViewer/SurfaceLambert");
            if (surfaceShader == null) surfaceShader = Shader.Find("Standard");

            for (int i = 0; i < Data.subMeshes.Count; i++)
            {
                ObjSubMesh sub = Data.subMeshes[i];
                int[] srcMap;
                Mesh mesh = ObjMeshBuilder.BuildSubMesh(Data, sub, out srcMap);

                var part = new ObjMeshPart
                {
                    source = sub,
                    mesh = mesh,
                    wireMesh = ObjMeshBuilder.BuildWireframe(mesh),
                    sourceVertexOf = srcMap
                };

                // 颜色：优先 .mtl 的 Kd，其次顶点色，最后调色板
                ObjMaterialInfo info = Data.FindMaterial(sub.materialName);
                if (info != null) part.baseColor = info.diffuse;
                else if (Data.HasVertexColors) part.baseColor = Color.white;
                else part.baseColor = Palette[i % Palette.Length];

                // 三角面对象
                part.surfaceGO = new GameObject(sub.objectName);
                part.surfaceGO.transform.SetParent(transform, false);
                part.filter = part.surfaceGO.AddComponent<MeshFilter>();
                part.filter.sharedMesh = mesh;
                part.renderer = part.surfaceGO.AddComponent<MeshRenderer>();
                part.renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

                // 线框对象
                part.wireGO = new GameObject(sub.objectName + "_wire");
                part.wireGO.transform.SetParent(transform, false);
                var wf = part.wireGO.AddComponent<MeshFilter>();
                wf.sharedMesh = part.wireMesh;
                var wr = part.wireGO.AddComponent<MeshRenderer>();
                wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                wr.receiveShadows = false;
                part.wireGO.SetActive(false);

                // 材质（每个 part 一份实例）
                part.material = new Material(surfaceShader) { name = "Mat_" + sub.materialName };
                part.material.SetColor(ColorId, part.baseColor);
                part.material.SetColor("_Emission", Color.black);
                part.material.SetColor("_RimColor", new Color(0.30f, 0.70f, 1f));
                part.material.SetFloat("_RimPower", 3.2f);
                part.material.SetFloat("_Smoothness", info != null ? Mathf.Clamp01(info.shininess / 200f) : 0.4f);
                part.renderer.sharedMaterial = part.material;
                wr.sharedMaterial = part.material;

                // 缓存初始数据（顶点动画/模式切换的基准）
                part.restVertices = mesh.vertices;
                part.restNormals = mesh.normals;
                part.restColors = new Color[mesh.vertexCount];
                for (int c = 0; c < part.restColors.Length; c++) part.restColors[c] = Color.white;

                overallBounds.Encapsulate(mesh.bounds);
                hasBounds = true;
                parts.Add(part);
            }

            Parts = parts.ToArray();
            ModelBounds = hasBounds ? overallBounds : new Bounds(Vector3.zero, Vector3.one);
        }

        void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
            Parts = new ObjMeshPart[0];
            Data = null;
            IsLoaded = false;
            LoadError = null;
        }

        // =====================================================================
        //  ★ 顶点数据访问 API ★
        // =====================================================================

        /// <summary>OBJ 文件里所有 v 行的原始顶点（只读视图，未去重、未变换）。</summary>
        public IReadOnlyList<Vector3> SourceVertices => Data != null ? (IReadOnlyList<Vector3>)Data.positions : new List<Vector3>();

        /// <summary>取第 i 个子网格的 Unity 顶点数组（返回的是 Mesh 内部数据的拷贝）。</summary>
        public Vector3[] GetVertices(int partIndex) => Parts[partIndex].mesh.vertices;

        /// <summary>合并全部子网格的顶点，拼成一个数组（顺序 = Parts 顺序拼接）。</summary>
        public Vector3[] GetAllVertices()
        {
            var all = new Vector3[MeshVertexCount];
            int o = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                Vector3[] v = Parts[i].mesh.vertices;
                Array.Copy(v, 0, all, o, v.Length);
                o += v.Length;
            }
            return all;
        }

        /// <summary>把合并顶点数组写回所有子网格（未来功能入口：顶点动画、变形、编辑）。</summary>
        public void SetAllVertices(Vector3[] all)
        {
            int o = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                ObjMeshPart p = Parts[i];
                int n = p.mesh.vertexCount;
                if (o + n > all.Length) break;
                var slice = new Vector3[n];
                Array.Copy(all, o, slice, 0, n);
                p.mesh.SetVertices(slice);
                o += n;
            }
        }

        public Vector3[] GetAllNormals()
        {
            var all = new Vector3[MeshVertexCount];
            int o = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                Vector3[] v = Parts[i].mesh.normals;
                Array.Copy(v, 0, all, o, v.Length);
                o += v.Length;
            }
            return all;
        }

        public Color[] GetAllColors()
        {
            var all = new Color[MeshVertexCount];
            int o = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                Color[] v = Parts[i].mesh.colors;
                if (v.Length == 0) v = new Color[Parts[i].mesh.vertexCount];
                Array.Copy(v, 0, all, o, v.Length);
                o += v.Length;
            }
            return all;
        }

        /// <summary>写回顶点色（同样按子网格顺序拼接）。</summary>
        public void SetAllColors(Color[] all)
        {
            int o = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                ObjMeshPart p = Parts[i];
                int n = p.mesh.vertexCount;
                if (o + n > all.Length) break;
                var slice = new Color[n];
                Array.Copy(all, o, slice, 0, n);
                p.mesh.SetColors(slice);
                o += n;
            }
        }

        /// <summary>遍历所有顶点做一个例子：返回顶点重心的世界坐标。</summary>
        public Vector3 ComputeVertexCentroid()
        {
            Vector3 sum = Vector3.zero;
            long count = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                Vector3[] v = Parts[i].mesh.vertices;
                for (int k = 0; k < v.Length; k++) sum += v[k];
                count += v.Length;
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        /// <summary>重新计算法线并刷新（顶点被改动后调用）。</summary>
        public void RecalculateNormals()
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                Parts[i].mesh.RecalculateNormals();
                Parts[i].mesh.RecalculateBounds();
                Parts[i].restNormals = Parts[i].mesh.normals;
            }
        }

        /// <summary>恢复初始顶点（关掉顶点动画/撤销修改）。</summary>
        public void RestoreRestPose()
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                ObjMeshPart p = Parts[i];
                p.mesh.SetVertices(p.restVertices);
                p.mesh.RecalculateBounds();
            }
        }

        // =====================================================================
        //  显示模式
        // =====================================================================

        /// <summary>按当前 shadingMode 重新写顶点色 / 切换线框显示。</summary>
        public void ApplyShadingMode()
        {
            if (!IsLoaded) return;

            float minY = ModelBounds.min.y, maxY = ModelBounds.max.y;
            float span = Mathf.Max(1e-4f, maxY - minY);

            for (int i = 0; i < Parts.Length; i++)
            {
                ObjMeshPart p = Parts[i];
                bool wire = shadingMode == ShadingMode.Wireframe;

                p.surfaceGO.SetActive(!wire);
                p.wireGO.SetActive(wire);

                var colors = new Color[p.mesh.vertexCount];
                switch (shadingMode)
                {
                    case ShadingMode.MaterialColor:
                        p.material.SetColor(ColorId, p.baseColor);
                        p.material.SetColor("_Emission", Color.black);
                        for (int k = 0; k < colors.Length; k++) colors[k] = p.restColors[k];
                        break;

                    case ShadingMode.HeightGradient:
                    {
                        p.material.SetColor(ColorId, Color.white);
                        p.material.SetColor("_Emission", Color.black);
                        Vector3[] vs = p.restVertices;
                        for (int k = 0; k < colors.Length; k++)
                        {
                            float t = Mathf.Clamp01((vs[k].y - minY) / span);
                            colors[k] = Gradient(t);
                        }
                        break;
                    }

                    case ShadingMode.NormalColor:
                    {
                        p.material.SetColor(ColorId, Color.white);
                        p.material.SetColor("_Emission", Color.black);
                        Vector3[] ns = p.restNormals;
                        for (int k = 0; k < colors.Length; k++)
                            colors[k] = new Color(ns[k].x * 0.5f + 0.5f, ns[k].y * 0.5f + 0.5f, ns[k].z * 0.5f + 0.5f, 1f);
                        break;
                    }

                    case ShadingMode.Wireframe:
                        p.material.SetColor(ColorId, Color.white);
                        p.material.SetColor("_Emission", new Color(0.10f, 0.55f, 0.85f));
                        Vector3[] wv = p.restVertices;
                        for (int k = 0; k < colors.Length; k++)
                        {
                            float t = Mathf.Clamp01((wv[k].y - minY) / span);
                            colors[k] = Color.Lerp(new Color(0.25f, 0.85f, 1f), new Color(1f, 0.6f, 0.25f), t);
                        }
                        break;
                }

                p.mesh.SetColors(colors);
            }
        }

        /// <summary>青 → 紫 → 橙 的高度渐变。</summary>
        public static Color Gradient(float t)
        {
            t = Mathf.Clamp01(t);
            if (t < 0.5f) return Color.Lerp(new Color(0.15f, 0.85f, 1f), new Color(0.55f, 0.45f, 1f), t * 2f);
            return Color.Lerp(new Color(0.55f, 0.45f, 1f), new Color(1f, 0.55f, 0.20f), (t - 0.5f) * 2f);
        }

        // =====================================================================
        //  ★ 顶点动画示例 ★  —— 证明"顶点可写"，这也是后续功能的模板
        // =====================================================================

        float _breathePhase;

        void Update()
        {
            if (!IsLoaded) return;

            if (breatheAnimation)
            {
                _breathePhase += Time.deltaTime * 1.8f;
                AnimateBreathe(_breathePhase, breatheAmplitude);
            }
        }

        /// <summary>沿法线方向做正弦"呼吸"位移：直接改 mesh.vertices。</summary>
        public void AnimateBreathe(float phase, float amplitude)
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                ObjMeshPart p = Parts[i];
                Vector3[] vs = p.restVertices;
                Vector3[] ns = p.restNormals;
                var outV = new Vector3[vs.Length];
                for (int k = 0; k < vs.Length; k++)
                {
                    float wave = Mathf.Sin(phase + vs[k].y * 0.6f + vs[k].x * 0.3f);
                    outV[k] = vs[k] + ns[k] * (wave * amplitude);
                }
                p.mesh.SetVertices(outV);
                p.mesh.RecalculateBounds();
            }
        }

        public void StopBreathe()
        {
            breatheAnimation = false;
            RestoreRestPose();
        }

        /// <summary>取当前（可能已被动画修改的）全部顶点，供顶点云显示使用。</summary>
        public void CopyCurrentVerticesTo(List<Vector3> buffer)
        {
            buffer.Clear();
            for (int i = 0; i < Parts.Length; i++)
            {
                Vector3[] v = Parts[i].mesh.vertices;
                for (int k = 0; k < v.Length; k++) buffer.Add(v[k]);
            }
        }
    }
}
