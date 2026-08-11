using sutty.Setting;

namespace sutty.UI.Helpers;

/// <summary>
/// 한국어/영어 문자열 선택 헬퍼 (명령어 제외한 모든 UI 텍스트에 사용).
/// XAML: Text="{x:Bind h:Loc.T('한국어', 'English')}"  (xmlns:h="using:sutty.UI.Helpers")
/// C#  : Loc.T("한국어", "English")
/// 언어는 Setting > Appearance에서 변경. x:Bind는 OneTime이라
/// 이미 떠 있는 화면은 다시 열어야(패널 전환/재시작) 반영된다.
/// </summary>
public static class Loc
{
    public static bool IsKorean => SettingsService.Current.Language != "en";

    public static string T(string korean, string english) => IsKorean ? korean : english;
}
