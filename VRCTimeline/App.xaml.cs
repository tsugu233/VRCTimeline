using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VRCTimeline.Data;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;
using VRCTimeline.ViewModels;

namespace VRCTimeline;

/// <summary>
/// アプリケーションのエントリーポイント。
/// DI コンテナの構成、シングルインスタンス制御、テーマ適用、DB マイグレーション、
/// システムトレイアイコンの管理を行う。
/// </summary>
public partial class App : Application
{
    /// <summary>DI サービスプロバイダー</summary>
    private IServiceProvider _serviceProvider = null!;

    /// <summary>シングルインスタンス制御用 Mutex</summary>
    private static Mutex? _mutex;

    /// <summary>Mutex の所有権を取得できたか</summary>
    private bool _mutexOwned;

    /// <summary>システムトレイアイコン</summary>
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    /// <summary>トレイメニュー「表示」項目</summary>
    private System.Windows.Forms.ToolStripMenuItem? _trayShowItem;

    /// <summary>トレイメニュー「終了」項目</summary>
    private System.Windows.Forms.ToolStripMenuItem? _trayExitItem;

    /// <summary>名前付きパイプサーバーのキャンセルトークン</summary>
    private CancellationTokenSource? _pipeCts;

    /// <summary>シングルインスタンス制御用の Mutex 名</summary>
    private const string MutexName = "VRCTimeline_SingleInstance_Mutex";

    /// <summary>二重起動時のウィンドウ表示通知用パイプ名</summary>
    private const string PipeName = "VRCTimeline_SingleInstance_Pipe";

    /// <summary>
    /// アプリケーション起動処理。
    /// DI 構成、設定読み込み、DB 初期化、テーマ適用、メインウィンドウ表示を行う。
    /// --startup 引数時はウィンドウを非表示で起動する。
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // シングルインスタンス制御
        _mutex = new Mutex(true, MutexName, out _mutexOwned);
        if (!_mutexOwned)
        {
            NotifyExistingInstance();
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        StartPipeServer();

        try
        {
            // DI コンテナの構成
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // 設定の読み込み
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            await settingsService.LoadAsync();

            // スタートアップレジストリを現在の exe パスに同期する。
            // バージョンアップ等で exe の場所が変わってもここで自動追従する。
            StartupRegistryService.Sync(settingsService.Settings.LaunchOnStartup);

            // DB の初期化（テーブル作成・マイグレーション）
            await using (var db = new AppDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EnsureMissingTablesAsync(db);
            }

            // 言語の初期化（未設定の場合はシステムカルチャから自動検出して保存）
            var langSettings = settingsService.Settings;
            if (string.IsNullOrEmpty(langSettings.Language))
            {
                langSettings.Language = LocalizationService.DetectSystemLanguage();
                await settingsService.SaveAsync();
            }
            LocalizationService.SetLanguage(langSettings.Language);

            // WPF コントロール（Calendar 等）のデフォルト言語を現在のカルチャに合わせる。
            // これにより以後新規作成される Calendar の曜日・月名表示が現在言語で初期化される。
            try
            {
                FrameworkElement.LanguageProperty.OverrideMetadata(
                    typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(
                        XmlLanguage.GetLanguage(LocalizationService.GetCurrentCulture().IetfLanguageTag)));
            }
            catch { /* OverrideMetadata は型ごとに 1 度のみ呼び出し可能 */ }

            // テーマの適用
            ApplyTheme(settingsService.Settings.IsDarkMode,
                settingsService.Settings.AccentColorHex,
                settingsService.Settings.ButtonTextColorHex);

            var mainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainViewModel>()
            };

            SetupNotifyIcon(mainWindow);

            // 閉じるボタンでウィンドウを非表示にする（トレイに最小化）
            mainWindow.Closing += (s, args) =>
            {
                args.Cancel = true;
                mainWindow.Hide();
            };

            bool silentStart = e.Args.Contains("--startup");
            if (!silentStart)
                mainWindow.Show();

            // 設定ファイル破損を検知していた場合、ウィンドウが前面化された後に一度だけ通知する。
            if (settingsService.LoadCorruptionDetected)
            {
                var backupPath = settingsService.CorruptionBackupPath ?? string.Empty;
                if (silentStart)
                {
                    // サイレント起動時は MainWindow が初めて Show されるタイミングまで通知を遅延
                    void OnFirstShow(object s, System.Windows.DependencyPropertyChangedEventArgs args)
                    {
                        if (args.NewValue is not true) return;
                        mainWindow.IsVisibleChanged -= OnFirstShow;
                        ShowSettingsCorruptedDialog(mainWindow, backupPath);
                    }
                    mainWindow.IsVisibleChanged += OnFirstShow;
                }
                else
                {
                    ShowSettingsCorruptedDialog(mainWindow, backupPath);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"アプリケーションの起動に失敗しました / Failed to start:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>設定ファイル破損の通知ダイアログを一度だけ表示する</summary>
    private static void ShowSettingsCorruptedDialog(Window owner, string backupPath)
    {
        var title = LocalizationService.GetString("Str_SettingsCorruptedTitle");
        var template = LocalizationService.GetString("Str_SettingsCorruptedMessage");
        // リソース文字列内のリテラル "\n" を OS 改行に置換してから {0} を埋める
        var message = string.Format(template.Replace("\\n", Environment.NewLine), backupPath);
        if (owner.IsVisible)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>システムトレイアイコンとコンテキストメニューを設定する</summary>
    private void SetupNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "VRC Timeline",
            Visible = true
        };

        var exePath = Environment.ProcessPath;
        if (exePath != null && File.Exists(exePath))
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        else
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;

        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow(mainWindow);

        _trayShowItem = new System.Windows.Forms.ToolStripMenuItem(
            LocalizationService.GetString("Str_TrayShow"));
        _trayShowItem.Click += (s, e) => ShowMainWindow(mainWindow);

        _trayExitItem = new System.Windows.Forms.ToolStripMenuItem(
            LocalizationService.GetString("Str_TrayExit"));
        _trayExitItem.Click += (s, e) =>
        {
            _notifyIcon.Visible = false;
            Shutdown();
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayExitItem);
        _notifyIcon.ContextMenuStrip = menu;

        LocalizationService.LanguageChanged += UpdateTrayMenuText;
    }

    /// <summary>言語変更時にトレイメニューのテキストを更新する</summary>
    private void UpdateTrayMenuText()
    {
        Dispatcher.Invoke(() =>
        {
            if (_trayShowItem != null)
                _trayShowItem.Text = LocalizationService.GetString("Str_TrayShow");
            if (_trayExitItem != null)
                _trayExitItem.Text = LocalizationService.GetString("Str_TrayExit");
        });
    }

    /// <summary>メインウィンドウを表示してフォーカスを当てる</summary>
    private static void ShowMainWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>二重起動時に既存インスタンスのウィンドウ表示を要求する名前付きパイプサーバーを開始する</summary>
    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync(token);
                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is Window w)
                            ShowMainWindow(w);
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, token);
    }

    /// <summary>既存インスタンスにパイプ接続してウィンドウ表示を通知する</summary>
    private static void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
        }
        catch { }
    }

    /// <summary>アプリケーション終了時のクリーンアップ処理</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        LocalizationService.LanguageChanged -= UpdateTrayMenuText;
        _pipeCts?.Cancel();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        if (_mutexOwned && _mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>DI コンテナにサービスと ViewModel を登録する</summary>
    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LoadingService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<SelfPlayerService>();
        services.AddSingleton<VRChatProcessMonitor>();
        services.AddSingleton<PhotoWatcher>();
        services.AddTransient<LogScanner>();
        services.AddTransient<PhotoScanner>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<RealtimeMonitorViewModel>();
        services.AddSingleton<ActivityHistoryViewModel>();
        services.AddSingleton<PhotoManagerViewModel>();
        services.AddSingleton<NotificationLogViewModel>();
        services.AddSingleton<VideoLogViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    /// <summary>
    /// MaterialDesign テーマを適用する。
    /// アクセントカラーとボタンテキストカラーをカスタマイズする。
    /// </summary>
    internal static void ApplyTheme(bool isDark, string? accentHex = null, string? buttonTextHex = null)
    {
        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();
        theme.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);

        if (TryParseHexColor(accentHex, out var accent))
            theme.SetPrimaryColor(accent);

        var fg = TryParseHexColor(buttonTextHex, out var btnFg)
            ? btnFg
            : Color.FromRgb(0x20, 0x20, 0x20);

        theme.PrimaryLight = new ColorPair(theme.PrimaryLight.Color, fg);
        theme.PrimaryMid = new ColorPair(theme.PrimaryMid.Color, fg);
        theme.PrimaryDark = new ColorPair(theme.PrimaryDark.Color, fg);

        paletteHelper.SetTheme(theme);

        // アクセントカラーの明るいバリアントをリソースに登録
        if (TryParseHexColor(accentHex, out var ac))
        {
            var light = Color.FromRgb(
                (byte)Math.Min(255, ac.R + (255 - ac.R) * 0.4),
                (byte)Math.Min(255, ac.G + (255 - ac.G) * 0.4),
                (byte)Math.Min(255, ac.B + (255 - ac.B) * 0.4));
            var lightBrush = new SolidColorBrush(light);
            lightBrush.Freeze();
            Current.Resources["PrimaryHueLightBrush"] = lightBrush;

            var lightFgBrush = new SolidColorBrush(fg);
            lightFgBrush.Freeze();
            Current.Resources["PrimaryHueLightForegroundBrush"] = lightFgBrush;
        }
    }

    /// <summary>Hex カラー文字列を Color に変換する（失敗時は false を返す）</summary>
    internal static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// DB に不足しているテーブルを追加作成する。
    /// EnsureCreated だけでは既存 DB への新テーブル追加が行われないため、
    /// 生成スクリプトから欠落テーブルを検出して個別に作成する。
    /// </summary>
    private static async Task EnsureMissingTablesAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            var existing = new HashSet<string>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    existing.Add(reader.GetString(0));
            }

            var script = db.Database.GenerateCreateScript();
            foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = statement.Trim();
                if (!trimmed.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameStart = trimmed.IndexOf('"');
                var nameEnd = trimmed.IndexOf('"', nameStart + 1);
                if (nameStart < 0 || nameEnd < 0) continue;

                var tableName = trimmed[(nameStart + 1)..nameEnd];
                if (existing.Contains(tableName)) continue;

                using var create = conn.CreateCommand();
                create.CommandText = trimmed;
                await create.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
