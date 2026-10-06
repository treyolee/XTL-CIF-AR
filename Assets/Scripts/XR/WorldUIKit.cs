// WorldUIKit.cs — world-space UI widget kit for the Quest spatial interface.
// Dark "glass" panels with cyan accent glow, procedurally generated sprites (no binary assets),
// TextMeshPro text with a legacy-Text fallback, and physics-collider interactables driven by
// XRRayInteractor (no EventSystem dependency).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// ─── Interaction contract (implemented by widgets, driven by XRRayInteractor) ───

public interface IWorldInteractable
{
    void OnHoverEnter();
    void OnHoverExit();
    void OnPress(Vector3 worldPoint);
    void OnDrag(Ray ray);
    void OnRelease();
}

/// <summary>Marks a root that can be grabbed (grip) and carried by a controller.</summary>
public class WorldGrabbable : MonoBehaviour
{
    [Tooltip("Transform moved when grabbed. Defaults to this transform.")]
    public Transform target;
    private void Awake() { if (target == null) target = transform; }
}

// ─── Style palette ─────────────────────────────────────────────────────────────

public static class WorldUIStyle
{
    public static readonly Color PanelFill    = new Color(0.030f, 0.055f, 0.085f, 0.92f);
    public static readonly Color PanelRim     = new Color(0.20f, 0.85f, 1.00f, 0.85f);
    public static readonly Color Accent       = new Color(0.21f, 0.88f, 1.00f, 1f);
    public static readonly Color AccentDim    = new Color(0.21f, 0.88f, 1.00f, 0.35f);
    public static readonly Color ButtonIdle   = new Color(0.075f, 0.115f, 0.16f, 0.95f);
    public static readonly Color ButtonHover  = new Color(0.11f, 0.19f, 0.26f, 0.98f);
    public static readonly Color ButtonActive = new Color(0.10f, 0.30f, 0.38f, 1f);
    public static readonly Color TextMain     = new Color(0.92f, 0.97f, 1.00f, 1f);
    public static readonly Color TextDim      = new Color(0.60f, 0.72f, 0.80f, 1f);
    public static readonly Color Danger       = new Color(1.00f, 0.38f, 0.34f, 1f);
}

// ─── Text wrapper: TMP preferred, legacy fallback ──────────────────────────────

public class UIText
{
    public TMPro.TMP_Text tmp;
    public Text legacy;
    public GameObject gameObject;
    public RectTransform rect;

    public string text
    {
        get => tmp != null ? tmp.text : (legacy != null ? legacy.text : "");
        set { if (tmp != null) tmp.text = value; else if (legacy != null) legacy.text = value; }
    }
    public Color color
    {
        set { if (tmp != null) tmp.color = value; else if (legacy != null) legacy.color = value; }
    }
}

// ─── Widget kit ────────────────────────────────────────────────────────────────

public static class WorldUIKit
{
    private static Sprite _fillSprite;     // rounded rect, white (tintable)
    private static Sprite _outlineSprite;  // rounded rect ring, white (tintable)
    private static Sprite _circleSprite;   // soft circle, white
    private static Font   _fallbackFont;

    public const float CanvasScale = 0.0006f; // 1 px = 0.6 mm

    /// <summary>Collider → widget map for allocation-free ray-hit lookup (per-frame hot path).</summary>
    public static readonly Dictionary<Collider, IWorldInteractable> InteractableRegistry = new();

    // Register a widget's collider for ray lookup, and auto-unregister when it's destroyed
    // (paged lists rebuild their buttons on every page turn).
    private static void Register(Collider col, IWorldInteractable widget)
    {
        InteractableRegistry[col] = widget;
        col.gameObject.AddComponent<WorldInteractableHandle>().key = col;
    }

    /// <summary>Register a custom widget's collider for ray hover/press (auto-unregisters on destroy).</summary>
    public static void RegisterInteractable(Collider col, IWorldInteractable widget) => Register(col, widget);

    // ── Canvas / panel construction ───────────────────────────────────────────

    /// <summary>World-space canvas of the given pixel size (scaled to metres by CanvasScale).</summary>
    public static Canvas MakeCanvas(string name, Vector2 sizePx, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (parent != null) go.transform.SetParent(parent, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta   = sizePx;
        rt.localScale  = Vector3.one * CanvasScale;
        return canvas;
    }

    /// <summary>Glass panel: dark fill + thin accent rim.</summary>
    public static RectTransform MakePanel(Transform parent, Vector2 sizePx, Vector2 anchoredPos,
                                          Vector2 anchor, Vector2 pivot)
    {
        var go = new GameObject("Panel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
        rt.sizeDelta = sizePx; rt.anchoredPosition = anchoredPos;

        var fill = go.AddComponent<Image>();
        fill.sprite = FillSprite(); fill.type = Image.Type.Sliced;
        fill.color  = WorldUIStyle.PanelFill;

        var rimGO = new GameObject("Rim", typeof(RectTransform));
        rimGO.transform.SetParent(go.transform, false);
        var rrt = rimGO.GetComponent<RectTransform>();
        rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one;
        rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
        var rim = rimGO.AddComponent<Image>();
        rim.sprite = OutlineSprite(); rim.type = Image.Type.Sliced;
        rim.color  = new Color(WorldUIStyle.PanelRim.r, WorldUIStyle.PanelRim.g, WorldUIStyle.PanelRim.b, 0.35f);
        rim.raycastTarget = false;
        return rt;
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    public static UIText MakeText(Transform parent, string content, float sizePx, Color color,
                                  Vector2 sizeDelta, Vector2 anchoredPos, Vector2 anchor, Vector2 pivot,
                                  TextAnchor align = TextAnchor.MiddleLeft, bool bold = false)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
        rt.sizeDelta = sizeDelta; rt.anchoredPosition = anchoredPos;

        var result = new UIText { gameObject = go, rect = rt };

        TMPro.TextMeshProUGUI tmp = null;
        try
        {
            tmp = go.AddComponent<TMPro.TextMeshProUGUI>();
            if (tmp.font == null) { UnityEngine.Object.DestroyImmediate(tmp); tmp = null; }
        }
        catch (Exception) { tmp = null; }

        if (tmp != null)
        {
            tmp.text = content;
            tmp.fontSize = sizePx;
            tmp.color = color;
            tmp.fontStyle = bold ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
            tmp.alignment = ToTmpAlign(align);
            tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            result.tmp = tmp;
        }
        else
        {
            var t = go.AddComponent<Text>();
            t.font = FallbackFont();
            t.text = content;
            t.fontSize = Mathf.RoundToInt(sizePx);
            t.color = color;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow   = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            result.legacy = t;
        }
        return result;
    }

    private static TMPro.TextAlignmentOptions ToTmpAlign(TextAnchor a) => a switch
    {
        TextAnchor.UpperLeft    => TMPro.TextAlignmentOptions.TopLeft,
        TextAnchor.UpperCenter  => TMPro.TextAlignmentOptions.Top,
        TextAnchor.UpperRight   => TMPro.TextAlignmentOptions.TopRight,
        TextAnchor.MiddleLeft   => TMPro.TextAlignmentOptions.Left,
        TextAnchor.MiddleCenter => TMPro.TextAlignmentOptions.Center,
        TextAnchor.MiddleRight  => TMPro.TextAlignmentOptions.Right,
        TextAnchor.LowerLeft    => TMPro.TextAlignmentOptions.BottomLeft,
        TextAnchor.LowerCenter  => TMPro.TextAlignmentOptions.Bottom,
        _                       => TMPro.TextAlignmentOptions.BottomRight,
    };

    // ── Widgets ───────────────────────────────────────────────────────────────

    public static WorldButton MakeButton(Transform parent, string label, Sprite icon,
                                         Vector2 sizePx, Vector2 anchoredPos, Action onClick,
                                         Vector2? anchor = null, float fontSize = 26f)
    {
        var a = anchor ?? new Vector2(0, 1);
        var go = new GameObject("Btn_" + label, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = a; rt.anchorMax = a; rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = sizePx; rt.anchoredPosition = anchoredPos;

        var bg = go.AddComponent<Image>();
        bg.sprite = FillSprite(); bg.type = Image.Type.Sliced;
        bg.color  = WorldUIStyle.ButtonIdle;

        var glowGO = new GameObject("Glow", typeof(RectTransform));
        glowGO.transform.SetParent(go.transform, false);
        var grt = glowGO.GetComponent<RectTransform>();
        grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one;
        grt.offsetMin = Vector2.zero; grt.offsetMax = Vector2.zero;
        var glow = glowGO.AddComponent<Image>();
        glow.sprite = OutlineSprite(); glow.type = Image.Type.Sliced;
        glow.color  = new Color(0, 0, 0, 0);
        glow.raycastTarget = false;

        float textX = 0f;
        if (icon != null)
        {
            var icGO = new GameObject("Icon", typeof(RectTransform));
            icGO.transform.SetParent(go.transform, false);
            var irt = icGO.GetComponent<RectTransform>();
            float iconSize = Mathf.Min(sizePx.y - 14f, 44f);
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(0, 0.5f);
            irt.pivot = new Vector2(0, 0.5f);
            irt.sizeDelta = new Vector2(iconSize, iconSize);
            irt.anchoredPosition = new Vector2(12f, 0);
            var img = icGO.AddComponent<Image>();
            img.sprite = icon; img.color = WorldUIStyle.TextMain; img.raycastTarget = false;
            textX = iconSize + 20f;
        }

        UIText txt = null;
        if (!string.IsNullOrEmpty(label))
        {
            txt = MakeText(go.transform, label, fontSize, WorldUIStyle.TextMain,
                new Vector2(sizePx.x - textX - 8f, sizePx.y),
                new Vector2(textX + (icon != null ? 0f : 0f), 0),
                new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                icon != null ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
            if (icon == null)
            {
                txt.rect.anchorMin = new Vector2(0.5f, 0.5f); txt.rect.anchorMax = new Vector2(0.5f, 0.5f);
                txt.rect.pivot = new Vector2(0.5f, 0.5f); txt.rect.anchoredPosition = Vector2.zero;
            }
        }

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(sizePx.x, sizePx.y, 8f);
        col.center = new Vector3(sizePx.x * 0.5f, -sizePx.y * 0.5f, 0); // pivot top-left

        var btn = go.AddComponent<WorldButton>();
        btn.Init(bg, glow, txt, onClick);
        Register(col, btn);
        return btn;
    }

    public static WorldToggle MakeToggle(Transform parent, string label, bool isOn,
                                         Vector2 anchoredPos, Action<bool> onChanged, float width = 220f)
    {
        var go = new GameObject("Tgl_" + label, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(width, 44f); rt.anchoredPosition = anchoredPos;

        // pill track
        var trackGO = new GameObject("Track", typeof(RectTransform));
        trackGO.transform.SetParent(go.transform, false);
        var trt = trackGO.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0, 0.5f); trt.anchorMax = new Vector2(0, 0.5f); trt.pivot = new Vector2(0, 0.5f);
        trt.sizeDelta = new Vector2(64f, 30f); trt.anchoredPosition = new Vector2(0, 0);
        var track = trackGO.AddComponent<Image>();
        track.sprite = FillSprite(); track.type = Image.Type.Sliced;

        // knob
        var knobGO = new GameObject("Knob", typeof(RectTransform));
        knobGO.transform.SetParent(trackGO.transform, false);
        var krt = knobGO.GetComponent<RectTransform>();
        krt.anchorMin = new Vector2(0, 0.5f); krt.anchorMax = new Vector2(0, 0.5f); krt.pivot = new Vector2(0.5f, 0.5f);
        krt.sizeDelta = new Vector2(24f, 24f);
        var knob = knobGO.AddComponent<Image>();
        knob.sprite = CircleSprite(); knob.raycastTarget = false;

        var txt = MakeText(go.transform, label, 24f, WorldUIStyle.TextMain,
            new Vector2(width - 76f, 44f), new Vector2(76f, 0),
            new Vector2(0, 0.5f), new Vector2(0, 0.5f));

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(width, 44f, 8f);
        col.center = new Vector3(width * 0.5f, -22f, 0);

        var tgl = go.AddComponent<WorldToggle>();
        tgl.Init(track, knob, krt, isOn, onChanged);
        Register(col, tgl);
        return tgl;
    }

    public static WorldSlider MakeSlider(Transform parent, float min, float max, float value,
                                         Vector2 sizePx, Vector2 anchoredPos,
                                         Action<float> onChanged, Action onCommit)
    {
        var go = new GameObject("Slider", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = sizePx; rt.anchoredPosition = anchoredPos;

        var trackGO = new GameObject("Track", typeof(RectTransform));
        trackGO.transform.SetParent(go.transform, false);
        var trt = trackGO.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0, 0.5f); trt.anchorMax = new Vector2(1, 0.5f); trt.pivot = new Vector2(0.5f, 0.5f);
        trt.offsetMin = new Vector2(0, -6f); trt.offsetMax = new Vector2(0, 6f);
        var track = trackGO.AddComponent<Image>();
        track.sprite = FillSprite(); track.type = Image.Type.Sliced;
        track.color = new Color(1, 1, 1, 0.10f);
        track.raycastTarget = false;

        var fillGO = new GameObject("Fill", typeof(RectTransform));
        fillGO.transform.SetParent(trackGO.transform, false);
        var frt = fillGO.GetComponent<RectTransform>();
        frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(0, 1); frt.pivot = new Vector2(0, 0.5f);
        frt.anchoredPosition = Vector2.zero;
        var fill = fillGO.AddComponent<Image>();
        fill.sprite = FillSprite(); fill.type = Image.Type.Sliced;
        fill.color = WorldUIStyle.Accent;
        fill.raycastTarget = false;

        var handleGO = new GameObject("Handle", typeof(RectTransform));
        handleGO.transform.SetParent(go.transform, false);
        var hrt = handleGO.GetComponent<RectTransform>();
        hrt.anchorMin = new Vector2(0, 0.5f); hrt.anchorMax = new Vector2(0, 0.5f); hrt.pivot = new Vector2(0.5f, 0.5f);
        hrt.sizeDelta = new Vector2(30f, 30f);
        var handle = handleGO.AddComponent<Image>();
        handle.sprite = CircleSprite(); handle.color = Color.white; handle.raycastTarget = false;

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(sizePx.x + 20f, Mathf.Max(sizePx.y, 44f), 8f);
        col.center = new Vector3(sizePx.x * 0.5f, -sizePx.y * 0.5f, 0);

        var slider = go.AddComponent<WorldSlider>();
        slider.Init(min, max, value, frt, hrt, sizePx.x, onChanged, onCommit);
        Register(col, slider);
        return slider;
    }

    /// <summary>−/value/+ integer stepper. Returns the value label for external refresh.</summary>
    public static UIText MakeStepper(Transform parent, string label, Vector2 anchoredPos,
                                     Func<int> get, Action<int> set, int step, int min, int max,
                                     Action onChanged)
    {
        MakeText(parent, label, 24f, WorldUIStyle.TextDim, new Vector2(48f, 44f),
            anchoredPos, new Vector2(0, 1), new Vector2(0, 1));

        UIText val = null;
        MakeButton(parent, "-", null, new Vector2(48f, 44f),
            anchoredPos + new Vector2(52f, 0),
            () => { set(Mathf.Clamp(get() - step, min, max)); if (val != null) val.text = get().ToString(); onChanged(); });

        val = MakeText(parent, get().ToString(), 26f, WorldUIStyle.TextMain, new Vector2(70f, 44f),
            anchoredPos + new Vector2(104f, 0), new Vector2(0, 1), new Vector2(0, 1), TextAnchor.MiddleCenter, true);

        MakeButton(parent, "+", null, new Vector2(48f, 44f),
            anchoredPos + new Vector2(178f, 0),
            () => { set(Mathf.Clamp(get() + step, min, max)); if (val != null) val.text = get().ToString(); onChanged(); });

        return val;
    }

    // ── Procedural sprites ────────────────────────────────────────────────────

    public static Sprite FillSprite()
    {
        if (_fillSprite == null) _fillSprite = RoundedRectSprite(64, 18, false);
        return _fillSprite;
    }

    public static Sprite OutlineSprite()
    {
        if (_outlineSprite == null) _outlineSprite = RoundedRectSprite(64, 18, true);
        return _outlineSprite;
    }

    public static Sprite CircleSprite()
    {
        if (_circleSprite == null)
        {
            const int S = 64;
            var tex = NewTex(S, S);
            var px = new Color[S * S];
            float r = S * 0.5f - 2f, cx = S * 0.5f - 0.5f, cy = S * 0.5f - 0.5f;
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float a = Mathf.Clamp01(r - d + 0.5f);
                px[y * S + x] = new Color(1, 1, 1, a);
            }
            tex.SetPixels(px); tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }
        return _circleSprite;
    }

    private static Sprite RoundedRectSprite(int size, int radius, bool outlineOnly)
    {
        var tex = NewTex(size, size);
        var px = new Color[size * size];
        float half = size * 0.5f, inner = half - radius;
        const float border = 3f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = Mathf.Max(Mathf.Abs(x - half + 0.5f) - inner, 0f);
            float dy = Mathf.Max(Mathf.Abs(y - half + 0.5f) - inner, 0f);
            float d  = Mathf.Sqrt(dx * dx + dy * dy) - radius;   // signed distance to rounded rect
            float a;
            if (outlineOnly)
                a = Mathf.Clamp01(1f - Mathf.Abs(d + border * 0.5f) / (border * 0.5f) + 0.5f) *
                    Mathf.Clamp01(-d + 0.5f);
            else
                a = Mathf.Clamp01(-d + 0.5f);
            px[y * size + x] = new Color(1, 1, 1, a);
        }
        tex.SetPixels(px); tex.Apply();
        float b = radius + 4;
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                             100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }

    private static Texture2D NewTex(int w, int h)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        return t;
    }

    private static Font FallbackFont()
    {
        if (_fallbackFont == null)
            _fallbackFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return _fallbackFont;
    }
}

// ─── Widget behaviours ─────────────────────────────────────────────────────────

public class WorldButton : MonoBehaviour, IWorldInteractable
{
    private Image  _bg, _glow;
    private UIText _label;
    private Action _onClick;
    private bool   _selected;

    public void Init(Image bg, Image glow, UIText label, Action onClick)
    { _bg = bg; _glow = glow; _label = label; _onClick = onClick; }

    /// <summary>Persistent "active" highlight (e.g. current display style / current tab).</summary>
    public void SetSelected(bool on)
    {
        _selected = on;
        if (_bg != null) _bg.color = on ? WorldUIStyle.ButtonActive : WorldUIStyle.ButtonIdle;
        if (_glow != null) _glow.color = on ? WorldUIStyle.Accent : new Color(0, 0, 0, 0);
        if (_label != null) _label.color = on ? WorldUIStyle.Accent : WorldUIStyle.TextMain;
    }

    public void SetTint(Color idle) { if (!_selected && _bg != null) _bg.color = idle; }

    public void OnHoverEnter()
    {
        if (_bg != null && !_selected) _bg.color = WorldUIStyle.ButtonHover;
        if (_glow != null && !_selected) _glow.color = WorldUIStyle.AccentDim;
    }
    public void OnHoverExit()
    {
        if (_bg != null && !_selected) _bg.color = WorldUIStyle.ButtonIdle;
        if (_glow != null && !_selected) _glow.color = new Color(0, 0, 0, 0);
    }
    public void OnPress(Vector3 p) { transform.localScale = Vector3.one * 0.96f; }
    public void OnDrag(Ray ray) { }
    public void OnRelease()
    {
        transform.localScale = Vector3.one;
        _onClick?.Invoke();
    }
}

public class WorldToggle : MonoBehaviour, IWorldInteractable
{
    private Image _track, _knob;
    private RectTransform _knobRt;
    private bool _isOn;
    private Action<bool> _onChanged;

    public void Init(Image track, Image knob, RectTransform knobRt, bool isOn, Action<bool> onChanged)
    { _track = track; _knob = knob; _knobRt = knobRt; _onChanged = onChanged; SetVisual(isOn); }

    public bool IsOn => _isOn;
    public void SetWithoutNotify(bool on) => SetVisual(on);

    private void SetVisual(bool on)
    {
        _isOn = on;
        if (_track != null) _track.color = on ? WorldUIStyle.ButtonActive : new Color(1, 1, 1, 0.10f);
        if (_knob != null)  _knob.color  = on ? WorldUIStyle.Accent : new Color(1, 1, 1, 0.55f);
        if (_knobRt != null) _knobRt.anchoredPosition = new Vector2(on ? 46f : 17f, 0);
    }

    public void OnHoverEnter() { if (_knob != null) _knob.color = Color.white; }
    public void OnHoverExit()  { SetVisual(_isOn); }
    public void OnPress(Vector3 p) { }
    public void OnDrag(Ray ray) { }
    public void OnRelease() { SetVisual(!_isOn); _onChanged?.Invoke(_isOn); }
}

public class WorldSlider : MonoBehaviour, IWorldInteractable
{
    private float _min, _max, _value, _widthPx;
    private RectTransform _fill, _handle;
    private Action<float> _onChanged;
    private Action _onCommit;

    public float Value => _value;

    public void Init(float min, float max, float value, RectTransform fill, RectTransform handle,
                     float widthPx, Action<float> onChanged, Action onCommit)
    {
        _min = min; _max = max; _widthPx = widthPx;
        _fill = fill; _handle = handle;
        _onChanged = onChanged; _onCommit = onCommit;
        SetWithoutNotify(value);
    }

    public void SetWithoutNotify(float v)
    {
        _value = Mathf.Clamp(v, _min, _max);
        float t = Mathf.InverseLerp(_min, _max, _value);
        if (_fill  != null) _fill.sizeDelta = new Vector2(t * _widthPx, 0);
        if (_handle != null) _handle.anchoredPosition = new Vector2(t * _widthPx, 0);
    }

    public void OnHoverEnter() { }
    public void OnHoverExit()  { }

    public void OnPress(Vector3 worldPoint) { ApplyWorldPoint(worldPoint); }

    public void OnDrag(Ray ray)
    {
        // Intersect the pointer ray with the slider's plane so dragging keeps
        // tracking even when the ray slides off the collider.
        var plane = new Plane(transform.forward, transform.position);
        if (plane.Raycast(ray, out float enter))
            ApplyWorldPoint(ray.GetPoint(enter));
    }

    public void OnRelease() { _onCommit?.Invoke(); }

    private void ApplyWorldPoint(Vector3 worldPoint)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint); // pivot top-left
        float t = Mathf.Clamp01(local.x / _widthPx);
        _value = Mathf.Lerp(_min, _max, t);
        SetWithoutNotify(_value);
        _onChanged?.Invoke(_value);
    }
}

/// <summary>Removes a widget's collider from <see cref="WorldUIKit.InteractableRegistry"/> on destroy.</summary>
public class WorldInteractableHandle : MonoBehaviour
{
    public Collider key;
    private void OnDestroy()
    {
        if (!ReferenceEquals(key, null)) WorldUIKit.InteractableRegistry.Remove(key);
    }
}

/// <summary>
/// Pinch-and-drag handle: index-pinch on it and move your hand to carry <see cref="target"/>
/// (a panel) through the room; it keeps facing you. Uses the same reliable index-pinch path as
/// buttons — grip/middle-pinch grabbing proved too flaky for hand tracking.
/// </summary>
public class WorldDragHandle : MonoBehaviour, IWorldInteractable
{
    public Transform target;
    public UnityEngine.UI.Image highlight;   // optional hover feedback

    private Vector3 _offset;      // target.position − grab point, world
    private Vector3 _grabPoint;
    private float _dist = -1f;
    private Color _idle;

    private void Awake() { if (highlight != null) _idle = highlight.color; }

    public void OnHoverEnter() { if (highlight != null) highlight.color = new Color(1, 1, 1, 0.14f); }
    public void OnHoverExit()  { if (highlight != null) highlight.color = _idle; }

    public void OnPress(Vector3 worldPoint)
    {
        if (target == null) return;
        _grabPoint = worldPoint;
        _offset = target.position - worldPoint;
        _dist = -1f;
    }

    public void OnDrag(Ray ray)
    {
        if (target == null) return;
        if (_dist < 0f) _dist = Vector3.Distance(ray.origin, _grabPoint);   // hold at the pick-up distance
        target.position = ray.GetPoint(_dist) + _offset;

        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 to = target.position - cam.transform.position;
            if (to.sqrMagnitude > 1e-4f) target.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
        }
    }

    public void OnRelease() { _dist = -1f; }
}
