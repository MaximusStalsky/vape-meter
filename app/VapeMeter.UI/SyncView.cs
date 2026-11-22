using System.Net;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VapeMeter.Core;
using static VapeMeter.UI.VapeVisuals;

namespace VapeMeter.UI;

public sealed partial class MainView
{
    private static readonly TimeSpan ForegroundSyncInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _syncTimer = new();
    private bool _syncActive = true;
    private bool _syncKeyRejected;
    private int _syncFailures;
    private SyncConflict? _offeredSyncConflict;
    private bool _savedKeyUnavailable;

    public void MarkSavedKeyUnavailable()
    {
        _savedKeyUnavailable = true;
        BuildNavigation();
    }

    public void SetSyncActive(bool active)
    {
        if (_syncActive == active)
        {
            return;
        }
        _syncActive = active;
        if (active)
        {
            RequestSync();
            TryOfferSyncConflict();
        }
        else
        {
            _syncTimer.Stop();
        }
    }

    public void RequestSync()
    {
        if (_syncActive)
        {
            _ = SynchronizeAsync();
        }
    }

    private async Task SynchronizeAsync(bool manual = false)
    {
        if (_synchronize is null || _lifetime is not { IsCancellationRequested: false } lifetime || !_syncActive)
        {
            return;
        }
        _syncTimer.Stop();
        if (_syncBusy)
        {
            _syncQueued = true;
            return;
        }

        _syncBusy = true;
        var oldError = _syncError;
        try
        {
            do
            {
                _syncQueued = false;
                await Task.Run(() => _synchronize(lifetime.Token), lifetime.Token);
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }
                _syncError = null;
                _syncKeyRejected = false;
                _syncFailures = 0;
            }
            while (_syncQueued && _syncActive);

            if (manual)
            {
                ShowNotice("Синхронизация завершена");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested)
            {
                _syncFailures = Math.Min(_syncFailures + 1, 4);
                _syncKeyRejected = error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
                _syncError = _syncKeyRejected
                    ? "Timeweb отклонил ключ. Изменения сохранены локально."
                    : "Нет связи с Timeweb. Изменения сохранены локально.";
                if (_syncActive && (manual || _mobileOverview && _syncError != oldError))
                {
                    ShowNotice(_syncError, error: true);
                }
            }
        }
        finally
        {
            _syncBusy = false;
            if (!lifetime.IsCancellationRequested)
            {
                ScheduleNextSync();
                UpdateSyncedView();
            }
            else if (!ReferenceEquals(_lifetime, lifetime))
            {
                RequestSync();
            }
        }
    }

    private void UpdateSyncedView()
    {
        try
        {
            if (!_mobileOverview || _section.Id == "Settings" || !_records.SequenceEqual(_store.GetReplacements()))
            {
                Refresh();
            }
            else
            {
                BuildNavigation();
            }
            TryOfferSyncConflict();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void ScheduleNextSync()
    {
        if (!_mobileOverview || !_syncActive || _syncKeyRejected)
        {
            return;
        }
        _syncTimer.Interval = _syncFailures == 0
            ? ForegroundSyncInterval
            : TimeSpan.FromSeconds(Math.Min(4 << (_syncFailures - 1), ForegroundSyncInterval.TotalSeconds));
        _syncTimer.Start();
    }

    private Control? BuildMobileSyncStatus()
    {
        if (!_mobileOverview)
        {
            return null;
        }
        if (_savedKeyUnavailable && _openPlatformSettings is { } openSettings)
        {
            return SyncStatusButton("Проверьте сохранённый ключ Timeweb", openSettings, "VapeMeter.SyncKeyUnavailable");
        }
        if (_synchronize is null)
        {
            return null;
        }
        if (_syncError is not null)
        {
            var label = _syncKeyRejected ? "Ключ Timeweb отклонён" : "Нет связи с Timeweb · работа локально";
            return SyncStatusButton(label, ShowSyncUnavailable, "VapeMeter.SyncUnavailable");
        }
        if (_store.GetSyncStatus().Conflicts.FirstOrDefault() is { } conflict)
        {
            return SyncStatusButton("Есть расхождения с Timeweb", () => ShowSyncConflict(conflict),
                "VapeMeter.SyncConflicts");
        }
        return null;
    }

    private static Button SyncStatusButton(string title, Action action, string id)
    {
        var button = ActionButton(title, action, "danger", id: id);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.FontSize = 12;
        button.MinHeight = 34;
        return button;
    }

    private void ShowSyncUnavailable()
    {
        var local = ActionButton("Продолжить локально", DismissDialog, id: "VapeMeter.StayOffline");
        var connect = ActionButton(_syncKeyRejected ? "Настройки подключения" : "Повторить", () =>
        {
            DismissDialog();
            if (_syncKeyRejected && _openPlatformSettings is not null)
            {
                _openPlatformSettings();
            }
            else
            {
                _ = SynchronizeAsync(manual: true);
            }
        }, "primary", id: "VapeMeter.RetrySync");
        var message = _syncKeyRejected
            ? "История и новые изменения сохраняются на телефоне. Откройте настройки подключения, чтобы проверить ключ."
            : "История и новые изменения сохраняются на телефоне. Приложение отправит изменения при восстановлении связи.";
        OpenDialog(_syncKeyRejected ? "Ключ Timeweb отклонён" : "Нет связи с Timeweb",
            Text(message, 14, tone: "muted", wrap: true), Column(7, connect, local), local);
    }

    private void TryOfferSyncConflict()
    {
        if (!_mobileOverview || _synchronize is null || !_syncActive || !IsVisible || _overlay.IsVisible
            || _lifetime is not { IsCancellationRequested: false })
        {
            return;
        }
        if (_store.GetSyncStatus().Conflicts.FirstOrDefault() is { } conflict && conflict != _offeredSyncConflict)
        {
            ShowSyncConflict(conflict);
        }
    }

    private void ShowSyncConflict(SyncConflict conflict)
    {
        _offeredSyncConflict = conflict;
        var local = ActionButton("Записать с телефона в Timeweb", () => ResolveMobileConflict(conflict, true),
            "primary", id: "VapeMeter.SyncKeepLocal");
        var server = ActionButton("Взять версию Timeweb", () => ResolveMobileConflict(conflict, false),
            id: "VapeMeter.SyncKeepServer");
        var later = ActionButton("Позже", DismissDialog, id: "VapeMeter.SyncDecideLater");
        OpenDialog("Расхождение с Timeweb", DescribeConflict(conflict), Column(7, local, server, later), later);
    }

    private void ResolveMobileConflict(SyncConflict conflict, bool keepLocal)
    {
        DismissDialog();
        ResolveConflict(conflict, keepLocal);
    }
}
