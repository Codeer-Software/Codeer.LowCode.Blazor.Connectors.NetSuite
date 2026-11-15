using System.Text;

namespace NetSuite.Mock
{
    /// <summary>モックの REST の入口 (/services/rest/...) を結び付ける。</summary>
    public static class MockNetSuiteEndpoints
    {
        sealed class Result : IResult
        {
            readonly MockResponse _response;
            public Result(MockResponse response) => _response = response;

            public async Task ExecuteAsync(HttpContext context)
            {
                context.Response.StatusCode = _response.Status;
                foreach (var header in _response.Headers) context.Response.Headers[header.Key] = header.Value;
                if (_response.Body != null)
                {
                    context.Response.ContentType = _response.ContentType + "; charset=utf-8";
                    await context.Response.WriteAsync(_response.Body, Encoding.UTF8);
                }
            }
        }

        static IResult ToResult(MockResponse response) => new Result(response);

        static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

        static async Task<string> ReadBodyAsync(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        public static IEndpointRouteBuilder MapMockNetSuite(this IEndpointRouteBuilder app, MockNetSuite mock)
        {
            var rest = app.MapGroup("/services/rest");

            rest.MapPost("/auth/oauth2/v1/token", async (HttpRequest request) =>
            {
                var authorization = request.Headers.Authorization.ToString();
                if (!request.HasFormContentType)
                    return ToResult(mock.IssueToken(new Dictionary<string, string>(), authorization, BaseUrl(request)));
                var form = await request.ReadFormAsync();
                return ToResult(mock.IssueToken(form.ToDictionary(e => e.Key, e => e.Value.ToString()), authorization, BaseUrl(request)));
            });

            //OpenID Connect (アプリのログイン)。discovery・鍵・認可の画面。iss と各 URL は要求を受けた URL から作る
            app.MapGet("/.well-known/openid-configuration", (HttpRequest request) => ToResult(mock.GetOpenIdConfiguration(BaseUrl(request))));
            rest.MapGet("/auth/oauth2/v1/keys", () => ToResult(mock.GetSigningKeys()));
            app.MapGet(MockNetSuite.AuthorizePath, (HttpRequest request)
                => ToResult(mock.Authorize(request.Query.ToDictionary(e => e.Key, e => e.Value.ToString()), false)));
            app.MapPost(MockNetSuite.AuthorizePath, async (HttpRequest request) =>
            {
                var form = request.HasFormContentType ? await request.ReadFormAsync() : null;
                return ToResult(mock.Authorize(form?.ToDictionary(e => e.Key, e => e.Value.ToString()) ?? new Dictionary<string, string>(), true));
            });

            //認証が要る入口
            IResult? Authenticate(HttpRequest request)
            {
                var failed = mock.Authenticate(request.Headers.Authorization.ToString());
                return failed == null ? null : ToResult(failed);
            }

            rest.MapGet("/record/v1/metadata-catalog", (HttpRequest request)
                => Authenticate(request) ?? ToResult(mock.GetMetadataCatalog(BaseUrl(request))));

            rest.MapGet("/record/v1/metadata-catalog/{type}", (HttpRequest request, string type)
                => Authenticate(request) ?? ToResult(mock.GetMetadata(type, request.Headers.Accept.ToString(), BaseUrl(request))));

            rest.MapPost("/query/v1/suiteql", async (HttpRequest request) =>
            {
                var failed = Authenticate(request);
                if (failed != null) return failed;
                return ToResult(mock.QuerySuiteQl(request.Query["limit"].FirstOrDefault(), request.Query["offset"].FirstOrDefault(),
                    request.Headers["Prefer"].ToString(), await ReadBodyAsync(request)));
            });

            rest.MapPost("/record/v1/{type}", async (HttpRequest request, string type) =>
            {
                var failed = Authenticate(request);
                if (failed != null) return failed;
                return ToResult(mock.CreateRecord(type, await ReadBodyAsync(request), BaseUrl(request)));
            });

            rest.MapGet("/record/v1/{type}/{id}", (HttpRequest request, string type, string id)
                => Authenticate(request) ?? ToResult(mock.GetRecord(type, id, BaseUrl(request),
                    string.Equals(request.Query["expandSubResources"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase))));

            rest.MapMethods("/record/v1/{type}/{id}", new[] { "PATCH" }, async (HttpRequest request, string type, string id) =>
            {
                var failed = Authenticate(request);
                if (failed != null) return failed;
                return ToResult(mock.UpdateRecord(type, id, await ReadBodyAsync(request), request.Query["replace"].FirstOrDefault()));
            });

            rest.MapDelete("/record/v1/{type}/{id}", (HttpRequest request, string type, string id)
                => Authenticate(request) ?? ToResult(mock.DeleteRecord(type, id)));

            //それ以外の /services/rest/...
            rest.MapFallback((HttpRequest request)
                => ToResult(MockNetSuite.Error(404, "INVALID_REQUEST", $"This mock does not support {request.Method} {request.Path}.")));

            return app;
        }
    }
}
