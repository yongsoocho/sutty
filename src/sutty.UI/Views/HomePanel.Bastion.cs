using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sutty.UI.Views;

public sealed partial class HomePanel
{
    private readonly BastionConnectionService _bastionProfiles = new(
        HostProfileStore.GetById,
        id => LocalCredentialVault.Default.TryRead(id, out var secret) ? secret : null);
    private bool _updatingBastion;
    private bool _savingBastion;

    private void BastionPanel_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSavedBastions();
        RefreshBastionPath();
    }

    private void SavedBastionCombo_DropDownOpened(object sender, object e) => RefreshSavedBastions();

    private void RefreshSavedBastions(string? selectId = null)
    {
        if (SavedBastionCombo is null) return;
        var previousChoice = SavedBastionCombo.SelectedItem as BastionProfileChoice;
        var selectedId = selectId ?? previousChoice?.ProfileId;
        try
        {
            var choices = BastionConnectionService.GetChoices(HostProfileStore.GetAll(limit: 10_000));
            var replacement = choices.FirstOrDefault(choice => choice.ProfileId == selectedId);
            var sourceChanged = selectId is null && previousChoice is not null && previousChoice != replacement;
            var items = new List<object> { Loc.T("직접 입력", "Enter manually") };
            items.AddRange(choices);
            var wasUpdating = _updatingBastion;
            _updatingBastion = true;
            try
            {
                SavedBastionCombo.ItemsSource = items;
                SavedBastionCombo.SelectedItem = sourceChanged ? items[0] : (object?)replacement ?? items[0];
            }
            finally { _updatingBastion = wasUpdating; }
            if (sourceChanged)
            {
                ResetBastionSelection(clearSecrets: true);
                ShowBastionStatus("원본 경유 서버 프로필이 변경되었거나 삭제되었습니다. 다시 선택하거나 직접 확인하세요.",
                    "The source bastion profile changed or was deleted. Select it again or review the settings manually.");
            }
        }
        catch (Exception error) when (IsBastionStorageError(error))
        {
            ShowBastionStatus("저장된 경유 서버를 읽지 못했습니다. 직접 입력하거나 다시 열어 주세요.",
                "Could not load saved bastions. Enter one manually or reopen the list.");
        }
    }

    private void SavedBastionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBastion) return;
        if (SavedBastionCombo.SelectedItem is not BastionProfileChoice choice)
        {
            ResetBastionSelection(clearSecrets: true);
            return;
        }

        BastionConnectionDraft draft;
        try { draft = _bastionProfiles.Load(choice.ProfileId); }
        catch (Exception error) when (IsBastionStorageError(error))
        {
            ApplyBastionRoute(new ConnectionRoute { Type = ConnectionRouteType.SshJump, Port = 22 });
            ResetBastionSelection(clearSecrets: true);
            ShowBastionStatus("선택한 경유 서버가 삭제되었거나 사용할 수 없습니다. 다시 선택하거나 직접 입력하세요.",
                "The selected bastion was deleted or is unavailable. Select another or enter it manually.");
            return;
        }

        try
        {
            ApplyBastionRoute(draft.Route);
            StrictRouteCheck.IsChecked = true;
            ShowBastionStatus(
                draft.CredentialUnavailable
                    ? "경유 서버의 저장된 인증정보를 읽지 못했습니다. 비밀번호 또는 키 암호를 입력하세요."
                    : "경유 서버 설정을 불러왔습니다. 현재 연결의 변경은 원본 프로필에 반영되지 않습니다.",
                draft.CredentialUnavailable
                    ? "Could not read the saved bastion credential. Enter its password or key passphrase."
                    : "Bastion settings loaded. Changes to this connection do not update the source profile.",
                success: !draft.CredentialUnavailable);
        }
        finally
        {
            draft.Route.Password = "";
            draft.Route.Passphrase = "";
        }
    }

    private void ApplyBastionRoute(ConnectionRoute route)
    {
        _updatingBastion = true;
        try
        {
            SelectRoute(ConnectionRouteType.SshJump);
            ProxyHostBox.Text = route.Host;
            ProxyPortBox.Text = route.Port.ToString();
            ProxyUsernameBox.Text = route.Username;
            JumpAuthCombo.SelectedItem = JumpAuthCombo.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, route.AuthMethod.ToString(), StringComparison.Ordinal));
            JumpKeyPathBox.Text = route.PrivateKeyPath;
            ProxyPasswordBox.Password = route.Password;
            JumpPassphraseBox.Password = route.Passphrase;
        }
        finally { _updatingBastion = false; }
        RefreshJumpAuthenticationUi();
        ClearFormStatus();
        RefreshBastionPath();
    }

    private void BastionIdentity_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResetBastionSelection(clearSecrets: true);
        RefreshBastionPath();
    }

    private void ResetBastionSelection(bool clearSecrets)
    {
        if (_updatingBastion || SavedBastionCombo is null || ProxyPasswordBox is null || JumpPassphraseBox is null)
            return;
        _updatingBastion = true;
        try
        {
            if (clearSecrets)
            {
                ProxyPasswordBox.Password = "";
                JumpPassphraseBox.Password = "";
            }
            SavedBastionCombo.SelectedIndex = SavedBastionCombo.Items.Count > 0 ? 0 : -1;
            BastionStatusText.Visibility = Visibility.Collapsed;
        }
        finally { _updatingBastion = false; }
    }

    private void RefreshBastionPath()
    {
        if (BastionPathText is null || ProxyHostBox is null || ProxyPortBox is null ||
            HostBox is null || PortBox is null) return;
        if (SelectedRouteType() != ConnectionRouteType.SshJump) return;
        var gateway = ProxyHostBox.Text.Trim();
        var target = HostBox.Text.Trim();
        BastionPathText.Text = SshRouteDisplay.FormatPath(new SshConnectionInfo
        {
            Host = target.Length > 0 ? target : Loc.T("목적지", "Destination"),
            Port = int.TryParse(PortBox.Text, out var port) ? port : 22,
            Route = new ConnectionRoute
            {
                Type = ConnectionRouteType.SshJump,
                Host = gateway.Length > 0 ? gateway : Loc.T("중앙 서버", "Bastion"),
                Port = int.TryParse(ProxyPortBox.Text, out var jumpPort) ? jumpPort : 22,
            },
        }, Loc.T("내 PC", "This PC"));
    }

    private async void SaveBastion_Click(object sender, RoutedEventArgs e)
    {
        if (_savingBastion || _connectInFlight || SelectedRouteType() != ConnectionRouteType.SshJump) return;
        var host = ProxyHostBox.Text.Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) ||
            !int.TryParse(ProxyPortBox.Text, out var port) || port is < 1 or > 65_535 ||
            string.IsNullOrWhiteSpace(ProxyUsernameBox.Text) ||
            (SelectedJumpAuthMethod() == SshAuthMethod.PublicKey && string.IsNullOrWhiteSpace(JumpKeyPathBox.Text)))
        {
            ShowFormError(ProxyHostBox, "경유 서버 주소·포트·사용자·인증을 확인하세요.",
                "Check the bastion address, port, user, and authentication.");
            return;
        }

        var profile = new HostProfileDraft
        {
            Host = host, Port = port, Username = ProxyUsernameBox.Text.Trim(),
            AuthMethod = SelectedJumpAuthMethod().ToString(),
            PrivateKeyPath = SelectedJumpAuthMethod() == SshAuthMethod.PublicKey ? JumpKeyPathBox.Text.Trim() : "",
            Route = new HostRouteProfile(),
        };
        var name = new TextBox { Text = host, MaxLength = 128,
            Header = Loc.T("이름", "Name"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("주소와 인증 방식만 새 Host로 저장합니다. 비밀번호와 키 암호는 저장하지 않습니다.",
                "Save the address and authentication settings as a new Host. Passwords and key passphrases are not saved.") });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = RequestedTheme, Content = content,
            Title = Loc.T("Bastion 저장 · Beta", "Save Bastion · Beta"),
            PrimaryButtonText = Loc.T("저장", "Save"), CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        _savingBastion = true;
        SaveBastionButton.IsEnabled = false;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            profile.DisplayName = name.Text.Trim();
            var saved = HostProfileStore.Save(profile);
            RefreshSavedBastions(saved.Id);
            ShowBastionStatus("경유 서버 프로필을 저장했습니다. 비밀번호와 키 암호는 현재 연결에만 유지됩니다.",
                "Bastion profile saved. Passwords and key passphrases remain only in this connection.", success: true);
        }
        catch (Exception error) when (IsBastionStorageError(error))
        {
            ShowBastionStatus("경유 서버 프로필을 저장하지 못했습니다. 입력과 로컬 저장소를 확인하세요.",
                "Could not save the bastion profile. Check the inputs and local storage.");
        }
        finally
        {
            _savingBastion = false;
            SaveBastionButton.IsEnabled = true;
        }
    }

    private void ShowBastionStatus(string korean, string english, bool success = false)
    {
        BastionStatusText.Text = Loc.T(korean, english);
        BastionStatusText.Foreground = ThemeResources.Brush(this, success ? "TextMuted" : "StatusAmber");
        BastionStatusText.Visibility = Visibility.Visible;
    }

    private static bool IsBastionStorageError(Exception error) => error is IOException or
        UnauthorizedAccessException or SqliteException or ArgumentException or InvalidOperationException;
}
