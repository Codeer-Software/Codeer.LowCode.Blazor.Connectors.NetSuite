# Codeer.LowCode.Blazor.Connectors.NetSuite — 設計判断のための調査結果

- 文書ID: RES-02
- 調査日: 2026-11-14
- 回答対象: `04_research_request_design.md`（REQ-02）の全26問
- 回答順: **K → P → L → M → N**
- 位置付け: 公開一次資料を基にした調査結果と仕様上の判断材料。内部クラス設計や実装完了報告ではない。
- 関連資料: `06_sandbox_verification_plan.md`（未確定事項を確定するための実機検証手順）

## 0. 結論と証拠の扱い

### 0.1 最も重要な結論

**「CLB上で親と明細を別モジュールとして見せること」と、「NetSuiteへ親と明細を別レコードとして逐次保存すること」は分けるべき。前者は維持できるが、後者を全種類に共通適用する仕様は成立しない。**

独立したカスタム子レコードは通常のCRUDを使える。一方、取引明細・住所サブレコードは親に属する資源であり、取引明細の単独DELETEも用意されていない。仕訳では追加・更新の途中でも貸借一致が必要になる。[S01][S08][S12][S14]

この違いから、保存仕様は次のように分けることを提案する。

| 対象 | CLB上の表示 | 推奨する保存単位 | 判断の確度 |
|---|---|---|---|
| 独立した標準レコード・独立したカスタムレコード | 通常モジュール | 1レコード | RESTで当該操作が公開されていることが条件 |
| 親への参照を持つカスタム子レコード | 親子モジュール | 子1レコード | 公式資料で確認 |
| 受注・請求などの取引明細 | 明細モジュール／グリッドを維持可能 | 親＋同じ親に属する今回の変更を集約 | 仕様提案。ネイティブRESTの保存制約に基づく |
| 仕訳明細 | 明細モジュール／グリッドを維持可能 | 貸借が成立する変更集合 | 公式の均衡条件から必須 |
| 住所などのサブレコード | 専用フィールド又は親に従属する編集領域 | 親コンテキスト | 公式資料で確認 |
| SuiteQL／Dataset／保存検索の結果行 | 一覧・集計 | 原則読み取り。更新先との対応が確認できた場合だけ別途書き込み | 検索結果の行をそのまま独立CRUD対象にしない |

**画面全体を1トランザクションにする提案ではない。** REQ-02で決定済みの「先の書き込みは後の失敗で戻さない」は維持する。ただし「1件」を常に明細1行とせず、親に属する一組の変更を1回のREST要求にまとめる必要がある。

### 0.2 確認レベル

- **公式確認**: Oracle、各製品ベンダーの公開資料に明示されている。
- **判断／提案**: 公式仕様から導くコネクター側の推奨仕様。NetSuiteの保証事項そのものではない。
- **未確認／要実機**: 対象アカウントのメタデータ・機能・権限・カスタマイズ又は実際の応答が必要。

**本調査ではNetSuite実アカウントへのリクエストを実行していない。** 以下の例は「公式掲載例からの抜粋」又は「公式仕様を基に組み立てた検証用例」であり、実測ログではない。要求されたエラーの実物や対象アカウント固有の応答は、未取得であることを明記した。

前提文書として名前が挙がっている `03_research_request.md`（REQ-01）の独立した本文は今回取得できていない。そのため、REQ-01の未提示の回答様式やQ-I2/Q-I3を推測せず、REQ-02に掲載された26問と回答条件を対象にした。既存API-01/SPEC-01をこの文書で自動的に書き換えるものでもない。

### 0.3 バージョンとアカウント差

公開ヘルプには2026.2の追加機能を含む。保存検索のREST実行、SuiteQLのバインド変数などについて、古い「できない」という説明を採用しない。一方、契約アカウントへの適用時期・有効機能・ロール・カスタマイズは別途確認する。[S28][S29][S58]

以下のHTTP例ではベースURLを省略する。`/record/v1` と `/query/v1` は、実際には `https://<account>.suitetalk.api.netsuite.com/services/rest` に続く。認証は通常 `Authorization: Bearer ...`、JSON本文は `Content-Type: application/json`。レコード名・フィールド名・書き込み可能性は、その接続のメタデータを正とする。[S16]

---

## K. 明細・サブレコードの書き込み

### Q-K1. 明細なしで取引レコードをPOSTできるか

**回答: 「全対象を明細ゼロで作れる」とする仕様は不可。標準的な作成では必要な明細を親と同時に送る。レコード別の厳密なゼロ件POSTの成否とエラー本文は未実測。**

| 種別 | 確認できた事実 | 初期対応で採るべき条件 | 未確定事項 |
|---|---|---|---|
| `salesOrder` | 販売取引には少なくとも1明細が必要という公式説明があり、受注のREST作成例もitemを含む | 通常の商品受注は有効なitem明細を1行以上含める | カスタマイズ等がある実アカウントのゼロ件POST応答 |
| `invoice` | RESTには商品明細付きのほか、請求可能な商品・時間を適用する作成例もある | 有効な請求明細を同時に送る。**`item`が空かどうかだけでは判定しない** | `itemcost`／`time`等の適用で成立する最小条件。全明細なしの実応答 |
| `purchaseOrder` | RESTの公式作成例はitemを含む。ただし同じページの制限説明と矛盾がある | 商品明細付きの作成を検証対象とし、空の親作成を前提にしない | item/expenseの公開範囲、経費のみの作成、ゼロ件作成の成否 |
| `estimate` | 販売取引の1明細以上の規則と、明細付きのREST例がある | 通常見積は有効なitem明細を1行以上含める | RESTゼロ件POSTの実応答 |
| `journalentry` | 借方・貸方が均衡する明細が必要。公式説明では最低2行、各側1行以上 | 有効なaccountを持つ借方・貸方の明細をまとめる | 通貨・子会社・会計帳簿等の追加必須条件と実エラー |

根拠: 販売取引規則[S10]、Invoice[S05]、Purchase Order[S06]、Estimate[S11]、Journal Entry[S07][S08][S09]。

**注意: Purchase Orderの公開ヘルプには、itemsをRESTからアクセス不可とする記述と、itemを含むPOST例が同居する。** 「RESTで発注明細は使えない」とも「すべての明細種別が使える」とも、このページだけでは断定しない。対象テナントのOpenAPI/JSON Schemaと実機結果で確定する。[S06][S16]

InvoiceはAccounts Receivable、Purchase OrderはPurchase Orders等の有効化が必要。税の扱いも別条件であり、Invoice/POのREST説明ではLegacy TaxではなくSuiteTaxが必要とされる。UIで作れる条件をそのままRESTへ転用しない。[S05][S06]

次は**作成形式を示す検証用例**であり、すべてのアカウントでの最小成功ペイロードではない。

```http
POST /record/v1/salesOrder
Content-Type: application/json

{
  "entity": { "id": "<customer-id>" },
  "item": {
    "items": [
      { "item": { "id": "<item-id>" }, "quantity": 1 }
    ]
  }
}
```

```http
POST /record/v1/journalentry
Content-Type: application/json

{
  "subsidiary": { "id": "<subsidiary-id>" },
  "line": {
    "items": [
      { "account": { "id": "<debit-account-id>" }, "debit": 100 },
      { "account": { "id": "<credit-account-id>" }, "credit": 100 }
    ]
  }
}
```

各種別の明細ゼロ時のHTTPステータス、`o:errorCode`、`detail`は**未取得**。よく見かける英文エラーを、今回のREST応答として捏造しない。検証T-K01で、本文なし・空配列・有効な最小明細を区別して採取する。

### Q-K2. PATCHで明細を1行追加できるか。既存行への影響は

**回答: 対応サブリストではできる。ただし、keyed/non-keyedの既定動作と、取引全体の業務検証を区別する。**

| サブリスト | `replace`なしのPATCH | 既存行の扱い |
|---|---|---|
| keyed | キー一致行を更新。一致しない行／追加用の行を追加 | 指定しなかった既存行は維持 |
| non-keyed | 送った行を追加 | 指定しなかった既存行は維持。ただし行の識別更新には使えない |

これは**REST**の規則。SOAPの`replaceAll`の説明をそのまま当てはめない。[S02][S03]

受注では、追加時に`line`を省略することが公式のユースケースに明記されている。以下はID・数量を置き換えた例。[S04]

```http
PATCH /record/v1/salesOrder/<parent-id>
Content-Type: application/json

{
  "item": {
    "items": [
      { "item": { "id": "<item-id>" }, "quantity": 3 }
    ]
  }
}
```

構造上の既存行は残るが、**「既存行を指定しない＝金額・税・引当・スクリプトによる変更が一切起こらない」ではない**。これは親取引の保存である。仕訳なら、借方1行だけの追加で均衡が壊れれば保存できない。[S08][S15]

通常の更新例には204 No Contentが示され、追加行キーが応答本文で必ず返る契約は確認できない。保存後のGETを前提とする。[S02]

### Q-K3. 既存行1行の更新・削除と行指定キー

**回答: keyed明細の1行更新はできる。1行だけの直接DELETEはできない。削除は親のサブリスト置換に変換する。**

受注・請求・仕訳の公式更新例は、明細要素の`line`で既存行を指定している。[S04][S05][S08]

```http
PATCH /record/v1/salesOrder/<parent-id>
Content-Type: application/json

{
  "item": {
    "items": [
      { "line": 2, "quantity": 5 }
    ]
  }
}
```

削除は、**残す行すべて**を指定した置換。次は行1・2・3が存在し、行2を除く説明例。

```http
PATCH /record/v1/salesOrder/<parent-id>?replace=item
Content-Type: application/json

{
  "item": {
    "items": [
      { "line": 1 },
      { "line": 3 }
    ]
  }
}
```

keyed置換では一致する行を残し、要求にない行を除く。上のキーのみの要素で成立するか、追加の必須値が必要かは対象レコードのメタデータと実機で確定する。**置換時に新規行を送る場合は、その新規行の必須データが必要**。[S01][S03]

non-keyedでは「既存行のキー」がないため、1行の変更・削除は全体スナップショットを編集して置換する必要がある。[S01]

**キーに関する結論:** 全サブリストを`line`固定にしない。取引・サブリスト・操作によってキーが異なり、複合キーを持つ資源もある。配列添字を永続キーにせず、メタデータ・返されたself URL・実際のキー項目を保持する。[S16][S54]

**置換の安全条件（提案）:** 全ページを読み切る／読み取り専用・計算項目をそのまま書き戻さない／未取得行を削除しない／他利用者の同時変更を検出する。今回確認した資料だけでは、全対象に共通するETag/If-Match等の原子的な競合防止契約は確定できない。単なる事前GETとの比較を完全な競合防止と呼ばない。

### Q-K4. N回の1行PATCHと1回の一括PATCHは同じか【判定】

**回答: 同じとは限らない。汎用保存の既定は親単位にまとめるべき。**

REST保存を契機に、REST Web ServicesコンテキストのUser Event／ワークフローが実行され得る。N回保存すれば、途中状態がそれぞれ業務検証・処理の対象になり得る。特定のスクリプトが正確にN回動くかはデプロイ条件・イベント種類・処理内容次第で、実測が必要。[S15]

| 論点 | 逐次保存と一括保存の差 |
|---|---|
| 貸借などの整合性 | 仕訳は各要求の終了時点で均衡が必要。完成形が正しくても途中形が不正なら失敗する |
| 合計・税 | 保存ごとの計算が介在する。現行RESTでは課税行のgrossAmtによる再計算も説明されている |
| 引当・価格・ワークフロー | 中間状態、実行時点、他の更新の介入により結果が変わり得る。全設定で同じとは保証できない |
| 実行負荷 | N回のネットワーク往復とN回の保存処理。1回の大きな要求との性能差は計測が必要 |
| 途中失敗 | 既に成功した要求だけ残る。既定方針で許容しても、利用者に成功分を明示する必要がある |
| 同時実行 | 直列N回は同時実行数Nを消費するわけではない。並列化すればアカウントの同時実行枠と親レコード競合に影響する |

根拠: 仕訳[S08]、税再計算[S05][S06]、実行コンテキスト[S15]、サービス階層の同時実行枠[S59]。業務結果の非同一性は、これらの仕様からの判断である。

**【判定】同一画面保存に含まれる同じ親への変更は、原則として親単位に1回のPOST/PATCHへ集約する。** すべてのサブリストを必ず全置換するという意味ではない。keyedでは差分更新を使い、削除など必要な場合だけ置換する。

既存受注への単発の1行追記などは、個別機能として許容できる。だが「全取引でN行を順に保存してよい」という共通仕様にはしない。ダミーの仮明細を作って後で消す方法も、会計・引当・通知を誘発し得るため、標準回避策には採用しない。

### Q-K5. REST明細キーとSuiteQL transactionlineの列の対応／追加後のキー取得

**回答: 候補列を読むことはできるが、汎用的なキー同値関係は今回の公開資料では確定できない。追加直後のキーは再取得を基本とする。**

| REST側 | SuiteQL側の候補 | 判定 |
|---|---|---|
| 親レコードID | `transactionline.transaction` | 親取引への関連として利用する構想。対象テナントのRecords Catalogで確認 |
| `item.items[].line` | `transactionline.id` | 同値をコネクターの共通保証にできる公式対応表を確認できず |
| `lineUniqueKey`相当 | `transactionline.uniquekey` | 名前の類似だけではREST更新キーと同一と扱えない |
| 配列内の位置 | `linesequencenumber`等 | 保存用キーに転用しない。順番と識別子は別 |

対象のAnalyticsスキーマはRecords Catalogで確認する。以下は**比較検証用SQL**で、列の存在・権限は実機で確認する。[S28][S56]

```sql
SELECT
  tl.transaction,
  tl.id,
  tl.uniquekey,
  tl.linesequencenumber,
  tl.item
FROM transactionline tl
WHERE tl.transaction = ?
ORDER BY tl.linesequencenumber
```

Inbound Shipmentでは仕入注文行の`lineuniquekey`を使う別用途が明記されている。これは「すべてのREST取引明細の更新に同じキーを使える」という根拠にはならない。[S60]

**保存後の確実な紐付け方法についての提案:**

1. 親作成は応答ヘッダーのLocation等で親IDを取得する。更新成功時に行キーが返るとは仮定しない。[S17][S02]
2. 親の明細を全ページ再取得し、実際のRESTキーとself URLを取得する。
3. 複数新規行とCLBの仮IDを対応付ける必要がある場合、利用可能なら独自の取引行カスタム項目に相関用UUIDを設定して再取得で照合する。例の`custcol_clb_line_token`は**本提案で作る項目名**で、NetSuite標準項目ではない。[S53]
4. 独自項目が使えず、重複行・並べ替え・スクリプトによる追加・同時更新がある場合、品目＋数量や前後差分だけで常に一意に特定できる保証はない。その条件では自動対応付けを未対応とするか、画面を再読込して確定IDへ差し替える仕様を決める。

コネクター内の行識別は、少なくとも`レコード種別＋親ID＋サブリスト名＋RESTキー`を区別する。SuiteQL行IDを無検証で直接保存に渡さない。判定に必要な実証はT-K05。

### Q-K6. 明細の独立GET／PATCH／DELETE APIはあるか

**回答: 親を含むURLでのGETはある。独立レコードとしての完全なCRUDはない。単独DELETEは不可。**

公式に掲載されたURL形は次のとおり。[S12]

```http
GET /record/v1/salesOrder/1/item
GET /record/v1/salesOrder/1/item/1
```

ただし、これらは親を指定するサブリソースであり、`/transactionline/<id>`のような汎用独立レコードCRUDを示していない。

直接PATCHについては、一部資源で実例がある。たとえばInventory Itemの価格には複合キーを含むサブリストURLへのPATCH例がある。したがって「全サブリソースで直接PATCH禁止」とも断言しない。[S54]

**今回の対象5取引の`.../item/{line}`又は`.../line/{line}`への直接PATCHがどこまで共通対応するかは、公開の一般説明だけでは確定できない。** 安全な共通経路としては、公式に説明された親へのPATCH＋明細キー指定を使用する。直接URLはテナントOpenAPIでメソッドが公開されている場合に限り採用候補とする。[S04][S08][S16]

要求されたGET応答の実物、および直接PATCH/DELETEの実際のHTTPステータスは未取得。未対応時のステータスが必ず405になるなどの断定はしない。

### Q-K7. customerのaddressBookと住所サブレコード

**回答: 親コンテキストで作成・取得・編集できるが、住所を独立レコードとして通常CRUDする方式にはできない。**

`addressBook`は住所録の行、`addressBookAddress`はその行が持つ住所サブレコードで、同じものではない。公式資料には、顧客作成時に住所録と住所を埋め込む例、住所を後から親へのPATCHで追加する類例、親から住所まで辿るGET例がある。[S12][S13][S61]

| K1〜K6に対応する論点 | 回答 |
|---|---|
| 住所なしの親作成 | 標準的な顧客では住所を別途追加する構成を採り得る。ただし必須住所を課すカスタムフォーム・スクリプト等は別。無住所POSTの実測は未実施 |
| 住所1件の追加 | 親PATCHでaddressBookのitemsへ追加する方式 |
| 住所1件の更新 | 既存住所録行のキーを指定して親から更新。キー項目名は対象メタデータで確認 |
| 住所1件の削除 | 住所録行の除去と住所サブレコードのnull化を区別。直接DELETEは使えない。置換又は型別に許されたnull化を検証 |
| N回更新 | 親を複数回保存する。デフォルト請求先／配送先、住所正規化、スクリプト等の影響を検証 |
| 行ID・URL | 公式例に`customer/107/addressBook/39/addressBookAddress`がある。ただし39は「全住所に共通する独立CRUD ID」を意味しない |

次は**親に住所を追加する構造の検証用例**。プロパティの正確な大文字小文字、住所国コードの形式、キー項目、必須項目は実際のスキーマに合わせる。

```http
PATCH /record/v1/customer/<customer-id>
Content-Type: application/json

{
  "addressBook": {
    "items": [
      {
        "label": "CLB-test-address",
        "addressBookAddress": {
          "country": "US",
          "state": "CA",
          "zip": "94105",
          "addressee": "CLB test",
          "addr1": "Test street"
        }
      }
    ]
  }
}
```

**住所録行のキー名を`id`・`internalId`・`line`のいずれかに固定する根拠は今回未取得。** スキーマとGETの実物で確定する。住所のSuiteQL側テーブル・列との対応も取引行と同じく別検証が必要（T-K07）。

### Q-K8. 親への参照を持つカスタム子レコード

**回答: できる。親画面にサブリスト表示する設定でも、独立したカスタムレコードであることは変わらない。**

Oracleは、RESTではカスタム親子関係の子を親レコードに表示せず、子コレクションを親条件で絞り込む方法を案内している。また、カスタムレコードのインスタンスには通常と同じCRUDを利用できる。[S14]

以下は**例示用に作る型・項目名**であり、実在のテナント設定ではない。

```http
POST /record/v1/customrecord_clb_child
Content-Type: application/json

{
  "name": "Child A",
  "custrecord_clb_parent": { "id": "<parent-id>" }
}
```

```http
PATCH /record/v1/customrecord_clb_child/<child-id>
Content-Type: application/json

{"name":"Child A updated"}
```

```http
DELETE /record/v1/customrecord_clb_child/<child-id>
```

Custom Records機能、事前に定義された型、必要なロール権限が条件。型定義そのものの作成は、このインスタンスCRUDとは別で、UI/SDFが必要。親削除時の連動削除や独自の必須制約まで一般化しない。[S14]

したがって、REQ-02の「親を作りIDを受け取り、そのIDを入れた子を1行ずつ作る」は、この種類についてそのまま使える。

### Q-K9. 方式の分類【判定】

**回答: (a)は独立した子レコード。(b)は限定条件付き。(c)には独立CRUDではない明細・サブレコードが入る。『初回だけまとめれば全面解決』ではない。**

| 分類 | 対象 | 判定 |
|---|---|---|
| **(a) そのまま成立** | 親参照を持つ独立したカスタム子レコード | 通常CRUD・独立IDがある。必須・権限・スクリプト条件は別 |
| **(b) 初回の親＋明細集約に加え、親PATCHへの変換を認めれば限定成立** | salesOrder／estimate／invoice、検証済みのpurchaseOrderのkeyed明細 | 作成時に必要明細を含める。その後の単発追加・更新は親PATCHに変換可能。削除は全残存行の置換。各保存後に業務上成立することが条件 |
| **(c) 1行ごとに必ず確定させる方式は不成立** | journalentryの一般的な金額変更・追加・削除 | 貸借整合が複数行に跨る。初回だけ集約しても解決しない |
| **(c) 独立子レコードCRUDとしては不成立** | non-keyedサブリスト、住所・在庫詳細等のサブレコード | 親コンテキスト、置換、子階層や固有キーの対応が必要 |

**厳密に『変更は初回の親POSTへの明細同梱だけで、それ以外は既存の独立子CRUDを一切変えない』という意味なら、(b)は成立しない。** 親PATCHへの書き換え、行キー解決、削除時の置換が別途必要だからである。

**【判定】共通化するのは画面・検索・項目対応の操作感。保存は「独立レコード」と「親に属する一組の変更」の2経路に分ける。** CLBが画面保存の束を受け取っているなら、その束から親ごとの変更を集められる差し込み点が必要。取得できない場合、取引明細の同時編集保存は専用機能にするか初期対応から外す。成功したことにしてIDを返さず保留するような、既存の即時保存契約に反する実装は避ける。

---

## P. ほかの製品の見せ方

### Q-P1. 明細を別テーブルに見せる製品と、書き込みへの変換

**回答: CDataで明確な先例がある。一方、多くの連携製品は親レコード＋明細配列／リストの入力を採る。読み取り用の表が存在することは、独立した明細CRUDがあることを意味しない。**

| 製品・資料の対象 | 明細の見せ方 | 書き込みで確認できた範囲 |
|---|---|---|
| CData ADO.NET Provider for NetSuite、SuiteTalkテーブル説明、Build 25.0.9539 | `IncludeChildTables=true`で`ParentTableName_ChildListName`を子表として公開 | 子表DMLを子リスト／親への変更として解釈。親＋複数子の新規作成は`#TEMP`にためて親INSERTのAggregateへ渡す |
| SuiteAnalytics Connect | 分析用の表・関連を参照 | **読み取り専用**。INSERT/UPDATE/DELETEの変換例にはならない |
| Celigo | NetSuiteインポートでItems等のサブリストをマッピング | 行照合キー、Line ID、Replace All Lines等を指定。独立したtransactionline汎用CRUDという説明ではない |
| Workato NetSuite SOAP | 親オブジェクトの入れ子リスト | Update objectのReplace allで置換／追記を選ぶ。これはSOAPコネクターの仕様 |
| Workato NetSuite REST | レコード種別を選ぶCreate/Update等 | 親レコード操作として公開。確認したページからは独立した明細DMLの保証は読み取れない |
| Boomi NetSuite | レコード種別ごとのOperation＋XMLプロファイル | 親のitemList／expenseList等を入力。Initializeで元伝票から明細を得てCreateへ渡す例がある |
| MuleSoft NetSuite／Composer | Record Type＋Item List | Composer公式例はSales OrderのRecord配下にItem Listをマッピングする |

根拠: [S41]〜[S48]、[S55][S62][S63]。

CData公式説明からの短い直接引用:

> “Delete commands will instead be interpreted as a command to delete the specific row from the child list.” [S41]

すなわち、子表のDELETEは親そのものの削除ではなく、子リストの行削除として扱われる。ただし公開ページから分かるのは**製品が提供する意味付け**までで、すべての内部通信を「このREST PATCHへ変換している」と特定できるわけではない。ここで調べたCDataの説明はXML Aggregateを使うSuiteTalk系モデルであり、CLBのREST実装へワイヤー形式をそのままコピーしない。

親＋子作成の先例で特に重要な点:

- CDataは複数行を`#TEMP`へ蓄え、親INSERT時に渡す方式を説明する。
- 親IDを指定せず子表へ1行ずつINSERTする別形式では、**親1件に2明細ではなく、1明細を持つ親が2件**になる例を掲載している。
- 既存の親へ追記する場合は、親のInternalIdを指定する。[S41]

CDataのDeep Insert説明には一般例としてJSONも出るが、NetSuite向けの箇所ではXML Aggregateという制限がある。一般的なInvoices/InvoiceLinesの例を、NetSuite実テーブル名やJSON対応の証拠としない。[S42]

**Power Apps／Dataverse、OutSystems、Mendixについて:** 今回確認できた公開一次資料では、取引明細の子テーブルDMLを何のNetSuite要求へ変換するかまで裏付けられなかった。非対応と断定するものではない。使用するコネクター／仮想テーブルプロバイダー／個別実装を特定しない限り、今回の設計根拠には採用しない。

### Q-P2. 取引種別ごとの表か、transaction共通表か

**回答: 目的によって両方ある。書き込み用は種別別、分析用は共通の取引モデルという分離が見られる。**

CDataの書き込み可能なTransaction TablesにはSalesOrder、Invoice、Estimate、PurchaseOrder等が別々に示される。Workato／Boomi／MuleSoftも操作対象のレコード種別を選ぶ。SuiteQLの公式例には`transaction`への共通問い合わせがある。[S41][S46][S47][S62][S28]

**CLBへの示唆:** 「1モジュール＝1レコード種別」を維持しつつ、一覧がSuiteQLの`transaction`を使う場合は種別条件をコネクター側で付ける。集計・結合結果の1行を、そのまま任意の取引レコードにPATCHしてはならない。行には確実な書き込み先レコード種別・親ID・必要なら明細キーの対応が必要。

### Q-P3. 固定・動的選択肢の見せ方

**回答: 型メタデータ、参照データ、専用の選択値取得機能を使い分ける先例がある。すべてを静的enumへ統一する標準は確認できない。**

BoomiはCustomListに対してget-customList、RecordRef/ListにはgetSelectValueを利用し、文字名称からInternal IDへ解決する説明がある。またExecuteにGet Select Valueを公開している。[S46]

MuleSoftのNetSuite ConnectorにもGet Select Valueが独立した操作として掲載される。[S47]

これらはSOAP系の先例だが、**候補値の解決が通常の列型メタデータと別機能になる**ことの根拠になる。REST側では真のenumはメタデータ、文脈・ロール依存候補はselectOptionsという分離が妥当。[S16][S18]

CDataの子表公開機能だけから、UIの動的候補依存関係まで自動的に分かるとは判断できない。Designerへの固定候補取り込みは、enumか、明示的にスナップショット化する参照データに限定する。

### Q-P4. 利用者権限・記録・接続主体の分担

**回答: NetSuiteは接続主体とロールの権限を執行する。連携製品側の利用者が自動でNetSuite利用者に置き換わるわけではない。共通接続と利用者別接続の両方の先例がある。**

Workato RESTの現在の接続資料は、バックエンド用のM2Mと利用者単位のAuthorization Code Grantを明確に分けている。接続の作成権限はWorkato、対象データへの権限はNetSuiteのEntity/Roleと付与権限に依存する。[S45]

この資料が保証するのは認証方式の選択であり、WorkatoのログがCLB利用者の全監査要件を代替できるという意味ではない。他製品についても、サービス接続を共有した場合に各アプリ利用者がSystem NotesのSet byへ自動反映される証拠は確認していない。

**CLBへの提案:** 共通M2MならCLB側で利用者認可と監査を持つ。利用者ごとにNetSuiteへ本人としてアクセスさせるなら、対応ライセンス・ロール・同意・トークン管理を伴うAuthorization Code方式を別オプションとする（M節）。

### Q-P5. 共通する見せ方は何か【判定】

**回答: 単一の「全行が独立CRUDできる表」という事実上の標準は確認できない。確認した製品群では、親レコードと明細を一組として書き込む考え方が共通する。**

**【判定】採るべき先例は「読むときは表／行として使いやすく見せ、書くときはNetSuiteの親レコード・明細リストの制約を守る」。** CDataのように子表へSQLを書けても、その下で変換・集約が必要になる。これをCLBへ持ち込む場合、UI上の独立モジュールとサーバー保存単位を分ける仕様が最も整合する。[S41][S43][S44][S46][S55]

これは確認対象の共通傾向からの判断であり、市場全体の規格やすべての製品の実装を確定したものではない。

---

## L. NetSuite側に残る履歴

### Q-L1. M2Mで更新したSystem NotesのSet by／Context

**回答: NetSuiteが認証したEntity/RoleとREST経路が記録の基準になる。共通M2MからCLBの各利用者を識別することはできない。対象テナントでの表示の実物は未取得。**

M2MのマッピングはApplication・Entity・Role・証明書で構成される。System Notesには変更者・ロール・変更内容・実行経路が記録され、REST Web ServicesのコンテキストはSystem Notesでも確認できる。[S35][S30][S15]

以下は**期待する記録の意味を示す表であり、実測値ではない**。

| 項目 | 共通M2Mで期待される意味 | 留意点 |
|---|---|---|
| Set by／変更者 | M2Mでマッピングした連携用Entity | CLBにログインした人の名前ではない |
| Role | トークンに対応するNetSuiteロール | UIの表示項目や参照方法による |
| Context | REST Web Services経由の変更 | 表示名とAnalytics側のコード／列値は同一とは限らない |
| Field／Old Value／New Value | 記録対象となる項目変更 | すべてのHTTP本文や全行差分を保存するログではない |

ただし、派生処理・自動処理の記録まで、すべて必ず同じSet byになるとは断定しない。Contextは連鎖した処理で開始元の経路を共有することがある。[S64]

また、`Log System Notes on Updates Only`が有効なら新規作成時の記録は更新時と同じではない。明細の履歴とヘッダーの履歴も区別が必要。削除はL3のとおり、通常System Notesが保持されないケースがある。[S30][S31]

**実物の例:** 未取得。T-L01で、M2M作成・更新・削除、スクリプトによる派生更新をそれぞれ行い、UIとSuiteQLの双方を採取する。文字列`REST Web Services`、SuiteScript定数`REST_WEBSERVICES`、Analytics列の内部値を混同しない。

### Q-L2. CLB利用者の名前／IDをNetSuite履歴へ残す公式方法

**回答: M2M要求の任意ヘッダーでSystem NotesのSet byをCLB利用者へ差し替える公式手段は確認できない。通常のデータ項目へ外部利用者情報を保存することはできるが、認証主体とは別。**

System Notesの変更者はNetSuiteが認識する利用者であり、共通M2Mの下流にいるCLB利用者は自動的には分からない。本人のNetSuite認証主体として扱う経路は、M1の利用者別認証である。[S30][S35][S38]

**提案する監査補助情報:** `外部利用者ID`、`外部利用者表示名`、`CLB要求ID`、`操作日時`を、利用可能なカスタム項目又は独立した監査用カスタムレコードへ記録する。取引の例示名は`custbody_clb_actor_id`、`custbody_clb_request_id`。これらは製品標準ではなく導入時に定義する項目。[S14][S53]

注意点:

- カスタム項目の値を更新しても、System NotesのSet byそのものが変わるわけではない。
- 「最後の更新者」項目は履歴全体ではない。同じ利用者が連続操作すれば、その項目の値が変わらず、毎回の操作識別には使えない。
- 通常レコードの削除前に値を入れても、削除後にその値や通常System Notesが残る保証はない。
- データ更新と監査レコードの別要求は、同一トランザクションとは限らない。片方だけ成功する窓がある。
- 利用者IDはブラウザーの自己申告を信用せず、CLBサーバーで認証済み利用者から確定する。アクセストークン・秘密鍵・不要な個人情報は監査ログへ保存しない。

**結論:** 共通M2Mを維持するなら、CLB側に利用者・操作・対象・結果・相関IDの監査記録を残し、NetSuiteの履歴と突合できる仕様にする。NetSuite側だけで各CLB利用者の責任主体を完全に証明できる、とは約束しない。

### Q-L3. System NotesをSuiteQLで読む方法と削除後の履歴

**回答: できる。`SystemNote`をAnalytics経由で問い合わせる。ただし権限・対象システム・削除時の扱いが違う。**

OracleはAnalyticsデータソースからSystem Notes／System Notes v2を検索できると説明する。全変更を参照するには、Administrator、Core Administrator Permissions、又は`System Notes for Analytics and REST`が必要。これがない場合、自分が変更した分だけになる。[S32]

Google Security Operationsの公式NetSuite連携実装にも、REST SuiteQLで`SystemNote`を読むクエリがあり、列名が具体的に確認できる。[S49]

次はその列構成を基に、バインド変数を使って**本調査で組み立てた例**。`RecordID`だけでは他レコード種別と混同し得るため、実装では`RecordTypeID`も指定する。

```http
POST /query/v1/suiteql?limit=100&offset=0
Content-Type: application/json
Prefer: transient

{
  "q": "SELECT sn.Date, sn.RecordTypeID, sn.RecordID, sn.Name, BUILTIN.DF(sn.Name) AS ActorName, sn.Role, BUILTIN.DF(sn.Role) AS RoleName, sn.Context, BUILTIN.DF(sn.Context) AS ContextName, sn.Field, sn.OldValue, sn.NewValue FROM SystemNote sn WHERE sn.RecordTypeID = ? AND sn.RecordID = ? ORDER BY sn.Date",
  "params": ["<record-type-id>", "<record-id>"]
}
```

バインド変数は現行REST SuiteQLに公式対応がある。型IDの具体値と列の公開範囲はテナントで確認する。[S28]

削除履歴は次のように分かれる。

| 種類 | 削除後の扱い |
|---|---|
| 通常System Notes | 所属レコードが削除されると、そのSystem Notesも削除される |
| System Notes v2 | 対応レコードでは削除後も履歴を保持する |
| Deleted Recordログ | 一部レコードで基本情報を保持。通常System Notesの完全な代替ではない |

根拠: [S31][S33]。

Oracleの移行ガイドには`deletedrecord`をSuiteQLで読む例がある。一方、削除記録の一般説明にはデータソースやNetSuite Data Intelligence機能の条件がある。**すべての型・すべての契約で同一の削除履歴テーブルが利用可能とは扱わない**。[S50][S65]

通常SystemNoteの検索結果には、UIに見える非フィールド操作等がそのまま全部出るとは限らない。削除前後にUI／Analytics／Deleted Recordそれぞれで何が残るかを検証する。

### Q-L4. REST要求の実行ログと保存期間

**回答: 実行ログはある。保存期間の確定値は、今回確認したREST公式ページからは取得できない。**

閲覧経路は、`Setup > Integration > Manage Integrations > 対象Integration > Execution Log > REST Web Services`。[S34]

時刻、所要時間、結果、利用者メール、HTTPメソッド／ステータス、URL、要求／応答本文が掲載される。機密フィールドはマスクされる。System Notesとは異なりHTTP呼び出しの調査に使うログである。[S34]

**保存期間:** RESTの当該ページには日数が明記されていない。SOAP、MCP、SuiteScriptのログに記載された「本番21日／Sandbox7日」「30日」等を、RESTの確定値として転記しない。T-L04で対象アカウントの実表示・設定・Oracleへの確認により確定する。

**提案:** 長期監査や利用者別追跡をREST実行ログだけに依存させない。CLB側ログの保持期間、アクセス権、マスキング、改ざん対策は別仕様にする。公開資料で未確認のREST実行ログ一括エクスポートAPIを前提にもしない。

---

## M. 利用者ごとの権限をNetSuiteに任せる方式

### Q-M1. Authorization Code GrantはRecord ServiceとSuiteQLの両方で使えるか

**回答: できる。両方ともREST Web ServicesへのOAuth 2.0アクセスとして構成できる。ただし各利用者にNetSuite側の適切なユーザー・ロール・権限・ライセンスが必要。**

ロールには`Log in using OAuth 2.0 Access Tokens`を付与し、REST Web Services、SuiteAnalytics Workbook、対象データへの権限等を揃える。IntegrationでAuthorization Code Grantと必要スコープを有効にする。管理用の`OAuth 2.0 Authorized Applications Management`だけを付けてもAPI利用権限の代わりにはならない。[S38][S66]

Workato RESTも、M2Mと利用者別Authorization Codeを接続方法として説明している。[S45]

| トークン | 現行公式説明 |
|---|---|
| Access token | 60分 |
| 通常のRefresh token | 7日 |
| Public clientのRefresh token | 既定2日・1回限り。Integration設定で1〜720時間に変更可能 |

根拠: OAuth 2.0のToken Endpoint資料[S37]。CLBサーバーでクライアントシークレットを安全に保持する方式と、Public clientを混同しない。リフレッシュトークンの失効・期限切れ・同意取消しを検出し、新しい認可フローへ誘導する。無期限の無人運用を約束しない。

ライセンスはロールとログインメール等によって計数される。一般利用者、限定用途利用者などの区分があるため、「APIなら全員無料」「必ず全員同じフルライセンス」とは断定しない。利用範囲に合うライセンスをOracle／販売契約で確認する。**共通M2Mという技術構成だけで間接利用の契約条件が解消するわけでもない**。[S40][S67]

**CLBへの仕様上の影響:** CLB利用者とNetSuite利用者／アカウント／ロールの対応、利用者別トークン保管、再認可、利用者変更時のキャッシュ分離が必要。CLBのログインとNetSuiteの認可は別イベントとして扱う。

### Q-M2. 1つのIntegrationで複数ロールをM2Mで使い分けられるか

**回答: できる。異なるEntity/Role用のマッピングと証明書を用意し、対応するトークンを使い分ける。**

M2M設定ではApplication・Entity・Role・証明書を指定する。1枚の証明書は1つの組合せに対応し、別の組合せには別証明書が必要。現行説明では、同じIntegration・Entity・Roleの組合せにつき有効な証明書は最大5枚。[S35][S36]

これは、任意の要求に`roleId`ヘッダーを追加してトークンのロールを変える仕組みではない。トークン取得時の証明書／マッピングを切り替え、そのトークンを利用する。

**提案:** サーバーで「接続プロファイル」を選ぶ。ブラウザーが指定した任意ロールIDで強い権限に切り替えない。トークン・メタデータ・選択肢キャッシュをアカウント／接続主体／ロール単位で分離する。複数ロールに分けても、同じEntityを共有するなら利用者本人の監査にはならない。

Sandbox更新等ではM2M設定がコピー・保持されないという注意があるため、環境ごとの再設定も運用条件に含める。[S35]

### Q-M3. SuiteQLへのロール制限の効き方

**回答: ロール制限が効く。ただし『常に行を黙って除く』でも『常にエラー』でもない。**

OracleはSuiteQLがSuiteAnalytics Workbookと同じロールベースのデータアクセス制限を適用すると説明している。レコード種別・フィールドの公開範囲も、有効機能と権限に依存する。[S39][S56]

| 制限の層 | コネクターが想定すべき扱い |
|---|---|
| 子会社等による参照範囲 | 許可された範囲しか取得できない。取得件数ゼロもあり得る |
| レコード種別・列の参照権限なし | スキーマ上非公開／問い合わせエラーとなり得る。必ず空集合にはならない |
| System Notes専用権限なし | 自分の変更分のみ返るという明示的な例がある |
| 書き込み対象が許可範囲外 | 読めない行を指定して書いてよいわけではない。書き込み側でも別途検証される |

具体的な子会社・部門・従業員制限ごとの「0件になる／特定エラーになる」の一覧までは、今回の公開資料から確定できない。T-M03で同一SQLを制限の異なるロールで比較する。[S32][S39]

SuiteScriptの`N/query.runSuiteQL`にある`metaDataProvider: STATIC`等を、RESTの要求オプションとして実装してはならない。これは別インターフェースの設定である。[S57]

**共通M2Mでの最重要点:** NetSuiteが制限するのは共通ロールの範囲であり、CLB利用者A/Bの差ではない。CLB側で利用者別制限が必要なら、一覧だけでなく詳細、集計、参照選択、明細、保存、Action、Transformにもサーバー側で適用する。ブラウザーで行を隠すだけでは認可にならない。

---

## N. データソースに載せない機能

### Q-N1. selectOptionsの新規／既存、ページング、絞り込み、依存関係

**回答: 新規・既存とも取得できる。ページングと名前による絞り込みがある。依存する全項目の一覧を自動取得する公式APIは今回確認できない。**

公式の基本形式は、新規がPOST、既存がPATCH。同じレコードURLでも、以下のAcceptが選択肢取得を指定する。[S18]

```http
POST /record/v1/customer?fields=entitystatus&limit=50&offset=0
Accept: application/vnd.oracle.resource+json; type=select-options
Content-Type: application/json

{}
```

```http
PATCH /record/v1/customer/<customer-id>?fields=entitystatus&limit=50&offset=0
Accept: application/vnd.oracle.resource+json; type=select-options
Content-Type: application/json

{}
```

**これは選択肢取得用の要求。通常の保存PATCHとは別の処理として実装し、Acceptを落としたまま送らない。** 本文には候補判定に必要な編集中の項目値を送る。公式には仕訳の子会社や明細値で候補が変わる例がある。[S18]

次は**公式掲載の候補値を1件だけ抜粋したもの**。全HTTP応答ではない。[S18]

```json
{"id":13,"refName":"CUSTOMER-Closed Won"}
```

構造上は`<field>._selectOptions.items`配下に候補が入り、そのフィールドごとに`count`、`offset`、`hasMore`、`totalResults`、ページングリンクを持つ。

- 複数項目は`fields=a,b`。
- `q`で使える演算子は`CONTAIN`、`IS`、`START_WITH`。比較対象は`refName`でありIDではない。
- `limit/offset`は要求内で共通だが、各項目の候補数と次ページリンクは別。
- ロールが違えば候補も変わる。参照先のレコード一覧を直接読む権限がなくても選択肢取得が可能な場合がある。[S18]

**依存関係の判定:** JSON Schemaの型・参照先から、入力依存の全グラフを復元できるという保証は見つからない。Designerで再取得トリガー項目を指定する、対応済みレコードに既知の依存関係を用意する、必要な現在値を送って都度解決する、といった仕様が必要。依存条件とロールを無視した全利用者共通キャッシュは採用しない。

### Q-N2. Actionの一覧・承認例・要求と応答

**回答: 対応Actionは使える。ただしRESTメタデータにはAction一覧が提供されず、すべてのUI承認ボタンが`@approve`になるわけではない。**

一覧の一次資料はOracleの**Supported Record Actions**。RESTはSuiteScriptの対応Actionと同じ範囲をサポートする説明がある。[S19][S20]

確認した一覧には、Time（TimeBill）のapprove/reject/retract/submit、Inventory Countのapprove等がある。一方、受注について何も確認せず`/salesOrder/<id>/@approve`を生成する根拠はない。[S20]

Timeの承認の**仕様を合成した検証用要求例**:

```http
POST /record/v1/timeBill/<time-entry-id>/@approve
Content-Type: application/json

{}
```

`timeBill`の正確な公開リソース名、機能有効化、対象状態、承認可能なロールは実機メタデータと業務設定で確認する。公式Time Actionsのapproveは追加パラメーター不要とされるが、**この承認要求を今回実行した結果ではない**。[S21]

Action共通資料には、Vendor Paymentのconfirm成功時に200 OKと次の値を含む応答例がある。[S19]

```json
{"result":true}
```

ただし、これは**confirmの公式掲載値の抜粋**。Time承認の応答も常に同じbooleanだとは扱わない。承認の応答実物、通知内容、エラーはT-N02で採取する。

受注の標準的な承認ユースケースは、Oracleが`orderStatus`をBへPATCHする方式を説明している。[S22]

```http
PATCH /record/v1/salesOrder/<sales-order-id>
Content-Type: application/json

{"orderStatus":"B"}
```

標準の状態変更と、カスタム承認ワークフローの正式な承認遷移は同義とは限らない。後者を単なるフィールド書き換えで代用できると約束しない。

**仕様提案:** 専用ボタンから呼ぶ機能には、Action、特定状態変更、Transform等の処理種類を区別して保持する。表示名が「承認」だけで呼び出し先を機械的に決めない。

### Q-N3. Transformの一覧、受注→請求書、同時の値・明細変更

**回答: 対応している組合せならできる。変換情報はメタデータにも提供される。**

出所は、RESTのTransforming Records、SuiteScript `record.transform(options)`のSupported Transformations表、対象アカウントのメタデータ。変換元・変換先がRESTで公開され、機能・状態・ロール条件を満たすことが必要。[S23][S24][S16]

受注→請求書の形式は次のとおり。これは**公式URL形式を基にした例**。

```http
POST /record/v1/salesOrder/<sales-order-id>/!transform/invoice
Content-Type: application/json

{"memo":"Created from CLB"}
```

公式掲載例は204 No Content。生成したレコードはLocation等から特定してGETする。**本文に完成した請求書JSONや生成明細の全IDが必ず返る前提にはしない**。[S23]

変換時に本文でフィールドや明細値を渡す例も掲載されている。例えば、以下は公式例の構造を使った**検証用ペイロード**。

```json
{
  "memo": "Invoice created from CLB",
  "item": {
    "items": [
      {"item":{"id":"<item-id>"},"amount":10}
    ]
  }
}
```

ただし、これだけで同じ品目が複数行ある受注の「どの元明細を変更するか」が確実に定まるとは言えない。`orderLine`等の対応、部分請求、既請求数量、明細追加／置換の意味は変換種別ごとに検証する。元伝票のREST行キーを無条件で変換先の行キーに流用しない。

**仕様提案:** 変換元・変換先・ヘッダー上書き・明細マッピングを専用機能として明示する。親子の通常保存の途中で暗黙にTransformしない。

### Q-N4. 保存検索・Datasetの結果、列型、ページングと上限

**回答: 両方の結果取得はできる。ただし、列定義と型をRESTだけで完全自動取得できるという仕様にはできない。**

#### 保存検索

現行RESTは保存検索の一覧取得と実行をサポートする。2026.2の月次リリース情報にも追加が掲載されている。[S25][S58]

```http
GET /query/v1/savedsearch
GET /query/v1/savedsearch/<script-id-or-internal-id>/result?limit=100&offset=0
```

実行結果は`items`を持つコレクション形式。次は**公式結果の1行から一部の値を抜粋**したもの。[S25]

```json
{"balance":111.804,"internalid":"21","isinactive":false}
```

参照値が`id/refName`オブジェクトとなる例もある。通常／カスタム項目、結合、数式、集計等を扱える一方、RESTで保存検索の定義を作成・編集・削除したり、実行時に条件・フィルター・ソートを上書きしたりすることはできない。完全な検索定義の返却も非対応。[S25]

**列名と型:** 実行結果のキーとJSON値から一部を観察できるが、空結果、全null、数式、同じ表示名等を含め、完全な型付き列スキーマを得られる保証は確認できない。検索の定義側で決めた列契約をDesignerへ登録するか、サンプル取り込み＋明示型指定を用意する。

#### Dataset

```http
GET /query/v1/dataset
GET /query/v1/dataset/<dataset-id>/result?limit=100&offset=0
```

SuiteAnalytics Workbook機能と権限が必要。DatasetはUIで定義し、RESTには**Datasetメタデータが提供されない**。[S26]

公式の結果例には数値・日付が文字列として出る。以下は**掲載値の一部**。[S26]

```json
{"amount":"5.33333334","itemcount":"1","trandate":"5/28/2010"}
```

数式列は利用者が付けた名前がキーになる。したがってJSONの文字列を「業務上も文字列型」と確定できない。通常Record GETと同じ型／参照表現になるとも限らない。Dataset一覧にある名前・ID・説明・基礎レコード種別は、列型スキーマではない。[S26]

#### ページングと件数上限

`limit/offset`、`links`、`hasMore`、`count`、`totalResults`を使う。一般のCollection Paging説明は既定1,000件、最大1,000ページ、SuiteAnalytics Connectの有無による最大件数の説明を含み、offsetはlimitで割り切れる必要がある。[S27]

ただし、上限を次のように区別する。

| 対象 | 今回確認できた上限情報 | コネクター仕様 |
|---|---|---|
| REST SuiteQL | 専用ページに最大100,000件と明記 | Connect契約があればREST SuiteQLも100万件になる、とはしない |
| Dataset／一般コレクション | 共通PagingページではConnectなし100,000、Connect利用時最大1,000,000等の説明 | 対象アカウントと呼出し経路で確認。ページサイズ・ページ数によっても到達件数が変わる |
| 新しい保存検索REST | 一覧のページングと共通Pagingへの参照、結果のページング構造は確認 | 結果実行固有の全条件・上限を本文だけで完全確定できず。100k/1mを無条件に保証しない |

根拠: [S25][S26][S27][S28]。大規模データでは、仕様上の上限に達したことと検索終了を区別し、黙って途中の結果を「全件」と表示しない。T-N04で実境界を記録する。

### Q-N5. 固定選択肢をメタデータから全部取れるか／SuiteQL表

**回答: 真のenumはメタデータから候補を取得できる。しかし、`entityStatus`や`customlist_xxx`を含めたすべての候補を、固定enumとして一括取得できるわけではない。**

**質問の前提で区別が必要な点:** OracleのselectOptions例は`entitystatus`を対象にしており、Recordの応答例ではCustomer Statusへの参照になっている。したがって`entityStatus`を単純な不変enumの代表として実装しない。[S18][S51]

| 種類 | 取得元 | Designerへの扱い |
|---|---|---|
| スキーマ上のenum | Metadata Catalogのenum／利用可能な内部値 | 静的候補として取り込み可能。ただしスキーマ更新時に差分確認 |
| Customer Status等の参照 | 参照先レコードAPI、利用可能ならSuiteQL、又はselectOptions | ID＋表示名。文脈・権限制限があれば動的解決 |
| カスタムリスト | 当該カスタムリストのデータ／selectOptions等 | メタデータだけで全候補値が揃うとは仮定しない。取得権限・非アクティブ・表示名・言語も考慮 |

メタデータはロールやカスタマイズによって変化する。カスタムリストはRESTのCRUD対象として説明されるが、これと「JSON Schemaへすべてのリスト値が埋め込まれる」は別の主張である。[S16][S14]

SuiteQLの参照表は対象アカウントのRecords Catalogを正とする。標準の`customerstatus`、カスタムリストのスクリプトIDに対応する表等を候補として確認する。今回、**すべての`customlist_xxx`で同じ列名・同じ公開方法を保証する一次資料は取得できていない**。`SELECT id,name FROM customlist_xxx`を無検証の汎用実装に固定しない。`query.Type.CUSTOM_LIST`の存在は、個々の候補値テーブルのスキーマを保証するものではない。[S28][S52][S53]

**仕様提案:** 固定候補は「メタデータenum」「選択肢データのスナップショット」「実行時selectOptions」を明示的に区別する。スナップショットの再取り込みと、既存値が候補から外れた場合の表示・保存方針を決める。

---

## 6. 仕様へ反映すべき判断と、実装を止める条件

### 6.1 今回の調査で決められること

1. **独立レコードの保存と、親に属する明細の保存は分ける。** UI／モジュールの見せ方まで別製品にする必要はない。
2. **取引の新規作成は、必要明細を同じPOSTに含める。** 空の親やダミー明細を標準前提にしない。
3. **明細削除は汎用DELETEへ流さない。** 型別の親PATCH／置換処理と、全行取得・競合対策が必要。
4. **SuiteQLは検索用のモデル、RESTは書き込み用のモデルとして対応付けを検証する。** 列名・IDの類似だけで同一としない。
5. **共通M2MならCLBの利用者認可・監査はCLB側にも必要。** NetSuiteロールを用意するだけでCLB利用者間の制御が自動完成するわけではない。
6. **選択肢・Action・Transform・保存検索は、それぞれ公開範囲が違う。** 全部がメタデータだけで自動生成できるとはしない。

これらは前節の確認結果に基づく仕様上の判断であり、クラス名やコードの差し込み位置を決定したものではない。

### 6.2 対応済みと宣言する前の必須確認

| 優先 | 未確定事項 | 対応する質問／テスト | 未解決のまま実装した場合のリスク |
|---|---|---|---|
| 最優先 | CLBで同じ親への変更集合を取得できるか | K4/K9、T-C01 | 明細を毎回即確定し、仕訳等が成立しない |
| 最優先 | REST行キーとSuiteQL列の対応・安定性 | K5、T-K05 | 別明細更新、削除、保存後ID取り違え |
| 最優先 | 削除時の置換と同時更新対策 | K3/K6、T-K03/T-K06 | 読んでいない行・他利用者の追加行を消す |
| 最優先 | POの公式記述矛盾、明細種別の公開範囲 | K1、T-K01 | API非公開項目を対応済みとして扱う |
| 高 | 共通M2Mの利用者認可・監査 | L/M、T-L01/T-M03 | 他利用者データへのアクセス、操作主体不明 |
| 高 | selectOptions依存・キャッシュ分離 | N1/N5、T-N01/T-N05 | 不正な選択肢提示、権限漏えい |
| 高 | 保存検索／Datasetの列契約と上限 | N4、T-N04 | 型誤認、列変更による破損、結果欠落 |
| 運用 | REST実行ログの保持日数 | L4、T-L04 | 必要な期間の監査証跡を失う |

### 6.3 今回得られていない証拠

- 対象アカウントでのゼロ明細POSTの実レスポンス・エラー全文。
- 対象5取引のすべてについての行キー対応表と、行追加・削除・並べ替え後の安定性。
- 顧客住所録の実際のキー項目と、住所サブレコードの更新／削除の成功ペイロード。
- 承認Actionの実リクエスト／レスポンスと、カスタムワークフローへの適合性。
- 保存検索／Dataset固有の上限の境界テストと型・列名の全ケース。
- M2MでのSystem Notes実表示、派生スクリプトの記録主体、REST実行ログの保持日数。

公開一次資料で保存方式を分ける根拠は得られたが、これらのアカウント固有の契約までは資料だけでは確定できない。次の検証文書で、何を送って何を保存すれば判定できるかを指定する。


---

## 7. 一次資料一覧

参照日はいずれも2026-11-14。ページの例示値は実アカウントでの検証結果ではない。SOAP／SuiteScriptの資料は、そのインターフェースであることを明記して利用し、RESTのワイヤー仕様へ無条件に転用していない。

- [S01] Oracle — **Working with Sublists**。K2/K3/K9：keyed・non-keyed、単独削除の制約。
- [S02] Oracle — **Updating a Sublist**。K2/K5：既定PATCH、204応答。
- [S03] Oracle — **Replacing a Sublist**。K2/K3：replaceの動作。
- [S04] Oracle — **Use Case For Updating Your Sales Order**。K2/K3：line更新、キー省略追加、削除の置換。
- [S05] Oracle — **Invoice**。K1/K3/K4：item以外の作成、税、line更新。
- [S06] Oracle — **Purchase Order**。K1/K4：item公開範囲の説明と例が矛盾。
- [S07] Oracle — **Journal Entry**。K1：RESTの作成構造と機能制限。
- [S08] Oracle — **Use Case for Updating Line Items in a Journal Entry**。K1/K4：更新時の貸借均衡。
- [S09] Oracle — **Journal Entry Body and Sublist Fields**。K1：最低2行等のレコード規則。SOAP側資料でありREST実測ではない。
- [S10] Oracle — **Adding Items on a Sales Transaction**。K1：販売取引の明細要件。
- [S11] Oracle — **Estimate**。K1：見積のRESTレコード。
- [S12] Oracle — **Accessing Subresources in REST Web Services**。K6/K7：GETパス、独立作成・直接削除の制約。
- [S13] Oracle — **Working with Subrecords**。K7：サブレコードの親コンテキスト。
- [S14] Oracle — **Using CRUD Operations on Custom Records, Custom Lists, and Custom Transactions v1**。K8/N5：独立カスタム子CRUD、カスタムリスト。
- [S15] Oracle — **Using the REST Web Services SuiteScript Execution Context**。K4/L1：REST起点のスクリプトと履歴。
- [S16] Oracle — **Working with Resource Metadata**。K/N：動的スキーマ、enum、OpenAPI、ロール別メタデータ。
- [S17] Oracle — **Creating a Record Instance**。K5：204とLocation。一般説明を他の専用機能へ無条件に拡張しない。
- [S18] Oracle — **selectOptions Operation**。N1/N5：専用Accept、候補、入力依存、検索、ページング。
- [S19] Oracle — **Executing Record Actions**。N2：@形式、200例、メタデータなし。
- [S20] Oracle — **Supported Record Actions**。N2：型別対応Actionの一覧。
- [S21] Oracle — **Time Actions**。N2：approve、必要パラメーター、SuiteScript側結果。
- [S22] Oracle — **Use Case For Approving Your Sales Order**。N2：orderStatus A/BのPATCH。
- [S23] Oracle — **Transforming Records**。N3：変換URL、204、本文での上書き。
- [S24] Oracle — **record.transform(options)**。N3：対応変換表。REST公開条件も別途必要。
- [S25] Oracle — **Using Saved Search in REST Web Services**。N4：保存検索REST、応答、定義編集・条件上書き不可。
- [S26] Oracle — **Working with SuiteAnalytics Datasets in REST Web Services**。N4：Dataset結果、列名、メタデータなし。
- [S27] Oracle — **Collection Paging**。N4：limit/offsetと一般のページング上限。
- [S28] Oracle — **Executing SuiteQL Queries Through REST Web Services**。K5/L3/N4：SuiteQL、バインド変数、100,000件上限。
- [S29] Oracle — **SuiteTalk Web Services Integration — 2026.2 Release Notes**。0：バインド変数等の現行追加。
- [S30] Oracle — **Viewing Transaction System Notes**。L1：Set by、変更履歴、作成履歴の設定。
- [S31] Oracle — **Searching System Notes**。L3：通常System Notesと削除、v2保持。
- [S32] Oracle — **Viewing System Notes in Analytics Data Source**。L3/M3：全履歴権限、自身のみの結果、スキーマ差。
- [S33] Oracle — **System Notes and System Notes v2**。L3：両システムの対象・削除履歴の差。
- [S34] Oracle — **Using the REST Web Services Execution Log**。L4：閲覧場所・保存項目。保持日数は当該本文に記載なし。
- [S35] Oracle — **OAuth 2.0 Client Credentials Setup**。M2：Entity/Role/Applicationマッピング、証明書数、環境差。
- [S36] Oracle — **Certificate Conditions**。M2：組合せごとの証明書。
- [S37] Oracle — **Step Two POST Request to the Token Endpoint**。M1：Authorization Codeとトークン期限。
- [S38] Oracle — **Set Up OAuth 2.0 Roles**。M1：OAuth利用権限と管理権限の区別。
- [S39] Oracle — **SuiteQL**。M3：Workbookと同じロール制限。
- [S40] Oracle — **How NetSuite Counts User Licenses**。M1：ロール・ログインメールによる計数。
- [S41] CData — **ADO.NET Provider for NetSuite — Tables (Build 25.0.9539)**。P1/P2：子表、DML、親INSERTへの集約。
- [S42] CData — **Inserting Parent and Child Records**。P1：#TEMPとAggregate。一般例とNetSuite XMLの区別。
- [S43] Celigo — **Import NetSuite line items**。P1：サブリストマッピング、行照合、置換。
- [S44] Workato — **NetSuite SOAP — Update object action**。P1：入れ子リスト、Replace all。SOAP資料。
- [S45] Workato — **NetSuite REST — Connection setup**。P4/M1：M2Mと利用者別Authorization Code。
- [S46] Boomi — **NetSuite operation**。P1/P3：XMLプロファイル、明細、Get Select Value。
- [S47] MuleSoft — **NetSuite Connector 13.0 Reference**。P1/P3：レコード操作、Get Select Value。
- [S48] Oracle — **SuiteAnalytics Connect**。P1：読み取り専用。現行NetSuite2.com。
- [S49] Google Cloud — **Collect Oracle NetSuite logs — Google Security Operations**。L3：実装者の一次資料。SystemNoteのREST SuiteQL例と列名。
- [S50] Oracle — **getDeleted — SOAP to REST migration**。L3：deletedrecordへのSuiteQL例。
- [S51] Oracle — **Getting the Result of an Executed Request**。N5：entityStatusのcustomerstatus参照例。
- [S52] Oracle — **query.Type**。N5：CUSTOM_LISTは個別候補表スキーマの保証ではない。
- [S53] Oracle — **Conventions for Naming Custom Objects**。K5/L2/N5：custcol/custbody/customlistの区分。
- [S54] Oracle — **Inventory Item**。K3/K6：価格サブリストへの複合キーPATCH例。
- [S55] Salesforce — **Design an Effective Salesforce NetSuite Flow**。P1：MuleSoft ComposerのSales Order＋Item List入力。
- [S56] Oracle — **Record Types and Fields**。K5/M3：Analyticsの公開データと権限。
- [S57] Oracle — **query.runSuiteQL(options)**。M3：metaDataProviderはN/queryの設定。
- [S58] Oracle — **September Monthly Release — 2026.2**。0/N4：保存検索RESTの追加。
- [S59] Oracle — **NetSuite Service Tier Structure**。K4：同時実行枠とSuiteCloud Plus。
- [S60] Oracle — **Inbound Shipment**。K5：PO lineuniquekeyの別用途。
- [S61] Oracle — **Use Case — Adding an Employee Address**。K7：親PATCHでの住所追加の類例。
- [S62] Workato — **NetSuite REST — Create record action**。P1/P2：レコード種別選択と親ID。
- [S63] Workato — **NetSuite REST — Update record action**。P1：親レコード操作。
- [S64] Oracle — **Understanding the Context for Changes**。L1：連鎖処理のContext。
- [S65] Oracle — **Searching for Deleted Records**。L3：削除記録とデータソース・機能条件。
- [S66] Oracle — **Prerequisites and Setup for REST Web Services**。M1：REST/Workbook有効化と権限。
- [S67] Oracle — **User Licenses**。M1：利用に適合するライセンスが必要。

<!-- Reference links -->

[S01]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_1545142407.html
[S02]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545295601.html
[S03]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545297048.html
[S04]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_159795512874.html
[S05]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_161488248489.html
[S06]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_161165602348.html
[S07]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_159886587653.html
[S08]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_0224115029.html
[S09]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4099136433.html
[S10]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4588373194.html
[S11]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0719105836.html
[S12]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_159894563219.html
[S13]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_156337267817.html
[S14]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_158100110171.html
[S15]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157927488310.html
[S16]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_1540810168.html
[S17]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1545141395.html
[S18]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0115100241.html
[S19]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156631158782.html
[S20]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1516982564.html
[S21]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1529337353.html
[S22]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_159795516182.html
[S23]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157901123882.html
[S24]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4267258715.html
[S25]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0804090827.html
[S26]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156577938018.html
[S27]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156414087576.html
[S28]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157909186990.html
[S29]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N3950559.html
[S30]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N555355.html
[S31]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N673560.html
[S32]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_96115732745.html
[S33]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_160225379741.html
[S34]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156570320384.html
[S35]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_162686838198.html
[S36]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_162755332391.html
[S37]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_158081952044.html
[S38]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157771510070.html
[S39]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_156257770590.html
[S40]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0304103142.html
[S41]: https://cdn.cdata.com/help/DNM/ado/pg_suitetalktableinfo.htm
[S42]: https://cdn.cdata.com/help/DNK/rcmd/pg_deepinsert.htm
[S43]: https://docs.celigo.com/hc/en-us/articles/360052710532-Import-NetSuite-line-items
[S44]: https://docs.workato.com/en/connectors/netsuite/netsuite-update-object-action.html
[S45]: https://docs.workato.com/en/connectors/netsuite-rest/connect.html
[S46]: https://help.boomi.com/docs/Atomsphere/Integration/Connectors/r-atm-NetSuite_operation_8c774e1e-5b05-4a8b-ab5a-2cfcb6ed918a
[S47]: https://docs.mulesoft.com/netsuite-connector/latest/netsuite-reference
[S48]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/chapter_3963845427.html
[S49]: https://docs.cloud.google.com/chronicle/docs/ingestion/default-parsers/oracle-netsuite
[S50]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0701101315.html
[S51]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_161252889998.html
[S52]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1510878994.html
[S53]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_162428958352.html
[S54]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_161522936052.html
[S55]: https://trailhead.salesforce.com/fr/content/learn/modules/netsuite-data-sync-with-mulesoft-composer/design-a-flow-22
[S56]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_164312261929.html
[S57]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_157960542026.html
[S58]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_42174309908.html
[S59]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/subsect_159162378384.html
[S60]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_7165449964.html
[S61]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_1208042345.html
[S62]: https://docs.workato.com/en/connectors/netsuite-rest/create-record-action
[S63]: https://docs.workato.com/en/connectors/netsuite-rest/update-record-action
[S64]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4032860783.html
[S65]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_4031815869.html
[S66]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_5085602973.html
[S67]: https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/article_0304102503.html
