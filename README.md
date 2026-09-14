# Permafrost v0.02.10

This build fixes the BF2 bundle aggregation container using the real uploaded compatibility file: Permafrost now isolates the 9,376,152-byte Zstandard frame from the 3,206,756-byte zero-padded tail before decompression, and then adopts the aggregation table's exact 23-catalog runtime order.

# Permafrost — Standalone v0.02

Standalone level-editor research/build for **STAR WARS™ Battlefront™ II (2017)**, aimed at custom-map production for the Galactic Conquest + KYBER workflow.

v0.02 is the first **direct-install integration** build. The editor no longer needs Frosty to *read* the game-data index or to open supported level EBX assets.

## v0.02 milestone

The application now has four connected layers:

1. **Installed-game scanner**
   - Reads `Data/layout.toc` / `Patch/layout.toc`.
   - Reads the Frostbite compact DB-object layout metadata.
   - Discovers Win32 install catalogs.
   - Reads `cas.cat` catalog metadata.
   - Reads the Battlefront II manifest and its bundle/file mapping.
   - Reads bundle headers to index EBX and RES assets by Frostbite path.
   - Reads CAS block streams, including Battlefront II Zstandard blocks.

2. **Asset index**
   - Searchable EBX/RES index.
   - Level-path list (`levels/...`).
   - Fast level-only EBX FileGuid index for resolving `LevelData -> SubWorldData -> LayerData` links.
   - Optional **Deep Index All EBX GUIDs** pass for resolving non-level blueprint GUIDs too.

3. **Level session / scene graph**
   - Opens a level directly from the installed game by asset path.
   - Recursively follows level/subworld/layer external EBX references.
   - Builds an Outliner while preserving which EBX owns each object.
   - Finds Frostbite `LinearTransform` data, including `BlueprintTransform` and nested transform structures.
   - Displays transform-bearing objects as selectable 3D proxies.

4. **Safe editing workspace**
   - X/Y/Z translation editing.
   - ±1-unit nudge controls.
   - Viewport object selection.
   - Undo/redo.
   - Modified EBXs are written to a separate workspace overlay under `%LOCALAPPDATA%`.
   - **The original Battlefront II Data/Patch files are never overwritten.**

The app still supports opening the exported `.bin` EBX samples from v0.01 as a fallback/debug path.

## Build requirements

- Windows 10/11
- Visual Studio 2022
- .NET 8 SDK
- Visual Studio workload: **Desktop development with .NET**
- NuGet restore enabled

Open `Permafrost.sln`, set `Permafrost` as the startup project, restore packages, and build x64/Any CPU.

### NuGet dependency

`ZstdSharp.Port` is used only for the Zstandard-compressed CAS blocks used by Battlefront II.

## First direct-install test

1. Run the editor.
2. Point **Game install** at the folder containing:
   - `Data/layout.toc`
   - `Patch/layout.toc` (normally present in an updated installation)
3. Click **Scan Install**.
4. Wait for the status bar to finish indexing bundle headers and level EBX GUIDs.
5. In **Levels**, search:
   `levels/mp/kamino_01/kamino_01`
6. Select the exact Kamino root level asset and click **Open Selected Level Asset**.
7. Switch to the **Outliner** (opening does this automatically).
8. Expand the hierarchy. The target architecture chain should ultimately include the Landing Pad layer and its placed `SpatialPrefabReferenceObjectData`.
9. Select a transform-bearing object. The viewport proxy and Properties panel should show its real transform.
10. Change X/Y/Z or use the ±X/±Y/±Z buttons.
11. Use **File → Save Workspace Changes**.

For the known Kamino landing-pad architecture placement, the reference transform from the supplied sample is approximately:

- X `83.681076`
- Y `-20.178137`
- Z `253.010437`
- Y rotation `45.412°`

The editor can move that placement in memory/workspace now. v0.02 deliberately does **not** write back into retail CAS files.

## Controls

- **LMB** — select proxy
- **RMB drag** — orbit camera
- **MMB drag** — pan camera
- **Mouse wheel** — zoom
- **Focus** — frame selected object
- **Frame All** — frame all transform proxies
- **Ctrl+Z / Ctrl+Y** — undo / redo

## Workspace layout

By default the editor creates:

`%LOCALAPPDATA%/Permafrost/Workspaces/<install-name>/`

with modified EBX overlays under:

`Modified/Ebx/<frostbite-asset-path>.bin`

That separation is intentional. The future mod builder will consume the workspace overlays and produce the actual BF2/KYBER loadable output.

## Known limitations in v0.02

- This is the **first untested-on-a-retail-install integration pass in this environment**. The code follows the BF2/Frostbite layout, manifest, bundle and CAS structures, but I cannot mount your local Battlefront II `Data`/`Patch` directories here. If a particular retail/update manifest variant differs, export the **Diagnostics** tab and we can add the compatibility case immediately.
- The viewport currently renders **placement proxies**, not native BF2 meshes.
- Rotation/scale are decoded and displayed but v0.02 only writes translation. This avoids corrupting the Frostbite basis vectors until the transform-gizmo implementation is validated against the game.
- Non-level blueprint names require the optional deep GUID index. Native prefab/mesh resolution is the next renderer milestone.
- Workspace changes are not yet packaged into a `.fbmod`, KYBER mod, or patched bundle. That is the native output milestone after install reading is proven.
- Terrain, navmesh/pathfinding, objectives, spawn editing and custom imports are later layers of the same architecture.

## Next milestones

### v0.03 — Native prefab / mesh viewport
- Resolve `SpatialPrefabReferenceObjectData.Blueprint` through the full GUID index.
- Recursively expand `SpatialPrefabBlueprint` contents.
- Resolve `RigidMeshAsset -> MeshSetResource`.
- Read MeshSet RES/chunks.
- Render the real Kamino architecture rather than proxy cubes.
- Use the supplied `MeshVariationDb_Win32` data to begin material/variation resolution.

### v0.04 — Editing tools
- Real move/rotate/scale gizmos.
- Multi-select.
- Duplicate/delete object instances.
- Layer visibility/locking.
- Better dirty-state tracking and per-asset revert.

### v0.05 — Native mod output
- Consume workspace EBX modifications.
- Preserve/rebuild required bundle references.
- Produce a BF2/KYBER-loadable mod without modifying the original installation.
- Add one-click test launch hooks.

### Later
- Terrain sculpt/paint.
- Custom mesh/texture import.
- Collision tools.
- Gameplay/spawn/objective boundaries.
- AI/pathfinding tools.
- KYBER level declaration and Galactic Conquest metadata.

See `ARCHITECTURE.md` for the backend boundaries and `TESTING.md` for the exact first-run validation sequence.


## v0.02.6 install-root resolver

The install picker is forgiving: you can select the Battlefront II root, `Data`, `Patch`, or a folder beneath them. The scanner walks upward to the root containing `Data\layout.toc`. If resolution fails, the error now includes the selected folder and the concrete paths checked.


## v0.02.6 retail install compatibility

The direct BF2 scanner now supports the Battlefront II bundle aggregation mapping and trims trailing NULs from layout catalog strings. The compatibility map is cached under `%LOCALAPPDATA%\Permafrost\Cache` and validated against the installed manifest before use.

## v0.02.9 aggregation/container fix

The supplied `VanillaBundleAggregation.kb` proved that its four-byte decoded-size prefix is big-endian (`0x04C00000` = 79,691,776 bytes). The file is a fixed 12 MiB container: the real Zstandard frame ends at 9,376,152 payload bytes and the final 3,206,756 bytes are zero padding. Permafrost now strips that padding before decompression. The decoded table contains 23 runtime catalogs and 4,777 bundle mappings; that catalog order replaces the provisional 24-entry install-chunk list for aggregation file references.


## v0.02.10 BF2 bundle-header endian fix

The v0.02.9 retail diagnostic proved that the 23-catalog aggregation table validates and all 4,777 physical bundle mappings are available, but every bundle header was rejected. The cause was byte order in `BundleReader`: Frostbite bundle metadata is big-endian, while .NET `BinaryReader.ReadUInt32/ReadUInt64` is little-endian. v0.02.10 now reads bundle magic/counts/name offsets/original sizes/RES types/RES ids in big-endian order. It also emits the raw/decompressed header bytes and the precise parser/decompression failure in diagnostics if a bundle still fails.
