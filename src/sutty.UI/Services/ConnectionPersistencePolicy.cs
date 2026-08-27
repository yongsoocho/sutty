namespace sutty.UI.Services;

/// <summary>Connection outcomes used to decide whether a transient connection may persist.</summary>
public enum ConnectionAttemptOutcome
{
    Success,
    Failed,
    Cancelled,
}

/// <summary>
/// Keeps Quick Connect persistence decisions independent from dialogs and session views.
/// A failed or cancelled attempt can never create or update a Saved Host.
/// </summary>
public static class ConnectionPersistencePolicy
{
    public static bool ShouldOfferSave(
        ConnectionAttemptOutcome outcome,
        bool saveAlreadyRequested,
        string? savedHostId) =>
        outcome == ConnectionAttemptOutcome.Success &&
        !saveAlreadyRequested &&
        string.IsNullOrWhiteSpace(savedHostId);

    public static bool ShouldPersistProfile(
        ConnectionAttemptOutcome outcome,
        bool saveRequested) =>
        outcome == ConnectionAttemptOutcome.Success && saveRequested;
}
