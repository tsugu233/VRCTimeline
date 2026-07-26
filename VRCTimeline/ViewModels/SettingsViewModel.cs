using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;

namespace VRCTimeline.ViewModels;

/// <summary>言語選択用の選択肢モデル</summary>
public sealed class LanguageOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>既定表示期間の選択肢モデル（日数 + ローカライズ済み表示名）</summary>
public sealed class FilterPeriodOption
{
    public int Days { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 設定画面の ViewModel。
/// ログ・写真フォルダのパス、テーマ、起動設定などをバインドし、
/// プロパティ変更時に自動保存する。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly LoadingService _loading;
    private readonly DialogService _dialog;
    private readonly NavigationService _navigation;

    /// <summary>過去ログ取り込みを VRChat 実行中にブロックするための参照</summary>
    private readonly VRChatProcessMonitor _processMonitor;

    /// <summary>写真フォルダ変更時に監視を再起動するための参照</summary>
    private readonly PhotoWatcher _photoWatcher;

    /// <summary>初期ロード中の自動保存を抑制するフラグ</summary>
    private bool _suppressSave;

    /// <summary>カラーピッカードラッグ等の高頻度変更をまとめるためのデバウンス用 CTS</summary>
    private CancellationTokenSource? _saveCts;

    /// <summary>自動保存のデバウンス時間（ミリ秒）</summary>
    private const int SaveDebounceMs = 250;

    /// <summary>VRChat ログフォルダのパス</summary>
    [ObservableProperty]
    private string _logDirectory = string.Empty;

    /// <summary>VRChat 写真フォルダのパス</summary>
    [ObservableProperty]
    private string _photoDirectory = string.Empty;

    /// <summary>Windows 起動時に自動起動するか</summary>
    [ObservableProperty]
    private bool _launchOnStartup;

    /// <summary>起動時に最小化するか</summary>
    [ObservableProperty]
    private bool _minimizeOnStartup;

    /// <summary>VRChat 起動を検知してウィンドウを表示するか</summary>
    [ObservableProperty]
    private bool _autoDetectVRChat;

    /// <summary>ダークモード有効フラグ</summary>
    [ObservableProperty]
    private bool _isDarkMode;

    /// <summary>アクセントカラーの Hex 値</summary>
    [ObservableProperty]
    private string _accentColorHex = "#79A1CB";

    /// <summary>ボタンテキストカラーの Hex 値</summary>
    [ObservableProperty]
    private string _buttonTextColorHex = "#262626";

    /// <summary>アクセントカラーピッカーの表示状態</summary>
    [ObservableProperty]
    private bool _isAccentPickerOpen;

    /// <summary>ボタンテキストカラーピッカーの表示状態</summary>
    [ObservableProperty]
    private bool _isButtonTextPickerOpen;

    /// <summary>データインポート中フラグ</summary>
    [ObservableProperty]
    private bool _isImporting;

    /// <summary>インポート進捗メッセージ</summary>
    [ObservableProperty]
    private string _importStatus = string.Empty;

    /// <summary>インポートセクションを表示するか（既存データがない場合のみ表示）</summary>
    [ObservableProperty]
    private bool _showImportSection = true;

    /// <summary>過去ログ取り込み中フラグ</summary>
    [ObservableProperty]
    private bool _isPastLogImporting;

    /// <summary>過去ログ取り込みの進捗・結果メッセージ</summary>
    [ObservableProperty]
    private string _pastLogImportStatus = string.Empty;

    /// <summary>
    /// 進捗・結果メッセージを表示中かどうか。
    /// 空文字のときに表示欄を畳み、ボタンと区切り線の間に空行分の余白が残らないようにする。
    /// </summary>
    [ObservableProperty]
    private bool _hasPastLogImportStatus;

    /// <summary>選択中の言語</summary>
    private LanguageOption? _selectedLanguage;

    /// <summary>選択可能な言語の一覧</summary>
    public List<LanguageOption> AvailableLanguages { get; } =
    [
        new() { Code = "ja", DisplayName = "日本語" },
        new() { Code = "en", DisplayName = "English" },
        new() { Code = "ko", DisplayName = "한국어" },
    ];

    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value == null) return;
            if (!_suppressSave)
                LocalizationService.SetLanguage(value.Code);
            // 永続化は OnPropertyChanged 経由のデバウンス保存に任せる（SaveableProperties に含まれる）
        }
    }

    /// <summary>各一覧画面の既定表示期間（日数）。SelectedPeriod 経由で更新され、永続化される。</summary>
    [ObservableProperty]
    private int _defaultFilterDays = 14;

    /// <summary>選択中の既定表示期間</summary>
    private FilterPeriodOption? _selectedPeriod;

    /// <summary>選択可能な既定表示期間の一覧（言語変更時に再構築される）</summary>
    public ObservableCollection<FilterPeriodOption> AvailablePeriods { get; } = [];

    public FilterPeriodOption? SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (!SetProperty(ref _selectedPeriod, value) || value == null) return;
            // DefaultFilterDays は SaveableProperties に含まれるため、変更でデバウンス保存が走る。
            DefaultFilterDays = value.Days;
        }
    }

    /// <summary>言語に合わせて既定期間の選択肢を再構築する</summary>
    private void RebuildPeriodOptions()
    {
        AvailablePeriods.Clear();
        AvailablePeriods.Add(new() { Days = 7, DisplayName = LocalizationService.GetString("Settings_Period_1Week") });
        AvailablePeriods.Add(new() { Days = 14, DisplayName = LocalizationService.GetString("Settings_Period_2Weeks") });
        AvailablePeriods.Add(new() { Days = 30, DisplayName = LocalizationService.GetString("Settings_Period_1Month") });
        AvailablePeriods.Add(new() { Days = 90, DisplayName = LocalizationService.GetString("Settings_Period_3Months") });
        AvailablePeriods.Add(new() { Days = 180, DisplayName = LocalizationService.GetString("Settings_Period_6Months") });
        AvailablePeriods.Add(new() { Days = 365, DisplayName = LocalizationService.GetString("Settings_Period_1Year") });
    }

    /// <summary>言語切替時に既定期間の選択肢ラベルを再生成し、選択状態を維持する</summary>
    private void OnLanguageChanged()
    {
        RebuildPeriodOptions();
        // 同じ Days の選択肢を新しいラベル付きで選び直す（保存はトリガしない）。
        _selectedPeriod = AvailablePeriods.FirstOrDefault(p => p.Days == DefaultFilterDays);
        OnPropertyChanged(nameof(SelectedPeriod));
    }

    /// <summary>変更時に自動保存するプロパティの名前一覧</summary>
    private static readonly HashSet<string> SaveableProperties =
    [
        nameof(LogDirectory), nameof(PhotoDirectory), nameof(LaunchOnStartup),
        nameof(MinimizeOnStartup), nameof(AutoDetectVRChat), nameof(IsDarkMode),
        nameof(AccentColorHex), nameof(ButtonTextColorHex), nameof(SelectedLanguage),
        nameof(DefaultFilterDays)
    ];

    public SettingsViewModel(SettingsService settingsService, LoadingService loadingService, DialogService dialogService, NavigationService navigationService, PhotoWatcher photoWatcher, VRChatProcessMonitor processMonitor)
    {
        _settingsService = settingsService;
        _loading = loadingService;
        _dialog = dialogService;
        _navigation = navigationService;
        _photoWatcher = photoWatcher;
        _processMonitor = processMonitor;
        RebuildPeriodOptions();
        LoadFromSettings();
        // 既定期間の選択肢ラベルを言語切替に追従させる（本 VM は Singleton のため解除不要）。
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _ = CheckExistingDataAsync();
    }

    /// <summary>既存データの有無を確認し、インポートセクションの表示を制御する</summary>
    private async Task CheckExistingDataAsync()
    {
        try
        {
            await using var db = new AppDbContext();
            ShowImportSection = !await db.WorldVisits.AnyAsync();
        }
        catch
        {
            ShowImportSection = true;
        }
    }

    /// <summary>保存済み設定を各プロパティに反映する（自動保存を一時抑制）</summary>
    private void LoadFromSettings()
    {
        _suppressSave = true;
        try
        {
            var s = _settingsService.Settings;
            LogDirectory = s.VRChatLogDirectory;
            PhotoDirectory = s.PhotoDirectory;
            LaunchOnStartup = s.LaunchOnStartup;
            MinimizeOnStartup = s.MinimizeOnStartup;
            AutoDetectVRChat = s.AutoDetectVRChat;
            IsDarkMode = s.IsDarkMode;
            AccentColorHex = s.AccentColorHex;
            ButtonTextColorHex = s.ButtonTextColorHex;
            _selectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == s.Language)
                                 ?? AvailableLanguages[0];
            OnPropertyChanged(nameof(SelectedLanguage));

            DefaultFilterDays = s.DefaultFilterDays;
            _selectedPeriod = AvailablePeriods.FirstOrDefault(p => p.Days == s.DefaultFilterDays);
            OnPropertyChanged(nameof(SelectedPeriod));
        }
        finally
        {
            _suppressSave = false;
        }
    }

    /// <summary>
    /// 保存対象プロパティの変更を検知して自動保存する。
    /// カラーピッカードラッグ等で連続発火するため、最後の変更から SaveDebounceMs だけ待ってから書き込む。
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_suppressSave || e.PropertyName == null) return;
        if (!SaveableProperties.Contains(e.PropertyName)) return;

        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = DebouncedSaveAsync(token);
    }

    /// <summary>デバウンス遅延後に保存を実行する。デバウンス中の追加変更でキャンセルされる。</summary>
    private async Task DebouncedSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounceMs, token);
            await SaveSettingsInternalAsync();
        }
        catch (OperationCanceledException) { /* 後続の変更にデバウンスを譲る */ }
    }

    /// <summary>現在のプロパティ値を設定ファイルに保存し、スタートアップ登録を更新する</summary>
    private async Task SaveSettingsInternalAsync()
    {
        try
        {
            var s = _settingsService.Settings;
            s.VRChatLogDirectory = LogDirectory;
            s.PhotoDirectory = PhotoDirectory;
            s.LaunchOnStartup = LaunchOnStartup;
            s.MinimizeOnStartup = MinimizeOnStartup;
            s.AutoDetectVRChat = AutoDetectVRChat;
            s.IsDarkMode = IsDarkMode;
            s.AccentColorHex = AccentColorHex;
            s.ButtonTextColorHex = ButtonTextColorHex;
            s.Language = SelectedLanguage?.Code ?? string.Empty;
            s.DefaultFilterDays = DefaultFilterDays;
            await _settingsService.SaveAsync();
            StartupRegistryService.Sync(LaunchOnStartup);
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    // ── テーマ変更ハンドラ ──

    partial void OnPastLogImportStatusChanged(string value)
    {
        HasPastLogImportStatus = !string.IsNullOrEmpty(value);
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        App.ApplyTheme(value, AccentColorHex, ButtonTextColorHex);
    }

    partial void OnAccentColorHexChanged(string value)
    {
        App.ApplyTheme(IsDarkMode, value, ButtonTextColorHex);
    }

    partial void OnButtonTextColorHexChanged(string value)
    {
        App.ApplyTheme(IsDarkMode, AccentColorHex, value);
    }

    // ── カラーピッカー開閉 ──

    [RelayCommand]
    private void ToggleAccentPicker() => IsAccentPickerOpen = !IsAccentPickerOpen;

    [RelayCommand]
    private void ToggleButtonTextPicker() => IsButtonTextPickerOpen = !IsButtonTextPickerOpen;

    // ── フォルダ選択 ──

    /// <summary>VRChat ログフォルダをダイアログで選択する</summary>
    [RelayCommand]
    private void BrowseLogDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Str_BrowseLogFolder"),
            InitialDirectory = GetExistingInitialDirectory(LogDirectory)
        };
        if (dialog.ShowDialog() == true)
            LogDirectory = dialog.FolderName;
    }

    /// <summary>
    /// 写真フォルダをダイアログで選択する。
    /// フォルダ変更時は DB 内の写真パスを一括置換し、PhotoWatcher を再起動する。
    /// </summary>
    [RelayCommand]
    private async Task BrowsePhotoDirectoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Str_BrowsePhotoFolder"),
            InitialDirectory = GetExistingInitialDirectory(PhotoDirectory)
        };
        if (dialog.ShowDialog() != true) return;

        var oldDir = PhotoDirectory;
        var newDir = dialog.FolderName;
        PhotoDirectory = newDir;

        // フォルダが変わった場合、既存レコードのパスを新フォルダに置換
        if (!string.IsNullOrEmpty(oldDir) && !string.IsNullOrEmpty(newDir)
            && !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            await ReplacePhotoPathsAsync(oldDir, newDir);
        }

        // 新しいフォルダで監視を再開し、未登録写真をスキャン
        _photoWatcher.Restart();
    }

    /// <summary>DB 内の写真ファイルパスのディレクトリ部分を一括置換する</summary>
    private static async Task ReplacePhotoPathsAsync(string oldDir, string newDir)
    {
        try
        {
            var oldPrefix = oldDir.TrimEnd('\\') + "\\";
            var newPrefix = newDir.TrimEnd('\\') + "\\";

            await using var db = new AppDbContext();
            var photos = await db.PhotoRecords
                .Where(p => p.FilePath.StartsWith(oldPrefix))
                .ToListAsync();

            if (photos.Count == 0) return;

            foreach (var photo in photos)
                photo.FilePath = newPrefix + photo.FilePath.Substring(oldPrefix.Length);

            await db.SaveChangesAsync();
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    /// <summary>
    /// フォルダ選択ダイアログの InitialDirectory として渡せる、現存する最も近いディレクトリを返す。
    /// 設定パスが既に削除されている場合に、上位の親までフォールバックする。
    /// </summary>
    private static string GetExistingInitialDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        catch { /* パス解析の失敗時は空文字を返すだけで、フォルダダイアログの初期位置として無害 */ }
        return string.Empty;
    }

    /// <summary>アプリデータフォルダをエクスプローラーで開く</summary>
    [RelayCommand]
    private static void OpenDataFolder()
    {
        var dir = Path.GetDirectoryName(AppDbContext.DbPath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    /// <summary>VRChatActivityLogViewer の DB からデータをインポートする</summary>
    [RelayCommand]
    private async Task ImportActivityLogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.GetString("Str_SelectDatabase"),
            Filter = LocalizationService.GetString("Str_SqliteFilter"),
            FileName = "VRChatActivityLog.db"
        };

        if (dialog.ShowDialog() != true) return;

        IsImporting = true;
        ImportStatus = LocalizationService.GetString("Str_Importing");
        _loading.Show(LocalizationService.GetString("Str_ImportingMessage"));
        try
        {
            var service = new ActivityLogImportService();
            var progress = new Progress<string>(msg =>
            {
                ImportStatus = msg;
                _loading.UpdateMessage(msg);
            });
            await Task.Run(() => service.ImportAsync(dialog.FileName, progress));
            _navigation.NotifyDataImported();
        }
        catch (Exception ex)
        {
            ImportStatus = LocalizationService.GetString("Str_ErrorPrefix") + ex.Message;
        }
        finally
        {
            IsImporting = false;
            _loading.Hide();
        }
    }

    /// <summary>
    /// 過去の VRChat ログファイルをフォルダから一括で取り込む。
    /// リアルタイム監視が動いていなかった期間（アプリ未起動時など）の履歴を後から補完するための機能。
    /// 現在のセッションの記録と競合しないよう VRChat 実行中はブロックし、
    /// 既存データと重複する訪問がある場合は上書きするかユーザーに確認する。
    /// </summary>
    [RelayCommand]
    private async Task ImportPastLogsAsync()
    {
        if (IsPastLogImporting) return;

        // 監視中のセッションと取り込みが同じ訪問を同時に書き換えるのを防ぐ
        if (_processMonitor.IsVRChatRunning)
        {
            await _dialog.ShowInfoAsync(LocalizationService.GetString("Str_PastLogVRChatRunning"));
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Str_SelectPastLogFolder"),
            InitialDirectory = GetExistingInitialDirectory(LogDirectory)
        };
        if (dialog.ShowDialog() != true) return;

        IsPastLogImporting = true;
        try
        {
            var service = new PastLogImportService();
            var progress = new Progress<string>(msg =>
            {
                PastLogImportStatus = msg;
                _loading.UpdateMessage(msg);
            });

            // ── 解析（DB 未変更） ──
            PastLogParseResult parsed;
            _loading.Show(LocalizationService.GetString("Str_Importing"));
            try
            {
                parsed = await Task.Run(() => service.Parse(dialog.FolderName, progress));
            }
            finally
            {
                _loading.Hide();
            }

            if (parsed.TotalFiles == 0 || parsed.InvalidFiles == parsed.TotalFiles)
            {
                PastLogImportStatus = string.Empty;
                await _dialog.ShowInfoAsync(LocalizationService.GetString("Str_PastLogNoLogs"));
                return;
            }
            if (parsed.Visits.Count == 0)
            {
                PastLogImportStatus = string.Empty;
                await _dialog.ShowInfoAsync(LocalizationService.GetString("Str_PastLogNoData"));
                return;
            }

            // ── 重複確認（はい = 上書き / いいえ = スキップして追加分のみ） ──
            bool overwrite = false;
            var duplicates = await service.CountDuplicatesAsync(parsed);
            if (duplicates > 0)
            {
                overwrite = await _dialog.ShowConfirmAsync(string.Format(
                    LocalizationService.GetString("Str_PastLogDuplicateConfirm"), duplicates));
            }

            // ── DB 反映 ──
            PastLogImportSummary summary;
            _loading.Show(LocalizationService.GetString("Str_ImportingMessage"));
            try
            {
                summary = await Task.Run(() => service.ApplyAsync(parsed, overwrite, progress));
            }
            finally
            {
                _loading.Hide();
            }

            _navigation.NotifyDataImported();

            PastLogImportStatus = string.Format(
                LocalizationService.GetString("Str_PastLogSummaryShort"),
                summary.AddedVisits, summary.OverwrittenVisits, summary.SkippedVisits);

            var message = string.Format(
                LocalizationService.GetString("Str_PastLogSummary"),
                summary.AddedVisits, summary.OverwrittenVisits, summary.SkippedVisits);
            if (parsed.InvalidFiles > 0)
            {
                message += "\n" + string.Format(
                    LocalizationService.GetString("Str_PastLogSummaryInvalid"), parsed.InvalidFiles);
            }
            await _dialog.ShowInfoAsync(message, LocalizationService.GetString("Str_Done"));
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex);
            PastLogImportStatus = LocalizationService.GetString("Str_ErrorPrefix") + ex.Message;
        }
        finally
        {
            IsPastLogImporting = false;
        }
    }

    /// <summary>動画サムネイルのキャッシュを削除する</summary>
    [RelayCommand]
    private async Task ClearThumbnailCacheAsync()
    {
        if (!await _dialog.ShowConfirmAsync(LocalizationService.GetString("Str_ConfirmClearCache")))
            return;

        VideoInfoService.ClearCache();
        await _dialog.ShowInfoAsync(
            LocalizationService.GetString("Str_CacheCleared"),
            LocalizationService.GetString("Str_Done"));
    }

    /// <summary>サムネイルキャッシュフォルダをエクスプローラーで開く</summary>
    [RelayCommand]
    private static void OpenThumbnailCacheFolder()
    {
        Directory.CreateDirectory(VideoInfoService.CacheDir);
        Process.Start(new ProcessStartInfo(VideoInfoService.CacheDir) { UseShellExecute = true });
    }

    /// <summary>
    /// Window 非表示時に表示用リソースを破棄する。
    /// ViewModel の Singleton インスタンスは破棄されないため、再表示時には Loaded イベント経由で
    /// 再ロードされる。設定値の状態は SettingsService 側で保持されており、本 ViewModel 内には
    /// 表示用キャッシュコレクションを持たないため、呼び出し側の統一目的で no-op を提供する。
    /// </summary>
    public void ReleaseUiResources()
    {
        // no-op: 表示用キャッシュは保持していない。状態は SettingsService 側にある。
    }

}
