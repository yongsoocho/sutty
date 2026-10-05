using CommunityToolkit.Mvvm.ComponentModel;
using sutty.Command;
using sutty.UI.Helpers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace sutty.UI.ViewModels;

/// <summary>Command 패널의 playbook 항목 하나. $1, $2 자리표시자 입력 UI 상태 포함.</summary>
public sealed class CommandItemVm : ObservableObject
{
    public CommandTemplate Template { get; private set; }

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
        ParamNumbers = CommandTemplateExpansion.GetParameterNumbers(template.CommandText);
    }

    public string Name => Template.Name;
    public string CommandText => Template.CommandText;

    public void UpdateTemplate(CommandTemplate template)
    {
        if (template.Id != Template.Id)
            throw new System.ArgumentException("Cannot change a command item's identity.", nameof(template));
        var commandChanged = Template.CommandText != template.CommandText;
        Template = template;
        if (commandChanged)
        {
            ParamNumbers.Clear();
            ParamNumbers.AddRange(CommandTemplateExpansion.GetParameterNumbers(template.CommandText));
            Params.Clear();
            ShowParams = false;
        }
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CommandText));
    }

    public void RefreshLanguage()
    {
        foreach (var parameter in Params)
            parameter.RefreshLanguage();
    }

    public void PrepareParams()
    {
        if (Params.Count > 0) return;
        foreach (var n in ParamNumbers)
            Params.Add(new CommandParamVm { Number = n });
    }

    /// <summary>Expands placeholders from the template only, preserving literal values.</summary>
    public string BuildCommand()
    {
        return CommandTemplateExpansion.Expand(Template.CommandText,
            Params.ToDictionary(parameter => parameter.Number, parameter => parameter.Value.Trim()));
    }
}

/// <summary>자리표시자 입력 칸 하나 ($1 → TextBox).</summary>
public sealed class CommandParamVm : ObservableObject
{
    public int Number { get; set; }
    public string Label => Loc.T($"${Number} 값 입력", $"Value for ${Number}");
    public string Value { get; set; } = "";
    public void RefreshLanguage() => OnPropertyChanged(nameof(Label));
}
