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

public sealed class TranslateNodeCommand : IEditorCommand
{
    private readonly SceneNode _node;
    private readonly Action<SceneNode>? _onChanged;
    private readonly (double X, double Y, double Z) _before;
    private readonly (double X, double Y, double Z) _after;

    public TranslateNodeCommand(SceneNode node, double x, double y, double z, Action<SceneNode>? onChanged = null)
    {
        if (node.Transform?.TranslationStruct == null || node.Document == null)
            throw new InvalidOperationException("The selected node does not have an editable Frostbite translation.");
        _node = node;
        _onChanged = onChanged;
        _before = (node.Transform.X, node.Transform.Y, node.Transform.Z);
        _after = (x, y, z);
    }

    public string Name => $"Move {_node.Name}";

    public void Execute() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply((double X, double Y, double Z) value)
    {
        var transform = _node.Transform!;
        var trans = transform.TranslationStruct!;
        var document = _node.Document!;
        document.PatchFloat(trans, "x", (float)value.X);
        document.PatchFloat(trans, "y", (float)value.Y);
        document.PatchFloat(trans, "z", (float)value.Z);
        transform.X = value.X;
        transform.Y = value.Y;
        transform.Z = value.Z;
        _onChanged?.Invoke(_node);
    }
}
