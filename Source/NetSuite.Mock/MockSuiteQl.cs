using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

namespace NetSuite.Mock
{
    /// <summary>SuiteQL として受け付けない・動かせない文。</summary>
    sealed class MockSqlException : Exception
    {
        public MockSqlException(string message) : base(message) { }
    }

    /// <summary>
    /// SuiteQL (Oracle の書き方) を SQLite で動かす。
    /// 文の形を確かめ、SQLite に無い書き方 (ROWNUM・NUMTODSINTERVAL) を書き換え、Oracle の関数 (TO_CHAR など) を SQLite の関数として足す。
    /// 日付は 'YYYY-MM-DD'、日時は UTC の 'YYYY-MM-DD HH:MM:SS' の文字で持つ。
    /// </summary>
    static class MockSuiteQl
    {
        enum Kind { Space, Word, Quoted, Text, Number, Param, Symbol }

        record Token(Kind Kind, string Raw)
        {
            public bool IsWord(string word) => Kind == Kind.Word && string.Equals(Raw, word, StringComparison.OrdinalIgnoreCase);
            public bool IsSymbol(string symbol) => Kind == Kind.Symbol && Raw == symbol;
            public string Name => Kind == Kind.Quoted ? Raw[1..^1] : Raw;
            public bool IsIdentifier => Kind == Kind.Word || Kind == Kind.Quoted;
        }

        static List<Token> Tokenize(string sql)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                var start = i;
                if (char.IsWhiteSpace(c))
                {
                    while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                    tokens.Add(new(Kind.Space, sql[start..i]));
                }
                else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n') i++;
                    tokens.Add(new(Kind.Space, " "));
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new MockSqlException("Unterminated comment.");
                    i = end + 2;
                    tokens.Add(new(Kind.Space, " "));
                }
                else if (c == '\'' || c == '"')
                {
                    i++;
                    while (true)
                    {
                        if (sql.Length <= i) throw new MockSqlException(c == '\'' ? "Unterminated string literal." : "Unterminated quoted identifier.");
                        if (sql[i] == c)
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new(c == '\'' ? Kind.Text : Kind.Quoted, sql[start..i]));
                }
                else if (char.IsAsciiDigit(c))
                {
                    while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.')) i++;
                    tokens.Add(new(Kind.Number, sql[start..i]));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new(Kind.Word, sql[start..i]));
                }
                else if (c == '?')
                {
                    i++;
                    tokens.Add(new(Kind.Param, "?"));
                }
                else
                {
                    var two = i + 1 < sql.Length ? sql.Substring(i, 2) : string.Empty;
                    if (two is "<=" or ">=" or "<>" or "!=" or "||")
                    {
                        i += 2;
                        tokens.Add(new(Kind.Symbol, two));
                    }
                    else
                    {
                        i++;
                        tokens.Add(new(Kind.Symbol, c.ToString()));
                    }
                }
            }
            return tokens;
        }

        static string Join(IEnumerable<Token> tokens) => string.Concat(tokens.Select(e => e.Raw));

        static int NextNonSpace(List<Token> tokens, int index)
        {
            for (var i = index; i < tokens.Count; i++) if (tokens[i].Kind != Kind.Space) return i;
            return -1;
        }

        static int PreviousNonSpace(List<Token> tokens, int index)
        {
            for (var i = index; 0 <= i; i--) if (tokens[i].Kind != Kind.Space) return i;
            return -1;
        }

        static int MatchingClose(List<Token> tokens, int open)
        {
            for (int i = open, depth = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsSymbol("(")) depth++;
                else if (tokens[i].IsSymbol(")") && --depth == 0) return i;
            }
            throw new MockSqlException("Unbalanced parentheses.");
        }

        static int MatchingOpen(List<Token> tokens, int close)
        {
            for (int i = close, depth = 0; 0 <= i; i--)
            {
                if (tokens[i].IsSymbol(")")) depth++;
                else if (tokens[i].IsSymbol("(") && --depth == 0) return i;
            }
            throw new MockSqlException("Unbalanced parentheses.");
        }

        // ---------------------------------------------------------------- 文の確認と書き換え

        /// <summary>
        /// SuiteQL を確かめて SQLite の SQL にする。? は順に @p0, @p1 ... になる。
        /// </summary>
        internal static string Prepare(string query, int parameterCount, Func<string, bool> isRecord)
        {
            var tokens = Tokenize(query);
            var first = NextNonSpace(tokens, 0);
            if (first < 0 || !tokens[first].IsWord("select")) throw new MockSqlException("Only SELECT statements are supported.");
            if (tokens.Any(e => e.IsSymbol(";"))) throw new MockSqlException("Only a single statement is allowed.");

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.IsIdentifier && (token.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || token.Name.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase)))
                    throw new MockSqlException($"Unknown identifier '{token.Name}'.");
                if (!(token.IsWord("from") || token.IsWord("join"))) continue;
                var next = NextNonSpace(tokens, i + 1);
                if (next < 0 || !tokens[next].IsIdentifier) continue;
                var after = NextNonSpace(tokens, next + 1);
                if (0 <= after && tokens[after].IsSymbol(".")) continue;
                if (!isRecord(tokens[next].Name)) throw new MockSqlException($"Record '{tokens[next].Name}' was not found.");
            }
            //transaction (取引の表・transactionline の親への列) は SQLite の予約語なので引用符を付ける
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsWord("transaction")) tokens[i] = new(Kind.Quoted, "\"transaction\"");
            }

            var count = tokens.Count(e => e.Kind == Kind.Param);
            if (count != parameterCount)
                throw new MockSqlException($"The query has {count} parameter marker(s) but {parameterCount} parameter value(s) were given.");

            tokens = RewriteRowNum(tokens);
            tokens = RewriteIntervals(tokens);
            tokens = AddOracleNullOrdering(tokens);

            var n = 0;
            return Join(tokens.Select(e => e.Kind == Kind.Param ? new Token(Kind.Word, "@p" + (n++).ToString(CultureInfo.InvariantCulture)) : e));
        }

        //select * from (<Q>) where ROWNUM <= N → <Q> limit N (並べたあとの上位 N 行)
        static List<Token> RewriteRowNum(List<Token> tokens)
        {
            while (true)
            {
                var index = tokens.FindIndex(e => e.IsWord("rownum"));
                if (index < 0) return tokens;

                var where = PreviousNonSpace(tokens, index - 1);
                var close = where < 0 ? -1 : PreviousNonSpace(tokens, where - 1);
                var op = NextNonSpace(tokens, index + 1);
                var number = op < 0 ? -1 : NextNonSpace(tokens, op + 1);
                if (where < 0 || close < 0 || op < 0 || number < 0 || !tokens[where].IsWord("where") || !tokens[close].IsSymbol(")") ||
                    !(tokens[op].IsSymbol("<=") || tokens[op].IsSymbol("<")) || tokens[number].Kind != Kind.Number)
                    throw new MockSqlException("ROWNUM is supported only in the form: select * from (...) where ROWNUM <= n");
                var open = MatchingOpen(tokens, close);
                var from = PreviousNonSpace(tokens, open - 1);
                var star = from < 0 ? -1 : PreviousNonSpace(tokens, from - 1);
                var select = star < 0 ? -1 : PreviousNonSpace(tokens, star - 1);
                if (select < 0 || !tokens[from].IsWord("from") || !tokens[star].IsSymbol("*") || !tokens[select].IsWord("select"))
                    throw new MockSqlException("ROWNUM is supported only in the form: select * from (...) where ROWNUM <= n");

                var limit = long.Parse(tokens[number].Raw, CultureInfo.InvariantCulture);
                if (tokens[op].IsSymbol("<")) limit--;
                var replacement = tokens.GetRange(open + 1, close - open - 1);
                replacement.Add(new(Kind.Space, " "));
                replacement.Add(new(Kind.Word, "limit"));
                replacement.Add(new(Kind.Space, " "));
                replacement.Add(new(Kind.Number, Math.Max(0, limit).ToString(CultureInfo.InvariantCulture)));
                tokens.RemoveRange(select, number - select + 1);
                tokens.InsertRange(select, replacement);
            }
        }

        //(式 + NUMTODSINTERVAL(n, 'MINUTE')) → (NS_ADD_INTERVAL(式, 1, n, 'MINUTE'))
        static List<Token> RewriteIntervals(List<Token> tokens)
        {
            while (true)
            {
                var index = tokens.FindIndex(e => e.IsWord("numtodsinterval"));
                if (index < 0) return tokens;

                const string form = "NUMTODSINTERVAL is supported only in the form: (expression + NUMTODSINTERVAL(n, 'unit'))";
                var op = PreviousNonSpace(tokens, index - 1);
                var argsOpen = NextNonSpace(tokens, index + 1);
                if (op < 0 || argsOpen < 0 || !(tokens[op].IsSymbol("+") || tokens[op].IsSymbol("-")) || !tokens[argsOpen].IsSymbol("(")) throw new MockSqlException(form);
                var argsClose = MatchingClose(tokens, argsOpen);
                var groupClose = NextNonSpace(tokens, argsClose + 1);
                if (groupClose < 0 || !tokens[groupClose].IsSymbol(")")) throw new MockSqlException(form);
                var groupOpen = MatchingOpen(tokens, groupClose);
                if (op <= groupOpen) throw new MockSqlException(form);

                var replacement = new List<Token> { new(Kind.Word, "NS_ADD_INTERVAL"), new(Kind.Symbol, "(") };
                replacement.AddRange(tokens.GetRange(groupOpen + 1, op - groupOpen - 1));
                replacement.Add(new(Kind.Symbol, ","));
                replacement.Add(new(Kind.Number, tokens[op].IsSymbol("+") ? "1" : "-1"));
                replacement.Add(new(Kind.Symbol, ","));
                replacement.AddRange(tokens.GetRange(argsOpen + 1, argsClose - argsOpen - 1));
                replacement.Add(new(Kind.Symbol, ")"));
                tokens.RemoveRange(groupOpen + 1, argsClose - groupOpen);
                tokens.InsertRange(groupOpen + 1, replacement);
            }
        }

        //Oracle の既定 (昇順は空値が最後・降順は空値が最初) に合わせる。SQLite は空値を最小として並べる
        static List<Token> AddOracleNullOrdering(List<Token> tokens)
        {
            var result = new List<Token>();
            for (var i = 0; i < tokens.Count; i++)
            {
                result.Add(tokens[i]);
                if (!tokens[i].IsWord("by")) continue;
                var order = PreviousNonSpace(tokens, i - 1);
                if (order < 0 || !tokens[order].IsWord("order")) continue;

                //並びの項目を、同じ深さの , で区切って読む (閉じ括弧・limit・文の終わりまで)
                var depth = 0;
                var item = new List<Token>();
                var j = i + 1;
                void FlushItem()
                {
                    var last = item.FindLastIndex(e => e.Kind != Kind.Space);
                    var hasNulls = item.Any(e => e.IsWord("nulls"));
                    if (0 <= last && !hasNulls)
                    {
                        var desc = item[last].IsWord("desc");
                        item.Insert(last + 1, new(Kind.Word, desc ? " NULLS FIRST" : " NULLS LAST"));
                    }
                    result.AddRange(item);
                    item.Clear();
                }
                for (; j < tokens.Count; j++)
                {
                    var t = tokens[j];
                    if (t.IsSymbol("(")) depth++;
                    else if (t.IsSymbol(")"))
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (depth == 0 && (t.IsWord("limit") || t.IsWord("offset") || t.IsWord("fetch"))) break;
                    if (depth == 0 && t.IsSymbol(","))
                    {
                        FlushItem();
                        result.Add(t);
                        continue;
                    }
                    item.Add(t);
                }
                FlushItem();
                i = j - 1;
            }
            return result;
        }

        // ---------------------------------------------------------------- 実行

        internal class QueryResult
        {
            public List<List<(string Key, string Value)>> Items { get; } = new();
            public long TotalResults { get; set; }
        }

        internal static QueryResult Execute(SqliteConnection db, string sql, List<string?> parameters, int limit, int offset)
        {
            var result = new QueryResult();
            try
            {
                using (var count = db.CreateCommand())
                {
                    count.CommandText = $"select count(*) from ({sql})";
                    Bind(count, parameters);
                    result.TotalResults = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                using var command = db.CreateCommand();
                command.CommandText = $"{sql} limit {limit} offset {offset}";
                Bind(command, parameters);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new List<(string, string)>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        //null の列はキーごと省く
                        if (reader.IsDBNull(i)) continue;
                        row.Add((reader.GetName(i).ToLowerInvariant(), MockValues.ToResultText(reader.GetValue(i))));
                    }
                    result.Items.Add(row);
                }
            }
            catch (SqliteException e)
            {
                throw new MockSqlException(e.Message);
            }
            catch (MockSqlFunctionException e)
            {
                throw new MockSqlException(e.Message);
            }
            return result;
        }

        static void Bind(SqliteCommand command, List<string?> parameters)
        {
            for (var i = 0; i < parameters.Count; i++)
                command.Parameters.AddWithValue("@p" + i.ToString(CultureInfo.InvariantCulture), (object?)parameters[i] ?? DBNull.Value);
        }

        // ---------------------------------------------------------------- Oracle の関数

        sealed class MockSqlFunctionException : Exception
        {
            public MockSqlFunctionException(string message) : base(message) { }
        }

        internal static void RegisterFunctions(SqliteConnection db)
        {
            db.CreateFunction<object?, object?>("TO_CHAR", v => ToChar(v, null), true);
            db.CreateFunction<object?, object?, object?>("TO_CHAR", (v, f) => ToChar(v, f), true);
            db.CreateFunction<object?, object?>("TO_DATE", v => ToDate(v, null), true);
            db.CreateFunction<object?, object?, object?>("TO_DATE", (v, f) => ToDate(v, f), true);
            db.CreateFunction<object?, object?>("TRUNC", v => Trunc(v, null), true);
            db.CreateFunction<object?, object?, object?>("TRUNC", (v, f) => Trunc(v, f), true);
            db.CreateFunction<object?, object?, object?>("ADD_MONTHS", (v, n) => AddMonths(v, n), true);
            db.CreateFunction<object?, object?, object?, object?, object?>("NS_ADD_INTERVAL", (v, sign, n, unit) => AddInterval(v, sign, n, unit), true);
            db.CreateFunction<object?, object?, object?>("NVL", (a, b) => IsNull(a) ? b : a, true);
        }

        static bool IsNull(object? value) => value == null || value is DBNull;

        static bool TryGetDate(object? value, out DateTime date)
        {
            date = default;
            return value is string text && DateTime.TryParseExact(text, new[] { MockValues.DateFormat, MockValues.DateTimeFormat, "yyyy-MM-dd'T'HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        static DateTime GetDate(object? value, string function)
            => TryGetDate(value, out var date) ? date : throw new MockSqlFunctionException($"{function}: '{value}' is not a date.");

        static string DateTimeText(DateTime value) => value.ToString(MockValues.DateTimeFormat, CultureInfo.InvariantCulture);

        static double GetNumber(object? value, string function)
        {
            return value switch
            {
                long l => l,
                double d => d,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => throw new MockSqlFunctionException($"{function}: '{value}' is not a number."),
            };
        }

        //Oracle の書式 (YYYY-MM-DD HH24:MI:SS など) → .NET の書式
        static (string Format, bool HasTime) ConvertFormat(string oracle, string function)
        {
            var sb = new StringBuilder();
            var hasTime = false;
            var i = 0;
            bool Take(string part)
            {
                if (string.Compare(oracle, i, part, 0, part.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                i += part.Length;
                return true;
            }
            while (i < oracle.Length)
            {
                if (Take("YYYY")) sb.Append("yyyy");
                else if (Take("HH24")) { sb.Append("HH"); hasTime = true; }
                else if (Take("HH12") || Take("HH")) { sb.Append("hh"); hasTime = true; }
                else if (Take("MI")) { sb.Append("mm"); hasTime = true; }
                else if (Take("SS")) { sb.Append("ss"); hasTime = true; }
                else if (Take("MM")) sb.Append("MM");
                else if (Take("DD")) sb.Append("dd");
                else if (Take("YY")) sb.Append("yy");
                else if (oracle[i] is '-' or '/' or ':' or ' ' or '.' or ',')
                {
                    sb.Append('\\').Append(oracle[i]);
                    i++;
                }
                else throw new MockSqlFunctionException($"{function}: the format '{oracle}' is not supported by the mock.");
            }
            return (sb.ToString(), hasTime);
        }

        static object? ToChar(object? value, object? format)
        {
            if (IsNull(value)) return null;
            if (TryGetDate(value, out var date))
            {
                if (IsNull(format)) return value is string s && s.Length == 10 ? date.ToString(MockValues.DateFormat, CultureInfo.InvariantCulture) : DateTimeText(date);
                return date.ToString(ConvertFormat(Convert.ToString(format, CultureInfo.InvariantCulture)!, "TO_CHAR").Format, CultureInfo.InvariantCulture);
            }
            return MockValues.ToResultText(value!);
        }

        static object? ToDate(object? value, object? format)
        {
            if (IsNull(value)) return null;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            if (IsNull(format))
            {
                if (TryGetDate(text, out var d)) return text.Length == 10 ? d.ToString(MockValues.DateFormat, CultureInfo.InvariantCulture) : DateTimeText(d);
                throw new MockSqlFunctionException($"TO_DATE: '{text}' is not a date.");
            }
            var (net, hasTime) = ConvertFormat(Convert.ToString(format, CultureInfo.InvariantCulture)!, "TO_DATE");
            if (!DateTime.TryParseExact(text, net, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new MockSqlFunctionException($"TO_DATE: '{text}' does not match the format '{format}'.");
            return hasTime ? DateTimeText(date) : date.ToString(MockValues.DateFormat, CultureInfo.InvariantCulture);
        }

        static object? Trunc(object? value, object? format)
        {
            if (IsNull(value)) return null;
            if (value is long or double)
            {
                var digits = IsNull(format) ? 0 : (int)GetNumber(format, "TRUNC");
                var number = GetNumber(value, "TRUNC");
                var scale = Math.Pow(10, digits);
                var truncated = Math.Truncate(number * scale) / scale;
                return value is long && digits >= 0 ? (object)(long)truncated : truncated;
            }
            var date = GetDate(value, "TRUNC");
            var unit = IsNull(format) ? "DD" : Convert.ToString(format, CultureInfo.InvariantCulture)!.ToUpperInvariant();
            DateTime result = unit switch
            {
                "DD" or "DDD" or "J" => date.Date,
                "HH" or "HH12" or "HH24" => new DateTime(date.Year, date.Month, date.Day, date.Hour, 0, 0),
                "MI" => new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, 0),
                "IW" => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
                "MM" or "MON" or "MONTH" or "RM" => new DateTime(date.Year, date.Month, 1),
                "Q" => new DateTime(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
                "YYYY" or "YEAR" or "SYYYY" or "SYEAR" or "YYY" or "YY" or "Y" => new DateTime(date.Year, 1, 1),
                _ => throw new MockSqlFunctionException($"TRUNC: the unit '{unit}' is not supported by the mock."),
            };
            return DateTimeText(result);
        }

        static object? AddMonths(object? value, object? months)
        {
            if (IsNull(value) || IsNull(months)) return null;
            var date = GetDate(value, "ADD_MONTHS");
            var n = (int)Math.Truncate(GetNumber(months, "ADD_MONTHS"));
            var result = date.AddMonths(n);
            //Oracle: 月の最終日は、結果の月の最終日になる
            if (date.Day == DateTime.DaysInMonth(date.Year, date.Month))
                result = new DateTime(result.Year, result.Month, DateTime.DaysInMonth(result.Year, result.Month)).Add(date.TimeOfDay);
            return DateTimeText(result);
        }

        static object? AddInterval(object? value, object? sign, object? amount, object? unit)
        {
            if (IsNull(value) || IsNull(amount)) return null;
            var date = GetDate(value, "NUMTODSINTERVAL");
            var n = GetNumber(amount, "NUMTODSINTERVAL") * GetNumber(sign, "NUMTODSINTERVAL");
            var name = Convert.ToString(unit, CultureInfo.InvariantCulture)?.ToUpperInvariant();
            TimeSpan span = name switch
            {
                "DAY" => TimeSpan.FromDays(n),
                "HOUR" => TimeSpan.FromHours(n),
                "MINUTE" => TimeSpan.FromMinutes(n),
                "SECOND" => TimeSpan.FromSeconds(n),
                _ => throw new MockSqlFunctionException($"NUMTODSINTERVAL: the unit '{unit}' is not supported."),
            };
            return DateTimeText(date.Add(span));
        }
    }
}
