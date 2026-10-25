using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Liuzhuan.Utils;

/// <summary>One notification when switching the entire filter; ordinary updates retain object identity.</summary>
public sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public void Replace(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
