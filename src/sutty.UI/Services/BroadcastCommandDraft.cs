using System;

namespace sutty.UI.Services;

/// <summary>A submitted draft is retained until that exact revision is approved.</summary>
internal sealed class BroadcastCommandDraft
{
    public string Text { get; private set; } = "";
    public long Revision { get; private set; }

    public void Update(string text)
    {
        if (string.Equals(Text, text, StringComparison.Ordinal)) return;
        Text = text;
        Revision++;
    }

    public BroadcastCommandSubmission Submit() => new(Normalize(Text), Revision);

    public bool TryApprove(BroadcastCommandSubmission submission)
    {
        if (submission.DraftRevision != Revision ||
            !string.Equals(submission.Command, Normalize(Text), StringComparison.Ordinal))
            return false;
        Update("");
        return true;
    }

    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}

public sealed record BroadcastCommandSubmission(string Command, long? DraftRevision = null);
