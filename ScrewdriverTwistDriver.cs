using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class ScrewdriverTwistDriver : MonoBehaviour
{
    [Header("XR")]
    public XRGrabInteractable grab;

    [Header("Tip")]
    public Transform tip;
    public float engageRadius = 0.05f;

    [Header("Twist Tuning")]
    [Tooltip("Multiplier for detected twist degrees.")]
    public float twistSensitivity = 6f;

    [Tooltip("Ignore tiny micro-jitter (degrees per frame).")]
    public float deadzoneDegrees = 0.15f;

    [Header("Haptics")]
    public bool enableHaptics = true;

    [Tooltip("How often we can pulse haptics (seconds).")]
    public float hapticCooldown = 0.04f;

    [Range(0f, 1f)] public float hapticBaseAmplitude = 0.08f;
    [Range(0f, 1f)] public float hapticAmplitudePerDeg = 0.01f;
    [Range(0f, 1f)] public float hapticMaxAmplitude = 0.25f;
    public float hapticDuration = 0.02f;

    ScrewSocket engaged;
    Quaternion lastRot;
    bool hasLastRot;
    float nextHapticTime;

    void Awake()
    {
        if (grab == null) grab = GetComponent<XRGrabInteractable>();
    }

    void Update()
    {
        if (grab == null || !grab.isSelected || tip == null)
        {
            engaged = null;
            hasLastRot = false;
            return;
        }

        var s = FindClosestSocketWithScrew();
        if (s == null || s.seatedScrew == null)
        {
            engaged = null;
            hasLastRot = false;
            return;
        }

        if (s != engaged)
        {
            engaged = s;
            lastRot = transform.rotation;
            hasLastRot = true;
            return;
        }

        if (!hasLastRot)
        {
            lastRot = transform.rotation;
            hasLastRot = true;
            return;
        }

        Quaternion current = transform.rotation;
        Quaternion deltaQ = current * Quaternion.Inverse(lastRot);

        deltaQ.ToAngleAxis(out float angleDeg, out Vector3 axis);
        if (axis.sqrMagnitude < 1e-8f)
        {
            lastRot = current;
            return;
        }

        // convert 0..360 to -180..180
        if (angleDeg > 180f) angleDeg -= 360f;

        Vector3 socketAxis = engaged.AxisWorld.normalized;
        Vector3 deltaAxis = axis.normalized;

        // Component of rotation around socket axis
        float component = Vector3.Dot(deltaAxis, socketAxis);
        float twistDeg = angleDeg * component * twistSensitivity;

        float absTwist = Mathf.Abs(twistDeg);
        if (absTwist >= deadzoneDegrees)
        {
            engaged.ApplyTwistDegrees(twistDeg);
            TryHaptics(absTwist);
        }

        lastRot = current;
    }

    void TryHaptics(float absTwistDeg)
    {
        if (!enableHaptics) return;
        if (Time.time < nextHapticTime) return;

        var controllerInteractor = GetSelectingControllerInteractor();
        if (controllerInteractor == null) return;

        float amp = hapticBaseAmplitude + (absTwistDeg * hapticAmplitudePerDeg);
        amp = Mathf.Clamp(amp, 0f, hapticMaxAmplitude);

        controllerInteractor.SendHapticImpulse(amp, hapticDuration);
        nextHapticTime = Time.time + hapticCooldown;
    }

    XRBaseInputInteractor GetSelectingControllerInteractor()
    {
        if (grab == null) return null;

        var list = grab.interactorsSelecting;
        if (list == null || list.Count == 0) return null;

        var asController = list[0] as XRBaseInputInteractor;
        if (asController != null) return asController;

        var mb = list[0] as MonoBehaviour;
        if (mb != null)
            return mb.GetComponentInParent<XRBaseInputInteractor>();

        return null;
    }

    ScrewSocket FindClosestSocketWithScrew()
    {
        var sockets = FindObjectsByType<ScrewSocket>(FindObjectsSortMode.None);
        ScrewSocket best = null;
        float bestD = float.PositiveInfinity;

        foreach (var s in sockets)
        {
            if (s == null) continue;
            if (s.seatedScrew == null) continue;

            float d = Vector3.Distance(tip.position, s.EntryWorld);
            if (d > engageRadius) continue;

            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }

        return best;
    }
}
