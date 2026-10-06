// ControlTablet.cs — the grabbable tabbed control panel for the headset UI.
// Tabs: STYLE · MEASURE · BONDS · PLANES · PRESSURE · LIBRARY  (+ exit)
// Every control routes through the existing public APIs on CrystalCIFViewer /
// CrystalInteraction, so behaviour is identical to the desktop app.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ControlTablet : MonoBehaviour
{
    public CrystalCIFViewer viewer;
    public CrystalInteraction interaction;

    private const float W = 700f, H = 560f;   // px
    private const float RailW = 96f;
    private const float TabSpacing = 70f;     // 6 tabs + exit fit inside H

    private Canvas _canvas;
    private readonly List<(WorldButton btn, GameObject content)> _tabs = new();
    private readonly WorldButton[] _styleButtons = new WorldButton[5];
    private readonly WorldButton[] _modeButtons  = new WorldButton[4];
    private WorldSlider _bondMin, _bondMax, _pressure;
    private UIText _bondMinVal, _bondMaxVal, _pressureVal, _pressureInfo, _millerReadout, _k0Val;
    private WorldToggle _cellToggle, _axesToggle, _millerToggle;
    private RectTransform _libraryList;
    private bool _millerShow;
    private int _activeTab = -1;

    public void Build()
    {
        _canvas = WorldUIKit.MakeCanvas("ControlTablet", new Vector2(W, H), transform);
        var root = _canvas.GetComponent<RectTransform>();

        WorldUIKit.MakePanel(root, new Vector2(W, H), Vector2.zero,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

        BuildHeader(root);
        BuildTabRail(root);

        // content containers
        var styleC    = MakeContent(root); BuildStyleTab(styleC);
        var measureC  = MakeContent(root); BuildMeasureTab(measureC);
        var bondsC    = MakeContent(root); BuildBondsTab(bondsC);
        var planesC   = MakeContent(root); BuildPlanesTab(planesC);
        var pressureC = MakeContent(root); BuildPressureTab(pressureC);
        var libC      = MakeContent(root); BuildLibraryTab(libC);

        AddTab(root, 0, IconFactory.BallStick(),  styleC.gameObject);
        AddTab(root, 1, IconFactory.Measure(),    measureC.gameObject);
        AddTab(root, 2, IconFactory.Bonds(),      bondsC.gameObject);
        AddTab(root, 3, IconFactory.Planes(),     planesC.gameObject);
        AddTab(root, 4, IconFactory.Pressure(),   pressureC.gameObject);
        AddTab(root, 5, IconFactory.Library(),    libC.gameObject);

        // exit at the bottom of the rail (its own slot below the last tab)
        var exit = WorldUIKit.MakeButton(root, null, IconFactory.Power(),
            new Vector2(64f, 64f), new Vector2(16f, -(H - 72f)), QuitApp);
        exit.SetTint(new Color(0.30f, 0.09f, 0.09f, 0.95f));

        SelectTab(0);
        CifAndroidBootstrap.LibraryReady += OnLibraryReady;

        if (viewer != null)
        {
            viewer.OnCrystalBuilt += _ => RefreshFromViewer();
            RefreshFromViewer();
        }
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    private void BuildHeader(RectTransform root)
    {
        // The header doubles as the drag handle (pinch here to move the tablet).
        var header = new GameObject("Header", typeof(RectTransform));
        header.transform.SetParent(root, false);
        var hrt = header.GetComponent<RectTransform>();
        hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(0, 1); hrt.pivot = new Vector2(0, 1);
        hrt.sizeDelta = new Vector2(W, 56f); hrt.anchoredPosition = Vector2.zero;

        var img = header.AddComponent<Image>();
        img.sprite = WorldUIKit.FillSprite(); img.type = Image.Type.Sliced;
        img.color = new Color(1, 1, 1, 0.05f);

        WorldUIKit.MakeText(header.transform, "CRYSTAL CONSOLE", 28f, WorldUIStyle.Accent,
            new Vector2(400, 56), new Vector2(24, 0), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
            TextAnchor.MiddleLeft, bold: true);
        WorldUIKit.MakeText(header.transform, "pinch here to move", 20f, WorldUIStyle.TextDim,
            new Vector2(220, 56), new Vector2(-24, 0), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
            TextAnchor.MiddleRight);

        var col = header.AddComponent<BoxCollider>();
        col.size = new Vector3(W, 56f, 8f);
        col.center = new Vector3(W * 0.5f, -28f, 0);

        // Hands: index-pinch the header and move to carry the console. Controllers: grip also works.
        var drag = header.AddComponent<WorldDragHandle>();
        drag.target = transform;
        drag.highlight = img;
        WorldUIKit.RegisterInteractable(col, drag);
        var grab = header.AddComponent<WorldGrabbable>();
        grab.target = transform;
    }

    private void BuildTabRail(RectTransform root)
    {
        var rail = new GameObject("Rail", typeof(RectTransform));
        rail.transform.SetParent(root, false);
        var rrt = rail.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0, 0); rrt.anchorMax = new Vector2(0, 1); rrt.pivot = new Vector2(0, 0.5f);
        rrt.sizeDelta = new Vector2(RailW, -56f); rrt.anchoredPosition = new Vector2(0, -28f);
        var img = rail.AddComponent<Image>();
        img.sprite = WorldUIKit.FillSprite(); img.type = Image.Type.Sliced;
        img.color = new Color(0, 0, 0, 0.28f);
        img.raycastTarget = false;
    }

    private RectTransform MakeContent(RectTransform root)
    {
        var go = new GameObject("Content", typeof(RectTransform));
        go.transform.SetParent(root, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(W - RailW - 40f, H - 76f);
        rt.anchoredPosition = new Vector2(RailW + 24f, -68f);
        go.SetActive(false);
        return rt;
    }

    private void AddTab(RectTransform root, int index, Sprite icon, GameObject content)
    {
        var btn = WorldUIKit.MakeButton(root, null, icon, new Vector2(64f, 64f),
            new Vector2(16f, -(66f + index * TabSpacing)), () => SelectTab(index));
        _tabs.Add((btn, content));
    }

    private void SelectTab(int index)
    {
        if (index == 5) RefreshLibrary();   // rescan on open
        _activeTab = index;
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].btn.SetSelected(i == index);
            _tabs[i].content.SetActive(i == index);
        }
    }

    // ── STYLE ────────────────────────────────────────────────────────────────

    private static readonly string[] StyleNames =
        { "Ball & Stick", "Space-filling", "Stick", "Wireframe", "Polyhedral" };

    private void BuildStyleTab(RectTransform c)
    {
        Header(c, "DISPLAY STYLE");
        Sprite[] icons = { IconFactory.BallStick(), IconFactory.SpaceFill(), IconFactory.Stick(),
                           IconFactory.Wireframe(), IconFactory.Polyhedral() };
        for (int i = 0; i < 5; i++)
        {
            int captured = i;
            _styleButtons[i] = WorldUIKit.MakeButton(c, StyleNames[i], icons[i],
                new Vector2(268f, 58f),
                new Vector2((i % 2) * 288f, -(48f + (i / 2) * 70f)),
                () => { viewer.SetDisplayStyle((DisplayStyle)captured); RefreshStyleButtons(); });
        }

        Header(c, "OVERLAYS", y: -280f);
        _cellToggle = WorldUIKit.MakeToggle(c, "Unit cell", viewer == null || viewer.showUnitCell,
            new Vector2(0, -324f), v => viewer.SetUnitCellVisible(v));
        _axesToggle = WorldUIKit.MakeToggle(c, "a b c axes", viewer == null || viewer.showAxisIndicator,
            new Vector2(288f, -324f), v => viewer.SetAxisVisible(v));
    }

    private void RefreshStyleButtons()
    {
        for (int i = 0; i < 5; i++)
            _styleButtons[i]?.SetSelected(viewer != null && (int)viewer.displayStyle == i);
    }

    // ── MEASURE ──────────────────────────────────────────────────────────────

    private static readonly string[] ModeNames = { "Info", "Distance", "Angle", "Torsion" };

    private void BuildMeasureTab(RectTransform c)
    {
        Header(c, "MEASUREMENT MODE");
        for (int i = 0; i < 4; i++)
        {
            int captured = i;
            _modeButtons[i] = WorldUIKit.MakeButton(c, ModeNames[i], null,
                new Vector2(268f, 56f),
                new Vector2((i % 2) * 288f, -(48f + (i / 2) * 68f)),
                () =>
                {
                    interaction?.SetMode((CrystalInteraction.MeasureMode)captured);
                    RefreshModeButtons(captured);
                });
        }
        RefreshModeButtons(1); // Distance default

        WorldUIKit.MakeButton(c, "Clear selection", IconFactory.Close(),
            new Vector2(268f, 56f), new Vector2(0, -212f),
            () => interaction?.ClearSelection());

        WorldUIKit.MakeText(c, "Point at an atom and pull the trigger to select.\nResults appear beside the crystal.",
            22f, WorldUIStyle.TextDim, new Vector2(540, 80), new Vector2(0, -292f),
            new Vector2(0, 1), new Vector2(0, 1), TextAnchor.UpperLeft);
    }

    private void RefreshModeButtons(int active)
    {
        for (int i = 0; i < 4; i++) _modeButtons[i]?.SetSelected(i == active);
    }

    // ── BONDS ────────────────────────────────────────────────────────────────

    private void BuildBondsTab(RectTransform c)
    {
        Header(c, "BOND LENGTH WINDOW");

        WorldUIKit.MakeText(c, "Min", 24f, WorldUIStyle.TextDim, new Vector2(70, 40),
            new Vector2(0, -56f), new Vector2(0, 1), new Vector2(0, 1));
        _bondMin = WorldUIKit.MakeSlider(c, 0f, 4f, viewer != null ? viewer.bondMinAngstrom : 0f,
            new Vector2(370f, 40f), new Vector2(80f, -56f),
            v => { if (_bondMinVal != null) _bondMinVal.text = $"{v:F2} Å"; },
            () => viewer.SetBondRange(_bondMin.Value, _bondMax.Value));
        _bondMinVal = WorldUIKit.MakeText(c, "", 24f, WorldUIStyle.TextMain, new Vector2(100, 40),
            new Vector2(462f, -56f), new Vector2(0, 1), new Vector2(0, 1));

        WorldUIKit.MakeText(c, "Max", 24f, WorldUIStyle.TextDim, new Vector2(70, 40),
            new Vector2(0, -126f), new Vector2(0, 1), new Vector2(0, 1));
        _bondMax = WorldUIKit.MakeSlider(c, 0f, 4f, viewer != null ? viewer.bondMaxAngstrom : 2f,
            new Vector2(370f, 40f), new Vector2(80f, -126f),
            v => { if (_bondMaxVal != null) _bondMaxVal.text = $"{v:F2} Å"; },
            () => viewer.SetBondRange(_bondMin.Value, _bondMax.Value));
        _bondMaxVal = WorldUIKit.MakeText(c, "", 24f, WorldUIStyle.TextMain, new Vector2(100, 40),
            new Vector2(462f, -126f), new Vector2(0, 1), new Vector2(0, 1));

        WorldUIKit.MakeText(c,
            "Defaults come from the CIF (or covalent radii)\nwhen a structure loads.",
            22f, WorldUIStyle.TextDim, new Vector2(540, 70), new Vector2(0, -200f),
            new Vector2(0, 1), new Vector2(0, 1), TextAnchor.UpperLeft);
    }

    // ── PLANES ───────────────────────────────────────────────────────────────

    private void BuildPlanesTab(RectTransform c)
    {
        Header(c, "MILLER (hkl) PLANES");

        _millerToggle = WorldUIKit.MakeToggle(c, "Show planes", _millerShow,
            new Vector2(0, -52f), v => { _millerShow = v; ApplyMiller(); });

        WorldUIKit.MakeStepper(c, "h", new Vector2(0, -120f),
            () => viewer.millerH, v => viewer.millerH = v, 1, -6, 6, ApplyMiller);
        WorldUIKit.MakeStepper(c, "k", new Vector2(0, -176f),
            () => viewer.millerK, v => viewer.millerK = v, 1, -6, 6, ApplyMiller);
        WorldUIKit.MakeStepper(c, "l", new Vector2(0, -232f),
            () => viewer.millerL, v => viewer.millerL = v, 1, -6, 6, ApplyMiller);

        _millerReadout = WorldUIKit.MakeText(c, "", 46f, WorldUIStyle.Accent,
            new Vector2(240, 70), new Vector2(300f, -160f), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleCenter, bold: true);
        UpdateMillerReadout();
    }

    private void ApplyMiller()
    {
        viewer?.SetMillerPlane(viewer.millerH, viewer.millerK, viewer.millerL, _millerShow);
        UpdateMillerReadout();
    }

    private void UpdateMillerReadout()
    {
        if (_millerReadout != null && viewer != null)
            _millerReadout.text = $"({viewer.millerH} {viewer.millerK} {viewer.millerL})";
    }

    // ── PRESSURE ─────────────────────────────────────────────────────────────

    private void BuildPressureTab(RectTransform c)
    {
        Header(c, "PRESSURE  ·  BIRCH–MURNAGHAN");

        _pressureVal = WorldUIKit.MakeText(c, "0.0 GPa", 40f, WorldUIStyle.Accent,
            new Vector2(240, 56), new Vector2(0, -46f), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleLeft, bold: true);

        _pressure = WorldUIKit.MakeSlider(c, 0f, 136f, viewer != null ? viewer.pressureGPa : 0f,
            new Vector2(540f, 40f), new Vector2(0, -112f),
            v =>
            {
                if (_pressureVal != null) _pressureVal.text = $"{v:F1} GPa";
                UpdatePressureInfo(v);
            },
            () => viewer.SetPressure(_pressure.Value));

        _k0Val = WorldUIKit.MakeStepper(c, "K0", new Vector2(0, -184f),
            () => Mathf.RoundToInt(viewer.bulkModulusK0),
            v => viewer.bulkModulusK0 = v, 5, 5, 500,
            () => { viewer.SetBulkModulus(viewer.bulkModulusK0); UpdatePressureInfo(_pressure.Value); });
        WorldUIKit.MakeText(c, "GPa (bulk modulus)", 22f, WorldUIStyle.TextDim,
            new Vector2(260, 44), new Vector2(240f, -184f), new Vector2(0, 1), new Vector2(0, 1));

        _pressureInfo = WorldUIKit.MakeText(c, "", 24f, WorldUIStyle.TextMain,
            new Vector2(540, 40), new Vector2(0, -252f), new Vector2(0, 1), new Vector2(0, 1));
        UpdatePressureInfo(viewer != null ? viewer.pressureGPa : 0f);
    }

    private void UpdatePressureInfo(float gpa)
    {
        if (_pressureInfo == null || viewer == null) return;
        float s = viewer.PredictLinearCompression(gpa);
        _pressureInfo.text = $"bonds -{(1f - s) * 100f:F1}%     cell -{(1f - s * s * s) * 100f:F1}%";
    }

    // ── LIBRARY ──────────────────────────────────────────────────────────────
    // Paged two-column grid. Big, pinch-friendly targets — drag-scrolling with a hand ray is
    // unreliable, paging is not. 12 per page; opens on the page holding the current structure.

    private const int LibCols = 2, LibRows = 6, LibPerPage = LibCols * LibRows;
    private const float LibW = 564f;
    private List<(string name, string path)> _libFiles = new();
    private int _libPage;
    private UIText _libPageText, _libCountText;
    private WorldButton _libPrev, _libNext;

    private void BuildLibraryTab(RectTransform c)
    {
        Header(c, "STRUCTURE LIBRARY");
        _libCountText = WorldUIKit.MakeText(c, "", 22f, WorldUIStyle.TextDim,
            new Vector2(240, 36), new Vector2(LibW - 240f, 0), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleRight);

        var listGO = new GameObject("List", typeof(RectTransform));
        listGO.transform.SetParent(c, false);
        _libraryList = listGO.GetComponent<RectTransform>();
        _libraryList.anchorMin = new Vector2(0, 1); _libraryList.anchorMax = new Vector2(0, 1);
        _libraryList.pivot = new Vector2(0, 1);
        _libraryList.sizeDelta = new Vector2(LibW, LibRows * 60f);
        _libraryList.anchoredPosition = new Vector2(0, -46f);

        // Pager: ‹  page / pages  ›
        _libPrev = WorldUIKit.MakeButton(c, null, IconFactory.ChevronLeft(), new Vector2(64f, 52f),
            new Vector2(0f, -416f), () => { _libPage--; RefreshLibrary(rescan: false, jumpToCurrent: false); });
        _libNext = WorldUIKit.MakeButton(c, null, IconFactory.ChevronRight(), new Vector2(64f, 52f),
            new Vector2(LibW - 64f, -416f), () => { _libPage++; RefreshLibrary(rescan: false, jumpToCurrent: false); });
        _libPageText = WorldUIKit.MakeText(c, "", 26f, WorldUIStyle.TextMain,
            new Vector2(LibW - 160f, 52f), new Vector2(80f, -416f), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleCenter, bold: true);
    }

    private void RefreshLibrary(bool rescan = true, bool jumpToCurrent = true)
    {
        if (_libraryList == null || interaction == null) return;
        if (rescan) _libFiles = interaction.ScanCifs();

        string current = viewer != null ? viewer.CurrentStructureName : "";
        int pages = Mathf.Max(1, Mathf.CeilToInt(_libFiles.Count / (float)LibPerPage));
        if (jumpToCurrent && !string.IsNullOrEmpty(current))
        {
            int idx = _libFiles.FindIndex(f =>
                string.Equals(f.name, current, System.StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _libPage = idx / LibPerPage;
        }
        _libPage = Mathf.Clamp(_libPage, 0, pages - 1);

        for (int i = _libraryList.childCount - 1; i >= 0; i--)
        {
            var child = _libraryList.GetChild(i).gameObject;
            child.SetActive(false);   // colliders off immediately (Destroy is deferred to frame end)
            Destroy(child);
        }

        if (_libFiles.Count == 0)
            WorldUIKit.MakeText(_libraryList, "No CIF files found.", 24f, WorldUIStyle.TextDim,
                new Vector2(540, 40), Vector2.zero, new Vector2(0, 1), new Vector2(0, 1));

        int start = _libPage * LibPerPage;
        for (int i = 0; i < LibPerPage && start + i < _libFiles.Count; i++)
        {
            var f = _libFiles[start + i];
            int col = i % LibCols, row = i / LibCols;
            bool isCurrent = string.Equals(f.name, current, System.StringComparison.OrdinalIgnoreCase);
            string captured = f.path;
            var b = WorldUIKit.MakeButton(_libraryList, CrystalInteraction.PrettyName(f.name), null,
                new Vector2(276f, 52f), new Vector2(col * 288f, -row * 60f),
                () => viewer.LoadCifFromPath(captured), fontSize: 24f);
            if (isCurrent) b.SetSelected(true);
        }

        if (_libPageText != null)  _libPageText.text  = pages > 1 ? $"{_libPage + 1} / {pages}" : "";
        if (_libCountText != null) _libCountText.text = $"{_libFiles.Count} structures";
        if (_libPrev != null) _libPrev.gameObject.SetActive(_libPage > 0);
        if (_libNext != null) _libNext.gameObject.SetActive(_libPage < pages - 1);
    }

    // On Quest the bundled CIFs are copied out of the APK at startup; refresh if the library
    // tab is already open when that finishes.
    private void OnLibraryReady()
    {
        if (_activeTab == 5) RefreshLibrary(rescan: true, jumpToCurrent: false);
    }

    private void OnDestroy()
    {
        CifAndroidBootstrap.LibraryReady -= OnLibraryReady;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private void Header(RectTransform c, string text, float y = 0f)
    {
        WorldUIKit.MakeText(c, text, 24f, WorldUIStyle.TextDim,
            new Vector2(560, 36), new Vector2(0, y), new Vector2(0, 1), new Vector2(0, 1),
            TextAnchor.MiddleLeft, bold: true);
    }

    /// <summary>Sync widgets to viewer state (called on every crystal rebuild).</summary>
    public void RefreshFromViewer()
    {
        if (viewer == null) return;
        RefreshStyleButtons();
        _bondMin?.SetWithoutNotify(viewer.bondMinAngstrom);
        _bondMax?.SetWithoutNotify(viewer.bondMaxAngstrom);
        if (_bondMinVal != null) _bondMinVal.text = $"{viewer.bondMinAngstrom:F2} Å";
        if (_bondMaxVal != null) _bondMaxVal.text = $"{viewer.bondMaxAngstrom:F2} Å";
        _pressure?.SetWithoutNotify(viewer.pressureGPa);
        if (_pressureVal != null) _pressureVal.text = $"{viewer.pressureGPa:F1} GPa";
        if (_k0Val != null) _k0Val.text = Mathf.RoundToInt(viewer.bulkModulusK0).ToString();
        UpdatePressureInfo(viewer.pressureGPa);
        _cellToggle?.SetWithoutNotify(viewer.showUnitCell);
        _axesToggle?.SetWithoutNotify(viewer.showAxisIndicator);
        UpdateMillerReadout();
        if (_activeTab == 5) RefreshLibrary(rescan: false, jumpToCurrent: false);
    }

    private void QuitApp()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
