# NetSuiteコネクタ追加仕様 ― ファイル添付・接続運用・受入試験

2026年11月20日 ｜ 対象：Codeer.LowCode.Blazor.Connectors.NetSuite ｜ API調査・仕様・Sandbox検証計画の差分

受注や商談にPDFを添付する機能は、File Cabinetにファイルを作成し、そのファイルを業務レコードに関連付ける2段階で構成する。ファイルIDが既知なら、作成、添付、取得、解除、本体削除まで標準RESTで扱える。既存の受注を開いた時に「NetSuite画面から添付した分も含め、現在の添付を一覧表示する」には、親レコードからファイルIDを引けることを別途確認する必要がある。[File CabinetのREST仕様][files]・[添付と解除][attach]・[添付ファイルを対象にした検索][search]

## 1. 以前の資料への適用箇所

| 以前の資料・項目 | 今回の差分 |
|---|---|
| 01 利用API調査／NS-21・22 | ファイル操作、レコードとの関連付け、一覧の探索を§2～3に追加。Document ServiceとRecord Serviceの呼び分けを明記 |
| 02 仕様案／Module・権限・保存 | Designerで取り込む情報、既存の添付欄との接続、親保存と添付の部分成功を§4～5に追加 |
| 05 設計調査／データソースに載せない機能 | 添付をレコードの通常列と独立した操作として扱う。明細仮想表やSuiteQLのCRUD変換にバイナリを載せない |
| 01 利用API調査／NS-24・同時実行 | 証明書管理APIのホスト・パス・入力、同時実行枠の照会を§6に追加 |
| 06 Sandbox検証計画、および11月16日の公開版レビュー | 添付・運用試験を§7に追加。既出のレビュー指摘は§8に対応付けた |

以下のHTTP契約はOracleの公開資料による。画面動作、再開方法、対応判定はCLB側の設計提案。実アカウントでの実行結果や現行リポジトリの対応済み機能を示すものではない。

## 2. ファイル操作のREST契約

下表のパスは接続先のSuiteTalkホストの `/services/rest/` に続く。IDは対象接続内のinternal ID。ファイルはバイナリのままサーバーから送受信できる。[File Cabinet][files]・[添付と解除][attach]

| 利用者の操作 | HTTPとパス | 実装上の要点 |
|---|---|---|
| 新しいファイルを登録 | `POST document/v1/file/` | multipart/form-data。公式例の送信パートは `attributes`（JSON）と `data`（バイナリ）。204応答の `x-ns-link` 末尾からfileIdを得る |
| 登録済みファイルを添付 | `POST record/v1/{type}/{parentId}/!attach/file/{fileId}` | bodyは空でよい。成功204。Oracleの実例は商談 `opportunity/379` とファイル `398` |
| 名前・保存場所を読む | `GET document/v1/file/{fileId}` | JSONに属性とコンテンツへのリンク。外部IDが設定済みなら `file/eid:{externalId}` でも照会可能 |
| 本体をダウンロード | `GET document/v1/file/{fileId}/content` | `Accept: application/octet-stream`。200応答をストリーム転送。欠落または内容なしは404、規定サイズ超過は413 |
| 添付を外す | `POST record/v1/{type}/{parentId}/!detach/file/{fileId}` | bodyは空、成功204。この親との関係を解除し、File Cabinetの本体は保持する |
| 本体を削除 | `DELETE document/v1/file/{fileId}` | 成功204。File Cabinet全体に作用する管理操作。通常の添付欄の「外す」からは呼ばない |
| 本体を差し替え | `PATCH document/v1/file/{fileId}/content` | 属性を維持して内容を置換。複数の親で同じファイルを共有していれば同じ内容を参照する。特定の親だけ差し替える場合は新規添付の成功後に旧添付を外す |
| 文書サービスのメタデータ取得 | `GET document/v1/metadata-catalog` | `Accept: application/swagger+json`。file、folderそれぞれのサブカタログへの直接アクセスはサポートされない |

**実装時に間違えやすい点。** アップロード時は、公式例では `attributes` に `{"isLink":false,"fileName":"quote.pdf","folder":{"id":"458"}}` を入れ、`data` にPDFを入れる。一方、取得時のファイル名は `name`、複合形式で取得したバイナリの応答パート名は `content`。送受信のDTOを一つに固定せず、Sandboxで要求と応答を記録する。通常のレコード作成で使うLocationだけを見ると、ファイル作成成功後にIDを取り落とす。[作成・取得例][files]

**サイズと実行方式。** Document Serviceは同期実行で、アップロード・ダウンロード上限は10 MB。CLB側にもサイズ判定を設け、サーバー応答の413も利用者に返す。境界のバイト数とmultipartのオーバーヘッドの扱いは試験で決める。File Cabinetには別経路で登録された10 MB超の既存ファイルもあり得るので、一覧取得の成否とダウンロード可否を分ける。[制限と取得時の応答][files]

## 3. 親レコードに付いているファイルをどう列挙するか

受入条件は「親の種類とIDを渡すと、その親に現在付いていて実行主体が参照できるファイルのID・名前が返る」。ファイル名やフォルダIDの一致だけでは関連付けを判断できない。今回確認した標準REST資料では、この条件を満たす汎用一覧エンドポイントは特定できなかった。

| 経路 | 判明したこと | 実装判断 |
|---|---|---|
| Document Serviceのファイル検索 | File Cabinet内の検索・ページングが可能 | 既存ファイルを選ぶ用途。親との関連の証拠には使わない。[File Cabinet][files] |
| Advanced Search／Saved Search | Oracleは取引、商談、顧客、カスタムレコード等で「File Fields」を結果列に指定できると説明。RESTの保存検索実行は条件・フィルター・ソートの上書き不可 | Sandboxで親ID固定の保存検索を作り、親ID・ファイルID・名前を含められるか、UIとRESTの結果が一致するかを確認。固定検索の成功だけで任意の親を引けるとは判断しない。[添付検索][search]・[保存検索REST][saved] |
| 任意の親を指定する一覧 | 標準RESTで親指定の手段を実証できなかった場合の候補は、対象親のinternal IDで検索を限定する小さなRESTlet。SuiteScriptのN/searchは検索条件・結合列を指定できる | 親種別ごとに検索タイプ、ファイル結合ID、返せる列を確認。結果を親ID・ファイルID単位で重複排除し、安定した順序でページングする。RESTletを選んだ接続だけ配備・認証設定を追加する。[N/search][nsearch] |
| CLBが登録したファイルIDの記録 | アップロード直後の続行や、部分成功の照合に使える | 他システムやNetSuite UIでの添付・解除は記録されない。完全な添付一覧として表示しない |

最初にNetSuite UIで商談または受注にPDFを添付し、同じ親を連携用ロールで検索する。親ID・ファイルIDの組が実際に返る経路を採用する。他の親にしか付いていないファイルが混じる、IDが返らない、権限不足、ページ続行不能の各ケースを「添付0件」として処理しない。メールメッセージに添えたファイルは、業務レコードに直接付くファイルとは取得範囲を分ける。

## 4. CLBのModule、Designer、サーバーへの反映案

- **Designerで取り込むもの**　既存の接続定義とレコード種別を流用する。Document Serviceのメタデータは接続単位で取得し、Moduleには保存先フォルダと利用する操作を設定する。フォルダIDは環境別に解決する。対象型ごとに「ファイル登録・添付・一覧・取得・解除」の検証結果を保持し、商談での成功を全レコードに一律適用しない。
- **既存の添付欄との接続**　CLBのファイル入出力の拡張点を先に確認する。サーバーが接続ID・Module・親IDから添付操作を解決し、一覧にはfileId、表示名、取得できた場合だけサイズと種類を返す。未保存の親は、親保存の成功でinternal IDが確定してから添付する。
- **ダウンロード**　CLBサーバーで親レコードの閲覧権限と、そのファイルが当該親で参照可能なことを確認して転送する。M2M認証のNetSuite側ロールと画面利用者の権限が異なるため、ブラウザーが渡したfileIdだけで本体を返さない。NetSuiteのフォルダ制限も適用される。既存添付の所属確認ができない構成では、任意のfileIdでの取得を公開しない。[NetSuite上のフォルダ権限][permissions]
- **操作名と影響範囲**　通常のレコード画面は「添付を外す」、File Cabinetの本体を消す管理機能は「ファイル本体を削除」とする。親レコード削除に本体削除を連動させない。共有ファイルの置換は別操作にする。
- **画面・バッチ共通**　サーバーサイドスクリプトから使う場合も同じ添付処理・結果を利用する。複数ファイルではファイルごとに結果を返す。成功したファイルの再送を避けられるようにする。

## 5. 部分成功と再実行

親の保存、ファイルの作成、親への添付はそれぞれ独立して確定する。CLB側は操作ID、接続、親種別・親ID、分かった時点のfileId、最後に確定した段階を保持する。通信切断も明示的な拒否も同じ「失敗」として再送すると、同じPDFや受注が二重登録され得る。

| 発生箇所 | 利用者への結果／再開位置 |
|---|---|
| 親保存に失敗 | 添付は未開始。親の入力を修正する。親保存の応答が失われた場合は先に親を照合する |
| 親保存済み、ファイル作成を拒否 | 親の実IDを残す。ファイルだけ再選択・再実行できる |
| ファイル作成済み、attachを拒否 | 「ファイル登録済み・添付未完了」とfileIdを記録。フォルダや対象レコードの権限を確認後、同じfileIdからattachを続行 |
| 作成の応答消失、または204でID不明 | 「登録結果を確認中」とする。既知の外部IDがあれば対象ファイルを照合する。名前だけで既存と断定しない。照合不能なら自動再作成を止める |
| attach／detachの応答消失 | 検証済みの一覧経路またはNetSuite UIで対象の組を照合する。目的の状態なら完了とし、未反映時のみ続行。孤立した可能性だけで本体を自動削除しない |
| 親だけの差し替えで旧添付の解除に失敗 | 新旧双方のfileIdを保持し、新添付を再作成せず旧添付の解除から再開する |

外部ID指定の `PUT document/v1/file/eid:{externalId}` は既存ファイルを置換する。連携先のファイルを上書きしてよい業務だけで採用する。同期POSTの結果が不明な時の無条件な再送手段には使わない。[File CabinetのPUT仕様][files]

## 6. 添付以外の追加事項

### 6.1 画面処理とバッチが共有する同時実行枠

接続診断に `GET /services/rest/system/v1/governanceLimits` を追加する案。応答の `accountConcurrencyLimit` はアカウント上限、`accountUnallocatedConcurrencyLimit` は個別割当なしの統合が共有する枠。個別割当の `integrationSpecific` では `integrationConcurrencyLimit` が返る。`accountLimit` や `internal` の場合に個別枠がないことをパースエラーにしない。[GovernanceLimits][limits]

この数値はリアルタイムの残り接続数ではない。CLBの画面、サーバーサイドスクリプト、別プロセスのバッチが同一アカウントを使う場合は、合計の並列数を調整する。診断取得ができない環境では設定済みの並列数で継続する。添付の送信結果が不明な場合は、読み取りと同じ再試行ルールに入れず§5に従う。

### 6.2 NS-24の証明書ローテーション

認証済み管理処理向けの契約。ベースURLは公式例で `https://{accountHost}.restlets.api.netsuite.com/services/rest/auth/oauth2/v1/clients/{clientId}/certificates`。通常の業務APIとホストが異なる。必要な権限は `Manage own OAuth 2.0 Client Credentials certificates`、トークンのスコープはREST Web Services。通常の業務ロールにこの管理操作を足すことは添付機能の導入条件ではない。[管理API概要][rotate]

| 操作 | 要求 | 確認する値 |
|---|---|---|
| 一覧 | ベースURLにGET | 証明書ID、有効期限、失効状態。対象のIntegration・Role・Entityを記録する。[一覧][certlist] |
| 新証明書登録 | 同URLにPOST | JSONの `fileContent` に公開証明書のPEM、`role` と `entity` に対象ID。秘密鍵はCLB側の保管先で扱う。[登録][certnew] |
| 旧証明書失効 | ベースURLの `/{certificateId}/revoke` にPOST | 新しい鍵とCertificate IDで新規にトークンを取得し、対象APIの読取が成功してから旧IDを失効させる。[失効][certrevoke] |

運用試験では、旧トークンのキャッシュを使わず新しい設定で認証できたことを確認する。切替の途中で失敗した場合に旧証明書を使えるよう、失効を最後の段階に置く。Sandbox refresh後は対象環境でM2M設定を再確認する。[環境別セットアップ][setup]

## 7. 既存Sandbox検証計画に追加するケース

検証用の商談または受注A・B、参照を制限したフォルダ、テスト専用PDFを用意する。対象リリース・レコード型・ロール・操作元（UI／CLB）を結果に併記する。以下はすべて未実施。

| ケース | 操作 | 合格条件 |
|---|---|---|
| F01 登録→添付→取得 | 日本語名のPDFをAに添付し、CLBから再取得 | 作成204とx-ns-link、添付204を確認。NetSuite UIの添付、ファイル名、取得バイト列のハッシュが一致 |
| F02 共有・解除・削除 | 同じfileIdをAとBに付け、Aから外す。その後Bから外しテスト用本体を削除 | Aからだけ消え、解除直後はBで取得できる。本体削除後は本体を取得できない。操作別のHTTPを記録 |
| F03 既存一覧・ページング | UIとCLBから別々にAへ添付。Bにも同名の別ファイルを添付。UIからAの1件を解除 | Aの一覧はUIと一致し、Bのファイルが混在しない。ページをまたぐ場合もID集合に欠落・重複がない。0件と取得不能を区別 |
| F04 権限 | 親は閲覧可だがフォルダを参照できないロール、CLBで親の閲覧不可の利用者で照合 | 名前・内容の表示はそれぞれの許可範囲内。既知のIDを指定しても権限外ファイルを取得できない |
| F05 サイズ・ファイル形式 | 10 MB前後のファイルを複数サイズで送受信し、通常PDFとURL型の既存ファイルも開く | 実測バイト数、応答コード、CLB表示を記録。取得できないファイルでも成功や空ファイルにならない |
| F06 障害からの再開 | 作成応答の消失、作成後の添付拒否、添付応答消失を個別に再現 | 親・ファイルの重複なし。確定済みIDと要照合の段階が画面／バッチ結果に残る |
| F07 環境別メタデータ | 商談、受注、および利用予定のカスタムレコードでメタデータ取り込みと操作を確認 | 型・ロール別に対応表ができる。未対応型で添付の成功や一覧0件を表示しない |
| O01 接続の同時実行枠 | 診断結果と設定値を比較。画面の読取と添付バッチを並行 | 個別枠の有無を処理でき、読み取りの再試行でファイルの書き込みが重複しない |
| O02 証明書切替 | 検証用Integrationに新証明書を登録し、全呼出元を切替後、旧証明書を失効 | 新鍵による新規トークン取得と読取を確認。失効後も画面とバッチが新鍵で動く |

## 8. 前回レビューから引き継ぐ項目

11月16日のレビュー対象はコミット `a9d3991a0a33a2e6b5173f13a01e184054f30923`。下表は同時点の指摘を実装担当が既存の修正と照合するためのもの。現行版で未修正との判定ではない。[当時のレビュー資料][review]

| 既出の論点 | 今回の実装で接続する箇所／確認内容 |
|---|---|
| 明細保存と他者が追加した行の消失 | 添付でも、古い画面の一覧と現在一覧の差から削除対象を推測しない。利用者が外したfileIdだけを解除する |
| 明細仮想表の所属・集計 | 添付一覧を汎用の取引明細表から推測しない。親の型・IDで絞り、UIの所属と照合する |
| 一括登録の部分成功・結果不明 | §5の段階別結果を既存のエラー通知経路へ反映し、本番設定の画面とバッチでも区別できるようにする |
| 外部従業員ログインのExample | 実際のログイン経路から、親と添付を同じCLBユーザー権限で参照する試験を追加する |
| 状態を条件にした更新・削除の競合 | 「承認前だけ解除可能」等を要求するModuleは、確認と変更の間に状態が変わるケースを試験し、保証できる範囲を明記する |
| Designerのメタデータ更新中断 | 文書サービスの定義を追加する際も、一時ファイルの検証後に置換する。取得失敗で最後に使えた定義を失わない |

**実装の順序。** 最初にF01とF03を行う。前者で標準RESTの往復、後者で既存ファイルを含む一覧の取得経路を決める。その結果でDesignerが提示する操作を確定し、F02・F04～F07を通して画面とバッチへ接続する。O01・O02は接続運用の独立した改善として扱える。

[files]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0909085903.html
[attach]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0113084334.html
[search]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N658802.html
[saved]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0804090827.html
[nsearch]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4345764122.html
[permissions]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N490137.html
[limits]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0625010817.html
[rotate]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_92130837826.html
[certlist]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_11111634165.html
[certnew]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0209112025.html
[certrevoke]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0209112818.html
[setup]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_162686838198.html
[review]: https://chatgpt.com/library?file_id=libfile_fde3_f-4628da357e2081918c8e280a638f5d66_5478cc2377808191a35dae91c62310ab&account_id=personal
