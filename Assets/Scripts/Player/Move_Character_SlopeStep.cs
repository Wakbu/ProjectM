using UnityEngine;

/// <summary>
/// Move_Character_SlopeStep
/// - 3인칭 액션용 Rigidbody 기반 이동(카메라 기준), 점프, 중력 튜닝
/// - 업힐 품질: 지면 스냅 + 노멀 안정화 + 경사 히스테리시스 + 업힐 보조
/// - 스텝(단차) 자동 승급, 애니 파라미터 갱신
///
/// 핵심 변경(점프 이슈 해결):
/// - 점프 입력은 Update에서 "버퍼"만 저장, 실제 점프 결정은 FixedUpdate(접지 갱신 이후)에서 수행
/// - 코요테 타임(막 떨어진 직후 점프 허용) & 점프 직후 스냅 금지로 경사/단차에서도 확실한 점프 보장
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(0)]
public class Move_Character_SlopeStep : MonoBehaviour
{
    // ───────────────────────────────────────────────────────────────────────────
    // 이동/점프/중력
    // ───────────────────────────────────────────────────────────────────────────
    [Header("Speed")]
    [Tooltip("지면에 붙어있을 때 목표 이동 속도(수평, m/s)")]
    public float speed = 5f;

    [Tooltip("공중에서의 목표 속도 배율(공중 조작성)")]
    public float airSpeedMultiplier = 1.2f;

    [Tooltip("지면에서 목표 수평속도로의 보간 가속도(값이 클수록 즉각 응답)")]
    public float groundAccel = 40f;

    [Tooltip("공중에서 목표 수평속도로의 보간 가속도(너무 크면 공중에서 급격 전환)")]
    public float airAccel = 20f;

    [Header("Jump")]
    [Tooltip("점프 초기 상향 속도(즉시 부여, m/s)")]
    public float jumpForce = 8f;

    [Tooltip("점프 시 전진 보조량(수평 추가치, m/s)")]
    public float jumpForwardBoost = 2.5f;

    [Header("Gravity Tune")]
    [Tooltip("낙하 가속 배율(>1이면 더 빨리 떨어짐)")]
    public float fallMultiplier = 2.0f;

    [Tooltip("스페이스를 떼었을 때 상승 억제 배율(짧은 점프 느낌)")]
    public float lowJumpMultiplier = 2.0f;

    [Header("Jump Buffer & Coyote Time")]
    [Tooltip("점프 키를 조금 이르게 눌러도 이 시간(초) 동안 입력을 버퍼링하여 다음 접지 프레임에 점프 허용")]
    public float jumpBufferTime = 0.12f;

    [Tooltip("발이 막 떨어진 직후에도 이 시간(초) 동안 점프 허용(코요테 타임)")]
    public float coyoteTime = 0.12f;

    [Tooltip("점프 직후 지면 스냅을 잠깐 비활성화하여 즉시 재접지되는 것을 방지(초)")]
    public float jumpSnapLockout = 0.12f;

    [Header("Animation (BlendTree MoveX/MoveZ)")]
    [Tooltip("애니 파라미터 보간 시간 상수(작을수록 민감)")]
    [SerializeField] private float animSmooth = 0.08f;

    [Tooltip("애니메이터(자식 가능). MoveX/MoveZ/Grounded/VerticalSpeed 갱신")]
    public Animator animator;

    [Header("Camera Basis")]
    [Tooltip("이동 방향 기준이 되는 카메라(없으면 Camera.main)")]
    public Transform cameraTransform;

    [Tooltip("TPS 카메라 스크립트(선택). GetYaw()로 yaw 제공")]
    public ThirdPersonCamera tpsCamera;

    [Tooltip("true면 매 프레임 camera.forward의 수평투영을 이동 기준으로 사용")]
    public bool useRealtimeCameraForward = false;

    [Header("Rotation Policy")]
    [Tooltip("뒤/좌/우 입력 시에도 몸을 전방으로 유지할지 여부")]
    public bool backpedalMode = true;

    [Tooltip("전방 회전으로 간주하는 최소 정면성(localZ 임계값)")]
    public float forwardRotateThreshold = 0.2f;

    [Tooltip("일반 회전 속도(도/초)")]
    public float bodyTurnSpeedDegPerSec = 360f;

    [Tooltip("퀵턴(정면↔후면 급전환) 활성화")]
    public bool enableQuickTurn = true;

    [Tooltip("퀵턴 시 즉시 스냅 회전할지(아니면 빠른 보간)")]
    public bool quickTurnSnap = false;

    [Tooltip("퀵턴 회전 속도(도/초, 스냅이 아닐 때)")]
    public float quickTurnSpeedDegPerSec = 720f;

    [Tooltip("퀵턴 감지 임계: 이전 프레임 정면성과 현재 프레임 반대 정면성")]
    public float quickTurnDetect = 0.75f;

    [Tooltip("퀵턴 허용 측면 편차(좌우 입력 허용 범위)")]
    public float quickTurnLateralTolerance = 0.2f;

    [Tooltip("전진 의도 시 정지/저속이면 즉시 전방으로 회전(턴 인 플레이스)")]
    public bool turnInPlaceOnForwardIntent = true;

    [Tooltip("전진 의도 판단에서 Z가 X보다 얼마나 우세해야 하는지(가중치)")]
    public float forwardIntentBias = 0.75f;

    [Tooltip("턴-인-플레이스로 간주하는 평면 속도 임계값(이하이면 제자리 회전)")]
    public float turnInPlaceSpeedEps = 0.05f;

    [Tooltip("턴-인-플레이스 회전 속도(도/초)")]
    public float turnInPlaceSpeedDegPerSec = 720f;

    // ───────────────────────────────────────────────────────────────────────────
    // 지면/경사/스텝
    // ───────────────────────────────────────────────────────────────────────────
    [Header("Ground Probe")]
    [Tooltip("지면 접지를 검사하는 SphereCast 반경(발 반경 기준)")]
    public float groundCheckRadius = 0.225f;

    [Tooltip("발바닥 기준 약간 위 오프셋(지면 캐스트 시작 높이 보정)")]
    public float groundCheckOffset = 0.05f;

    [Tooltip("Ground로 인식할 레이어 마스크(여기에 포함된 레이어만 '땅'으로 인식)")]
    public LayerMask groundLayers = ~0;

    [Header("Slope")]
    [Range(0, 89)]
    [Tooltip("오를 수 있는 최대 경사(도). 그 이상은 위로 진입 억제")]
    public float slopeLimitDeg = 50f;

    [Range(0f, 10f)]
    [Tooltip("경사 한계 경계에서 떨림 방지를 위한 버퍼 각도(도)")]
    public float slopeHysteresisDeg = 3f;

    [Range(0f, 1f)]
    [Tooltip("경사면 이동 시 의도 방향 보조(groundAccel과 곱)")]
    public float slopeAssist = 0.25f;

    [Tooltip("지면으로 눌러 접지를 유지하는 하향 가속")]
    public float groundStickForce = 30f;

    [Tooltip("업힐(경사 오르기) 시 중력의 경사 성분을 상쇄하는 보조 가속")]
    public float uphillBoostAccel = 12f;

    [Header("Ground Snap (끊김 방지)")]
    [Tooltip("발 아래가 이 거리 이내로 비었고 하강 속도가 작으면 지면으로 스냅")]
    public float groundSnapDistance = 0.2f;

    [Tooltip("이보다 빠르게 떨어지는 중이면 스냅 금지(탈출 허용)")]
    public float groundSnapMaxFallSpeed = 4.5f;

    [Range(1, 5)]
    [Tooltip("지면 노멀 안정화를 위한 주변 샘플 수(1=중심만)")]
    public int groundNormalSamples = 3;

    [Header("Step (단차)")]
    [Tooltip("자동으로 올라탈 수 있는 단차의 최대 높이(m)")]
    public float stepOffsetHeight = 0.35f;

    [Tooltip("단차 감지를 위한 전방 탐색 거리(m)")]
    public float stepSearchDistance = 0.3f;

    [Range(0f, 1f)]
    [Tooltip("단차 승급 시 보간 계수(0~1, 클수록 즉시 올라탐)")]
    public float stepLerp = 0.35f;

    [Header("Debug")]
    [Tooltip("디버그 선/노멀/탐색 레이 그리기")]
    public bool debugDraw = false;

    // ── Animation Parameters (추가) ───────────────────────────────────────────────
    [Header("Animation Parameters")]
    [Tooltip("BlendTree의 이동 X 파라미터명")]
    [SerializeField] private string animParamMoveX = "MoveX";

    [Tooltip("BlendTree의 이동 Z 파라미터명")]
    [SerializeField] private string animParamMoveZ = "MoveZ";

    [Tooltip("접지 여부 파라미터명 (bool)")]
    [SerializeField] private string animParamGrounded = "Grounded";

    [Tooltip("수직 속도 파라미터명 (float)")]
    [SerializeField] private string animParamVerticalSpeed = "VerticalSpeed";

    [Tooltip("점프 시작 트리거명 (Trigger)")]
    [SerializeField] private string animParamJump = "Jump";

    [Tooltip("착지 트리거명 (Trigger)")]
    [SerializeField] private string animParamLand = "Land";

    [Tooltip("하강 중 불리언명 (bool)")]
    [SerializeField] private string animParamFalling = "Falling";

    [Tooltip("점프 정점 트리거명 (선택, 비워두면 사용 안 함)")]
    [SerializeField] private string animParamApex = "Apex";

    // 해시 캐시
    private int _hMoveX, _hMoveZ, _hGrounded, _hVSpeed, _hJump, _hLand, _hFalling, _hApex;

    // 상태 추적
    private bool wasGrounded = true;
    private float prevYVel = 0f;


    // ───────────────────────────────────────────────────────────────────────────
    // 내부 상태/캐시
    // ───────────────────────────────────────────────────────────────────────────
    private bool isGrounded = true;
    private Vector3 groundNormal = Vector3.up;
    private float slopeLimitCos;
    private float slopeLimitCos_Hi; // 히스테리시스 상한
    private Rigidbody rb;

    private float inputX, inputZ;
    private Vector3 desiredMoveDir = Vector3.zero;
    private Vector3 lastMoveDir = Vector3.forward;

    private float moveXSmoothed, moveZSmoothed, vxVel, vzVel;
    private float desiredYaw; private bool hasDesiredYaw;
    private float prevLocalZ = 0f, prevLateral = 0f;

    // 점프 타이머(입력 버퍼/코요테/스냅 잠금)
    private float jumpBufferTimer = 0f;
    private float coyoteTimer = 0f;
    private float snapLockoutTimer = 0f;

    private RigidbodyConstraints _initialConstraints;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        _initialConstraints = rb.constraints; // 인스펙터 제약값 기억
    }

    /// <summary>
    /// Reset()
    /// - 컴포넌트가 추가될 때 기본 groundLayers를 'Default'와 'Ground'로 설정(안전장치)
    /// </summary>
    void Reset()
    {
        groundLayers = LayerMask.GetMask("Default", "Ground");
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Unity 라이프사이클
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Start()
    /// - 회전 제약(X/Z만 잠금), 보간 설정
    /// - 카메라/애니메이터 캐시
    /// - 경사 한계 cos, 히스테리시스 cos 미리 계산
    /// </summary>
    void Start()
    {
        // 회전: X/Z만 잠가서 기울기/전복 방지, Y 회전은 코드가 제어
        rb.constraints = _initialConstraints
                       | RigidbodyConstraints.FreezeRotationX
                       | RigidbodyConstraints.FreezeRotationZ;

        rb.interpolation = RigidbodyInterpolation.Interpolate; // 시각적 떨림 감소

        if (cameraTransform == null && Camera.main) cameraTransform = Camera.main.transform;
        if (tpsCamera == null && cameraTransform) tpsCamera = cameraTransform.GetComponent<ThirdPersonCamera>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator) animator.updateMode = AnimatorUpdateMode.Fixed; // 물리 프레임과 동기화

        desiredYaw = transform.eulerAngles.y; hasDesiredYaw = true;

        slopeLimitCos = Mathf.Cos(slopeLimitDeg * Mathf.Deg2Rad);
        slopeLimitCos_Hi = Mathf.Cos(Mathf.Max(0.0f, slopeLimitDeg - slopeHysteresisDeg) * Mathf.Deg2Rad);

        // Animator 파라미터 해시 캐시
        if (animator)
        {
            _hMoveX = Animator.StringToHash(animParamMoveX);
            _hMoveZ = Animator.StringToHash(animParamMoveZ);
            _hGrounded = Animator.StringToHash(animParamGrounded);
            _hVSpeed = Animator.StringToHash(animParamVerticalSpeed);
            _hJump = string.IsNullOrEmpty(animParamJump) ? 0 : Animator.StringToHash(animParamJump);
            _hLand = string.IsNullOrEmpty(animParamLand) ? 0 : Animator.StringToHash(animParamLand);
            _hFalling = string.IsNullOrEmpty(animParamFalling) ? 0 : Animator.StringToHash(animParamFalling);
            _hApex = string.IsNullOrEmpty(animParamApex) ? 0 : Animator.StringToHash(animParamApex);
        }

        wasGrounded = isGrounded;
        prevYVel = rb.linearVelocity.y;

    }

    /// <summary>
    /// Update()
    /// - 입력만 읽어서 '의도' 상태를 갱신(이동/회전)
    /// - 점프 키는 '버퍼 타이머'만 갱신(실제 점프는 FixedUpdate에서 결정)
    /// - 중력 튜닝(낙하/짧은 점프)
    /// - 애니 파라미터 갱신
    /// </summary>
    void Update()
    {
        // 1) 이동 입력
        inputX = Input.GetAxisRaw("Horizontal");
        inputZ = Input.GetAxisRaw("Vertical");

        // 2) 점프 입력 → 버퍼만 갱신 (눌렀던 사실을 일정 시간 기억)
        if (Input.GetKeyDown(KeyCode.Space))
        {
            jumpBufferTimer = jumpBufferTime;
            if (debugDraw) Debug.Log($"Timers  buf={jumpBufferTimer:0.00}  coyote={coyoteTimer:0.00}  lockout={snapLockoutTimer:0.00}  grounded={isGrounded}");
        }

        // 3) 카메라 기준 축
        GetCameraBasis(out Vector3 camF, out Vector3 camR);

        // 4) 이동 의도 계산
        Vector3 moveDir = camF * inputZ + camR * inputX;
        if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();
        desiredMoveDir = moveDir;
        if (moveDir.sqrMagnitude > 0f) lastMoveDir = moveDir;

        if (debugDraw)
        {
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camF * 2f, Color.cyan);
            Debug.DrawRay(transform.position + Vector3.up * 1.8f, camR * 2f, Color.yellow);
            Debug.DrawRay(transform.position + Vector3.up * 0.2f, desiredMoveDir * 2f, Color.green);
        }

        // 5) 중력 튜닝(낙하 강화/짧은 점프)
        if (rb.linearVelocity.y < 0f)
            rb.linearVelocity += Vector3.up * Physics.gravity.y * (fallMultiplier - 1f) * Time.deltaTime;
        else if (rb.linearVelocity.y > 0f && !Input.GetKey(KeyCode.Space))
            rb.linearVelocity += Vector3.up * Physics.gravity.y * (lowJumpMultiplier - 1f) * Time.deltaTime;

        // 6) 회전
        HandleFacingRotation(desiredMoveDir);

        // 7) 애니 파라미터
        UpdateAnimatorParams();
    }


    /// <summary>
    /// FixedUpdate()
    /// - 물리 기반 처리: 접지/노멀 안정화/스냅 → 이동/가속 보간 → 스텝 승급 → 경사 제한/보조/스틱
    /// - 점프 결정은 여기서(접지 최신화 이후) 수행: jumpBuffer + coyote + snapLockout를 만족해야 점프
    /// </summary>
    void FixedUpdate()
    {
        // (1) 접지 판정 + 노멀 안정화 + 스냅(락아웃 고려)
        GroundProbeAndSnap();

        // ★ 애니메이션 전이 이벤트 처리 (GroundProbeAndSnap() 직후에 두는 것을 권장)
        if (animator)
        {
            // 착지: 공중 → 접지
            if (!wasGrounded && isGrounded)
            {
                if (_hLand != 0) animator.SetTrigger(_hLand);
                if (_hFalling != 0) animator.SetBool(_hFalling, false); // 더 이상 하강 아님
            }

            // 하강 플래그: 공중이며 y속도가 음수일 때
            if (!isGrounded && _hFalling != 0)
            {
                bool fallingNow = rb.linearVelocity.y < -0.01f;
                animator.SetBool(_hFalling, fallingNow);
            }

            // (선택) 정점: 상승 → 하강 전환 프레임
            if (!isGrounded && _hApex != 0 && prevYVel > 0f && rb.linearVelocity.y <= 0f)
            {
                animator.SetTrigger(_hApex);
            }
        }

        // 다음 프레임 전이 계산을 위해 저장
        wasGrounded = isGrounded;
        prevYVel = rb.linearVelocity.y;


        // (2) 타이머 갱신: 코요테/버퍼/락아웃
        if (isGrounded) coyoteTimer = coyoteTime;              // 접지면 코요테 리필
        else if (coyoteTimer > 0f) coyoteTimer -= Time.fixedDeltaTime;

        if (jumpBufferTimer > 0f) jumpBufferTimer -= Time.fixedDeltaTime;
        if (snapLockoutTimer > 0f) snapLockoutTimer -= Time.fixedDeltaTime;

        // (3) 점프 결정: 접지 최신화 이후, 버퍼+코요테+락아웃 조건을 만족해야 실행
        bool canJumpNow = (jumpBufferTimer > 0f) && (coyoteTimer > 0f) && (snapLockoutTimer <= 0f);
        if (canJumpNow)
        {
            DoJump();                          // 실제 점프 실행
            jumpBufferTimer = 0f;             // 입력 소진
            coyoteTimer = 0f;             // 공중 전환
            snapLockoutTimer = jumpSnapLockout; // 스냅 잠금 시작
        }

        // (4) 경사 접선 투영 및 가속 보간
        Vector3 moveDir = desiredMoveDir;
        if (isGrounded && moveDir.sqrMagnitude > 0f)
            moveDir = Vector3.ProjectOnPlane(moveDir, groundNormal).normalized;

        float maxHoriz = isGrounded ? speed : speed * airSpeedMultiplier;
        Vector3 targetHV = moveDir * maxHoriz;

        Vector3 v = rb.linearVelocity;
        float accel = isGrounded ? groundAccel : airAccel;

        Vector3 hvCur = v; hvCur.y = 0f;
        hvCur = Vector3.ProjectOnPlane(hvCur, groundNormal);

        v.x = Mathf.MoveTowards(hvCur.x, targetHV.x, accel * Time.fixedDeltaTime);
        v.z = Mathf.MoveTowards(hvCur.z, targetHV.z, accel * Time.fixedDeltaTime);
        v.y = rb.linearVelocity.y;
        rb.linearVelocity = v;

        // (5) 스텝 처리
        if (isGrounded && desiredMoveDir.sqrMagnitude > 0.001f)
            TryStepUp(moveDir);

        // (6) 경사 제한/보조/스틱
        if (isGrounded)
        {
            float cos = Vector3.Dot(groundNormal, Vector3.up);
            bool clearlyOver = (cos < slopeLimitCos_Hi);

            if (clearlyOver)
            {
                Vector3 lv = rb.linearVelocity;
                if (lv.y > 0f) { lv.y = 0f; rb.linearVelocity = lv; }

                Vector3 along = Vector3.ProjectOnPlane(rb.linearVelocity, groundNormal);
                rb.linearVelocity = new Vector3(along.x, rb.linearVelocity.y, along.z);
            }
            else
            {
                if (slopeAssist > 0f && desiredMoveDir.sqrMagnitude > 0.001f)
                {
                    Vector3 alongSlope = Vector3.ProjectOnPlane(desiredMoveDir, groundNormal).normalized;
                    rb.AddForce(alongSlope * (groundAccel * slopeAssist), ForceMode.Acceleration);
                }

                if (uphillBoostAccel > 0f && desiredMoveDir.sqrMagnitude > 0.001f)
                {
                    Vector3 downAlong = Vector3.Project(Physics.gravity, Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized);
                    Vector3 upAlong = -downAlong;
                    rb.AddForce(upAlong.normalized * uphillBoostAccel, ForceMode.Acceleration);
                }

                rb.AddForce(-groundNormal * groundStickForce, ForceMode.Acceleration);
            }
        }
    }


    /// <summary>
    /// DoJump()
    /// - 실제 점프 실행: 상향 초기속도 + (선택) 전진 부스트
    /// - 스냅 락아웃은 FixedUpdate에서 이미 시작
    /// </summary>
    void DoJump()
    {
        var v = rb.linearVelocity;
        v.y = jumpForce;
        rb.linearVelocity = v;

        if (lastMoveDir.sqrMagnitude > 0.001f)
        {
            Vector3 add = lastMoveDir.normalized * jumpForwardBoost;
            rb.linearVelocity = new Vector3(rb.linearVelocity.x + add.x, rb.linearVelocity.y, rb.linearVelocity.z + add.z);
        }

        // ★ 점프 시작 트리거
        if (animator && _hJump != 0) animator.SetTrigger(_hJump);

        if (debugDraw) Debug.Log("Jump!");
    }


    /// <summary>
    /// GroundProbeAndSnap()
    /// - '발 위치(캡슐 바닥)' 기준으로 중심+주변 SphereCast를 아래로 쏴서 접지/노멀을 얻는다.
    /// - 살짝 떠 있고 하강속도가 작으며, 스냅 락아웃이 아닐 때만 바닥 Y로 스냅한다.
    /// </summary>
    void GroundProbeAndSnap()
    {
        isGrounded = false;
        groundNormal = Vector3.up;

        // --- 캡슐 기준 정보 추출 ---
        float capRadius = groundCheckRadius;
        float capHeight = capRadius * 2f;
        Vector3 capCenterWS = transform.position + Vector3.up * (groundCheckOffset + groundCheckRadius);

        if (TryGetComponent<CapsuleCollider>(out var capsule))
        {
            capRadius = Mathf.Max(0.05f, capsule.radius);
            capHeight = Mathf.Max(capsule.height, capRadius * 2.01f);
            capCenterWS = transform.TransformPoint(capsule.center);
        }

        // '발(바닥 원)'의 월드 y좌표: 캡슐 중심에서 아래로 (height/2 - radius)
        float footY = capCenterWS.y - (capHeight * 0.5f - capRadius);
        Vector3 footWS = new Vector3(capCenterWS.x, footY, capCenterWS.z);

        // 프로브 시작점: 발에서 약간 위
        float probeRadius = Mathf.Min(groundCheckRadius, capRadius * 0.95f);
        Vector3 originBase = footWS + Vector3.up * (groundCheckOffset + probeRadius + 0.02f);

        // 충분한 캐스트 거리(발 근처에서 아래로 여유 있게)
        float probeDistance = groundCheckOffset + stepOffsetHeight + 0.8f;

        // 주변 오프셋(원형)
        int samples = Mathf.Max(1, groundNormalSamples);
        Vector3 nAccum = Vector3.zero;
        Vector3 hitPoint = Vector3.zero;
        bool found = false;

        for (int i = 0; i < samples; i++)
        {
            Vector3 lateral = Vector3.zero;
            if (i > 0)
            {
                float ang = (360f / (samples - 1)) * (i - 1) * Mathf.Deg2Rad;
                // 발 원판 기준의 주변 샘플 반경(캡슐 반경의 약 0.5)
                float r = capRadius * 0.5f;
                lateral = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
            }

            Vector3 org = originBase + lateral;

            if (Physics.SphereCast(org, probeRadius, Vector3.down, out RaycastHit hit, probeDistance, groundLayers, QueryTriggerInteraction.Ignore))
            {
                found = true;
                nAccum += hit.normal;
                hitPoint = hit.point;
                if (debugDraw) Debug.DrawRay(hit.point, hit.normal * 0.4f, Color.green);
            }
            else if (debugDraw)
            {
                Debug.DrawRay(org, Vector3.down * probeDistance, Color.red);
            }
        }

        if (found)
        {
            isGrounded = true;
            groundNormal = nAccum.normalized;

            // 스냅: 점프 락아웃 중이 아니고, 하강속도 작으며, 바로 아래 얕은 공백일 때만
            float vy = rb.linearVelocity.y;
            bool canSnap = (snapLockoutTimer <= 0f) && (vy <= groundSnapMaxFallSpeed);

            // 발 바로 아래에 공간이 있는지(하향 레이)
            if (canSnap && !Physics.Raycast(originBase, Vector3.down, groundSnapDistance, ~0, QueryTriggerInteraction.Ignore))
            {
                // 발이 놓일 Y 목표값 = 히트 지점 + 오프셋(발 두께 고려)
                float desiredY = hitPoint.y + groundCheckOffset + probeRadius;
                float dy = desiredY - footWS.y;
                if (dy > -groundSnapDistance && dy < 0.05f)
                {
                    // y만 살짝 보정
                    rb.MovePosition(rb.position + new Vector3(0f, dy, 0f));
                }
            }
        }
    }


    // ───────────────────────────────────────────────────────────────────────────
    // 스텝(단차) 승급
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// TryStepUp(moveDir)
    /// - 낮은 레벨에서만 막히고 높은 레벨은 비어 있으면 살짝 올라탄다
    /// - 승급량은 stepOffsetHeight, 보간은 stepLerp(0~1) 사용
    /// </summary>
    void TryStepUp(Vector3 moveDir)
    {
        float capRadius = 0.25f;
        float capHeight = capRadius * 2f;
        Vector3 capCenterWS = transform.position;

        if (TryGetComponent<CapsuleCollider>(out var capsule))
        {
            capRadius = capsule.radius;
            capHeight = Mathf.Max(capsule.height, capRadius * 2.01f);
            capCenterWS = transform.TransformPoint(capsule.center);
        }

        float footY = capCenterWS.y - (capHeight * 0.5f - capRadius);
        Vector3 footWS = new Vector3(capCenterWS.x, footY, capCenterWS.z);

        Vector3 low = footWS + Vector3.up * (groundCheckOffset + 0.02f);
        Vector3 high = footWS + Vector3.up * Mathf.Clamp(stepOffsetHeight + groundCheckOffset, 0.05f, 0.9f);
        float search = Mathf.Max(stepSearchDistance, capRadius * 0.9f);

        if (Physics.SphereCast(low, capRadius * 0.5f, moveDir, out RaycastHit lowHit, search, groundLayers, QueryTriggerInteraction.Ignore))
        {
            bool blockedHigh = Physics.SphereCast(high, capRadius * 0.5f, moveDir, out _, search, groundLayers, QueryTriggerInteraction.Ignore);
            if (!blockedHigh)
            {
                Vector3 climb = Vector3.up * Mathf.Clamp(stepOffsetHeight, 0f, 0.7f);
                Vector3 target = rb.position + climb + moveDir * 0.02f;
                rb.MovePosition(Vector3.Lerp(rb.position, target, Mathf.Clamp01(stepLerp)));

                if (debugDraw)
                {
                    Debug.DrawLine(low, low + moveDir * search, Color.yellow);
                    Debug.DrawLine(high, high + moveDir * search, Color.cyan);
                }
            }
        }
    }


    // ───────────────────────────────────────────────────────────────────────────
    // 카메라 기준 축
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// GetCameraBasis(out camF, out camR)
    /// - 이동 입력(H/V)을 카메라 기준의 전/우 벡터로 변환하기 위한 기준 축 제공
    /// - 실시간 카메라 forward/right(수평투영) 또는 TPS yaw 기반 사용
    /// </summary>
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

    // ───────────────────────────────────────────────────────────────────────────
    // 회전 정책
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// HandleFacingRotation(moveDir)
    /// - 입력/속도를 바탕으로 Yaw 회전 결정
    /// - 전진 의도 시: 턴-인-플레이스(정지) 또는 일반 회전(이동 중)
    /// - 비전진 시: backpedal 정책/퀵턴에 따라 반전/스냅
    /// </summary>
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

                prevLocalZ = localZ; prevLateral = lateral;
                return;
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

        float cur = transform.eulerAngles.y;
        float spd = (enableQuickTurn && !quickTurnSnap) ? quickTurnSpeedDegPerSec : bodyTurnSpeedDegPerSec;
        float nxt = Mathf.MoveTowardsAngle(cur, desiredYaw, spd * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, nxt, 0f);

        prevLocalZ = localZ;
        prevLateral = lateral;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // 애니메이터 파라미터
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// UpdateAnimatorParams()
    /// - 평면 속도를 로컬 공간으로 변환하여 MoveX/MoveZ(-1~1)로 정규화/보간
    /// - Grounded(bool), VerticalSpeed(float)도 갱신
    /// </summary>
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

        // 해시 사용
        animator.SetFloat(_hMoveX, moveXSmoothed);
        animator.SetFloat(_hMoveZ, moveZSmoothed);
        animator.SetBool(_hGrounded, isGrounded);
        animator.SetFloat(_hVSpeed, rb.linearVelocity.y);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // 안전장치: 캡슐 치수 자동 보정
    // ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// OnValidate()
    /// - 에디터에서 값이 바뀔 때 캡슐 치수 보정(Height >= 2*Radius, 하단이 발밑에 오도록)
    /// - 실수로 Height가 0이 되는 상황 방지(항상 접지/점프불가 원인 방지)
    /// </summary>
    void OnValidate()
    {
        if (TryGetComponent<CapsuleCollider>(out var cap))
        {
            cap.radius = Mathf.Max(0.1f, cap.radius);
            cap.height = Mathf.Max(cap.radius * 2.01f, cap.height); // Height >= 2*Radius
            // 피벗이 발바닥에 있다면 center.y를 height*0.5로 두는 게 일반적
            cap.center = new Vector3(0f, cap.height * 0.5f, 0f);
        }
    }
}
