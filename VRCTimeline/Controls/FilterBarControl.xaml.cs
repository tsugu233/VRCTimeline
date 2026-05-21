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
    /// <summary>
    /// このコントロール配下にロード済みの DatePicker。
    /// 言語切替時に DatePickerTextBox の「yyyy/MM/dd (ddd)」表示を現在のカルチャで
    /// 再書き換えするための参照を保持する（DatePicker_Loaded 内で登録される）。
    /// </summary>
    private readonly List<DatePicker> _datePickers = [];

    public FilterBarControl()
    {
        InitializeComponent();
        UpdateCalendarLanguage();
        LocalizationService.LanguageChanged += UpdateCalendarLanguage;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= UpdateCalendarLanguage;
    }

    /// <summary>
    /// 言語変更時にカレンダーのロケールを更新し、選択済み日付のテキスト
    /// （曜日略称を含む）を新しいカルチャで書き換える。
    /// DatePickerTextBox.Text は TextChanged 経由でしか更新されないため、
    /// 手動で再フォーマットして反映させる必要がある。
    /// 加えて、カレンダーポップアップが現在開いている場合は、その中の曜日ヘッダー・
    /// 月年表示も即時で書き換える（閉じられている場合は次回 CalendarOpened で適用される）。
    /// </summary>
    private void UpdateCalendarLanguage()
    {
        this.Language = XmlLanguage.GetLanguage(DateFormatHelper.GetCurrentCulture().Name);
        foreach (var dp in _datePickers)
        {
            RefreshDatePickerText(dp);
            RefreshOpenCalendarPopup(dp);
        }
    }

    /// <summary>
    /// DatePicker の選択日付テキストを現在のカルチャで再フォーマットする。
    /// 視覚的な再描画のため Dispatcher 経由で非同期にテキストを差し替える。
    /// </summary>
    private void RefreshDatePickerText(DatePicker dp)
    {
        if (!dp.SelectedDate.HasValue) return;
        var tb = FindVisualChild<DatePickerTextBox>(dp);
        if (tb == null) return;
        var text = dp.SelectedDate.Value.ToString("yyyy/MM/dd (ddd)", DateFormatHelper.GetCurrentCulture());
        if (tb.Text == text) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => tb.Text = text);
    }

    /// <summary>
    /// 現在開いているカレンダーポップアップの言語表示（曜日ヘッダー・月年表示）を
    /// 現在のカルチャで上書きする。閉じている場合は次回 CalendarOpened ハンドラで処理される。
    /// </summary>
    private void RefreshOpenCalendarPopup(DatePicker dp)
    {
        if (!dp.IsDropDownOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var popup = FindVisualChild<Popup>(dp);
            if (popup?.Child is not FrameworkElement popupContent) return;
            var calendar = FindVisualChild<System.Windows.Controls.Calendar>(popupContent);
            if (calendar == null) return;
            ApplyCalendarLocalization(dp, calendar);
        });
    }

    /// <summary>
    /// 指定 Calendar の曜日ヘッダー・月年表示を現在のカルチャに揃える。
    /// Language プロパティへの代入は意図的に行わない:
    /// - calendar.Language を変更すると Calendar 側で再レイアウトが起き、popup のサイズが変動する。
    /// - dp.Language を変更すると WPF が DatePickerTextBox を一瞬デフォルトフォーマット
    ///   （曜日なし）に再描画してから、当方の TextChanged 経由のカスタムフォーマット
    ///   （曜日付き）に戻る「揺らぎ」が発生する。
    /// Popup の中身は HwndSource 境界のため Language inheritance が伝播しないが、
    /// RefreshCalendarLocalization の視覚ツリー直接操作経路（DataContext + TextBlock.Text）は
    /// Calendar.Language ではなく DateFormatHelper.GetCurrentCulture() を直接参照するため
    /// Language プロパティの状態に依存せず正しく動作する。
    /// </summary>
    private static void ApplyCalendarLocalization(DatePicker dp, System.Windows.Controls.Calendar calendar)
    {
        RefreshCalendarLocalization(calendar);
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

        // 言語切替時にこの DatePicker のテキストも再フォーマット対象にするため参照を保持する。
        // 同じインスタンスが Loaded を再発火しても重複登録されないよう Contains で抑止。
        if (!_datePickers.Contains(dp))
            _datePickers.Add(dp);

        // 日付選択時・popup 開閉時に WPF が DatePickerTextBox を一瞬カルチャ既定の ShortDatePattern
        // (ko-KR の "2026.4.16." 等) で書き直すため、当方の "yyyy/MM/dd (ddd)" 形式へ書き戻す。
        // 同期的に書き戻すと DatePicker 内部の Text 同期や UIAutomation のイベント連鎖により
        // StackOverflowException が発生する (tb.Text 直接代入 / SetCurrentValue 双方で再現確認済)
        // ため Dispatcher.BeginInvoke で非同期化する。
        // 副作用として、popup 開閉直後の極短時間 (1 フレーム前後) は WPF 既定形式が可視化される
        // 揺らぎが残るが、同期化が技術的に不可能なため受け入れる。
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

                // MaterialDesignThemes 5.x の DatePicker は popup.Child が Calendar インスタンス
                // そのものになる。WPF 標準だと popup.Child は Border 等のラッパーで Calendar は
                // その子孫だが、ここでは popupContent 自身が Calendar である可能性が高い。
                // popupContent 自体の型優先 → 子孫探索の順でフォールバックする。
                var calendar = popupContent as System.Windows.Controls.Calendar
                    ?? FindVisualChild<System.Windows.Controls.Calendar>(popupContent);
                if (calendar != null)
                {
                    ApplyCalendarLocalization(dp, calendar);
                }

                ApplyCalendarDarkTheme(popupContent);

                // 注意: 以前ここで calendar.LayoutTransform = new ScaleTransform(1.3, 1.3) と
                // calendar.HorizontalAlignment = HorizontalAlignment.Center を適用していたが、
                // 修正前は popup.Child が Calendar でなかった (FindVisualChild が null を返していた)
                // ため実際には一度も走っていなかった。修正で calendar が解決されるようになった結果
                // 初めて 1.3 倍スケールが適用され、ユーザー体感で popup サイズが約 1.5 倍となり、
                // 拡大した popup が DatePickerTextBox 領域に被って文字が揺らいで見える原因となった。
                // 元々機能していなかった視覚効果なので意図的に撤去する。

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
    /// Calendar の曜日ヘッダーとヘッダーボタン（月/年表示）を現在のカルチャで書き換える。
    /// MaterialDesignThemes 等によるテンプレ差し替えで PART_MonthView の構造が想定と異なる
    /// 環境でも動くよう、複数のリフレッシュ経路を順に試す:
    ///   1. CalendarItem.SetMonthModeDayTitles をリフレクションで直接呼ぶ
    ///   2. ビジュアルツリー内の Grid で「Row=0 のセルが 7 個」のものを探し、
    ///      その 7 セルに対し DataContext と inner TextBlock.Text の両方を上書きする
    ///   3. ヘッダーボタン (PART_HeaderButton) の Content を現在カルチャでフォーマット
    /// 1 で内部の更新が成功する環境では 2 はバインディング経由で同じ結果になる(冪等)、
    /// 1 が空振りする環境では 2 が補う、という二段構え。
    /// </summary>
    private static void RefreshCalendarLocalization(System.Windows.Controls.Calendar calendar)
    {
        var ci = FindVisualChild<CalendarItem>(calendar);
        if (ci == null) return;

        var culture = DateFormatHelper.GetCurrentCulture();
        var dtf = culture.DateTimeFormat;
        var firstDay = (int)dtf.FirstDayOfWeek;
        var dayNames = dtf.ShortestDayNames;

        // 副作用を最小化するため、可視 TextBlock の Text のみを直接書き換える。
        // DataContext や Owner を変更すると Calendar 内部の binding/layout がカスケードして
        // popup サイズ変動や DatePickerTextBox の一瞬の再描画（揺らぎ）が発生するため、
        // それらは触らない。tb.Text の直接代入は Binding を切るが、本メソッドは言語切替
        // および popup 開閉のたびに呼ばれるため、表示は常に現在カルチャに保たれる。
        foreach (var grid in FindVisualChildren<Grid>(ci))
        {
            var row0Cells = grid.Children.OfType<FrameworkElement>()
                .Where(c => Grid.GetRow(c) == 0)
                .OrderBy(c => Grid.GetColumn(c))
                .ToList();
            if (row0Cells.Count != 7) continue;

            for (int i = 0; i < 7; i++)
            {
                var cell = row0Cells[i];
                var name = dayNames[(firstDay + i) % 7];
                var tb = cell as TextBlock ?? FindVisualChild<TextBlock>(cell);
                if (tb != null && tb.Text != name) tb.Text = name;
            }
            break; // 1 個目の該当 Grid だけ更新
        }

        // ヘッダーボタン（月/年表示）を現在カルチャでフォーマット。Content が同じなら触らない。
        if (ci.Template?.FindName("PART_HeaderButton", ci) is Button headerBtn)
        {
            var displayDate = calendar.DisplayDate;
            var newContent = calendar.DisplayMode switch
            {
                CalendarMode.Month => displayDate.ToString("Y", culture),
                CalendarMode.Year => displayDate.ToString("yyyy", culture),
                CalendarMode.Decade => $"{displayDate.Year - displayDate.Year % 10} - {displayDate.Year - displayDate.Year % 10 + 9}",
                _ => (string?)headerBtn.Content?.ToString()
            };
            if (newContent != null && !object.Equals(headerBtn.Content, newContent))
                headerBtn.Content = newContent;
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
