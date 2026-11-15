using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    public class NetSuiteRecordDefinitionTest
    {
        const string Schema = """
            {
              "type": "object",
              "properties": {
                "companyName": { "type": "string" },
                "id": { "type": "string" },
                "isInactive": { "type": "boolean" },
                "daysOverdue": { "type": "integer" },
                "creditLimit": { "type": "number" },
                "startDate": { "type": "string", "format": "date" },
                "lastModifiedDate": { "type": "string", "format": "date-time" },
                "subsidiary": { "type": "object", "properties": { "id": { "type": "string" }, "refName": { "type": "string" } } },
                "currency": { "$ref": "/services/rest/record/v1/metadata-catalog/currency" },
                "addressBook": { "$ref": "/services/rest/record/v1/metadata-catalog/customer-addressBookCollection" },
                "contactRoles": { "type": "object", "properties": { "items": { "type": "array" }, "totalResults": { "type": "integer" } } },
                "tags": { "type": "array", "items": { "type": "string" } },
                "custentity_Flag": { "type": "boolean" },
                "bad-name": { "type": "string" },
                "noType": { "description": "unknown" },
                "links": { "type": "array", "items": { "type": "object" } },
                "refName": { "type": "string" }
              },
              "required": ["companyName", "subsidiary", "id"]
            }
            """;

        static NetSuiteRecordDefinition Parse(string schema = Schema)
        {
            using var json = JsonDocument.Parse(schema);
            return NetSuiteRecordDefinition.FromJsonSchema("customer", json.RootElement);
        }

        [Test]
        public void 種類の判定()
        {
            var definition = Parse();
            var kinds = definition.Fields.ToDictionary(e => e.Column, e => e.Kind);
            Assert.That(kinds, Is.EquivalentTo(new Dictionary<string, NetSuiteFieldKind>
            {
                ["id"] = NetSuiteFieldKind.String,
                ["companyname"] = NetSuiteFieldKind.String,
                ["isinactive"] = NetSuiteFieldKind.Boolean,
                ["daysoverdue"] = NetSuiteFieldKind.Integer,
                ["creditlimit"] = NetSuiteFieldKind.Number,
                ["startdate"] = NetSuiteFieldKind.Date,
                ["lastmodifieddate"] = NetSuiteFieldKind.DateTime,
                ["subsidiary"] = NetSuiteFieldKind.Reference,
                ["currency"] = NetSuiteFieldKind.Reference,
                ["custentity_flag"] = NetSuiteFieldKind.Boolean,
            }));
        }

        [Test]
        public void 列名は小文字でREST名は元のまま()
        {
            var definition = Parse();
            Assert.That(definition.FindByColumn("companyname")!.RestName, Is.EqualTo("companyName"));
            Assert.That(definition.FindByColumn("CUSTENTITY_FLAG")!.RestName, Is.EqualTo("custentity_Flag"));
        }

        [Test]
        public void 明細とlinksとrefNameと配列は列にならない()
        {
            var columns = Parse().Fields.Select(e => e.Column).ToList();
            Assert.That(columns, Has.None.EqualTo("addressbook"));
            Assert.That(columns, Has.None.EqualTo("contactroles"));
            Assert.That(columns, Has.None.EqualTo("tags"));
            Assert.That(columns, Has.None.EqualTo("links"));
            Assert.That(columns, Has.None.EqualTo("refname"));
            Assert.That(columns, Has.None.EqualTo("bad-name"));
            Assert.That(columns, Has.None.EqualTo("notype"));
        }

        [Test]
        public void idが先頭でString型で残りは列名順()
        {
            var columns = Parse().Fields.Select(e => e.Column).ToList();
            Assert.That(columns[0], Is.EqualTo("id"));
            Assert.That(columns.Skip(1), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));

            //id が integer と書かれていても文字列
            var definition = Parse("""{"properties":{"name":{"type":"string"},"id":{"type":"integer"}}}""");
            Assert.That(definition.Fields[0].Column, Is.EqualTo("id"));
            Assert.That(definition.Fields[0].Kind, Is.EqualTo(NetSuiteFieldKind.String));
        }

        [Test]
        public void ToTableDefinitionの型()
        {
            var table = Parse().ToTableDefinition();
            Assert.That(table.Name, Is.EqualTo("customer"));
            var columns = table.Columns.ToDictionary(e => e.Name);
            void Check(string column, Type type, string raw)
            {
                Assert.That(columns[column].NetTypeFullName, Is.EqualTo(type.FullName), column);
                Assert.That(columns[column].RawDbTypeName, Is.EqualTo(raw), column);
            }
            Check("id", typeof(string), "VARCHAR2");
            Check("companyname", typeof(string), "VARCHAR2");
            Check("isinactive", typeof(bool), "BOOLEAN");
            Check("daysoverdue", typeof(long), "INTEGER");
            Check("creditlimit", typeof(decimal), "NUMBER");
            Check("startdate", typeof(DateOnly), "DATE");
            Check("lastmodifieddate", typeof(DateTime), "TIMESTAMP WITH LOCAL TIME ZONE");
            Check("subsidiary", typeof(string), "REFERENCE");
        }

        [Test]
        public void requiredは項目の定義に残すが表の定義の列は全部nullable()
        {
            //項目の定義: required の項目は IsNullable が false、id は常に nullable
            var definition = Parse();
            Assert.That(definition.FindByColumn("companyname")!.IsNullable, Is.False);
            Assert.That(definition.FindByColumn("subsidiary")!.IsNullable, Is.False);
            Assert.That(definition.FindByColumn("creditlimit")!.IsNullable, Is.True);
            Assert.That(definition.FindByColumn("id")!.IsNullable, Is.True);

            //表の定義: NOT NULL にすると本体が空の値を型の既定値 (0・0001-01-01・false) に置き換えて書くので、全部 nullable (必須は NetSuite が保存時に確かめる)
            var columns = definition.ToTableDefinition().Columns;
            Assert.That(columns.Select(e => e.IsNullable), Is.All.True);
        }

        [Test]
        public void propertiesが無ければ例外()
        {
            var e = Assert.Throws<NetSuiteException>(() => Parse("""{"type":"object"}"""));
            Assert.That(e!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
        }

        [Test]
        public void 偽物のcustomerの定義()
        {
            var definition = Parse(FakeNetSuite.CustomerSchema);
            Assert.That(definition.Fields.Select(e => e.Column), Is.EqualTo(new[]
            {
                "id", "companyname", "creditlimit", "isinactive", "lastmodifieddate", "startdate", "subsidiary",
            }));
        }

        // ---------------------------------------------------------------- 明細 (サブリスト)

        static Task<NetSuiteRecordDefinition> ParseAsync(string schema, Dictionary<string, string>? catalog = null, List<string>? requested = null)
        {
            using var json = JsonDocument.Parse(schema);
            return NetSuiteRecordDefinition.FromJsonSchemaAsync("salesorder", json.RootElement.Clone(), name =>
            {
                requested?.Add(name);
                return Task.FromResult(catalog != null && catalog.TryGetValue(name, out var text) ? (JsonElement?)JsonDocument.Parse(text).RootElement.Clone() : null);
            });
        }

        const string RowProperties = """
            {
              "line": { "type": "integer" },
              "item": { "type": "object", "properties": { "id": { "type": "string" }, "refName": { "type": "string" } } },
              "quantity": { "type": "number" },
              "expectedShipDate": { "type": "string", "format": "date" },
              "inventoryDetail": { "$ref": "/services/rest/record/v1/metadata-catalog/salesOrder-item-inventoryDetail" },
              "links": { "type": "array" }
            }
            """;

        [Test]
        public async Task その場に書かれた明細はlineを持てば列になり行の項目を持つ()
        {
            var definition = await ParseAsync($$$$"""
                {"properties":{
                  "id":{"type":"string"},
                  "item":{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{{{{RowProperties}}}},"required":["item"]}},"totalResults":{"type":"integer"}}}
                }}
                """);

            var sublist = definition.FindByColumn("item")!;
            Assert.That(sublist.Kind, Is.EqualTo(NetSuiteFieldKind.Sublist));
            Assert.That(sublist.RestName, Is.EqualTo("item"));
            //入れ子 (inventoryDetail) と links は行の項目にしない
            Assert.That(sublist.SublistFields.Select(e => (e.RestName, e.Kind)), Is.EqualTo(new[]
            {
                ("line", NetSuiteFieldKind.Integer),
                ("item", NetSuiteFieldKind.Reference),
                ("quantity", NetSuiteFieldKind.Number),
                ("expectedShipDate", NetSuiteFieldKind.Date),
            }));
            Assert.That(sublist.FindSublistField("item")!.IsNullable, Is.False);
            Assert.That(sublist.FindSublistField("quantity")!.IsNullable, Is.True);

            var column = definition.ToTableDefinition().Columns.Single(e => e.Name == "item");
            Assert.That((column.NetTypeFullName, column.RawDbTypeName), Is.EqualTo((typeof(string).FullName, "CLOB")));
        }

        [Test]
        public async Task refの明細は集まりと行の定義を読んでたどる()
        {
            var requested = new List<string>();
            var definition = await ParseAsync("""
                {"properties":{"id":{"type":"string"},"item":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-itemCollection"}}}
                """, new()
                {
                    ["salesOrder-itemCollection"] = """{"type":"object","properties":{"items":{"type":"array","items":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-itemElement"}}}}""",
                    ["salesOrder-itemElement"] = $$$"""{"type":"object","properties":{{{RowProperties}}}}""",
                }, requested);

            Assert.That(definition.FindByColumn("item")!.SublistFields.Select(e => e.RestName), Is.EqualTo(new[] { "line", "item", "quantity", "expectedShipDate" }));
            Assert.That(requested, Is.EqualTo(new[] { "salesOrder-itemCollection", "salesOrder-itemElement" }));
        }

        [Test]
        public async Task lineの無い明細と読めない明細と循環するrefは列にしない()
        {
            var requested = new List<string>();
            var definition = await ParseAsync("""
                {"properties":{
                  "id":{"type":"string"},
                  "addressBook":{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"addressId":{"type":"string"}}}}}},
                  "missing":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-missingCollection"},
                  "loop":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-loop"},
                  "bad":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-x?y"}
                }}
                """, new()
                {
                    ["salesOrder-loop"] = """{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-loop"}""",
                }, requested);

            Assert.That(definition.Fields.Select(e => e.Column), Is.EqualTo(new[] { "id" }));
            //たどる回数には上限がある・名前に使えない文字の $ref は読みに行かない
            Assert.That(requested.Count(e => e == "salesOrder-loop"), Is.EqualTo(4));
            Assert.That(requested, Has.None.Contains("?"));

            //たどる先を渡さなければ $ref の明細は読みに行かず列にしない
            using var json = JsonDocument.Parse("""{"properties":{"item":{"$ref":"/services/rest/record/v1/metadata-catalog/salesOrder-itemCollection"}}}""");
            Assert.That(NetSuiteRecordDefinition.FromJsonSchema("salesorder", json.RootElement).Fields, Is.Empty);
        }
    }
}
