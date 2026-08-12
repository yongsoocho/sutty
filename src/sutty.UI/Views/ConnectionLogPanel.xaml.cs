using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Diagnostics;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace sutty.UI.Views;

public sealed partial class ConnectionLogPanel : UserControl
{
    private const int MaxDisplayedEntries = 2_000;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _subscribed;

    public ObservableCollection<ConnectionLogItemVm> Entries { get; } = [];

    public ConnectionLogPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshFromStore();
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshFromStore();
    }

    private void ConnectionLogPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            ConnectionLogStore.EntryAdded += ConnectionLogStore_EntryAdded;
            _subscribed = true;
        }

        _refreshTimer ??= CreateRefreshTimer();
        RefreshFromStore();
    }

    private void ConnectionLogPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            ConnectionLogStore.EntryAdded -= ConnectionLogStore_EntryAdded;
            _subscribed = false;
        }
        _refreshTimer?.Stop();
    }

    private DispatcherQueueTimer CreateRefreshTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => RefreshFromStore();
        return timer;
    }

    private void ConnectionLogStore_EntryAdded(ConnectionLogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_subscribed || _refreshTimer is null)
                return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshFromStore();

    private void LevelFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Entries is not null)
            RefreshFromStore();
    }

    private void RefreshFromStore()
    {
        if (SearchBox is null || LevelFilter is null || LogList is null || DetailBox is null)
            return;

        var logList = LogList;
        var detailBox = DetailBox;

        var snapshot = ConnectionLogStore.Snapshot();
        var query = SearchBox.Text.Trim();
        var filter = LevelFilter.SelectedIndex;
        var selectedSequence = (logList.SelectedItem as ConnectionLogItemVm)?.Entry.Sequence;

        var filtered = snapshot
            .AsEnumerable()
            .Reverse()
            .Where(entry => filter switch
            {
                1 => entry.Severity >= ConnectionLogSeverity.Warning,
                2 => entry.Severity == ConnectionLogSeverity.Verbose,
                _ => true,
            })
            .Select(entry => new ConnectionLogItemVm(entry))
            .Where(entry => entry.Matches(query))
            .Take(MaxDisplayedEntries)
            .ToArray();

        Entries.Clear();
        foreach (var entry in filtered)
            Entries.Add(entry);

        if (selectedSequence is long sequence)
            logList.SelectedItem = Entries.FirstOrDefault(item => item.Entry.Sequence == sequence);
        if (logList.SelectedItem is not ConnectionLogItemVm)
            detailBox.Text = "";

        CountText.Text = filtered.Length == snapshot.Count
            ? filtered.Length.ToString("N0")
            : $"{filtered.Length:N0} / {snapshot.Count:N0}";
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        logList.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DetailBox.Text = (LogList.SelectedItem as ConnectionLogItemVm)?.FullText ?? "";

    private void CopyVisible_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
            return;

        var text = new StringBuilder();
        foreach (var entry in Entries.Reverse())
        {
            if (text.Length > 0)
                text.AppendLine().AppendLine();
            text.Append(entry.FullText);
        }
        Helpers.ClipboardHelper.CopyText(text.ToString());
    }
}
