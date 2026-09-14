# v0.02 First Validation Checklist

Run these in order. If a step fails, export **File → Export Scan Diagnostics...** and keep the exact error dialog/screenshot.

## A. Install scan

1. Select the Battlefront II install folder (the one containing `Data` and `Patch`).
2. Click **Scan Install**.
3. Expected:
   - Catalog count > 0
   - Manifest bundle count > 0
   - Indexed EBX > 0
   - `levels/...` assets appear in the Levels tab

If EBX remains zero, stop there and send the diagnostics. That indicates a manifest/bundle-aggregation compatibility case, not an EBX editor failure.

## B. Kamino root

Search for:

`levels/mp/kamino_01/kamino_01`

Open the exact root level path. Expected:

- Outliner opens
- root is a LevelData EBX
- linked level/subworld/layer assets appear below reference objects
- no retail files are modified

## C. Landing-pad placement

Find the Landing Pad architecture layer and its `SpatialPrefabReferenceObjectData` placement.

Expected known reference transform:

- X about `83.681076`
- Y about `-20.178137`
- Z about `253.010437`
- Y rotation about `45.412`

The object should also have a blue proxy in the viewport.

## D. Move test

Change X from about `83.681076` to `103.681076`, or click `+X` repeatedly.

Expected:

- proxy moves immediately
- selected EBX becomes dirty (asterisk in rebound Outliner labels)
- Ctrl+Z returns it to the previous location
- Ctrl+Y reapplies it

## E. Safe save

Use **File → Save Workspace Changes**.

Expected:

- a modified EBX is created under `%LOCALAPPDATA%/Permafrost/Workspaces/.../Modified/Ebx/...`
- the retail `Data` and `Patch` files remain untouched
- rescanning/reopening the same asset uses the overlay through `GameDataSource`

## F. Optional deep GUID pass

Click **Deep Index All EBX GUIDs**.

This may take significantly longer. It prepares the v0.03 prefab/mesh resolver by mapping non-level EBX FileGuids (for example SpatialPrefabBlueprint assets) back to asset names.
