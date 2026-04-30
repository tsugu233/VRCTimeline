# VRC Timeline

VRChat 活動記録ツール

## 概要

VRC Timeline は VRChat のログファイルを自動で読み取り、  

ワールド訪問履歴・プレイヤー履歴・写真・動画・通知を  

まとめて記録・閲覧できる Windows アプリです。

動画のタイトル及びサムネの取得以外はすべてローカルでの動作となります。  
<br>

## 動作環境

・OS       : Windows 10 / 11（64bit）  

・ランタイム: .NET 8 デスクトップランタイム  

　※ 未インストールの場合は下記 URL から入手してください  

　https://dotnet.microsoft.com/ja-jp/download/dotnet/8.0  

　「.NET デスクトップ ランタイム 8.x.x」→「x64」  

・VRChat   : PC 版（Steam / VRChat 公式インストーラー）  
<br>

## インストール・起動方法

1\. ダウンロードした zip ファイルを任意のフォルダに展開します。

2\. VRCTimeline.exe をダブルクリックして起動します。

&#x20; （初回起動時は警告が出ると思いますが、詳細より起動してください。）

&nbsp;&nbsp;&nbsp;初回起動時にデータベースが自動生成されます。  
<br>


※nukora氏の「VRChatActivityLogViewer」を利用していた方は、  

　設定画面よりインポートすることでデータを引き継げます。  

　データの不足がない限り問題なく利用できます。  
<br>


## 初期設定

基本的には初回起動時に設定されます。  

もし正常でない場合は左メニューの「設定」を開いて以下を確認・設定してください。  
<br>


### ■ VRChat ログフォルダ

VRChat のログファイルが保存されているフォルダを指定します。  

通常は自動で検出されますが、変更している場合は手動で指定してください。  

デフォルト: `C:/Users/<ユーザー名>/AppData/LocalLow/VRChat/VRChat`  
<br>


### ■ 写真フォルダ

VRChat のスクリーンショットが保存されているフォルダを指定します。  

デフォルト: `C:/Users/<ユーザー名>/Pictures/VRChat`  
<br>


### ■ 起動設定

・「Windows 起動時に自動起動」: PC 起動時に VRC Timeline を自動起動  

・「起動時にタスクトレイに最小化」: 起動時にウィンドウを非表示にする  

・「VRChat 起動時に自動表示」: VRChat が起動したらウィンドウを前面に表示  
<br>


### ■ テーマ

ダーク / ライトモードの切替と、アクセントカラーのカスタマイズができます。  
<br>


## 各機能の説明



### ■ リアルタイム

VRChat 起動中に下記情報をリアルタイム表示します。  

・ワールド  

・プレイヤー情報(本人の移動、他の人の入退室の時刻)  

・Boop、インバイト通知、リクイン通知  


VRChat が起動すると自動でモニタリングを開始します。  
<br>
<img width="1186" height="793" alt="スクリーンショット 2026-04-30 174317" src="https://github.com/user-attachments/assets/3e40c2e6-1177-4f55-b157-214ba7c40d84" />


### ■ アクティビティ

過去のワールド訪問履歴を一覧表示します。  

・日付範囲・ワールド名・プレイヤー名でフィルタリング可能  

・プレイヤー名で検索すると同席回数・累計時間が表示されます  

・訪問を選択すると「再参加」ボタンが表示されます（同インスタンスへ移動）  

・「写真を表示」でその訪問時に撮影した写真を写真タブで表示します  
<br>
<img width="1186" height="793" alt="スクリーンショット 2026-04-30 165315" src="https://github.com/user-attachments/assets/9d20e1fa-bb42-4d1b-9494-00e9341a1813" />


### ■ 写真

VRChat スクリーンショットをワールド訪問ごとにグループ表示します。  

・日付範囲・ワールド名・プレイヤー名でフィルタリング可能  

・写真をクリックすると詳細（撮影時刻・ワールド・一緒にいたプレイヤー）表示  

・詳細のプレイヤーを押すとそのプレイヤーで絞り込み検索をかけます  

・改名しても同じ人物として表示されます  

・「開く」で既定のビューアで写真を開きます  

・「フォルダを開く」でエクスプローラーを開きます  
<br>
<img width="1186" height="793" alt="スクリーンショット 2026-04-30 165221" src="https://github.com/user-attachments/assets/915bd39d-f311-4d77-bf1f-be4407426336" />


### ■ 通知履歴

受信した Invite / Request Invite / Boop を記録・表示します。  

・送信者名・日付・通知種別でフィルタリング可能  
<br>
<img width="1186" height="793" alt="スクリーンショット 2026-04-30 165430" src="https://github.com/user-attachments/assets/32e7b844-b92e-4a59-9d0b-bce789a68191" />


### ■ 動画履歴

ワールド内で再生された動画の URL を記録・表示します。  

・YouTube の動画はタイトルとサムネイルを自動取得します  

・URL をクリックすると既定のブラウザで動画を開きます  
<br>
<img width="1186" height="793" alt="スクリーンショット 2026-05-01 021319" src="https://github.com/user-attachments/assets/08988fe0-093e-4682-a591-791d07e7b027" />


### ■ 設定

各種設定と、以前のデータのインポートができます。  
<br>

## VRChatActivityLogViewer からのデータインポート

VRChatActivityLogViewer を以前ご利用の場合、その DB データを  

VRC Timeline にインポートできます。

設定 → 「VRChatActivityLogViewerのDBをインポート」ボタン → VRChatActivityLog.db ファイルを選択

※ 既にアクティビティデータがある場合、インポートボタンは非表示になります。  

※ インポートをする際はVRChatを起動していない状態での実行を推奨します。
<br>
<img width="1240" height="790" alt="スクリーンショット 2026-04-30 161254" src="https://github.com/user-attachments/assets/49d049ed-bb82-4dc0-85a1-e71fe3c83ae4" />


## データの保存場所

アプリのデータは以下のフォルダに保存されます。

```text

 %APPDATA%/VRCTimeline/
   ├─ vrctimeline.db   (活動データベース)
   ├─ settings.json    (設定ファイル)
   └─ thumbnails/      (動画サムネイルキャッシュ)

```

「設定」→「データフォルダを開く」でフォルダを確認できます。  
<br>


## アンインストール

1\. VRCTimeline.exe を削除してください。

2\. 必要であれば `%APPDATA%/VRCTimeline` フォルダも削除してください。

&#x20;  （アクティビティデータ・設定がすべて消えます）  
<br>


## よくある質問

Q. VRChat のデータが記録されない  

A. VRChat のログ出力が有効になっているか確認してください。  

&nbsp;&nbsp;&nbsp;&nbsp;VRChat の設定から「ログを出力する」を有効にしてください。  

&nbsp;&nbsp;&nbsp;&nbsp;また設定画面でログフォルダのパスが正しいか確認してください。  
<br>

Q. 写真が表示されない  

A. 設定画面で写真フォルダのパスが正しいか確認してください。  
<br>

Q. .NET ランタイムに関するエラーが出る  

A. .NET 8 デスクトップランタイム（x64）をインストールしてください。  

&nbsp;&nbsp;&nbsp;&nbsp;  Microsoft 公式サイト（ https://dotnet.microsoft.com/ja-jp/download/dotnet/8.0 ）から  

&nbsp;&nbsp;&nbsp;&nbsp;「.NET デスクトップ ランタイム 8.x.x」→「x64」を選択してください。  
<br>

Q. YouTube サムネイルが取得されない  

A. インターネット接続を確認してください。  

&nbsp;&nbsp;&nbsp;&nbsp;取得に失敗した場合は次回読み込み時に再試行されます。  
<br>


## 免責事項

・本ツールは非公式ツールです。VRChat Inc. とは一切関係ありません。  

・本ツールの使用によって生じたいかなる損害についても製作者は責任を負いません。  

・VRChat の仕様変更により、一部機能が動作しなくなる場合があります。  
<br>


## 更新履歴

ver 1.0.0  初回公開リリース  
<br>


## クレジット

過去の活動ログのインポート機能において、  

nukora氏の「VRChatActivityTools (MIT License)」との互換性を実装しています。  

長い間、愛用させていただいた素晴らしいツールに敬意を表します。  

https://github.com/nukora/VRChatActivityTools  
<br>


## 制作

&#x20; 製作者: tsugu233

