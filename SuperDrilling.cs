using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Combined behaviour:
/// • Drill spin
/// • Staged hole depth based on physical penetration (no time gating)
/// • Dust FX: contact burst, deepen burst, continuous fountain
/// • Base surface dust decal + patches (only when drilling vertically down)
/// • Dust always settles on the top face of the block (gravity)
/// • Dust clamped to a dedicated DustArea collider so it never crosses edges
/// • Drilling only into assigned objects
/// • Hole size stays CONSTANT in world space regardless of parent (wood block) scale
/// • Haptics via InputDevices: light in air, depth-based stronger in wood (0.3 → 0.9),
///   hand detected from the interactor’s parent chain (Left Hand / Right Hand etc.)
///
/// Screwing:
/// • Creates a ScrewSocket ONLY when the hole is fully drilled (final stage reached).
/// • Socket axis is based on holeAxisWorld (into wood) and is NOT affected by holeRotationOffsetEuler.
/// </summary>
public class SuperDrilling : MonoBehaviour
{
    // ------------------------------------------------------
    // SPIN SETTINGS
    // ------------------------------------------------------

    [Header("Spin Settings")]
    public UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable;
    public Transform[] rotatingParts;
    public float maxSpinSpeed = 1500f;
    public float spinAcceleration = 3000f;
    public float spinDeceleration = 2000f;

    float currentSpinSpeed = 0f;
    bool triggerHeld = false;

    // ------------------------------------------------------
    // SCREWING (ENABLED)
    // ------------------------------------------------------

    [Header("Screwing Integration")]
    public bool enableScrewing = true;

    [Tooltip("Snap radius for the screw seating into this drilled hole.")]
    public float screwSocketSnapRadius = 0.03f;

    [Tooltip("How much the screw is initially inset when seated.")]
    public float screwSocketInsetMeters = 0.003f;

    [Tooltip("How aligned the screw must be to seat (1=perfect).")]
    [Range(0f, 1f)]
    public float screwSocketAlignmentDot = 0.6f;

    // NEW: gate so socket is created only when fully drilled
    bool socketCreatedForThisHole = false;

    // ------------------------------------------------------
    // DRILL RAYCAST FILTERING (OPTION A)
    // ------------------------------------------------------

    [Header("Drill Raycast Filtering")]
    [Tooltip("Layer name used for the runtime ScrewSocket sphere so the drill raycast ignores it.")]
    public string screwSocketLayerName = "ScrewSocket";

    int _drillRaycastMask = ~0;

    void RefreshDrillRaycastMask()
    {
        int socketLayer = LayerMask.NameToLayer(screwSocketLayerName);
        if (socketLayer >= 0)
        {
            _drillRaycastMask = ~(1 << socketLayer);
        }
        else
        {
            // If layer not found, don't exclude anything (fail-safe)
            _drillRaycastMask = ~0;
        }
    }

    // ------------------------------------------------------
    // AUDIO
    // ------------------------------------------------------

    [Header("Audio")]
    [Tooltip("Looping drilling sound while spinning in air (no valid wood contact).")]
    public AudioSource airDrillAudio;

    [Tooltip("Looping drilling sound while actually drilling into wood.")]
    public AudioSource woodDrillAudio;

    [Header("Audio Fading")]
    [Tooltip("How long it takes for drill sounds to fade out when stopping (seconds).")]
    public float fadeOutTime = 0.15f;

    Coroutine airFadeCoroutine;
    Coroutine woodFadeCoroutine;

    [Header("Audio Loop Region")]
    [Tooltip("If true, only loop the middle part of clips (avoids obvious restart).")]
    public bool useCustomLoopRegion = false;

    [Tooltip("Loop window for the air-drilling clip (seconds within the clip).")]
    public float airLoopStart = 0.3f;
    public float airLoopEnd = 2.7f;

    [Tooltip("Loop window for the wood-drilling clip (seconds within the clip).")]
    public float woodLoopStart = 0.3f;
    public float woodLoopEnd = 2.7f;

    // ------------------------------------------------------
    // HAPTICS
    // ------------------------------------------------------

    [Header("Haptics")]
    [Tooltip("Strength while drilling in air (light vibration).")]
    [Range(0f, 1f)] public float airHapticStrength = 0.2f;

    [Tooltip("Minimum haptic strength when just entering wood (~0% depth).")]
    [Range(0f, 1f)] public float woodHapticMin = 0.3f;

    [Tooltip("Maximum haptic strength at full depth (~100% depth).")]
    [Range(0f, 1f)] public float woodHapticMax = 0.9f;

    [Tooltip("How long each haptic impulse lasts (seconds).")]
    public float hapticDuration = 0.02f;

    // ------------------------------------------------------
    // DRILLING REFERENCES
    // ------------------------------------------------------

    [Header("Drilling References")]
    public Transform drillTip;
    public GameObject[] holeStages;

    [Tooltip("Only these objects (and their children) are drillable.")]
    public GameObject[] drillableObjects;

    // ------------------------------------------------------
    // DRILLING SETTINGS
    // ------------------------------------------------------

    [Header("Drilling Settings")]
    [Tooltip("Raycast distance from drill tip to find surface (can be longer for detection).")]
    public float raycastDistance = 0.2f;

    [Tooltip("Must be this close to the surface to actually count as drilling (meters). Try 0.005–0.01.")]
    public float contactDistance = 0.01f;

    [Tooltip("Max PERPENDICULAR distance from hole axis to still count as same hole.")]
    public float maxContactDistance = 0.02f;

    [Tooltip("How much physical forward movement (m) corresponds to maximum depth.")]
    public float maxPhysicalDepth = 0.05f;

    [Tooltip("Unused now (kept so the inspector doesn't break).")]
    public float secondsToMaxDepth = 1.5f;

    [Header("Hole Offset & Rotation")]
    [Tooltip("Extra rotation (degrees) applied to the hole prefab.")]
    public Vector3 holeRotationOffsetEuler = new Vector3(0, 180, 0);

    [Tooltip("Offset along the hole forward axis so the big mouth stays on the surface.")]
    public float holeForwardOffset = 0f;

    [Header("Hole World Size")]
    [Tooltip("Final world-space size (X,Y,Z) of the hole mesh, regardless of parent scale.")]
    public Vector3 holeWorldSize = new Vector3(0.03f, 0.03f, 0.04f);

    // ------------------------------------------------------
    // WHEN IS SURFACE DUST ALLOWED?
    // ------------------------------------------------------

    [Header("Dust Placement Rules")]
    [Tooltip("How close drill direction must be to vertical (down) to allow dust settling on top of the block.")]
    [Range(0f, 1f)]
    public float verticalDrillThreshold = 0.7f;

    [Tooltip("Keep surface dust this far away from the dust area edges.")]
    public float dustEdgeMargin = 0.02f;

    [Header("Dust Area Override")]
    public BoxCollider dustAreaOverride;

    // ------------------------------------------------------
    // DUST FX (PARTICLES)
    // ------------------------------------------------------

    [Header("Dust FX (Particles)")]
    public ParticleSystem contactDustFX;
    public ParticleSystem deepenDustFX;
    public ParticleSystem continuousDustFX;

    // ------------------------------------------------------
    // BASE SURFACE DUST DECAL
    // ------------------------------------------------------

    [Header("Base Surface Dust On Block")]
    public GameObject surfaceDustPrefab;
    public float surfaceDustForwardOffset = 0.0005f;
    public float surfaceDustMinSize = 0.02f;
    public float surfaceDustMaxSize = 0.04f;
    public float surfaceDustAngleOffset = 0f;

    [Header("Base Surface Dust Intensity")]
    [Range(0f, 1f)] public float surfaceDustMinAlpha = 0.05f;
    [Range(0f, 1f)] public float surfaceDustMaxAlpha = 0.4f;
    public float surfaceDustMinTiling = 1f;
    public float surfaceDustMaxTiling = 1.4f;

    GameObject currentSurfaceDust;
    MeshRenderer currentSurfaceRenderer;
    Material currentSurfaceMaterial;

    // ------------------------------------------------------
    // MANY SMALL DUST PATCHES
    // ------------------------------------------------------

    [Header("Surface Dust Patches (small blobs)")]
    public GameObject surfaceDustPatchPrefab;

    [Range(0f, 1f)] public float patchStartDepth = 0.15f;
    public float patchIntervalAtStart = 0.3f;
    public float patchIntervalAtMax = 0.05f;
    public int maxPatchesPerHole = 25;
    public float patchRadiusMin = 0.01f;
    public float patchRadiusMax = 0.03f;
    public Vector2 patchSizeRange = new Vector2(0.01f, 0.02f);

    [Header("Randomness Per Hole")]
    [Range(0f, 1f)] public float patchIntervalRandomness = 0.3f;
    [Range(0f, 1f)] public float patchRadiusRandomness = 0.3f;

    float patchSpawnTimer = 0f;
    int patchesSpawned = 0;

    float perHoleIntervalFactor = 1f;
    float perHoleRadiusFactor = 1f;

    // ------------------------------------------------------
    // DEBUG
    // ------------------------------------------------------

    public bool debugRay = true;
    public bool ignoreTriggerForDebug = false;

    // ------------------------------------------------------
    // INTERNAL STATE
    // ------------------------------------------------------

    GameObject currentHole;
    Transform sessionParent;
    BoxCollider sessionCollider;

    Vector3 sessionPointWorld;
    Vector3 sessionNormalWorld;
    Vector3 holeAxisWorld;

    Vector3 dustPlaneNormalWorld;
    float dustPlaneHeightWorld;
    Vector3 dustCenterWorld;

    float accumulatedDepth = 0f;
    float lastProjection = 0f;
    float accumulatedTime = 0f;
    int currentStage = -1;
    int maxStageReached = 0;
    bool drillingSessionActive = false;

    bool allowSurfaceDustThisHole = false;

    // =====================================================================
    // INPUT EVENTS
    // =====================================================================

    void OnEnable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.activated.AddListener(OnTriggerPress);
            grabInteractable.deactivated.AddListener(OnTriggerRelease);
        }

        if (airDrillAudio != null) airDrillAudio.loop = true;
        if (woodDrillAudio != null) woodDrillAudio.loop = true;

        // OPTION A: compute raycast mask (excludes ScrewSocket layer)
        RefreshDrillRaycastMask();
    }

    void OnDisable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.activated.RemoveListener(OnTriggerPress);
            grabInteractable.deactivated.RemoveListener(OnTriggerRelease);
        }

        StopContinuousDust();

        if (airFadeCoroutine != null) StopCoroutine(airFadeCoroutine);
        if (woodFadeCoroutine != null) StopCoroutine(woodFadeCoroutine);
        airFadeCoroutine = null;
        woodFadeCoroutine = null;

        if (airDrillAudio != null)
        {
            airDrillAudio.Stop();
            airDrillAudio.volume = 1f;
        }
        if (woodDrillAudio != null)
        {
            woodDrillAudio.Stop();
            woodDrillAudio.volume = 1f;
        }
    }

    void OnTriggerPress(ActivateEventArgs args)
    {
        triggerHeld = true;

        ResetSessionState();
        drillingSessionActive = true;
    }

    void OnTriggerRelease(DeactivateEventArgs args)
    {
        triggerHeld = false;
        drillingSessionActive = false;

        ResetSessionState();
        StopContinuousDust();
        UpdateAudio(false, false);
    }

    // =====================================================================
    // UPDATE LOOP
    // =====================================================================

    void Update()
    {
        bool isGrabbed = grabInteractable != null && grabInteractable.isSelected;

        UpdateSpin(isGrabbed && triggerHeld);
        UpdateDrilling(isGrabbed);

        if (useCustomLoopRegion)
        {
            UpdateLoopRegion(airDrillAudio, airLoopStart, airLoopEnd);
            UpdateLoopRegion(woodDrillAudio, woodLoopStart, woodLoopEnd);
        }
    }

    // =====================================================================
    // DRILL SPIN
    // =====================================================================

    void UpdateSpin(bool spinInput)
    {
        if (spinInput)
            currentSpinSpeed = Mathf.MoveTowards(currentSpinSpeed, maxSpinSpeed, spinAcceleration * Time.deltaTime);
        else
            currentSpinSpeed = Mathf.MoveTowards(currentSpinSpeed, 0, spinDeceleration * Time.deltaTime);

        if (Mathf.Abs(currentSpinSpeed) < 1f) return;

        foreach (Transform t in rotatingParts)
        {
            if (t != null)
                t.Rotate(Vector3.up, currentSpinSpeed * Time.deltaTime, Space.Self);
        }
    }

    // =====================================================================
    // DRILLING LOGIC
    // =====================================================================

    void UpdateDrilling(bool isGrabbed)
    {
        if (!isGrabbed || drillTip == null)
        {
            UpdateAudio(false, false);
            return;
        }

        bool drillingInput = ignoreTriggerForDebug ? isGrabbed : (triggerHeld && isGrabbed);
        if (!drillingInput || !drillingSessionActive)
        {
            StopContinuousDust();
            UpdateAudio(false, false);
            return;
        }

        bool insideCurrentWood = false;
        if (drillingSessionActive && sessionCollider != null && currentHole != null)
        {
            insideCurrentWood = sessionCollider.bounds.Contains(drillTip.position);
        }

        Vector3 origin = drillTip.position;
        Vector3 dir = drillTip.forward;

        if (debugRay)
            Debug.DrawRay(origin, dir * raycastDistance, Color.red);

        // OPTION A: raycast ignores ScrewSocket layer via _drillRaycastMask
        if (Physics.Raycast(origin, dir, out RaycastHit hit, raycastDistance, _drillRaycastMask, QueryTriggerInteraction.UseGlobal))
        {
            bool tipTouchingSurface = hit.distance <= Mathf.Max(0.0001f, contactDistance);

            if (!IsDrillable(hit.transform))
            {
                if (insideCurrentWood)
                {
                    float radialInside = DistancePointToLine(drillTip.position, sessionPointWorld, holeAxisWorld);
                    if (radialInside <= maxContactDistance)
                    {
                        UpdateDepth();
                        UpdateContinuousDustFountain();
                        UpdateAudio(true, true);
                        SendDepthBasedWoodHaptics();
                        return;
                    }
                }

                StopContinuousDust();
                UpdateAudio(true, false);
                SendHaptics(airHapticStrength, hapticDuration);
                return;
            }

            if (!tipTouchingSurface)
            {
                StopContinuousDust();
                UpdateAudio(true, false);
                SendHaptics(airHapticStrength, hapticDuration);
                return;
            }

            if (currentHole == null)
            {
                AnchorHole(hit);
                UpdateAudio(true, true);
                SendDepthBasedWoodHaptics();
                return;
            }

            float radial = DistancePointToLine(drillTip.position, sessionPointWorld, holeAxisWorld);
            if (radial > maxContactDistance)
            {
                StopContinuousDust();
                UpdateAudio(true, false);
                SendHaptics(airHapticStrength, hapticDuration);
                return;
            }

            UpdateDepth();
            UpdateContinuousDustFountain();
            UpdateAudio(true, true);
            SendDepthBasedWoodHaptics();
            return;
        }

        if (insideCurrentWood && currentHole != null)
        {
            float radialInside = DistancePointToLine(drillTip.position, sessionPointWorld, holeAxisWorld);
            if (radialInside <= maxContactDistance)
            {
                UpdateDepth();
                UpdateContinuousDustFountain();
                UpdateAudio(true, true);
                SendDepthBasedWoodHaptics();
                return;
            }
        }

        StopContinuousDust();
        UpdateAudio(true, false);
        SendHaptics(airHapticStrength, hapticDuration);
    }

    // =====================================================================
    // HOLE ANCHORING
    // =====================================================================

    void AnchorHole(RaycastHit hit)
    {
        sessionParent = GetDrillableRoot(hit.transform);
        sessionCollider = sessionParent != null ? sessionParent.GetComponentInChildren<BoxCollider>() : null;

        sessionPointWorld = hit.point;
        sessionNormalWorld = hit.normal.normalized;
        holeAxisWorld = -sessionNormalWorld;

        Vector3 ha = holeAxisWorld.normalized;
        float vDot = Mathf.Abs(Vector3.Dot(ha, Vector3.down));
        allowSurfaceDustThisHole = vDot >= verticalDrillThreshold;

        accumulatedDepth = 0f;
        lastProjection = 0f;
        accumulatedTime = 0f;
        currentStage = 0;
        maxStageReached = 0;

        // NEW: reset socket gate for this hole
        socketCreatedForThisHole = false;

        patchSpawnTimer = 0f;
        patchesSpawned = 0;

        float r1 = Random.value;
        float r2 = Random.value;
        perHoleIntervalFactor =
            Mathf.Lerp(1f - patchIntervalRandomness, 1f + patchIntervalRandomness, r1);
        perHoleRadiusFactor =
            Mathf.Lerp(1f - 0.5f * patchRadiusRandomness, 1f + 0.5f * patchRadiusRandomness, r2);

        if (allowSurfaceDustThisHole)
        {
            dustPlaneNormalWorld = Vector3.up;

            BoxCollider clampCol = dustAreaOverride != null ? dustAreaOverride : sessionCollider;

            if (clampCol != null)
            {
                Bounds b = clampCol.bounds;
                dustPlaneHeightWorld = b.max.y + surfaceDustForwardOffset;
            }
            else
            {
                dustPlaneHeightWorld = sessionPointWorld.y + surfaceDustForwardOffset;
            }

            dustCenterWorld = new Vector3(sessionPointWorld.x, dustPlaneHeightWorld, sessionPointWorld.z);
            ClampInsideDustArea(ref dustCenterWorld, surfaceDustMaxSize * 0.5f);
        }

        SpawnHoleStage(0);

        // IMPORTANT CHANGE:
        // We do NOT create the screw socket here anymore.
        // It will be created ONLY when the hole reaches final stage in UpdateDepth().

        if (allowSurfaceDustThisHole)
            SpawnSurfaceDustDecal();

        PlayDust(contactDustFX, sessionPointWorld, Quaternion.LookRotation(sessionNormalWorld));
    }

    // =====================================================================
    // CREATE SCREW SOCKET (ONLY ONCE, ONLY WHEN FULLY DRILLED)
    // =====================================================================

    void CreateScrewSocketAtCurrentHole()
    {
        if (sessionParent == null) return;

        GameObject go = new GameObject("ScrewSocket_Runtime");
        go.transform.SetParent(sessionParent, true);

        // OPTION A: put the socket on a dedicated layer so the drill raycast ignores it
        int socketLayer = LayerMask.NameToLayer(screwSocketLayerName);
        if (socketLayer >= 0)
            go.layer = socketLayer;

        go.transform.position = sessionPointWorld;

        // Axis INTO wood. Your holeRotationOffsetEuler (Y=180) does NOT affect this.
        go.transform.rotation = Quaternion.LookRotation(holeAxisWorld, Vector3.up);

        SphereCollider sc = go.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = screwSocketSnapRadius;

        ScrewSocket socket = go.AddComponent<ScrewSocket>();
        socket.snapRadius = screwSocketSnapRadius;
        socket.snapInsetMeters = screwSocketInsetMeters;
        socket.snapAlignmentDot = screwSocketAlignmentDot;
    }

    // =====================================================================
    // DEPTH CALCULATION
    // =====================================================================

    void UpdateDepth()
    {
        Vector3 tipOffset = drillTip.position - sessionPointWorld;
        float projection = Vector3.Dot(tipOffset, holeAxisWorld);

        float delta = projection - lastProjection;

        if (delta > 0f)
            accumulatedDepth += delta;

        lastProjection = projection;

        float finalFactor = Mathf.Clamp01(accumulatedDepth / maxPhysicalDepth);

        int targetStage = Mathf.Clamp(
            Mathf.FloorToInt(finalFactor * (holeStages.Length - 1)),
            0,
            holeStages.Length - 1
        );

        if (targetStage < maxStageReached)
            targetStage = maxStageReached;

        if (targetStage != currentStage)
        {
            currentStage = targetStage;
            maxStageReached = targetStage;

            if (currentHole != null)
                Destroy(currentHole);

            SpawnHoleStage(targetStage);

            PlayDust(deepenDustFX, sessionPointWorld, Quaternion.LookRotation(sessionNormalWorld));
        }

        // NEW: create screw socket ONLY when fully drilled (final stage reached)
        if (enableScrewing && !socketCreatedForThisHole && holeStages != null && holeStages.Length > 0)
        {
            int finalStageIndex = holeStages.Length - 1;
            if (maxStageReached >= finalStageIndex)
            {
                CreateScrewSocketAtCurrentHole();
                socketCreatedForThisHole = true;
            }
        }

        if (allowSurfaceDustThisHole)
        {
            UpdateSurfaceDustScale(finalFactor);
            UpdateSurfaceDustVisual(finalFactor);
            UpdateDustPatches(finalFactor);
        }
    }

    // =====================================================================
    // HOLE SPAWNING (KEEP YOUR VISUAL ROTATION, FIX PREFAB AXIS SAFELY)
    // =====================================================================

    void SpawnHoleStage(int stage)
    {
        if (stage < 0 || stage >= holeStages.Length) return;
        if (sessionParent == null) return;

        // Axis INTO wood (this is already correct in your system)
        Quaternion baseRot = Quaternion.LookRotation(holeAxisWorld, Vector3.up);

        // Visual rotation — KEEP your Y = 180 (this is required for your mesh)
        Quaternion finalRot = baseRot * Quaternion.Euler(holeRotationOffsetEuler);

        // Offset along the SAME forward direction the hole mesh uses
        Vector3 forwardOffset = (finalRot * Vector3.forward) * holeForwardOffset;

        Vector3 parentScale = sessionParent.lossyScale;
        Vector3 localScale = new Vector3(
            holeWorldSize.x / Mathf.Max(parentScale.x, 0.0001f),
            holeWorldSize.y / Mathf.Max(parentScale.y, 0.0001f),
            holeWorldSize.z / Mathf.Max(parentScale.z, 0.0001f)
        );

        if (currentHole != null)
            Destroy(currentHole);

        currentHole = Instantiate(
            holeStages[stage],
            sessionPointWorld + forwardOffset,
            finalRot,
            sessionParent
        );

        currentHole.transform.localScale = localScale;
    }

    // =====================================================================
    // BASE SURFACE DUST DECAL
    // =====================================================================

    void SpawnSurfaceDustDecal()
    {
        if (!allowSurfaceDustThisHole) return;
        if (surfaceDustPrefab == null || sessionParent == null)
            return;

        Quaternion align = Quaternion.FromToRotation(Vector3.forward, dustPlaneNormalWorld);
        Quaternion extra = Quaternion.AngleAxis(surfaceDustAngleOffset, dustPlaneNormalWorld);
        Quaternion rot = extra * align;

        Vector3 pos = dustCenterWorld;

        currentSurfaceDust = Instantiate(surfaceDustPrefab, pos, rot, sessionParent);

        float s = surfaceDustMinSize;
        currentSurfaceDust.transform.localScale = new Vector3(s, s, s);

        currentSurfaceRenderer = currentSurfaceDust.GetComponentInChildren<MeshRenderer>();
        if (currentSurfaceRenderer != null)
        {
            currentSurfaceMaterial = currentSurfaceRenderer.material;
            UpdateSurfaceDustVisual(0f);
        }
        else
        {
            currentSurfaceMaterial = null;
        }
    }

    void UpdateSurfaceDustScale(float depthFactor01)
    {
        if (!allowSurfaceDustThisHole) return;
        if (currentSurfaceDust == null) return;

        float t = Mathf.Clamp01(depthFactor01);
        float s = Mathf.Lerp(surfaceDustMinSize, surfaceDustMaxSize, t);
        currentSurfaceDust.transform.localScale = new Vector3(s, s, s);
    }

    void UpdateSurfaceDustVisual(float depthFactor01)
    {
        if (!allowSurfaceDustThisHole) return;
        if (currentSurfaceMaterial == null) return;

        float t = Mathf.Clamp01(depthFactor01);

        float alpha = Mathf.Lerp(surfaceDustMinAlpha, surfaceDustMaxAlpha, t);
        Color col = currentSurfaceMaterial.color;
        col.a = alpha;
        currentSurfaceMaterial.color = col;

        float tiling = Mathf.Lerp(surfaceDustMinTiling, surfaceDustMaxTiling, t);
        currentSurfaceMaterial.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
    }

    // =====================================================================
    // DUST PATCHES
    // =====================================================================

    void UpdateDustPatches(float depthFactor01)
    {
        if (!allowSurfaceDustThisHole) return;
        if (surfaceDustPatchPrefab == null || sessionParent == null)
            return;

        if (depthFactor01 < patchStartDepth)
            return;

        if (patchesSpawned >= maxPatchesPerHole)
            return;

        float t = Mathf.Clamp01(depthFactor01);
        float intervalBase = Mathf.Lerp(patchIntervalAtStart, patchIntervalAtMax, t);
        float interval = intervalBase * perHoleIntervalFactor;

        patchSpawnTimer += Time.deltaTime;
        if (patchSpawnTimer < interval)
            return;

        patchSpawnTimer = 0f;
        SpawnDustPatch();
    }

    void SpawnDustPatch()
    {
        if (!allowSurfaceDustThisHole) return;
        if (surfaceDustPatchPrefab == null || sessionParent == null)
            return;

        Vector3 n = dustPlaneNormalWorld.normalized;

        Vector3 tangent = Vector3.Cross(n, Vector3.forward);
        if (tangent.sqrMagnitude < 0.001f)
            tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();

        Vector3 bitangent = Vector3.Cross(n, tangent);

        float rMin = patchRadiusMin * perHoleRadiusFactor;
        float rMax = patchRadiusMax * perHoleRadiusFactor;

        float radius = Random.Range(rMin, rMax);
        float angle = Random.Range(0f, Mathf.PI * 2f);
        Vector3 offset =
            (Mathf.Cos(angle) * tangent + Mathf.Sin(angle) * bitangent) * radius;

        Vector3 pos = dustCenterWorld + offset;
        pos.y = dustPlaneHeightWorld;

        ClampInsideDustArea(ref pos, patchSizeRange.y * 0.5f);

        Quaternion alignRot = Quaternion.FromToRotation(Vector3.forward, n);
        Quaternion spinRot = Quaternion.AngleAxis(Random.Range(0f, 360f), n);
        Quaternion rot = spinRot * alignRot;

        GameObject patch = Instantiate(surfaceDustPatchPrefab, pos, rot, sessionParent);

        float s = Random.Range(patchSizeRange.x, patchSizeRange.y);
        patch.transform.localScale = new Vector3(s, s, s);

        patchesSpawned++;
    }

    // =====================================================================
    // CONTINUOUS DUST FOUNTAIN
    // =====================================================================

    void UpdateContinuousDustFountain()
    {
        if (continuousDustFX == null) return;

        Vector3 pos = sessionPointWorld;
        Quaternion rot = Quaternion.LookRotation(sessionNormalWorld);

        continuousDustFX.transform.position = pos;
        continuousDustFX.transform.rotation = rot;

        if (!continuousDustFX.isPlaying)
            continuousDustFX.Play();
    }

    void StopContinuousDust()
    {
        if (continuousDustFX != null && continuousDustFX.isPlaying)
            continuousDustFX.Stop();
    }

    // =====================================================================
    // AUDIO HELPERS
    // =====================================================================

    void UpdateAudio(bool drillingInput, bool isWoodContact)
    {
        if (!drillingInput)
        {
            StopLoop(airDrillAudio, ref airFadeCoroutine);
            StopLoop(woodDrillAudio, ref woodFadeCoroutine);
            return;
        }

        if (isWoodContact)
        {
            StopLoop(airDrillAudio, ref airFadeCoroutine);
            StartLoop(woodDrillAudio, ref woodFadeCoroutine);
        }
        else
        {
            StopLoop(woodDrillAudio, ref woodFadeCoroutine);
            StartLoop(airDrillAudio, ref airFadeCoroutine);
        }
    }

    void StartLoop(AudioSource src, ref Coroutine fadeCoroutine)
    {
        if (src == null) return;

        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        src.loop = true;

        if (!src.isPlaying)
        {
            src.volume = 1f;
            src.Play();
        }
        else
        {
            src.volume = 1f;
        }
    }

    void StopLoop(AudioSource src, ref Coroutine fadeCoroutine)
    {
        if (src == null) return;

        if (!gameObject.activeInHierarchy)
        {
            src.Stop();
            src.volume = 1f;
            return;
        }

        if (!src.isPlaying)
        {
            src.volume = 1f;
            return;
        }

        if (fadeCoroutine != null)
            StopCoroutine(fadeCoroutine);

        fadeCoroutine = StartCoroutine(FadeOutAndStop(src, fadeOutTime));
    }

    IEnumerator FadeOutAndStop(AudioSource src, float duration)
    {
        float startVolume = src.volume;
        float t = 0f;

        while (t < duration && src != null && src.isPlaying)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            src.volume = Mathf.Lerp(startVolume, 0f, k);
            yield return null;
        }

        if (src != null)
        {
            src.Stop();
            src.volume = 1f;
        }
    }

    void UpdateLoopRegion(AudioSource src, float loopStart, float loopEnd)
    {
        if (src == null || src.clip == null || !src.isPlaying) return;
        if (loopEnd <= loopStart) return;

        float safeEnd = Mathf.Min(loopEnd, src.clip.length);

        if (src.time < loopStart)
        {
            src.time = loopStart;
            return;
        }

        if (src.time >= safeEnd)
        {
            src.time = loopStart;
        }
    }

    // =====================================================================
    // HAPTIC HELPERS
    // =====================================================================

    XRNode DetermineHandFromInteractor(IXRSelectInteractor interactor)
    {
        var mb = interactor as MonoBehaviour;
        if (mb == null) return XRNode.LeftHand;

        Transform t = mb.transform;
        while (t != null)
        {
            string name = t.name.ToLower();

            if (name.Contains("right"))
                return XRNode.RightHand;
            if (name.Contains("left"))
                return XRNode.LeftHand;

            t = t.parent;
        }

        return XRNode.LeftHand;
    }

    void SendHaptics(float amplitude, float duration)
    {
        if (grabInteractable == null) return;

        var interactor = grabInteractable.firstInteractorSelecting;
        if (interactor == null) return;

        XRNode node = DetermineHandFromInteractor(interactor);

        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid)
        {
            device.SendHapticImpulse(0, amplitude, duration);
        }
    }

    void SendDepthBasedWoodHaptics()
    {
        float depth01 = Mathf.Clamp01(accumulatedDepth / maxPhysicalDepth);
        float strength = Mathf.Lerp(woodHapticMin, woodHapticMax, depth01);
        SendHaptics(strength, hapticDuration);
    }

    // =====================================================================
    // UTILITIES
    // =====================================================================

    static float DistancePointToLine(Vector3 point, Vector3 linePoint, Vector3 lineDir)
    {
        Vector3 d = lineDir;
        if (d.sqrMagnitude < 1e-10f) return Vector3.Distance(point, linePoint);
        d.Normalize();
        Vector3 v = point - linePoint;
        Vector3 proj = Vector3.Dot(v, d) * d;
        Vector3 perp = v - proj;
        return perp.magnitude;
    }

    void PlayDust(ParticleSystem fx, Vector3 pos, Quaternion rot)
    {
        if (fx == null) return;

        fx.transform.position = pos;
        fx.transform.rotation = rot;

        fx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        fx.Play();
    }

    bool IsDrillable(Transform t)
    {
        if (drillableObjects == null || drillableObjects.Length == 0)
            return false;

        foreach (var go in drillableObjects)
        {
            if (go == null) continue;
            if (t == go.transform || t.IsChildOf(go.transform))
                return true;
        }
        return false;
    }

    Transform GetDrillableRoot(Transform t)
    {
        if (drillableObjects == null) return t;

        foreach (var go in drillableObjects)
        {
            if (go == null) continue;
            if (t == go.transform || t.IsChildOf(go.transform))
                return go.transform;
        }

        return t;
    }

    bool ClampInsideDustArea(ref Vector3 worldPos, float halfSize)
    {
        BoxCollider clampCol = dustAreaOverride != null ? dustAreaOverride : sessionCollider;
        if (clampCol == null)
            return false;

        Bounds b = clampCol.bounds;

        float minX = b.min.x + dustEdgeMargin + halfSize;
        float maxX = b.max.x - dustEdgeMargin - halfSize;
        float minZ = b.min.z + dustEdgeMargin + halfSize;
        float maxZ = b.max.z - dustEdgeMargin - halfSize;

        worldPos.x = Mathf.Clamp(worldPos.x, minX, maxX);
        worldPos.z = Mathf.Clamp(worldPos.z, minZ, maxZ);
        worldPos.y = dustPlaneHeightWorld;

        return true;
    }

    void ResetSessionState()
    {
        currentHole = null;
        currentStage = -1;
        maxStageReached = 0;

        sessionParent = null;
        sessionCollider = null;

        sessionPointWorld = Vector3.zero;
        sessionNormalWorld = Vector3.up;
        holeAxisWorld = Vector3.forward;

        dustPlaneNormalWorld = Vector3.up;
        dustPlaneHeightWorld = 0f;
        dustCenterWorld = Vector3.zero;

        accumulatedDepth = 0f;
        lastProjection = 0f;
        accumulatedTime = 0f;

        currentSurfaceDust = null;
        currentSurfaceRenderer = null;
        currentSurfaceMaterial = null;

        patchSpawnTimer = 0f;
        patchesSpawned = 0;

        perHoleIntervalFactor = 1f;
        perHoleRadiusFactor = 1f;

        allowSurfaceDustThisHole = false;

        // NEW: reset socket flag when sessions reset
        socketCreatedForThisHole = false;
    }
}
