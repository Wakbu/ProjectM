using UnityEngine;

/// 카메라가 플레이어(피벗) 뒤에서 따라갈 때
/// 벽/지형에 부딪히면 카메라 거리를 줄여서 클리핑을 방지하는 컴포넌트.
/// ThirdPersonCamera가 위치/회전을 계산한 "이후"에 실행되도록 실행 순서를 높임.
[DefaultExecutionOrder(10000)]
[DisallowMultipleComponent]
public class CameraCollision : MonoBehaviour
{
    [Header("Pivot / Target")]
    [Tooltip("카메라가 바라볼 기준점(보통 플레이어 머리 높이의 피벗).")]
    public Transform pivot;               // 플레이어 또는 CameraPivot
    [Tooltip("Pivot 기준 추가 오프셋(피벗을 플레이어에 직접 지정했다면 여기서 높이 등 보정).")]
    public Vector3 pivotOffset = new Vector3(0f, 2.0f, 0f);

    [Header("Distance")]
    [Tooltip("카메라의 기본(최대) 거리. ThirdPersonCamera.distance와 일치시키세요.")]
    public float maxDistance = 5f;
    [Tooltip("장애물에 너무 붙지 않게 하는 최소 거리.")]
    public float minDistance = 0.2f;

    [Header("Collision")]
    [Tooltip("스피어캐스트 반경(카메라 캡슐 크기 느낌). 너무 작으면 파고들고, 너무 크면 과도하게 당겨집니다.")]
    public float sphereRadius = 0.25f;
    [Tooltip("벽과 살짝 띄우는 여유 거리.")]
    public float wallPadding = 0.1f;
    [Tooltip("충돌 판정에 사용할 레이어 마스크(지형/벽/오브젝트). Player 레이어는 제외하세요.")]
    public LayerMask collisionLayers = ~0; // 기본: 전부. 프로젝트에 맞춰 'Environment' 등으로 설정 권장.

    [Header("Smoothing")]
    [Tooltip("거리 변화 스무딩 시간(낮을수록 반응 빠름).")]
    public float smoothTime = 0.25f;

    float _currentDistance;
    float _distanceVel;

    void Awake()
    {
        // 시작 시 현재 거리를 적당히 초기화
        if (_currentDistance <= 0f) _currentDistance = maxDistance;
        if (pivot == null && transform.parent != null)
        {
            // 부모가 피벗인 경우 자동 할당(선택적)
            pivot = transform.parent;
        }
    }

    void LateUpdate()
    {
        if (!pivot) return;

        Vector3 pivotPos = pivot.position + pivotOffset;

        // 카메라가 바라보는 방향 기준으로 "pivot → 카메라" 방향을 계산.
        // 보통 카메라는 pivot을 향해 LookAt 되어 있으므로, pivot에서 카메라로의 방향은 -transform.forward.
        Vector3 dirPivotToCam = (-transform.forward).normalized;

        // 목표 거리를 기본적으로 maxDistance로 두고, 장애물이 있으면 더 짧게.
        float targetDistance = maxDistance;

        // 스피어캐스트로 충돌 체크(피벗에서 카메라 방향으로 쏨).
        // QueryTriggerInteraction.Ignore로 트리거는 무시.
        if (Physics.SphereCast(pivotPos, sphereRadius, dirPivotToCam,
                               out RaycastHit hit, maxDistance + wallPadding,
                               collisionLayers, QueryTriggerInteraction.Ignore))
        {
            // 최소 거리~히트 지점까지 중 (벽과 padding 만큼 띄움)
            targetDistance = Mathf.Clamp(hit.distance - wallPadding, minDistance, maxDistance);
        }

        // 부드럽게 거리 보간(SmoothDamp)
        _currentDistance = Mathf.SmoothDamp(_currentDistance, targetDistance, ref _distanceVel, smoothTime);

        // 최종 카메라 위치 = 피벗에서 '현재 거리'만큼 뒤로
        Vector3 camPos = pivotPos + dirPivotToCam * _currentDistance;

        // 회전은 기존 ThirdPersonCamera가 이미 계산했으므로 손대지 않고, 위치만 보정
        transform.position = camPos;

        // (선택) 혹시 회전이 틀어질 수 있는 경우 pivot을 계속 바라보도록 유지하고 싶다면:
        // transform.LookAt(pivotPos);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!pivot) return;
        Gizmos.color = Color.cyan;
        Vector3 pivotPos = pivot.position + pivotOffset;
        Gizmos.DrawWireSphere(pivotPos, 0.05f);
        // 기대 카메라 라인 표시
        Vector3 dir = (-transform.forward);
        Gizmos.DrawLine(pivotPos, pivotPos + dir * maxDistance);
    }
#endif
}
