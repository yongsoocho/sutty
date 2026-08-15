using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Command;
using sutty.Core.Sftp;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views
{
    public sealed record MultiSftpUploadRequest(string LocalPath, string RemoteDirectory);
    public sealed record MultiSftpDownloadRequest(string RemotePath, string LocalDirectory);

    /// <summary>
    /// Multi command 오른쪽 패널.
    /// 위: 브로드캐스트 입력(멀티라인, Enter=실행), 아래: 저장된 playbook 목록.
    /// 실행되는 모든 명령은 BroadcastRequested로 나가 체크된 모든 세션에 전송된다.
    /// </summary>
    public sealed partial class MultiCommandPanel : UserControl
    {
        public void RefreshLanguage()
        {
            Bindings.Update();
            foreach (var target in SftpTargets)
                target.RefreshLanguage();
        }

        public ObservableCollection<CommandItemVm> Items { get; } = [];
        public ObservableCollection<MultiSftpTargetVm> SftpTargets { get; } = [];
        public IntPtr OwnerWindowHandle { get; set; }

        /// <summary>체크된 모든 세션에서 이 명령을 실행해 달라는 신호.</summary>
        public event EventHandler<string>? BroadcastRequested;
        public event EventHandler<MultiSftpUploadRequest>? SftpUploadRequested;
        public event EventHandler<MultiSftpDownloadRequest>? SftpDownloadRequested;
        public event EventHandler? SftpRetryFailedRequested;
        public event EventHandler? SftpResumePendingRequested;

        public MultiCommandPanel()
        {
            InitializeComponent();

            var settings = SettingsService.Current;
            BroadcastBox.FontFamily = new FontFamily(settings.TerminalFontFamily + ", Consolas");

            foreach (var template in CommandStore.GetAll())
                Items.Add(new CommandItemVm(template));
            EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetBroadcastRunning(bool isRunning, string? status = null)
        {
            BroadcastBox.IsEnabled = !isRunning;
            RunBroadcastButton.IsEnabled = !isRunning;
            CommandsList.IsEnabled = !isRunning;
            BroadcastProgress.IsActive = isRunning;
            BroadcastProgress.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            BroadcastStatusText.Text = status ?? (isRunning
                ? Helpers.Loc.T("세션별로 실행 중…", "Running on each session…")
                : "");
            BroadcastStatusPanel.Visibility = string.IsNullOrWhiteSpace(BroadcastStatusText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public void ShowBroadcastStatus(string status)
        {
            BroadcastStatusText.Text = status;
            BroadcastStatusPanel.Visibility = Visibility.Visible;
        }

        public void SetSftpRunning(bool isRunning, string? status = null)
        {
            UploadFilesButton.IsEnabled = !isRunning;
            UploadFolderButton.IsEnabled = !isRunning;
            DownloadPathButton.IsEnabled = !isRunning;
            SftpRemoteDirectoryBox.IsEnabled = !isRunning;
            RetryFailedButton.IsEnabled = !isRunning;
            ResumePendingButton.IsEnabled = !isRunning;
            if (!string.IsNullOrWhiteSpace(status))
                SftpStatusText.Text = status;
        }

        public void ResetSftpTargets()
        {
            SftpTargets.Clear();
            RetryFailedButton.Visibility = Visibility.Collapsed;
        }

        public void UpdateSftpTarget(MultiSftpTargetStatus status)
        {
            var item = SftpTargets.FirstOrDefault(candidate => candidate.Id == status.Target.Id);
            if (item is null)
            {
                item = new MultiSftpTargetVm(status.Target.Id, status.Target.DisplayName);
                SftpTargets.Add(item);
            }
            item.Update(status);
        }

        public void CompleteSftpBatch(MultiSftpBatchResult result)
        {
            var succeeded = result.Targets.Count(item => item.State == MultiSftpTargetState.Succeeded);
            var failed = result.Failed.Count;
            SftpStatusText.Text = Helpers.Loc.T(
                $"서버별 전송 완료 · 성공 {succeeded} · 실패 {failed}",
                $"Per-server transfer complete · {succeeded} succeeded · {failed} failed");
            RetryFailedButton.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowSftpStatus(string korean, string english)
            => SftpStatusText.Text = Helpers.Loc.T(korean, english);

        public void SetRecoveredJobCount(int count)
        {
            ResumePendingButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResumePendingButton.Content = Helpers.Loc.T(
                $"복원된 전송 {count}개 재개",
                $"Resume {count} restored transfer(s)");
        }

        // WinUI TextBox는 줄구분자로 '\r'을 쓴다
        private static string NormalizeNewlines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n');

        // ── 브로드캐스트 입력 (SessionView 입력줄과 같은 규칙) ──

        private void BroadcastBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            var shiftDown = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shiftDown) return;

            var caret = Math.Min(BroadcastBox.SelectionStart, BroadcastBox.Text.Length);
            var currentLine = NormalizeNewlines(BroadcastBox.Text[..caret]).Split('\n')[^1].TrimEnd();
            if (currentLine.EndsWith('\\') || currentLine.EndsWith('`')) return;

            e.Handled = true;
            RunBroadcastFromBox();
        }

        private void RunBroadcast_Click(object sender, RoutedEventArgs e)
            => RunBroadcastFromBox();

        private void RunBroadcastFromBox()
        {
            var command = NormalizeNewlines(BroadcastBox.Text).Trim();
            if (command.Length == 0) return;

            BroadcastBox.Text = "";
            BroadcastRequested?.Invoke(this, command);
        }

        // ── 저장된 커맨드 실행 ──

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CommandItemVm vm) return;

            if (vm.ParamNumbers.Count > 0)
            {
                vm.PrepareParams();
                vm.ShowParams = !vm.ShowParams;
            }
            else
            {
                Execute(vm);
            }
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CommandItemVm vm)
                Execute(vm);
        }

        private void Execute(CommandItemVm vm)
        {
            var command = vm.BuildCommand();
            CommandStore.IncrementUsage(vm.Template.Id);
            vm.ShowParams = false;
            BroadcastRequested?.Invoke(this, command);
        }

        private async void UploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetRemoteDirectory(out var remoteDirectory))
                return;
            if (OwnerWindowHandle == IntPtr.Zero)
            {
                ShowSftpStatus("파일 선택 창을 열 수 없습니다.", "The file picker is unavailable.");
                return;
            }

            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is not null && !string.IsNullOrWhiteSpace(file.Path))
                    SftpUploadRequested?.Invoke(this, new MultiSftpUploadRequest(file.Path, remoteDirectory));
            }
            catch (Exception error)
            {
                ShowSftpStatus(
                    $"파일 선택 실패: {error.Message}",
                    $"Could not select a file: {error.Message}");
            }
        }

        private async void UploadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetRemoteDirectory(out var remoteDirectory))
                return;
            if (OwnerWindowHandle == IntPtr.Zero)
            {
                ShowSftpStatus("폴더 선택 창을 열 수 없습니다.", "The folder picker is unavailable.");
                return;
            }

            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
                    SftpUploadRequested?.Invoke(this, new MultiSftpUploadRequest(folder.Path, remoteDirectory));
            }
            catch (Exception error)
            {
                ShowSftpStatus(
                    $"폴더 선택 실패: {error.Message}",
                    $"Could not select a folder: {error.Message}");
            }
        }

        private async void DownloadPath_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetRemoteDirectory(out var remotePath))
                return;
            if (OwnerWindowHandle == IntPtr.Zero)
            {
                ShowSftpStatus("폴더 선택 창을 열 수 없습니다.", "The folder picker is unavailable.");
                return;
            }

            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
                    SftpDownloadRequested?.Invoke(
                        this,
                        new MultiSftpDownloadRequest(remotePath, folder.Path));
            }
            catch (Exception error)
            {
                ShowSftpStatus(
                    $"다운로드 폴더 선택 실패: {error.Message}",
                    $"Could not select the download folder: {error.Message}");
            }
        }

        private void RetryFailed_Click(object sender, RoutedEventArgs e)
            => SftpRetryFailedRequested?.Invoke(this, EventArgs.Empty);

        private void ResumePending_Click(object sender, RoutedEventArgs e)
            => SftpResumePendingRequested?.Invoke(this, EventArgs.Empty);

        private bool TryGetRemoteDirectory(out string remoteDirectory)
        {
            var value = SftpRemoteDirectoryBox.Text.Trim();
            if (!value.StartsWith("/", StringComparison.Ordinal))
            {
                remoteDirectory = "";
                ShowSftpStatus(
                    "원격 폴더는 /로 시작하는 절대 경로여야 합니다.",
                    "The remote folder must be an absolute path beginning with /. ");
                return false;
            }

            remoteDirectory = RemotePath.Normalize(value);
            return true;
        }
    }
}
