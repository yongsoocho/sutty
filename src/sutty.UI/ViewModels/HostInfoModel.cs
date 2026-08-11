using System;
using System.Collections.Generic;

namespace sutty.UI.ViewModels;

/// <summary>
/// History 패널 카드 하나에 표시할 접속 히스토리 (SQLite host_history에서 로드).
/// 비밀번호/passphrase를 제외한 연결 초안과 표시 정보를 가진다.
/// </summary>
public class HostInfoModel
{
    public long Id { get; set; }
    public string Alias { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTime? LastConnected { get; set; }

    /// <summary>True when the user explicitly pinned this host.</summary>
    public bool IsPinned { get; set; }

    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string AuthMethod { get; set; } = "Password";
    public string PrivateKeyPath { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// <summary>상단 고정(TOP) 카드일 때만 채워지는 총 접속 횟수.</summary>
    public int ConnectionCount { get; set; }

    public bool HasCount => ConnectionCount > 0;
    public bool HasTags => Tags.Count > 0;
    public string CountText => $"{ConnectionCount}×";

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
}
