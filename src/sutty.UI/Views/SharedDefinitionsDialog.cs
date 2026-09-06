using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

/// <summary>Explicit local selection → JSON preview → file picker. No shared accounts or background sync.</summary>
public static class SharedDefinitionsDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot, IntPtr windowHandle)
    {
        try
        {
            if (windowHandle == IntPtr.Zero)
                throw new InvalidOperationException("The file picker requires an owner window.");
            var hosts = HostProfileStore.GetAll(limit: 1_000);
            var commands = CommandStore.GetAll();
            var hostSelections = new List<(HostProfile Host, CheckBox Check)>();
            var commandSelections = new List<(CommandTemplate Command, CheckBox Check)>();
            var content = new StackPanel { Spacing = 12, MinWidth = 480, MaxWidth = 660 };
            content.Children.Add(Paragraph(Loc.T(
                "호스트·그룹·태그·경로·터널·선택 명령을 JSON 한 개로 공유합니다. 비밀번호·키 경로·자격증명 ID·호스트 키 신뢰·기록은 제외됩니다. ProxyCommand는 내보내지 않으며 해당 호스트 가져오기는 차단됩니다.",
                "Share hosts, groups, tags, routes, tunnels, and selected commands in one JSON file. Passwords, key paths, credential IDs, host-key trust, and history are excluded. ProxyCommand text is omitted and those hosts are blocked on import.")));
            content.Children.Add(Paragraph(Loc.T(
                "호스트명·사용자명·서버 경로도 민감할 수 있습니다. 명령에 직접 넣은 토큰까지 자동 제거하지 않습니다. 공유 전 내용을 검토하세요. 각 PC에서 호스트의 인증 별칭에 맞는 키·계정을 직접 연결합니다.",
                "Hostnames, usernames, and server paths may also be sensitive. Tokens embedded in command text are not automatically removed. Review the file before sharing. On each PC, bind your own key/account for each host's authentication alias.")));
            var dialog = new ContentDialog
            {
                Title = Loc.T("호스트·명령 공유", "Share hosts and commands"), XamlRoot = xamlRoot,
                Content = new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = Loc.T("선택 항목 미리보기", "Preview selection"),
                SecondaryButtonText = Loc.T("JSON 가져오기", "Import JSON"),
                CloseButtonText = Loc.T("닫기", "Close"), DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
            };
            void RefreshSelection() => dialog.IsPrimaryButtonEnabled =
                hostSelections.Any(row => row.Check.IsChecked == true) || commandSelections.Any(row => row.Check.IsChecked == true);
            content.Children.Add(Paragraph(Loc.T("공유할 호스트 선택", "Choose hosts to share"), true));
            foreach (var host in hosts)
            {
                var check = new CheckBox
                {
                    Content = $"{host.DisplayName} · {host.Username}@{host.Host}:{host.Port}",
                    IsChecked = false,
                };
                AutomationProperties.SetName(check, Loc.T($"공유 호스트 선택: {host.DisplayName}", $"Share host: {host.DisplayName}"));
                check.Checked += (_, _) => RefreshSelection();
                check.Unchecked += (_, _) => RefreshSelection();
                hostSelections.Add((host, check));
                var row = new StackPanel { Spacing = 3 };
                row.Children.Add(check);
                row.Children.Add(Paragraph($"{host.GroupName} · {string.Join(", ", host.Tags)} · {host.Route.Type} · {Loc.T("터널", "Tunnels")}: {host.Tunnels.Count}"));
                if (!string.IsNullOrWhiteSpace(host.AuthenticationAlias))
                    row.Children.Add(Paragraph(Loc.T($"인증 별칭: {host.AuthenticationAlias}", $"Authentication alias: {host.AuthenticationAlias}")));
                content.Children.Add(row);
            }
            if (hosts.Count == 0) content.Children.Add(Paragraph(Loc.T("저장 호스트가 없습니다.", "No saved hosts yet.")));
            content.Children.Add(Paragraph(Loc.T("공유할 명령 템플릿 선택", "Choose command templates to share"), true));
            foreach (var command in commands)
            {
                var check = new CheckBox { Content = command.Name, IsChecked = false };
                AutomationProperties.SetName(check, Loc.T($"공유 명령 선택: {command.Name}", $"Share command: {command.Name}"));
                check.Checked += (_, _) => RefreshSelection();
                check.Unchecked += (_, _) => RefreshSelection();
                commandSelections.Add((command, check));
                content.Children.Add(check);
                content.Children.Add(new TextBox { Text = command.CommandText, IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 120 });
            }
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                InitializeWithWindow.Initialize(picker, windowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is null || string.IsNullOrWhiteSpace(file.Path)) return;
                var preview = DefinitionSharingService.PreviewFile(file.Path);
                var applied = await HostImportPreviewDialog.ShowAsync(xamlRoot, preview);
                if (applied is not null)
                    await MessageAsync(xamlRoot, Loc.T("가져오기 결과", "Import result"), Loc.T(
                        $"추가 {applied.Added} · 갱신 {applied.Updated} · 건너뜀 {applied.Skipped} · 실패 {applied.Failed}\n호스트를 열어 이 PC의 키·계정을 설정하세요. 실패 항목은 저장소와 원본을 확인한 뒤 다시 미리 보세요.",
                        $"Added {applied.Added} · Updated {applied.Updated} · Skipped {applied.Skipped} · Failed {applied.Failed}\nOpen hosts to configure this PC's key/account. For failed items, check storage and the source, then preview again."));
                return;
            }
            if (result != ContentDialogResult.Primary) return;
            var json = DefinitionSharingService.Export(
                hostSelections.Where(row => row.Check.IsChecked == true).Select(row => row.Host),
                commandSelections.Where(row => row.Check.IsChecked == true).Select(row => row.Command));
            var review = new StackPanel { Spacing = 10, MinWidth = 480, MaxWidth = 660 };
            review.Children.Add(Paragraph(Loc.T(
                "이 내용 그대로 저장합니다. 서버 정보와 명령에 비밀정보가 없는지 확인하세요. 외부 공유는 사용자가 선택한 경로로 직접 전달합니다.",
                "This exact content will be saved. Review server details and commands for sensitive information. You choose how to send the file to others.")));
            var jsonText = new TextBox { Text = json, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                MinHeight = 240, MaxHeight = 430, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
            AutomationProperties.SetName(jsonText, Loc.T("공유 JSON 전체 미리보기", "Complete shared JSON preview"));
            review.Children.Add(jsonText);
            var previewDialog = new ContentDialog
            {
                Title = Loc.T("내보내기 내용 확인", "Review export content"), XamlRoot = xamlRoot, Content = review,
                PrimaryButtonText = Loc.T("JSON 저장", "Save JSON"), CloseButtonText = Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await previewDialog.ShowAsync() != ContentDialogResult.Primary) return;
            var savePicker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "sutty-definitions" };
            savePicker.FileTypeChoices.Add("Sutty JSON", new List<string> { ".json" });
            InitializeWithWindow.Initialize(savePicker, windowHandle);
            var selected = await savePicker.PickSaveFileAsync();
            if (selected is null || string.IsNullOrWhiteSpace(selected.Path)) return;
            await DefinitionSharingService.SaveFileAsync(selected.Path, json);
            await MessageAsync(xamlRoot, Loc.T("공유 파일 저장됨", "Sharing file saved"), selected.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or Microsoft.Data.Sqlite.SqliteException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Definition sharing failed: {error.GetType().Name}");
            await MessageAsync(xamlRoot, Loc.T("공유 작업을 완료하지 못했습니다", "Sharing could not be completed"), Loc.T(
                "파일 권한·저장소를 확인하세요. 가져오기는 schemaVersion 1의 올바른 JSON, 최대 4 MiB/1,000개 정의를 지원합니다. 원본은 변경하지 않았습니다.",
                "Check file permissions and storage. Import accepts valid schemaVersion 1 JSON, up to 4 MiB and 1,000 definitions. The import source was not changed."));
        }
    }

    private static async Task MessageAsync(XamlRoot root, string title, string text)
    {
        await new ContentDialog { Title = title, Content = Paragraph(text), CloseButtonText = Loc.T("확인", "OK"), XamlRoot = root }.ShowAsync();
    }

    private static TextBlock Paragraph(string text, bool bold = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };
}
