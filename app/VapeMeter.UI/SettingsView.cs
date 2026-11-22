using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VapeMeter.Core;
using static VapeMeter.UI.VapeVisuals;

namespace VapeMeter.UI;

public sealed partial class MainView
{
    private static readonly FilePickerFileType JsonHistory = new("История Vape Meter")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };
    private static readonly FilePickerFileType SqliteSnapshot = new("Резервная копия SQLite")
    {
        Patterns = ["*.sqlite", "*.db"],
        MimeTypes = ["application/vnd.sqlite3"],
    };

    private Control BuildSettings()
    {
        var theme = ActionButton(IsLightTheme ? "Включить тёмную" : "Включить светлую",
            ToggleTheme, icon: IsLightTheme ? "moon" : "sun", id: "VapeMeter.ThemeToggle");
        var appearanceSettings = Column(12,
            Heading("settings", "Оформление и окно", "Персональные параметры"),
            SettingRow("Тема оформления", IsLightTheme ? "Светлая тема" : "Тёмная тема", theme));
        if (_getCloseToTray is not null && _setCloseToTray is not null)
        {
            appearanceSettings.Children.Add(Rule());
            appearanceSettings.Children.Add(BuildCloseBehavior());
        }
        var appearance = Card(appearanceSettings);

        var export = AsyncButton("Экспорт", () => SaveCopyAsync(false), icon: "file", id: "VapeMeter.Export");
        var import = AsyncButton("Импорт", () => LoadCopyAsync(false), id: "VapeMeter.Import");
        var backup = AsyncButton("Сохранить копию", () => SaveCopyAsync(true), icon: "file", id: "VapeMeter.Backup");
        var restore = AsyncButton("Восстановить", () => LoadCopyAsync(true), id: "VapeMeter.Restore");
        var data = Card(Column(12,
            Heading("layers", "Данные", "Локальный журнал замен"),
            SettingRow("История в JSON", "Для переноса истории и добавления дат из файла", Wrap(export, import)),
            Rule(),
            SettingRow("Резервная копия", "Снимок SQLite. Восстановление объединяет историю с текущими записями.",
                Wrap(backup, restore))));
        return Column(12, appearance, data, BuildSyncSettings());
    }

    private Control BuildCloseBehavior()
    {
        var closeToTray = _getCloseToTray!.Invoke();
        var title = Text("При нажатии на крестик", 15, FontWeight.SemiBold, wrap: true);
        AutomationProperties.SetAutomationId(title, "VapeMeter.CloseBehavior");
        RadioButton Choice(string caption, bool toTray, string id)
        {
            var choice = new RadioButton
            {
                Content = caption,
                GroupName = "VapeMeter.CloseBehavior",
                IsChecked = closeToTray == toTray,
            };
            AutomationProperties.SetAutomationId(choice, id);
            choice.PropertyChanged += (_, change) =>
            {
                if (change.Property != RadioButton.IsCheckedProperty || choice.IsChecked != true)
                {
                    return;
                }

                try
                {
                    if (_getCloseToTray!.Invoke() != toTray)
                    {
                        _setCloseToTray!.Invoke(toTray);
                    }
                }
                catch (Exception error)
                {
                    ShowError(error);
                    Refresh();
                }
            };
            return choice;
        }

        return SideBySide(title, Wrap(
            Choice("Свернуть в трей", toTray: true, "VapeMeter.CloseToTray"),
            Choice("Выйти из приложения", toTray: false, "VapeMeter.CloseToExit")));
    }

    private Control BuildSyncSettings()
    {
        var status = _store.GetSyncStatus();
        var caption = _synchronize is null ? "Этот экземпляр работает локально" : "Timeweb · vapemeter.stalsky.org";
        var body = Column(11, Heading("sync", "Синхронизация", caption, mint: true));
        var lastSync = status.LastSynced is DateTimeOffset last
            ? last.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", Russian)
            : "Ещё не выполнялась";
        var description = Column(5,
            Text($"Последний обмен: {lastSync}", 13, wrap: true),
            Text($"В очереди: {status.PendingCount}", 12, tone: "muted"));
        if (_syncError is not null)
        {
            description.Children.Add(Text(_syncError, 12, tone: "danger", wrap: true));
        }

        var sync = AsyncButton(_syncBusy ? "Синхронизация…" : "Синхронизировать", () => SynchronizeAsync(manual: true),
            icon: "sync", id: "VapeMeter.SyncNow");
        sync.IsEnabled = _synchronize is not null && !_syncBusy;
        body.Children.Add(Inset(SideBySide(description, sync)));
        if (_openPlatformSettings is { } openSettings)
        {
            body.Children.Add(ActionButton("Подключение", openSettings, icon: "settings",
                id: "VapeMeter.ConnectionSettings"));
        }

        foreach (var conflict in status.Conflicts)
        {
            body.Children.Add(BuildConflict(conflict));
        }
        return Card(body);
    }

    private Control BuildConflict(SyncConflict conflict)
    {
        var actions = Wrap(
            ActionButton("Записать локальную версию в Timeweb", () => ResolveConflict(conflict, true),
                id: "VapeMeter.KeepLocal." + conflict.OperationId),
            ActionButton("Взять версию Timeweb", () => ResolveConflict(conflict, false),
                id: "VapeMeter.KeepServer." + conflict.OperationId));
        return Inset(Column(7, DescribeConflict(conflict), actions));
    }

    private static Control DescribeConflict(SyncConflict conflict)
    {
        var reason = conflict.Code switch
        {
            "duplicate_date" => "Эта дата уже отмечена на сервере",
            "dataset_changed" => "На сервере восстановлена другая история",
            "version_conflict" => "Запись изменилась на другом устройстве",
            _ => "Нужно согласовать изменение с сервером",
        };
        var local = conflict.LocalDate is DateOnly localDate
            ? $"отметить замену {DateText(localDate)}" : "убрать замену";
        var remote = conflict.TimewebRecord switch
        {
            { Deleted: true } removed => $"замена за {DateText(removed.Date)} убрана",
            { } record => $"замена за {DateText(record.Date)} отмечена",
            _ => "записи нет",
        };
        var details = Column(6, Text(reason, 14, FontWeight.SemiBold, wrap: true));
        if (conflict.PreviousDate is { } previous)
        {
            details.Children.Add(Text($"Исходная дата: {DateText(previous)}", 13, tone: "muted", wrap: true));
        }
        details.Children.Add(Text($"Локально: {local}\nВ Timeweb: {remote}", 13, wrap: true));
        if (conflict.OccupyingRecord is { } occupant)
        {
            var message = $"В Timeweb уже есть отдельная замена за {DateText(occupant.Date)}.";
            if (conflict.KeepLocalRemovesSource && conflict.TimewebRecord is { } source)
            {
                message += $" Локальный вариант уберёт в Timeweb замену за {DateText(source.Date)}.";
            }
            details.Children.Add(Text(message, 13, tone: "muted", wrap: true));
        }
        return details;
    }

    private void ResolveConflict(SyncConflict conflict, bool keepLocal)
    {
        try
        {
            _store.ResolveConflict(conflict, keepLocal);
            AfterChange(keepLocal ? "Выбран локальный вариант" : "Принята версия Timeweb");
        }
        catch (Exception error)
        {
            Refresh();
            ShowError(error);
        }
    }

    private Control SettingRow(string title, string description, Control action) =>
        SideBySide(Column(3, Text(title, 15, FontWeight.SemiBold),
            Text(description, 12, tone: "muted", wrap: true)), action);

    private async Task SaveCopyAsync(bool backup)
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is not { CanSave: true })
            {
                ShowNotice("На этой платформе не удалось открыть выбор файла.", error: true);
                return;
            }
            using var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = backup ? "Сохранить резервную копию" : "Экспортировать историю",
                SuggestedFileName = $"vapemeter-{DateTime.Now:yyyy-MM-dd-HHmmss}",
                DefaultExtension = backup ? "sqlite" : "json",
                FileTypeChoices = [backup ? SqliteSnapshot : JsonHistory],
                ShowOverwritePrompt = true,
            });
            if (file is null)
            {
                return;
            }
            await WithCopyAsync(file, write: true, path =>
            {
                if (File.Exists(path))
                {
                    throw new IOException("Выберите новое имя файла, чтобы сохранить существующую копию.");
                }
                if (backup)
                {
                    _store.Backup(path);
                }
                else
                {
                    _store.Export(path);
                }
            });
            ShowNotice(backup ? "Резервная копия сохранена" : "История экспортирована");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async Task LoadCopyAsync(bool backup)
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is not { CanOpen: true })
            {
                ShowNotice("На этой платформе не удалось открыть выбор файла.", error: true);
                return;
            }
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = backup ? "Восстановить резервную копию" : "Импортировать историю",
                AllowMultiple = false,
                FileTypeFilter = [backup ? SqliteSnapshot : JsonHistory],
            });
            if (files.Count == 0)
            {
                return;
            }
            using var file = files[0];
            var imported = 0;
            await WithCopyAsync(file, write: false, path =>
            {
                if (backup)
                {
                    _store.Restore(path);
                }
                else
                {
                    imported = _store.Import(path);
                }
            });
            AfterChange(backup ? "Даты восстановлены из резервной копии" : $"Импортировано записей: {imported}");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private static async Task WithCopyAsync(IStorageFile file, bool write, Action<string> useFile)
    {
        if (file.TryGetLocalPath() is { } path)
        {
            await Task.Run(() => useFile(path));
            return;
        }

        var temporary = Directory.CreateTempSubdirectory("vapemeter-file-");
        var copy = Path.Combine(temporary.FullName, "transfer");
        try
        {
            if (write)
            {
                await Task.Run(() => useFile(copy));
                await using var source = File.OpenRead(copy);
                await using var output = await file.OpenWriteAsync();
                if (output.CanSeek)
                {
                    output.SetLength(0);
                }
                await source.CopyToAsync(output);
            }
            else
            {
                await using (var source = await file.OpenReadAsync())
                await using (var output = File.Create(copy))
                {
                    await source.CopyToAsync(output);
                }
                await Task.Run(() => useFile(copy));
            }
        }
        finally
        {
            File.Delete(copy);
            temporary.Delete();
        }
    }
}
