using System.Data.Common;
using System.IO;
using System.IO.Pipes;
using System.Runtime;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
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
                await EnsureSchemaUpToDateAsync(db);
            }

            // 言語の初期化（未設定の場合はシステムカルチャから自動検出して保存）
            // LoadIOFailureDetected 時は SaveAsync が no-op になるが、永続化されないだけで
            // このセッションの言語表示は問題なく機能する。
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

            // 閉じるボタンでウィンドウを非表示にする（トレイに最小化）。
            // Hide 直後に UI 表示用リソースを解放してメモリを即座に縮小する。
            // バックグラウンドサービス（VRChatProcessMonitor / PhotoWatcher / LogWatcher）は継続。
            mainWindow.Closing += (s, args) =>
            {
                args.Cancel = true;
                mainWindow.Hide();
                OnMainWindowHidden(mainWindow);
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

            // 設定ファイルの IO 失敗（ロック・権限拒否等）を検知していた場合も同様に通知する。
            // この間 SaveAsync は no-op になっており、ユーザの既存設定ファイルは保護されている。
            if (settingsService.LoadIOFailureDetected)
            {
                if (silentStart)
                {
                    void OnFirstShow(object s, System.Windows.DependencyPropertyChangedEventArgs args)
                    {
                        if (args.NewValue is not true) return;
                        mainWindow.IsVisibleChanged -= OnFirstShow;
                        ShowSettingsLockedDialog(mainWindow);
                    }
                    mainWindow.IsVisibleChanged += OnFirstShow;
                }
                else
                {
                    ShowSettingsLockedDialog(mainWindow);
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

    /// <summary>
    /// 設定ファイル IO 失敗（ロック等）の通知ダイアログを表示する。
    /// このセッション中は SaveAsync が no-op になり、既存設定ファイルが上書きされない旨を伝える。
    /// </summary>
    private static void ShowSettingsLockedDialog(Window owner)
    {
        var title = LocalizationService.GetString("Str_SettingsLockedTitle");
        var template = LocalizationService.GetString("Str_SettingsLockedMessage");
        var message = template.Replace("\\n", Environment.NewLine);
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
        if (window.DataContext is MainViewModel vm)
            vm.OnShown();
    }

    /// <summary>
    /// Window 非表示時に UI 表示用リソースを解放する。
    /// 順序:
    ///   1. MainViewModel.OnHidden() で（リアルタイム画面以外の）VM の表示用コレクションをクリア＋ナビ位置をリセット
    ///   2. MainWindow._viewCache を「リアルタイム画面を残して」クリア。
    ///      これにより再表示時にナビ切替や PropertyChanged を経由しなくても画面が見える状態を維持する
    ///   3. サムネイル LRU キャッシュをクリアして BitmapImage 群を解放
    ///   4. LOH コンパクションを 1 回だけ要求してから GC を 2 回実行。Bitmap 等の大型オブジェクトは
    ///      通常 LOH に置かれて GC されにくいため、明示的なコンパクションを挟む
    ///   5. <see cref="NativeMethods.TryTrimWorkingSet"/> でワーキングセットを Windows にトリム要求。
    ///      .NET の GC は managed heap を縮小しても OS の working set は自動では縮小しないため、
    ///      タスクマネージャ表示で「Hide 直後にメモリが下がる」体験を出すには必須
    /// </summary>
    private static void OnMainWindowHidden(Window mainWindow)
    {
        if (mainWindow.DataContext is not MainViewModel vm) return;

        vm.OnHidden();

        if (mainWindow is MainWindow mw)
            mw.ClearViewCache(vm.RealtimeMonitorVm);

        ThumbnailCache.Clear();

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        NativeMethods.TryTrimWorkingSet();
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
                catch { /* パイプ通信の一時的エラー（相手の中断等）はリトライで吸収 */ }
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
        catch { /* 接続失敗時は通知諦め（既存インスタンスが落ちている等の想定内ケース） */ }
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
    /// 既存 DB のスキーマを現行モデルに追従させる。EnsureCreated は既存 DB に対しては
    /// 何もしないため、以下を追加適用する:
    ///   1. 不足テーブルの CREATE TABLE（GenerateCreateScript から抽出）
    ///   2. 既存テーブルへの不足列の ALTER TABLE ADD COLUMN
    ///   3. 既存テーブルへの不足インデックスの CREATE INDEX IF NOT EXISTS
    /// 全工程を単一トランザクションで囲うため、途中失敗時はロールバックされ
    /// 既存データに対して非破壊（CREATE / ALTER ADD / CREATE INDEX のみで DROP / UPDATE は実行しない）。
    /// </summary>
    private static async Task EnsureSchemaUpToDateAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var existingTables = await GetExistingTablesAsync(conn, tx);
                await CreateMissingTablesAsync(db, conn, tx, existingTables);

                foreach (var entityType in db.Model.GetEntityTypes())
                {
                    var tableName = entityType.GetTableName();
                    if (string.IsNullOrEmpty(tableName)) continue;
                    if (!existingTables.Contains(tableName)) continue;

                    var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
                    await EnsureMissingColumnsAsync(conn, tx, entityType, tableName, storeObject);
                    await EnsureMissingIndicesAsync(conn, tx, entityType, tableName, storeObject);
                }

                await tx.CommitAsync();
            }
            catch
            {
                try { await tx.RollbackAsync(); } catch { /* rollback 失敗は元の例外を優先 */ }
                throw;
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task<HashSet<string>> GetExistingTablesAsync(DbConnection conn, DbTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            existing.Add(reader.GetString(0));
        return existing;
    }

    private static async Task CreateMissingTablesAsync(AppDbContext db, DbConnection conn, DbTransaction tx, HashSet<string> existingTables)
    {
        var script = db.Database.GenerateCreateScript();
        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = statement.Trim();
            if (!trimmed.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                continue;

            var nameStart = trimmed.IndexOf('"');
            if (nameStart < 0) continue;
            var nameEnd = trimmed.IndexOf('"', nameStart + 1);
            if (nameEnd < 0) continue;

            var tableName = trimmed[(nameStart + 1)..nameEnd];
            if (existingTables.Contains(tableName)) continue;

            using var create = conn.CreateCommand();
            create.Transaction = tx;
            create.CommandText = trimmed;
            await create.ExecuteNonQueryAsync();
            existingTables.Add(tableName);
        }
    }

    private static async Task EnsureMissingColumnsAsync(
        DbConnection conn, DbTransaction tx, IEntityType entityType, string tableName, StoreObjectIdentifier storeObject)
    {
        var existingCols = await GetExistingColumnsAsync(conn, tx, tableName);

        foreach (var property in entityType.GetProperties())
        {
            // PK は ALTER で追加不能。新規テーブルでは既に CREATE TABLE 側で作成済み。
            if (property.IsPrimaryKey()) continue;

            var columnName = property.GetColumnName(storeObject);
            if (string.IsNullOrEmpty(columnName)) continue;
            if (existingCols.Contains(columnName)) continue;

            var columnType = property.GetColumnType(storeObject);
            if (string.IsNullOrEmpty(columnType)) continue;

            var isNullable = property.IsColumnNullable(storeObject);
            var def = BuildAddColumnDefinition(columnName, columnType, isNullable, property);
            // SQLite の ALTER TABLE ADD COLUMN で安全に追加できないケース
            // （NOT NULL かつ既定値推定不可・PK・UNIQUE 等）は次回起動でも検出されるので
            // ここでは黙ってスキップする。既存データは一切変更しない。
            if (def == null) continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN {def}";
            await cmd.ExecuteNonQueryAsync();
            existingCols.Add(columnName);
        }
    }

    private static async Task<HashSet<string>> GetExistingColumnsAsync(DbConnection conn, DbTransaction tx, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        var nameOrdinal = reader.GetOrdinal("name");
        while (await reader.ReadAsync())
            cols.Add(reader.GetString(nameOrdinal));
        return cols;
    }

    /// <summary>
    /// ADD COLUMN 用の列定義文字列を構築する。
    /// FOREIGN KEY / UNIQUE / PRIMARY KEY 等の制約は付けない（SQLite の ALTER TABLE 制限により
    /// 既存行があると追加不能になるため）。FK 関係は EF Core のモデル側で表現されるので
    /// 列だけ追加できれば実用上問題ない。
    /// </summary>
    private static string? BuildAddColumnDefinition(string columnName, string columnType, bool isNullable, IProperty property)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(columnName).Append('"').Append(' ').Append(columnType);
        if (!isNullable)
        {
            var defaultLiteral = GetSafeDefaultLiteral(columnType, property);
            if (defaultLiteral == null) return null;
            sb.Append(" NOT NULL DEFAULT ").Append(defaultLiteral);
        }
        return sb.ToString();
    }

    /// <summary>NOT NULL 列を ALTER で追加する際の安全なデフォルトリテラルを返す。推定不可なら null。</summary>
    private static string? GetSafeDefaultLiteral(string columnType, IProperty property)
    {
        var efDefault = property.GetDefaultValue();
        if (efDefault != null)
        {
            return efDefault switch
            {
                string s => $"'{s.Replace("'", "''")}'",
                bool b => b ? "1" : "0",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
                _ => efDefault.ToString()
            };
        }

        var upper = columnType.ToUpperInvariant();
        if (upper.Contains("TEXT") || upper.Contains("CHAR") || upper.Contains("CLOB"))
            return "''";
        if (upper.Contains("INT") || upper.Contains("REAL") || upper.Contains("NUMERIC") ||
            upper.Contains("FLOAT") || upper.Contains("DOUB") || upper.Contains("DECIMAL"))
            return "0";
        if (upper.Contains("BLOB"))
            return "x''";
        return null;
    }

    private static async Task EnsureMissingIndicesAsync(
        DbConnection conn, DbTransaction tx, IEntityType entityType, string tableName, StoreObjectIdentifier storeObject)
    {
        var existing = await GetExistingIndicesAsync(conn, tx, tableName);

        foreach (var index in entityType.GetIndexes())
        {
            var indexName = index.GetDatabaseName(storeObject);
            if (string.IsNullOrEmpty(indexName)) continue;
            if (existing.Contains(indexName)) continue;

            var columnList = new List<string>(index.Properties.Count);
            var canBuild = true;
            foreach (var p in index.Properties)
            {
                var col = p.GetColumnName(storeObject);
                if (string.IsNullOrEmpty(col)) { canBuild = false; break; }
                columnList.Add($"\"{col}\"");
            }
            if (!canBuild) continue;

            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"CREATE {unique}INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" ({string.Join(", ", columnList)})";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<HashSet<string>> GetExistingIndicesAsync(DbConnection conn, DbTransaction tx, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA index_list(\"{tableName}\")";
        var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        var nameOrdinal = reader.GetOrdinal("name");
        while (await reader.ReadAsync())
            indices.Add(reader.GetString(nameOrdinal));
        return indices;
    }
}
