// CrystalDirectGrab.cs — near-field ("direct") crystal manipulation with hand tracking.
//
//   • Make a FIST with your hand inside the unit cell → grab & carry the crystal (it follows
//     your hand's position and rotation). Open the hand to release.
//   • Make a FIST with BOTH hands inside the cell → pinch-scale: moving your fists apart/together
//     scales the crystal; the midpoint carries it.
//
// Fist detection and palm position come from the XR Hands joint data (the "Hand Tracking
// Subsystem" OpenXR feature), which is far more reliable than aim-pinch strengths. Joint poses
// are reported in tracking space and converted to world through `trackingSpace`.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

public class CrystalDirectGrab : MonoBehaviour
{
    public Transform crystal;
    public Transform trackingSpace;               // XR Origin tracking space (Head.parent)

    [Header("Scale bounds")]
    public float minScale = 0.2f;
    public float maxScale = 4f;

    [Header("Gesture tuning")]
    [Tooltip("Margin (m) added around the cell so a fist near it still counts as inside.")]
    public float insideMargin = 0.06f;
    [Tooltip("Mean fingertip→palm distance (m) to START a fist.")]
    public float fistGrab = 0.055f;
    [Tooltip("Mean fingertip→palm distance (m) to RELEASE (hysteresis).")]
    public float fistRelease = 0.085f;

    private XRHandSubsystem _subsys;
    private static readonly List<XRHandSubsystem> _subsysBuf = new();

    private bool _fistL, _fistR;   // hysteretic fist state
    private bool _grabL, _grabR;   // grabbing (fist that began inside the cell)

    // one-hand carry
    private bool _oneActive;
    private Vector3 _grabOffset;
    private Quaternion _grabCrystalRot0, _grabPalmRot0;

    // two-hand scale
    private bool _twoActive;
    private float _baseDist;
    private Vector3 _baseScale, _baseMid, _baseCrystalPos;

    private static readonly XRHandJointID[] Tips =
    { XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip };

    public bool IsGrabbed => _oneActive || _twoActive;
    public bool IsGrabbingLeft  => _grabL;
    public bool IsGrabbingRight => _grabR;
    public bool IsFistLeft  => _fistL;
    public bool IsFistRight => _fistR;

    public void ForceRelease() { _grabL = _grabR = _oneActive = _twoActive = false; }

    private void Update()
    {
        if (crystal == null) return;
        EnsureSubsystem();
        if (_subsys == null || !_subsys.running) return;

        bool okL = HandState(_subsys.leftHand,  out Vector3 palmL, out Quaternion rotL, ref _fistL);
        bool okR = HandState(_subsys.rightHand, out Vector3 palmR, out Quaternion rotR, ref _fistR);

        Bounds cell = CrystalBounds();
        cell.Expand(insideMargin * 2f);

        UpdateGrab(ref _grabL, okL, _fistL, palmL, cell);
        UpdateGrab(ref _grabR, okR, _fistR, palmR, cell);

        int n = (_grabL ? 1 : 0) + (_grabR ? 1 : 0);

        if (n >= 2)
        {
            _oneActive = false;
            if (!_twoActive) BeginTwo(palmL, palmR);
            UpdateTwo(palmL, palmR);
        }
        else if (n == 1)
        {
            _twoActive = false;
            Vector3 p = _grabL ? palmL : palmR;
            Quaternion r = _grabL ? rotL : rotR;
            if (!_oneActive) BeginOne(p, r);
            UpdateOne(p, r);
        }
        else
        {
            _oneActive = _twoActive = false;
        }
    }

    // A grab STARTS only when a fist forms with the palm inside the cell; it CONTINUES while the
    // fist is held (even as the hand moves out), and releases when the hand opens.
    private void UpdateGrab(ref bool grab, bool ok, bool fist, Vector3 palm, Bounds cell)
    {
        if (!ok) { grab = false; return; }
        if (grab) { if (!fist) grab = false; }
        else if (fist && cell.Contains(palm)) grab = true;
    }

    // ── one-hand carry (rigid follow) ──────────────────────────────────────────

    private void BeginOne(Vector3 palm, Quaternion palmRot)
    {
        _oneActive = true;
        _grabOffset      = Quaternion.Inverse(palmRot) * (crystal.position - palm);
        _grabCrystalRot0 = crystal.rotation;
        _grabPalmRot0    = palmRot;
    }

    private void UpdateOne(Vector3 palm, Quaternion palmRot)
    {
        Quaternion delta = palmRot * Quaternion.Inverse(_grabPalmRot0);
        crystal.rotation = delta * _grabCrystalRot0;
        crystal.position = palm + palmRot * _grabOffset;
    }

    // ── two-hand scale (+ carry by midpoint) ───────────────────────────────────

    private void BeginTwo(Vector3 pL, Vector3 pR)
    {
        _twoActive       = true;
        _baseDist        = Mathf.Max(0.02f, Vector3.Distance(pL, pR));
        _baseScale       = crystal.localScale;
        _baseMid         = (pL + pR) * 0.5f;
        _baseCrystalPos  = crystal.position;
    }

    private void UpdateTwo(Vector3 pL, Vector3 pR)
    {
        float ratio = Vector3.Distance(pL, pR) / _baseDist;
        float s = Mathf.Clamp(_baseScale.x * ratio, minScale, maxScale);
        crystal.localScale = Vector3.one * s;
        crystal.position = _baseCrystalPos + ((pL + pR) * 0.5f - _baseMid);
    }

    // ── hand reading ───────────────────────────────────────────────────────────

    private bool HandState(XRHand hand, out Vector3 palmWorld, out Quaternion palmRotWorld, ref bool fist)
    {
        palmWorld = Vector3.zero; palmRotWorld = Quaternion.identity;
        if (!hand.isTracked) { fist = false; return false; }
        if (!TryJoint(hand, XRHandJointID.Palm, out palmWorld, out palmRotWorld)) { fist = false; return false; }

        float sum = 0f; int cnt = 0;
        foreach (var id in Tips)
            if (TryJoint(hand, id, out Vector3 tip, out _)) { sum += Vector3.Distance(tip, palmWorld); cnt++; }
        if (cnt == 0) { fist = false; return false; }

        float avg = sum / cnt;
        fist = fist ? avg < fistRelease : avg < fistGrab;   // hysteresis
        return true;
    }

    private bool TryJoint(XRHand hand, XRHandJointID id, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = Vector3.zero; worldRot = Quaternion.identity;
        var joint = hand.GetJoint(id);
        if (!joint.TryGetPose(out Pose p)) return false;
        if (trackingSpace != null)
        {
            worldPos = trackingSpace.TransformPoint(p.position);
            worldRot = trackingSpace.rotation * p.rotation;
        }
        else { worldPos = p.position; worldRot = p.rotation; }
        return true;
    }

    private Bounds CrystalBounds()
    {
        var atoms = crystal.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) return new Bounds(crystal.position, Vector3.one * 0.1f);
        var b = new Bounds(atoms[0].transform.position, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.position);
        return b;
    }

    private void EnsureSubsystem()
    {
        if (_subsys != null && _subsys.running) return;
        SubsystemManager.GetSubsystems(_subsysBuf);
        _subsys = _subsysBuf.Count > 0 ? _subsysBuf[0] : null;
    }
}
