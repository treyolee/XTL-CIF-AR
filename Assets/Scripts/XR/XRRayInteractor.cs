// XRRayInteractor.cs — one per hand. Casts a ray from the controller:
//   trigger → press/drag/release on IWorldInteractable widgets, or atom selection
//   grip    → grab a WorldGrabbable (control tablet) with a controller; hands drag it via
//             WorldDragHandle (index pinch). Crystal grab/scale lives in CrystalDirectGrab.
// Renders a fading ray + dot reticle in the accent colour.

using UnityEngine;
using UnityEngine.XR;

public class XRRayInteractor : MonoBehaviour
{
    public XRNode hand = XRNode.RightHand;
    public float maxDistance = 6f;
    public CrystalInteraction interaction;   // SelectAtom target
    public XRRigDriver rig;                  // unified controller/hand input + aim pose
    // Crystal grab/scale is handled separately by CrystalDirectGrab (fist gestures).
    // This interactor only grips WorldGrabbable UI panels (the tablet).

    private LineRenderer _line;
    private Transform _reticle;
    private IWorldInteractable _hovered;
    private IWorldInteractable _pressed;
    private bool _prevTrigger, _prevGrip;
    private WorldGrabbable _grabbedUI;
    private Vector3 _uiGrabPosOffset;
    private Quaternion _uiGrabRotOffset;
    private float _lastPointerOkTime = -10f;   // grace window so the ray doesn't flicker

    private void Start()
    {
        _line = gameObject.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.positionCount = 2;
        _line.startWidth = 0.0035f;
        _line.endWidth   = 0.0008f;
        _line.material = new Material(Shader.Find("Sprites/Default"));
        var c = WorldUIStyle.Accent;
        _line.startColor = new Color(c.r, c.g, c.b, 0.85f);
        _line.endColor   = new Color(c.r, c.g, c.b, 0.05f);
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var ret = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ret.name = "Reticle";
        Destroy(ret.GetComponent<Collider>());
        ret.transform.localScale = Vector3.one * 0.008f;
        var rend = ret.GetComponent<Renderer>();
        rend.material = new Material(Shader.Find("Sprites/Default")) { color = WorldUIStyle.Accent };
        _reticle = ret.transform;
        _reticle.gameObject.SetActive(false);
    }

    private void Update()
    {
        // Interactions only while the pointer is genuinely available (controller valid, or
        // hand in an aiming pose). The ray VISUAL gets a short grace window so brief
        // tracking dropouts don't make the laser flicker.
        bool pointerOk = rig == null || rig.IsPointerAvailable(hand);
        if (pointerOk) _lastPointerOkTime = Time.time;
        bool showRay = pointerOk || (Time.time - _lastPointerOkTime) < 0.25f;

        if (_line != null) _line.enabled = showRay;
        if (!showRay && _reticle != null) _reticle.gameObject.SetActive(false);

        // Sanity-checked aim ray: pointer-pose direction, but the origin snaps to the hand
        // anchor if the pointer pose reports from a mismatched reference frame.
        var ray = rig != null ? rig.GetAimRay(hand) : new Ray(transform.position, transform.forward);

        if (!pointerOk)
        {
            // Keep any in-progress press/UI-grab from acting on stale poses.
            if (_pressed != null) { try { _pressed.OnRelease(); } catch { } _pressed = null; }
            _grabbedUI = null;
            if (showRay) DrawRay(ray, maxDistance * 0.35f, hit: false);
            _prevTrigger = false; _prevGrip = false;
            return;
        }

        bool trigger = rig != null ? rig.GetSelect(hand) : XRRigDriver.GetTrigger(hand);
        bool grip    = rig != null ? rig.GetGrab(hand)   : XRRigDriver.GetGrip(hand);

        // ── Grip: UI panel grab or crystal grab ──────────────────────────────
        if (grip && !_prevGrip)
            TryBeginGrab(ray);
        if (!grip && _prevGrip)
            EndGrab();
        if (_grabbedUI != null)
        {
            var t = _grabbedUI.target;
            t.position = transform.TransformPoint(_uiGrabPosOffset);
            t.rotation = transform.rotation * _uiGrabRotOffset;
        }

        // ── Trigger drag in progress ─────────────────────────────────────────
        if (_pressed != null)
        {
            // Wrap so a throwing widget callback (e.g. a click handler that rebuilds the
            // scene and hits an error) can NEVER leave _pressed stuck — which would brick
            // this hand permanently. _pressed is always cleared on release, come what may.
            try { _pressed.OnDrag(ray); } catch (System.Exception e) { Debug.LogException(e); }
            DrawRay(ray, maxDistance * 0.5f, hit: false);
            if (!trigger)
            {
                try { _pressed.OnRelease(); } catch (System.Exception e) { Debug.LogException(e); }
                finally { _pressed = null; }
            }
            _prevTrigger = trigger; _prevGrip = grip;
            return;
        }

        // ── Hover pick (allocation-free: registry lookup, then direct GetComponent) ──
        IWorldInteractable hitWidget = null;
        AtomInfo hitAtom = null;
        float hitDist = maxDistance;
        if (Physics.Raycast(ray, out var hit, maxDistance, Physics.DefaultRaycastLayers,
                            QueryTriggerInteraction.Collide))
        {
            hitDist = hit.distance;
            if (!WorldUIKit.InteractableRegistry.TryGetValue(hit.collider, out hitWidget))
                hitAtom = hit.collider.GetComponent<AtomInfo>();
        }

        // hover transitions
        if (!ReferenceEquals(hitWidget, _hovered))
        {
            _hovered?.OnHoverExit();
            _hovered = hitWidget;
            _hovered?.OnHoverEnter();
        }

        // trigger press
        if (trigger && !_prevTrigger)
        {
            if (_hovered != null)
            {
                _pressed = _hovered;
                try { _pressed.OnPress(hit.point); } catch (System.Exception e) { Debug.LogException(e); }
            }
            else if (hitAtom != null && interaction != null)
            {
                interaction.SelectAtom(hitAtom);
            }
        }

        DrawRay(ray, hitDist, hitWidget != null || hitAtom != null);
        _prevTrigger = trigger; _prevGrip = grip;
    }

    private void TryBeginGrab(Ray ray)
    {
        // Only WorldGrabbable UI panels (the tablet header) are grip-grabbable here.
        if (Physics.Raycast(ray, out var hit, maxDistance, Physics.DefaultRaycastLayers,
                            QueryTriggerInteraction.Collide))
        {
            var grabbable = hit.collider.GetComponentInParent<WorldGrabbable>();
            if (grabbable != null)
            {
                _grabbedUI = grabbable;
                var t = grabbable.target;
                _uiGrabPosOffset = transform.InverseTransformPoint(t.position);
                _uiGrabRotOffset = Quaternion.Inverse(transform.rotation) * t.rotation;
            }
        }
    }

    private void EndGrab()
    {
        _grabbedUI = null;
    }

    private void DrawRay(Ray ray, float dist, bool hit)
    {
        if (_line == null) return;
        _line.SetPosition(0, ray.origin);
        _line.SetPosition(1, ray.origin + ray.direction * dist);
        if (_reticle != null)
        {
            _reticle.gameObject.SetActive(hit);
            if (hit) _reticle.position = ray.origin + ray.direction * dist;
        }
    }
}
