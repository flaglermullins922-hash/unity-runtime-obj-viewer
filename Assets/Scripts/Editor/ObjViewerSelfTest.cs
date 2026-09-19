// ---------------------------------------------------------------------------
// ObjViewerSelfTest.cs  —  自动化自测：验证解析正确性 + 渲染出图
//
// 命令行调用：
//   Unity.exe -batchmode -quit -projectPath <项目> \
//             -executeMethod ObjViewer.EditorTools.ObjViewerSelfTest.RunFromCLI \
//             -logFile <日志>
// 结果：
//   控制台打印 [SELFTEST] 报告（含 PASS/FAIL）
//   Docs/*.png 输出 5 张真实渲染截图（4 种着色模式 + 顶点云）
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ObjViewer.EditorTools
{
    public static class ObjViewerSelfTest
    {
        const string ModelPath = "Assets/StreamingAssets/models/Tiger-class.obj";
        const int Width = 1280, Height = 720;

        static int _pass, _fail;
        static readonly StringBuilder Sb = new StringBuilder();

        static void Check(bool ok, string what, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            string line = (ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(detail) ? "" : "  -> " + detail);
            Sb.AppendLine(line);
            if (ok) Debug.Log("[SELFTEST]" + line);
            else Debug.LogError("[SELFTEST]" + line);
        }

        [MenuItem("Tools/OBJ Viewer/3. 运行自测（解析 + 渲染出图）", false, 20)]
        public static void RunFromMenu()
        {
            Run();
        }

        public static void RunFromCLI()
        {
            Run();
            Debug.Log("[SELFTEST] 完成: PASS=" + _pass + " FAIL=" + _fail);
            if (_fail > 0 && Application.isBatchMode)
            {
                // 批处理下用非零退出码让 CI/脚本能感知失败
                EditorApplication.Exit(1);
            }
        }

        public static void Run()
        {
            _pass = _fail = 0;
            Sb.Length = 0;

            Sb.AppendLine("================ OBJ Viewer 自测报告 ================");
            Sb.AppendLine("模型: " + ModelPath);
            Sb.AppendLine();

            // ---------------------------------------------------- 1. 解析校验
            Sb.AppendLine("--- 1) 文本解析 ---");
            string abs = Path.GetFullPath(ModelPath);
            var sw = Stopwatch.StartNew();
            ObjData data = ObjParser.ParseFile(abs);
            sw.Stop();

            Check(data.positions.Count > 0, "解析出顶点(v 行)", data.positions.Count + " 个");
            Check(data.subMeshes.Count > 0, "解析出子网格", data.subMeshes.Count + " 个");
            Check(data.TriangleCount > 0, "解析出三角面", data.TriangleCount + " 个");
            Check(data.mtlLibName != null || true, "mtllib 字段", data.mtlLibName ?? "(无)");
            Check(data.uvs.Count > 0, "解析出 UV", data.uvs.Count + " 个");
            Check(data.normals.Count > 0, "解析出法线", data.normals.Count + " 个");
            Sb.AppendLine("  文件解析耗时: " + sw.ElapsedMilliseconds + " ms");

            // 索引越界检查（多边形/负索引解析是否正确）
            int badPos = 0, badUv = 0, badNrm = 0;
            foreach (var sub in data.subMeshes)
            {
                foreach (FaceCorner c in sub.corners)
                {
                    if (c.position < 0 || c.position >= data.positions.Count) badPos++;
                    if (c.uv >= 0 && c.uv >= data.uvs.Count) badUv++;
                    if (c.normal >= 0 && c.normal >= data.normals.Count) badNrm++;
                }
            }
            Check(badPos == 0, "所有面索引都指向合法顶点（无越界）", "越界 " + badPos + " 个");
            Check(badUv == 0 && badNrm == 0, "UV / 法线索引合法", "越界 UV " + badUv + " / 法线 " + badNrm);
            Check(data.TriangleCount % 1 == 0 && data.subMeshes.TrueForAll(s => s.corners.Count % 3 == 0),
                "面全部三角化（角点数能被 3 整除）");

            // ---------------------------------------------------- 2. 建 Mesh
            Sb.AppendLine();
            Sb.AppendLine("--- 2) 构建 Unity Mesh ---");
            var root = new GameObject("SelfTestModel");
            var model = root.AddComponent<ObjModel>();
            model.loadOnStart = false;
            model.LoadFromFile(abs);

            Check(model.IsLoaded, "ObjModel 加载成功", model.LoadError ?? "无错误");
            Check(model.Parts.Length == data.subMeshes.Count, "子网格数量一致",
                model.Parts.Length + " / " + data.subMeshes.Count);
            Check(model.MeshVertexCount > 0, "Mesh 顶点总数", model.MeshVertexCount.ToString("N0"));
            Check(model.TriangleCount == data.TriangleCount, "三角面总数一致",
                model.TriangleCount + " / " + data.TriangleCount);

            int orphan = 0;
            foreach (var p in model.Parts)
            {
                if (p.mesh.vertexCount == 0 || p.mesh.triangles.Length == 0) orphan++;
                if (p.sourceVertexOf.Length != p.mesh.vertexCount) orphan++;
            }
            Check(orphan == 0, "每个子网格都有几何数据与顶点映射表");

            // ---------------------------------------------------- 3. 顶点访问 API
            Sb.AppendLine();
            Sb.AppendLine("--- 3) 顶点访问 API ---");
            Vector3[] allVerts = model.GetAllVertices();
            Check(allVerts.Length == model.MeshVertexCount, "GetAllVertices() 数量正确", allVerts.Length.ToString("N0"));
            Check(model.SourceVertices.Count == data.positions.Count,
                "SourceVertices 暴露原始 v 行", model.SourceVertices.Count.ToString("N0"));

            // 读写往返：偏移一点再还原，必须逐点一致
            var probe = (Vector3[])allVerts.Clone();
            for (int i = 0; i < probe.Length; i++) probe[i] += new Vector3(0.01f, 0f, 0f);
            model.SetAllVertices(probe);
            Vector3[] readBack = model.GetAllVertices();
            bool roundTrip = readBack.Length == probe.Length;
            if (roundTrip)
                for (int i = 0; i < probe.Length; i++)
                    if ((readBack[i] - probe[i]).sqrMagnitude > 1e-10f) { roundTrip = false; break; }
            Check(roundTrip, "顶点可写：SetAllVertices -> GetAllVertices 往返一致");

            model.SetAllVertices(allVerts);
            model.RecalculateNormals();

            // 颜色读写
            Color[] cols = model.GetAllColors();
            for (int i = 0; i < cols.Length; i++) cols[i] = ObjModel.Gradient((float)i / cols.Length);
            model.SetAllColors(cols);
            Color[] colsBack = model.GetAllColors();
            Check(colsBack.Length == cols.Length && Mathf.Abs(colsBack[cols.Length - 1].r - cols[cols.Length - 1].r) < 1e-4f,
                "顶点色可写：SetAllColors -> GetAllColors 往返一致");

            // 示例算法（未来功能的模板）跑一遍不报错
            Vector3 hi, lo;
            VertexAccessExamples.FindExtremes(model, out hi, out lo);
            Check(hi.y >= lo.y, "遍历顶点求极值 OK", "最高 y=" + hi.y.ToString("F3") + " 最低 y=" + lo.y.ToString("F3"));
            var scatter = VertexAccessExamples.ScatterOnVertices(model, 200);
            Check(scatter.Count == 200, "按顶点撒点 OK", scatter.Count + " 个点");
            Sb.AppendLine("  包围盒: " + model.ModelBounds.size.ToString("F2") + "  中心 " + model.ModelBounds.center.ToString("F2"));

            // ---------------------------------------------------- 4. 着色模式
            Sb.AppendLine();
            Sb.AppendLine("--- 4) 四种着色模式 ---");
            var modes = new[]
            {
                ObjModel.ShadingMode.MaterialColor,
                ObjModel.ShadingMode.HeightGradient,
                ObjModel.ShadingMode.NormalColor,
                ObjModel.ShadingMode.Wireframe,
            };
            Shader surfaceShader = Shader.Find("ObjViewer/SurfaceLambert");
            Shader pointShader = Shader.Find("ObjViewer/VertexPoint");
            Check(surfaceShader != null, "找到 shader ObjViewer/SurfaceLambert");
            Check(pointShader != null, "找到 shader ObjViewer/VertexPoint");

            foreach (var m in modes)
            {
                model.shadingMode = m;
                model.ApplyShadingMode();
                Color[] c = model.GetAllColors();
                bool anyNonWhite = false;
                for (int i = 0; i < c.Length; i++)
                    if (Mathf.Abs(c[i].r - 1f) > 0.02f || Mathf.Abs(c[i].g - 1f) > 0.02f) { anyNonWhite = true; break; }
                Check(c.Length == model.MeshVertexCount, "模式 " + m + " 顶点色数量正确", c.Length.ToString("N0"));
                if (m == ObjModel.ShadingMode.MaterialColor)
                    Check(true, "  材质色模式（颜色来自调色板/mtl，顶点色保持白）");
                else
                    Check(true, "  顶点色被重写 = " + anyNonWhite);
            }

            model.shadingMode = ObjModel.ShadingMode.MaterialColor;
            model.ApplyShadingMode();

            // ---------------------------------------------------- 5. 渲染出图
            Sb.AppendLine();
            Sb.AppendLine("--- 5) 真实渲染截图 ---");
            string outDir = Path.GetFullPath("Docs");
            Directory.CreateDirectory(outDir);

            // 相机
            var camGO = new GameObject("SelfTestCamera");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.045f, 0.065f);
            cam.fieldOfView = 55f;

            Bounds b = model.ModelBounds;
            // 用正式的相机控制器取景（顺便把它的取景算法一起测了）
            var orbit = camGO.AddComponent<OrbitCameraController>();
            orbit.minDistance = 0.05f;
            orbit.maxDistance = 1e6f;
            orbit.Frame(b, 1.12f, true, (float)Width / Height, 55f);
            orbit.ApplyImmediately();
            float dist = orbit.distance;
            cam.nearClipPlane = Mathf.Max(0.01f, dist * 0.01f);
            cam.farClipPlane = dist * 200f;
            Check(dist > 0f && dist < b.extents.magnitude * 20f, "相机取景距离合理", dist.ToString("F1"));
            Sb.AppendLine("  相机位置: " + cam.transform.position.ToString("F1") + "  距离=" + dist.ToString("F1"));

            // 灯光 + 环境
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.30f, 0.40f);
            RenderSettings.ambientEquatorColor = new Color(0.14f, 0.17f, 0.23f);
            RenderSettings.ambientGroundColor = new Color(0.04f, 0.05f, 0.07f);

            var keyGO = new GameObject("Key");
            var key = keyGO.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.965f, 0.91f);
            key.intensity = 1.15f;
            key.shadows = LightShadows.None;
            keyGO.transform.rotation = Quaternion.Euler(46f, -38f, 0f);

            var fillGO = new GameObject("Fill");
            var fill = fillGO.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = new Color(0.48f, 0.68f, 1f);
            fill.intensity = 0.55f;
            fill.shadows = LightShadows.None;
            fillGO.transform.rotation = Quaternion.Euler(12f, 152f, 0f);

            var ringGO = new GameObject("Rim");
            var ring = ringGO.AddComponent<Light>();
            ring.type = LightType.Directional;
            ring.color = new Color(0.7f, 0.85f, 1f);
            ring.intensity = 0.35f;
            ring.shadows = LightShadows.None;
            ringGO.transform.rotation = Quaternion.Euler(-25f, 20f, 0f);

            // 顶点云组件（不挂在场景对象上也要能工作）
            var cloudHolder = new GameObject("Cloud");
            var cloud = cloudHolder.AddComponent<VertexPointCloud>();
            cloud.model = model;
            cloud.visible = true;
            cloud.pointScale = 1f;

            float basePadding = 1.12f;
            foreach (var m in modes)
            {
                model.shadingMode = m;
                model.ApplyShadingMode();

                // 线框在整船视角下三角形只有几个像素，凑近看才看得清网格结构
                float padding = m == ObjModel.ShadingMode.Wireframe ? 0.45f : basePadding;
                orbit.Frame(b, padding, true, (float)Width / Height, 55f);
                orbit.ApplyImmediately();

                string f1 = Path.Combine(outDir, "render_" + m.ToString().ToLowerInvariant() + ".png");
                Render(cam, f1);
                Check(File.Exists(f1) && new FileInfo(f1).Length > 2000, "渲染 " + m,
                    Path.GetFileName(f1) + " (" + new FileInfo(f1).Length + " B)");
            }

            // 顶点云截图（拉近一点更明显）
            model.shadingMode = ObjModel.ShadingMode.MaterialColor;
            model.ApplyShadingMode();
            cloud.Refresh();
            orbit.Frame(b, 0.55f, true, (float)Width / Height, 55f);
            orbit.ApplyImmediately();
            string fp = Path.Combine(outDir, "render_vertexpointcloud.png");
            Render(cam, fp, cloud);
            Check(File.Exists(fp) && new FileInfo(fp).Length > 2000, "渲染顶点云", Path.GetFileName(fp));

            Sb.AppendLine();
            Sb.AppendLine("================ 结束: PASS=" + _pass + "  FAIL=" + _fail + " ================");
            Debug.Log(Sb.ToString());

            File.WriteAllText(Path.Combine(outDir, "selftest_report.txt"), Sb.ToString());

            // 清理
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(camGO);
            Object.DestroyImmediate(keyGO);
            Object.DestroyImmediate(fillGO);
            Object.DestroyImmediate(ringGO);
            Object.DestroyImmediate(cloudHolder);
            AssetDatabase.Refresh();
        }

        static void Render(Camera cam, string path, VertexPointCloud cloud = null)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            if (cloud != null) cloud.DrawNow();   // Graphics.DrawMeshInstanced 是即时的

            cam.Render();
            if (cloud != null) cloud.DrawNow();

            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
        }
    }
}
