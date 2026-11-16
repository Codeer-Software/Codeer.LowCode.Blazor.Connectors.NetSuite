# Codeer.LowCode.Blazor.Connectors.NetSuite 公開版レビュー

レビュー日　2026-11-16　／　対象　main の `a9d3991a0a33a2e6b5173f13a01e184054f30923`  
対象範囲　README、Docs/NetSuite.md、Server、Designer、Example（Cookieホスト）、Mock・結合・ブラウザー試験のソース。リンクはすべて確認した版に固定した。[対象コミット][commit]

## 優先順

| 順位 | 観点 | 起こりうること | 最初の変更 |
|---|---|---|---|
| 1 | 明細保存 | 数量を変更しただけでも、別の利用者が後から追加した行が消える | 削除対象を明示し、旧画面と現在の明細の差だけで全置換しない |
| 2 | 明細の参照・集計 | 一つの取引に複数のサブリストがあると、別名の明細表が同じ行集合を返しうる | RESTサブリストとSuiteAnalytics側の行・列を対応付けたものだけ公開 |
| 3 | 一括登録・障害表示 | 先頭の行は登録済みでも、利用者がどこから再開してよいか分からない | 確定した実IDと失敗位置を保持し、結果不明を本番画面に返す |
| 4 | ログインの方式2 | NetSuite従業員をCLBユーザーにする構成をExampleでそのまま試せない | ログイン用のストアにもラップ済みアクセサーを渡す |
| 5 | 業務条件付きの更新・削除 | 承認状態や版を読取後に別の処理が変えると、確認時と異なる状態に更新をかけうる | 厳密な条件付き更新が必要なModuleの対応範囲を明示 |
| 6 | Designerメタ情報の更新 | 更新中断で、最後に使えていた定義ファイルまで読み込めなくなる可能性 | 一時ファイルで検証し、完成後に置換 |

### 1. 明細の削除を「現在のサーバーにだけ存在する行」から推定している

**該当条件**　更新データにサブリスト列を含む場合。Aが行1を開き、Bが行2を追加。Aが行1の数量だけを変え、開いた時点の明細配列を保存する。実装は保存時の最新明細を取得し、提出された配列が含まない行2を見つけると `replace` を指定する。Aが削除を操作していなくても行2が失われうる。サブリスト列を送らない親項目だけのPATCHはこの経路を通らない。仕様には後勝ちが明記されているが、削除操作がなくても起こる点が読み取りづらい。また、すでに削除された行や同じ `line` の二つ目を新規追加として扱うテストもある。[仕様][lastwins]・[最新明細の取得と送信][update]・[削除・新規行の判定][diff]・[既存テスト][stale]

**修正案**　表示時の行キー・元値と、利用者が明示した追加・変更・削除を持つ。追加・既存行の変更は対象行だけを送る。既存行が消えていたら競合、重複キーは入力エラーとする。削除に全置換が必要で、同時変更を確実に検出できない構成では、標準の複数人編集でその操作を無効にするか、整合性を保証できる保存手段を選ぶ。直前の再読込だけでは、その後の変更までは守れない。

**受入試験**　Aの数量変更とBの新規行の両方が残る。Bが削除した行をAが変更しても復活しない。単独利用で明示削除した場合は指定行だけが消える。いずれも、実際の更新本文・URLと保存後の行集合を比較する。

### 2. サブリスト別の仮想表がSuiteQLで同じ行集合に展開される

**該当条件**　同じ取引タイプで二つ以上のサブリストが取り込まれ、明細仮想表が作られる場合。`CreateLineTables` はサブリストごとに表を作り `LineSublist` も保持する。一方、読み取り時は親の取引種別・`mainline='F'`・`taxline='F'` だけで `transactionline` を絞る。ここではサブリスト名を使っていない。そのため異なる仮想表を数えると同じ母集団になりうる。REST側の項目がSuiteAnalyticsの列に対応しなければ、射影時に列エラーになる可能性もある。[仮想表の生成][linetables]・[SuiteQL変換][linequery]・[公開仕様][linehelp]

**修正案**　取引種別・サブリストごとにSuiteAnalyticsの表、行を区別する条件、投影列とキーを定義する。裏付けのない組み合わせでは明細仮想表をDesignerへ提示せず、読み取り可能な親のRESTサブリストとして扱う。

**受入試験**　対象SandboxのRecords Catalogで、同じ取引に共存する2種類の明細を選び、各1件以上を登録。RESTで確認した所属・件数・金額を、それぞれの仮想表の一覧と合計に照合する。実アカウントでどの取引・サブリストがこの条件を満たすかは未確認。

### 3. 部分成功と結果不明を保存画面まで届ける

**一括登録**　`ExecuteAsync` の複数INSERT経路は逐次作成し、各 `InsertCoreAsync` の戻り値である実IDを捨てている。途中で失敗すると先行行は確定済みで、呼出元には成功行の集合が返らない。Exampleは一括INSERTのしきい値を100に設定している。1件ずつ確定すること自体は公開仕様に明記されている。[逐次登録][bulk]・[Exampleのしきい値][bulkexample]・[確定単位の仕様][savehelp]

**本番画面**　コネクターは応答消失を `WriteResultUnknown` に分類する。ExampleのSubmitは `ToClientMessage` を通り、`NetSuiteException` がそのまま渡ると、本番では他のシステム例外と同じ汎用メッセージになる。利用者が登録済みか照合する必要を判断しにくい。書込へのHTTP 503も、実装とテストは再送せず `ServiceUnavailable` とする一方、仕様ではサーバー側エラーを一括して `WriteResultUnknown` と説明している。[HTTP応答の分類][http]・[503の試験][test503]・[例外型][exclass]・[Submit入口][controller]・[表示変換][errors]・[公開仕様][savehelp]

**修正案**　同じSubmit内の行を、確定した実ID・失敗・未実行・結果不明に分ける。通常の `IDbAccessor.ExecuteAsync` は件数しか返せないため、一括経路の対象を制限するか、操作結果を保持する専用経路からSubmit結果へ接続する。本番では安全な定型文と照合用IDを返す。503は、対象処理が始まっていないと根拠をもって判断できる場合を別扱いにし、それ以外は照合を求める。トークン取得段階の障害と、業務書込を送った後の障害を区別する。

**受入試験**　3行中2行目で入力拒否、サーバーで保存後に応答消失、書込への503の3ケースをProduction設定のExampleから実行する。1行目の実ID、照合が必要な行、未送信の行が判別でき、再操作で1行目が二重登録されないことを確認する。

### 4. 従業員をユーザーにする方式2の配線をExampleで完成させる

**現状は文書化済みの制約。** 通常の `ModuleDataIO` はラップ済みのアクセサーを使うが、`ExternalLoginUserResolver` とCookie検証のストア生成には元の `DbAccess` が渡される。方式2で外部ユーザー表を探せない理由はDocsに記載され、対応するSelenium試験は `Ignore`。Mockでストアが使えることと、ホストのログイン画面から使えることを区別したい。[ホストの生成][host]・[ログイン解決][resolver]・[Cookie検証][cookie]・[既知の制約][loginhelp]・[Selenium試験][e2e]・[Mock試験][loginmock]

**修正案**　`DataService` に物理DB用の具象 `DbAccess` と、通常Module・ユーザー表用の `IDbAccessor` を持たせる。後者を `ModuleDataIO` とログインストアの両方に渡す。物理DB用の一時ファイル・`ClearAsync` 等は従来の具象型で扱う。方式2の外部ログインで参照した従業員と、業務APIに設定したM2MのEntity・ロールは別に記載する。[業務APIの実行主体][role]

**受入試験**　通常のログイン画面経由で登録済みと未登録従業員を試し、方式2のIgnoreを外す。合わせて従来のローカルユーザー方式と、パスワード列／SessionStampを持たない従業員モジュールの仕様を確かめる。

### 5. ID以外のWHERE条件を保証できる操作範囲を明示する

追加WHEREを含むUPDATE・DELETEは `ExistsAsync` で先に件数を調べ、その後にID指定のREST書込を呼ぶ。実装コメントにも確認から書込までの競合は防げないと記載されている。例えば「未承認の間だけ編集可」の条件を検査した直後に承認された場合、古い業務条件に基づいた更新が起こりうる。通常のID指定更新まで同じ制約とみなす必要はない。[更新・削除経路][writes]・[事前確認][exists]

**修正案と受入試験**　Designerまたは仕様に、ベストエフォートの事前確認と、保存時点の保証を必要とするModuleの違いを明記する。保証を必要とする場合はサーバー側で条件と変更を一体として扱う検証済みの処理を選択するか、編集機能を利用不可にする。通常の承認・編集を並行して、業務で必要な順序と結果をSandboxで確かめる。

### 6. メタ情報を完成後に置換する

Toolsからの更新は最終パスへ直接 `WriteAllTextAsync` し、Designerは同じファイルを読み込む。書込中断・同時更新の際には、正しく使えていた定義を壊す可能性がある。メニュー処理には同一接続の多重起動を抑える処理が見当たらない。[ファイルへの書込][metawrite]・[読込とキャッシュ][metaread]・[メニュー][menu]

**修正案と受入試験**　同一接続の更新を直列化する。同じディレクトリに仮ファイルを書き、再読込で検証後に置換してキャッシュを更新する。更新中断・二重クリック・オフライン再起動の後、旧版か新版の完全な定義をGUIとCLIが開けることを試験する。

## FileMaker版に採用する点

既存のDbAccess設定から通常Moduleを組み立てる方法、ServerとDesigner双方のアクセサーの包み方、手動更新した定義をオフラインで利用する方法、Mock・一般のModule操作・GUIの層にわたる試験構成を採用する。API実行主体、条件付き保存、ポータル明細の所属・削除、結果不明の伝え方はFileMakerの契約に合わせた。[FileMakerコネクター仕様](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.FileMaker/blob/main/Docs/02_filemaker_connector_spec.md)

**検証範囲**　コードと仕様の静的レビュー。今回の環境には .NET SDK がなく、リポジトリのテストは実行していない。NetSuiteの実アカウント・FileMakerの実ファイルによる動作確認も未実施。「起こりうる」はコード上の条件付き指摘で、顧客環境の障害発生を示すものではない。[リポジトリの実機確認状況][readme]

[commit]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/commit/a9d3991a0a33a2e6b5173f13a01e184054f30923
[readme]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/README.md#L12
[lastwins]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Docs/NetSuite.md#L184
[update]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs#L329-L363
[diff]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteSublistJson.cs#L109-L144
[stale]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Test/MockSublistTest.cs#L405-L425
[linetables]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteRecordDefinition.cs#L64-L85
[linequery]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/SuiteQlTranslator.cs#L522-L530
[linehelp]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Docs/NetSuite.md#L194-L210
[bulk]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs#L261-L264
[bulkexample]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Infrastructure/CustomizedModuleDataIO.cs#L19
[savehelp]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Docs/NetSuite.md#L314-L319
[http]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteConnection.cs#L313-L351
[test503]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Test/NetSuiteConnectionTest.cs#L327-L333
[exclass]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteException.cs#L26-L31
[controller]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Controllers/ModuleDataController.cs#L63-L64
[errors]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Infrastructure/ExceptionHandlerUtils.cs#L32-L54
[host]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Infrastructure/DataService.cs#L20-L33
[resolver]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Infrastructure/ExternalLoginUserResolver.cs#L24-L28
[cookie]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Example/LowCodeApp.Server/Infrastructure/CookieAuthentication.cs#L43
[loginhelp]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Docs/NetSuite.md#L288-L292
[e2e]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest/Scenario/NetSuiteLoginTest.cs#L135-L143
[loginmock]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Test/MockLoginAccountTest.cs#L36-L50
[role]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Docs/NetSuite.md#L25-L32
[writes]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs#L243-L259
[exists]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteDbAccessor.cs#L293-L300
[metawrite]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteMetadataFile.cs#L59-L73
[metaread]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Server/NetSuiteMetadataFile.cs#L93-L108
[menu]: https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Connectors.NetSuite/blob/a9d3991a0a33a2e6b5173f13a01e184054f30923/Source/Codeer.LowCode.Blazor.Connectors.NetSuite.Designer/NetSuiteDesignerInitializer.cs#L66-L86
