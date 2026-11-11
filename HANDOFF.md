# Handoff – Wind-Smile Bolt Tool (for continuing in a new chat)

**Status (2026-11-11):** v1.0 source complete, NOT yet compiled (authoring sandbox had no .NET SDK).
Next step: user runs `build.bat` on the AutoCAD 2027 PC and sends any compiler errors.

**Target:** AutoCAD 2027, .NET 10, `net10.0-windows`, WPF. References `accoremgd/acdbmgd/acmgd` from `$(AcadDir)`.

## Architecture
| File | Role |
|---|---|
| `Data/Standards.cs` | ISO + JIS size tables (M3–M48), standard lengths, thread length b, JSON override in `%APPDATA%\WindSmile\BoltTool` |
| `Data/BoltJob.cs` | All parameters of a block (part, assembly, view, block). JSON → stored in XData `WS_BOLTTOOL` |
| `Data/Settings.cs` | last job, default length per size |
| `Geometry/Mesh.cs` | Vec3/Mat3, welded triangle mesh, `Loft` = θ-dependent revolve (hex, socket, chamfers) |
| `Geometry/PartBuilder.cs` | part meshes, helical thread, assembly layout (axis +X, head top x=0) |
| `Geometry/HiddenLineRenderer.cs` | crease+silhouette edges, depth-tested sampling, thread conventions, convex-hull wipeouts, polyline chaining |
| `Cad/BlockFactory.cs` | block definitions (2D lines layer 0 ByBlock + wipeouts / 3D SubDMesh→Solid), names with hash |
| `Cad/AssemblyEditor.cs` | insert with multi-block jig, Apply() regenerates linked set keeping head top fixed |
| `Cad/LengthGripOverrule.cs` | ▶ grips: "len" (bolt end) and "clamp" (nut); applied on CommandEnded |
| `Cad/LayerUtil.cs` | layers, HIDDEN linetype, regapp, XData helpers |
| `Commands.cs` | BOLTTOOL, BOLTEDIT, BOLTTORQUE + internal WS_BOLTINSERT/MAKE/UPDATE (window → SendStringToExecute) |
| `Torque/TorqueCalculator.cs` | simplified VDI 2230: F_M, M_A, surface pressure; friction tables |
| `UI/BoltToolWindow.xaml(.cs)` | 4 tabs + live preview; `UI/PreviewControl.cs` orbit drag + joystick |

View convention: `Mat3.View(az, el, spin)` – az 0/el 0 = side (head left), az +90 head end, −90 thread end, el 90 top, iso = ±45/35.264.

## Ideas not done yet
- more parts: flange bolt/nut, countersunk screw, nyloc nut, button head
- dimension/label (mleader with designation) option
- palette (PaletteSet) docking instead of modeless window

## Changes after v1.0 (same day)
- First build on the user's PC succeeded (before the items below). Source of truth = this folder on the PC.
- `install.bat` / `uninstall.bat`: autoload bundle in `%APPDATA%\Autodesk\ApplicationPlugins\WS.BoltTool.bundle`.
- `UI/RibbonUi.cs`: "Wind-Smile" ribbon tab (needs AcWindows.dll + AdWindows.dll references).
- `UI/LegendControl.cs`: dimension-letter legend on the Part tab.
- Wipeout fix: bolt head / shank split by max-x (under-head face belongs to the head). Block hash key "v3".
- `HiddenLineRenderer.Overkill`: merges collinear overlapping segments before chaining.
- `UI/Loc.cs`: EN/JP texts (English string = key), tooltips by control name; `Settings.Language`.
- Window: arrow-key rotation (1°, Shift 15°, Ctrl 0.1°), Ctrl+Z / Ctrl+Y undo stack of job JSON (`Track()` in `Render`).
- Preview: scale by bounding sphere (no zoom change while orbiting); view-angle snapping (`BoltJob.ViewSnap`, raw vs snapped angles in `Rotate`).
- Preview: Ctrl+click selects washers/nuts, Ctrl+drag moves them along the bolt (`BoltJob.Offsets` per role, applied in `PartBuilder.ComputeLayout`), red distance label from the under-head face, click it to type a value.
- None of the items in this section has been compiled yet.
