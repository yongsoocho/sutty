using sutty.UI.Services;

internal static class MultiSessionSelectionSelfTests
{
    public static void Run()
    {
        PagesRetainEveryTarget();
        RefreshRetainsRunningSlotsWithoutRetargeting();
        EmptyAndDuplicateSessionsAreSafe();
        DraftIsClearedOnlyAfterExactApproval();
        Console.WriteLine("Multi-session selection and paging self-tests passed.");
    }

    private static MultiSessionSelectionState<object, Slot> CreateState() => new(
        session => new Slot(session) { IsSelected = true },
        slot => slot.Session,
        slot => slot.IsSelected,
        (slot, selected) => slot.IsSelected = selected);

    private static void PagesRetainEveryTarget()
    {
        var state = CreateState();
        var sessions = Enumerable.Range(0, 21).Select(_ => new object()).ToArray();
        state.SetSessions(sessions);
        Assert(state.PageCount == 3 && state.AllSlots.Count == 21,
            "all sessions remain accessible beyond nine and the former sixteen-slot limit");
        Assert(state.GetSelectedSlots().Count == 0,
            "new sessions remain unchecked even if a slot factory defaults to selected");

        var firstPage = state.GetPageSlots();
        Assert(firstPage.Count == 9 && firstPage.All(slot => slot is not null),
            "the first page has exactly nine session cards");
        firstPage[0]!.IsSelected = true;
        state.MovePage(1);
        state.GetPageSlots()[5]!.IsSelected = true;
        state.MovePage(1);
        var lastPage = state.GetPageSlots();
        Assert(lastPage.Count == 9 && lastPage.Count(slot => slot is not null) == 3,
            "the final page includes six non-selectable placeholders");
        lastPage[2]!.IsSelected = true;
        Assert(state.GetSelectedSlots().Select(slot => slot.Session)
            .SequenceEqual(new[] { sessions[0], sessions[14], sessions[20] }),
            "broadcast targets include checked sessions from every page and no unchecked sessions");

        var broadcastSnapshot = state.GetSelectedSlots();
        state.SetAllSelected(true);
        Assert(state.GetSelectedSlots().Count == 21,
            "select all includes sessions on pages that are not currently visible");
        state.SetAllSelected(false);
        Assert(state.GetSelectedSlots().Count == 0,
            "clear all deselects every page");
        Assert(broadcastSnapshot.Count == 3,
            "later checkbox changes do not change an already captured broadcast target list");

        state.MovePage(100);
        Assert(state.PageIndex == 2, "next-page navigation stays on the final page");
        state.MovePage(-100);
        Assert(state.PageIndex == 0, "previous-page navigation stays on the first page");
    }

    private static void RefreshRetainsRunningSlotsWithoutRetargeting()
    {
        var state = CreateState();
        var sessions = Enumerable.Range(0, 12).Select(_ => new object()).ToArray();
        state.SetSessions(sessions);
        var runningSlot = state.AllSlots[10];
        runningSlot.IsSelected = true;
        runningSlot.Output = "running";
        state.MovePage(1);

        var addedSession = new object();
        var reordered = new[] { sessions[10], addedSession, sessions[0], sessions[11] };
        state.SetSessions(reordered);
        Assert(ReferenceEquals(state.AllSlots[0], runningSlot) && runningSlot.IsSelected,
            "reordering and refreshing preserves the actual checked slot instance");
        Assert(!state.AllSlots[1].IsSelected,
            "a newly opened session does not inherit a closed or selected slot's checkbox");
        Assert(state.PageIndex == 0 && state.PageCount == 1,
            "closing sessions on the final page returns to the remaining page");
        runningSlot.Output = "completed after refresh";
        Assert(state.GetPageSlots()[0]!.Output == "completed after refresh",
            "an in-flight completion updates the retained card after a refresh");

        state.SetSessions(new[] { addedSession, sessions[0] });
        Assert(state.GetSelectedSlots().Count == 0 && ReferenceEquals(runningSlot.Session, sessions[10]),
            "closed targets are removed and their running slot is never retargeted");
        runningSlot.Output = "late completion from a closed session";
        Assert(state.AllSlots.All(slot => string.IsNullOrEmpty(slot.Output)),
            "late output from a closed session cannot appear on a replacement session");
        state.SetSessions(new[] { sessions[10] });
        Assert(!ReferenceEquals(state.AllSlots[0], runningSlot) && !state.AllSlots[0].IsSelected,
            "a removed and later registered session must be selected again");
    }

    private static void EmptyAndDuplicateSessionsAreSafe()
    {
        var state = CreateState();
        state.SetSessions(Array.Empty<object>());
        state.SetAllSelected(true);
        Assert(state.PageCount == 1 && state.GetPageSlots().Count == 9 &&
               state.GetPageSlots().All(slot => slot is null) && state.GetSelectedSlots().Count == 0,
            "an empty workspace keeps nine placeholders and cannot select a broadcast target");

        var session = new object();
        state.SetSessions(new[] { session, session });
        state.SetAllSelected(true);
        Assert(state.AllSlots.Count == 1 && state.GetSelectedSlots().Count == 1,
            "a duplicate session reference cannot receive the same broadcast twice");
    }

    private static void DraftIsClearedOnlyAfterExactApproval()
    {
        var draft = new BroadcastCommandDraft();
        draft.Update("  printf one\r\nprintf two\r  ");
        var submitted = draft.Submit();
        Assert(submitted.Command == "printf one\nprintf two",
            "preview command is normalized exactly once");
        Assert(draft.Text.Length > 0,
            "submitting without approval retains the draft on zero targets, invalid state or cancelled confirmation");
        Assert(!draft.TryApprove(new BroadcastCommandSubmission("saved command")) && draft.Text.Length > 0,
            "approving a saved command never clears an unrelated input draft");
        draft.Update("new draft");
        Assert(!draft.TryApprove(submitted) && draft.Text == "new draft",
            "approving an old command cannot discard a newer draft");
        draft.Update("  printf one\r\nprintf two\r  ");
        Assert(!draft.TryApprove(submitted), "returning to old text does not approve a different revision");
        Assert(draft.TryApprove(draft.Submit()) && draft.Text.Length == 0,
            "the approved current draft is cleared once");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class Slot(object session)
    {
        public object Session { get; } = session;
        public bool IsSelected { get; set; }
        public string Output { get; set; } = "";
    }
}
