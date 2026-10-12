// DesktopCameraFramer.cs — frames the crystal for the non-XR (Mac / Editor) build.
//
// The scene's only camera is the XR Origin's head camera. On a headset it is driven by head
// tracking; on desktop nothing moves it, so it just sits wherever the rig was left in the scene
// (plus XR Origin's camera-height offset) looking at nothing. This component, added by
// XRSpatialUI when no XR device is active, turns off the tracking/AR components on that camera
// and places it so the whole crystal fills the view. It re-frames when a different structure is
// loaded, but leaves the view alone for slider-driven rebuilds so the user's orbit/zoom sticks.

using UnityEngine;
using UnityEngine.XR.ARFoundation;

public class DesktopCameraFramer : MonoBehaviour
{
    public CrystalCIFViewer viewer;
    [Tooltip("Extra room around the crystal (1 = touching the frame edges).")]
    public float padding = 1.25f;
    public Color background = new Color(0.07f, 0.08f, 0.10f, 1f);

    private Camera _cam;
    private string _framedStructure;
    private bool _pendingFrame;

    private void Start()
    {
        if (viewer == null) viewer = GetComponent<CrystalCIFViewer>();
        _cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
        if (_cam == null) { Debug.LogWarning("[DesktopCameraFramer] no camera in scene"); enabled = false; return; }

        // Head-tracking + passthrough components are meaningless without a device; disable
        // them so nothing fights the framing (and nothing paints a camera background).
        var tpd = _cam.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
        if (tpd != null) tpd.enabled = false;
        var bg = _cam.GetComponent<ARCameraBackground>();
        if (bg != null) bg.enabled = false;
        var mgr = _cam.GetComponent<ARCameraManager>();
        if (mgr != null) mgr.enabled = false;

        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = background;

        if (viewer != null) viewer.OnCrystalBuilt += OnBuilt;
        _pendingFrame = true;   // frame whatever is already there
    }

    private void OnDestroy()
    {
        if (viewer != null) viewer.OnCrystalBuilt -= OnBuilt;
    }

    private void OnBuilt(CrystalCIFViewer.CrystalModel _)
    {
        // Re-frame only when the structure changed (new CIF), not for bond/pressure rebuilds.
        string name = viewer != null ? viewer.CurrentStructureName : null;
        if (name != _framedStructure) _pendingFrame = true;
    }

    // LateUpdate: after XROrigin has applied its camera-height offset for the frame.
    private void LateUpdate()
    {
        if (!_pendingFrame || _cam == null || viewer == null) return;
        if (!TryGetCrystalBounds(out var b)) return;   // nothing built yet; try next frame
        _pendingFrame = false;
        _framedStructure = viewer.CurrentStructureName;
        Frame(b);
    }

    private bool TryGetCrystalBounds(out Bounds b)
    {
        b = default;
        var atoms = viewer.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) return false;
        b = new Bounds(atoms[0].transform.position, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.position);
        b.Expand(0.1f * b.size.magnitude + 0.05f);   // allow for atom radii / cell frame
        return true;
    }

    private void Frame(Bounds b)
    {
        float radius = b.extents.magnitude;
        float vFov = _cam.fieldOfView * Mathf.Deg2Rad;
        float hFov = 2f * Mathf.Atan(Mathf.Tan(vFov * 0.5f) * _cam.aspect);
        float fov  = Mathf.Min(vFov, hFov);
        float dist = radius / Mathf.Sin(fov * 0.5f) * padding;

        // Slightly above and to the front-right, like a textbook figure.
        Vector3 dir = new Vector3(0.35f, 0.3f, -1f).normalized;
        var t = _cam.transform;
        t.position = b.center + dir * dist;
        t.rotation = Quaternion.LookRotation(b.center - t.position, Vector3.up);

        _cam.nearClipPlane = Mathf.Max(0.01f, dist * 0.02f);
        _cam.farClipPlane  = Mathf.Max(_cam.farClipPlane, dist * 10f);
    }
}
