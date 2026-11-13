using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>記録した 1 つの要求。</summary>
    public class RecordedRequest
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public string Url { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; init; } = string.Empty;

        public bool IsToken => Path.EndsWith("/auth/oauth2/v1/token");
        public bool IsMetadata => Path.Contains("/record/v1/metadata-catalog/");
        public bool IsSuiteQl => Path.EndsWith("/query/v1/suiteql");
        public bool IsRecordWrite => Path.Contains("/record/v1/") && !IsMetadata;

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public JsonNode? Json => string.IsNullOrEmpty(Body) ? null : JsonNode.Parse(Body);

        public string Query(string name)
        {
            var query = new Uri(Url).Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv[0] == name) return Uri.UnescapeDataString(kv.Length == 2 ? kv[1] : string.Empty);
            }
            return string.Empty;
        }

        public Dictionary<string, string> Form()
            => Body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('=', 2))
                .ToDictionary(e => WebUtility.UrlDecode(e[0]), e => WebUtility.UrlDecode(e.Length == 2 ? e[1] : string.Empty));
    }

    /// <summary>SuiteQL の 1 回の要求。</summary>
    public class SuiteQlCall
    {
        public string Q { get; init; } = string.Empty;
        public List<string> Params { get; init; } = new();
        public int Limit { get; init; }
        public int Offset { get; init; }
        public bool HasParamsProperty { get; init; }
    }

    /// <summary>SuiteQL の応答。</summary>
    public class SuiteQlResult
    {
        public List<Dictionary<string, string>> Items { get; init; } = new();
        public bool HasMore { get; init; }
    }

    /// <summary>
    /// NetSuite の偽物 (HttpMessageHandler)。要求を記録し、トークン・メタデータ・SuiteQL・レコードの書き込みに応答する。
    /// </summary>
    public class FakeNetSuite : HttpMessageHandler
    {
        public const string ServiceUrl = "https://1234567.suitetalk.api.netsuite.com";
        public const string RestUrl = ServiceUrl + "/services/rest";
        public const string TokenUrl = RestUrl + "/auth/oauth2/v1/token";

        readonly object _lock = new();
        readonly List<(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond, int Remaining)> _overrides = new();
        int _tokenCount;

        public List<RecordedRequest> Requests { get; } = new();
        public List<SuiteQlCall> SuiteQlCalls { get; } = new();

        /// <summary>トークン以外の要求。</summary>
        public List<RecordedRequest> ApiRequests => Requests.Where(e => !e.IsToken).ToList();
        public List<RecordedRequest> TokenRequests => Requests.Where(e => e.IsToken).ToList();
        public List<RecordedRequest> WriteRequests => Requests.Where(e => e.IsRecordWrite).ToList();

        /// <summary>トークンの expires_in (秒)。</summary>
        public int ExpiresIn { get; set; } = 3600;

        /// <summary>POST で作ったレコードの Id (Location に入れる)。</summary>
        public string CreatedId { get; set; } = "123";

        /// <summary>レコード種別 → JSON Schema。</summary>
        public Dictionary<string, string> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = CustomerSchema,
            ["subsidiary"] = SubsidiarySchema,
        };

        /// <summary>SuiteQL が返す行 (limit / offset で切り出す)。<see cref="SuiteQl"/> があればそちらを使う。</summary>
        public List<Dictionary<string, string>> Rows { get; set; } = new();

        /// <summary>SuiteQL の応答を要求ごとに決める。</summary>
        public Func<SuiteQlCall, SuiteQlResult>? SuiteQl { get; set; }

        public const string CustomerSchema = """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "companyName": { "type": "string" },
                "isInactive": { "type": "boolean" },
                "creditLimit": { "type": "number" },
                "startDate": { "type": "string", "format": "date" },
                "lastModifiedDate": { "type": "string", "format": "date-time" },
                "subsidiary": { "type": "object", "properties": { "id": { "type": "string" }, "refName": { "type": "string" } } },
                "addressBook": { "$ref": "/services/rest/record/v1/metadata-catalog/customer-addressBookCollection" },
                "links": { "type": "array", "items": { "type": "object" } },
                "refName": { "type": "string" }
              }
            }
            """;

        public const string SubsidiarySchema = """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "name": { "type": "string" }
              }
            }
            """;

        /// <summary>条件に合う要求に、times 回だけ別の応答を返す。</summary>
        public void Override(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> respond, int times = 1)
        {
            lock (_lock) _overrides.Add((match, respond, times));
        }

        /// <summary>条件に合う要求で、times 回だけ例外を投げる (通信の失敗)。</summary>
        public void Throw(Func<RecordedRequest, bool> match, Func<Exception> exception, int times = 1)
            => Override(match, _ => throw exception(), times);

        public static HttpResponseMessage Status(HttpStatusCode code, string? body = null)
        {
            var response = new HttpResponseMessage(code);
            if (body != null) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }

        public static HttpResponseMessage ErrorDetails(HttpStatusCode code, params (string Detail, string ErrorCode, string ErrorPath)[] details)
        {
            var array = new JsonArray();
            foreach (var d in details)
            {
                array.Add(new JsonObject { ["detail"] = d.Detail, ["o:errorCode"] = d.ErrorCode, ["o:errorPath"] = d.ErrorPath });
            }
            var body = new JsonObject
            {
                ["type"] = "https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.1",
                ["title"] = "Bad Request",
                ["status"] = (int)code,
                ["o:errorDetails"] = array,
            };
            return Status(code, body.ToJsonString());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Url = request.RequestUri!.ToString(),
                Path = request.RequestUri!.AbsolutePath,
                Body = body,
            };
            foreach (var h in request.Headers) recorded.Headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null) foreach (var h in request.Content.Headers) recorded.Headers[h.Key] = string.Join(",", h.Value);

            Func<RecordedRequest, HttpResponseMessage>? respond = null;
            lock (_lock)
            {
                Requests.Add(recorded);
                for (var i = 0; i < _overrides.Count; i++)
                {
                    var o = _overrides[i];
                    if (!o.Match(recorded)) continue;
                    respond = o.Respond;
                    if (o.Remaining <= 1) _overrides.RemoveAt(i);
                    else _overrides[i] = (o.Match, o.Respond, o.Remaining - 1);
                    break;
                }
            }
            if (respond != null) return respond(recorded);
            return Default(recorded);
        }

        HttpResponseMessage Default(RecordedRequest request)
        {
            if (request.IsToken && request.Method == HttpMethod.Post)
            {
                var n = Interlocked.Increment(ref _tokenCount);
                return Status(HttpStatusCode.OK, $$"""{"access_token":"token-{{n}}","expires_in":"{{ExpiresIn}}","token_type":"Bearer"}""");
            }
            if (request.IsMetadata && request.Method == HttpMethod.Get)
            {
                var type = Uri.UnescapeDataString(request.Path.Split('/').Last());
                if (!Schemas.TryGetValue(type, out var schema)) return ErrorDetails(HttpStatusCode.NotFound, ("Record type not found.", "NONEXISTENT_ID", ""));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(schema, Encoding.UTF8) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/schema+json");
                return response;
            }
            if (request.IsSuiteQl && request.Method == HttpMethod.Post)
            {
                var json = JsonNode.Parse(request.Body)!.AsObject();
                var call = new SuiteQlCall
                {
                    Q = json["q"]!.GetValue<string>(),
                    Params = json["params"]?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? new(),
                    HasParamsProperty = json.ContainsKey("params"),
                    Limit = int.Parse(request.Query("limit")),
                    Offset = int.Parse(request.Query("offset")),
                };
                lock (_lock) SuiteQlCalls.Add(call);
                var result = SuiteQl?.Invoke(call) ?? new SuiteQlResult
                {
                    Items = Rows.Skip(call.Offset).Take(call.Limit).ToList(),
                    HasMore = call.Offset + call.Limit < Rows.Count,
                };
                var items = new JsonArray();
                foreach (var row in result.Items)
                {
                    var item = new JsonObject { ["links"] = new JsonArray() };
                    foreach (var kv in row) item[kv.Key] = kv.Value;
                    items.Add(item);
                }
                var page = new JsonObject
                {
                    ["links"] = new JsonArray(),
                    ["count"] = result.Items.Count,
                    ["hasMore"] = result.HasMore,
                    ["items"] = items,
                    ["offset"] = call.Offset,
                    ["totalResults"] = Rows.Count,
                };
                return Status(HttpStatusCode.OK, page.ToJsonString());
            }
            if (request.IsRecordWrite)
            {
                var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                if (request.Method == HttpMethod.Post)
                {
                    var type = request.Path.Split('/').Last();
                    response.Headers.Location = new Uri($"{RestUrl}/record/v1/{type}/{CreatedId}");
                }
                return response;
            }
            return Status(HttpStatusCode.NotFound, """{"title":"Not Found"}""");
        }
    }
}
