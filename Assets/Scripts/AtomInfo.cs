using UnityEngine;

/// <summary>
/// Crystallographic metadata attached to each rendered atom GameObject by CrystalCIFViewer.
/// Used by CrystalInteraction for picking and measurement.
/// </summary>
[DisallowMultipleComponent]
public class AtomInfo : MonoBehaviour
{
    public string  AtomLabel;
    public string  Element;
    public Vector3 FracCoords;
    public Vector3 CartAngstrom; // Cartesian coordinates in Å (not Unity units)
    public float   Occupancy = 1f;        // primary-site occupancy (1 = fully occupied)
    public string  SecondElement;         // non-null for joint-occupancy (bi-colour) sites
    public float   SecondOccupancy = 0f;  // occupancy of the second element, if any
}
