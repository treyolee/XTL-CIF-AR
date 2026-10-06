using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps an a/b/c axis gizmo pinned to a fixed screen corner at a constant size,
/// rotating only to match the crystal's current orientation. Axis labels are
/// billboarded so they always face the user and stay right-side up.
///
/// Created at runtime by CrystalCIFViewer.DrawAxisIndicator.
/// </summary>
[DefaultExecutionOrder(100)] // run after the crystal transform controller has moved the model
public class AxisGizmoFollower : MonoBehaviour
{
    [Tooltip("The crystal root whose rotation the gizmo mirrors.")]
    public Transform crystal;

    [Tooltip("Camera the gizmo anchors to. Falls back to Camera.main when null.")]
    public Camera cam;

    [Tooltip("Axis label transforms to keep facing the camera, upright.")]
    public Transform[] labels;

    [Header("Placement")]
    [Tooltip("Viewport position (0..1) where the gizmo sits. (0,0)=bottom-left.")]
    public Vector2 viewport = new Vector2(0.10f, 0.16f);
    [Tooltip("Distance in front of the camera. Fixed depth keeps the on-screen size constant.")]
    public float depth = 3.0f;

    private void LateUpdate()
    {
        var c = cam != null ? cam : Camera.main;
        if (c == null || crystal == null) return;

        // Pin to a fixed point in the camera's view → stationary on screen, constant size.
        transform.position = c.ViewportToWorldPoint(new Vector3(viewport.x, viewport.y, depth));

        // Only the rotation tracks the crystal, so the gizmo shows the live orientation.
        transform.rotation = crystal.rotation;

        // Labels always face the user and stay upright.
        if (labels != null)
        {
            var rot = Quaternion.LookRotation(c.transform.forward, c.transform.up);
            foreach (var l in labels)
                if (l != null) l.rotation = rot;
        }
    }
}
