using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace sutty.UI.ViewModels;

/// <summary>
/// React의 props에 해당하는 데이터 모델.
/// 카드 하나에 표시할 호스트 정보를 담는다.
/// </summary>
public class HostInfoModel
{
    public string Hostname { get; set; } = "";
    public string Alias { get; set; } = "";
    public string IP { get; set; } = "";
    public string Username { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateTime? LastConnected { get; set; }

    /// <summary>로고 이미지 경로 (ms-appx:///Assets/ubuntu.png 등)</summary>
    public string LogoPath { get; set; } = "";

    /// <summary>로고가 없을 때 배경색으로 쓸 Brush</summary>
    public SolidColorBrush? LogoBg { get; set; }

    // ── 헬퍼 ──

    /// <summary>"ssh, dev, cash" 형태의 태그 한 줄 요약</summary>
    public string TagSummary => Tags.Length > 0 ? string.Join(", ", Tags) : "";

    /// <summary>"3 min ago", "2 days ago" 등 사람이 읽기 편한 시간</summary>
    public string LastConnectedText => LastConnected switch
    {
        null => "never",
        DateTime d when (DateTime.Now - d).TotalMinutes < 1 => "just now",
        DateTime d when (DateTime.Now - d).TotalMinutes < 60 => $"{(int)(DateTime.Now - d).TotalMinutes} min ago",
        DateTime d when (DateTime.Now - d).TotalHours < 24 => $"{(int)(DateTime.Now - d).TotalHours}h ago",
        DateTime d when (DateTime.Now - d).TotalDays < 30 => $"{(int)(DateTime.Now - d).TotalDays}d ago",
        DateTime d => d.ToString("yyyy-MM-dd"),
    };

    /// <summary>로고 ImageSource (바인딩용)</summary>
    public ImageSource? LogoImage => string.IsNullOrEmpty(LogoPath)
        ? null
        : new BitmapImage(new Uri(LogoPath));
}