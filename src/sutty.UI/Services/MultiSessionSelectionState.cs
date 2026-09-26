using System;
using System.Collections.Generic;
using System.Linq;

namespace sutty.UI.Services;

/// <summary>Retains slot identity and limits selection to eligible sessions in the active display scope.</summary>
internal sealed class MultiSessionSelectionState<TSession, TSlot>(
    Func<TSession, TSlot> createSlot,
    Func<TSlot, TSession?> getSession,
    Func<TSlot, bool> isSelected,
    Action<TSlot, bool> setSelected,
    Func<TSlot, bool>? isEligible = null)
    where TSession : class
    where TSlot : class
{
    public const int PageSize = 9;
    private List<TSlot> _slots = [];

    public IReadOnlyList<TSlot> AllSlots => _slots;
    public int PageIndex { get; private set; }
    public int PageCount => Math.Max(1, (_slots.Count + PageSize - 1) / PageSize);
    public bool IsPaginationEnabled { get; private set; } = true;
    public int HiddenSessionCount => IsPaginationEnabled ? 0 : Math.Max(0, _slots.Count - PageSize);
    public int EligibleCount => InScopeSlots().Count(CanSelect);

    public void SetPaginationEnabled(bool enabled)
    {
        IsPaginationEnabled = enabled;
        if (!enabled) PageIndex = 0;
        ClearUnavailableSelections();
    }

    public void SetSessions(IReadOnlyList<TSession> sessions)
    {
        var previous = new Dictionary<TSession, TSlot>(ReferenceEqualityComparer.Instance);
        foreach (var slot in _slots)
        {
            if (getSession(slot) is { } session)
                previous.TryAdd(session, slot);
        }

        var seen = new HashSet<TSession>(ReferenceEqualityComparer.Instance);
        var next = new List<TSlot>(sessions.Count);
        foreach (var session in sessions)
        {
            if (!seen.Add(session)) continue;
            if (!previous.TryGetValue(session, out var slot))
            {
                slot = createSlot(session);
                setSelected(slot, false);
            }
            next.Add(slot);
        }

        // Retain actual slot instances: running commands still write to those objects.
        _slots = next;
        PageIndex = IsPaginationEnabled ? Math.Min(PageIndex, PageCount - 1) : 0;
        ClearUnavailableSelections();
    }

    public IReadOnlyList<TSlot?> GetPageSlots()
    {
        var result = new TSlot?[PageSize];
        var start = PageIndex * PageSize;
        for (var i = 0; i < PageSize && start + i < _slots.Count; i++)
            result[i] = _slots[start + i];
        return result;
    }

    public void MovePage(int offset) =>
        PageIndex = IsPaginationEnabled ? Math.Clamp(PageIndex + offset, 0, PageCount - 1) : 0;

    public void SetSelected(TSlot slot, bool selected)
    {
        if (!_slots.Contains(slot)) return;
        setSelected(slot, selected && InScopeSlots().Contains(slot) && CanSelect(slot));
    }

    public void SetAllSelected(bool selected)
    {
        var scope = InScopeSlots().ToHashSet();
        foreach (var slot in _slots)
            setSelected(slot, selected && scope.Contains(slot) && CanSelect(slot));
    }

    public List<TSlot> GetSelectedSlots() => InScopeSlots().Where(slot => CanSelect(slot) && isSelected(slot)).ToList();

    private IEnumerable<TSlot> InScopeSlots() => IsPaginationEnabled ? _slots : _slots.Take(PageSize);
    private bool CanSelect(TSlot slot) => isEligible?.Invoke(slot) ?? true;

    private void ClearUnavailableSelections()
    {
        var scope = InScopeSlots().ToHashSet();
        foreach (var slot in _slots)
            if (!scope.Contains(slot) || !CanSelect(slot)) setSelected(slot, false);
    }
}
