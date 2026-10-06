// CrystalCIFViewer.cs
// Unity 6.2+ — Crystal viewer with CIF parsing, symmetry expansion (covers all space groups via ops in CIF),
// replication, atom/bond rendering, optional culling of atoms not touching the base cell, and clean teardown.
//
// Usage:
// 1) Put this file in Assets/Scripts.
// 2) Add the component to an empty GameObject.
// 3) Assign a CIF TextAsset (or a path in editor), tweak options, click "Rebuild Now" or play.


using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public enum DisplayStyle { BallAndStick, SpaceFilling, Stick, Wireframe, Polyhedral }

[ExecuteAlways]
public class CrystalCIFViewer : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("Assign a CIF text file directly. If both are set, cifText takes precedence.")]
    public TextAsset cifText;
    [Tooltip("Or provide an absolute/relative path under Application.dataPath for editor use.")]
    public string cifPath = "";

    [Header("Build Options")]
    public bool buildOnStart = true;
    [Tooltip("Clear & rebuild in editor when values change.")]
    public bool liveRebuildInEditor = false;

    [Header("Replication (unit cells)")]
    [Min(1)] public int replicateX = 1;
    [Min(1)] public int replicateY = 1;
    [Min(1)] public int replicateZ = 1;

    [Header("Rendering")]
    [Tooltip("Uniform scale for atom spheres (multiplies covalent radius).")]
    [Range(0.3f, 2.0f)] public float atomScale = 0.9f;
    [Tooltip("Cylinder radius for bonds.")]
    [Range(0.01f, 0.2f)] public float bondRadius = 0.05f;
    [Tooltip("Max cutoff = (rA + rB) * this factor to create a bond.")]
    [Range(0.9f, 1.4f)] public float bondCutoffScale = 1.1f;
    [Tooltip("Minimum bond length in Å. Atom pairs closer than this are not bonded.")]
    [Range(0f, 4f)] public float bondMinAngstrom = 0f;
    [Tooltip("Maximum bond length in Å. Atom pairs farther than this are not bonded.")]
    [Range(0f, 4f)] public float bondMaxAngstrom = 2.0f;
    [Tooltip("Only attempt to create bonds for atoms whose fractional coords are in [0,1)^3 of the base cell (fewer bonds).")]
    public bool bondsOnlyWithinBaseCell = true;
    [Tooltip("Hide hydrogens (often reduces clutter).")]
    public bool hideHydrogen = false;

    [Header("Culling")]
    [Tooltip("If enabled, drops any atom whose sphere does not intersect the base unit cell [0,1)^3.")]
    public bool removeNonTouchingBaseCell = false;

    [Header("Unit cell & axes")]
    [Tooltip("Show the unit-cell wireframe box.")]
    public bool showUnitCell = true;
    [Tooltip("Show the a/b/c crystallographic axis indicator at the cell origin.")]
    public bool showAxisIndicator = true;

    [Header("Miller plane (hkl)")]
    public bool showMillerPlane = false;
    public int  millerH = 1;
    public int  millerK = 1;
    public int  millerL = 1;

    [Header("Pressure (2nd-order Birch–Murnaghan)")]
    [Tooltip("Applied pressure in GPa. Isotropically compresses the unit cell.")]
    public float pressureGPa = 0f;
    [Tooltip("Zero-pressure bulk modulus K0 in GPa (e.g. ~37 for quartz).")]
    public float bulkModulusK0 = 37f;

    [Header("Display Style")]
    public DisplayStyle displayStyle = DisplayStyle.BallAndStick;

    [Header("Debug")]
    public bool logParsing = false;

    // Internals
    private Transform atomsRoot;
    private Transform bondsRoot;
    private GameObject _unitCellGO;
    private GameObject _axisGO;
    private GameObject _millerGO;
    private bool _bondRangeUserSet = false; // true once the user drags a bond slider

    // VESTA-style bonding: a per-element-pair window (from VestaBondTable) for the pairs present
    // in the current structure. The max slider scales every window by (slider / _autoBondMax),
    // so a silicate keeps Si–O while never picking up O–O, however far the slider goes.
    private readonly Dictionary<(string, string), VestaBondTable.Spec> _pairSpecs = new();
    private readonly HashSet<string> _polyCentres = new(StringComparer.OrdinalIgnoreCase);
    private bool  _heuristicBonds;     // VESTA's table knows none of the pairs → covalent fallback
    private float _autoBondMax = 2f;   // default max-slider value for this structure
    public bool UsingVestaBondTable => !_heuristicBonds;

    // Track generated roots so we can clean up in both play/edit
    private readonly List<GameObject> _generated = new();

    // Exposed for CrystalInteraction
    public event Action<CrystalModel> OnCrystalBuilt;
    public CrystalModel LastBaseModel { get; private set; }

    // Style records — populated by BuildScene, consumed by ApplyStyle
    private struct AtomRecord { public Transform tf; public string element; public float covR; public float vdwR; }
    private struct BondRecord { public Transform tf; public float baseW; public float halfLen; }
    private readonly List<AtomRecord>         _atomRec   = new();
    private readonly List<BondRecord>         _bondRec   = new();
    private readonly Dictionary<int,List<int>> _bondGraph = new();

    // ----------------------------- Unity lifecycle -----------------------------

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (liveRebuildInEditor && !Application.isPlaying)
        {
            UnityEditor.EditorApplication.delayCall -= TryBuild;
            UnityEditor.EditorApplication.delayCall += TryBuild;
        }
#endif
    }

    private void Start()
    {
        if (buildOnStart) TryBuild();
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) ClearGenerated();
#endif
    }
    private void OnDestroy() => ClearGenerated();
    private void OnApplicationQuit() => ClearGenerated();

    // ----------------------------- Helpers: manage generated objects -----------------------------

    private GameObject MakeRoot(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        // Ensure edit-mode generated content doesn't persist in scene/prefab files
        go.hideFlags = HideFlags.DontSave | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        _generated.Add(go);
        return go;
    }

    // Like MakeRoot but NOT parented to the crystal transform — used for the camera-anchored
    // axis gizmo so it stays put on screen while the crystal pans/zooms.
    private GameObject MakeDetachedRoot(string name)
    {
        var go = new GameObject(name);
        go.hideFlags = HideFlags.DontSave | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        _generated.Add(go);
        return go;
    }

    // Work out which element pairs bond, VESTA-style. VESTA ignores the CIF's _geom_bond loop and
    // applies a fixed per-pair table (style.ini → VestaBondTable). Elements VESTA never bonds
    // (pure metals, alloys, diamond…) fall back to a covalent-radius estimate so ball-and-stick
    // still shows something.
    private void ComputeBondSpecs(CrystalModel model)
    {
        _pairSpecs.Clear();
        _polyCentres.Clear();

        var elems = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var at in model.Atoms)
            if (seen.Add(at.Element)) elems.Add(at.Element);

        float autoMax = 0f;
        for (int i = 0; i < elems.Count; i++)
        for (int j = i; j < elems.Count; j++)
        {
            if (!VestaBondTable.TryGet(elems[i], elems[j], out var spec)) continue;
            _pairSpecs[(spec.A1, spec.A2)] = spec;
            if (spec.Polyhedron) _polyCentres.Add(spec.A1);
            if (spec.Max > autoMax) autoMax = spec.Max;
        }

        _heuristicBonds = _pairSpecs.Count == 0;
        if (_heuristicBonds)
        {
            // (rA + rB) * bondCutoffScale over the pairs present; heteronuclear preferred so a
            // large self-pair (Na–Na, Ca–Ca) doesn't inflate the window.
            float Rad(string e) => ElementStylings.CovalentRadii.TryGetValue(e, out var v) ? v : 0.8f;
            float maxPair = 0f;
            if (elems.Count <= 1) maxPair = elems.Count == 1 ? 2f * Rad(elems[0]) : 1.6f;
            else
                for (int i = 0; i < elems.Count; i++)
                for (int j = i + 1; j < elems.Count; j++)
                    maxPair = Mathf.Max(maxPair, Rad(elems[i]) + Rad(elems[j]));
            autoMax = maxPair * bondCutoffScale;
            // Heuristic polyhedra: anions are vertices, everything else a centre.
            foreach (var e in elems) if (!PolyLigands.Contains(e)) _polyCentres.Add(e);
        }
        _autoBondMax = Mathf.Clamp(autoMax, 0.5f, 4f);
    }

    // Reset the sliders to this structure's VESTA default (min 0, max = longest pair window).
    private void ApplyDefaultBondRange()
    {
        bondMinAngstrom = 0f;
        bondMaxAngstrom = _autoBondMax;
    }

    /// <summary>The effective bond window (Å) for an element pair under the current slider
    /// settings. False → these two elements are never bonded (VESTA has no entry for them).</summary>
    public bool TryGetBondWindow(string elemA, string elemB, out float minA, out float maxA)
    {
        if (_heuristicBonds)
        {
            minA = bondMinAngstrom; maxA = bondMaxAngstrom;
            return true;
        }
        if (!_pairSpecs.TryGetValue((elemA, elemB), out var spec) &&
            !_pairSpecs.TryGetValue((elemB, elemA), out spec))
        { minA = maxA = 0f; return false; }

        float scale = _autoBondMax > 1e-3f ? bondMaxAngstrom / _autoBondMax : 1f;
        minA = Mathf.Max(spec.Min, bondMinAngstrom);
        maxA = spec.Max * scale;
        return maxA > minA;
    }

    // Is `vertexElem` a polyhedron vertex around a `centreElem` centre, and within what window?
    private bool TryGetPolyWindow(string centreElem, string vertexElem, out float minA, out float maxA)
    {
        if (_heuristicBonds)
        {
            minA = bondMinAngstrom; maxA = bondMaxAngstrom;
            return !PolyLigands.Contains(centreElem) && PolyLigands.Contains(vertexElem);
        }
        if (!_pairSpecs.TryGetValue((centreElem, vertexElem), out var spec) || !spec.Polyhedron)
        { minA = maxA = 0f; return false; }
        float scale = _autoBondMax > 1e-3f ? bondMaxAngstrom / _autoBondMax : 1f;
        minA = Mathf.Max(spec.Min, bondMinAngstrom);
        maxA = spec.Max * scale;
        return maxA > minA;
    }

    private void ClearGenerated()
    {
        for (int i = _generated.Count - 1; i >= 0; i--)
        {
            var go = _generated[i];
            if (go == null) continue;

#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(go);
            else Destroy(go);
#else
            Destroy(go);
#endif
        }
        _generated.Clear();
        atomsRoot = null;
        bondsRoot = null;
        _unitCellGO = null;
        _axisGO = null;
        _millerGO = null;
        _atomRec.Clear();
        _bondRec.Clear();
        _bondGraph.Clear();
    }

    // ----------------------------- Public entrypoint -----------------------------

    [ContextMenu("Rebuild Now")]
    public void TryBuild()
    {
        string raw = null;
        if (cifText != null) raw = cifText.text;
        else if (!string.IsNullOrWhiteSpace(cifPath))
        {
            string path = cifPath;
            if (!Path.IsPathRooted(path)) path = Path.Combine(Application.dataPath, path);
            try { if (File.Exists(path)) raw = File.ReadAllText(path); }
            catch (Exception e) { Debug.LogError($"[CrystalCIFViewer] Failed to read '{path}': {e.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            Debug.LogWarning("[CrystalCIFViewer] No CIF input provided.");
            return;
        }

        // Parse -> model
        var model = CifReader.Parse(raw, logParsing);
        if (model == null || model.Atoms.Count == 0)
        {
            Debug.LogWarning("[CrystalCIFViewer] CIF parsed but no atoms found.");
            return;
        }

        // Apply pressure (isotropic Birch–Murnaghan compression) to the cell before building.
        ApplyPressureToMetrics(model);

        // Default bond-length window comes from the CIF (or covalent radii) until the user
        // overrides it with the sliders.
        ComputeBondSpecs(model);
        if (!_bondRangeUserSet)
            ApplyDefaultBondRange();

        // Expand symmetry (covers all space groups present in the CIF)
        var expanded = SymmetryApplier.Expand(model);

        // Replicate
        var replicated = Replicator.Replicate(expanded, replicateX, replicateY, replicateZ);

        // (Re)build scene
        BuildScene(model, replicated);
    }

    /// <summary>Absolute path of the CIF currently displayed (empty when an assigned TextAsset is used).</summary>
    public string CurrentCifPath => cifPath;

    /// <summary>File name (no extension) of the structure on display — works for both the
    /// startup TextAsset and anything loaded from the library. Used to highlight the list.</summary>
    public string CurrentStructureName =>
        cifText != null ? cifText.name
        : string.IsNullOrEmpty(cifPath) ? "" : Path.GetFileNameWithoutExtension(cifPath);

    /// <summary>Load and display a CIF from an arbitrary file path at runtime.</summary>
    public bool LoadCifFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Debug.LogWarning($"[CrystalCIFViewer] CIF not found: {path}");
            return false;
        }
        cifText = null;                 // a file path now drives the build
        cifPath = path;
        _bondRangeUserSet = false;      // new structure → recompute the default bond window
        TryBuild();
        return LastBaseModel != null;
    }

    // ----------------------------- Scene building -----------------------------

    private void BuildScene(CrystalModel baseModel, CrystalModel replicated)
    {
        // Wipe old build
        ClearGenerated();

        // Recreate roots that won't be saved with the scene
        atomsRoot = MakeRoot("Atoms").transform;
        bondsRoot = MakeRoot("Bonds").transform;

        // Cache element styles
        var colors = ElementStylings.DefaultColors;
        var radii  = ElementStylings.CovalentRadii;

        // Build a working list of atoms, optionally culling those that don't touch the base cell
        var workingAtoms = new List<Atom>(replicated.Atoms);

        // Defensive: wrap every atom into [0,1)^3 and deduplicate.
        // Guards against any symmetry-expansion float drift that leaves atoms outside the cell.
        // Only for the base cell — replication intentionally shifts atoms to [0,n).
        if (replicateX == 1 && replicateY == 1 && replicateZ == 1)
        {
            var seenW = new HashSet<(string, int, int, int)>();
            var cleanList = new List<Atom>(workingAtoms.Count);
            foreach (var a in workingAtoms)
            {
                var f = new Vector3(a.Frac.x - Mathf.Floor(a.Frac.x),
                                    a.Frac.y - Mathf.Floor(a.Frac.y),
                                    a.Frac.z - Mathf.Floor(a.Frac.z));
                if (!seenW.Add((a.Element,
                        (int)Mathf.Round(f.x * 10000f),
                        (int)Mathf.Round(f.y * 10000f),
                        (int)Mathf.Round(f.z * 10000f)))) continue;
                cleanList.Add(new Atom
                {
                    Label     = a.Label,
                    Element   = a.Element,
                    Frac      = f,
                    Cartesian = baseModel.Metrics.FracToCartesianUnity(f),
                    Occupancy = a.Occupancy,
                });
            }
            workingAtoms = cleanList;
        }

        if (removeNonTouchingBaseCell)
        {
            var filtered = new List<Atom>(workingAtoms.Count);
            foreach (var a in workingAtoms)
            {
                if (hideHydrogen && (a.Element == "H" || a.Element == "D" || a.Element == "T")) continue;

                float rA = radii.TryGetValue(a.Element, out float rrA) ? rrA : 0.8f; // Å
                float rUnity = rA * atomScale * VisualizationUnits.AngstromToUnity;

                if (AtomTouchesBaseCell(a.Cartesian, rUnity, baseModel.Metrics))
                    filtered.Add(a);
            }
            workingAtoms = filtered;
        }


        // Detect joint-occupancy sites: two atoms of different elements sharing one position.
        // The primary atom is drawn as a bi-colour sphere; the duplicate is skipped.
        var siteMap       = new Dictionary<(int, int, int), int>();   // posKey -> primary index
        var secondElement = new Dictionary<int, string>();            // primary index -> 2nd element
        var secondOcc     = new Dictionary<int, float>();             // primary index -> 2nd occupancy
        var skipAtom      = new HashSet<int>();                       // indices folded into a primary
        for (int i = 0; i < workingAtoms.Count; i++)
        {
            var a = workingAtoms[i];
            var k = ((int)Mathf.Round(a.Frac.x * 1000f),
                     (int)Mathf.Round(a.Frac.y * 1000f),
                     (int)Mathf.Round(a.Frac.z * 1000f));
            if (siteMap.TryGetValue(k, out int prim))
            {
                if (workingAtoms[prim].Element != a.Element && !secondElement.ContainsKey(prim))
                {
                    secondElement[prim] = a.Element;
                    secondOcc[prim]     = a.Occupancy;
                }
                skipAtom.Add(i);
            }
            else siteMap[k] = i;
        }

        // Build atoms; collect base-cell mask indices in the same pass to avoid a second loop.
        var atomGO = new List<(GameObject go, string element, Vector3 pos, float radius)>();
        HashSet<int> maskBaseCell = bondsOnlyWithinBaseCell ? new HashSet<int>() : null;

        for (int idx = 0; idx < workingAtoms.Count; idx++)
        {
            if (skipAtom.Contains(idx)) continue;
            var a = workingAtoms[idx];
            if (hideHydrogen && (a.Element == "H" || a.Element == "D" || a.Element == "T")) continue;

            float r = radii.TryGetValue(a.Element, out float rr) ? rr : 0.8f; // Å (fallback)
            float scale = r * atomScale;

            string elem2 = secondElement.TryGetValue(idx, out var e2) ? e2 : null;
            var go = CreateAtomSphere(a.Element, elem2, colors);
            go.name = elem2 != null ? $"{a.Element}/{elem2}_{atomGO.Count}" : $"{a.Element}_{atomGO.Count}";
            go.transform.SetParent(atomsRoot, false);
            // Local space so the model stays rigidly aligned with the cell box and axis gizmo
            // after the user has orbited/zoomed and then triggers a rebuild (e.g. bond slider).
            go.transform.localPosition = a.Cartesian;
            go.transform.localScale = Vector3.one * (scale * 2f * VisualizationUnits.AngstromToUnity);

            // Store crystallographic data for interaction picking.
            var atomInfo = go.AddComponent<AtomInfo>();
            atomInfo.AtomLabel       = elem2 != null ? $"{a.Label}/{elem2}" : a.Label;
            atomInfo.Element         = a.Element;
            atomInfo.FracCoords      = a.Frac;
            atomInfo.CartAngstrom    = a.Cartesian / VisualizationUnits.AngstromToUnity;
            atomInfo.Occupancy       = a.Occupancy;
            atomInfo.SecondElement   = elem2;
            atomInfo.SecondOccupancy = elem2 != null && secondOcc.TryGetValue(idx, out var so) ? so : 0f;

            // Keep the sphere collider as a plain static collider for raycast picking.
            // No Rigidbody is present so it has no effect on scene physics.

            if (maskBaseCell != null &&
                a.Frac.x >= 0f && a.Frac.x < 1f &&
                a.Frac.y >= 0f && a.Frac.y < 1f &&
                a.Frac.z >= 0f && a.Frac.z < 1f)
            {
                maskBaseCell.Add(atomGO.Count);
            }

            atomGO.Add((go, a.Element, a.Cartesian, r));
            float vdwR = ElementStylings.VdwRadii.TryGetValue(a.Element, out float vr) ? vr : r * 1.5f;
            _atomRec.Add(new AtomRecord { tf = go.transform, element = a.Element, covR = r, vdwR = vdwR });
        }

        // Build bonds (absolute Å distance range, set by the bond-length sliders).
        int n = atomGO.Count;

        // Grid cell sized to the bond search radius — avoids degenerate single-bucket behaviour on large cells.
        float hashCell = Mathf.Max(4f, bondMaxAngstrom) * VisualizationUnits.AngstromToUnity;
        var grid = new SpatialHash(cell: hashCell);
        for (int i = 0; i < n; i++) grid.Add(i, atomGO[i].pos);

        for (int i = 0; i < n; i++)
        {
            if (bondsOnlyWithinBaseCell && !maskBaseCell.Contains(i)) continue;

            var ai = atomGO[i];
            foreach (var j in grid.Candidates(ai.pos))
            {
                if (j <= i) continue; // undirected
                var aj = atomGO[j];

                float d     = Vector3.Distance(ai.pos, aj.pos);
                float dAng  = d / VisualizationUnits.AngstromToUnity; // Å
                if (dAng < 0.05f) continue; // skip overlapping/degenerate pairs
                if (!TryGetBondWindow(ai.element, aj.element, out float wMin, out float wMax)) continue;
                if (dAng < wMin || dAng > wMax) continue;

                // Create bi-coloured bond cylinder i-j (each half matches its atom)
                var bt = CreateBondCylinder(ai.pos, aj.pos, ai.element, aj.element, colors);
                _bondRec.Add(new BondRecord { tf = bt, baseW = bondRadius, halfLen = Vector3.Distance(ai.pos, aj.pos) * 0.5f });
                if (!_bondGraph.TryGetValue(i, out var li)) { li = new List<int>(); _bondGraph[i] = li; }
                if (!_bondGraph.TryGetValue(j, out var lj)) { lj = new List<int>(); _bondGraph[j] = lj; }
                li.Add(j); lj.Add(i);
            }
        }

        // Draw unit cell box and axis indicator for base model
        DrawUnitCellWireframe(baseModel.Metrics);
        DrawAxisIndicator(baseModel.Metrics);

        ApplyStyle();
        LastBaseModel = baseModel;
        RebuildMillerPlane();
        OnCrystalBuilt?.Invoke(baseModel);
    }

    // Generated geometry never casts/receives shadows — visually negligible for atoms and
    // bonds, and a large GPU saving on mobile/Quest where hundreds of spheres add up.
    private static void NoShadows(Renderer r)
    {
        if (r == null) return;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private Transform CreateBondCylinder(Vector3 a, Vector3 b, string elemA, string elemB,
                                         Dictionary<string, Color> colors)
    {
        var go = new GameObject("Bond");
        go.transform.SetParent(bondsRoot, false);

        Vector3 dir = b - a;
        float len = dir.magnitude;
        // Local space (bondsRoot sits at local identity under the crystal transform) so bonds
        // stay aligned with atoms/cell after a transform + rebuild. The mesh axis is +Y, which
        // points from a → b, so submesh 0 (lower half) is a's colour and submesh 1 (upper) is b's.
        go.transform.localPosition = (a + b) * 0.5f;
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir.normalized);
        go.transform.localScale    = new Vector3(bondRadius, len * 0.5f, bondRadius);

        go.AddComponent<MeshFilter>().sharedMesh = GetSplitCylinderMesh();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = new[]
        {
            ElementStylings.GetOrMakeMaterial(elemA, colors),
            ElementStylings.GetOrMakeMaterial(elemB, colors),
        };
        NoShadows(mr);
        return go.transform;
    }

    // A unit cylinder (radius 0.5, height 2 along Y) whose surface is split at the y=0 midpoint
    // into two submeshes — lower half (submesh 0) and upper half (submesh 1) — for bi-colour bonds.
    private static Mesh _splitCylinderMesh;
    private static Mesh GetSplitCylinderMesh()
    {
        if (_splitCylinderMesh != null) return _splitCylinderMesh;

        const int seg = 16;
        const float r = 0.5f;
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var triLo = new List<int>();
        var triHi = new List<int>();

        void AddTri(List<int> dst, int i0, int i1, int i2, Vector3 outwardRef)
        {
            Vector3 fn = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
            if (Vector3.Dot(fn, outwardRef) < 0f) { (i1, i2) = (i2, i1); }
            dst.Add(i0); dst.Add(i1); dst.Add(i2);
        }
        Vector3 RadialRef(int a, int b, int c)
        {
            var p = (verts[a] + verts[b] + verts[c]) / 3f;
            return new Vector3(p.x, 0f, p.z);
        }

        // Three side rings at y = -1, 0, +1 with outward radial normals.
        int s0 = verts.Count;
        for (int i = 0; i < seg; i++) { float t = (float)i / seg * 2f * Mathf.PI; var n = new Vector3(Mathf.Cos(t), 0, Mathf.Sin(t)); verts.Add(new Vector3(n.x * r, -1f, n.z * r)); norms.Add(n); }
        int s1 = verts.Count;
        for (int i = 0; i < seg; i++) { float t = (float)i / seg * 2f * Mathf.PI; var n = new Vector3(Mathf.Cos(t), 0, Mathf.Sin(t)); verts.Add(new Vector3(n.x * r,  0f, n.z * r)); norms.Add(n); }
        int s2 = verts.Count;
        for (int i = 0; i < seg; i++) { float t = (float)i / seg * 2f * Mathf.PI; var n = new Vector3(Mathf.Cos(t), 0, Mathf.Sin(t)); verts.Add(new Vector3(n.x * r,  1f, n.z * r)); norms.Add(n); }

        for (int i = 0; i < seg; i++)
        {
            int j = (i + 1) % seg;
            AddTri(triLo, s0 + i, s1 + i, s0 + j, RadialRef(s0 + i, s1 + i, s0 + j));
            AddTri(triLo, s0 + j, s1 + i, s1 + j, RadialRef(s0 + j, s1 + i, s1 + j));
            AddTri(triHi, s1 + i, s2 + i, s1 + j, RadialRef(s1 + i, s2 + i, s1 + j));
            AddTri(triHi, s1 + j, s2 + i, s2 + j, RadialRef(s1 + j, s2 + i, s2 + j));
        }

        // End caps.
        int cb = verts.Count; verts.Add(new Vector3(0, -1, 0)); norms.Add(Vector3.down);
        int rb = verts.Count;
        for (int i = 0; i < seg; i++) { float t = (float)i / seg * 2f * Mathf.PI; verts.Add(new Vector3(Mathf.Cos(t) * r, -1f, Mathf.Sin(t) * r)); norms.Add(Vector3.down); }
        for (int i = 0; i < seg; i++) AddTri(triLo, cb, rb + i, rb + (i + 1) % seg, Vector3.down);

        int ct = verts.Count; verts.Add(new Vector3(0, 1, 0)); norms.Add(Vector3.up);
        int rt = verts.Count;
        for (int i = 0; i < seg; i++) { float t = (float)i / seg * 2f * Mathf.PI; verts.Add(new Vector3(Mathf.Cos(t) * r, 1f, Mathf.Sin(t) * r)); norms.Add(Vector3.up); }
        for (int i = 0; i < seg; i++) AddTri(triHi, ct, rt + i, rt + (i + 1) % seg, Vector3.up);

        var mesh = new Mesh { name = "SplitCylinder" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(triLo, 0);
        mesh.SetTriangles(triHi, 1);
        mesh.RecalculateBounds();
        _splitCylinderMesh = mesh;
        return mesh;
    }

    // ─── Display style ─────────────────────────────────────────────────────────

    private static readonly HashSet<string> PolyLigands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "O", "N", "F", "Cl", "Br", "I", "S", "Se", "Te", "H", "D" };

    public void SetDisplayStyle(DisplayStyle s)
    {
        displayStyle = s;
        if (_atomRec.Count > 0) ApplyStyle();
    }

    /// <summary>Set the bond-length acceptance window in Å and rebuild bonds.</summary>
    public void SetBondRange(float minA, float maxA)
    {
        _bondRangeUserSet = true;
        bondMinAngstrom = Mathf.Min(minA, maxA);
        bondMaxAngstrom = Mathf.Max(minA, maxA);
        TryBuild();
    }

    // ─── Pressure: 2nd-order Birch–Murnaghan isotropic compression ──────────────

    /// <summary>Set applied pressure (GPa) and rebuild the compressed structure.</summary>
    public void SetPressure(float gpa) { pressureGPa = Mathf.Max(0f, gpa); TryBuild(); }

    /// <summary>Set the zero-pressure bulk modulus K0 (GPa) and rebuild.</summary>
    public void SetBulkModulus(float k0) { bulkModulusK0 = Mathf.Max(1f, k0); TryBuild(); }

    /// <summary>Linear compression ratio s = ℓ/ℓ0 = (V/V0)^(1/3) at the current P and K0.</summary>
    public float CurrentLinearCompression => LinearCompressionRatio(pressureGPa, bulkModulusK0);

    /// <summary>Predict the linear compression ratio for an arbitrary pressure (for live UI readouts).</summary>
    public float PredictLinearCompression(float gpa) => LinearCompressionRatio(gpa, bulkModulusK0);

    // Invert the 2nd-order Birch–Murnaghan EoS  P = 1.5·K0·[x^(7/3) − x^(5/3)],  x = V0/V,
    // for the linear ratio s = (V/V0)^(1/3). Monotonic in V, so bisection is robust.
    private static float LinearCompressionRatio(float P, float K0)
    {
        if (P <= 0f || K0 <= 0f) return 1f;
        float lo = 0.2f, hi = 1.0f;             // r = V/V0 in (0,1]
        for (int it = 0; it < 60; it++)
        {
            float r  = 0.5f * (lo + hi);
            float x  = 1f / r;
            float Pr = 1.5f * K0 * (Mathf.Pow(x, 7f / 3f) - Mathf.Pow(x, 5f / 3f));
            if (Pr > P) lo = r; else hi = r;    // Pr decreases as r grows
        }
        return Mathf.Pow(0.5f * (lo + hi), 1f / 3f);
    }

    // Compress the cell isotropically: scale a, b, c by s (fractional coords unchanged).
    private void ApplyPressureToMetrics(CrystalModel model)
    {
        float s = LinearCompressionRatio(pressureGPa, bulkModulusK0);
        if (Mathf.Abs(s - 1f) < 1e-5f) return;
        var m = model.Metrics;
        model.Metrics = CellMetrics.FromABC(m.a * s, m.b * s, m.c * s, m.alpha, m.beta, m.gamma);
    }

    /// <summary>Show or hide the unit-cell wireframe box.</summary>
    public void SetUnitCellVisible(bool v)
    {
        showUnitCell = v;
        if (_unitCellGO != null) _unitCellGO.SetActive(v);
    }

    /// <summary>Show or hide the a/b/c axis indicator.</summary>
    public void SetAxisVisible(bool v)
    {
        showAxisIndicator = v;
        if (_axisGO != null) _axisGO.SetActive(v);
    }

    // ─── Atom sphere construction (single- or bi-coloured) ──────────────────────

    private static Mesh _splitSphereMesh;

    /// <summary>
    /// Create an atom sphere. If <paramref name="elemB"/> is non-null the sphere is split
    /// into two coloured hemispheres (joint-occupancy site visualisation).
    /// </summary>
    private GameObject CreateAtomSphere(string elemA, string elemB, Dictionary<string, Color> colors)
    {
        if (elemB == null)
        {
            var goSingle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var rend = goSingle.GetComponent<Renderer>();
            if (rend != null) rend.sharedMaterial = ElementStylings.GetOrMakeMaterial(elemA, colors);
            NoShadows(rend);
            return goSingle;
        }

        var go = new GameObject("AtomSplit");
        go.AddComponent<MeshFilter>().sharedMesh = GetSplitSphereMesh();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = new[]
        {
            ElementStylings.GetOrMakeMaterial(elemA, colors),
            ElementStylings.GetOrMakeMaterial(elemB, colors),
        };
        NoShadows(mr);
        var sc = go.AddComponent<SphereCollider>();
        sc.radius = 0.5f; // matches the unit-diameter mesh below
        return go;
    }

    // A UV sphere (radius 0.5) whose triangles are split into two submeshes by the
    // sign of their centroid's local X — giving a clean two-tone left/right sphere.
    private static Mesh GetSplitSphereMesh()
    {
        if (_splitSphereMesh != null) return _splitSphereMesh;

        const int lon = 24, lat = 16;
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        for (int y = 0; y <= lat; y++)
        {
            float theta = (float)y / lat * Mathf.PI;
            for (int x = 0; x <= lon; x++)
            {
                float phi = (float)x / lon * 2f * Mathf.PI;
                var nrm = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi),
                                      Mathf.Cos(theta),
                                      Mathf.Sin(theta) * Mathf.Sin(phi));
                verts.Add(nrm * 0.5f);
                norms.Add(nrm);
            }
        }

        var triA = new List<int>();
        var triB = new List<int>();
        int stride = lon + 1;
        for (int y = 0; y < lat; y++)
        for (int x = 0; x < lon; x++)
        {
            int i0 = y * stride + x, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
            AssignTri(verts, triA, triB, i0, i2, i1);
            AssignTri(verts, triA, triB, i1, i2, i3);
        }

        var mesh = new Mesh { name = "SplitSphere" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(triA, 0);
        mesh.SetTriangles(triB, 1);
        mesh.RecalculateBounds();
        _splitSphereMesh = mesh;
        return mesh;
    }

    private static void AssignTri(List<Vector3> verts, List<int> triA, List<int> triB, int a, int b, int c)
    {
        Vector3 va = verts[a], vb = verts[b], vc = verts[c];

        // Ensure the triangle winds counter-clockwise as seen from outside the sphere
        // (centre at origin), so back-face culling doesn't make the atom look see-through.
        Vector3 faceNormal = Vector3.Cross(vb - va, vc - va);
        Vector3 outward    = va + vb + vc; // centroid direction from the sphere centre
        int i1 = b, i2 = c;
        if (Vector3.Dot(faceNormal, outward) < 0f) { i1 = c; i2 = b; }

        float cx = (va.x + vb.x + vc.x) / 3f;
        var dst = cx < 0f ? triA : triB;
        dst.Add(a); dst.Add(i1); dst.Add(i2);
    }

    private void ApplyStyle()
    {
        // Remove any existing polyhedral meshes (parented to atomsRoot, named "Poly_*").
        if (atomsRoot != null)
        {
            for (int k = atomsRoot.childCount - 1; k >= 0; k--)
            {
                var ch = atomsRoot.GetChild(k);
                if (ch.name.StartsWith("Poly_"))
                {
                    if (Application.isPlaying) Destroy(ch.gameObject);
                    else DestroyImmediate(ch.gameObject);
                }
            }
        }

        foreach (var rec in _atomRec)
        {
            if (rec.tf == null) continue;
            bool isLigand = !_polyCentres.Contains(rec.element);   // vertex atoms stay visible in Polyhedral

            bool  visible;
            float radius;
            float mult;

            switch (displayStyle)
            {
                case DisplayStyle.SpaceFilling:
                    visible = true; radius = rec.vdwR; mult = 1f;
                    break;
                case DisplayStyle.Stick:
                    visible = true; radius = rec.covR; mult = 0.15f;
                    break;
                case DisplayStyle.Wireframe:
                    visible = false; radius = rec.covR; mult = 0f;
                    break;
                case DisplayStyle.Polyhedral:
                    visible = isLigand; radius = rec.covR; mult = isLigand ? 0.5f : 0f;
                    break;
                default: // BallAndStick
                    visible = true; radius = rec.covR; mult = 1f;
                    break;
            }

            rec.tf.localScale = Vector3.one * (radius * atomScale * 2f * VisualizationUnits.AngstromToUnity * mult);
            var rend = rec.tf.GetComponent<Renderer>();
            if (rend != null) rend.enabled = visible;
            // Hidden atoms must not keep blocking picking rays (invisible colliders in front
            // of UI panels made lasers "pass through" menus in Wireframe/Polyhedral styles).
            var col = rec.tf.GetComponent<Collider>();
            if (col != null) col.enabled = visible;
        }

        bool bondsVisible = displayStyle != DisplayStyle.SpaceFilling && displayStyle != DisplayStyle.Polyhedral;
        float bondMult = displayStyle switch
        {
            DisplayStyle.Wireframe => 0.35f,
            DisplayStyle.Stick     => 3.0f,
            _                      => 1f,
        };

        foreach (var rec in _bondRec)
        {
            if (rec.tf == null) continue;
            var rend = rec.tf.GetComponent<Renderer>();
            if (rend != null) rend.enabled = bondsVisible;
            if (bondsVisible)
            {
                float w = rec.baseW * bondMult;
                rec.tf.localScale = new Vector3(w, rec.halfLen, w);
            }
        }

        if (displayStyle == DisplayStyle.Polyhedral) BuildPolyhedra();
    }

    private void BuildPolyhedra()
    {
        if (atomsRoot == null || LastBaseModel == null) return;
        var m = LastBaseModel.Metrics;
        Vector3 av = m.aUnity, bv = m.bUnity, cv = m.cUnity;
        var polyMat = ElementStylings.GetPolyhedralMaterial();

        int count = _atomRec.Count;
        for (int i = 0; i < count; i++)
        {
            var rec = _atomRec[i];
            if (rec.tf == null || !_polyCentres.Contains(rec.element)) continue;   // A1 of a VESTA poly pair
            Vector3 center = rec.tf.localPosition;

            // Gather every bonded corner around this centre, INCLUDING periodic images from the
            // 26 neighbouring cells, so polyhedra at the cell boundary are complete (VESTA-style).
            var verts = new List<Vector3>();
            for (int j = 0; j < count; j++)
            {
                if (j == i || _atomRec[j].tf == null) continue;
                if (!TryGetPolyWindow(rec.element, _atomRec[j].element, out float wMin, out float wMax)) continue;
                float minU = wMin * VisualizationUnits.AngstromToUnity, maxU = wMax * VisualizationUnits.AngstromToUnity;
                float min2 = minU * minU, max2 = maxU * maxU;
                Vector3 basePos = _atomRec[j].tf.localPosition;
                for (int la = -1; la <= 1; la++)
                for (int lb = -1; lb <= 1; lb++)
                for (int lc = -1; lc <= 1; lc++)
                {
                    Vector3 p  = basePos + la * av + lb * bv + lc * cv;
                    float   d2 = (p - center).sqrMagnitude;
                    if (d2 >= min2 && d2 <= max2) verts.Add(p);
                }
            }
            // Drop near-coincident corners that would make the hull degenerate.
            for (int x = verts.Count - 1; x >= 0; x--)
                for (int y = 0; y < x; y++)
                    if ((verts[x] - verts[y]).sqrMagnitude < 1e-6f) { verts.RemoveAt(x); break; }
            if (verts.Count < 3) continue;

            var pts  = verts.ToArray();
            var tris = ConvexHullTris(center, pts);
            if (tris.Length < 3) continue;

            // Build a FLAT-shaded solid: 3 unique verts per face with one outward normal each.
            // Faceted look + correct lighting from any side (rendered double-sided), like VESTA.
            var fv = new List<Vector3>(tris.Length);
            var fn = new List<Vector3>(tris.Length);
            var ft = new List<int>(tris.Length);
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 p0 = pts[tris[t]], p1 = pts[tris[t + 1]], p2 = pts[tris[t + 2]];
                Vector3 nrm = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                int b0 = fv.Count;
                fv.Add(p0); fv.Add(p1); fv.Add(p2);
                fn.Add(nrm); fn.Add(nrm); fn.Add(nrm);
                ft.Add(b0); ft.Add(b0 + 1); ft.Add(b0 + 2);
            }

            var mesh = new Mesh { name = $"Poly_{rec.element}_{i}" };
            mesh.SetVertices(fv);
            mesh.SetNormals(fn);
            mesh.SetTriangles(ft, 0);
            mesh.RecalculateBounds();

            var go = new GameObject($"Poly_{rec.element}_{i}");
            go.transform.SetParent(atomsRoot, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            var tinted = new Material(polyMat);
            Color baseCol = ElementStylings.DefaultColors.TryGetValue(rec.element, out var ec) ? ec : Color.white;
            tinted.color = new Color(baseCol.r, baseCol.g, baseCol.b, 1f);
            mr.sharedMaterial = tinted;
            NoShadows(mr);
        }
    }

    // O(n^4) convex hull for small vertex sets (coordination polyhedra, n <= 8).
    private static int[] ConvexHullTris(Vector3 center, Vector3[] pts)
    {
        int n = pts.Length;
        if (n < 3) return Array.Empty<int>();
        var result = new List<int>();

        for (int i = 0; i < n - 2; i++)
        for (int j = i + 1; j < n - 1; j++)
        for (int k = j + 1; k < n;     k++)
        {
            var normal = Vector3.Cross(pts[j] - pts[i], pts[k] - pts[i]);
            if (normal.sqrMagnitude < 1e-12f) continue;

            // All other vertices must lie on one side of the plane.
            float side = 0f;
            bool valid = true;
            for (int m = 0; m < n; m++)
            {
                if (m == i || m == j || m == k) continue;
                float d = Vector3.Dot(normal, pts[m] - pts[i]);
                if (Mathf.Abs(d) < 1e-6f) continue;
                if (side == 0f) side = Mathf.Sign(d);
                else if (Mathf.Sign(d) != side) { valid = false; break; }
            }
            if (!valid) continue;

            // Orient face outward from the central atom.
            if (Vector3.Dot(normal, pts[i] - center) < 0)
                result.AddRange(new[] { i, k, j });
            else
                result.AddRange(new[] { i, j, k });
        }
        return result.ToArray();
    }

    private void DrawUnitCellWireframe(CellMetrics m)
    {
        var root = MakeRoot("UnitCell");
        _unitCellGO = root;

        Vector3 o = Vector3.zero;
        Vector3 a = m.aUnity;
        Vector3 b = m.bUnity;
        Vector3 c = m.cUnity;

        // 8 corners of the cell parallelepiped.
        Vector3[] corners =
        {
            o,       o+a,     o+b,     o+c,
            o+a+b,   o+a+c,   o+b+c,   o+a+b+c
        };
        // 12 edges as corner-index pairs.
        int[,] edges =
        {
            {0,1},{0,2},{0,3},{1,4},{1,5},{2,4},{2,6},{3,5},{3,6},{4,7},{5,7},{6,7}
        };

        // Solid geometry instead of a LineRenderer strip: thin cylinders per edge with
        // matching spheres at every corner, so joints are rounded and seamless from any angle.
        const float frameR = 0.012f;
        var mat = ElementStylings.GetCellFrameMaterial();

        for (int e = 0; e < edges.GetLength(0); e++)
        {
            Vector3 p0 = corners[edges[e, 0]];
            Vector3 p1 = corners[edges[e, 1]];
            Vector3 dir = p1 - p0;

            var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = $"CellEdge_{e}";
            cyl.transform.SetParent(root.transform, false);
            cyl.transform.localPosition = (p0 + p1) * 0.5f;
            cyl.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir.normalized);
            cyl.transform.localScale    = new Vector3(frameR * 2f, dir.magnitude * 0.5f, frameR * 2f);
            var cr = cyl.GetComponent<Renderer>();
            cr.sharedMaterial = mat;
            NoShadows(cr);
            var cc = cyl.GetComponent<Collider>();
            if (cc != null) { if (Application.isPlaying) Destroy(cc); else DestroyImmediate(cc); }
        }

        foreach (var p in corners)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = "CellCorner";
            s.transform.SetParent(root.transform, false);
            s.transform.localPosition = p;
            s.transform.localScale    = Vector3.one * (frameR * 2f);
            var sr = s.GetComponent<Renderer>();
            sr.sharedMaterial = mat;
            NoShadows(sr);
            var sc = s.GetComponent<Collider>();
            if (sc != null) { if (Application.isPlaying) Destroy(sc); else DestroyImmediate(sc); }
        }

        root.SetActive(showUnitCell);
    }

    // Small a/b/c crystallographic axis gizmo, anchored to a fixed screen corner.
    // It stays put (and constant size) while the crystal pans/zooms; only its rotation
    // tracks the crystal so it shows the current orientation. Labels billboard upright.
    private void DrawAxisIndicator(CellMetrics m)
    {
        var root = MakeDetachedRoot("AxisIndicator");
        _axisGO = root;

        const float len = 0.5f; // fixed Unity length; on-screen size is constant
        var axes = new (Vector3 dir, Color col, string label)[]
        {
            (m.aUnity.normalized, new Color(0.90f, 0.20f, 0.20f), "a"),
            (m.bUnity.normalized, new Color(0.20f, 0.80f, 0.20f), "b"),
            (m.cUnity.normalized, new Color(0.30f, 0.45f, 1.00f), "c"),
        };

        var labelTfs = new List<Transform>();
        foreach (var ax in axes)
        {
            var go = new GameObject($"Axis_{ax.label}");
            go.transform.SetParent(root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace   = false;
            lr.positionCount   = 2;
            lr.widthMultiplier = 0.025f;
            lr.numCapVertices  = 2;
            lr.alignment       = LineAlignment.View;
            lr.SetPositions(new[] { Vector3.zero, ax.dir * len });
            lr.sharedMaterial  = ElementStylings.GetWireMaterial();
            lr.startColor = lr.endColor = ax.col;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var lblGO = new GameObject($"Label_{ax.label}");
            lblGO.transform.SetParent(root.transform, false);
            lblGO.transform.localPosition = ax.dir * (len * 1.18f);
            lblGO.transform.localScale    = Vector3.one * 0.0075f;
            var tm = lblGO.AddComponent<TextMesh>();
            tm.text      = ax.label;
            tm.fontSize  = 180;
            tm.color     = ax.col;
            tm.anchor    = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            labelTfs.Add(lblGO.transform);
        }

        var follower = root.AddComponent<AxisGizmoFollower>();
        follower.crystal = transform;
        var ci = GetComponent<CrystalInteraction>();
        follower.cam     = ci != null ? ci.pickCamera : null;
        follower.labels  = labelTfs.ToArray();

        root.SetActive(showAxisIndicator);
    }

    // ----------------------------- Miller (hkl) lattice planes -----------------------------

    /// <summary>Set the Miller indices to visualise and whether the plane family is shown.</summary>
    public void SetMillerPlane(int h, int k, int l, bool show)
    {
        millerH = h; millerK = k; millerL = l; showMillerPlane = show;
        RebuildMillerPlane();
    }

    private void RebuildMillerPlane()
    {
        if (_millerGO != null)
        {
            _generated.Remove(_millerGO);
            if (Application.isPlaying) Destroy(_millerGO); else DestroyImmediate(_millerGO);
            _millerGO = null;
        }
        if (!showMillerPlane || LastBaseModel == null) return;
        if (millerH == 0 && millerK == 0 && millerL == 0) return;

        _millerGO = MakeRoot("MillerPlanes");
        BuildMillerPlanes(LastBaseModel.Metrics, _millerGO.transform);
    }

    // Cube corners and the 12 edges connecting them (fractional coordinates).
    private static readonly Vector3[] CubeCorners =
    {
        new(0,0,0), new(1,0,0), new(0,1,0), new(0,0,1),
        new(1,1,0), new(1,0,1), new(0,1,1), new(1,1,1)
    };
    private static readonly int[,] CubeEdges =
    {
        {0,1},{0,2},{0,3},{1,4},{1,5},{2,4},{2,6},{3,5},{3,6},{4,7},{5,7},{6,7}
    };

    private void BuildMillerPlanes(CellMetrics m, Transform parent)
    {
        int h = millerH, k = millerK, l = millerL;

        // Range of f = h*x + k*y + l*z over the unit cube [0,1]^3.
        float fmin = 0f, fmax = 0f;
        foreach (int coef in new[] { h, k, l }) { if (coef > 0) fmax += coef; else fmin += coef; }

        int nLo = Mathf.CeilToInt(fmin);
        int nHi = Mathf.FloorToInt(fmax);

        int drawn = 0;
        const int maxPlanes = 12; // guard against clutter for high indices
        for (int nVal = nLo; nVal <= nHi && drawn < maxPlanes; nVal++)
        {
            if (BuildOneMillerPlane(m, h, k, l, nVal, parent, drawn)) drawn++;
        }
    }

    private bool BuildOneMillerPlane(CellMetrics m, int h, int k, int l, int n, Transform parent, int index)
    {
        // Collect intersections of the plane h*x+k*y+l*z=n with the cube edges (fractional).
        var fracPts = new List<Vector3>();
        for (int e = 0; e < CubeEdges.GetLength(0); e++)
        {
            Vector3 p0 = CubeCorners[CubeEdges[e, 0]];
            Vector3 p1 = CubeCorners[CubeEdges[e, 1]];
            float f0 = h * p0.x + k * p0.y + l * p0.z - n;
            float f1 = h * p1.x + k * p1.y + l * p1.z - n;
            if (Mathf.Abs(f0 - f1) < 1e-6f) continue;      // edge parallel to plane
            float t = f0 / (f0 - f1);
            if (t < -1e-5f || t > 1f + 1e-5f) continue;     // intersection outside this edge
            fracPts.Add(Vector3.Lerp(p0, p1, Mathf.Clamp01(t)));
        }
        if (fracPts.Count < 3) return false;

        // Convert to local Unity coordinates.
        var pts = new List<Vector3>(fracPts.Count);
        foreach (var f in fracPts) pts.Add(m.FracToCartesianUnity(f));

        // De-duplicate near-coincident vertices.
        for (int i = pts.Count - 1; i >= 0; i--)
            for (int j = 0; j < i; j++)
                if ((pts[i] - pts[j]).sqrMagnitude < 1e-8f) { pts.RemoveAt(i); break; }
        if (pts.Count < 3) return false;

        // Order vertices around the polygon centroid within the plane.
        Vector3 c = Vector3.zero;
        foreach (var p in pts) c += p;
        c /= pts.Count;

        Vector3 normal = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]).normalized;
        Vector3 u = (pts[0] - c).normalized;
        Vector3 v = Vector3.Cross(normal, u);
        pts.Sort((p, q) =>
        {
            float ap = Mathf.Atan2(Vector3.Dot(p - c, v), Vector3.Dot(p - c, u));
            float aq = Mathf.Atan2(Vector3.Dot(q - c, v), Vector3.Dot(q - c, u));
            return ap.CompareTo(aq);
        });

        // Triangle fan (front faces); back faces are handled by a double-sided material.
        var tris = new List<int>();
        for (int i = 1; i < pts.Count - 1; i++) { tris.Add(0); tris.Add(i); tris.Add(i + 1); }

        var mesh = new Mesh { name = $"Miller_{h}{k}{l}_{n}" };
        mesh.SetVertices(pts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject($"Plane_{h}{k}{l}_{n}");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = ElementStylings.GetMillerPlaneMaterial();
        NoShadows(mr);
        return true;
    }

    // ----------------------------- Touching-base-cell test -----------------------------

    private bool AtomTouchesBaseCell(Vector3 atomPosUnity, float atomRadiusUnity, CellMetrics m)
    {
        // Convert center to fractional coords (can be outside [0,1))
        Vector3 f = m.FracFromUnity(atomPosUnity);

        // Closest point in the [0,1]^3 fractional cell
        Vector3 fClamped = new Vector3(Mathf.Clamp01(f.x), Mathf.Clamp01(f.y), Mathf.Clamp01(f.z));

        // Back to Unity space
        Vector3 closestUnity = m.FracToCartesianUnity(fClamped);

        // Intersects if inside or within sphere radius to the boundary
        float d = Vector3.Distance(atomPosUnity, closestUnity);
        return d <= atomRadiusUnity + 1e-6f;
    }

    // ----------------------------- Data structures -----------------------------

    public class CrystalModel
    {
        public string Title;
        public CellMetrics Metrics;
        public List<Atom> Atoms = new List<Atom>();
        public List<SymOp> Symmetry = new List<SymOp>();
        public string SpaceGroupName;
        public int SpaceGroupNumber = 0;
        public float BondMinHint = 0f; // from CIF _geom_bond_distance (0 = none)
        public float BondMaxHint = 0f;
    }

    public class Atom
    {
        public string Label;
        public string Element;
        public Vector3 Frac;      // fractional [0,1)
        public Vector3 Cartesian; // Unity units
        public float   Occupancy = 1f; // site occupancy (1 = fully occupied)
    }

    public struct CellMetrics
    {
        public float a, b, c;      // Å
        public float alpha, beta, gamma; // degrees
        public Matrix4x4 fracToCart;     // Å basis matrix
        public Matrix4x4 unityToFrac;    // Unity->fractional
        public Vector3 aCart, bCart, cCart; // Å basis
        public Vector3 aUnity, bUnity, cUnity; // Unity basis (Å scaled)

        public float MaxSpanUnity => Mathf.Max(a, Mathf.Max(b, c)) * VisualizationUnits.AngstromToUnity;

        public static CellMetrics FromABC(float a, float b, float c, float alpha, float beta, float gamma)
        {
            // Convert angles to radians
            float ar = alpha * Mathf.Deg2Rad;
            float br = beta  * Mathf.Deg2Rad;
            float gr = gamma * Mathf.Deg2Rad;

            // Conventional cell vectors in Cartesian (Å)
            float cosA = Mathf.Cos(ar), cosB = Mathf.Cos(br), cosG = Mathf.Cos(gr), sinG = Mathf.Sin(gr);

            Vector3 va = new Vector3(a, 0, 0);
            Vector3 vb = new Vector3(b * cosG, b * sinG, 0);
            float cx = c * cosB;
            float cy = c * (cosA - cosB * cosG) / Mathf.Max(1e-8f, sinG);
            float cz = Mathf.Sqrt(Mathf.Max(0f, c*c - cx*cx - cy*cy));
            Vector3 vc = new Vector3(cx, cy, cz);

            var M = Matrix4x4.identity;
            M.SetColumn(0, new Vector4(va.x, va.y, va.z, 0));
            M.SetColumn(1, new Vector4(vb.x, vb.y, vb.z, 0));
            M.SetColumn(2, new Vector4(vc.x, vc.y, vc.z, 0));
            M.SetColumn(3, new Vector4(0, 0, 0, 1));

            var cm = new CellMetrics
            {
                a = a, b = b, c = c,
                alpha = alpha, beta = beta, gamma = gamma,
                fracToCart = M,
                aCart = va, bCart = vb, cCart = vc,
                aUnity = va * VisualizationUnits.AngstromToUnity,
                bUnity = vb * VisualizationUnits.AngstromToUnity,
                cUnity = vc * VisualizationUnits.AngstromToUnity
            };

            // Build Unity-basis matrix with columns aUnity,bUnity,cUnity and invert to get unity->frac
            var U = Matrix4x4.identity;
            U.SetColumn(0, new Vector4(cm.aUnity.x, cm.aUnity.y, cm.aUnity.z, 0));
            U.SetColumn(1, new Vector4(cm.bUnity.x, cm.bUnity.y, cm.bUnity.z, 0));
            U.SetColumn(2, new Vector4(cm.cUnity.x, cm.cUnity.y, cm.cUnity.z, 0));
            cm.unityToFrac = U.inverse;

            return cm;
        }

        public Vector3 FracToCartesianUnity(Vector3 f)
        {
            Vector3 ang = fracToCart.MultiplyVector(f); // Å
            return ang * VisualizationUnits.AngstromToUnity;
        }

        public Vector3 FracFromUnity(Vector3 pUnity)
        {
            return unityToFrac.MultiplyVector(pUnity);
        }
    }

    public struct SymOp
    {
        // x' = coefX*x + coefY*y + coefZ*z + offset
        public float xCx, xCy, xCz, xOff;
        public float yCx, yCy, yCz, yOff;
        public float zCx, zCy, zCz, zOff;

        public Vector3 Apply(Vector3 frac)
        {
            float nx = xCx * frac.x + xCy * frac.y + xCz * frac.z + xOff;
            float ny = yCx * frac.x + yCy * frac.y + yCz * frac.z + yOff;
            float nz = zCx * frac.x + zCy * frac.y + zCz * frac.z + zOff;
            // wrap to [0,1)
            nx = nx - Mathf.Floor(nx);
            ny = ny - Mathf.Floor(ny);
            nz = nz - Mathf.Floor(nz);
            return new Vector3(nx, ny, nz);
        }

        public static SymOp Identity => new SymOp { xCx=1, yCy=1, zCz=1 };
    }

    // ----------------------------- CIF Reader -----------------------------

    static class CifReader
    {
        // Basic CIF keys we care about
        static readonly string[] SymKeys = new[]{
            "_space_group_symop_operation_xyz",
            "_symmetry_equiv_pos_as_xyz"
        };

        public static CrystalModel Parse(string raw, bool verboseLog=false)
        {
            var model = new CrystalModel();
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .ToList();

            // Title: prefer a human-readable chemical name; fall back to formula, then the
            // data_ block id (minus the "data_" prefix), then a generic label.
            model.Title = FirstNonEmpty(
                GetString(lines, "_chemical_name_mineral"),
                GetString(lines, "_chemical_name_common"),
                GetString(lines, "_chemical_name_systematic"),
                GetString(lines, "_chemical_formula_sum"),
                GetString(lines, "_chemical_formula_structural"));
            if (string.IsNullOrWhiteSpace(model.Title) || model.Title == "?" || model.Title == ".")
            {
                var dataLine = lines.FirstOrDefault(l => l.StartsWith("data_", StringComparison.OrdinalIgnoreCase));
                model.Title = dataLine != null && dataLine.Length > 5
                    ? dataLine.Substring(5).Trim()
                    : "Crystal";
            }

            // Cell params
            float a = GetFloat(lines, "_cell_length_a");
            float b = GetFloat(lines, "_cell_length_b");
            float c = GetFloat(lines, "_cell_length_c");
            float alpha = GetFloat(lines, "_cell_angle_alpha");
            float beta  = GetFloat(lines, "_cell_angle_beta");
            float gamma = GetFloat(lines, "_cell_angle_gamma");
            if (a <= 0 || b <= 0 || c <= 0 || alpha <= 0 || beta <= 0 || gamma <= 0)
            {
                Debug.LogError("[CIF] Missing/invalid cell parameters.");
                return null;
            }
            model.Metrics = CellMetrics.FromABC(a, b, c, alpha, beta, gamma);

            // Space group name/number (optional)
            model.SpaceGroupName = GetString(lines, "_space_group_name_H-M_alt") 
                                   ?? GetString(lines, "_symmetry_space_group_name_H-M");
            model.SpaceGroupNumber = (int)GetFloat(lines, "_space_group_IT_number");
            if (model.SpaceGroupNumber == 0)
                model.SpaceGroupNumber = (int)GetFloat(lines, "_symmetry_Int_Tables_number");

            // Symmetry operations
            var symOps = new List<SymOp>();
            foreach (var key in SymKeys)
            {
                var ops = ReadLoopValues(lines, key);
                if (ops.Count > 0)
                {
                    foreach (var opStr in ops)
                    {
                        if (SymmetryParser.TryParse(opStr, out var op))
                            symOps.Add(op);
                    }
                    break;
                }
            }
            if (symOps.Count == 0)
            {
                // If CIF lacks explicit ops, fall back to identity so at least the asymmetric unit renders.
                symOps.Add(SymOp.Identity);
                if (verboseLog) Debug.LogWarning("[CIF] No symmetry ops in file. Rendering asymmetric unit only.");
            }
            model.Symmetry = symOps;

            // Atom loop
            var atomBlock = ReadLoopBlock(lines, "_atom_site_");
            if (atomBlock.Headers.Count == 0 || atomBlock.Rows.Count == 0)
            {
                Debug.LogError("[CIF] No _atom_site loop found.");
                return null;
            }
            int idxLabel = atomBlock.Headers.FindIndex(h => h.EndsWith("label", StringComparison.OrdinalIgnoreCase));
            int idxType  = atomBlock.Headers.FindIndex(h => h.EndsWith("type_symbol", StringComparison.OrdinalIgnoreCase) || h.EndsWith("type", StringComparison.OrdinalIgnoreCase));
            int idxFx    = atomBlock.Headers.FindIndex(h => h.EndsWith("fract_x", StringComparison.OrdinalIgnoreCase));
            int idxFy    = atomBlock.Headers.FindIndex(h => h.EndsWith("fract_y", StringComparison.OrdinalIgnoreCase));
            int idxFz    = atomBlock.Headers.FindIndex(h => h.EndsWith("fract_z", StringComparison.OrdinalIgnoreCase));
            int idxOcc   = atomBlock.Headers.FindIndex(h => h.EndsWith("occupancy", StringComparison.OrdinalIgnoreCase));

            if (idxFx < 0 || idxFy < 0 || idxFz < 0)
            {
                Debug.LogError("[CIF] Atom fractional coordinates not found.");
                return null;
            }

            foreach (var row in atomBlock.Rows)
            {
                string label = Safe(row, idxLabel);
                string elem = ElementStylings.CleanElement( Safe(row, idxType), label );
                float fx = ParseFloat(Safe(row, idxFx));
                float fy = ParseFloat(Safe(row, idxFy));
                float fz = ParseFloat(Safe(row, idxFz));
                float occ = idxOcc >= 0 ? ParseFloat(Safe(row, idxOcc)) : 1f;
                if (occ <= 0f) occ = 1f;

                var aAtom = new Atom
                {
                    Label = string.IsNullOrEmpty(label) ? elem : label,
                    Element = elem,
                    Frac = new Vector3(fx - Mathf.Floor(fx), fy - Mathf.Floor(fy), fz - Mathf.Floor(fz)),
                    Occupancy = occ
                };
                model.Atoms.Add(aAtom);
            }

            // Optional explicit bond distances — used to seed the default bond-length window.
            var bondDists = ReadLoopValues(lines, "_geom_bond_distance");
            bool anyBond = false;
            float bMin = 0f, bMax = 0f;
            foreach (var s in bondDists)
            {
                float d = ParseFloat(s);
                if (d <= 0.1f) continue;
                if (!anyBond) { bMin = bMax = d; anyBond = true; }
                else { if (d < bMin) bMin = d; if (d > bMax) bMax = d; }
            }
            if (anyBond)
            {
                model.BondMinHint = bMin;
                model.BondMaxHint = bMax + 0.05f; // small cushion so the longest bond still draws
            }

            if (verboseLog)
            {
                Debug.Log($"[CIF] Parsed {model.Atoms.Count} atoms, {model.Symmetry.Count} symmetry ops. Space group: {model.SpaceGroupName} #{model.SpaceGroupNumber}");
            }

            return model;
        }

        // --- helpers ---

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v) && v != "?" && v != ".")
                    return v.Trim();
            return null;
        }

        private static float GetFloat(List<string> lines, string key)
        {
            string s = GetString(lines, key);
            return ParseFloat(s);
        }

        private static string GetString(List<string> lines, string key)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) ||
                                                 l.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (line == null) return null;
            if (line.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                int idx = lines.IndexOf(line);
                if (idx >= 0 && idx + 1 < lines.Count)
                    return CleanCifValue(lines[idx + 1]);
                return null;
            }
            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            return CleanCifValue(parts[1]);
        }

        private static string CleanCifValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            v = v.Trim();
            var m = Regex.Match(v, @"^([+-]?\d+(\.\d+)?)(\(\d+\))?$");
            if (m.Success) return m.Groups[1].Value;
            if ((v.StartsWith("'") && v.EndsWith("'")) || (v.StartsWith("\"") && v.EndsWith("\"")))
                v = v.Substring(1, v.Length - 2);
            return v;
        }

        private static float ParseFloat(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            if (s.Contains("/"))
            {
                var sp = s.Split('/');
                if (sp.Length == 2 && float.TryParse(sp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float n)
                    && float.TryParse(sp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d) && Math.Abs(d) > 1e-7f)
                {
                    return n / d;
                }
            }
            s = Regex.Replace(s, @"\([0-9]+\)", "");
            float v;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0f;
        }

        private static List<string> ReadLoopValues(List<string> lines, string key)
        {
            var vals = new List<string>();
            int idx = lines.FindIndex(l => l.StartsWith("loop_", StringComparison.OrdinalIgnoreCase));
            while (idx >= 0)
            {
                int i = idx + 1;
                var headers = new List<string>();
                while (i < lines.Count && lines[i].StartsWith("_"))
                {
                    headers.Add(lines[i]);
                    i++;
                }
                if (headers.Contains(key))
                {
                    int col = headers.IndexOf(key);
                    while (i < lines.Count && !lines[i].StartsWith("loop_", StringComparison.OrdinalIgnoreCase) &&
                           !lines[i].StartsWith("_") && !lines[i].StartsWith("data_", StringComparison.OrdinalIgnoreCase))
                    {
                        var row = SplitCifRow(lines[i], headers.Count);
                        if (row.Count == headers.Count)
                        {
                            vals.Add(CleanCifValue(row[col]));
                        }
                        i++;
                    }
                }
                idx = lines.FindIndex(idx + 1, l => l.StartsWith("loop_", StringComparison.OrdinalIgnoreCase));
            }
            return vals;
        }

        private static (List<string> Headers, List<List<string>> Rows) ReadLoopBlock(List<string> lines, string headerPrefix)
        {
            int idx = lines.FindIndex(l => l.StartsWith("loop_", StringComparison.OrdinalIgnoreCase));
            while (idx >= 0)
            {
                int i = idx + 1;
                var headers = new List<string>();
                while (i < lines.Count && lines[i].StartsWith("_"))
                {
                    headers.Add(lines[i]);
                    i++;
                }
                if (headers.Exists(h => h.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var rows = new List<List<string>>();
                    while (i < lines.Count && !lines[i].StartsWith("loop_", StringComparison.OrdinalIgnoreCase) &&
                           !lines[i].StartsWith("_") && !lines[i].StartsWith("data_", StringComparison.OrdinalIgnoreCase))
                    {
                        var row = SplitCifRow(lines[i], headers.Count);
                        if (row.Count == headers.Count) rows.Add(row);
                        i++;
                    }
                    return (headers, rows);
                }
                idx = lines.FindIndex(idx + 1, l => l.StartsWith("loop_", StringComparison.OrdinalIgnoreCase));
            }
            return (new List<string>(), new List<List<string>>());
        }

        private static List<string> SplitCifRow(string line, int expected)
        {
            var matches = Regex.Matches(line, @"(?:'[^']*'|""[^""]*""|\S+)");
            var list = matches.Cast<Match>().Select(m => CleanCifValue(m.Value)).ToList();
            return list;
        }

        private static string Safe(List<string> row, int idx) => (idx >= 0 && idx < row.Count) ? row[idx] : null;
    }

    // ----------------------------- Symmetry parsing & application -----------------------------

    static class SymmetryParser
    {
        public static bool TryParse(string op, out SymOp sym)
        {
            sym = SymOp.Identity;
            if (string.IsNullOrWhiteSpace(op)) return false;

            var cleaned = op.ToLowerInvariant().Replace(" ", "");
            var parts = cleaned.Split(',');
            if (parts.Length != 3) return false;

            bool okX = TryParseComponent(parts[0], out float xCx, out float xCy, out float xCz, out float xOff);
            bool okY = TryParseComponent(parts[1], out float yCx, out float yCy, out float yCz, out float yOff);
            bool okZ = TryParseComponent(parts[2], out float zCx, out float zCy, out float zCz, out float zOff);
            if (!(okX && okY && okZ)) return false;

            sym.xCx = xCx; sym.xCy = xCy; sym.xCz = xCz; sym.xOff = xOff;
            sym.yCx = yCx; sym.yCy = yCy; sym.yCz = yCz; sym.yOff = yOff;
            sym.zCx = zCx; sym.zCy = zCy; sym.zCz = zCz; sym.zOff = zOff;
            return true;
        }

        private static bool TryParseComponent(string expr, out float cx, out float cy, out float cz, out float off)
        {
            cx = cy = cz = off = 0f;
            if (string.IsNullOrEmpty(expr)) return false;

            var tokens = new List<string>();
            int i = 0;
            while (i < expr.Length)
            {
                char sgn = '+';
                if (expr[i] == '+' || expr[i] == '-') { sgn = expr[i]; i++; }
                int start = i;
                while (i < expr.Length && expr[i] != '+' && expr[i] != '-') i++;
                string tok = expr.Substring(start, i - start);
                if (string.IsNullOrEmpty(tok)) continue;
                tokens.Add((sgn == '-' ? "-" : "+") + tok);
            }

            foreach (var t in tokens)
            {
                string body = t.Substring(1);
                int sgn = t[0] == '-' ? -1 : 1;

                if (body == "x") cx += sgn;
                else if (body == "y") cy += sgn;
                else if (body == "z") cz += sgn;
                else
                {
                    float val = ParseOffset(body);
                    off += sgn * val;
                }
            }
            return true;
        }

        private static float ParseOffset(string s)
        {
            if (s.Contains("/"))
            {
                var sp = s.Split('/');
                if (sp.Length == 2 && int.TryParse(sp[0], out int n) && int.TryParse(sp[1], out int d) && d != 0)
                    return (float)n / d;
            }
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return f;
            return 0f;
        }
    }

    static class SymmetryApplier
    {
        public static CrystalModel Expand(CrystalModel src)
        {
            var dst = new CrystalModel
            {
                Title = src.Title,
                Metrics = src.Metrics,
                SpaceGroupName = src.SpaceGroupName,
                SpaceGroupNumber = src.SpaceGroupNumber,
                Symmetry = src.Symmetry
            };

            var seen = new HashSet<(string elem, int ex, int ey, int ez)>();
            foreach (var a in src.Atoms)
            {
                foreach (var op in src.Symmetry)
                {
                    var f = op.Apply(a.Frac);
                    // Wrap to [0, 1) — symmetry ops often produce coords like -0.1 or 1.2.
                    f = new Vector3(f.x - Mathf.Floor(f.x), f.y - Mathf.Floor(f.y), f.z - Mathf.Floor(f.z));
                    var key = (a.Element, (int)Mathf.Round(f.x * 10000f), (int)Mathf.Round(f.y * 10000f), (int)Mathf.Round(f.z * 10000f));
                    if (seen.Add(key))
                    {
                        var cart = src.Metrics.FracToCartesianUnity(f);
                        dst.Atoms.Add(new Atom
                        {
                            Label = a.Label,
                            Element = a.Element,
                            Frac = f,
                            Cartesian = cart,
                            Occupancy = a.Occupancy
                        });
                    }
                }
            }
            return dst;
        }
    }

    static class Replicator
    {
        public static CrystalModel Replicate(CrystalModel baseCell, int nx, int ny, int nz)
        {
            nx = Mathf.Max(1, nx); ny = Mathf.Max(1, ny); nz = Mathf.Max(1, nz);

            var dst = new CrystalModel
            {
                Title = baseCell.Title,
                Metrics = baseCell.Metrics,
                SpaceGroupName = baseCell.SpaceGroupName,
                SpaceGroupNumber = baseCell.SpaceGroupNumber,
                Symmetry = baseCell.Symmetry
            };

            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
            {
                Vector3 shiftF = new Vector3(ix, iy, iz);
                foreach (var a in baseCell.Atoms)
                {
                    var f = a.Frac + shiftF;
                    var cart = baseCell.Metrics.FracToCartesianUnity(f);
                    dst.Atoms.Add(new Atom
                    {
                        Label = a.Label,
                        Element = a.Element,
                        Frac = f,
                        Cartesian = cart,
                        Occupancy = a.Occupancy
                    });
                }
            }
            return dst;
        }
    }

    // ----------------------------- Styling & materials -----------------------------

    internal static class ElementStylings
    {
        private static readonly Dictionary<string, Material> MatCache = new();
        private static Material bondMat;
        private static Material wireMat;
        private static Material polyMat;
        private static Material millerMat;

        // Find a shader by name, falling back through guaranteed-present shaders so a stripped
        // URP shader can never produce a null (which would throw in `new Material(null)` and
        // abort the whole scene build). URP/Lit + URP/Unlit are pinned in Always Included
        // Shaders, but this is the safety net if that ever fails.
        private static Shader SafeShader(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            return Shader.Find("Sprites/Default")   // always present
                ?? Shader.Find("Hidden/InternalErrorShader");
        }
        private static Material cellFrameMat;

        // Bondi (1964) van der Waals radii in Å — used for Space-filling style.
        public static readonly Dictionary<string, float> VdwRadii = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["H"]=1.20f,["He"]=1.40f,
            ["Li"]=1.82f,["Be"]=1.53f,["B"]=1.92f,["C"]=1.70f,["N"]=1.55f,["O"]=1.52f,["F"]=1.47f,["Ne"]=1.54f,
            ["Na"]=2.27f,["Mg"]=1.73f,["Al"]=1.84f,["Si"]=2.10f,["P"]=1.80f,["S"]=1.80f,["Cl"]=1.75f,["Ar"]=1.88f,
            ["K"]=2.75f,["Ca"]=2.31f,["Sc"]=2.11f,["Ti"]=1.87f,["V"]=1.79f,["Cr"]=1.89f,["Mn"]=1.97f,
            ["Fe"]=1.94f,["Co"]=1.92f,["Ni"]=1.63f,["Cu"]=1.40f,["Zn"]=1.39f,
            ["Ga"]=1.87f,["Ge"]=2.11f,["As"]=1.85f,["Se"]=1.90f,["Br"]=1.85f,["Kr"]=2.02f,
            ["Rb"]=3.03f,["Sr"]=2.49f,["Pd"]=1.63f,["Ag"]=1.72f,["Cd"]=1.58f,
            ["In"]=1.93f,["Sn"]=2.17f,["Sb"]=2.06f,["Te"]=2.06f,["I"]=1.98f,["Xe"]=2.16f,
            ["Cs"]=3.43f,["Ba"]=2.68f,["Pt"]=1.75f,["Au"]=1.66f,["Hg"]=1.55f,
            ["Tl"]=1.96f,["Pb"]=2.02f,["Bi"]=2.07f,
        };

        public static readonly Dictionary<string, float> CovalentRadii = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            // Period 1
            ["H"]=0.31f, ["D"]=0.31f, ["T"]=0.31f, ["He"]=0.28f,
            // Period 2
            ["Li"]=1.28f, ["Be"]=0.96f, ["B"]=0.84f, ["C"]=0.76f, ["N"]=0.71f, ["O"]=0.66f, ["F"]=0.57f, ["Ne"]=0.58f,
            // Period 3
            ["Na"]=1.66f, ["Mg"]=1.41f, ["Al"]=1.21f, ["Si"]=1.11f, ["P"]=1.07f, ["S"]=1.05f, ["Cl"]=1.02f, ["Ar"]=1.06f,
            // Period 4
            ["K"]=2.03f, ["Ca"]=1.76f, ["Sc"]=1.70f, ["Ti"]=1.60f, ["V"]=1.53f, ["Cr"]=1.39f, ["Mn"]=1.50f,
            ["Fe"]=1.32f, ["Co"]=1.26f, ["Ni"]=1.24f, ["Cu"]=1.32f, ["Zn"]=1.22f,
            ["Ga"]=1.22f, ["Ge"]=1.20f, ["As"]=1.19f, ["Se"]=1.20f, ["Br"]=1.20f, ["Kr"]=1.16f,
            // Period 5
            ["Rb"]=2.20f, ["Sr"]=1.95f, ["Y"]=1.90f, ["Zr"]=1.75f, ["Nb"]=1.64f, ["Mo"]=1.54f, ["Tc"]=1.47f,
            ["Ru"]=1.46f, ["Rh"]=1.42f, ["Pd"]=1.39f, ["Ag"]=1.45f, ["Cd"]=1.44f,
            ["In"]=1.42f, ["Sn"]=1.39f, ["Sb"]=1.39f, ["Te"]=1.38f, ["I"]=1.39f, ["Xe"]=1.40f,
            // Period 6
            ["Cs"]=2.44f, ["Ba"]=2.15f,
            ["La"]=2.07f, ["Ce"]=2.04f, ["Pr"]=2.03f, ["Nd"]=2.01f, ["Pm"]=1.99f, ["Sm"]=1.98f,
            ["Eu"]=1.98f, ["Gd"]=1.96f, ["Tb"]=1.94f, ["Dy"]=1.92f, ["Ho"]=1.92f, ["Er"]=1.89f,
            ["Tm"]=1.90f, ["Yb"]=1.87f, ["Lu"]=1.87f,
            ["Hf"]=1.75f, ["Ta"]=1.70f, ["W"]=1.62f, ["Re"]=1.51f, ["Os"]=1.44f, ["Ir"]=1.41f,
            ["Pt"]=1.36f, ["Au"]=1.36f, ["Hg"]=1.32f,
            ["Tl"]=1.45f, ["Pb"]=1.46f, ["Bi"]=1.48f, ["Po"]=1.40f, ["At"]=1.50f, ["Rn"]=1.50f,
            // Period 7
            ["Fr"]=2.60f, ["Ra"]=2.21f,
            ["Ac"]=2.15f, ["Th"]=2.06f, ["Pa"]=2.00f, ["U"]=1.96f, ["Np"]=1.90f, ["Pu"]=1.87f,
            ["Am"]=1.80f, ["Cm"]=1.69f, ["Bk"]=1.68f, ["Cf"]=1.68f, ["Es"]=1.65f, ["Fm"]=1.67f,
            ["Md"]=1.73f, ["No"]=1.76f, ["Lr"]=1.61f,
            ["Rf"]=1.57f, ["Db"]=1.49f, ["Sg"]=1.43f, ["Bh"]=1.41f, ["Hs"]=1.34f, ["Mt"]=1.29f,
            ["Ds"]=1.28f, ["Rg"]=1.21f, ["Cn"]=1.22f, ["Nh"]=1.36f, ["Fl"]=1.43f,
            ["Mc"]=1.62f, ["Lv"]=1.75f, ["Ts"]=1.65f, ["Og"]=1.57f
        };

        public static readonly Dictionary<string, Color> DefaultColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            // Period 1
            ["H"] = new Color(1.00f, 1.00f, 1.00f),
            ["He"]= new Color(0.85f, 1.00f, 1.00f),
            // Period 2
            ["Li"]= new Color(0.80f, 0.50f, 1.00f),
            ["Be"]= new Color(0.76f, 1.00f, 0.00f),
            ["B"] = new Color(1.00f, 0.71f, 0.71f),
            ["C"] = new Color(0.20f, 0.20f, 0.20f),
            ["N"] = new Color(0.10f, 0.10f, 0.90f),
            ["O"] = new Color(0.90f, 0.10f, 0.10f),
            ["F"] = new Color(0.10f, 0.80f, 0.10f),
            ["Ne"]= new Color(0.70f, 0.89f, 0.96f),
            // Period 3
            ["Na"]= new Color(0.00f, 0.00f, 0.80f),
            ["Mg"]= new Color(0.40f, 0.80f, 0.40f),
            ["Al"]= new Color(0.70f, 0.70f, 0.80f),
            ["Si"]= new Color(0.80f, 0.60f, 0.30f),
            ["P"] = new Color(1.00f, 0.50f, 0.00f),
            ["S"] = new Color(1.00f, 0.90f, 0.10f),
            ["Cl"]= new Color(0.10f, 0.80f, 0.10f),
            ["Ar"]= new Color(0.50f, 0.82f, 0.89f),
            // Period 4
            ["K"] = new Color(0.50f, 0.00f, 1.00f),
            ["Ca"]= new Color(0.60f, 0.60f, 0.60f),
            ["Sc"]= new Color(0.90f, 0.90f, 0.90f),
            ["Ti"]= new Color(0.75f, 0.76f, 0.78f),
            ["V"] = new Color(0.65f, 0.65f, 0.67f),
            ["Cr"]= new Color(0.54f, 0.60f, 0.78f),
            ["Mn"]= new Color(0.61f, 0.48f, 0.78f),
            ["Fe"]= new Color(0.80f, 0.40f, 0.10f),
            ["Co"]= new Color(0.94f, 0.56f, 0.63f),
            ["Ni"]= new Color(0.31f, 0.82f, 0.31f),
            ["Cu"]= new Color(0.80f, 0.50f, 0.20f),
            ["Zn"]= new Color(0.60f, 0.60f, 0.90f),
            ["Ga"]= new Color(0.76f, 0.56f, 0.56f),
            ["Ge"]= new Color(0.40f, 0.56f, 0.56f),
            ["As"]= new Color(0.74f, 0.50f, 0.89f),
            ["Se"]= new Color(1.00f, 0.63f, 0.00f),
            ["Br"]= new Color(0.65f, 0.16f, 0.16f),
            ["Kr"]= new Color(0.36f, 0.72f, 0.82f),
            // Period 5
            ["Rb"]= new Color(0.44f, 0.18f, 0.69f),
            ["Sr"]= new Color(0.00f, 1.00f, 0.00f),
            ["Y"] = new Color(0.58f, 1.00f, 1.00f),
            ["Zr"]= new Color(0.58f, 0.88f, 0.88f),
            ["Nb"]= new Color(0.45f, 0.76f, 0.79f),
            ["Mo"]= new Color(0.33f, 0.71f, 0.71f),
            ["Tc"]= new Color(0.23f, 0.62f, 0.62f),
            ["Ru"]= new Color(0.14f, 0.56f, 0.56f),
            ["Rh"]= new Color(0.04f, 0.49f, 0.55f),
            ["Pd"]= new Color(0.00f, 0.41f, 0.52f),
            ["Ag"]= new Color(0.75f, 0.75f, 0.75f),
            ["Cd"]= new Color(1.00f, 0.85f, 0.56f),
            ["In"]= new Color(0.65f, 0.46f, 0.45f),
            ["Sn"]= new Color(0.40f, 0.50f, 0.50f),
            ["Sb"]= new Color(0.62f, 0.39f, 0.71f),
            ["Te"]= new Color(0.83f, 0.48f, 0.00f),
            ["I"] = new Color(0.58f, 0.00f, 0.58f),
            ["Xe"]= new Color(0.26f, 0.62f, 0.69f),
            // Period 6
            ["Cs"]= new Color(0.34f, 0.09f, 0.56f),
            ["Ba"]= new Color(0.00f, 0.79f, 0.00f),
            ["La"]= new Color(0.44f, 0.83f, 1.00f),
            ["Ce"]= new Color(1.00f, 1.00f, 0.78f),
            ["Pr"]= new Color(0.85f, 1.00f, 0.78f),
            ["Nd"]= new Color(0.78f, 1.00f, 0.78f),
            ["Pm"]= new Color(0.64f, 1.00f, 0.78f),
            ["Sm"]= new Color(0.56f, 1.00f, 0.78f),
            ["Eu"]= new Color(0.38f, 1.00f, 0.78f),
            ["Gd"]= new Color(0.27f, 1.00f, 0.78f),
            ["Tb"]= new Color(0.19f, 1.00f, 0.78f),
            ["Dy"]= new Color(0.12f, 1.00f, 0.78f),
            ["Ho"]= new Color(0.00f, 1.00f, 0.61f),
            ["Er"]= new Color(0.00f, 0.90f, 0.46f),
            ["Tm"]= new Color(0.00f, 0.83f, 0.32f),
            ["Yb"]= new Color(0.00f, 0.75f, 0.22f),
            ["Lu"]= new Color(0.00f, 0.67f, 0.14f),
            ["Hf"]= new Color(0.30f, 0.76f, 1.00f),
            ["Ta"]= new Color(0.30f, 0.65f, 1.00f),
            ["W"] = new Color(0.13f, 0.58f, 0.84f),
            ["Re"]= new Color(0.15f, 0.49f, 0.67f),
            ["Os"]= new Color(0.15f, 0.40f, 0.59f),
            ["Ir"]= new Color(0.09f, 0.33f, 0.53f),
            ["Pt"]= new Color(0.82f, 0.82f, 0.88f),
            ["Au"]= new Color(1.00f, 0.82f, 0.14f),
            ["Hg"]= new Color(0.72f, 0.72f, 0.82f),
            ["Tl"]= new Color(0.65f, 0.33f, 0.30f),
            ["Pb"]= new Color(0.34f, 0.35f, 0.38f),
            ["Bi"]= new Color(0.62f, 0.31f, 0.71f),
            ["Po"]= new Color(0.67f, 0.36f, 0.00f),
            ["At"]= new Color(0.46f, 0.31f, 0.27f),
            ["Rn"]= new Color(0.26f, 0.51f, 0.59f),
            // Period 7
            ["Fr"]= new Color(0.26f, 0.00f, 0.40f),
            ["Ra"]= new Color(0.00f, 0.49f, 0.00f),
            ["Ac"]= new Color(0.44f, 0.67f, 0.98f),
            ["Th"]= new Color(0.00f, 0.73f, 1.00f),
            ["Pa"]= new Color(0.00f, 0.63f, 1.00f),
            ["U"] = new Color(0.00f, 0.56f, 1.00f),
            ["Np"]= new Color(0.00f, 0.50f, 1.00f),
            ["Pu"]= new Color(0.00f, 0.42f, 1.00f),
            ["Am"]= new Color(0.33f, 0.36f, 0.95f),
            ["Cm"]= new Color(0.47f, 0.36f, 0.89f),
            ["Bk"]= new Color(0.54f, 0.31f, 0.89f),
            ["Cf"]= new Color(0.63f, 0.21f, 0.83f),
            ["Es"]= new Color(0.70f, 0.12f, 0.83f),
            ["Fm"]= new Color(0.70f, 0.12f, 0.73f),
            ["Md"]= new Color(0.70f, 0.05f, 0.65f),
            ["No"]= new Color(0.74f, 0.05f, 0.53f),
            ["Lr"]= new Color(0.78f, 0.00f, 0.40f),
            ["Rf"]= new Color(0.80f, 0.00f, 0.35f),
            ["Db"]= new Color(0.82f, 0.00f, 0.31f),
            ["Sg"]= new Color(0.85f, 0.00f, 0.27f),
            ["Bh"]= new Color(0.88f, 0.00f, 0.22f),
            ["Hs"]= new Color(0.90f, 0.00f, 0.18f),
            ["Mt"]= new Color(0.92f, 0.00f, 0.15f),
            ["Ds"]= new Color(0.92f, 0.00f, 0.15f),
            ["Rg"]= new Color(0.92f, 0.00f, 0.15f),
            ["Cn"]= new Color(0.92f, 0.00f, 0.15f),
            ["Nh"]= new Color(0.92f, 0.00f, 0.15f),
            ["Fl"]= new Color(0.92f, 0.00f, 0.15f),
            ["Mc"]= new Color(0.92f, 0.00f, 0.15f),
            ["Lv"]= new Color(0.92f, 0.00f, 0.15f),
            ["Ts"]= new Color(0.92f, 0.00f, 0.15f),
            ["Og"]= new Color(0.92f, 0.00f, 0.15f),
        };

        public static string CleanElement(string elem, string fallbackFromLabel)
        {
            if (!string.IsNullOrEmpty(elem))
            {
                elem = elem.Trim();
                elem = Regex.Replace(elem, @"[\d\+\-].*$", "");
            }
            if (string.IsNullOrEmpty(elem) && !string.IsNullOrEmpty(fallbackFromLabel))
            {
                var m = Regex.Match(fallbackFromLabel, @"^[A-Za-z]+");
                if (m.Success) elem = m.Value;
            }
            if (string.IsNullOrEmpty(elem)) elem = "C";
            elem = char.ToUpper(elem[0]) + (elem.Length > 1 ? elem.Substring(1).ToLowerInvariant() : "");
            return elem;
        }

        // The render colour for an element — same logic GetOrMakeMaterial uses, exposed for the legend.
        public static Color GetColor(string element)
        {
            return DefaultColors.TryGetValue(element, out var c)
                ? c
                : Color.HSVToRGB((Mathf.Abs(element.GetHashCode()) % 100) / 100f, 0.6f, 0.9f);
        }

        public static Material GetOrMakeMaterial(string element, Dictionary<string, Color> palette)
        {
            if (MatCache.TryGetValue(element, out var m) && m != null) return m;
            Color col = palette.TryGetValue(element, out var c) ? c : Color.HSVToRGB((Mathf.Abs(element.GetHashCode()) % 100) / 100f, 0.6f, 0.9f);
            m = new Material(SafeShader("Universal Render Pipeline/Lit"));
            m.name = $"Mat_{element}";
            m.color = col;
            MatCache[element] = m;
            return m;
        }

        public static Material GetBondMaterial()
        {
            if (bondMat != null) return bondMat;
            bondMat = new Material(SafeShader("Universal Render Pipeline/Lit"));
            bondMat.name = "Mat_Bond";
            bondMat.color = new Color(0.5f,0.5f,0.5f,1f);
            return bondMat;
        }

        public static Material GetWireMaterial()
        {
            if (wireMat != null) return wireMat;
            wireMat = new Material(Shader.Find("Sprites/Default"));
            wireMat.name = "Mat_UnitCellWire";
            wireMat.color = new Color(0.1f, 0.9f, 0.9f, 1f);
            return wireMat;
        }

        // Cell frame material. Uses URP/Lit (always bundled with the pipeline, so it survives
        // build shader-stripping) with emission so the frame stays evenly bright from any angle.
        public static Material GetCellFrameMaterial()
        {
            if (cellFrameMat != null) return cellFrameMat;
            cellFrameMat = new Material(SafeShader("Universal Render Pipeline/Lit"));
            cellFrameMat.name = "Mat_CellFrame";
            var col = new Color(0.1f, 0.9f, 0.9f, 1f);
            cellFrameMat.color = col;
            cellFrameMat.EnableKeyword("_EMISSION");
            cellFrameMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            cellFrameMat.SetColor("_EmissionColor", col);
            return cellFrameMat;
        }

        public static Material GetPolyhedralMaterial()
        {
            if (polyMat != null) return polyMat;
            polyMat = new Material(SafeShader("Universal Render Pipeline/Lit"));
            polyMat.name = "Mat_Polyhedral";
            // Opaque, double-sided, low-gloss solid — the VESTA-style faceted polyhedron look.
            polyMat.SetFloat("_Cull", 0f);          // double-sided (faces read regardless of winding)
            polyMat.SetFloat("_Smoothness", 0.1f);
            polyMat.color = Color.white;
            return polyMat;
        }

        public static Material GetMillerPlaneMaterial()
        {
            if (millerMat != null) return millerMat;
            // URP/Lit (always bundled) instead of Unlit (can be stripped from builds).
            // Emission keeps it flat/bright like an unlit plane; transparent + double-sided.
            millerMat = new Material(SafeShader("Universal Render Pipeline/Lit"));
            millerMat.name = "Mat_MillerPlane";
            millerMat.SetFloat("_Surface", 1f);
            millerMat.SetFloat("_Blend",   0f);
            millerMat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            millerMat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            millerMat.SetInt("_ZWrite",    0);
            millerMat.SetFloat("_Cull",    0f); // double-sided
            millerMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            millerMat.EnableKeyword("_EMISSION");
            millerMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            millerMat.renderQueue = 3000;
            var col = new Color(0.20f, 0.80f, 1.00f, 0.32f);
            millerMat.SetColor("_BaseColor",     col);
            millerMat.SetColor("_EmissionColor", new Color(col.r, col.g, col.b) * 0.6f);
            millerMat.color = col;
            return millerMat;
        }
    }

    // ----------------------------- Utility classes -----------------------------

    static class VisualizationUnits
    {
        // 1 Å shown as this many Unity units; tweak for scene scale.
        public const float AngstromToUnity = 0.1f;
    }

    class SpatialHash
    {
        private readonly float cell;
        private readonly Dictionary<(int,int,int), List<int>> buckets = new();
        public SpatialHash(float cell) { this.cell = Mathf.Max(1e-4f, cell); }

        private (int,int,int) Key(Vector3 p)
        {
            return ((int)Mathf.Floor(p.x / cell), (int)Mathf.Floor(p.y / cell), (int)Mathf.Floor(p.z / cell));
        }

        public void Add(int id, Vector3 p)
        {
            var k = Key(p);
            if (!buckets.TryGetValue(k, out var list)) buckets[k] = list = new List<int>();
            list.Add(id);
        }

        public IEnumerable<int> Candidates(Vector3 p)
        {
            var k = Key(p);
            for (int dx=-1; dx<=1; dx++)
            for (int dy=-1; dy<=1; dy++)
            for (int dz=-1; dz<=1; dz++)
            {
                var kk = (k.Item1+dx, k.Item2+dy, k.Item3+dz);
                if (buckets.TryGetValue(kk, out var list))
                    foreach (var id in list) yield return id;
            }
        }
    }

}

#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
static class CrystalCIFViewerEditorHooks
{
    static CrystalCIFViewerEditorHooks()
    {
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
        {
            foreach (var v in UnityEngine.Object.FindObjectsByType<CrystalCIFViewer>())
            {
                var clearMethod = typeof(CrystalCIFViewer).GetMethod(
                    "ClearGenerated",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                );
                clearMethod?.Invoke(v, null);
            }
        }
    }
}
#endif
