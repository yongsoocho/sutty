using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace sutty.UI.ViewModels;

public sealed class BrowserItemCollection<T> : ObservableCollection<T>
{
    // The virtualizing list needs one new snapshot, even for a directory with 100,000 entries.
    public void ReplaceWith(IEnumerable<T> entries)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var entry in entries) Items.Add(entry);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
