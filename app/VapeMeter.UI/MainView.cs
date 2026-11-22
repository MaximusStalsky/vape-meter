using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using VapeMeter.Analytics;
using VapeMeter.Core;
using static VapeMeter.UI.VapeVisuals;

namespace VapeMeter.UI;

public sealed partial class MainView : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly MeterStore _store;
    private readonly Func<CancellationToken, Task<SyncStatus>>? _synchronize;
    private readonly Func<bool>? _getCloseToTray;
    private readonly Action<bool>? _setCloseToTray;
    private readonly Action? _openPlatformSettings;
    private readonly bool _mobileOverview;
    private readonly Grid _workspace = new();
    private readonly Border _sidebar = new();
    private readonly Border _body = new();
    private readonly ScrollViewer _scroll;
    private readonly Image? _artwork;
    private readonly Grid _overlay = new() { IsVisible = false };
    private readonly Border _notice = new() { IsVisible = false, IsHitTestVisible = false };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly IReadOnlyList<Section> _sections;

    private CancellationTokenSource? _lifetime;
    private IReadOnlyList<Replacement> _records = [];
    private Section _section;
    private LayoutMode _layout = LayoutMode.Wide;
    private DateOnly _selectedDate = Today;
    private int _periodOffset;
    private bool _chartAll;
    private bool _shortViewport;
    private bool _syncBusy;
    private bool _syncQueued;
    private string? _syncError;
    private Button? _selectedDateButton;

    private enum LayoutMode { Wide, Rail, Compact }
    private sealed record Section(string Id, string Title, string Caption, string Icon, Func<Control> Build);

    public MainView(MeterStore store, Func<CancellationToken, Task<SyncStatus>>? synchronize = null,
        bool useHostBackdrop = false, Func<bool>? getCloseToTray = null, Action<bool>? setCloseToTray = null,
        Action? openPlatformSettings = null, bool mobileOverview = false)
    {
        _store = store;
        _synchronize = synchronize;
        _getCloseToTray = getCloseToTray;
        _setCloseToTray = setCloseToTray;
        _openPlatformSettings = openPlatformSettings;
        _mobileOverview = mobileOverview;
        _sections =
        [
            new("Overview", "Обзор", "Календарь замен и срок службы картриджей", "home", BuildOverview),
            new("History", "История", "Журнал замен картриджей", "history", BuildHistory),
            new("Analytics", "Аналитика", "Статистика и срок службы картриджей", "chart", BuildAnalytics),
            new("Settings", "Настройки", "Оформление, данные и синхронизация", "settings", BuildSettings),
        ];
        _section = _sections[0];
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Noto Sans");
        FontSize = 14;

        _workspace.ColumnDefinitions = new ColumnDefinitions("176,*");
        _workspace.RowDefinitions = new RowDefinitions("*,Auto");
        _workspace.ClipToBounds = true;
        if (!useHostBackdrop)
        {
            _workspace.Classes.Add("vm-workspace");
            _artwork = new Image
            {
                Source = Backdrop.Value,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
                Opacity = .83,
            };
            SpanWorkspace(_artwork);
            _workspace.Children.Add(_artwork);
        }
        _sidebar.Classes.Add("vm-sidebar");
        _workspace.Children.Add(_sidebar);

        _body.MaxWidth = 1340;
        _body.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(_scroll, 1);
        _workspace.Children.Add(_scroll);

        _notice.Classes.Add("vm-card");
        _notice.Padding = new Thickness(13, 9);
        _notice.MaxWidth = 410;
        _notice.HorizontalAlignment = HorizontalAlignment.Right;
        _notice.VerticalAlignment = VerticalAlignment.Bottom;
        _notice.Margin = new Thickness(20);
        SpanWorkspace(_notice);
        _workspace.Children.Add(_notice);

        _overlay.Background = new SolidColorBrush(Color.FromArgb(170, 12, 6, 22));
        SpanWorkspace(_overlay);
        _workspace.Children.Add(_overlay);
        Content = _workspace;

        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice.IsVisible = false;
        };
        SizeChanged += OnViewportChanged;
        AttachedToVisualTree += (_, _) =>
        {
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
            RequestSync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _lifetime?.Cancel();
            _syncTimer.Stop();
            _noticeTimer.Stop();
        };
        _syncTimer.Tick += (_, _) => RequestSync();
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape && _overlay.IsVisible)
            {
                DismissDialog();
                args.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyTheme();
        ApplyLayout();
        Refresh(preserveScroll: false);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private CalendarPeriod Period => CalendarPeriod.ForOffset(Today, _periodOffset);
    private bool IsCompact => _layout == LayoutMode.Compact;
    private bool IsCondensedOverview => _section.Id == "Overview" && !IsCompact && _shortViewport;

    public bool IsLightTheme => _store.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
    public event EventHandler? ThemeChanged;

    public void ToggleTheme()
    {
        try
        {
            _store.Theme = IsLightTheme ? "Dark" : "Light";
            ApplyTheme();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            if (_section.Id == "Settings")
            {
                Refresh();
            }
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }



    private void ApplyTheme()
    {
        var light = IsLightTheme;
        if (_artwork is not null)
        {
            _artwork.IsVisible = !light;
        }
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }


    private void Navigate(Section section)
    {
        if (_section == section)
        {
            return;
        }
        _section = section;
        Refresh(preserveScroll: false);
    }

    private void OnViewportChanged(object? sender, SizeChangedEventArgs args)
    {
        var layout = args.NewSize.Width switch
        {
            < 710 => LayoutMode.Compact,
            < 1060 => LayoutMode.Rail,
            _ => LayoutMode.Wide,
        };
        var shortViewport = args.NewSize.Height < 1048;
        var widthChanged = layout != _layout;
        if (!widthChanged && (shortViewport == _shortViewport || _section.Id != "Overview" || IsCompact))
        {
            _shortViewport = shortViewport;
            return;
        }

        _layout = layout;
        _shortViewport = shortViewport;
        if (widthChanged)
        {
            ApplyLayout();
        }
        Refresh();
    }

    private void ApplyLayout()
    {
        _workspace.ColumnDefinitions[0].Width = new GridLength(_layout switch
        {
            LayoutMode.Wide => 176,
            LayoutMode.Rail => 68,
            _ => 0,
        });
        Grid.SetColumn(_scroll, IsCompact ? 0 : 1);
        Grid.SetColumnSpan(_scroll, IsCompact ? 2 : 1);
        Grid.SetRow(_sidebar, IsCompact ? 1 : 0);
        Grid.SetColumnSpan(_sidebar, IsCompact ? 2 : 1);
        _sidebar.BorderThickness = IsCompact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        _sidebar.Padding = IsCompact ? new Thickness(7, 5) : new Thickness(9, 18, 9, 12);

        _notice.Margin = new Thickness(15, 15, 15, IsCompact ? 69 : 15);
    }

    private void BuildNavigation()
    {

        if (IsCompact)
        {
            var row = new Grid { ColumnSpacing = 4 };
            for (var i = 0; i < _sections.Count; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var button = CreateNavButton(_sections[i]);
                Grid.SetColumn(button, i);
                row.Children.Add(button);
            }
            var syncStatus = BuildMobileSyncStatus();
            _sidebar.Child = syncStatus is null ? row : Column(4, syncStatus, row);
            return;
        }

        var stack = Column(5, _sections.Select(CreateNavButton).ToArray<Control>());
        var dock = new DockPanel();
        if (_layout == LayoutMode.Wide)
        {
            var status = StatusLabel();
            DockPanel.SetDock(status, Dock.Bottom);
            dock.Children.Add(status);
        }
        dock.Children.Add(stack);
        _sidebar.Child = dock;
    }

    private Button CreateNavButton(Section section)
    {
        var button = ActionButton(section.Title, () => Navigate(section), "nav", id: "VapeMeter." + section.Id);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Classes.Set("active", _section == section);
        ToolTip.SetTip(button, section.Title);
        var icon = Glyph(section.Icon, 17, _section == section ? "" : "muted");
        button.Content = _layout switch
        {
            LayoutMode.Wide => Row(9, icon, Text(section.Title, 13, FontWeight.SemiBold)),
            LayoutMode.Compact => Column(3, icon, Text(section.Title, 10, FontWeight.SemiBold)),
            _ => icon,
        };
        button.HorizontalContentAlignment = _layout == LayoutMode.Wide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        if (IsCompact)
        {
            button.Padding = new Thickness(2, 6);
            button.MinHeight = 47;
        }

        return button;
    }

    private Control StatusLabel()
    {
        var connected = _synchronize is not null;
        var status = _store.GetSyncStatus();
        var unavailable = connected && _syncError is not null;
        var note = !connected ? "Локальный режим" :
            unavailable ? status.PendingCount > 0 ? $"Недоступен\nВ очереди: {status.PendingCount}" : "Сервер недоступен" :
            status.Conflicts.Count > 0 ? $"Конфликтов: {status.Conflicts.Count}" :
            status.PendingCount > 0 ? $"В очереди: {status.PendingCount}" :
            _syncBusy ? "Синхронизация…" :
            status.LastSynced is null ? "Ожидает обмена" : "Синхронизировано";

        var marker = Dot();
        marker.Classes.Set("warning", unavailable || connected && status.Conflicts.Count > 0);
        marker.Classes.Set("pending", connected && !unavailable && status.PendingCount > 0 && status.Conflicts.Count == 0);
        marker.Margin = new Thickness(0, 5, 0, 0);
        marker.VerticalAlignment = VerticalAlignment.Top;

        var label = Text(connected ? "Timeweb" : "Хранилище", 12, FontWeight.SemiBold);
        AutomationProperties.SetAutomationId(label, "VapeMeter.SyncStatusLabel");
        var detail = Text(note, 11, tone: unavailable ? "danger" : "muted", wrap: true);
        AutomationProperties.SetAutomationId(detail, "VapeMeter.SyncStatusDetail");
        var text = Column(2, label, detail);
        Grid.SetColumn(text, 1);
        var line = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 7,
            Margin = new Thickness(5, 8, 0, 0),
            Children = { marker, text },
        };
        return Column(1, Rule(), line);
    }

    private void Refresh(bool preserveScroll = true)
    {
        var offset = preserveScroll ? _scroll.Offset : default;
        try
        {
            _records = _store.GetReplacements();
            BuildNavigation();
            AutomationProperties.SetAutomationId(_scroll, "VapeMeter." + _section.Id + "Scroll");
            var horizontal = _layout == LayoutMode.Wide ? 28 : 13;
            _body.Padding = IsCondensedOverview
                ? new Thickness(horizontal, 10, horizontal, 10)
                : _layout == LayoutMode.Wide ? new Thickness(horizontal, 20, horizontal, 25) : new Thickness(horizontal, 15, horizontal, 20);
            _body.Child = _mobileOverview && _section.Id == "Overview"
                ? _section.Build()
                : Column(IsCondensedOverview ? 9 : 14, Intro(), _section.Build());
            Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
        }
        catch (Exception error)
        {
            _body.Child = Card(Column(8,
                Text("Не удалось открыть данные", 18, FontWeight.SemiBold),
                Text(error.Message, tone: "muted", wrap: true),
                ActionButton("Повторить", () => Refresh())));
        }
    }

    private Control Intro()
    {
        var title = Text(_section.Title, IsCondensedOverview ? 32 : _layout == LayoutMode.Wide ? 36 : 29, FontWeight.Bold);
        AutomationProperties.SetAutomationId(title, "VapeMeter.PageTitle");
        var heading = Column(3, title, Text(_section.Caption, 13, tone: "muted", wrap: true));
        var add = _section.Id is "Overview" or "History"
            ? ActionButton("Добавить замену", () => EditDate(null, _selectedDate), "primary", "plus", "VapeMeter.Add")
            : null;
        if (add is not null)
        {
            add.MinHeight = IsCondensedOverview ? 36 : 40;
        }
        if (add is null)
        {
            return heading;
        }
        if (IsCompact)
        {
            return Column(8, heading, add);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        add.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(add, 1);
        grid.Children.Add(heading);
        grid.Children.Add(add);
        return grid;
    }

    private void AfterChange(string message)
    {
        Refresh();
        ShowNotice(message);
        RequestSync();
    }

    private void ShowError(Exception error) => ShowNotice(error.Message, error: true);

    private void ShowNotice(string message, bool error = false)
    {
        var notice = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 7 };
        var label = Text(message, 13, tone: error ? "danger" : "", wrap: true);
        notice.Children.Add(Glyph(error ? "close" : "check", 16, error ? "muted" : "mint"));
        Grid.SetColumn(label, 1);
        notice.Children.Add(label);
        _notice.Child = notice;
        AutomationProperties.SetName(_notice, message);
        _notice.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static void SpanWorkspace(Control control)
    {
        Grid.SetColumnSpan(control, 2);
        Grid.SetRowSpan(control, 2);
    }

    private static string DateText(DateOnly date) => date.ToString("d MMM yyyy", Russian);
    private static string ShortDate(DateOnly date) => date.ToString("d MMM", Russian);
    private static string DateRange(CalendarPeriod period) =>
        $"{(period.Start.Year == period.End.Year ? ShortDate(period.Start) : DateText(period.Start))} — {DateText(period.End)}";
}
