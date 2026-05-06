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

    /// <summary>写真フォルダ変更時に監視を再起動するための参照</summary>
    private readonly PhotoWatcher _photoWatcher;

    /// <summary>初期ロード中の自動保存を抑制するフラグ</summary>
    private bool _suppressSave;

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
            {
                _settingsService.Settings.Language = value.Code;
                LocalizationService.SetLanguage(value.Code);
                _ = _settingsService.SaveAsync();
            }
        }
    }

    /// <summary>変更時に自動保存するプロパティの名前一覧</summary>
    private static readonly HashSet<string> SaveableProperties =
    [
        nameof(LogDirectory), nameof(PhotoDirectory), nameof(LaunchOnStartup),
        nameof(MinimizeOnStartup), nameof(AutoDetectVRChat), nameof(IsDarkMode),
        nameof(AccentColorHex), nameof(ButtonTextColorHex)
    ];

    public SettingsViewModel(SettingsService settingsService, LoadingService loadingService, DialogService dialogService, NavigationService navigationService, PhotoWatcher photoWatcher)
    {
        _settingsService = settingsService;
        _loading = loadingService;
        _dialog = dialogService;
        _navigation = navigationService;
        _photoWatcher = photoWatcher;
        LoadFromSettings();
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
        }
        finally
        {
            _suppressSave = false;
        }
    }

    /// <summary>保存対象プロパティの変更を検知して自動保存する</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressSave && e.PropertyName != null && SaveableProperties.Contains(e.PropertyName))
            _ = SaveSettingsInternalAsync();
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
            await _settingsService.SaveAsync();
            StartupRegistryService.Sync(LaunchOnStartup);
        }
        catch { }
    }

    // ── テーマ変更ハンドラ ──

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
            Title = LocalizationService.GetString("Str_BrowseLogFolder")
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
            Title = LocalizationService.GetString("Str_BrowsePhotoFolder")
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
        catch { }
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

}
