using CommunityToolkit.Mvvm.ComponentModel;
using sutty.Command;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace sutty.UI.ViewModels;

/// <summary>Command 패널의 playbook 항목 하나. $1, $2 자리표시자 입력 UI 상태 포함.</summary>
public sealed class CommandItemVm : ObservableObject
{
    public CommandTemplate Template { get; }

    /// <summary>명령에 등장하는 자리표시자 번호들 ($1, $2 → [1, 2]).</summary>
    public List<int> ParamNumbers { get; }

    public ObservableCollection<CommandParamVm> Params { get; } = [];

    private bool _showParams;
    public bool ShowParams
    {
        get => _showParams;
        set => SetProperty(ref _showParams, value);
    }

    public CommandItemVm(CommandTemplate template)
    {
        Template = template;
        ParamNumbers = Regex.Matches(template.CommandText, @"\$(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    public string Name => Template.Name;
    public string CommandText => Template.CommandText;

    public void PrepareParams()
    {
        if (Params.Count > 0) return;
        foreach (var n in ParamNumbers)
            Params.Add(new CommandParamVm { Number = n, Label = $"${n} 값 입력" });
    }

    /// <summary>$n을 입력값으로 치환한 최종 명령. ($12를 $1보다 먼저 치환해 충돌 방지)</summary>
    public string BuildCommand()
    {
        var command = Template.CommandText;
        foreach (var p in Params.OrderByDescending(p => p.Number))
            command = command.Replace($"${p.Number}", p.Value.Trim());
        return command;
    }
}

/// <summary>자리표시자 입력 칸 하나 ($1 → TextBox).</summary>
public sealed class CommandParamVm
{
    public int Number { get; set; }
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}
