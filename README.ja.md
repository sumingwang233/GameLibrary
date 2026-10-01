<p align="center">
  <img src="assets/readme/hero.svg" width="960" alt="GameLibrary — Windows game library">
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.en.md">English</a> · <a href="README.ja.md">日本語</a>
</p>

# GameLibrary

ドライブに散らばったゲームを、一つのライブラリに。フォルダーを選び、起動ファイルを確認すると、一覧からゲームを開けます。

**[Windows インストーラーをダウンロード](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe)** · [すべてのリリース](https://github.com/sumingwang233/GameLibrary/releases) · [不具合の報告](https://github.com/sumingwang233/GameLibrary/issues)

[ダウンロード](#download) · [画面](#demo) · [使い始める](#start) · [よくある質問](#faq)

<a id="download"></a>
## ダウンロード

公開中の安定版は **v1.5.5** です。**Windows 10 / 11 x64** に対応しています。この版のデスクトップ UI は簡体字中国語です。README の翻訳は、配布中のアプリが各言語に対応していることを意味しません。

| ファイル | 用途 |
|---|---|
| [`GameLibrary-Setup-v1.5.5.exe`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe) | 推奨。現在のユーザー向け。インストール先を選択できます。 |
| [`GameLibrary-Portable-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Portable-win-x64-v1.5.5.zip) | 展開して GameLibrary.Desktop.exe を実行します。 |
| [`GameLibrary-Tools-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Tools-win-x64-v1.5.5.zip) | コマンドラインや自動化向けの Host、CLI、MCP。 |
| [`GameLibrary-v1.5.5-SHA256SUMS.txt`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-v1.5.5-SHA256SUMS.txt) | 上記 3 パッケージの SHA-256 チェックサム。 |

実行ファイルとインストーラーは Authenticode 未署名です。Windows が発行元不明の警告を表示する場合があります。このリポジトリから取得し、同じ版のチェックサムと照合してください。

```powershell
Get-FileHash .\GameLibrary-Setup-v1.5.5.exe -Algorithm SHA256
```

<a id="demo"></a>
## 画面

![カバー、検索、お気に入り、ナビゲーションを備えたゲーム一覧](website/public/assets/library.png)

![ゲームを整理するためのタグ管理](website/public/assets/tags.png)

v1.5.5 の独立したサンプルライブラリで撮影しています。アプリ標準のカバーを使用し、個人のゲームフォルダーは含みません。操作動画はまだ制作していません。[録画手順](docs/demo-recording.md)を用意しています。

<a id="start"></a>
## 使い始める

1. GameLibrary を開き、ゲームが入っているフォルダーを追加します。
2. スキャン後、候補のゲーム名、フォルダー、起動ファイルを確認して登録します。
3. ライブラリからゲームを選んで起動します。必要に応じてカバー、お気に入り、タグを設定します。

スキャンで見つかった項目は候補です。すべての EXE をゲームとして登録するわけではありません。認識されないゲームは手動で追加できます。

## 普段の操作

| 目的 | 操作 |
|---|---|
| ゲームを探す | 名前で検索し、タグで絞り込み、よく遊ぶゲームをお気に入りに登録します。 |
| コレクションを整理する | カバーやタグを編集し、グリッドとリストを切り替えます。 |
| 起動を設定する | 元の EXE を保持したまま、引数、ツール、翻訳手順を設定します。 |
| スクリプトから操作する | CLI と MCP は同じローカル Host とライブラリに接続します。 |

<a id="faq"></a>
## データとよくある質問

<details>
<summary><strong>スキャンや一覧からの削除で、ゲームファイルも消えますか？</strong></summary>

追加、スキャン、登録情報の削除では元のファイルを保持します。ファイルのクリーンアップを明示的に選択した場合は別途確認し、Windows のごみ箱を使用します。確定する前に対象フォルダーを確認してください。
</details>

<details>
<summary><strong>データの保存先は？アンインストールすると消えますか？</strong></summary>

既定のデータフォルダーは `%LOCALAPPDATA%\GameLibrary` です。登録情報はローカル SQLite に保存します。アンインストールしてもライブラリデータと元のゲームファイルは残ります。移動前にデータをバックアップしてください。プログラムのフォルダーはライブラリのバックアップではありません。
</details>

<details>
<summary><strong>アカウント、通信、開発ツールは必要ですか？</strong></summary>

ローカルゲームの整理にアカウントは不要で、ライブラリのテレメトリは送信しません。更新確認では GitHub Release にアクセスします。起動したゲームや設定したツールは通信する場合があります。配布物には .NET ランタイムが含まれ、利用者が .NET SDK、Node.js、Rust をインストールする必要はありません。
</details>

<details>
<summary><strong>どのパッケージを選び、どう更新すればよいですか？</strong></summary>

インストーラーはウィザード、スタートメニュー、アンインストール登録を提供します。ポータブル版は展開して実行します。ツール版は CLI と MCP 向けです。更新前にアプリを終了し、新版をインストールするかポータブル版のプログラムを置き換えます。データフォルダーは保持してください。変更や制約は各版の Release 説明を確認してください。
</details>

<details>
<summary><strong>現在のソース：v1.7.2、検証待ちプレリリース</strong></summary>

v1.7.2 は詳細画面の表紙 Ctrl+V、変更プレビュー付き Flash フォルダー審査、タグの既定色・白色と文字への色適用、未翻訳 Unity ゲームへのローカル翻訳プラグイン設定を追加します。API Key と元ファイルのバックアップはこの PC のみに保存します。設定後はユーザーが起動し翻訳結果を確認します。更新時に既存の無視ルールをリセットします。schema27 から戻すには移行前バックアップが必要です。[検証記録](docs/releases/v1.7.2-validation.md)をご覧ください。

v1.6.1 は推奨起動方法、中国語エントリーの優先、試行の検証後の自動既定化、明確な失敗の廃棄と手動復元を追加します。カードのプレイ時間、タイトルバーのテーマ切替、Windows タスクバーのアイコンも改善します。上記の安定版 v1.5.5 には含まれません。[v1.6.1 の説明](docs/releases/v1.6.1.md)に変更と検証状況を記載しています。

v1.7.0 は[ゲーム名翻訳](docs/title-translation.md)を追加します。Google/Bing の無料・キー不要の翻訳、一括処理の進捗とキャンセル、原文と中国語訳の表示切替に対応します。両レイアウトのタイトル下のプレイ時間も修正しました。[v1.7.0 プレリリース](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.0)でお試しください。未完了の手動検証は[検証記録](docs/releases/v1.7.0-validation.md)に記載しています。上部の安定版ダウンロードは v1.5.5 のままです。

v1.7.1 は翻訳後の副題の記号を修正します。原文に `-` がある場合、連続する長いダッシュを `：` に置換し、先頭と末尾の囲み記号を除去します。既存の訳名には詳細画面で再翻訳してください。原文と手動編集した訳名は保持します。[v1.7.1 プレリリース](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.1)と[検証記録](docs/releases/v1.7.1-validation.md)をご覧ください。
</details>

<details>
<summary><strong>開発と構成</strong></summary>

Windows、`global.json` で固定した .NET SDK 10.0.401、Node.js 22.12+、Rust stable が必要です。Windows MSVC Rust ツールチェーンと対応する C++ ビルドツールもインストールしてください。

```powershell
dotnet format --verify-no-changes
dotnet build GameLibrary.slnx -c Release
dotnet test GameLibrary.slnx -c Release --no-build
npm --prefix src/GameLibrary.Tauri ci
npm --prefix src/GameLibrary.Tauri test
npm --prefix src/GameLibrary.Tauri run typecheck
npm --prefix src/GameLibrary.Tauri run tauri dev
```

主 UI は Tauri + React、WPF は移行期間中の代替 UI です。デスクトップ、CLI、MCP は共通の契約を通じて .NET Host に接続します。Application はユースケース、Domain は業務ルール、Infrastructure は SQLite、ファイル、OS 連携を扱います。[構成](docs/code-review-graph/architecture.md) · [操作カタログ](contracts/operations.v1.json) · [リリース方針](docs/release-policy.md)

パッケージ作成はメンテナーから明示的な依頼がある場合だけ行い、通常の変更で自動公開しません。
</details>

## 協力とライセンス

[Issues](https://github.com/sumingwang233/GameLibrary/issues) にバージョン、エラー、再現手順を添えて報告してください。画像の個人パスは隠してください。提出前に関連チェックを実行し、共通操作の変更では契約、クライアント、テストを一緒に更新してください。

[MIT License](LICENSE)
