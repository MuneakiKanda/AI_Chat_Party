# OpenAI Slice 2 実API確認（手動・課金を伴う）

通常の `dotnet test` とMock実行は外部APIへ接続しない。
以下はOpenAIの利用権限・残高とWindowsの対話ターミナルがある利用者が、明示操作で実施する。
実装の検証と実API接続検証は別であり、この手順による実接続確認はまだ行っていない。

## 準備

1. Windowsで.NET SDK 10を用意し、READMEのrestore / build / testを行う。
2. APIキーを用意する。キーを引数・設定ファイル・チャット・ログへ書かない。
3. 登録コマンドで非表示入力する。Escは登録を中止する。

```powershell
dotnet run --project src/AIConversationStudio.Console -c Release --no-build -- --store-credential openai-personal
```

APIキーはWindows Credential ManagerのGeneric Credentialsへ、
`AIConversationStudio/openai-personal` という参照名で保存する。
アプリに渡すのは `openai-personal` だけ。OSユーザーごとの保存であり、別ユーザー／環境にキーは移らない。
再登録で更新でき、削除はWindowsの資格情報マネージャーから行う。
登録後の読取と更新は実API確認で検証する。Linuxでは平文保存へ代替せず失敗する。

## 接続先・モデルを確認（無課金プレビュー）

`<利用可能なモデルID>` を自分のOpenAIプロジェクトで利用可能なIDへ置き換える。
モデルを自動選択・自動代替しない。Chat Completionsのsystem/developerに対応するモデルが必要。

```powershell
dotnet run --project src/AIConversationStudio.Console -c Release --no-build -- --openai --model <利用可能なモデルID> --credential-ref openai-personal
```

接続先は `https://api.openai.com/v1/chat/completions` と表示される。
接続先は公式OpenAIに限定し、リダイレクト追従を無効にしている。
カスタム接続先はOpenAICompatibleの後続設計で扱う。
**このコマンドはAPIキー読取・HTTP送信を行わない。**

## 1回だけAPIへ送信

接続先とモデルを確認後、`--confirm-api-call` を付ける。実応答に応じた課金が発生する。

```powershell
dotnet run --project src/AIConversationStudio.Console -c Release --no-build -- --openai --model <利用可能なモデルID> --credential-ref openai-personal --confirm-api-call
```

- 短い挨拶を1回だけ要求。確定した本文を標準出力、本文・キーを含まない診断JSONを標準エラーへ表示する。
- 失敗時は診断JSONの `communication` に原因分類が出る。Providerのエラー本文・例外詳細は表示しない。
- PASS・拒否も通信は成功しうる。`communication` と `posting` を個別に確認する。
- GatewayのAttempt予約を通す。再試行・再生成・モデル代替は行わない。
- 個別の締切が必要なら `--timeout-seconds <秒数>` を明示指定できる。既定秒数は設計未決のため未設定。
- `--history script` で台本形式も選べる（再実行は別の有料リクエストになる）。

## 停止確認

送信開始後にCtrl+Cを押す。新規予約禁止→epoch更新→キャンセル通知の経路を通る。
停止後の遅延応答は保存されず、プロセスの終了コードは130になる。
Provider側の処理停止や課金停止を保証するものではない。
短時間で応答する場合は停止操作の再現が困難なため、決定的な遅延・無視・停止整合性の検証はHTTPスタブテストを使う。

## 検証記録

実施時にはOS／SDK／モデルID／成功または原因分類／停止結果を記録する。
キー・認証ヘッダー・会話本文・人格は記録しない。
本PRではLinux上のスタブ検証までで、実API・Windows実機検証は未実施。
