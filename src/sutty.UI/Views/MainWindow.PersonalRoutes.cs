using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class MainWindow
{
    private readonly BastionConnectionService _personalGateways = new(HostProfileStore.GetById,
        id => LocalCredentialVault.Default.TryRead(id, out var secret) ? secret : null);

    private static bool IsRouteError(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException;

    private async Task ShowPersonalRouteErrorAsync(Exception error)
    {
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Loc.T("접속 경로를 확인하세요", "Review the connection route"),
            Content = Loc.T("호스트 메뉴에서 허용된 경로와 경유 서버를 다시 선택하세요.", "Select an available route and gateway in the host menu.") + "\n" + error.Message,
            CloseButtonText = Loc.T("닫기", "Close"),
        }.ShowAsync();
    }

    private async Task ChoosePersonalRouteAsync(HostInfoModel host)
    {
        if (string.IsNullOrEmpty(host.ProfileId)) return;
        try
        {
            var saved = HostProfileStore.GetById(host.ProfileId) ?? throw new InvalidOperationException("Host not found.");
            PersonalHostRouteStore.Validate(saved, new(PersonalRouteMode.Profile));
            var previous = PersonalHostRouteStore.Read(saved.Id);
            var picker = new ComboBox { Header = Loc.T("접속 경로", "Connection route"), MinWidth = 280 };
            void Add(string name, PersonalRouteChoice choice)
            {
                var item = new ComboBoxItem { Content = name, Tag = choice };
                picker.Items.Add(item);
                if (choice == previous) picker.SelectedItem = item;
            }
            Add(Loc.T("공유 호스트 설정 따르기", "Use saved host route"), new(PersonalRouteMode.Profile));
            if (!saved.Route.DisableDirect && saved.Route.LegacyDisableDirect != true)
                Add(Loc.T("직접 연결", "Direct"), new(PersonalRouteMode.Direct));
            foreach (var gateway in BastionConnectionService.GetChoices(HostProfileStore.GetAll(limit: 10_000)))
                if (gateway.ProfileId != saved.Id)
                    Add("Bastion · " + gateway, new(PersonalRouteMode.Bastion, gateway.ProfileId));
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = saved.DisplayName, Content = picker,
                PrimaryButtonText = Loc.T("이번만 연결", "Connect once"),
                SecondaryButtonText = Loc.T("내 기본값으로 저장", "Save my default"),
                CloseButtonText = Loc.T("취소", "Cancel"), DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = picker.SelectedIndex >= 0, IsSecondaryButtonEnabled = picker.SelectedIndex >= 0,
            };
            picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = picker.SelectedIndex >= 0;
            var result = await dialog.ShowAsync();
            if (picker.SelectedItem is not ComboBoxItem { Tag: PersonalRouteChoice choice }) return;
            if (result == ContentDialogResult.Secondary) PersonalHostRouteStore.Save(saved.Id, choice);
            if (result == ContentDialogResult.Primary) await OpenHistoryDraftAsync(host, once: choice);
        }
        catch (Exception error) when (IsRouteError(error)) { await ShowPersonalRouteErrorAsync(error); }
    }

    // Prompt against the resolved endpoints. A private route never enters the shared-profile editor.
    private async Task<bool> ConfirmRouteCredentialsAsync(SshConnectionInfo draft)
    {
        var fields = new List<(PasswordBox Box, bool Required, Action<string> Apply)>();
        void Add(string title, bool required, Action<string> apply) =>
            fields.Add((new PasswordBox { Header = title }, required, apply));
        void AddEndpoint(string label, SshAuthMethod auth, string password, string passphrase,
            Action<string> setPassword, Action<string> setPassphrase)
        {
            if (auth == SshAuthMethod.Password && string.IsNullOrEmpty(password))
                Add(label + " · " + Loc.T("비밀번호", "Password"), true, setPassword);
            if (auth == SshAuthMethod.PublicKey && string.IsNullOrEmpty(passphrase))
                Add(label + " · " + Loc.T("키 암호 (선택)", "Key passphrase (optional)"), false, setPassphrase);
        }
        AddEndpoint(draft.Username + "@" + draft.Host, draft.AuthMethod, draft.Password, draft.Passphrase,
            value => draft.Password = value, value => draft.Passphrase = value);
        if (draft.Route.Type == ConnectionRouteType.SshJump)
        {
            var hop = draft.Route;
            AddEndpoint("Bastion · " + hop.Username + "@" + hop.Host, hop.AuthMethod, hop.Password, hop.Passphrase,
                value => hop.Password = value, value => hop.Passphrase = value);
        }
        if (fields.Count == 0) return true;
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Loc.T("이번 연결의 인증", "Connection credentials"), Content = panel,
            PrimaryButtonText = Loc.T("연결", "Connect"), CloseButtonText = Loc.T("취소", "Cancel"),
            IsPrimaryButtonEnabled = fields.All(field => !field.Required),
        };
        foreach (var field in fields)
        {
            panel.Children.Add(field.Box);
            field.Box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
                fields.All(item => !item.Required || item.Box.Password.Length > 0);
        }
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            foreach (var field in fields) field.Apply(field.Box.Password);
            return true;
        }
        finally { foreach (var field in fields) field.Box.Password = ""; }
    }
}
