using UnityEngine;

/// <summary>
/// 단순 3인칭 카메라 컨트롤러
/// - 마우스 입력을 "Update"에서 먼저 처리하여 yaw/pitch를 확정
/// - "LateUpdate"에서 위치/회전을 적용(모든 대상 이동 후 카메라 위치 보정)
/// - 플레이어는 이 컴포넌트의 GetYaw() 또는 카메라 Transform.forward(flatten)를 사용해 이동 기준으로 삼음
/// </summary>
[DefaultExecutionOrder(-100)] // ★ 플레이어 Update보다 먼저 실행되어 yaw가 항상 최신
public class ThirdPersonCamera : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("따라갈 타겟(보통 플레이어) Transform")]
    public Transform target;

    [Header("Orbit")]
    [Tooltip("마우스 감도(회전)")]
    public float mouseSensitivity = 3f;

    [Tooltip("타겟으로부터의 거리")]
    public float distance = 5f;

    [Tooltip("타겟 기준 바라볼 지점의 높이(머리 높이 정도)")]
    public float height = 2f;

    [Tooltip("피치(상하 회전) 최소 각도(아래)")]
    public float minPitch = -30f;

    [Tooltip("피치(상하 회전) 최대 각도(위)")]
    public float maxPitch = 60f;

    // 내부 상태(누적 각)
    private float yaw;   // 좌우(Y축)
    private float pitch; // 상하(X축)

    void Start()
    {
        // 마우스 락
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // 시작 각도 초기화
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = Mathf.Clamp(e.x, minPitch, maxPitch);
    }

    void Update()
    {
        // ★ 카메라 입력을 "Update"에서 먼저 처리 → 플레이어 Update가 최신 yaw를 읽을 수 있음
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    void LateUpdate()
    {
        if (!target) return;

        // 누적된 yaw/pitch로 실제 카메라 위치/시선 적용
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 lookPos = target.position + Vector3.up * height;
        Vector3 camPos = lookPos - rot * Vector3.forward * distance;

        transform.position = camPos;
        transform.LookAt(lookPos);
    }

    /// <summary>현재 카메라의 수평(Yaw) 각도(도) 반환. 플레이어 이동 기준으로 사용</summary>
    public float GetYaw() => yaw;
}
