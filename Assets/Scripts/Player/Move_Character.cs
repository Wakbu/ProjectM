using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 3인칭 액션 캐릭터 컨트롤
/// - 이동/점프: Rigidbody 기반 물리 이동(가속 보간)
/// - 카메라 기준 이동: 
///     A) ThirdPersonCamera의 최신 Yaw 숫자 기반  (기본)
///     B) 카메라 Transform.forward 평면화(실시간)   (옵션: useRealtimeCameraForward)
/// - 애니메이터: 2D 블렌드 트리(Parameters: MoveX, MoveZ) 가정
/// - 회전 정책: 뒤/옆 입력 시 몸을 돌리지 않는 백페달 + 앞→뒤 급전환 퀵턴(옵션)
/// - **Forward Intent 강화**: W(전진) 의도가 명확하면 각도 크기와 무관하게 카메라 전방으로 즉시 회전
/// - 프레임 순서: 카메라 Update(각 확정) → 플레이어 Update(방향/회전/애니) → FixedUpdate(물리)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(0)] // 카메라(-100) → 플레이어(0)
public class Move_Character : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 이동 / 가속
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Speed")]
    [Tooltip("지상에서의 최대 수평 이동 속도 (m/s)")]
    public float speed = 5f;

    [Tooltip("공중에서의 수평 이동 속도 배율 (지상 기준 배수)")]
    public float airSpeedMultiplier = 1.2f;

    [Tooltip("지상에서 목표 속도에 접근하는 가속도(값이 클수록 즉각적)")]
    public float groundAccel = 40f;

    [Tooltip("공중에서 목표 속도에 접근하는 가속도(지상보다 낮게 설정 권장)")]
    public float airAccel = 20f;

    // ─────────────────────────────────────────────────────────────────────────────
    // 점프
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Jump")]
    [Tooltip("점프 시 초기 상승 속도 (Y축)")]
    public float jumpForce = 8f;

    [Tooltip("점프 순간, 최근 입력 방향으로 더해줄 추가 수평 속도(경쾌함)")]
    public float jumpForwardBoost = 2.5f;

    // ─────────────────────────────────────────────────────────────────────────────
    // 중력 튜닝
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Gravity Tune")]
    [Tooltip("낙하 중 중력 강화 배율 (2~3 권장)")]
    public float fallMultiplier = 2.0f;

    [Tooltip("점프 키를 짧게 눌렀을 때 더 낮게 점프하도록 하는 중력 배율")]
    public float lowJumpMultiplier = 2.0f;

    // ─────────────────────────────────────────────────────────────────────────────
    // 애니메이터(2D 블렌드 MoveX/MoveZ)
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Animation (BlendTree MoveX/MoveZ)")]
    [Tooltip("MoveX/MoveZ 파라미터 스무딩 시간 (낮을수록 즉각 반응)")]
    [SerializeField] private float animSmooth = 0.08f;

    [Tooltip("자식 모델에 있을 수도 있어 비어 있으면 Start에서 자동 탐색")]
    public Animator animator;

    // ─────────────────────────────────────────────────────────────────────────────
    // 카메라 참조 (Yaw 또는 forward 사용)
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Camera Basis")]
    [Tooltip("플레이어가 참고할 카메라 Transform (비우면 Camera.main)")]
    public Transform cameraTransform;

    [Tooltip("ThirdPersonCamera(카메라 yaw 제공). 비우면 Transform.forward 사용 가능")]
    public ThirdPersonCamera tpsCamera;

    [Tooltip("On: Transform.forward(평면화)로 실시간 기준 사용 / Off: ThirdPersonCamera yaw 숫자 사용")]
    public bool useRealtimeCameraForward = false;

    [Tooltip("디버그용: 계산된 camF/camR, 이동방향 레이를 그려줌")]
    public bool debugDraw = false;

    // ─────────────────────────────────────────────────────────────────────────────
    // 회전 정책 (백페달 + 퀵턴 + 전진 의도 강화)
    // ─────────────────────────────────────────────────────────────────────────────
    [Header("Rotation Policy")]
    [Tooltip("뒤/좌/우 이동 시 몸을 돌리지 않고 보행 모션만 유지(백페달)")]
    public bool backpedalMode = true;

    [Tooltip("앞으로 이동한다고 판정할 최소 로컬 Z (0~1). 이보다 커야 몸을 회전 갱신 (기존 규칙)")]
    public float forwardRotateThreshold = 0.2f;

    [Tooltip("일반 회전 속도 (deg/s)")]
    public float bodyTurnSpeedDegPerSec = 360f; // 360 = 1초에 한 바퀴

    [Space]
    [Tooltip("앞(+Z) → 뒤(-Z) 급전환 시 퀵턴(180°) 처리 활성화")]
    public bool enableQuickTurn = true;

    [Tooltip("true면 즉시 스냅 회전, false면 빠른 보간 회전")]
    public bool quickTurnSnap = false;

    [Tooltip("퀵턴 보간 회전 속도 (deg/s)")]
    public float quickTurnSpeedDegPerSec = 720f;

    [Tooltip("앞(+Z)과 뒤(-Z)를 판정할 최소 입력 크기 (0.6~0.9 권장)")]
    public float quickTurnDetect = 0.75f;

    [Tooltip("퀵턴을 '정면 뒤로' 입력에만 허용(대각선에서는 금지)하기 위한 좌/우 허용치")]
    public float quickTurnLateralTolerance = 0.2f;

    [Space]
    [Tooltip("정지 또는 저속에서 W 의도가 명확하면 각도와 무관하게 카메라 전방으로 즉시 회전")]
    public bool turnInPlaceOnForwardIntent = true;

    [Tooltip("전진 의도 판단 시, |W|가 |A/D|보다 얼마나 커야 하는지의 비율(0.5~1.0 추천)")]
    public float forwardIntentBias = 0.75f;

    [Tooltip("제자리 턴으로 간주할 수평 속도 임계값(이 이하이면 사실상 정지)")]
    public float turnInPlaceSpeedEps = 0.05f;

    [Tooltip("제자리 턴 속도 (deg/s) - 큰 각도에서 빠르게 돌아보기 위함")]
    public float turnInPlaceSpeedDegPerSec = 720f;

    // ─────────────────────────────────────────────────────────────────────────────
    // 상태/참조(물리)
    // ─────────────────────────────────────────────────────────────────────────────
    [Tooltip("지면 접촉 여부")]
    private bool isGrounded = true;

    [Tooltip("Rigidbody(물리) - 이름은 characterRigidbody로 고정 요청")]
    private Rigidbody characterRigidbody;

    // 입력 캐시
    private float inputX;            // A/D
    private float inputZ;            // W/S
    private bool wantJump;          // 점프 요청(원샷)

    // 이동/애니 내부 캐시
    private Vector3 desiredMoveDir = Vector3.zero;  // 최신 프레임에서 계산된 이동 방향(월드)
    private Vector3 lastMoveDir = Vector3.forward;  // 점프 부스트용 최근 방향(초기: 앞)

    private float moveXSmoothed, moveZSmoothed;     // 애니 파라미터 스무딩
    private float vxVel, vzVel;

    private float desiredYaw;                       // 수렴할 목표 Y각
    private bool hasDesiredYaw = false;
    private float prevLocalZ = 0f;                  // 앞→뒤 급전환 감지용
    private float prevLateral = 0f;                 // 대각선 필터용

    // ─────────────────────────────────────────────────────────────────────────────
    // Unity LifeCycle
    // ─────────────────────────────────────────────────────────────────────────────
    void Start()
    {
        characterRigidbody = GetComponent<Rigidbody>();
        characterRigidbody.freezeRotation = true;
        characterRigidbody.linearDamping = 0f;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (tpsCamera == null && cameraTransform != null)
            tpsCamera = cameraTransform.GetComponent<ThirdPersonCamera>(); // 있으면 자동

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator != null)
            animator.updateMode = AnimatorUpdateMode.Fixed;

        desiredYaw = transform.eulerAngles.y;
        hasDesiredYaw = true;
    }

    void Update()
    {
        // ── 1) 입력 수집 ──
        inputX = Input.GetAxisRaw("Horizontal"); // A/D
        inputZ = Input.GetAxisRaw("Vertical");   // W/S
        if (Input.GetKeyDown(KeyCode.Space)) wantJump = true;

        // ── 2) 카메라 기준 벡터 계산 ──
        GetCameraBasis(out Vector3 camF, out Vector3 camR);

        if (debugDraw)
        {
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camF * 2f, Color.cyan);
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camR * 2f, Color.yellow);
        }

        // ── 3) 이동 방향 계산(월드) ──
        Vector3 moveDir = camF * inputZ + camR * inputX;
        if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

        desiredMoveDir = moveDir;
        if (moveDir.sqrMagnitude > 0f) lastMoveDir = moveDir;

        if (debugDraw)
            Debug.DrawRay(transform.position + Vector3.up * 0.2f, desiredMoveDir * 2f, Color.green);

        // ── 4) 점프 ──
        if (wantJump && isGrounded)
        {
            Vector3 v = characterRigidbody.linearVelocity;
            v.y = jumpForce;
            characterRigidbody.linearVelocity = v;

            if (lastMoveDir.sqrMagnitude > 0.001f)
            {
                Vector3 hv = characterRigidbody.linearVelocity;
                Vector3 add = lastMoveDir.normalized * jumpForwardBoost;
                hv.x += add.x; hv.z += add.z;
                characterRigidbody.linearVelocity = hv;
            }
            isGrounded = false;
            // if (animator) animator.SetTrigger("Jump");
        }
        wantJump = false;

        // ── 5) 중력 튜닝 ──
        if (characterRigidbody.linearVelocity.y < 0f)
        {
            characterRigidbody.linearVelocity += Vector3.up * Physics.gravity.y * (fallMultiplier - 1f) * Time.deltaTime;
        }
        else if (characterRigidbody.linearVelocity.y > 0f && !Input.GetKey(KeyCode.Space))
        {
            characterRigidbody.linearVelocity += Vector3.up * Physics.gravity.y * (lowJumpMultiplier - 1f) * Time.deltaTime;
        }

        // ── 6) 회전 처리 ──
        HandleFacingRotation(desiredMoveDir);

        // ── 7) 애니 파라미터 갱신 ──
        UpdateAnimatorParams();
    }

    void FixedUpdate()
    {
        // 물리 프레임에서 수평 가속 보간으로 이동
        Vector3 moveDir = desiredMoveDir;

        float maxHoriz = isGrounded ? speed : speed * airSpeedMultiplier;
        Vector3 targetHV = moveDir * maxHoriz;

        Vector3 v = characterRigidbody.linearVelocity;
        float accel = isGrounded ? groundAccel : airAccel;

        v.x = Mathf.MoveTowards(v.x, targetHV.x, accel * Time.fixedDeltaTime);
        v.z = Mathf.MoveTowards(v.z, targetHV.z, accel * Time.fixedDeltaTime);
        characterRigidbody.linearVelocity = v;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Ground"))
        {
            isGrounded = true;
            if (animator) animator.SetBool("Grounded", true);
        }
    }

    void OnCollisionExit(Collision collision)
    {
        if (collision.gameObject.CompareTag("Ground"))
        {
            isGrounded = false;
            if (animator) animator.SetBool("Grounded", false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 카메라 기준(수평) 벡터 계산
    // - useRealtimeCameraForward = false (기본): ThirdPersonCamera의 최신 yaw → 수평 전방/오른쪽
    // - useRealtimeCameraForward = true  : cameraTransform.forward를 평면화해서 직접 사용
    // ─────────────────────────────────────────────────────────────────────────────
    private void GetCameraBasis(out Vector3 camF, out Vector3 camR)
    {
        if (useRealtimeCameraForward && cameraTransform != null)
        {
            Vector3 f = cameraTransform.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
            camF = f.normalized;

            Vector3 r = cameraTransform.right; r.y = 0f;
            camR = r.sqrMagnitude > 0.0001f ? r.normalized : Vector3.right;
            return;
        }

        float yawDeg = 0f;
        if (tpsCamera != null) yawDeg = tpsCamera.GetYaw();
        else if (cameraTransform) yawDeg = cameraTransform.eulerAngles.y;

        Quaternion yawOnly = Quaternion.Euler(0f, yawDeg, 0f);
        camF = yawOnly * Vector3.forward;
        camR = yawOnly * Vector3.right;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 회전 처리 (백페달 유지 + 퀵턴 + 전진 의도 강화)
    // ─────────────────────────────────────────────────────────────────────────────
    private void HandleFacingRotation(Vector3 moveDir)
    {
        if (!hasDesiredYaw)
        {
            desiredYaw = transform.eulerAngles.y;
            hasDesiredYaw = true;
        }

        // 현재 수평 속도(정지/저속 판정용)
        Vector3 hv = characterRigidbody.linearVelocity; hv.y = 0f;
        float planarSpeed = hv.magnitude;

        // 입력 의도 판정
        bool hasInput = Mathf.Abs(inputX) + Mathf.Abs(inputZ) > 0.001f;
        bool forwardIntent = inputZ > 0f && (inputZ >= Mathf.Abs(inputX) * forwardIntentBias);

        // 기본 로컬 투영
        float localZ = Vector3.Dot(moveDir, transform.forward); // +앞 / -뒤
        float lateral = Vector3.Dot(moveDir, transform.right);  // 좌우 크기

        if (hasInput && moveDir.sqrMagnitude > 0.0001f)
        {
            // ★★ 핵심: "전진 의도"가 명확할 때는 각도와 무관하게 카메라 전방으로 회전
            if (forwardIntent)
            {
                float targetYawFwd = Quaternion.LookRotation(moveDir, Vector3.up).eulerAngles.y;

                // 정지/저속이면 더 빠른 제자리 턴을 적용해 큰 각도에서도 즉시 돌아봄
                float turnSpd = (planarSpeed <= turnInPlaceSpeedEps)
                                ? turnInPlaceSpeedDegPerSec
                                : bodyTurnSpeedDegPerSec;

                desiredYaw = targetYawFwd;
                float curYaw = transform.eulerAngles.y;
                float nextYaw = Mathf.MoveTowardsAngle(curYaw, desiredYaw, turnSpd * Time.deltaTime);
                transform.rotation = Quaternion.Euler(0f, nextYaw, 0f);

                // forwardIntent일 때는 아래 기존 규칙(백페달/퀵턴)을 건너뛰어 튐 방지
                prevLocalZ = localZ; // 상태 갱신만
                prevLateral = lateral;
                return;
            }

            // (기존 규칙) 앞으로 입력이 아니라면 기존 백페달/퀵턴 로직 적용
            if (localZ > forwardRotateThreshold)
            {
                desiredYaw = Quaternion.LookRotation(moveDir, Vector3.up).eulerAngles.y;
            }
            else
            {
                if (!backpedalMode)
                    desiredYaw = Quaternion.LookRotation(-moveDir, Vector3.up).eulerAngles.y;

                if (enableQuickTurn &&
                    prevLocalZ > +quickTurnDetect &&
                    localZ < -quickTurnDetect &&
                    Mathf.Abs(lateral) < quickTurnLateralTolerance)
                {
                    float targetYaw = Quaternion.LookRotation(-moveDir, Vector3.up).eulerAngles.y;

                    if (quickTurnSnap)
                    {
                        transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
                        desiredYaw = targetYaw;
                    }
                    else
                    {
                        desiredYaw = targetYaw;
                    }
                }
            }
        }

        // 일반 회전(기본 속도 또는 퀵턴 속도)
        float cur = transform.eulerAngles.y;
        float spd = (enableQuickTurn && !quickTurnSnap) ? quickTurnSpeedDegPerSec : bodyTurnSpeedDegPerSec;
        float nxt = Mathf.MoveTowardsAngle(cur, desiredYaw, spd * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, nxt, 0f);

        prevLocalZ = localZ;
        prevLateral = lateral;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 애니메이터 파라미터 갱신
    // ─────────────────────────────────────────────────────────────────────────────
    private void UpdateAnimatorParams()
    {
        if (animator == null) return;

        Vector3 hv = characterRigidbody.linearVelocity; hv.y = 0f;
        Vector3 localHV = transform.InverseTransformDirection(hv);

        float maxHoriz = isGrounded ? speed : speed * airSpeedMultiplier;
        Vector2 local01 = (maxHoriz > 0.01f)
            ? new Vector2(localHV.x, localHV.z) / maxHoriz
            : Vector2.zero;

        local01 = Vector2.ClampMagnitude(local01, 1f);

        moveXSmoothed = Mathf.SmoothDamp(moveXSmoothed, local01.x, ref vxVel, animSmooth);
        moveZSmoothed = Mathf.SmoothDamp(moveZSmoothed, local01.y, ref vzVel, animSmooth);

        animator.SetFloat("MoveX", moveXSmoothed);        // -1(왼) ~ +1(오)
        animator.SetFloat("MoveZ", moveZSmoothed);        // -1(뒤) ~ +1(앞)
        animator.SetBool("Grounded", isGrounded);
        animator.SetFloat("VerticalSpeed", characterRigidbody.linearVelocity.y);
    }
}
