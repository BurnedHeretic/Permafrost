# Permafrost v0.02.5

This patch addresses the first successful retail Battlefront II install scan.

## What the v0.02.4 diagnostics proved

- The retail `layout.toc` is being parsed correctly.
- 24 Battlefront II install catalogs are discovered.
- The manifest contains 4,777 logical bundles.
- The previous scanner used the manifest `StartIndex/Count` mapping directly and therefore opened the wrong physical CAS ranges for BF2 bundles.
- Catalog strings also retained a trailing `\0`, which forced slow fallback filesystem searches.

## Fixes

- Trim Frostbite DB sized strings at the trailing NUL terminator.
- Cache CAS path resolution rather than repeatedly searching the install tree.
- Added the Battlefront II bundle aggregation compatibility path.
- On first use the editor retrieves the public BF2 aggregation data used by GLACIER and stores it under:

  `%LOCALAPPDATA%\Permafrost\Cache\VanillaBundleAggregation.kb`

- The table is decompressed and validated against **every bundle hash** in the installed manifest before it is trusted.
- The compatibility data itself is not bundled in this source archive.
- If the compatibility map is unavailable, v0.02.5 now fails fast instead of spending minutes probing all 4,777 bundles with a mapping known to be wrong.

## Expected next scan

The status should include:

`Loading cached BF2 bundle aggregation map...`

or on the first run:

`Downloading BF2 bundle aggregation compatibility map (first run only)...`

followed by:

`BF2 bundle aggregation map loaded and validated (4,777 bundles).`

Then bundle header indexing should begin and EBX/RES counts should become non-zero.
