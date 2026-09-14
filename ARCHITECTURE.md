# v0.02 Integration Architecture

## Design rule

The editor never needs to mutate the retail Battlefront II installation. The install is a **read-only source database**. Edits live in a workspace overlay until the mod-output backend packages them.

```text
Battlefront II installation (read-only)
  Data/layout.toc + Patch/layout.toc
                 |
                 v
          GameLayoutReader
                 |
     +-----------+-----------+
     |                       |
 install catalogs         manifest
     |                       |
   cas.cat              bundle map
     |                       |
     +-----------+-----------+
                 |
            BundleReader
                 |
        GameAssetIndex
       EBX / RES / paths
                 |
          GameDataSource
       CAS read + decompress
                 |
           EbxV4Reader
                 |
      LevelSessionBuilder
 LevelData/SubWorld/Layer graph
                 |
       SceneNode hierarchy
                 |
      Editor / 3D viewport
                 |
          Undo/Redo edits
                 |
       Workspace overlays
                 |
        [future ModBuilder]
                 |
          KYBER/BF2 output
```

## Core namespaces

### `Core/Frostbite`
Owns physical game-data formats:

- compact DB-object reader for layout metadata
- install/catalog discovery
- `cas.cat` parser
- manifest file-reference decoding
- bundle header/index reader
- CAS block decompression

This layer does not know about WPF or editor UI.

### `Core/Assets`
Owns the logical installed-game asset database:

- `GameAssetEntry`
- `GameAssetIndex`
- `GameDataSource`

`GameDataSource` is the boundary used by higher layers. It can open an EBX from retail CAS storage or transparently prefer a workspace overlay of the same asset.

### `Core/Ebx`
Standalone self-describing EBX v2/v4 parser. It owns:

- classes / fields / arrays / imports / instances
- internal and external pointers
- field byte locations for safe fixed-size editing
- FileGuid extraction

The long-term writer should live here too. The current writer patches fixed-size `Float32` values in an in-memory copy and never changes structural offsets.

### `Core/Scene`
Converts engine data into editor concepts:

- `SceneNode`
- `SceneTransform`
- `LevelSession`
- `LevelSessionBuilder`

A scene node retains both its `EbxObject` and its owning `EbxDocument`, so selecting an object in the viewport can edit the correct LayerData EBX even when the open root is a LevelData several references above it.

### `Core/Workspace`
Stores overrides outside the game install. `GameDataSource.OpenEbxAsync` checks the workspace first, making saved edits reopenable without modifying retail CAS files.

### `Core/Commands`
Undo/redo boundary. Translation is already command-based so future gizmo operations can use the same stack.

### `Controls/SceneViewport`
Current integration renderer. It deliberately consumes only `SceneNode` and does not know how EBX/CAS works. This allows native MeshSet rendering to replace proxy geometry without rewriting the scanner/editor architecture.

## Reference resolution strategy

Opening a level uses a two-stage GUID index:

1. **Level GUID index** — scans only `levels/...` EBX headers. Fast enough to run automatically and sufficient for recursively resolving LevelData, SubWorldData and LayerData.
2. **Deep EBX GUID index** — optional full EBX header pass. This resolves external object/prefab blueprint GUIDs and is the basis for v0.03 native mesh reconstruction.

Only the EBX header needs to be decompressed to obtain the FileGuid, so deep indexing does not need to fully parse every EBX.

## Editing safety

A translation edit changes three existing `Float32` fields (`trans.x/y/z`) at known byte offsets in an in-memory EBX buffer. Saving writes that buffer to the workspace overlay.

This gives us a safe editing pipeline before implementing a complete structural EBX writer. Object duplication/deletion will require the full writer because arrays, instance tables, reference counts and dependencies must then be rebuilt.

## Native output boundary

Do **not** make the UI write retail CAS files. The future output backend should instead accept:

- original install index
- workspace EBX/RES overrides
- new assets
- bundle membership/dependencies

and produce a separate loadable mod payload. This keeps editing reversible and allows both Frosty-compatible and KYBER-native packaging strategies to be explored without changing the editor model.
