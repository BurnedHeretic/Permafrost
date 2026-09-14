# Permafrost v0.02.9

This build is the first release under the **Permafrost** project name.

## Bundle aggregation fix verified against the supplied BF2 file

The uploaded `VanillaBundleAggregation.kb` is 12,582,912 bytes. Its structure is:

- 4-byte big-endian decoded-size prefix: 79,691,776 bytes
- standard Zstandard frame magic: `28 B5 2F FD`
- actual Zstandard frame length: 9,376,152 bytes
- trailing zero padding: 3,206,756 bytes

The previous loader passed the complete fixed-size container to ZstdSharp. After the real frame ended, the decoder saw the zero padding as another frame and reported `Unknown frame descriptor` or `Src size is incorrect`.

v0.02.9 parses the Zstandard frame boundary first, verifies that the remaining container bytes are zero padding, and only supplies the real frame to ZstdSharp.

## Catalog-index correction

The decoded BF2 aggregation database contains 23 runtime catalogs and 4,777 bundles. The raw install manifest currently exposes 24 install chunks because it also includes `installation/default`. The aggregation table intentionally omits that layout-only slot.

Permafrost now switches to the aggregation database's exact catalog order once the table has been validated. This prevents every aggregation `ManifestFileRef` from being shifted by one catalog and resolving to the wrong CAS file.

## Rename

The solution, project, assembly, root namespace, window title, diagnostics, cache path and default workspace path are now all named **Permafrost**.

New paths:

- `%LOCALAPPDATA%\\Permafrost\\Cache`
- `%LOCALAPPDATA%\\Permafrost\\Workspaces`

A one-time compatibility migration copies the old aggregation cache from `%LOCALAPPDATA%\\FrostbiteLevelEditor\\Cache` when present.
