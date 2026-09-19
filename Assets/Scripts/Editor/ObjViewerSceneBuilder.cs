// ---------------------------------------------------------------------------
// ObjViewerSceneBuilder.cs  —  编辑器辅助：一键生成可运行的演示场景
//
// 菜单：Tools / OBJ Viewer / ...
// 也可以在命令行批处理里调用 BuildSceneFromCLI()，实现无人值守搭建。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ObjViewer.EditorTools
{
    public static class ObjViewerSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/ObjViewerDemo.unity";
        const string ModelFile = "Tiger-class.obj";

        [MenuItem("Tools/OBJ Viewer/1. 生成演示场景并加入 Build Settings", false, 10)]
        public static void BuildSceneMenu()
        {
            BuildScene();
            EditorUtility.DisplayDialog("OBJ Viewer",
                "演示场景已生成：\n" + ScenePath + "\n\n直接点 Play 即可浏览模型。", "好");
        }

        [MenuItem("Tools/OBJ Viewer/2. 应用 Player Settings", false, 11)]
        public static void ApplyPlayerSettingsMenu()
        {
            ApplyPlayerSettings();
            EditorUtility.DisplayDialog("OBJ Viewer", "Player Settings 已应用。", "好");
        }

        /// <summary>供命令行 -executeMethod 调用。</summary>
        public static void BuildSceneFromCLI()
        {
            ApplyPlayerSettings();
            BuildScene();
            Debug.Log("[ObjViewerSceneBuilder] CLI 构建完成: " + ScenePath);
        }

        public static void BuildScene()
        {
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var controller = new GameObject("[ObjViewer]");
            var ctrl = controller.AddComponent<ObjViewerController>();
            ctrl.modelFileName = ModelFile;

            var camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.045f, 0.065f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 6000f;
            cam.fieldOfView = 55f;
            camGO.AddComponent<AudioListener>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            // 加入 Build Settings，保证打包后第一个场景就是它
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool found = list.Exists(s => s.path == ScenePath);
            if (!found) list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();

            Debug.Log("[ObjViewerSceneBuilder] 场景已生成: " + ScenePath);
        }

        public static void ApplyPlayerSettings()
        {
            PlayerSettings.companyName = "ZhuYinHe";
            PlayerSettings.productName = "Runtime OBJ Viewer";
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
            EditorPrefs.SetBool("ObjViewer.PlayerSettingsApplied", true);
            Debug.Log("[ObjViewerSceneBuilder] Player Settings 已应用");
        }
    }
}
