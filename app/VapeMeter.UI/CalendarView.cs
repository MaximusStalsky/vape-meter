using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VapeMeter.Analytics;
using VapeMeter.Core;
using static VapeMeter.UI.VapeVisuals;

namespace VapeMeter.UI;

public sealed partial class MainView
{
    private static readonly string[] Weekdays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
    private static readonly DateOnly FirstAllowedDate = new(1900, 1, 1);
    private IInputElement? _dialogOriginalFocus;

    private Control BuildCalendar()
    {
        var period = Period;
        var recordsByDay = _records.ToDictionary(record => record.Date);
        var headline = _periodOffset == 0 ? "Последние 30 дней" : "Предыдущие 30 дней";
        var header = Heading("calendar", headline, DateRange(period));
        var periodButtons = PeriodButtons();

        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        headings.Children.Add(header);
        Grid.SetColumn(periodButtons, 1);
        headings.Children.Add(periodButtons);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"),
            ColumnSpacing = 5,
            RowSpacing = IsCondensedOverview ? 4 : 5,
        };
        AutomationProperties.SetName(grid, "Календарь замен");
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < Weekdays.Length; i++)
        {
            var label = Text(Weekdays[i], 11, FontWeight.SemiBold, i >= 5 ? "accent" : "muted");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }

        _selectedDateButton = null;
        var place = period.MondayOffset;
        foreach (var date in period.Dates())
        {
            var row = place / 7 + 1;
            if (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var record = recordsByDay.GetValueOrDefault(date);
            var day = BuildCalendarDay(date, record);
            Grid.SetRow(day, row);
            Grid.SetColumn(day, place % 7);
            grid.Children.Add(day);
            place++;
        }

        var calendar = Column(IsCondensedOverview ? 8 : 11, headings, grid);
        if (!_mobileOverview)
        {
            calendar.Children.Add(BuildSelectedDate(recordsByDay.GetValueOrDefault(_selectedDate)));
        }
        return OverviewCard(calendar, "VapeMeter.Calendar");
    }

    private Control PeriodButtons()
    {
        var previous = IconButton("Предыдущие 30 дней", "left", () => ShiftPeriod(1), "VapeMeter.PreviousPeriod");
        previous.IsEnabled = Period.Start > FirstAllowedDate.AddDays(30);
        var next = IconButton("Следующие 30 дней", "right", () => ShiftPeriod(-1), "VapeMeter.NextPeriod");
        next.IsEnabled = _periodOffset > 0;
        var row = Row(5, previous, next);
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    private void ShiftPeriod(int shift)
    {
        var offset = _periodOffset + shift;
        if (offset < 0 || CalendarPeriod.ForOffset(Today, offset).Start < FirstAllowedDate)
        {
            return;
        }
        _periodOffset = offset;
        _selectedDate = Period.End;
        Refresh();
        Dispatcher.UIThread.Post(() => _selectedDateButton?.Focus(), DispatcherPriority.Loaded);
    }

    private Button BuildCalendarDay(DateOnly date, Replacement? record)
    {
        var selected = date == _selectedDate;
        var title = DateText(date);
        var description = title + (record is null ? "" : ", есть замена") + (date == Today ? ", сегодня" : "");
        var tile = new Button();
        tile.Classes.Add("vm-button");
        tile.Classes.Add("day");
        AutomationProperties.SetName(tile, description);
        AutomationProperties.SetAutomationId(tile, "VapeMeter.Day." + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        tile.MinHeight = IsCondensedOverview ? 33 : _layout == LayoutMode.Wide ? 43 : 38;
        tile.HorizontalAlignment = HorizontalAlignment.Stretch;
        tile.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        tile.VerticalContentAlignment = VerticalAlignment.Stretch;
        tile.Classes.Set("weekend", date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        tile.Classes.Set("recorded", record is not null);
        tile.Classes.Set("selected", selected);
        tile.Classes.Set("today", date == Today);
        if (selected)
        {
            _selectedDateButton = tile;
        }

        var face = new Grid();
        face.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(Russian),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (record is not null)
        {
            var marker = Glyph("pod", 11, selected ? "white" : "");
            marker.HorizontalAlignment = HorizontalAlignment.Right;
            marker.VerticalAlignment = VerticalAlignment.Bottom;
            marker.Margin = new Thickness(0, 0, 3, 2);
            face.Children.Add(marker);
        }
        if (date == Today)
        {
            var marker = Dot();
            marker.Width = marker.Height = 5;
            marker.HorizontalAlignment = HorizontalAlignment.Left;
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(4, 3, 0, 0);
            face.Children.Add(marker);
        }
        tile.Content = face;
        if (_mobileOverview)
        {
            tile.Click += (_, _) =>
            {
                _selectedDate = date;
                _selectedDateButton?.Classes.Remove("selected");
                _selectedDateButton = tile;
                tile.Classes.Add("selected");
            };
            var remove = record is not null;
            var action = DateMenuItem(remove ? "Убрать замену" : "Добавить замену", () => ApplyMobileDateAction(date, remove));
            AutomationProperties.SetAutomationId(action, "VapeMeter.MobileDateAction");
            tile.Flyout = new MenuFlyout { Items = { action } };
            return tile;
        }

        tile.Click += (_, _) =>
        {
            _selectedDate = date;
            Refresh();
            Dispatcher.UIThread.Post(() => _selectedDateButton?.Focus(), DispatcherPriority.Loaded);
        };
        var menu = new ContextMenu();
        if (record is null)
        {
            menu.Items.Add(DateMenuItem("Добавить замену", () => EditDate(null, date)));
        }
        else
        {
            menu.Items.Add(DateMenuItem("Изменить дату", () => EditDate(record, date)));
            menu.Items.Add(DateMenuItem("Удалить запись", () => ConfirmDelete(record)));
        }
        tile.ContextMenu = menu;
        ToolTip.SetTip(tile, description);
        return tile;
    }

    private void ApplyMobileDateAction(DateOnly date, bool remove)
    {
        try
        {
            if (remove)
            {
                var current = _store.GetReplacements().FirstOrDefault(record => record.Date == date);
                if (current is null)
                {
                    Refresh();
                    return;
                }
                _store.DeleteReplacement(current.Id);
                AfterChange("Замена убрана");
            }
            else
            {
                _store.AddReplacement(date);
                AfterChange("Замена добавлена");
            }
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private static MenuItem DateMenuItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    private Control BuildSelectedDate(Replacement? record)
    {
        var status = record is null ? "Замен не отмечено" : "Замена отмечена";
        var summary = Heading(record is null ? "calendar" : "pod", DateText(_selectedDate), status, record is not null);
        var actions = record is null
            ? Wrap(ActionButton("Добавить", () => EditDate(null, _selectedDate), "primary", "plus", "VapeMeter.AddSelected"))
            : Wrap(
                IconButton("Изменить дату", "edit", () => EditDate(record, _selectedDate), "VapeMeter.EditSelected"),
                IconButton("Удалить запись", "trash", () => ConfirmDelete(record), "VapeMeter.DeleteSelected"));
        var panel = Inset(SideBySide(summary, actions));
        if (IsCondensedOverview)
        {
            panel.Padding = new Thickness(10, 5);
        }
        return panel;
    }

    private Control SideBySide(Control left, Control right)
    {
        if (IsCompact)
        {
            return Column(8, left, right);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private Control BuildHistory()
    {
        var body = Column(10, Heading("history", "Журнал замен", $"Всего записей: {_records.Count}"));
        if (_records.Count == 0)
        {
            body.Children.Add(Inset(Text("В журнале пока нет замен", 14, tone: "muted", wrap: true)));
            return Card(body);
        }

        var ordered = _records.OrderBy(record => record.Date).ToArray();
        for (var i = ordered.Length - 1; i >= 0; i--)
        {
            var record = ordered[i];
            var details = i == 0 ? "Первая отметка" : $"Предыдущий картридж: {Duration(record.Date.DayNumber - ordered[i - 1].Date.DayNumber)}";
            var tools = Wrap(
                IconButton("Изменить дату", "edit", () => EditDate(record, record.Date), "VapeMeter.HistoryEdit." + record.Id),
                IconButton("Удалить запись", "trash", () => ConfirmDelete(record), "VapeMeter.HistoryDelete." + record.Id));
            body.Children.Add(Inset(SideBySide(Heading("pod", DateText(record.Date), details), tools)));
        }
        return Card(body);
    }

    private void EditDate(Replacement? record, DateOnly date)
    {
        var picker = new CalendarDatePicker
        {
            FirstDayOfWeek = DayOfWeek.Monday,
            DisplayDateStart = FirstAllowedDate.ToDateTime(TimeOnly.MinValue),
            DisplayDateEnd = Today.ToDateTime(TimeOnly.MinValue),
            SelectedDate = date.ToDateTime(TimeOnly.MinValue),
            SelectedDateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "dd.MM.yyyy",
            PlaceholderText = "дд.мм.гггг",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(picker, "Дата замены");
        AutomationProperties.SetAutomationId(picker, "VapeMeter.EditorDate");
        var warning = Text("", 13, tone: "danger", wrap: true);
        warning.IsVisible = false;
        var editor = Column(7, Text("Дата замены", 13, FontWeight.SemiBold), picker, warning);
        var save = ActionButton("Сохранить", () =>
        {
            if (!DateOnly.TryParseExact(picker.Text?.Trim(), "dd.MM.yyyy", Russian,
                    System.Globalization.DateTimeStyles.None, out var entered)
                || entered < FirstAllowedDate || entered > Today)
            {
                warning.Text = "Выберите дату от 01.01.1900 до сегодняшнего дня.";
                warning.IsVisible = true;
                return;
            }

            try
            {
                if (record is null)
                {
                    _store.AddReplacement(entered);
                }
                else
                {
                    _store.ChangeReplacement(record.Id, entered);
                }
                _selectedDate = entered;
                _periodOffset = (Today.DayNumber - entered.DayNumber) / 30;
                DismissDialog();
                AfterChange(record is null ? "Замена добавлена" : "Дата замены изменена");
            }
            catch (Exception error)
            {
                warning.Text = error.Message;
                warning.IsVisible = true;
            }
        }, "primary", id: "VapeMeter.EditorSave");
        ShowDialog(record is null ? "Добавить замену" : "Изменить дату", editor, save, picker);
    }

    private void ConfirmDelete(Replacement record)
    {
        var body = Text($"Удалить запись за {DateText(record.Date)}?", 14, tone: "muted", wrap: true);
        var delete = ActionButton("Удалить", () =>
        {
            try
            {
                _store.DeleteReplacement(record.Id);
                DismissDialog();
                AfterChange("Запись удалена");
            }
            catch (Exception error)
            {
                DismissDialog();
                ShowError(error);
            }
        }, "danger", "trash", "VapeMeter.ConfirmDelete");
        ShowDialog("Удалить замену", body, delete);
    }

    private void ShowDialog(string title, Control content, Button action, Control? focus = null)
    {
        var cancel = ActionButton("Отмена", DismissDialog, id: "VapeMeter.DialogCancel");
        var actions = Row(7, cancel, action);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        OpenDialog(title, content, actions, focus ?? cancel);
    }

    private void OpenDialog(string title, Control content, Control actions, Control focus)
    {
        _dialogOriginalFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var card = Card(Column(13, Text(title, 19, FontWeight.SemiBold), content, actions), "VapeMeter.Dialog");
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Width = Math.Min(408, Math.Max(280, Bounds.Width - 32));
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);

        _overlay.Children.Clear();
        _overlay.Children.Add(card);
        _scroll.IsEnabled = false;
        _sidebar.IsEnabled = false;
        _overlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => focus.Focus(), DispatcherPriority.Loaded);
    }

    private void DismissDialog()
    {
        _overlay.IsVisible = false;
        _overlay.Children.Clear();
        _scroll.IsEnabled = true;
        _sidebar.IsEnabled = true;
        _dialogOriginalFocus?.Focus();
        _dialogOriginalFocus = null;
        Dispatcher.UIThread.Post(TryOfferSyncConflict);
    }
}
