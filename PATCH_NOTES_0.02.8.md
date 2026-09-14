# Permafrost v0.02.9

- Retains the downloaded `VanillaBundleAggregation.kb` **before** attempting to parse it, so failed compatibility-map loads are inspectable instead of disappearing.
- Invalid previous cache files are preserved as `VanillaBundleAggregation.kb.invalid` while a fresh copy is acquired.
- Scan diagnostics now include the first 32 bytes, the bytes immediately after the four-byte size prefix, and the apparent Zstd payload magic when aggregation decompression fails.
- No change to retail game files; all diagnostics remain read-only.
