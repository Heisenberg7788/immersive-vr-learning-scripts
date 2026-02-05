using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
[DefaultExecutionOrder(+80)]
public class OneShotWireCutter : MonoBehaviour
{
    [Header("Blade Trigger (child BoxCollider set to IsTrigger = ON)")]
    public BoxCollider bladeTrigger;

    [Header("Optional Animator (visual open/close)")]
    public Animator animator;
    public bool driveAnimator = true;
    public string heldBool = "Held";
    public string closeBool = "Close";

    [Header("Cut behaviour")]
    public bool cutOnActivate = true;
    public float findRadius = 0.035f;

    [Header("Tail rigidification")]
    public bool addMeshColliderToTail = true;
    public bool tailColliderConvex = true;
    public bool addRigidbodyToTail = true;
    public float tailMass = 0.05f;

    [Header("Debug")]
    public bool verboseLogs = true;

    // ---------- NEW: CUT SOUND ----------
    [Header("Cut Sound")]
    [Tooltip("AudioSource with the cut sound assigned. It will play exactly once per cut.")]
    public AudioSource cutSound;
    // ------------------------------------

    // ---------- NEW: CUT HAPTICS ----------
    [Header("Cut Haptics")]
    public bool enableCutHaptics = true;
    [Range(0f, 1f)] public float cutHapticAmplitude = 0.5f;
    public float cutHapticDuration = 0.08f;
    // --------------------------------------

    XRGrabInteractable grab;

    void Reset()
    {
        grab = GetComponent<XRGrabInteractable>();
        animator = GetComponent<Animator>();
        if (!bladeTrigger)
        {
            foreach (var bc in GetComponentsInChildren<BoxCollider>())
                if (bc.isTrigger) { bladeTrigger = bc; break; }
        }
    }

    void Awake()
    {
        if (!grab) grab = GetComponent<XRGrabInteractable>();
        if (!animator) animator = GetComponent<Animator>();
    }

    void OnEnable()
    {
        grab.selectEntered.AddListener(OnGrabbed);
        grab.selectExited.AddListener(OnReleased);
        grab.activated.AddListener(OnActivate);
        grab.deactivated.AddListener(OnDeactivate);
    }

    void OnDisable()
    {
        grab.selectEntered.RemoveListener(OnGrabbed);
        grab.selectExited.RemoveListener(OnReleased);
        grab.activated.RemoveListener(OnActivate);
        grab.deactivated.RemoveListener(OnDeactivate);
    }

    void OnGrabbed(SelectEnterEventArgs _)
    {
        if (driveAnimator && animator && !string.IsNullOrEmpty(heldBool))
            animator.SetBool(heldBool, true);
    }

    void OnReleased(SelectExitEventArgs _)
    {
        if (driveAnimator && animator)
        {
            if (!string.IsNullOrEmpty(heldBool)) animator.SetBool(heldBool, false);
            if (!string.IsNullOrEmpty(closeBool)) animator.SetBool(closeBool, false);
        }
    }

    void OnActivate(ActivateEventArgs _)
    {
        if (driveAnimator && animator && !string.IsNullOrEmpty(closeBool))
            animator.SetBool(closeBool, true);

        if (cutOnActivate) CutNow();
    }

    void OnDeactivate(DeactivateEventArgs _)
    {
        if (driveAnimator && animator && !string.IsNullOrEmpty(closeBool))
            animator.SetBool(closeBool, false);
    }

    // ============================================================
    // MAIN CUT FUNCTION
    // ============================================================

    public void CutNow()
    {
        if (!bladeTrigger)
        {
            if (verboseLogs) Debug.LogWarning("[Cutter] Blade trigger not assigned.");
            return;
        }

        bool cut = TryCutOnceOverlap() || TryCutOnceProximityScan();

        if (cut)
        {
            PlayCutSound();      // NEW
            if (enableCutHaptics) SendCutHaptics();  // existing haptics
        }

        if (verboseLogs)
            Debug.Log(cut ? "[Cutter] CUT succeeded." : "[Cutter] No rope close enough.");
    }

    // ============================================================
    // SOUND
    // ============================================================

    void PlayCutSound()
    {
        if (cutSound == null) return;

        // ensure it restarts cleanly
        cutSound.Stop();
        cutSound.Play();
    }

    // ============================================================
    // CUT LOGIC
    // ============================================================

    bool TryCutOnceOverlap()
    {
        var center = bladeTrigger.bounds.center;
        var halfExtents = bladeTrigger.bounds.extents;
        var rot = bladeTrigger.transform.rotation;

        var hits = Physics.OverlapBox(center, halfExtents, rot, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return false;

        HashSet<VRCoilWithRopeMesh> ropes = new HashSet<VRCoilWithRopeMesh>();
        foreach (var h in hits)
        {
            var rope = h.GetComponentInParent<VRCoilWithRopeMesh>();
            if (rope) ropes.Add(rope);
        }

        foreach (var rope in ropes)
        {
            if (TryCutThisRope(rope, center)) return true;
        }
        return false;
    }

    bool TryCutOnceProximityScan()
    {
        var center = bladeTrigger.bounds.center;
        var ropes = Object.FindObjectsOfType<VRCoilWithRopeMesh>();
        if (ropes == null || ropes.Length == 0) return false;

        foreach (var rope in ropes)
        {
            if (!rope.isActiveAndEnabled) continue;
            if (TryCutThisRope(rope, center)) return true;
        }
        return false;
    }

    bool TryCutThisRope(VRCoilWithRopeMesh rope, Vector3 bladeCenterWS)
    {
        int segIndex = rope.GetClosestSegmentIndex(bladeCenterWS, findRadius);

        if (segIndex <= 1 || segIndex >= rope.CurrentSegmentCount - 2)
            return false;

        rope.RequestSplitAt(segIndex, bladeCenterWS);

        var tail = VRCoilWithRopeMesh.LastSpawnedTail;
        if (tail) SolidifyTail(tail);

        return true;
    }

    void SolidifyTail(VRCoilWithRopeMesh tail)
    {
        tail.enabled = false;

        var grabComp = tail.GetComponent<XRGrabInteractable>();
        if (grabComp) Destroy(grabComp);

        if (addMeshColliderToTail)
        {
            var mf = tail.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                var mc = tail.GetComponent<MeshCollider>();
                if (!mc) mc = tail.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = tailColliderConvex;
            }
        }

        if (addRigidbodyToTail && !tail.GetComponent<Rigidbody>())
        {
            var rb = tail.gameObject.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.001f, tailMass);
        }
    }

    // ============================================================
    // HAPTICS
    // ============================================================

    XRNode DetermineHand()
    {
        if (grab == null) return XRNode.LeftHand;

        var interactor = grab.firstInteractorSelecting;
        if (interactor == null) return XRNode.LeftHand;

        var mb = interactor as MonoBehaviour;
        if (mb == null) return XRNode.LeftHand;

        Transform t = mb.transform;
        while (t != null)
        {
            string name = t.name.ToLower();
            if (name.Contains("right")) return XRNode.RightHand;
            if (name.Contains("left")) return XRNode.LeftHand;
            t = t.parent;
        }

        return XRNode.LeftHand;
    }

    void SendCutHaptics()
    {
        XRNode node = DetermineHand();
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid) return;

        device.SendHapticImpulse(0, cutHapticAmplitude, cutHapticDuration);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!bladeTrigger) return;
        Gizmos.matrix = Matrix4x4.TRS(
            bladeTrigger.transform.TransformPoint(bladeTrigger.center),
            bladeTrigger.transform.rotation,
            bladeTrigger.transform.lossyScale);
        Gizmos.color = new Color(1, 0, 0, 0.15f);
        Gizmos.DrawCube(Vector3.zero, bladeTrigger.size);
        Gizmos.color = new Color(1, 0, 0, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, bladeTrigger.size);
    }
#endif
}
