using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
[DefaultExecutionOrder(+50)]
public class VRCoilWithRopeMesh : MonoBehaviour
{
    // ---------- Placement ----------
    public enum PlacementMode { AtThisTransform, Anchors, ExplicitWorld }
    [Header("Placement")]
    public PlacementMode placement = PlacementMode.AtThisTransform;
    public Transform explicitRoot, explicitTip, rootAnchor, tipAnchor;

    // ---------- Post (capture) ----------
    [Header("Post (capture)")]
    public CapsuleCollider post;
    public bool requireGrabToCapture = true;
    [Min(0f)] public float captureMinHoldTime = 0.08f;
    [Range(0.0005f, 0.06f)] public float captureBand = 0.008f;
    [Range(0.001f, 0.12f)] public float exitBand = 0.02f;

    // ---------- Geometry ----------
    [Header("Helix Geometry")]
    [Min(0.001f)] public float pitch = 0.01f;     // meters per turn along axis
    [Min(0.0005f)] public float tubeRadius = 0.004f;
    [Range(3, 24)] public int tubeSides = 8;
    [Range(16, 1200)] public int segmentsPerMeter = 200;
    public bool limitToCylinderSpan = true;
    [Min(0f)] public float endMargin = 0.0015f;

    // ---------- Direction ----------
    public enum WindingDirection { Clockwise = +1, CounterClockwise = -1 }
    [Header("Direction")]
    public WindingDirection allowedDirection = WindingDirection.Clockwise;

    // ---------- Input ----------
    public enum WindInput { TipOrbital }
    [Header("Input")]
    public WindInput windInput = WindInput.TipOrbital;
    [Range(0f, 15f)] public float rollDeadbandDeg = 2.0f;
    [Range(10f, 180f)] public float rollMaxDegPerStep = 45f;
    [Header("Start / Release Gates")]
    [Range(10f, 360f)] public float minDegreesToStart = 90f;
    [Range(0f, 20f)] public float zeroReleaseAngleDeg = 3f;
    [Range(0f, 90f)] public float releaseAngleDeg = 25f;
    public bool autoReleaseOnLetGoNearZero = true;

    // ---------- Tip lock ----------
    [Header("Tip Surface Lock")]
    public bool lockTipToSurface = true;
    [Min(0f)] public float tipSphereRadius = 0.02f;
    public bool disableTipCollisionsWhileCaptured = true;

    // ---------- Post radius override ----------
    [Header("Post Radius Override")]
    public bool overridePostRadius = false;
    [Min(0f)] public float postRadiusOverride = 0.06f;

    // ---------- Tip (XR) ----------
    [Header("Tip (XR)")]
    public Transform tip;
    public XRGrabInteractable tipGrab;
    public Rigidbody tipRB;

    // ---------- Length & physics ----------
    [Header("Length & Physics")]
    [Min(0.02f)] public float startVisibleLength = 1f;
    [Min(0.2f)] public float maxLength = 50f;
    public bool useGravity = true;
    public Vector3 gravity = new(0, -9.81f, 0);
    [Range(0.90f, 0.9995f)] public float damping = 0.996f;
    [Range(1, 40)] public int constraintIterations = 18;
    public bool pinRoot = true;
    [Range(0, 20)] public int pinHeadCount = 0;
    public bool groundClamp = false;
    public float groundY = 0f;

    // ---------- Length budget ----------
    [Header("Length Budget")]
    public bool autoExtendLength = true;
    [Min(1)] public int lengthTurnsBudget = 30;
    [Range(64, 20000)] public int maxSegmentsHardCap = 6000;

    // ---------- Read-only helpers ----------
    public float VisibleLengthMeters => Mathf.Max(0f, (count - 1) * segLen);

    // ---------- Internals ----------
    MeshFilter mf; Mesh mesh;
    Vector3[] pos, prev; int count; float segLen; int maxSeg;

    enum Mode { Free, Hover, Captured }
    Mode mode = Mode.Free;

    // capsule/post (world-space)
    Vector3 capCenterWS, capAxisDirWS; float capHalfSpanWS, capRadiusWS;
    float targetR, halfLinearSpan;

    // continuous helix tracking
    Vector3 baseRadial; float baseZ;
    float thetaTotal; float prevPhi; bool hasPrevPhi;
    bool committed; float commitAccumRad; float hoverTime;

    // arc-length mapping (meters/radian along helix)
    float sPerRad;

    // helpers
    int allowedSign => (int)allowedDirection;
    Collider tipCol;
    bool isGrabbed => tipGrab && tipGrab.isSelected;

    // cooldown after release
    float releaseCooldown = 0f;
    [Min(0f)] public float releaseCooldownTime = 0.15f;

    // ==================== CUT / SPLIT SUPPORT ====================
    [Header("Cut / Split")]
    [Tooltip("Prefab of a rope object (same script) used to spawn the cut tail.")]
    public GameObject ropePrefabForTail;

    // published pointer to the most recent tail (used by WireCutter)
    public static VRCoilWithRopeMesh LastSpawnedTail;

    public int CurrentSegmentCount => count;

    // ---------- Unity ----------
    void Awake()
    {
        mf = GetComponent<MeshFilter>();
        if (!mf.sharedMesh) mf.sharedMesh = new Mesh { name = "VRCoilMesh" };
        mesh = mf.sharedMesh; mesh.MarkDynamic();

        TryAutoAssignPost();
        ResolvePost();
        PrecomputeStepAndArc();

        segLen = 1f / Mathf.Max(1, segmentsPerMeter);

        float budgetMeters = Mathf.Max(startVisibleLength, maxLength);
        if (autoExtendLength) budgetMeters = Mathf.Max(budgetMeters, lengthTurnsBudget * pitch + 0.5f);

        maxSeg = Mathf.Clamp(Mathf.CeilToInt(budgetMeters / segLen) + 8, 32, maxSegmentsHardCap);
        pos = new Vector3[maxSeg]; prev = new Vector3[maxSeg];

        Vector3 root = GetRootWS();
        Vector3 tip0 = GetTipWS();
        count = Mathf.Clamp(Mathf.CeilToInt(startVisibleLength / segLen) + 1, 2, maxSeg);
        for (int i = 0; i < count; i++) pos[i] = prev[i] = Vector3.Lerp(root, tip0, i / (float)(count - 1));

        if (tipGrab)
        {
            tipGrab.selectEntered.AddListener(_ => OnGrab(true));
            tipGrab.selectExited.AddListener(_ => OnGrab(false));
        }
        tipCol = tip ? tip.GetComponent<Collider>() : null;

        PushToMesh();
    }

    void OnDestroy()
    {
        if (tipGrab)
        {
            tipGrab.selectEntered.RemoveAllListeners();
            tipGrab.selectExited.RemoveAllListeners();
        }
    }

    void FixedUpdate()
    {
        if (releaseCooldown > 0f) releaseCooldown = Mathf.Max(0f, releaseCooldown - Time.fixedDeltaTime);

        ResolvePost();
        PrecomputeStepAndArc();

        IntegrateVerlet(Time.fixedDeltaTime);
        if (groundClamp) ClampGround();

        if (mode == Mode.Free) UpdateFree();
        else if (mode == Mode.Hover) UpdateHover();
        else UpdateCaptured();

        ApplyDistanceConstraints();
        PushToMesh();
    }

    // ---------- Physics ----------
    void IntegrateVerlet(float dt)
    {
        Vector3 g = useGravity ? gravity * (dt * dt) : Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = pos[i];
            Vector3 v = (pos[i] - prev[i]) * damping;
            prev[i] = p; pos[i] = p + v + g;
        }
    }

    void ApplyDistanceConstraints()
    {
        for (int it = 0; it < constraintIterations; it++)
        {
            if (pinRoot) { prev[0] = pos[0]; pos[0] = GetRootWS(); }

            for (int i = 0; i < count - 1; i++)
            {
                Vector3 d = pos[i + 1] - pos[i]; float m = d.magnitude; if (m < 1e-8f) continue;
                Vector3 c = d * (1f - segLen / m) * 0.5f; pos[i] += c; pos[i + 1] -= c;
            }

            if (mode != Mode.Captured && tip)
            {
                int last = count - 1; pos[last] = tip.position; prev[last] = tip.position;
            }
        }
    }

    void ClampGround() { for (int i = 0; i < count; i++) if (pos[i].y < groundY) pos[i].y = groundY; }

    // ---------- States ----------

    // NEW: much more forgiving capture — anywhere around the post
    void UpdateFree()
    {
        if (!tip || !post) return;
        if (requireGrabToCapture && !isGrabbed) return;
        if (releaseCooldown > 0f) return;

        Vector3 tipPos = tip.position;
        float r = (tipPos - ClosestOnAxis(tipPos)).magnitude;

        float desired = targetR + (lockTipToSurface ? tipSphereRadius : 0f);

        // generous capture radius: a bit bigger than the post + tip radius
        float captureRadius = desired + captureBand * 2f;

        if (r <= captureRadius)
        {
            EnterCapturedFromTip();
        }
    }

    // NEW: go straight into Captured when near the post (no picky hover region)
    void EnterCapturedFromTip()
    {
        if (!tip || !post) return;

        Vector3 tipPos = tip.position;

        // Start at the z-position where the user is around the post (clamped to span)
        float tipZ = AxisT(tipPos);
        baseZ = Mathf.Clamp(tipZ, -halfLinearSpan + endMargin, halfLinearSpan - endMargin);

        // radial direction from axis through tip
        Vector3 center = AxisPoint(baseZ);
        Vector3 rVec = tipPos - center;
        baseRadial = (rVec.sqrMagnitude > 1e-10f) ? rVec.normalized : AxisRight();

        thetaTotal = 0f;
        committed = false;
        commitAccumRad = 0f;
        hoverTime = 0f;

        float phi = TipAzimuth(tipPos, baseRadial);
        prevPhi = phi;
        hasPrevPhi = true;

        // lock collisions the same way as captured state
        if (disableTipCollisionsWhileCaptured)
        {
            if (tipRB) tipRB.detectCollisions = false;
            if (tipCol) tipCol.isTrigger = true;
        }
        if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.Instantaneous;

        mode = Mode.Captured;

        // snap tip onto the helix surface immediately
        PlaceTipOnSurface(phi, true);
    }

    // (Kept for completeness, but normal flow now skips Hover and goes straight to Captured)
    void UpdateFree_OLD()
    {
        if (!tip || !post) return;
        if (requireGrabToCapture && !isGrabbed) return;
        if (releaseCooldown > 0f) return;

        float desired = targetR + (lockTipToSurface ? tipSphereRadius : 0f);
        float r = (tip.position - ClosestOnAxis(tip.position)).magnitude;
        if (Mathf.Abs(r - desired) <= captureBand) EnterHover();
    }

    void EnterHover()
    {
        Vector3 end = pos[count - 1];

        float z = AxisT(end); baseZ = z;

        Vector3 center = AxisPoint(z);
        Vector3 r = end - center;
        baseRadial = (r.sqrMagnitude > 1e-10f) ? r.normalized : AxisRight();

        thetaTotal = 0f; committed = false; commitAccumRad = 0f;
        hasPrevPhi = false; hoverTime = 0f;

        if (disableTipCollisionsWhileCaptured)
        {
            if (tipRB) tipRB.detectCollisions = true;
            if (tipCol) tipCol.isTrigger = false;
        }
        if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.Instantaneous;

        mode = Mode.Hover;

        float phi = TipAzimuth(tip.position, baseRadial);
        PlaceTipOnSurface(phi, true);
    }

    void UpdateHover()
    {
        if (!tip || !post || (requireGrabToCapture && !isGrabbed)) { ResetCaptureState(); mode = Mode.Free; return; }

        float phi = TipAzimuth(tip.position, baseRadial);
        PlaceTipOnSurface(phi, false);

        float desired = targetR + (lockTipToSurface ? tipSphereRadius : 0f);
        float r = (tip.position - ClosestOnAxis(tip.position)).magnitude;
        if (Mathf.Abs(r - desired) > captureBand + exitBand) { ResetCaptureState(); mode = Mode.Free; return; }

        hoverTime += Time.fixedDeltaTime;
        if (hoverTime >= captureMinHoldTime)
        {
            float leftZ = -halfLinearSpan + endMargin;
            float rightZ = halfLinearSpan - endMargin;
            float tipZ = AxisT(tip.position);
            baseZ = leftZ; // older behaviour we experimented with; no longer used in normal flow

            Vector3 center = AxisPoint(baseZ);
            Vector3 rVec = tip.position - center; rVec.Normalize();
            baseRadial = rVec;

            thetaTotal = 0f; committed = false; commitAccumRad = 0f;
            hasPrevPhi = true; prevPhi = phi;

            if (disableTipCollisionsWhileCaptured)
            {
                if (tipRB) tipRB.detectCollisions = false;
                if (tipCol) tipCol.isTrigger = true;
            }
            if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.Instantaneous;

            mode = Mode.Captured;
        }
    }

    void UpdateCaptured()
    {
        if (!tip) { PlaceFromTotal(thetaTotal, prevPhi, false); return; }

        float phi = TipAzimuth(tip.position, baseRadial);
        float d = ShortestAngle(phi - prevPhi);
        prevPhi = phi;

        if (!committed)
        {
            float dead = Mathf.Deg2Rad * rollDeadbandDeg;
            float clamp = Mathf.Deg2Rad * rollMaxDegPerStep;
            if (Mathf.Abs(d) >= dead) commitAccumRad += Mathf.Abs(Mathf.Clamp(d, -clamp, clamp));
            PlaceTipOnSurface(phi, false);
            if (commitAccumRad >= Mathf.Deg2Rad * minDegreesToStart) committed = true;
            if (requireGrabToCapture && !isGrabbed) { ForceRelease(); return; }
            return;
        }

        {
            float dead = Mathf.Deg2Rad * rollDeadbandDeg;
            float clamp = Mathf.Deg2Rad * rollMaxDegPerStep;
            float step = (Mathf.Abs(d) >= dead) ? Mathf.Clamp(d, -clamp, clamp) : 0f;

            int sgn = step > 0f ? +1 : -1;
            if (sgn == allowedSign) thetaTotal += Mathf.Abs(step);
            else thetaTotal = Mathf.Max(0f, thetaTotal - Mathf.Abs(step));
        }

        if (thetaTotal < Mathf.Deg2Rad * zeroReleaseAngleDeg) { ForceRelease(); return; }

        float visMeters = Mathf.Min(maxLength, thetaTotal * sPerRad + 2f * pitch);
        int desiredSegs = Mathf.Clamp(Mathf.CeilToInt(visMeters * segmentsPerMeter) + 2, 2, maxSeg);
        if (desiredSegs > count)
        {
            for (int i = count; i < desiredSegs && i < maxSeg; i++) pos[i] = prev[i] = pos[count - 1];
            count = Mathf.Min(desiredSegs, maxSeg);
        }

        PlaceFromTotal(thetaTotal, phi, false);
        PackTailByArc(thetaTotal, phi);

        float desiredR2 = targetR + (lockTipToSurface ? tipSphereRadius : 0f);
        float tipR = (tip.position - ClosestOnAxis(tip.position)).magnitude;
        bool pulledAway = tipR > (desiredR2 + exitBand * 0.8f);
        float releaseRad = Mathf.Deg2Rad * Mathf.Max(0f, releaseAngleDeg);
        if (thetaTotal < releaseRad && pulledAway) ForceRelease();
    }

    // ---------- Placement along helix ----------
    void PlaceFromTotal(float thetaTot, float phiEnd, bool instant)
    {
        float twoPi = 2f * Mathf.PI;
        float z = baseZ + allowedSign * (thetaTot / twoPi) * pitch;
        z = Mathf.Clamp(z, -halfLinearSpan + endMargin, halfLinearSpan - endMargin);

        Vector3 radial = RotateAroundAxis(baseRadial, AxisDir(), phiEnd);
        Vector3 pWire = AxisPoint(z) + radial * targetR;
        Vector3 pTip = AxisPoint(z) + radial * (targetR + (lockTipToSurface ? tipSphereRadius : 0f));

        int last = count - 1;
        pos[last] = prev[last] = pWire;

        if (tip)
        {
            if (tipRB && tipRB.isKinematic) { if (instant) tipRB.position = pTip; else tipRB.MovePosition(pTip); }
            else tip.position = pTip;
        }
    }

    void PackTailByArc(float thetaTot, float phiEnd)
    {
        if (count < 3) return;

        int last = count - 1;
        int start = Mathf.Max(1, Mathf.Clamp(pinHeadCount, 0, Mathf.Max(0, count - 2)));

        for (int i = last - 1; i >= start; i--)
        {
            int segsFromEnd = last - i;
            float backMeters = segsFromEnd * segLen;

            float theta_i = Mathf.Max(0f, thetaTot - backMeters / Mathf.Max(1e-8f, sPerRad));
            float twoPi = 2f * Mathf.PI;

            float z_i = baseZ + allowedSign * (theta_i / twoPi) * pitch;
            z_i = Mathf.Clamp(z_i, -halfLinearSpan + endMargin, halfLinearSpan - endMargin);

            float ang_i = phiEnd - (thetaTot - theta_i);

            Vector3 radial_i = RotateAroundAxis(baseRadial, AxisDir(), ang_i);
            pos[i] = prev[i] = AxisPoint(z_i) + radial_i * targetR;
        }
    }

    void PlaceTipOnSurface(float phi, bool instant)
    {
        float z = Mathf.Clamp(baseZ, -halfLinearSpan + endMargin, halfLinearSpan - endMargin);
        Vector3 radial = RotateAroundAxis(baseRadial, AxisDir(), phi);

        Vector3 pWire = AxisPoint(z) + radial * targetR;
        Vector3 pTip = AxisPoint(z) + radial * (targetR + (lockTipToSurface ? tipSphereRadius : 0f));

        int last = count - 1;
        pos[last] = prev[last] = pWire;
        if (tip)
        {
            if (tipRB && tipRB.isKinematic) { if (instant) tipRB.position = pTip; else tipRB.MovePosition(pTip); }
            else tip.position = pTip;
        }
    }

    // ---------- Capture / Release ----------
    void OnGrab(bool down)
    {
        if (down)
        {
            if (mode != Mode.Free && disableTipCollisionsWhileCaptured)
            {
                if (tipRB) tipRB.detectCollisions = false;
                if (tipCol) tipCol.isTrigger = true;
            }
            if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.Instantaneous;
        }
        else
        {
            if (disableTipCollisionsWhileCaptured)
            {
                if (tipRB) tipRB.detectCollisions = true;
                if (tipCol) tipCol.isTrigger = false;
            }
            if (mode == Mode.Captured)
            {
                if (!committed) { ForceRelease(); return; }
                if (autoReleaseOnLetGoNearZero && thetaTotal < Mathf.Deg2Rad * zeroReleaseAngleDeg) { ForceRelease(); return; }
                if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.VelocityTracking;
            }
            else ResetCaptureState();
        }
    }

    void ForceRelease()
    {
        ResetCaptureState();
        mode = Mode.Free;
        releaseCooldown = releaseCooldownTime;
    }

    void ResetCaptureState()
    {
        committed = false; commitAccumRad = 0f;
        thetaTotal = 0f; hasPrevPhi = false;

        if (disableTipCollisionsWhileCaptured)
        {
            if (tipRB) tipRB.detectCollisions = true;
            if (tipCol) tipCol.isTrigger = false;
        }
        if (tipGrab) tipGrab.movementType = XRGrabInteractable.MovementType.VelocityTracking;
    }

    // ---------- Axis / geometry ----------
    void ResolvePost()
    {
        var p = post;
        if (!p)
        {
            capCenterWS = transform.position;
            capAxisDirWS = Vector3.up;
            capHalfSpanWS = 0.12f;
            capRadiusWS = 0.06f;
        }
        else
        {
            if (overridePostRadius) p.radius = postRadiusOverride;

            capCenterWS = p.transform.TransformPoint(p.center);

            Vector3 localAxis = (p.direction == 0) ? Vector3.right : (p.direction == 1) ? Vector3.up : Vector3.forward;
            Vector3 axisWorld = p.transform.TransformDirection(localAxis);
            if (axisWorld.sqrMagnitude < 1e-12f) axisWorld = Vector3.up;
            capAxisDirWS = axisWorld.normalized;

            float axisScale = p.transform.TransformVector(localAxis).magnitude;
            Vector3 localPerpA = (p.direction == 0) ? Vector3.up : Vector3.right;
            Vector3 localPerpB = (p.direction == 2) ? Vector3.up : Vector3.forward;
            float radiusScale = Mathf.Max(
                p.transform.TransformVector(localPerpA).magnitude,
                p.transform.TransformVector(localPerpB).magnitude);

            capRadiusWS = Mathf.Max(1e-5f, p.radius * radiusScale);
            float cylHalfLenLocal = Mathf.Max(0f, p.height * 0.5f - p.radius);
            capHalfSpanWS = cylHalfLenLocal * Mathf.Max(1e-6f, axisScale);
        }

        targetR = capRadiusWS;
        halfLinearSpan = capHalfSpanWS;
    }

    void PrecomputeStepAndArc()
    {
        float R = Mathf.Max(0.0001f, targetR);
        float k = pitch / (2f * Mathf.PI);
        sPerRad = Mathf.Sqrt(R * R + k * k); // meters per radian along helix
        segLen = 1f / Mathf.Max(1, segmentsPerMeter);
    }

    Vector3 AxisDir() => capAxisDirWS;
    Vector3 AxisPoint(float z) { z = Mathf.Clamp(z, -halfLinearSpan, halfLinearSpan); return capCenterWS + capAxisDirWS * z; }
    float AxisT(Vector3 worldPos) { float t = Vector3.Dot(worldPos - capCenterWS, capAxisDirWS); return Mathf.Clamp(t, -halfLinearSpan, halfLinearSpan); }
    Vector3 ClosestOnAxis(Vector3 wp)
    {
        Vector3 a = capCenterWS - capAxisDirWS * capHalfSpanWS;
        Vector3 b = capCenterWS + capAxisDirWS * capHalfSpanWS;
        Vector3 ab = b - a; float u = Mathf.Clamp01(Vector3.Dot(wp - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude)); return a + ab * u;
    }
    Vector3 AxisRight()
    {
        Vector3 d = AxisDir(); Vector3 up = Mathf.Abs(Vector3.Dot(d, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
        return Vector3.Cross(up, d).normalized;
    }
    static Vector3 RotateAroundAxis(Vector3 v, Vector3 axis, float angRad)
    {
        return Quaternion.AngleAxis(angRad * Mathf.Rad2Deg, axis) * v;
    }
    static float ShortestAngle(float d) { while (d > Mathf.PI) d -= 2f * Mathf.PI; while (d < -Mathf.PI) d += 2f * Mathf.PI; return d; }

    float TipAzimuth(Vector3 worldPos, Vector3 baseU)
    {
        Vector3 axis = AxisDir();
        Vector3 center = ClosestOnAxis(worldPos);
        Vector3 r = worldPos - center;

        if (r.sqrMagnitude < 1e-10f)
            r = baseU * (targetR + Mathf.Max(0f, tipSphereRadius));

        r.Normalize();

        Vector3 u = baseU;
        Vector3 v = Vector3.Cross(axis, u);
        if (v.sqrMagnitude < 1e-8f)
        {
            u = AxisRight();
            v = Vector3.Cross(axis, u).normalized;
        }

        float x = Vector3.Dot(r, u);
        float y = Vector3.Dot(r, v);
        return Mathf.Atan2(y, x); // radians
    }

    // ---------- Mesh ----------
    void PushToMesh()
    {
        if (count < 2) return;
        int ringCount = count, vertsPerRing = tubeSides;
        int vertCount = ringCount * vertsPerRing, triCount = (ringCount - 1) * tubeSides * 2;

        var v = new Vector3[vertCount];
        var n = new Vector3[vertCount];
        var uv = new Vector2[vertCount];
        var t = new int[triCount * 3];

        for (int i = 0; i < ringCount; i++)
        {
            Vector3 p = pos[i];
            Vector3 fwd = (i == 0) ? (pos[i + 1] - pos[i]).normalized :
                          (i == ringCount - 1) ? (pos[i] - pos[i - 1]).normalized :
                                            (pos[i + 1] - pos[i - 1]).normalized;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;

            Vector3 up = Mathf.Abs(Vector3.Dot(fwd.normalized, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            Vector3 right = Vector3.Cross(fwd, up).normalized;
            up = Vector3.Cross(right, fwd).normalized;

            for (int s = 0; s < tubeSides; s++)
            {
                float a = (s / (float)tubeSides) * Mathf.PI * 2f;
                Vector3 off = right * Mathf.Cos(a) * tubeRadius + up * Mathf.Sin(a) * tubeRadius;
                int idx = i * vertsPerRing + s;
                v[idx] = transform.InverseTransformPoint(p + off);
                n[idx] = transform.InverseTransformDirection(off.normalized);
                uv[idx] = new Vector2(s / (float)tubeSides, i / (float)(ringCount - 1));
            }
        }
        int tri = 0;
        for (int i = 0; i < ringCount - 1; i++)
            for (int s = 0; s < tubeSides; s++)
            {
                int a0 = i * vertsPerRing + s, a1 = i * vertsPerRing + ((s + 1) % tubeSides);
                int b0 = (i + 1) * vertsPerRing + s, b1 = (i + 1) * vertsPerRing + ((s + 1) % tubeSides);
                t[tri++] = a0; t[tri++] = b0; t[tri++] = a1;
                t[tri++] = a1; t[tri++] = b0; t[tri++] = b1;
            }

        mesh.Clear();
        mesh.vertices = v; mesh.normals = n; mesh.uv = uv; mesh.triangles = t; mesh.RecalculateBounds();
    }

    // ---------- Public API used by feeder & cutter ----------
    public void SetVisibleLengthMeters(float meters)
    {
        meters = Mathf.Clamp(meters, 0.02f, maxLength);
        int desired = Mathf.Clamp(Mathf.CeilToInt(meters * Mathf.Max(1, segmentsPerMeter)) + 1, 2, maxSeg);
        if (desired != count)
        {
            if (desired > count)
                for (int i = count; i < desired && i < maxSeg; i++)
                    pos[i] = prev[i] = pos[count - 1];

            count = desired;
        }
    }

    public int GetClosestSegmentIndex(Vector3 worldPoint, float maxDist)
    {
        if (count < 2) return -1;
        int best = -1;
        float bestSqr = maxDist * maxDist;
        for (int i = 0; i < count; i++)
        {
            float d = (pos[i] - worldPoint).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = i; }
        }
        return best;
    }

    public Vector3 GetLastParticleWS() => (count > 0) ? pos[count - 1] : transform.position;
    public void ForceMeshRefresh() => PushToMesh();

    public void RequestSplitAt(int splitIndex, Vector3 worldCutPoint)
    {
        if (splitIndex <= 0 || splitIndex >= count - 1) return;
        if (!ropePrefabForTail) { Debug.LogWarning("[VRCoilWithRopeMesh] ropePrefabForTail is not assigned."); return; }

        var feeder = GetComponentInParent<WireReelFeederRealistic>();
        bool reenable = feeder && feeder.enabled;
        if (reenable) feeder.enabled = false;

        var tailGO = Instantiate(ropePrefabForTail, transform.parent);
        var tail = tailGO.GetComponent<VRCoilWithRopeMesh>();
        if (!tail) { Debug.LogError("[VRCoilWithRopeMesh] Tail prefab must also have VRCoilWithRopeMesh."); if (reenable) feeder.enabled = true; return; }

        CopyPublicSettingsTo(tail);
        SplitParticlesInto(this, tail, splitIndex, worldCutPoint);

        // Keep the grabbable tip on the reel side (your requirement):
        // (do not move 'tip' to tail)

        ForceMeshRefresh();
        tail.ForceMeshRefresh();

        LastSpawnedTail = tail;

        if (reenable) feeder.enabled = true;
    }

    void CopyPublicSettingsTo(VRCoilWithRopeMesh dst)
    {
        // Geometry & sim
        dst.pitch = this.pitch;
        dst.tubeRadius = this.tubeRadius;
        dst.tubeSides = this.tubeSides;
        dst.segmentsPerMeter = this.segmentsPerMeter;
        dst.limitToCylinderSpan = this.limitToCylinderSpan;
        dst.endMargin = this.endMargin;

        // physics
        dst.useGravity = this.useGravity;
        dst.gravity = this.gravity;
        dst.damping = this.damping;
        dst.constraintIterations = this.constraintIterations;

        // post/refs
        dst.post = this.post;
        dst.disableTipCollisionsWhileCaptured = this.disableTipCollisionsWhileCaptured;

        // materials
        var mrSrc = GetComponent<MeshRenderer>();
        var mrDst = dst.GetComponent<MeshRenderer>();
        if (mrSrc && mrDst) mrDst.sharedMaterials = mrSrc.sharedMaterials;
    }

    void EnsureCapacityLike(VRCoilWithRopeMesh other)
    {
        if (pos == null || pos.Length < other.pos.Length) pos = new Vector3[other.pos.Length];
        if (prev == null || prev.Length < other.prev.Length) prev = new Vector3[other.prev.Length];
        if (maxSeg < other.maxSeg) maxSeg = other.maxSeg;
    }

    void SplitParticlesInto(VRCoilWithRopeMesh reelSide, VRCoilWithRopeMesh tailSide, int splitIndex, Vector3 cutPointWS)
    {
        int nReel = splitIndex + 1;
        int nTail = reelSide.count - splitIndex;

        tailSide.EnsureCapacityLike(reelSide);

        for (int i = 0; i < nTail; i++)
        {
            tailSide.pos[i] = reelSide.pos[splitIndex + i];
            tailSide.prev[i] = reelSide.prev[splitIndex + i];
        }
        tailSide.count = nTail;

        reelSide.count = nReel;
        reelSide.pos[nReel - 1] = cutPointWS;
        reelSide.prev[nReel - 1] = cutPointWS;

        if (nTail > 1)
        {
            Vector3 dir = (tailSide.pos[1] - tailSide.pos[0]).normalized;
            tailSide.prev[0] = tailSide.pos[0] - dir * 0.01f;
        }
    }

    // ---------- Utilities ----------
    Vector3 GetRootWS()
    {
        return placement switch
        {
            PlacementMode.AtThisTransform => transform.position,
            PlacementMode.Anchors => rootAnchor ? rootAnchor.position : transform.position,
            PlacementMode.ExplicitWorld => explicitRoot ? explicitRoot.position : transform.position,
            _ => transform.position
        };
    }

    Vector3 GetTipWS()
    {
        if (placement == PlacementMode.AtThisTransform) return transform.position + transform.up * startVisibleLength;
        if (placement == PlacementMode.Anchors && tipAnchor) return tipAnchor.position;
        if (placement == PlacementMode.ExplicitWorld && explicitTip) return explicitTip.position;
        return transform.position + transform.up * startVisibleLength;
    }

    void TryAutoAssignPost()
    {
        if (post) return;
        post = GetComponent<CapsuleCollider>();
        if (!post) post = GetComponentInChildren<CapsuleCollider>();
    }
}

