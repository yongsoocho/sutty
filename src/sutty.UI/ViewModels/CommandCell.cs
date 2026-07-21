using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace sutty.UI.ViewModels;

/// <summary>
/// Jupyter 노트북의 셀 하나에 해당.
/// Command가 있으면 [입력 블록 + 출력 블록], null이면 시스템 메시지(출력만).
/// </summary>
public sealed class CommandCell : ObservableObject
{
    // XAML 타입 생성기가 init 접근자를 지원하지 않아 set 사용 (생성 후 바꾸지 말 것)

    /// <summary>실행한 명령. null이면 연결 상태 같은 시스템 메시지 셀.</summary>
    public string? Command { get; set; }

    /// <summary>입력 블록 앞에 붙는 프롬프트 (예: admin@10.0.0.15).</summary>
    public string Prompt { get; set; } = "$";

    /// <summary>몇 번째 명령인지. 시스템 셀은 0.</summary>
    public int Index { get; set; }

    public string InLabel => Index > 0 ? $"In [{Index}]" : "In [ ]";
    public string IndexText => Index > 0 ? Index.ToString() : "";

    /// <summary>명령 실행 시작 시각.</summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    private string _timeText = "";
    /// <summary>완료 후 "14:01:52 · 12ms" 형태로 채워지는 타임스탬프.</summary>
    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value);
    }

    public bool HasCommand => !string.IsNullOrEmpty(Command);

    private string _output = "";
    public string Output
    {
        get => _output;
        set
        {
            if (SetProperty(ref _output, value))
                OnPropertyChanged(nameof(HasOutput));
        }
    }

    public bool HasOutput => !string.IsNullOrEmpty(_output);

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }
}
