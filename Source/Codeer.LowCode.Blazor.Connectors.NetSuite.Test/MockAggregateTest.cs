using Codeer.LowCode.Blazor.Aggregation;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// モック + 本物のコネクター + 本体の ModuleDataIO.AggregateAsync で、集計を端から端まで確かめる。
    /// 期待値は mock-data.json の初期の行 (customrecord_sample = Sample) から計算する。
    /// </summary>
    public class MockAggregateTest
    {
        MockHarness _h = null!;
        List<JsonObject> _rows = null!;

        [SetUp]
        public async Task SetUp()
        {
            _h = await MockHarness.StartAsync();
            _rows = MockHarness.Rows("customrecord_sample");
        }

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        static AggregateMeasure Measure(AggregateFunction function, string variable = "") => new() { Function = function, Variable = variable };

        Task<AggregateResult> AggregateAsync(AggregateCondition condition) => _h.IO.AggregateAsync(condition);

        //SQL の sum と同じく、値が 1 つも無ければ null
        static decimal? Sum(IEnumerable<JsonObject> rows, string name)
        {
            var values = rows.Select(e => e.Number(name)).OfType<decimal>().ToList();
            return values.Count == 0 ? null : values.Sum();
        }

        static string CompanyName(string? id) => id == null ? string.Empty : MockHarness.Rows("customer").Single(e => e.Id() == id).Text("companyName")!;

        // ---------------------------------------------------------------- 軸なし

        [Test]
        public async Task 軸なしの件数と合計と平均と最小と最大と重複なしの件数()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                Measures =
                {
                    Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value"), Measure(AggregateFunction.Avg, "Amount.Value"),
                    Measure(AggregateFunction.Min, "Amount.Value"), Measure(AggregateFunction.Max, "Quantity.Value"), Measure(AggregateFunction.CountDistinct, "Customer.Value"),
                },
            });

            var amounts = _rows.Select(e => e.Number("custrecord_amount")).OfType<decimal>().ToList();
            var row = result.Rows.Single();
            Assert.That(row.Value(0), Is.EqualTo((decimal)_rows.Count));
            Assert.That(row.Value(1), Is.EqualTo(amounts.Sum()));
            Assert.That((decimal)row.Value(2)!, Is.EqualTo(amounts.Average()).Within(0.0001m));
            Assert.That(row.Value(3), Is.EqualTo(amounts.Min()));
            Assert.That(row.Value(4), Is.EqualTo(_rows.Select(e => e.Number("custrecord_quantity")).Max()));
            Assert.That(row.Value(5), Is.EqualTo((decimal)_rows.Select(e => e.Ref("custrecord_customer")).OfType<string>().Distinct().Count()));
            Assert.That(result.TotalCount, Is.EqualTo(_rows.Count));
            Assert.That(result.GroupCount, Is.EqualTo(1));
        }

        [Test]
        public async Task 検索条件つきの集計()
        {
            var condition = new AggregateCondition("Sample") { Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value") } };
            condition.Condition = MultiMatchCondition.And(
                new FieldValueMatchCondition { SearchTargetVariable = "Done.Value", Comparison = MatchComparison.Equal, Value = new BooleanValue { Value = true } },
                new FieldValueMatchCondition { SearchTargetVariable = "OrderDate.Value", Comparison = MatchComparison.GreaterThanOrEqual, Value = new DateOnlyValue { Value = new DateOnly(2026, 1, 1) } },
                new FieldValueMatchCondition { SearchTargetVariable = "Customer.CompanyName.Value", Comparison = MatchComparison.Like, Value = new StringValue { Value = "a" } });

            var result = await AggregateAsync(condition);

            var expected = _rows.Where(e => e.Bool("custrecord_done") == true && e.Date("custrecord_date") >= new DateOnly(2026, 1, 1) &&
                                            CompanyName(e.Ref("custrecord_customer")).Contains('a', StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.That(expected, Has.Count.GreaterThan(1));
            Assert.That(result.Rows.Single().Value(0), Is.EqualTo((decimal)expected.Count));
            Assert.That(result.Rows.Single().Value(1), Is.EqualTo(Sum(expected, "custrecord_amount")));
        }

        // ---------------------------------------------------------------- 値の軸

        [Test]
        public async Task 文字の項目を軸にする()
        {
            var result = await _h.IO.AggregateAsync(new AggregateCondition("Subsidiary") { Groups = { new ValueGroup { Variable = "Country.Value" } }, Measures = { Measure(AggregateFunction.Count) } });

            var expected = MockHarness.Rows("subsidiary").GroupBy(e => e.Text("country")).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => (decimal)e.Count())));
        }

        [Test]
        public async Task 候補つきの選択を軸にすると候補の順で空値は最後()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                Groups = { new ValueGroup { Variable = "Status.Value" } },
                Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value") },
            });

            var order = new[] { "open", "won", "lost", null };
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(order));
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.EqualTo(new[] { "Open", "Won", "Lost", "" }));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(order.Select(s => (decimal)_rows.Count(e => e.Text("custrecord_status") == s))));
            Assert.That(result.Rows.Select(e => e.Value(1)), Is.EqualTo(order.Select(s => Sum(_rows.Where(e => e.Text("custrecord_status") == s), "custrecord_amount"))));
            Assert.That(result.TotalCount, Is.EqualTo(_rows.Count));
            Assert.That(result.GroupCount, Is.EqualTo(4));
        }

        [Test]
        public async Task リンクを軸にすると表示名が付き表示名の順に並ぶ()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample") { Groups = { new ValueGroup { Variable = "Customer.Value" } }, Measures = { Measure(AggregateFunction.Count) } });

            var expected = _rows.GroupBy(e => e.Ref("custrecord_customer"))
                .OrderBy(e => e.Key == null ? 1 : 0).ThenBy(e => CompanyName(e.Key), StringComparer.Ordinal).ToList();
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.EqualTo(expected.Select(e => CompanyName(e.Key))));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => (decimal)e.Count())));
        }

        // ---------------------------------------------------------------- 日付の区切り

        static DateOnly PeriodStart(DateTime value, DateBucket bucket, int fiscalYearStartMonth)
        {
            var d = DateOnly.FromDateTime(value);
            switch (bucket)
            {
                case DateBucket.Day: return d;
                case DateBucket.Week: return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                case DateBucket.Month: return new DateOnly(d.Year, d.Month, 1);
                case DateBucket.Quarter:
                case DateBucket.Year:
                    {
                        //年度の始まりの月だけずらして、暦の四半期・年で区切る
                        var shift = fiscalYearStartMonth - 1;
                        var shifted = d.AddMonths(-shift);
                        var start = bucket == DateBucket.Quarter ? new DateOnly(shifted.Year, (shifted.Month - 1) / 3 * 3 + 1, 1) : new DateOnly(shifted.Year, 1, 1);
                        return start.AddMonths(shift);
                    }
            }
            throw new ArgumentException(bucket.ToString());
        }

        [TestCase(DateBucket.Day, 1)]
        [TestCase(DateBucket.Week, 1)]
        [TestCase(DateBucket.Month, 1)]
        [TestCase(DateBucket.Quarter, 1)]
        [TestCase(DateBucket.Quarter, 4)]
        [TestCase(DateBucket.Year, 1)]
        [TestCase(DateBucket.Year, 4)]
        public async Task 日付の項目を区切って軸にする(DateBucket bucket, int fiscalYearStartMonth)
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                UtcOffsetMinutes = 540,
                Groups = { new DateGroup { Variable = "OrderDate.Value", Bucket = bucket, FiscalYearStartMonth = fiscalYearStartMonth } },
                Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Quantity.Value") },
            });

            //日付の項目は時差で動かさない
            var expected = _rows.GroupBy(e => e.Date("custrecord_date") is DateOnly d ? PeriodStart(d.ToDateTime(TimeOnly.MinValue), bucket, fiscalYearStartMonth) : (DateOnly?)null)
                .OrderBy(e => e.Key == null ? 1 : 0).ThenBy(e => e.Key).ToList();
            Assert.That(expected, Has.Count.GreaterThan(2));
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => (object?)e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => (decimal)e.Count())));
            Assert.That(result.Rows.Select(e => e.Value(1)), Is.EqualTo(expected.Select(e => Sum(e, "custrecord_quantity"))));
        }

        [TestCase(0)]
        [TestCase(540)]
        [TestCase(-300)]
        public async Task UTCの日時は時差を足してから月で区切る(int utcOffsetMinutes)
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                UtcOffsetMinutes = utcOffsetMinutes,
                Groups = { new DateGroup { Variable = "Recorded.Value", Bucket = DateBucket.Month } },
                Measures = { Measure(AggregateFunction.Count) },
            });

            var expected = _rows.GroupBy(e => e.DateTimeUtc("custrecord_recorded") is DateTime d ? PeriodStart(d.AddMinutes(utcOffsetMinutes), DateBucket.Month, 1) : (DateOnly?)null)
                .OrderBy(e => e.Key == null ? 1 : 0).ThenBy(e => e.Key).ToList();
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => (object?)e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => (decimal)e.Count())));
        }

        [Test]
        public async Task 時差を変えると月の境目の行が隣の月に移る()
        {
            async Task<Dictionary<object, object?>> CountByMonth(int offset)
            {
                var result = await AggregateAsync(new AggregateCondition("Sample")
                {
                    UtcOffsetMinutes = offset,
                    Groups = { new DateGroup { Variable = "Recorded.Value", Bucket = DateBucket.Month } },
                    Measures = { Measure(AggregateFunction.Count) },
                });
                return result.Rows.Where(e => e.Key(0) != null).ToDictionary(e => e.Key(0)!, e => e.Value(0));
            }

            var utc = await CountByMonth(0);
            var jst = await CountByMonth(540);
            //2026-03-31T20:00Z は日本時間で 4 月、2026-02-28T16:00Z は 3 月
            Assert.That(jst[new DateOnly(2026, 4, 1)], Is.EqualTo((decimal)utc[new DateOnly(2026, 4, 1)]! + 1));
            Assert.That(jst[new DateOnly(2026, 2, 1)], Is.EqualTo((decimal)utc[new DateOnly(2026, 2, 1)]! - 1));
        }

        [Test]
        public async Task 時で区切るとキーは日時()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                UtcOffsetMinutes = 540,
                Groups = { new DateGroup { Variable = "Recorded.Value", Bucket = DateBucket.Hour } },
                Measures = { Measure(AggregateFunction.Count) },
            });

            var expected = _rows.Select(e => e.DateTimeUtc("custrecord_recorded")).OfType<DateTime>()
                .Select(d => d.AddMinutes(540)).Select(d => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0)).Distinct().Order().ToList();
            Assert.That(result.Rows.Where(e => e.Key(0) != null).Select(e => e.Key(0)), Is.EqualTo(expected));
        }

        // ---------------------------------------------------------------- 最小・最大

        [Test]
        public async Task 最小と最大は項目の型で返る()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                Measures =
                {
                    Measure(AggregateFunction.Min, "OrderDate.Value"), Measure(AggregateFunction.Max, "Recorded.Value"),
                    Measure(AggregateFunction.Min, "Name.Value"), Measure(AggregateFunction.Max, "Amount.Value"),
                },
            });

            var values = result.Rows.Single().Values.Select(e => e.GetValue()).ToList();
            Assert.That(values[0], Is.TypeOf<DateOnly>().And.EqualTo(_rows.Select(e => e.Date("custrecord_date")).Min()));
            Assert.That(values[1], Is.TypeOf<DateTime>().And.EqualTo(_rows.Select(e => e.DateTimeUtc("custrecord_recorded")).Max()));
            Assert.That(values[2], Is.TypeOf<string>().And.EqualTo(_rows.Select(e => e.Text("name")).Min(StringComparer.Ordinal)));
            Assert.That(values[3], Is.TypeOf<decimal>().And.EqualTo(_rows.Select(e => e.Number("custrecord_amount")).Max()));
        }

        // ---------------------------------------------------------------- HAVING・上限・行を選ぶ定義

        [Test]
        public async Task HAVINGで合計の大きいグループだけ()
        {
            var condition = new AggregateCondition("Sample") { Groups = { new ValueGroup { Variable = "Customer.Value" } }, Measures = { Measure(AggregateFunction.Sum, "Amount.Value") } };
            condition.Having.Add(new AggregateHaving { MeasureIndex = 0, Comparison = MatchComparison.GreaterThan, Value = 2000m });

            var result = await AggregateAsync(condition);

            var expected = _rows.GroupBy(e => e.Ref("custrecord_customer")).Where(e => Sum(e, "custrecord_amount") > 2000)
                .OrderBy(e => e.Key == null ? 1 : 0).ThenBy(e => CompanyName(e.Key), StringComparer.Ordinal).ToList();
            Assert.That(expected, Has.Count.EqualTo(2));
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => Sum(e, "custrecord_amount"))));
            //HAVING で外れた行も含めた、条件に合った行数
            Assert.That(result.TotalCount, Is.EqualTo(_rows.Count));
            Assert.That(result.GroupCount, Is.EqualTo(2));
        }

        [Test]
        public async Task LimitCountで件数の多い順に上位だけ()
        {
            var condition = new AggregateCondition("Sample") { Groups = { new ValueGroup { Variable = "Customer.Value" } }, Measures = { Measure(AggregateFunction.Count) }, LimitCount = 3 };
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Measure, Index = 0, IsDescending = true });

            var result = await AggregateAsync(condition);

            var groups = _rows.GroupBy(e => e.Ref("custrecord_customer")).ToList();
            var expected = groups.OrderByDescending(e => e.Count()).ThenBy(e => e.Key == null ? 1 : 0).ThenBy(e => CompanyName(e.Key), StringComparer.Ordinal).Take(3).ToList();
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(expected.Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(expected.Select(e => (decimal)e.Count())));
            Assert.That(result.IsLimited, Is.True);
            Assert.That(result.GroupCount, Is.EqualTo(groups.Count));
            Assert.That(result.TotalCount, Is.EqualTo(_rows.Count));
        }

        [Test]
        public async Task WithinGroupsOfで件数の上位2件の得意先の行だけを集計する()
        {
            var condition = new AggregateCondition("Sample")
            {
                Groups = { new ValueGroup { Variable = "Customer.Value" } },
                Measures = { Measure(AggregateFunction.Sum, "Amount.Value") },
                WithinGroupsOf = new AggregateCondition("Sample")
                {
                    Groups = { new ValueGroup { Variable = "Customer.Value" } },
                    Measures = { Measure(AggregateFunction.Count) },
                    SortConditions = { new AggregateSort { Target = AggregateSortTarget.Measure, Index = 0, IsDescending = true } },
                    LimitCount = 2,
                },
            };

            var result = await AggregateAsync(condition);

            var top = _rows.GroupBy(e => e.Ref("custrecord_customer"))
                .OrderByDescending(e => e.Count()).ThenBy(e => e.Key == null ? 1 : 0).ThenBy(e => CompanyName(e.Key), StringComparer.Ordinal).Take(2)
                .OrderBy(e => CompanyName(e.Key), StringComparer.Ordinal).ToList();
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(top.Select(e => e.Key)));
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(top.Select(e => Sum(e, "custrecord_amount"))));
            Assert.That(result.TotalCount, Is.EqualTo(top.Sum(e => e.Count())));
        }

        [Test]
        public async Task WithinGroupsOfで月と状態の二つの軸()
        {
            var result = await AggregateAsync(new AggregateCondition("Sample")
            {
                Groups = { new DateGroup { Variable = "OrderDate.Value", Bucket = DateBucket.Month }, new ValueGroup { Variable = "Status.Value" } },
                Measures = { Measure(AggregateFunction.Count) },
                WithinGroupsOf = new AggregateCondition("Sample")
                {
                    Groups = { new ValueGroup { Variable = "Status.Value" } },
                    Measures = { Measure(AggregateFunction.Sum, "Amount.Value") },
                    Having = { new AggregateHaving { MeasureIndex = 0, Comparison = MatchComparison.GreaterThan, Value = 5000m } },
                },
            });

            var statuses = _rows.GroupBy(e => e.Text("custrecord_status")).Where(e => Sum(e, "custrecord_amount") > 5000).Select(e => e.Key).ToHashSet();
            var expected = _rows.Where(e => statuses.Contains(e.Text("custrecord_status")))
                .GroupBy(e => (Month: e.Date("custrecord_date") is DateOnly d ? new DateOnly(d.Year, d.Month, 1) : (DateOnly?)null, Status: e.Text("custrecord_status")))
                .ToList();
            Assert.That(result.Rows.Select(e => (e.Key(0), e.Key(1), e.Value(0))),
                Is.EquivalentTo(expected.Select(e => ((object?)e.Key.Month, (object?)e.Key.Status, (object?)(decimal)e.Count()))));
        }
    }
}
