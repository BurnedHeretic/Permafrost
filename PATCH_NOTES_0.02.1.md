# Permafrost v0.02.1

Build-fix release for the first v0.02 Visual Studio test.

## Fixed
- Added `System.Windows.Controls` import to `SceneViewport.cs`, which owns WPF `Viewport3D`.
- Added explicit project-wide `System.IO` and common .NET global usings so `BinaryReader`, `File`, `Path`, `MemoryStream`, `InvalidDataException`, etc. do not rely on SDK-generated implicit imports.
- Set C# language version to `latest`.
- Updated visible application branding to **Permafrost** while intentionally retaining the existing `Permafrost` namespaces/XAML class names for source compatibility.

## Important
Renaming the Visual Studio solution/project or containing folder is safe. Avoid bulk-renaming the C# namespaces or the `x:Class` values in XAML at this stage unless both sides are changed together.
