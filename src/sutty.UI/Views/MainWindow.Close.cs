using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using sutty.UI.Helpers;
using sutty.Core.Sftp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class MainWindow
{
    private bool _closePromptOpen;
    private bool _allowWindowClose;

    private async Task<bool> ConfirmWorkspacesCloseAsync(IReadOnlyList<SessionWorkspaceView> workspaces, bool closingWindow = false)
    {
        _closePromptOpen = true;
        foreach (var workspace in workspaces) workspace.FileTree.SuspendAutomaticEdits(true);
        var accepted = false;
        try
        {
            var affected = new List<string>();
            var multiJob = _multiSftpInProgress != 0 && !string.IsNullOrWhiteSpace(_lastMultiSftpQueueJobId)
                ? _sftpTransferQueue.Get(_lastMultiSftpQueueJobId)
                : null;
            foreach (var workspace in workspaces)
            {
                var files = workspace.FileTree;
                var count = files.Transfers.Count(transfer => transfer.IsActive);
                var info = workspace.SessionView.Session.Info;
                var targetId = !string.IsNullOrWhiteSpace(info.SavedHostId)
                    ? $"profile:{info.SavedHostId}"
                    : $"endpoint:{info.Username.Trim().ToLowerInvariant()}@{info.Host.Trim().ToLowerInvariant()}:{info.Port}";
                var hasMultiTransfer = multiJob?.Targets.Any(target => target.Id == targetId &&
                    target.State is SftpQueueTargetState.Pending or SftpQueueTargetState.Running) == true;
                if (count == 0 && !files.HasOpenEdits && !hasMultiTransfer) continue;
                var pending = await files.HasPendingEditsAsync();
                affected.Add($"{workspace.SessionView.Session.Info.Title}: " +
                    Loc.T($"전송 {count}개", $"{count} active transfer(s)") +
                    (hasMultiTransfer ? Loc.T(" · 다중 서버 전송 대상", " · active multi-host transfer target") : "") +
                    (files.HasOpenEdits ? Loc.T(" · 편집본 있음", " · editor copies open") : "") +
                    (pending ? Loc.T(" · 미반영 변경", " · changes pending") : ""));
            }
            if (closingWindow && _multiSftpInProgress != 0)
                affected.Add(Loc.T("다중 서버 전송 진행 중", "Multi-host transfer in progress"));
            if (affected.Count == 0) return accepted = true;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = closingWindow ? Loc.T("Sutty 종료", "Close Sutty") : Loc.T("서버 연결 종료", "Close server session"),
                Content = new TextBlock
                {
                    Text = string.Join("\n", affected) + "\n\n" +
                        Loc.T("진행 중인 전송을 중단합니다. 외부 편집기에서 저장하지 않은 내용은 감지할 수 없으므로 먼저 저장하세요. 편집본은 아래 폴더에 보관됩니다.\n",
                            "Active transfers will stop. Save in your external editor first; unsaved editor content cannot be detected. Working copies are retained in:\n") +
                        sutty.Core.Sftp.RemoteEditSession.DefaultStorageRoot,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                PrimaryButtonText = Loc.T("중단하고 닫기", "Stop and close"),
                CloseButtonText = Loc.T("계속 작업", "Keep working"),
                DefaultButton = ContentDialogButton.Close,
            };
            return accepted = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false; // A competing dialog must never turn a failed prompt into consent.
        }
        finally
        {
            if (!accepted)
                foreach (var workspace in workspaces) workspace.FileTree.SuspendAutomaticEdits(false);
            _closePromptOpen = false;
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose) return;
        args.Cancel = true;
        if (_closePromptOpen || _windowClosing) return;
        var workspaces = _sessionWorkspaces.Values.ToArray();
        if (!await ConfirmWorkspacesCloseAsync(workspaces, closingWindow: true)) return;
        _windowClosing = true;
        try { FlushWorkspaceSnapshot(); FlushRightPanelWidth(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Close settings flush failed: {error.GetType().Name}"); }
        try
        {
            var shutdown = workspaces.Select(DetachAndCloseSessionAsync)
                .Concat(GetOpenLocalTerminalViews().Select(view => ObserveCloseOperationAsync(view.CloseAsync())));
            await Task.WhenAll(shutdown).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception) { /* Connection disposal is best effort; durable checkpoints and editor copies are retained. */ }
        _allowWindowClose = true;
        Close();
    }

    private Task DetachAndCloseSessionAsync(SessionWorkspaceView workspace)
    {
        // Detach synchronously cancels edits/transfers and unsubscribes callbacks before its
        // first await. Start transport disconnect as well so a pending bind/tunnel operation
        // cannot prevent SSH/SFTP cleanup. Observe both even if the outer close times out.
        var detach = workspace.DetachAsync(userInitiated: true);
        var disconnect = _sessions.CloseAsync(workspace.SessionView.Session);
        return Task.WhenAll(ObserveCloseOperationAsync(detach), ObserveCloseOperationAsync(disconnect));
    }

    private static async Task ObserveCloseOperationAsync(Task operation)
    {
        try { await operation; }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Session cleanup failed: {error.GetType().Name}"); }
    }
}
