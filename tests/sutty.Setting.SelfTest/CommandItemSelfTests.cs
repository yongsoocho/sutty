using sutty.Command;
using sutty.Setting;
using sutty.UI.ViewModels;

internal static class CommandItemSelfTests
{
    public static void Run()
    {
        PositionalParametersExpandOnlyTemplateTokens();
        OverflowAndNonAsciiNumbersRemainLiteral();
        RefreshRetainsOnlyCompatibleDrafts();
        ParameterLabelsFollowLanguageWithoutChangingValues();
        Console.WriteLine("Command item draft and expansion self-tests passed.");
    }

    private static void PositionalParametersExpandOnlyTemplateTokens()
    {
        var item = new CommandItemVm(new CommandTemplate
        {
            Id = 1, Name = "Expansion", CommandText = "printf '%s' '$12' '$1' '$12' '$01'",
        });
        Assert(item.ParamNumbers.SequenceEqual([1, 12]), "placeholder numbers are unique and sorted");
        item.PrepareParams();
        item.Params.Single(parameter => parameter.Number == 1).Value = "first";
        item.Params.Single(parameter => parameter.Number == 12).Value = "literal $1 and $12";
        Assert(item.BuildCommand() == "printf '%s' 'literal $1 and $12' 'first' 'literal $1 and $12' 'first'",
            "supplied dollar-number text is never expanded by a later parameter replacement");
        var missing = CommandTemplateExpansion.Expand("$1 $12", new Dictionary<int, string> { [1] = "value" });
        Assert(missing == "value $12", "missing values remain visible instead of matching a shorter token");
    }

    private static void OverflowAndNonAsciiNumbersRemainLiteral()
    {
        const string command = "echo $12345678901234567890 $2147483648 $\u0661 $0 $2147483647";
        var item = new CommandItemVm(new CommandTemplate { Id = 2, CommandText = command });
        Assert(item.ParamNumbers.SequenceEqual([0, int.MaxValue]),
            "long digit sequences cannot crash the library and only valid ASCII identifiers become inputs");
        item.PrepareParams();
        item.Params.Single(parameter => parameter.Number == 0).Value = "zero";
        item.Params.Single(parameter => parameter.Number == int.MaxValue).Value = "max";
        Assert(item.BuildCommand() == "echo $12345678901234567890 $2147483648 $\u0661 zero max",
            "unsupported numbers remain unchanged in the exact command shown for execution");
    }

    private static void RefreshRetainsOnlyCompatibleDrafts()
    {
        var item = new CommandItemVm(new CommandTemplate { Id = 3, Name = "Before", CommandText = "echo $1" });
        item.PrepareParams();
        item.ShowParams = true;
        var parameter = item.Params[0];
        parameter.Value = "retained input";
        item.UpdateTemplate(new CommandTemplate { Id = 3, Name = "After", CommandText = "echo $1", UsageCount = 9 });
        Assert(item.ShowParams && ReferenceEquals(item.Params[0], parameter) && item.BuildCommand() == "echo retained input",
            "usage reorder and renaming retain the open input and the exact parameter object");
        Assert(item.Name == "After" && item.Template.UsageCount == 9, "retained cards use current store metadata");
        item.UpdateTemplate(new CommandTemplate { Id = 3, Name = "After", CommandText = "rm $2" });
        Assert(!item.ShowParams && item.Params.Count == 0 && item.ParamNumbers.SequenceEqual([2]),
            "changing the actual template discards incompatible inputs instead of reusing them on another command");
        var rejected = false;
        try { item.UpdateTemplate(new CommandTemplate { Id = 4, CommandText = "different" }); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected && item.Template.Id == 3, "a retained command card cannot silently change identity");
    }

    private static void ParameterLabelsFollowLanguageWithoutChangingValues()
    {
        var originalLanguage = SettingsService.Current.Language;
        try
        {
            var parameter = new CommandParamVm { Number = 12, Value = "literal $1" };
            SettingsService.Current.Language = "en";
            Assert(parameter.Label == "Value for $12", "English parameter fields have an English label");
            var labelChanged = false;
            parameter.PropertyChanged += (_, e) => labelChanged |= e.PropertyName == nameof(parameter.Label);
            SettingsService.Current.Language = "ko";
            parameter.RefreshLanguage();
            Assert(labelChanged && parameter.Label != "Value for $12" && parameter.Value == "literal $1",
                "changing language updates the input label without replacing the parameter value");
        }
        finally { SettingsService.Current.Language = originalLanguage; }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
