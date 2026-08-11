namespace sutty.Command;

/// <summary>
/// 자주 쓰는 명령어 템플릿 하나 (playbook 항목).
/// CommandText에 $1, $2 … 자리표시자를 넣으면 실행 시 값을 입력받아 치환된다.
/// 예) lvcreate -n $1 -L $2
/// </summary>
public sealed class CommandTemplate
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string CommandText { get; set; } = "";
    public int UsageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
