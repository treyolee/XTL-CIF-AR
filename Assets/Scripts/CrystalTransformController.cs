using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Add to the same GameObject as CrystalCIFViewer.
///
/// Drag                        : orbit the crystal around its visual centre.
/// Alt + drag                  : pan.
/// Two-finger swipe up/down    : zoom (scroll Y).
/// Two-finger swipe left/right : pan  (scroll X).
/// F / Home                    : reset transform.
/// </summary>
[DefaultExecutionOrder(-10)] // runs before CrystalInteraction so OrbitEndedThisFrame is ready
[RequireComponent(typeof(CrystalCIFViewer))]
public class CrystalTransformController : MonoBehaviour
{
    [Header("Orbit  (drag)")]
    public float orbitSpeed = 0.35f;

    [Header("Pan  (Alt+drag  or  horizontal scroll/swipe)")]
    public float panSpeed       = 0.01f;
    public float scrollPanSpeed = 0.002f;

    [Header("Zoom  (vertical scroll / two-finger swipe)")]
    public float scaleSpeed = 0.1f;   // fraction per 1 scroll-unit; clamped to ±30 % per frame
    public float minScale   = 0.05f;
    public float maxScale   = 20f;

    [Header("Debug")]
    [Tooltip("Log mouse/scroll values to Console every frame so you can confirm input is arriving.")]
    public bool debugInput = false;

    // ── read by CrystalInteraction to suppress selection after a drag ──────────
    public bool IsOrbiting          { get; private set; }
    public bool OrbitEndedThisFrame { get; private set; }

    private Vector2 _prevPos;
    private Vector2 _leftDownPos;
    private bool    _leftHeld;
    private Vector3 _pivot;

    private const float DragThresholdPx = 5f;

    // ─── lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        GetComponent<CrystalCIFViewer>().OnCrystalBuilt += _ => RecalculatePivot();
    }

    private void Start()
    {
        RecalculatePivot();
        // Seed _prevPos so the first-frame delta is zero instead of mouse-to-origin.
        var mouse = Mouse.current;
        if (mouse != null) _prevPos = mouse.position.ReadValue();
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    private void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null)
        {
            if (debugInput) Debug.LogWarning("[CrystalTransform] Mouse.current is null.");
            return;
        }

        OrbitEndedThisFrame = false;

        var pos    = mouse.position.ReadValue();
        var delta  = pos - _prevPos;
        var scroll = mouse.scroll.ReadValue();

        if (debugInput && (scroll.sqrMagnitude > 0.001f || delta.sqrMagnitude > 0.001f))
            Debug.Log($"[CrystalTransform] pos={pos} delta={delta} scroll={scroll}  leftHeld={_leftHeld}  isOrbiting={IsOrbiting}");

        HandleOrbitAndPan(mouse, pos, delta);
        HandleScroll(scroll);
        HandleReset();

        _prevPos = pos;
    }

    // ── Orbit (drag) and Pan (Alt+drag) ───────────────────────────────────────

    private void HandleOrbitAndPan(Mouse mouse, Vector2 pos, Vector2 delta)
    {
        var  kb      = Keyboard.current;
        bool altHeld = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);

        if (mouse.leftButton.wasPressedThisFrame)
        {
            // A press that starts on UI (slider, button, toggle) belongs to the UI —
            // ignore the whole drag so it can't also orbit/pan the crystal.
            _leftHeld    = !PointerOverUI();
            _leftDownPos = pos;
            IsOrbiting   = false;
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            OrbitEndedThisFrame = IsOrbiting;
            _leftHeld  = false;
            IsOrbiting = false;
        }

        if (!_leftHeld || !mouse.leftButton.isPressed || delta.sqrMagnitude <= 0f) return;

        if (altHeld)
        {
            var cam = PickCamera();
            if (cam == null) return;
            var move = cam.transform.right * (-delta.x * panSpeed)
                     + cam.transform.up    * (-delta.y * panSpeed);
            transform.position += move;
            _pivot             += move;
        }
        else
        {
            if (!IsOrbiting && Vector2.Distance(pos, _leftDownPos) > DragThresholdPx)
                IsOrbiting = true;

            if (IsOrbiting)
            {
                var cam = PickCamera();
                transform.RotateAround(_pivot, Vector3.up, delta.x * orbitSpeed);
                // Fall back to world-right if camera unavailable.
                var camRight = cam != null ? cam.transform.right : Vector3.right;
                transform.RotateAround(_pivot, camRight, -delta.y * orbitSpeed);
            }
        }
    }

    // ── Scroll: vertical = zoom, horizontal = pan ─────────────────────────────

    private void HandleScroll(Vector2 scroll)
    {
        // Scrolling over a UI panel shouldn't zoom/pan the crystal behind it.
        if (scroll.sqrMagnitude > 0.001f && PointerOverUI()) return;

        // Horizontal → pan (camera needed; skip if unavailable)
        if (Mathf.Abs(scroll.x) > 0.001f)
        {
            var cam = PickCamera();
            if (cam != null)
            {
                var move = cam.transform.right * (-scroll.x * scrollPanSpeed);
                transform.position += move;
                _pivot             += move;
            }
        }

        // Vertical → zoom (no camera needed)
        if (Mathf.Abs(scroll.y) > 0.001f)
        {
            // Clamp the per-frame multiplier so fast trackpad swipes don't jump wildly.
            float factor   = 1f + Mathf.Clamp(scroll.y * scaleSpeed, -0.3f, 0.3f);
            float newScale = Mathf.Clamp(transform.localScale.x * factor, minScale, maxScale);

            Vector3 pivotLocal    = transform.InverseTransformPoint(_pivot);
            transform.localScale  = Vector3.one * newScale;
            Vector3 pivotWorldNew = transform.TransformPoint(pivotLocal);
            transform.position   += _pivot - pivotWorldNew;
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    private void HandleReset()
    {
        var kb = Keyboard.current;
        if (kb == null || CrystalInteraction.IsTypingInUI()) return;   // 'f' in a search term ≠ reset
        if (kb.fKey.wasPressedThisFrame || kb.homeKey.wasPressedThisFrame)
            ResetTransform();
    }

    /// <summary>Restore position, rotation and scale to their startup defaults.</summary>
    public void ResetTransform()
    {
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale    = Vector3.one;
        RecalculatePivot();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RecalculatePivot()
    {
        var atoms = transform.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) { _pivot = transform.position; return; }
        var b = new Bounds(atoms[0].transform.position, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.position);
        _pivot = b.center;
    }

    private Camera PickCamera()
    {
        var ci = GetComponent<CrystalInteraction>();
        if (ci != null && ci.pickCamera != null) return ci.pickCamera;
        return Camera.main;
    }

    private static bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
