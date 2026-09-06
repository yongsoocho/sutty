using System;
using System.Collections.Generic;

namespace sutty.UI.Services;

public enum FileBrowserSort { Name, Size, Modified }

/// <summary>Only successfully opened folders enter the bounded, in-memory history.</summary>
public sealed class FileBrowserNavigationHistory(StringComparer comparer)
{
    private readonly List<string> _paths = [];
    private int _index = -1;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _paths.Count - 1;
    public string? BackPath => CanGoBack ? _paths[_index - 1] : null;
    public string? ForwardPath => CanGoForward ? _paths[_index + 1] : null;

    public void Record(string path, int historyOffset = 0)
    {
        if (historyOffset is -1 or 1 && _index + historyOffset >= 0 &&
            _index + historyOffset < _paths.Count && comparer.Equals(_paths[_index + historyOffset], path))
        {
            _index += historyOffset;
            return;
        }
        if (_index >= 0 && comparer.Equals(_paths[_index], path)) return;
        if (_index + 1 < _paths.Count) _paths.RemoveRange(_index + 1, _paths.Count - _index - 1);
        _paths.Add(path);
        if (_paths.Count > 100) _paths.RemoveAt(0);
        _index = _paths.Count - 1;
    }

    public void Clear()
    {
        _paths.Clear();
        _index = -1;
    }
}
