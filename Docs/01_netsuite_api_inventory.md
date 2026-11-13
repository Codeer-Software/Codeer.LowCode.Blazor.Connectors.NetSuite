# Codeer.LowCode.Blazor.Connectors.NetSuite — 利用API調査

- 文書ID: API-01
- 作成日・調査基準日: 2026-11-13
- 版: 0.1
- 対象: Claude Codeによる実装に先立つAPI選定・制約整理
- 対応文書: [仕様案 SPEC-01](02_netsuite_specification_draft.md)

> **目的は、CLBのModuleとNetSuiteに既にあるレコード・項目・参照・選択肢の設定を結び付けること。汎用HTTPクライアントや、APIごとの入出力Moduleを利用者に作らせる製品にはしない。**

## 1. 調査範囲と確度

Oracleの公開公式資料、およびCodeerのExtras／Starterの公開ソースを確認した。NetSuite実アカウントへの接続、対象ロールでのメタデータ取得、実データのCRUD、CLB本体内部の拡張点検証は行っていない。

本書の「確認」は公開資料上の確認を意味する。「採用案」はコネクターの仕様提案であり、既存機能ではない。APIが公式資料に存在しても、すべてのアカウント・ロール・リリース・レコードで使用できるという意味ではない。

GitHubは調査時に取得できた`main`を参照した。コミットSHAは固定していないため、実装開始時に実際の対象コミットとNuGetバージョンを記録する。ドキュメントとコードが食い違う場合は対象版の実コードで再確認する。

## 2. 採用方針

| 区分 | API・方式 | 用途・判断 |
|---|---|---|
| 必須 | OAuth 2.0 Client Credentials（証明書付きM2M） | CLBサーバーからNetSuiteへの接続。一般的なclient secretだけの方式とは異なる |
| 必須 | REST Record ServiceのMetadata Catalog | 接続先に実在するレコード・項目・型・参照・操作を取得 |
| 必須 | REST Record Service | 詳細取得、作成、更新、削除。編集時のデータ構造の基準 |
| 必須候補 | REST Query ServiceのSuiteQL | 一覧の複数列取得、条件検索、並び順。対応関係を確認できた対象で使用 |
| 必須候補 | `selectOptions` | NetSuiteの選択肢・入力コンテキストをCLBに反映 |
| 補助 | Record Collection Filtering | SuiteQL対応が未確認の対象の限定的な一覧取得、ID検索など |
| 拡張候補 | Saved Search、Dataset、Action、Transform | NetSuite側の既存設定・業務操作の再利用 |
| 後続候補 | 非同期処理、Document Service、RESTlet、証明書ローテーション | 初期版の必須にはしない |
| 不採用 | SOAP、TBAを新規標準にする方式、CData依存 | 本件の基本経路にしない |

RESTとOAuth 2.0を新規連携の基本とする。Oracleは2027.1から新規TBA連携を制限し、2028.2でSOAPを利用不可にする計画を案内している。日付はOracleのリリース番号であり、特定の暦日への読み替えはしない。[O33]

RESTのメタデータ、CRUD、SuiteQLは公式機能として案内されている。標準経路にNetSuite側の独自スクリプト配備を必須としない。[O36]

## 3. エンドポイント一覧

### 3.1 共通のベースURL

以下の`{REST}`は、管理者が登録するアカウント固有のSuiteTalk URLに`/services/rest`を付けたもの。

```text
https://{account-specific-host}.suitetalk.api.netsuite.com/services/rest
```

実際のサービスURLはNetSuiteのCompany InformationにあるCompany URLsで確認する。アカウントIDの文字列加工だけで本番・SandboxのURLを決め打ちしない。[O01]

下表の`{type}`、`{field}`、`{sublist}`は、原則として対象アカウントのメタデータ等で確認した識別子を使用する。ブラウザーから任意のパスを受け取る設計にはしない。

### 3.2 初期版の中心となるAPI

| API ID | HTTP | パス | 主な入力・出力 | CLBでの用途 | 根拠 |
|---|---|---|---|---|---|
| NS-01 | POST | `{REST}/auth/oauth2/v1/token` | form形式のgrantと署名済みJWT → access token | サーバー認証 | [O05] [O06] |
| NS-02 | GET | `{REST}/record/v1/metadata-catalog` | レコード種別とメタデータへのリンク | データソース内のレコード選択 | [O03] |
| NS-03 | GET | `{REST}/record/v1/metadata-catalog?select={types}` | OpenAPIまたはJSON Schema | 項目・型・操作の取得 | [O03] [O04] |
| NS-04 | GET | `{REST}/record/v1/metadata-catalog/{type}` | 指定レコードのメタデータ | 必要な定義の遅延取得 | [O03] |
| NS-05 | GET | `{REST}/record/v1/{type}?q=...&limit=...&offset=...` | 条件 → 主にIDと参照リンク | 限定的な一覧・ID解決 | [O11] [O34] |
| NS-06 | GET | `{REST}/record/v1/{type}/{id}` | レコードJSON。body項目は`fields`で選択可能 | 詳細表示、保存後の再読込 | [O14] |
| NS-07 | POST | `{REST}/record/v1/{type}` | 作成値 → 成功時204、Location | 新規保存 | [O13] |
| NS-08 | PATCH | `{REST}/record/v1/{type}/{id}` | 変更値。未指定項目は変更しない | 更新保存 | [O15] |
| NS-09 | DELETE | `{REST}/record/v1/{type}/{id}` | 対象ID | 明示的に許可された削除 | [O16] |
| NS-10 | PUT | `{REST}/record/v1/{type}/eid:{externalId}` | 外部IDと値 → 作成または更新 | 外部ID連携を設定した場合のUpsert | [O17] [O18] |
| NS-11 | GET | `{REST}/record/v1/{type}/{id}/{subresource}` | サブリスト／サブレコード等 | 明細・住所などの読込 | [O21] |
| NS-12 | PATCH | `{REST}/record/v1/{type}/{id}`（必要時`?replace={sublist}`） | 親と明細の更新内容 | 検証済み明細の保存 | [O20] |
| NS-13 | POST | `{REST}/query/v1/suiteql?limit=...&offset=...` | `q`と`params` → 一覧行 | 複数列の一覧・検索 | [O09] |
| NS-14 | POST／PATCH | 新規: `{REST}/record/v1/{type}?fields={fields}`／既存: 同パスの`/{id}`付き | 特別なAccept、必要なら現在値 → 選択肢 | Select／参照候補の表示 | [O19] |

**NS-14は通常の保存と同じURL・HTTPメソッドを使う。用途を区別するAcceptヘッダーの欠落を許してはならない。** 詳細は§6。

### 3.3 後続機能として検討するAPI

| API ID | HTTP | パス・方式 | 用途・制限 | 根拠 |
|---|---|---|---|---|
| NS-15 | GET | `{REST}/query/v1/savedsearch` | 現在のユーザー／ロールが使える保存検索の一覧 | [O23] |
| NS-16 | GET | `{REST}/query/v1/savedsearch/{savedSearchId}/result` | 既存Saved Searchの結果取得。条件・並び順の上書きは不可 | [O23] |
| NS-17 | GET | `{REST}/query/v1/dataset/{datasetId}/result` | 既存Datasetの結果取得。Datasetのメタデータは提供されない | [O24] |
| NS-18 | POST | `{REST}/record/v1/{type}/{id}/@{action}` | 承認等。Actionのメタデータは提供されない | [O25] |
| NS-19 | POST | `{REST}/record/v1/{sourceType}/{id}/!transform/{targetType}` | 受注から請求書等。対応する変換のみ | [O26] |
| NS-20 | 元のメソッド／GET | 対応する要求に`Prefer: respond-async`、返された`{REST}/async/v1/job/{id}`等を追跡 | 長時間処理。対象サービスごとの対応確認が必要 | [O27] [O34] |
| NS-21 | GET／POST／PATCH／DELETE等 | `{REST}/document/v1/file`、`folder`および個別リソース | ファイル添付連携候補。操作ごとの仕様確認が必要 | [O28] |
| NS-22 | GET | `{REST}/document/v1/metadata-catalog` | Document Serviceのメタデータ | [O28] |
| NS-23 | 配備したスクリプトによる | `https://{host}.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=...&deploy=...` | 標準APIで不足する個別拡張。標準利用の前提にはしない | [O37] [O38] |
| NS-24 | GET／POST | OAuth 2.0 Client Credentials Certificate Rotation Endpoint | 証明書の列挙・追加・失効。具体的URL／要求詳細は実装前の追加確認事項 | [O35] |

NS-24はAPIの存在と操作種別までの確認であり、実装可能な完全な契約は未確認。存在を推測したURLは記載しない。

## 4. 認証・接続設定

### 4.1 M2M認証の要求

トークン取得は`application/x-www-form-urlencoded`を用い、次の値を送る。[O05]

| 項目 | 値・意味 |
|---|---|
| `grant_type` | `client_credentials` |
| `client_assertion_type` | `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` |
| `client_assertion` | 登録証明書に対応する秘密鍵で署名したJWT |

JWTには`kid`（Certificate ID）、`iss`（Client ID）、`aud`（トークンURL）、`scope`、`iat`、`exp`を設定する。対応署名方式はPS256／PS384／PS512／ES256／ES384／ES512。JWTの`exp`と`iat`の差は60分未満という条件がある。[O06]

本コネクターの標準対象であるREST Web Services用のscopeを使用する。RESTletやSuiteAnalytics Connectを採用しないのに、そのscopeまで一律に追加しない。M2MのマッピングはNetSuite側でアプリケーション・Entity・Role・証明書を関連付ける。[O06] [O07]

取得したaccess tokenの有効時間は公式資料では3600秒。実装は応答の`expires_in`を扱い、期限前に再取得する。通常のclient secret方式やrefresh token更新を、このM2Mフローに取り違えない。[O05]

### 4.2 管理者が準備する情報

接続識別名、アカウント固有のサービスURL、Client ID、Certificate ID、秘密鍵の参照先、接続先環境、使用するロールの情報を管理する。秘密鍵やaccess tokenをModule定義・配布デザイン・ブラウザーへ含めないことを採用案とする。

NetSuite側ではREST Web Services等の機能、認証設定、利用レコードとAnalyticsに必要な権限を確認する。一般セットアップ資料の権限表と、採用するOAuth 2.0 M2Mの管理設定は別々に照合する。管理者ロールをアプリの実行ロールにすることを既定にしない。[O08] [O07]

Sandbox等のM2M設定は本番から自動コピーされず、Sandbox refreshで設定が消える点を導入手順に含める。[O07]

## 5. メタデータで取得するものと取得できないもの

### 5.1 Record側メタデータの利用

`Accept: application/swagger+json`でOpenAPI、`Accept: application/schema+json`でJSON Schemaを要求できる。操作・URL・パラメーターの情報も扱うため、コネクターではOpenAPIを主に利用する案とする。全件の巨大な定義を毎回取らず、一覧取得後に選択したレコードと参照先を取得する。[O02] [O03] [O04]

抽出対象は、レコード／項目の識別子、型、参照先、enum、サブリスト、サブレコード、検索可能性、公開されているCRUD操作など。カスタマイズと実行ユーザーに応じてスキーマが変わる。[O02]

必須・読み取り専用等は**メタデータに表現される範囲で**利用する。UI上のすべての入力制御、条件付き必須、業務計算、フォーム配置、ワークフロー、クライアントスクリプトを復元できるとは扱わない。メタデータだけで判断できない制約は、レコード別の確認と保存時のサーバー検証で扱う。

参照先が`select`に含まれない場合、その参照定義が汎用objectになる場合がある。型が分からないことを「どんな値でも保存可」と解釈しない。[O04]

### 5.2 RecordメタデータとSuiteQLのスキーマを混同しない

| 項目 | Record Service | Query Service／SuiteQL |
|---|---|---|
| 主な形 | レコードJSON、参照object、明細items | 表形式の列・行、JOIN等 |
| 主な用途 | 詳細・更新・項目構造 | 一覧・条件検索 |
| 参照元 | REST Metadata Catalog | AnalyticsのRecords Catalog、実行検証 |
| コネクターの扱い | 読み書きの基本契約 | 検証済みの読取対応関係を使用 |

RESTの項目名を小文字にしただけでSuiteQLの列名になる、といった変換は保証しない。接続先のRecord定義とAnalytics定義を突き合わせる必要がある、というのが本調査からの判断である。[O02] [O09] [O10]

Records CatalogはNetSuite内の参照ツールとして案内されている。本調査では、これを汎用的に丸ごと取得する公開RESTスキーマAPIは確認できなかった。`/query/v1/metadata-catalog`等を推測で実装しない。非公開UI APIやHTMLスクレイピングも標準経路にしない。[O10]

**採用案:** 検証済みレコード対応情報をコネクター側に持ち、カスタム項目をメタデータと照合して補う。自動確定できない項目は「SuiteQL一覧未対応」等と表示する。利用者にCRUD用とQuery用の二重の入出力Moduleを作らせない。対象によって追加対応が必要なことは隠さない。

### 5.3 カスタム設定の注意

カスタムレコードのインスタンスはRESTでCRUDでき、レコード種別はScript IDで指定する。一方、カスタムレコード**種別の新規作成**はこのREST CRUDとは別の操作で、公式資料はUIまたはSDFを案内している。[O22]

また、カスタムレコードの親子関係では、親のREST応答に子が出ない場合があり、子側の親参照で検索する。カスタムレコード、標準レコードの明細、任意のカスタムサブリストを同じものとして扱わない。一般制限ページではカスタムサブリストは利用不可と案内されている。[O22] [O34]

## 6. 参照項目・選択肢

enumで固定値が明らかなものはメタデータを利用し、選択可能値がロールや他の入力項目に依存するものは`selectOptions`を優先する。[O02] [O19]

```http
POST {REST}/record/v1/customer?fields=entitystatus
Accept: application/vnd.oracle.resource+json; type=select-options
Content-Type: application/json

{}
```

既存レコードでは対象ID付きのPATCHを使用する。必要に応じて、候補の決定に使う現在値をbodyで渡す。結果の`id`と`refName`を値と表示に分けて保持する。[O19]

**コネクター側の採用案:**

- 新規／既存を区別し、選択肢照会では絶対に保存用のAcceptへフォールバックしない。
- 依存する値が変わった場合、候補を再取得し、現在選択値の有効性を再確認する。
- 大量の候補は検索・ページングし、全候補の毎回ロードを避ける。
- 候補キャッシュは接続・認証主体・ロール・対象レコード・対象項目・依存値で分離する。
- 「参照先レコードを直接一覧できる権限」と「その項目の候補を選択できる権限」を同一視しない。

## 7. 一覧・検索・ページング

### 7.1 SuiteQL

NS-13には`Prefer: transient`が必要。**調査時点の公式資料にはREST経由のバインドパラメーター`params`が記載されている。** 「REST SuiteQLにはパラメーターがない」という古い前提を採用しない。[O09]

次は契約形状を示す例であり、実アカウントで実行済みのサンプルではない。

```http
POST {REST}/query/v1/suiteql?limit=50&offset=0
Prefer: transient
Content-Type: application/json

{
  "q": "SELECT id, entityid FROM customer WHERE id = ? ORDER BY id",
  "params": ["123"]
}
```

値はパラメーター化し、識別子やORDER BYはサーバーが許可した項目から組み立てる。識別子は値パラメーターで保護できないため、任意SQL・列名・ソート式をブラウザーから受け付けない。

SuiteQL RESTの結果上限は公式資料で100,000件。一般のページングは最大1ページ1,000件、`offset`は`limit`の倍数という制約がある。一般ページング資料には最大1,000ページの記述もあるため、小さいページサイズで無制限に先まで進める仕様にはしない。Connectの大件数説明を、REST SuiteQLの上限解除と解釈しない。[O09] [O12]

戻り列の大文字・小文字に依存しないこと、日付・数値等の結果表現を検証することも必要。[O32]

### 7.2 Record Collection Filtering

`q`では項目・演算子・値、AND／OR等を使えるが、対応項目はメタデータの`x-ns-filterable`等で確認する。通常のコレクション検索はIDとリンクを返すので、一覧の表示列をそろえるには個別GETが追加で必要となる。[O11] [O34]

**採用案:** SuiteQLで確実に取得できる対象を優先し、Record検索＋個別GETは制限付きの補助モードとする。N件のページに対し約1＋N回の要求になる可能性を把握し、ページサイズと同時実行数を抑える。対応していない並べ替えを、取得済みの1ページだけで並べ替えて実装したことにしない。

### 7.3 CLB側への適用条件

検索、権限制限、並び順、件数は同じ条件集合で扱う。特に行権限をページ取得後の表示除外だけで処理しない。件数・ページ境界・候補表示からの情報漏えいを避ける。

SuiteQLではID等の一意キーを末尾に含む順序を採用案とする。ただしページ間に外部更新が入るため、複数リクエスト全体が同一時点のスナップショットになるとは保証しない。

## 8. 作成・更新・削除と値の扱い

### 8.1 書込契約

POST成功時に本文がない場合があるため、JSON本文の有無だけで成功を判定しない。作成結果のIDはLocation等、確認できた応答契約から取り出す。[O13]

PATCHは未指定項目を変更しない。nullによる消去の扱いもあるため、「変更なし」「消去」「空文字」「false」「0」を分ける。読み取れた項目がすべて書き込めるとは限らない。[O15]

| ケース | 採用案 |
|---|---|
| 通常の新規作成 | POST。NetSuite発番のinternal IDを保持 |
| 通常の編集 | internal IDを使ったPATCH。許可された変更項目のみ送信 |
| 明示的な消去 | 対象項目のnull／消去契約を確認して送信 |
| 外部IDを使う連携 | 設定した用途だけでPUT Upsertを使用 |
| 削除 | DELETEが可能な対象で、設計設定と操作権限の両方が許可した場合のみ |
| 無効化 | 削除とは別機能。対象項目と業務上の意味が確定した場合のみ |

internal IDとexternal IDは混ぜない。参照先の表示名をキーにしない。外部IDには`eid:`というURL形式があり、記号を含む値に注意事項もある。値の検証とURLエンコードを行う。[O17] [O18]

### 8.2 日付・数値

日付のみの項目をタイムゾーン変換で前後日にずらさない。日時はUTC／ISO 8601とCLBの表示タイムゾーンを区別する。Recordの日時形式とRecord検索条件の形式が同じとは限らない。[O31]

金額・数量の桁数、null、真偽値、選択値、SuiteQL結果の文字列表現は実アカウントで往復検証する。JSONの数値を無条件に浮動小数点へ丸める仕様にはしない。

### 8.3 保存成功後の再取得

保存後はNetSuite側の初期値・採番・計算結果等を再取得してCLBへ反映する案とする。ただし「保存は成功したが、再取得だけ失敗した」を保存失敗として再POSTしない。状態を分けて返す。

## 9. 明細・サブレコード

サブリストにはkeyed／non-keyedがあり、更新方法が異なる。行削除を単純なDELETEとして扱えず、`replace`を使う全置換が必要なケースもある。[O20]

**仕様上の重要条件:**

1. 行番号や画面上の順番ではなく、NetSuite側で有効な行識別方法を保持する。
2. 全置換は、未表示行・未取得ページ・編集対象外の値を消す危険がある。内容全体を安全に扱えると確認した明細だけで許可する。
3. 応答JSONを丸ごと再送しない。読み取り専用項目・リンク情報を除き、確認済みの書込形式を使う。
4. サブレコードは親の文脈で扱う。独立したCRUDテーブルと見なさない。[O21]
5. カスタム親子レコードは独立レコードの場合があるため、1回の親PATCHで一緒に保存できるとは仮定しない。[O22]

初期版では閲覧と構造把握を先行し、編集は対象のレコード／明細ごとに実機確認して開放する案とする。

## 10. 拡張APIの位置付け

### 10.1 Saved Search／Dataset

NetSuiteで作成済みの検索を、CLBの読み取り専用一覧へ結び付ける候補。Saved Searchには列挙・実行APIがあるが、RESTから検索定義の作成・更新・削除、検索条件やソートの上書き、完全な定義の取得はできない。[O23]

Datasetも既存定義の実行が中心で、RESTで定義を作成せず、メタデータも提供されない。返却列の型やModuleへの対応は別途確認が必要で、空結果からの型推測はできない。[O24]

集計結果の1行を、更新可能なNetSuiteレコード1件と誤認しない。詳細画面への遷移は、レコード種別とIDを確実に対応付けられたときだけ許可する。

### 10.2 Action／Transform

Actionは`@action`、Transformは`!transform`のAPIで、単なる項目更新と分ける。Actionはメタデータがないため「すべてのボタンを自動列挙」できるとはしない。Transformは対応変換をメタデータ等で確認する。[O25] [O26]

実行許可、確認表示、結果ID、再実行防止を個別に定める。状態項目をPATCHすれば正式な承認処理等と同じになる、とは仮定しない。

### 10.3 非同期処理に関する資料の不整合

非同期処理の一般説明は`Prefer: respond-async`を案内する一方、一般制限ページはQuery Serviceの非同期を非対応と記載している。Saved Searchの個別ページには同期・非同期双方の記述がある。対象APIやリリース差を今回の公開資料だけで一意に解消できていない。[O27] [O34] [O23]

**採用案:** 初期版の一覧・保存は同期を基本とする。後続の非同期機能はサービスごとに検証し、Recordの対応をQuery全体へ一般化しない。非同期の冪等性機構を、通常の同期POSTでも自動的に使えるとは扱わない。

### 10.4 ファイル・個別拡張

Document Serviceにはfile／folderのAPIがあり、公式資料ではアップロード・ダウンロードは10 MB、同期実行とされる。通常レコードAPIの一般的な要求サイズ制限をファイルへ流用しない。[O28]

RESTletは標準機能で不足する場合の個別拡張とする。古いサンプルのNLAuth等を採用せず、認証は現行OAuth 2.0資料で確認する。RESTletを書くだけで複数レコードが原子的に更新できる、とも仮定しない。[O37] [O38]

## 11. エラー・制限・セキュリティ

### 11.1 エラー応答

HTTP statusに加えて`o:errorDetails`内の`o:errorCode`、`detail`、必要に応じて`o:errorPath`等を処理する。書込では`X-NetSuite-PropertyNameValidation: error`を使う案とし、未知の項目が無視されて「保存成功」になることを避ける。値検証も厳格側で扱う。[O29]

### 11.2 並列実行と再試行

同時実行数はアカウント単位で、Web ServicesとRESTletの合算を考慮する。制限超過時には429等を扱う。アカウントの他システムも同じ枠を使うため、固定の最大値をライブラリに埋め込まない。[O30] [O29]

| 状況 | 採用案 |
|---|---|
| 読取の一時障害・制限超過 | 上限付きバックオフ。操作として読取であることを判定 |
| 認証切れ | トークンの再取得を制御。無限再試行をしない |
| 権限不足・入力不正 | 原則再試行せず、原因を返す |
| 書込が実行前に拒否されたと確認できる制限超過 | 制御した再試行を検討 |
| 書込送信後の切断・タイムアウト・処理成否不明 | 「結果不明」。確認なしに同じ作成要求を再送しない |

SuiteQLやselectOptionsはPOST／PATCHでも読取用途である。反対に、HTTPメソッドがPUTだから業務副作用まで安全に再実行できるとは限らない。再試行判断は操作の意味で行う。

### 11.3 CLBとの境界

接続先・レコード種別・書込項目をブラウザーの指定だけで決定しない。サーバー上のModule定義と許可設定を基準とする。SQLや外部APIへ直接つなぐことで、CLBの行権限・項目権限を迂回しない。

NetSuiteのM2MロールはCLBの各利用者の権限とは別。複数利用者が同じ接続を使う場合、CLB側の認可を必ず追加する。

本調査では、複数の独立したREST要求やNetSuiteとローカルDBをまとめる分散トランザクション、および全レコード共通のETag／If-Matchによる更新保証は確認していない。あるものとして仕様化しない。競合防止は対象ごとの確認事項とする。

## 12. Extras／Starterから確認した前提

| 公開コードで確認したこと | 本コネクターへの示唆 | 根拠 |
|---|---|---|
| 共通・Designer・Serverの分離 | 同様の責務分離を検討できる | [G01] [G06] |
| Mailはデザイン設定と送信インフラ設定を分離 | Module定義には接続名を持たせ、秘密情報をサーバー側で解決する考え方を参考にする | [G02] [G05] |
| ExtrasApiは機能別ハンドラへ振り分ける | 追加機能のホスト統合の参考。ただしModuleのCRUDの受け口とは別 | [G03] |
| ExtrasApiContextは現在ユーザーのModuleDataIO、DesignData、UserId、監査等を保持 | 認証・認可・監査の文脈を失わない | [G04] |
| StarterのModuleDataControllerは一覧をModuleDataIOへ渡し、保存をSubmitWithTransactionAsyncへ渡す | 現行の権限・返却契約・保存意味を確認してから拡張する | [G07] |

**確認していないこと:** 現行CLB本体に、外部データソースを安全に差し込める公開インターフェースがあるか、DbAccess設定UIをどの拡張点で再利用できるか。存在を確認していないインターフェース名やメソッド名を仕様の前提にしない。

ControllerでNetSuiteへ早期分岐するだけの実装では、従来ModuleDataIOにあった認可等を飛ばすおそれがある。これはコード構造からの注意点であり、拡張位置の決定は次の設計工程で行う。

## 13. 実装前の確認項目

| 確認ID | 検証 | 合格条件・記録内容 |
|---|---|---|
| V01 | M2M認証 | 対象環境・最小権限ロールでtoken取得。秘密情報の非露出 |
| V02 | メタデータ取得 | 標準／カスタムレコードとカスタム項目を列挙 |
| V03 | メタデータの権限差 | ロールを変えた場合にキャッシュ混在なし |
| V04 | RESTとSuiteQLの対応 | 同一レコードのID・値・参照値を比較し、対応表を記録 |
| V05 | SuiteQLのparams | 文字列・数値・日付・null等の採用型を確認。非対応時に文字列連結へ逃げない |
| V06 | 一覧 | 条件・並び順・件数・ページ境界が一致 |
| V07 | 参照候補 | 新規／既存・依存値変更・ページングで候補が妥当 |
| V08 | selectOptions安全性 | 照会の前後でレコードが作成／更新されない |
| V09 | CRUD | 作成ID、変更なし／消去／0／false、保存後値を確認 |
| V10 | カスタム項目 | 追加・改名・削除・型変更の検出。既存画面を破壊しない |
| V11 | 明細 | キー、非キー、未表示行、削除、住所等を対象別に記録 |
| V12 | 権限 | 別利用者のID、未許可項目、偽の接続名等の要求を拒否 |
| V13 | 制限・障害 | 429、401、入力エラー、書込結果不明を区別 |
| V14 | 保存境界 | 複数独立レコード／DB混在要求を副作用前に検査 |
| V15 | 競合 | 対応レコードの更新競合を検証。未保証の範囲を明示 |
| V16 | CLB拡張点 | UI流用・データ処理差替え・認可・スクリプト経路を実コードで確認 |
| V17 | オプションAPI | Saved Search、Dataset、非同期、Fileは採用時のみ追加検証 |

検証結果にはNetSuite環境・リリース、ロール、カスタマイズ、CLB対象版、実行日を付記する。HTTPモックの合格と、NetSuite実機での合格は分けて記録する。

## 14. 参照資料

各リンクは2026-11-13の調査で参照した資料。Oracleの説明やGitHubのmainは変更されるため、実装時に再確認する。

- [G01] Extras / README.md
- [G02] Extras / docs/Mail.md
- [G03] Extras / Extras.Server/Api/ExtrasApi.cs
- [G04] Extras / Extras.Server/Api/ExtrasApiContext.cs
- [G05] Extras / Extras.Server/Mail/MailApi.cs
- [G06] Extras / Extras.Designer.csproj
- [G07] Starter / Cookie/LowCodeApp.Server/Controllers/ModuleDataController.cs
- [O01] REST Web Services URL Schema and Account-Specific URLs
- [O02] Working with Resource Metadata
- [O03] Getting Metadata
- [O04] Working with OpenAPI 3.0 Metadata
- [O05] POST Request to the Token Endpoint and the Access Token Response
- [O06] The Request Token Structure
- [O07] OAuth 2.0 Client Credentials Setup
- [O08] Prerequisites and Setup for REST Web Services
- [O09] Executing SuiteQL Queries Through REST Web Services
- [O10] Records Catalog Overview
- [O11] Record Collection Filtering
- [O12] Collection Paging
- [O13] Creating a Record Instance
- [O14] Getting a Record Instance
- [O15] Updating a Record Instance
- [O16] Deleting a Record Instance
- [O17] Using the Upsert Operation
- [O18] Using External IDs
- [O19] selectOptions Operation
- [O20] Working with Sublists
- [O21] Accessing Subresources in REST Web Services
- [O22] Using CRUD Operations on Custom Records, Custom Lists, and Custom Transactions v1
- [O23] Using Saved Search in REST Web Services
- [O24] Working with SuiteAnalytics Datasets in REST Web Services
- [O25] Executing Record Actions
- [O26] Transforming Records
- [O27] REST Web Services Request Processing
- [O28] Working with File Cabinet Records in REST Web Services
- [O29] Error Handling in REST Web Services
- [O30] Concurrency Governance and Session Management
- [O31] Using Datetime Fields
- [O32] SuiteQL Limitations and Exceptions
- [O33] SOAP Removal Plans FAQ
- [O34] Limitations of REST Web Services
- [O35] OAuth 2.0 Client Credentials Certificate Rotation Endpoint
- [O36] Overview of SuiteTalk REST Web Services
- [O37] Example Hello World RESTlet（URL形式のみ参照。旧認証例は採用しない）
- [O38] OAuth 2.0

[G01]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/README.md
[G02]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/docs/Mail.md
[G03]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/Source/Codeer.LowCode.Blazor.Extras.Server/Api/ExtrasApi.cs
[G04]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/Source/Codeer.LowCode.Blazor.Extras.Server/Api/ExtrasApiContext.cs
[G05]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/Source/Codeer.LowCode.Blazor.Extras.Server/Mail/MailApi.cs
[G06]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Extras/blob/main/Source/Codeer.LowCode.Blazor.Extras.Designer/Codeer.LowCode.Blazor.Extras.Designer.csproj
[G07]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Starter/blob/main/Source/Hosts/Cookie/LowCodeApp.Server/Controllers/ModuleDataController.cs
[O01]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1546938065.html
[O02]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_1540810168.html
[O03]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1540810174.html
[O04]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545126526.html
[O05]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_162755359851.html
[O06]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_162790605110.html
[O07]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_162686838198.html
[O08]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_5085602973.html
[O09]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157909186990.html
[O10]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_159367781370.html
[O11]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545222128.html
[O12]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156414087576.html
[O13]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545141395.html
[O14]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545141500.html
[O15]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545142173.html
[O16]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545142287.html
[O17]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156335203191.html
[O18]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156334828635.html
[O19]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0115100241.html
[O20]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_1545142407.html
[O21]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_159894563219.html
[O22]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_158100110171.html
[O23]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0804090827.html
[O24]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156577938018.html
[O25]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156631158782.html
[O26]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157901123882.html
[O27]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_161251386312.html
[O28]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0909085903.html
[O29]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156570709583.html
[O30]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_1559222360.html
[O31]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157305090645.html
[O32]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156257796125.html
[O33]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_2104046421.html
[O34]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_1544786455.html
[O35]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_92130837826.html
[O36]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_1540391670.html
[O37]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4628875570.html
[O38]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_157769826287.html
