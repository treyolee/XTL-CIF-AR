# XTL CIF-AR — Mixed-Reality Crystal Structure Viewer

An NSF-funded teaching tool that turns standard crystallographic information files (CIF) into interactive 3-D crystal structures you can walk around, grab, scale, and measure in mixed reality on Meta Quest headsets. It also runs as a desktop app on macOS.

**Features**

- Parses CIF files and expands symmetry to build the full unit cell (48 structures bundled)
- Ball-and-stick, space-filling, wireframe, and VESTA-style coordination-polyhedra views
- Bonding uses VESTA's default per-element-pair bond-length table
- Bi-coloured atoms and bonds for mixed-occupancy sites
- Atom picking with element, site label, fractional coordinates, and occupancy
- Distance and angle measurement between atoms
- Miller-index (hkl) plane visualiser
- Pressure slider (0–136 GPa) using a 2nd-order Birch–Murnaghan equation of state
- Unit-cell frame, crystallographic axis indicator, and element legend
- On Quest: colour passthrough, hand tracking, fist-grab to move/scale the crystal, and a floating control console

---

## Run it on a Meta Quest (no Unity required)

Works on Quest 3, Quest 3S, and Quest Pro.

1. **Download the APK** from the latest entry on the [Releases page](../../releases).
2. **Enable Developer Mode** on your headset (one-time). You need a free Meta developer account: at <https://developer.oculus.com/manage/organizations/create/> create an organisation, then in the **Meta Horizon** phone app go to *Menu → Devices → your headset → Headset Settings → Developer Mode* and switch it on.
3. **Install the APK** using one of:
   - [Meta Quest Developer Hub](https://developer.oculus.com/meta-quest-developer-hub/) — connect the headset by USB, accept the "Allow USB debugging" prompt in the headset, then drag the APK onto the device panel.
   - [SideQuest](https://sidequestvr.com/) — connect by USB and use *Install APK from folder*.
   - Command line, if you have Android platform-tools:
     ```bash
     adb install -r XTL-CIF-AR.apk
     ```
4. In the headset, open **Library → Unknown Sources** and launch the app.

**Using it:** The crystal appears about arm's length in front of you with a control console to the left and a measurement panel to the right. Point at the console and pinch (index finger to thumb) to press buttons. Make a **fist inside the crystal** to grab and move it; make a fist with **both hands** inside it to scale. Pinch the console header bar to drag the console. Press **A/X** to show/hide the console and **B/Y** to re-centre everything in front of you.

---

## Build from source

### Requirements

- **Unity 6000.4.0f1** (Unity 6) with the **Android Build Support** module (including OpenJDK and Android SDK/NDK tools). For the desktop build you also need Mac Build Support.
- A Meta Quest in Developer Mode, connected by USB, for *Build and Run*.

All packages (Meta XR SDK, OpenXR, XR Hands, AR Foundation, URP, TextMesh Pro, Input System) are pulled automatically from `Packages/manifest.json` when the project is first opened.

### Steps

1. Clone the repository and open the folder in Unity Hub. First import takes a few minutes while packages download and shaders compile.
2. Open the scene in `Assets/Scenes/`.
3. **Quest:** *File → Build Profiles → Android → Switch Platform*, then *Build and Run* with the headset connected. The bundled CIF library is copied into `StreamingAssets` automatically by an editor build hook.
4. **macOS desktop:** switch the build profile to *macOS* and build. The desktop version uses trackpad orbit/pan/zoom and an on-screen HUD; press **F** to open the structure browser.

If hands do not appear in the headset, check *Project Settings → XR Plug-in Management → OpenXR → Android*: **Hand Tracking Subsystem** and **Meta Hand Tracking Aim** must be enabled along with the Meta Quest features.

### Adding your own crystal structures

Drop a `.cif` file (renamed to `.txt`, so Unity imports it as a text asset) into `Assets/GameObjects/`. The build hook syncs it into the bundled library; on desktop you can also open any CIF directly from the structure browser.

---

## Project layout

| Path | Contents |
|------|----------|
| `Assets/Scripts/CrystalCIFViewer.cs` | CIF parsing, symmetry expansion, bonding, polyhedra, Miller planes, pressure model |
| `Assets/Scripts/CrystalInteraction.cs` | Desktop HUD, atom picking, measurement, structure browser |
| `Assets/Scripts/VestaBondTable.cs` | VESTA's default bond-length table |
| `Assets/Scripts/XR/` | Quest spatial UI: rig, hand input, virtual hands, direct grab, control console, placard |
| `Assets/StreamingAssets/CIF/` | Bundled CIF library |
| `Assets/Editor/CifLibrarySync.cs` | Build hook that syncs the CIF library |

## Acknowledgements

This material is based upon work supported by the National Science Foundation. Any opinions, findings, and conclusions or recommendations expressed in this material are those of the authors and do not necessarily reflect the views of the National Science Foundation.

Bond-length defaults follow the table shipped with VESTA (K. Momma and F. Izumi, *J. Appl. Crystallogr.* **44**, 1272–1276 (2011)).
