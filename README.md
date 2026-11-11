# Wind-Smile Bolt Tool – AutoCAD 2027 (.NET 10)

Generates blocks of bolts, nuts, plain / large / spring washers and complete bolted connections
(ISO or JIS), in any 2D view (side, ends, top, isometric, free orbit) or as 3D mesh / solid,
with wipeouts, length grips and a tightening-torque calculator.

## Build & load

1. Install the **.NET 10 SDK** (x64).
2. Run `build.bat` (or `build.bat "D:\path\to\AutoCAD 2027"` if AutoCAD is not in the default folder).
3. In AutoCAD: `NETLOAD` → `bin\Release\net10.0-windows\WS.BoltTool.dll`.
4. Autoload at every AutoCAD start: close AutoCAD and run `install.bat` (after each new build, run it again).
   It copies the DLL and `PackageContents.xml` to
   `%APPDATA%\Autodesk\ApplicationPlugins\WS.BoltTool.bundle\`. `uninstall.bat` removes it.

## Commands

Also available as buttons on the **Wind-Smile** ribbon tab (added when the plugin loads).

| Command | What it does |
|---|---|
| `BOLTTOOL` | Opens the tool window |
| `BOLTEDIT` | Pick a placed bolt block → loads its settings → change → **Update selected block** |
| `BOLTTORQUE` | Opens the window on the torque calculator |

## Window

* **Part** – standard (ISO / JIS), part type, size, length (standard list or any value), insertion point,
  thread (none / simplified / detailed helix). *Set as default* stores your most used length per size.
* **Assembly** – bolt + washer under head + clamp length + washer / spring washer (nut side or head side)
  + nut / nut + lock nut. Bolt length is chosen automatically (next standard length with 2 pitches
  protruding) or set manually. *Separate block per part* creates one block per part, inserted as a linked set.
* **View & Block** – 2D view / 3D mesh / 3D solid, view presets, azimuth / elevation sliders, hex spin
  (corners or flats), hidden lines, scale, wipeout on/off and the wipeout layer (default `01_WIPEOUT`).
* **Torque** – property class, surface, lubrication, clamped material → preload, tightening torque
  (with ± friction range), surface-pressure check and a table for all sizes.
* **Preview** – drag to orbit, the round *orbit* joystick (bottom right) rotates continuously while held,
  wheel = zoom, right-drag = pan, double-click = fit. Red cross = block origin 0,0,0, blue triangles = grips.

* **Language** – English / 日本語 switch at the top of the window (remembered). Hover any field for a pop-up explanation.
* **Keyboard** – arrow keys rotate the view by 1° (Shift 15°, Ctrl 0.1°); Ctrl+Z / Ctrl+Y undo and redo setting changes.
* **Clean 2D output** – overlapping and duplicate lines are merged before the block is written (like OVERKILL).

## Block rules

* Block origin **0,0,0 = connection point on the axis** (default: middle of the bolt head tip;
  nuts / washers: bearing face) → parts snap together with INSertion / NODe points.
* All lines on layer **0**, colour / linetype / lineweight **ByBlock**. Only wipeouts go on the wipeout layer.
* Draw order inside a block: wipeout first, then lines. For separate parts the bolt is inserted first,
  then washers and nuts, so their wipeouts cover the shank correctly.
* Hide the wipeout frame with `WIPEOUTFRAME 2` (shown on screen, not plotted) or `0`.
* Block names: `WS_ISO_HB_M12x40_SIDE_A1B2C3D4` – identical settings reuse the same definition.

## "Dynamic" length

AutoCAD's .NET API can't author dynamic-block parameters, so the tool uses a **grip overrule** instead
(works on every block it created, settings are stored in XData):

* ▶ grip at the thread end → drag to change the bolt length (snaps to standard lengths, option).
* ▶ grip on the nut (or on the nut side of an assembly block) → drag to change the clamp length:
  washers / spring washer / nuts move together and, with automatic length, the bolt length follows.
* Option *Nut side follows the bolt end*: when you stretch the bolt, the nut side keeps its protrusion.

## Data

Dimension tables are written to `%APPDATA%\WindSmile\BoltTool\standard_ISO.json` / `standard_JIS.json`
on first use. Check and edit them there (e.g. catalogue values of your supplier) – restart AutoCAD
or reload the DLL to apply. Values are typical catalogue values; verify for critical use.

The torque calculator follows simplified VDI 2230-1 (90 % yield utilisation, μ from typical tables).
It's for guidance only – use the fastener supplier's friction data for critical joints.

## Known limits

* Not compiled in the authoring environment (no .NET SDK there) – the first build may need small fixes.
* 3D solid with detailed helical thread usually can't be converted to an ACIS solid (not watertight)
  → it stays a mesh. Use simplified/no thread for solids.
* Wipeouts can't have holes – in end views a nut's wipeout also covers the bolt hole.
