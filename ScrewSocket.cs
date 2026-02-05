using UnityEngine;

[RequireComponent(typeof(Collider))]
public class ScrewSocket : MonoBehaviour
{
    [Header("Snap")]
    public float snapRadius = 0.05f;
    public float snapInsetMeters = 0.0015f;

    // REQUIRED by SuperDrilling (used if you also check alignment there)
    [Range(0f, 1f)]
    public float snapAlignmentDot = 0.25f;

    public float seatCooldown = 0.15f;

    [Header("Runtime")]
    public Screw seatedScrew;

    float lastSeatTime = -999f;

    // Screwing state
    float travelMeters = 0f;

    // Direction lock / ratchet
    [Header("Direction Lock (Anti Re-grip Unscrew)")]
    [Tooltip("How many degrees in the opposite direction before we allow direction flip (intentional unscrew).")]
    public float reverseFlipThresholdDeg = 35f;

    [Tooltip("If true, direction is locked after first meaningful twist. Small reverse is ignored until threshold is exceeded.")]
    public bool lockDirection = true;

    int lockedTwistSign = 0;              // +1 or -1 (sign of world twist degrees)
    float reverseAccumDeg = 0f;           // how much opposite twist we accumulated

    // IMPORTANT:
    // Socket transform.forward must be the hole axis (INTO the wood)
    public Vector3 AxisWorld => transform.forward.normalized;
    public Vector3 EntryWorld => transform.position;

    void Awake()
    {
        var c = GetComponent<Collider>();
        c.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (seatedScrew != null) return;

        var screw = other.GetComponentInParent<Screw>();
        if (screw == null) return;

        if (CanSeat(screw))
            SeatExternal(screw);
    }

    public bool CanSeat(Screw screw)
    {
        if (screw == null) return false;
        if (seatedScrew != null) return false;
        if ((Time.time - lastSeatTime) < seatCooldown) return false;
        if (screw.Tip == null) return false;

        if (Vector3.Distance(screw.Tip.position, EntryWorld) > snapRadius)
            return false;

        // Align check: screw shaft axis should match hole axis
        float dot = Mathf.Abs(Vector3.Dot(screw.ShaftAxisWorld, AxisWorld));
        if (dot < snapAlignmentDot) return false;

        return true;
    }

    public void SeatExternal(Screw screw)
    {
        if (!CanSeat(screw)) return;

        lastSeatTime = Time.time;
        seatedScrew = screw;

        seatedScrew.SetKinematic(true);
        seatedScrew.DisableGrab(true);

        travelMeters = 0f;

        // reset direction lock
        lockedTwistSign = 0;
        reverseAccumDeg = 0f;

        // Align screw model axis → hole axis
        Quaternion align = Quaternion.FromToRotation(seatedScrew.ShaftAxisWorld, AxisWorld);
        seatedScrew.transform.rotation = align * seatedScrew.transform.rotation;

        // Position: put tip exactly on hole entry (+ inset)
        Vector3 desiredTipPos = EntryWorld + AxisWorld * snapInsetMeters;
        seatedScrew.transform.position += (desiredTipPos - seatedScrew.Tip.position);
    }

    public void ApplyTwistDegrees(float deltaDegrees)
    {
        if (seatedScrew == null) return;

        // -------------------------
        // Direction lock / ratchet
        // -------------------------
        int sign = deltaDegrees > 0f ? +1 : (deltaDegrees < 0f ? -1 : 0);

        if (lockDirection)
        {
            // Lock direction on first real twist
            if (lockedTwistSign == 0 && sign != 0)
            {
                lockedTwistSign = sign;
                reverseAccumDeg = 0f;
            }

            // If twisting opposite to locked direction, accumulate but DO NOT apply until threshold crossed
            if (lockedTwistSign != 0 && sign != 0 && sign != lockedTwistSign)
            {
                reverseAccumDeg += Mathf.Abs(deltaDegrees);

                if (reverseAccumDeg < reverseFlipThresholdDeg)
                {
                    // Ignore small opposite twist (re-grip noise)
                    return;
                }

                // User intentionally reversed enough → flip direction lock
                lockedTwistSign = sign;
                reverseAccumDeg = 0f;
            }
            else
            {
                // Same direction as locked (or zero): reset reverse accumulator
                reverseAccumDeg = 0f;
            }
        }

        // ---------------------------------
        // Real screw behavior (turn -> travel)
        // ---------------------------------
        float signed = deltaDegrees * seatedScrew.tightenDirection;  // tightenDirection handles CW vs CCW meaning
        float deltaTurns = signed / 360f;
        float deltaTravel = deltaTurns * seatedScrew.threadPitchMetersPerTurn;

        travelMeters = Mathf.Clamp(
            travelMeters + deltaTravel,
            0f,
            seatedScrew.maxTravelMeters
        );

        // Rotate around hole axis ONLY (world axis)
        seatedScrew.transform.Rotate(AxisWorld, deltaDegrees, Space.World);

        // Move along hole axis ONLY (so it goes straight, no wobble)
        Vector3 desiredTipPos = EntryWorld + AxisWorld * (snapInsetMeters + travelMeters);
        seatedScrew.transform.position += (desiredTipPos - seatedScrew.Tip.position);
    }
}
