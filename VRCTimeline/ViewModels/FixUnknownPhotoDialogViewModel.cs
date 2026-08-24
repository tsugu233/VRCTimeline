using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using VRCTimeline.Helpers;
using VRCTimeline.Services;

namespace VRCTimeline.ViewModels;

/// <summary>ダイアログの動作モード。</summary>
public enum FixDialogMode
{
    /// <summary>「不明なワールド」写真: 既存訪問への割り当て or 新規ワールド名作成 ＋ フレンド付与。</summary>
    CreateOrAssign,

    /// <summary>手動訪問の編集: ワールド名変更・手動フレンド編集・修正の取り消し。</summary>
    EditManualVisit,

    /// <summary>手動フレンドを持つ実訪問の編集: 手動フレンドの追加/削除のみ（名前は変更不可）。</summary>
    EditRealVisitFriends,

    /// <summary>
    /// 分類済み写真の移動・グループ統合: 移動先の既存訪問を選ぶ or 新規訪問を作成。
    /// UI は CreateOrAssign と同じで、文言と「移動先候補から統合元を除外する」点だけが異なる。
    /// </summary>
    MovePhotos
}

/// <summary>
/// 「不明なワールド」写真の手動修正・および手動データの再編集ダイアログの ViewModel。
/// モードに応じて、新規割り当て／手動訪問の編集／実訪問の手動フレンド編集を切り替える。
/// 確定時は Confirmed、取り消し時は UndoRequested を立てて DialogHost を閉じ、呼び出し側が結果を適用する。
/// </summary>
public partial class FixUnknownPhotoDialogViewModel : ObservableObject
{
    private const string DialogHostId = "RootDialogHost";

    /// <summary>ワールド名検索で一度に表示する移動先候補の上限。</summary>
    private const int MaxSearchResults = 50;

    private readonly List<KnownPlayer> _knownPlayers;
    private readonly string _currentWorldName;

    /// <summary>撮影時刻の近傍候補（±6h）。検索欄が空のときの表示内容。</summary>
    private readonly List<CandidateVisit> _nearbyCandidates;

    /// <summary>ワールド名検索の対象となる全訪問（JoinedAt 降順）。</summary>
    private readonly List<CandidateVisit> _allVisits;

    public FixUnknownPhotoDialogViewModel(
        FixDialogMode mode,
        int targetCount,
        string? currentWorldName,
        IReadOnlyList<CandidateVisit> candidates,
        IReadOnlyList<KnownPlayer> knownPlayers,
        IReadOnlyList<TaggedFriend> existingFriends,
        IReadOnlyList<CandidateVisit>? allVisits = null,
        int? excludeVisitId = null)
    {
        Mode = mode;
        TargetCount = targetCount;
        _currentWorldName = currentWorldName ?? string.Empty;
        _knownPlayers = knownPlayers.ToList();

        // 統合元グループ自身は移動先になり得ないので、両方のリストから先に落としておく。
        _nearbyCandidates = candidates.Where(c => c.Id != excludeVisitId).ToList();
        _allVisits = allVisits == null
            ? _nearbyCandidates
            : allVisits.Where(c => c.Id != excludeVisitId).ToList();
        foreach (var c in _nearbyCandidates) Candidates.Add(c);

        if (mode is FixDialogMode.CreateOrAssign or FixDialogMode.MovePhotos)
        {
            // 候補があれば「既存訪問へ割り当て」をデフォルト、なければ「新規名入力」モード。
            _useExistingVisit = Candidates.Count > 0;
            SelectedCandidate = Candidates.FirstOrDefault();
        }
        else
        {
            _useExistingVisit = false;
            // 手動訪問の編集では現在のワールド名を初期表示する。
            _worldName = _currentWorldName;
        }

        foreach (var f in existingFriends) TaggedFriends.Add(f);

        // フレンドのみ登録（ワールド名なし）でも保存可能にするため、タグ増減で保存可否を再評価する。
        TaggedFriends.CollectionChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public FixDialogMode Mode { get; }

    /// <summary>編集モードか（新規割り当て・移動以外）</summary>
    public bool IsEditMode =>
        Mode is FixDialogMode.EditManualVisit or FixDialogMode.EditRealVisitFriends;

    /// <summary>割り当て先を選ぶモードか（未分類の初回修正／分類済み写真の移動）</summary>
    public bool IsAssignMode => Mode is FixDialogMode.CreateOrAssign or FixDialogMode.MovePhotos;

    /// <summary>割り当てモード切替（既存／新規）を表示するか</summary>
    public bool ShowModeToggle => IsAssignMode;

    /// <summary>移動先の検索欄と候補リストを表示するか</summary>
    public bool ShowVisitSearch => IsAssignMode;

    /// <summary>ワールド名を変更できるか</summary>
    public bool AllowRename => IsAssignMode || Mode == FixDialogMode.EditManualVisit;

    /// <summary>手動訪問編集用の単独ワールド名入力欄を表示するか</summary>
    public bool ShowRenameField => Mode == FixDialogMode.EditManualVisit;

    /// <summary>実訪問のため読み取り専用のワールド名を表示するか</summary>
    public bool ShowReadOnlyWorld => Mode == FixDialogMode.EditRealVisitFriends;

    /// <summary>「修正の取り消し」を許可するか（手動訪問のみ）</summary>
    public bool AllowUndo => Mode == FixDialogMode.EditManualVisit;

    /// <summary>編集対象の現在のワールド名（読み取り専用表示用）</summary>
    public string CurrentWorldName => _currentWorldName;

    /// <summary>ダイアログタイトル（モードで切替）</summary>
    public string TitleText => LocalizationService.GetString(
        Mode switch
        {
            FixDialogMode.MovePhotos => "Photo_Move_Title",
            FixDialogMode.CreateOrAssign => "Photo_FixUnknown_Title",
            _ => "Photo_FixEdit_Title"
        });

    /// <summary>サブタイトル（新規/移動=対象枚数 / 編集=ワールド名）</summary>
    public string SubtitleText => IsEditMode
        ? _currentWorldName
        : string.Format(
            LocalizationService.GetString(
                Mode == FixDialogMode.MovePhotos ? "Photo_Move_TargetCount" : "Photo_FixUnknown_TargetCount"),
            TargetCount);

    /// <summary>修正対象の写真枚数</summary>
    public int TargetCount { get; }

    /// <summary>割り当て候補の既存訪問</summary>
    public ObservableCollection<CandidateVisit> Candidates { get; } = [];

    /// <summary>現在リストに出ている候補が1件以上あるか（候補リスト／0件メッセージの出し分けに使用）</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>
    /// 「既存訪問へ割り当て」を選べるか。近傍候補が 0 件でも検索すれば他の訪問を選べるので、
    /// HasCandidates（絞り込み後の件数）ではなく訪問が 1 件でも存在するかで判定する。
    /// </summary>
    public bool CanAssignExisting => _allVisits.Count > 0;

    /// <summary>候補が 0 件のときの説明文（近傍に無い／検索条件に一致しない）。</summary>
    public string NoCandidateText => LocalizationService.GetString(
        string.IsNullOrWhiteSpace(VisitSearchText)
            ? "Photo_FixUnknown_NoCandidates"
            : "Photo_Move_NoSearchResults");

    /// <summary>true=既存訪問へ割り当て / false=新規ワールド名を入力</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _useExistingVisit;

    partial void OnUseExistingVisitChanged(bool value)
    {
        OnPropertyChanged(nameof(CreateNewMode));
    }

    /// <summary>UseExistingVisit の反転（新規入力モード用ラジオのバインド先）</summary>
    public bool CreateNewMode
    {
        get => !UseExistingVisit;
        set => UseExistingVisit = !value;
    }

    /// <summary>選択中の候補訪問</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private CandidateVisit? _selectedCandidate;

    /// <summary>移動先の絞り込み検索テキスト（ワールド名）</summary>
    [ObservableProperty]
    private string _visitSearchText = string.Empty;

    partial void OnVisitSearchTextChanged(string value) => UpdateCandidates();

    /// <summary>
    /// 移動先候補の絞り込み。検索欄が空なら撮影時刻の近傍候補（±6h）に戻し、
    /// 入力があれば全訪問からワールド名で絞る（別の日のグループへも統合できるようにするため）。
    /// </summary>
    private void UpdateCandidates()
    {
        var search = VisitSearchText?.Trim();
        var previous = SelectedCandidate;

        var matches = string.IsNullOrEmpty(search)
            ? _nearbyCandidates
            : _allVisits
                .Where(v => KanaHelper.ContainsKanaInsensitive(v.WorldName, search))
                .Take(MaxSearchResults)
                .ToList();

        Candidates.Clear();
        foreach (var m in matches) Candidates.Add(m);
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(NoCandidateText));

        // 絞り込み後も同じ訪問が残っていれば選択を維持し、消えたなら先頭を選び直す。
        SelectedCandidate = Candidates.FirstOrDefault(c => c.Id == previous?.Id)
                            ?? Candidates.FirstOrDefault();
    }

    /// <summary>新規入力モード／手動訪問編集で使うワールド名</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _worldName = string.Empty;

    // ── 同席フレンドのタグ付け ──

    /// <summary>フレンド検索入力テキスト</summary>
    [ObservableProperty]
    private string _friendSearchText = string.Empty;

    /// <summary>検索に対するオートコンプリート候補</summary>
    public ObservableCollection<KnownPlayer> FriendSuggestions { get; } = [];

    /// <summary>タグ付け済みフレンド（チップ表示）。編集モードでは既存の手動フレンドを初期投入する。</summary>
    public ObservableCollection<TaggedFriend> TaggedFriends { get; } = [];

    partial void OnFriendSearchTextChanged(string value) => UpdateSuggestions();

    private void UpdateSuggestions()
    {
        FriendSuggestions.Clear();
        var search = FriendSearchText?.Trim();
        if (string.IsNullOrEmpty(search)) return;

        var taggedKeys = TaggedFriends.Select(t => Key(t.DisplayName, t.UserId)).ToHashSet();
        var matches = _knownPlayers
            .Where(p => KanaHelper.ContainsKanaInsensitive(p.DisplayName, search)
                        && !taggedKeys.Contains(Key(p.DisplayName, p.UserId)))
            .Take(8);
        foreach (var m in matches) FriendSuggestions.Add(m);
    }

    /// <summary>UserId があれば UserId、無ければ表示名で同一性を判定するためのキー。</summary>
    private static string Key(string name, string userId)
        => !string.IsNullOrEmpty(userId) ? "u:" + userId : "n:" + name;

    [RelayCommand]
    private void AddFriendFromSuggestion(KnownPlayer? player)
    {
        if (player == null) return;
        AddTagged(player.DisplayName, player.UserId);
    }

    [RelayCommand]
    private void AddFriendFreeText()
    {
        var name = FriendSearchText?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        // 既知プレイヤーに完全一致があれば UserId を引き継ぐ（カードクリック検索や統計連動のため）。
        var known = _knownPlayers.FirstOrDefault(p =>
            string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        AddTagged(name, known?.UserId ?? string.Empty);
    }

    private void AddTagged(string name, string userId)
    {
        var key = Key(name, userId);
        if (TaggedFriends.Any(t => Key(t.DisplayName, t.UserId) == key)) return;
        TaggedFriends.Add(new TaggedFriend(name, userId));
        FriendSearchText = string.Empty;
        FriendSuggestions.Clear();
    }

    [RelayCommand]
    private void RemoveFriend(TaggedFriend? friend)
    {
        if (friend == null) return;
        TaggedFriends.Remove(friend);
    }

    /// <summary>保存ボタンが押されて確定したか</summary>
    public bool Confirmed { get; private set; }

    /// <summary>「修正の取り消し」が押されたか</summary>
    public bool UndoRequested { get; private set; }

    // 既存割り当てモードでは候補選択が必須。新規モードでは、ワールド名が空でも
    // フレンドを1人以上タグ付けしていれば保存可（ワールド名は「不明なワールド」で自動命名）。
    // 編集モードは常に保存可（名前変更／フレンド差分を適用。変更なしでも no-op）。
    private bool CanSave()
    {
        if (IsEditMode) return true;
        return UseExistingVisit
            ? SelectedCandidate != null
            : !string.IsNullOrWhiteSpace(WorldName) || TaggedFriends.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Confirmed = true;
        try { DialogHost.Close(DialogHostId); }
        catch { /* ダイアログセッションが既に閉じている等は無視 */ }
    }

    [RelayCommand]
    private void Undo()
    {
        UndoRequested = true;
        try { DialogHost.Close(DialogHostId); }
        catch { /* 同上 */ }
    }
}

/// <summary>
/// タグ付け済みフレンド（手動同席者）の表示・結果用モデル。
/// SessionId が非 null の場合は既存の手動セッション（編集モードでのプリロード分）を表す。
/// </summary>
public record TaggedFriend(string DisplayName, string UserId, int? SessionId = null);
