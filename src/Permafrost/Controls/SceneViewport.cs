using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Permafrost.Core.Scene;
using Permafrost.Core.Rendering;

namespace Permafrost.Controls;

public enum ViewportGizmoMode
{
    Select,
    Move,
    Rotate,
    Scale
}

public enum ViewportGizmoAxis
{
    X,
    Y,
    Z
}

public sealed class GizmoDragEventArgs : EventArgs
{
    public required SceneNode Node { get; init; }
    public required ViewportGizmoMode Mode { get; init; }
    public required ViewportGizmoAxis Axis { get; init; }
    public required SceneTransform Target { get; init; }
}

public sealed class GizmoFaultEventArgs : EventArgs
{
    public required string Message { get; init; }
}

/// <summary>
/// WPF 3D viewport for the standalone editor. v0.04 renders decoded BF2 MeshSet preview
/// triangles when available and provides interactive world-space move / rotate / scale gizmos.
/// Geometry and gizmo manipulation remain preview-only until MainWindow commits one undoable
/// Frostbite LinearTransform edit at the end of a drag.
/// </summary>
public sealed class SceneViewport
{
    private readonly Viewport3D _viewport;
    private readonly Model3DGroup _scene = new();
    private readonly PerspectiveCamera _camera = new();
    private readonly Dictionary<SceneNode, GeometryModel3D> _models = new();
    private readonly Dictionary<GeometryModel3D, SceneNode> _nodesByModel = new();
    private readonly Dictionary<SceneNode, Material> _normalMaterials = new();
    private readonly Dictionary<GeometryModel3D, ViewportGizmoAxis> _gizmoParts = new();
    private readonly Model3DGroup _selectionGizmo = new();

    private SceneNode? _root;
    private bool _showMeshes = true;
    private bool _showHelpers = true;
    private bool _showCollision = true;
    private bool _showLights = true;

    private Point3D _target = new(0, 0, 0);
    private double _distance = 120;
    private double _yaw = -35;
    private double _pitch = -20;
    private Point _lastMouse;
    private bool _orbiting;
    private bool _panning;
    private SceneNode? _selected;
    private ViewportGizmoMode _gizmoMode = ViewportGizmoMode.Select;
    private bool _snapEnabled;
    private double _snapStep = 1.0;

    private bool _gizmoDragging;
    private ViewportGizmoAxis _dragAxis;
    private Point _dragStartMouse;
    private SceneTransform? _dragStartTransform;
    private SceneTransform? _dragPreviewTransform;
    private double _dragStartScreenAngle;

    public event EventHandler<SceneNode?>? NodeClicked;
    public event EventHandler<GizmoDragEventArgs>? GizmoDragPreview;
    public event EventHandler<GizmoDragEventArgs>? GizmoDragCompleted;
    public event EventHandler<GizmoFaultEventArgs>? GizmoDragFaulted;

    public SceneViewport(Viewport3D viewport)
    {
        _viewport = viewport;
        _viewport.Camera = _camera;
        _scene.Children.Add(new AmbientLight(Color.FromRgb(85, 85, 85)));
        _scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -1)));
        _scene.Children.Add(_selectionGizmo);
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });

        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        _viewport.MouseRightButtonDown += Viewport_MouseRightButtonDown;
        _viewport.MouseRightButtonUp += Viewport_MouseRightButtonUp;
        _viewport.MouseDown += Viewport_MouseDown;
        _viewport.MouseUp += Viewport_MouseUp;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseWheel += Viewport_MouseWheel;

        UpdateCamera();
    }

    public void Render(SceneNode root)
    {
        _root = root;
        RenderInternal(root, frameAll: true);
    }

    public void SetGizmoMode(ViewportGizmoMode mode)
    {
        if (_gizmoDragging)
            CancelGizmoDrag();
        _gizmoMode = mode;
        UpdateSelectionGizmo(_selected);
    }

    public void SetGizmoSnap(bool enabled, double step)
    {
        _snapEnabled = enabled;
        _snapStep = double.IsFinite(step) && step > 0.000001 ? step : 1.0;
    }

    public void PreviewNativeMesh(NativeMeshInfo info, string displayName)
    {
        CancelGizmoDrag();
        var preview = new SceneNode
        {
            Name = displayName,
            TypeName = "AssetPreview",
            Transform = new SceneTransform(),
            NativeMesh = info
        };
        _selected = null;
        var previousShowMeshes = _showMeshes;
        _showMeshes = true;
        RenderInternal(preview, frameAll: false);
        _showMeshes = previousShowMeshes;
        FrameNativePreview(info);
    }

    public void RestoreScene(bool frameAll = false)
    {
        if (_root != null)
            RenderInternal(_root, frameAll);
    }

    public void RefreshScene(bool frameAll = false)
    {
        if (_root != null)
            RenderInternal(_root, frameAll);
    }

    public (double X, double Y, double Z) GetPlacementPoint() => (_target.X, _target.Y, _target.Z);

    public void SetVisibilityOptions(bool showMeshes, bool showHelpers, bool showCollision, bool showLights)
    {
        _showMeshes = showMeshes;
        _showHelpers = showHelpers;
        _showCollision = showCollision;
        _showLights = showLights;
        if (_root != null)
            RenderInternal(_root, frameAll: false);
    }

    private void RenderInternal(SceneNode root, bool frameAll)
    {
        CancelGizmoDrag();
        var previousSelection = _selected;
        foreach (var model in _models.Values)
            _scene.Children.Remove(model);
        _models.Clear();
        _nodesByModel.Clear();
        _normalMaterials.Clear();
        _selected = null;
        ClearGizmo();

        var transformed = SceneBuilder.Flatten(root).Where(n => n.Transform != null).ToList();
        var visible = transformed.Where(ShouldRender).ToList();
        foreach (var node in visible)
        {
            var material = CreateNodeMaterial(node);
            GeometryModel3D model;
            if (node.NativeMesh is { HasGeometry: true, Geometry: not null } nativeGeometry)
            {
                model = CreateNativeGeometry(nativeGeometry.Geometry, material);
            }
            else if (node.NativeMesh is { HasBounds: true } nativeBounds)
            {
                model = CreateBox(nativeBounds.BoundsMin.X, nativeBounds.BoundsMin.Y, nativeBounds.BoundsMin.Z,
                                  nativeBounds.BoundsMax.X, nativeBounds.BoundsMax.Y, nativeBounds.BoundsMax.Z, material);
            }
            else
            {
                model = CreateCube(1.8, material);
            }
            ApplyTransform(model, node.Transform!);
            _models[node] = model;
            _nodesByModel[model] = node;
            _normalMaterials[node] = material;
            _scene.Children.Add(model);
        }

        if (previousSelection != null)
            SetSelected(previousSelection);
        if (frameAll)
            FrameAll(visible);
    }

    private bool ShouldRender(SceneNode node)
    {
        if (node.TypeName.Contains("Collision", StringComparison.OrdinalIgnoreCase))
            return _showCollision;
        if (node.TypeName.Contains("Light", StringComparison.OrdinalIgnoreCase))
            return _showLights;
        if (node.NativeMesh != null)
            return _showMeshes;
        return _showHelpers;
    }

    public void RefreshNode(SceneNode node)
    {
        if (node.Transform == null || !_models.TryGetValue(node, out var model))
            return;
        ApplyTransform(model, node.Transform);
        if (ReferenceEquals(node, _selected))
            UpdateSelectionGizmo(node);
    }

    public void SetSelected(SceneNode? node)
    {
        if (_selected != null && _models.TryGetValue(_selected, out var oldModel) && _normalMaterials.TryGetValue(_selected, out var oldMaterial))
        {
            oldModel.Material = oldMaterial;
            oldModel.BackMaterial = oldMaterial;
        }

        CancelGizmoDrag();
        _selected = node;
        if (node != null && _models.TryGetValue(node, out var model))
        {
            var selectedMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(231, 138, 58)));
            model.Material = selectedMaterial;
            model.BackMaterial = selectedMaterial;
        }
        UpdateSelectionGizmo(node);
    }

    private void ClearGizmo()
    {
        _selectionGizmo.Transform = Transform3D.Identity;
        _gizmoParts.Clear();
        _selectionGizmo.Children.Clear();
    }

    private void UpdateSelectionGizmo(SceneNode? node, SceneTransform? preview = null)
    {
        ClearGizmo();
        if (_gizmoMode == ViewportGizmoMode.Select || node?.Transform == null || !_models.ContainsKey(node))
            return;

        var t = preview ?? node.Transform;
        var length = Math.Clamp(_distance * 0.055, 1.5, 25.0);
        var thickness = Math.Max(0.035, length * 0.035);
        var red = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(225, 78, 78)));
        var green = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(95, 205, 120)));
        var blue = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(80, 145, 235)));

        if (_gizmoMode == ViewportGizmoMode.Move)
        {
            AddAxisBar(t, ViewportGizmoAxis.X, length, thickness, red, arrowTip: true);
            AddAxisBar(t, ViewportGizmoAxis.Y, length, thickness, green, arrowTip: true);
            AddAxisBar(t, ViewportGizmoAxis.Z, length, thickness, blue, arrowTip: true);
        }
        else if (_gizmoMode == ViewportGizmoMode.Scale)
        {
            AddAxisBar(t, ViewportGizmoAxis.X, length, thickness, red, arrowTip: false);
            AddAxisBar(t, ViewportGizmoAxis.Y, length, thickness, green, arrowTip: false);
            AddAxisBar(t, ViewportGizmoAxis.Z, length, thickness, blue, arrowTip: false);
        }
        else if (_gizmoMode == ViewportGizmoMode.Rotate)
        {
            var radius = length * 0.78;
            var ringThickness = Math.Max(thickness * 1.25, radius * 0.035);
            AddRotationRing(t, ViewportGizmoAxis.X, radius, ringThickness, red);
            AddRotationRing(t, ViewportGizmoAxis.Y, radius, ringThickness, green);
            AddRotationRing(t, ViewportGizmoAxis.Z, radius, ringThickness, blue);
        }
    }

    private void AddAxisBar(SceneTransform t, ViewportGizmoAxis axis, double length, double thickness, Material material, bool arrowTip)
    {
        var half = thickness;
        var translate = new TranslateTransform3D(t.X, t.Y, t.Z);

        if (arrowTip)
        {
            // Move gizmo: UE-style narrow shaft plus a clearly pointed arrow head.
            var coneLength = Math.Max(thickness * 5.5, length * 0.22);
            var shaftEnd = Math.Max(thickness * 2.0, length - coneLength);
            GeometryModel3D bar = axis switch
            {
                ViewportGizmoAxis.X => CreateBox(0, -half, -half, shaftEnd, half, half, material),
                ViewportGizmoAxis.Y => CreateBox(-half, 0, -half, half, shaftEnd, half, material),
                _ => CreateBox(-half, -half, 0, half, half, shaftEnd, material)
            };
            var tip = CreateAxisCone(axis, shaftEnd, length, Math.Max(thickness * 3.0, length * 0.075), material);
            bar.Transform = translate;
            tip.Transform = translate;
            AddGizmoPart(bar, axis);
            AddGizmoPart(tip, axis);
            return;
        }

        // Scale gizmo: keep the square handle, visually distinct from Move.
        var handleHalf = thickness * 3.2;
        GeometryModel3D scaleBar;
        GeometryModel3D scaleHandle;
        switch (axis)
        {
            case ViewportGizmoAxis.X:
                scaleBar = CreateBox(0, -half, -half, length, half, half, material);
                scaleHandle = CreateBox(length - handleHalf, -handleHalf, -handleHalf, length + handleHalf, handleHalf, handleHalf, material);
                break;
            case ViewportGizmoAxis.Y:
                scaleBar = CreateBox(-half, 0, -half, half, length, half, material);
                scaleHandle = CreateBox(-handleHalf, length - handleHalf, -handleHalf, handleHalf, length + handleHalf, handleHalf, material);
                break;
            default:
                scaleBar = CreateBox(-half, -half, 0, half, half, length, material);
                scaleHandle = CreateBox(-handleHalf, -handleHalf, length - handleHalf, handleHalf, handleHalf, length + handleHalf, material);
                break;
        }
        scaleBar.Transform = translate;
        scaleHandle.Transform = translate;
        AddGizmoPart(scaleBar, axis);
        AddGizmoPart(scaleHandle, axis);
    }

    private static GeometryModel3D CreateAxisCone(
        ViewportGizmoAxis axis, double baseDistance, double tipDistance, double radius, Material material)
    {
        const int segments = 12;
        var mesh = new MeshGeometry3D();

        Point3D PointOnRing(double angle)
        {
            var c = Math.Cos(angle) * radius;
            var si = Math.Sin(angle) * radius;
            return axis switch
            {
                ViewportGizmoAxis.X => new Point3D(baseDistance, c, si),
                ViewportGizmoAxis.Y => new Point3D(c, baseDistance, si),
                _ => new Point3D(c, si, baseDistance)
            };
        }

        var tip = axis switch
        {
            ViewportGizmoAxis.X => new Point3D(tipDistance, 0, 0),
            ViewportGizmoAxis.Y => new Point3D(0, tipDistance, 0),
            _ => new Point3D(0, 0, tipDistance)
        };
        var baseCenter = axis switch
        {
            ViewportGizmoAxis.X => new Point3D(baseDistance, 0, 0),
            ViewportGizmoAxis.Y => new Point3D(0, baseDistance, 0),
            _ => new Point3D(0, 0, baseDistance)
        };

        var tipIndex = 0;
        var centerIndex = 1;
        mesh.Positions.Add(tip);
        mesh.Positions.Add(baseCenter);
        for (var i = 0; i < segments; i++)
            mesh.Positions.Add(PointOnRing(i * Math.PI * 2.0 / segments));

        for (var i = 0; i < segments; i++)
        {
            var a = 2 + i;
            var b = 2 + ((i + 1) % segments);
            mesh.TriangleIndices.Add(tipIndex);
            mesh.TriangleIndices.Add(a);
            mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(centerIndex);
            mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a);
        }
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private void AddRotationRing(SceneTransform t, ViewportGizmoAxis axis, double radius, double thickness, Material material)
    {
        const int segments = 48;
        for (var i = 0; i < segments; i++)
        {
            var angle = i * (Math.PI * 2.0 / segments);
            var c = Math.Cos(angle) * radius;
            var s = Math.Sin(angle) * radius;
            double x = 0, y = 0, z = 0;
            switch (axis)
            {
                case ViewportGizmoAxis.X: y = c; z = s; break;
                case ViewportGizmoAxis.Y: x = c; z = s; break;
                case ViewportGizmoAxis.Z: x = c; y = s; break;
            }
            var part = CreateBox(x - thickness, y - thickness, z - thickness,
                                 x + thickness, y + thickness, z + thickness, material);
            part.Transform = new TranslateTransform3D(t.X, t.Y, t.Z);
            AddGizmoPart(part, axis);
        }
    }

    private void AddGizmoPart(GeometryModel3D model, ViewportGizmoAxis axis)
    {
        _selectionGizmo.Children.Add(model);
        _gizmoParts[model] = axis;
    }

    public void Focus(SceneNode? node)
    {
        if (node?.Transform == null) return;
        var t = node.Transform;
        _target = new Point3D(t.X, t.Y, t.Z);
        _distance = Math.Max(12, Math.Min(_distance, 45));
        UpdateCamera();
        UpdateSelectionGizmo(_selected);
    }

    public void FrameAll(SceneNode root) => FrameAll(SceneBuilder.Flatten(root).Where(n => n.Transform != null).ToList());

    private void FrameAll(IReadOnlyList<SceneNode> nodes)
    {
        if (nodes.Count == 0)
        {
            _target = new Point3D(0, 0, 0);
            _distance = 120;
            UpdateCamera();
            UpdateSelectionGizmo(_selected);
            return;
        }

        var xs = nodes.Select(n => n.Transform!.X).ToArray();
        var ys = nodes.Select(n => n.Transform!.Y).ToArray();
        var zs = nodes.Select(n => n.Transform!.Z).ToArray();
        _target = new Point3D((xs.Min() + xs.Max()) * 0.5, (ys.Min() + ys.Max()) * 0.5, (zs.Min() + zs.Max()) * 0.5);
        var extent = Math.Max(xs.Max() - xs.Min(), Math.Max(ys.Max() - ys.Min(), zs.Max() - zs.Min()));
        _distance = Math.Max(35, extent * 1.25 + 20);
        UpdateCamera();
        UpdateSelectionGizmo(_selected);
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_viewport);
        var hit = VisualTreeHelper.HitTest(_viewport, point) as RayHitTestResult;
        if (hit?.ModelHit is GeometryModel3D gizmoModel && _gizmoParts.TryGetValue(gizmoModel, out var axis) &&
            _selected?.Transform is { IsFullyEditable: true })
        {
            StartGizmoDrag(axis, point);
            e.Handled = true;
            return;
        }

        if (hit?.ModelHit is GeometryModel3D model && _nodesByModel.TryGetValue(model, out var node))
        {
            SetSelected(node);
            NodeClicked?.Invoke(this, node);
            e.Handled = true;
        }
        else
        {
            SetSelected(null);
            NodeClicked?.Invoke(this, null);
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gizmoDragging) return;
        var node = _selected;
        var target = _dragPreviewTransform?.Clone();
        var mode = _gizmoMode;
        var axis = _dragAxis;
        _gizmoDragging = false;
        _dragStartTransform = null;
        _dragPreviewTransform = null;
        _selectionGizmo.Transform = Transform3D.Identity;
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();

        if (node != null && target != null)
        {
            try
            {
                // Restore the model to the authoritative node state. MainWindow immediately commits
                // the final target as one undoable edit, then RefreshNode redraws the same result.
                if (_models.TryGetValue(node, out var model) && node.Transform != null)
                    ApplyTransform(model, node.Transform);
                UpdateSelectionGizmo(node);
                GizmoDragCompleted?.Invoke(this, new GizmoDragEventArgs
                {
                    Node = node,
                    Mode = mode,
                    Axis = axis,
                    Target = target
                });
            }
            catch (Exception ex)
            {
                UpdateSelectionGizmo(node);
                GizmoDragFaulted?.Invoke(this, new GizmoFaultEventArgs { Message = $"Could not commit the gizmo drag: {ex.Message}" });
            }
        }
        e.Handled = true;
    }

    private void StartGizmoDrag(ViewportGizmoAxis axis, Point mouse)
    {
        if (_selected?.Transform == null || _gizmoMode == ViewportGizmoMode.Select)
            return;
        _dragAxis = axis;
        _dragStartMouse = mouse;
        _dragStartTransform = _selected.Transform.Clone();
        _dragPreviewTransform = _dragStartTransform.Clone();
        _dragStartScreenAngle = ScreenAngleAroundSelection(mouse, _dragStartTransform);
        _gizmoDragging = true;
        _viewport.CaptureMouse();
    }

    public void CancelGizmoDrag()
    {
        if (!_gizmoDragging) return;
        if (_selected != null && _selected.Transform != null && _models.TryGetValue(_selected, out var model))
            ApplyTransform(model, _selected.Transform);
        _gizmoDragging = false;
        _dragStartTransform = null;
        _dragPreviewTransform = null;
        _selectionGizmo.Transform = Transform3D.Identity;
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();
        UpdateSelectionGizmo(_selected);
    }

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_gizmoDragging) return;
        _orbiting = true;
        _lastMouse = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _orbiting = false;
        if (!_panning) _viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_gizmoDragging || e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _lastMouse = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = false;
        if (!_orbiting) _viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_gizmoDragging)
        {
            try
            {
                UpdateGizmoDrag(e.GetPosition(_viewport));
            }
            catch (Exception ex)
            {
                CancelGizmoDrag();
                GizmoDragFaulted?.Invoke(this, new GizmoFaultEventArgs { Message = $"Gizmo drag was cancelled safely: {ex.Message}" });
            }
            e.Handled = true;
            return;
        }

        if (!_orbiting && !_panning) return;
        var current = e.GetPosition(_viewport);
        var dx = current.X - _lastMouse.X;
        var dy = current.Y - _lastMouse.Y;
        _lastMouse = current;

        if (_orbiting)
        {
            _yaw += dx * 0.32;
            _pitch = Math.Clamp(_pitch - dy * 0.28, -88, 88);
            UpdateCamera();
        }
        else if (_panning)
        {
            var look = _camera.LookDirection;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, _camera.UpDirection);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            up.Normalize();
            var scale = Math.Max(0.02, _distance * 0.0018);
            var delta = right * (-dx * scale) + up * (dy * scale);
            _target += delta;
            UpdateCamera();
        }
    }

    private void UpdateGizmoDrag(Point mouse)
    {
        if (_selected == null || _dragStartTransform == null || !_models.TryGetValue(_selected, out var model))
            return;

        var start = _dragStartTransform;
        var next = start.Clone();
        if (_gizmoMode == ViewportGizmoMode.Move)
        {
            var delta = GetAxisWorldDelta(start, _dragAxis, mouse);
            switch (_dragAxis)
            {
                case ViewportGizmoAxis.X: next.X = start.X + delta; break;
                case ViewportGizmoAxis.Y: next.Y = start.Y + delta; break;
                case ViewportGizmoAxis.Z: next.Z = start.Z + delta; break;
            }
        }
        else if (_gizmoMode == ViewportGizmoMode.Scale)
        {
            var pixelDelta = GetAxisPixelDelta(start, _dragAxis, mouse);
            var scaleDelta = pixelDelta / 90.0;
            switch (_dragAxis)
            {
                case ViewportGizmoAxis.X: next.ScaleX = Math.Max(0.001, start.ScaleX + scaleDelta); break;
                case ViewportGizmoAxis.Y: next.ScaleY = Math.Max(0.001, start.ScaleY + scaleDelta); break;
                case ViewportGizmoAxis.Z: next.ScaleZ = Math.Max(0.001, start.ScaleZ + scaleDelta); break;
            }
        }
        else if (_gizmoMode == ViewportGizmoMode.Rotate)
        {
            var currentAngle = ScreenAngleAroundSelection(mouse, start);
            var delta = NormalizeAngle(currentAngle - _dragStartScreenAngle);
            switch (_dragAxis)
            {
                case ViewportGizmoAxis.X: next.RotationX = start.RotationX + delta; break;
                case ViewportGizmoAxis.Y: next.RotationY = start.RotationY + delta; break;
                case ViewportGizmoAxis.Z: next.RotationZ = start.RotationZ + delta; break;
            }
        }

        if (_snapEnabled)
            ApplyActiveAxisSnap(next);

        if (!IsFinite(next))
            throw new InvalidOperationException("The calculated transform was not finite.");

        _dragPreviewTransform = next;
        ApplyTransform(model, next);

        // Do not rebuild the Model3D gizmo tree while WPF is processing a captured mouse drag.
        // Rebuilding it every MouseMove was fragile for newly staged editor-only objects.
        // The gizmo stays fixed for rotate/scale; for move, translate the group by the preview delta.
        _selectionGizmo.Transform = _gizmoMode == ViewportGizmoMode.Move
            ? new TranslateTransform3D(next.X - start.X, next.Y - start.Y, next.Z - start.Z)
            : Transform3D.Identity;

        GizmoDragPreview?.Invoke(this, new GizmoDragEventArgs
        {
            Node = _selected,
            Mode = _gizmoMode,
            Axis = _dragAxis,
            Target = next.Clone()
        });
    }

    private void ApplyActiveAxisSnap(SceneTransform next)
    {
        var step = Math.Max(0.000001, _snapStep);
        static double Snap(double value, double amount) => Math.Round(value / amount, MidpointRounding.AwayFromZero) * amount;

        if (_gizmoMode == ViewportGizmoMode.Move)
        {
            if (_dragAxis == ViewportGizmoAxis.X) next.X = Snap(next.X, step);
            else if (_dragAxis == ViewportGizmoAxis.Y) next.Y = Snap(next.Y, step);
            else next.Z = Snap(next.Z, step);
        }
        else if (_gizmoMode == ViewportGizmoMode.Rotate)
        {
            if (_dragAxis == ViewportGizmoAxis.X) next.RotationX = Snap(next.RotationX, step);
            else if (_dragAxis == ViewportGizmoAxis.Y) next.RotationY = Snap(next.RotationY, step);
            else next.RotationZ = Snap(next.RotationZ, step);
        }
        else if (_gizmoMode == ViewportGizmoMode.Scale)
        {
            if (_dragAxis == ViewportGizmoAxis.X) next.ScaleX = Math.Max(0.001, Snap(next.ScaleX, step));
            else if (_dragAxis == ViewportGizmoAxis.Y) next.ScaleY = Math.Max(0.001, Snap(next.ScaleY, step));
            else next.ScaleZ = Math.Max(0.001, Snap(next.ScaleZ, step));
        }
    }

    private double GetAxisWorldDelta(SceneTransform start, ViewportGizmoAxis axis, Point mouse)
    {
        var pixels = GetAxisPixelDelta(start, axis, mouse);
        var origin = new Point3D(start.X, start.Y, start.Z);
        var axisVector = AxisVector(axis);
        if (!TryProject(origin, out var a) || !TryProject(origin + axisVector, out var b))
            return 0;
        var projectedUnit = b - a;
        if (projectedUnit.Length < 0.05)
            return 0; // axis is almost directly into the camera; avoid explosive world deltas

        var worldDelta = pixels / projectedUnit.Length;
        var maxDrag = Math.Max(10.0, _distance * 2.5);
        return double.IsFinite(worldDelta) ? Math.Clamp(worldDelta, -maxDrag, maxDrag) : 0;
    }

    private double GetAxisPixelDelta(SceneTransform start, ViewportGizmoAxis axis, Point mouse)
    {
        var origin = new Point3D(start.X, start.Y, start.Z);
        var axisVector = AxisVector(axis);
        var gizmoLength = Math.Clamp(_distance * 0.055, 1.5, 25.0);
        if (!TryProject(origin, out var a) || !TryProject(origin + axisVector * gizmoLength, out var b))
            return 0;
        var screenAxis = b - a;
        if (screenAxis.Length < 0.001)
            return mouse.X - _dragStartMouse.X;
        screenAxis.Normalize();
        var delta = mouse - _dragStartMouse;
        return Vector.Multiply(delta, screenAxis);
    }

    private double ScreenAngleAroundSelection(Point mouse, SceneTransform transform)
    {
        if (!TryProject(new Point3D(transform.X, transform.Y, transform.Z), out var center))
            return 0;
        return Math.Atan2(mouse.Y - center.Y, mouse.X - center.X) * 180.0 / Math.PI;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private static bool IsFinite(SceneTransform t) =>
        double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.Z) &&
        double.IsFinite(t.RotationX) && double.IsFinite(t.RotationY) && double.IsFinite(t.RotationZ) &&
        double.IsFinite(t.ScaleX) && double.IsFinite(t.ScaleY) && double.IsFinite(t.ScaleZ);

    private static Vector3D AxisVector(ViewportGizmoAxis axis) => axis switch
    {
        ViewportGizmoAxis.X => new Vector3D(1, 0, 0),
        ViewportGizmoAxis.Y => new Vector3D(0, 1, 0),
        _ => new Vector3D(0, 0, 1)
    };

    private bool TryProject(Point3D world, out Point screen)
    {
        screen = default;
        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        if (width <= 1 || height <= 1)
            return false;

        var forward = _camera.LookDirection;
        if (forward.LengthSquared < 1e-9)
            return false;
        forward.Normalize();
        var upSeed = _camera.UpDirection;
        if (upSeed.LengthSquared < 1e-9)
            upSeed = new Vector3D(0, 1, 0);
        upSeed.Normalize();
        var right = Vector3D.CrossProduct(forward, upSeed);
        if (right.LengthSquared < 1e-9)
            return false;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();

        var v = world - _camera.Position;
        var depth = Vector3D.DotProduct(v, forward);
        if (depth <= Math.Max(0.001, _camera.NearPlaneDistance))
            return false;
        var focal = height / (2.0 * Math.Tan(_camera.FieldOfView * Math.PI / 360.0));
        screen = new Point(
            width * 0.5 + Vector3D.DotProduct(v, right) * focal / depth,
            height * 0.5 - Vector3D.DotProduct(v, up) * focal / depth);
        return double.IsFinite(screen.X) && double.IsFinite(screen.Y);
    }

    private void FrameNativePreview(NativeMeshInfo info)
    {
        double minX = -1, minY = -1, minZ = -1, maxX = 1, maxY = 1, maxZ = 1;
        if (info.HasBounds)
        {
            minX = info.BoundsMin.X; minY = info.BoundsMin.Y; minZ = info.BoundsMin.Z;
            maxX = info.BoundsMax.X; maxY = info.BoundsMax.Y; maxZ = info.BoundsMax.Z;
        }
        else if (info.Geometry is { } geometry && geometry.Positions.Length > 0)
        {
            minX = geometry.Positions.Min(p => (double)p.X);
            minY = geometry.Positions.Min(p => (double)p.Y);
            minZ = geometry.Positions.Min(p => (double)p.Z);
            maxX = geometry.Positions.Max(p => (double)p.X);
            maxY = geometry.Positions.Max(p => (double)p.Y);
            maxZ = geometry.Positions.Max(p => (double)p.Z);
        }

        _target = new Point3D((minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5);
        var extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        _distance = Math.Max(3.0, extent * 1.8 + 2.0);
        UpdateCamera();
        UpdateSelectionGizmo(null);
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_gizmoDragging) return;
        _distance *= e.Delta > 0 ? 0.86 : 1.16;
        _distance = Math.Clamp(_distance, 1.5, 100000);
        UpdateCamera();
        UpdateSelectionGizmo(_selected);
        e.Handled = true;
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180.0;
        var pitch = _pitch * Math.PI / 180.0;
        var cp = Math.Cos(pitch);
        var direction = new Vector3D(cp * Math.Sin(yaw), Math.Sin(pitch), cp * Math.Cos(yaw));
        var position = _target - direction * _distance;
        _camera.Position = position;
        _camera.LookDirection = _target - position;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FieldOfView = 55;
        _camera.NearPlaneDistance = Math.Max(0.01, _distance / 10000.0);
        _camera.FarPlaneDistance = Math.Max(100000, _distance * 20);
    }

    private static void ApplyTransform(GeometryModel3D model, SceneTransform t)
    {
        model.Transform = new Transform3DGroup
        {
            Children = new Transform3DCollection
            {
                new ScaleTransform3D(Math.Max(0.001, t.ScaleX), Math.Max(0.001, t.ScaleY), Math.Max(0.001, t.ScaleZ)),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), t.RotationX)),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), t.RotationY)),
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), t.RotationZ)),
                new TranslateTransform3D(t.X, t.Y, t.Z)
            }
        };
    }

    private static Material CreateNodeMaterial(SceneNode node)
    {
        Color color;
        if (node.NativeMesh is { HasGeometry: true })
            color = Color.FromRgb(112, 165, 205);
        else if (node.NativeMesh is { HasBounds: true })
            color = Color.FromRgb(64, 190, 220);
        else if (node.TypeName.Contains("Collision", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(190, 95, 95);
        else if (node.TypeName.Contains("SpatialPrefab", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(92, 155, 220);
        else if (node.TypeName.Contains("Light", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(235, 205, 95);
        else
            color = Color.FromRgb(145, 162, 185);
        return new DiffuseMaterial(new SolidColorBrush(color));
    }

    private static GeometryModel3D CreateNativeGeometry(NativeMeshGeometry geometry, Material material)
    {
        var mesh = new MeshGeometry3D();
        foreach (var p in geometry.Positions)
            mesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
        foreach (var index in geometry.TriangleIndices)
            mesh.TriangleIndices.Add(index);
        if (mesh.CanFreeze)
            mesh.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static GeometryModel3D CreateCube(double size, Material material)
    {
        var h = size * 0.5;
        return CreateBox(-h, -h, -h, h, h, h, material);
    }

    private static GeometryModel3D CreateBox(
        double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ,
        Material material)
    {
        static (double Min, double Max) Expand(double min, double max)
        {
            if (Math.Abs(max - min) >= 0.02) return (min, max);
            var c = (min + max) * 0.5;
            return (c - 0.01, c + 0.01);
        }
        (minX, maxX) = Expand(minX, maxX);
        (minY, maxY) = Expand(minY, maxY);
        (minZ, maxZ) = Expand(minZ, maxZ);

        var p = new[]
        {
            new Point3D(minX,minY,minZ), new Point3D(maxX,minY,minZ), new Point3D(maxX,maxY,minZ), new Point3D(minX,maxY,minZ),
            new Point3D(minX,minY,maxZ), new Point3D(maxX,minY,maxZ), new Point3D(maxX,maxY,maxZ), new Point3D(minX,maxY,maxZ)
        };
        var triangles = new[]
        {
            0,2,1, 0,3,2, 4,5,6, 4,6,7,
            0,1,5, 0,5,4, 3,7,6, 3,6,2,
            1,2,6, 1,6,5, 0,4,7, 0,7,3
        };
        var mesh = new MeshGeometry3D();
        foreach (var point in p) mesh.Positions.Add(point);
        foreach (var index in triangles) mesh.TriangleIndices.Add(index);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
}
