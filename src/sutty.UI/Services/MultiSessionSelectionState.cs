using System;
using System.Collections.Generic;
using System.Linq;

namespace sutty.UI.Services;

/// <summary>Keeps broadcast selection independent of the nine cards currently visible.</summary>
internal sealed class MultiSessionSelectionState<TSession, TSlot>(
    Func<TSession, TSlot> createSlot,
    Func<TSlot, TSession?> getSession,
    Func<TSlot, bool> isSelected,
    Action<TSlot, bool> setSelected)
    where TSession : class
    where TSlot : class
{
    public const int PageSize = 9;
    private List<TSlot> _slots = [];

    public IReadOnlyList<TSlot> AllSlots => _slots;
    public int PageIndex { get; private set; }
    public int PageCount => Math.Max(1, (_slots.Count + PageSize - 1) / PageSize);

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
        PageIndex = Math.Min(PageIndex, PageCount - 1);
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
        PageIndex = Math.Clamp(PageIndex + offset, 0, PageCount - 1);

    public void SetAllSelected(bool selected)
    {
        foreach (var slot in _slots)
            setSelected(slot, selected);
    }

    public List<TSlot> GetSelectedSlots() => _slots.Where(isSelected).ToList();
}
