# 実装状況

## Slice 1

実装設計書 v0.2 §6・§8のMockによる縦切りを実装。
Domain → Application ← Infrastructure の依存方向を維持し、Consoleで組み立てる。
テストプロジェクトはPoCのため1つにまとめ、関心ごとにファイルを分離している。

| 受け入れ項目 | 実装・検証 |
| --- | --- |
| T01–T03 | 2キャラ1往復、他者がOthersになる視点変換、2種類の履歴 |
| T04–T06、T24 | PASS、なりすまし拒否、自名前置き、引用保持・Unicode・不正形式 |
| T07–T09 | 最後のAttempt枠の競合、上限後のAdapter未呼び出し、再試行なし |
| T10、T18 | キャンセル、予約後送信前キャンセル、Reserved/Sentの区別 |
| T11、T15、T17、T19–T20 | メモリStore境界で鮮度・投稿上限・同時コミット・Drainingを検証 |
| T13 | 合成の秘密文字列・人格・本文・エラー詳細がJSON診断に含まれない |
| T22–T23 | ConnectionId単位のAdapter、モデル上書き、指示セグメントと版 |

T15/T17の正式スケジューラ経由の検証はSlice 4、T12/T16/T21の実Adapter・
ストリーミング・実API緊急停止はSlice 2以降。T14は型付きエラーと代替なしの検証までで、
参加者の利用不可状態・復帰管理は未実装。T25のツール往復は未実装。

## PoCでの実装判断

- 型とポートは設計スケッチをコンパイル可能な名前で整理した。
- `IConversationStore` に予約・ライフサイクル・投稿のポートをまとめ、同一同期境界を使用する。
- 世代・スナップショットの登録は生成開始時。予約と送信の間にキャンセルしてもAttemptを返却しない。
- 再試行条件の具体値は未決のため、Gatewayは一度だけAdapterを呼ぶ。
- ModelBindingとルーム設定はOrchestrator構築時に固定する。
- `StartedAt` が鮮度判定の起点。時刻とIDは注入し、テストは実時間待機を使わない。
- 休止検査の基盤としてセッション全体のepochを利用する。休止変更は他の進行中生成にも影響するため、
  キャラクターごとの世代管理と復帰処理はSlice 5で拡張する。
- `ToolCalls` の型は用意するが、Slice 1では拒否として終了し、ツール実行や追加API送信はしない。
- テスト用の全文捕捉と、アプリ利用者向けの会話表示を、本文を含まない診断出力と分離する。
- イベントと計測はインメモリ。永続化・プロセス障害時の配送保証は後続の設計対象。

## 残りのSlice

2. Provider 1社、OS資格情報ストア、HTTPスタブ、最小緊急停止の実API検証。
3. Provider 2社、接続設定、異なるProviderで会話。
4. イベント集約、K=2、遅延、無操作停止、スケジューラと投稿制御の統合。
5. ルーム／グローバル休止と復帰、Draft Registry、Tool Calling。
6. Avalonia UI、ルーム一覧・未読・管理画面。

## PR #1 第1回レビュー反映

- 投稿確定後の通知失敗は `NotificationResult.Failed` として記録し、投稿結果と生成監査記録を保持する。
  通知の自動再試行は行わない。永続化時のTransactional Outboxは後続で検討する。
- 人間の投稿はRunning中のみ受け付ける。Draining / Stoppedでは例外で拒否し、履歴・連番を変更しない。
- 接続の存在確認はAttempt予約前に行う。未登録接続は枠を消費せず、予約後の通信失敗やキャンセルは消費する。
- 回帰テストを含む全69件が成功。


## Slice 2：OpenAI（実装済み、実接続検証は未実施）

- `OpenAIAdapter`：既存の `IProviderAdapter` を維持。注入可能なHttpClientで非ストリーミングの
  Chat Completionsを呼び出す。モデルIDは各リクエストの設定値を使用し、代替や再試行をしない。
- `OpenAIConnection`：ConnectionId・CredentialRef・公式HTTPSエンドポイントのみ保持。
  OpenAICompatibleは範囲外。APIキーは送信直前にInfrastructure内の `ICredentialStore` から取得する。
- `WindowsCredentialStore`：Windows Generic Credentials（現在のOSユーザー、ローカルマシン永続化）。
  Win32のCredReadW/CredWriteWを利用。非Windowsでは明示的なエラーにし、平文ファイル・環境変数へ代替しない。
- アプリ指示をsystem、ルーム指示・人格を明示的な区切りと優先順位付きのdeveloperメッセージへ変換。
  同一レイヤー内はルーム→人格の順。Selfだけassistant、Othersと台本はuser。
  この割当が指定モデルで利用可能であることは必要条件であり、モデルが指示優先順位を守る保証ではない。
- 応答は最終的なassistantのテキスト（finish_reason=stop）のみ受け付ける。
  不正JSON・未完了・ツール応答はInvalidResponse。使用量不明は欠損のまま扱う。
- `EmergencyStopController`：予約を閉じepoch更新→キャンセル通知。
  同じ経路を実APIコマンドのCtrl+Cと決定的テストで使用する。
- 実APIコマンドはモデルと接続先を事前表示し、`--confirm-api-call` がなければ課金通信しない。
  明示確認後も1 Generationのみ実行し、Gatewayの100 Attempt上限を維持する。

### エラー分類

| 証拠 | 結果 |
| --- | --- |
| 呼び出し元トークンのキャンセル | Cancelled |
| DNS解決エラー | ConnectionFailedBeforeSend（送信前と断定できる範囲のみ） |
| 応答ヘッダー受信後のOperationCanceledException、呼び出し元は未キャンセル | TimedOutAfterSend |
| 応答ヘッダー未受信のOperationCanceledException、呼び出し元は未キャンセル | TimeoutSendStateUnknown |
| 401/403 | AuthenticationFailed |
| 429 | RateLimited（Retry-Afterのdeltaを欠損許容で保持、自動再試行なし） |
| エラーコードinsufficient_quota | QuotaExceeded |
| エラーコードmodel_not_found | ModelNotAvailable |
| 接続リセット、一般の接続エラー、他のHTTPエラー | TransportFailed（送信状態を断定しない） |
| 資格情報が取得不可 | CredentialUnavailable |
| 成功応答の形式が不正／未完了 | InvalidResponse |

ヘッダー未受信は「未送信」の証拠にならない。HttpClientの観測からProviderの実処理・課金状況を
断定しない。Gatewayも呼び出し元未キャンセルのOperationCanceledExceptionをCancelledにしない。
AttemptSent/SentCountは既存契約上のAdapter呼び出し開始を示し、ネットワークへのバイト送信の証拠ではない。
資格情報取得に失敗した場合も、Gateway予約後なら保守的に枠を消費する。

### 検証結果と制限

- .NET 10 / Linuxでロック付きrestore、Release build（警告0）、全101テスト成功（失敗0）。
  Slice 1の69件を維持し、HTTPスタブと送信形式・エラー・秘密情報非記録・緊急停止を32件追加。
- 通常ログ／診断JSON／エラー記録に合成キー・人格・会話が含まれないことを検証。
  内部例外には合成キーを注入し、公開結果には汎用のエラー分類だけが残ることを確認。
- タイムアウトや停止はTaskCompletionSourceと例外注入で再現し、実時間待機・課金を使わない。
- プレビュー（実API未送信）とSlice 1のMockコンソールを実行確認済み。
- **実API接続確認は未実施**：この環境はLinuxでWindows資格情報ストアを利用できず、APIキーの登録もない。
  OpenAIへの実ネットワーク到達性・モデル利用権限・課金・実応答は未確認。
- Windowsネイティブ資格情報ストアはコンパイル確認まで。Windows実機で登録・読取・更新・失敗時の挙動を
  [手順](openai-smoke-test.md) に従って検証する必要がある。

### レビュー対象の実装提案・未決事項

- 初回のAPIとしてChat Completions、system/developerの割当を採用。指定モデルがこの形式に対応することが必要。
  非対応時に別API・ロール・モデルへ自動変更しない。このAPI／割当の採用可否は今回のレビュー対象。
- タイムアウト秒数は未決。HttpClient既定100秒を暗黙の仕様にせず、確認コマンドでは未指定時に自動締切を
  設けない（Timeout.InfiniteTimeSpan）。利用者が明示指定する `--timeout-seconds` は許可する。
  既定値の確定は設計側へ委ねる。未指定時はCtrl+Cによる停止が必要になりうる。
- 再試行回数・待機時間は引き続き未決かつ無効。絶対時刻のRetry-Afterから待機時間を算出する処理も未実装。
- Windows以外の資格情報ストア、Provider別モデル能力判定、2社目、ツール、ストリーミング、UIは未実装。
- 次Sliceには実機確認結果、モデル互換性、資格情報OS展開を引き継ぐ。
  イベント配送保証・監査処理の耐障害性（Outboxを含む）は前PR同様の後続課題。
