using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Permafrost.Core.Scene;

namespace Permafrost.Controls;

/// <summary>
/// Lightweight WPF 3D viewport used by the integration milestone. It renders transform proxies,
/// supports selection, orbit/pan/zoom, and keeps the UI independent from Frosty's renderer.
/// Native BF2 MeshSet rendering slots in behind the same SceneNode model later.
/// </summary>
public sealed class SceneViewport
{
    private readonly Viewport3D _viewport;
    private readonly Model3DGroup _scene = new();
    private readonly PerspectiveCamera _camera = new();
    private readonly Dictionary<SceneNode, GeometryModel3D> _models = new();
    private readonly Dictionary<GeometryModel3D, SceneNode> _nodesByModel = new();
    private readonly Dictionary<SceneNode, Material> _normalMaterials = new();

    private Point3D _target = new(0, 0, 0);
    private double _distance = 120;
    private double _yaw = -35;
    private double _pitch = -20;
    private Point _lastMouse;
    private bool _orbiting;
    private bool _panning;
    private SceneNode? _selected;

    public event EventHandler<SceneNode?>? NodeClicked;

    public SceneViewport(Viewport3D viewport)
    {
        _viewport = viewport;
        _viewport.Camera = _camera;
        _scene.Children.Add(new AmbientLight(Color.FromRgb(85, 85, 85)));
        _scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -1)));
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });

        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
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
        foreach (var model in _models.Values)
            _scene.Children.Remove(model);
        _models.Clear();
        _nodesByModel.Clear();
        _normalMaterials.Clear();
        _selected = null;

        var transformed = SceneBuilder.Flatten(root).Where(n => n.Transform != null).ToList();
        foreach (var node in transformed)
        {
            var material = CreateNodeMaterial(node);
            var model = CreateCube(1.8, material);
            ApplyTransform(model, node.Transform!);
            _models[node] = model;
            _nodesByModel[model] = node;
            _normalMaterials[node] = material;
            _scene.Children.Add(model);
        }

        FrameAll(transformed);
    }

    public void RefreshNode(SceneNode node)
    {
        if (node.Transform == null || !_models.TryGetValue(node, out var model))
            return;
        ApplyTransform(model, node.Transform);
    }

    public void SetSelected(SceneNode? node)
    {
        if (_selected != null && _models.TryGetValue(_selected, out var oldModel) && _normalMaterials.TryGetValue(_selected, out var oldMaterial))
        {
            oldModel.Material = oldMaterial;
            oldModel.BackMaterial = oldMaterial;
        }

        _selected = node;
        if (node != null && _models.TryGetValue(node, out var model))
        {
            var selectedMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(255, 190, 70)));
            model.Material = selectedMaterial;
            model.BackMaterial = selectedMaterial;
        }
    }

    public void Focus(SceneNode? node)
    {
        if (node?.Transform == null) return;
        var t = node.Transform;
        _target = new Point3D(t.X, t.Y, t.Z);
        _distance = Math.Max(12, Math.Min(_distance, 45));
        UpdateCamera();
    }

    public void FrameAll(SceneNode root) => FrameAll(SceneBuilder.Flatten(root).Where(n => n.Transform != null).ToList());

    private void FrameAll(IReadOnlyList<SceneNode> nodes)
    {
        if (nodes.Count == 0)
        {
            _target = new Point3D(0, 0, 0);
            _distance = 120;
            UpdateCamera();
            return;
        }

        var xs = nodes.Select(n => n.Transform!.X).ToArray();
        var ys = nodes.Select(n => n.Transform!.Y).ToArray();
        var zs = nodes.Select(n => n.Transform!.Z).ToArray();
        _target = new Point3D((xs.Min() + xs.Max()) * 0.5, (ys.Min() + ys.Max()) * 0.5, (zs.Min() + zs.Max()) * 0.5);
        var extent = Math.Max(xs.Max() - xs.Min(), Math.Max(ys.Max() - ys.Min(), zs.Max() - zs.Min()));
        _distance = Math.Max(35, extent * 1.25 + 20);
        UpdateCamera();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport)) as RayHitTestResult;
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

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
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
        if (e.ChangedButton != MouseButton.Middle) return;
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

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance *= e.Delta > 0 ? 0.86 : 1.16;
        _distance = Math.Clamp(_distance, 1.5, 100000);
        UpdateCamera();
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
                new ScaleTransform3D(Math.Max(0.25, t.ScaleX), Math.Max(0.25, t.ScaleY), Math.Max(0.25, t.ScaleZ)),
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
        if (node.TypeName.Contains("Collision", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(190, 95, 95);
        else if (node.TypeName.Contains("SpatialPrefab", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(92, 155, 220);
        else if (node.TypeName.Contains("Light", StringComparison.OrdinalIgnoreCase))
            color = Color.FromRgb(235, 205, 95);
        else
            color = Color.FromRgb(145, 162, 185);
        return new DiffuseMaterial(new SolidColorBrush(color));
    }

    private static GeometryModel3D CreateCube(double size, Material material)
    {
        var h = size * 0.5;
        var p = new[]
        {
            new Point3D(-h,-h,-h), new Point3D(h,-h,-h), new Point3D(h,h,-h), new Point3D(-h,h,-h),
            new Point3D(-h,-h,h),  new Point3D(h,-h,h),  new Point3D(h,h,h),  new Point3D(-h,h,h)
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
