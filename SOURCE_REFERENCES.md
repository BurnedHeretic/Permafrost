# Research references

The backend is an independent implementation based on Battlefront II samples supplied for this project plus publicly visible Frostbite format implementations used for cross-checking.

Key cross-checks used while implementing the direct-install reader:

- **FrostyToolsuite 1.0.6.3** `FrostySdk/FileSystem.cs`
  - Battlefront manifest file-ref bit layout (catalog / Patch flag / CAS index)
  - Patch `layout.toc` preference
  - Manifest `startIndex/count` -> file-list mapping
- **FrostyToolsuite 1.0.6.3** `FrostySdk/IO/EbxReader.cs`
  - EBX v2/v4 magic and self-describing class/field/instance layout
- **ArmchairDevelopers/Glacier** `glacier_fs/src/fb/db_object.rs`
  - compact DB-object type tags used by `layout.toc`
- **ArmchairDevelopers/Glacier** `glacier_fs/src/fb/catalog.rs`
  - `cas.cat` preamble/magic and resource entry layout
- **ArmchairDevelopers/Glacier** `glacier_fs/src/fb/cas.rs`
  - Battlefront II CAS block framing and Zstandard compression code
- **ArmchairDevelopers/Glacier** `glacier_fs/src/fb/bundle.rs`
  - bundle header magic/counts/string table and EBX/RES metadata order

No Frosty or Glacier binaries are bundled or required at runtime.
