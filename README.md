# Permafrost

**Permafrost** is an experimental level editor and Frostbite authoring toolkit for **STAR WARS™ Battlefront™ II (2017)**.

The project is focused on making Battlefront II levels easier to inspect, reconstruct, edit, repurpose, and export without relying on a collection of separate tools. Permafrost aims to provide a single editor for map layout, object placement, prefab and blueprint editing, material changes, objective template swaps, and Frostbite asset inspection.

> **Project status:** Active development  
> **Current milestone:** `v0.06.x` — native Frostbite parsing, editing, and export pipeline

---

## Overview

Permafrost began as a Battlefront II map viewer and has grown into a broader level-authoring environment.

The long-term goal is to reconstruct Battlefront II maps using the same Frostbite relationships the game itself relies on, rather than approximating scenes through loose dependency scanning.

Current development is centered around:

- authoritative EBX parsing
- native EBX → RES → CHUNK relationships
- MeshSet reconstruction
- prefab / blueprint / object placement
- editable transforms
- map and project saving
- cross-map object reuse
- material swapping
- Starfighter Assault objective template swapping
- Frosty-compatible mod export

---

## Current Features

### Level loading and scene reconstruction

- Scan a Battlefront II installation
- Fast cached asset indexing
- Discover retail map roots
- Load ground and space levels
- Resolve linked subworlds and layers
- Reconstruct native Frostbite scene hierarchies
- Parse EBX, RES, and CHUNK asset relationships
- Native MeshSet rendering
- Composite MeshSet support
- StaticModelGroup instance expansion
- Nested prefab transforms
- Collision and light visualization
- Level Outliner with searchable scene hierarchy

### Editing

- Select, move, rotate, and scale level objects
- Transform gizmos and numeric transform editing
- Snap / nudge controls
- Duplicate and delete writable placements
- Undo / Redo
- Dirty-state tracking
- Save and Save As project workflows

### Game Palette

Permafrost includes a searchable **Game Palette** for using assets that already exist in Battlefront II.

Supported workflows include:

- place existing game prefabs
- place blueprints / object blueprints
- reuse retail map objects
- browse mesh assets
- use existing game assets as material donors

The aim is to make level creation feel closer to a traditional game editor while still writing valid Frostbite data.

### Cross-map object import

Objects can be imported from another Battlefront II level while preserving compatible placement information such as:

- position
- rotation
- scale
- blueprint / prefab references
- supported instance data

This makes it possible to reuse map content without manually recreating every reference.

### Mesh import

External editor geometry can currently be imported from:

- OBJ
- STL

Imported external meshes are treated as Permafrost authoring geometry.

They are **not automatically converted into native Frostbite MeshSet resources**. Native arbitrary-mesh import remains a separate future pipeline.

### Material swapping

Permafrost can swap embedded mesh material slots using another Battlefront II mesh as a donor.

This includes:

- locating the selected object's MeshAsset
- enumerating material slots
- selecting donor assets from the retail game palette
- copying compatible material references
- remapping required EBX imports
- storing the result as an editable workspace overlay
- Undo / Redo support

Full in-editor Frostbite shader and texture preview is still under development.

---

## Prequel Space Objective Templates

Permafrost includes a dedicated objective-template workflow for the prequel-era Starfighter Assault maps.

Current templates:

- **Map Default**
- **Republic Defense — Kamino**
- **CIS Defense — Ryloth**

The objective switcher is designed to transplant the complete gameplay objective setup rather than only replacing visible objective props.

This includes compatible versions of:

- SpaceBattle gameplay
- AI
- automation
- main gameplay logic
- Phase 1
- Phase 2
- Phase 3
- scripted events
- secondary objectives
- cruiser objectives
- spawns
- turrets
- associated schematics
- dependent prefab / blueprint EBXs required by those objective layers

For example, **Ryloth can be configured to use the Kamino Republic Defense objective structure**, while retaining Ryloth's own environment, art, backdrop, lighting, and level design.

---

## Project Files

Permafrost uses `.pfmap` project files to preserve editor state.

A project can contain:

- modified EBX overlays
- structural map edits
- imported placements
- external editor meshes
- transforms
- workspace state
- map-specific authoring information

Retail Battlefront II files are not modified directly.

---

## Export

The **File** menu currently provides:

- New
- Open
- Save
- Save As
- Export EBX
- Export as `.fbmod`
- Export Log Diagnostics
- Exit

### EBX export

Modified EBXs can be exported directly for debugging or use in other Frostbite workflows.

### `.fbmod` export

Permafrost can build Frosty-style `.fbmod` output for edited EBX content.

Objective-template exports also follow required external EBX dependencies so that transplanted gameplay layers can include assets that may normally belong only to the source map's bundles.

This area is still under active development and should be treated as experimental.

---

## Native Frostbite Pipeline

The `0.06.x` branch represents a major architectural change.

Earlier Permafrost builds relied more heavily on recursive dependency discovery. The current pipeline is being rewritten around Frostbite's actual object and resource semantics.

Current work includes:

- typed EBX object interpretation
- internal and external pointers
- boxed values
- exported instance GUIDs
- authoritative render relationships
- MeshAsset semantics
- SpatialPrefab handling
- ObjectBlueprint handling
- StaticModelGroup handling
- MeshSet LOD and section parsing
- composite mesh part transforms
- native primitive topology
- bundle-local asset resolution

The goal is simple:

> **If Battlefront II can render and instantiate an object, Permafrost should eventually be able to reconstruct and edit that same object using the same underlying asset relationships.**

---

## Performance

Large Battlefront II levels contain thousands of objects and can reference very large dependency graphs.

Permafrost includes several optimizations:

- persistent install asset-index cache
- EBX GUID cache
- cached CAS access
- single-flight EBX loading
- resolver result caching
- direct MeshAsset caching
- scene-tree virtualization
- background native-mesh resolution
- render-only dependency pruning
- duplicate placement suppression
- shared WPF mesh geometry for repeated instances

A first scan is slower because Permafrost builds its caches. Later launches should be significantly faster when the game installation has not changed.

---

## Building

### Requirements

- Windows 10 or Windows 11
- Visual Studio 2022 or newer
- .NET SDK / desktop workload required by the solution
- A legal installation of **STAR WARS Battlefront II (2017)**

### Build

1. Clone the repository.
2. Open `Permafrost.sln`.
3. Restore NuGet packages if Visual Studio does not do so automatically.
4. Build the solution.
5. Launch Permafrost.
6. Set your Battlefront II installation path.
7. Select **Scan Install**.

Example Steam installation path:

```text
C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Battlefront II
```

---

## Basic Workflow

```text
Scan Install
    ↓
Choose a level
    ↓
Wait for scene + native MeshSet reconstruction
    ↓
Select objects in the viewport or Outliner
    ↓
Move / Rotate / Scale / Duplicate / Delete
    ↓
Place new content from the Game Palette
    ↓
Optionally swap materials or space-map objective templates
    ↓
Save the .pfmap project
    ↓
Export EBX or .fbmod
```

---

## Diagnostics

Permafrost can export detailed diagnostics for troubleshooting.

The diagnostic output includes information such as:

- bundle scan status
- cache status
- EBX / RES / CHUNK counts
- loaded level documents
- native MeshSet resolution
- unresolved objects
- dependency traversal
- duplicate suppression
- StaticModelGroup expansion
- composite MeshSet reconstruction
- objective-template activity

When reporting a rendering or parser issue, please include:

1. Permafrost version
2. map name
3. screenshot of the viewport
4. exported diagnostics log
5. any relevant Frosty-exported EBX `.bin` files if available

---

## Development Status

Permafrost is **not finished software**.

Expect:

- incomplete Frostbite class coverage
- maps that reconstruct more accurately than others
- some unsupported asset types
- incomplete shader / texture rendering
- export edge cases
- occasional parser issues with unusual generated assets

The project is evolving quickly and file formats or project structures may change between versions.

---

## Roadmap

Major areas still planned:

- full Frostbite material and texture rendering
- shader parameter inspection
- native texture replacement
- improved terrain editing
- arbitrary native MeshSet import
- richer prefab creation
- object property editing UI
- gameplay logic visualization
- objective graph editing
- additional gamemode templates
- layer / phase visibility controls
- improved `.fbmod` compatibility
- map validation tools
- packaging / release builds

---

## Contributing

Bug reports, parser research, format documentation, and code contributions are welcome.

Useful reports include:

- map and level name
- Permafrost version
- exact reproduction steps
- screenshots
- exported diagnostics
- expected vs actual result

Please avoid distributing copyrighted game assets in issues or pull requests.

---

## Legal

Permafrost is an unofficial fan-made modding and development tool.

It is **not affiliated with, endorsed by, or sponsored by Electronic Arts, DICE, Lucasfilm, Disney, or Frostbite**.

**STAR WARS**, **STAR WARS Battlefront**, and related names and assets are trademarks and copyrighted works of their respective owners.

Permafrost does not include Battlefront II game assets. Users are expected to provide their own legally obtained game installation.

---

## Credits

Permafrost is built through reverse engineering, experimentation, and study of the wider Frostbite modding ecosystem.

Special acknowledgement goes to the developers and contributors behind **Frosty Tool Suite** and the wider Battlefront modding community, whose public research and tooling have helped document many Frostbite formats and workflows.

---

<p align="center">
  <b>PERMAFROST</b><br>
  Battlefront II Level Editor
</p>
