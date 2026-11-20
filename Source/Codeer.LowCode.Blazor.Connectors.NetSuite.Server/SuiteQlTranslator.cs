using System.Globalization;
using System.Text;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    enum SqlTokenKind
    {
        Space,
        Word,
        QuotedIdentifier,
        StringLiteral,
        Parameter,
        Number,
        Symbol,
    }

    record SqlToken(SqlTokenKind Kind, string Text)
    {
        public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;
        /// <summary>識別子 (引用符つき・なし)。</summary>
        public bool IsIdentifier => Kind == SqlTokenKind.QuotedIdentifier || Kind == SqlTokenKind.Word;
    }

    /// <summary>読み出しの結果の 1 列。</summary>
    class SuiteQlResultColumn
    {
        /// <summary>本体に返す行のキー (本体が書いた別名のまま)。</summary>
        public string Key { get; init; } = string.Empty;
        /// <summary>SuiteQL の結果での列名。</summary>
        public string SourceName { get; init; } = string.Empty;
        /// <summary>結果の種類。レコードの項目ならその種類、最小・最大・日付の丸めの式は中の項目から決まる種類。それ以外の式は null。</summary>
        public NetSuiteFieldKind? Kind { get; init; }
        /// <summary>件数・合計などの集計の式。</summary>
        public bool IsNumericExpression { get; init; }
        /// <summary>明細の列 (<see cref="NetSuiteFieldKind.Sublist"/>) の定義。SuiteQL では読まず、行ごとに REST で読む。</summary>
        public NetSuiteFieldDefinition? Sublist { get; init; }
    }

    class SuiteQlSelect
    {
        public string Sql { get; init; } = string.Empty;
        public List<string> Parameters { get; init; } = new();
        /// <summary>読む行数 (本体がページングを付けたとき)。null なら全部。</summary>
        public int? Limit { get; init; }
        public int Offset { get; init; }
        /// <summary>SuiteQL の結果から返す列 (明細の列は除く)。</summary>
        public List<SuiteQlResultColumn> Columns { get; init; } = new();
        /// <summary>明細の列。行ごとに <see cref="Table"/> のレコードを REST で読んで値にする。</summary>
        public List<SuiteQlResultColumn> Sublists { get; init; } = new();
        /// <summary>添付ファイルの仮想の列 (<see cref="NetSuiteFieldKind.Attachments"/>)。読んだ行の全部を 1 回の保存検索で引き当てる。</summary>
        public List<SuiteQlResultColumn> Attachments { get; init; } = new();
        /// <summary>主表のレコード名 (REST のレコード名)。</summary>
        public string Table { get; init; } = string.Empty;
        /// <summary>SuiteQL の結果での主表の internal ID の列名 (明細・添付の列があるとき)。</summary>
        public string IdSourceName { get; init; } = string.Empty;
    }

    class RecordInsert
    {
        public string Table { get; init; } = string.Empty;
        /// <summary>列名 → パラメータ名。</summary>
        public List<(string Column, string Parameter)> Values { get; } = new();
        /// <summary>採番された Id を入れて返すパラメータ名 (無ければ空)。</summary>
        public string ReturningParameter { get; set; } = string.Empty;
    }

    class RecordUpdate
    {
        public string Table { get; init; } = string.Empty;
        public List<(string Column, string Parameter)> Values { get; } = new();
        public RecordWhere Where { get; set; } = new();
    }

    class RecordDelete
    {
        public string Table { get; init; } = string.Empty;
        public RecordWhere Where { get; set; } = new();
    }

    class RecordWhere
    {
        /// <summary>対象のレコードの Id を持つパラメータ名。</summary>
        public string IdParameter { get; set; } = string.Empty;
        /// <summary>Id の一致のほかに条件があるか (論理削除・楽観ロックなど)。</summary>
        public bool HasOtherConditions { get; set; }
        /// <summary>where より後ろの SQL (本体が書いたまま)。</summary>
        public string Sql { get; set; } = string.Empty;
    }

    /// <summary>
    /// 本体 (Codeer.LowCode.Blazor) が Oracle 向けに組み立てた SQL を、NetSuite の操作に読み替える。
    /// 読み出しは SuiteQL に、書き込みは REST のレコード操作 (対象のレコード・項目・値) に。本体が組み立てる形だけを受け付け、それ以外は拒否する。
    /// </summary>
    static class SuiteQlTranslator
    {
        const string DateFormat = "YYYY-MM-DD";
        const string DateTimeFormat = "YYYY-MM-DD HH24:MI:SS";
        internal const string DateFormatNet = "yyyy-MM-dd";
        internal const string DateTimeFormatNet = "yyyy-MM-dd HH:mm:ss";

        internal static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (!(char.IsAsciiLetter(text[0]) || text[0] == '_')) return false;
            return text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        }

        // ---------------------------------------------------------------- 字句

        internal static List<SqlToken> Tokenize(string sql)
        {
            var tokens = new List<SqlToken>();
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c))
                {
                    while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                    tokens.Add(new(SqlTokenKind.Space, " "));
                }
                else if (c == '"')
                {
                    var end = sql.IndexOf('"', i + 1);
                    if (end < 0) throw NotSupported(sql);
                    tokens.Add(new(SqlTokenKind.QuotedIdentifier, sql.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                }
                else if (c == '\'')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (sql.Length <= i) throw NotSupported(sql);
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(sql[i++]);
                    }
                    tokens.Add(new(SqlTokenKind.StringLiteral, sb.ToString()));
                }
                else if (c == ':' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    var start = i++;
                    while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    tokens.Add(new(SqlTokenKind.Parameter, sql[start..i]));
                }
                else if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.')) i++;
                    tokens.Add(new(SqlTokenKind.Number, sql[start..i]));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new(SqlTokenKind.Word, sql[start..i]));
                }
                else
                {
                    var two = i + 1 < sql.Length ? sql.Substring(i, 2) : string.Empty;
                    if (two is "<=" or ">=" or "<>" or "!=" or "||")
                    {
                        tokens.Add(new(SqlTokenKind.Symbol, two));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new(SqlTokenKind.Symbol, c.ToString()));
                        i++;
                    }
                }
            }
            return tokens;
        }

        static List<SqlToken> WithoutSpaces(IEnumerable<SqlToken> tokens) => tokens.Where(e => e.Kind != SqlTokenKind.Space).ToList();

        static NetSuiteException NotSupported(string sql)
            => new("This operation is not supported for a NetSuite data source: " + Shorten(sql));

        static string Shorten(string sql)
        {
            var text = string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 200 ? text : text[..200] + "...";
        }

        static string IdentifierText(SqlToken token, string sql)
        {
            if (!token.IsIdentifier || !IsIdentifier(token.Text)) throw NotSupported(sql);
            return token.Text;
        }

        // ---------------------------------------------------------------- 読み出し

        internal static bool IsSelect(string sql)
            => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("select") == true;

        /// <param name="getDefinition">表 (レコード・明細の表) の定義。無い表はエラーにする。</param>
        internal static async Task<SuiteQlSelect> TranslateSelectAsync(string sql, IReadOnlyDictionary<string, object?> args,
            Func<string, Task<NetSuiteRecordDefinition>> getDefinition)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            if (tokens.Count == 0 || !tokens[0].IsWord("select")) throw NotSupported(sql);
            if (tokens.Any(e => e.IsSymbol(";"))) throw NotSupported(sql);

            //本体が付けたページング (offset N rows fetch next M rows only) は SuiteQL に書けないので外し、要求の limit / offset にする
            int? limit = null;
            var offset = 0;
            if (8 <= tokens.Count)
            {
                var t = tokens.GetRange(tokens.Count - 8, 8);
                if (t[0].IsWord("offset") && t[1].Kind == SqlTokenKind.Number && t[2].IsWord("rows") && t[3].IsWord("fetch") && t[4].IsWord("next") &&
                    t[5].Kind == SqlTokenKind.Number && t[6].IsWord("rows") && t[7].IsWord("only"))
                {
                    offset = int.Parse(t[1].Text, CultureInfo.InvariantCulture);
                    limit = int.Parse(t[5].Text, CultureInfo.InvariantCulture);
                    tokens.RemoveRange(tokens.Count - 8, 8);
                }
            }

            //派生表の中のページング (集計の「上位 N 件のグループだけ」) は URL に出せないので、ROWNUM で絞る形にする:
            //(select ... order by ... offset 0 rows fetch next N rows only) → (select * from (select ... order by ...) where ROWNUM <= N)
            for (var i = 0; i + 7 < tokens.Count; i++)
            {
                if (!(tokens[i].IsWord("offset") && tokens[i + 1].Kind == SqlTokenKind.Number && tokens[i + 2].IsWord("rows") && tokens[i + 3].IsWord("fetch") &&
                      tokens[i + 4].IsWord("next") && tokens[i + 5].Kind == SqlTokenKind.Number && tokens[i + 6].IsWord("rows") && tokens[i + 7].IsWord("only"))) continue;
                var open = -1;
                for (int k = i - 1, nest = 0; 0 <= k; k--)
                {
                    if (tokens[k].IsSymbol(")")) nest++;
                    else if (tokens[k].IsSymbol("("))
                    {
                        if (nest == 0) { open = k; break; }
                        nest--;
                    }
                }
                if (open < 0 || tokens[i + 1].Text != "0" || tokens.Count <= i + 8 || !tokens[i + 8].IsSymbol(")")) throw NotSupported(sql);
                var count = tokens[i + 5];
                tokens.RemoveRange(i, 8);
                tokens.InsertRange(i, new SqlToken[]
                {
                    new(SqlTokenKind.Symbol, ")"), new(SqlTokenKind.Word, "where"), new(SqlTokenKind.Word, "ROWNUM"), new(SqlTokenKind.Symbol, "<="), count,
                });
                tokens.InsertRange(open + 1, new SqlToken[]
                {
                    new(SqlTokenKind.Word, "select"), new(SqlTokenKind.Symbol, "*"), new(SqlTokenKind.Word, "from"), new(SqlTokenKind.Symbol, "("),
                });
            }

            //どの深さでも、読む表は定義があるもの (メタ情報のファイルにあるレコードと明細の表) だけ。無ければ getDefinition がエラーにする
            var depth = 0;
            var topFrom = -1;
            var topGroupBy = -1;
            var topOrderBy = -1;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")")) depth--;
                else if ((tokens[i].IsWord("from") || tokens[i].IsWord("join")) && i + 1 < tokens.Count && !tokens[i + 1].IsSymbol("("))
                {
                    await getDefinition(IdentifierText(tokens[i + 1], sql));
                }
                if (depth != 0) continue;
                if (tokens[i].IsWord("from") && topFrom < 0) topFrom = i;
                if (tokens[i].IsWord("group") && i + 1 < tokens.Count && tokens[i + 1].IsWord("by")) topGroupBy = i;
                if (tokens[i].IsWord("order") && i + 1 < tokens.Count && tokens[i + 1].IsWord("by")) topOrderBy = i;
            }
            if (topFrom < 0) throw NotSupported(sql);

            //from / join の別名 → レコード (一番外の階層)。allAliases はどの階層かを問わない (明細の列の使われ方を確かめる)
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mainAlias = string.Empty;
            depth = 0;
            for (var i = topFrom; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")")) depth--;
                if (!(tokens[i].IsWord("from") || tokens[i].IsWord("join"))) continue;
                if (tokens.Count <= i + 1 || tokens[i + 1].IsSymbol("(")) continue;
                var (table, alias, _) = ReadTable(tokens, i, sql);
                allAliases[alias] = table;
                if (depth != 0) continue;
                aliases[alias] = table;
                if (tokens[i].IsWord("from") && string.IsNullOrEmpty(mainAlias)) mainAlias = alias;
            }

            //select の項目
            var columns = new List<SuiteQlResultColumn>();
            var select = new List<List<SqlToken>>();
            {
                var item = new List<SqlToken>();
                depth = 0;
                for (var i = 1; i < topFrom; i++)
                {
                    if (tokens[i].IsSymbol("(")) depth++;
                    else if (tokens[i].IsSymbol(")")) depth--;
                    if (depth == 0 && tokens[i].IsSymbol(","))
                    {
                        select.Add(item);
                        item = new();
                        continue;
                    }
                    item.Add(tokens[i]);
                }
                select.Add(item);
            }

            var body = new List<SqlToken>();
            body.Add(new(SqlTokenKind.Word, "select"));
            var sublists = new List<SuiteQlResultColumn>();
            var attachments = new List<SuiteQlResultColumn>();
            var idSourceName = string.Empty;
            var emitted = 0;
            for (var index = 0; index < select.Count; index++)
            {
                var item = select[index];
                if (item.Count == 0) throw NotSupported(sql);

                //別名
                var alias = string.Empty;
                var expression = item;
                if (3 <= item.Count && item[^2].IsWord("as") && item[^1].IsIdentifier)
                {
                    alias = IdentifierText(item[^1], sql);
                    expression = item.GetRange(0, item.Count - 2);
                }
                //別名が無い項目は、本体が書いた式をそのままキーにする
                var key = string.IsNullOrEmpty(alias) ? ToSql(expression, null, null) : alias;
                var sourceName = string.IsNullOrEmpty(alias) ? $"nsx{index}" : alias;

                //表.列 ならレコードの項目
                NetSuiteFieldKind? kind = null;
                if (expression.Count == 3 && expression[0].IsIdentifier && expression[1].IsSymbol(".") && expression[2].IsIdentifier &&
                    aliases.TryGetValue(expression[0].Text, out var table))
                {
                    var definition = await getDefinition(table);
                    var field = definition.FindByColumn(expression[2].Text);
                    if (field == null) throw new NetSuiteException($"Record type '{table}' has no field '{expression[2].Text}'.");
                    kind = field.Kind;
                    var isMain = string.Equals(expression[0].Text, mainAlias, StringComparison.OrdinalIgnoreCase);
                    if (kind == NetSuiteFieldKind.Sublist)
                    {
                        //明細は SuiteQL では読まず、行ごとにレコードを REST で読む。読めるのは主表の明細だけ
                        if (!isMain) throw SublistNotSupported(table, field);
                        sublists.Add(new SuiteQlResultColumn { Key = key, Kind = kind, Sublist = field });
                        continue;
                    }
                    if (kind == NetSuiteFieldKind.Attachments)
                    {
                        //添付は SuiteQL では読まず、読んだ行の internal ID で保存検索の結果に引き当てる。読めるのは主表の添付だけ
                        if (!isMain) throw SublistNotSupported(table, field);
                        attachments.Add(new SuiteQlResultColumn { Key = key, Kind = kind });
                        continue;
                    }
                    if (isMain && field.Column == "id" && string.IsNullOrEmpty(idSourceName)) idSourceName = sourceName;
                }
                else
                {
                    await CheckNoSublistAsync(expression, allAliases, getDefinition);
                }
                var isNumericExpression = kind == null && expression.Count != 0 &&
                    (expression[0].IsWord("count") || expression[0].IsWord("sum") || expression[0].IsWord("avg"));
                if (kind == null && !isNumericExpression) kind = await GetExpressionKindAsync(expression, aliases, getDefinition);

                if (emitted++ != 0) body.Add(new(SqlTokenKind.Symbol, ","));
                columns.Add(new SuiteQlResultColumn
                {
                    Key = key,
                    SourceName = sourceName,
                    Kind = kind,
                    IsNumericExpression = isNumericExpression,
                });

                //日付・日時は、アカウントの表示形式に左右されないよう決まった形の文字にして受け取る
                if (kind == NetSuiteFieldKind.Date || kind == NetSuiteFieldKind.DateTime)
                {
                    body.Add(new(SqlTokenKind.Word, "TO_CHAR"));
                    body.Add(new(SqlTokenKind.Symbol, "("));
                    body.AddRange(expression);
                    body.Add(new(SqlTokenKind.Symbol, ","));
                    body.Add(new(SqlTokenKind.StringLiteral, kind == NetSuiteFieldKind.Date ? DateFormat : DateTimeFormat));
                    body.Add(new(SqlTokenKind.Symbol, ")"));
                }
                else
                {
                    body.AddRange(expression);
                }
                body.Add(new(SqlTokenKind.Word, "as"));
                body.Add(new(SqlTokenKind.Word, sourceName));
            }
            //明細の列は、主表の列として select に書かれたときだけ読める (条件・並び・集計・結合先には使えない)
            await CheckNoSublistAsync(tokens.GetRange(topFrom, tokens.Count - topFrom), allAliases, getDefinition);
            //明細・添付を読むには行の internal ID が要る。本体が select していなければ足す (結果としては返さない)
            if ((sublists.Count != 0 || attachments.Count != 0) && string.IsNullOrEmpty(idSourceName))
            {
                idSourceName = "nsxid";
                if (emitted++ != 0) body.Add(new(SqlTokenKind.Symbol, ","));
                body.AddRange(new SqlToken[]
                {
                    new(SqlTokenKind.Word, mainAlias), new(SqlTokenKind.Symbol, "."), new(SqlTokenKind.Word, "id"),
                    new(SqlTokenKind.Word, "as"), new(SqlTokenKind.Word, idSourceName),
                });
            }
            body.AddRange(tokens.GetRange(topFrom, tokens.Count - topFrom));

            //何回かに分けて読んでも順序が変わらないよう、並びの最後に行を一意に指す列 (internal ID・明細の表は uniquekey) を足す (件数・集計には足さない)
            var keyColumn = aliases.TryGetValue(mainAlias, out var mainTable) ? (await getDefinition(mainTable)).KeyColumn : "id";
            if (topGroupBy < 0 && !columns.Any(e => e.IsNumericExpression) && !string.IsNullOrEmpty(mainTable) &&
                (await getDefinition(mainTable)).FindByColumn(keyColumn) != null)
            {
                var orderBy = topOrderBy < 0 ? new List<SqlToken>() : tokens.GetRange(topOrderBy, tokens.Count - topOrderBy);
                var hasId = false;
                for (var i = 0; i + 2 < orderBy.Count; i++)
                {
                    if (string.Equals(orderBy[i].Text, mainAlias, StringComparison.OrdinalIgnoreCase) && orderBy[i + 1].IsSymbol(".") &&
                        string.Equals(orderBy[i + 2].Text, keyColumn, StringComparison.OrdinalIgnoreCase)) hasId = true;
                }
                if (!hasId)
                {
                    if (topOrderBy < 0)
                    {
                        body.Add(new(SqlTokenKind.Word, "order"));
                        body.Add(new(SqlTokenKind.Word, "by"));
                    }
                    else
                    {
                        body.Add(new(SqlTokenKind.Symbol, ","));
                    }
                    body.Add(new(SqlTokenKind.Word, mainAlias));
                    body.Add(new(SqlTokenKind.Symbol, "."));
                    body.Add(new(SqlTokenKind.Word, keyColumn));
                }
            }

            //取引のレコードは SuiteQL では transaction 表の一部、明細の表は transactionline 表の一部
            var lineTables = new Dictionary<string, NetSuiteRecordDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in allAliases.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var definition = await getDefinition(table);
                if (definition.IsLineTable) lineTables[table] = definition;
            }
            body = ReplaceTransactionTables(body, sql, lineTables);

            var parameters = new List<string>();
            return new SuiteQlSelect
            {
                Sql = ToSql(body, args, parameters),
                Parameters = parameters,
                Limit = limit,
                Offset = offset,
                Columns = columns,
                Sublists = sublists,
                Attachments = attachments,
                Table = aliases.TryGetValue(mainAlias, out var main) ? main : string.Empty,
                IdSourceName = idSourceName,
            };
        }

        //from / join の後ろの表と別名。next は別名の次の位置
        static (string Table, string Alias, int Next) ReadTable(List<SqlToken> tokens, int index, string sql)
        {
            var table = IdentifierText(tokens[index + 1], sql);
            var alias = table;
            var next = index + 2;
            if (next < tokens.Count && tokens[next].IsWord("as")) next++;
            if (next < tokens.Count && tokens[next].IsIdentifier && !IsClauseWord(tokens[next]))
            {
                alias = IdentifierText(tokens[next], sql);
                next++;
            }
            return (table, alias, next);
        }

        static NetSuiteException SublistNotSupported(string table, NetSuiteFieldDefinition field)
            => new($"The {(field.Kind == NetSuiteFieldKind.Attachments ? "attachments" : "sublist")} column '{field.Column}' of record type '{table}' can only be read as a column of the main record. " +
                "It cannot be used in a condition, a sort, an aggregate or a joined record.");

        //式・条件の中に明細の列 (別名.列) があれば例外
        static async Task CheckNoSublistAsync(List<SqlToken> tokens, Dictionary<string, string> aliases, Func<string, Task<NetSuiteRecordDefinition>> getDefinition)
        {
            for (var i = 0; i + 2 < tokens.Count; i++)
            {
                if (!tokens[i].IsIdentifier || !tokens[i + 1].IsSymbol(".") || !tokens[i + 2].IsIdentifier) continue;
                if (!aliases.TryGetValue(tokens[i].Text, out var table)) continue;
                var field = (await getDefinition(table)).FindByColumn(tokens[i + 2].Text);
                if (field?.Kind is NetSuiteFieldKind.Sublist or NetSuiteFieldKind.Attachments) throw SublistNotSupported(table, field);
            }
        }

        // ---------------------------------------------------------------- 取引のレコード

        //REST のレコード名 → SuiteQL の transaction 表の type の値
        static readonly Dictionary<string, string> _transactionTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["salesorder"] = "SalesOrd",
            ["invoice"] = "CustInvc",
            ["purchaseorder"] = "PurchOrd",
            ["estimate"] = "Estimate",
            ["journalentry"] = "Journal",
            ["creditmemo"] = "CustCred",
            ["vendorbill"] = "VendBill",
            ["cashsale"] = "CashSale",
            ["customerpayment"] = "CustPymt",
            ["vendorpayment"] = "VendPymt",
            ["itemfulfillment"] = "ItemShip",
            ["itemreceipt"] = "ItemRcpt",
            ["returnauthorization"] = "RtnAuth",
            ["vendorcredit"] = "VendCred",
            ["customerdeposit"] = "CustDep",
            ["opportunity"] = "Opprtnty",
            //2026-11-20 に足したもの (種別の値は公開の記事の一覧。REST のレコード名との対応は実物で確かめていない)
            ["transferorder"] = "TrnfrOrd",
            ["workorder"] = "WorkOrd",
            ["workorderissue"] = "WOIssue",
            ["workordercompletion"] = "WOCompl",
            ["workorderclose"] = "WOClose",
            ["assemblybuild"] = "Build",
            ["assemblyunbuild"] = "Unbuild",
            ["inventoryadjustment"] = "InvAdjst",
            ["inventorytransfer"] = "InvTrnfr",
            ["inventorycount"] = "InvCount",
            ["bintransfer"] = "BinTrnfr",
            ["binworksheet"] = "BinWksht",
            ["expensereport"] = "ExpRept",
            ["customerrefund"] = "CustRfnd",
            ["depositapplication"] = "DepAppl",
            ["deposit"] = "Deposit",
            ["check"] = "Check",
            ["creditcardcharge"] = "CardChrg",
            ["vendorreturnauthorization"] = "VendAuth",
            ["purchaserequisition"] = "PurchReq",
            ["blanketpurchaseorder"] = "BlankOrd",
            ["purchasecontract"] = "PurchCon",
            ["requestforquote"] = "Rfq",
            ["vendorprepayment"] = "VPrep",
            ["vendorprepaymentapplication"] = "VPrepApp",
        };

        /// <summary>取引のレコードなら transaction 表の type の値。取引でなければ null。</summary>
        internal static string? GetTransactionType(string recordType) => _transactionTypes.TryGetValue(recordType, out var type) ? type : null;

        //取引 → transactionline の行 (mainline・taxline でない行) がそのまま行になる明細の REST 名。
        //品目の明細と経費の明細の両方を持つ取引 (発注・仕入請求など) は、transactionline で 2 つを分ける条件を確かめていないので入れない。
        //仕訳 (journalentry) は mainline の付き方を確かめていないので入れない
        static readonly Dictionary<string, string> _lineSublists = new(StringComparer.OrdinalIgnoreCase)
        {
            ["salesorder"] = "item",
            ["invoice"] = "item",
            ["estimate"] = "item",
            ["creditmemo"] = "item",
            ["cashsale"] = "item",
            ["returnauthorization"] = "item",
            ["opportunity"] = "item",
            ["itemfulfillment"] = "item",
            ["itemreceipt"] = "item",
            ["transferorder"] = "item",
        };

        /// <summary>明細の表を作る明細 (transactionline の行がその明細の行になるもの) の REST 名。無ければ null。</summary>
        internal static string? GetLineSublist(string recordType)
            => GetTransactionType(recordType) != null && _lineSublists.TryGetValue(recordType, out var sublist) ? sublist : null;

        //from / join の取引のレコードを (select * from transaction where type = '<種別>') <別名> にする (別名が無ければ表名を別名に)。
        //明細の表は (select tl.* from transactionline tl inner join transaction t on t.id = tl.transaction where t.type = '<種別>' and tl.mainline = 'F' and tl.taxline = 'F') <別名>
        static List<SqlToken> ReplaceTransactionTables(List<SqlToken> tokens, string sql, IReadOnlyDictionary<string, NetSuiteRecordDefinition> lineTables)
        {
            var result = new List<SqlToken>();
            for (var i = 0; i < tokens.Count; i++)
            {
                result.Add(tokens[i]);
                if (!(tokens[i].IsWord("from") || tokens[i].IsWord("join")) || tokens.Count <= i + 1 || tokens[i + 1].IsSymbol("(")) continue;
                var (table, alias, next) = ReadTable(tokens, i, sql);
                if (lineTables.TryGetValue(table, out var line))
                {
                    //種別は組み込みの対応表の値 (利用者の文字列ではない)
                    var lineType = GetTransactionType(line.LineParent!)!;
                    result.AddRange(WithoutSpaces(Tokenize(
                        $"(select tl.* from transactionline tl inner join transaction t on t.id = tl.transaction where t.type = '{lineType}' and tl.mainline = 'F' and tl.taxline = 'F')")));
                    result.Add(new(SqlTokenKind.Word, alias));
                    i = next - 1;
                    continue;
                }
                var type = GetTransactionType(table);
                if (type == null) continue;
                result.AddRange(new SqlToken[]
                {
                    new(SqlTokenKind.Symbol, "("), new(SqlTokenKind.Word, "select"), new(SqlTokenKind.Symbol, "*"), new(SqlTokenKind.Word, "from"),
                    new(SqlTokenKind.Word, "transaction"), new(SqlTokenKind.Word, "where"), new(SqlTokenKind.Word, "type"), new(SqlTokenKind.Symbol, "="),
                    new(SqlTokenKind.StringLiteral, type), new(SqlTokenKind.Symbol, ")"), new(SqlTokenKind.Word, alias),
                });
                i = next - 1;
            }
            return result;
        }

        /// <summary>
        /// 集計で本体が書く式の、結果の種類。最小・最大は中の項目と同じ種類、日付の丸め (TRUNC / ADD_MONTHS / 時差を足す式) は日時。
        /// それ以外 (case 式など) は null = 文字のまま返す。
        /// </summary>
        static async Task<NetSuiteFieldKind?> GetExpressionKindAsync(List<SqlToken> expression, Dictionary<string, string> aliases,
            Func<string, Task<NetSuiteRecordDefinition>> getDefinition)
        {
            if (expression.Count == 0) return null;
            var isMinMax = expression[0].IsWord("min") || expression[0].IsWord("max");
            var isDateRounding = expression[0].IsWord("trunc") || expression[0].IsWord("add_months") || expression[0].IsSymbol("(");
            if (!isMinMax && !isDateRounding) return null;

            //式の中の最初の項目
            NetSuiteFieldKind? fieldKind = null;
            for (var i = 0; i + 2 < expression.Count; i++)
            {
                if (!expression[i].IsIdentifier || !expression[i + 1].IsSymbol(".") || !expression[i + 2].IsIdentifier) continue;
                if (!aliases.TryGetValue(expression[i].Text, out var table)) continue;
                fieldKind = (await getDefinition(table)).FindByColumn(expression[i + 2].Text)?.Kind;
                break;
            }
            var isDate = fieldKind == NetSuiteFieldKind.Date || fieldKind == NetSuiteFieldKind.DateTime;
            if (isMinMax && !expression.Skip(1).Any(e => e.IsWord("trunc") || e.IsWord("add_months") || e.IsWord("numtodsinterval"))) return fieldKind;
            //丸めた結果は時刻まで持つことがある (時間単位) ので、日付の項目でも日時として受け取る
            return isDate ? NetSuiteFieldKind.DateTime : null;
        }

        static bool IsClauseWord(SqlToken token)
            => token.Kind == SqlTokenKind.Word &&
               (token.IsWord("where") || token.IsWord("on") || token.IsWord("left") || token.IsWord("right") || token.IsWord("inner") || token.IsWord("join") ||
                token.IsWord("group") || token.IsWord("order") || token.IsWord("having") || token.IsWord("union") || token.IsWord("full") || token.IsWord("cross"));

        /// <summary>
        /// SuiteQL の文字にする。引用符つきの識別子は引用符を外し (名前は英数字と _ だけ)、パラメータは値を入れる:
        /// 文字は ? (値は parameters へ)、数値・真偽・日付は決まった書式のリテラル。args が null なら式の文字だけを作る。
        /// </summary>
        static string ToSql(List<SqlToken> tokens, IReadOnlyDictionary<string, object?>? args, List<string>? parameters)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var text = token.Kind switch
                {
                    SqlTokenKind.QuotedIdentifier => IsIdentifier(token.Text) ? token.Text : throw new NetSuiteException($"'{token.Text}' cannot be used as a name for a NetSuite data source."),
                    SqlTokenKind.StringLiteral => "'" + token.Text.Replace("'", "''") + "'",
                    SqlTokenKind.Parameter => args == null ? token.Text : ParameterToSql(tokens, i, args, parameters!),
                    _ => token.Text,
                };
                if (sb.Length != 0 && NeedsSpace(tokens[i - 1], token)) sb.Append(' ');
                sb.Append(text);
            }
            return sb.ToString();
        }

        static bool NeedsSpace(SqlToken previous, SqlToken current)
        {
            if (current.IsSymbol(".") || previous.IsSymbol(".")) return false;
            if (current.IsSymbol(",") || current.IsSymbol(")")) return false;
            if (previous.IsSymbol("(")) return false;
            if (current.IsSymbol("(") && previous.Kind == SqlTokenKind.Word && !IsKeywordBeforeParenthesis(previous)) return false;
            return true;
        }

        static bool IsKeywordBeforeParenthesis(SqlToken token)
            => token.IsWord("and") || token.IsWord("or") || token.IsWord("not") || token.IsWord("in") || token.IsWord("exists") || token.IsWord("where") ||
               token.IsWord("on") || token.IsWord("from") || token.IsWord("join") || token.IsWord("select") || token.IsWord("values") || token.IsWord("by") ||
               token.IsWord("then") || token.IsWord("else") || token.IsWord("when") || token.IsWord("having") || token.IsWord("like") || token.IsWord("as");

        static string ParameterToSql(List<SqlToken> tokens, int index, IReadOnlyDictionary<string, object?> args, List<string> parameters)
        {
            var name = tokens[index].Text;
            if (!args.TryGetValue(name, out var value)) throw new NetSuiteException($"The value of parameter '{name}' is missing.");

            //like ... escape <文字>: 本体のエスケープ文字 (制御文字) を \ に置き換える
            var afterEscape = 0 < index && tokens[index - 1].IsWord("escape");
            if (afterEscape) return @"'\'";
            var afterLike = 0 < index && tokens[index - 1].IsWord("like");
            var hasEscape = index + 1 < tokens.Count && tokens[index + 1].IsWord("escape");
            if (afterLike && hasEscape && value is string pattern)
            {
                var escape = index + 2 < tokens.Count && args.TryGetValue(tokens[index + 2].Text, out var e) ? e?.ToString() ?? string.Empty : string.Empty;
                if (escape.Length == 1 && escape != @"\") value = pattern.Replace(@"\", @"\\").Replace(escape, @"\");
            }
            return ValueToSql(value, parameters);
        }

        internal static string ValueToSql(object? value, List<string> parameters)
        {
            switch (value)
            {
                case null:
                case DBNull:
                    return "NULL";
                case bool b:
                    return b ? "'T'" : "'F'";
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case DateOnly date:
                    return $"TO_DATE('{date.ToString(DateFormatNet, CultureInfo.InvariantCulture)}', '{DateFormat}')";
                case DateTime dateTime:
                    return $"TO_DATE('{dateTime.ToString(DateTimeFormatNet, CultureInfo.InvariantCulture)}', '{DateTimeFormat}')";
                case DateTimeOffset dateTimeOffset:
                    return $"TO_DATE('{dateTimeOffset.UtcDateTime.ToString(DateTimeFormatNet, CultureInfo.InvariantCulture)}', '{DateTimeFormat}')";
                default:
                    parameters.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return "?";
            }
        }

        // ---------------------------------------------------------------- 書き込み

        /// <summary>
        /// 本体の insert (1 件: BEGIN INSERT ... RETURNING "id" INTO :returning_id; END; / 一括: BEGIN INSERT ...; INSERT ...; END;) を読む。
        /// </summary>
        internal static List<RecordInsert> ParseInserts(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var i = 0;
            if (i < tokens.Count && tokens[i].IsWord("begin")) i++;
            var list = new List<RecordInsert>();
            while (i < tokens.Count && tokens[i].IsWord("insert"))
            {
                i++;
                Expect(tokens, ref i, t => t.IsWord("into"), sql);
                var insert = new RecordInsert { Table = IdentifierText(Next(tokens, ref i, sql), sql) };
                Expect(tokens, ref i, t => t.IsSymbol("("), sql);
                var columns = new List<string>();
                while (true)
                {
                    columns.Add(IdentifierText(Next(tokens, ref i, sql), sql));
                    var separator = Next(tokens, ref i, sql);
                    if (separator.IsSymbol(")")) break;
                    if (!separator.IsSymbol(",")) throw NotSupported(sql);
                }
                Expect(tokens, ref i, t => t.IsWord("values"), sql);
                Expect(tokens, ref i, t => t.IsSymbol("("), sql);
                var parameters = new List<string>();
                while (true)
                {
                    var parameter = Next(tokens, ref i, sql);
                    if (parameter.Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
                    parameters.Add(parameter.Text);
                    var separator = Next(tokens, ref i, sql);
                    if (separator.IsSymbol(")")) break;
                    if (!separator.IsSymbol(",")) throw NotSupported(sql);
                }
                if (columns.Count != parameters.Count) throw NotSupported(sql);
                for (var c = 0; c < columns.Count; c++) insert.Values.Add((columns[c], parameters[c]));

                if (i < tokens.Count && tokens[i].IsWord("returning"))
                {
                    i++;
                    var idColumn = IdentifierText(Next(tokens, ref i, sql), sql);
                    if (!string.Equals(idColumn, "id", StringComparison.OrdinalIgnoreCase)) throw new NetSuiteException("The Id field of a NetSuite module must be bound to the column 'id'.");
                    Expect(tokens, ref i, t => t.IsWord("into"), sql);
                    var parameter = Next(tokens, ref i, sql);
                    if (parameter.Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
                    insert.ReturningParameter = parameter.Text;
                }
                if (i < tokens.Count && tokens[i].IsSymbol(";")) i++;
                list.Add(insert);
            }
            if (i < tokens.Count && tokens[i].IsWord("end")) i++;
            if (i < tokens.Count && tokens[i].IsSymbol(";")) i++;
            if (i != tokens.Count || list.Count == 0) throw NotSupported(sql);
            return list;
        }

        /// <summary>書き込み (insert into / update / delete from) の対象の表。読めない形なら空 (形の確かめは読み取りの側でする)。</summary>
        internal static List<string> GetWriteTables(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var tables = new List<string>();
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                var target = tokens[i].IsWord("update") ? i + 1 : (tokens[i].IsWord("insert") || tokens[i].IsWord("delete")) && i + 2 < tokens.Count ? i + 2 : -1;
                if (target < 0 || tokens.Count <= target || !tokens[target].IsIdentifier || !IsIdentifier(tokens[target].Text)) continue;
                if (target == i + 2 && !(tokens[i + 1].IsWord("into") || tokens[i + 1].IsWord("from"))) continue;
                tables.Add(tokens[target].Text);
            }
            return tables;
        }

        internal static bool IsUpdate(string sql) => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("update") == true;
        internal static bool IsDelete(string sql) => WithoutSpaces(Tokenize(sql)).FirstOrDefault()?.IsWord("delete") == true;

        /// <summary>本体の update (update "表" set "列"=:p0,... where "id"=:pid0 [and ...]) を読む。</summary>
        internal static RecordUpdate ParseUpdate(string sql)
        {
            var all = Tokenize(sql);
            var tokens = WithoutSpaces(all);
            var i = 0;
            Expect(tokens, ref i, t => t.IsWord("update"), sql);
            var update = new RecordUpdate { Table = IdentifierText(Next(tokens, ref i, sql), sql) };
            Expect(tokens, ref i, t => t.IsWord("set"), sql);
            while (true)
            {
                var column = IdentifierText(Next(tokens, ref i, sql), sql);
                Expect(tokens, ref i, t => t.IsSymbol("="), sql);
                var parameter = Next(tokens, ref i, sql);
                if (parameter.Kind != SqlTokenKind.Parameter) throw NotSupported(sql);
                update.Values.Add((column, parameter.Text));
                var separator = Next(tokens, ref i, sql);
                if (separator.IsWord("where")) break;
                if (!separator.IsSymbol(",")) throw NotSupported(sql);
            }
            update.Where = ParseWhere(tokens.GetRange(i, tokens.Count - i), sql);
            return update;
        }

        /// <summary>本体の delete (delete from "表" where "id"=:pid0 [and ...]) を読む。</summary>
        internal static RecordDelete ParseDelete(string sql)
        {
            var tokens = WithoutSpaces(Tokenize(sql));
            var i = 0;
            Expect(tokens, ref i, t => t.IsWord("delete"), sql);
            Expect(tokens, ref i, t => t.IsWord("from"), sql);
            var delete = new RecordDelete { Table = IdentifierText(Next(tokens, ref i, sql), sql) };
            Expect(tokens, ref i, t => t.IsWord("where"), sql);
            delete.Where = ParseWhere(tokens.GetRange(i, tokens.Count - i), sql);
            return delete;
        }

        static RecordWhere ParseWhere(List<SqlToken> where, string sql)
        {
            if (where.Any(e => e.IsSymbol(";"))) throw NotSupported(sql);
            //先頭は Id の一致 ("id"=:pid0)。複合キーは扱わない
            if (where.Count < 3 || !where[0].IsIdentifier || !string.Equals(where[0].Text, "id", StringComparison.OrdinalIgnoreCase) ||
                !where[1].IsSymbol("=") || where[2].Kind != SqlTokenKind.Parameter)
                throw new NetSuiteException("A record of a NetSuite data source can only be written by its internal ID (bind the Id field to the column 'id').");
            if (3 < where.Count && !where[3].IsWord("and")) throw NotSupported(sql);
            return new RecordWhere
            {
                IdParameter = where[2].Text,
                HasOtherConditions = 3 < where.Count,
                Sql = string.Join(" ", where.Select(e => e.Kind switch
                {
                    SqlTokenKind.QuotedIdentifier => "\"" + e.Text + "\"",
                    SqlTokenKind.StringLiteral => "'" + e.Text.Replace("'", "''") + "'",
                    _ => e.Text,
                })),
            };
        }

        static SqlToken Next(List<SqlToken> tokens, ref int i, string sql)
        {
            if (tokens.Count <= i) throw NotSupported(sql);
            return tokens[i++];
        }

        static void Expect(List<SqlToken> tokens, ref int i, Func<SqlToken, bool> predicate, string sql)
        {
            if (!predicate(Next(tokens, ref i, sql))) throw NotSupported(sql);
        }
    }
}
