// VirtualHands.cs — clean, translucent virtual hands drawn from XR Hands joint data.
//
// Passthrough video distorts your real hands (especially near the edges of the view), which
// makes pinching and fist-grabbing hard to judge. This renders a simple skeletal hand — joint
// spheres + bone capsules — exactly where tracking says your hand is, so what you see is what
// the interaction code sees. Hands tint while a fist-grab is active.
//
// Everything is procedural (no prefabs/meshes) and uses URP/Lit, which is pinned in Always
// Included Shaders. Joint poses are reported in tracking space → converted via trackingSpace.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

public class VirtualHands : MonoBehaviour
{
    public Transform trackingSpace;
    public CrystalDirectGrab grabber;          // optional: tint while grabbing

    [Header("Look")]
    public Color handColor = new Color(0.55f, 0.90f, 1.00f, 0.55f);
    public Color grabColor = new Color(1.00f, 0.80f, 0.25f, 0.75f);
    public float jointRadius = 0.0065f;
    public float boneRadius  = 0.0045f;

    private XRHandSubsystem _subsys;
    private static readonly List<XRHandSubsystem> _buf = new();

    private Material _mat;
    private HandVisual _left, _right;

    // Finger chains (wrist → metacarpal → … → tip). The wrist→metacarpal links sketch the palm.
    private static readonly XRHandJointID[][] Chains =
    {
        new[] { XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
        new[] { XRHandJointID.Wrist, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
        new[] { XRHandJointID.Wrist, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
        new[] { XRHandJointID.Wrist, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
        new[] { XRHandJointID.Wrist, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
    };
    // Knuckle arc across the metacarpal heads to close the palm outline.
    private static readonly XRHandJointID[] Knuckles =
    {
        XRHandJointID.IndexProximal, XRHandJointID.MiddleProximal, XRHandJointID.RingProximal, XRHandJointID.LittleProximal
    };

    private class HandVisual
    {
        public GameObject root;
        public Dictionary<XRHandJointID, Transform> joints = new();
        public List<(XRHandJointID a, XRHandJointID b, Transform tf)> bones = new();
        public List<Renderer> renderers = new();
        public bool tinted;
    }

    private void Start()
    {
        _mat = MakeMaterial(handColor);
        _left  = BuildHand("LeftHand");
        _right = BuildHand("RightHand");
        _left.root.SetActive(false);
        _right.root.SetActive(false);
    }

    private void LateUpdate()   // after tracking has updated this frame
    {
        if (_subsys == null || !_subsys.running)
        {
            SubsystemManager.GetSubsystems(_buf);
            _subsys = _buf.Count > 0 ? _buf[0] : null;
            if (_subsys == null) return;
        }
        Drive(_left,  _subsys.leftHand,  grabber != null && grabber.IsGrabbingLeft);
        Drive(_right, _subsys.rightHand, grabber != null && grabber.IsGrabbingRight);
    }

    private void Drive(HandVisual v, XRHand hand, bool grabbing)
    {
        if (!hand.isTracked) { if (v.root.activeSelf) v.root.SetActive(false); return; }
        if (!v.root.activeSelf) v.root.SetActive(true);

        foreach (var kv in v.joints)
        {
            if (TryJoint(hand, kv.Key, out Vector3 p)) { kv.Value.position = p; kv.Value.gameObject.SetActive(true); }
            else kv.Value.gameObject.SetActive(false);
        }
        foreach (var (a, b, tf) in v.bones)
        {
            var ta = v.joints[a]; var tb = v.joints[b];
            if (!ta.gameObject.activeSelf || !tb.gameObject.activeSelf) { tf.gameObject.SetActive(false); continue; }
            tf.gameObject.SetActive(true);
            Vector3 d = tb.position - ta.position;
            float len = d.magnitude;
            tf.position = (ta.position + tb.position) * 0.5f;
            if (len > 1e-5f) tf.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            tf.localScale = new Vector3(boneRadius * 2f, len * 0.5f, boneRadius * 2f);
        }

        if (grabbing != v.tinted)
        {
            v.tinted = grabbing;
            var c = grabbing ? grabColor : handColor;
            foreach (var r in v.renderers) r.material.color = c;   // per-hand instances
        }
    }

    private HandVisual BuildHand(string name)
    {
        var v = new HandVisual { root = new GameObject(name) };
        v.root.transform.SetParent(transform, false);

        var ids = new HashSet<XRHandJointID>();
        foreach (var chain in Chains) foreach (var id in chain) ids.Add(id);
        ids.Add(XRHandJointID.Palm);

        foreach (var id in ids)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = id.ToString();
            Destroy(s.GetComponent<Collider>());
            s.transform.SetParent(v.root.transform, false);
            s.transform.localScale = Vector3.one * (jointRadius * 2f * (id == XRHandJointID.Palm ? 1.6f : 1f));
            Style(s, v);
            v.joints[id] = s.transform;
        }

        void Bone(XRHandJointID a, XRHandJointID b)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            c.name = $"{a}-{b}";
            Destroy(c.GetComponent<Collider>());
            c.transform.SetParent(v.root.transform, false);
            Style(c, v);
            v.bones.Add((a, b, c.transform));
        }
        foreach (var chain in Chains)
            for (int i = 0; i + 1 < chain.Length; i++) Bone(chain[i], chain[i + 1]);
        for (int i = 0; i + 1 < Knuckles.Length; i++) Bone(Knuckles[i], Knuckles[i + 1]);

        return v;
    }

    private void Style(GameObject go, HandVisual v)
    {
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = _mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        v.renderers.Add(r);
    }

    private bool TryJoint(XRHand hand, XRHandJointID id, out Vector3 world)
    {
        world = Vector3.zero;
        if (!hand.GetJoint(id).TryGetPose(out Pose p)) return false;
        world = trackingSpace != null ? trackingSpace.TransformPoint(p.position) : p.position;
        return true;
    }

    private static Material MakeMaterial(Color c)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        var m = new Material(shader) { name = "Mat_VirtualHand" };
        if (shader.name.Contains("Universal"))
        {
            // URP/Lit → transparent, soft emissive so it reads over passthrough.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.SetFloat("_Smoothness", 0.4f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b) * 0.35f);
            m.renderQueue = 3000;
        }
        m.color = c;
        return m;
    }
}
