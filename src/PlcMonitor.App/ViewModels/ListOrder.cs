namespace PlcMonitor.App.ViewModels;

/// <summary>Reordenar una lista arrastrando elementos sobre otro.</summary>
public static class ListOrder
{
    /// <summary>
    /// La lista con <paramref name="moved"/> (en su orden actual) justo antes de <paramref name="target"/>, o justo
    /// después con <paramref name="after"/>. Null si no se puede (el destino es uno de los movidos) o si no cambia nada.
    /// </summary>
    public static List<T>? Move<T>(IReadOnlyList<T> items, IReadOnlyCollection<T> moved, T target, bool after)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(moved);
        if (moved.Count == 0 || moved.Contains(target) || !items.Contains(target))
            return null;
        var result = items.Where(i => !moved.Contains(i)).ToList();
        result.InsertRange(result.IndexOf(target) + (after ? 1 : 0), items.Where(moved.Contains));
        return result.SequenceEqual(items) ? null : result;
    }
}
