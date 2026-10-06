// XRSpatialUI.cs — add to the same GameObject as CrystalCIFViewer.
//
// Watches for an active XR display. On desktop it does nothing, so Mac/Windows builds are
// unaffected. When a headset is present it:
//   • disables the desktop mouse controls and hides the screen-space HUD
//   • sets up the camera rig (or adopts a Meta OVRCameraRig if one exists)
//   • spawns ray interactors, the crystal grabber, the placard, and the control tablet
//   • places the crystal at a comfortable height in front of the user
// Controls: trigger = point/click · grip = grab crystal or tablet ·
//           A/X = show/hide tablet · B/Y = recentre in front of the user.

using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;   // AR Session / camera passthrough (MissionPlanning stack)

[RequireComponent(typeof(CrystalCIFViewer))]
public class XRSpatialUI : MonoBehaviour
{
    [Tooltip("Solid background colour used when passthrough is not configured.")]
    public Color fallbackBackground = new Color(0.02f, 0.03f, 0.05f, 0f);

    private CrystalCIFViewer _viewer;
    private CrystalInteraction _interaction;
    private CrystalTransformController _desktopControls;

    private XRRigDriver _rig;
    private CrystalDirectGrab _grabber;
    private VirtualHands _hands;
    private CrystalPlacard _placard;
    private ControlTablet _tablet;
    private Transform _trackingSpace;
    private Transform _infoPanel;
    private UIText _infoText;
    private bool _active;
    private bool _placed;
    private bool _prevPrimary, _prevSecondary;
    private float _healTimer;

    private void Awake()
    {
        _viewer = GetComponent<CrystalCIFViewer>();
        _interaction = GetComponent<CrystalInteraction>();
        _desktopControls = GetComponent<CrystalTransformController>();
    }

    private void Start()
    {
        // On the headset, enable passthrough as early as possible so OVRManager initializes
        // it during startup. Desktop is untouched (guarded so the Mac build keeps its camera).
        if (Application.platform == RuntimePlatform.Android || XRSettings.isDeviceActive)
            TryEnablePassthrough();
    }

    private void Update()
    {
        if (!_active)
        {
            if (XRSettings.isDeviceActive) Activate();
            return;
        }

        // Initial placement — only once head tracking is GENUINELY live (device reports
        // tracked and the head has moved a real distance from the origin), so content isn't
        // placed relative to a default/garbage pose during startup.
        if (!_placed && _rig != null && _rig.Head != null)
        {
            var eye = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            bool tracked = eye.isValid &&
                           eye.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t;
            if (tracked && _rig.Head.localPosition.sqrMagnitude > 0.01f)
            {
                PlaceInFrontOfUser();
                // Seed the visual centre so the FIRST structure switch keeps its centre where
                // this structure currently sits (OnCrystalRebuilt then maintains it thereafter).
                _localCenter = ComputeLocalCenter();
                _hasCenter = true;
                _placed = true;
            }
        }

        // Gentle self-heal: everything shares the tracking space now, so distances here are
        // room-relative and stable. If the crystal ends up out of reach (bad early placement,
        // origin-mode switch, carried away), bring it back — this is also the only recovery
        // path for hand-tracking users, who have no recenter button.
        if (_placed && _rig != null && _rig.Head != null)
        {
            _healTimer += Time.deltaTime;
            if (_healTimer > 2f)
            {
                _healTimer = 0f;
                Vector3 c = CrystalBounds().center;
                Vector3 h = _rig.Head.position;
                if (Vector3.Distance(h, c) > 3f || Mathf.Abs(c.y - h.y) > 1.5f)
                {
                    Debug.Log($"[XRSpatialUI] Crystal out of reach (dist {Vector3.Distance(h, c):F1} m) — re-placing.");
                    _grabber?.ForceRelease();
                    PlaceInFrontOfUser();
                }
            }
        }

        // Invisible world recenter: something external still drags the rig through world
        // space. Content is immune (it shares the tracking space), but unbounded coordinates
        // would eventually break float precision — so when the rig root strays >5 m from the
        // origin, snap it back. Camera, hands, and content all move uniformly: the user
        // perceives nothing.
        if (_rig != null && _rig.Head != null)
        {
            Transform top = _rig.Head;
            while (top.parent != null) top = top.parent;
            if (top.position.sqrMagnitude > 25f)
            {
                Debug.Log($"[XRSpatialUI] Re-zeroing rig root '{top.name}' (was at {top.position}).");
                top.position = Vector3.zero;
            }
        }

        // A/X toggles the tablet; B/Y recentres everything.
        bool primary = XRRigDriver.GetPrimaryButton(XRNode.RightHand) ||
                       XRRigDriver.GetPrimaryButton(XRNode.LeftHand);
        if (primary && !_prevPrimary && _tablet != null)
            _tablet.gameObject.SetActive(!_tablet.gameObject.activeSelf);
        _prevPrimary = primary;

        bool secondary = XRRigDriver.GetSecondaryButton(XRNode.RightHand) ||
                         XRRigDriver.GetSecondaryButton(XRNode.LeftHand);
        if (secondary && !_prevSecondary)
            PlaceInFrontOfUser();
        _prevSecondary = secondary;

    }

    private readonly System.Collections.Generic.List<string> _errors = new();

    // Run one activation step; a failure is logged instead of
    // silently aborting every later step.
    private void Step(string name, System.Action action)
    {
        try { action(); }
        catch (System.Exception e)
        {
            _errors.Add($"{name}: {e.GetType().Name} {e.Message}");
            Debug.LogException(e);
        }
    }

    private void Activate()
    {
        _active = true;
        Debug.Log("[XRSpatialUI] XR device active — switching to spatial UI.");

        Step("desktop-off", () =>
        {
            if (_desktopControls != null) _desktopControls.enabled = false;
            if (_interaction != null) _interaction.enabled = false;
            var hud = GameObject.Find("CrystalUI");
            if (hud != null) hud.SetActive(false);
        });

        Step("rig", () =>
        {
            _rig = gameObject.AddComponent<XRRigDriver>();
            _rig.Setup();
        });

        Step("locomotion", NeutralizeLocomotion);
        Step("passthrough", TryEnablePassthrough);

        Step("anchor", () =>
        {
            // Anchor content inside the tracking space so any world-space rig motion moves
            // camera and content together (stable room view regardless of cause).
            _trackingSpace = _rig != null && _rig.Head != null ? _rig.Head.parent : null;
            if (_trackingSpace != null)
            {
                transform.SetParent(_trackingSpace, true);
                Debug.Log($"[XRSpatialUI] Content anchored to tracking space '{_trackingSpace.name}'.");
            }
        });

        Step("rig-lock", () =>
        {
            // Something in the scene drives the rig root downward every frame (confirmed via
            // the HUD: 'OVRCameraRig' world y keeps decreasing). Pin the root's pose after
            // every other script has run — everything lives under it, so the user sees no
            // change, but the fall is cancelled outright.
            if (_rig != null && _rig.Head != null)
            {
                Transform top = _rig.Head;
                while (top.parent != null) top = top.parent;
                if (top.GetComponent<RigAnchorLock>() == null)
                    top.gameObject.AddComponent<RigAnchorLock>();

                // Also log the root's component inventory — the mover is one of these.
                var sb = new System.Text.StringBuilder($"[XRSpatialUI] '{top.name}' components: ");
                foreach (var comp in top.GetComponents<Component>())
                    if (comp != null) sb.Append(comp.GetType().Name).Append("  ");
                Debug.Log(sb.ToString());
            }
        });

        Step("grabber", () =>
        {
            _grabber = gameObject.AddComponent<CrystalDirectGrab>();
            _grabber.crystal = transform;
            _grabber.trackingSpace = _trackingSpace;   // set in the anchor step above
        });

        Step("virtual-hands", () =>
        {
            // Passthrough hands are camera-distorted; draw clean virtual hands from joint data.
            var handsGO = new GameObject("VirtualHands");
            if (_trackingSpace != null) handsGO.transform.SetParent(_trackingSpace, false);
            _hands = handsGO.AddComponent<VirtualHands>();
            _hands.trackingSpace = _trackingSpace;
            _hands.grabber = _grabber;
        });

        Step("interactors", () =>
        {
            MakeInteractor(XRNode.LeftHand,  _rig.Left);
            MakeInteractor(XRNode.RightHand, _rig.Right);
        });

        Step("placard", () =>
        {
            var placardGO = new GameObject("PlacardRoot");
            if (_trackingSpace != null) placardGO.transform.SetParent(_trackingSpace, true);
            _placard = placardGO.AddComponent<CrystalPlacard>();
            _placard.viewer = _viewer;
            _placard.head = _rig.Head;
            _placard.Build();
        });

        Step("tablet", () =>
        {
            var tabletGO = new GameObject("TabletRoot");
            if (_trackingSpace != null) tabletGO.transform.SetParent(_trackingSpace, true);
            _tablet = tabletGO.AddComponent<ControlTablet>();
            _tablet.viewer = _viewer;
            _tablet.interaction = _interaction;
            _tablet.Build();
        });

        Step("info-panel", () =>
        {
            // Selection / measurement readout — the screen-space HUD is disabled in headset
            // mode, so mirror it into a world-space panel to the right of the crystal.
            var canvas = WorldUIKit.MakeCanvas("InfoPanel", new Vector2(520, 300),
                _trackingSpace != null ? _trackingSpace : null);
            _infoPanel = canvas.transform;
            var root = canvas.GetComponent<RectTransform>();
            WorldUIKit.MakePanel(root, new Vector2(520, 300), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            WorldUIKit.MakeText(root, "SELECTION", 22f, WorldUIStyle.TextDim,
                new Vector2(480, 28), new Vector2(20, -12), new Vector2(0, 1), new Vector2(0, 1),
                TextAnchor.MiddleLeft, bold: true);
            _infoText = WorldUIKit.MakeText(root, "Point at an atom and pinch to select.",
                22f, WorldUIStyle.TextMain, new Vector2(484, 232), new Vector2(20, -48),
                new Vector2(0, 1), new Vector2(0, 1), TextAnchor.UpperLeft);
            if (_interaction != null) _interaction.OnReadout += OnReadoutChanged;
        });

        Step("rebuild-anchor", () =>
        {
            // Keep the crystal's visual centre fixed in the room across rebuilds. Different
            // structures have different bounds centres relative to the root — without this,
            // loading a new CIF makes the new crystal materialise offset from the old one
            // (e.g. albite → quartz shifted it half a metre, right into the user's head).
            if (_viewer != null) _viewer.OnCrystalBuilt += OnCrystalRebuilt;
        });

        Step("diagnostics", () => StartCoroutine(LogDiagnosticsAfterDelay()));
    }

    private void OnDestroy()
    {
        if (_viewer != null) _viewer.OnCrystalBuilt -= OnCrystalRebuilt;
        if (_interaction != null) _interaction.OnReadout -= OnReadoutChanged;
    }

    private void OnReadoutChanged(string selection, string result)
    {
        if (_infoText == null) return;
        string txt = "";
        if (!string.IsNullOrEmpty(result))    txt += result + "\n\n";
        if (!string.IsNullOrEmpty(selection)) txt += selection;
        _infoText.text = string.IsNullOrEmpty(txt)
            ? "Point at an atom and pinch to select." : txt;
    }

    private Vector3 _localCenter;
    private bool _hasCenter;

    private void OnCrystalRebuilt(CrystalCIFViewer.CrystalModel model)
    {
        Vector3 newLocal = ComputeLocalCenter();
        if (_placed && _hasCenter)
        {
            Vector3 oldWorld = transform.TransformPoint(_localCenter);
            Vector3 newWorld = transform.TransformPoint(newLocal);
            transform.position += oldWorld - newWorld;   // new structure appears where the old one was
        }
        _localCenter = newLocal;
        _hasCenter = true;
    }

    private Vector3 ComputeLocalCenter()
    {
        var atoms = transform.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) return Vector3.zero;
        var b = new Bounds(atoms[0].transform.localPosition, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.localPosition);
        return b.center;
    }

    // One-shot status report ~8s in — readable on device via `adb logcat -s Unity`.
    private System.Collections.IEnumerator LogDiagnosticsAfterDelay()
    {
        yield return new WaitForSeconds(8f);
        // Building blocks can finish initializing after activation — sweep again.
        NeutralizeLocomotion();
        var mgr = FindAnyObjectByType<OVRManager>();
        var layer = FindAnyObjectByType<OVRPassthroughLayer>();
        bool handL = _rig != null && _rig.IsHandTracked(XRNode.LeftHand);
        bool handR = _rig != null && _rig.IsHandTracked(XRNode.RightHand);
        var xrCam = FindXRCamera();
        var crystalCenter = CrystalBounds().center;
        Vector3 headPos = _rig != null && _rig.Head != null ? _rig.Head.position : Vector3.zero;
        Debug.Log($"[XRSpatialUI] Diagnostics — OVRManager:{mgr != null}  " +
                  $"passthroughFlag:{(mgr != null && mgr.isInsightPassthroughEnabled)}  " +
                  $"passthroughLayer:{layer != null}  handTrackedL:{handL}  handTrackedR:{handR}  " +
                  $"device:{XRSettings.loadedDeviceName}  " +
                  $"activeCameras:{Camera.allCamerasCount}  xrCam:{(xrCam != null ? xrCam.name : "none")}  " +
                  $"bgAlpha:{(xrCam != null ? xrCam.backgroundColor.a.ToString("F2") : "-")}  " +
                  $"hdr:{(xrCam != null && xrCam.allowHDR)}  " +
                  $"crystal:{crystalCenter}  head:{headPos}  dist:{Vector3.Distance(headPos, crystalCenter):F2}m  " +
                  $"placed:{_placed}");
    }

    private void MakeInteractor(XRNode node, Transform hand)
    {
        if (hand == null) return;
        var ir = hand.gameObject.AddComponent<XRRayInteractor>();
        ir.hand = node;
        ir.interaction = _interaction;
        ir.rig = _rig;
    }

    // The camera that actually renders to the headset: the Meta rig's CenterEyeAnchor when
    // present. Camera.main is unreliable here — the desktop camera may also be tagged MainCamera.
    private Camera FindXRCamera()
    {
        if (_rig != null && _rig.Head != null)
        {
            var c = _rig.Head.GetComponent<Camera>();
            if (c != null) return c;
        }
        var ce = GameObject.Find("CenterEyeAnchor");
        if (ce != null)
        {
            var c = ce.GetComponent<Camera>();
            if (c != null) return c;
        }
        return Camera.main;
    }

    // In XR every enabled camera renders to the HMD — a leftover desktop camera means the
    // whole scene is stereo-rendered twice (major GPU cost) and its opaque background can
    // sit on top of the passthrough. Keep exactly one.
    private void DisableExtraCameras(Camera keep)
    {
        if (keep == null) return;
        foreach (var cam in Camera.allCameras)   // enabled cameras only
        {
            if (cam == keep || cam.targetTexture != null) continue;
            cam.enabled = false;
            var listener = cam.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = false;
            Debug.Log($"[XRSpatialUI] Disabled extra camera '{cam.name}' — XR renders via '{keep.name}'.");
        }
        var keepListener = keep.GetComponent<AudioListener>();
        if (keepListener != null) keepListener.enabled = true;
    }

    // Enable video passthrough by default when a Meta rig is present.
    // (Requires Passthrough Support in the Oculus project config + manifest, both enabled.)
    private void TryEnablePassthrough()
    {
        var cam = FindXRCamera();
        DisableExtraCameras(cam);
        if (cam != null)
        {
            // Alpha-zero background: correct compositing for passthrough, and a clean
            // dark backdrop if passthrough is unavailable.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = fallbackBackground;

            // HDR and post-processing destroy the eye buffer's alpha channel in URP, which
            // makes the compositor treat ALL rendered content as transparent — passthrough
            // then shows through everything and the app appears empty. Force both off.
            cam.allowHDR = false;
            var extra = cam.GetComponent<UniversalAdditionalCameraData>();
            if (extra != null)
            {
                extra.renderPostProcessing = false;
                extra.antialiasing = AntialiasingMode.None; // FXAA also rewrites alpha
            }
        }

        // AR Foundation route (Unity's XR Origin stack — the proven MissionPlanning setup):
        // an ARSession plus ARCameraManager/ARCameraBackground on the eye camera makes the
        // meta-openxr "Camera (Passthrough)" feature composite the real world behind content.
        EnsureARFoundationPassthrough(cam);

        // Legacy Meta-rig route (only relevant while an OVRManager is in the scene).
        var mgr = FindAnyObjectByType<OVRManager>();
        if (mgr == null)
        {
            Debug.Log("[XRSpatialUI] No OVRManager — using the AR Foundation passthrough path.");
            return;
        }

        mgr.isInsightPassthroughEnabled = true;

        var existing = FindAnyObjectByType<OVRPassthroughLayer>();
        if (existing != null)
        {
            existing.overlayType = OVROverlay.OverlayType.Underlay;
        }
        else
        {
            // Create on an inactive GO so the layer's initialization sees Underlay from the
            // start — its default is Overlay, which would draw passthrough over the whole app.
            var layerGO = new GameObject("PassthroughLayer");
            layerGO.SetActive(false);
            var layer = layerGO.AddComponent<OVRPassthroughLayer>();
            layer.overlayType = OVROverlay.OverlayType.Underlay;
            layerGO.SetActive(true);
        }
        Debug.Log($"[XRSpatialUI] Passthrough enabled (underlay) on camera '{(cam != null ? cam.name : "none")}'.");
    }

    // Guarantee the AR Foundation pieces exist: an AR Session in the scene and the camera
    // manager/background on the eye camera. Safe to call repeatedly; adds only what's missing.
    private void EnsureARFoundationPassthrough(Camera cam)
    {
        try
        {
            if (FindAnyObjectByType<ARSession>() == null)
            {
                var s = new GameObject("AR Session");
                s.AddComponent<ARSession>();
                s.AddComponent<ARInputManager>();
                Debug.Log("[XRSpatialUI] Created AR Session (+ARInputManager).");
            }
            if (cam != null)
            {
                if (cam.GetComponent<ARCameraManager>() == null)
                {
                    cam.gameObject.AddComponent<ARCameraManager>();
                    Debug.Log("[XRSpatialUI] Added ARCameraManager to eye camera.");
                }
                if (cam.GetComponent<ARCameraBackground>() == null)
                {
                    cam.gameObject.AddComponent<ARCameraBackground>();
                    Debug.Log("[XRSpatialUI] Added ARCameraBackground to eye camera.");
                }
            }
        }
        catch (System.Exception e)
        {
            _errors.Add($"arfoundation: {e.GetType().Name} {e.Message}");
            Debug.LogException(e);
        }
    }

    // A passthrough tabletop app must not have artificial locomotion. If the camera rig
    // carries a player controller (CharacterController + gravity — common on rig prefabs
    // and building blocks), and the scene has NO floor collider, the rig free-falls forever:
    // all content appears to accelerate upward ("crystal flying into the sky").
    // Walk the rig hierarchy and disable every gravity/locomotion source.
    private void NeutralizeLocomotion()
    {
        if (_rig == null || _rig.Head == null) return;

        // Locomotion / rig-moving scripts referenced by NAME so this compiles on any SDK version.
        // OVRHeadsetEmulator is an editor-testing tool that integrates garbage input into rig
        // motion when it runs in a device build — it was the "crystal falling into the sky" bug.
        string[] locomotionTypes =
        {
            "OVRPlayerController", "SimpleCapsuleWithStickMovement",
            "CharacterMovement", "PlayerController", "LocomotionController",
            "OVRHeadsetEmulator",
        };

        for (var t = _rig.Head; t != null; t = t.parent)
        {
            // The headset emulator is pure editor tooling; on device it integrates garbage
            // input into rig motion, and merely disabling it may leave callbacks registered.
            // Destroy it outright.
            var emu = t.GetComponent("OVRHeadsetEmulator");
            if (emu != null)
            {
                Destroy(emu);
                Debug.Log($"[XRSpatialUI] DESTROYED OVRHeadsetEmulator on '{t.name}'.");
            }

            var cc = t.GetComponent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                cc.enabled = false;
                Debug.Log($"[XRSpatialUI] Disabled CharacterController on '{t.name}' (rig was free-falling — no floor collider in AR).");
            }

            var rb = t.GetComponent<Rigidbody>();
            if (rb != null && (rb.useGravity || !rb.isKinematic))
            {
                rb.useGravity  = false;
                rb.isKinematic = true;
                Debug.Log($"[XRSpatialUI] Froze Rigidbody on '{t.name}'.");
            }

            foreach (var typeName in locomotionTypes)
            {
                if (t.GetComponent(typeName) is Behaviour b && b.enabled)
                {
                    b.enabled = false;
                    Debug.Log($"[XRSpatialUI] Disabled locomotion script '{typeName}' on '{t.name}'.");
                }
            }
        }
    }

    private void PlaceInFrontOfUser(bool moveTablet = true)
    {
        if (_rig == null || _rig.Head == null) return;
        var head = _rig.Head;

        // Heal any runaway scale BEFORE computing bounds — an inflated crystal has its
        // visual centre metres away from its pivot, which defeats distance-based placement.
        float s = transform.localScale.x;
        if (s > 4f || s < 0.2f)
        {
            Debug.Log($"[XRSpatialUI] Resetting runaway crystal scale {s:F2} → 1.");
            transform.localScale = Vector3.one;
        }

        Vector3 fwd = head.forward; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;

        // Crystal: ~70 cm ahead, slightly below eye level.
        Vector3 crystalCenter = head.position + fwd * 0.70f + Vector3.down * 0.05f;
        var bounds = CrystalBounds();
        transform.position += crystalCenter - bounds.center;

        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        // Tablet: to the left of the crystal, angled toward the user.
        if (moveTablet && _tablet != null)
        {
            var t = _tablet.transform;
            t.position = head.position + fwd * 0.55f + right * -0.42f + Vector3.down * 0.12f;
            t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);
        }

        // Info readout: to the right of the crystal, angled toward the user.
        if (_infoPanel != null)
        {
            _infoPanel.position = head.position + fwd * 0.6f + right * 0.42f + Vector3.down * 0.05f;
            _infoPanel.rotation = Quaternion.LookRotation(_infoPanel.position - head.position, Vector3.up);
        }
        // Placard positions itself each frame (docked under the crystal).
        if (_placard != null)
            _placard.transform.position = crystalCenter + Vector3.down * 0.3f;
    }

    private Bounds CrystalBounds()
    {
        var atoms = transform.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) return new Bounds(transform.position, Vector3.one * 0.2f);
        var b = new Bounds(atoms[0].transform.position, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.position);
        return b;
    }
}

/// <summary>
/// Pins a transform's world pose to what it was when this component was added. Runs with a
/// very late execution order so it overrides whatever other script wrote the pose that frame.
/// Used on the camera-rig root to cancel an unidentified downward driver — everything in the
/// app lives under the root, so the correction is imperceptible to the user.
/// </summary>
[DefaultExecutionOrder(32000)]
public class RigAnchorLock : MonoBehaviour
{
    private Vector3 _pos;
    private Quaternion _rot;

    private void OnEnable()
    {
        _pos = transform.position;
        _rot = transform.rotation;
        // Some movers write the pose AFTER LateUpdate (render callbacks). Enforcing again
        // in onBeforeRender wins the write race at the very last moment before the frame.
        Application.onBeforeRender += Enforce;
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= Enforce;
    }

    private void LateUpdate() => Enforce();

    private void Enforce()
    {
        transform.position = _pos;
        transform.rotation = _rot;
    }
}
