using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[DefaultExecutionOrder(+75)]
public class WireReelFeederRealistic : MonoBehaviour
{
    public enum AxleAxis { X, Y, Z }

    [Header("References")]
    public VRCoilWithRopeMesh wire;
    public Transform tip;
    public Transform exitAnchor;
    public Transform reelVisual;

    [Header("Axle")]
    public AxleAxis axle = AxleAxis.Z;
    public bool clockwiseWhenPayout = true;

    [Header("Mechanics")]
    public float effectiveRadius = 0.10f;
    public float inertia = 2.5f;
    [Range(0f, 2f)] public float friction = 0.2f;

    [Header("Retraction (visual length control)")]
    public float slack = 0.05f;
    public float retractSpeed = 2.0f;

    // ---------- NEW: HAPTICS SETTINGS ----------
    [Header("Haptics (optional)")]
    [Tooltip("Grab interactable the user holds while winding/unwinding (reel handle / tool).")]
    public UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable hapticsGrabInteractable;

    [Range(0f, 1f)]
    [Tooltip("Very light vibration when reel is just moving.")]
    public float baseHapticStrength = 0.04f;

    [Range(0f, 1f)]
    [Tooltip("Maximum vibration when reel spins fast.")]
    public float maxHapticStrength = 0.10f;

    [Tooltip("Seconds for each vibration pulse.")]
    public float hapticDuration = 0.02f;

    [Tooltip("Minimum reel angular speed (rad/s) before vibration starts.")]
    public float minAngularSpeedForHaptics = 0.2f;
    // -------------------------------------------

    float prevVisibleLen;
    float omega; // rad/s

    void Start()
    {
        if (!reelVisual) reelVisual = transform;
        if (!wire) wire = FindFirstObjectByType<VRCoilWithRopeMesh>();
        if (!tip && wire) tip = wire.tip;
        if (!exitAnchor) exitAnchor = transform;
        prevVisibleLen = wire ? wire.VisibleLengthMeters : 0f;
    }

    void FixedUpdate()
    {
        if (!wire || !exitAnchor || !reelVisual) return;

        float dt = Time.fixedDeltaTime;

        Vector3 tipPos = (tip != null) ? tip.position : wire.GetLastParticleWS();
        float straightDist = Vector3.Distance(exitAnchor.position, tipPos);
        float targetLen = Mathf.Max(slack, straightDist + slack);

        float visible = wire.VisibleLengthMeters;

        if (visible > targetLen)
        {
            float newLen = Mathf.Max(targetLen, visible - retractSpeed * dt);
            wire.SetVisibleLengthMeters(newLen);
            visible = newLen;
        }
        else
        {
            wire.SetVisibleLengthMeters(targetLen);
            visible = targetLen;
        }

        float linV = Mathf.Clamp((visible - prevVisibleLen) / dt, -10f, 10f);
        float targetOmega = (effectiveRadius > 1e-5f) ? (linV / effectiveRadius) : 0f;
        float dir = clockwiseWhenPayout ? -1f : +1f;

        float accel = (targetOmega - omega) / Mathf.Max(0.0001f, inertia);
        omega += accel * dt;
        omega *= Mathf.Clamp01(1f - friction * dt);

        // ---------- NEW: tiny friction haptics while reel spins ----------
        SendReelHaptics(Mathf.Abs(omega));
        // ------------------------------------------------------------------

        float delta = omega * dt * dir;
        RotateReel(delta);

        prevVisibleLen = visible;
    }

    void RotateReel(float deltaRadiansSigned)
    {
        Vector3 axisLocal = axle switch
        {
            AxleAxis.X => Vector3.right,
            AxleAxis.Y => Vector3.up,
            _ => Vector3.forward
        };
        Vector3 axisWorld = reelVisual.TransformDirection(axisLocal);
        reelVisual.Rotate(axisWorld, deltaRadiansSigned * Mathf.Rad2Deg, Space.World);
    }

    // ================== NEW HAPTIC HELPERS ==================

    // Walk up the interactor’s parents (Left Hand, Right Hand, Left Controller, etc.)
    // and decide which XRNode to use.
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

        // Fallback if we didn't find anything
        return XRNode.LeftHand;
    }

    void SendReelHaptics(float angularSpeedAbs)
    {
        if (hapticsGrabInteractable == null) return;

        // Which hand is actually holding the reel / handle?
        var interactor = hapticsGrabInteractable.firstInteractorSelecting;
        if (interactor == null) return; // nobody grabbing -> no vibration

        // Only vibrate when reel really moving
        if (angularSpeedAbs < minAngularSpeedForHaptics)
            return;

        // Map reel speed to strength
        float t = Mathf.Clamp01(angularSpeedAbs / 8f);   // 8 rad/s ≈ full effect, tweak if needed
        float amp = Mathf.Lerp(baseHapticStrength, maxHapticStrength, t);

        XRNode node = DetermineHandFromInteractor(interactor);
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid) return;

        // channel 0 = default vibration channel
        device.SendHapticImpulse(0, amp, hapticDuration);
    }

    // ========================================================
}
