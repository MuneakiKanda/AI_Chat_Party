# AI Conversation Studio

複数のAIキャラクターが同じルームで対話するアプリの開発リポジトリです。
`docs` の実装設計書 v0.2 に沿って、最初の **Slice 1：F# コンソール Mock PoC** を実装しています。

## 実行

.NET SDK 10 が必要です。`mise.toml` / `global.json` が対象バージョンを指定します。
`mise` を利用する場合は `mise install` と `mise exec -- dotnet ...` を利用できます。

```sh
dotnet restore AIConversationStudio.sln --locked-mode
dotnet build AIConversationStudio.sln --no-restore
dotnet test AIConversationStudio.sln --no-build --no-restore
dotnet run --project src/AIConversationStudio.Console --no-build -- --theme "AIと一緒に学ぶには？"
```

現在のクラウド作業環境では、各シェルで次の設定を行うと導入済み SDK を利用できます。

```sh
export DOTNET_ROOT=/workspace/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
cd /workspace/AI_Chat_Party
```

コンソールは外部APIや認証情報を使わず、ユーザーのテーマ投稿後に2人が1回ずつ応答します。
応答文は固定のMockです。テーマに応じた実AIの生成は後続Sliceで実装します。

```sh
# 台本形式の履歴で実行
dotnet run --project src/AIConversationStudio.Console -- --history script
# 2人目の応答をPASS／なりすまし／タイムアウトに変更
dotnet run --project src/AIConversationStudio.Console -- --scenario pass
dotnet run --project src/AIConversationStudio.Console -- --scenario reject
dotnet run --project src/AIConversationStudio.Console -- --scenario timeout
# 本文を含まないJSON診断を標準エラーへ出力
dotnet run --project src/AIConversationStudio.Console -- --diagnostics
```

`--history named|script`、`--theme`、`--scenario speak|pass|reject|timeout`、`--diagnostics` を指定できます。
確定した会話は標準出力に表示します。PASS、拒否、失敗は会話として保存されません。
Ctrl+Cはセッションを停止し、キャンセルを要求します。

## 構成

| プロジェクト | 責務 |
| --- | --- |
| Domain | ID、会話・通信・投稿結果、上限と状態の型 |
| Application | ポート、プロンプト合成、履歴変換、応答正規化、Gateway、会話処理 |
| Infrastructure | Mock Adapter、原子的メモリStore、計測、本文を含まない診断 |
| Console | 設定・依存性の組み立て、有限の逐次会話ハーネス |
| Tests | 純粋関数、並行予約・投稿、キャンセル、会話全体の受け入れテスト |

API Attemptは送信前に最大100枠を予約し、キャンセルでも返却しません。
AI投稿は最大50件で、人間の投稿を除外します。最終Attemptで新規処理を閉じ、
進行中の生成だけを完了させる `Draining` を経て停止します。
投稿コミットは、上限・世代・休止・重複・生成開始から60秒／新着5件の鮮度を同一ロック内で検査します。
投稿イベントは保存成功後にだけ発行します。

通信結果と会話上の結果は個別に記録します。通常のJSON診断には会話本文、人格設定、
生のエラーメッセージを含めません。Mockのリクエスト捕捉は合成データのテスト内でのみ明示的に有効化します。

## 現在の範囲と次の実装

Slice 1は設計書で定めた逐次ハーネスです。イベント駆動の並列スケジューラはSlice 4で導入します。
保存先は単一プロセスのメモリで、終了すると会話・計測結果は失われます。
構造化応答は `{ "decision": "pass" }` または `{ "decision": "speak", "content": "本文" }` を受け付けます。
通常の応答では、前後の空白を除いた完全一致の `[PASS]` だけがPASSになります。

次はSlice 2の実Provider Adapter・OS資格情報ストア・HTTPスタブによる送信形式検証です。
その後、2社目のProvider、並列会話、休止・ツール、Avalonia UIを設計書の順に実装します。
現在は自動再試行、モデルの自動代替、実ツール実行、実API通信を行いません。
詳細は [実装状況](docs/implementation-status.md) を参照してください。
