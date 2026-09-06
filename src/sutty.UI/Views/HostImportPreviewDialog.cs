using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>A shared review surface for existing importers and portable JSON definitions.</summary>
public static class HostImportPreviewDialog
{
    public static async Task<HostImportApplyResult?> ShowAsync(XamlRoot xamlRoot, HostProfileImportBatch batch)
    {
        try { return await ShowAsync(xamlRoot, DefinitionSharingService.Preview(batch)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            System.Diagnostics.Debug.WriteLine($"Import review failed: {error.GetType().Name}");
            await new ContentDialog
            {
                XamlRoot = xamlRoot, Title = Loc.T("가져오기 검토 오류", "Import review error"),
                Content = Loc.T("원본 필드, 최대 1,000개 항목 제한, 저장소 접근 권한을 확인한 뒤 다시 시도하세요.",
                    "Check the source fields, the 1,000-item limit, and storage permissions, then try again."),
                CloseButtonText = Loc.T("확인", "OK"),
            }.ShowAsync();
            return null;
        }
    }

    public static async Task<HostImportApplyResult?> ShowAsync(XamlRoot xamlRoot, DefinitionImportPreview preview)
    {
        var list = new StackPanel { Spacing = 12, MinWidth = 480, MaxWidth = 660 };
        list.Children.Add(Paragraph(Loc.T(
            "선택한 항목만 적용합니다. 새 항목도 기본 선택하지 않습니다. 갱신은 아래 기존 대상을 바꿉니다. 가져온 뒤 호스트를 열어 이 PC의 키·계정을 연결하세요. 명령은 실행되지 않습니다.",
            "Only selected items are applied; nothing is selected initially. Update replaces the existing target shown below. Open imported hosts to bind this PC's key/account. Commands are never executed.")));
        var allKinds = preview.Hosts.Select(row => row.Kind).Concat(preview.Commands.Select(row => row.Kind)).ToArray();
        list.Children.Add(Paragraph(Loc.T(
            $"추가 {allKinds.Count(kind => kind == ImportChangeKind.Add)} · 변경 {allKinds.Count(kind => kind == ImportChangeKind.Change)} · 중복 {allKinds.Count(kind => kind == ImportChangeKind.Duplicate)} · 오류 {allKinds.Count(kind => kind == ImportChangeKind.Invalid)}",
            $"Add {allKinds.Count(kind => kind == ImportChangeKind.Add)} · Change {allKinds.Count(kind => kind == ImportChangeKind.Change)} · Duplicate {allKinds.Count(kind => kind == ImportChangeKind.Duplicate)} · Invalid {allKinds.Count(kind => kind == ImportChangeKind.Invalid)}")));
        foreach (var warning in preview.Warnings)
            list.Children.Add(Paragraph($"⚠ {warning}"));

        var dialog = new ContentDialog
        {
            Title = Loc.T("가져오기 미리보기", "Review import"), XamlRoot = xamlRoot,
            Content = new ScrollViewer { Content = list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = Loc.T("선택 적용", "Apply selected"), CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
        };
        void RefreshApply() => dialog.IsPrimaryButtonEnabled = preview.Hosts.Any(row => row.Choice != ImportChoice.Skip) ||
            preview.Commands.Any(row => row.Choice != ImportChoice.Skip);
        var additions = new List<ComboBox>();
        var selectAdds = new Button { Content = Loc.T("새 항목 선택", "Select additions") };
        AutomationProperties.SetName(selectAdds, Loc.T("미리보기의 새 항목 선택", "Select new items in preview"));
        selectAdds.Click += (_, _) => { foreach (var combo in additions) combo.SelectedIndex = 1; };
        list.Children.Add(selectAdds);
        foreach (var row in preview.Hosts)
        {
            var draft = row.Draft;
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(Paragraph($"{Label(row.Kind)} · {draft.DisplayName}", true));
            content.Children.Add(Paragraph($"{draft.Username}@{draft.Host}:{draft.Port} · {draft.AuthMethod}"));
            content.Children.Add(Paragraph($"{draft.GroupName} · {draft.Environment} · {string.Join(", ", draft.Tags ?? [])}"));
            if (!string.IsNullOrWhiteSpace(draft.AuthenticationAlias))
                content.Children.Add(Paragraph(Loc.T($"인증 별칭: {draft.AuthenticationAlias}", $"Authentication alias: {draft.AuthenticationAlias}")));
            if (row.Existing is { } existing)
                content.Children.Add(Paragraph(Loc.T(
                    $"갱신 대상: {existing.DisplayName} · {existing.Username}@{existing.Host}:{existing.Port}",
                    $"Update target: {existing.DisplayName} · {existing.Username}@{existing.Host}:{existing.Port}")));
            if (row.Detail.Length > 0) content.Children.Add(Paragraph(row.Detail));
            var route = draft.Route;
            var details = route is null ? Loc.T("경로 오류", "Invalid route") :
                $"{Loc.T("경로", "Route")}: {route.Type} {route.Username}@{route.Host}:{route.Port}\n" +
                string.Join("\n", (draft.Tunnels ?? []).Select(tunnel =>
                    $"{tunnel.Type} · {tunnel.BindHost}:{tunnel.BindPort} → {tunnel.DestinationHost}:{tunnel.DestinationPort}"));
            content.Children.Add(new Expander
            {
                Header = Loc.T("경로 및 터널 검토", "Review route and tunnels"),
                Content = Paragraph(details), HorizontalAlignment = HorizontalAlignment.Stretch,
            });
            var choices = Choices(row.Kind, row.Existing is not null, draft.DisplayName,
                choice => { row.Choice = choice; RefreshApply(); });
            if (row.Kind == ImportChangeKind.Add) additions.Add(choices);
            content.Children.Add(choices);
            list.Children.Add(new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                BorderBrush = ThemeResources.Brush(list, "CardBorder"), Child = content });
        }
        if (preview.Commands.Count > 0)
            list.Children.Add(Paragraph(Loc.T(
                "명령 템플릿의 토큰·비밀번호를 직접 확인하세요. 가져오기는 내용을 저장할 뿐 실행하지 않습니다.",
                "Review command templates for embedded tokens/passwords. Import stores their text without running it.")));
        foreach (var row in preview.Commands)
        {
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(Paragraph($"{Label(row.Kind)} · {row.Name}", true));
            content.Children.Add(new TextBox { Text = row.CommandText, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 180 });
            var choices = Choices(row.Kind, row.Existing is not null, row.Name,
                choice => { row.Choice = choice; RefreshApply(); });
            if (row.Kind == ImportChangeKind.Add) additions.Add(choices);
            content.Children.Add(choices);
            list.Children.Add(content);
        }
        selectAdds.IsEnabled = additions.Count > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return DefinitionSharingService.Apply(preview);
    }

    private static ComboBox Choices(ImportChangeKind kind, bool existing, string name, Action<ImportChoice> changed)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        void Add(string label, ImportChoice value) => combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        Add(Loc.T("건너뛰기", "Skip"), ImportChoice.Skip);
        if (kind != ImportChangeKind.Invalid)
        {
            if (kind == ImportChangeKind.Add) Add(Loc.T("새 호스트/명령 추가", "Add new definition"), ImportChoice.Add);
            Add(Loc.T("복제로 별도 저장", "Save as a copy"), ImportChoice.Copy);
            if (existing) Add(Loc.T("기존 항목 갱신", "Update existing"), ImportChoice.Update);
        }
        combo.SelectedIndex = 0;
        combo.IsEnabled = kind != ImportChangeKind.Invalid;
        AutomationProperties.SetName(combo, Loc.T($"{name} 가져오기 선택", $"Import choice for {name}"));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: ImportChoice value }) changed(value);
        };
        return combo;
    }

    private static string Label(ImportChangeKind kind) => kind switch
    {
        ImportChangeKind.Add => Loc.T("추가", "Add"), ImportChangeKind.Change => Loc.T("변경", "Change"),
        ImportChangeKind.Duplicate => Loc.T("중복", "Duplicate"), _ => Loc.T("오류/미지원", "Invalid/unsupported"),
    };

    private static TextBlock Paragraph(string text, bool bold = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };
}
