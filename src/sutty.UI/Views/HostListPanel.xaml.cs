using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace sutty.UI.Views;

/// <summary>
/// 접속 히스토리 (SQLite connection_log, append-only).
/// - PINNED: 사용자가 직접 고정한 호스트
/// - RECENT: 최근 접속 기록 최신순 (같은 서버도 접속마다 한 줄씩)
/// 카드 클릭 시 Home에 비밀 없는 실제 연결 초안을 불러온다.
/// </summary>
public sealed partial class HostListPanel : UserControl
{
    private readonly List<HostInfoModel> _allPinned = [];
    private readonly List<HostInfoModel> _allRecent = [];
    private string _currentQuery = "";

    public ObservableCollection<HostInfoModel> PinnedHosts { get; } = [];
    public ObservableCollection<HostInfoModel> FilteredHosts { get; } = [];

    /// <summary>카드를 활성화하면 실제 연결 초안 열기를 요청한다.</summary>
    public event EventHandler<HostInfoModel>? ConnectRequested;

    public HostListPanel()
    {
        this.InitializeComponent();
        LoadFromStore();
        ApplyFilter("");

        // ItemsRepeater가 만든 HostCard에 클릭 이벤트를 연결 (두 리스트 모두)
        PinnedRepeater.ElementPrepared += OnElementPrepared;
        HostRepeater.ElementPrepared += OnElementPrepared;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Controls.HostCard card)
        {
            card.Clicked -= OnCardClicked;
            card.Clicked += OnCardClicked;
            card.PinToggled -= OnPinToggled;
            card.PinToggled += OnPinToggled;
        }
    }

    private void OnCardClicked(object? sender, HostInfoModel host)
        => ConnectRequested?.Invoke(this, host);

    /// <summary>설정 변경 후 보관 기한과 pin 목록을 현재 화면에 다시 적용한다.</summary>
    public void RefreshFromStore()
    {
        LoadFromStore();
        ApplyFilter(_currentQuery);
    }

    public void RefreshLanguage() => Bindings.Update();

    private void OnPinToggled(object? sender, HostInfoModel host)
    {
        HostHistoryStore.SetPinned(
            host.Hostname,
            host.Alias,
            !host.IsPinned,
            host.Username,
            host.Port,
            host.AuthMethod,
            host.PrivateKeyPath,
            host.Tags);

        RefreshFromStore();
    }

    // ── SQLite에서 로드 (보관 기한 정리 → 고정 호스트 + 최근 로그) ──

    private void LoadFromStore()
    {
        HostHistoryStore.Purge(SettingsService.Current.HistoryRetentionDays);

        _allPinned.Clear();
        foreach (var entry in HostHistoryStore.GetPinned())
        {
            _allPinned.Add(new HostInfoModel
            {
                Alias = entry.Alias,
                Hostname = entry.Hostname,
                LastConnected = entry.ConnectedAt,
                ConnectionCount = entry.ConnectionCount,
                IsPinned = true,
                Username = entry.Username,
                Port = entry.Port,
                AuthMethod = entry.AuthMethod,
                PrivateKeyPath = entry.PrivateKeyPath,
                Tags = [.. entry.Tags],
            });
        }

        _allRecent.Clear();
        foreach (var entry in HostHistoryStore.GetRecent(150))
        {
            _allRecent.Add(new HostInfoModel
            {
                Id = entry.Id,
                Alias = entry.Alias,
                Hostname = entry.Hostname,
                LastConnected = entry.ConnectedAt,
                IsPinned = entry.IsPinned,
                Username = entry.Username,
                Port = entry.Port,
                AuthMethod = entry.AuthMethod,
                PrivateKeyPath = entry.PrivateKeyPath,
                Tags = [.. entry.Tags],
            });
        }
    }

    // ── 검색 필터 (두 섹션 모두에 적용) ──

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ApplyFilter(sender.Text);
    }

    private void ApplyFilter(string query)
    {
        _currentQuery = query;
        var q = query.Trim();

        static bool Match(HostInfoModel h, string q) =>
            h.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            h.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            h.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            h.Tags.Any(tag => tag.Contains(q, StringComparison.OrdinalIgnoreCase));

        PinnedHosts.Clear();
        foreach (var h in _allPinned.Where(h => q.Length == 0 || Match(h, q)))
            PinnedHosts.Add(h);

        FilteredHosts.Clear();
        foreach (var h in _allRecent.Where(h => q.Length == 0 || Match(h, q)))
            FilteredHosts.Add(h);

        PinnedHeader.Visibility = PinnedHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = FilteredHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = PinnedHosts.Count == 0 && FilteredHosts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
