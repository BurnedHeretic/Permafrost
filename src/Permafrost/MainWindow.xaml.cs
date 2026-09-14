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
using Permafrost.Core.Scene;
using Permafrost.Core.Workspace;
using Microsoft.Win32;

namespace Permafrost;

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
    private string _lastDiagnostics = "No scan has been run.";

    public MainWindow()
    {
        InitializeComponent();
        _sceneViewport = new SceneViewport(Viewport);
        _sceneViewport.NodeClicked += SceneViewport_NodeClicked;
        _undo.Changed += (_, _) => UpdateUndoUi();
        InstallPathBox.Text = DetectInstallPath() ?? string.Empty;
        UpdateTransformUiEnabled(false);
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
            Outliner.ItemsSource = null;
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
            RefreshLevelList();
            UpdateDiagnostics();
            UpdateIndexStats();
            LeftTabs.SelectedIndex = 0;

            var summary = $"Indexed {_dataSource.Assets.EbxCount:N0} EBX and {_dataSource.Assets.ResCount:N0} RES assets; " +
                          $"{_allLevelAssets.Count:N0} level-path EBXs; {_dataSource.Assets.GuidCount:N0} level GUIDs.";
            StatusText.Text = summary;
            ViewportOverlay.Text = "Game indexed — choose a level asset from the Levels tab";

            if (_dataSource.Assets.EbxCount == 0)
            {
                MessageBox.Show(this,
                    "The install layout was read, but no EBX assets could be indexed from the bundle manifest. " +
                    "Open the Diagnostics tab and send me its contents; that will identify the exact retail manifest compatibility case to add.",
                    "Install scanned; bundle index needs compatibility work", MessageBoxButton.OK, MessageBoxImage.Warning);
                LeftTabs.SelectedIndex = 2;
            }
        }
        catch (Exception ex)
        {
            _lastDiagnostics = ex.ToString();
            DiagnosticsBox.Text = _lastDiagnostics;
            LeftTabs.SelectedIndex = 2;
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
            UpdateDiagnostics();
            StatusText.Text = $"Deep GUID index complete: {_dataSource.Assets.GuidCount:N0} EBX GUIDs resolved.";
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
            MessageBox.Show(this, "Select a level EBX asset first. For Kamino, search for levels/mp/kamino_01/kamino_01.",
                "Select a level", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _undo.Clear();

            Outliner.ItemsSource = new[] { _sceneRoot };
            var nodes = _session.Flatten().ToArray();
            var placements = nodes.Count(n => n.Transform != null);
            OutlinerSummary.Text = $"{nodes.Length:N0} nodes • {placements:N0} editable transforms • {_session.Documents.Count:N0} EBXs";
            _sceneViewport.Render(_sceneRoot);
            ViewportOverlay.Text = $"{entry.Name}  •  {_session.Documents.Count:N0} linked EBXs  •  {placements:N0} transforms";
            LeftTabs.SelectedIndex = 1;
            UpdateInspector();
            StatusText.Text = $"Opened {entry.Name}. Select a blue/red proxy or Outliner object to edit its transform.";
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

    private void LevelSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLevelList();

    private void RefreshLevelList()
    {
        var text = LevelSearchBox?.Text?.Trim() ?? string.Empty;
        IEnumerable<GameAssetEntry> query = _allLevelAssets;
        if (!string.IsNullOrWhiteSpace(text))
            query = query.Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        LevelList.ItemsSource = query.Take(10000).ToList();
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
            _document = EbxV4Reader.Read(dialog.FileName);
            _sceneRoot = SceneBuilder.Build(_document);
            _selectedNode = null;
            _undo.Clear();
            Outliner.ItemsSource = new[] { _sceneRoot };
            var nodes = SceneBuilder.Flatten(_sceneRoot).ToArray();
            OutlinerSummary.Text = $"{nodes.Length:N0} nodes • {nodes.Count(n => n.Transform != null):N0} transforms • exported EBX mode";
            _sceneViewport.Render(_sceneRoot);
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

    private void SceneViewport_NodeClicked(object? sender, SceneNode? node)
    {
        _selectedNode = node;
        UpdateInspector();
    }

    private void Outliner_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as SceneNode;
        _sceneViewport.SetSelected(_selectedNode);
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        var node = _selectedNode;
        SelectedName.Text = node?.Name ?? "Nothing selected";
        SelectedType.Text = node?.TypeName ?? string.Empty;
        SelectedAsset.Text = node?.OwnerAsset?.Name ?? node?.Document?.AssetName ?? string.Empty;
        PropertyGrid.ItemsSource = node == null ? null : SceneBuilder.GetPropertyRows(node);

        var t = node?.Transform;
        UpdateTransformUiEnabled(t?.TranslationStruct != null && node?.Document != null);
        if (t == null)
        {
            XBox.Text = YBox.Text = ZBox.Text = string.Empty;
            RotationText.Text = ScaleText.Text = "—";
            return;
        }

        XBox.Text = t.X.ToString("0.######", CultureInfo.InvariantCulture);
        YBox.Text = t.Y.ToString("0.######", CultureInfo.InvariantCulture);
        ZBox.Text = t.Z.ToString("0.######", CultureInfo.InvariantCulture);
        RotationText.Text = $"{t.RotationX:0.###}, {t.RotationY:0.###}, {t.RotationZ:0.###}";
        ScaleText.Text = $"{t.ScaleX:0.###}, {t.ScaleY:0.###}, {t.ScaleZ:0.###}";
    }

    private void ApplyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.Transform?.TranslationStruct == null || _selectedNode.Document == null)
            return;

        if (!TryParse(XBox.Text, out var x) || !TryParse(YBox.Text, out var y) || !TryParse(ZBox.Text, out var z))
        {
            MessageBox.Show(this, "Translation values must be numbers.", "Invalid transform", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyTranslationCommand(x, y, z);
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.Transform is not { } t || sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 3) return;
        var dx = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var dy = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var dz = double.Parse(parts[2], CultureInfo.InvariantCulture);
        ApplyTranslationCommand(t.X + dx, t.Y + dy, t.Z + dz);
    }

    private void ApplyTranslationCommand(double x, double y, double z)
    {
        if (_selectedNode == null) return;
        try
        {
            _undo.Do(new TranslateNodeCommand(_selectedNode, x, y, z, n => _sceneViewport.RefreshNode(n)));
            UpdateInspector();
            RefreshOutlinerLabels();
            var asset = _selectedNode.OwnerAsset?.Name ?? _selectedNode.Document?.AssetName ?? "EBX";
            StatusText.Text = $"Moved {_selectedNode.Name}. {asset} is dirty in memory; Save Workspace Changes writes a safe overlay, never the game install.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot patch transform", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            StatusText.Text = count == 0
                ? "No workspace changes to save."
                : $"Saved {count:N0} modified EBX overlay(s) to {_session.DataSource.Workspace!.Root}. Original Battlefront II files were not changed.";
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
            FileName = "BFLE_ScanDiagnostics.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, _lastDiagnostics);
        StatusText.Text = $"Exported diagnostics: {dialog.FileName}";
    }

    private void UpdateDiagnostics()
    {
        if (_dataSource == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("Permafrost v0.02.10 — scan diagnostics");
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
        sb.AppendLine($"Indexed EBX: {_dataSource.Assets.EbxCount:N0}");
        sb.AppendLine($"Indexed RES: {_dataSource.Assets.ResCount:N0}");
        sb.AppendLine($"Level-path EBX: {_allLevelAssets.Count:N0}");
        sb.AppendLine($"Resolved level GUIDs: {_dataSource.Assets.GuidCount:N0}");
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
        IndexStatsText.Text = $"EBX {_dataSource.Assets.EbxCount:N0} • RES {_dataSource.Assets.ResCount:N0} • Level GUIDs {_dataSource.Assets.GuidCount:N0}";
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
    }

    private void UpdateTransformUiEnabled(bool enabled)
    {
        XBox.IsEnabled = enabled;
        YBox.IsEnabled = enabled;
        ZBox.IsEnabled = enabled;
        ApplyTranslationButton.IsEnabled = enabled;
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
