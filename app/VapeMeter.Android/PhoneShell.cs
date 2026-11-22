using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VapeMeter.Core;
using VapeMeter.UI;

namespace VapeMeter.Android;

internal sealed class PhoneShell : Border
{
    private readonly MeterStore _store;
    private readonly AndroidCredentials _credentials;
    private readonly TimewebConnection _connection;
    private readonly Grid _content = new();
    private MainView? _journal;
    private Control? _setup;
    private TextBox? _keyEntry;
    private CancellationTokenSource? _verification;
    private TopLevel? _topLevel;
    private IInputPane? _inputPane;

    public PhoneShell(MeterStore store, AndroidCredentials credentials, TimewebConnection connection)
    {
        _store = store;
        _credentials = credentials;
        _connection = connection;
        TopLevel.SetAutoSafeAreaPadding(this, true);
        Child = _content;

        AttachedToVisualTree += (_, _) => AttachWindow();
        DetachedFromVisualTree += (_, _) => DetachWindow();
        SizeChanged += (_, _) => UpdateKeyboardPadding();

        try
        {
            var (configured, token) = _credentials.Load();
            if (configured)
            {
                ShowJournal(token);
            }
            else
            {
                ShowConnection();
            }
        }
        catch (Exception)
        {
            ShowConnection("Сохранённый ключ недоступен. Подключите Android заново.");
        }
    }

    private void ShowJournal(string? token)
    {
        CloseSetup();
        if (_journal is not null)
        {
            _content.Children.Remove(_journal);
        }

        Func<CancellationToken, Task<SyncStatus>>? synchronize =
            token is null ? null : _connection.MakeSync(_store, token).SyncAsync;
        _journal = new MainView(_store, synchronize, openPlatformSettings: () => ShowConnection());
        _content.Children.Insert(0, _journal);
    }

    private void ShowConnection(string? initialError = null)
    {
        if (_setup is not null)
        {
            return;
        }
        if (_journal is not null)
        {
            _journal.IsVisible = false;
        }

        var error = Label(initialError ?? "", 13, "danger");
        error.IsVisible = initialError is not null;
        var keyEntry = new TextBox
        {
            PlaceholderText = "Ключ Android",
            PasswordChar = '●',
            MaxLength = 121,
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _keyEntry = keyEntry;
        AutomationProperties.SetName(keyEntry, "Ключ Android");
        AutomationProperties.SetAutomationId(keyEntry, "VapeMeter.AndroidKey");

        var connect = ButtonFor("Подключить Timeweb", "VapeMeter.ConnectAndroid", primary: true);
        var offline = ButtonFor("Работать автономно", "VapeMeter.OfflineAndroid");
        connect.Click += async (_, _) =>
        {
            if (_verification is not null)
            {
                return;
            }
            var key = keyEntry.Text?.Trim() ?? "";
            if (!TimewebConnection.IsAndroidKey(key))
            {
                SetError(error, "Вставьте ключ для Android, начинающийся с android.");
                return;
            }

            using var verification = new CancellationTokenSource();
            _verification = verification;
            error.IsVisible = false;
            keyEntry.IsEnabled = false;
            connect.IsEnabled = false;
            connect.Content = "Проверяем ключ…";
            offline.IsEnabled = false;
            try
            {
                await _connection.VerifyAsync(key, verification.Token);
                verification.Token.ThrowIfCancellationRequested();
                try
                {
                    _credentials.Save(key);
                }
                catch (Exception)
                {
                    SetError(error, "Не удалось сохранить ключ в Android Keystore.");
                    return;
                }

                keyEntry.Text = "";
                ShowJournal(key);
            }
            catch (OperationCanceledException) when (verification.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                SetError(error, "Timeweb отклонил ключ. Проверьте, что вставили ключ Android целиком.");
            }
            catch (Exception)
            {
                SetError(error, "Не удалось проверить ключ. Проверьте интернет и попробуйте ещё раз.");
            }
            finally
            {
                if (ReferenceEquals(_verification, verification))
                {
                    _verification = null;
                    connect.Content = "Подключить Timeweb";
                    connect.IsEnabled = true;
                    offline.IsEnabled = true;
                    keyEntry.IsEnabled = true;
                }
            }
        };

        offline.Click += (_, _) =>
        {
            try
            {
                _credentials.UseOffline();
                ShowJournal(null);
            }
            catch (Exception)
            {
                SetError(error, "Не удалось сохранить режим работы. Попробуйте ещё раз.");
            }
        };

        var title = Label("Vape Meter", 28, "accent");
        title.FontWeight = FontWeight.Bold;
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                title,
                Label("Подключение к Timeweb", 18),
                Label("Вставьте выданный вам ключ Android. После подключения здесь появятся ваши записи.", 14, "muted"),
                keyEntry, error, connect, offline,
            },
        };
        if (_journal is not null)
        {
            var cancel = ButtonFor("Вернуться к журналу", "VapeMeter.CancelConnection");
            cancel.Click += (_, _) => CloseSetup();
            content.Children.Add(cancel);
        }

        var card = new Border { Child = content, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Stretch };
        card.Classes.Add("vm-card");
        var screen = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                Padding = new Thickness(16, 24),
                Child = card,
            },
        };
        var page = new Grid { Children = { screen } };
        page.Classes.Add("vm-workspace");
        _setup = page;
        _content.Children.Add(page);
    }

    private void CloseSetup()
    {
        _verification?.Cancel();
        _verification = null;
        if (_keyEntry is { } keyEntry)
        {
            keyEntry.Text = "";
            _keyEntry = null;
        }
        if (_setup is { } setup)
        {
            _content.Children.Remove(setup);
            _setup = null;
        }
        if (_journal is not null)
        {
            _journal.IsVisible = true;
        }
    }

    private void AttachWindow()
    {
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
        {
            return;
        }

        if (_topLevel.InsetsManager is { } insets)
        {
            insets.DisplayEdgeToEdgePreference = true;
        }
        _topLevel.BackRequested += OnBackRequested;
        _inputPane = _topLevel.InputPane;
        if (_inputPane is not null)
        {
            _inputPane.StateChanged += OnInputPaneChanged;
        }
        ActualThemeVariantChanged += OnThemeChanged;
        UpdateSystemBars();
        UpdateKeyboardPadding();
    }

    private void DetachWindow()
    {
        _verification?.Cancel();
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
        }
        if (_inputPane is not null)
        {
            _inputPane.StateChanged -= OnInputPaneChanged;
        }
        ActualThemeVariantChanged -= OnThemeChanged;
        _topLevel = null;
        _inputPane = null;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs args)
    {
        if (_setup is not null && _journal is not null)
        {
            CloseSetup();
            args.Handled = true;
            return;
        }

        if (_journal is { IsVisible: true } journal)
        {
            var dismiss = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape };
            journal.RaiseEvent(dismiss);
            args.Handled = dismiss.Handled;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs args) => UpdateSystemBars();

    private void UpdateSystemBars()
    {
        if (_topLevel?.InsetsManager is { } insets)
        {
            insets.SystemBarColor = _store.Theme == "Light" ? Color.Parse("#F9F6FE") : Color.Parse("#110D1C");
        }
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs args)
    {
        UpdateKeyboardPadding();
        if (_keyEntry is { IsFocused: true } entry)
        {
            Dispatcher.UIThread.Post(() => entry.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    private void UpdateKeyboardPadding()
    {
        var bottomPadding = _topLevel is { } top
            && _inputPane is { State: InputPaneState.Open } input
            && input.OccludedRect.Height > 0
            && this.TranslatePoint(new Point(0, Bounds.Height), top) is { } bottom
                ? Math.Clamp(bottom.Y - input.OccludedRect.Top, 0, Bounds.Height)
                : 0;
        Padding = new Thickness(0, 0, 0, bottomPadding);
    }

    private static TextBlock Label(string value, double fontSize, string tone = "")
    {
        var label = new TextBlock { Text = value, FontSize = fontSize, TextWrapping = TextWrapping.Wrap };
        label.Classes.Add("vm-text");
        if (tone.Length != 0)
        {
            label.Classes.Add(tone);
        }
        return label;
    }

    private static Button ButtonFor(string caption, string id, bool primary = false)
    {
        var button = new Button
        {
            Content = caption,
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        button.Classes.Add("vm-button");
        if (primary)
        {
            button.Classes.Add("primary");
        }
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    private static void SetError(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }
}
