using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using VRCTimeline.Helpers;
using VRCTimeline.Services;

namespace VRCTimeline.Controls;

/// <summary>
/// 各画面共通のフィルターバーコントロール。
/// 日付範囲、プレイヤー名、ワールド名、動画タイトル、種別フィルターを提供する。
/// 各フィルター項目の表示/非表示は依存関係プロパティで制御される。
/// </summary>
public partial class FilterBarControl : UserControl
{

    public FilterBarControl()
    {
        InitializeComponent();
        UpdateCalendarLanguage();
        LocalizationService.LanguageChanged += UpdateCalendarLanguage;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= UpdateCalendarLanguage;
    }

    /// <summary>言語変更時にカレンダーのロケールを更新する</summary>
    private void UpdateCalendarLanguage()
    {
        this.Language = XmlLanguage.GetLanguage(DateFormatHelper.GetCurrentCulture().Name);
    }

    // ── 日付範囲フィルター ──

    /// <summary>フィルター開始日</summary>
    public static readonly DependencyProperty FilterDateFromProperty =
        DependencyProperty.Register(nameof(FilterDateFrom), typeof(DateTime), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(DateTime.Today.AddDays(-30), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public DateTime FilterDateFrom
    {
        get => (DateTime)GetValue(FilterDateFromProperty);
        set => SetValue(FilterDateFromProperty, value);
    }

    /// <summary>フィルター終了日</summary>
    public static readonly DependencyProperty FilterDateToProperty =
        DependencyProperty.Register(nameof(FilterDateTo), typeof(DateTime), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(DateTime.Today.AddDays(1), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public DateTime FilterDateTo
    {
        get => (DateTime)GetValue(FilterDateToProperty);
        set => SetValue(FilterDateToProperty, value);
    }

    // ── テキストフィルター ──

    /// <summary>プレイヤー名フィルターテキスト</summary>
    public static readonly DependencyProperty PlayerFilterTextProperty =
        DependencyProperty.Register(nameof(PlayerFilterText), typeof(string), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string PlayerFilterText
    {
        get => (string)GetValue(PlayerFilterTextProperty);
        set => SetValue(PlayerFilterTextProperty, value);
    }

    /// <summary>ワールド名フィルターテキスト</summary>
    public static readonly DependencyProperty WorldFilterTextProperty =
        DependencyProperty.Register(nameof(WorldFilterText), typeof(string), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string WorldFilterText
    {
        get => (string)GetValue(WorldFilterTextProperty);
        set => SetValue(WorldFilterTextProperty, value);
    }

    /// <summary>動画タイトルフィルターテキスト</summary>
    public static readonly DependencyProperty VideoTitleFilterTextProperty =
        DependencyProperty.Register(nameof(VideoTitleFilterText), typeof(string), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string VideoTitleFilterText
    {
        get => (string)GetValue(VideoTitleFilterTextProperty);
        set => SetValue(VideoTitleFilterTextProperty, value);
    }

    // ── 種別フィルター ──

    /// <summary>種別フィルターの選択肢リスト</summary>
    public static readonly DependencyProperty TypeFilterItemsProperty =
        DependencyProperty.Register(nameof(TypeFilterItems), typeof(IEnumerable), typeof(FilterBarControl));

    public IEnumerable? TypeFilterItems
    {
        get => (IEnumerable?)GetValue(TypeFilterItemsProperty);
        set => SetValue(TypeFilterItemsProperty, value);
    }

    /// <summary>選択中の種別フィルター値</summary>
    public static readonly DependencyProperty SelectedTypeFilterProperty =
        DependencyProperty.Register(nameof(SelectedTypeFilter), typeof(string), typeof(FilterBarControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string? SelectedTypeFilter
    {
        get => (string?)GetValue(SelectedTypeFilterProperty);
        set => SetValue(SelectedTypeFilterProperty, value);
    }

    // ── 検索コマンド ──

    /// <summary>検索ボタンに紐づくコマンド</summary>
    public static readonly DependencyProperty SearchCommandProperty =
        DependencyProperty.Register(nameof(SearchCommand), typeof(ICommand), typeof(FilterBarControl));

    public ICommand? SearchCommand
    {
        get => (ICommand?)GetValue(SearchCommandProperty);
        set => SetValue(SearchCommandProperty, value);
    }

    // ── フィルター項目の表示制御 ──

    /// <summary>プレイヤー名フィルターを表示するか</summary>
    public static readonly DependencyProperty ShowPlayerFilterProperty =
        DependencyProperty.Register(nameof(ShowPlayerFilter), typeof(bool), typeof(FilterBarControl),
            new PropertyMetadata(true));

    public bool ShowPlayerFilter
    {
        get => (bool)GetValue(ShowPlayerFilterProperty);
        set => SetValue(ShowPlayerFilterProperty, value);
    }

    /// <summary>ワールド名フィルターを表示するか</summary>
    public static readonly DependencyProperty ShowWorldFilterProperty =
        DependencyProperty.Register(nameof(ShowWorldFilter), typeof(bool), typeof(FilterBarControl),
            new PropertyMetadata(true));

    public bool ShowWorldFilter
    {
        get => (bool)GetValue(ShowWorldFilterProperty);
        set => SetValue(ShowWorldFilterProperty, value);
    }

    /// <summary>動画タイトルフィルターを表示するか</summary>
    public static readonly DependencyProperty ShowVideoTitleFilterProperty =
        DependencyProperty.Register(nameof(ShowVideoTitleFilter), typeof(bool), typeof(FilterBarControl),
            new PropertyMetadata(false));

    public bool ShowVideoTitleFilter
    {
        get => (bool)GetValue(ShowVideoTitleFilterProperty);
        set => SetValue(ShowVideoTitleFilterProperty, value);
    }

    /// <summary>種別フィルターを表示するか</summary>
    public static readonly DependencyProperty ShowTypeFilterProperty =
        DependencyProperty.Register(nameof(ShowTypeFilter), typeof(bool), typeof(FilterBarControl),
            new PropertyMetadata(false));

    public bool ShowTypeFilter
    {
        get => (bool)GetValue(ShowTypeFilterProperty);
        set => SetValue(ShowTypeFilterProperty, value);
    }

    /// <summary>プログレスバーを表示するか</summary>
    public static readonly DependencyProperty ShowProgressBarProperty =
        DependencyProperty.Register(nameof(ShowProgressBar), typeof(bool), typeof(FilterBarControl),
            new PropertyMetadata(false));

    public bool ShowProgressBar
    {
        get => (bool)GetValue(ShowProgressBarProperty);
        set => SetValue(ShowProgressBarProperty, value);
    }

    // ── ビジュアルツリー探索ユーティリティ ──

    /// <summary>ビジュアルツリーから指定型の最初の子要素を探す</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>
    /// DatePicker のロード時にカスタマイズを適用する。
    /// テキスト表示を曜日付きフォーマットにし、カレンダーポップアップをダークテーマ化する。
    /// </summary>
    private void DatePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DatePicker dp) return;
        dp.ApplyTemplate();
        var tb = FindVisualChild<DatePickerTextBox>(dp);
        if (tb == null) return;

        tb.TextAlignment = TextAlignment.Center;

        // 日付選択時に曜日付きフォーマットで表示
        tb.TextChanged += (_, _) =>
        {
            if (!dp.SelectedDate.HasValue) return;
            var expected = dp.SelectedDate.Value.ToString("yyyy/MM/dd (ddd)", DateFormatHelper.GetCurrentCulture());
            if (tb.Text != expected)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => tb.Text = expected);
        };

        if (dp.SelectedDate.HasValue)
        {
            var text = dp.SelectedDate.Value.ToString("yyyy/MM/dd (ddd)", DateFormatHelper.GetCurrentCulture());
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => tb.Text = text);
        }

        // カレンダーポップアップのダークテーマ適用
        dp.CalendarOpened += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var popup = FindVisualChild<Popup>(dp);
                if (popup?.Child is not FrameworkElement popupContent) return;

                // Calendar の曜日・月名は CalendarItem.OnApplyTemplate() 内でキャッシュされ、
                // Language プロパティを後から変更しても更新されない。
                // ここでビジュアルツリーから直接 TextBlock を見つけて現在のカルチャで上書きする。
                var calendar = FindVisualChild<System.Windows.Controls.Calendar>(popupContent);
                if (calendar != null)
                {
                    var lang = XmlLanguage.GetLanguage(DateFormatHelper.GetCurrentCulture().IetfLanguageTag);
                    calendar.Language = lang;
                    dp.Language = lang;
                    RefreshCalendarLocalization(calendar);

                    // 月送り・モード変更でヘッダーが再描画されるたびにローカライズ補正する
                    // （Calendar インスタンスごとに 1 度だけ購読する）
                    if (calendar.Tag as string != "LocalizationHooked")
                    {
                        var capturedCalendar = calendar;
                        calendar.DisplayDateChanged += (_, _) =>
                            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                                () => RefreshCalendarLocalization(capturedCalendar));
                        calendar.DisplayModeChanged += (_, _) =>
                            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                                () => RefreshCalendarLocalization(capturedCalendar));
                        calendar.Tag = "LocalizationHooked";
                    }
                }

                ApplyCalendarDarkTheme(popupContent);

                if (calendar != null)
                {
                    calendar.LayoutTransform = new ScaleTransform(1.3, 1.3);
                    calendar.HorizontalAlignment = HorizontalAlignment.Center;
                }

                popup.Placement = PlacementMode.Bottom;
                popup.HorizontalOffset = -(dp.ActualWidth * 0.3);
            });
        };
    }

    /// <summary>
    /// カレンダーポップアップにダークテーマを適用する。
    /// </summary>
    private static void ApplyCalendarDarkTheme(FrameworkElement popupContent)
    {
        var darkBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

        foreach (var border in FindVisualChildren<Border>(popupContent))
        {
            if (border.Background is SolidColorBrush bg && bg.Color.R > 200)
                border.Background = darkBg;
        }

        foreach (var ci in FindVisualChildren<CalendarItem>(popupContent))
        {
            ci.Background = darkBg;
            ci.BorderThickness = new Thickness(0);
            ci.Foreground = Brushes.White;
        }

        foreach (var tb in FindVisualChildren<TextBlock>(popupContent))
            tb.Foreground = Brushes.White;

        foreach (var btn in FindVisualChildren<Button>(popupContent))
            btn.Foreground = Brushes.White;

        foreach (var path in FindVisualChildren<System.Windows.Shapes.Path>(popupContent))
            path.Fill = Brushes.White;

        foreach (var ci in FindVisualChildren<CalendarItem>(popupContent))
        {
            var headerBtn = ci.Template?.FindName("PART_HeaderButton", ci) as Button;
            var prevBtn = ci.Template?.FindName("PART_PreviousButton", ci) as Button;
            var nextBtn = ci.Template?.FindName("PART_NextButton", ci) as Button;

            if (headerBtn != null) { headerBtn.MinHeight = 40; headerBtn.FontSize = 15; }
            if (prevBtn != null) prevBtn.MinHeight = 40;
            if (nextBtn != null) nextBtn.MinHeight = 40;

            if (headerBtn?.Parent is FrameworkElement headerPanel)
                headerPanel.Margin = new Thickness(4, 8, 4, 12);
        }
    }

    /// <summary>
    /// Calendar の曜日ヘッダーとヘッダーボタン（月/年表示）のテキストを
    /// 現在のカルチャに合わせて手動で書き換える。
    /// WPF Calendar は CalendarItem.OnApplyTemplate() 時点で曜日名・月名をキャッシュするため、
    /// Language プロパティを後から変更しても自動更新されない問題への対処。
    /// </summary>
    private static void RefreshCalendarLocalization(System.Windows.Controls.Calendar calendar)
    {
        var ci = FindVisualChild<CalendarItem>(calendar);
        if (ci?.Template == null) return;

        var culture = DateFormatHelper.GetCurrentCulture();

        // 曜日ヘッダー（MonthView の Row 0）
        if (ci.Template.FindName("PART_MonthView", ci) is Grid monthView)
        {
            var firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
            var dayNames = culture.DateTimeFormat.AbbreviatedDayNames;

            var headerCells = monthView.Children
                .OfType<UIElement>()
                .Where(c => Grid.GetRow(c) == 0)
                .OrderBy(c => Grid.GetColumn(c))
                .ToList();

            // 週番号列がある場合は先頭を除外
            var dayCells = headerCells.Count > 7
                ? headerCells.Skip(headerCells.Count - 7).ToList()
                : headerCells;

            for (int i = 0; i < dayCells.Count && i < 7; i++)
            {
                var element = dayCells[i];
                var tb = element as TextBlock ?? FindVisualChild<TextBlock>(element);
                if (tb != null)
                {
                    var dayIdx = (firstDay + i) % 7;
                    tb.Text = dayNames[dayIdx];
                }
            }
        }

        // ヘッダーボタン（月/年表示）
        if (ci.Template.FindName("PART_HeaderButton", ci) is Button headerBtn)
        {
            var displayDate = calendar.DisplayDate;
            headerBtn.Content = calendar.DisplayMode switch
            {
                CalendarMode.Month => displayDate.ToString("Y", culture),
                CalendarMode.Year => displayDate.ToString("yyyy", culture),
                CalendarMode.Decade => $"{displayDate.Year - displayDate.Year % 10} - {displayDate.Year - displayDate.Year % 10 + 9}",
                _ => headerBtn.Content
            };
        }
    }

    /// <summary>ビジュアルツリーから指定型の全子要素を列挙する</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child))
                yield return c;
        }
    }
}
