# Bolt Tool – AutoCAD 2027 (.NET 10)

![Bolt Tool window](docs/screenshot.png)

An AutoCAD plugin that generates blocks of bolts, nuts, plain / large / spring washers and complete
bolted connections (ISO or JIS). Blocks can be any 2D view (side, ends, top, isometric, free orbit)
or a 3D mesh / solid, with wipeouts, stretch grips for length, and a tightening-torque calculator.

## Contents

- [Requirements](#requirements)
- [Build, load and update](#build-load-and-update)
- [Commands](#commands)
- [The window](#the-window)
- [Preview controls](#preview-controls)
- [Moving washers and nuts in the preview](#moving-washers-and-nuts-in-the-preview)
- [Grips, measuring and snapshot in the preview](#grips-measuring-and-snapshot-in-the-preview)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [In the drawing](#in-the-drawing)
- [Block rules](#block-rules)
- [Torque calculator](#torque-calculator)
- [Settings and data files](#settings-and-data-files)
- [Known limits](#known-limits)
- [Project layout](#project-layout)

## Requirements

- AutoCAD 2027 (64-bit)
- .NET 10 SDK (x64) to build – https://dotnet.microsoft.com/download/dotnet/10.0
- Windows

## Build, load and update

**First time**

1. Run `build.bat`. If AutoCAD is not in `C:\Program Files\Autodesk\AutoCAD 2027`, pass its folder:
   `build.bat "D:\Autodesk\AutoCAD 2027"`.
2. Either load it once by hand – in AutoCAD type `NETLOAD` and pick
   `bin\Release\net10.0-windows\BoltTool.dll` –
   or make it load at every start: close AutoCAD and run `install.bat`.

`install.bat` copies the DLL and `PackageContents.xml` to
`%APPDATA%\Autodesk\ApplicationPlugins\BoltTool.bundle\`. `uninstall.bat` removes that folder.

**Updating after a code change**

1. Close AutoCAD (it cannot replace a loaded plugin).
2. Run `build.bat`.
3. Run `install.bat`.
4. Start AutoCAD.

> [!IMPORTANT]
> **Older AutoCAD (2025 / 2026, .NET 8):** build with a different target instead of `build.bat`:
> ```
> dotnet build BoltTool.csproj -c Release -p:TargetFramework=net8.0-windows -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2025"
> ```
> The DLL is then in `bin\Release\net8.0-windows\` – load it with `NETLOAD` (`install.bat` only copies the .NET 10 build).
> Not tested on those versions yet. AutoCAD 2024 and older (.NET Framework 4.8) are not supported.

## Commands

All three are also buttons on the **Bolt Tool** ribbon tab, which is added when the plugin loads.

| Command | Ribbon button | What it does |
|---|---|---|
| `BOLTTOOL` | Bolt Tool | Opens the tool window |
| `BOLTEDIT` | Edit Bolt | Pick a placed Bolt Tool block → its settings load into the window → change them → **Update selected block** |
| `BOLTTORQUE` | Torque | Opens the window on the torque calculator |

## The window

The language (English / 日本語) is switched at the top left; English is the default. Hover any field
for a pop-up explanation. The window reopens in the state it was closed in.

### Part tab – one single part

| Control | Meaning |
|---|---|
| Standard | ISO or JIS dimensions |
| Part | Hex bolt (partial / full thread), socket head cap screw, threaded rod, hex nut, thin nut, flange nut, channel nut, channel nut + holder, plain washer, large washer, spring lock washer |
| Size | M3 … M48. Changing it loads the default length of that size |
| Length L | Length under the head (threaded rod: total length). Pick a standard length or type any value |
| Set as default | Stores the current length as the default for this size |
| Insertion / connection point | Where the block origin 0,0,0 sits on the axis (see Block rules) |
| Thread | None, simplified (thin root lines) or detailed (real helix) |
| Dimensions | The main dimensions of the selected part |
| What the letters mean | Small picture showing where d, P, s, k, L, b, m, d1, d2, h are measured |

### Assembly tab – a bolted connection

| Control | Meaning |
|---|---|
| Create a bolted connection | On: bolt + washers + nuts together. Off: only the single part from the Part tab |
| Bolt | Bolt type used in the connection |
| Clamp length | Total thickness of the parts the bolt holds together. 0 is allowed – the head-side and nut-side elements then touch |
| Usual set / Double nut / Spring both sides / No nut (tapped hole) / Channel nut set | Ready-made arrangements. Double nut uses two equal nuts. Channel nut set = spring washer + large washer under the head, channel nut + holder on the other side (fixing to a strut channel) |
| Sort to usual order | Washer against the part, then spring washer, then nut, thin nut last |
| Element list | Every washer and nut in its real order from the head to the bolt end. Each row: kind, **T** thickness, **Ø** outer diameter, **gap** free space in front of it, ▲ ▼ to reorder, ✕ to remove. Clear T or Ø to go back to the standard size |
| ＋ Add on the head side / nut side | Adds a plain washer, large washer, spring washer, hex nut, thin nut, flange nut, channel nut or channel nut + holder |
| Diagram | Side view with the order, free gaps, clamp length, protrusion and bolt length |
| Choose standard length automatically | Picks the shortest standard length that fits everything plus the protrusion |
| Thread sticking out beyond the last nut | Type it in pitches or in mm – the two boxes convert into each other |
| Separate block per part | On: each part is its own block, inserted as a linked set. Off: one block for the whole connection |
| Nut side follows the bolt end | When the bolt is stretched with its tip grip, the nut side moves with the bolt end |
| Close all gaps | Every element touches the one before it again (the clamp length stays) |

The **Length** box on the Part tab always shows the current bolt length. With automatic length it follows
what the connection needs; choosing a length there switches to manual length.

**How the positions are worked out** (from the underside of the head toward the tip): head-side elements
with their gaps → clamp length → nut-side elements with their gaps → thread sticking out beyond the last
nut. With automatic length the bolt is the next standard length that fits this total.

#### Channel nuts (strut channel / ダクターチャンネル)

| Element | What it is |
|---|---|
| Channel nut (for 41 channel) | Rectangular nut 35 × 20 mm (40 mm long above M16), thickness 0.8 × d (8 mm for M10). Two opposite corners are cut so it can be turned in the channel; two V-grooves on the face that grips the channel lips. Its long side lies across the channel |
| Channel nut + holder (翼型ナット + 固定ブロック) | The channel nut plus its plastic holder: plate 46 × 28 × 2 mm lying along the channel outside the lips, with V-notches and two wing tabs, and a Ø18 collar in the slot. The collar height is the channel lip thickness (2.5 mm by default) – change it with the element's **T**: total T = plate 2 + lip + nut thickness |

For the holder version set the clamp length to the thickness of the part you fix only (e.g. the 目隠し板);
the holder plate and the lip are already inside the element. The grooved face always points to the clamped parts.

### View & Block tab – what goes into the block

| Control | Meaning |
|---|---|
| Output | 2D view, 3D mesh or 3D solid |
| Azimuth / Elevation | Exact view direction. Azimuth 0° = side, +90° = head end, −90° = thread end; elevation 90° = top, 35.26° = isometric |
| Hex spin | Turns the hexagon about the axis: 0° across corners, 30° across flats |
| Show hidden lines | Adds hidden edges with the HIDDEN linetype |
| Scale | Size factor of the block (1 = real size in mm) |
| Add wipeout behind each part | Masks whatever lies under the block |
| Wipeout layer | Layer for the wipeouts (default `01_WIPEOUT`; a new name is created automatically) |
| Length grip snaps to standard lengths | Dragging the length grip jumps between standard lengths |

### Bar under the preview

| Control | Meaning |
|---|---|
| Side, Head end, Thread end, Top, Iso SW / SE / NE / NW | Jump to a standard view |
| Snap view angles | While orbiting, the view catches on standard angles (see below) |
| Show wipeouts | Shows the wipeout areas in the preview only |

### Bottom buttons

| Button | Meaning |
|---|---|
| Insert into drawing | Creates the block(s) and lets you place them (point, then rotation) |
| Update selected block | Shown after `BOLTEDIT`: applies the settings to the picked block |
| Create block only | Creates the block definition without placing it |
| Reset | All settings back to defaults |

## Preview controls

The preview shows exactly what the block will contain. The red cross is the block origin 0,0,0, the
blue triangles are the stretch grips.

| Action | Result |
|---|---|
| Left-drag | Orbit the view |
| Round **orbit** joystick (bottom right) | Hold and push: rotates continuously, faster further from the centre |
| Mouse wheel | Zoom |
| Right-drag or middle-drag | Pan |
| Double-click | Fit |

The zoom stays the same from every direction while orbiting, and the view does not jump or re-zoom when
settings change. Use **Fit view** or double-click the preview if newly added parts extend beyond the visible frame.

**View snapping** (when *Snap view angles* is ticked): turning left / right catches every 30° and 45°;
tilting up / down catches at 0°, 30°, 35.26° (isometric), 45°, 60° and 90°. Arrow keys never snap.

## Moving washers and nuts in the preview

Works in Assembly mode.

| Action | Result |
|---|---|
| Ctrl+click a washer or nut | Selects it (highlighted blue, also in the element list). Ctrl+click more to add them |
| Ctrl+click a selected part | Removes it from the selection |
| Ctrl+click empty space, or Esc | Clears the selection |
| Ctrl+drag a part | Moves all selected parts along the bolt in 0.5 mm steps. They stop when they touch the next element |
| Keep pulling past the neighbour | A green outline shows where the part will land. Release to swap places – the order in the element list changes with it |
| Red distance labels | The free distance in front of each selected part, measured from the element just before it (or from the underside of the head). For the first nut-side part this is clamp length + its gap |
| Click a red label | Type an exact distance, Enter = apply, Esc = cancel |

Elements cannot overlap: every item has a free gap in front of it, separate from the solid
**clamp length**. Moving the first nut-side element away from the head increases its free gap.
Moving it toward the head first closes its free gap and then makes the clamped parts thinner, down
to 0 – so the nut side can come right up to the head-side washer. Moving the last head-side element away from the head shifts the plate
and nut-side parts together. Reordering by pulling past another item works within the same
side of the clamped parts; use the element list to move an item to the other side. Moving does
not work when looking straight along the bolt (end views).

## Grips, measuring and snapshot in the preview

| Action | Result |
|---|---|
| Drag the blue ▶ at the bolt tip | Changes only the bolt length (switches an assembly to manual length) |
| Drag the blue ▶ on the first nut-side element | Moves the whole nut side (clamp length); the bolt length follows |
| Shift + click two points | Green dimension with arrows. The number is the **projected 2D distance** in the current view, not necessarily the physical 3D distance. Snaps use drawing vertices (not midpoints/centres) |
| Esc | Removes the measurements and the selection |
| Save JPG | Saves the preview exactly as shown, with its dimensions, as a JPG file |

Measurements belong to one view; they are removed when the view is rotated or the parts change.
The blue **L** arrow adjusts bolt length, while **Clamp** adjusts the solid material thickness.
They are plugin-specific preview grips, not ordinary drawing construction lines.

## Keyboard shortcuts

| Key | Result |
|---|---|
| Arrow keys | Rotate the view by 1° |
| Shift + arrow | Rotate by 15° |
| Ctrl + arrow | Rotate by 0.1° |
| Ctrl+Z | Undo the last settings change |
| Ctrl+Y or Ctrl+Shift+Z | Redo |
| Esc | Clear the part selection / close the distance field |
| Enter in a number field | Apply the value |

Arrow keys rotate when the focus is not in a text box, list or slider – click the preview first.

## In the drawing

AutoCAD's .NET API cannot create dynamic-block parameters, so the tool adds its own stretch grips to
every block it created (the settings are stored on the block reference as XData). They only work while
the plugin is loaded.

- **▶ grip at the bolt tip** – drag to change only the bolt length (snaps to standard lengths if the option is on).
- **▶ grip on the first nut-side element** (on its rim, at the face that touches the clamped part) – drag to
  move the whole nut side: washers, spring washer and nuts move together and the bolt length follows.
- **`BOLTEDIT`** – reopens any placed block in the window for full editing.

For a linked set (separate blocks), editing or stretching one part updates the others.

## Block rules

- Block origin **0,0,0 = connection point on the axis**. Choices for a connection: middle of the head top
  (default), head-side contact face (where the head or its washers press on the part), nut-side contact
  face (where the nut-side washers / nut press on the part), or the bolt tip. Single nuts and washers:
  bearing face or outer face.
- All lines are on layer **0** with colour / linetype / lineweight **ByBlock**. Only wipeouts go on the wipeout layer.
- Inside a block the wipeout is drawn first, then the lines. For separate parts the bolt is inserted
  first, then washers and nuts, so their wipeouts cover the shank.
- 2D output is cleaned before it is written: overlapping and duplicate lines are merged (like `OVERKILL`).
- Hide wipeout frames with `WIPEOUTFRAME 0`; `WIPEOUTFRAME 2` shows them on screen but does not plot them.
- Block names look like `BT_ISO_HB_M12x40_SIDE_A1B2C3D4`; identical settings reuse the same definition.

## Torque calculator

Choose the method, size, property class, bolt / nut surface, lubrication and the clamped material.
Friction values fill in from the lists and can be overtyped.

| Method | Preload | Usual utilisation |
|---|---|---|
| VDI 2230 (Europe / ISO practice) | yield utilisation by the combined tension + torsion stress | 90 % |
| JIS B 1083-style **simplified coefficient estimate** (Japan) | user-entered utilisation × yield × stress area | 70 % example **only**, not specified by JIS |

The JIS selection is a **simplified coefficient calculation**, not a full
implementation of the standard's yield-clamp-force and joint-control procedures.

Both use the same torque formula `T = F · (0.16·P + 0.58·d2·μG + μK·D_Km/2) = K·d·F`; the torque
coefficient K is shown with the result. Results: preload, tightening torque with a range for μ ± 0.02,
surface-pressure check under the head and a table for all sizes.

- **Property class** – a short note under the list says where each class is typically used.
- **Materials** – JIS materials (SS400, SM490, S45C, SCM440, SUS304, FC250, FCD450, A5052, A6063, ADC12),
  conductor materials (copper C1100, brass, aluminium busbar) and EN materials.
- **Electrical joints** – for a conductor material a yellow note reminds you that the torque of the
  equipment or terminal maker governs; the displayed mechanical torque estimate is **not** an approved
  electrical-terminal tightening torque or a verified mechanical safe limit.

> [!WARNING]
> Friction coefficients and permissible surface pressures are typical or approximate values, not supplier
> data, and the property-class notes describe common practice only. Use the results as guidance and follow
> the drawing, specification and supplier for critical joints.

## Settings and data files

Everything is stored in `%APPDATA%\BoltTool\`:

| File | Content |
|---|---|
| `standard_ISO.json`, `standard_JIS.json` | Dimension tables and default lengths – editable |
| `settings.json` | Last used part, assembly, view and block settings |
| `ui.json` | Window size and position, last tab, torque selections |
| `language.txt` | `en` or `ja` |

> [!WARNING]
> The dimension tables (especially JIS head / nut sizes and spring washers) are typical catalogue values
> that have not been checked against the standards. Compare them with your supplier's catalogue and
> correct the JSON files where needed. Restart AutoCAD after editing.

Deleting a file resets that part to its defaults.

## Known limits

- A 3D solid with detailed helical thread usually cannot be converted to a real solid and stays a mesh.
  Use simplified or no thread for solids.
- Wipeouts cannot have holes: in end views a nut's or washer's wipeout also covers its hole.
- Elements can be moved past the thread end; with a manual bolt length this gives a "shorter than required" warning.
- The flange nut dimensions are an approximation of ISO 4161 / JIS B1190.
- Channel nut and holder dimensions are approximations (catalogue M10 channel nut 35 × 20 × 8; holder taken from an
  installation drawing). The holder's wing tabs are drawn flat. Check against the maker's drawing (e.g. MLAN-20S).
- The preview length/clamp arrows and drawing grips are **custom Bolt Tool overrules**, not native
  AutoCAD dynamic-block parameters. They require the plugin to be loaded.
- The v2 source compiles on AutoCAD 2027; the latest additions (channel nuts, clamp squeeze) still need testing in AutoCAD.
- Blocks placed with an older version (before the element list) lose their washers / nuts links when edited; re-insert them.
- The Japanese texts are unreviewed.
- Blocks already in a drawing keep their old geometry after a plugin update; re-insert or `BOLTEDIT` them.

## Project layout

| Folder / file | Role |
|---|---|
| `Data/` | Size tables, block parameters (`BoltJob`), saved settings |
| `Geometry/` | Part meshes, assembly layout, hidden-line renderer |
| `Cad/` | Block creation, insert jig, linked-set updates, stretch grips |
| `UI/` | Window, preview, legend, ribbon tab, English / Japanese texts |
| `Torque/` | Torque calculation |
| `Commands.cs` | AutoCAD commands and plugin start-up |
| `build.bat`, `install.bat`, `uninstall.bat` | Build and autoload |
| `HANDOFF.md` | Developer notes for continuing the work |
