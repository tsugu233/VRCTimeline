using System.Globalization;
using System.Windows;

namespace VRCTimeline.Services;

/// <summary>
/// 言語リソースの切り替えと文字列取得を管理する静的サービス。
/// ResourceDictionary を差し替えることで DynamicResource バインドを動的に更新する。
/// </summary>
public static class LocalizationService
{
    public static event Action? LanguageChanged;

    private static string _currentLanguage = "ja";

    /// <summary>現在適用中の言語コード</summary>
    public static string CurrentLanguage => _currentLanguage;

    /// <summary>指定コードの言語リソースを適用する</summary>
    public static void SetLanguage(string languageCode)
    {
        if (Application.Current == null) return;

        _currentLanguage = languageCode;

        // WPF Calendar など内部で CurrentCulture を参照するコントロールに反映させるため、
        // スレッドカルチャと既定スレッドカルチャを更新する
        var culture = GetCurrentCulture();
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // 新しい辞書を先に追加してから古い辞書を削除することで、
        // DynamicResource バインドが一瞬でも未解決になる瞬間をなくし
        // "Resource not found" 警告を抑止する
        var uri = new Uri(
            $"pack://application:,,,/VRCTimeline;component/Resources/Strings.{languageCode}.xaml");
        var newDict = new ResourceDictionary { Source = uri };
        Application.Current.Resources.MergedDictionaries.Add(newDict);

        var oldDicts = Application.Current.Resources.MergedDictionaries
            .Where(d => IsLanguageDictionary(d) && d != newDict)
            .ToList();
        foreach (var old in oldDicts)
            Application.Current.Resources.MergedDictionaries.Remove(old);

        LanguageChanged?.Invoke();
    }

    /// <summary>現在の言語に対応する CultureInfo を返す</summary>
    public static CultureInfo GetCurrentCulture()
        => _currentLanguage switch
        {
            "ko" => CultureInfo.GetCultureInfo("ko-KR"),
            "en" => CultureInfo.GetCultureInfo("en-US"),
            _ => CultureInfo.GetCultureInfo("ja-JP")
        };

    /// <summary>リソースキーに対応するローカライズ文字列を返す。未登録の場合はキーをそのまま返す</summary>
    public static string GetString(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>PCの UI カルチャからデフォルト言語コードを検出する</summary>
    public static string DetectSystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ja" => "ja",
            "ko" => "ko",
            _ => "en"
        };

    private static bool IsLanguageDictionary(ResourceDictionary d)
        => d.Source?.ToString().Contains("/Resources/Strings.") == true;
}
