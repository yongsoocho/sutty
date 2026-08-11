using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.Core.Security;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>
/// Displays explicit saved-host profiles separately from append-only connection history.
/// Secret values are resolved only when a saved profile is opened.
/// </summary>
public sealed partial class HostListPanel : UserControl
{
    private readonly List<HostInfoModel> _allSaved = [];
    private readonly List<HostInfoModel> _allTop = [];
    private readonly List<HostInfoModel> _allRecent = [];
    private string _currentQuery = "";
    private bool _storeUnavailable;

    public ObservableCollection<HostInfoModel> SavedHosts { get; } = [];
    public ObservableCollection<HostInfoModel> TopHosts { get; } = [];
    public ObservableCollection<HostInfoModel> FilteredHosts { get; } = [];

    public event EventHandler<HostInfoModel>? ConnectRequested;

    public HostListPanel()
    {
        InitializeComponent();
        LoadFromStore();
        ApplyFilter("");

        SavedRepeater.ElementPrepared += OnElementPrepared;
        TopRepeater.ElementPrepared += OnElementPrepared;
        HostRepeater.ElementPrepared += OnElementPrepared;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Controls.HostCard card) return;

        card.Clicked -= OnCardClicked;
        card.Clicked += OnCardClicked;
        card.PrimaryActionRequested -= OnPrimaryActionRequested;
        card.PrimaryActionRequested += OnPrimaryActionRequested;
        card.DeleteRequested -= OnDeleteRequested;
        card.DeleteRequested += OnDeleteRequested;
    }

    private void OnCardClicked(object? sender, HostInfoModel host)
        => ConnectRequested?.Invoke(this, host);

    public void RefreshFromStore()
    {
        LoadFromStore();
        ApplyFilter(_currentQuery);
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshFromStore();
    }

    private async void OnPrimaryActionRequested(object? sender, HostInfoModel host)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(host.ProfileId))
            {
                HostProfileStore.SetFavorite(host.ProfileId, !host.IsPinned);
                RefreshFromStore();
                return;
            }

            HostProfileStore.Save(new HostProfileDraft
            {
                DisplayName = host.Alias,
                Host = host.Hostname,
                Port = host.Port,
                Username = host.Username,
                AuthMethod = host.AuthMethod,
                PrivateKeyPath = host.PrivateKeyPath,
                Tags = host.Tags,
                IsFavorite = true,
            });
            RefreshFromStore();
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                                      IOException or UnauthorizedAccessException or
                                      ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Saved-host action failed: {error.GetType().Name}");
            await ShowStorageActionErrorAsync();
        }
    }

    private async void OnDeleteRequested(object? sender, HostInfoModel host)
    {
        if (!host.IsSavedProfile || string.IsNullOrWhiteSpace(host.ProfileId)) return;

        var dialog = new ContentDialog
        {
            Title = Helpers.Loc.T("저장 호스트 삭제", "Delete saved host"),
            Content = Helpers.Loc.T(
                $"'{host.Alias}' 저장 호스트를 삭제할까요? 접속 기록은 유지됩니다.",
                $"Delete the saved host '{host.Alias}'? Connection history will be kept."),
            PrimaryButtonText = Helpers.Loc.T("삭제", "Delete"),
            CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (!HostProfileStore.Delete(host.ProfileId)) return;

            if (!string.IsNullOrWhiteSpace(host.CredentialId))
            {
                try
                {
                    LocalCredentialVault.Default.Delete(host.CredentialId);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              System.Security.Cryptography.CryptographicException or
                                              System.ComponentModel.Win32Exception or ArgumentException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Encrypted credential cleanup failed: {error.GetType().Name}");
                }
            }

            RefreshFromStore();
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                                      IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Saved-host delete failed: {error.GetType().Name}");
            await ShowStorageActionErrorAsync();
        }
    }

    private async Task ShowStorageActionErrorAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Helpers.Loc.T("호스트 저장소 오류", "Host storage error"),
            Content = Helpers.Loc.T(
                "저장 호스트 변경을 완료하지 못했습니다. 저장소 접근 권한과 디스크 상태를 확인하세요.",
                "The saved-host change could not be completed. Check storage permissions and disk health."),
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void LoadFromStore()
    {
        _allSaved.Clear();
        _allTop.Clear();
        _allRecent.Clear();
        _storeUnavailable = false;

        try
        {
            HostProfileStore.EnsureInitialized();
            HostHistoryStore.Purge(SettingsService.Current.HistoryRetentionDays);

            var profiles = HostProfileStore.GetAll(limit: 1_000);
            var profileByConnection = profiles
                .GroupBy(profile => ConnectionKey(profile.Host, profile.Port, profile.Username))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var profile in profiles)
                _allSaved.Add(FromProfile(profile));

            foreach (var entry in HostHistoryStore.GetMostFrequent(
                         Math.Clamp(SettingsService.Current.HistoryTopHostCount, 1, 16)))
            {
                _allTop.Add(FromHistory(entry, FindProfile(entry, profileByConnection)));
            }

            foreach (var entry in HostHistoryStore.GetRecent(150))
                _allRecent.Add(FromHistory(entry, FindProfile(entry, profileByConnection)));
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                                      IOException or UnauthorizedAccessException)
        {
            _storeUnavailable = true;
            System.Diagnostics.Debug.WriteLine($"Host storage load failed: {error.GetType().Name}");
        }
    }

    private static HostInfoModel FromProfile(HostProfile profile) => new()
    {
        ProfileId = profile.Id,
        IsSavedProfile = true,
        CredentialId = profile.CredentialId,
        Alias = profile.DisplayName,
        Hostname = profile.Host,
        LastConnected = profile.LastConnectedAtUtc?.LocalDateTime,
        IsPinned = profile.IsFavorite,
        Username = profile.Username,
        Port = profile.Port,
        AuthMethod = profile.AuthMethod,
        PrivateKeyPath = profile.PrivateKeyPath,
        Tags = [.. profile.Tags],
        GroupName = profile.GroupName,
        Environment = profile.Environment,
    };

    private static HostInfoModel FromHistory(HostHistoryEntry entry, HostProfile? profile) => new()
    {
        Id = entry.Id,
        ProfileId = profile?.Id,
        CredentialId = profile?.CredentialId,
        Alias = entry.Alias,
        Hostname = entry.Hostname,
        LastConnected = entry.ConnectedAt,
        ConnectionCount = entry.ConnectionCount,
        IsPinned = profile?.IsFavorite == true,
        Username = entry.Username,
        Port = entry.Port,
        AuthMethod = entry.AuthMethod,
        PrivateKeyPath = entry.PrivateKeyPath,
        Tags = [.. entry.Tags],
        GroupName = profile?.GroupName ?? "",
        Environment = profile?.Environment ?? HostEnvironments.Unclassified,
        Outcome = entry.Outcome,
        ErrorCode = entry.ErrorCode,
    };

    private static HostProfile? FindProfile(
        HostHistoryEntry entry,
        IReadOnlyDictionary<string, HostProfile> profileByConnection) =>
        profileByConnection.GetValueOrDefault(ConnectionKey(entry.Hostname, entry.Port, entry.Username));

    private static string ConnectionKey(string host, int port, string username) =>
        $"{host.Trim().ToLowerInvariant()}\u001f{port}\u001f{username.Trim().ToLowerInvariant()}";

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ApplyFilter(sender.Text);
    }

    private void ApplyFilter(string query)
    {
        _currentQuery = query;
        var normalizedQuery = query.Trim();

        static bool Matches(HostInfoModel host, string value) =>
            host.Alias.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            host.Hostname.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            host.Username.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            host.GroupName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            host.Environment.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            host.Tags.Any(tag => tag.Contains(value, StringComparison.OrdinalIgnoreCase));

        ReplaceWithMatches(SavedHosts, _allSaved, normalizedQuery, Matches);
        ReplaceWithMatches(TopHosts, _allTop, normalizedQuery, Matches);
        ReplaceWithMatches(FilteredHosts, _allRecent, normalizedQuery, Matches);

        SavedHeader.Visibility = SavedHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TopHeader.Visibility = TopHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = FilteredHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoreErrorState.Visibility = _storeUnavailable ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !_storeUnavailable &&
                                SavedHosts.Count == 0 && TopHosts.Count == 0 && FilteredHosts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void ReplaceWithMatches(
        ObservableCollection<HostInfoModel> target,
        IEnumerable<HostInfoModel> source,
        string query,
        Func<HostInfoModel, string, bool> matches)
    {
        target.Clear();
        foreach (var host in source.Where(host => query.Length == 0 || matches(host, query)))
            target.Add(host);
    }
}
