# OriginalLauncher

fenrir の検索体験を令和に持ち込むための、Windows 向け自作ランチャー。
設定可能なホットキー(既定は CapsLock)で呼び出すボーダレスなポップアップから、
migemo によるあいまい日本語検索でフォルダ・exe・ショートカットを一発起動する。

## 特徴

- **設定可能な起動キー** — `config.json` の `Hotkey` で任意のキー+修飾キーの組み合わせに変更できる。既定は CapsLock（修飾キーなし）。`WH_KEYBOARD_LL` の低レベルキーボードフックで実現しており、修飾キーが揃わない場合はそのキーの通常入力を妨げない。
- **migemo 対応のあいまい検索** — [cmigemo](https://github.com/koron/cmigemo) を FFI で呼び出し、ローマ字のまま日本語フォルダ名・ファイル名を検索できる（例: `Kensaku` → 「検索」にヒット）。fenrir 由来の慣習で、**先頭文字を大文字で打った時だけ** migemo のかな/ローマ字あいまい展開が有効になる。先頭が小文字の場合は migemo を経由せず、入力文字列そのままの部分一致（大文字小文字は区別しない）で検索する。
- **表示対象を絞る** — フォルダ・`.exe`・`.lnk` のみを索引し、`node_modules` / `obj` / `bin` / `.git` などノイズになりがちなフォルダは既定で除外。
- **アイコン表示** — `SHGetFileInfo` で OS 標準の関連付けアイコンを取得して一覧に表示。
- **使用頻度によるランキング** — 起動した項目は使用回数・最終使用日時を記録し、次回以降の検索結果で優先的に上位表示する（未使用のものは走査順のまま）。
- **常駐 / トレイ** — 専用アイコンでタスクトレイに常駐し、右クリックメニューから明示的に終了するまで動作し続ける。
- **ショートカットコマンド** — `/s`・`/o` のような引数なしコマンドは入力した瞬間に即実行される（インデックス再構築・設定画面表示）。`//` や `/m` `/a` のような検索ショートカットはプレフィックスちょうどを入力すると入力欄がクリアされて「待機状態」になり、右側にその機能の説明が表示される。続けて検索語を入力して `Enter` で実行（`Esc` で待機状態のみ解除）。設定画面から自由に追加できる（既定は `//` → Google 検索）。
- **設定画面** — クエリ欄で `/o` と入力するか、トレイアイコンの右クリックメニューから開く。起動キー・インデックス対象ルート・最大表示件数・検索ショートカットをその場で編集・保存でき、保存すると（起動キー含めて）アプリを再起動せずに反映される。

## 動作環境

- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（ビルド・実行とも x64）

同梱の `migemo.dll`（[cmigemo](https://github.com/koron/cmigemo) v1.7.0 Windows MSVC x64 ビルド）が
x64 専用のネイティブライブラリのため、プロジェクトは `PlatformTarget=x64` 固定にしている。

## ビルド & 実行

```powershell
dotnet build
dotnet run --project src/OriginalLauncher
```

起動するとタスクトレイに常駐する。既定では `CapsLock` でポップアップを開閉する。

## 使い方

| 操作 | 内容 |
|---|---|
| `CapsLock`(既定、`config.json` で変更可) | ポップアップの表示/非表示をトグル |
| 文字入力（先頭大文字、例 `Kensaku`） | migemo によるかな/ローマ字あいまい一致の結果をリアルタイム表示（最大20件） |
| 文字入力（先頭小文字、例 `chrome`） | migemo を使わず、入力文字列そのままの部分一致（大文字小文字は区別しない）の結果をリアルタイム表示 |
| `↑` / `↓` | 候補選択を移動 |
| `Enter` / ダブルクリック | 選択中の候補を起動してポップアップを閉じる |
| `Esc` / フォーカス喪失 | ポップアップを閉じる |
| `/s` | ファイルインデックスを再構築（入力した瞬間に即実行） |
| `/o` | 設定画面を開く（入力した瞬間に即実行） |
| `//` などのショートカットのプレフィックス | 入力欄がクリアされ、右側に説明を表示した「待機状態」になる |
| 待機状態で検索語 + `Enter` | 検索ショートカットを実行（既定は `//` → Google 検索、設定画面で追加可） |
| 待機状態で `Esc` | 待機状態のみ解除（ポップアップは閉じない） |
| トレイアイコン右クリック → 設定 | 設定画面を開く |
| トレイアイコン右クリック → 終了 | アプリケーションを終了 |

## 設定ファイル

初回起動時に `%AppData%\OriginalLauncher\config.json` が既定値で自動生成される。
索引対象のルート・走査深度・除外パターン・拡張子ホワイトリストはここで変更できる。

```json
{
  "Roots": [
    { "Path": "C:\\Users\\<user>\\Desktop", "MaxDepth": 3, "Excludes": [] },
    { "Path": "D:\\", "MaxDepth": 4, "Excludes": ["D:\\SomeApp\\node_modules"] }
  ],
  "ExcludeNames": ["node_modules", "obj", "bin", "packages", ".git", ".vs"],
  "Extensions": [".exe", ".lnk"],
  "Hotkey": { "Key": "CapsLock", "Modifiers": [] },
  "MaxResults": 20,
  "SearchShortcuts": [
    { "Prefix": "//", "Description": "ブラウザ検索 (Google)", "UrlTemplate": "https://www.google.com/search?q={query}" },
    { "Prefix": "/m", "Description": "GoogleMap検索", "UrlTemplate": "https://www.google.com/maps/search/{query}" },
    { "Prefix": "/a", "Description": "Amazon検索", "UrlTemplate": "https://www.amazon.co.jp/s?k={query}" }
  ]
}
```

- `Roots[].Path` — 索引対象のルートフォルダ。
- `Roots[].MaxDepth` — そのルートからの最大走査深度。
- `Roots[].Excludes` — このパス配下は走査しない（前方一致）。fenrir の `-` 除外指定に相当。
- `ExcludeNames` — フォルダ名がここに含まれる場合、どのルートでも走査しない。
- `Extensions` — フォルダ以外で表示対象にする拡張子。
- `Hotkey.Key` — 起動キー。`"CapsLock"` のほか `System.Windows.Input.Key` の名前(例: `"Space"`, `"F13"`, `"L"`)を指定できる。
- `Hotkey.Modifiers` — `"Alt"` / `"Control"` / `"Shift"` / `"Windows"` を組み合わせて指定(例: `["Control", "Alt"]`)。修飾キーが揃わない限り、そのキー自体の通常入力は妨げられない。
- `MaxResults` — 検索結果の最大表示件数。
- `SearchShortcuts[].Prefix` / `.UrlTemplate` — クエリ先頭のプレフィックスと、`{query}` を検索語に置き換えて開く URL。fenrir 時代の `/m`（GoogleMap）`/a`（Amazon）のような運用を再現できる。
- `SearchShortcuts[].Description` — 何のショートカットかの短い説明。プレフィックス入力後、入力欄の右側に表示される。

`/o` で開く設定画面から GUI で編集でき、保存すると再起動なしで反映される（config.json を直接編集した場合はアプリの再起動が必要）。

## プロジェクト構成

```
src/OriginalLauncher/
├─ App.xaml(.cs)          起動処理、トレイアイコン、各サービスの配線
├─ MainWindow.xaml(.cs)    ボーダレス検索ポップアップ本体
├─ SettingsWindow.xaml(.cs) 設定画面("/o" ・ トレイメニューから起動)
├─ GlobalHotkey.cs         WH_KEYBOARD_LL による設定可能な起動キー(既定 CapsLock)
├─ CommandRouter.cs        "/s" "/o" "//" などのショートカットコマンド解釈
├─ MigemoService.cs        cmigemo (migemo.dll) の P/Invoke ラッパー
├─ LauncherConfig.cs       config.json の読み込み/既定値生成
├─ FileIndexer.cs          設定に基づくファイル/フォルダ走査
├─ IconProvider.cs         SHGetFileInfo によるアイコン取得
├─ UsageStore.cs           使用回数・最終使用日時の記録(検索結果のランキングに使用)
├─ ResultItem.cs           検索結果の表示用ラッパー
├─ Assets/AppIcon.ico      アプリアイコン(exe / トレイ共通)
├─ migemo.dll              cmigemo ネイティブライブラリ (x64)
└─ dict/migemo/            cmigemo 辞書一式 (UTF-8 版)
```

## サードパーティ

- [cmigemo](https://github.com/koron/cmigemo) — MIT License, Copyright (c) MURAOKA Taro (KoRoN).
  `migemo.dll` と `dict/migemo/` 以下の辞書ファイルは cmigemo v1.7.0 の公式リリースから取得したものをそのまま同梱している。

## ライセンス

MIT License — 詳細は [LICENSE](LICENSE) を参照。
