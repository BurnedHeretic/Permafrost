using Permafrost.Core.Scene;

namespace Permafrost.Core.Commands;

public interface IEditorCommand
{
    string Name { get; }
    void Execute();
    void Undo();
}

public sealed class UndoStack
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.TryPeek(out var c) ? c.Name : null;
    public string? RedoName => _redo.TryPeek(out var c) ? c.Name : null;

    public event EventHandler? Changed;

    public void Do(IEditorCommand command)
    {
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var command)) return;
        command.Undo();
        _redo.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command)) return;
        command.Execute();
        _undo.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public readonly record struct TransformSnapshot(
    double X, double Y, double Z,
    double RotationX, double RotationY, double RotationZ,
    double ScaleX, double ScaleY, double ScaleZ)
{
    public static TransformSnapshot From(SceneTransform transform) => new(
        transform.X, transform.Y, transform.Z,
        transform.RotationX, transform.RotationY, transform.RotationZ,
        transform.ScaleX, transform.ScaleY, transform.ScaleZ);
}

/// <summary>
/// Patches a Frostbite LinearTransform in-place. BF2 stores the orientation/scale as the
/// right/up/forward basis vectors and translation separately, so rotation/scale editing must
/// rewrite the three basis vectors rather than patching an Euler-angle field that does not exist.
/// </summary>
public sealed class TransformNodeCommand : IEditorCommand
{
    private readonly SceneNode _node;
    private readonly Action<SceneNode>? _onChanged;
    private readonly TransformSnapshot _before;
    private readonly TransformSnapshot _after;
    private readonly string _name;

    public TransformNodeCommand(
        SceneNode node,
        TransformSnapshot after,
        string name = "Transform",
        Action<SceneNode>? onChanged = null)
    {
        if (node.Transform is not { IsFullyEditable: true } || (!node.IsEditorOnly && node.Document == null))
            throw new InvalidOperationException("The selected node does not have an editable transform.");

        _node = node;
        _onChanged = onChanged;
        _before = TransformSnapshot.From(node.Transform);
        _after = after;
        _name = name;
    }

    public string Name => $"{_name} {_node.Name}";

    public void Execute() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(TransformSnapshot value)
    {
        SceneTransformEditor.Apply(_node, value);
        _onChanged?.Invoke(_node);
    }

}

public sealed class TranslateNodeCommand : IEditorCommand
{
    private readonly TransformNodeCommand _inner;

    public TranslateNodeCommand(SceneNode node, double x, double y, double z, Action<SceneNode>? onChanged = null)
    {
        if (node.Transform == null)
            throw new InvalidOperationException("The selected node does not have an editable Frostbite translation.");
        var t = node.Transform;
        _inner = new TransformNodeCommand(node,
            new TransformSnapshot(x, y, z, t.RotationX, t.RotationY, t.RotationZ, t.ScaleX, t.ScaleY, t.ScaleZ),
            "Move", onChanged);
    }

    public string Name => _inner.Name;
    public void Execute() => _inner.Execute();
    public void Undo() => _inner.Undo();
}

/// <summary>
/// Lightweight command wrapper used for editor-only scene operations such as staging,
/// duplicating and deleting preview placements. This keeps those actions in the same
/// undo/redo stack as Frostbite transform edits without pretending they are serialized EBX edits.
/// </summary>
public sealed class DelegateEditorCommand : IEditorCommand
{
    private readonly Action _execute;
    private readonly Action _undo;

    public DelegateEditorCommand(string name, Action execute, Action undo)
    {
        Name = name;
        _execute = execute;
        _undo = undo;
    }

    public string Name { get; }
    public void Execute() => _execute();
    public void Undo() => _undo();
}
