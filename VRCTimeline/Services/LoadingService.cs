using CommunityToolkit.Mvvm.ComponentModel;

namespace VRCTimeline.Services;

/// <summary>
/// グローバルローディング UI の表示を制御するサービス。
/// 参照カウント方式で複数の非同期処理が同時にローディングを要求できる。
/// </summary>
public partial class LoadingService : ObservableObject
{
    /// <summary>ローディング表示の参照カウント</summary>
    private int _count;

    /// <summary>
    /// _count と IsLoading の整合性を保つためのロック。
    /// Interlocked のみだと「Hide の if-block に入った直後に別スレッドの Show が走ると、
    /// 後続の IsLoading=false が Show の IsLoading=true を上書きする」競合があるため、
    /// Show/Hide 全体を直列化する。呼び出しは UI スレッドが中心で性能影響は無視可能。
    /// </summary>
    private readonly object _lock = new();

    /// <summary>ローディング中かどうか</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>ローディング中に表示するメッセージ</summary>
    [ObservableProperty]
    private string _loadingMessage = "";

    /// <summary>
    /// メッセージの下に小さく表示するサブメッセージ（"50 / 1000" のような進捗表示用）。
    /// 空文字なら非表示扱い（XAML 側で Visibility を切替）。
    /// </summary>
    [ObservableProperty]
    private string _loadingSubMessage = "";

    /// <summary>ローディング表示を開始する（参照カウントをインクリメント）</summary>
    public void Show(string message = "読み込み中...")
    {
        lock (_lock)
        {
            _count++;
            LoadingMessage = message;
            LoadingSubMessage = ""; // 新しい Show では前回ロードの進捗を引き継がない
            IsLoading = true;
        }
    }

    /// <summary>ローディングメッセージのみを更新する</summary>
    public void UpdateMessage(string message)
    {
        LoadingMessage = message;
    }

    /// <summary>進捗などのサブメッセージを更新する（空文字でクリア）</summary>
    public void UpdateSubMessage(string message)
    {
        LoadingSubMessage = message;
    }

    /// <summary>ローディング表示を終了する（参照カウントが0になったら非表示）</summary>
    public void Hide()
    {
        lock (_lock)
        {
            if (--_count <= 0)
            {
                _count = 0; // 過剰 Hide で負にならないようガード
                IsLoading = false;
                LoadingSubMessage = "";
            }
        }
    }
}
