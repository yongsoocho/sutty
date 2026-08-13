using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Security;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using sutty.Core.Terminal;
using sutty.Setting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

namespace sutty.UI.Views
{
    public sealed partial class MainWindow : Window
    {
        public sutty.UI.ViewModels.MainViewModel ViewModel { get; }

        /// <summary>동시에 열 수 있는 로컬/SSH 작업 탭 최대 개수.</summary>
        private const int MaxSessions = 16;
        private static readonly TimeSpan BroadcastCommandTimeout = TimeSpan.FromSeconds(60);

        private readonly SessionManager _sessions = new();
        private readonly SemaphoreSlim _hostKeyPromptGate = new(1, 1);
        private Window? _settingWindow;
        private FileTreePanel? _fileTreePanel;
        private string _appIconPath = "";
        private bool _isMultiView;
        private int _broadcastInProgress;

        public MainWindow()
        {
            ViewModel = new sutty.UI.ViewModels.MainViewModel();
            InitializeComponent();


            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            System.IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId =
                Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow =
                AppWindow.GetFromWindowId(windowId);
            _appIconPath = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "Assets", "sutty.ico");
            appWindow.SetIcon(_appIconPath);

            // sutty를 닫으면 설정 창도 같이 닫고, 저장 안 된 패널 폭이 있으면 마저 저장
            Closed += (_, _) =>
            {
                FlushRightPanelWidth();
                _settingWindow?.Close();
                foreach (var localView in GetOpenLocalTerminalViews())
                    _ = localView.CloseAsync();
                LocalCredentialVault.Default.Dispose();
            };

            ApplyTheme(SettingsService.Current.Theme);

            // 저장된 창 크기 복원 + 리사이즈 시 디바운스 저장
            Helpers.WindowSizePersistence.Attach(this,
                s => new SizeInt32(s.MainWindowWidth, s.MainWindowHeight),
                (s, size) => { s.MainWindowWidth = size.Width; s.MainWindowHeight = size.Height; });

            RestoreRightPanelWidth();

            LeftNav.SelectedItem = LeftNav.MenuItems[0];
        }

        // ── 오른쪽 패널 폭: 스플리터 드래그 → 디바운스 저장 ──

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panelWidthSaveTimer;

        private void RestoreRightPanelWidth()
        {
            var saved = SettingsService.Current.RightPanelWidth;
            if (saved > 0)
                RightPanelColumn.Width = new GridLength(Math.Clamp(saved, 300, 800));

            _panelWidthSaveTimer = DispatcherQueue.CreateTimer();
            _panelWidthSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _panelWidthSaveTimer.IsRepeating = false;
            _panelWidthSaveTimer.Tick += (_, _) =>
            {
                SettingsService.Current.RightPanelWidth = (int)RightPanelColumn.ActualWidth;
                SettingsService.Save();
            };

            RightPanelHost.SizeChanged += (_, _) =>
            {
                _panelWidthSaveTimer.Stop();
                _panelWidthSaveTimer.Start(); // 드래그 중 이벤트 폭주 → 마지막만 저장
            };
        }

        // 창을 닫는 순간 디바운스 대기 중이던 폭을 놓치지 않게 즉시 저장
        private void FlushRightPanelWidth()
        {
            if (_panelWidthSaveTimer is { IsRunning: true })
            {
                _panelWidthSaveTimer.Stop();
                SettingsService.Current.RightPanelWidth = (int)RightPanelColumn.ActualWidth;
                SettingsService.Save();
            }
        }

        // ── 테마 (전환 UI는 Setting > Appearance에 있음) ──

        private void ApplyTheme(string theme)
        {
            Helpers.ThemeManager.Apply(theme, Root);
            var preset = Helpers.ThemeManager.Find(theme);
            ApplyTitleBarColors(this, preset);

            // 설정 창이 열려 있으면 같이 바꿔 준다
            if (_settingWindow?.Content is FrameworkElement settingRoot)
            {
                settingRoot.RequestedTheme = Root.RequestedTheme;
                ApplyTitleBarColors(_settingWindow, preset);
            }
        }

        private static void ApplyTitleBarColors(Window window, Helpers.ThemePreset preset)
        {
            var titleBar = window.AppWindow.TitleBar;
            var appBackground = ParseColor(preset.Colors["AppBg"]);
            var foreground = ParseColor(preset.Colors["TextPrimary"]);
            var inactiveForeground = ParseColor(preset.Colors["TextFaint"]);
            var hoverBackground = ParseColor(preset.Colors["CardBgHover"]);
            var pressedBackground = ParseColor(preset.Colors["PillBg"]);

            titleBar.BackgroundColor = appBackground;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = appBackground;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = appBackground;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = appBackground;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        }

        private static Windows.UI.Color ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(
                255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }

        // ── 왼쪽 네비게이션 ──

        private void LeftNav_ItemInvoked(
            NavigationView sender,
            NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is "Setting")
            {
                OpenSettingWindow();
            }
        }

        private void LeftNav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                RightPanel.Content = item.Tag switch
                {
                    "Home" => CreateHomePanel(),
                    "Search" => CreateHostListPanel(),
                    "Folder" => CreateFileTreePanel(),
                    "Command" => CreateCommandPanel(),
                    "Multi" => CreateMultiPanel(),
                    "Logs" => new ConnectionLogPanel(),
                    _ => null
                };

                // Multi에서는 메인 영역이 4×4 세션 그리드로 바뀐다
                _isMultiView = item.Tag as string == "Multi";
                MultiGrid.Visibility = _isMultiView ? Visibility.Visible : Visibility.Collapsed;
                UpdateSessionArea();
            }
        }

        private HomePanel CreateHomePanel()
        {
            var panel = new HomePanel
            {
                OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this)
            };
            panel.ConnectRequested += async (_, info) => await OpenSessionTabAsync(info);
            return panel;
        }

        private HostListPanel CreateHostListPanel()
        {
            var panel = new HostListPanel();
            panel.ConnectRequested += async (_, host) => await OpenHistoryDraftAsync(host);
            return panel;
        }

        private async Task OpenHistoryDraftAsync(ViewModels.HostInfoModel host)
        {
            var alias = host.Alias;
            var hostname = host.Hostname;
            var username = host.Username;
            var port = host.Port;
            var authMethodName = host.AuthMethod;
            var privateKeyPath = host.PrivateKeyPath;
            var tags = host.Tags;
            var profileId = host.ProfileId;
            var credentialId = host.CredentialId;
            var groupName = host.GroupName;
            var environment = host.Environment;
            var favorite = host.IsPinned;
            CredentialSecret? credential = null;

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                try
                {
                    if (sutty.Command.HostProfileStore.GetById(profileId) is { } profile)
                    {
                        alias = profile.DisplayName;
                        hostname = profile.Host;
                        username = profile.Username;
                        port = profile.Port;
                        authMethodName = profile.AuthMethod;
                        privateKeyPath = profile.PrivateKeyPath;
                        tags = profile.Tags;
                        credentialId = profile.CredentialId;
                        groupName = profile.GroupName;
                        environment = profile.Environment;
                        favorite = profile.IsFavorite;
                    }

                    if (!string.IsNullOrWhiteSpace(credentialId))
                        LocalCredentialVault.Default.TryRead(credentialId, out credential);
                }
                catch (Exception error) when (error is System.IO.IOException or
                                              UnauthorizedAccessException or
                                              System.Security.Cryptography.CryptographicException or
                                              System.ComponentModel.Win32Exception or
                                              Microsoft.Data.Sqlite.SqliteException or
                                              ArgumentException)
                {
                    Debug.WriteLine($"Saved credential load failed: {error.GetType().Name}");
                    var warning = new ContentDialog
                    {
                        Title = Helpers.Loc.T("저장 자격증명 확인 필요", "Saved credential needs attention"),
                        Content = Helpers.Loc.T(
                            "저장된 자격증명을 읽지 못했습니다. 호스트 정보는 불러왔으며 비밀값을 다시 입력할 수 있습니다.",
                            "The saved credential could not be read. The host details were loaded; enter the secret again."),
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot,
                    };
                    await warning.ShowAsync();
                }
            }

            var authMethod = Enum.TryParse<SshAuthMethod>(authMethodName, true, out var parsed) &&
                             parsed is SshAuthMethod.Password or SshAuthMethod.PublicKey
                ? parsed
                : SshAuthMethod.Password;

            var draft = new SshConnectionInfo
            {
                Host = hostname,
                Port = port is >= 1 and <= 65535 ? port : 22,
                DisplayName = alias,
                Username = username,
                AuthMethod = authMethod,
                PrivateKeyPath = authMethod == SshAuthMethod.PublicKey ? privateKeyPath : "",
                Password = authMethod == SshAuthMethod.Password ? credential?.Password ?? "" : "",
                Passphrase = authMethod == SshAuthMethod.PublicKey
                    ? credential?.PrivateKeyPassphrase ?? ""
                    : "",
                Tags = [.. tags],
                SavedHostId = profileId,
                SaveProfile = !string.IsNullOrWhiteSpace(profileId),
                RememberCredential = credential is not null,
                CredentialId = credentialId,
                GroupName = groupName,
                Environment = environment,
                IsFavorite = favorite,
            };

            // History cards execute a connection instead of returning to the editor.
            // Secrets never live in SQLite, so request only a missing password in place.
            draft.SaveProfile = false;
            if (authMethod == SshAuthMethod.Password &&
                string.IsNullOrEmpty(draft.Password) &&
                !await PromptForHistoryPasswordAsync(draft))
            {
                return;
            }

            await OpenSessionTabAsync(draft);
        }

        private async Task<bool> PromptForHistoryPasswordAsync(SshConnectionInfo draft)
        {
            var passwordBox = new PasswordBox
            {
                Header = Helpers.Loc.T("비밀번호", "Password"),
                PlaceholderText = Helpers.Loc.T("SSH 비밀번호 입력", "Enter SSH password"),
                MinWidth = 320,
            };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = $"{draft.Username}@{draft.Host}:{draft.Port}",
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(passwordBox);

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("접속 기록에서 연결", "Connect from history"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("연결", "Connect"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot,
            };
            passwordBox.PasswordChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = passwordBox.Password.Length > 0;
            dialog.Opened += (_, _) => passwordBox.Focus(FocusState.Programmatic);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            draft.Password = passwordBox.Password;
            return draft.Password.Length > 0;
        }

        private FileTreePanel CreateFileTreePanel()
        {
            if (_fileTreePanel is null)
            {
                _fileTreePanel = new FileTreePanel
                {
                    OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this),
                };
                _fileTreePanel.OpenTerminalHereRequested += FileTree_OpenTerminalHereRequested;
            }

            var panel = _fileTreePanel;
            panel.OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            panel.RefreshLanguage();
            // Reconcile the cached panel immediately so stale nodes and transfers from a
            // previously active server are invalidated before the control is reattached.
            _ = LoadFileTreeForActiveSessionAsync(panel);
            // The first call can happen while the cached control is still unloaded. Run a
            // second pass after the selection change attaches it so cwd navigation is applied.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(panel, RightPanel.Content))
                    _ = LoadFileTreeForActiveSessionAsync(panel);
            });
            return panel;
        }

        private async void FileTree_OpenTerminalHereRequested(object? sender, string remotePath)
        {
            var view = ActiveSessionView;
            if (view is null)
            {
                await ShowOpenTerminalFailedAsync();
                return;
            }

            var allowExistingInput = false;
            if (view.HasOpenInteractiveTerminal)
            {
                allowExistingInput = await ConfirmTerminalInputAsync(remotePath);
                if (!allowExistingInput)
                    return;
            }

            if (await view.OpenDirectoryInTerminalAsync(remotePath, allowExistingInput))
                return;

            // The PTY may have opened while the remote path was being validated. Never
            // inject into that newly-existing foreground program without a fresh prompt.
            if (!allowExistingInput && view.HasOpenInteractiveTerminal)
            {
                if (!await ConfirmTerminalInputAsync(remotePath))
                    return;
                if (await view.OpenDirectoryInTerminalAsync(remotePath, true))
                    return;
            }

            await ShowOpenTerminalFailedAsync();
        }

        private async Task<bool> ConfirmTerminalInputAsync(string remotePath)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "터미널에서 이미 프로그램이 실행 중일 수 있습니다. 아래 명령을 현재 PTY 입력으로 보내시겠습니까?",
                    "A program may already be running in the terminal. Send this command to the current PTY input?"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"cd {remotePath}",
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Helpers.ThemeResources.Brush(Root, "AccentTeal"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("기존 터미널 입력 확인", "Confirm terminal input"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("cd 명령 보내기", "Send cd command"),
                CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task ShowOpenTerminalFailedAsync()
        {
            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("터미널에서 열 수 없음", "Could not open in terminal"),
                Content = Helpers.Loc.T(
                    "활성 SSH 세션과 원격 경로를 확인하세요.",
                    "Check the active SSH session and remote path."),
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async Task LoadFileTreeForActiveSessionAsync(FileTreePanel panel)
        {
            var view = ActiveSessionView;
            await panel.LoadAsync(view?.Session);
            if (view is null || !ReferenceEquals(view, ActiveSessionView) ||
                !ReferenceEquals(panel, RightPanel.Content))
                return;

            if (view.WorkingDirectory.StartsWith("/", StringComparison.Ordinal))
                await panel.NavigateToPathAsync(view.WorkingDirectory);
        }

        private void SessionView_WorkingDirectoryChanged(object? sender, string remotePath)
        {
            if (sender is not SessionView view || !ReferenceEquals(view, ActiveSessionView) ||
                RightPanel.Content is not FileTreePanel panel)
                return;

            _ = panel.NavigateToPathAsync(remotePath);
        }

        private MultiCommandPanel CreateMultiPanel()
        {
            var panel = new MultiCommandPanel();
            panel.BroadcastRequested += async (_, command) =>
                await BroadcastFromPanelAsync(panel, command);
            return panel;
        }

        private async Task BroadcastFromPanelAsync(MultiCommandPanel panel, string command)
        {
            if (Interlocked.CompareExchange(ref _broadcastInProgress, 1, 0) != 0)
            {
                panel.ShowBroadcastStatus(Helpers.Loc.T(
                    "이미 다른 브로드캐스트가 실행 중입니다.",
                    "Another broadcast is already running."));
                return;
            }

            var completed = false;
            var failed = false;
            panel.SetBroadcastRunning(true);
            try
            {
                completed = await BroadcastAsync(command);
            }
            catch (Exception error)
            {
                failed = true;
                Debug.WriteLine($"Broadcast batch failed: {error}");
            }
            finally
            {
                Interlocked.Exchange(ref _broadcastInProgress, 0);
                panel.SetBroadcastRunning(false, failed
                    ? Helpers.Loc.T("브로드캐스트 실행 실패", "Broadcast failed")
                    : completed
                        ? Helpers.Loc.T("브로드캐스트 완료", "Broadcast complete")
                        : null);
            }
        }

        // 체크된 모든 세션에 같은 명령을 병렬로 전송하고, 결과를 그리드 셀에 표시
        private async Task<bool> BroadcastAsync(string command)
        {
            var targets = MultiGrid.GetTargetSlots();
            if (targets.Count == 0)
            {
                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("대상 세션 없음", "No target sessions"),
                    Content = Helpers.Loc.T(
                        "체크된 세션이 없습니다. 그리드에서 대상 세션을 체크하세요.",
                        "No sessions are checked. Check target sessions in the grid."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await dialog.ShowAsync();
                return false;
            }

            var productionTargets = targets
                .Where(slot => slot.IsProduction)
                .ToList();
            if (productionTargets.Count > 0)
            {
                var targetNames = string.Join(", ", productionTargets.Select(slot => slot.Title));
                var warning = new StackPanel { Spacing = 10 };
                warning.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        $"PROD 태그가 있는 {productionTargets.Count}개 세션이 포함되어 있습니다.",
                        $"This broadcast includes {productionTargets.Count} session(s) tagged PROD."),
                    Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                warning.Children.Add(new TextBlock
                {
                    Text = targetNames,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });
                warning.Children.Add(new TextBlock
                {
                    Text = command,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var confirm = new ContentDialog
                {
                    Title = Helpers.Loc.T("PROD 브로드캐스트 확인", "Confirm PROD broadcast"),
                    Content = warning,
                    PrimaryButtonText = Helpers.Loc.T(
                        $"{targets.Count}개 세션에서 실행",
                        $"Run on {targets.Count} sessions"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                    return false;
            }

            await Task.WhenAll(targets.Select(slot =>
                RunBroadcastOnSlotAsync(slot, command)));
            return true;
        }

        private static async Task RunBroadcastOnSlotAsync(ViewModels.MultiSlotVm slot, string command)
        {
            slot.LastOutput = "…";
            slot.ResultText = Helpers.Loc.T("실행 중", "running");
            using var timeoutCancellation = new CancellationTokenSource(BroadcastCommandTimeout);
            try
            {
                var result = await slot.ExecuteAsync(command, timeoutCancellation.Token);
                var output = result.CombinedOutput;
                var timedOut = timeoutCancellation.IsCancellationRequested &&
                               string.Equals(
                                   result.ExitSignal,
                                   "CANCELLED",
                                   StringComparison.OrdinalIgnoreCase);
                slot.ResultText = timedOut
                    ? Helpers.Loc.T("시간 초과", "timed out")
                    : slot.LocalView is not null
                    ? Helpers.Loc.T("완료", "complete")
                    : result.ExitCode is int exitCode
                        ? $"exit {exitCode}"
                        : result.ExitSignal is { Length: > 0 } signal
                            ? signal.ToLowerInvariant()
                            : Helpers.Loc.T("실패", "failed");
                slot.LastOutput = string.IsNullOrWhiteSpace(output)
                    ? timedOut
                        ? Helpers.Loc.T(
                            "60초 안에 응답이 없어 중단했습니다.",
                            "Stopped after no response for 60 seconds.")
                        : slot.LocalView is not null
                        ? Helpers.Loc.T("(출력 없음)", "(no output)")
                        : result.Succeeded
                            ? Helpers.Loc.T("(출력 없음)", "(no output)")
                            : Helpers.Loc.T("(출력 없이 실패)", "(failed with no output)")
                    : output.Length > 400 ? output[..400] + "…" : output;
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                slot.ResultText = Helpers.Loc.T("시간 초과", "timed out");
                slot.LastOutput = Helpers.Loc.T(
                    "60초 안에 응답이 없어 중단했습니다.",
                    "Stopped after no response for 60 seconds.");
            }
            catch (Exception ex)
            {
                slot.ResultText = Helpers.Loc.T("실패", "failed");
                slot.LastOutput = $"error: {ex.Message}";
            }
        }

        private CommandPanel CreateCommandPanel()
        {
            var panel = new CommandPanel();
            // playbook 실행 → 현재 선택된 세션 탭의 터미널에 입력
            panel.RunRequested += async (_, command) =>
            {
                if ((TitleTabs.SelectedItem as TabViewItem)?.DataContext is not SessionView view)
                {
                    var dialog = new ContentDialog
                    {
                        Title = Helpers.Loc.T("활성 세션 없음", "No active session"),
                        Content = Helpers.Loc.T(
                            "명령을 실행할 세션이 없습니다. 먼저 서버에 연결하세요.",
                            "There is no session to run the command in. Connect to a server first."),
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot,
                    };
                    await dialog.ShowAsync();
                    return;
                }
                await view.RunExternalCommandAsync(command);
            };
            return panel;
        }

        // ── 세션 탭 ──

        /// <summary>현재 선택된 탭의 세션. 없으면 null.</summary>
        private SessionView? ActiveSessionView =>
            (TitleTabs.SelectedItem as TabViewItem)?.DataContext as SessionView;

        private ISshSession? ActiveSession => ActiveSessionView?.Session;

        private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var controlDown = IsKeyDown(Windows.System.VirtualKey.Control);
            var altDown = IsKeyDown(Windows.System.VirtualKey.Menu);
            var shortcutNumber = ShortcutNumber(e.Key);

            if (controlDown && !altDown)
            {
                if (shortcutNumber is int tabNumber)
                {
                    var index = tabNumber - 1;
                    if (index >= 0 && index < TitleTabs.TabItems.Count)
                    {
                        e.Handled = true;
                        if (_isMultiView && LeftNav.MenuItems.Count > 0)
                            LeftNav.SelectedItem = LeftNav.MenuItems[0];
                        TitleTabs.SelectedItem = TitleTabs.TabItems[index];
                        UpdateSessionArea();
                    }
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.T)
                {
                    e.Handled = true;
                    await OpenLocalTerminalTabAsync();
                    return;
                }

                // Win32 VK_OEM_COMMA. VirtualKey does not expose a named comma member.
                if ((int)e.Key == 0xBC)
                {
                    e.Handled = true;
                    OpenSettingWindow();
                }
                return;
            }

            if (!altDown || controlDown || shortcutNumber is not int navigationNumber)
                return;

            var items = LeftNav.MenuItems
                .Concat(LeftNav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .ToArray();
            var navigationIndex = navigationNumber - 1;
            if (navigationIndex < 0 || navigationIndex >= items.Length)
                return;

            e.Handled = true;
            var item = items[navigationIndex];
            if (item.Tag is "Setting")
                OpenSettingWindow();
            else
                LeftNav.SelectedItem = item;
        }

        private static int? ShortcutNumber(Windows.System.VirtualKey key)
        {
            var value = (int)key;
            if (value is >= 0x31 and <= 0x39)
                return value - 0x30;
            if (value is >= 0x61 and <= 0x69)
                return value - 0x60;
            return null;
        }

        private static bool IsKeyDown(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private async void TitleTabs_AddTabButtonClick(TabView sender, object args)
            => await OpenLocalTerminalTabAsync();

        private async Task OpenLocalTerminalTabAsync()
        {
            if (TitleTabs.TabItems.Count >= MaxSessions)
            {
                var limitDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("탭 개수 제한", "Tab limit"),
                    Content = Helpers.Loc.T(
                        $"탭은 최대 {MaxSessions}개까지 열 수 있습니다. 탭을 닫고 다시 시도하세요.",
                        $"You can open up to {MaxSessions} tabs. Close a tab and try again."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await limitDialog.ShowAsync();
                return;
            }

            // The + button is a direct request to show a terminal. Leave the Multi
            // dashboard first so the newly selected local tab is immediately visible.
            if (_isMultiView && LeftNav.MenuItems.Count > 0)
                LeftNav.SelectedItem = LeftNav.MenuItems[0];

            var view = new LocalTerminalView();
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Helpers.ThemeResources.Brush(Root, "StatusIdle"),
            };
            var metadata = new TextBlock
            {
                Text = Environment.MachineName,
                FontSize = 11,
                Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = "PowerShell",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(metadata);

            void UpdateStatusDot(TerminalState state) =>
                dot.Fill = Helpers.ThemeResources.Brush(Root, state switch
                {
                    TerminalState.Open => "StatusGreen",
                    TerminalState.Opening => "StatusAmber",
                    TerminalState.Failed => "StatusRed",
                    _ => "StatusIdle",
                });

            dot.ActualThemeChanged += (_, _) => UpdateStatusDot(view.Terminal.TerminalState);
            metadata.ActualThemeChanged += (_, _) =>
                metadata.Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint");
            view.Terminal.TerminalStateChanged += (_, state) =>
                DispatcherQueue.TryEnqueue(() => UpdateStatusDot(state));

            var tab = new TabViewItem
            {
                Header = header,
                IsClosable = true,
                DataContext = view,
            };

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
            UpdateSessionArea();
        }

        private async Task OpenSessionTabAsync(SshConnectionInfo info)
        {
            // 세션 개수 제한 (Multi 그리드 4×4와 맞춤)
            if (TitleTabs.TabItems.Count >= MaxSessions)
            {
                var limitDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("세션 개수 제한", "Session limit"),
                    Content = Helpers.Loc.T(
                        $"세션은 최대 {MaxSessions}개까지 열 수 있습니다. 탭을 닫고 다시 시도하세요.",
                        $"You can open up to {MaxSessions} sessions. Close a tab and try again."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await limitDialog.ShowAsync();
                return;
            }

            try
            {
                _ = HostEndpointIdentity.Create(info.Host, info.Port);
            }
            catch (ArgumentException ex)
            {
                ConnectionLogStore.Append(
                    Guid.Empty,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "Validation",
                    "SSH 연결 주소가 올바르지 않습니다.",
                    "The SSH connection address is invalid.",
                    ex.Message);
                var invalidHostDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("연결 주소 확인", "Check connection address"),
                    Content = Helpers.Loc.T(
                        $"호스트 또는 포트가 올바르지 않습니다.\n\n{ex.Message}",
                        $"The host or port is invalid.\n\n{ex.Message}"),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await invalidHostDialog.ShowAsync();
                return;
            }

            info.HostKeyPromptAsync ??= PromptUnknownHostKeyAsync;

            ISshSession session;
            try
            {
                session = _sessions.Create(info);
            }
            catch (RoutePolicyViolationException error)
            {
                ConnectionLogStore.Append(
                    Guid.Empty,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "Route policy",
                    "연결 경로 정책이 SSH 연결을 차단했습니다.",
                    "The connection route policy blocked this SSH connection.",
                    error.Message);
                var routeDialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("연결 경로 정책", "Connection route policy"),
                    Content = error.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await routeDialog.ShowAsync();
                return;
            }
            var savedProfile = await PersistSavedProfileAsync(info);
            var view = new SessionView(session);
            view.WorkingDirectoryChanged += SessionView_WorkingDirectoryChanged;

            // 리디자인 탭 헤더: [상태점] 세션이름 username
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Helpers.ThemeResources.Brush(Root, "StatusIdle"),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = info.Title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrWhiteSpace(info.Username))
            {
                var metadata = new TextBlock
                {
                    Text = info.Username,
                    FontSize = 11,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                metadata.ActualThemeChanged += (_, _) =>
                    metadata.Foreground = Helpers.ThemeResources.Brush(Root, "TextFaint");
                header.Children.Add(metadata);
            }

            void UpdateStatusDot(SessionState state) =>
                dot.Fill = Helpers.ThemeResources.Brush(Root, state switch
                {
                    SessionState.Connected => "StatusGreen",
                    SessionState.Connecting or SessionState.Disconnecting => "StatusAmber",
                    SessionState.Failed => "StatusRed",
                    _ => "StatusIdle",
                });

            // 상태와 테마가 바뀔 때 코드로 만든 탭 표시도 함께 갱신한다.
            dot.ActualThemeChanged += (_, _) => UpdateStatusDot(session.State);
            session.StateChanged += (_, state) =>
                DispatcherQueue.TryEnqueue(() => UpdateStatusDot(state));

            var tab = new TabViewItem
            {
                Header = header,
                IsClosable = true,
                DataContext = view,
            };

            TitleTabs.TabItems.Add(tab);
            TitleTabs.SelectedItem = tab;
            UpdateSessionArea();

            var timer = Stopwatch.StartNew();
            var outcome = "Failed";
            string? errorCode = "SSH.CONNECT.FAILED";
            try
            {
                await session.ConnectAsync();
                if (session.State == SessionState.Connected)
                {
                    outcome = "Success";
                    errorCode = null;
                    var connectedProfileId = savedProfile?.Id ?? info.SavedHostId;
                    if (!string.IsNullOrWhiteSpace(connectedProfileId))
                        sutty.Command.HostProfileStore.MarkConnected(connectedProfileId);
                }
            }
            catch (OperationCanceledException)
            {
                outcome = "Cancelled";
                errorCode = "SSH.CONNECT.CANCELLED";
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Unexpected connection failure: {error.GetType().Name}");
                ConnectionLogStore.Append(
                    session.Id,
                    info.Title,
                    $"{info.Host}:{info.Port}",
                    ConnectionLogSeverity.Error,
                    "UI connection flow",
                    "SSH 연결 처리 중 예상하지 못한 오류가 발생했습니다.",
                    "An unexpected error occurred in the SSH connection flow.",
                    error.ToString());
            }
            finally
            {
                timer.Stop();
                info.Password = "";
                info.Passphrase = "";
                try
                {
                    sutty.Command.HostHistoryStore.Append(
                        info.Title,
                        info.Host,
                        info.Username,
                        info.Port,
                        info.AuthMethod.ToString(),
                        info.AuthMethod == SshAuthMethod.PublicKey ? info.PrivateKeyPath : "",
                        info.Tags,
                        outcome,
                        errorCode,
                        timer.ElapsedMilliseconds);
                }
                catch (Exception historyError) when (historyError is System.IO.IOException or
                                                     Microsoft.Data.Sqlite.SqliteException or
                                                     UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Connection history append failed: {historyError.GetType().Name}");
                }

                if (RightPanel.Content is HostListPanel historyPanel)
                    historyPanel.RefreshFromStore();
            }

            if (outcome == "Failed")
                await ShowConnectionFailureAsync(session);
        }

        private async Task ShowConnectionFailureAsync(ISshSession session)
        {
            var diagnostic = ConnectionLogStore.Snapshot()
                .LastOrDefault(entry => entry.SessionId == session.Id &&
                                        entry.Severity >= ConnectionLogSeverity.Error);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = diagnostic is null
                    ? Helpers.Loc.T(
                        "SSH 연결을 완료하지 못했습니다.",
                        "The SSH connection could not be completed.")
                    : Helpers.Loc.T(diagnostic.MessageKo, diagnostic.MessageEn),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = session.LastError ?? Helpers.Loc.T("알 수 없는 오류", "Unknown error"),
                Foreground = Helpers.ThemeResources.Brush(Root, "StatusRed"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            content.Children.Add(new TextBlock
            {
                Text = Helpers.Loc.T(
                    "DNS·소켓·키 교환·호스트 키·인증 단계의 상세 기록은 로그 화면에서 확인할 수 있습니다.",
                    "Detailed DNS, socket, key-exchange, host-key, and authentication diagnostics are available in Logs."),
                Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                Title = Helpers.Loc.T("SSH 연결 실패", "SSH connection failed"),
                Content = content,
                PrimaryButtonText = Helpers.Loc.T("로그 열기", "Open logs"),
                CloseButtonText = Helpers.Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                SelectNavigationItem("Logs");
        }

        private void SelectNavigationItem(string tag)
        {
            var item = LeftNav.MenuItems
                .Concat(LeftNav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
            if (item is not null)
                LeftNav.SelectedItem = item;
        }

        private async Task<sutty.Command.HostProfile?> PersistSavedProfileAsync(SshConnectionInfo info)
        {
            if (!info.SaveProfile) return null;

            var originalCredentialId = info.CredentialId;
            string? credentialId = null;
            string? createdCredentialId = null;

            try
            {
                if (info.RememberCredential)
                {
                    var secret = info.AuthMethod == SshAuthMethod.Password
                        ? new CredentialSecret(Password: info.Password)
                        : new CredentialSecret(PrivateKeyPassphrase: info.Passphrase);

                    if (secret.IsEmpty)
                    {
                        credentialId = null;
                    }
                    else
                    {
                        // Write a new encrypted record first. The profile reference is then
                        // swapped atomically in SQLite, so a failed save never corrupts the
                        // credential used by the existing profile.
                        credentialId = LocalCredentialVault.Default.Store(secret);
                        createdCredentialId = credentialId;
                    }
                }

                var profile = sutty.Command.HostProfileStore.Save(new sutty.Command.HostProfileDraft
                {
                    DisplayName = info.Title,
                    Host = info.Host,
                    Port = info.Port,
                    Username = info.Username,
                    AuthMethod = info.AuthMethod.ToString(),
                    PrivateKeyPath = info.AuthMethod == SshAuthMethod.PublicKey ? info.PrivateKeyPath : "",
                    Tags = info.Tags,
                    GroupName = info.GroupName,
                    Environment = info.Environment,
                    IsFavorite = info.IsFavorite,
                    CredentialId = credentialId,
                }, info.SavedHostId);

                info.SavedHostId = profile.Id;
                info.CredentialId = profile.CredentialId;

                if (!string.IsNullOrWhiteSpace(originalCredentialId) &&
                    !string.Equals(originalCredentialId, credentialId, StringComparison.Ordinal))
                {
                    try { LocalCredentialVault.Default.Delete(originalCredentialId); }
                    catch (Exception cleanupError) when (cleanupError is System.IO.IOException or
                                                         UnauthorizedAccessException or
                                                         System.Security.Cryptography.CryptographicException or
                                                         System.ComponentModel.Win32Exception or
                                                         ArgumentException)
                    {
                        Debug.WriteLine(
                            $"Superseded encrypted credential cleanup failed: {cleanupError.GetType().Name}");
                    }
                }
                return profile;
            }
            catch (Exception error) when (error is System.IO.IOException or
                                          UnauthorizedAccessException or
                                          System.Security.Cryptography.CryptographicException or
                                          System.ComponentModel.Win32Exception or
                                          Microsoft.Data.Sqlite.SqliteException or
                                          ArgumentException or InvalidOperationException)
            {
                if (!string.IsNullOrWhiteSpace(createdCredentialId))
                {
                    try { LocalCredentialVault.Default.Delete(createdCredentialId); }
                    catch (Exception cleanupError) when (cleanupError is System.IO.IOException or
                                                         UnauthorizedAccessException or
                                                         System.Security.Cryptography.CryptographicException or
                                                         System.ComponentModel.Win32Exception or
                                                         ArgumentException)
                    {
                        Debug.WriteLine(
                            $"Failed credential rollback cleanup: {cleanupError.GetType().Name}");
                    }
                }

                Debug.WriteLine($"Saved-host persistence failed: {error.GetType().Name}");
                var warning = new ContentDialog
                {
                    Title = Helpers.Loc.T("호스트 저장 실패", "Could not save host"),
                    Content = Helpers.Loc.T(
                        "저장 호스트 또는 자격증명을 기록하지 못했습니다. 이번 연결은 계속 진행합니다.",
                        "The saved host or credential could not be written. This connection attempt will continue."),
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await warning.ShowAsync();
                return null;
            }
        }

        private async Task<HostKeyDecision> PromptUnknownHostKeyAsync(
            HostKeyVerification verification,
            CancellationToken ct)
        {
            await _hostKeyPromptGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var details = new StackPanel { Spacing = 8 };
                details.Children.Add(new TextBlock
                {
                    Text = Helpers.Loc.T(
                        "이 서버의 공개 호스트키가 아직 저장되어 있지 않습니다. 아래 지문을 서버 관리자와 확인하세요.",
                        "This server's public host key is not saved yet. Verify the fingerprint with the server administrator."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.Endpoint.Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextPrimary"),
                    IsTextSelectionEnabled = true,
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.PresentedKey.Algorithm,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "TextMuted"),
                    IsTextSelectionEnabled = true,
                });
                details.Children.Add(new TextBlock
                {
                    Text = verification.PresentedKey.Sha256Fingerprint,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Helpers.ThemeResources.Brush(Root, "AccentTeal"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var dialog = new ContentDialog
                {
                    Title = Helpers.Loc.T("알 수 없는 SSH 호스트", "Unknown SSH host"),
                    Content = details,
                    PrimaryButtonText = Helpers.Loc.T("신뢰하고 저장", "Trust and save"),
                    SecondaryButtonText = Helpers.Loc.T("이번만 연결", "Connect once"),
                    CloseButtonText = Helpers.Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };

                return (await dialog.ShowAsync()) switch
                {
                    ContentDialogResult.Primary => HostKeyDecision.TrustAndSave,
                    ContentDialogResult.Secondary => HostKeyDecision.TrustOnce,
                    _ => HostKeyDecision.Cancel,
                };
            }
            finally
            {
                _hostKeyPromptGate.Release();
            }
        }

        private void TitleTabs_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
            => UpdateSessionArea();

        private void UpdateSessionArea()
        {
            EmptyTabHeader.Visibility = TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SessionHost.Content = (TitleTabs.SelectedItem as TabViewItem)?.DataContext as FrameworkElement;
            SessionHost.Visibility = _isMultiView ? Visibility.Collapsed : Visibility.Visible;
            NoSessionState.Visibility = !_isMultiView && TitleTabs.TabItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Multi 그리드가 보이는 중이면 열린 세션 목록으로 갱신
            if (_isMultiView)
                MultiGrid.SetSessions(GetOpenTerminalViews());

            // Keep the cached Files panel bound to the active tab even while it is hidden.
            // This invalidates old-server nodes/transfers before the panel can be shown again.
            if (_fileTreePanel is { } fileTree)
                _ = LoadFileTreeForActiveSessionAsync(fileTree);
        }

        private System.Collections.Generic.List<SessionView> GetOpenSessionViews()
        {
            var views = new System.Collections.Generic.List<SessionView>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is TabViewItem tab && tab.DataContext is SessionView view)
                    views.Add(view);
            }
            return views;
        }

        private System.Collections.Generic.List<FrameworkElement> GetOpenTerminalViews()
        {
            var views = new System.Collections.Generic.List<FrameworkElement>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is not TabViewItem tab)
                    continue;
                if (tab.DataContext is SessionView sessionView)
                    views.Add(sessionView);
                else if (tab.DataContext is LocalTerminalView localView)
                    views.Add(localView);
            }
            return views;
        }

        private System.Collections.Generic.List<LocalTerminalView> GetOpenLocalTerminalViews()
        {
            var views = new System.Collections.Generic.List<LocalTerminalView>();
            foreach (var item in TitleTabs.TabItems)
            {
                if (item is TabViewItem tab && tab.DataContext is LocalTerminalView view)
                    views.Add(view);
            }
            return views;
        }

        // X 클릭 → SSH·SFTP 채널을 모두 끊고 탭을 닫는다 (확인 없이 즉시)
        private async void TitleTabs_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Tab);
            UpdateSessionArea();

            // 탭은 먼저 닫고, 연결 정리는 백그라운드로 (UI가 안 막히게)
            if (args.Tab.DataContext is SessionView view)
            {
                view.WorkingDirectoryChanged -= SessionView_WorkingDirectoryChanged;
                await _sessions.CloseAsync(view.Session);
            }
            else if (args.Tab.DataContext is LocalTerminalView localView)
            {
                await localView.CloseAsync();
            }
        }

        // ── 설정 창 ──

        private void OpenSettingWindow()
        {
            if (_settingWindow is null)
            {
                var panel = new SettingsPanel();
                panel.SettingsChanged += (_, args) => ApplySettingsChanges(args.Changes);
                panel.ThemeChanged += (_, themeName) => ApplyTheme(themeName);

                _settingWindow = new Window
                {
                    Title = Helpers.Loc.T("Sutty — 설정", "Sutty — Settings"),
                    ExtendsContentIntoTitleBar = true,
                    Content = panel,
                };
                _settingWindow.AppWindow.SetIcon(_appIconPath); // 설정 창에도 앱 아이콘
                panel.RequestedTheme = Root.RequestedTheme;
                ApplyTitleBarColors(_settingWindow, Helpers.ThemeManager.Find(SettingsService.Current.Theme));
                _settingWindow.Closed += (_, _) => _settingWindow = null;

                // 저장된 크기로 열고, 드래그 리사이즈도 기억
                Helpers.WindowSizePersistence.Attach(_settingWindow,
                    s => new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight),
                    (s, size) => { s.SettingWindowWidth = size.Width; s.SettingWindowHeight = size.Height; });

                _settingWindow.Activate();
            }

            DispatcherQueue.TryEnqueue(() => BringToFront(_settingWindow));
        }

        private void ApplySettingsChanges(SettingChangeKind changes)
        {
            if (changes.HasFlag(SettingChangeKind.TerminalAppearance) ||
                changes.HasFlag(SettingChangeKind.TerminalMode) ||
                changes.HasFlag(SettingChangeKind.TerminalFeatures))
            {
                foreach (var view in GetOpenSessionViews())
                    view.ApplyTerminalSettings();

                if (changes.HasFlag(SettingChangeKind.TerminalAppearance))
                {
                    foreach (var localView in GetOpenLocalTerminalViews())
                        localView.ApplyTerminalSettings();
                }
            }

            if (changes.HasFlag(SettingChangeKind.Language))
            {
                Bindings.Update();
                if (_settingWindow is not null)
                    _settingWindow.Title = Helpers.Loc.T("Sutty — 설정", "Sutty — Settings");
                foreach (var view in GetOpenSessionViews())
                    view.RefreshLanguage();
                foreach (var localView in GetOpenLocalTerminalViews())
                    localView.RefreshLanguage();
                MultiGrid.RefreshLanguage();

                // 입력 중인 Home 폼이나 검색 상태를 잃지 않고 현재 패널의
                // one-time localization binding만 다시 평가한다.
                switch (RightPanel.Content)
                {
                    case HomePanel localizedHome: localizedHome.RefreshLanguage(); break;
                    case HostListPanel localizedHistory: localizedHistory.RefreshLanguage(); break;
                    case FileTreePanel localizedFiles: localizedFiles.RefreshLanguage(); break;
                    case CommandPanel localizedCommands: localizedCommands.RefreshLanguage(); break;
                    case MultiCommandPanel localizedMulti: localizedMulti.RefreshLanguage(); break;
                    case ConnectionLogPanel localizedLogs: localizedLogs.RefreshLanguage(); break;
                }
            }

            var applyDefaultPort = (changes & SettingChangeKind.ConnectionPort) != 0;
            var applyDefaultKeepAlive = (changes & SettingChangeKind.ConnectionKeepAlive) != 0;
            if ((applyDefaultPort || applyDefaultKeepAlive) &&
                RightPanel.Content is HomePanel homePanel)
            {
                homePanel.ApplyConnectionDefaults(applyDefaultPort, applyDefaultKeepAlive);
            }

            if (changes.HasFlag(SettingChangeKind.History) &&
                RightPanel.Content is HostListPanel historyPanel)
            {
                historyPanel.RefreshFromStore();
            }

            if (changes.HasFlag(SettingChangeKind.Window))
                ApplyWindowSizesFromSettings();
        }

        // 설정 패널에서 저장한 창 크기 숫자를 즉시 반영
        private void ApplyWindowSizesFromSettings()
        {
            var s = SettingsService.Current;

            if (s.MainWindowWidth > 0 && s.MainWindowHeight > 0)
                AppWindow.ResizeClient(new SizeInt32(s.MainWindowWidth, s.MainWindowHeight));

            if (_settingWindow is not null && s.SettingWindowWidth > 0 && s.SettingWindowHeight > 0)
                _settingWindow.AppWindow.ResizeClient(new SizeInt32(s.SettingWindowWidth, s.SettingWindowHeight));

            // 오른쪽 패널 폭도 숫자로 지정 가능
            if (s.RightPanelWidth > 0)
                RightPanelColumn.Width = new GridLength(Math.Clamp(s.RightPanelWidth, 300, 800));
        }

        private static void BringToFront(Window window)
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore();

                presenter.IsAlwaysOnTop = true;
                presenter.IsAlwaysOnTop = false;
            }
            window.Activate();
        }
    }
}
