# PlainClip

[English](README.en.md) | 日本語

PlainClip（Microsoft Store での表示名は Plain Clip）は、Windows 11 向けの軽量なクリップボード履歴アプリです。タスクトレイに常駐し、直近のプレーンテキストだけをメモリ上に保持して、左手だけの操作で素早く書式なしで貼り付けます。

このリポジトリは、一般公開している PlainClip のソースコードです。

## 主な機能

- 左 Shift を 2 回押すと、現在のクリップボードのテキストを書式（フォント・色・HTML など）なしで貼り付け（キーは右 Shift・右 Ctrl・左 Ctrl、または通常のショートカットに変更可・無効化可）
- テキストのコピーを監視し、プレーンテキストだけを履歴に保存（既定 10 件、5～50 件。Windows のクリップボードの内容は変えない）
- タスクトレイのアイコンのクリック（または登録したショートカット）で履歴一覧を表示し、クリック・`↑↓`+`Enter`・`1`～`9` で貼り付け、`Delete` で削除
- 数字ショートカット（任意。例 `Ctrl+Alt+1`～`9`）で、一覧を開かずに番号の履歴を貼り付け
- パスワード管理ソフトなどが「履歴に残さない」と指定したコピーは履歴に入れない（Windows のクリップボードの履歴と同じ規約）
- Windows へのサインイン時の自動起動（任意）
- 表示言語は英語と日本語（Windows の表示言語に合わせるか、設定で固定）

画像・ファイルの履歴、定型文、検索、同期などの機能はありません。履歴はメモリ上だけに保持し、ファイルには保存しません（終了すると消えます）。PlainClip がインターネットへ送信することはありません（詳しくは下記のプライバシーポリシー）。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- Microsoft Store 版は .NET を内包しているため、追加のインストールは不要です。
- このリポジトリのソースからビルドして実行する場合は、[.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。

## インストール

正式な配布先は Microsoft Store です。通常はこちらから入手してください（無料）。

- Microsoft Store: https://apps.microsoft.com/detail/9MVCBZVNBJW2

この GitHub リポジトリでは実行ファイル（バイナリ）を配布していません。

## 基本的な使い方

1. 起動するとタスクトレイに常駐します。初回は「使い方」の画面が表示されます。
2. 普段どおりテキストをコピーします。
3. 貼り付けたい場所で左 Shift を 2 回押すと、現在のクリップボードのテキストを書式なしで貼り付けます。
4. 少し前にコピーしたテキストは、トレイアイコンをクリックして履歴一覧から選びます。
5. キーの割り当て・履歴件数・自動起動・表示言語などは、トレイアイコンの右クリック →「設定...」で変更できます。終了は同じメニューの「終了」です。

詳しい使い方は取扱説明書をご覧ください。

- 取扱説明書: https://www.ja1bus.com/plainclip/manual/

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)（確認済み: 8.0.425）
- `dotnet` CLI だけでビルドできます（Visual Studio は不要）。
- 本体は NuGet パッケージに依存しません。テストだけが NuGet の `Microsoft.NET.Test.Sdk`・`xunit`・`xunit.runner.visualstudio` を使います（初回のビルド時に nuget.org から取得します）。

### コマンド

リポジトリのルートで実行します。

```powershell
dotnet build PlainClip.csproj
dotnet test Tests/PlainClip.Tests.csproj
dotnet run --project PlainClip.csproj
```

`dotnet run` で起動するとタスクトレイに常駐します。終了はトレイのメニューの「終了」です。

配布用のフォルダ一式（Framework-dependent、win-x64）を作るには次を実行します。出力先は `publish\FrameworkDependent\` で、実行には .NET 8 Desktop Runtime（x64）が必要です。

```powershell
dotnet publish PlainClip.csproj -c Release -p:PublishProfile=FrameworkDependent
```

出力には `LICENSE` と `THIRD-PARTY-NOTICES.md` も入ります（アプリの設定画面の「MIT License」「第三者のソフトウェア」から表示します）。

### 構成

| パス | 内容 |
|---|---|
| `PlainClip.csproj` | 本体（WPF、`net8.0-windows`）。タスクトレイだけ WinForms の `NotifyIcon` を使用 |
| `Core/` | 画面・Win32 に依存しないロジック（履歴・書式なしテキストへの正規化・ダブルタップ判定・ショートカット・設定）と、画面の文字列（英語 `Strings.resx`・日本語 `Strings.ja.resx`） |
| `Tests/` | `Core` の単体テスト（xUnit） |
| `Services/`・`Views/`・`Interop/` | クリップボードの監視と読み書き・キーボードフック・ショートカット・貼り付け・トレイなどの処理、画面、Win32 API の宣言 |
| `icon.png`・`Resources/icon.ico` | アプリアイコン（`pwsh -File tools/draw-icon.ps1` で `icon.png` を描画し、`pwsh -File tools/make-icon.ps1` で `icon.ico` を作成） |

Microsoft Store 版のパッケージ（MSIX）を作る仕組みは、このリポジトリに含めていません。`PlainClip.csproj` にある `PlainClipMsix` の条件と `Services/AutoStartService.Packaged.cs` は Store 版のためのものです。

ソースコード中のコメントは主に日本語です。コメント中の「PROJECT.md」「§N」は、このリポジトリには含めていない開発用の仕様書とその節番号です。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの使用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## 関連リンク

- Web ページ: https://www.ja1bus.com/plainclip/
- 取扱説明書: https://www.ja1bus.com/plainclip/manual/
- プライバシーポリシー: https://www.ja1bus.com/plainclip/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9MVCBZVNBJW2
- お問い合わせ: contact@ja1bus.com
