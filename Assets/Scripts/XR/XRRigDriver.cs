// XRRigDriver.cs — camera + controller tracking without any SDK-specific dependency.
//
// If a Meta OVRCameraRig exists in the scene (added via Meta Building Blocks for passthrough),
// its anchors are used as-is. Otherwise a minimal rig is created and driven every frame from
// UnityEngine.XR.InputDevices, which works under both the Oculus XR plugin and OpenXR.

using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;   // Unity XR Hands — MetaAimHand aim pose + pinch (MissionPlanning stack)

public class XRRigDriver : MonoBehaviour
{
    public Transform Head  { get; private set; }
    public Transform Left  { get; private set; }
    public Transform Right { get; private set; }

    private bool _usingExternalRig;   // true when a Meta OVRCameraRig drives the anchors
    private bool _driveLeft, _driveRight;
    private OVRHand _handLeft, _handRight;   // Meta hand tracking (null when absent)

    /// <summary>The tracking-space root (parent of the head), or null for a self-built rig.</summary>
    public Transform RigRoot { get; private set; }

    /// <summary>Find or build the rig. Call once when XR becomes active.</summary>
    public void Setup()
    {
        // Adopt the real XR camera if one exists. The Meta rig's eye camera is the camera
        // that is actually head-tracked by OVRManager — we must render through it, never a
        // camera of our own. Look by anchor name first, then any head-tracked scene camera.
        var head = FindExistingXRHead();
        if (head != null)
        {
            Head = head;
            RigRoot = head.parent != null ? head.parent : head;
            _usingExternalRig = true;

            // Controller ray origins: reuse named anchors if present, else create them under
            // the RIG ROOT (never under the crystal) and drive them from InputDevices.
            var la = GameObject.Find("LeftHandAnchor");
            var ra = GameObject.Find("RightHandAnchor");
            if (la != null) { Left = la.transform; } else { Left = MakeController("LeftController", RigRoot); _driveLeft = true; }
            if (ra != null) { Right = ra.transform; } else { Right = MakeController("RightController", RigRoot); _driveRight = true; }

            FindHands();
            Debug.Log($"[XRRigDriver] Adopted existing XR camera '{Head.name}' (rigRoot '{RigRoot.name}').");
            return;
        }

        // No XR camera anywhere — build a minimal rig at the SCENE ROOT (never parented to
        // the crystal, which would create a placement→camera feedback loop).
        var camGO = new GameObject("XRCamera");
        var cam = camGO.AddComponent<Camera>();
        camGO.tag = "MainCamera";

        var rigRoot = new GameObject("XRRig").transform;
        rigRoot.SetParent(null, false);
        rigRoot.position = Vector3.zero;
        rigRoot.rotation = Quaternion.identity;
        RigRoot = rigRoot;

        cam.transform.SetParent(rigRoot, false);
        cam.nearClipPlane = 0.05f;
        Head = cam.transform;

        Left  = MakeController("LeftController",  rigRoot);
        Right = MakeController("RightController", rigRoot);
        _driveLeft = _driveRight = true;
        _usingExternalRig = false;
        Debug.LogWarning("[XRRigDriver] No existing XR camera found — built a fallback rig at scene root.");
    }

    // The head-tracked camera already in the scene, if any (Meta rig or otherwise).
    private static Transform FindExistingXRHead()
    {
        var ce = GameObject.Find("CenterEyeAnchor");
        if (ce != null && ce.GetComponent<Camera>() != null) return ce.transform;

        // Any stereo-targeting camera whose transform is actually driven (an OVR-style rig
        // parents it under a tracking space). A bare desktop camera is NOT adopted — its
        // transform would never move, head-locking all content.
        foreach (var cam in Camera.allCameras)
            if (cam.stereoTargetEye != StereoTargetEyeMask.None && cam.targetTexture == null &&
                cam.transform.parent != null)
                return cam.transform;

        return null;
    }

    private static Transform MakeController(string name, Transform parent)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    private void Update()
    {
        if (Head == null) return;

        // Head: only when we built the rig ourselves (an adopted Meta rig drives its own head).
        if (!_usingExternalRig)
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (head.isValid)
            {
                if (head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 hp)) Head.localPosition = hp;
                if (head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion hr)) Head.localRotation = hr;
            }
        }
        // Controllers: drive any transforms WE created (even alongside an adopted rig).
        if (_driveLeft)  DrivePose(XRNode.LeftHand,  Left);
        if (_driveRight) DrivePose(XRNode.RightHand, Right);
    }

    private static void DrivePose(XRNode node, Transform target)
    {
        var dev = InputDevices.GetDeviceAtXRNode(node);
        if (!dev.isValid) return;
        if (dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)) target.localPosition = p;
        if (dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion r)) target.localRotation = r;
    }

    // ── Meta hand tracking ────────────────────────────────────────────────────

    private void FindHands()
    {
        foreach (var hand in FindObjectsByType<OVRHand>())
        {
            bool isLeft = (Left != null && hand.transform.IsChildOf(Left)) ||
                          hand.name.ToLowerInvariant().Contains("left");
            if (isLeft) _handLeft = hand; else _handRight = hand;
        }
        if (_handLeft != null || _handRight != null)
            Debug.Log("[XRRigDriver] Hand tracking components found — pinch input enabled.");
        else
            Debug.LogWarning("[XRRigDriver] No OVRHand components in scene — hand input unavailable. " +
                             "Add Meta's 'Hand Tracking' building block, and enable Hand & Body " +
                             "Tracking in the headset's Movement Tracking settings.");
    }

    private OVRHand HandFor(XRNode node) => node == XRNode.LeftHand ? _handLeft : _handRight;

    // ── Unity XR Hands (preferred path — matches the proven MissionPlanning stack) ──
    // MetaAimHand delivers the system aim pose + pinch strengths via the
    // "Meta Hand Tracking Aim" OpenXR feature. Poses arrive in tracking space and are
    // transformed into world space through the rig's tracking-space transform.

    private static MetaAimHand UnityAimFor(XRNode node) =>
        node == XRNode.LeftHand ? MetaAimHand.left : MetaAimHand.right;

    private static bool UnityAimReady(MetaAimHand a) =>
        a != null && a.added && a.isTracked.ReadValue() > 0.5f;

    private static bool UnityAimValid(MetaAimHand a) =>
        UnityAimReady(a) && ((MetaAimFlags)a.aimFlags.ReadValue() & MetaAimFlags.Valid) != 0;

    private Ray? UnityAimRay(XRNode node)
    {
        var a = UnityAimFor(node);
        if (!UnityAimValid(a)) return null;
        Vector3 p = a.devicePosition.ReadValue();
        Quaternion r = a.deviceRotation.ReadValue();
        Transform space = Head != null ? Head.parent : null;
        if (space != null)
        {
            p = space.TransformPoint(p);
            r = space.rotation * r;
        }
        return new Ray(p, r * Vector3.forward);
    }

    /// <summary>True when this side is currently driven by a tracked hand (not a controller).</summary>
    public bool IsHandTracked(XRNode node)
    {
        if (UnityAimReady(UnityAimFor(node))) return true;
        var h = HandFor(node);
        return h != null && h.IsTracked;
    }

    // The system's intent filter: a hand may only interact while Meta reports a valid pointer
    // pose (a deliberate aiming posture). A curled, resting, or swinging hand fails this —
    // which is exactly what prevents fist-pinch phantom grabs from flinging the crystal.
    private static bool HandAiming(OVRHand h) =>
        h != null && h.IsTracked && h.IsDataValid && h.IsPointerPoseValid && h.PointerPose != null;

    /// <summary>Ray-origin transform. Hands ALWAYS use the pointer pose (never the raw anchor).</summary>
    public Transform GetAimTransform(XRNode node)
    {
        var h = HandFor(node);
        if (h != null && h.IsTracked)
            return h.PointerPose != null ? h.PointerPose : null;
        return node == XRNode.LeftHand ? Left : Right;
    }

    /// <summary>The tracked hand/controller anchor for this side (always at the physical hand).</summary>
    public Transform GetAnchor(XRNode node) => node == XRNode.LeftHand ? Left : Right;

    /// <summary>
    /// The pointing ray for this side. Direction comes from the pointer pose (Meta's
    /// stabilized aim). Origin normally does too — but if the pointer pose sits implausibly
    /// far from the hand anchor (reference-frame mismatch in some hand-prefab setups), the
    /// anchor position is used instead so the ray always starts at the physical hand.
    /// </summary>
    public Ray GetAimRay(XRNode node)
    {
        // 1) Unity XR Hands aim pose (the proven MissionPlanning path).
        var unityRay = UnityAimRay(node);
        if (unityRay.HasValue) return unityRay.Value;

        // 2) OVR hand pointer pose / controller anchor fallback.
        var anchor = GetAnchor(node);
        var aim = GetAimTransform(node);
        if (aim == null)
            return anchor != null ? new Ray(anchor.position, anchor.forward) : default;
        if (anchor == null || aim == anchor)
            return new Ray(aim.position, aim.forward);

        Vector3 origin = (aim.position - anchor.position).sqrMagnitude < 0.25f  // within 0.5 m
            ? aim.position
            : anchor.position;
        return new Ray(origin, aim.forward);
    }

    // A controller must be ACTIVELY tracked to interact. A set-aside controller that lost
    // optical tracking dead-reckons on its IMU — its pose drifts away with accelerating
    // error, and a physically-squeezed grip on it would drag grabbed objects into the sky.
    private static bool ControllerTracked(XRNode node)
    {
        var d = InputDevices.GetDeviceAtXRNode(node);
        return d.isValid && d.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t;
    }

    /// <summary>May this side interact right now? (controller actively tracked, or hand aiming)</summary>
    public bool IsPointerAvailable(XRNode node)
    {
        if (UnityAimReady(UnityAimFor(node))) return UnityAimValid(UnityAimFor(node));
        var h = HandFor(node);
        if (h != null && h.IsTracked) return HandAiming(h);
        return ControllerTracked(node);
    }

    /// <summary>Select: controller trigger OR index-finger pinch while aiming.</summary>
    public bool GetSelect(XRNode node)
    {
        var ua = UnityAimFor(node);
        if (UnityAimReady(ua))
            return UnityAimValid(ua) && ua.indexPressed.ReadValue() > 0.5f;
        var h = HandFor(node);
        if (h != null && h.IsTracked)
            return HandAiming(h) && h.GetFingerIsPinching(OVRHand.HandFinger.Index);
        return ControllerTracked(node) && GetTrigger(node);
    }

    // Grab hysteresis state per hand — a relaxed/curling hand produces constant weak
    // middle-pinch signals, so grabbing needs a firm threshold to START (0.85) and a
    // lower one to HOLD (0.55), and invalid tracking never grabs.
    private bool _grabActiveL, _grabActiveR;

    /// <summary>Grab: controller grip OR firm middle-finger pinch while aiming (with hysteresis).</summary>
    public bool GetGrab(XRNode node)
    {
        var ua = UnityAimFor(node);
        if (UnityAimReady(ua))
        {
            if (!UnityAimValid(ua))
            {
                if (node == XRNode.LeftHand) _grabActiveL = false; else _grabActiveR = false;
                return false;
            }
            bool cur = node == XRNode.LeftHand ? _grabActiveL : _grabActiveR;
            float strength = ua.pinchStrengthMiddle.ReadValue();
            bool nxt = cur ? strength > 0.4f : strength > 0.7f;   // easier to start, sticky to hold
            if (node == XRNode.LeftHand) _grabActiveL = nxt; else _grabActiveR = nxt;
            return nxt;
        }

        var h = HandFor(node);
        if (h != null && h.IsTracked)
        {
            // No aiming pose → no grab, ever. This is what stops a fist or swinging arm
            // (which reads as a max-strength "pinch") from grabbing the crystal.
            if (!HandAiming(h))
            {
                if (node == XRNode.LeftHand) _grabActiveL = false; else _grabActiveR = false;
                return false;
            }
            bool current = node == XRNode.LeftHand ? _grabActiveL : _grabActiveR;
            float s = h.GetFingerPinchStrength(OVRHand.HandFinger.Middle);
            bool next = current ? s > 0.55f : s > 0.85f;
            if (node == XRNode.LeftHand) _grabActiveL = next; else _grabActiveR = next;
            return next;
        }
        return ControllerTracked(node) && GetGrip(node);
    }

    // ── Static button helpers (valid under any loader) ────────────────────────

    public static bool GetTrigger(XRNode hand)
    {
        var d = InputDevices.GetDeviceAtXRNode(hand);
        return d.isValid && d.TryGetFeatureValue(CommonUsages.triggerButton, out bool v) && v;
    }

    public static bool GetGrip(XRNode hand)
    {
        var d = InputDevices.GetDeviceAtXRNode(hand);
        return d.isValid && d.TryGetFeatureValue(CommonUsages.gripButton, out bool v) && v;
    }

    public static bool GetPrimaryButton(XRNode hand)   // A / X
    {
        var d = InputDevices.GetDeviceAtXRNode(hand);
        return d.isValid && d.TryGetFeatureValue(CommonUsages.primaryButton, out bool v) && v;
    }

    public static bool GetSecondaryButton(XRNode hand) // B / Y
    {
        var d = InputDevices.GetDeviceAtXRNode(hand);
        return d.isValid && d.TryGetFeatureValue(CommonUsages.secondaryButton, out bool v) && v;
    }
}
