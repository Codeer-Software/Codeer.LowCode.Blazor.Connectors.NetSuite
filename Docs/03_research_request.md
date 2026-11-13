# Codeer.LowCode.Blazor.Connectors.NetSuite — 追加調査の依頼

- 文書ID: REQ-01
- 作成日: 2026-11-13
- 依頼先: ChatGPT (調査担当)
- 前提文書: [API-01](01_netsuite_api_inventory.md) / [SPEC-01](02_netsuite_specification_draft.md)

## 1. 何のための調査か

コネクターの最初の版を、NetSuite の実アカウントなしで実装した。HTTP の偽物を相手にしたテストは通るが、
NetSuite の実際の応答・構文について **公開資料で裏を取れていない前提** がコードに入っている。
この依頼は、その前提を 1 つずつ確かめるためのもの。API-01 の調査は「どの API を使うか」までで、
ここで聞くのは「その API に実際に何を送り、何が返るか」の細部である。

### 実装の作り (前提を読むのに必要な分だけ)

- NetSuite を CLB の **データソース** として見せている。CLB 本体は NetSuite 用データソースを Oracle だと思って SQL を組み立て、
  コネクターがその SQL を受け取って読み替える。
- **読み出し** (一覧・検索・件数): 本体の SELECT を SuiteQL に直して `POST /services/rest/query/v1/suiteql` に送る。
- **書き込み**: 本体の INSERT / UPDATE / DELETE を読み取り、`POST / PATCH / DELETE /services/rest/record/v1/{type}[/{id}]` にする。
- **表と列の名前**: モジュールの表名 = REST のレコード名 (例 `customer`)。列名 = REST の項目名を小文字にしたもの (例 `companyName` → `companyname`)。
  SuiteQL にはこの表名・列名をそのまま使い、書き込みでは REST の元の項目名に戻す。
- **レコードの定義**: `GET /services/rest/record/v1/metadata-catalog/{type}` (`Accept: application/schema+json`) の JSON Schema から列の一覧と型を作る。

## 2. 回答の書き方

- 質問ごとに、質問 ID (Q-A1 など) を見出しにして答える。
- それぞれ **結論 → 根拠** の順。根拠は Oracle 公式ヘルプの URL と該当箇所の引用 (原文) を付ける。
- 公式資料で確認できたことと、コミュニティの記事・フォーラム・他社コネクターの実装から分かったことを **分けて書く**
  (「公式」「非公式 (出所)」のどちらかを各項目に明記)。非公式の情報しかなければ、その旨と出所の URL を書く。
- 分からなかったものは「確認できず」と書く。推測で埋めない。
- 応答・要求の **実物の例** (JSON・HTTP ヘッダー) が資料にあれば、省略せずそのまま貼る。こちらはその形に合わせてコードを直す。
- 「いまの前提」と違っていた項目は、冒頭に **【前提と相違】** と付ける。

## 3. 質問

### A. 認証 (OAuth 2.0 Client Credentials)

いまの前提: JWT のヘッダーは `{"typ":"JWT","alg":"PS256","kid":"<Certificate ID>"}`、
ペイロードは `{"iss":"<Client ID>","scope":["rest_webservices"],"aud":"<トークン URL>","iat":<秒>,"exp":<iat+300>}`。
トークン URL は `https://<アカウント>.suitetalk.api.netsuite.com/services/rest/auth/oauth2/v1/token`。

- **Q-A1** `scope` クレームの形。JSON の配列か、1 つの文字列か。文字列なら複数の区切りはカンマか空白か。公式の JWT の例をそのまま示してほしい。
- **Q-A2** `aud` クレームに入れる値。トークン URL そのものでよいか (アカウント固有のホスト名か、共通のホスト名か)。
- **Q-A3** `iat` / `exp` 以外に必須のクレームはあるか (`jti`・`nbf` など)。`exp - iat` の上限は何分か。
- **Q-A4** PS256 のとき、RSA 鍵の長さの要件 (3072 ビット以上など) と、PSS の salt 長の指定はあるか。ES256 のときの曲線の指定。
- **Q-A5** トークン応答の JSON の実物。`expires_in` は数値か文字列か。
- **Q-A6** 失敗時 (署名不正・証明書失効・マッピング無し) の HTTP ステータスと応答本文の形。

### B. レコードのメタデータ (JSON Schema)

いまの前提: 応答はルートに `properties` を持つ JSON Schema で、項目は次のように判定している。

| 判定 | 列の種類 |
|---|---|
| `"type":"string"` | 文字列 |
| `"type":"string","format":"date"` | 日付 |
| `"type":"string","format":"date-time"` | 日時 |
| `"type":"boolean"` | 真偽 |
| `"type":"integer"` | 整数 |
| `"type":"number"` | 数値 |
| `"$ref"` があり、参照先の名前に `-` が無い | 参照 (ほかのレコード・選択肢) |
| `"$ref"` があり、参照先の名前に `-` がある (例 `customer-addressBookCollection`) | 明細・サブレコード (列にしない) |
| `"type":"object"` で `properties` に `id` があり `items` が無い | 参照 |
| `"type":"array"`、それ以外 | 列にしない |

- **Q-B1** `GET /services/rest/record/v1/metadata-catalog/customer` を `Accept: application/schema+json` で呼んだ応答の実物
  (公式資料・記事にある例。全体が無理なら、文字列・真偽・数値・日付・日時・参照・選択肢 (enum)・複数選択・明細・サブレコード・カスタム項目の
  それぞれ 1 つずつが入る抜粋)。
- **Q-B2** 上の判定表で、実物と合わない行はどれか。特に、ほかのレコードへの参照 (例 `subsidiary`) と、固定の選択肢 (例 `entityStatus` や `_xxx` 形式の enum) が
  スキーマでどう書かれるか。
- **Q-B3** 必須の項目はスキーマのどこに現れるか (ルートの `required` 配列か、項目ごとの属性か)。
- **Q-B4** 読み取り専用 (書き込めない) 項目はスキーマから見分けられるか (`readOnly`・`x-ns-*` など)。見分けられないなら、そのことを。
- **Q-B5** `x-ns-filterable` など `x-ns-` で始まる拡張の一覧と意味。
- **Q-B6** 項目の表示名 (`title`) は付くか。付くなら言語は何で決まるか。
- **Q-B7** `application/schema+json` と `application/swagger+json` で、項目ごとに得られる情報に差はあるか。
- **Q-B8** レコード名の大文字・小文字。`metadata-catalog/Customer` と `metadata-catalog/customer` は同じか。`record/v1/{type}` の `{type}` はどうか。

### C. SuiteQL の構文 (REST 経由)

いまの前提: 次のような SuiteQL を送っている (本体の SQL から引用符とページング句を外したもの)。

```sql
select customer.id as customer_id, customer.companyname as customer_companyname,
  TO_CHAR(customer.startdate, 'YYYY-MM-DD') as customer_startdate,
  subsidiary_0.name as subsidiary_0_name
from customer left join subsidiary subsidiary_0 on customer.subsidiary=subsidiary_0.id
where (customer.companyname like ? escape '\') and (customer.isinactive = 'F')
  and (customer.creditlimit >= 10) and (customer.startdate < TO_DATE('2026-01-02', 'YYYY-MM-DD'))
order by customer.companyname desc, customer.id
```

それぞれ、REST 経由の SuiteQL で使えるか・注意点を答えてほしい。

- **Q-C1** 表の別名を `AS` なしで書く (`left join subsidiary subsidiary_0`)。列の別名を `as` つきで書く。
- **Q-C2** `LIKE ? ESCAPE '\'`。ESCAPE 句は使えるか。
- **Q-C3** `TO_CHAR(日付列, 'YYYY-MM-DD')`、`TO_CHAR(日時列, 'YYYY-MM-DD HH24:MI:SS')`、`TO_DATE('...', '...')`。日時の比較に `TO_DATE` を使ってよいか、`TO_TIMESTAMP` が要るか。
- **Q-C4** 真偽の項目の比較は `= 'T'` / `= 'F'` でよいか。
- **Q-C5** 集計。本体の集計機能は次の形の SuiteQL になる。それぞれ使えるか:
  - `count(*)`、`count(distinct 列)`、`sum(列)`、`avg(列)`、`min(列)`、`max(列)`、`group by 式, 式`、`having count(*) >= 2`
  - 日付の丸め: `TRUNC(列)`、`TRUNC(列, 'HH')`、`TRUNC(列, 'IW')`、`TRUNC(列, 'MM')`、`TRUNC(列, 'Q')`、`TRUNC(列, 'YYYY')`、`ADD_MONTHS(列, -3)`
  - 時差を足す: `(列 + NUMTODSINTERVAL(540, 'MINUTE'))`
  - `case when 式 is null then 1 else 0 end`、`case 式 when 'a' then 0 when 'b' then 1 else 2 end` を select と group by の両方に書く
  - select で付けた別名を `order by` に書く (`order by z0, g0 desc`)
  - 丸めた式を `TO_CHAR(TRUNC(列, 'MM'), 'YYYY-MM-DD HH24:MI:SS') as g0` で受け取り、`group by TRUNC(列, 'MM')` と書く (select と group by で式の形が違う)
  - 派生表との結合: `from t inner join (select ... group by ...) c_l_b_sel on (式 = c_l_b_sel.g0 or (式 is null and c_l_b_sel.g0 is null))`
  - 派生表の件数: `select count(*) from (select 1 as c_l_b_g from t group by 式) c_l_b_agg`
  - 派生表の中で上位 N 件に絞る: `(select * from (select ... group by ... order by m0 desc) where ROWNUM <= 10) c_l_b_sel` (並べたあとに ROWNUM で切る形で、順序どおりの上位が得られるか)
- **Q-C6** 副問い合わせ (`where exists (select 1 from ... where ...)`、`from (select ...)`)。
- **Q-C7** `?` と `params`。値は文字列だけか、数値・真偽・null も渡せるか。`?` の個数の上限。`id = ?` に文字列 `"123"` を渡して数値の列と比較できるか。
- **Q-C8** 二重引用符つきの識別子 (`"customer"."companyname"`) は使えるか (いまは外して送っているので、使えないなら「使えない」の確認だけでよい)。
- **Q-C9** SQL 文の中に `OFFSET ... FETCH NEXT` や `ROWNUM` を書けるか (いまは書かず、URL の `limit` / `offset` を使っている)。
- **Q-C10** 文の長さ・結合の数・`IN (...)` の要素数などの上限。

### D. SuiteQL の結果 (REST 経由)

いまの前提: 応答は `{"links":[...],"count":n,"hasMore":bool,"items":[{"links":[],"<列>":"<値>",...}],"offset":n,"totalResults":n}`。
`items` の各行は、値がすべて文字列、値が null の列はキーごと省かれる、真偽は `"T"` / `"F"`、キーは別名を小文字にしたもの。
`limit` は 1000 まで、`offset` は `limit` の倍数。次のページがあるかは `hasMore` で見ている。

- **Q-D1** 応答の実物の例。上の前提と違う点。
- **Q-D2** null の列は本当に省かれるか。空文字はどう返るか。
- **Q-D3** 数値の列 (整数・小数・通貨) は JSON の文字列で返るか、数値で返るか。小数の桁は落ちないか。
- **Q-D4** 行のキーの大文字・小文字。別名 `customer_companyName` を付けたら、キーはどう返るか。
- **Q-D5** 日付・日時の列を `TO_CHAR` せずに選んだときの形 (アカウント・利用者の表示形式に従うのか)。
- **Q-D6** `limit` の上限、`offset` の制約、結果全体の上限 (100,000 行) を超えたときの応答。`totalResults` は常に付くか。
- **Q-D7** 並びを指定しないとき、ページをまたいだ順序は安定するか。

### E. 日時と時刻帯

いまの前提: 書き込みでは日時を UTC の `"2026-03-04T05:06:07Z"` で送る。読み出しでは `TO_CHAR(列, 'YYYY-MM-DD HH24:MI:SS')` の結果を **UTC として** 扱っている。
ここが一番自信が無い。

- **Q-E1** SuiteQL で日時の列を `TO_CHAR` したとき、返る時刻はどの時刻帯か (UTC・アカウントの時刻帯・接続した利用者 (M2M の Entity) の設定・DB の時刻帯)。
- **Q-E2** SuiteQL で UTC の時刻を確実に得る書き方 (`SYS_EXTRACT_UTC`・`AT TIME ZONE`・`TZ_OFFSET` などが使えるか)。
- **Q-E3** SuiteQL の WHERE で日時を比較するとき、リテラルはどの時刻帯として解釈されるか。
- **Q-E4** REST のレコード (GET / POST / PATCH) での日時の形式と時刻帯。`Z` つきの UTC を送ってよいか。日付だけの項目の形式 (`"2026-03-04"`)。
- **Q-E5** 日付だけの項目 (例 `startDate`) が、時刻帯の変換で前後の日にずれる既知の事例はあるか。

### F. REST のレコード名・項目名と SuiteQL の表・列の対応

いまの前提: SuiteQL の表名 = REST のレコード名、SuiteQL の列名 = REST の項目名の小文字。これが成り立つレコードだけを対象にするつもりでいる。

- **Q-F1** 次のレコードで前提が成り立つか。成り立たないなら SuiteQL 側の表名と、絞り込みに要る条件:
  `customer` / `vendor` / `employee` / `contact` / `subsidiary` / `department` / `location` / `classification` / `currency` /
  `inventoryItem` などの品目 / `salesOrder` / `invoice` / `purchaseOrder` / カスタムレコード (`customrecord_xxx`) / カスタムリスト (`customlist_xxx`)。
- **Q-F2** 取引 (salesOrder など) は SuiteQL では `transaction` 表に入ると理解している。REST のレコード名から SuiteQL の表と種別の条件 (`type = 'SalesOrd'` など) を決める公式の対応表はあるか。
- **Q-F3** 項目名が「小文字にしただけ」では一致しない代表例 (REST と SuiteQL で名前が違う項目)。`customer` で 5 つほど。
- **Q-F4** ほかのレコードを参照する項目 (例 `subsidiary`) は、SuiteQL の列では参照先の internal ID が返るか。表示名を得る `BUILTIN.DF(列)` は REST 経由でも使えるか。
- **Q-F5** カスタム項目 (`custentity_xxx`・`custbody_xxx`・`custrecord_xxx`) は、REST と SuiteQL で同じ名前か。
- **Q-F6** 複数選択の項目は、REST のレコードと SuiteQL でそれぞれどう表現されるか。
- **Q-F7** SuiteQL で使える表・列の一覧を API で取る方法は本当に無いか (API-01 では「確認できなかった」としている)。あれば要求と応答の例。
- **Q-F8** SuiteQL を使うのに要るロールの権限 (SuiteAnalytics Workbook など) と、レコードごとの権限が SuiteQL の結果にどう効くか。

### G. レコードの書き込み (REST)

いまの前提: 作成は `POST record/v1/{type}`、成功は 204 で `Location` ヘッダーの最後の区切りが internal ID。
更新は `PATCH record/v1/{type}/{id}` に変えた項目だけ、消すときは JSON の `null`。削除は `DELETE record/v1/{type}/{id}`。
参照の項目は `{"id":"3"}`、真偽は `true` / `false`、数値は JSON の数値、日付は `"2026-03-04"` で送る。
ヘッダー `X-NetSuite-PropertyNameValidation: Error` と `X-NetSuite-PropertyValueValidation: Error` を付けている。
無い ID への PATCH / DELETE は 404 が返ると想定している。

- **Q-G1** 上の前提のうち、公式資料と合わないもの。POST 成功時の応答 (ステータス・`Location` の形) の実物。
- **Q-G2** 2 つの検証ヘッダーの正確な名前と、取りうる値 (`Error` / `Warning` / `Ignore` など。大文字・小文字)。既定値。
- **Q-G3** 選択肢 (enum) の項目の書き込みの形 (`{"id":"_xxx"}` か、文字列そのものか)。
- **Q-G4** 項目を消す (空にする) とき、`null` でよいか。文字列・参照・日付・数値・真偽のそれぞれで。
- **Q-G5** 無い ID への GET / PATCH / DELETE のステータスとエラーコード。
- **Q-G6** 条件つきの更新 (ETag / `If-Match`・バージョン番号) は REST のレコードにあるか。無ければ、同時更新の検出に使える項目 (`lastModifiedDate` など) の精度。
- **Q-G7** 同じ作成要求の二重送信を防ぐ仕組み (冪等キーのヘッダーなど) は同期の POST にあるか。
- **Q-G8** 読み取り専用の項目を PATCH に入れたときの応答 (エラーになるか、無視されるか)。
- **Q-G9** レコードの種別ごとに、REST で作成・更新・削除ができるかどうかを API から知る方法 (メタデータに操作の一覧が出るか)。

### H. エラーと呼び出しの制限

いまの前提: エラーの本文は `{"type":"...","title":"...","status":400,"o:errorDetails":[{"detail":"...","o:errorCode":"...","o:errorPath":"..."}]}`。
429 と 502 / 503 / 504 は時間を置いてやり直す (書き込みは 429 だけ)。401 はトークンを取り直して 1 回やり直す。
書き込みに 503 以外の 5xx が返ったときは「実行されたかどうか分からない」として扱い、やり直さない。

- **Q-H1** エラー本文の実物の例 (400 の入力エラー・401・403・404・429)。
- **Q-H2** 同時実行の上限を超えたときの HTTP ステータス・エラーコード・`Retry-After` ヘッダーの有無。そのとき要求は NetSuite 側で **実行されていない** と言えるか
  (書き込みを安全にやり直せるかの判断に使う)。
- **Q-H5** 書き込み (POST / PATCH / DELETE) に 500 / 502 / 503 / 504 が返ったとき、NetSuite 側で実行されている場合はあるか。
  ステータスごとに「実行されていないと言える / 言えない」を分けてほしい。
- **Q-H3** 1 回の要求のサイズ・処理時間の上限。
- **Q-H4** 利用者の入力エラー (`o:errorPath`) は、項目名がどの形 (REST の項目名・JSON パス) で返るか。明細の行のエラーの形。

### I. 次の段階のための下調べ

次に実装する候補。見せ方は次のように決めてあり、その前提で実装できるかを確かめる資料がほしい。

- 取引のレコード: 利用者が書く表名は常に REST のレコード名 (`salesOrder`)。SuiteQL の表・種別の条件との違いはコネクターが吸収する (Q-F1 / Q-F2)
- 明細・サブレコード: 親のレコードとは別の表 (子のレコード) として見せ、読み出しは SuiteQL、書き込みは親のレコードへの REST 要求にする
- 選択肢: 相手がレコードなら参照 (いまと同じ)。固定の選択肢なら、候補の一覧を読める表として見せる

- **Q-I1** `selectOptions` の要求と応答の実物 (新規・既存それぞれ)。候補が多いときのページング・絞り込みの方法。固定の選択肢の候補を SuiteQL で読めるか (表の名前)。
- **Q-I2** 明細 (サブリスト) の読み出しの形 (`GET record/v1/salesOrder/{id}/item`・`expandSubResources=true`) と、行の追加・変更・削除の要求の形 (keyed / non-keyed の例を 1 つずつ)。
  明細の行を SuiteQL で読むときの表 (`transactionline` など) と、行を 1 つに決めるキー。REST の行のキーと SuiteQL の行のキーは対応するか。
- **Q-I3** 住所などのサブレコードの読み書きの形 (`customer` の `addressBook`)。SuiteQL で読むときの表。

### J. 動作確認の環境

- **Q-J1** NetSuite の動作確認ができる環境を用意する方法と費用・申請の手順 (SuiteCloud Developer Network の開発者アカウント、Sandbox、デモアカウントなど)。
  日本の法人が申し込むときの窓口。
- **Q-J2** その環境で OAuth 2.0 Client Credentials (M2M)・REST Web Services・SuiteQL を有効にする手順 (メニューの場所・要る機能・ロールの権限) を、
  導入手順書にそのまま使える粒度で。
