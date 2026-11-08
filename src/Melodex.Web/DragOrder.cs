namespace Melodex.Web;

/// <summary>
/// Moving a row of a list by dragging it: which row is being dragged, and where it would go, shown as a line above or
/// below the row under the pointer.
/// </summary>
public sealed class DragOrder
{
    private int? _from;
    private int? _over;

    public void Start(int index) => (_from, _over) = (index, null);

    public void Over(int index) => _over = _from is null ? null : index;

    /// <summary>Moves the dragged row to where it was dropped.</summary>
    public void Drop(int index, Action<int, int> move)
    {
        if (_from is { } from && from != index)
            move(from, index);
        End();
    }

    /// <summary>Moves the dragged row to where it was dropped, for a move that's saved, so the list is drawn again once it is.</summary>
    public Task DropAsync(int index, Func<int, int, Task> move)
    {
        var from = _from;
        End();
        return from is { } f && f != index ? move(f, index) : Task.CompletedTask;
    }

    public void End() => (_from, _over) = (null, null);

    public string Class(int index) =>
        index == _from ? "dragging"
        : index == _over && _from is { } from ? (index < from ? "drop-before" : "drop-after")
        : "";
}
