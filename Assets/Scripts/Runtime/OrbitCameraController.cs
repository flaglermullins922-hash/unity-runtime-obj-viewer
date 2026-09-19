// ---------------------------------------------------------------------------
// OrbitCameraController.cs  —  模型浏览相机：左键旋转 / 右键平移 / 滚轮缩放
// ---------------------------------------------------------------------------

using UnityEngine;

namespace ObjViewer
{
    public sealed class OrbitCameraController : MonoBehaviour
    {
        public Transform pivot;                 // 环绕中心（可为空，用内部位置）
        public float distance = 10f;
        public float minDistance = 0.05f;
        public float maxDistance = 100000f;

        [Header("角度")]
        public float yaw = 35f;
        public float pitch = 18f;
        public float minPitch = -89f;
        public float maxPitch = 89f;

        [Header("手感")]
        public float rotateDegPerPixel = 0.30f;   // 左键拖拽：度 / 像素
        public float panSpeed = 1f;
        public float panScale = 0.0018f;          // 右键/中键平移系数
        public float zoomPerNotch = 0.90f;        // 滚轮每格缩放比例
        public float keyRotateSpeed = 60f;        // 方向键旋转：度 / 秒
        public float damping = 12f;
        public bool autoRotate = false;
        public float autoRotateSpeed = 8f;

        Vector3 _targetPos;      // 当前（平滑后的）中心
        Vector3 _desiredTarget;
        float _desiredDistance;
        float _desiredYaw, _desiredPitch;

        // ---- 输入诊断（面板显示，也方便排查"拖不动"这类问题）----
        /// <summary>鼠标是否落在 Game 视图范围内。</summary>
        public bool MouseInsideView { get; private set; }
        /// <summary>本帧是否按住了鼠标键。</summary>
        public bool IsDragging { get; private set; }
        /// <summary>本帧鼠标位移（像素）。</summary>
        public Vector2 LastMouseDelta { get; private set; }
        /// <summary>是否曾经成功识别到一次拖拽（用来判断输入到底有没有进来）。</summary>
        public bool EverReceivedDrag { get; private set; }

        Vector2 _lastMousePos;
        bool _hasLastMouse;

        void Awake()
        {
            _desiredYaw = yaw;
            _desiredPitch = pitch;
            _desiredDistance = distance;
            _desiredTarget = pivot != null ? pivot.position : Vector3.zero;
            _targetPos = _desiredTarget;
        }

        void LateUpdate()
        {
            // ================================================================
            //  输入读取：一律使用 Input.mousePosition / mouseScrollDelta，
            //  不使用 Input.GetAxis("Mouse X"/"Mouse Y"/"Mouse ScrollWheel")。
            //  原因：GetAxis 依赖 ProjectSettings/InputManager.asset 里的轴定义，
            //        轴缺失/被改动/工程重建时静默返回 0 —— 表现就是"拖不动"。
            //        mousePosition 是原始数据，不受任何轴配置影响。
            // ================================================================
            Vector2 mp = Input.mousePosition;
            MouseInsideView = mp.x >= 0f && mp.y >= 0f && mp.x < Screen.width && mp.y < Screen.height;

            Vector2 delta = Vector2.zero;
            if (_hasLastMouse)
            {
                delta = mp - _lastMousePos;
                // 鼠标跳变保护：从窗口外回来时会瞬移，直接丢弃这一帧
                if (delta.sqrMagnitude > 250000f) delta = Vector2.zero;
            }
            _lastMousePos = mp;
            _hasLastMouse = true;
            LastMouseDelta = delta;

            bool left = Input.GetMouseButton(0);
            bool middle = Input.GetMouseButton(2);
            bool right = Input.GetMouseButton(1);
            IsDragging = left || middle || right;
            if (IsDragging && delta.sqrMagnitude > 0.01f) EverReceivedDrag = true;

            // ---- 旋转：左键拖拽（或 左键+Shift 时改为平移）------------------
            if (left && !Input.GetKey(KeyCode.LeftShift))
            {
                _desiredYaw += delta.x * rotateDegPerPixel;
                _desiredPitch -= delta.y * rotateDegPerPixel;
                _desiredPitch = Mathf.Clamp(_desiredPitch, minPitch, maxPitch);
            }

            // ---- 平移：右键 / 中键（或 左键+Shift）--------------------------
            if (right || middle || (left && Input.GetKey(KeyCode.LeftShift)))
            {
                float s = _desiredDistance * panScale * panSpeed;
                _desiredTarget -= transform.right * (delta.x * s);
                _desiredTarget -= transform.up * (delta.y * s);
            }

            // ---- 缩放：滚轮（mouseScrollDelta.y，向上为正）------------------
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 1e-4f)
                _desiredDistance *= Mathf.Pow(zoomPerNotch, scroll);

            // ---- 键盘备用：方向键旋转（万一鼠标不可用也能浏览）--------------
            float kx = 0f, ky = 0f;
            if (Input.GetKey(KeyCode.LeftArrow)) kx -= 1f;
            if (Input.GetKey(KeyCode.RightArrow)) kx += 1f;
            if (Input.GetKey(KeyCode.UpArrow)) ky += 1f;
            if (Input.GetKey(KeyCode.DownArrow)) ky -= 1f;
            if (kx != 0f || ky != 0f)
            {
                _desiredYaw += kx * keyRotateSpeed * Time.unscaledDeltaTime;
                _desiredPitch = Mathf.Clamp(_desiredPitch + ky * keyRotateSpeed * 0.75f * Time.unscaledDeltaTime,
                                            minPitch, maxPitch);
            }

            _desiredDistance = Mathf.Clamp(_desiredDistance, minDistance, maxDistance);

            if (autoRotate)
                _desiredYaw += autoRotateSpeed * Time.unscaledDeltaTime;

            // ---- 平滑 -------------------------------------------------------
            float k = 1f - Mathf.Exp(-damping * Time.unscaledDeltaTime);
            _targetPos = Vector3.Lerp(_targetPos, _desiredTarget, k);
            distance = Mathf.Lerp(distance, _desiredDistance, k);
            yaw = Mathf.LerpAngle(yaw, _desiredYaw, k);
            pitch = Mathf.Lerp(pitch, _desiredPitch, k);

            // ---- 摆位 -------------------------------------------------------
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rot;
            transform.position = _targetPos - rot * Vector3.forward * distance;
        }

        /// <summary>
        /// 把相机移到能完整看到 bounds 的位置。
        /// 算法：把包围盒 8 个角点投到相机坐标系，求「同时满足水平与垂直视锥」所需的最小距离。
        /// 比简单的 extents.magnitude 精确得多 —— 细长模型（如本例 573×76×182 的飞船）能正好铺满画面。
        /// </summary>
        public void Frame(Bounds bounds, float padding = 1.15f, bool keepAngles = true, float aspectOverride = 0f, float verticalFov = 0f)
        {
            Camera cam = GetComponent<Camera>();

            float vFov = verticalFov > 0f ? verticalFov : (cam != null ? cam.fieldOfView : 60f);
            float aspect = aspectOverride;
            if (aspect <= 0f)
            {
                if (cam != null && cam.targetTexture != null)
                    aspect = (float)cam.targetTexture.width / cam.targetTexture.height;
                else
                    aspect = cam != null && cam.aspect > 0.01f ? cam.aspect : 16f / 9f;
            }

            float halfV = Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad);
            float halfH = halfV * aspect;

            Quaternion rot = Quaternion.Euler(_desiredPitch, _desiredYaw, 0f);
            Vector3 fwd = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;
            Vector3 up = rot * Vector3.up;

            Vector3 c = bounds.center, e = bounds.extents;
            float needed = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));

                Vector3 rel = corner - c;
                float z = Vector3.Dot(rel, fwd);                    // 沿视线方向（远处为正）
                float x = Mathf.Abs(Vector3.Dot(rel, right));
                float y = Mathf.Abs(Vector3.Dot(rel, up));

                float d = Mathf.Max(z + x / halfH, z + y / halfV);
                if (d > needed) needed = d;
            }

            if (needed < 1e-4f) needed = Mathf.Max(1f, e.magnitude);
            _desiredTarget = bounds.center;
            _desiredDistance = Mathf.Clamp(needed * padding, minDistance, maxDistance);

            if (!keepAngles)
            {
                _desiredYaw = 35f;
                _desiredPitch = 18f;
            }
        }

        /// <summary>按比例缩放距离（供 UI 按钮调用）。</summary>
        public void ZoomBy(float factor)
        {
            _desiredDistance = Mathf.Clamp(_desiredDistance * factor, minDistance, maxDistance);
        }

        /// <summary>
        /// 直接设置目标角度（供脚本/时间轴驱动使用）。
        /// 注意：Frame() 内部读取的是 _desiredYaw/_desiredPitch，只改公有字段 yaw/pitch 不会生效。
        /// </summary>
        public void SetAngles(float yawDeg, float pitchDeg)
        {
            _desiredYaw = yawDeg;
            _desiredPitch = Mathf.Clamp(pitchDeg, minPitch, maxPitch);
        }

        /// <summary>立刻把目标值应用到 transform（LateUpdate 之外用得上，例如批处理渲染/截图）。</summary>
        public void ApplyImmediately()
        {
            _targetPos = _desiredTarget;
            distance = _desiredDistance;
            yaw = _desiredYaw;
            pitch = _desiredPitch;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rot;
            transform.position = _targetPos - rot * Vector3.forward * distance;
        }

        public void ResetView(Bounds bounds)
        {
            _desiredYaw = 35f;
            _desiredPitch = 18f;
            Frame(bounds, 1.15f, true);
        }
    }
}
