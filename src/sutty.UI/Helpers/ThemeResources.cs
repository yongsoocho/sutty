using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace sutty.UI.Helpers;

/// <summary>
/// 코드비하인드에서 테마별 팔레트 브러시를 찾을 때 사용.
/// (Application.Current.Resources[key]는 ThemeDictionaries 안까지 못 들어가므로
///  요소의 ActualTheme에 맞는 딕셔너리에서 직접 찾는다)
/// </summary>
public static class ThemeResources
{
    public static Brush Brush(FrameworkElement scope, string key)
    {
        var themeKey = scope.ActualTheme == ElementTheme.Light ? "Light" : "Dark";

        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary rd &&
                rd.TryGetValue(key, out var value) &&
                value is Brush brush)
            {
                return brush;
            }
        }

        // 테마 무관 리소스 (StatusGreen 등)
        return (Brush)Application.Current.Resources[key];
    }
}
