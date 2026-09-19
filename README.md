# Unity 运行时 OBJ 模型导入与浏览（Runtime OBJ Viewer）

> 课程作业：把一个 `.obj` 模型导入 Unity 场景中并浏览。
> 要求 ①「向大模型学习实现方法」→ 见 [第一节](#一实现方法怎么来的含-ai-咨询记录)；
> 要求 ②「所选方法能访问模型顶点，便于未来加功能」→ 见 [第三节](#三顶点访问-api作业要求-2-的核心)。

打开工程 → 按 Play 即可浏览模型，无需任何手工拖拽配置。

| 项目 | 值 |
|---|---|
| Unity 版本 | 2022.3.62f3c1 |
| 渲染管线 | Built-in RP |
| 演示模型 | Tiger-class.obj（33,974 个顶点 / 49,489 个三角面 / 14 个子网格） |
| 实测解析+建网格耗时 | **94 ~ 400 ms**（含 3.8 MB 文本解析与 Mesh 构建） |
| 自动化自测 | 36 项断言全部通过（见 [第六节](#六自动化自测)） |
| 演示视频 | [`Docs/demo_20s.mp4`](Docs/demo_20s.mp4)（20.0 秒 / 1280×720 / 30 fps） |

---

## 一、实现方法怎么来的（含 AI 咨询记录）

先向大模型/检索确认「Unity 加载 OBJ 模型」有哪几条路，再把它们摆在一起比较。

### 三条可选路线

| 路线 | 做法 | 能否访问顶点 | 结论 |
|---|---|---|---|
| **A. 编辑器内置导入** | 把 `.obj` 拖进 `Assets/`，Unity 的 ModelImporter 自动烘焙成 Mesh 资产 | 能（`Mesh.vertices`），但顶点已被 Unity 加工过：去重、拼合、坐标转换、单位缩放都由导入器决定，且**无法在运行时重新导入或换模型** | ❌ 不选 |
| **B. 运行时自己解析**（本项目采用） | 运行时读 `.obj` 文本 → 自己建 `Mesh` | **完全掌控**：顶点表、三角形索引、UV、法线全在自己手里，且 Mesh 天然可读写，随时能改顶点 | ✅ **选它** |
| **C. 第三方运行时加载库** | 如 Runtime OBJ Loader、UnityObjParser、FastObjImporter | 能，但属于黑盒：顶点数据的组织方式由库决定，后续要加顶点级功能得先读懂并改造别人的代码 | ❌ 不选 |

**选 B 的理由**：作业要求 ② 明确说「方法应该能够访问到模型顶点，便于未来增加其他功能」。
路线 A 的顶点是「Unity 导入器的产物」，路线 C 的顶点是「第三方库的产物」，只有路线 B 的顶点是**我们自己解析出来的原始数据**——
既能拿到 OBJ 文件里逐行的 `v` 顶点表，也能拿到去重后真正送进 GPU 的顶点数组，两者可以互相对照。后续想加顶点动画、顶点着色、模型编辑等功能，直接改数组即可，不需要对抗任何中间层。

### 因此本项目的技术要点

1. **自己写 OBJ 解析器**，而不是调 `AssetDatabase` 或第三方库；
2. **保留两份顶点**：`DataSource.positions`（OBJ 原始 `v` 行，一对一）与 `Mesh.vertices`（Unity 去重后的顶点），并保存两者之间的映射表；
3. **自己建 Mesh 并自己管材质**，让 Mesh 始终处于「可读写」状态。

解析器支持：`v / vn / vt`、带顶点色的 `v x y z r g b`、四种面索引写法（`v`、`v/vt`、`v//vn`、`v/vt/vn`）、**负数索引**、**n 边形面自动扇形三角化**、`o/g/usemtl` 子网格切分、`mtllib` 与 MTL 材质色（`Kd/Ka/Ks/Ns/d`）。

> 实际踩到的坑：演示模型只提供了 9,598 条面法线，却有 33,974 个顶点。
> 缺失的顶点法线如果留成 `(0,0,0)`，shader 里 `normalize` 会算出 NaN 导致渲染异常。
> 处理方式是：先整网格自动算法线，再把文件里**原本写明**的法线覆盖回去，两类顶点都正确。

---

## 二、项目结构

```
Assets/
├── Scripts/
│   ├── Runtime/                     # 运行时脚本（游戏逻辑）
│   │   ├── ObjParsing.cs            #   OBJ / MTL 文本解析（纯数据层，不依赖渲染）
│   │   ├── ObjMeshBuilder.cs        #   ObjData → Unity Mesh，含顶点映射表
│   │   ├── ObjModel.cs              # ★核心：加载 / 渲染 / 顶点访问 API / 着色模式
│   │   ├── OrbitCameraController.cs #   浏览相机：旋转 / 平移 / 缩放 / 精确取景
│   │   ├── VertexPointCloud.cs      #   顶点可视化（每个顶点画一个小方块）
│   │   ├── ObjViewerController.cs   #   总控：自动搭场景 / 键盘操作 / 信息面板
│   │   └── VertexAccessExamples.cs  #   顶点访问示例集（未来功能的模板）
│   └── Editor/                      # 仅编辑器用
│       ├── ObjViewerSceneBuilder.cs #   一键生成演示场景 + Build Settings
│       ├── ObjViewerSelfTest.cs     #   36 项自动化自测（解析/API/渲染出图）
│       └── DemoVideoRecorder.cs     #   逐帧渲染 20 秒演示视频（管道喂给 ffmpeg）
├── Shaders/
│   ├── SurfaceLambert.shader        #   顶点色 + 半兰伯特 + 边缘光
│   └── VertexPoint.shader           #   顶点云/参考网格（GPU Instancing）
├── StreamingAssets/models/
│   └── Tiger-class.obj              # 演示模型（运行时从这里读取）
└── Scenes/ObjViewerDemo.unity       # 演示场景（空场景 + 一个控制器对象）

Docs/                                # 自测报告、渲染截图、演示视频
```

---

## 三、顶点访问 API（作业要求 ② 的核心）

全部集中在 `ObjModel` 上，拿到组件后即可使用：

| 成员 | 说明 |
|---|---|
| `ObjData Data` | 解析出来的完整原始数据：`positions`（`v` 行）、`uvs`、`normals`、`subMeshes`、`materials` |
| `IReadOnlyList<Vector3> SourceVertices` | **OBJ 文件里逐行的原始顶点**（一回事，未去重、未变换） |
| `ObjMeshPart[] Parts` | 每个子网格：`mesh`、`renderer`、`material`、`sourceVertexOf`（Mesh 顶点 → OBJ `v` 行号的反查表） |
| `Vector3[] GetVertices(int partIndex)` | 取某个子网格的 Unity 顶点数组 |
| `Vector3[] GetAllVertices()` | 合并全部子网格顶点为**一个数组** |
| `void SetAllVertices(Vector3[])` | **把顶点数组写回 Mesh** ← 顶点级功能的总入口 |
| `Color[] GetAllColors()` / `SetAllColors(Color[])` | 顶点色读写 |
| `void RecalculateNormals()` | 顶点改完后重算法线并刷新 |
| `void RestoreRestPose()` | 恢复初始顶点 |
| `event Action<ObjModel> Loaded` | 加载完成事件 |

`Assets/Scripts/Runtime/VertexAccessExamples.cs` 里给了 7 个「未来功能」模板，都是直接消费上面的 API：

1. `FindExtremes` —— 遍历全部顶点求最高/最低点
2. `ScaleVertices` —— 以顶点重心为中心整体缩放
3. `ApplyHeightHeatmap` —— 按顶点高度写顶点色
4. `JitterVertices` —— 顶点随机抖动（爆炸/受击形变）
5. `DescribeSourceMapping` —— 把 Mesh 顶点反查回 OBJ 的 `v` 行号
6. `ListSubMeshes` —— 列出子网格与材质
7. `ScatterOnVertices` —— 在模型表面按顶点撒点

程序内还有两个**已经在跑**的顶点级功能，用来证明 API 真的能用：
**顶点云显示**（`VertexPointCloud.cs` 把每个顶点画成一个小方块）与**顶点呼吸动画**（`ObjModel.AnimateBreathe` 沿法线做正弦位移）。

---

## 四、怎么运行

### 编辑器里

1. Unity Hub 用 **2022.3.62f3c1** 打开本工程；
2. 打开场景 `Assets/Scenes/ObjViewerDemo.unity`（或任意空场景）；
3. 按 **Play**。相机、灯光、环境、模型全部由 `ObjViewerController` 在运行时自动创建，不需要手工拖任何东西。

> 也可以从菜单 `Tools → OBJ Viewer → 1. 生成演示场景并加入 Build Settings` 重新生成场景。

### 操作

| 操作 | 功能 |
|---|---|
| 左键拖拽 | 旋转（轨道） |
| 右键 / 中键拖拽 | 平移 |
| 滚轮 | 缩放 |
| `1` `2` `3` `4` | 着色模式：材质色 / 顶点高度渐变 / 法线可视化 / 线框 |
| `空格` | 顶点云开关（每个顶点一个小方块） |
| `V` | 顶点呼吸动画（沿法线位移，演示顶点可写） |
| `R` / `F` / `T` | 重置视角 / 重新取景 / 自动旋转 |
| `+` `-` | 顶点云点大小 |
| `Tab` / `H` | 收起信息面板 / 收起操作提示 |
| `Esc` | 退出（构建版） |

信息面板会实时显示：原始顶点数、Mesh 顶点数、三角面数、子网格数、解析耗时、包围盒尺寸、FPS，
以及**前 3 个顶点的坐标快照与顶点重心** —— 直接证明程序确实读到了顶点数据。

### 换成自己的模型

1. 把 `.obj` 放到 `Assets/StreamingAssets/models/`；
2. 场景里 `[ObjViewer]` 对象的 `Obj Viewer Controller → Model File Name` 填新文件名；
3. 如果模型自带同名 `.mtl`，放在同一目录即可自动读取材质颜色；没有 `.mtl` 时会用内置调色板给子网格分配颜色。
4. 相机取景是按模型包围盒自动计算的，模型多大都能自动框好。

---

## 五、运行时加载 vs 编辑器导入：实测对比

| 对比项 | 编辑器导入（路线 A） | 本项目（路线 B） |
|---|---|---|
| 顶点是否可写 | 需在 Import 设置里开 Read/Write，且改的是导入器产物 | 天然可写（运行时 `new Mesh()`） |
| 能否运行时换模型 | ❌ 要重新导入资源 | ✅ 换个文件名即可，也可以从磁盘任意路径加载 |
| 原始 `v` 顶点表 | 拿不到（导入时被合并） | ✅ `SourceVertices` 原样保留 |
| Mesh 顶点 ↔ OBJ `v` 行映射 | 无 | ✅ `sourceVertexOf` 反查表 |
| 依赖 | Unity 导入器行为 | 自己的解析器（约 300 行） |
| 加载耗时 | 导入一次后瞬开 | 94~400 ms（本项目实测） |

---

## 六、自动化自测

`Tools → OBJ Viewer → 3. 运行自测（解析 + 渲染出图）`，或命令行：

```bash
Unity.exe -batchmode -quit -projectPath <项目路径> \
  -executeMethod ObjViewer.EditorTools.ObjViewerSelfTest.RunFromCLI \
  -logFile selftest.log
```

覆盖 36 项断言，全部通过（报告见 [`Docs/selftest_report.txt`](Docs/selftest_report.txt)）：

* **文本解析**：顶点/UV/法线/三角面数量、索引越界检查、多边形三角化完整性
* **建 Mesh**：子网格数、顶点数、三角面数一致性、顶点映射表
* **顶点 API**：`GetAllVertices` 数量、**写入→读回 往返一致性**、顶点色往返、极值/撒点示例
* **着色模式**：4 种模式的顶点色数量与内容
* **真实渲染**：渲染出 5 张 PNG 并校验文件有效（`Docs/render_*.png`）

---

## 七、演示视频

[`Docs/demo_20s.mp4`](Docs/demo_20s.mp4)（**20.0 秒** / 1280×720 / 30 fps / H.264）

视频不是屏幕录制，而是 `DemoVideoRecorder.cs` 在 Unity 里**逐帧渲染**、用管道直送 ffmpeg 合成的，
所以帧率绝对稳定、可重复生成。时间轴：

| 时间 | 内容 |
|---|---|
| 0 – 4 s | 材质色模式，相机环绕 |
| 4 – 7 s | 切换到**顶点高度渐变**（顶点色由顶点 Y 坐标算出来） |
| 7 – 11 s | 打开**顶点云**，镜头推近看每个顶点 |
| 11 – 14.5 s | **顶点呼吸动画**（沿法线位移，证明顶点可写） |
| 14.5 – 17.5 s | **线框**模式，能看清三角剖分 |
| 17.5 – 20 s | **法线可视化**，拉远收尾 |

重新生成视频：

```bash
Unity.exe -batchmode -quit -projectPath <项目路径> \
  -executeMethod ObjViewer.EditorTools.DemoVideoRecorder.RunFromCLI \
  -logFile video.log
```

---

## 八、说明

* 演示模型 `Tiger-class.obj` 来自公开的 BSG Tiger-class (fanon) 模型，文件内 `o` 字段标注的作者为
  `EVE-Kaneda_NepsterCZ`，仅用于课程作业演示，版权归原作者。
* 本工程不含 `Library/` 等 Unity 生成目录（见 `.gitignore`），克隆后用 Unity 2022.3.62 打开即可。
