// ---------------------------------------------------------------------------
// ObjViewerController.cs  —  总控：自动搭建场景、键盘操作、屏幕面板
//
// 特点：完全"零手工配置"。打开任意场景直接 Play 就能用，
//       相机 / 灯光 / 环境 / 模型 / 顶点云 全部由代码创建。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace ObjViewer
{
    [DefaultExecutionOrder(-50)]
    public sealed class ObjViewerController : MonoBehaviour
    {
        [Header("模型")]
        public string modelFileName = "Tiger-class.obj";

        [Header("界面")]
        public bool showPanel = true;
        public bool showHelp = true;

        ObjModel _model;
        VertexPointCloud _cloud;
        OrbitCameraController _orbit;
        Camera _cam;

        float _fps = 60f;
        static Font _cnFont;

        static readonly KeyCode[] ModeKeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

        // ---------------------------------------------------------------- 启动
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            if (FindObjectOfType<ObjViewerController>() != null) return;
            var go = new GameObject("[ObjViewer]");
            go.AddComponent<ObjViewerController>();
        }

        void Awake()
        {
            Initialize();
        }

        /// <summary>
        /// 初始化全部运行环境。Awake 会自动调用，也可由脚本/自测手动调用。
        /// （编辑器非 Play 模式下 AddComponent 不会触发 Awake，手动调用入口是必要的）
        /// </summary>
        public void Initialize()
        {
            SetupRenderEnvironment();
            SetupCamera();
            SetupLights();
            SetupModel();
        }

        /// <summary>当前模型（供外部脚本访问）。</summary>
        public ObjModel Model => _model;
        /// <summary>顶点云组件。</summary>
        public VertexPointCloud Cloud => _cloud;
        /// <summary>相机控制器。</summary>
        public OrbitCameraController Orbit => _orbit;

        void SetupRenderEnvironment()
        {
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.30f, 0.40f);
            RenderSettings.ambientEquatorColor = new Color(0.14f, 0.17f, 0.23f);
            RenderSettings.ambientGroundColor = new Color(0.04f, 0.05f, 0.07f);
            QualitySettings.shadowDistance = 400f;
        }

        void SetupCamera()
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var cgo = GameObject.Find("Main Camera");
                if (cgo == null)
                {
                    cgo = new GameObject("Main Camera");
                    cgo.transform.position = new Vector3(0f, 0f, -10f);
                }
                cgo.tag = "MainCamera";
                _cam = cgo.GetComponent<Camera>();
                if (_cam == null) _cam = cgo.AddComponent<Camera>();
            }

            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.035f, 0.045f, 0.065f);
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 6000f;
            _cam.fieldOfView = 55f;
            _cam.allowHDR = true;

            _orbit = _cam.GetComponent<OrbitCameraController>();
            if (_orbit == null) _orbit = _cam.gameObject.AddComponent<OrbitCameraController>();
        }

        void SetupLights()
        {
            var key = new GameObject("Key Light");
            var kl = key.AddComponent<Light>();
            kl.type = LightType.Directional;
            kl.color = new Color(1f, 0.965f, 0.91f);
            kl.intensity = 1.15f;
            kl.shadows = LightShadows.Soft;
            kl.shadowStrength = 0.6f;
            key.transform.rotation = Quaternion.Euler(46f, -38f, 0f);

            var fill = new GameObject("Fill Light");
            var fl = fill.AddComponent<Light>();
            fl.type = LightType.Directional;
            fl.color = new Color(0.48f, 0.68f, 1f);
            fl.intensity = 0.5f;
            fl.shadows = LightShadows.None;
            fill.transform.rotation = Quaternion.Euler(12f, 152f, 0f);

            var rim = new GameObject("Rim Light");
            var rl = rim.AddComponent<Light>();
            rl.type = LightType.Directional;
            rl.color = new Color(0.75f, 0.85f, 1f);
            rl.intensity = 0.35f;
            rl.shadows = LightShadows.None;
            rim.transform.rotation = Quaternion.Euler(-25f, 20f, 0f);
        }

        void SetupModel()
        {
            var mgo = new GameObject("OBJ Model");
            _model = mgo.AddComponent<ObjModel>();
            _model.modelFile = modelFileName;
            _model.loadOnStart = false;

            // ⚠ 顺序很重要：ObjModel.Load() 是【同步】的，构建完 Mesh 会立刻触发 Loaded 事件，
            //   而回调 OnModelLoaded 里要用到 _cloud。所以顶点云必须在 Load() 之前创建，
            //   否则回调里 _cloud 还是 null → NullReferenceException。
            _cloud = gameObject.AddComponent<VertexPointCloud>();
            _cloud.model = _model;
            _cloud.visible = false;

            _model.Loaded += OnModelLoaded;
            _model.Load(modelFileName);
        }

        void OnModelLoaded(ObjModel m)
        {
            if (m == null) return;
            BuildGroundGrid(m.ModelBounds);
            if (_orbit != null)
            {
                _orbit.ResetView(m.ModelBounds);
                _orbit.pivot = null;
            }
            if (_cloud != null)
            {
                _cloud.model = m;
                _cloud.Refresh();
            }
        }

        /// <summary>地面参考网格：让浏览时有空间参照（不是模型的一部分）。</summary>
        Mesh _gridMesh;
        void BuildGroundGrid(Bounds bounds)
        {
            float y = bounds.min.y - bounds.extents.y * 0.02f;
            float size = Mathf.Max(bounds.size.x, bounds.size.z) * 2.2f;
            float step = size / 24f;

            var verts = new List<Vector3>();
            var idx = new List<int>();
            for (int i = 0; i <= 24; i++)
            {
                float t = -size * 0.5f + step * i;
                verts.Add(new Vector3(t, y, -size * 0.5f)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(t, y, size * 0.5f)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(-size * 0.5f, y, t)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(size * 0.5f, y, t)); idx.Add(verts.Count - 1);
            }

            _gridMesh = new Mesh { name = "GroundGrid" };
            _gridMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _gridMesh.SetVertices(verts);
            _gridMesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
            _gridMesh.RecalculateBounds();

            Shader sh = Shader.Find("ObjViewer/VertexPoint");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var mat = new Material(sh) { name = "GridMat" };
            mat.SetColor("_ColorA", new Color(0.10f, 0.16f, 0.24f));
            mat.SetColor("_ColorB", new Color(0.10f, 0.16f, 0.24f));
            mat.SetFloat("_HeightMin", -1000f);
            mat.SetFloat("_HeightMax", 1000f);
            mat.SetFloat("_Glow", 1.0f);

            _gridGO = new GameObject("Ground Grid");
            var mf = _gridGO.AddComponent<MeshFilter>();
            mf.sharedMesh = _gridMesh;
            var mr = _gridGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        GameObject _gridGO;

        // ---------------------------------------------------------------- 交互
        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-5f, Time.unscaledDeltaTime), 0.08f);

            if (Input.GetKeyDown(KeyCode.Tab)) showPanel = !showPanel;
            if (Input.GetKeyDown(KeyCode.H)) showHelp = !showHelp;

            if (Input.GetKeyDown(KeyCode.Space))
            {
                _cloud.visible = !_cloud.visible;
                if (_cloud.visible) _cloud.Refresh();
            }

            for (int i = 0; i < ModeKeys.Length; i++)
                if (Input.GetKeyDown(ModeKeys[i])) SetShadingMode((ObjModel.ShadingMode)i);

            if (Input.GetKeyDown(KeyCode.V))
            {
                _model.breatheAnimation = !_model.breatheAnimation;
                if (!_model.breatheAnimation) _model.RestoreRestPose();
            }

            if (Input.GetKeyDown(KeyCode.R)) _orbit.ResetView(_model.ModelBounds);
            if (Input.GetKeyDown(KeyCode.F)) _orbit.Frame(_model.ModelBounds, 1.35f);
            if (Input.GetKeyDown(KeyCode.T)) _orbit.autoRotate = !_orbit.autoRotate;

            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                _cloud.pointScale = Mathf.Clamp(_cloud.pointScale * 1.25f, 0.1f, 20f);
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                _cloud.pointScale = Mathf.Clamp(_cloud.pointScale / 1.25f, 0.1f, 20f);

            if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        }

        void SetShadingMode(ObjModel.ShadingMode mode)
        {
            _model.shadingMode = mode;
            _model.ApplyShadingMode();
        }

        // ---------------------------------------------------------------- 界面
        static Font ChineseFont
        {
            get
            {
                if (_cnFont == null)
                {
                    try
                    {
                        _cnFont = Font.CreateDynamicFontFromOSFont(
                            new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, 15);
                    }
                    catch { _cnFont = null; }
                }
                return _cnFont;
            }
        }

        GUIStyle _box, _label, _title, _hint;
        Texture2D _bg;

        void EnsureStyles()
        {
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.10f, 0.86f));
                _bg.Apply();
            }
            Font f = ChineseFont;
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, richText = true };
                _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
                _hint = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
                if (f != null) { _title.font = f; _label.font = f; _hint.font = f; }
                _title.normal.textColor = new Color(0.55f, 0.88f, 1f);
                _label.normal.textColor = new Color(0.92f, 0.94f, 0.97f);
                _hint.normal.textColor = new Color(0.62f, 0.68f, 0.78f);
                _box = new GUIStyle(GUI.skin.box);
                _box.normal.background = _bg;
            }
        }

        void OnGUI()
        {
            EnsureStyles();
            float scale = Mathf.Clamp(Screen.height / 900f, 0.8f, 2.2f);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);

            if (showPanel) DrawStatsPanel();
            DrawStartupHint(scale);

            GUI.matrix = old;
        }

        /// <summary>进入 Play 后前几秒在屏幕下方显示操作提示（之后自动淡出）。</summary>
        void DrawStartupHint(float scale)
        {
            float t = Time.timeSinceLevelLoad;
            if (t > 9f) return;
            float alpha = t < 6.5f ? 1f : Mathf.InverseLerp(9f, 6.5f, t);

            float sw = Screen.width / scale;
            float sh = Screen.height / scale;
            float w = Mathf.Min(720f, sw - 40f);
            float h = 78f;
            var rect = new Rect(sw * 0.5f - w * 0.5f, sh - h - 26f, w, h);

            string text = "左键拖拽 = 旋转　　右键 / 中键拖拽 = 平移　　滚轮 = 缩放　　← ↑ ↓ → = 旋转\n" +
                          "1 2 3 4 = 着色模式　空格 = 顶点云　V = 呼吸动画　R = 重置视角　Tab = 收起面板";

            var style = new GUIStyle(GUI.skin.label)
            {
                font = _label.font,
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
            };
            style.normal.textColor = new Color(0.90f, 0.95f, 1f, alpha);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(rect, _bg);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, rect.height - 24f), text, style);
        }

        void DrawStatsPanel()
        {
            float w = 372f, x = 14f, y = 14f;
            float scale = Mathf.Clamp(Screen.height / 900f, 0.8f, 2.2f);
            float availH = Screen.height / scale - 28f;       // 面板高度自适应，避免被 Game 视图裁切
            GUILayout.BeginArea(new Rect(x, y, w, Mathf.Min(availH, 760f)), _box);

            GUILayout.Space(8);
            GUILayout.Label("运行时 OBJ 导入与顶点浏览", _title);
            GUILayout.Space(2);
            GUILayout.Label("Unity " + Application.unityVersion + "  ·  Built-in RP", _hint);
            GUILayout.Space(6);

            // ---- 输入自检放在最上方：拖不动时一眼就能看出卡在哪一环 ----
            if (_orbit != null)
            {
                GUILayout.Label(string.Format("鼠标在 Game 视图内  {0}      已识别到拖拽  {1}",
                    _orbit.MouseInsideView ? "<color=#7fe0ff>是</color>"
                                           : "<color=#ff9090>否 ← 鼠标要先移进 Game 窗口</color>",
                    _orbit.EverReceivedDrag ? "<color=#7fe0ff>是</color>" : "<color=#ff9090>否</color>"), _label);
                GUILayout.Label(string.Format("鼠标位移 ({0:F0}, {1:F0}) px    按住鼠标键 {2}    相机 yaw {3:F0}° pitch {4:F0}°",
                    _orbit.LastMouseDelta.x, _orbit.LastMouseDelta.y,
                    _orbit.IsDragging ? "是" : "否", _orbit.yaw, _orbit.pitch), _hint);
            }

            GUILayout.Space(6);

            if (!_model.IsLoaded)
            {
                GUILayout.Label(_model.LoadError != null ? "<color=#ff8080>加载失败: " + _model.LoadError + "</color>" : "正在加载…", _label);
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label(string.Format("模型文件      {0}", System.IO.Path.GetFileName(_model.LoadedFrom)), _label);
            GUILayout.Label(string.Format("原始顶点 v 行   <b>{0:N0}</b>", _model.SourceVertexCount), _label);
            GUILayout.Label(string.Format("Mesh 顶点数    <b>{0:N0}</b>", _model.MeshVertexCount), _label);
            GUILayout.Label(string.Format("三角面数      <b>{0:N0}</b>", _model.TriangleCount), _label);
            GUILayout.Label(string.Format("子网格 / 材质   {0} 个", _model.Parts.Length), _label);
            GUILayout.Label(string.Format("解析+建网格    {0} ms", _model.LoadMilliseconds), _label);
            GUILayout.Label(string.Format("包围盒尺寸     {0:F2} × {1:F2} × {2:F2}",
                _model.ModelBounds.size.x, _model.ModelBounds.size.y, _model.ModelBounds.size.z), _label);
            GUILayout.Label(string.Format("渲染 FPS      {0:F0}", _fps), _label);

            // 顶点数据快照 —— 直接证明"程序拿到了顶点数组"
            IReadOnlyList<Vector3> src = _model.SourceVertices;
            if (src.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label("<b>顶点数据快照（可直接读取）</b>", _label);
                int show = Mathf.Min(3, src.Count);
                for (int i = 0; i < show; i++)
                    GUILayout.Label(string.Format("   v[{0}] = ({1:F3}, {2:F3}, {3:F3})", i, src[i].x, src[i].y, src[i].z), _hint);
                Vector3 c = _model.ComputeVertexCentroid();
                GUILayout.Label(string.Format("   顶点重心 = ({0:F3}, {1:F3}, {2:F3})", c.x, c.y, c.z), _hint);
            }

            GUILayout.Space(8);
            GUILayout.Label(string.Format("着色模式：<color=#7fe0ff>{0}</color>",
                ModeName(_model.shadingMode)), _label);
            GUILayout.Label(string.Format("顶点云：{0}   呼吸动画：{1}",
                _cloud.visible ? "开" : "关", _model.breatheAnimation ? "开" : "关"), _label);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("1 材质", GUILayout.Height(22))) SetShadingMode(ObjModel.ShadingMode.MaterialColor);
            if (GUILayout.Button("2 高度", GUILayout.Height(22))) SetShadingMode(ObjModel.ShadingMode.HeightGradient);
            if (GUILayout.Button("3 法线", GUILayout.Height(22))) SetShadingMode(ObjModel.ShadingMode.NormalColor);
            if (GUILayout.Button("4 线框", GUILayout.Height(22))) SetShadingMode(ObjModel.ShadingMode.Wireframe);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("空格 顶点云", GUILayout.Height(22)))
            { _cloud.visible = !_cloud.visible; if (_cloud.visible) _cloud.Refresh(); }
            if (GUILayout.Button("V 呼吸", GUILayout.Height(22)))
            { _model.breatheAnimation = !_model.breatheAnimation; if (!_model.breatheAnimation) _model.RestoreRestPose(); }
            if (GUILayout.Button("R 重置视角", GUILayout.Height(22))) _orbit.ResetView(_model.ModelBounds);
            GUILayout.EndHorizontal();

            // ---- 视角控制按钮：鼠标拖不动时，直接点这里也能完整浏览 ----
            GUILayout.Space(4);
            GUILayout.Label("<b>视角控制</b>（鼠标拖不动时点这些按钮一样能操作）", _label);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀ 左转", GUILayout.Height(24))) _orbit.SetAngles(_orbit.yaw - 30f, _orbit.pitch);
            if (GUILayout.Button("右转 ▶", GUILayout.Height(24))) _orbit.SetAngles(_orbit.yaw + 30f, _orbit.pitch);
            if (GUILayout.Button("▲ 抬高", GUILayout.Height(24))) _orbit.SetAngles(_orbit.yaw, _orbit.pitch + 12f);
            if (GUILayout.Button("▼ 降低", GUILayout.Height(24))) _orbit.SetAngles(_orbit.yaw, _orbit.pitch - 12f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("放大 +", GUILayout.Height(24))) _orbit.ZoomBy(0.8f);
            if (GUILayout.Button("缩小 −", GUILayout.Height(24))) _orbit.ZoomBy(1.25f);
            if (GUILayout.Button(_orbit.autoRotate ? "停止自转" : "自动旋转", GUILayout.Height(24))) _orbit.autoRotate = !_orbit.autoRotate;
            if (GUILayout.Button("重置视角", GUILayout.Height(24))) _orbit.ResetView(_model.ModelBounds);
            GUILayout.EndHorizontal();

            GUILayout.Label(string.Format("相机  yaw={0:F0}°  pitch={1:F0}°  距离={2:F1}",
                _orbit.yaw, _orbit.pitch, _orbit.distance), _hint);

            if (showHelp)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>操作</b>  左键拖拽=旋转  右键/中键拖拽=平移  滚轮=缩放", _hint);
                GUILayout.Label("方向键=旋转   Tab 收起面板   T 自动旋转   +/- 点大小   Esc 退出", _hint);
            }

            GUILayout.Space(6);
            GUILayout.EndArea();
        }

        static string ModeName(ObjModel.ShadingMode m)
        {
            switch (m)
            {
                case ObjModel.ShadingMode.MaterialColor: return "材质颜色";
                case ObjModel.ShadingMode.HeightGradient: return "顶点高度渐变";
                case ObjModel.ShadingMode.NormalColor: return "法线可视化";
                case ObjModel.ShadingMode.Wireframe: return "线框";
            }
            return m.ToString();
        }
    }
}
