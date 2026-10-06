using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// Measurement tools and crystal info HUD for CrystalCIFViewer.
///
/// Add this component to the same GameObject as CrystalCIFViewer.
/// The public SelectAtom() and SetMode() methods can be called from VR
/// controller scripts so XR-based picking integrates without modifying this class.
///
/// Keyboard shortcuts (desktop / editor):
///   1  Info mode       2  Distance       3  Angle       4  Torsion
///   Tab  cycle mode    C  toggle crystal info panel
///   LMB  select atom   RMB / Esc  clear selection
/// </summary>
[RequireComponent(typeof(CrystalCIFViewer))]
public class CrystalInteraction : MonoBehaviour
{
    public enum MeasureMode { Info = 0, Distance = 1, Angle = 2, Torsion = 3 }

    [Header("Picking")]
    [Tooltip("Camera used for mouse-ray picking. Leave null to use Camera.main.")]
    public Camera    pickCamera;
    public LayerMask pickLayers = Physics.DefaultRaycastLayers;

    [Header("Visuals")]
    public Color measureColor   = new Color(1f, 0.92f, 0.02f);
    public Color highlightColor = new Color(1f, 0.65f, 0.0f);
    [Range(0.005f, 0.05f)] public float measureLineWidth = 0.015f;

    [Header("CIF library")]
    [Tooltip("Extra folders to scan for .cif/.txt structures at runtime, in addition to StreamingAssets and persistentDataPath.")]
    public string[] extraCifFolders;

    [Header("Debug")]
    [Tooltip("Log pick ray diagnostics to the Console on every left-click attempt.")]
    public bool debugPicking = true;

    // ── state ─────────────────────────────────────────────────────────────────
    private CrystalCIFViewer        _viewer;
    private MeasureMode             _mode = MeasureMode.Distance;
    private readonly List<AtomInfo> _sel  = new(4);
    private readonly Dictionary<AtomInfo, Material> _origMats = new();

    // ── scene objects ─────────────────────────────────────────────────────────
    private GameObject _measureRoot;

    // ── UI ────────────────────────────────────────────────────────────────────
    private Canvas _canvas;
    private Text   _crystalInfoText;
    private Text   _selText;
    private Text   _measureText;
    private Text   _modeText;
    private readonly Text[] _styleLabels = new Text[5]; // one per DisplayStyle value
    private Slider _minSlider, _maxSlider;
    private Text   _minVal, _maxVal;
    private Font   _uiFont;
    private GameObject _legendPanel;
    private Text   _millerText;
    private bool   _millerShow;
    private GameObject _browserPanel;
    private bool   _browserOpen;
    private Slider _pressureSlider;
    private Text   _pressureVal, _k0Val, _pressureInfo;

    // ─── lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        _viewer = GetComponent<CrystalCIFViewer>();
        _viewer.OnCrystalBuilt += HandleCrystalBuilt;
    }

    private void Start()
    {
        BuildUI();
        RefreshModeBar();
        if (_viewer.LastBaseModel != null)
            HandleCrystalBuilt(_viewer.LastBaseModel);
    }

    private void OnDestroy()
    {
        if (_viewer != null) _viewer.OnCrystalBuilt -= HandleCrystalBuilt;
        foreach (var a in _sel.ToArray()) Unhighlight(a);
        if (_measureRoot != null) Destroy(_measureRoot);
        if (_canvas != null)      Destroy(_canvas.gameObject);
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    private void Update()
    {
        HandleKeys();

        var mouse = Mouse.current;
        if (mouse == null)
        {
            if (debugPicking) Debug.LogWarning("[CrystalInteraction] Mouse.current is null — no mouse device registered.");
            return;
        }

        var cam = pickCamera != null ? pickCamera : Camera.main;
        if (cam == null)
        {
            if (debugPicking) Debug.LogWarning("[CrystalInteraction] No camera found. Assign pickCamera or tag a camera MainCamera.");
            return;
        }

        var mousePos = mouse.position.ReadValue();
        var ray = cam.ScreenPointToRay((Vector3)mousePos);

        // Always visible in Scene view during play — helps confirm ray direction.
        Debug.DrawRay(ray.origin, ray.direction * 50f, Color.yellow);

        // Fire on release so a drag-orbit doesn't accidentally select an atom.
        if (!mouse.leftButton.wasReleasedThisFrame) return;
        var tc = GetComponent<CrystalTransformController>();
        if (tc != null && tc.OrbitEndedThisFrame) return;
        // Clicks on UI (buttons, sliders, panels) belong to the UI, not atom picking.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        if (debugPicking) Debug.Log($"[CrystalInteraction] Click at screen {mousePos}  ray origin={ray.origin:F2} dir={ray.direction:F2}");

        if (Physics.Raycast(ray, out var hit, 1000f, pickLayers, QueryTriggerInteraction.Collide))
        {
            if (debugPicking) Debug.Log($"[CrystalInteraction] Raycast hit '{hit.collider.gameObject.name}' layer={hit.collider.gameObject.layer} dist={hit.distance:F3}");
            var a = hit.collider.GetComponent<AtomInfo>();
            if (a != null)
                SelectAtom(a);
            else if (debugPicking)
                Debug.Log("[CrystalInteraction] Hit object has no AtomInfo — likely a bond or unrelated collider.");
        }
        else
        {
            if (debugPicking) Debug.Log("[CrystalInteraction] Raycast hit nothing.");
        }
    }

    private void HandleKeys()
    {
        var kb    = Keyboard.current;
        var mouse = Mouse.current;

        // While typing in the structure search box, letters must go to the text field — not
        // trigger shortcuts (typing "quartz" would otherwise toggle the browser on the 'o').
        if (kb != null && !IsTypingInUI())
        {
            if (kb.digit1Key.wasPressedThisFrame) SetMode(MeasureMode.Info);
            if (kb.digit2Key.wasPressedThisFrame) SetMode(MeasureMode.Distance);
            if (kb.digit3Key.wasPressedThisFrame) SetMode(MeasureMode.Angle);
            if (kb.digit4Key.wasPressedThisFrame) SetMode(MeasureMode.Torsion);
            if (kb.tabKey.wasPressedThisFrame)    SetMode((MeasureMode)(((int)_mode + 1) % 4));
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (_browserOpen) ToggleBrowser(); else ClearSelection();
            }
            if (kb.vKey.wasPressedThisFrame)
            {
                var next = (DisplayStyle)(((int)_viewer.displayStyle + 1) % 5);
                _viewer.SetDisplayStyle(next);
                RefreshStyleBar();
            }
            if (kb.cKey.wasPressedThisFrame && _crystalInfoText != null)
            {
                var panel = _crystalInfoText.transform.parent.gameObject;
                panel.SetActive(!panel.activeSelf);
            }
            if (kb.oKey.wasPressedThisFrame && _browserPanel != null)
                ToggleBrowser();
        }

        if (mouse != null && mouse.rightButton.wasPressedThisFrame)
            ClearSelection();
    }

    // ─── Public API (callable from VR controller scripts) ────────────────────

    /// <summary>Toggle selection of an atom. Safe to call from a VR controller's select event.</summary>
    public void SelectAtom(AtomInfo atom)
    {
        if (atom == null) return;

        int idx = _sel.IndexOf(atom);
        if (idx >= 0)
        {
            Unhighlight(atom);
            _sel.RemoveAt(idx);
        }
        else
        {
            if (_sel.Count >= MaxForMode(_mode))
            {
                Unhighlight(_sel[0]);
                _sel.RemoveAt(0);
            }
            Highlight(atom);
            _sel.Add(atom);
        }
        Refresh();
    }

    public void ClearSelection()
    {
        foreach (var a in _sel.ToArray()) Unhighlight(a);
        _sel.Clear();
        Refresh();
    }

    public void SetMode(MeasureMode m)
    {
        ClearSelection();
        _mode = m;
        RefreshModeBar();
    }

    // ─── Highlight ────────────────────────────────────────────────────────────

    private void Highlight(AtomInfo atom)
    {
        if (atom == null || _origMats.ContainsKey(atom)) return;
        var rend = atom.GetComponent<Renderer>();
        if (rend == null) return;
        _origMats[atom] = rend.sharedMaterial;
        var mat = new Material(rend.sharedMaterial);
        mat.color = Color.Lerp(rend.sharedMaterial.color, highlightColor, 0.65f);
        rend.material = mat;
    }

    private void Unhighlight(AtomInfo atom)
    {
        if (atom == null) return;
        var rend = atom.GetComponent<Renderer>();
        if (rend != null && _origMats.TryGetValue(atom, out var orig))
        {
            var inst = rend.material;
            rend.sharedMaterial = orig;
            if (inst != null && inst != orig) Destroy(inst);
        }
        _origMats.Remove(atom);
    }

    // ─── Measurements ─────────────────────────────────────────────────────────

    private void Refresh()
    {
        ClearMeasureVisuals();

        string result = "";
        switch (_mode)
        {
            case MeasureMode.Info when _sel.Count == 1:
                result = FormatAtomInfo(_sel[0]);
                break;

            case MeasureMode.Distance when _sel.Count == 2:
            {
                float d = Dist(_sel[0], _sel[1]);
                result = $"<b>Distance</b>  {_sel[0].AtomLabel} – {_sel[1].AtomLabel}\n<b>{d:F4} Å</b>";
                SpawnLine(_sel[0].transform.position, _sel[1].transform.position, $"{d:F3} Å");
                break;
            }

            case MeasureMode.Angle when _sel.Count == 3:
            {
                float ang = Angle(_sel[0], _sel[1], _sel[2]);
                result = $"<b>Angle</b>  {_sel[0].AtomLabel}–{_sel[1].AtomLabel}–{_sel[2].AtomLabel}\n<b>{ang:F2}°</b>";
                SpawnLine(_sel[0].transform.position, _sel[1].transform.position, "");
                SpawnLine(_sel[1].transform.position, _sel[2].transform.position, "");
                SpawnLabel($"{ang:F2}°", _sel[1].transform.position + Vector3.up * 0.12f);
                break;
            }

            case MeasureMode.Torsion when _sel.Count == 4:
            {
                float tor = Torsion(_sel[0], _sel[1], _sel[2], _sel[3]);
                result = $"<b>Torsion</b>  {_sel[0].AtomLabel}–{_sel[1].AtomLabel}–{_sel[2].AtomLabel}–{_sel[3].AtomLabel}\n<b>{tor:F2}°</b>";
                for (int i = 0; i < 3; i++)
                    SpawnLine(_sel[i].transform.position, _sel[i + 1].transform.position, "");
                var mid = (_sel[1].transform.position + _sel[2].transform.position) * 0.5f;
                SpawnLabel($"{tor:F2}°", mid + Vector3.up * 0.12f);
                break;
            }

            default:
            {
                int need = MaxForMode(_mode) - _sel.Count;
                if (need > 0 && _sel.Count > 0)
                    result = $"Select {need} more atom{(need > 1 ? "s" : "")}…";
                break;
            }
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _sel.Count; i++)
        {
            var a = _sel[i];
            sb.AppendLine($"[{i + 1}] <b>{a.AtomLabel}</b> ({a.Element})" +
                          $"   ({a.FracCoords.x:F4}, {a.FracCoords.y:F4}, {a.FracCoords.z:F4})");
        }
        string selectionText = sb.ToString().TrimEnd();

        if (_selText != null)     _selText.text = selectionText;
        if (_measureText != null) _measureText.text = result;

        // Surface the readout so the XR spatial UI can show it in-world (the screen-space HUD
        // is disabled in headset mode).
        OnReadout?.Invoke(selectionText, result);
    }

    /// <summary>Fired whenever the selection/measurement readout changes: (selectionText, resultText).</summary>
    public event System.Action<string, string> OnReadout;

    private static int MaxForMode(MeasureMode m) => m switch
    {
        MeasureMode.Info     => 1,
        MeasureMode.Distance => 2,
        MeasureMode.Angle    => 3,
        MeasureMode.Torsion  => 4,
        _                    => 1,
    };

    // All calculations performed in Angstroms via CartAngstrom.
    private static float Dist(AtomInfo a, AtomInfo b)
        => Vector3.Distance(a.CartAngstrom, b.CartAngstrom);

    private static float Angle(AtomInfo a, AtomInfo vertex, AtomInfo c)
    {
        var v1 = (a.CartAngstrom - vertex.CartAngstrom).normalized;
        var v2 = (c.CartAngstrom - vertex.CartAngstrom).normalized;
        return Mathf.Acos(Mathf.Clamp(Vector3.Dot(v1, v2), -1f, 1f)) * Mathf.Rad2Deg;
    }

    private static float Torsion(AtomInfo a, AtomInfo b, AtomInfo c, AtomInfo d)
    {
        var b1 = b.CartAngstrom - a.CartAngstrom;
        var b2 = c.CartAngstrom - b.CartAngstrom;
        var b3 = d.CartAngstrom - c.CartAngstrom;
        var n1 = Vector3.Cross(b1, b2).normalized;
        var n2 = Vector3.Cross(b2, b3).normalized;
        var m1 = Vector3.Cross(n1, b2.normalized);
        return Mathf.Atan2(Vector3.Dot(m1, n2), Vector3.Dot(n1, n2)) * Mathf.Rad2Deg;
    }

    // ─── Measurement visuals ──────────────────────────────────────────────────

    private void ClearMeasureVisuals()
    {
        if (_measureRoot != null) Destroy(_measureRoot);
        _measureRoot = new GameObject("MeasureOverlay");
    }

    private void SpawnLine(Vector3 from, Vector3 to, string label)
    {
        var go = new GameObject("MLine");
        go.transform.SetParent(_measureRoot.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace   = true;
        lr.positionCount   = 2;
        lr.SetPositions(new[] { from, to });
        lr.widthMultiplier = measureLineWidth;
        lr.sharedMaterial  = CrystalCIFViewer.ElementStylings.GetWireMaterial();
        lr.startColor = lr.endColor = measureColor;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        if (!string.IsNullOrEmpty(label))
            SpawnLabel(label, (from + to) * 0.5f + Vector3.up * 0.06f);
    }

    private void SpawnLabel(string text, Vector3 worldPos)
    {
        var go = new GameObject("MLabel");
        go.transform.SetParent(_measureRoot.transform, false);
        go.transform.position   = worldPos;
        go.transform.localScale = Vector3.one * 0.009f; // slightly smaller for headset viewing
        var tm = go.AddComponent<TextMesh>();
        tm.text     = text;
        tm.fontSize = 240;
        tm.color    = measureColor;
        tm.anchor   = TextAnchor.MiddleCenter;
        go.AddComponent<Billboard>();
    }

    // ─── Crystal info ─────────────────────────────────────────────────────────

    private void HandleCrystalBuilt(CrystalCIFViewer.CrystalModel model)
    {
        ClearSelection();
        if (_crystalInfoText != null)
            _crystalInfoText.text = BuildCrystalInfoString(model);
        RefreshStyleBar();
        RefreshBondSliders();
        RebuildLegend(model);
    }

    private static string BuildCrystalInfoString(CrystalCIFViewer.CrystalModel m)
    {
        var met = m.Metrics;
        float vol = CellVolume(met);
        return $"<b>{m.Title}</b>\n" +
               $"Space group: <b>{m.SpaceGroupName ?? "?"}</b>{(m.SpaceGroupNumber > 0 ? $"  (#{m.SpaceGroupNumber})" : "")}\n" +
               $"Crystal system: {CrystalSystem(m.SpaceGroupNumber, m.SpaceGroupName)}\n" +
               $"a = {met.a:F4} Å    b = {met.b:F4} Å    c = {met.c:F4} Å\n" +
               $"α = {met.alpha:F3}°   β = {met.beta:F3}°   γ = {met.gamma:F3}°\n" +
               $"V = {vol:F2} Å³     ASU atoms: {m.Atoms.Count}";
    }

    private static float CellVolume(CrystalCIFViewer.CellMetrics m)
    {
        float ca = Mathf.Cos(m.alpha * Mathf.Deg2Rad);
        float cb = Mathf.Cos(m.beta  * Mathf.Deg2Rad);
        float cg = Mathf.Cos(m.gamma * Mathf.Deg2Rad);
        return m.a * m.b * m.c * Mathf.Sqrt(Mathf.Max(0f,
            1f - ca * ca - cb * cb - cg * cg + 2f * ca * cb * cg));
    }

    public static string CrystalSystem(int n, string hmName = null)
    {
        // Try the IT number first (authoritative).
        if (n > 0)
        {
            if (n <= 2)   return "Triclinic";
            if (n <= 15)  return "Monoclinic";
            if (n <= 74)  return "Orthorhombic";
            if (n <= 142) return "Tetragonal";
            if (n <= 167) return "Trigonal";
            if (n <= 194) return "Hexagonal";
            return "Cubic";
        }
        // Fall back to parsing the Hermann-Mauguin name.
        return CrystalSystemFromHM(hmName) ?? "Unknown";
    }

    // Derive crystal system from the short H-M symbol (e.g. "P 32 2 1" → Trigonal).
    // Works by inspecting which symmetry direction is first (after the lattice letter).
    private static string CrystalSystemFromHM(string hm)
    {
        if (string.IsNullOrWhiteSpace(hm)) return null;
        var p = hm.Trim().Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        // p[0] = lattice (P/C/I/F/R…), p[1..] = symmetry symbols per direction.
        if (p.Length < 2) return null;
        string s0 = p[1];
        string s1 = p.Length > 2 ? p[2] : "";
        if (s0.StartsWith("6")  || s0.StartsWith("-6"))  return "Hexagonal";
        if (s0.StartsWith("3")  || s0.StartsWith("-3"))  return "Trigonal";
        if (s0.StartsWith("4")  || s0.StartsWith("-4"))  return "Tetragonal";
        // Cubic: body-diagonal [111] is always the second symmetry direction and contains 3.
        if (s1.StartsWith("3")  || s1.StartsWith("-3"))  return "Cubic";
        if (p.Length >= 4)                                return "Orthorhombic";
        if (s0 == "1" || s0 == "-1")                     return "Triclinic";
        return "Monoclinic";
    }

    private static string FormatAtomInfo(AtomInfo a) =>
        $"<b>{a.AtomLabel}</b>  ({a.Element})\n" +
        $"Frac:  ({a.FracCoords.x:F5},  {a.FracCoords.y:F5},  {a.FracCoords.z:F5})\n" +
        $"Cart:  ({a.CartAngstrom.x:F4},  {a.CartAngstrom.y:F4},  {a.CartAngstrom.z:F4}) Å\n" +
        $"Occupancy:  {FormatOccupancy(a)}";

    private static string FormatOccupancy(AtomInfo a)
    {
        if (!string.IsNullOrEmpty(a.SecondElement))
            return $"{a.Element} {a.Occupancy:F3} / {a.SecondElement} {a.SecondOccupancy:F3}";
        return a.Occupancy.ToString("F3");
    }

    // ─── Mode bar ─────────────────────────────────────────────────────────────

    private void RefreshModeBar()
    {
        if (_modeText == null) return;
        string[] names = { "Info", "Distance", "Angle", "Torsion" };
        string[] keys  = { "1",    "2",        "3",     "4"       };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < names.Length; i++)
        {
            if (i == (int)_mode)
                sb.Append($"<b>[{names[i]}]</b>");
            else
                sb.Append($"<color=#aaaaaa>{keys[i]}:{names[i]}</color>");
            if (i < names.Length - 1) sb.Append("   ");
        }
        _modeText.text = sb.ToString();
    }

    // ─── UI construction ──────────────────────────────────────────────────────

    private void BuildUI()
    {
        // Ensure an EventSystem exists for button clicks.
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        var cgo = new GameObject("CrystalUI");
        _canvas = cgo.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 50;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 800); // better match for a laptop screen
        cgo.AddComponent<GraphicRaycaster>();

        Font font = GetUIFont();
        _uiFont = font;

        // Crystal info — top-left
        var ciPanel = MakePanel(cgo.transform, new Vector2(480, 148),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, -10));
        _crystalInfoText = AddText(ciPanel, font, 15, TextAnchor.UpperLeft);
        _crystalInfoText.text = "No crystal loaded.";

        // Mode bar — top-centre
        var modePanel = MakePanel(cgo.transform, new Vector2(480, 32),
            new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -10));
        _modeText = AddText(modePanel, font, 15, TextAnchor.MiddleCenter);

        // Selection list — top-right
        var selPanel = MakePanel(cgo.transform, new Vector2(520, 120),
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -10));
        _selText = AddText(selPanel, font, 14, TextAnchor.UpperLeft);

        // Measurement result — bottom-left
        var measPanel = MakePanel(cgo.transform, new Vector2(480, 108),
            new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(10, 10));
        _measureText = AddText(measPanel, font, 17, TextAnchor.UpperLeft);

        // Controls hint — bottom-right
        var hintPanel = MakePanel(cgo.transform, new Vector2(430, 78),
            new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-10, 10));
        var ht = AddText(hintPanel, font, 13, TextAnchor.UpperLeft);
        ht.color = new Color(1, 1, 1, 0.55f);
        ht.text  = "Click=select   RMB/Esc=clear   Tab=cycle mode\nDrag=orbit   Alt+drag=pan   Scroll=zoom/pan\nF=reset   V=cycle style   C=info   O=open structures";

        // Style bar — below mode bar
        var stylePanel = MakePanel(cgo.transform, new Vector2(560, 32),
            new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -48));
        BuildStyleBar(stylePanel, font);
        RefreshStyleBar();

        // Controls — left, below crystal info (toggles + bond-length sliders)
        var ctrlPanel = MakePanel(cgo.transform, new Vector2(300, 116),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, -166));
        BuildControls(ctrlPanel, font);

        // Miller (hkl) plane — left, below the controls panel
        var millerPanel = MakePanel(cgo.transform, new Vector2(300, 116),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, -290));
        BuildMillerPanel(millerPanel, font);

        // Pressure (Birch–Murnaghan) — left, below the Miller panel
        var pressurePanel = MakePanel(cgo.transform, new Vector2(300, 112),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, -414));
        BuildPressurePanel(pressurePanel, font);

        // Element legend — right, below the selection list (populated per crystal)
        _legendPanel = MakePanel(cgo.transform, new Vector2(160, 60),
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -138));

        // Structure browser — toggle button (top-centre, below the style bar) + popup panel
        var browserBtnPanel = MakePanel(cgo.transform, new Vector2(170, 30),
            new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -84));
        AddButton(browserBtnPanel, font, "Structures", new Vector2(5, -1), new Vector2(160, 28), ToggleBrowser);

        BuildBrowserPanel(cgo.transform, font);

        // Exit button — bottom-right corner, just above the controls hint
        var exitPanel = MakePanel(cgo.transform, new Vector2(84, 30),
            new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-10, 96));
        var exitBtn = AddButton(exitPanel, font, "Exit", new Vector2(4, -1), new Vector2(76, 28), QuitApp);
        exitBtn.GetComponent<Image>().color = new Color(0.55f, 0.12f, 0.12f, 0.95f);
    }

    private void QuitApp()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ─── Structure browser (runtime CIF library) ──────────────────────────────

    // Searchable, scrollable two-column grid — comfortably handles hundreds of structures.
    private const float BrowserW = 560f, BrowserH = 540f;
    private RectTransform _browserContent, _browserViewport;
    private ScrollRect _browserScroll;
    private InputField _browserSearch;
    private Text _browserCount;
    private List<(string name, string path)> _libraryCache = new();

    private void BuildBrowserPanel(Transform canvas, Font font)
    {
        _browserPanel = MakePanel(canvas, new Vector2(BrowserW, BrowserH),
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero);
        var bg = _browserPanel.GetComponent<Image>();
        if (bg != null) bg.color = new Color(0.05f, 0.05f, 0.05f, 0.96f);

        var title = AddLabel(_browserPanel, font, "Load structure", new Vector2(14, -10));
        title.fontSize = 16; title.fontStyle = FontStyle.Bold;
        title.GetComponent<RectTransform>().sizeDelta = new Vector2(200, 24);

        _browserCount = AddLabel(_browserPanel, font, "", new Vector2(214, -10));
        _browserCount.color = new Color(1f, 1f, 1f, 0.55f);
        _browserCount.alignment = TextAnchor.MiddleRight;
        _browserCount.GetComponent<RectTransform>().sizeDelta = new Vector2(BrowserW - 270, 24);

        AddButton(_browserPanel, font, "X", new Vector2(BrowserW - 40, -9), new Vector2(26, 24), ToggleBrowser);

        _browserSearch = AddSearchField(_browserPanel, font, new Vector2(14, -42), new Vector2(BrowserW - 28, 30));
        _browserSearch.onValueChanged.AddListener(_ =>
        {
            PopulateBrowser();
            if (_browserScroll != null) _browserScroll.verticalNormalizedPosition = 1f;
        });

        // Scroll view: viewport (clipped) → content (grid, grows to fit) + slim scrollbar.
        var scrollGO = new GameObject("Scroll", typeof(RectTransform));
        scrollGO.transform.SetParent(_browserPanel.transform, false);
        var srt = scrollGO.GetComponent<RectTransform>();
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
        srt.offsetMin = new Vector2(14, 14);
        srt.offsetMax = new Vector2(-28, -82);

        var vp = new GameObject("Viewport", typeof(RectTransform));
        vp.transform.SetParent(scrollGO.transform, false);
        _browserViewport = vp.GetComponent<RectTransform>();
        _browserViewport.anchorMin = Vector2.zero; _browserViewport.anchorMax = Vector2.one;
        _browserViewport.offsetMin = Vector2.zero; _browserViewport.offsetMax = Vector2.zero;
        _browserViewport.pivot = new Vector2(0f, 1f);
        vp.AddComponent<RectMask2D>();
        vp.AddComponent<Image>().color = Color.clear;   // catches wheel/drag between buttons

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(vp.transform, false);
        _browserContent = content.GetComponent<RectTransform>();
        _browserContent.anchorMin = new Vector2(0f, 1f); _browserContent.anchorMax = new Vector2(1f, 1f);
        _browserContent.pivot = new Vector2(0.5f, 1f);
        _browserContent.offsetMin = Vector2.zero; _browserContent.offsetMax = Vector2.zero;
        var grid = content.AddComponent<GridLayoutGroup>();
        float innerW = BrowserW - 14f - 28f;
        grid.cellSize = new Vector2((innerW - 8f) * 0.5f, 30f);
        grid.spacing = new Vector2(8f, 6f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 2;
        grid.childAlignment = TextAnchor.UpperLeft;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _browserScroll = scrollGO.AddComponent<ScrollRect>();
        _browserScroll.viewport = _browserViewport;
        _browserScroll.content = _browserContent;
        _browserScroll.horizontal = false;
        _browserScroll.vertical = true;
        _browserScroll.movementType = ScrollRect.MovementType.Clamped;
        _browserScroll.scrollSensitivity = 30f;
        _browserScroll.verticalScrollbar = MakeScrollbar(_browserPanel.transform);
        _browserScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        _browserPanel.SetActive(false);
    }

    private void ToggleBrowser()
    {
        _browserOpen = !_browserOpen;
        _browserPanel.SetActive(_browserOpen);
        if (_browserOpen)
        {
            _libraryCache = ScanCifs();
            PopulateBrowser();
            ScrollBrowserToCurrent();
        }
        else if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);   // drop search focus so hotkeys work
        }
    }

    private void PopulateBrowser()
    {
        if (_browserContent == null) return;
        for (int i = _browserContent.childCount - 1; i >= 0; i--)
        {
            var child = _browserContent.GetChild(i).gameObject;
            child.SetActive(false);   // excluded from layout immediately (Destroy is deferred)
            Destroy(child);
        }

        string filter = _browserSearch != null ? _browserSearch.text.Trim() : "";
        string current = _viewer.CurrentStructureName;
        int shown = 0;
        foreach (var f in _libraryCache)
        {
            if (!MatchesFilter(f.name, filter)) continue;

            bool isCurrent = string.Equals(f.name, current, System.StringComparison.OrdinalIgnoreCase);
            string capturedPath = f.path;
            var b = AddButton(_browserContent.gameObject, _uiFont, (isCurrent ? "» " : "") + PrettyName(f.name),
                Vector2.zero, new Vector2(100f, 30f), () =>
                {
                    _viewer.LoadCifFromPath(capturedPath);
                    PopulateBrowser();   // move the highlight
                });
            var t = b.GetComponentInChildren<Text>();
            t.fontSize = 14;
            t.alignment = TextAnchor.MiddleLeft;
            t.GetComponent<RectTransform>().offsetMin = new Vector2(10f, 0f);
            if (isCurrent)
            {
                b.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.1f, 0.95f);
                t.color = new Color(1f, 0.85f, 0.1f);
            }
            shown++;
        }

        if (_libraryCache.Count == 0)
            _browserCount.text = "No CIF files found — add them to StreamingAssets/CIF";
        else
            _browserCount.text = filter.Length > 0
                ? $"{shown} of {_libraryCache.Count} match"
                : $"{_libraryCache.Count} structures";
    }

    // Bring the structure on display into view when the browser opens.
    private void ScrollBrowserToCurrent()
    {
        if (_browserContent == null || _browserViewport == null) return;
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(_browserContent);

        string current = _viewer.CurrentStructureName;
        int index = 0, found = -1;
        string filter = _browserSearch != null ? _browserSearch.text.Trim() : "";
        foreach (var f in _libraryCache)
        {
            if (!MatchesFilter(f.name, filter)) continue;
            if (string.Equals(f.name, current, System.StringComparison.OrdinalIgnoreCase)) { found = index; break; }
            index++;
        }
        float contentH = _browserContent.rect.height, viewH = _browserViewport.rect.height;
        if (found < 0 || contentH <= viewH) { _browserContent.anchoredPosition = Vector2.zero; return; }
        float rowY = (found / 2) * 36f;
        _browserContent.anchoredPosition = new Vector2(0f, Mathf.Clamp(rowY - viewH * 0.4f, 0f, contentH - viewH));
    }

    private static InputField AddSearchField(GameObject parent, Font font, Vector2 pos, Vector2 size)
    {
        var go = new GameObject("Search", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = size; rt.anchoredPosition = pos;
        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.08f);
        var field = go.AddComponent<InputField>();

        Text Child(string name, string content, Color color, FontStyle style)
        {
            var c = new GameObject(name, typeof(RectTransform));
            c.transform.SetParent(go.transform, false);
            var crt = c.GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
            crt.offsetMin = new Vector2(10, 2); crt.offsetMax = new Vector2(-10, -2);
            var t = c.AddComponent<Text>();
            if (font != null) t.font = font;
            t.fontSize = 14; t.color = color; t.fontStyle = style; t.text = content;
            t.alignment = TextAnchor.MiddleLeft; t.supportRichText = false;
            return t;
        }

        field.placeholder   = Child("Placeholder", "Search structures…", new Color(1f, 1f, 1f, 0.4f), FontStyle.Italic);
        field.textComponent = Child("Text", "", Color.white, FontStyle.Normal);
        field.targetGraphic = img;
        field.lineType = InputField.LineType.SingleLine;
        return field;
    }

    private static Scrollbar MakeScrollbar(Transform parent)
    {
        var go = new GameObject("Scrollbar", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(1f, 0.5f);
        rt.offsetMin = new Vector2(-24f, 14f); rt.offsetMax = new Vector2(-14f, -82f);
        go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);

        var area = new GameObject("Sliding Area", typeof(RectTransform));
        area.transform.SetParent(go.transform, false);
        var art = area.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
        art.offsetMin = Vector2.zero; art.offsetMax = Vector2.zero;

        var handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(area.transform, false);
        var hrt = handle.GetComponent<RectTransform>();
        hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one;
        hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
        var himg = handle.AddComponent<Image>();
        himg.color = new Color(0.4f, 0.75f, 1f, 0.85f);

        var sb = go.AddComponent<Scrollbar>();
        sb.handleRect = hrt;
        sb.targetGraphic = himg;
        sb.direction = Scrollbar.Direction.BottomToTop;
        return sb;
    }

    private static bool MatchesFilter(string name, string filter) =>
        filter.Length == 0 ||
        name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
        PrettyName(name).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Display name for a CIF file: "quartz" → "Quartz", "copper-fcc" → "Copper (fcc)".</summary>
    public static string PrettyName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        string s = raw.Replace('_', ' ').Trim();
        int dash = s.LastIndexOf('-');
        if (dash > 0 && dash < s.Length - 1 && s.Length - dash - 1 <= 4 && s.IndexOf('-') == dash)
            s = s.Substring(0, dash) + " (" + s.Substring(dash + 1) + ")";
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>True while the user is typing in a UI text field — hotkeys must stand down.</summary>
    public static bool IsTypingInUI()
    {
        var es = EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        if (go == null) return false;
        var field = go.GetComponent<InputField>();
        return field != null && field.isFocused;
    }

    public List<(string name, string path)> ScanCifs()
    {
        var found = new List<(string, string)>();
        var seenNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Scan a folder (and its subfolders) for CIF files, de-duplicating by file name so the
        // same structure shipped in StreamingAssets and dropped into persistentData lists once.
        void Scan(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return; }
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".cif" && ext != ".txt") continue;
                if (ext == ".txt" && !LooksLikeCif(f)) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (!seenNames.Add(name)) continue;
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }
                found.Add((name, full));
            }
        }

        // StreamingAssets ships with the build and works identically in the editor and a player.
        Scan(Application.streamingAssetsPath);
        Scan(Application.persistentDataPath);
        if (extraCifFolders != null)
            foreach (var d in extraCifFolders) Scan(d);
#if UNITY_EDITOR
        // Editor convenience: CIFs dropped into Assets/GameObjects show up immediately in Play
        // mode. Builds get them via CifLibrarySync, which copies them into StreamingAssets.
        Scan(Path.Combine(Application.dataPath, "GameObjects"));
#endif

        found.Sort((a, b) => string.Compare(a.Item1, b.Item1, System.StringComparison.OrdinalIgnoreCase));
        return found;
    }

    // Lightweight check that a .txt file is actually a CIF (avoids listing arbitrary text files).
    private static bool LooksLikeCif(string path)
    {
        try
        {
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (line.IndexOf("_cell_length", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("_atom_site",   System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("loop_",        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (++n > 80) break;
            }
        }
        catch { }
        return false;
    }

    // ─── Miller plane panel (hkl steppers) ────────────────────────────────────

    private void BuildMillerPanel(GameObject parent, Font font)
    {
        var title = AddLabel(parent, font, "(hkl) lattice plane", new Vector2(10, -8));
        title.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 16);

        AddToggle(parent, font, "Show", _millerShow, new Vector2(200, -8),
            v => { _millerShow = v; ApplyMiller(); });

        AddStepper(parent, font, "h", new Vector2(10, -36), () => _viewer.millerH, v => _viewer.millerH = v, 1, -6, 6, ApplyMiller);
        AddStepper(parent, font, "k", new Vector2(10, -62), () => _viewer.millerK, v => _viewer.millerK = v, 1, -6, 6, ApplyMiller);
        AddStepper(parent, font, "l", new Vector2(10, -88), () => _viewer.millerL, v => _viewer.millerL = v, 1, -6, 6, ApplyMiller);

        _millerText = AddLabel(parent, font, $"({_viewer.millerH} {_viewer.millerK} {_viewer.millerL})", new Vector2(160, -58));
        _millerText.GetComponent<RectTransform>().sizeDelta = new Vector2(130, 24);
        _millerText.fontSize = 18;
    }

    private void ApplyMiller()
    {
        _viewer.SetMillerPlane(_viewer.millerH, _viewer.millerK, _viewer.millerL, _millerShow);
        if (_millerText != null)
            _millerText.text = $"({_viewer.millerH} {_viewer.millerK} {_viewer.millerL})";
    }

    // ─── Pressure panel (2nd-order Birch–Murnaghan compression) ───────────────

    private void BuildPressurePanel(GameObject parent, Font font)
    {
        AddLabel(parent, font, "Pressure (GPa)", new Vector2(10, -8))
            .GetComponent<RectTransform>().sizeDelta = new Vector2(150, 16);
        _pressureVal = AddLabel(parent, font, $"{_viewer.pressureGPa:F1}", new Vector2(210, -8));
        _pressureVal.GetComponent<RectTransform>().sizeDelta = new Vector2(70, 16);

        _pressureSlider = AddSlider(parent, font, new Vector2(10, -34), 0f, 136f, _viewer.pressureGPa, out _);
        _pressureSlider.GetComponent<RectTransform>().sizeDelta = new Vector2(270, 16);
        _pressureSlider.onValueChanged.AddListener(v =>
        {
            if (_pressureVal != null) _pressureVal.text = $"{v:F1}";
            RefreshPressureReadout(v);          // live estimate while dragging
        });
        AddSliderCommit(_pressureSlider, () => _viewer.SetPressure(_pressureSlider.value));

        // Bulk modulus K0 stepper (per-mineral stiffness).
        AddStepper(parent, font, "K0", new Vector2(10, -62),
            () => Mathf.RoundToInt(_viewer.bulkModulusK0),
            v => _viewer.bulkModulusK0 = v,
            5, 5, 500, () => { _viewer.SetBulkModulus(_viewer.bulkModulusK0); RefreshPressureReadout(_pressureSlider.value); });
        AddLabel(parent, font, "GPa", new Vector2(128, -62))
            .GetComponent<RectTransform>().sizeDelta = new Vector2(40, 16);

        _pressureInfo = AddLabel(parent, font, "", new Vector2(10, -88));
        _pressureInfo.GetComponent<RectTransform>().sizeDelta = new Vector2(280, 16);
        RefreshPressureReadout(_viewer.pressureGPa);
    }

    // Show the compression implied by a given pressure (bond = linear, cell = volumetric).
    private void RefreshPressureReadout(float gpa)
    {
        if (_pressureInfo == null) return;
        float s = _viewer.PredictLinearCompression(gpa);
        float bondPct = (1f - s) * 100f;
        float cellPct = (1f - s * s * s) * 100f;
        _pressureInfo.text = $"<color=#aaccff>bonds −{bondPct:F1}%   cell −{cellPct:F1}%</color>";
    }

    // ─── Element legend ───────────────────────────────────────────────────────

    private void RebuildLegend(CrystalCIFViewer.CrystalModel model)
    {
        if (_legendPanel == null) return;

        for (int i = _legendPanel.transform.childCount - 1; i >= 0; i--)
            Destroy(_legendPanel.transform.GetChild(i).gameObject);

        var seen  = new HashSet<string>();
        var elems = new List<string>();
        foreach (var a in model.Atoms)
            if (seen.Add(a.Element)) elems.Add(a.Element);

        AddLabel(_legendPanel, _uiFont, "Elements", new Vector2(8, -6))
            .GetComponent<RectTransform>().sizeDelta = new Vector2(140, 16);

        float y = -26f;
        foreach (var e in elems)
        {
            var sw = new GameObject("Swatch", typeof(RectTransform));
            sw.transform.SetParent(_legendPanel.transform, false);
            sw.AddComponent<Image>().color = CrystalCIFViewer.ElementStylings.GetColor(e);
            var srt = sw.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 1); srt.anchorMax = new Vector2(0, 1); srt.pivot = new Vector2(0, 1);
            srt.sizeDelta = new Vector2(14, 14); srt.anchoredPosition = new Vector2(10, y - 1);

            var lbl = AddLabel(_legendPanel, _uiFont, e, new Vector2(30, y));
            lbl.GetComponent<RectTransform>().sizeDelta = new Vector2(110, 16);

            y -= 20f;
        }

        _legendPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(160, 30 + elems.Count * 20 + 6);
    }

    // ─── Controls panel (toggles + bond-length sliders) ───────────────────────

    private void BuildControls(GameObject parent, Font font)
    {
        AddToggle(parent, font, "Show cell", _viewer.showUnitCell,     new Vector2(10, -8),
            v => _viewer.SetUnitCellVisible(v));
        AddToggle(parent, font, "Show axes", _viewer.showAxisIndicator, new Vector2(150, -8),
            v => _viewer.SetAxisVisible(v));

        AddLabel(parent, font, "Bond min", new Vector2(10, -40));
        AddLabel(parent, font, "Bond max", new Vector2(10, -76));

        _minSlider = AddSlider(parent, font, new Vector2(86, -40), 0f, 4f, _viewer.bondMinAngstrom, out _minVal);
        _maxSlider = AddSlider(parent, font, new Vector2(86, -76), 0f, 4f, _viewer.bondMaxAngstrom, out _maxVal);

        _minVal.text = $"{_viewer.bondMinAngstrom:F2} Å";
        _maxVal.text = $"{_viewer.bondMaxAngstrom:F2} Å";

        _minSlider.onValueChanged.AddListener(v => _minVal.text = $"{v:F2} Å");
        _maxSlider.onValueChanged.AddListener(v => _maxVal.text = $"{v:F2} Å");

        // Rebuild only when the slider is released, not on every drag frame.
        AddSliderCommit(_minSlider, () => _viewer.SetBondRange(_minSlider.value, _maxSlider.value));
        AddSliderCommit(_maxSlider, () => _viewer.SetBondRange(_minSlider.value, _maxSlider.value));
    }

    // Sync the sliders to the viewer's current bond range (e.g. CIF-derived defaults on load).
    private void RefreshBondSliders()
    {
        if (_minSlider != null) _minSlider.SetValueWithoutNotify(Mathf.Clamp(_viewer.bondMinAngstrom, 0f, 4f));
        if (_maxSlider != null) _maxSlider.SetValueWithoutNotify(Mathf.Clamp(_viewer.bondMaxAngstrom, 0f, 4f));
        if (_minVal != null) _minVal.text = $"{_viewer.bondMinAngstrom:F2} Å";
        if (_maxVal != null) _maxVal.text = $"{_viewer.bondMaxAngstrom:F2} Å";
    }

    private static void AddSliderCommit(Slider slider, System.Action onCommit)
    {
        var trigger = slider.gameObject.AddComponent<EventTrigger>();
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
        entry.callback.AddListener(_ => onCommit());
        trigger.triggers.Add(entry);
    }

    private static Text AddLabel(GameObject parent, Font font, string text, Vector2 pos)
    {
        var go = new GameObject("Lbl", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var t = go.AddComponent<Text>();
        if (font != null) t.font = font;
        t.fontSize = 13; t.color = Color.white; t.alignment = TextAnchor.MiddleLeft; t.text = text;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(80, 16); rt.anchoredPosition = pos;
        return t;
    }

    private Button AddButton(GameObject parent, Font font, string label, Vector2 pos, Vector2 size, System.Action onClick)
    {
        var go = new GameObject("Btn", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.22f, 0.22f, 0.22f, 0.95f);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = size; rt.anchoredPosition = pos;

        var t = AddText(go, font, 16, TextAnchor.MiddleCenter);
        t.text = label;
        // Fill the whole button and never clip — otherwise small buttons (the -/+ steppers)
        // truncate their glyph and render as blank boxes.
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow   = VerticalWrapMode.Overflow;
        var trt = t.GetComponent<RectTransform>();
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() => onClick());
        return btn;
    }

    // A −/value/+ integer stepper (used for Miller indices and the bulk modulus).
    private void AddStepper(GameObject parent, Font font, string axis, Vector2 pos,
                            System.Func<int> get, System.Action<int> set,
                            int step, int min, int max, System.Action onChanged)
    {
        AddLabel(parent, font, axis, new Vector2(pos.x, pos.y))
            .GetComponent<RectTransform>().sizeDelta = new Vector2(20, 16);

        Text val = null;
        AddButton(parent, font, "-", new Vector2(pos.x + 20, pos.y), new Vector2(24, 20), () =>
        {
            set(Mathf.Clamp(get() - step, min, max));
            if (val != null) val.text = get().ToString();
            onChanged();
        });

        val = AddLabel(parent, font, get().ToString(), new Vector2(pos.x + 48, pos.y));
        val.alignment = TextAnchor.MiddleCenter;
        val.GetComponent<RectTransform>().sizeDelta = new Vector2(40, 18);

        AddButton(parent, font, "+", new Vector2(pos.x + 90, pos.y), new Vector2(24, 20), () =>
        {
            set(Mathf.Clamp(get() + step, min, max));
            if (val != null) val.text = get().ToString();
            onChanged();
        });
    }

    private Toggle AddToggle(GameObject parent, Font font, string label, bool isOn, Vector2 pos, System.Action<bool> onChange)
    {
        var go = new GameObject("Toggle", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(135, 18); rt.anchoredPosition = pos;
        var toggle = go.AddComponent<Toggle>();

        var box = new GameObject("Box", typeof(RectTransform));
        box.transform.SetParent(go.transform, false);
        var boxImg = box.AddComponent<Image>();
        boxImg.color = new Color(0.22f, 0.22f, 0.22f, 1f);
        var brt = box.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0, 0.5f); brt.anchorMax = new Vector2(0, 0.5f); brt.pivot = new Vector2(0, 0.5f);
        brt.sizeDelta = new Vector2(16, 16); brt.anchoredPosition = Vector2.zero;

        var chk = new GameObject("Check", typeof(RectTransform));
        chk.transform.SetParent(box.transform, false);
        var chkImg = chk.AddComponent<Image>();
        chkImg.color = new Color(1f, 0.85f, 0.1f, 1f);
        var crt = chk.GetComponent<RectTransform>();
        crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
        crt.offsetMin = new Vector2(3, 3); crt.offsetMax = new Vector2(-3, -3);

        var lblGO = new GameObject("Label", typeof(RectTransform));
        lblGO.transform.SetParent(go.transform, false);
        var lbl = lblGO.AddComponent<Text>();
        if (font != null) lbl.font = font;
        lbl.fontSize = 13; lbl.color = Color.white; lbl.alignment = TextAnchor.MiddleLeft; lbl.text = label;
        var lrt = lblGO.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
        lrt.offsetMin = new Vector2(20, 0); lrt.offsetMax = new Vector2(0, 0);

        toggle.targetGraphic = boxImg;
        toggle.graphic       = chkImg;
        toggle.isOn          = isOn;
        toggle.onValueChanged.AddListener(v => onChange(v));
        return toggle;
    }

    private Slider AddSlider(GameObject parent, Font font, Vector2 pos, float min, float max, float val, out Text valueText)
    {
        var go = new GameObject("Slider", typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(140, 16); rt.anchoredPosition = pos;
        var slider = go.AddComponent<Slider>();

        var bg = new GameObject("Background", typeof(RectTransform));
        bg.transform.SetParent(go.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.25f, 0.25f, 0.25f, 1f);
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0, 0.25f); bgrt.anchorMax = new Vector2(1, 0.75f);
        bgrt.offsetMin = Vector2.zero; bgrt.offsetMax = Vector2.zero;

        var fa = new GameObject("Fill Area", typeof(RectTransform));
        fa.transform.SetParent(go.transform, false);
        var fart = fa.GetComponent<RectTransform>();
        fart.anchorMin = new Vector2(0, 0.25f); fart.anchorMax = new Vector2(1, 0.75f);
        fart.offsetMin = Vector2.zero; fart.offsetMax = Vector2.zero;
        var fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(fa.transform, false);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.4f, 0.55f, 0.9f, 1f);
        var fillrt = fill.GetComponent<RectTransform>();
        fillrt.anchorMin = new Vector2(0, 0); fillrt.anchorMax = new Vector2(0, 1);
        fillrt.sizeDelta = new Vector2(10, 0);

        var ha = new GameObject("Handle Slide Area", typeof(RectTransform));
        ha.transform.SetParent(go.transform, false);
        var hart = ha.GetComponent<RectTransform>();
        hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one;
        hart.offsetMin = Vector2.zero; hart.offsetMax = Vector2.zero;
        var handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(ha.transform, false);
        var handleImg = handle.AddComponent<Image>();
        handleImg.color = Color.white;
        var hrt = handle.GetComponent<RectTransform>();
        hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(0, 1);
        hrt.sizeDelta = new Vector2(12, 0);

        slider.fillRect      = fillrt;
        slider.handleRect    = hrt;
        slider.targetGraphic = handleImg;
        slider.direction     = Slider.Direction.LeftToRight;
        slider.minValue = min; slider.maxValue = max; slider.value = val;

        var vtGO = new GameObject("Value", typeof(RectTransform));
        vtGO.transform.SetParent(parent.transform, false);
        var vt = vtGO.AddComponent<Text>();
        if (font != null) vt.font = font;
        vt.fontSize = 13; vt.color = Color.white; vt.alignment = TextAnchor.MiddleLeft;
        var vrt = vtGO.GetComponent<RectTransform>();
        vrt.anchorMin = new Vector2(0, 1); vrt.anchorMax = new Vector2(0, 1); vrt.pivot = new Vector2(0, 1);
        vrt.sizeDelta = new Vector2(60, 16); vrt.anchoredPosition = new Vector2(pos.x + 150, pos.y);
        valueText = vt;

        return slider;
    }

    private static readonly string[] StyleNames = { "Ball+Stick", "Space-fill", "Stick", "Wireframe", "Polyhedral" };

    private void BuildStyleBar(GameObject parent, Font font)
    {
        float btnW = 108f, btnH = 28f, spacing = 4f;
        float totalW = StyleNames.Length * btnW + (StyleNames.Length - 1) * spacing;
        float startX = -totalW * 0.5f + btnW * 0.5f;

        for (int i = 0; i < StyleNames.Length; i++)
        {
            var btn = new GameObject($"StyleBtn_{i}");
            btn.transform.SetParent(parent.transform, false);
            var img = btn.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
            var rt = btn.GetComponent<RectTransform>();
            rt.sizeDelta        = new Vector2(btnW, btnH);
            rt.anchoredPosition = new Vector2(startX + i * (btnW + spacing), 0);

            var lbl = AddText(btn, font, 12, TextAnchor.MiddleCenter);
            lbl.text = StyleNames[i];
            _styleLabels[i] = lbl;

            int captured = i;
            btn.AddComponent<UnityEngine.UI.Button>().onClick.AddListener(() =>
            {
                _viewer.SetDisplayStyle((DisplayStyle)captured);
                RefreshStyleBar();
            });
        }
    }

    private void RefreshStyleBar()
    {
        for (int i = 0; i < _styleLabels.Length; i++)
        {
            var lbl = _styleLabels[i];
            if (lbl == null) continue;
            bool active = (int)_viewer.displayStyle == i;
            lbl.text  = active ? $"<b>[{StyleNames[i]}]</b>" : StyleNames[i];
            lbl.color = active ? new Color(1f, 0.85f, 0.1f) : new Color(0.75f, 0.75f, 0.75f);
            var img = lbl.transform.parent.GetComponent<UnityEngine.UI.Image>();
            if (img) img.color = active ? new Color(0.25f, 0.25f, 0.1f, 0.9f) : new Color(0.15f, 0.15f, 0.15f, 0.85f);
        }
    }

    private static GameObject MakePanel(Transform parent, Vector2 size,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPos)
    {
        var go  = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.04f, 0.04f, 0.04f, 0.72f);
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin        = anchorMin;
        rt.anchorMax        = anchorMax;
        rt.pivot            = pivot;
        rt.sizeDelta        = size;
        rt.anchoredPosition = anchoredPos;
        return go;
    }

    private static Text AddText(GameObject panel, Font font, int fontSize, TextAnchor anchor)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(panel.transform, false);
        var t = go.AddComponent<Text>();
        if (font != null) t.font = font;
        t.fontSize        = fontSize;
        t.color           = Color.white;
        t.supportRichText = true;
        t.alignment       = anchor;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(6, 4);
        rt.offsetMax = new Vector2(-6, -4);
        return t;
    }

    private static Font GetUIFont()
    {
        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    // ─── Billboard — 3-D labels always face the camera ───────────────────────

    private class Billboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) transform.forward = cam.transform.forward;
        }
    }
}
