using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Gives a <see cref="DataGrid"/> one items list for its whole life and swaps the rows inside it. Avalonia's DataGrid
/// (11.2) leaks part of every row it builds when its <c>ItemsSource</c> is replaced: each new row's details presenter
/// adds a class handler that is never removed, and the compositor keeps the old rows' pens. A grid refreshed on a timer
/// (Query auto refresh, Locks, Activity) grew by several KB a refresh. Refilling one list and raising Reset reuses the
/// rows instead. In XAML, bind <c>controls:GridRows.Source</c> where <c>ItemsSource</c> would be bound.
/// </summary>
public static class GridRows
{
    public static readonly AttachedProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IEnumerable?>("Source", typeof(GridRows));

    static GridRows() => SourceProperty.Changed.AddClassHandler<DataGrid>((grid, e) => Load(grid, e.NewValue as IEnumerable));

    public static IEnumerable? GetSource(DataGrid grid) => grid.GetValue(SourceProperty);

    public static void SetSource(DataGrid grid, IEnumerable? value) => grid.SetValue(SourceProperty, value);

    /// <summary>Shows <paramref name="items"/> in the grid, reusing the list the grid already has when it can.</summary>
    public static void Load(DataGrid grid, IEnumerable? items)
    {
        var itemType = ItemType(items);
        if (grid.ItemsSource is IReloadableList list && (items is null || list.ItemType == itemType))
        {
            list.Reload(items);
            return;
        }
        if (items is null) return;
        var created = (IReloadableList)Activator.CreateInstance(typeof(ReloadableList<>).MakeGenericType(itemType))!;
        created.Reload(items);
        grid.ItemsSource = created;
    }

    private static Type ItemType(IEnumerable? items) =>
        items?.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0] ?? typeof(object);
}

internal interface IReloadableList : IEnumerable
{
    Type ItemType { get; }
    void Reload(IEnumerable? items);
}

/// <summary>
/// A list whose contents are swapped as a whole, with one Reset. The rows are the caller's list itself when it is a
/// <see cref="List{T}"/>, so the grid sees (and edits) exactly what it would have as its <c>ItemsSource</c>.
/// </summary>
public sealed class ReloadableList<T> : IList<T>, IList, IReadOnlyList<T>, INotifyCollectionChanged, IReloadableList
{
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);
    private List<T> _items = [];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public Type ItemType => typeof(T);

    /// <summary>The rows now shown.</summary>
    public IReadOnlyList<T> Items => _items;

    public void Reload(IEnumerable<T>? items)
    {
        _items = items as List<T> ?? items?.ToList() ?? [];
        Reset();
    }

    void IReloadableList.Reload(IEnumerable? items) => Reload(items as IEnumerable<T> ?? items?.Cast<T>());

    private void Reset() => CollectionChanged?.Invoke(this, ResetArgs);

    public T this[int index]
    {
        get => _items[index];
        set { _items[index] = value; Reset(); }
    }

    object? IList.this[int index]
    {
        get => _items[index];
        set => this[index] = (T)value!;
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;
    bool IList.IsFixedSize => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    public bool Contains(T item) => _items.Contains(item);
    bool IList.Contains(object? value) => value is T item && _items.Contains(item);
    public int IndexOf(T item) => _items.IndexOf(item);
    int IList.IndexOf(object? value) => value is T item ? _items.IndexOf(item) : -1;
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public void Add(T item) => Insert(_items.Count, item);

    int IList.Add(object? value)
    {
        Add((T)value!);
        return _items.Count - 1;
    }

    public void Insert(int index, T item)
    {
        _items.Insert(index, item);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
    }

    void IList.Insert(int index, object? value) => Insert(index, (T)value!);

    public bool Remove(T item)
    {
        var index = _items.IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    void IList.Remove(object? value)
    {
        if (value is T item) Remove(item);
    }

    public void RemoveAt(int index)
    {
        var item = _items[index];
        _items.RemoveAt(index);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
    }

    public void Clear()
    {
        _items.Clear();
        Reset();
    }
}
