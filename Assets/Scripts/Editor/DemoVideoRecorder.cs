// ---------------------------------------------------------------------------
// DemoVideoRecorder.cs  —  批处理逐帧渲染演示视频（20 秒 / 30fps / 1280x720）
//
// 思路：不用屏幕录制软件，直接在 Unity 里按固定步长推进动画，每帧渲染到
//       RenderTexture → 原始 RGB 像素通过管道喂给 ffmpeg → 输出 H.264 MP4。
//       好处：帧率绝对稳定、无桌面干扰、可重复生成。
//
// 命令行：
//   Unity.exe -batchmode -quit -projectPath <项目> \
//     -executeMethod ObjViewer.EditorTools.DemoVideoRecorder.RunFromCLI \
//     -logFile <日志>
//   环境变量 FFMPEG_PATH 可指定 ffmpeg.exe 位置。
// ---------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ObjViewer.EditorTools
{
    public static class DemoVideoRecorder
    {
        const string ModelPath = "Assets/StreamingAssets/models/Tiger-class.obj";
        const int Width = 1280, Height = 720, Fps = 30;
        const int TotalFrames = Fps * 20;                 // 20 秒
        const string OutVideo = "Docs/demo_20s.mp4";

        static string FindFfmpeg()
        {
            string env = System.Environment.GetEnvironmentVariable("FFMPEG_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            string winget = @"C:\Users\a\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe";
            if (File.Exists(winget)) return winget;

            foreach (string dir in System.Environment.GetEnvironmentVariable("PATH").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        [MenuItem("Tools/OBJ Viewer/4. 渲染 20 秒演示视频", false, 30)]
        public static void RunFromMenu() { Run(); }

        public static void RunFromCLI()
        {
            int rc = Run();
            if (rc != 0 && Application.isBatchMode) EditorApplication.Exit(rc);
        }

        public static int Run()
        {
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                Debug.LogError("[DEMOVIDEO] 找不到 ffmpeg.exe，请设置环境变量 FFMPEG_PATH");
                return 2;
            }
            Debug.Log("[DEMOVIDEO] ffmpeg = " + ffmpeg);

            // ---------------------------------------------------- 场景搭建
            string abs = Path.GetFullPath(ModelPath);
            var root = new GameObject("DemoModel");
            var model = root.AddComponent<ObjModel>();
            model.loadOnStart = false;
            model.LoadFromFile(abs);
            if (!model.IsLoaded)
            {
                Debug.LogError("[DEMOVIDEO] 模型加载失败: " + model.LoadError);
                return 3;
            }

            var camGO = new GameObject("DemoCamera");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.045f, 0.065f);
            cam.fieldOfView = 55f;
            cam.allowHDR = true;

            var orbit = camGO.AddComponent<OrbitCameraController>();
            orbit.minDistance = 0.05f;
            orbit.maxDistance = 1e6f;

            Bounds b = model.ModelBounds;
            float halfDiag = Mathf.Max(1f, b.extents.magnitude);

            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.30f, 0.40f);
            RenderSettings.ambientEquatorColor = new Color(0.14f, 0.17f, 0.23f);
            RenderSettings.ambientGroundColor = new Color(0.04f, 0.05f, 0.07f);

            MakeLight("Key", new Color(1f, 0.965f, 0.91f), 1.15f, new Vector3(46f, -38f, 0f));
            MakeLight("Fill", new Color(0.48f, 0.68f, 1f), 0.55f, new Vector3(12f, 152f, 0f));
            MakeLight("Rim", new Color(0.70f, 0.85f, 1f), 0.35f, new Vector3(-25f, 20f, 0f));

            // 地面参考网格（与运行时一致）
            var gridGO = BuildGrid(b);

            // 顶点云
            var cloudGO = new GameObject("Cloud");
            var cloud = cloudGO.AddComponent<VertexPointCloud>();
            cloud.model = model;
            cloud.visible = false;
            cloud.pointScale = 0.6f;
            cloud.Refresh();

            // ---------------------------------------------------- ffmpeg 管道
            string outPath = Path.GetFullPath(OutVideo);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            if (File.Exists(outPath)) File.Delete(outPath);

            string args = string.Format(CultureInfo.InvariantCulture,
                "-y -f rawvideo -pix_fmt rgb24 -s {0}x{1} -r {2} -i - -an -c:v libx264 -preset medium -crf 19 -pix_fmt yuv420p -movflags +faststart \"{3}\"",
                Width, Height, Fps, outPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                Debug.LogError("[DEMOVIDEO] 无法启动 ffmpeg");
                return 4;
            }

            // ffmpeg 的 stderr 必须持续读走，否则管道写满会卡死
            var errBuf = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errBuf.AppendLine(e.Data); };
            proc.BeginErrorReadLine();

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            var rowBytes = Width * 3;
            var buffer = new byte[rowBytes * Height];
            var flipped = new byte[rowBytes * Height];
            Stream stdin = proc.StandardInput.BaseStream;

            float aspect = (float)Width / Height;
            var sw = Stopwatch.StartNew();
            bool wasBreathing = false;

            // ---------------------------------------------------- 逐帧渲染
            for (int frame = 0; frame < TotalFrames; frame++)
            {
                float t = frame / (float)Fps;                        // 秒
                float progress = frame / (float)(TotalFrames - 1);   // 0..1

                // --- 时间轴编排 -------------------------------------------
                // 0-4s   材质色 + 环绕
                // 4-7s   高度渐变
                // 7-11s  顶点云（拉近）
                // 11-14.5s 呼吸动画（顶点位移）
                // 14.5-17.5s 线框（近景）
                // 17.5-20s 法线可视化 → 收尾拉远
                ObjModel.ShadingMode mode;
                float padding;
                float yaw, pitch;
                bool points = false;
                bool breathe = false;

                if (t < 4f)
                {
                    mode = ObjModel.ShadingMode.MaterialColor;
                    padding = 1.02f - 0.10f * (t / 4f);
                    yaw = 18f + 34f * (t / 4f);
                    pitch = 16f + 5f * (t / 4f);
                }
                else if (t < 7f)
                {
                    float u = (t - 4f) / 3f;
                    mode = ObjModel.ShadingMode.HeightGradient;
                    padding = 0.92f - 0.08f * u;
                    yaw = 52f + 26f * u;
                    pitch = 21f - 6f * u;
                }
                else if (t < 11f)
                {
                    float u = (t - 7f) / 4f;
                    mode = ObjModel.ShadingMode.MaterialColor;
                    padding = 0.72f - 0.32f * u;
                    yaw = 78f + 20f * u;
                    pitch = 15f + 6f * u;
                    points = true;
                }
                else if (t < 14.5f)
                {
                    float u = (t - 11f) / 3.5f;
                    mode = ObjModel.ShadingMode.MaterialColor;
                    padding = 0.78f + 0.06f * u;
                    yaw = 98f + 24f * u;
                    pitch = 21f - 5f * u;
                    breathe = true;
                }
                else if (t < 17.5f)
                {
                    float u = (t - 14.5f) / 3f;
                    mode = ObjModel.ShadingMode.Wireframe;
                    padding = 0.46f + 0.08f * u;
                    yaw = 122f + 20f * u;
                    pitch = 16f + 4f * u;
                }
                else
                {
                    float u = (t - 17.5f) / 2.5f;
                    mode = ObjModel.ShadingMode.NormalColor;
                    padding = 0.95f + 0.35f * u;
                    yaw = 142f + 22f * u;
                    pitch = 20f - 4f * u;
                }

                // --- 应用状态 ---------------------------------------------
                if (model.shadingMode != mode)
                {
                    model.shadingMode = mode;
                    model.ApplyShadingMode();
                }

                if (cloud.visible != points)
                {
                    cloud.visible = points;
                    if (points) cloud.Refresh();
                }

                if (breathe)
                {
                    float amp = 0.018f * Mathf.Sin(Mathf.PI * Mathf.Clamp01((t - 11f) / 3.5f));
                    model.AnimateBreathe(t * 2.0f, amp);
                }
                else if (wasBreathing)
                {
                    model.RestoreRestPose();     // 呼吸阶段结束时还原一次
                }
                wasBreathing = breathe;

                // --- 相机 -------------------------------------------------
                // 用当前 yaw/pitch 精确取景，构图稳定不抖
                SetOrbit(orbit, cam, b, yaw, pitch, padding, aspect);

                // --- 渲染 -------------------------------------------------
                if (cloud.visible)
                {
                    cloud.Refresh();       // 呼吸时顶点在动，每帧重采样
                    cloud.DrawNow();
                }

                cam.Render();

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                byte[] raw = tex.GetRawTextureData();
                System.Array.Copy(raw, buffer, Mathf.Min(raw.Length, buffer.Length));

                // RenderTexture 是自下而上的，写管道前翻转成自上而下
                for (int row = 0; row < Height; row++)
                    System.Array.Copy(buffer, row * rowBytes, flipped, (Height - 1 - row) * rowBytes, rowBytes);

                stdin.Write(flipped, 0, flipped.Length);

                if (frame % 60 == 0)
                    Debug.Log(string.Format("[DEMOVIDEO] 帧 {0}/{1}  t={2:F1}s", frame, TotalFrames, t));
            }

            stdin.Flush();
            stdin.Close();
            proc.WaitForExit(120000);
            sw.Stop();

            bool ok = File.Exists(outPath) && new FileInfo(outPath).Length > 10000;
            if (ok)
                Debug.Log(string.Format("[DEMOVIDEO] 完成: {0}  ({1:N0} 字节, {2:F1} 秒渲染, {3} 帧)",
                    outPath, new FileInfo(outPath).Length, sw.Elapsed.TotalSeconds, TotalFrames));
            else
            {
                Debug.LogError("[DEMOVIDEO] 输出文件异常，ffmpeg 日志:\n" + errBuf.ToString());
                return 5;
            }

            // 清理
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(camGO);
            Object.DestroyImmediate(cloudGO);
            if (gridGO != null) Object.DestroyImmediate(gridGO);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            AssetDatabase.Refresh();
            return 0;
        }

        /// <summary>用固定的 yaw/pitch/padding 摆好相机（等价于 OrbitCameraController.Frame + ApplyImmediately）。</summary>
        static void SetOrbit(OrbitCameraController orbit, Camera cam, Bounds b, float yaw, float pitch, float padding, float aspect)
        {
            orbit.SetAngles(yaw, pitch);          // 必须走 SetAngles：Frame() 读的是内部 _desiredYaw/_desiredPitch
            orbit.Frame(b, padding, true, aspect, cam.fieldOfView);
            orbit.ApplyImmediately();
        }

        static void MakeLight(string name, Color color, float intensity, Vector3 euler)
        {
            var go = new GameObject(name);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = color;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            go.transform.rotation = Quaternion.Euler(euler);
        }

        static GameObject BuildGrid(Bounds bounds)
        {
            float y = bounds.min.y - bounds.extents.y * 0.02f;
            float size = Mathf.Max(bounds.size.x, bounds.size.z) * 2.2f;
            float step = size / 24f;

            var verts = new System.Collections.Generic.List<Vector3>();
            var idx = new System.Collections.Generic.List<int>();
            for (int i = 0; i <= 24; i++)
            {
                float t = -size * 0.5f + step * i;
                verts.Add(new Vector3(t, y, -size * 0.5f)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(t, y, size * 0.5f)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(-size * 0.5f, y, t)); idx.Add(verts.Count - 1);
                verts.Add(new Vector3(size * 0.5f, y, t)); idx.Add(verts.Count - 1);
            }

            var mesh = new Mesh { name = "DemoGrid" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            Shader sh = Shader.Find("ObjViewer/VertexPoint");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var mat = new Material(sh) { name = "DemoGridMat" };
            mat.enableInstancing = true;
            mat.SetColor("_ColorA", new Color(0.10f, 0.16f, 0.24f));
            mat.SetColor("_ColorB", new Color(0.10f, 0.16f, 0.24f));
            mat.SetFloat("_HeightMin", -1000f);
            mat.SetFloat("_HeightMax", 1000f);
            mat.SetFloat("_Glow", 1.0f);

            var go = new GameObject("DemoGrid");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }
    }
}
