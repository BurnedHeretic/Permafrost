# Permafrost v0.02.3

## Install-root resolver fix

This patch fixes `DirectoryNotFoundException: Data\layout.toc was not found` when the folder picker is pointed at `Data`, `Patch`, `Data\Win32`, `Patch\Win32`, or a game-library parent instead of the exact Battlefront II root.

The scanner now:

- accepts the actual game root;
- walks upward from a selected child folder to find the root;
- accepts `Data` directly;
- checks immediate child folders when a library directory is selected;
- reports the exact paths it checked if resolution still fails; and
- normalizes the install textbox/workspace to the resolved root after a successful scan.

The expected root is the folder containing `Data`, `Patch`, and normally `starwarsbattlefrontii.exe`.
