# Bolt Tool – complete handoff

This file is written so that another assistant (or developer) can continue the work without the earlier
conversation. Read sections 1–4 first; section 8 is the list of work that is still open.

---

## 1. What this project is

An AutoCAD plugin (C#, WPF) that generates **blocks of bolts, nuts and washers** and complete bolted
connections, with a live preview, and a tightening-torque calculator.

- **Target:** AutoCAD 2027, .NET 10 (`net10.0-windows`), x64, Windows.
- **Assembly:** `BoltTool.dll`, root namespace `BoltToolApp`.
- **Repository:** `https://github.com/MLavrentii/BoltTool` (the owner pushes from VS Code).
- **Important rule from the owner:** no company name anywhere in the project (code, docs, names).
- **UI languages:** English (default) and Japanese.

## 2. Current state – read this before touching the code

| State | What |
|---|---|
| **Last commit** | `0a133df` ("Clean from WS"). Everything below is in the working tree, **not committed yet**. |
| **Working tree** | Claude's "v2 element stack" rewrite **plus** a review patch made in ChatGPT on 2026-11-11 (see `REVIEW_20261111.md` and `PATCH_INSTRUCTIONS.txt`). |
| **Build** | The working tree **compiles**: `bin\Release\net10.0-windows\BoltTool.dll` (2026-11-11) is newer than every source file and contains the review-patch code (`OnFitView`, `mm (2D)` label, `StackMover`). |
| **Not confirmed** | Whether the new DLL was installed with `install.bat` and tested in AutoCAD; whether `tests/BoltTool.CoreTests.csproj` was run (`dotnet run --project tests\BoltTool.CoreTests.csproj -c Release`). |

Next job: run the regression tests, `install.bat`, the acceptance test list in `REVIEW_20261111.md`, then commit.

## 3. Build, install, test

```
build.bat                      -> dotnet build -c Release (AcadDir = C:\Program Files\Autodesk\AutoCAD 2027)
build.bat "D:\path\AutoCAD 2027"   if AutoCAD is elsewhere
install.bat                    -> copies bin\Release\net10.0-windows\BoltTool.* + PackageContents.xml to
                                  %APPDATA%\Autodesk\ApplicationPlugins\BoltTool.bundle\   (AutoCAD must be closed)
uninstall.bat                  -> removes that bundle
```

- AutoCAD must be closed to replace the DLL. After a build always run `install.bat`.
- Delete `bin` and `obj` before the first v2 build (the assembly and namespace were renamed earlier).
- An old bundle `%APPDATA%\Autodesk\ApplicationPlugins\WS.BoltTool.bundle` may still exist on the owner's PC.
  It must be deleted, otherwise the old plugin loads too (symptom: window title contains a company name).
- References (not copied to output): `accoremgd`, `acdbmgd`, `acmgd`, `AcWindows`, `AdWindows` from `$(AcadDir)`.
- .NET 8 / AutoCAD 2025–2026: `dotnet build -c Release -p:TargetFramework=net8.0-windows -p:AcadDir="..."` (untested).

Commands in AutoCAD: `BOLTTOOL` (window), `BOLTEDIT` (edit a placed block), `BOLTTORQUE` (window on torque tab).
A ribbon tab "Bolt Tool" with three buttons is added at load.

## 4. Architecture

```
BoltTool.csproj, build.bat, install.bat, uninstall.bat, PackageContents.xml
Commands.cs                    plugin start-up (IExtensionApplication), commands, window <-> command hand-over
Data/
  Standards.cs                 PartKind enum + names/codes, SizeRow (dims per size), ISO and JIS tables M3–M48,
                               standard lengths, thread length; tables are written to and read from
                               %APPDATA%\BoltTool\standard_ISO.json / standard_JIS.json (user-editable)
  BoltJob.cs                   ALL parameters of a block (see 5). StackItem. JSON <-> XData.
  Settings.cs                  last job (settings.json), UiState (ui.json), language.txt, default lengths
Geometry/
  Mesh.cs                      Vec3, Mat3 (View matrix), welded triangle mesh, Loft() = revolve whose profile
                               may depend on the angle (hexagons, chamfers, sockets, flange)
  PartBuilder.cs               meshes of every part, helical thread, ComputeLayout() (positions along the axis)
  StackMover.cs                moving / swapping elements along the bolt (Ctrl+drag), "usual order" sort
  HiddenLineRenderer.cs        orthographic hidden-line removal -> 2D polylines; overkill merge; thread
                               conventions; convex-hull wipeout outline per part group
Cad/
  BlockFactory.cs              creates/reuses block definitions (2D lines + wipeouts, or 3D SubDMesh/solid),
                               block naming, base-point positions
  AssemblyEditor.cs            insert with a jig (one or several linked blocks), Apply() = regenerate a placed
                               block and its linked set
  LengthGripOverrule.cs        GripOverrule on BlockReference: custom stretch grips, applied on CommandEnded
  LayerUtil.cs                 layers, linetype, RegApp, XDataUtil (job JSON in XData, app name BT_BOLTTOOL)
Torque/
  TorqueCalculator.cs          VDI 2230 and JIS B 1083 methods, classes, finishes, materials
UI/
  BoltToolWindow.xaml(.cs)     the window: tabs Part / Assembly / View & Block / Torque, preview on the right
  PreviewControl.cs            custom-drawn preview with all mouse interaction
  StackDiagramControl.cs       small side-view diagram of the connection
  LegendControl.cs             picture explaining the dimension letters
  Loc.cs                       English/Japanese texts and tooltips
  RibbonUi.cs                  ribbon tab
docs/                          test renders (engine_test_render.png, overkill_before_after.png), screenshot.png
README.md                      user manual
```

### Conventions that everything relies on

- **Model axis:** the bolt axis is **+X**. The top of the bolt head (start of a threaded rod) is at x = 0,
  the shank runs toward +X.
- **View:** `Mat3.View(azimuth, elevation, spin)` maps model -> screen (x right, y up, z toward viewer).
  az 0 / el 0 = side view with the head on the left; az +90 = looking at the head; az −90 = thread end;
  el 90 = top; isometric = az ±45 / el 35.264. `spin` turns the hexagon about the axis.
- **A 2D block is exactly the projected drawing**, scaled by `job.Scale`, shifted so the chosen base point is at 0,0.
  `BlockFactory.Local(job, x)` gives the block-local vector of axial position x.
- **Block entities:** lines on layer `0`, colour / linetype / lineweight **ByBlock**; wipeouts on the chosen
  wipeout layer (default `01_WIPEOUT`), created first so they sit under the lines.
- **Roles:** every block reference stores the full job JSON in XData plus `Role`:
  `single` (one part), `assembly` (whole connection in one block), `bolt`, or the **Id of a stack element**
  (`e1`, `e2`, …) for linked separate blocks. Linked blocks share `AssemblyId`.
- **Texts:** the English string is the key. `Loc.T("text")`, `Loc.F("text {0}", args)`. Static XAML texts are
  translated by `Loc.Apply(window)`, tooltips by control name in `Loc.Tips`. Adding a text = add one entry to
  the `J` dictionary in `Loc.cs`. **Duplicate keys in those dictionaries crash at start-up.**
- **`_loading` flag** in the window: set it while changing controls from code so handlers do not fire.

## 5. Data model (`Data/BoltJob.cs`)

```
Standard (ISO/JIS), Size ("M12"), Kind (PartKind), Length, BasePoint
IsAssembly, ClampLength, AutoLength, MinProtrusionPitches, SeparateBlocks, StickToBoltEnd
Stack : List<StackItem>          // washers and nuts in physical order, head -> bolt end
    StackItem { Id, Kind, Side (Head | Nut), Gap, Thickness (0 = standard), OuterD (0 = standard) }
Azimuth, Elevation, Spin, Thread (None/Simplified/Detailed), HiddenLines, Output (View2D/Mesh3D/Solid3D), ViewSnap
Scale, Wipeout, WipeoutLayer, SnapLengthToStandard
AssemblyId, Role
// legacy, only read for migration: HeadWasher, NutWasher, Spring, NutKind, NutCount
```

- `EnsureStack()` must be called before using `Stack` (builds it from the legacy fields when null, sorts
  head-side items first, assigns ids, rejects NaN / infinity / negative values).
- **Gap rule (changed by the review patch):** `ClampLength` is always the solid thickness of the clamped parts.
  Every `StackItem.Gap` – also the first nut-side one – is the empty space directly in front of that element.
  Editing or dragging gaps never changes `ClampLength`.
- **Dragging (`StackMover`):** a group stops at contact; the clamped parts are a barrier (no swap across them,
  a selection spanning both sides cannot move). Dragging the last head-side element outward moves the plate
  and the whole nut side with it. Same-side swaps show the green ghost.
- `PartBuilder.ComputeLayout(job)` -> `Layout { Parts[], BoltLength, Required, UnderHead, ClampStart,
  ClampEnd, StackEnd, BoltEnd, HasNutSide, Warning }`. `Parts[0]` is always the bolt (or the single part).
- Bolt length: automatic = next standard length ≥ `Required` (= end of the last element + protrusion); manual = `Length`.
- Block cache key version `v5`: full-precision dimensions + the complete size-table row.

## 6. What exists (feature list)

### Built and run on the owner's PC (commit `0a133df` and earlier)
- Parts: hex bolt (partial / full thread), socket head cap screw, threaded rod, hex nut, thin nut, plain
  washer, large washer, spring washer; ISO and JIS tables.
- 2D views with hidden-line removal, any direction; 3D mesh / solid output; simplified or helical thread.
- Wipeout per part (convex outline); head and shank get separate wipeouts.
- Overkill clean-up of duplicate / overlapping lines.
- Window with live preview: orbit by drag, joystick, zoom, pan, arrow keys (1°, Shift 15°, Ctrl 0.1°),
  view presets, view-angle snapping, Ctrl+Z / Ctrl+Y undo of settings, Esc to unselect.
- Assembly with fixed washer / spring / nut options (old model), linked separate blocks or one block.
- Ctrl+click / Ctrl+drag of washers and nuts along the bolt with a distance label (old "offset" model).
- Stretch grips in the drawing (bolt length, clamp length), `BOLTEDIT`.
- Torque calculator (simplified VDI 2230), English / Japanese UI with tooltips, dimension legend.
- Autoload bundle, ribbon tab, window state and settings remembered between sessions.

### In the working tree – compiles, not yet confirmed in AutoCAD ("v2 element stack" + review patch)
1. **Element list** replaces the fixed washer / spring / nut boxes: free order, any number of elements on
   either side, per-element thickness, outer diameter and gap, presets (usual set, double nut with two equal
   nuts, spring both sides, tapped hole), "sort to usual order", "close all gaps". Built in code in
   `RebuildStackUi()`.
2. **New part** `FlangeNut` (dimensions approximated: flange Ø = 2.1·d + 1.3, height = d).
3. **Spring washer drawn flat** inside an assembly (was drawn with its free rise and poked into neighbours).
4. **Move / swap by dragging** (`StackMover.Move`): stop at contact; pulled ≥ max(half the neighbour, 2.5 mm)
   further, a green "ghost" outline shows the landing place; release swaps the order.
5. **Several distance labels** for a multi-selection, each clickable to type a value.
6. **Preview grips are draggable** (bolt length / clamp length).
7. **Grips in the drawing:** "len" at the bolt tip (length only); "clamp" on the rim of the first nut-side
   element at its contact face (moves the nut side, bolt length follows).
8. **Base points:** head top, head-side contact face (`ClampHead`), nut-side contact face (`ClampNut`), bolt tip.
9. **Sticky view frame:** the preview no longer refits / re-zooms when settings change.
10. **Measuring:** Shift+click two snapped points -> green dimension with arrows. **Save JPG** button.
11. **Protrusion** can be typed in pitches or in mm. **Length box** on the Part tab always shows the current
    bolt length; choosing a value switches an assembly to manual length.
12. **Diagram** (`StackDiagramControl`) on the Assembly and View & Block tabs.
13. **Hand cursor fix:** the `Cursor="Hand"` moved from the TabItem style to the tab header border.
14. **Torque:** method switch VDI 2230 / JIS B 1083 (T = K·d·F, K shown), property-class usage notes,
    JIS materials, conductor materials with a warning note.
15. **Review patch (ChatGPT, 2026-11-11):** clamp thickness separated from gaps (see 5), `Fit view` button,
    `L` / `Clamp` labels on the preview grips, numeric validation, measurements labelled `mm (2D)`, torque texts
    reworded (70 % is an example, conductor result = mechanical estimate only), block cache key v5,
    `install.bat` rewritten (rebuilds, stages, swaps the bundle, keeps `BoltTool.bundle.previous`, renames an old
    `WS.BoltTool.bundle` to `.disabled`), regression tests in `tests/`.
16. **Channel nut (2026-11-12, not compiled yet):** `PartKind.ChannelNut` for 41 mm strut channel (ダクターチャンネル): rectangle 35 × 20 × 0.8·d (M ≤ 16; 40 long above), two opposite corners cut, two V-grooves on the face that grips the channel lips (`PartBuilder.BuildChannelNut`, `Mesh.LoftAngles`). Dimensions are an approximation (catalogue M10 nut 35 × 20 × 8). Preset "Channel nut set" = spring washer + large washer under the head, channel nut on the nut side. `PartKind.ChannelNutHolder` = 翼型ナット + 固定ブロック (from the installation drawing): holder plate 46 × 28 × 2 mm along the channel with V-notches and two wing tabs, Ø18 collar in the slot (height = lip thickness, default 2.5, changed via the T override), metal channel nut below (`BuildChannelNutHolder`, `Mesh.MirroredX`). The "Channel nut set" preset uses it. Holder dimensions are approximations – verify with the maker.
17. **Clamp squeeze + label (2026-11-12, not compiled yet):** `ClampLength` may be 0. Ctrl+dragging the first nut-side element toward the head first uses up its gap, then reduces `ClampLength` (down to 0) instead of stopping at the clamp face (`StackMover.MoveLimits` lo = -(gap + clamp), `Move` squeeze). The red label in the preview now shows the distance from the element just before the selected one (or from under the head). Checked in `tests/Program.cs`.

## 7. Things to check next

- Run `tests/BoltTool.CoreTests.csproj` (no AutoCAD needed) and fix any failing check.
- Runtime in AutoCAD: language switch (rebuilds lists), Ctrl+drag swap, typing into the T / Ø / gap boxes,
  grip drag in the preview, `BOLTEDIT` on a block placed with v2, insert of a linked set.
- `StickToBoltEnd` + length grip still adds the length change to `ClampLength` (plate thickness). With the new
  gap rule it is probably more correct to add it to the first nut-side gap instead – decide and adjust in
  `LengthGripOverrule.Process` and `BoltToolWindow.OnGripDrag`.

## 8. Not done / open

### Asked for by the owner, not delivered
- **Real AutoCAD dynamic blocks.** Not possible through the .NET API (no public API to author parameters /
  actions). The custom grips are the substitute and need the plugin loaded. If true dynamic blocks are a must,
  the only route is a pre-authored template DWG or scripted BEDIT commands – neither was started.
- **Torque table for electrical terminals / busbars.** No verifiable public table was found, so none was
  added; only a warning note for conductor materials. Needs real values from JIS C 8201 / equipment makers.
- **Different thread size per element** (e.g. another size of washer on the same bolt). Only thickness and
  outer diameter can be overridden per element.
- **Bolt with captive washer (SEMS), nyloc nut, flange bolt, countersunk screw.** Only the flange nut was added.
- **Custom block name / prefix.** Names are automatic (`BT_<std>_<code>_<size>_<view>_<hash>`).

### Known weaknesses
- Dimension tables were typed from memory and never checked against the standards, especially JIS head /
  nut sizes and spring washers. Flange-nut dimensions are a formula.
- Friction coefficients, permissible surface pressures and the property-class usage notes are typical values /
  common practice, unverified.
- Wipeouts are convex hulls without holes: a washer's or nut's wipeout also covers its hole in end views.
- 3D solid output is a faceted conversion of the mesh; with helical thread it usually stays a mesh.
- Japanese texts are unreviewed machine-quality translations.
- Blocks placed before v2 use old role names; editing them drops and re-creates their washers / nuts.
- Measuring snaps only to polyline vertices (corners and tessellation points), not to midpoints or centres,
  and measurements are cleared whenever the view or the parts change.
- The preview hint text is three lines at the top left and can overlap the drawing.
- Whether AutoCAD passes Ctrl+Z and Esc through to the modeless window was never confirmed.

### Ideas
- Dock the window as a palette (PaletteSet).
- Leader / label with the designation next to an inserted bolt.
- Bill-of-materials attributes on the blocks.

## 9. Owner's standing preferences

- Keep the UI clean and easy to understand; explanations as tooltips.
- All block lines on layer 0 ByBlock; wipeout layer selectable, default `01_WIPEOUT`.
- Block origin on the bolt axis at a contact point.
- English default language; Japanese available.
- No company name in the project.
- He builds with `build.bat`, installs with `install.bat`, commits and pushes from VS Code (Git Graph).

## 10. How to continue (suggested first steps)

1. Close AutoCAD, delete `bin` and `obj`, run `build.bat`, copy the full error list.
2. Fix the errors file by file (section 7). Do not change the conventions in section 4.
3. `install.bat`, start AutoCAD, `BOLTTOOL`; test the list in section 7 ("Runtime things").
4. Commit as "v2 element stack".
5. Then pick items from section 8.
