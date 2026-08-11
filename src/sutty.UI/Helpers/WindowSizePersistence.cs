using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using sutty.Setting;
using System;
using Windows.Graphics;

namespace sutty.UI.Helpers;

/// <summary>
/// 창 크기를 settings.json에 기억해 두고 다음 실행 때 복원한다.
/// 드래그 리사이즈 중에는 Changed 이벤트가 수십 번 발생하므로
/// 디바운스(마지막 변경 후 600ms)로 최종 크기만 저장한다.
/// </summary>
public static class WindowSizePersistence
{
    public static void Attach(
        Window window,
        Func<AppSettings, SizeInt32> load,
        Action<AppSettings, SizeInt32> store)
    {
        var appWindow = window.AppWindow;

        // 저장된 크기가 있으면 복원 (모니터 작업 영역보다 크면 잘라낸다)
        var saved = load(SettingsService.Current);
        if (saved.Width > 0 && saved.Height > 0)
        {
            var workArea = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest)
                .WorkArea;
            appWindow.ResizeClient(new SizeInt32(
                Math.Min(saved.Width, workArea.Width),
                Math.Min(saved.Height, workArea.Height - 40)));
        }

        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SaveNow();

        appWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange) return;
            timer.Stop();
            timer.Start(); // 디바운스: 마지막 리사이즈만 저장
        };

        window.Closed += (_, _) =>
        {
            if (timer.IsRunning)
            {
                timer.Stop();
                SaveNow(); // 닫히기 직전에 미저장분 반영
            }
        };

        void SaveNow()
        {
            // 최대화/최소화 상태의 크기는 저장하지 않는다 (복원 시 이상해짐)
            if (appWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
                return;

            store(SettingsService.Current, appWindow.ClientSize);
            SettingsService.Save();
        }
    }
}
