# Vape Meter

Личный календарь замены картриджей. Windows и Android используют общие экраны Avalonia, расчёты и SQLite. Клиенты работают с локальным кэшем и синхронизируют изменения через HTTPS с Timeweb.

## Установка Android

Отдельный подписанный APK для Android 12 и новее, ARM64, находится в [GitHub Releases](https://github.com/MaximusStalsky/vape-meter/releases). GitHub показывает выпуск владельцу закрытого репозитория после входа в аккаунт.

При первом запуске нужно ввести ключ Android. На подготовленном Windows-ПК его показывает `app\show-android-connection.cmd`. На телефоне можно считать QR, скопировать текст и вставить ключ в Vape Meter. После подключения приложение автоматически загружает историю. Ключ хранится в защищённом хранилище телефона.

Для обновлений в Komi Store добавьте этот репозиторий после настройки доступа к приватным выпускам. В GitHub можно ограничить fine-grained personal access token этим репозиторием и правом **Contents: Read-only**. В Komi Store используется GitHub-токен, в Vape Meter — свой ключ Android.

## Код

- `app/VapeMeter.Windows` — оболочка Windows, окно и трей.
- `app/VapeMeter.Android` — Android-оболочка и подключение устройства.
- `app/VapeMeter.UI`, `app/VapeMeter.Core`, `app/VapeMeter.Analytics` — общие экраны, синхронизация и расчёты.
- `backend` — .NET API и схема SQLite.
- `ui-prototype` — согласованный интерактивный макет.

Для сборок используются .NET 10 SDK и Avalonia 12.1.6. Для Android нужны .NET Android workload, Android SDK и JDK 21. [Инструкция Android-клиента](app/VapeMeter.Android/README.md).

Материалы подписи и настройки сервера хранятся локально. GitHub Releases распространяет подписанный APK без учётных данных Timeweb.
