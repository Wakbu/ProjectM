using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(0)]
public class Move_Character_SlopeStep : MonoBehaviour
{
    // ── 기존 이동/점프/중력(네 스타일 유지) ─────────────────────────────────────
    [Header("Speed")]
    public float speed = 5f;
    public float airSpeedMultiplier = 1.2f;
    public float groundAccel = 40f;
    public float airAccel = 20f;

    [Header("Jump")]
    public float jumpForce = 8f;
    public float jumpForwardBoost = 2.5f;

    [Header("Gravity Tune")]
    public float fallMultiplier = 2.0f;
    public float lowJumpMultiplier = 2.0f;

    [Header("Animation (BlendTree MoveX/MoveZ)")]
    [SerializeField] private float animSmooth = 0.08f;
    public Animator animator;

    [Header("Camera Basis")]
    public Transform cameraTransform;
    public ThirdPersonCamera tpsCamera;
    public bool useRealtimeCameraForward = false;

    [Header("Rotation Policy")]
    public bool backpedalMode = true;
    public float forwardRotateThreshold = 0.2f;
    public float bodyTurnSpeedDegPerSec = 360f;
    public bool enableQuickTurn = true;
    public bool quickTurnSnap = false;
    public float quickTurnSpeedDegPerSec = 720f;
    public float quickTurnDetect = 0.75f;
    public float quickTurnLateralTolerance = 0.2f;
    public bool turnInPlaceOnForwardIntent = true;
    public float forwardIntentBias = 0.75f;
    public float turnInPlaceSpeedEps = 0.05f;
    public float turnInPlaceSpeedDegPerSec = 720f;

    // ── 추가: 지면/경사/스텝 파트 ────────────────────────────────────────────────
    [Header("Ground Probe")]
    [Tooltip("SphereCast 반경")] public float groundCheckRadius = 0.25f;
    [Tooltip("바닥에서 위 오프셋")] public float groundCheckOffset = 0.05f;
    [Tooltip("Ground 레이어 마스크")] public LayerMask groundLayers = ~0;

    [Header("Slope")]
    [Range(0, 89)] public float slopeLimitDeg = 50f;
    [Range(0f, 1f)] public float slopeAssist = 0.2f;
    [Tooltip("지면 스틱(아래로 당기는 힘, N)")] public float groundStickForce = 30f;

    [Header("Step (단차)")]
    [Tooltip("최대 승급 높이(m)")] public float stepOffsetHeight = 0.35f;
    [Tooltip("전방 탐색 거리(m)")] public float stepSearchDistance = 0.25f;
    [Range(0f, 1f)] public float stepLerp = 0.25f;

    [Header("Debug")]
    public bool debugDraw = false;

    // ── 상태/캐시 ───────────────────────────────────────────────────────────────
    private bool isGrounded = true;
    private Vector3 groundNormal = Vector3.up;
    private float slopeLimitCos;
    private Rigidbody rb;

    private float inputX, inputZ;
    private bool wantJump;
    private Vector3 desiredMoveDir = Vector3.zero;
    private Vector3 lastMoveDir = Vector3.forward;

    private float moveXSmoothed, moveZSmoothed, vxVel, vzVel;
    private float desiredYaw; private bool hasDesiredYaw;
    private float prevLocalZ = 0f, prevLateral = 0f;

    // ── Unity lifecycle ────────────────────────────────────────────────────────
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;

        if (cameraTransform == null && Camera.main) cameraTransform = Camera.main.transform;
        if (tpsCamera == null && cameraTransform) tpsCamera = cameraTransform.GetComponent<ThirdPersonCamera>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator) animator.updateMode = AnimatorUpdateMode.Fixed;

        desiredYaw = transform.eulerAngles.y; hasDesiredYaw = true;
        slopeLimitCos = Mathf.Cos(slopeLimitDeg * Mathf.Deg2Rad);
    }

    void Update()
    {
        // 입력
        inputX = Input.GetAxisRaw("Horizontal");
        inputZ = Input.GetAxisRaw("Vertical");
        if (Input.GetKeyDown(KeyCode.Space)) wantJump = true;

        // 카메라 기준
        GetCameraBasis(out Vector3 camF, out Vector3 camR);
        if (debugDraw)
        {
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camF * 2f, Color.cyan);
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camR * 2f, Color.yellow);
        }

        // 이동 의도
        Vector3 moveDir = camF * inputZ + camR * inputX;
        if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();
        desiredMoveDir = moveDir;
        if (moveDir.sqrMagnitude > 0f) lastMoveDir = moveDir;

        if (debugDraw)
            Debug.DrawRay(transform.position + Vector3.up * 0.2f, desiredMoveDir * 2f, Color.green);

        // 점프(요청만 세팅; 실제 y속도는 Update에서도 처리)
        if (wantJump && isGrounded)
        {
            Vector3 v = rb.linearVelocity; v.y = jumpForce; rb.linearVelocity = v;
            if (lastMoveDir.sqrMagnitude > 0.001f)
            {
                Vector3 hv = rb.linearVelocity;
                Vector3 add = lastMoveDir.normalized * jumpForwardBoost;
                hv.x += add.x; hv.z += add.z; rb.linearVelocity = hv;
            }
            isGrounded = false;
        }
        wantJump = false;

        // 중력 튜닝
        if (rb.linearVelocity.y < 0f)
        {
            rb.linearVelocity += Vector3.up * Physics.gravity.y * (fallMultiplier - 1f) * Time.deltaTime;
        }
        else if (rb.linearVelocity.y > 0f && !Input.GetKey(KeyCode.Space))
        {
            rb.linearVelocity += Vector3.up * Physics.gravity.y * (lowJumpMultiplier - 1f) * Time.deltaTime;
        }

        // 회전
        HandleFacingRotation(desiredMoveDir);

        // 애니
        UpdateAnimatorParams();
    }

    void FixedUpdate()
    {
        // 1) 지면 프로브(NEW)
        GroundProbe();

        // 2) 경사 접선 투영(NEW)
        Vector3 moveDir = desiredMoveDir;
        if (isGrounded && moveDir.sqrMagnitude > 0f)
            moveDir = Vector3.ProjectOnPlane(moveDir, groundNormal).normalized;

        // 3) 수평 가속 보간(기존 스타일 유지)
        float maxHoriz = isGrounded ? speed : speed * airSpeedMultiplier;
        Vector3 targetHV = moveDir * maxHoriz;

        Vector3 v = rb.linearVelocity;
        float accel = isGrounded ? groundAccel : airAccel;

        v.x = Mathf.MoveTowards(v.x, targetHV.x, accel * Time.fixedDeltaTime);
        v.z = Mathf.MoveTowards(v.z, targetHV.z, accel * Time.fixedDeltaTime);
        rb.linearVelocity = v;

        // 4) 스텝(단차) 처리(NEW)
        if (isGrounded && desiredMoveDir.sqrMagnitude > 0.001f)
            TryStepUp(moveDir);

        // 5) 경사 제한 + 보조/스틱(NEW)
        if (isGrounded)
        {
            float cos = Vector3.Dot(groundNormal, Vector3.up);
            if (cos < slopeLimitCos)
            {
                // 제한 초과: 위로 상승 억제
                if (Vector3.Dot(rb.linearVelocity, Vector3.up) > 0f)
                    rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
            }
            else
            {
                if (slopeAssist > 0f && desiredMoveDir.sqrMagnitude > 0.001f)
                {
                    Vector3 alongSlope = Vector3.ProjectOnPlane(desiredMoveDir, groundNormal).normalized;
                    rb.AddForce(alongSlope * (groundAccel * slopeAssist), ForceMode.Acceleration);
                }
                rb.AddForce(-groundNormal * groundStickForce, ForceMode.Acceleration);
            }
        }
    }

    // ── 지면 프로브 ────────────────────────────────────────────────────────────
    void GroundProbe()
    {
        isGrounded = false; groundNormal = Vector3.up;

        float castDistance = groundCheckOffset + 0.6f;
        Vector3 origin = transform.position + Vector3.up * (groundCheckOffset + groundCheckRadius + 0.01f);

        if (Physics.SphereCast(origin, groundCheckRadius, Vector3.down,
            out RaycastHit hit, castDistance, groundLayers, QueryTriggerInteraction.Ignore))
        {
            isGrounded = true;
            groundNormal = hit.normal;
            if (debugDraw) Debug.DrawRay(hit.point, hit.normal * 0.5f, Color.green);
        }
        else if (debugDraw)
        {
            Debug.DrawRay(origin, Vector3.down * castDistance, Color.red);
        }
    }

    // ── 스텝(단차) 승급 ─────────────────────────────────────────────────────────
    void TryStepUp(Vector3 moveDir)
    {
        float radius = 0.25f;
        if (TryGetComponent<CapsuleCollider>(out var capsule))
            radius = capsule.radius * 0.9f;

        Vector3 basePos = transform.position + Vector3.up * (groundCheckOffset + groundCheckRadius);
        Vector3 low = basePos + Vector3.up * 0.05f;
        Vector3 high = basePos + Vector3.up * stepOffsetHeight;

        if (Physics.SphereCast(low, radius * 0.5f, moveDir, out RaycastHit lowHit, stepSearchDistance, groundLayers, QueryTriggerInteraction.Ignore))
        {
            bool blockedHigh = Physics.SphereCast(high, radius * 0.5f, moveDir, out _, stepSearchDistance, groundLayers, QueryTriggerInteraction.Ignore);
            if (!blockedHigh)
            {
                Vector3 climb = Vector3.up * Mathf.Clamp(stepOffsetHeight, 0f, 0.7f) + moveDir * 0.02f;
                Vector3 target = rb.position + climb;
                Vector3 newPos = Vector3.Lerp(rb.position, target, Mathf.Clamp01(1f - stepLerp));
                rb.MovePosition(newPos);

                if (debugDraw)
                {
                    Debug.DrawLine(low, low + moveDir * stepSearchDistance, Color.yellow);
                    Debug.DrawLine(high, high + moveDir * stepSearchDistance, Color.cyan);
                }
            }
        }
    }

    // ── 카메라 기준 축 ──────────────────────────────────────────────────────────
    void GetCameraBasis(out Vector3 camF, out Vector3 camR)
    {
        if (useRealtimeCameraForward && cameraTransform)
        {
            Vector3 f = cameraTransform.forward; f.y = 0f; camF = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            Vector3 r = cameraTransform.right; r.y = 0f; camR = r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
            return;
        }
        float yawDeg = 0f;
        if (tpsCamera) yawDeg = tpsCamera.GetYaw();
        else if (cameraTransform) yawDeg = cameraTransform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, yawDeg, 0f);
        camF = yawOnly * Vector3.forward;
        camR = yawOnly * Vector3.right;
    }

    // ── 회전(네 전진 의도/퀵턴 규칙 유지) ───────────────────────────────────────
    void HandleFacingRotation(Vector3 moveDir)
    {
        if (!hasDesiredYaw) { desiredYaw = transform.eulerAngles.y; hasDesiredYaw = true; }

        Vector3 hv = rb.linearVelocity; hv.y = 0f;
        float planarSpeed = hv.magnitude;

        bool hasInput = Mathf.Abs(inputX) + Mathf.Abs(inputZ) > 0.001f;
        bool forwardIntent = inputZ > 0f && (inputZ >= Mathf.Abs(inputX) * forwardIntentBias);

        float localZ = Vector3.Dot(moveDir, transform.forward);
        float lateral = Vector3.Dot(moveDir, transform.right);

        if (hasInput && moveDir.sqrMagnitude > 0.0001f)
        {
            if (forwardIntent)
            {
                float targetYawFwd = Quaternion.LookRotation(moveDir, Vector3.up).eulerAngles.y;
                float turnSpd = (planarSpeed <= turnInPlaceSpeedEps) ? turnInPlaceSpeedDegPerSec : bodyTurnSpeedDegPerSec;
                desiredYaw = targetYawFwd;
                float curYaw = transform.eulerAngles.y;
                float nextYaw = Mathf.MoveTowardsAngle(curYaw, desiredYaw, turnSpd * Time.deltaTime);
                transform.rotation = Quaternion.Euler(0f, nextYaw, 0f);
                prevLocalZ = localZ; prevLateral = lateral; return;
            }

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
                    if (quickTurnSnap) { transform.rotation = Quaternion.Euler(0f, targetYaw, 0f); desiredYaw = targetYaw; }
                    else { desiredYaw = targetYaw; }
                }
            }
        }

        float cur = transform.eulerAngles.y;
        float spd = (enableQuickTurn && !quickTurnSnap) ? quickTurnSpeedDegPerSec : bodyTurnSpeedDegPerSec;
        float nxt = Mathf.MoveTowardsAngle(cur, desiredYaw, spd * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, nxt, 0f);

        prevLocalZ = localZ; prevLateral = lateral;
    }

    // ── 애니 파라미터 ──────────────────────────────────────────────────────────
    void UpdateAnimatorParams()
    {
        if (!animator) return;
        Vector3 hv = rb.linearVelocity; hv.y = 0f;
        Vector3 localHV = transform.InverseTransformDirection(hv);
        float maxHoriz = isGrounded ? speed : speed * airSpeedMultiplier;

        Vector2 local01 = (maxHoriz > 0.01f)
            ? new Vector2(localHV.x, localHV.z) / maxHoriz
            : Vector2.zero;
        local01 = Vector2.ClampMagnitude(local01, 1f);

        moveXSmoothed = Mathf.SmoothDamp(moveXSmoothed, local01.x, ref vxVel, animSmooth);
        moveZSmoothed = Mathf.SmoothDamp(moveZSmoothed, local01.y, ref vzVel, animSmooth);

        animator.SetFloat("MoveX", moveXSmoothed);
        animator.SetFloat("MoveZ", moveZSmoothed);
        animator.SetBool("Grounded", isGrounded);
        animator.SetFloat("VerticalSpeed", rb.linearVelocity.y);
    }
}
