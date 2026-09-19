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
        public float rotateSpeed = 0.25f;       // 度 / 像素
        public float panSpeed = 1f;
        public float zoomStep = 0.12f;
        public float damping = 12f;
        public bool autoRotate = false;
        public float autoRotateSpeed = 8f;

        Vector3 _targetPos;      // 当前（平滑后的）中心
        Vector3 _desiredTarget;
        float _desiredDistance;
        float _desiredYaw, _desiredPitch;

        bool _dragging;

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
            // ---- 输入 -------------------------------------------------------
            if (Input.GetMouseButton(0) && !Input.GetKey(KeyCode.LeftShift))
            {
                _desiredYaw += Input.GetAxis("Mouse X") * rotateSpeed * 4f;
                _desiredPitch -= Input.GetAxis("Mouse Y") * rotateSpeed * 4f;
                _desiredPitch = Mathf.Clamp(_desiredPitch, minPitch, maxPitch);
            }
            if (Input.GetMouseButton(0) && Input.GetKey(KeyCode.LeftShift) || Input.GetMouseButton(2))
            {
                float scale = _desiredDistance * 0.0018f * panSpeed;
                Vector3 right = transform.right;
                Vector3 up = transform.up;
                _desiredTarget -= right * (Input.GetAxis("Mouse X") * scale);
                _desiredTarget -= up * (Input.GetAxis("Mouse Y") * scale);
            }
            if (Input.GetMouseButton(1))
            {
                float scale = _desiredDistance * 0.0018f * panSpeed;
                _desiredTarget -= transform.right * (Input.GetAxis("Mouse X") * scale);
                _desiredTarget -= transform.up * (Input.GetAxis("Mouse Y") * scale);
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
                _desiredDistance *= 1f - scroll * 6f * zoomStep;

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
