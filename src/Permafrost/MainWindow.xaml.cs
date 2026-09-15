using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Permafrost.Controls;
using Permafrost.Core.Assets;
using Permafrost.Core.Commands;
using Permafrost.Core.Ebx;
using Permafrost.Core.Frostbite;
using Permafrost.Core.Maps;
using Permafrost.Core.Scene;
using Permafrost.Core.Rendering;
using Permafrost.Core.Workspace;
using Microsoft.Win32;

namespace Permafrost;

public enum TransformTool
{
    Select,
    Move,
    Rotate,
    Scale
}

public partial class MainWindow : Window
{
    private readonly SceneViewport _sceneViewport;
    private readonly UndoStack _undo = new();
    private GameDataSource? _dataSource;
    private LevelSession? _session;
    private EbxDocument? _document;
    private SceneNode? _sceneRoot;
    private SceneNode? _selectedNode;
    private List<GameAssetEntry> _allLevelAssets = new();
    private List<MapShortcutItem> _mapShortcutItems = new();
    private List<GameAssetEntry> _spaceLevelRoots = new();
    private string _lastDiagnostics = "No scan has been run.";
    private NativeMeshResolutionSummary? _nativeMeshSummary;
    private TransformTool _activeTransformTool = TransformTool.Select;
    private List<SceneNode> _outlinerSearchMatches = new();
    private List<GameAssetEntry> _assetBrowserResults = new();
    private bool _assetPreviewActive;
    private readonly List<SceneNode> _stagedPlacements = new();
    private readonly Dictionary<SceneNode, ImportedPlacement> _importedPlacements = new(ReferenceEqualityComparer.Instance);
    private List<LevelLayerTarget> _layerTargets = new();
    private TransformSnapshot? _transformClipboard;

    public MainWindow()
    {
        InitializeComponent();
        _sceneViewport = new SceneViewport(Viewport);
        _sceneViewport.NodeClicked += SceneViewport_NodeClicked;
        _sceneViewport.GizmoDragPreview += SceneViewport_GizmoDragPreview;
        _sceneViewport.GizmoDragCompleted += SceneViewport_GizmoDragCompleted;
        _sceneViewport.GizmoDragFaulted += SceneViewport_GizmoDragFaulted;
        _undo.Changed += (_, _) => UpdateUndoUi();
        InstallPathBox.Text = DetectInstallPath() ?? string.Empty;
        InitializeMapShortcutUi();
        InitializeAssetBrowserUi();
        UpdateTransformUiEnabled(false);
        SetTransformTool(TransformTool.Select);
    }

    private void BrowseInstall_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select STAR WARS Battlefront II install folder"
        };
        if (Directory.Exists(InstallPathBox.Text))
            dialog.InitialDirectory = InstallPathBox.Text;
        if (dialog.ShowDialog(this) == true)
            InstallPathBox.Text = dialog.FolderName;
    }

    private async void ScanGame_Click(object sender, RoutedEventArgs e)
    {
        var install = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
        {
            MessageBox.Show(this, "Choose the STAR WARS Battlefront II install folder first. You may select the game root, Data, Patch, or a folder inside them; the editor will resolve the root automatically.",
                "Game install required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true);
            _session = null;
            _document = null;
            _sceneRoot = null;
            _selectedNode = null;
            _nativeMeshSummary = null;
            ResolveMeshesButton.IsEnabled = false;
            Outliner.ItemsSource = null;
            OutlinerSearchBox.Text = string.Empty;
            AssetSearchBox.Text = string.Empty;
            AssetList.ItemsSource = null;
            _assetBrowserResults.Clear();
            _assetPreviewActive = false;
            _stagedPlacements.Clear();
            _importedPlacements.Clear();
            _layerTargets.Clear();
            LayerTargetComboBox.ItemsSource = null;
            UpdateStagedPlacementSummary();
            _undo.Clear();
            DiagnosticsBox.Text = "Scanning install...";
            ViewportOverlay.Text = "Scanning Battlefront II install...";

            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            _dataSource = await GameDataSource.ScanAsync(install, progress);
            var resolvedInstall = _dataSource.Layout.InstallRoot;
            InstallPathBox.Text = resolvedInstall;

            var workspacePath = WorkspaceManager.GetDefaultPath(resolvedInstall);
            _dataSource.Workspace = new WorkspaceManager(workspacePath, resolvedInstall);
            WorkspaceText.Text = $"Workspace: {workspacePath}";
            WorkspaceText.ToolTip = workspacePath;

            if (_dataSource.Assets.EbxCount > 0)
                await _dataSource.BuildLevelGuidIndexAsync(progress);

            _allLevelAssets = _dataSource.Assets.LevelAssets.ToList();
            _spaceLevelRoots = MapShortcutCatalog.DiscoverSpaceLevelRoots(_dataSource.Assets.SpaceLevelAssets).ToList();
            RefreshLevelList();
            RefreshMapShortcuts();
            RefreshAssetBrowser();
            UpdateDiagnostics();
            UpdateIndexStats();
            LeftTabs.SelectedIndex = 0;

            var detectedMaps = _mapShortcutItems.Count(x => x.IsAvailable);
            var summary = $"Indexed {_dataSource.Assets.EbxCount:N0} EBX, {_dataSource.Assets.ResCount:N0} RES and {_dataSource.Assets.ChunkCount:N0} bundle-local chunk records; " +
                          $"{_allLevelAssets.Count:N0} level-path EBXs; {_dataSource.Assets.GuidCount:N0} level GUIDs; " +
                          $"{detectedMaps}/{_mapShortcutItems.Count} map shortcuts detected; {_spaceLevelRoots.Count:N0} dedicated space roots found.";
            StatusText.Text = summary;
            ViewportOverlay.Text = "Game indexed — choose a map shortcut or a raw level asset";

            if (_dataSource.Assets.EbxCount == 0)
            {
                MessageBox.Show(this,
                    "The install layout was read, but no EBX assets could be indexed from the bundle manifest. " +
                    "Open the Diagnostics tab and send me its contents; that will identify the exact retail manifest compatibility case to add.",
                    "Install scanned; bundle index needs compatibility work", MessageBoxButton.OK, MessageBoxImage.Warning);
                LeftTabs.SelectedIndex = 3;
            }
        }
        catch (Exception ex)
        {
            _lastDiagnostics = ex.ToString();
            DiagnosticsBox.Text = _lastDiagnostics;
            LeftTabs.SelectedIndex = 3;
            StatusText.Text = "Install scan failed.";
            ViewportOverlay.Text = "Install scan failed — see Diagnostics";
            MessageBox.Show(this, ex.Message, "Failed to scan Battlefront II install", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }


    private async void DeepIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_dataSource == null)
        {
            MessageBox.Show(this, "Scan the Battlefront II install first.", "No game index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true);
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            await _dataSource.BuildEbxGuidIndexAsync(levelsOnly: false, progress);
            UpdateIndexStats();
            if (_session != null && _sceneRoot != null)
                await ResolveNativeMeshesCoreAsync(progress);
            UpdateDiagnostics();
            StatusText.Text = $"Deep GUID index complete: {_dataSource.Assets.GuidCount:N0} EBX GUIDs resolved." +
                              (_nativeMeshSummary == null ? string.Empty : $" Native renderer: {_nativeMeshSummary}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Deep GUID index failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OpenSelectedLevel_Click(object sender, RoutedEventArgs e) => await OpenSelectedLevelAsync();

    private async void LevelList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedLevelAsync();

    private async Task OpenSelectedLevelAsync()
    {
        if (_dataSource == null)
        {
            MessageBox.Show(this, "Scan the Battlefront II install first.", "No game index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (LevelList.SelectedItem is not GameAssetEntry entry)
        {
            MessageBox.Show(this, "Select a level EBX asset first, or use the Map Shortcuts picker above.",
                "Select a level", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OpenLevelAsync(entry);
    }

    private async Task OpenLevelAsync(GameAssetEntry entry)
    {
        if (_dataSource == null)
        {
            MessageBox.Show(this, "Scan the Battlefront II install first.", "No game index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true);
            StatusText.Text = $"Opening {entry.Name}...";
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            _session = await LevelSessionBuilder.BuildAsync(_dataSource, entry, progress);
            _document = null;
            _sceneRoot = _session.Root;
            _selectedNode = null;
            _assetPreviewActive = false;
            _stagedPlacements.Clear();
            _importedPlacements.Clear();
            UpdateStagedPlacementSummary();
            ReturnToLevelButton.IsEnabled = false;
            _undo.Clear();

            Outliner.ItemsSource = new[] { _sceneRoot };
            var nodes = _session.Flatten().ToArray();
            var placements = nodes.Count(n => n.Transform != null);
            UpdateOutlinerSummary();

            // Fast render index: only touch EBX headers in the bundles already participating in
            // this level graph. This resolves many prefab/mesh imports without forcing a global
            // 2M-entry GUID pass. The toolbar action can be retried after Deep Index if required.
            ResolveMeshesButton.IsEnabled = true;
            var bundleHashes = _session.Documents.Keys
                .SelectMany(name => _dataSource.Assets.GetBundleHashesForAsset(name, GameAssetKind.Ebx))
                .Concat(_dataSource.Assets.GetBundleHashesForAsset(entry))
                .Where(x => x != 0)
                .Distinct()
                .ToArray();
            await _dataSource.BuildGuidIndexForBundlesAsync(bundleHashes, progress);
            await ResolveNativeMeshesCoreAsync(progress, render: false);
            RefreshLayerTargets();

            _sceneViewport.Render(_sceneRoot);
            ApplyViewportFilters();
            OutlinerSearchBox.Text = string.Empty;
            ViewportOverlay.Text = $"{entry.Name}  •  {_session.Documents.Count:N0} linked EBXs  •  {placements:N0} transforms";
            RendererStatusText.Text = _nativeMeshSummary == null
                ? "v0.04.1 native geometry pipeline — asset browser/preview + snapping + map shortcuts"
                : $"v0.04.1 native renderer • {_nativeMeshSummary}";
            LeftTabs.SelectedIndex = 1;
            UpdateInspector();
            StatusText.Text = $"Opened {entry.Name}. Native renderer: {_nativeMeshSummary}. Native triangle meshes use blue-grey; cyan boxes are MeshSet-bounds fallbacks; grey/blue proxies are non-renderable helpers.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to open level asset.";
            MessageBox.Show(this, ex.ToString(), "Failed to open level", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshLayerTargets()
    {
        var previous = (LayerTargetComboBox.SelectedItem as LevelLayerTarget)?.Asset.NormalizedName;
        _layerTargets = _session == null ? new List<LevelLayerTarget>() : LevelObjectImporter.GetTargets(_session).ToList();
        LayerTargetComboBox.ItemsSource = _layerTargets;
        if (_layerTargets.Count == 0)
        {
            LayerTargetComboBox.SelectedIndex = -1;
            return;
        }

        var index = previous == null ? -1 : _layerTargets.FindIndex(x =>
            x.Asset.NormalizedName.Equals(previous, StringComparison.OrdinalIgnoreCase));
        LayerTargetComboBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void LayerTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAssetSelectionUi();
    }

    private void InitializeMapShortcutUi()
    {
        MapEraComboBox.ItemsSource = new[] { "All Eras", "Prequel Era", "Original Era", "Sequel Era" };
        MapEraComboBox.SelectedIndex = 0;
        MapKindComboBox.ItemsSource = new[] { "All Map Types", "Ground Maps", "Space Battles", "Capital Ships" };
        MapKindComboBox.SelectedIndex = 0;
        RefreshMapShortcuts();
    }

    private void RefreshMapShortcuts()
    {
        var previousName = (MapShortcutComboBox.SelectedItem as MapShortcutItem)?.DisplayName;
        _mapShortcutItems = MapShortcutCatalog.All
            .Select(definition => new MapShortcutItem
            {
                Definition = definition,
                Asset = _dataSource == null ? null : MapShortcutCatalog.Resolve(definition, _allLevelAssets)
            })
            .ToList();

        RefreshMapShortcutPicker(previousName);
        RebuildMapMenus();
    }

    private void RefreshMapShortcutPicker(string? preferredName = null)
    {
        var selectedEra = MapEraComboBox.SelectedIndex switch
        {
            1 => MapEra.Prequel,
            2 => MapEra.Original,
            3 => MapEra.Sequel,
            _ => (MapEra?)null
        };

        var selectedKind = MapKindComboBox.SelectedIndex switch
        {
            1 => MapKind.Ground,
            2 => MapKind.Space,
            3 => MapKind.CapitalShip,
            _ => (MapKind?)null
        };

        var items = _mapShortcutItems
            .Where(x => selectedEra == null || x.Definition.Era == selectedEra)
            .Where(x => selectedKind == null || x.Definition.Kind == selectedKind)
            .OrderByDescending(x => x.IsAvailable)
            .ThenBy(x => x.Definition.Kind)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        MapShortcutComboBox.ItemsSource = items;

        MapShortcutItem? selection = null;
        if (!string.IsNullOrWhiteSpace(preferredName))
            selection = items.FirstOrDefault(x => x.DisplayName.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
        selection ??= items.FirstOrDefault(x => x.IsAvailable) ?? items.FirstOrDefault();
        MapShortcutComboBox.SelectedItem = selection;
        UpdateMapShortcutSelectionUi();
    }

    private void RebuildMapMenus()
    {
        PopulateEraMenu(PrequelMapsMenu, MapEra.Prequel, "_Prequel Era");
        PopulateEraMenu(OriginalMapsMenu, MapEra.Original, "_Original Era");
        PopulateEraMenu(SequelMapsMenu, MapEra.Sequel, "_Sequel Era");
    }

    private void PopulateEraMenu(MenuItem menu, MapEra era, string baseHeader)
    {
        menu.Items.Clear();
        var items = _mapShortcutItems.Where(x => x.Definition.Era == era).ToList();
        var available = items.Count(x => x.IsAvailable);
        menu.Header = $"{baseHeader} ({available}/{items.Count})";

        foreach (var kind in new[] { MapKind.Ground, MapKind.Space, MapKind.CapitalShip })
        {
            var kindItems = items.Where(x => x.Definition.Kind == kind).ToList();
            if (kindItems.Count == 0)
                continue;
            var kindAvailable = kindItems.Count(x => x.IsAvailable);
            var kindMenu = new MenuItem
            {
                Header = kind switch
                {
                    MapKind.Space => $"_Space Battles ({kindAvailable}/{kindItems.Count})",
                    MapKind.CapitalShip => $"_Capital Ships ({kindAvailable}/{kindItems.Count})",
                    _ => $"_Ground Maps ({kindAvailable}/{kindItems.Count})"
                }
            };

            foreach (var item in kindItems)
            {
                var menuItem = new MenuItem
                {
                    Header = item.DisplayName,
                    Tag = item,
                    IsEnabled = item.IsAvailable,
                    ToolTip = item.IsAvailable ? item.Path : "Map root not detected in the current retail index."
                };
                menuItem.Click += MapMenuItem_Click;
                kindMenu.Items.Add(menuItem);
            }
            menu.Items.Add(kindMenu);
        }
    }

    private void MapEraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshMapShortcutPicker();
    private void MapKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshMapShortcutPicker();

    private void MapShortcutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMapShortcutSelectionUi();

    private void UpdateMapShortcutSelectionUi()
    {
        if (MapShortcutComboBox.SelectedItem is not MapShortcutItem item)
        {
            OpenMapShortcutButton.IsEnabled = false;
            MapShortcutPathText.Text = _dataSource == null
                ? "Scan the game install to detect map roots."
                : "Choose a map.";
            return;
        }

        OpenMapShortcutButton.IsEnabled = item.IsAvailable;
        MapShortcutPathText.Text = item.IsAvailable
            ? item.Path
            : _dataSource == null
                ? "Scan the game install to detect this map."
                : "This shortcut was not detected in the current retail index; use the raw browser if needed.";
    }

    private async void OpenMapShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (MapShortcutComboBox.SelectedItem is not MapShortcutItem item)
            return;
        await OpenMapShortcutAsync(item);
    }

    private async void MapMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MapShortcutItem item })
            await OpenMapShortcutAsync(item);
    }

    private async Task OpenMapShortcutAsync(MapShortcutItem item)
    {
        if (_dataSource == null)
        {
            MessageBox.Show(this, "Scan the Battlefront II install first.", "No game index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (item.Asset == null)
        {
            MessageBox.Show(this,
                $"Permafrost could not automatically locate the retail level root for {item.DisplayName}. You can still use the raw level browser below.",
                "Map shortcut unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = $"Map shortcut: {item.DisplayName} → {item.Asset.Name}";
        await OpenLevelAsync(item.Asset);
    }

    private void OpenMapPicker_Click(object sender, RoutedEventArgs e) => FocusMapPicker();

    private void FocusMapPicker()
    {
        LeftTabs.SelectedIndex = 0;
        MapShortcutComboBox.Focus();
        MapShortcutComboBox.IsDropDownOpen = true;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _sceneViewport.CancelGizmoDrag();
            UpdateInspector();
            StatusText.Text = "Gizmo drag cancelled.";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusMapPicker();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LeftTabs.SelectedIndex = 1;
            OutlinerSearchBox.Focus();
            OutlinerSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LeftTabs.SelectedIndex = 2;
            AssetSearchBox.Focus();
            AssetSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !IsTextEntryFocused())
        {
            CopySelectedTransform();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !IsTextEntryFocused())
        {
            PasteSelectedTransform();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !IsTextEntryFocused())
        {
            ResetSelectedRotationScale();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && !IsTextEntryFocused())
        {
            DuplicateSelectedStagedPlacement();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && !IsTextEntryFocused())
        {
            DeleteSelectedStagedPlacement();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && !IsTextEntryFocused())
        {
            if (e.Key == Key.Q) { SetTransformTool(TransformTool.Select); e.Handled = true; return; }
            if (e.Key == Key.W) { SetTransformTool(TransformTool.Move); e.Handled = true; return; }
            if (e.Key == Key.E) { SetTransformTool(TransformTool.Rotate); e.Handled = true; return; }
            if (e.Key == Key.R) { SetTransformTool(TransformTool.Scale); e.Handled = true; return; }
        }

        if (e.Key == Key.Enter && MapShortcutComboBox.IsKeyboardFocusWithin && MapShortcutComboBox.SelectedItem is MapShortcutItem item)
        {
            e.Handled = true;
            await OpenMapShortcutAsync(item);
            return;
        }

        if (e.Key == Key.Enter && OutlinerSearchResults.IsKeyboardFocusWithin && OutlinerSearchResults.SelectedItem is SceneNode result)
        {
            SelectSceneNode(result, focus: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && AssetList.IsKeyboardFocusWithin && AssetList.SelectedItem is GameAssetEntry)
        {
            e.Handled = true;
            await PreviewSelectedAssetAsync();
        }
    }

    private static bool IsTextEntryFocused() => Keyboard.FocusedElement is TextBox or ComboBox;

    private void LevelSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLevelList();

    private void RefreshLevelList()
    {
        var text = LevelSearchBox?.Text?.Trim() ?? string.Empty;
        IEnumerable<GameAssetEntry> query = _allLevelAssets;
        if (!string.IsNullOrWhiteSpace(text))
            query = query.Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        LevelList.ItemsSource = query.Take(10000).ToList();
    }

    private void InitializeAssetBrowserUi()
    {
        AssetKindComboBox.ItemsSource = new[]
        {
            "Renderable EBX candidates",
            "All EBX assets",
            "All RES assets",
            "All indexed assets"
        };
        AssetKindComboBox.SelectedIndex = 0;
        RefreshAssetBrowser();
    }

    private void AssetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAssetBrowser();
    private void AssetKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAssetBrowser();

    private void RefreshAssetBrowser()
    {
        if (AssetList == null || AssetSearchSummary == null)
            return;

        if (_dataSource == null)
        {
            _assetBrowserResults.Clear();
            AssetList.ItemsSource = null;
            AssetSearchSummary.Text = "Scan the game install, then type at least 2 characters.";
            UpdateAssetSelectionUi();
            return;
        }

        var needle = AssetSearchBox?.Text?.Trim() ?? string.Empty;
        if (needle.Length < 2)
        {
            _assetBrowserResults.Clear();
            AssetList.ItemsSource = null;
            AssetSearchSummary.Text = "Type at least 2 characters to search the retail asset index.";
            UpdateAssetSelectionUi();
            return;
        }

        var mode = AssetKindComboBox?.SelectedIndex ?? 0;
        IEnumerable<GameAssetEntry> query = mode switch
        {
            1 => _dataSource.Assets.SearchUnique(needle, GameAssetKind.Ebx),
            2 => _dataSource.Assets.SearchUnique(needle, GameAssetKind.Res),
            3 => _dataSource.Assets.SearchUnique(needle),
            _ => _dataSource.Assets.SearchUnique(needle, GameAssetKind.Ebx).Where(IsRenderableAssetCandidate)
        };

        _assetBrowserResults = query.Take(600).ToList();
        AssetList.ItemsSource = _assetBrowserResults;
        AssetSearchSummary.Text = _assetBrowserResults.Count == 600
            ? "600+ matches (refine search)"
            : $"{_assetBrowserResults.Count:N0} match(es)";
        if (_assetBrowserResults.Count > 0)
            AssetList.SelectedIndex = 0;
        else
            UpdateAssetSelectionUi();
    }

    private static bool IsRenderableAssetCandidate(GameAssetEntry entry)
    {
        var path = entry.NormalizedName;
        return path.Contains("/meshes/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("_mesh", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("meshasset", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/prefabs/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("prefab", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("blueprint", StringComparison.OrdinalIgnoreCase);
    }

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAssetSelectionUi();

    private async void AssetList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await PreviewSelectedAssetAsync();

    private void UpdateAssetSelectionUi(string? extra = null)
    {
        if (AssetList?.SelectedItem is not GameAssetEntry entry)
        {
            InspectAssetButton.IsEnabled = false;
            PreviewAssetButton.IsEnabled = false;
            StageAssetButton.IsEnabled = false;
            ImportAssetButton.IsEnabled = false;
            ReturnToLevelButton.IsEnabled = _assetPreviewActive && _sceneRoot != null;
            AssetSelectionDetails.Text = "No asset selected";
            return;
        }

        InspectAssetButton.IsEnabled = entry.Kind == GameAssetKind.Ebx;
        PreviewAssetButton.IsEnabled = entry.Kind == GameAssetKind.Ebx;
        StageAssetButton.IsEnabled = entry.Kind == GameAssetKind.Ebx && _session != null && _sceneRoot != null;
        ImportAssetButton.IsEnabled = entry.Kind == GameAssetKind.Ebx && _session != null && _sceneRoot != null && LayerTargetComboBox.SelectedItem is LevelLayerTarget;
        ReturnToLevelButton.IsEnabled = _assetPreviewActive && _sceneRoot != null;

        var sb = new StringBuilder();
        sb.Append($"{entry.Kind} • bundle 0x{entry.BundleHash:X8} • {entry.OriginalSize:N0} bytes");
        if (entry.Kind == GameAssetKind.Res)
            sb.Append($" • RID 0x{entry.ResId:X16} • type 0x{entry.ResType:X8}");
        if (entry.FileGuid is { } guid)
            sb.Append($" • GUID {guid}");
        if (!string.IsNullOrWhiteSpace(extra))
            sb.AppendLine().Append(extra);
        AssetSelectionDetails.Text = sb.ToString();
    }

    private async void InspectAsset_Click(object sender, RoutedEventArgs e) => await InspectSelectedAssetAsync();

    private async Task InspectSelectedAssetAsync()
    {
        if (_dataSource == null || AssetList.SelectedItem is not GameAssetEntry { Kind: GameAssetKind.Ebx } entry)
            return;

        try
        {
            SetBusy(true);
            var document = await _dataSource.OpenEbxAsync(entry);
            UpdateAssetSelectionUi($"Root: {document.RootObject.ClassName} • {document.Objects.Count:N0} object(s) • {document.Imports.Count:N0} import(s) • FileGuid {document.FileGuid}");
            StatusText.Text = $"Inspected {entry.Name}: {document.RootObject.ClassName}, {document.Objects.Count:N0} EBX objects.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Asset inspection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PreviewAsset_Click(object sender, RoutedEventArgs e) => await PreviewSelectedAssetAsync();

    private async Task PreviewSelectedAssetAsync()
    {
        if (_dataSource == null || AssetList.SelectedItem is not GameAssetEntry { Kind: GameAssetKind.Ebx } entry)
            return;

        try
        {
            SetBusy(true);
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            await _dataSource.BuildGuidIndexForBundlesAsync(_dataSource.Assets.GetBundleHashesForAsset(entry), progress);
            var document = await _dataSource.OpenEbxAsync(entry);
            var resolver = new NativeMeshResolver(_dataSource);
            var info = await resolver.ResolveAssetAsync(entry);
            if (info == null || (!info.HasGeometry && !info.HasBounds))
            {
                UpdateAssetSelectionUi($"Root: {document.RootObject.ClassName}. No renderable MeshSet resolved from this EBX chain. If this is a prefab, a deeper GUID index may be required.");
                StatusText.Text = $"No renderable MeshSet resolved for {entry.Name}.";
                return;
            }

            _sceneViewport.PreviewNativeMesh(info, entry.ShortName);
            _assetPreviewActive = true;
            ReturnToLevelButton.IsEnabled = _sceneRoot != null;
            ViewportOverlay.Text = $"ASSET PREVIEW • {entry.Name}";
            RendererStatusText.Text = info.Geometry is { } geometry
                ? $"v0.04.1 asset preview • LOD {geometry.LodIndex} • {geometry.VertexCount:N0} verts • {geometry.TriangleCount:N0} tris"
                : "v0.04.1 asset preview • MeshSet bounds fallback";
            UpdateAssetSelectionUi($"Root: {document.RootObject.ClassName} • MeshSet: {info.MeshSetResource?.Name ?? "—"} • {info.Status}");
            StatusText.Text = $"Previewing {entry.Name}. This is read-only; the open level has not been modified.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Asset preview failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StageAsset_Click(object sender, RoutedEventArgs e) => await StageSelectedAssetAsync();

    private async Task StageSelectedAssetAsync()
    {
        if (_dataSource == null || _session == null || _sceneRoot == null)
        {
            MessageBox.Show(this, "Open an install-backed level before staging an asset.",
                "Open a level first", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AssetList.SelectedItem is not GameAssetEntry { Kind: GameAssetKind.Ebx } entry)
            return;

        try
        {
            SetBusy(true);
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            await _dataSource.BuildGuidIndexForBundlesAsync(_dataSource.Assets.GetBundleHashesForAsset(entry), progress);
            var resolver = new NativeMeshResolver(_dataSource);
            var info = await resolver.ResolveAssetAsync(entry);
            if (info == null || (!info.HasGeometry && !info.HasBounds))
            {
                MessageBox.Show(this,
                    "Permafrost could not resolve renderable MeshSet geometry or bounds for this EBX chain. Try another mesh/prefab candidate or run Deep Index All EBX GUIDs.",
                    "Asset cannot be staged yet", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_assetPreviewActive)
            {
                _sceneViewport.RestoreScene(frameAll: false);
                _assetPreviewActive = false;
                ReturnToLevelButton.IsEnabled = false;
            }

            var point = _sceneViewport.GetPlacementPoint();
            var transform = new SceneTransform
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                ScaleX = 1,
                ScaleY = 1,
                ScaleZ = 1,
                IsEditorOnly = true
            };
            var node = new SceneNode
            {
                Name = $"{entry.ShortName} {_stagedPlacements.Count + 1}",
                TypeName = "StagedAssetPlacement",
                OwnerAsset = entry,
                Transform = transform,
                NativeMesh = info,
                IsEditorOnly = true
            };

            _undo.Do(new DelegateEditorCommand(
                $"Stage {entry.ShortName}",
                () => AddStagedPlacement(node),
                () => RemoveStagedPlacement(node)));
            SelectSceneNode(node, focus: true);
            SetTransformTool(TransformTool.Move);
            LeftTabs.SelectedIndex = 1;
            StatusText.Text = $"Staged {entry.ShortName} at the viewport focus point. Move/rotate/scale it with W/E/R. This placement is editor-only until the structural EBX insertion writer is enabled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Asset staging failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportAsset_Click(object sender, RoutedEventArgs e) => await ImportSelectedAssetAsync();

    private async Task ImportSelectedAssetAsync()
    {
        if (_dataSource == null || _session == null || _sceneRoot == null)
        {
            MessageBox.Show(this, "Open an install-backed level before importing an object.",
                "Open a level first", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AssetList.SelectedItem is not GameAssetEntry { Kind: GameAssetKind.Ebx } entry)
            return;
        if (LayerTargetComboBox.SelectedItem is not LevelLayerTarget target)
        {
            MessageBox.Show(this, "Choose an IMPORT TARGET LayerData first.",
                "Import target required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true);
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            await _dataSource.BuildGuidIndexForBundlesAsync(_dataSource.Assets.GetBundleHashesForAsset(entry), progress);

            var resolver = new NativeMeshResolver(_dataSource);
            var native = await resolver.ResolveAssetAsync(entry);
            var point = _sceneViewport.GetPlacementPoint();
            var placementPoint = new System.Numerics.Vector3((float)point.X, (float)point.Y, (float)point.Z);
            var placement = await LevelObjectImporter.PrepareAsync(_session, target, entry, native, placementPoint);
            _importedPlacements[placement.Node] = placement;

            if (_assetPreviewActive)
            {
                _sceneViewport.RestoreScene(frameAll: false);
                _assetPreviewActive = false;
                ReturnToLevelButton.IsEnabled = false;
            }

            _undo.Do(new DelegateEditorCommand(
                $"Import {entry.ShortName}",
                () => AttachImportedPlacement(placement),
                () => DetachImportedPlacement(placement)));

            SelectSceneNode(placement.Node, focus: true);
            SetTransformTool(TransformTool.Move);
            LeftTabs.SelectedIndex = 1;
            StatusText.Text = native?.HasGeometry == true
                ? $"Imported {entry.ShortName} into {target.Asset.ShortName}. This is a real structural EBX placement and native geometry is linked. Save Workspace Changes to write the overlay."
                : $"Imported {entry.ShortName} into {target.Asset.ShortName}. This is a real structural EBX placement. Native geometry is not decoded yet, but the Blueprint reference and transform will persist in the workspace overlay.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Object import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AttachImportedPlacement(ImportedPlacement placement)
    {
        placement.Attach();
        _sceneViewport.RefreshScene(frameAll: false);
        RefreshOutlinerLabels();
        UpdateOutlinerSummary();
        UpdateStagedPlacementSummary();
    }

    private void DetachImportedPlacement(ImportedPlacement placement)
    {
        placement.Detach();
        if (ReferenceEquals(_selectedNode, placement.Node))
        {
            _selectedNode = null;
            _sceneViewport.SetSelected(null);
        }
        _sceneViewport.RefreshScene(frameAll: false);
        RefreshOutlinerLabels();
        UpdateOutlinerSummary();
        UpdateStagedPlacementSummary();
        UpdateInspector();
    }

    private void DuplicateStaged_Click(object sender, RoutedEventArgs e) => DuplicateSelectedStagedPlacement();
    private void DeleteStaged_Click(object sender, RoutedEventArgs e) => DeleteSelectedStagedPlacement();

    private void DuplicateSelectedStagedPlacement()
    {
        if (_sceneRoot == null || _selectedNode is not { IsEditorOnly: true, Transform: not null } source)
        {
            StatusText.Text = "Select a staged placement first. Ctrl+D only duplicates editor-staged objects in this build.";
            return;
        }

        var transform = source.Transform.Clone();
        transform.IsEditorOnly = true;
        var offset = TransformStepBox != null && TryParse(TransformStepBox.Text, out var parsed) && parsed > 0 ? parsed : 1.0;
        transform.X += offset;
        var copy = new SceneNode
        {
            Name = $"{source.OwnerAsset?.ShortName ?? source.Name} {_stagedPlacements.Count + 1}",
            TypeName = "StagedAssetPlacement",
            OwnerAsset = source.OwnerAsset,
            Transform = transform,
            NativeMesh = source.NativeMesh,
            IsEditorOnly = true
        };
        _undo.Do(new DelegateEditorCommand(
            $"Duplicate {source.Name}",
            () => AddStagedPlacement(copy),
            () => RemoveStagedPlacement(copy)));
        SelectSceneNode(copy, focus: false);
        StatusText.Text = $"Duplicated staged placement. The copy was offset +{offset:0.###} on world X. This action is undoable.";
    }

    private void DeleteSelectedStagedPlacement()
    {
        if (_sceneRoot == null || _selectedNode == null)
            return;

        if (_selectedNode.IsImportedPlacement && _importedPlacements.TryGetValue(_selectedNode, out var imported))
        {
            var name = _selectedNode.Name;
            _undo.Do(new DelegateEditorCommand(
                $"Delete imported {name}",
                () => DetachImportedPlacement(imported),
                () => AttachImportedPlacement(imported)));
            StatusText.Text = $"Removed imported placement {name} from its LayerData Objects array. Save Workspace Changes to persist the structural deletion.";
            return;
        }

        if (_selectedNode is not { IsEditorOnly: true } node)
        {
            StatusText.Text = "Delete currently supports staged objects and objects imported during this Permafrost session; untouched retail objects remain protected.";
            return;
        }

        var originalIndex = _sceneRoot.Children.IndexOf(node);
        _undo.Do(new DelegateEditorCommand(
            $"Delete {node.Name}",
            () => RemoveStagedPlacement(node),
            () => AddStagedPlacement(node, originalIndex)));
        StatusText.Text = "Removed staged placement. Retail EBX data was not changed. Use Undo to restore it.";
    }

    private void AddStagedPlacement(SceneNode node, int index = -1)
    {
        if (_sceneRoot == null) return;
        if (!_sceneRoot.Children.Contains(node))
        {
            if (index >= 0 && index <= _sceneRoot.Children.Count)
                _sceneRoot.Children.Insert(index, node);
            else
                _sceneRoot.Children.Add(node);
        }
        if (!_stagedPlacements.Contains(node))
            _stagedPlacements.Add(node);
        _sceneViewport.RefreshScene(frameAll: false);
        RefreshOutlinerLabels();
        UpdateOutlinerSummary();
        UpdateStagedPlacementSummary();
    }

    private void RemoveStagedPlacement(SceneNode node)
    {
        if (_sceneRoot == null) return;
        _sceneRoot.Children.Remove(node);
        _stagedPlacements.Remove(node);
        if (ReferenceEquals(_selectedNode, node))
        {
            _selectedNode = null;
            _sceneViewport.SetSelected(null);
        }
        _sceneViewport.RefreshScene(frameAll: false);
        RefreshOutlinerLabels();
        UpdateOutlinerSummary();
        UpdateStagedPlacementSummary();
        UpdateInspector();
    }

    private void UpdateStagedPlacementSummary()
    {
        if (StagedPlacementSummary == null) return;
        var imported = _importedPlacements.Values.Count(x => x.IsAttached);
        if (_stagedPlacements.Count == 0 && imported == 0)
        {
            StagedPlacementSummary.Text = "No staged/imported placements";
            return;
        }
        var parts = new List<string>();
        if (_stagedPlacements.Count > 0) parts.Add($"{_stagedPlacements.Count:N0} staged (editor-only)");
        if (imported > 0) parts.Add($"{imported:N0} imported (structural EBX)");
        StagedPlacementSummary.Text = string.Join(" • ", parts);
    }

    private void UpdateOutlinerSummary()
    {
        if (_sceneRoot == null)
        {
            OutlinerSummary.Text = "No level loaded";
            return;
        }
        var nodes = SceneBuilder.Flatten(_sceneRoot).ToArray();
        var placements = nodes.Count(n => n.Transform != null);
        var docs = _session?.Documents.Count ?? (_document != null ? 1 : 0);
        var staged = _stagedPlacements.Count == 0 ? string.Empty : $" • {_stagedPlacements.Count:N0} staged";
        var importedCount = _importedPlacements.Values.Count(x => x.IsAttached);
        var imported = importedCount == 0 ? string.Empty : $" • {importedCount:N0} imported";
        OutlinerSummary.Text = $"{nodes.Length:N0} nodes • {placements:N0} editable transforms • {docs:N0} EBXs{staged}{imported}";
    }

    private void ReturnToLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_sceneRoot == null)
            return;
        _sceneViewport.RestoreScene(frameAll: true);
        ApplyViewportFilters();
        _assetPreviewActive = false;
        ReturnToLevelButton.IsEnabled = false;
        var name = _session?.RootAsset.Name ?? _document?.AssetName ?? "Loaded level";
        ViewportOverlay.Text = name;
        RendererStatusText.Text = _nativeMeshSummary == null
            ? "v0.04.1 native geometry pipeline"
            : $"v0.04.1 native renderer • {_nativeMeshSummary}";
        StatusText.Text = "Returned to the loaded level.";
    }

    private void OpenEbx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open exported Battlefront II EBX",
            Filter = "EBX binary (*.bin)|*.bin|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = "Reading exported EBX...";
            _session = null;
            _nativeMeshSummary = null;
            _assetPreviewActive = false;
            _stagedPlacements.Clear();
            _importedPlacements.Clear();
            UpdateStagedPlacementSummary();
            ReturnToLevelButton.IsEnabled = false;
            ResolveMeshesButton.IsEnabled = false;
            _document = EbxV4Reader.Read(dialog.FileName);
            _sceneRoot = SceneBuilder.Build(_document);
            _selectedNode = null;
            _undo.Clear();
            Outliner.ItemsSource = new[] { _sceneRoot };
            var nodes = SceneBuilder.Flatten(_sceneRoot).ToArray();
            OutlinerSummary.Text = $"{nodes.Length:N0} nodes • {nodes.Count(n => n.Transform != null):N0} transforms • exported EBX mode";
            _sceneViewport.Render(_sceneRoot);
            ApplyViewportFilters();
            OutlinerSearchBox.Text = string.Empty;
            LeftTabs.SelectedIndex = 1;
            ViewportOverlay.Text = $"{Path.GetFileName(dialog.FileName)}  •  {_document.RootObject.ClassName}  •  {_document.Objects.Count} objects";
            StatusText.Text = $"Loaded {_document.RootObject.ClassName}: {_document.Objects.Count} EBX objects, {_document.Imports.Count} external references.";
            UpdateInspector();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Failed to read EBX", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Failed to load EBX.";
        }
    }

    private void SceneViewport_NodeClicked(object? sender, SceneNode? node) => SelectSceneNode(node, focus: false);

    private void Outliner_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        SelectSceneNode(e.NewValue as SceneNode, focus: false);

    private void SelectSceneNode(SceneNode? node, bool focus)
    {
        _selectedNode = node;
        _sceneViewport.SetSelected(node);
        if (node?.Document != null && _layerTargets.Count > 0)
        {
            var targetIndex = _layerTargets.FindIndex(x => ReferenceEquals(x.Document, node.Document));
            if (targetIndex >= 0 && LayerTargetComboBox.SelectedIndex != targetIndex)
                LayerTargetComboBox.SelectedIndex = targetIndex;
        }
        if (focus)
            _sceneViewport.Focus(node);
        UpdateInspector();
    }

    private void OutlinerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sceneRoot == null)
        {
            OutlinerSearchResults.Visibility = Visibility.Collapsed;
            OutlinerSearchSummary.Text = "Load a level to search the scene";
            return;
        }

        var needle = OutlinerSearchBox.Text.Trim();
        if (needle.Length == 0)
        {
            _outlinerSearchMatches.Clear();
            OutlinerSearchResults.ItemsSource = null;
            OutlinerSearchResults.Visibility = Visibility.Collapsed;
            OutlinerSearchSummary.Text = "Type to search the loaded scene";
            return;
        }

        _outlinerSearchMatches = SceneBuilder.Flatten(_sceneRoot)
            .Where(n => n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        n.TypeName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        (n.OwnerAsset?.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (n.Document?.AssetName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(500)
            .ToList();
        OutlinerSearchResults.ItemsSource = _outlinerSearchMatches;
        OutlinerSearchResults.Visibility = Visibility.Visible;
        OutlinerSearchSummary.Text = _outlinerSearchMatches.Count == 500
            ? "500+ matches (refine search)"
            : $"{_outlinerSearchMatches.Count:N0} match(es)";
        if (_outlinerSearchMatches.Count > 0)
            OutlinerSearchResults.SelectedIndex = 0;
    }

    private void OutlinerSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlinerSearchResults.SelectedItem is SceneNode node)
            SelectSceneNode(node, focus: false);
    }

    private void OutlinerSearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OutlinerSearchResults.SelectedItem is SceneNode node)
            SelectSceneNode(node, focus: true);
    }

    private void UpdateInspector()
    {
        var node = _selectedNode;
        SelectedName.Text = node?.Name ?? "Nothing selected";
        SelectedType.Text = node?.TypeName ?? string.Empty;
        SelectedAsset.Text = node?.IsEditorOnly == true
            ? $"STAGED • {node.OwnerAsset?.Name ?? "editor asset"}"
            : node?.IsImportedPlacement == true
                ? $"IMPORTED • {node.ReferencedAsset?.Name ?? "blueprint"} → {node.OwnerAsset?.Name ?? node.Document?.AssetName ?? "layer"}"
                : node?.OwnerAsset?.Name ?? node?.Document?.AssetName ?? string.Empty;
        PropertyGrid.ItemsSource = node == null ? null : SceneBuilder.GetPropertyRows(node);

        var native = node?.NativeMesh;
        NativeMeshSetText.Text = native?.MeshSetResource?.Name ?? "—";
        NativeResIdText.Text = native == null || native.MeshSetResId == 0 ? "—" : $"0x{native.MeshSetResId:X16}";
        NativeGeometryText.Text = native?.Geometry is { } geometry
            ? $"LOD {geometry.LodIndex} • {geometry.VertexCount:N0} verts • {geometry.TriangleCount:N0} tris • {geometry.DataSource}"
            : native?.ParsedLodCount > 0 ? $"{native.ParsedLodCount} LOD(s) parsed • bounds fallback" : "—";
        NativeChunkText.Text = native?.Geometry is { UsesInlineData: true }
            ? "inline RES"
            : native?.LodChunkId?.ToString() ?? "—";
        NativeStatusText.Text = native?.Status ?? "Not resolved";

        var t = node?.Transform;
        UpdateTransformUiEnabled(t?.IsFullyEditable == true && (node?.Document != null || node?.IsEditorOnly == true));
        if (t == null)
        {
            XBox.Text = YBox.Text = ZBox.Text = string.Empty;
            RotXBox.Text = RotYBox.Text = RotZBox.Text = string.Empty;
            ScaleXBox.Text = ScaleYBox.Text = ScaleZBox.Text = string.Empty;
            return;
        }

        XBox.Text = t.X.ToString("0.######", CultureInfo.InvariantCulture);
        YBox.Text = t.Y.ToString("0.######", CultureInfo.InvariantCulture);
        ZBox.Text = t.Z.ToString("0.######", CultureInfo.InvariantCulture);
        RotXBox.Text = t.RotationX.ToString("0.###", CultureInfo.InvariantCulture);
        RotYBox.Text = t.RotationY.ToString("0.###", CultureInfo.InvariantCulture);
        RotZBox.Text = t.RotationZ.ToString("0.###", CultureInfo.InvariantCulture);
        ScaleXBox.Text = t.ScaleX.ToString("0.######", CultureInfo.InvariantCulture);
        ScaleYBox.Text = t.ScaleY.ToString("0.######", CultureInfo.InvariantCulture);
        ScaleZBox.Text = t.ScaleZ.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private void ApplyTransform_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.Transform is not { IsFullyEditable: true } || (_selectedNode.Document == null && !_selectedNode.IsEditorOnly))
            return;

        if (!TryReadTransformBoxes(out var target))
        {
            MessageBox.Show(this, "Position, rotation and scale values must all be valid numbers. Scale values cannot be zero.",
                "Invalid transform", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyTransformCommand(target, "Transform");
    }

    private bool TryReadTransformBoxes(out TransformSnapshot value)
    {
        value = default;
        if (!TryParse(XBox.Text, out var x) || !TryParse(YBox.Text, out var y) || !TryParse(ZBox.Text, out var z) ||
            !TryParse(RotXBox.Text, out var rx) || !TryParse(RotYBox.Text, out var ry) || !TryParse(RotZBox.Text, out var rz) ||
            !TryParse(ScaleXBox.Text, out var sx) || !TryParse(ScaleYBox.Text, out var sy) || !TryParse(ScaleZBox.Text, out var sz) ||
            Math.Abs(sx) < 0.000001 || Math.Abs(sy) < 0.000001 || Math.Abs(sz) < 0.000001)
            return false;
        value = new TransformSnapshot(x, y, z, rx, ry, rz, sx, sy, sz);
        return true;
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.Transform is not { IsFullyEditable: true } t || sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 3 || !TryParse(TransformStepBox.Text, out var step) || step <= 0) return;
        var ax = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var ay = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var az = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var current = TransformSnapshot.From(t);

        var target = _activeTransformTool switch
        {
            TransformTool.Rotate => current with
            {
                RotationX = current.RotationX + ax * step,
                RotationY = current.RotationY + ay * step,
                RotationZ = current.RotationZ + az * step
            },
            TransformTool.Scale => current with
            {
                ScaleX = ClampScale(current.ScaleX + ax * step),
                ScaleY = ClampScale(current.ScaleY + ay * step),
                ScaleZ = ClampScale(current.ScaleZ + az * step)
            },
            _ => current with
            {
                X = current.X + ax * step,
                Y = current.Y + ay * step,
                Z = current.Z + az * step
            }
        };

        var name = _activeTransformTool switch
        {
            TransformTool.Rotate => "Rotate",
            TransformTool.Scale => "Scale",
            _ => "Move"
        };
        ApplyTransformCommand(target, name);
    }

    private static double ClampScale(double value) => Math.Abs(value) < 0.001 ? (value < 0 ? -0.001 : 0.001) : value;

    private void ApplyTranslationCommand(double x, double y, double z)
    {
        if (_selectedNode?.Transform is not { } t) return;
        ApplyTransformCommand(new TransformSnapshot(x, y, z, t.RotationX, t.RotationY, t.RotationZ, t.ScaleX, t.ScaleY, t.ScaleZ), "Move");
    }

    private void ApplyTransformCommand(TransformSnapshot target, string operationName)
    {
        if (_selectedNode == null) return;
        try
        {
            _undo.Do(new TransformNodeCommand(_selectedNode, target, operationName, n => _sceneViewport.RefreshNode(n)));
            UpdateInspector();
            RefreshOutlinerLabels();
            var asset = _selectedNode.OwnerAsset?.Name ?? _selectedNode.Document?.AssetName ?? "EBX";
            StatusText.Text = _selectedNode.IsEditorOnly
                ? $"{operationName} applied to staged placement {_selectedNode.Name}. This staged object is editor-only and is not written into the level EBX yet."
                : $"{operationName} applied to {_selectedNode.Name}. {asset} is dirty in memory; Save Workspace Changes writes a safe overlay, never the game install.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot patch transform", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyTransform_Click(object sender, RoutedEventArgs e) => CopySelectedTransform();
    private void PasteTransform_Click(object sender, RoutedEventArgs e) => PasteSelectedTransform();
    private void ResetRotationScale_Click(object sender, RoutedEventArgs e) => ResetSelectedRotationScale();

    private void CopySelectedTransform()
    {
        if (_selectedNode?.Transform == null)
        {
            StatusText.Text = "Select an object with a transform first.";
            return;
        }
        _transformClipboard = TransformSnapshot.From(_selectedNode.Transform);
        StatusText.Text = $"Copied transform from {_selectedNode.Name}. Paste with Ctrl+Shift+V.";
    }

    private void PasteSelectedTransform()
    {
        if (_selectedNode?.Transform is not { IsFullyEditable: true })
        {
            StatusText.Text = "Select an editable object before pasting a transform.";
            return;
        }
        if (_transformClipboard is not { } copied)
        {
            StatusText.Text = "No copied transform is available yet.";
            return;
        }
        ApplyTransformCommand(copied, "Paste Transform");
    }

    private void ResetSelectedRotationScale()
    {
        if (_selectedNode?.Transform is not { IsFullyEditable: true } t)
        {
            StatusText.Text = "Select an editable object first.";
            return;
        }
        ApplyTransformCommand(new TransformSnapshot(
            t.X, t.Y, t.Z, 0, 0, 0, 1, 1, 1), "Reset Rotation/Scale");
    }

    private async void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            MessageBox.Show(this, "No install-backed level session is open. For an exported .bin, use File → Save Exported EBX As.",
                "No workspace session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var count = await _session.SaveDirtyAsync();
            RefreshOutlinerLabels();
            var stagedNote = _stagedPlacements.Count > 0
                ? $" {_stagedPlacements.Count:N0} STAGED placement(s) remain editor-only."
                : string.Empty;
            var importedCount = _importedPlacements.Values.Count(x => x.IsAttached);
            var importedNote = importedCount > 0
                ? $" {importedCount:N0} IMPORTED placement(s) are structural EBX edits and are included in the saved overlay."
                : string.Empty;
            StatusText.Text = count == 0
                ? "No EBX workspace changes to save." + stagedNote + importedNote
                : $"Saved {count:N0} modified EBX overlay(s) to {_session.DataSource.Workspace!.Root}. Original Battlefront II files were not changed." + importedNote + stagedNote;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to save workspace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var document = _selectedNode?.Document ?? _document;
        if (document == null)
        {
            MessageBox.Show(this, "Open an EBX or select an EBX-backed scene node first.", "Nothing to save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var suggested = document.AssetName != null ? SceneBuilder.ShortName(document.AssetName) : Path.GetFileNameWithoutExtension(document.SourcePath ?? "Level");
        var dialog = new SaveFileDialog
        {
            Title = "Save modified EBX copy",
            Filter = "EBX binary (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = suggested + "_modified.bin"
        };
        if (dialog.ShowDialog(this) != true) return;

        var wasInstallBacked = _session != null && !string.IsNullOrWhiteSpace(document.AssetName);
        document.SaveAs(dialog.FileName);
        if (wasInstallBacked)
            document.IsDirty = true; // exporting a debug copy must not clear the pending workspace edit
        RefreshOutlinerLabels();
        StatusText.Text = $"Saved exported copy: {dialog.FileName}";
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Permafrost scan diagnostics",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "Permafrost_ScanDiagnostics.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, _lastDiagnostics);
        StatusText.Text = $"Exported diagnostics: {dialog.FileName}";
    }

    private void UpdateDiagnostics()
    {
        if (_dataSource == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("Permafrost v0.04.1 — scan diagnostics");
        sb.AppendLine($"Install: {_dataSource.Layout.InstallRoot}");
        sb.AppendLine($"Catalogs: {_dataSource.Layout.Catalogs.Count:N0}");
        foreach (var catalog in _dataSource.Layout.Catalogs) sb.AppendLine($"  {catalog}");
        sb.AppendLine($"Manifest bundles: {_dataSource.Layout.Manifest.Bundles.Count:N0}");
        sb.AppendLine($"Bundle aggregation: {_dataSource.Layout.Manifest.BundleAggregationSource}");
        sb.AppendLine($"Bundle aggregation cache: {BundleAggregationMap.CachePath}");
        if (!string.IsNullOrWhiteSpace(_dataSource.Layout.Manifest.BundleAggregationError))
            sb.AppendLine($"Bundle aggregation error: {_dataSource.Layout.Manifest.BundleAggregationError}");
        sb.AppendLine($"Bundle headers attempted: {_dataSource.Diagnostics.BundlesAttempted:N0}");
        sb.AppendLine($"Bundle headers indexed: {_dataSource.Diagnostics.BundlesIndexed:N0}");
        sb.AppendLine($"Bundle headers failed: {_dataSource.Diagnostics.BundlesFailed:N0}");
        sb.AppendLine($"Indexed EBX: {_dataSource.Assets.EbxCount:N0} ({_dataSource.Assets.UniqueEbxCount:N0} unique names)");
        sb.AppendLine($"Cached EBX GUIDs loaded: {_dataSource.Diagnostics.CachedEbxGuidsLoaded:N0}");
        sb.AppendLine($"EBX GUID cache: {EbxGuidIndexCache.CachePath}");
        sb.AppendLine($"Indexed RES: {_dataSource.Assets.ResCount:N0}");
        sb.AppendLine($"Indexed RES IDs: {_dataSource.Assets.ResIdCount:N0}");
        sb.AppendLine($"Global manifest chunks: {_dataSource.Layout.Manifest.Chunks.Count:N0}");
        sb.AppendLine($"Bundle-local chunks indexed: {_dataSource.Assets.ChunkCount:N0} ({_dataSource.Assets.UniqueChunkCount:N0} unique GUIDs)");
        sb.AppendLine($"Level-path EBX: {_allLevelAssets.Count:N0}");
        sb.AppendLine($"Resolved EBX GUIDs: {_dataSource.Assets.GuidCount:N0}");
        sb.AppendLine($"Map shortcuts detected: {_mapShortcutItems.Count(x => x.IsAvailable):N0}/{_mapShortcutItems.Count:N0}");
        sb.AppendLine($"Dedicated space level roots detected: {_spaceLevelRoots.Count:N0}");
        foreach (var root in _spaceLevelRoots)
            sb.AppendLine($"  SPACE {root.Name}");
        foreach (var era in Enum.GetValues<MapEra>())
        {
            sb.AppendLine($"  {era}:");
            foreach (var map in _mapShortcutItems.Where(x => x.Definition.Era == era))
                sb.AppendLine($"    {(map.IsAvailable ? "OK" : "--")} [{map.KindName}] {map.DisplayName}: {map.Path}");
        }
        if (_nativeMeshSummary != null)
        {
            sb.AppendLine($"Native render resolution: {_nativeMeshSummary}");
            sb.AppendLine($"Native render chain: {_nativeMeshSummary.ChainDetails}");
        }
        sb.AppendLine();
        sb.AppendLine("Warnings:");
        if (_dataSource.Diagnostics.Warnings.Count == 0) sb.AppendLine("  (none)");
        foreach (var warning in _dataSource.Diagnostics.Warnings) sb.AppendLine("  " + warning);
        _lastDiagnostics = sb.ToString();
        DiagnosticsBox.Text = _lastDiagnostics;
    }

    private void UpdateIndexStats()
    {
        if (_dataSource == null)
        {
            IndexStatsText.Text = "No game index";
            return;
        }
        IndexStatsText.Text = $"EBX {_dataSource.Assets.EbxCount:N0} • RES {_dataSource.Assets.ResCount:N0} • Chunks {_dataSource.Assets.UniqueChunkCount:N0}+{_dataSource.Layout.Manifest.Chunks.Count:N0} • GUIDs {_dataSource.Assets.GuidCount:N0} • Maps {_mapShortcutItems.Count(x => x.IsAvailable)}/{_mapShortcutItems.Count} • Space {_spaceLevelRoots.Count}";
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
    private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();
    private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => DoUndo();
    private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => DoRedo();
    private void UndoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _undo.CanUndo;
    private void RedoCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _undo.CanRedo;

    private void DoUndo()
    {
        _undo.Undo();
        UpdateInspector();
        RefreshOutlinerLabels();
        CommandManager.InvalidateRequerySuggested();
    }

    private void DoRedo()
    {
        _undo.Redo();
        UpdateInspector();
        RefreshOutlinerLabels();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateUndoUi()
    {
        UndoMenuItem.IsEnabled = _undo.CanUndo;
        RedoMenuItem.IsEnabled = _undo.CanRedo;
        UndoMenuItem.Header = _undo.CanUndo ? $"Undo {_undo.UndoName}" : "Undo";
        RedoMenuItem.Header = _undo.CanRedo ? $"Redo {_undo.RedoName}" : "Redo";
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshOutlinerLabels()
    {
        // SceneNode deliberately stays a lightweight POCO. Rebinding is cheap for current level graphs
        // and makes the dirty marker (*) update immediately without adding editor/UI concerns to Core.
        if (_sceneRoot == null) return;
        Outliner.ItemsSource = null;
        Outliner.ItemsSource = new[] { _sceneRoot };
    }

    private async void ResolveMeshes_Click(object sender, RoutedEventArgs e)
    {
        if (_dataSource == null || _session == null || _sceneRoot == null)
            return;

        try
        {
            SetBusy(true);
            var progress = new Progress<ScanProgress>(p => StatusText.Text = p.Display);
            await ResolveNativeMeshesCoreAsync(progress);
            UpdateDiagnostics();
            UpdateInspector();
            StatusText.Text = $"Native render resolution complete: {_nativeMeshSummary}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Native mesh resolution failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ResolveNativeMeshesCoreAsync(IProgress<ScanProgress>? progress, bool render = true)
    {
        if (_dataSource == null || _sceneRoot == null) return;
        var resolver = new NativeMeshResolver(_dataSource);
        _nativeMeshSummary = await resolver.ResolveSceneAsync(_sceneRoot, progress);
        RendererStatusText.Text = $"v0.04.1 native renderer • {_nativeMeshSummary}";
        if (render)
            _sceneViewport.Render(_sceneRoot);
    }

    private void FocusSelected_Click(object sender, RoutedEventArgs e) => _sceneViewport.Focus(_selectedNode);

    private void FrameAll_Click(object sender, RoutedEventArgs e)
    {
        if (_sceneRoot != null)
            _sceneViewport.FrameAll(_sceneRoot);
    }

    private void SetBusy(bool busy)
    {
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        LevelList.IsEnabled = !busy;
        InstallPathBox.IsEnabled = !busy;
        MapEraComboBox.IsEnabled = !busy;
        MapKindComboBox.IsEnabled = !busy;
        MapShortcutComboBox.IsEnabled = !busy;
        OpenMapShortcutButton.IsEnabled = !busy && MapShortcutComboBox.SelectedItem is MapShortcutItem { IsAvailable: true };
        MapsMenu.IsEnabled = !busy;
        AssetSearchBox.IsEnabled = !busy;
        AssetKindComboBox.IsEnabled = !busy;
        LayerTargetComboBox.IsEnabled = !busy && _session != null;
        AssetList.IsEnabled = !busy;
        InspectAssetButton.IsEnabled = !busy && AssetList.SelectedItem is GameAssetEntry { Kind: GameAssetKind.Ebx };
        PreviewAssetButton.IsEnabled = !busy && AssetList.SelectedItem is GameAssetEntry { Kind: GameAssetKind.Ebx };
        StageAssetButton.IsEnabled = !busy && _session != null && _sceneRoot != null && AssetList.SelectedItem is GameAssetEntry { Kind: GameAssetKind.Ebx };
        ImportAssetButton.IsEnabled = !busy && _session != null && _sceneRoot != null && LayerTargetComboBox.SelectedItem is LevelLayerTarget && AssetList.SelectedItem is GameAssetEntry { Kind: GameAssetKind.Ebx };
        ReturnToLevelButton.IsEnabled = !busy && _assetPreviewActive && _sceneRoot != null;
        SnapGizmoCheck.IsEnabled = !busy;
        ResolveMeshesButton.IsEnabled = !busy && _session != null && _sceneRoot != null;
    }

    private void UpdateTransformUiEnabled(bool enabled)
    {
        XBox.IsEnabled = enabled; YBox.IsEnabled = enabled; ZBox.IsEnabled = enabled;
        RotXBox.IsEnabled = enabled; RotYBox.IsEnabled = enabled; RotZBox.IsEnabled = enabled;
        ScaleXBox.IsEnabled = enabled; ScaleYBox.IsEnabled = enabled; ScaleZBox.IsEnabled = enabled;
        ApplyTransformButton.IsEnabled = enabled;
        TransformStepBox.IsEnabled = enabled;
        MoveToolButton.IsEnabled = enabled;
        RotateToolButton.IsEnabled = enabled;
        ScaleToolButton.IsEnabled = enabled;
    }

    private void SnapGizmoChanged(object sender, RoutedEventArgs e) => UpdateGizmoSnap();
    private void TransformStepBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateGizmoSnap();

    private void UpdateGizmoSnap()
    {
        if (_sceneViewport == null) return;
        var fallback = _activeTransformTool switch
        {
            TransformTool.Rotate => 5.0,
            TransformTool.Scale => 0.1,
            _ => 1.0
        };
        var step = TransformStepBox != null && TryParse(TransformStepBox.Text, out var parsed) && parsed > 0 ? parsed : fallback;
        _sceneViewport.SetGizmoSnap(SnapGizmoCheck?.IsChecked == true, step);
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetTransformTool(TransformTool.Select);
    private void MoveTool_Click(object sender, RoutedEventArgs e) => SetTransformTool(TransformTool.Move);
    private void RotateTool_Click(object sender, RoutedEventArgs e) => SetTransformTool(TransformTool.Rotate);
    private void ScaleTool_Click(object sender, RoutedEventArgs e) => SetTransformTool(TransformTool.Scale);

    private void SetTransformTool(TransformTool tool)
    {
        _activeTransformTool = tool;
        _sceneViewport.SetGizmoMode(tool switch
        {
            TransformTool.Move => ViewportGizmoMode.Move,
            TransformTool.Rotate => ViewportGizmoMode.Rotate,
            TransformTool.Scale => ViewportGizmoMode.Scale,
            _ => ViewportGizmoMode.Select
        });
        SelectToolButton.FontWeight = tool == TransformTool.Select ? FontWeights.Bold : FontWeights.Normal;
        MoveToolButton.FontWeight = tool == TransformTool.Move ? FontWeights.Bold : FontWeights.Normal;
        RotateToolButton.FontWeight = tool == TransformTool.Rotate ? FontWeights.Bold : FontWeights.Normal;
        ScaleToolButton.FontWeight = tool == TransformTool.Scale ? FontWeights.Bold : FontWeights.Normal;
        TransformStepBox.Text = tool switch
        {
            TransformTool.Rotate => "5",
            TransformTool.Scale => "0.1",
            _ => "1"
        };
        UpdateGizmoSnap();
        var snapText = SnapGizmoCheck.IsChecked == true ? $" • Snap {TransformStepBox.Text}" : string.Empty;
        ViewportControlsText.Text = $"LMB select • RMB orbit • MMB pan • Wheel zoom • Active: {tool} (Q/W/E/R){snapText}";
        if (tool != TransformTool.Select)
            StatusText.Text = $"{tool} tool active. Drag the red/green/blue world-space gizmo handles for X/Y/Z{(SnapGizmoCheck.IsChecked == true ? " with snapping" : string.Empty)}, or use the Properties controls.";
    }

    private void SceneViewport_GizmoDragPreview(object? sender, GizmoDragEventArgs e)
    {
        if (!ReferenceEquals(e.Node, _selectedNode))
            return;
        var t = e.Target;
        XBox.Text = t.X.ToString("0.######", CultureInfo.InvariantCulture);
        YBox.Text = t.Y.ToString("0.######", CultureInfo.InvariantCulture);
        ZBox.Text = t.Z.ToString("0.######", CultureInfo.InvariantCulture);
        RotXBox.Text = t.RotationX.ToString("0.###", CultureInfo.InvariantCulture);
        RotYBox.Text = t.RotationY.ToString("0.###", CultureInfo.InvariantCulture);
        RotZBox.Text = t.RotationZ.ToString("0.###", CultureInfo.InvariantCulture);
        ScaleXBox.Text = t.ScaleX.ToString("0.######", CultureInfo.InvariantCulture);
        ScaleYBox.Text = t.ScaleY.ToString("0.######", CultureInfo.InvariantCulture);
        ScaleZBox.Text = t.ScaleZ.ToString("0.######", CultureInfo.InvariantCulture);
        StatusText.Text = $"{e.Mode} {e.Axis} — release LMB to commit, Esc to cancel.";
    }

    private void SceneViewport_GizmoDragCompleted(object? sender, GizmoDragEventArgs e)
    {
        if (!ReferenceEquals(e.Node, _selectedNode))
            SelectSceneNode(e.Node, focus: false);
        var t = e.Target;
        ApplyTransformCommand(new TransformSnapshot(
            t.X, t.Y, t.Z, t.RotationX, t.RotationY, t.RotationZ, t.ScaleX, t.ScaleY, t.ScaleZ),
            e.Mode.ToString());
    }

    private void SceneViewport_GizmoDragFaulted(object? sender, GizmoFaultEventArgs e)
    {
        UpdateInspector();
        StatusText.Text = e.Message;
        MessageBox.Show(this, e.Message, "Gizmo drag cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ViewportFilterChanged(object sender, RoutedEventArgs e) => ApplyViewportFilters();

    private void ApplyViewportFilters()
    {
        if (_sceneViewport == null) return;
        _sceneViewport.SetVisibilityOptions(
            ShowMeshesCheck.IsChecked == true,
            ShowHelpersCheck.IsChecked == true,
            ShowCollisionCheck.IsChecked == true,
            ShowLightsCheck.IsChecked == true);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static string? DetectInstallPath()
    {
        var candidates = new List<string>();
        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            candidates.Add(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games", "STAR WARS Battlefront II");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Origin Games", "STAR WARS Battlefront II");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "STAR WARS Battlefront II");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "STAR WARS Battlefront II");

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "Data", "layout.toc")));
    }
}
