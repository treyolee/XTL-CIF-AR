// CrystalPlacard.cs — the "museum label" for the crystal in AR:
// mineral name, space group + crystal system, cell parameters, and the element legend,
// docked below the model and yaw-billboarding smoothly toward the viewer.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CrystalPlacard : MonoBehaviour
{
    public CrystalCIFViewer viewer;
    public Transform head;

    private Canvas _canvas;
    private UIText _title, _sg, _cellA, _cellB, _vol;
    private RectTransform _legendRow;
    private readonly List<GameObject> _legendItems = new();
    private Vector3 _dockOffset = new Vector3(0f, -0.34f, 0f);

    public void Build()
    {
        _canvas = WorldUIKit.MakeCanvas("CrystalPlacard", new Vector2(560, 340), transform);
        var root = _canvas.GetComponent<RectTransform>();

        WorldUIKit.MakePanel(root, new Vector2(560, 340), Vector2.zero,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

        _title = WorldUIKit.MakeText(root, "—", 44f, WorldUIStyle.Accent,
            new Vector2(520, 56), new Vector2(24, -18), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleLeft, bold: true);

        _sg = WorldUIKit.MakeText(root, "", 26f, WorldUIStyle.TextMain,
            new Vector2(520, 36), new Vector2(24, -78), new Vector2(0, 1), new Vector2(0, 1));

        _cellA = WorldUIKit.MakeText(root, "", 24f, WorldUIStyle.TextDim,
            new Vector2(520, 32), new Vector2(24, -122), new Vector2(0, 1), new Vector2(0, 1));

        _cellB = WorldUIKit.MakeText(root, "", 24f, WorldUIStyle.TextDim,
            new Vector2(520, 32), new Vector2(24, -156), new Vector2(0, 1), new Vector2(0, 1));

        _vol = WorldUIKit.MakeText(root, "", 24f, WorldUIStyle.TextDim,
            new Vector2(520, 32), new Vector2(24, -190), new Vector2(0, 1), new Vector2(0, 1));

        // legend row container
        var legendGO = new GameObject("Legend", typeof(RectTransform));
        legendGO.transform.SetParent(root, false);
        _legendRow = legendGO.GetComponent<RectTransform>();
        _legendRow.anchorMin = new Vector2(0, 1); _legendRow.anchorMax = new Vector2(0, 1);
        _legendRow.pivot = new Vector2(0, 1);
        _legendRow.sizeDelta = new Vector2(520, 80);
        _legendRow.anchoredPosition = new Vector2(24, -238);

        if (viewer != null)
        {
            viewer.OnCrystalBuilt += Refresh;
            if (viewer.LastBaseModel != null) Refresh(viewer.LastBaseModel);
        }
    }

    private void OnDestroy()
    {
        if (viewer != null) viewer.OnCrystalBuilt -= Refresh;
    }

    // Crystal-local bounds centre, cached per build — recomputing world bounds from all
    // atom transforms every frame caused GC/traversal hitches on Quest.
    private Vector3 _localBoundsCenter;
    private bool _hasBounds;

    private void Refresh(CrystalCIFViewer.CrystalModel m)
    {
        if (_title == null) return;
        CacheLocalBounds();
        var met = m.Metrics;
        _title.text = m.Title ?? "Crystal";
        string sys = CrystalInteraction.CrystalSystem(m.SpaceGroupNumber, m.SpaceGroupName);
        string sg  = m.SpaceGroupName ?? "?";
        _sg.text    = $"{sg}{(m.SpaceGroupNumber > 0 ? $"  (#{m.SpaceGroupNumber})" : "")}   ·   {sys}";
        _cellA.text = $"a {met.a:F3} Å    b {met.b:F3} Å    c {met.c:F3} Å";
        // Spelled-out angle names: TMP's default atlas has no Greek glyphs.
        _cellB.text = $"alpha {met.alpha:F2}°   beta {met.beta:F2}°   gamma {met.gamma:F2}°";
        float vol = CellVolume(met);
        _vol.text   = $"V {vol:F1} Å³    ·    {m.Atoms.Count} atoms (asym.)";

        RebuildLegend(m);
    }

    private static float CellVolume(CrystalCIFViewer.CellMetrics m)
    {
        float ca = Mathf.Cos(m.alpha * Mathf.Deg2Rad);
        float cb = Mathf.Cos(m.beta  * Mathf.Deg2Rad);
        float cg = Mathf.Cos(m.gamma * Mathf.Deg2Rad);
        return m.a * m.b * m.c * Mathf.Sqrt(Mathf.Max(0f,
            1f - ca * ca - cb * cb - cg * cg + 2f * ca * cb * cg));
    }

    private void RebuildLegend(CrystalCIFViewer.CrystalModel m)
    {
        foreach (var go in _legendItems) if (go != null) Destroy(go);
        _legendItems.Clear();

        var seen = new HashSet<string>();
        var elems = new List<string>();
        foreach (var a in m.Atoms) if (seen.Add(a.Element)) elems.Add(a.Element);

        float x = 0f, y = 0f;
        foreach (var e in elems)
        {
            var item = new GameObject("Lg_" + e, typeof(RectTransform));
            item.transform.SetParent(_legendRow, false);
            var rt = item.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(110, 34);
            rt.anchoredPosition = new Vector2(x, y);

            var swGO = new GameObject("Swatch", typeof(RectTransform));
            swGO.transform.SetParent(item.transform, false);
            var srt = swGO.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0.5f); srt.anchorMax = new Vector2(0, 0.5f); srt.pivot = new Vector2(0, 0.5f);
            srt.sizeDelta = new Vector2(24, 24);
            var sw = swGO.AddComponent<Image>();
            sw.sprite = WorldUIKit.CircleSprite();
            sw.color = CrystalCIFViewer.ElementStylings.GetColor(e);
            sw.raycastTarget = false;

            WorldUIKit.MakeText(item.transform, e, 24f, WorldUIStyle.TextMain,
                new Vector2(76, 34), new Vector2(32, 0), new Vector2(0, 0.5f), new Vector2(0, 0.5f));

            _legendItems.Add(item);
            x += 116f;
            if (x > 420f) { x = 0f; y -= 38f; }
        }
    }

    private void LateUpdate()
    {
        if (viewer == null || head == null || _canvas == null) return;
        if (!_hasBounds) CacheLocalBounds();

        // Dock beneath the crystal (cached local centre → world) and yaw toward the viewer.
        Vector3 center = viewer.transform.TransformPoint(_localBoundsCenter);
        Vector3 target = center + _dockOffset * Mathf.Max(1f, viewer.transform.localScale.x);
        transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * 6f);

        Vector3 toHead = head.position - transform.position; toHead.y = 0f;
        if (toHead.sqrMagnitude > 1e-4f)
        {
            var look = Quaternion.LookRotation(-toHead.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 6f);
        }
    }

    private void CacheLocalBounds()
    {
        var atoms = viewer.transform.GetComponentsInChildren<AtomInfo>();
        if (atoms.Length == 0) { _localBoundsCenter = Vector3.zero; return; }
        // Atom localPosition is in the crystal's local frame (atomsRoot sits at local identity).
        var b = new Bounds(atoms[0].transform.localPosition, Vector3.zero);
        foreach (var a in atoms) b.Encapsulate(a.transform.localPosition);
        _localBoundsCenter = b.center;
        _hasBounds = true;
    }
}
