using System.Collections;
using System.ComponentModel;
using AIHub.Services.OfficeEditor;

namespace AIHub.Controls.OfficeTextEditor;

// DataGrid requests visible rows by index. A workbook with a value at row one
// million therefore does not allocate a million view models or empty cells.
internal sealed class OfficeWorkbookRows(OfficeWorkbook book, int count, int firstColumn, Func<WorkbookAddress, string, bool> commit) : IList
{
    internal sealed class GridRow(OfficeWorkbookRows owner, int index) : INotifyPropertyChanged
    {
        internal OfficeWorkbookRows Owner => owner;
        internal int Index => index;
        public string this[int column]
        {
            get => owner.Display(index, column);
            set => owner.Update(index, column, value);
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Notify() => PropertyChanged?.Invoke(this, new("Item[]"));
    }

    private readonly Dictionary<int, GridRow> _rows = [];
    internal string Display(int row, int column) => book.CellAt(new(row + 1, firstColumn + column)).Display;
    private void Update(int row, int column, string value) => commit(new(row + 1, firstColumn + column), value);
    internal void RefreshVisibleCells() { foreach (var row in _rows.Values.ToArray()) row.Notify(); }
    public int Count => count;
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public object this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
            if (!_rows.TryGetValue(index, out var view)) _rows[index] = view = new(this, index);
            return view;
        }
        set => throw new NotSupportedException();
    }
    public int IndexOf(object? value) => value is GridRow row && row.Owner == this ? row.Index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
    public void CopyTo(Array array, int index) { for (var i = 0; i < count; i++) array.SetValue(this[i], index + i); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
