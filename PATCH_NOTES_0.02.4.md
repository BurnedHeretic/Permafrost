# Permafrost v0.02.4

Fixes BF2 install scanning stopping with `No installed Win32 catalogs were found in layout.toc`.

- Catalog slots are now taken from `installManifest.installChunks` without requiring the folder to exist under `Data` first.
- Catalog order is preserved because manifest file references encode a catalog index.
- Patch-only catalogs are accepted.
- CAS resolution tries the manifest-selected tree, the opposite tree, then a narrow Win32 fallback lookup.
- `cas.cat` metadata remains optional for direct manifest/CAS reading.
- Adds clearer resolution errors including catalog index, CAS number, and attempted paths.
