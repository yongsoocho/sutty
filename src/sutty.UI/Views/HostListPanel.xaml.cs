using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace sutty.UI.Views;

public sealed partial class HostListPanel : UserControl
{
    // ═══════════════════════════════════════════════════
    //  React 비유:
    //  const [hosts, setHosts] = useState<HostInfoModel[]>([...])
    //  ObservableCollection이 useState + 자동 리렌더 역할
    // ═══════════════════════════════════════════════════

    private readonly ObservableCollection<HostInfoModel> _allHosts = [];
    public ObservableCollection<HostInfoModel> FilteredHosts { get; } = [];

    /// <summary>카드를 클릭하면 해당 호스트로 연결해 달라는 신호.</summary>
    public event EventHandler<HostInfoModel>? ConnectRequested;

    public HostListPanel()
    {
        this.InitializeComponent();
        LoadSampleData();
        ApplyFilter("");

        // ItemsRepeater가 만든 HostCard에 클릭 이벤트를 연결
        HostRepeater.ElementPrepared += (_, args) =>
        {
            if (args.Element is Controls.HostCard card)
            {
                card.Clicked -= OnCardClicked;
                card.Clicked += OnCardClicked;
            }
        };
    }

    private void OnCardClicked(object? sender, HostInfoModel host)
        => ConnectRequested?.Invoke(this, host);

    // ── 샘플 데이터 (나중에 DB/API로 교체) ──

    private void LoadSampleData()
    {
        var orangeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE9, 0x54, 0x20));
        var purpleBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x21, 0x6F));
        var tealBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1A, 0x8C, 0x8C));

        // React의 초기 state 배열과 같은 역할
        HostInfoModel[] samples =
        [
            new()
            {
                Hostname      = "Dev Scheduler",
                Alias         = "dev-scheduler",
                IP            = "10.0.0.15",
                Username      = "admin",
                Tags          = ["ssh", "dev"],
                LogoPath      = "ms-appx:///Assets/ubuntu.png",
                LogoBg        = orangeBrush,
                LastConnected = DateTime.Now.AddMinutes(-3),
            },
            new()
            {
                Hostname      = "Web Server us-1",
                Alias         = "web-us-1",
                IP            = "10.0.1.22",
                Username      = "deploy",
                Tags          = ["ssh", "dev", "cash"],
                LogoPath      = "ms-appx:///Assets/ubuntu.png",
                LogoBg        = orangeBrush,
                LastConnected = DateTime.Now.AddHours(-2),
            },
            new()
            {
                Hostname      = "Postgresql Replica-1",
                Alias         = "pg-replica-1",
                IP            = "10.0.2.10",
                Username      = "postgres",
                Tags          = ["ssh", "prod", "db"],
                LogoPath      = "ms-appx:///Assets/ubuntu.png",
                LogoBg        = purpleBrush,
                LastConnected = DateTime.Now.AddDays(-1),
            },
            new()
            {
                Hostname      = "Web Server us-0",
                Alias         = "web-us-0",
                IP            = "10.0.1.20",
                Username      = "ubuntu",
                Tags          = ["ssh", "dev", "cash"],
                LogoPath      = "ms-appx:///Assets/ubuntu.png",
                LogoBg        = tealBrush,
                LastConnected = null,
            },
        ];

        foreach (var h in samples)
            _allHosts.Add(h);
    }

    // ── 검색 필터 (React의 useMemo + filter에 해당) ──

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ApplyFilter(sender.Text);
    }

    private void ApplyFilter(string query)
    {
        var q = query.Trim().ToLowerInvariant();

        var results = string.IsNullOrEmpty(q)
            ? _allHosts.ToList()
            : _allHosts.Where(h =>
                h.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                h.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                h.IP.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                h.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();

        FilteredHosts.Clear();
        foreach (var h in results)
            FilteredHosts.Add(h);

        EmptyState.Visibility = FilteredHosts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── 호스트 추가 (React의 setHosts([...hosts, newHost])) ──

    private void AddHost_Click(object sender, RoutedEventArgs e)
    {
        // TODO: 다이얼로그를 띄워 입력 받은 뒤 추가
        // var newHost = await ShowAddHostDialog();
        // _allHosts.Add(newHost);
        // ApplyFilter(SearchBox.Text);
    }

    // ── 외부에서 호스트를 추가/삭제할 수 있는 public API ──

    public void AddHost(HostInfoModel host)
    {
        _allHosts.Add(host);
        ApplyFilter(SearchBox.Text);
    }

    public void RemoveHost(HostInfoModel host)
    {
        _allHosts.Remove(host);
        ApplyFilter(SearchBox.Text);
    }
}