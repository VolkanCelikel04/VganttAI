using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

LoadLocalEnvFile();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await ApplyRegistryMigrationAsync();
    Console.WriteLine("Registry migration applied.");
    return;
}

if (args.Contains("--seed-admin", StringComparer.OrdinalIgnoreCase))
{
    await SeedAdminAsync();
    Console.WriteLine("Admin user seeded.");
    return;
}

var port = int.TryParse(Environment.GetEnvironmentVariable("VGANTT_API_PORT"), out var configuredPort)
    ? configuredPort
    : 5055;

var bindHost = Environment.GetEnvironmentVariable("VGANTT_API_BIND_HOST") ?? "127.0.0.1";
var bindAddress = bindHost switch
{
    "*" or "0.0.0.0" => IPAddress.Any,
    "::" => IPAddress.IPv6Any,
    _ when IPAddress.TryParse(bindHost, out var parsedAddress) => parsedAddress,
    _ => throw new InvalidOperationException($"VGANTT_API_BIND_HOST is not a valid IP address: {bindHost}")
};

var listener = new TcpListener(bindAddress, port);
listener.Start();

Console.WriteLine($"Vgantt ERP AI API listening on http://{bindHost}:{port}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(client));
}

static async Task HandleClientAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            var request = await HttpRequestData.ReadAsync(client.GetStream());
            var response = await RouteAsync(request);
            await response.WriteAsync(client.GetStream());
        }
        catch (Exception error)
        {
            await HttpResponseData.Json(HttpStatusCode.InternalServerError, new { message = error.Message })
                .WriteAsync(client.GetStream());
        }
    }
}

static async Task<HttpResponseData> RouteAsync(HttpRequestData request)
{
    if (request.Method == "OPTIONS")
    {
        return HttpResponseData.Json(HttpStatusCode.NoContent, new { });
    }

    if (request.Method == "GET" && request.Path == "/health")
    {
        return HttpResponseData.Json(HttpStatusCode.OK, new
        {
            status = "ok",
            service = "Vgantt ERP AI API",
            utc = DateTimeOffset.UtcNow
        });
    }

    if (request.Method == "GET" && request.Path == "/db/health")
    {
        await using var connection = await OpenRegistryConnectionAsync();
        await using var command = new NpgsqlCommand("select version()", connection);
        var version = (string?)await command.ExecuteScalarAsync();

        return HttpResponseData.Json(HttpStatusCode.OK, new
        {
            status = "ok",
            provider = "postgresql",
            version
        });
    }

    if (request.Method == "GET" && request.Path == "/admin")
    {
        var adminSession = await ValidateAdminWebSessionAsync(request);
        return adminSession is null
            ? HttpResponseData.Html(HttpStatusCode.OK, BuildAdminLoginPage())
            : HttpResponseData.Redirect("/admin/dashboard");
    }

    if (request.Path.StartsWith("/admin/api/", StringComparison.OrdinalIgnoreCase))
    {
        return await RouteAdminApiAsync(request);
    }

    if (request.Method == "POST" && request.Path == "/admin/session")
    {
        var login = request.ReadJson<LoginRequest>();
        if (login is null || string.IsNullOrWhiteSpace(login.Username) || string.IsNullOrWhiteSpace(login.Password))
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "Username and password are required." });
        }

        var authenticatedUser = await AuthenticateAsync(login.Username, login.Password);
        if (authenticatedUser is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid username or password." });
        }

        if (!string.Equals(authenticatedUser.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return HttpResponseData.Json(HttpStatusCode.Forbidden, new { message = "Admin role is required." });
        }

        var token = JwtTokenService.Create(authenticatedUser);
        await CreateMobileLoginSessionAsync(authenticatedUser, login, token);

        return HttpResponseData.Json(
            HttpStatusCode.OK,
            new LoginResponse(
                Token: token,
                TenantName: authenticatedUser.TenantName,
                UserDisplayName: authenticatedUser.DisplayName,
                Role: authenticatedUser.Role
            ),
            new Dictionary<string, string>
            {
                ["Set-Cookie"] = BuildAdminSessionCookie(token)
            }
        );
    }

    if (request.Method == "GET" && request.Path == "/admin/logout")
    {
        return HttpResponseData.Redirect("/admin", new Dictionary<string, string>
        {
            ["Set-Cookie"] = ClearAdminSessionCookie()
        });
    }

    if (request.Method == "GET" &&
        (request.Path == "/admin/dashboard" ||
         request.Path == "/admin/settings/views" ||
         request.Path == "/admin/settings/columns" ||
         request.Path == "/admin/settings/relations" ||
         request.Path == "/admin/sales-views"))
    {
        var adminSession = await ValidateAdminWebSessionAsync(request);
        if (adminSession is null)
        {
            return HttpResponseData.Redirect("/admin");
        }

        var activePage = request.Path switch
        {
            "/admin/dashboard" => "dashboard",
            "/admin/settings/columns" => "columns",
            "/admin/settings/relations" => "relations",
            _ => "views"
        };
        return HttpResponseData.Html(HttpStatusCode.OK, BuildAdminAppPage(adminSession, activePage));
    }

    if (request.Method == "POST" && request.Path == "/admin/migrate")
    {
        var schemaPath = ResolveRegistrySchemaPath();
        if (!File.Exists(schemaPath))
        {
            return HttpResponseData.Json(HttpStatusCode.InternalServerError, new { message = $"Schema file not found: {schemaPath}" });
        }

        var sql = await File.ReadAllTextAsync(schemaPath);
        await using var connection = await OpenRegistryConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();

        return HttpResponseData.Json(HttpStatusCode.OK, new
        {
            status = "ok",
            applied = Path.GetFileName(schemaPath)
        });
    }

    if (request.Method == "POST" && request.Path == "/auth/login")
    {
        var login = request.ReadJson<LoginRequest>();
        if (login is null || string.IsNullOrWhiteSpace(login.Username) || string.IsNullOrWhiteSpace(login.Password))
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "Username and password are required." });
        }

        var authenticatedUser = await AuthenticateAsync(login.Username, login.Password);
        if (authenticatedUser is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid username or password." });
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await CreateMobileLoginSessionAsync(authenticatedUser, login, token);

        return HttpResponseData.Json(HttpStatusCode.OK, new LoginResponse(
            Token: token,
            TenantName: authenticatedUser.TenantName,
            UserDisplayName: authenticatedUser.DisplayName,
            Role: authenticatedUser.Role
        ));
    }

    if (request.Method == "POST" && request.Path == "/admin/sales-views")
    {
        var session = await ValidateAccessTokenAsync(request.BearerToken ?? request.Cookie("vgantt_admin_session"));
        if (session is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid or expired session." });
        }

        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return HttpResponseData.Json(HttpStatusCode.Forbidden, new { message = "Admin role is required." });
        }

        var views = request.ReadJson<SaveSalesViewsRequest>();
        if (views is null ||
            string.IsNullOrWhiteSpace(views.EffectiveView1Name) ||
            string.IsNullOrWhiteSpace(views.EffectiveView2Name) ||
            views.EffectiveRelationships.Count == 0)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "View1, View2 and at least one relationship are required." });
        }

        var saved = await SaveSalesViewsAsync(session, views);
        return HttpResponseData.Json(HttpStatusCode.OK, saved);
    }

    if (request.Method == "POST" && request.Path == "/admin/column-meanings")
    {
        var session = await ValidateAccessTokenAsync(request.BearerToken ?? request.Cookie("vgantt_admin_session"));
        if (session is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid or expired session." });
        }

        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return HttpResponseData.Json(HttpStatusCode.Forbidden, new { message = "Admin role is required." });
        }

        var requestBody = request.ReadJson<SaveColumnMeaningsRequest>();
        if (requestBody is null ||
            string.IsNullOrWhiteSpace(requestBody.ViewName) ||
            requestBody.Meanings is null ||
            requestBody.Meanings.Length == 0)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "View name and column meanings are required." });
        }

        var saved = await SaveColumnMeaningsAsync(session, requestBody);
        return HttpResponseData.Json(HttpStatusCode.OK, saved);
    }

    if (request.Method == "POST" && request.Path == "/assistant/ask")
    {
        var session = await ValidateAccessTokenAsync(request.BearerToken);
        if (session is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid or expired session." });
        }

        var assistant = request.ReadJson<AssistantRequest>();
        if (assistant is null || string.IsNullOrWhiteSpace(assistant.Question))
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "Question is required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await AnswerAssistantQuestionAsync(session, assistant.Question, assistant.History ?? []));
    }

    if (request.Method == "GET" && request.Path == "/tenant/db/health")
    {
        var session = await ValidateAccessTokenAsync(request.BearerToken);
        if (session is null)
        {
            return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid or expired session." });
        }

        try
        {
            return HttpResponseData.Json(HttpStatusCode.OK, await CheckTenantDbHealthAsync(session.TenantId));
        }
        catch (InvalidOperationException error)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = error.Message });
        }
        catch (Exception error)
        {
            return HttpResponseData.Json(HttpStatusCode.BadGateway, new { message = error.Message });
        }
    }

    return HttpResponseData.Json(HttpStatusCode.NotFound, new { message = "Not found." });
}

static async Task<HttpResponseData> RouteAdminApiAsync(HttpRequestData request)
{
    if (request.Method == "POST" && request.Path == "/admin/api/login")
    {
        return await CreateAdminLoginResponseAsync(request);
    }

    var session = await ValidateAccessTokenAsync(request.BearerToken ?? request.Cookie("vgantt_admin_session"));
    if (session is null)
    {
        return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid or expired session." });
    }

    if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
    {
        return HttpResponseData.Json(HttpStatusCode.Forbidden, new { message = "Admin role is required." });
    }

    if (request.Method == "GET" && request.Path == "/admin/api/me")
    {
        return HttpResponseData.Json(HttpStatusCode.OK, new
        {
            userDisplayName = session.DisplayName,
            tenantName = session.TenantName,
            tenantCode = session.TenantCode,
            role = session.Role
        });
    }

    if (request.Method == "GET" && request.Path == "/admin/api/dashboard")
    {
        return HttpResponseData.Json(HttpStatusCode.OK, await LoadAdminDashboardAsync(session.TenantId));
    }

    if (request.Method == "GET" && request.Path == "/admin/api/tenant-db-health")
    {
        try
        {
            return HttpResponseData.Json(HttpStatusCode.OK, await CheckTenantDbHealthAsync(session.TenantId));
        }
        catch (InvalidOperationException error)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = error.Message });
        }
        catch (Exception error)
        {
            return HttpResponseData.Json(HttpStatusCode.BadGateway, new { message = error.Message });
        }
    }

    if (request.Method == "GET" && request.Path == "/admin/api/views")
    {
        return HttpResponseData.Json(HttpStatusCode.OK, await LoadAdminViewsAsync(session.TenantId));
    }

    if (request.Method == "POST" && request.Path == "/admin/api/views")
    {
        var view = request.ReadJson<SaveAdminViewRequest>();
        if (view is null || string.IsNullOrWhiteSpace(view.ViewName))
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "view_name is required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await SaveAdminViewAsync(session, view));
    }

    if (request.Method == "GET" && request.Path == "/admin/api/view-columns")
    {
        if (!Guid.TryParse(request.QueryValue("viewId"), out var viewId))
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "viewId is required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await LoadAdminViewColumnsAsync(session.TenantId, viewId));
    }

    if (request.Method == "POST" && request.Path == "/admin/api/view-columns")
    {
        var columns = request.ReadJson<SaveAdminViewColumnsRequest>();
        if (columns is null || columns.ViewId == Guid.Empty)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "view_id is required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await SaveAdminViewColumnsAsync(session, columns));
    }

    if (request.Method == "POST" && request.Path == "/admin/api/view-columns/normalize")
    {
        var normalizeRequest = request.ReadJson<NormalizeAdminViewColumnsRequest>();
        if (normalizeRequest is null || normalizeRequest.ViewId == Guid.Empty)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "view_id is required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await NormalizeAdminViewColumnsAsync(session, normalizeRequest));
    }

    if (request.Method == "GET" && request.Path == "/admin/api/relations")
    {
        return HttpResponseData.Json(HttpStatusCode.OK, await LoadAdminRelationsAsync(session.TenantId));
    }

    if (request.Method == "POST" && request.Path == "/admin/api/relations")
    {
        var relation = request.ReadJson<SaveAdminRelationRequest>();
        if (relation is null ||
            string.IsNullOrWhiteSpace(relation.RelationName) ||
            relation.SourceViewId == Guid.Empty ||
            relation.TargetViewId == Guid.Empty ||
            relation.Columns is null ||
            relation.Columns.Length == 0)
        {
            return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "relation_name, source_view_id, target_view_id and relation columns are required." });
        }

        return HttpResponseData.Json(HttpStatusCode.OK, await SaveAdminRelationAsync(session, relation));
    }

    return HttpResponseData.Json(HttpStatusCode.NotFound, new { message = "Not found." });
}

static async Task<HttpResponseData> CreateAdminLoginResponseAsync(HttpRequestData request)
{
    var login = request.ReadJson<LoginRequest>();
    if (login is null || string.IsNullOrWhiteSpace(login.Username) || string.IsNullOrWhiteSpace(login.Password))
    {
        return HttpResponseData.Json(HttpStatusCode.BadRequest, new { message = "Username and password are required." });
    }

    var authenticatedUser = await AuthenticateAsync(login.Username, login.Password);
    if (authenticatedUser is null)
    {
        return HttpResponseData.Json(HttpStatusCode.Unauthorized, new { message = "Invalid username or password." });
    }

    if (!string.Equals(authenticatedUser.Role, "admin", StringComparison.OrdinalIgnoreCase))
    {
        return HttpResponseData.Json(HttpStatusCode.Forbidden, new { message = "Admin role is required." });
    }

    var token = JwtTokenService.Create(authenticatedUser);
    await CreateMobileLoginSessionAsync(authenticatedUser, login, token);

    return HttpResponseData.Json(
        HttpStatusCode.OK,
        new LoginResponse(
            Token: token,
            TenantName: authenticatedUser.TenantName,
            UserDisplayName: authenticatedUser.DisplayName,
            Role: authenticatedUser.Role
        ),
        new Dictionary<string, string>
        {
            ["Set-Cookie"] = BuildAdminSessionCookie(token)
        }
    );
}

static async Task<NpgsqlConnection> OpenRegistryConnectionAsync()
{
    var connectionString = Environment.GetEnvironmentVariable("VGANTT_REGISTRY_CONNECTION");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("VGANTT_REGISTRY_CONNECTION is missing.");
    }

    var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    return connection;
}

static async Task<AuthenticatedUser?> ValidateAdminWebSessionAsync(HttpRequestData request)
{
    var session = await ValidateAccessTokenAsync(request.Cookie("vgantt_admin_session"));
    return session is not null && string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase)
        ? session
        : null;
}

static string BuildAdminSessionCookie(string token)
{
    return $"vgantt_admin_session={Uri.EscapeDataString(token)}; Path=/admin; Max-Age=28800; HttpOnly; SameSite=Strict";
}

static string ClearAdminSessionCookie()
{
    return "vgantt_admin_session=; Path=/admin; Max-Age=0; HttpOnly; SameSite=Strict";
}

static string BuildAdminLoginPage()
{
    return """
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vgantt AI Admin Giris</title>
  <style>
    :root {
      color-scheme: light;
      font-family: Segoe UI, Arial, sans-serif;
      background: #f6f7f9;
      color: #1f2933;
      --line: #d9e0e7;
      --muted: #65707c;
      --primary: #16697a;
      --primary-dark: #0f5361;
      --surface: #ffffff;
      --danger: #b42318;
    }

    * {
      box-sizing: border-box;
    }

    body {
      margin: 0;
      background: #f6f7f9;
      min-height: 100vh;
      display: grid;
      place-items: center;
      padding: 24px;
    }

    .panel {
      width: min(440px, 100%);
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 24px;
    }

    h1 {
      font-size: 24px;
      margin: 0 0 4px;
    }

    p {
      margin: 0;
      color: var(--muted);
    }

    form {
      display: grid;
      gap: 14px;
      margin-top: 22px;
    }

    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
      color: #344054;
    }

    input {
      border: 1px solid #c8d0d9;
      border-radius: 6px;
      font: inherit;
      padding: 10px 12px;
    }

    button {
      justify-self: start;
      border: 0;
      border-radius: 6px;
      background: var(--primary);
      color: #ffffff;
      font: inherit;
      font-weight: 700;
      padding: 10px 16px;
      cursor: pointer;
    }

    button:hover {
      background: var(--primary-dark);
    }

    button:disabled {
      background: #8aa4ad;
      cursor: wait;
    }

    #message {
      min-height: 22px;
      color: var(--danger);
      font-size: 14px;
      font-weight: 600;
    }
  </style>
</head>
<body>
  <main class="panel">
    <h1>Vgantt AI Admin</h1>
    <p>Yonetim ekranina admin kullanici ile giris yapin.</p>
    <form id="loginForm">
      <label>
        Kullanici
        <input id="username" autocomplete="username" value="volkan.celikel@vgantt.com" required>
      </label>
      <label>
        Sifre
        <input id="password" type="password" autocomplete="current-password" required>
      </label>
      <button id="loginButton" type="submit">Giris yap</button>
      <div id="message" role="status"></div>
    </form>
  </main>
  <script>
    const form = document.getElementById('loginForm');
    const button = document.getElementById('loginButton');
    const message = document.getElementById('message');

    function value(id) {
      return document.getElementById(id).value.trim();
    }

    async function readError(response) {
      try {
        const payload = await response.json();
        return payload.message || response.statusText;
      } catch {
        return response.statusText;
      }
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      button.disabled = true;
      message.textContent = 'Giris yapiliyor...';

      try {
        const response = await fetch('/admin/api/login', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            username: value('username'),
            password: value('password')
          })
        });

        if (!response.ok) {
          throw new Error(await readError(response));
        }

        const login = await response.json();
        localStorage.setItem('vgantt_admin_token', login.token);
        window.location.href = '/admin/dashboard';
      } catch (error) {
        message.textContent = error.message || String(error);
      } finally {
        button.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}

static string BuildAdminAppPage(AuthenticatedUser session, string activePage)
{
    var tenantName = WebUtility.HtmlEncode(session.TenantName);
    var displayName = WebUtility.HtmlEncode(session.DisplayName);
    var role = WebUtility.HtmlEncode(session.Role);
    var activePageJson = JsonSerializer.Serialize(activePage, AppJson.Options);
    var viewsActive = activePage == "views" ? "active" : "";
    var columnsActive = activePage == "columns" ? "active" : "";
    var relationsActive = activePage == "relations" ? "active" : "";
    var pageTitle = activePage switch
    {
        "columns" => "Kolonlar",
        "relations" => "Iliskiler",
        _ => "Viewler"
    };
    var pageSubtitle = activePage switch
    {
        "columns" => "Secili view icin kolon sozlugunu yonetin.",
        "relations" => "Viewler arasindaki iliskileri yonetin.",
        _ => "ERP view tanimlarini yonetin."
    };

    return $$"""
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vgantt AI Admin</title>
  <style>
    :root {
      color-scheme: light;
      font-family: Segoe UI, Arial, sans-serif;
      background: #f4f6f8;
      color: #18232f;
      --line: #d7dee7;
      --muted: #667085;
      --primary: #146c5f;
      --primary-dark: #0f554b;
      --surface: #ffffff;
      --danger: #b42318;
      --success: #16794c;
      --warning: #8a5a00;
      --soft: #f8fafc;
    }

    * { box-sizing: border-box; }
    body { margin: 0; background: #f4f6f8; }
    .app-shell { min-height: 100vh; display: grid; grid-template-columns: 240px minmax(0, 1fr); }
    .sidebar { background: #18232f; color: #f9fafb; padding: 18px 14px; }
    .brand { display: grid; gap: 2px; padding: 8px 8px 18px; border-bottom: 1px solid rgba(255,255,255,.14); }
    .brand strong { font-size: 18px; }
    .brand span { color: #b8c4cc; font-size: 12px; }
    .nav-section { margin-top: 18px; }
    .nav-title { color: #b8c4cc; font-size: 12px; font-weight: 700; margin: 0 8px 8px; text-transform: uppercase; }
    .nav-item { display: flex; align-items: center; color: #fff; border-radius: 8px; padding: 10px 12px; font-weight: 700; text-decoration: none; margin-bottom: 6px; }
    .nav-item.active { background: rgba(255,255,255,.14); }
    .nav-item:hover { background: rgba(255,255,255,.09); }
    .content { min-width: 0; }
    .topbar { min-height: 65px; background: var(--surface); border-bottom: 1px solid var(--line); display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 14px 24px; }
    h1 { font-size: 24px; margin: 0 0 4px; }
    h2 { font-size: 18px; margin: 0 0 12px; }
    p { margin: 0; color: var(--muted); }
    main { width: min(1280px, calc(100% - 32px)); margin: 24px auto; display: grid; gap: 16px; }
    .user-meta { display: grid; gap: 8px; color: var(--muted); font-size: 13px; text-align: right; }
    .logout { color: var(--primary); font-weight: 700; text-decoration: none; border: 0; background: transparent; cursor: pointer; padding: 0; }
    .section { background: var(--surface); border: 1px solid var(--line); border-radius: 8px; padding: 20px; }
    .hidden { display: none; }
    .grid-2 { display: grid; grid-template-columns: minmax(330px, 420px) minmax(0, 1fr); gap: 16px; align-items: start; }
    .form-section { position: sticky; top: 16px; }
    .stats { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
    .stat { border: 1px solid var(--line); border-radius: 8px; padding: 16px; background: #fff; }
    .stat strong { display: block; font-size: 28px; margin-bottom: 4px; }
    form { display: grid; gap: 12px; }
    label { display: grid; gap: 6px; font-size: 13px; font-weight: 700; color: #344054; }
    input, textarea, select { border: 1px solid #c8d0d9; border-radius: 8px; font: inherit; padding: 10px 11px; min-width: 0; background: #fff; }
    input:focus, textarea:focus, select:focus { border-color: var(--primary); box-shadow: 0 0 0 3px rgba(20,108,95,.14); outline: none; }
    textarea { min-height: 92px; resize: vertical; }
    .check-row { display: flex; gap: 16px; flex-wrap: wrap; }
    .check-row label { display: flex; align-items: center; gap: 6px; border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; background: var(--soft); }
    details.advanced { border: 1px solid var(--line); border-radius: 8px; background: var(--soft); padding: 10px 12px; }
    details.advanced summary { cursor: pointer; font-weight: 800; color: #344054; }
    details.advanced .advanced-body { display: grid; gap: 12px; padding-top: 12px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { border-bottom: 1px solid var(--line); padding: 10px; text-align: left; vertical-align: middle; }
    th { color: #344054; font-size: 13px; background: #f8fafc; }
    td.mono { font-family: Consolas, monospace; font-size: 13px; }
    .table-wrap { overflow: auto; border: 1px solid var(--line); border-radius: 8px; }
    tbody tr { cursor: pointer; }
    tbody tr:hover { background: #f8fafc; }
    tbody tr.is-selected { background: #eef8f5; }
    .toolbar { display: flex; align-items: end; justify-content: space-between; gap: 12px; margin-bottom: 12px; }
    .button-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    button.primary, button.secondary, button.icon { border-radius: 8px; font: inherit; font-weight: 700; padding: 10px 13px; cursor: pointer; }
    button.primary { border: 0; background: var(--primary); color: #fff; }
    button.primary:hover { background: var(--primary-dark); }
    button.secondary, button.icon { border: 1px solid var(--line); background: #fff; color: #1f2933; }
    button.secondary:hover, button.icon:hover { border-color: #aab5c2; background: #f8fafc; }
    button:disabled { opacity: .65; cursor: wait; }
    .message { min-height: 22px; font-size: 14px; font-weight: 600; }
    .message.ok { color: var(--success); }
    .message.error { color: var(--danger); }
    .status { display: inline-flex; align-items: center; justify-content: center; min-width: 58px; border-radius: 999px; padding: 4px 9px; font-size: 12px; font-weight: 800; }
    .status.active { background: #e7f6ee; color: var(--success); }
    .status.passive { background: #fff3cd; color: var(--warning); }
    .relation-columns { display: grid; gap: 8px; }
    .relation-column-row { display: grid; grid-template-columns: 64px minmax(0, 1fr) 28px minmax(0, 1fr) 56px; gap: 8px; align-items: end; }
    .equals { align-self: center; text-align: center; color: var(--muted); font-weight: 700; }

    @media (max-width: 900px) {
      .app-shell, .grid-2, .stats, .relation-column-row { grid-template-columns: 1fr; }
      .form-section { position: static; }
      .sidebar { padding: 12px; }
      .topbar, .toolbar { display: grid; }
      .user-meta { text-align: left; }
    }
  </style>
</head>
<body>
  <section class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <strong>Vgantt AI Admin</strong>
        <span>{{tenantName}}</span>
      </div>
      <nav class="nav-section">
        <p class="nav-title">Admin</p>
        <a class="nav-item {{viewsActive}}" href="/admin/settings/views">Viewler</a>
        <a class="nav-item {{columnsActive}}" href="/admin/settings/columns">Kolonlar</a>
        <a class="nav-item {{relationsActive}}" href="/admin/settings/relations">Iliskiler</a>
      </nav>
    </aside>
    <div class="content">
      <header class="topbar">
        <div>
          <h1 id="pageTitle">{{pageTitle}}</h1>
          <p id="pageSubtitle">{{pageSubtitle}}</p>
        </div>
        <div class="user-meta">
          <span>{{displayName}} / {{role}}</span>
          <button id="logoutButton" class="logout" type="button">Cikis yap</button>
        </div>
      </header>
      <main>
        <section id="viewsPage" class="grid-2 hidden">
          <div class="section form-section">
            <h2>View Bilgisi</h2>
            <form id="viewForm">
              <input id="viewId" type="hidden">
              <label>Veritabanindaki View Adi<input id="viewName" placeholder="vgantt_ai_customer_order" required></label>
              <label>Ekran Adi<input id="viewDisplayNameTr" placeholder="Musteri Siparisleri"></label>
              <label>Aciklama<textarea id="viewDescriptionTr" placeholder="Satis siparis baslik bilgileri"></textarea></label>
              <div class="check-row"><label><input id="viewIsActive" type="checkbox" checked> Aktif</label></div>
              <div class="button-row">
                <button class="primary" type="submit">Kaydet</button>
                <button id="newViewButton" class="secondary" type="button">Yeni</button>
              </div>
              <div id="viewMessage" class="message" role="status"></div>
            </form>
          </div>
          <div class="section">
            <div class="toolbar">
              <h2>View Listesi</h2>
              <button id="refreshViewsButton" class="secondary" type="button">Yenile</button>
            </div>
            <div class="table-wrap">
              <table>
                <thead><tr><th>ID</th><th>Teknik Ad</th><th>Ekran Adi</th><th>Durum</th><th>Islem</th></tr></thead>
                <tbody id="viewsBody"></tbody>
              </table>
            </div>
          </div>
        </section>
        <section id="columnsPage" class="grid-2 hidden">
          <div class="section form-section">
            <h2>Kolon Bilgisi</h2>
            <form id="columnForm">
              <label>View<select id="columnsViewSelect"></select></label>
              <label>Veritabanindaki Kolon Adi<input id="columnNameInput" placeholder="order_no" required></label>
              <label>Ekran Adi<input id="columnDisplayNameInput" placeholder="Siparis No"></label>
              <label>AI Anlami<textarea id="columnSemanticInput" placeholder="Kolon anlami"></textarea></label>
              <details class="advanced">
                <summary>Gelismis ayarlar</summary>
                <div class="advanced-body">
                  <label>Veri Tipi<input id="columnDataTypeInput" placeholder="text"></label>
                  <div class="check-row">
                    <label><input id="columnFilterableInput" type="checkbox" checked> Filtre</label>
                    <label><input id="columnGroupableInput" type="checkbox" checked> Gruplama</label>
                    <label><input id="columnSummableInput" type="checkbox"> Toplam</label>
                    <label><input id="columnActiveInput" type="checkbox" checked> Aktif</label>
                  </div>
                </div>
              </details>
              <div class="button-row">
                <button class="primary" type="submit">Kaydet</button>
                <button id="newColumnButton" class="secondary" type="button">Yeni</button>
              </div>
              <div id="columnsMessage" class="message" role="status"></div>
            </form>
          </div>
          <div class="section">
            <div class="toolbar">
              <h2>Kolon Listesi</h2>
              <button id="refreshColumnsButton" class="secondary" type="button">Yenile</button>
            </div>
            <div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Teknik Ad</th><th>Ekran Adi</th><th>Veri Tipi</th><th>Durum</th><th>Islem</th>
                  </tr>
                </thead>
                <tbody id="columnsBody"></tbody>
              </table>
            </div>
          </div>
        </section>
        <section id="relationsPage" class="grid-2 hidden">
          <div class="section form-section">
            <h2>Iliski Bilgisi</h2>
            <form id="relationForm">
              <input id="relationId" type="hidden">
              <label>Kaynak View<select id="sourceViewSelect"></select></label>
              <label>Hedef View<select id="targetViewSelect"></select></label>
              <label>Iliski Adi<input id="relationName" placeholder="customer_order_to_lines" required></label>
              <h2>Kolon Eslesmeleri</h2>
              <div id="relationColumns" class="relation-columns"></div>
              <details class="advanced">
                <summary>Gelismis ayarlar</summary>
                <div class="advanced-body">
                  <label>Join Tipi
                    <select id="joinType">
                      <option>INNER JOIN</option>
                      <option>LEFT JOIN</option>
                      <option>RIGHT JOIN</option>
                      <option>FULL JOIN</option>
                    </select>
                  </label>
                  <label>Aciklama<textarea id="relationDescriptionTr" placeholder="Relation aciklamasi"></textarea></label>
                  <div class="check-row"><label><input id="relationIsActive" type="checkbox" checked> Aktif</label></div>
                </div>
              </details>
              <div class="button-row">
                <button id="addRelationColumnButton" class="secondary" type="button">Eslesme Ekle</button>
                <button class="primary" type="submit">Kaydet</button>
                <button id="newRelationButton" class="secondary" type="button">Yeni</button>
              </div>
              <div id="relationMessage" class="message" role="status"></div>
            </form>
          </div>
          <div class="section">
            <div class="toolbar">
              <h2>Iliski Listesi</h2>
              <button id="refreshRelationsButton" class="secondary" type="button">Yenile</button>
            </div>
            <div class="table-wrap">
              <table>
                <thead><tr><th>Iliski Adi</th><th>Kaynak</th><th>Hedef</th><th>Join</th><th>Durum</th></tr></thead>
                <tbody id="relationsBody"></tbody>
              </table>
            </div>
          </div>
        </section>
      </main>
    </div>
  </section>

  <script>
    const activePage = {{activePageJson}};
    const token = localStorage.getItem('vgantt_admin_token');
    let views = [];
    let columns = [];
    let relations = [];
    const relationColumnCache = new Map();

    if (!token) {
      window.location.href = '/admin';
    }

    function setMessage(id, type, text) {
      const element = document.getElementById(id);
      element.className = `message ${type || ''}`;
      element.textContent = text || '';
    }

    async function readError(response) {
      try {
        const payload = await response.json();
        return payload.message || response.statusText;
      } catch {
        return response.statusText;
      }
    }

    async function api(path, options = {}) {
      const headers = { authorization: `Bearer ${token}`, ...(options.headers || {}) };
      if (options.body && !headers['content-type']) {
        headers['content-type'] = 'application/json';
      }
      const response = await fetch(path, { ...options, headers });
      if (response.status === 401 || response.status === 403) {
        localStorage.removeItem('vgantt_admin_token');
        window.location.href = '/admin';
        throw new Error('Oturum gecersiz.');
      }
      if (!response.ok) {
        throw new Error(await readError(response));
      }
      return response.json();
    }

    function value(id) {
      return document.getElementById(id).value.trim();
    }

    function checked(id) {
      return document.getElementById(id).checked;
    }

    function escapeHtml(value) {
      return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;');
    }

    function shortId(value) {
      return String(value || '').slice(0, 8);
    }

    function statusBadge(isActive) {
      return `<span class="status ${isActive ? 'active' : 'passive'}">${isActive ? 'Aktif' : 'Pasif'}</span>`;
    }

    function optionLabelForColumn(column) {
      const label = column.displayNameTr || column.columnName;
      return label === column.columnName ? column.columnName : `${label} (${column.columnName})`;
    }

    function relationNamePart(viewId) {
      const view = views.find(item => item.viewId === viewId);
      return (view?.viewName || '')
        .toLowerCase()
        .replace(/[^a-z0-9_]/g, '_')
        .replace(/_+/g, '_')
        .replace(/^_+|_+$/g, '');
    }

    function updateRelationNameSuggestion() {
      const input = document.getElementById('relationName');
      if (!input || (input.value.trim() && input.dataset.autogenerated !== 'true')) return;
      const source = relationNamePart(value('sourceViewSelect'));
      const target = relationNamePart(value('targetViewSelect'));
      if (source && target) {
        input.value = `${source}_to_${target}`;
        input.dataset.autogenerated = 'true';
      }
    }

    function markSelectedRows(selector, dataKey, value) {
      for (const row of document.querySelectorAll(selector)) {
        row.classList.toggle('is-selected', row.dataset[dataKey] === String(value || ''));
      }
    }

    function showPage(page) {
      for (const id of ['viewsPage', 'columnsPage', 'relationsPage']) {
        const section = document.getElementById(id);
        if (section) section.classList.add('hidden');
      }
      const currentSection = document.getElementById(`${page}Page`);
      if (currentSection) currentSection.classList.remove('hidden');
    }

    async function loadViews() {
      views = await api('/admin/api/views');
      renderViews();
      fillColumnsViewSelect();
      fillRelationViewSelects();
    }

    function renderViews() {
      document.getElementById('viewsBody').innerHTML = views.map(view => `
        <tr data-view-id="${view.viewId}">
          <td class="mono">${shortId(view.viewId)}</td>
          <td class="mono">${escapeHtml(view.viewName)}</td>
          <td>${escapeHtml(view.displayNameTr)}</td>
          <td>${statusBadge(view.isActive)}</td>
          <td><button class="secondary" type="button" data-toggle-id="${view.viewId}">${view.isActive ? 'Pasife Al' : 'Aktif Yap'}</button></td>
        </tr>
      `).join('');

      for (const row of document.querySelectorAll('#viewsBody tr')) {
        row.addEventListener('click', () => editView(views.find(view => view.viewId === row.dataset.viewId)));
      }

      for (const button of document.querySelectorAll('[data-toggle-id]')) {
        button.addEventListener('click', async event => {
          event.stopPropagation();
          const viewId = button.dataset.toggleId;
          const current = views.find(view => view.viewId === viewId);
          if (!current) return;
          await upsertView({ ...current, isActive: !current.isActive });
        });
      }
    }

    function editView(view) {
      if (!view) return;
      document.getElementById('viewId').value = view.viewId;
      document.getElementById('viewName').value = view.viewName;
      document.getElementById('viewDisplayNameTr').value = view.displayNameTr || '';
      document.getElementById('viewDescriptionTr').value = view.descriptionTr || '';
      document.getElementById('viewIsActive').checked = view.isActive;
      markSelectedRows('#viewsBody tr', 'viewId', view.viewId);
    }

    function clearViewForm() {
      document.getElementById('viewId').value = '';
      document.getElementById('viewName').value = '';
      document.getElementById('viewDisplayNameTr').value = '';
      document.getElementById('viewDescriptionTr').value = '';
      document.getElementById('viewIsActive').checked = true;
      markSelectedRows('#viewsBody tr', 'viewId', '');
      setMessage('viewMessage', '', '');
    }

    async function upsertView(payload) {
      setMessage('viewMessage', '', 'Kaydediliyor...');
      const saved = await api('/admin/api/views', { method: 'POST', body: JSON.stringify(payload) });
      setMessage('viewMessage', 'ok', `${saved.viewName} kaydedildi.`);
      await loadViews();
      editView(saved);
      return saved;
    }

    async function saveView(event) {
      event.preventDefault();
      await upsertView({
        viewId: value('viewId') || null,
        viewName: value('viewName'),
        displayNameTr: value('viewDisplayNameTr'),
        descriptionTr: value('viewDescriptionTr'),
        isActive: checked('viewIsActive')
      });
    }

    function fillColumnsViewSelect() {
      const select = document.getElementById('columnsViewSelect');
      if (!select) return;
      const options = views.map(view => `<option value="${view.viewId}">${escapeHtml(view.viewName)}</option>`).join('');
      const selected = select.value;
      select.innerHTML = options;
      if (selected && views.some(view => view.viewId === selected)) {
        select.value = selected;
      }
    }

    function renderColumns() {
      const body = document.getElementById('columnsBody');
      if (!body) return;
      body.innerHTML = columns.map(column => `
        <tr data-column-name="${escapeHtml(column.columnName)}">
          <td class="mono">${escapeHtml(column.columnName)}</td>
          <td>${escapeHtml(column.displayNameTr)}</td>
          <td>${escapeHtml(column.dataType)}</td>
          <td>${statusBadge(column.isActive)}</td>
          <td><button class="secondary" type="button" data-column-toggle="${escapeHtml(column.columnName)}">${column.isActive ? 'Pasife Al' : 'Aktif Yap'}</button></td>
        </tr>
      `).join('');

      for (const row of document.querySelectorAll('#columnsBody tr')) {
        row.addEventListener('click', () => editColumn(columns.find(column => column.columnName === row.dataset.columnName)));
      }

      for (const button of document.querySelectorAll('[data-column-toggle]')) {
        button.addEventListener('click', async event => {
          event.stopPropagation();
          const columnName = button.dataset.columnToggle;
          const current = columns.find(column => column.columnName === columnName);
          if (!current) return;
          await upsertColumn({ ...current, isActive: !current.isActive });
        });
      }
    }

    async function loadColumnsForSelectedView() {
      const viewId = value('columnsViewSelect');
      if (!viewId) {
        columns = [];
        renderColumns();
        return;
      }
      columns = await api(`/admin/api/view-columns?viewId=${encodeURIComponent(viewId)}`);
      renderColumns();
    }

    function fillRelationViewSelects() {
      const options = views.map(view => `<option value="${view.viewId}">${escapeHtml(view.viewName)}</option>`).join('');
      for (const id of ['sourceViewSelect', 'targetViewSelect']) {
        const select = document.getElementById(id);
        if (!select) continue;
        const selected = select.value;
        select.innerHTML = options;
        if (selected && views.some(view => view.viewId === selected)) {
          select.value = selected;
        }
      }
      const sourceSelect = document.getElementById('sourceViewSelect');
      const targetSelect = document.getElementById('targetViewSelect');
      if (sourceSelect && targetSelect && sourceSelect.value === targetSelect.value && views.length > 1) {
        targetSelect.value = views[1].viewId;
      }
      updateRelationNameSuggestion();
    }

    function editColumn(column) {
      if (!column) return;
      document.getElementById('columnNameInput').value = column.columnName || '';
      document.getElementById('columnDisplayNameInput').value = column.displayNameTr || '';
      document.getElementById('columnDataTypeInput').value = column.dataType || '';
      document.getElementById('columnSemanticInput').value = column.semanticMeaningTr || '';
      document.getElementById('columnFilterableInput').checked = !!column.isFilterable;
      document.getElementById('columnGroupableInput').checked = !!column.isGroupable;
      document.getElementById('columnSummableInput').checked = !!column.isSummable;
      document.getElementById('columnActiveInput').checked = !!column.isActive;
      markSelectedRows('#columnsBody tr', 'columnName', column.columnName);
    }

    function clearColumnForm() {
      document.getElementById('columnNameInput').value = '';
      document.getElementById('columnDisplayNameInput').value = '';
      document.getElementById('columnDataTypeInput').value = '';
      document.getElementById('columnSemanticInput').value = '';
      document.getElementById('columnFilterableInput').checked = true;
      document.getElementById('columnGroupableInput').checked = true;
      document.getElementById('columnSummableInput').checked = false;
      document.getElementById('columnActiveInput').checked = true;
      markSelectedRows('#columnsBody tr', 'columnName', '');
      setMessage('columnsMessage', '', '');
    }

    async function upsertColumn(payload) {
      const viewId = value('columnsViewSelect');
      if (!viewId) {
        setMessage('columnsMessage', 'error', 'Once bir view secin.');
        return;
      }
      setMessage('columnsMessage', '', 'Kaydediliyor...');
      const saved = await api('/admin/api/view-columns', {
        method: 'POST',
        body: JSON.stringify({ viewId, columns: [payload] })
      });
      setMessage('columnsMessage', 'ok', `${saved.savedCount} kolon kaydedildi.`);
      await loadColumnsForSelectedView();
      editColumn(payload);
    }

    async function saveColumn(event) {
      event.preventDefault();
      await upsertColumn({
        columnName: value('columnNameInput'),
        displayNameTr: value('columnDisplayNameInput'),
        dataType: value('columnDataTypeInput'),
        semanticMeaningTr: value('columnSemanticInput'),
        isFilterable: checked('columnFilterableInput'),
        isGroupable: checked('columnGroupableInput'),
        isSummable: checked('columnSummableInput'),
        isActive: checked('columnActiveInput')
      });
    }

    async function getColumnsForView(viewId) {
      if (!viewId) return [];
      if (relationColumnCache.has(viewId)) return relationColumnCache.get(viewId);
      const viewColumns = await api(`/admin/api/view-columns?viewId=${encodeURIComponent(viewId)}`);
      relationColumnCache.set(viewId, viewColumns);
      return viewColumns;
    }

    function relationColumnOptions(viewId, selectedValue) {
      const viewColumns = relationColumnCache.get(viewId) || [];
      const selected = selectedValue || '';
      const options = viewColumns.map(column => `
        <option value="${escapeHtml(column.columnName)}" ${column.columnName === selected ? 'selected' : ''}>${escapeHtml(optionLabelForColumn(column))}</option>
      `).join('');
      const hasSelected = !selected || viewColumns.some(column => column.columnName === selected);
      const fallback = selected && !hasSelected
        ? `<option value="${escapeHtml(selected)}" selected>${escapeHtml(selected)}</option>`
        : '';
      return `<option value="">Secin</option>${fallback}${options}`;
    }

    async function refreshRelationColumnOptions() {
      const sourceViewId = value('sourceViewSelect');
      const targetViewId = value('targetViewSelect');
      await Promise.all([getColumnsForView(sourceViewId), getColumnsForView(targetViewId)]);
      for (const row of document.querySelectorAll('.relation-column-row')) {
        const sourceSelect = row.querySelector('[data-field="sourceColumnName"]');
        const targetSelect = row.querySelector('[data-field="targetColumnName"]');
        const sourceValue = sourceSelect.value;
        const targetValue = targetSelect.value;
        sourceSelect.innerHTML = relationColumnOptions(sourceViewId, sourceValue);
        targetSelect.innerHTML = relationColumnOptions(targetViewId, targetValue);
      }
    }

    async function relationViewsChanged() {
      updateRelationNameSuggestion();
      await refreshRelationColumnOptions();
    }

    function addRelationColumnRow(column = {}) {
      const row = document.createElement('div');
      row.className = 'relation-column-row';
      const sourceViewId = value('sourceViewSelect');
      const targetViewId = value('targetViewSelect');
      row.innerHTML = `
        <label>Sira<input data-field="ordinal" type="number" min="1" value="${column.ordinal || document.querySelectorAll('.relation-column-row').length + 1}"></label>
        <label>Kaynak Kolon<select data-field="sourceColumnName">${relationColumnOptions(sourceViewId, column.sourceColumnName || '')}</select></label>
        <span class="equals">=</span>
        <label>Hedef Kolon<select data-field="targetColumnName">${relationColumnOptions(targetViewId, column.targetColumnName || '')}</select></label>
        <button class="icon" type="button">Sil</button>
      `;
      row.querySelector('button').addEventListener('click', () => {
        if (document.querySelectorAll('.relation-column-row').length > 1) row.remove();
      });
      document.getElementById('relationColumns').appendChild(row);
      refreshRelationColumnOptions().catch(error => console.error(error));
    }

    function clearRelationForm() {
      document.getElementById('relationId').value = '';
      const relationNameInput = document.getElementById('relationName');
      relationNameInput.value = '';
      relationNameInput.dataset.autogenerated = 'true';
      if (views.length > 1) {
        document.getElementById('targetViewSelect').value = views[1].viewId;
      }
      updateRelationNameSuggestion();
      document.getElementById('joinType').value = 'INNER JOIN';
      document.getElementById('relationDescriptionTr').value = '';
      document.getElementById('relationIsActive').checked = true;
      document.getElementById('relationColumns').innerHTML = '';
      addRelationColumnRow({ ordinal: 1 });
      markSelectedRows('#relationsBody tr', 'relationId', '');
      setMessage('relationMessage', '', '');
    }

    async function editRelation(relation) {
      if (!relation) return;
      document.getElementById('relationId').value = relation.relationId;
      document.getElementById('relationName').value = relation.relationName;
      document.getElementById('relationName').dataset.autogenerated = 'false';
      document.getElementById('sourceViewSelect').value = relation.sourceViewId;
      document.getElementById('targetViewSelect').value = relation.targetViewId;
      document.getElementById('joinType').value = relation.joinType;
      document.getElementById('relationDescriptionTr').value = relation.descriptionTr || '';
      document.getElementById('relationIsActive').checked = relation.isActive;
      await refreshRelationColumnOptions();
      document.getElementById('relationColumns').innerHTML = '';
      for (const column of relation.columns) addRelationColumnRow(column);
      await refreshRelationColumnOptions();
      markSelectedRows('#relationsBody tr', 'relationId', relation.relationId);
    }

    async function loadRelations() {
      relations = await api('/admin/api/relations');
      document.getElementById('relationsBody').innerHTML = relations.map(relation => `
        <tr data-relation-id="${relation.relationId}">
          <td>${escapeHtml(relation.relationName)}</td>
          <td class="mono">${escapeHtml(relation.sourceViewName)}</td>
          <td class="mono">${escapeHtml(relation.targetViewName)}</td>
          <td>${escapeHtml(relation.joinType)}</td>
          <td>${statusBadge(relation.isActive)}</td>
        </tr>
      `).join('');

      for (const row of document.querySelectorAll('#relationsBody tr')) {
        row.addEventListener('click', () => {
          editRelation(relations.find(relation => relation.relationId === row.dataset.relationId))
            .catch(error => {
              console.error(error);
              setMessage('relationMessage', 'error', error.message || String(error));
            });
        });
      }
    }

    async function saveRelation(event) {
      event.preventDefault();
      const columnsPayload = [...document.querySelectorAll('.relation-column-row')]
        .map(row => ({
          ordinal: Number(row.querySelector('[data-field="ordinal"]').value || 1),
          sourceColumnName: row.querySelector('[data-field="sourceColumnName"]').value.trim(),
          targetColumnName: row.querySelector('[data-field="targetColumnName"]').value.trim()
        }))
        .filter(column => column.sourceColumnName && column.targetColumnName);

      setMessage('relationMessage', '', 'Kaydediliyor...');
      const saved = await api('/admin/api/relations', {
        method: 'POST',
        body: JSON.stringify({
          relationId: value('relationId') || null,
          relationName: value('relationName'),
          sourceViewId: value('sourceViewSelect'),
          targetViewId: value('targetViewSelect'),
          joinType: value('joinType'),
          descriptionTr: value('relationDescriptionTr'),
          isActive: checked('relationIsActive'),
          columns: columnsPayload
        })
      });
      setMessage('relationMessage', 'ok', `${saved.relationName} kaydedildi.`);
      await loadRelations();
      await editRelation(saved);
    }

    document.getElementById('logoutButton').addEventListener('click', () => {
      localStorage.removeItem('vgantt_admin_token');
      window.location.href = '/admin/logout';
    });
    document.getElementById('viewForm').addEventListener('submit', saveView);
    document.getElementById('newViewButton').addEventListener('click', clearViewForm);
    document.getElementById('refreshViewsButton').addEventListener('click', loadViews);
    document.getElementById('columnsViewSelect')?.addEventListener('change', loadColumnsForSelectedView);
    document.getElementById('columnForm')?.addEventListener('submit', saveColumn);
    document.getElementById('newColumnButton')?.addEventListener('click', clearColumnForm);
    document.getElementById('refreshColumnsButton')?.addEventListener('click', loadColumnsForSelectedView);
    document.getElementById('sourceViewSelect')?.addEventListener('change', () => relationViewsChanged().catch(error => setMessage('relationMessage', 'error', error.message || String(error))));
    document.getElementById('targetViewSelect')?.addEventListener('change', () => relationViewsChanged().catch(error => setMessage('relationMessage', 'error', error.message || String(error))));
    document.getElementById('relationName')?.addEventListener('input', event => {
      event.target.dataset.autogenerated = event.target.value.trim() ? 'false' : 'true';
    });
    document.getElementById('relationForm')?.addEventListener('submit', saveRelation);
    document.getElementById('newRelationButton')?.addEventListener('click', clearRelationForm);
    document.getElementById('addRelationColumnButton')?.addEventListener('click', () => addRelationColumnRow());
    document.getElementById('refreshRelationsButton')?.addEventListener('click', loadRelations);

    async function boot() {
      showPage(activePage);
      await loadViews();
      if (activePage === 'columns') {
        await loadColumnsForSelectedView();
      }
      if (activePage === 'relations') {
        clearRelationForm();
        await loadRelations();
      }
    }

    boot().catch(error => {
      console.error(error);
      alert(error.message || String(error));
    });
  </script>
</body>
</html>
""";
}

#pragma warning disable CS8321
static string BuildSalesViewsSettingsPage(AuthenticatedUser session)
{
    var tenantName = WebUtility.HtmlEncode(session.TenantName);
    var displayName = WebUtility.HtmlEncode(session.DisplayName);
    var role = WebUtility.HtmlEncode(session.Role);

    return $$"""
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vgantt AI Admin</title>
  <style>
    :root {
      color-scheme: light;
      font-family: Segoe UI, Arial, sans-serif;
      background: #f6f7f9;
      color: #1f2933;
      --line: #d9e0e7;
      --muted: #65707c;
      --primary: #16697a;
      --primary-dark: #0f5361;
      --surface: #ffffff;
      --danger: #b42318;
      --success: #16794c;
    }

    * {
      box-sizing: border-box;
    }

    body {
      margin: 0;
      background: #f6f7f9;
    }

    .app-shell {
      min-height: 100vh;
      display: grid;
      grid-template-columns: 240px minmax(0, 1fr);
    }

    .sidebar {
      background: #17242b;
      color: #f9fafb;
      padding: 18px 14px;
    }

    .brand {
      display: grid;
      gap: 2px;
      padding: 8px 8px 18px;
      border-bottom: 1px solid rgba(255,255,255,.14);
    }

    .brand strong {
      font-size: 18px;
    }

    .brand span {
      color: #b8c4cc;
      font-size: 12px;
    }

    .nav-section {
      margin-top: 18px;
    }

    .nav-title {
      color: #b8c4cc;
      font-size: 12px;
      font-weight: 700;
      margin: 0 8px 8px;
      text-transform: uppercase;
    }

    .nav-item {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: flex-start;
      gap: 10px;
      color: #ffffff;
      border: 0;
      border-radius: 6px;
      padding: 10px 12px;
      font: inherit;
      font-weight: 700;
      text-decoration: none;
      margin-bottom: 6px;
    }

    .nav-item.active {
      background: rgba(255,255,255,.12);
    }

    .content {
      min-width: 0;
    }

    .topbar {
      min-height: 65px;
      background: var(--surface);
      border-bottom: 1px solid var(--line);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 24px;
    }

    h1 {
      font-size: 24px;
      margin: 0 0 4px;
    }

    h2 {
      font-size: 18px;
      margin: 0;
    }

    p {
      margin: 0;
      color: var(--muted);
    }

    .user-meta {
      display: grid;
      gap: 8px;
      color: var(--muted);
      font-size: 13px;
      text-align: right;
    }

    .logout {
      color: var(--primary);
      font-weight: 700;
      text-decoration: none;
    }

    main {
      width: min(1120px, calc(100% - 32px));
      margin: 24px auto;
    }

    .section {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 20px;
    }

    .section-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;
      margin-bottom: 18px;
    }

    form {
      display: grid;
      gap: 14px;
    }

    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
      color: #344054;
    }

    input,
    textarea {
      border: 1px solid #c8d0d9;
      border-radius: 6px;
      font: inherit;
      padding: 10px 12px;
      min-width: 0;
    }

    textarea {
      min-height: 190px;
      resize: vertical;
      line-height: 1.45;
      font-family: Consolas, monospace;
    }

    .view-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 16px;
    }

    .view-card {
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 14px;
      display: grid;
      gap: 12px;
    }

    .relationship-list {
      display: grid;
      gap: 10px;
    }

    .relationship-row {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 28px minmax(0, 1fr) 40px;
      gap: 8px;
      align-items: end;
    }

    .equals {
      align-self: center;
      color: var(--muted);
      font-weight: 700;
      text-align: center;
    }

    button.secondary,
    button.icon {
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #ffffff;
      color: #1f2933;
      font: inherit;
      font-weight: 700;
      padding: 9px 12px;
      cursor: pointer;
    }

    button.icon {
      padding: 10px 0;
    }

    button.primary {
      justify-self: start;
      border: 0;
      border-radius: 6px;
      background: var(--primary);
      color: #ffffff;
      font: inherit;
      font-weight: 700;
      padding: 10px 16px;
      cursor: pointer;
    }

    button.primary:hover {
      background: var(--primary-dark);
    }

    button.primary:disabled {
      background: #8aa4ad;
      cursor: wait;
    }

    #message {
      min-height: 22px;
      font-size: 14px;
      font-weight: 600;
    }

    #message.ok {
      color: var(--success);
    }

    #message.error {
      color: var(--danger);
    }

    @media (max-width: 720px) {
      .app-shell {
        grid-template-columns: 1fr;
      }

      .sidebar {
        padding: 12px;
      }

      .topbar,
      .section-header {
        display: grid;
      }

      .user-meta {
        text-align: left;
      }

      .view-grid,
      .relationship-row {
        grid-template-columns: 1fr;
      }

      .equals {
        text-align: left;
      }
    }
  </style>
</head>
<body>
  <section class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <strong>Vgantt AI Admin</strong>
        <span>{{tenantName}}</span>
      </div>
      <nav class="nav-section">
        <p class="nav-title">Ayarlar</p>
        <a class="nav-item active" href="/admin/settings/views">View Baglantilari</a>
        <a class="nav-item" href="/admin/settings/columns">Kolon Anlamlari</a>
      </nav>
    </aside>
    <div class="content">
      <header class="topbar">
        <div>
          <h1>View Baglantilari</h1>
          <p>VIEW YAPISINI TANIMLAYIN</p>
        </div>
        <div class="user-meta">
          <span>{{displayName}} / {{role}}</span>
          <a class="logout" href="/admin/logout">Cikis yap</a>
        </div>
      </header>
      <main>
        <section class="section">
          <div class="section-header">
            <div>
              <h2>View eslestirme</h2>
              <p>View kolonlarini yazin, sonra bir veya daha fazla baglanti kolonu eslestirin.</p>
            </div>
          </div>
          <form id="salesViewsForm">
            <div class="view-grid">
              <div class="view-card">
                <label>
                  View1
                  <input id="view1Name" value="VGANTT_AI_CUSTOMER_ORDER" required>
                </label>
                <label>
                  View1 kolonlari
                  <textarea id="view1Columns" spellcheck="false">ORDER_NO
CREATED_DATE
CUSTOMER_NO</textarea>
                </label>
              </div>
              <div class="view-card">
                <label>
                  View2
                  <input id="view2Name" value="VGANTT_AI_CUSTOMER_ORDER_LINE" required>
                </label>
                <label>
                  View2 kolonlari
                  <textarea id="view2Columns" spellcheck="false">ORDER_NO
LINE_NO
REL_NO
CATALOG_NO
PRE_ACCOUNTING_ID</textarea>
                </label>
              </div>
            </div>
            <div>
              <h2>Esitleme ilkeleri</h2>
              <p>Birden fazla satir ekleyerek 2 veya 3 kolonlu baglanti kurabilirsiniz.</p>
            </div>
            <div id="relationshipList" class="relationship-list"></div>
            <button id="addRelationshipButton" class="secondary" type="button">Eslesme ekle</button>
            <button id="saveButton" class="primary" type="submit">Kaydet</button>
            <div id="message" role="status"></div>
          </form>
        </section>
      </main>
    </div>
  </section>
  <script>
    const form = document.getElementById('salesViewsForm');
    const button = document.getElementById('saveButton');
    const addRelationshipButton = document.getElementById('addRelationshipButton');
    const relationshipList = document.getElementById('relationshipList');
    const message = document.getElementById('message');

    function value(id) {
      return document.getElementById(id).value.trim();
    }

    function lines(id) {
      return splitColumnInput(value(id))
        .map(item => item.trim())
        .filter(Boolean);
    }

    function splitColumnInput(text) {
      const items = [];
      let current = '';
      let depth = 0;
      let inQuote = false;

      for (const char of text) {
        if (char === '"') {
          inQuote = !inQuote;
          current += char;
          continue;
        }

        if (!inQuote) {
          if (char === '(') {
            depth += 1;
          } else if (char === ')' && depth > 0) {
            depth -= 1;
          } else if ((char === ',' || char === '\n' || char === '\r') && depth === 0) {
            if (current.trim()) {
              items.push(current.trim());
            }
            current = '';
            continue;
          }
        }

        current += char;
      }

      if (current.trim()) {
        items.push(current.trim());
      }

      return items;
    }

    function addRelationship(view1Column = 'ORDER_NO', view2Column = 'ORDER_NO') {
      const row = document.createElement('div');
      row.className = 'relationship-row';
      row.innerHTML = `
        <label>
          View1 kolon
          <input class="relationship-view1" value="${view1Column}" required>
        </label>
        <span class="equals">=</span>
        <label>
          View2 kolon
          <input class="relationship-view2" value="${view2Column}" required>
        </label>
        <button class="icon" type="button" aria-label="Sil">X</button>
      `;
      row.querySelector('button').addEventListener('click', () => {
        if (relationshipList.children.length > 1) {
          row.remove();
        }
      });
      relationshipList.appendChild(row);
    }

    function relationships() {
      return [...relationshipList.querySelectorAll('.relationship-row')]
        .map(row => ({
          view1ColumnName: row.querySelector('.relationship-view1').value.trim(),
          view2ColumnName: row.querySelector('.relationship-view2').value.trim()
        }))
        .filter(item => item.view1ColumnName && item.view2ColumnName);
    }

    function setMessage(type, text) {
      message.className = type;
      message.textContent = text;
    }

    async function readError(response) {
      try {
        const payload = await response.json();
        return payload.message || response.statusText;
      } catch {
        return response.statusText;
      }
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      button.disabled = true;
      setMessage('', 'Kaydediliyor...');

      try {
        const response = await fetch('/admin/sales-views', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            view1Name: value('view1Name'),
            view2Name: value('view2Name'),
            view1Columns: lines('view1Columns'),
            view2Columns: lines('view2Columns'),
            relationships: relationships(),
            businessDomain: 'sales'
          })
        });

        if (!response.ok) {
          throw new Error(await readError(response));
        }

        const saved = await response.json();
        setMessage('ok', `${saved.tenantName}: ${saved.view1Name} -> ${saved.view2Name} (${saved.relationships.length} eslesme) kaydedildi.`);
      } catch (error) {
        setMessage('error', error.message || String(error));
      } finally {
        button.disabled = false;
      }
    });

    addRelationshipButton.addEventListener('click', () => addRelationship('', ''));
    addRelationship();
  </script>
</body>
</html>
""";
}

static async Task<string> BuildColumnMeaningsPageAsync(AuthenticatedUser session)
{
    var tenantName = WebUtility.HtmlEncode(session.TenantName);
    var displayName = WebUtility.HtmlEncode(session.DisplayName);
    var role = WebUtility.HtmlEncode(session.Role);
    var views = await LoadColumnMeaningViewsAsync(session.TenantId);
    var viewsJson = JsonSerializer.Serialize(views, AppJson.Options);

    return $$"""
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vgantt AI Admin</title>
  <style>
    :root {
      color-scheme: light;
      font-family: Segoe UI, Arial, sans-serif;
      background: #f6f7f9;
      color: #1f2933;
      --line: #d9e0e7;
      --muted: #65707c;
      --primary: #16697a;
      --primary-dark: #0f5361;
      --surface: #ffffff;
      --danger: #b42318;
      --success: #16794c;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      background: #f6f7f9;
    }

    .app-shell {
      min-height: 100vh;
      display: grid;
      grid-template-columns: 240px minmax(0, 1fr);
    }

    .sidebar {
      background: #17242b;
      color: #f9fafb;
      padding: 18px 14px;
    }

    .brand {
      display: grid;
      gap: 2px;
      padding: 8px 8px 18px;
      border-bottom: 1px solid rgba(255,255,255,.14);
    }

    .brand strong { font-size: 18px; }
    .brand span { color: #b8c4cc; font-size: 12px; }

    .nav-section { margin-top: 18px; }

    .nav-title {
      color: #b8c4cc;
      font-size: 12px;
      font-weight: 700;
      margin: 0 8px 8px;
      text-transform: uppercase;
    }

    .nav-item {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: flex-start;
      gap: 10px;
      color: #ffffff;
      border-radius: 6px;
      padding: 10px 12px;
      font: inherit;
      font-weight: 700;
      text-decoration: none;
      margin-bottom: 6px;
    }

    .nav-item.active {
      background: rgba(255,255,255,.12);
    }

    .content { min-width: 0; }

    .topbar {
      min-height: 65px;
      background: var(--surface);
      border-bottom: 1px solid var(--line);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 24px;
    }

    h1 { font-size: 24px; margin: 0 0 4px; }
    h2 { font-size: 18px; margin: 0; }
    p { margin: 0; color: var(--muted); }

    .user-meta {
      display: grid;
      gap: 8px;
      color: var(--muted);
      font-size: 13px;
      text-align: right;
    }

    .logout {
      color: var(--primary);
      font-weight: 700;
      text-decoration: none;
    }

    main {
      width: min(1120px, calc(100% - 32px));
      margin: 24px auto;
    }

    .section {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 20px;
    }

    .toolbar {
      display: flex;
      align-items: end;
      justify-content: space-between;
      gap: 12px;
      margin: 18px 0;
    }

    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
      color: #344054;
    }

    select,
    input,
    textarea {
      border: 1px solid #c8d0d9;
      border-radius: 6px;
      font: inherit;
      padding: 9px 10px;
      min-width: 0;
    }

    textarea {
      min-height: 42px;
      resize: vertical;
    }

    .bulk-panel {
      display: grid;
      gap: 10px;
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 12px;
      margin-bottom: 16px;
      background: #fbfcfd;
    }

    .bulk-panel textarea {
      min-height: 92px;
      font-family: Consolas, monospace;
      line-height: 1.4;
    }

    .hint {
      color: var(--muted);
      font-size: 13px;
    }

    button.secondary {
      justify-self: start;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #ffffff;
      color: #1f2933;
      font: inherit;
      font-weight: 700;
      padding: 9px 12px;
      cursor: pointer;
    }

    table {
      width: 100%;
      border-collapse: collapse;
    }

    th,
    td {
      border-bottom: 1px solid var(--line);
      padding: 8px;
      text-align: left;
      vertical-align: top;
    }

    th {
      color: #344054;
      font-size: 13px;
      background: #f8fafc;
    }

    td:first-child {
      width: 220px;
      font-family: Consolas, monospace;
      font-size: 13px;
    }

    .empty-state {
      color: var(--muted);
      font-family: inherit;
      padding: 18px 8px;
    }

    button.primary {
      border: 0;
      border-radius: 6px;
      background: var(--primary);
      color: #ffffff;
      font: inherit;
      font-weight: 700;
      padding: 10px 16px;
      cursor: pointer;
    }

    button.primary:hover { background: var(--primary-dark); }
    button.primary:disabled { background: #8aa4ad; cursor: wait; }

    #message {
      min-height: 22px;
      font-size: 14px;
      font-weight: 600;
      margin-top: 12px;
    }

    #message.ok { color: var(--success); }
    #message.error { color: var(--danger); }

    @media (max-width: 720px) {
      .app-shell { grid-template-columns: 1fr; }
      .topbar, .toolbar { display: grid; }
      .user-meta { text-align: left; }
      table, thead, tbody, tr, th, td { display: block; }
      th { display: none; }
      td:first-child { width: auto; }
    }
  </style>
</head>
<body>
  <section class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <strong>Vgantt AI Admin</strong>
        <span>{{tenantName}}</span>
      </div>
      <nav class="nav-section">
        <p class="nav-title">Ayarlar</p>
        <a class="nav-item" href="/admin/settings/views">View Baglantilari</a>
        <a class="nav-item active" href="/admin/settings/columns">Kolon Anlamlari</a>
      </nav>
    </aside>
    <div class="content">
      <header class="topbar">
        <div>
          <h1>Kolon Anlamlari</h1>
          <p>Kullaniciya gosterilecek musteri dilindeki alan anlamlarini yazin.</p>
        </div>
        <div class="user-meta">
          <span>{{displayName}} / {{role}}</span>
          <a class="logout" href="/admin/logout">Cikis yap</a>
        </div>
      </header>
      <main>
        <section class="section">
          <h2>Kolon sozlugu</h2>
          <div class="toolbar">
            <label>
              View
              <select id="viewSelect"></select>
            </label>
            <button id="saveButton" class="primary" type="button">Kaydet</button>
          </div>
          <div class="bulk-panel">
            <label>
              Excel'den toplu yapistir
              <textarea id="bulkPaste" placeholder="ORDER_NO    Siparis No    Musteri siparisi&#10;DATE_ENTERED    Olusturma Tarihi    Siparis acma tarihi"></textarea>
            </label>
            <button id="applyBulkButton" class="secondary" type="button">Tabloya isle</button>
            <p class="hint">Satir formati: kolon, musteri anlami, aciklama. Excel'den kopyalarsan sekme ayracini otomatik okur.</p>
          </div>
          <table>
            <thead>
              <tr>
                <th>Kolon</th>
                <th>Musteri acisindan anlami</th>
                <th>Aciklama / es anlamlar</th>
              </tr>
            </thead>
            <tbody id="columnsBody"></tbody>
          </table>
          <div id="message" role="status"></div>
        </section>
      </main>
    </div>
  </section>
  <script>
    const views = {{viewsJson}};
    const viewSelect = document.getElementById('viewSelect');
    const columnsBody = document.getElementById('columnsBody');
    const saveButton = document.getElementById('saveButton');
    const bulkPaste = document.getElementById('bulkPaste');
    const applyBulkButton = document.getElementById('applyBulkButton');
    const message = document.getElementById('message');

    function setMessage(type, text) {
      message.className = type;
      message.textContent = text;
    }

    function renderView(viewName) {
      const view = views.find(item => item.viewName === viewName);
      columnsBody.innerHTML = '';
      setMessage('', '');
      if (!view || view.columns.length === 0) {
        const row = document.createElement('tr');
        const cell = document.createElement('td');
        cell.colSpan = 3;
        cell.className = 'empty-state';
        cell.textContent = 'Bu view icin kayitli kolon bulunamadi.';
        row.appendChild(cell);
        columnsBody.appendChild(row);
        saveButton.disabled = true;
        return;
      }

      saveButton.disabled = false;
      for (const column of view.columns) {
        const row = document.createElement('tr');
        row.dataset.column = column.columnName;

        const columnCell = document.createElement('td');
        columnCell.textContent = column.columnName;

        const labelCell = document.createElement('td');
        const labelInput = document.createElement('input');
        labelInput.dataset.column = column.columnName;
        labelInput.dataset.field = 'customerLabel';
        labelInput.placeholder = 'Orn: Siparis No';
        labelInput.value = column.customerLabel || '';
        labelCell.appendChild(labelInput);

        const descriptionCell = document.createElement('td');
        const descriptionInput = document.createElement('textarea');
        descriptionInput.dataset.column = column.columnName;
        descriptionInput.dataset.field = 'customerDescription';
        descriptionInput.placeholder = 'Orn: Musteri siparisi, siparis numarasi';
        descriptionInput.value = column.customerDescription || '';
        descriptionCell.appendChild(descriptionInput);

        row.appendChild(columnCell);
        row.appendChild(labelCell);
        row.appendChild(descriptionCell);
        columnsBody.appendChild(row);
      }
    }

    function normalizeColumnKey(value) {
      return String(value || '')
        .trim()
        .replace(/^t\d+\./i, '')
        .replace(/^"/, '')
        .replace(/"$/, '')
        .toUpperCase();
    }

    function splitBulkLine(line) {
      const separator = line.includes('\t')
        ? '\t'
        : line.includes(';')
          ? ';'
          : ',';
      const parts = line.split(separator).map(part => part.trim()).filter(Boolean);
      if (parts.length < 2) {
        return null;
      }

      return {
        columnName: normalizeColumnKey(parts[0]),
        customerLabel: parts[1],
        customerDescription: parts.slice(2).join(' ')
      };
    }

    function applyBulkPaste() {
      const rows = [...columnsBody.querySelectorAll('tr[data-column]')];
      const byColumn = new Map(rows.map(row => [normalizeColumnKey(row.dataset.column), row]));
      const lines = bulkPaste.value.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
      let appliedCount = 0;
      const skipped = [];

      for (const line of lines) {
        const parsed = splitBulkLine(line);
        if (!parsed) {
          skipped.push(line);
          continue;
        }

        const row = byColumn.get(parsed.columnName);
        if (!row) {
          skipped.push(parsed.columnName);
          continue;
        }

        row.querySelector('[data-field="customerLabel"]').value = parsed.customerLabel;
        row.querySelector('[data-field="customerDescription"]').value = parsed.customerDescription;
        appliedCount += 1;
      }

      const skippedText = skipped.length > 0
        ? ` ${skipped.length} satir eslesmedi.`
        : '';
      setMessage(appliedCount > 0 ? 'ok' : 'error', `${appliedCount} satir tabloya islendi.${skippedText}`);
    }

    for (const view of views) {
      const option = document.createElement('option');
      option.value = view.viewName;
      option.textContent = view.viewName;
      viewSelect.appendChild(option);
    }

    viewSelect.addEventListener('change', () => renderView(viewSelect.value));
    applyBulkButton.addEventListener('click', applyBulkPaste);
    if (views.length > 0) {
      renderView(views[0].viewName);
    } else {
      renderView('');
    }

    async function readError(response) {
      try {
        const payload = await response.json();
        return payload.message || response.statusText;
      } catch {
        return response.statusText;
      }
    }

    saveButton.addEventListener('click', async () => {
      saveButton.disabled = true;
      setMessage('', 'Kaydediliyor...');

      try {
        const inputs = [...columnsBody.querySelectorAll('input[data-column], textarea[data-column]')];
        const byColumn = new Map();
        for (const input of inputs) {
          const columnName = input.dataset.column;
          const field = input.dataset.field;
          if (!byColumn.has(columnName)) {
            byColumn.set(columnName, { columnName, customerLabel: '', customerDescription: '' });
          }
          byColumn.get(columnName)[field] = input.value.trim();
        }

        const response = await fetch('/admin/column-meanings', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            viewName: viewSelect.value,
            meanings: [...byColumn.values()].filter(item => item.customerLabel || item.customerDescription)
          })
        });

        if (!response.ok) {
          throw new Error(await readError(response));
        }

        const saved = await response.json();
        const view = views.find(item => item.viewName === viewSelect.value);
        if (view) {
          for (const meaning of byColumn.values()) {
            const column = view.columns.find(item => item.columnName === meaning.columnName);
            if (column) {
              column.customerLabel = meaning.customerLabel;
              column.customerDescription = meaning.customerDescription;
            }
          }
        }
        setMessage('ok', `${saved.viewName}: ${saved.savedCount} kolon anlami kaydedildi.`);
      } catch (error) {
        setMessage('error', error.message || String(error));
      } finally {
        saveButton.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}

#pragma warning disable CS8321
static string BuildSalesViewsAdminPage()
{
    return """
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Vgantt AI Admin</title>
  <style>
    :root {
      color-scheme: light;
      font-family: Segoe UI, Arial, sans-serif;
      background: #f6f7f9;
      color: #1f2933;
      --line: #d9e0e7;
      --muted: #65707c;
      --primary: #16697a;
      --primary-dark: #0f5361;
      --surface: #ffffff;
      --danger: #b42318;
      --success: #16794c;
    }

    * {
      box-sizing: border-box;
    }

    body {
      margin: 0;
      background: #f6f7f9;
    }

    .login-page {
      min-height: 100vh;
      display: grid;
      place-items: center;
      padding: 24px;
    }

    .login-panel {
      width: min(440px, 100%);
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 24px;
    }

    h1 {
      font-size: 24px;
      margin: 0 0 4px;
    }

    h2 {
      font-size: 18px;
      margin: 0;
    }

    p {
      margin: 0;
      color: var(--muted);
    }

    form {
      display: grid;
      gap: 14px;
    }

    .login-form {
      margin-top: 22px;
    }

    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px;
    }

    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
      color: #344054;
    }

    input {
      border: 1px solid #c8d0d9;
      border-radius: 6px;
      font: inherit;
      padding: 10px 12px;
      min-width: 0;
    }

    button {
      justify-self: start;
      border: 0;
      border-radius: 6px;
      background: var(--primary);
      color: #ffffff;
      font: inherit;
      font-weight: 700;
      padding: 10px 16px;
      cursor: pointer;
    }

    button:hover {
      background: var(--primary-dark);
    }

    button:disabled {
      background: #8aa4ad;
      cursor: wait;
    }

    .app-shell {
      min-height: 100vh;
      display: grid;
      grid-template-columns: 240px minmax(0, 1fr);
    }

    .sidebar {
      background: #17242b;
      color: #f9fafb;
      padding: 18px 14px;
    }

    .brand {
      display: grid;
      gap: 2px;
      padding: 8px 8px 18px;
      border-bottom: 1px solid rgba(255,255,255,.14);
    }

    .brand strong {
      font-size: 18px;
    }

    .brand span {
      color: #b8c4cc;
      font-size: 12px;
    }

    .nav-section {
      margin-top: 18px;
    }

    .nav-title {
      color: #b8c4cc;
      font-size: 12px;
      font-weight: 700;
      margin: 0 8px 8px;
      text-transform: uppercase;
    }

    .nav-item {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: flex-start;
      gap: 10px;
      background: rgba(255,255,255,.12);
      color: #ffffff;
      border-radius: 6px;
      padding: 10px 12px;
      font-weight: 700;
    }

    .content {
      min-width: 0;
    }

    .topbar {
      min-height: 65px;
      background: var(--surface);
      border-bottom: 1px solid var(--line);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 24px;
    }

    .user-meta {
      color: var(--muted);
      font-size: 13px;
      text-align: right;
    }

    main {
      width: min(900px, calc(100% - 32px));
      margin: 24px auto;
    }

    .section {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 20px;
    }

    .section-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;
      margin-bottom: 18px;
    }

    .message {
      min-height: 22px;
      font-size: 14px;
      font-weight: 600;
    }

    .message.ok {
      color: var(--success);
    }

    .message.error {
      color: var(--danger);
    }

    .hidden {
      display: none;
    }

    @media (max-width: 720px) {
      .app-shell {
        grid-template-columns: 1fr;
      }

      .sidebar {
        padding: 12px;
      }

      .topbar,
      .section-header {
        display: grid;
      }

      .user-meta {
        text-align: left;
      }

      .grid {
        grid-template-columns: 1fr;
      }
    }
  </style>
</head>
<body>
  <section id="loginPage" class="login-page">
    <div class="login-panel">
      <h1>Vgantt AI Admin</h1>
      <p>Friterm yonetim ekranina giris yapin.</p>
      <form id="loginForm" class="login-form">
        <label>
          Kullanici
          <input id="username" autocomplete="username" value="volkan.celikel@vgantt.com" required>
        </label>
        <label>
          Sifre
          <input id="password" type="password" autocomplete="current-password" required>
        </label>
        <button id="loginButton" type="submit">Giris yap</button>
        <div id="loginMessage" class="message" role="status"></div>
      </form>
    </div>
  </section>

  <section id="appShell" class="app-shell hidden">
    <aside class="sidebar">
      <div class="brand">
        <strong>Vgantt AI Admin</strong>
        <span id="tenantName">Friterm</span>
      </div>
      <nav class="nav-section">
        <p class="nav-title">Ayarlar</p>
        <button class="nav-item" type="button">View Baglantilari</button>
      </nav>
    </aside>
    <div class="content">
      <header class="topbar">
        <div>
          <h1>View Baglantilari</h1>
          <p>Friterm satis view yapisini tanimlayin.</p>
        </div>
        <div id="userMeta" class="user-meta"></div>
      </header>
      <main>
        <section class="section">
          <div class="section-header">
            <div>
              <h2>Satis siparisi viewlari</h2>
              <p>Baslik ve satir viewlari ayni kolonla baglanir.</p>
            </div>
          </div>
          <form id="salesViewsForm">
            <label>
              Siparis view
              <input id="orderViewName" value="VGANTT_AI_CUSTOMER_ORDER" required>
            </label>
            <label>
              Siparis satir view
              <input id="orderLineViewName" value="VGANTT_AI_CUSTOMER_ORDER_LINE" required>
            </label>
            <label>
              Baglanti kolonu
              <input id="joinColumnName" value="ORDER_NO" required>
            </label>
            <button id="saveButton" type="submit">Kaydet</button>
            <div id="saveMessage" class="message" role="status"></div>
          </form>
        </section>
      </main>
    </div>
  </section>
  <script>
    const loginPage = document.getElementById('loginPage');
    const appShell = document.getElementById('appShell');
    const loginForm = document.getElementById('loginForm');
    const salesViewsForm = document.getElementById('salesViewsForm');
    const loginButton = document.getElementById('loginButton');
    const saveButton = document.getElementById('saveButton');
    const loginMessage = document.getElementById('loginMessage');
    const saveMessage = document.getElementById('saveMessage');
    const tenantName = document.getElementById('tenantName');
    const userMeta = document.getElementById('userMeta');
    let authToken = '';

    function value(id) {
      return document.getElementById(id).value.trim();
    }

    async function readError(response) {
      try {
        const payload = await response.json();
        return payload.message || response.statusText;
      } catch {
        return response.statusText;
      }
    }

    function setMessage(target, type, text) {
      target.className = `message ${type}`;
      target.textContent = text;
    }

    loginForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      loginButton.disabled = true;
      setMessage(loginMessage, '', 'Giris yapiliyor...');

      try {
        const loginResponse = await fetch('/auth/login', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            username: value('username'),
            password: value('password')
          })
        });

        if (!loginResponse.ok) {
          throw new Error(await readError(loginResponse));
        }

        const login = await loginResponse.json();
        authToken = login.token;
        tenantName.textContent = login.tenantName;
        userMeta.textContent = `${login.userDisplayName} / ${login.role}`;
        loginPage.classList.add('hidden');
        appShell.classList.remove('hidden');
        setMessage(loginMessage, '', '');
      } catch (error) {
        setMessage(loginMessage, 'error', error.message || String(error));
      } finally {
        loginButton.disabled = false;
      }
    });

    salesViewsForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      saveButton.disabled = true;
      setMessage(saveMessage, '', 'Kaydediliyor...');

      try {
        if (!authToken) {
          throw new Error('Oturum bulunamadi.');
        }

        const saveResponse = await fetch('/admin/sales-views', {
          method: 'POST',
          headers: {
            'content-type': 'application/json',
            authorization: `Bearer ${authToken}`
          },
          body: JSON.stringify({
            orderViewName: value('orderViewName'),
            orderLineViewName: value('orderLineViewName'),
            joinColumnName: value('joinColumnName'),
            businessDomain: 'sales'
          })
        });

        if (!saveResponse.ok) {
          throw new Error(await readError(saveResponse));
        }

        const saved = await saveResponse.json();
        setMessage(saveMessage, 'ok', `${saved.tenantName}: ${saved.orderViewName} -> ${saved.orderLineViewName} (${saved.joinColumnName}) kaydedildi.`);
      } catch (error) {
        setMessage(saveMessage, 'error', error.message || String(error));
      } finally {
        saveButton.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}
#pragma warning restore CS8321

static void LoadLocalEnvFile()
{
    var envPath = Path.Combine(AppContext.BaseDirectory, "env.local");
    if (!File.Exists(envPath))
    {
        envPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "env.local"));
    }

    if (!File.Exists(envPath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim().Trim('"');
        Environment.SetEnvironmentVariable(key, value);
    }
}

static async Task ApplyRegistryMigrationAsync()
{
    var schemaPath = ResolveRegistrySchemaPath();
    if (!File.Exists(schemaPath))
    {
        throw new FileNotFoundException("Registry schema file not found.", schemaPath);
    }

    var sql = await File.ReadAllTextAsync(schemaPath);
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static string ResolveRegistrySchemaPath()
{
    var publishedPath = Path.Combine(AppContext.BaseDirectory, "database", "001_registry_schema.sql");
    if (File.Exists(publishedPath))
    {
        return publishedPath;
    }

    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "database", "001_registry_schema.sql"));
}

static async Task SeedAdminAsync()
{
    var username = Environment.GetEnvironmentVariable("VGANTT_SEED_USERNAME") ?? "admin";
    var password = Environment.GetEnvironmentVariable("VGANTT_SEED_PASSWORD") ?? "admin123";
    var displayName = Environment.GetEnvironmentVariable("VGANTT_SEED_DISPLAY_NAME") ?? "Admin";
    var tenantName = Environment.GetEnvironmentVariable("VGANTT_SEED_TENANT_NAME") ?? "Demo ERP Tenant";
    var tenantCode = Environment.GetEnvironmentVariable("VGANTT_SEED_TENANT_CODE") ?? "demo";

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var tenantId = Guid.NewGuid();
    await using (var tenantCommand = new NpgsqlCommand("""
        insert into tenants (id, name, code)
        values (@id, @name, @code)
        on conflict (code) do update set name = excluded.name
        returning id
        """, connection, transaction))
    {
        tenantCommand.Parameters.AddWithValue("id", tenantId);
        tenantCommand.Parameters.AddWithValue("name", tenantName);
        tenantCommand.Parameters.AddWithValue("code", tenantCode);
        tenantId = (Guid)(await tenantCommand.ExecuteScalarAsync() ?? tenantId);
    }

    var userId = Guid.NewGuid();
    await using (var userCommand = new NpgsqlCommand("""
        insert into users (id, username, password_hash, display_name)
        values (@id, @username, @password_hash, @display_name)
        on conflict (username) do update
        set password_hash = excluded.password_hash,
            display_name = excluded.display_name
        returning id
        """, connection, transaction))
    {
        userCommand.Parameters.AddWithValue("id", userId);
        userCommand.Parameters.AddWithValue("username", username);
        userCommand.Parameters.AddWithValue("password_hash", PasswordHasher.Hash(password));
        userCommand.Parameters.AddWithValue("display_name", displayName);
        userId = (Guid)(await userCommand.ExecuteScalarAsync() ?? userId);
    }

    await using (var linkCommand = new NpgsqlCommand("""
        insert into user_tenants (user_id, tenant_id, role)
        values (@user_id, @tenant_id, 'admin')
        on conflict (user_id, tenant_id) do update set role = excluded.role
        """, connection, transaction))
    {
        linkCommand.Parameters.AddWithValue("user_id", userId);
        linkCommand.Parameters.AddWithValue("tenant_id", tenantId);
        await linkCommand.ExecuteNonQueryAsync();
    }

    await SeedTenantDbConnectionAsync(connection, transaction, tenantId);

    await transaction.CommitAsync();
}

static async Task SeedTenantDbConnectionAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId
)
{
    var tenantConnection = LoadTenantDbConnectionFromEnvironment();
    if (tenantConnection is null)
    {
        return;
    }

    await using (var deactivateCommand = new NpgsqlCommand("""
        update tenant_db_connections
        set is_active = false
        where tenant_id = @tenant_id
          and is_active = true
        """, connection, transaction))
    {
        deactivateCommand.Parameters.AddWithValue("tenant_id", tenantId);
        await deactivateCommand.ExecuteNonQueryAsync();
    }

    await using var command = new NpgsqlCommand("""
        insert into tenant_db_connections (
            tenant_id,
            provider,
            host,
            port,
            database_name,
            username,
            password_value,
            ssl_mode,
            is_active
        )
        values (
            @tenant_id,
            @provider,
            @host,
            @port,
            @database_name,
            @username,
            @password_value,
            @ssl_mode,
            true
        )
        """, connection, transaction);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    command.Parameters.AddWithValue("provider", tenantConnection.Provider);
    command.Parameters.AddWithValue("host", tenantConnection.Host);
    command.Parameters.AddWithValue("port", tenantConnection.Port);
    command.Parameters.AddWithValue("database_name", tenantConnection.DatabaseName);
    command.Parameters.AddWithValue("username", tenantConnection.Username);
    command.Parameters.AddWithValue("password_value", tenantConnection.Password);
    command.Parameters.AddWithValue("ssl_mode", tenantConnection.SslMode);
    await command.ExecuteNonQueryAsync();
}

static async Task<AuthenticatedUser?> AuthenticateAsync(string username, string password)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select u.id, t.id, u.display_name, u.password_hash, t.name as tenant_name, t.code as tenant_code, ut.role
        from users u
        join user_tenants ut on ut.user_id = u.id
        join tenants t on t.id = ut.tenant_id
        where u.username = @username
          and u.is_active = true
          and t.is_active = true
        order by t.created_at
        limit 1
        """, connection);

    command.Parameters.AddWithValue("username", username);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    var userId = reader.GetGuid(0);
    var tenantId = reader.GetGuid(1);
    var displayName = reader.GetString(2);
    var passwordHash = reader.GetString(3);
    var tenantName = reader.GetString(4);
    var tenantCode = reader.GetString(5);
    var role = reader.GetString(6);

    return PasswordHasher.Verify(password, passwordHash)
        ? new AuthenticatedUser(userId, tenantId, displayName, tenantName, tenantCode, role)
        : null;
}

static async Task<AuthenticatedUser?> LoadAuthenticatedUserAsync(Guid userId, Guid tenantId)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select u.id, t.id, u.display_name, t.name as tenant_name, t.code as tenant_code, ut.role
        from users u
        join user_tenants ut on ut.user_id = u.id
        join tenants t on t.id = ut.tenant_id
        where u.id = @user_id
          and t.id = @tenant_id
          and u.is_active = true
          and t.is_active = true
        limit 1
        """, connection);

    command.Parameters.AddWithValue("user_id", userId);
    command.Parameters.AddWithValue("tenant_id", tenantId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    return new AuthenticatedUser(
        UserId: reader.GetGuid(0),
        TenantId: reader.GetGuid(1),
        DisplayName: reader.GetString(2),
        TenantName: reader.GetString(3),
        TenantCode: reader.GetString(4),
        Role: reader.GetString(5)
    );
}

static async Task CreateMobileLoginSessionAsync(AuthenticatedUser user, LoginRequest login, string token)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    await using (var sessionCommand = new NpgsqlCommand("""
        insert into mobile_login_sessions (
            user_id,
            tenant_id,
            access_token_hash,
            device_id,
            device_name,
            platform,
            app_version,
            push_token,
            expires_at
        )
        values (
            @user_id,
            @tenant_id,
            @access_token_hash,
            @device_id,
            @device_name,
            @platform,
            @app_version,
            @push_token,
            @expires_at
        )
        """, connection, transaction))
    {
        sessionCommand.Parameters.AddWithValue("user_id", user.UserId);
        sessionCommand.Parameters.AddWithValue("tenant_id", user.TenantId);
        sessionCommand.Parameters.AddWithValue("access_token_hash", TokenHasher.Hash(token));
        sessionCommand.Parameters.AddWithValue("device_id", (object?)login.DeviceId ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("device_name", (object?)login.DeviceName ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("platform", (object?)login.Platform ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("app_version", (object?)login.AppVersion ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("push_token", (object?)login.PushToken ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("expires_at", DateTimeOffset.UtcNow.AddDays(30));
        await sessionCommand.ExecuteNonQueryAsync();
    }

    await using (var userCommand = new NpgsqlCommand("""
        update users
        set last_login_at = now(),
            updated_at = now()
        where id = @user_id
        """, connection, transaction))
    {
        userCommand.Parameters.AddWithValue("user_id", user.UserId);
        await userCommand.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static async Task<AuthenticatedUser?> ValidateMobileSessionAsync(string? token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        update mobile_login_sessions s
        set last_seen_at = now(),
            updated_at = now()
        from users u, tenants t, user_tenants ut
        where s.user_id = u.id
          and s.tenant_id = t.id
          and ut.user_id = u.id
          and ut.tenant_id = t.id
          and s.access_token_hash = @access_token_hash
          and s.revoked_at is null
          and s.expires_at > now()
          and u.is_active = true
          and t.is_active = true
        returning s.user_id, s.tenant_id, u.display_name, t.name, t.code, ut.role
        """, connection);

    command.Parameters.AddWithValue("access_token_hash", TokenHasher.Hash(token));

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    return new AuthenticatedUser(
        UserId: reader.GetGuid(0),
        TenantId: reader.GetGuid(1),
        DisplayName: reader.GetString(2),
        TenantName: reader.GetString(3),
        TenantCode: reader.GetString(4),
        Role: reader.GetString(5)
    );
}

static async Task<AuthenticatedUser?> ValidateAccessTokenAsync(string? token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    if (JwtTokenService.TryValidate(token, out var userId, out var tenantId))
    {
        return await LoadAuthenticatedUserAsync(userId, tenantId);
    }

    return await ValidateMobileSessionAsync(token);
}

static async Task<AdminDashboardResponse> LoadAdminDashboardAsync(Guid tenantId)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select
            (select count(*) from tenant_erp_objects where tenant_id = @tenant_id and object_type = 'view') as view_count,
            (select count(*)
             from tenant_erp_object_columns c
             join tenant_erp_objects o on o.id = c.object_id
             where o.tenant_id = @tenant_id) as column_count,
            (select count(*) from tenant_erp_relations where tenant_id = @tenant_id) as relation_count
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();

    return new AdminDashboardResponse(
        ViewCount: reader.GetInt64(0),
        ColumnCount: reader.GetInt64(1),
        RelationCount: reader.GetInt64(2)
    );
}

static async Task<AdminViewResponse[]> LoadAdminViewsAsync(Guid tenantId)
{
    var views = new List<AdminViewResponse>();
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select
            id,
            object_name,
            coalesce(display_name_tr, ''),
            coalesce(description_tr, description, ''),
            is_active
        from tenant_erp_objects
        where tenant_id = @tenant_id
          and object_type = 'view'
        order by object_name
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        views.Add(new AdminViewResponse(
            ViewId: reader.GetGuid(0),
            ViewName: reader.GetString(1),
            DisplayNameTr: reader.GetString(2),
            DescriptionTr: reader.GetString(3),
            IsActive: reader.GetBoolean(4)
        ));
    }

    return views.ToArray();
}

static async Task<AdminViewResponse> SaveAdminViewAsync(AuthenticatedUser session, SaveAdminViewRequest request)
{
    var viewName = request.ViewName.Trim();
    var displayNameTr = request.DisplayNameTr?.Trim();
    var descriptionTr = request.DescriptionTr?.Trim();

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var connectionId = await GetActiveTenantConnectionIdAsync(connection, transaction, session.TenantId);
    NpgsqlCommand command;
    if (request.ViewId is Guid viewId && viewId != Guid.Empty)
    {
        command = new NpgsqlCommand("""
            update tenant_erp_objects
            set connection_id = @connection_id,
                object_name = @object_name,
                object_type = 'view',
                business_domain = 'admin',
                display_name_tr = @display_name_tr,
                description = @description_tr,
                description_tr = @description_tr,
                is_queryable = true,
                is_active = @is_active,
                updated_at = now()
            where id = @id
              and tenant_id = @tenant_id
            returning id, object_name, coalesce(display_name_tr, ''), coalesce(description_tr, description, ''), is_active
            """, connection, transaction);
        command.Parameters.AddWithValue("id", viewId);
    }
    else
    {
        command = new NpgsqlCommand("""
            insert into tenant_erp_objects (
                tenant_id,
                connection_id,
                object_name,
                object_type,
                business_domain,
                display_name_tr,
                description,
                description_tr,
                is_queryable,
                is_active,
                updated_at
            )
            values (
                @tenant_id,
                @connection_id,
                @object_name,
                'view',
                'admin',
                @display_name_tr,
                @description_tr,
                @description_tr,
                true,
                @is_active,
                now()
            )
            on conflict (tenant_id, object_name) do update
            set connection_id = excluded.connection_id,
                display_name_tr = excluded.display_name_tr,
                description = excluded.description,
                description_tr = excluded.description_tr,
                is_queryable = true,
                is_active = excluded.is_active,
                updated_at = now()
            returning id, object_name, coalesce(display_name_tr, ''), coalesce(description_tr, description, ''), is_active
            """, connection, transaction);
    }

    await using (command)
    {
        command.Parameters.AddWithValue("tenant_id", session.TenantId);
        command.Parameters.AddWithValue("connection_id", (object?)connectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("object_name", viewName);
        command.Parameters.AddWithValue("display_name_tr", (object?)displayNameTr ?? DBNull.Value);
        command.Parameters.AddWithValue("description_tr", (object?)descriptionTr ?? DBNull.Value);
        command.Parameters.AddWithValue("is_active", request.IsActive);

        AdminViewResponse saved;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("View could not be saved.");
            }

            saved = new AdminViewResponse(
                ViewId: reader.GetGuid(0),
                ViewName: reader.GetString(1),
                DisplayNameTr: reader.GetString(2),
                DescriptionTr: reader.GetString(3),
                IsActive: reader.GetBoolean(4)
            );
        }

        await transaction.CommitAsync();
        return saved;
    }
}

static async Task<AdminViewColumnResponse[]> LoadAdminViewColumnsAsync(Guid tenantId, Guid viewId)
{
    var columns = new List<AdminViewColumnResponse>();
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select
            c.id,
            c.object_id,
            c.column_name,
            coalesce(c.display_name_tr, m.customer_label, ''),
            coalesce(c.data_type, ''),
            coalesce(c.semantic_meaning_tr, m.customer_description, c.description, ''),
            c.is_filterable,
            c.is_groupable,
            c.is_summable,
            c.is_active
        from tenant_erp_object_columns c
        join tenant_erp_objects o on o.id = c.object_id
        left join tenant_erp_column_meanings m
            on m.column_id = c.id
           and m.language_code = 'tr'
        where o.tenant_id = @tenant_id
          and c.object_id = @view_id
        order by c.column_name
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    command.Parameters.AddWithValue("view_id", viewId);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        columns.Add(new AdminViewColumnResponse(
            ColumnId: reader.GetGuid(0),
            ViewId: reader.GetGuid(1),
            ColumnName: reader.GetString(2),
            DisplayNameTr: reader.GetString(3),
            DataType: reader.GetString(4),
            SemanticMeaningTr: reader.GetString(5),
            IsFilterable: reader.GetBoolean(6),
            IsGroupable: reader.GetBoolean(7),
            IsSummable: reader.GetBoolean(8),
            IsActive: reader.GetBoolean(9)
        ));
    }

    return columns.ToArray();
}

static async Task<SaveAdminViewColumnsResponse> SaveAdminViewColumnsAsync(
    AuthenticatedUser session,
    SaveAdminViewColumnsRequest request
)
{
    var columns = (request.Columns ?? [])
        .Where(column => !string.IsNullOrWhiteSpace(column.ColumnName))
        .Select(column => column with
        {
            ColumnName = column.ColumnName.Trim(),
            DisplayNameTr = column.DisplayNameTr?.Trim(),
            DataType = column.DataType?.Trim(),
            SemanticMeaningTr = column.SemanticMeaningTr?.Trim()
        })
        .ToArray();

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await EnsureTenantViewAsync(connection, transaction, session.TenantId, request.ViewId);

    var savedCount = 0;
    foreach (var column in columns)
    {
        await using var command = new NpgsqlCommand("""
            insert into tenant_erp_object_columns (
                object_id,
                column_name,
                data_type,
                business_name,
                display_name_tr,
                description,
                semantic_meaning_tr,
                is_sensitive,
                is_filterable,
                is_groupable,
                is_summable,
                is_active,
                updated_at
            )
            values (
                @object_id,
                @column_name,
                @data_type,
                @business_name,
                @display_name_tr,
                @semantic_meaning_tr,
                @semantic_meaning_tr,
                false,
                @is_filterable,
                @is_groupable,
                @is_summable,
                @is_active,
                now()
            )
            on conflict (object_id, column_name) do update
            set data_type = excluded.data_type,
                business_name = excluded.business_name,
                display_name_tr = excluded.display_name_tr,
                description = excluded.description,
                semantic_meaning_tr = excluded.semantic_meaning_tr,
                is_filterable = excluded.is_filterable,
                is_groupable = excluded.is_groupable,
                is_summable = excluded.is_summable,
                is_active = excluded.is_active,
                updated_at = now()
            returning id
            """, connection, transaction);

        command.Parameters.AddWithValue("object_id", request.ViewId);
        command.Parameters.AddWithValue("column_name", column.ColumnName);
        command.Parameters.AddWithValue("data_type", (object?)column.DataType ?? DBNull.Value);
        command.Parameters.AddWithValue("business_name", string.IsNullOrWhiteSpace(column.DisplayNameTr) ? column.ColumnName : column.DisplayNameTr);
        command.Parameters.AddWithValue("display_name_tr", (object?)column.DisplayNameTr ?? DBNull.Value);
        command.Parameters.AddWithValue("semantic_meaning_tr", (object?)column.SemanticMeaningTr ?? DBNull.Value);
        command.Parameters.AddWithValue("is_filterable", column.IsFilterable);
        command.Parameters.AddWithValue("is_groupable", column.IsGroupable);
        command.Parameters.AddWithValue("is_summable", column.IsSummable);
        command.Parameters.AddWithValue("is_active", column.IsActive);

        var columnId = (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Column could not be saved."));
        await UpsertColumnMeaningAsync(connection, transaction, columnId, column.DisplayNameTr, column.SemanticMeaningTr);
        savedCount++;
    }

    await transaction.CommitAsync();
    return new SaveAdminViewColumnsResponse(request.ViewId, savedCount);
}

static async Task<NormalizeAdminViewColumnsResponse> NormalizeAdminViewColumnsAsync(
    AuthenticatedUser session,
    NormalizeAdminViewColumnsRequest request
)
{
    await ValidateTenantViewOwnershipAsync(session.TenantId, request.ViewId);

    var draftColumns = request.Columns ?? [];
    if (draftColumns.Length == 0 && !string.IsNullOrWhiteSpace(request.RawInput))
    {
        draftColumns = ParseBulkColumnsFromRawText(request.RawInput);
    }

    var normalized = draftColumns
        .Where(column => !string.IsNullOrWhiteSpace(column.ColumnName))
        .Select(NormalizeAdminViewColumnInput)
        .GroupBy(column => column.ColumnName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Last())
        .ToArray();

    if (normalized.Length == 0)
    {
        return new NormalizeAdminViewColumnsResponse(
            Message: "Islenecek kolon bulunamadi.",
            Columns: []
        );
    }

    var aiKey = Environment.GetEnvironmentVariable("VGANTT_OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(aiKey))
    {
        return new NormalizeAdminViewColumnsResponse(
            Message: "AI anahtari yok; kolonlar kurallara gore sadeleştirildi.",
            Columns: normalized
        );
    }

    try
    {
        var aiColumns = await NormalizeColumnsWithOpenAiAsync(normalized, aiKey);
        var merged = aiColumns
            .Where(column => !string.IsNullOrWhiteSpace(column.ColumnName))
            .Select(NormalizeAdminViewColumnInput)
            .GroupBy(column => column.ColumnName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        return new NormalizeAdminViewColumnsResponse(
            Message: "Kolonlar AI ile duzeltildi.",
            Columns: merged.Length == 0 ? normalized : merged
        );
    }
    catch
    {
        return new NormalizeAdminViewColumnsResponse(
            Message: "AI su an kullanilamadi; kurala dayali duzeltme uygulandi.",
            Columns: normalized
        );
    }
}

static async Task ValidateTenantViewOwnershipAsync(Guid tenantId, Guid viewId)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select 1
        from tenant_erp_objects
        where id = @view_id
          and tenant_id = @tenant_id
        limit 1
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    command.Parameters.AddWithValue("view_id", viewId);

    var exists = await command.ExecuteScalarAsync();
    if (exists is null)
    {
        throw new InvalidOperationException("Secilen view bulunamadi.");
    }
}

static AdminViewColumnInput[] ParseBulkColumnsFromRawText(string rawInput)
{
    var rows = rawInput
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(row => row.Trim())
        .Where(row => row.Length > 0);

    var columns = new List<AdminViewColumnInput>();
    foreach (var row in rows)
    {
        var parts = row.Split(['\t', ';', '|'], StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            continue;
        }

        columns.Add(new AdminViewColumnInput(
            ColumnName: parts[0],
            DisplayNameTr: parts.Length > 1 ? parts[1] : null,
            DataType: parts.Length > 2 ? parts[2] : null,
            SemanticMeaningTr: parts.Length > 3 ? parts[3] : null,
            IsFilterable: true,
            IsGroupable: true,
            IsSummable: Regex.IsMatch(parts[0], "(amount|total|qty|quantity|tutar|adet|miktar)", RegexOptions.IgnoreCase),
            IsActive: true
        ));
    }

    return columns.ToArray();
}

static AdminViewColumnInput NormalizeAdminViewColumnInput(AdminViewColumnInput column)
{
    var normalizedName = Regex.Replace(column.ColumnName.Trim().ToLowerInvariant(), @"[^a-z0-9_]", "_");
    normalizedName = Regex.Replace(normalizedName, @"_+", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(normalizedName))
    {
        normalizedName = "column";
    }

    var displayName = string.IsNullOrWhiteSpace(column.DisplayNameTr)
        ? normalizedName.Replace('_', ' ')
        : column.DisplayNameTr.Trim();
    var semantic = string.IsNullOrWhiteSpace(column.SemanticMeaningTr)
        ? displayName
        : column.SemanticMeaningTr.Trim();
    var dataType = string.IsNullOrWhiteSpace(column.DataType) ? "text" : column.DataType.Trim().ToLowerInvariant();
    var isSummable = column.IsSummable || Regex.IsMatch(normalizedName, "(amount|total|qty|quantity|tutar|adet|miktar)", RegexOptions.IgnoreCase);

    return column with
    {
        ColumnName = normalizedName,
        DisplayNameTr = displayName,
        DataType = dataType,
        SemanticMeaningTr = semantic,
        IsSummable = isSummable
    };
}

static async Task<AdminViewColumnInput[]> NormalizeColumnsWithOpenAiAsync(AdminViewColumnInput[] columns, string apiKey)
{
    var model = Environment.GetEnvironmentVariable("VGANTT_AI_MODEL") ?? "gpt-5-mini";
    var endpoint = Environment.GetEnvironmentVariable("VGANTT_OPENAI_RESPONSES_URL") ?? "https://api.openai.com/v1/responses";
    var requestBody = new Dictionary<string, object?>
    {
        ["model"] = model,
        ["instructions"] = "Normalize ERP column definitions. Return only JSON schema. Keep meaning in Turkish. Use lowercase snake_case for columnName.",
        ["input"] = JsonSerializer.Serialize(columns, AppJson.Options),
        ["max_output_tokens"] = 1400,
        ["text"] = new Dictionary<string, object?>
        {
            ["format"] = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["name"] = "normalized_columns",
                ["strict"] = true,
                ["schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new[] { "columns" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["columns"] = new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["items"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["required"] = new[] { "columnName", "displayNameTr", "dataType", "semanticMeaningTr", "isFilterable", "isGroupable", "isSummable", "isActive" },
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["columnName"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["displayNameTr"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["dataType"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["semanticMeaningTr"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["isFilterable"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                                    ["isGroupable"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                                    ["isSummable"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                                    ["isActive"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
    requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    requestMessage.Content = new StringContent(JsonSerializer.Serialize(requestBody, AppJson.Options), Encoding.UTF8, "application/json");

    using var response = await httpClient.SendAsync(requestMessage);
    var responseBody = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"AI model error: {(int)response.StatusCode} {responseBody}");
    }

    using var document = JsonDocument.Parse(responseBody);
    var outputText = ExtractOpenAiOutputText(document.RootElement);
    if (string.IsNullOrWhiteSpace(outputText))
    {
        throw new InvalidOperationException("AI model did not return normalized columns.");
    }

    var parsed = JsonSerializer.Deserialize<NormalizedColumnsOutput>(outputText, AppJson.Options);
    return parsed?.Columns ?? [];
}

static async Task<AdminRelationResponse[]> LoadAdminRelationsAsync(Guid tenantId)
{
    var relations = new Dictionary<Guid, AdminRelationResponseBuilder>();
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select
            r.id,
            r.relation_name,
            r.source_view_id,
            source_view.object_name,
            r.target_view_id,
            target_view.object_name,
            r.join_type,
            coalesce(r.description_tr, ''),
            r.is_active,
            rc.id,
            rc.source_column_name,
            rc.target_column_name,
            rc.ordinal
        from tenant_erp_relations r
        join tenant_erp_objects source_view on source_view.id = r.source_view_id
        join tenant_erp_objects target_view on target_view.id = r.target_view_id
        left join tenant_erp_relation_columns rc on rc.relation_id = r.id
        where r.tenant_id = @tenant_id
        order by r.relation_name, rc.ordinal
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var relationId = reader.GetGuid(0);
        if (!relations.TryGetValue(relationId, out var relation))
        {
            relation = new AdminRelationResponseBuilder(
                RelationId: relationId,
                RelationName: reader.GetString(1),
                SourceViewId: reader.GetGuid(2),
                SourceViewName: reader.GetString(3),
                TargetViewId: reader.GetGuid(4),
                TargetViewName: reader.GetString(5),
                JoinType: reader.GetString(6),
                DescriptionTr: reader.GetString(7),
                IsActive: reader.GetBoolean(8)
            );
            relations[relationId] = relation;
        }

        if (!reader.IsDBNull(9))
        {
            relation.Columns.Add(new AdminRelationColumnResponse(
                RelationColumnId: reader.GetGuid(9),
                SourceColumnName: reader.GetString(10),
                TargetColumnName: reader.GetString(11),
                Ordinal: reader.GetInt32(12)
            ));
        }
    }

    return relations.Values
        .Select(relation => relation.ToResponse())
        .ToArray();
}

static async Task<AdminRelationResponse> SaveAdminRelationAsync(
    AuthenticatedUser session,
    SaveAdminRelationRequest request
)
{
    var relationName = request.RelationName.Trim();
    var joinType = NormalizeJoinType(request.JoinType);
    var descriptionTr = request.DescriptionTr?.Trim();
    var columns = (request.Columns ?? [])
        .Where(column =>
            !string.IsNullOrWhiteSpace(column.SourceColumnName) &&
            !string.IsNullOrWhiteSpace(column.TargetColumnName)
        )
        .Select((column, index) => column with
        {
            SourceColumnName = column.SourceColumnName.Trim(),
            TargetColumnName = column.TargetColumnName.Trim(),
            Ordinal = column.Ordinal <= 0 ? index + 1 : column.Ordinal
        })
        .OrderBy(column => column.Ordinal)
        .ToArray();

    if (columns.Length == 0)
    {
        throw new InvalidOperationException("At least one relation column is required.");
    }

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var sourceViewName = await EnsureTenantViewAsync(connection, transaction, session.TenantId, request.SourceViewId);
    var targetViewName = await EnsureTenantViewAsync(connection, transaction, session.TenantId, request.TargetViewId);

    Guid relationId;
    if (request.RelationId is Guid existingRelationId && existingRelationId != Guid.Empty)
    {
        await using var updateCommand = new NpgsqlCommand("""
            update tenant_erp_relations
            set relation_name = @relation_name,
                source_view_id = @source_view_id,
                target_view_id = @target_view_id,
                join_type = @join_type,
                description_tr = @description_tr,
                is_active = @is_active,
                updated_at = now()
            where id = @id
              and tenant_id = @tenant_id
            returning id
            """, connection, transaction);

        updateCommand.Parameters.AddWithValue("id", existingRelationId);
        updateCommand.Parameters.AddWithValue("tenant_id", session.TenantId);
        updateCommand.Parameters.AddWithValue("relation_name", relationName);
        updateCommand.Parameters.AddWithValue("source_view_id", request.SourceViewId);
        updateCommand.Parameters.AddWithValue("target_view_id", request.TargetViewId);
        updateCommand.Parameters.AddWithValue("join_type", joinType);
        updateCommand.Parameters.AddWithValue("description_tr", (object?)descriptionTr ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("is_active", request.IsActive);

        relationId = (Guid)(await updateCommand.ExecuteScalarAsync() ?? throw new InvalidOperationException("Relation could not be saved."));
    }
    else
    {
        await using var insertCommand = new NpgsqlCommand("""
            insert into tenant_erp_relations (
                tenant_id,
                relation_name,
                source_view_id,
                target_view_id,
                join_type,
                description_tr,
                is_active,
                updated_at
            )
            values (
                @tenant_id,
                @relation_name,
                @source_view_id,
                @target_view_id,
                @join_type,
                @description_tr,
                @is_active,
                now()
            )
            on conflict (tenant_id, relation_name) do update
            set source_view_id = excluded.source_view_id,
                target_view_id = excluded.target_view_id,
                join_type = excluded.join_type,
                description_tr = excluded.description_tr,
                is_active = excluded.is_active,
                updated_at = now()
            returning id
            """, connection, transaction);

        insertCommand.Parameters.AddWithValue("tenant_id", session.TenantId);
        insertCommand.Parameters.AddWithValue("relation_name", relationName);
        insertCommand.Parameters.AddWithValue("source_view_id", request.SourceViewId);
        insertCommand.Parameters.AddWithValue("target_view_id", request.TargetViewId);
        insertCommand.Parameters.AddWithValue("join_type", joinType);
        insertCommand.Parameters.AddWithValue("description_tr", (object?)descriptionTr ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("is_active", request.IsActive);

        relationId = (Guid)(await insertCommand.ExecuteScalarAsync() ?? throw new InvalidOperationException("Relation could not be saved."));
    }

    await using (var deleteColumnsCommand = new NpgsqlCommand("""
        delete from tenant_erp_relation_columns
        where relation_id = @relation_id
        """, connection, transaction))
    {
        deleteColumnsCommand.Parameters.AddWithValue("relation_id", relationId);
        await deleteColumnsCommand.ExecuteNonQueryAsync();
    }

    foreach (var column in columns)
    {
        await using var columnCommand = new NpgsqlCommand("""
            insert into tenant_erp_relation_columns (
                relation_id,
                source_column_name,
                target_column_name,
                ordinal,
                updated_at
            )
            values (
                @relation_id,
                @source_column_name,
                @target_column_name,
                @ordinal,
                now()
            )
            """, connection, transaction);

        columnCommand.Parameters.AddWithValue("relation_id", relationId);
        columnCommand.Parameters.AddWithValue("source_column_name", column.SourceColumnName);
        columnCommand.Parameters.AddWithValue("target_column_name", column.TargetColumnName);
        columnCommand.Parameters.AddWithValue("ordinal", column.Ordinal);
        await columnCommand.ExecuteNonQueryAsync();
    }

    await SyncLegacyRelationshipRowsAsync(
        connection,
        transaction,
        session.TenantId,
        request.SourceViewId,
        request.TargetViewId,
        descriptionTr,
        columns
    );

    await transaction.CommitAsync();

    return new AdminRelationResponse(
        RelationId: relationId,
        RelationName: relationName,
        SourceViewId: request.SourceViewId,
        SourceViewName: sourceViewName,
        TargetViewId: request.TargetViewId,
        TargetViewName: targetViewName,
        JoinType: joinType,
        DescriptionTr: descriptionTr ?? string.Empty,
        IsActive: request.IsActive,
        Columns: columns.Select(column => new AdminRelationColumnResponse(
            RelationColumnId: Guid.Empty,
            SourceColumnName: column.SourceColumnName,
            TargetColumnName: column.TargetColumnName,
            Ordinal: column.Ordinal
        )).ToArray()
    );
}

static async Task<string> EnsureTenantViewAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId,
    Guid viewId
)
{
    await using var command = new NpgsqlCommand("""
        select object_name
        from tenant_erp_objects
        where tenant_id = @tenant_id
          and id = @view_id
          and object_type = 'view'
        """, connection, transaction);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    command.Parameters.AddWithValue("view_id", viewId);

    return (string?)await command.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("View not found for this tenant.");
}

static async Task UpsertColumnMeaningAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid columnId,
    string? displayNameTr,
    string? semanticMeaningTr
)
{
    if (string.IsNullOrWhiteSpace(displayNameTr) && string.IsNullOrWhiteSpace(semanticMeaningTr))
    {
        return;
    }

    await using var command = new NpgsqlCommand("""
        insert into tenant_erp_column_meanings (
            column_id,
            customer_label,
            customer_description,
            language_code,
            updated_at
        )
        values (
            @column_id,
            @customer_label,
            @customer_description,
            'tr',
            now()
        )
        on conflict (column_id, language_code) do update
        set customer_label = excluded.customer_label,
            customer_description = excluded.customer_description,
            updated_at = now()
        """, connection, transaction);

    command.Parameters.AddWithValue("column_id", columnId);
    command.Parameters.AddWithValue("customer_label", displayNameTr ?? string.Empty);
    command.Parameters.AddWithValue("customer_description", (object?)semanticMeaningTr ?? DBNull.Value);
    await command.ExecuteNonQueryAsync();
}

static string NormalizeJoinType(string? joinType)
{
    var normalized = string.IsNullOrWhiteSpace(joinType)
        ? "INNER JOIN"
        : joinType.Trim().ToUpperInvariant();

    return normalized switch
    {
        "INNER" or "INNER JOIN" => "INNER JOIN",
        "LEFT" or "LEFT JOIN" => "LEFT JOIN",
        "RIGHT" or "RIGHT JOIN" => "RIGHT JOIN",
        "FULL" or "FULL JOIN" => "FULL JOIN",
        _ => throw new InvalidOperationException("join_type must be INNER JOIN, LEFT JOIN, RIGHT JOIN or FULL JOIN.")
    };
}

static async Task SyncLegacyRelationshipRowsAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId,
    Guid sourceViewId,
    Guid targetViewId,
    string? descriptionTr,
    SaveAdminRelationColumnRequest[] columns
)
{
    await using (var deactivateCommand = new NpgsqlCommand("""
        update tenant_erp_object_relationships
        set is_active = false,
            updated_at = now()
        where tenant_id = @tenant_id
          and parent_object_id = @parent_object_id
          and child_object_id = @child_object_id
        """, connection, transaction))
    {
        deactivateCommand.Parameters.AddWithValue("tenant_id", tenantId);
        deactivateCommand.Parameters.AddWithValue("parent_object_id", sourceViewId);
        deactivateCommand.Parameters.AddWithValue("child_object_id", targetViewId);
        await deactivateCommand.ExecuteNonQueryAsync();
    }

    foreach (var column in columns)
    {
        await using var command = new NpgsqlCommand("""
            insert into tenant_erp_object_relationships (
                tenant_id,
                parent_object_id,
                child_object_id,
                parent_column_name,
                child_column_name,
                relationship_type,
                description,
                is_active,
                updated_at
            )
            values (
                @tenant_id,
                @parent_object_id,
                @child_object_id,
                @parent_column_name,
                @child_column_name,
                'one_to_many',
                @description,
                true,
                now()
            )
            on conflict (
                tenant_id,
                parent_object_id,
                child_object_id,
                parent_column_name,
                child_column_name
            ) do update
            set relationship_type = excluded.relationship_type,
                description = excluded.description,
                is_active = true,
                updated_at = now()
            """, connection, transaction);

        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("parent_object_id", sourceViewId);
        command.Parameters.AddWithValue("child_object_id", targetViewId);
        command.Parameters.AddWithValue("parent_column_name", column.SourceColumnName);
        command.Parameters.AddWithValue("child_column_name", column.TargetColumnName);
        command.Parameters.AddWithValue("description", (object?)descriptionTr ?? "View relation column mapping.");
        await command.ExecuteNonQueryAsync();
    }
}

static async Task<SalesViewsResponse> SaveSalesViewsAsync(AuthenticatedUser session, SaveSalesViewsRequest views)
{
    var view1Name = NormalizeOracleIdentifier(views.EffectiveView1Name);
    var view2Name = NormalizeOracleIdentifier(views.EffectiveView2Name);
    var relationships = views.EffectiveRelationships
        .Select(relationship => new ViewRelationshipRequest(
            NormalizeColumnNameFromInput(relationship.View1ColumnName),
            NormalizeColumnNameFromInput(relationship.View2ColumnName)
        ))
        .Where(relationship =>
            !string.IsNullOrWhiteSpace(relationship.View1ColumnName) &&
            !string.IsNullOrWhiteSpace(relationship.View2ColumnName)
        )
        .Distinct()
        .ToArray();

    if (relationships.Length == 0)
    {
        throw new InvalidOperationException("At least one valid relationship is required.");
    }

    var view1Columns = NormalizeColumnList(views.View1Columns)
        .Concat(relationships.Select(relationship => relationship.View1ColumnName))
        .Distinct()
        .ToArray();
    var view2Columns = NormalizeColumnList(views.View2Columns)
        .Concat(relationships.Select(relationship => relationship.View2ColumnName))
        .Distinct()
        .ToArray();
    var businessDomain = string.IsNullOrWhiteSpace(views.BusinessDomain)
        ? "sales"
        : views.BusinessDomain.Trim().ToLowerInvariant();

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var connectionId = await GetActiveTenantConnectionIdAsync(connection, transaction, session.TenantId);
    var view1ObjectId = await UpsertErpObjectAsync(
        connection,
        transaction,
        session.TenantId,
        connectionId,
        view1Name,
        businessDomain,
        "AI sorgularinda kullanilacak View1 tanimi."
    );
    var view2ObjectId = await UpsertErpObjectAsync(
        connection,
        transaction,
        session.TenantId,
        connectionId,
        view2Name,
        businessDomain,
        "AI sorgularinda kullanilacak View2 tanimi."
    );

    await UpsertErpColumnsAsync(connection, transaction, view1ObjectId, view1Columns);
    await UpsertErpColumnsAsync(connection, transaction, view2ObjectId, view2Columns);

    await using (var deactivateCommand = new NpgsqlCommand("""
        update tenant_erp_object_relationships
        set is_active = false,
            updated_at = now()
        where tenant_id = @tenant_id
          and parent_object_id = @parent_object_id
          and child_object_id = @child_object_id
        """, connection, transaction))
    {
        deactivateCommand.Parameters.AddWithValue("tenant_id", session.TenantId);
        deactivateCommand.Parameters.AddWithValue("parent_object_id", view1ObjectId);
        deactivateCommand.Parameters.AddWithValue("child_object_id", view2ObjectId);
        await deactivateCommand.ExecuteNonQueryAsync();
    }

    foreach (var relationship in relationships)
    {
        await using var relationshipCommand = new NpgsqlCommand("""
            insert into tenant_erp_object_relationships (
                tenant_id,
                parent_object_id,
                child_object_id,
                parent_column_name,
                child_column_name,
                relationship_type,
                description,
                is_active,
                updated_at
            )
            values (
                @tenant_id,
                @parent_object_id,
                @child_object_id,
                @parent_column_name,
                @child_column_name,
                'one_to_many',
                @description,
                true,
                now()
            )
            on conflict (
                tenant_id,
                parent_object_id,
                child_object_id,
                parent_column_name,
                child_column_name
            ) do update
            set relationship_type = excluded.relationship_type,
                description = excluded.description,
                is_active = true,
                updated_at = now()
            """, connection, transaction);

        relationshipCommand.Parameters.AddWithValue("tenant_id", session.TenantId);
        relationshipCommand.Parameters.AddWithValue("parent_object_id", view1ObjectId);
        relationshipCommand.Parameters.AddWithValue("child_object_id", view2ObjectId);
        relationshipCommand.Parameters.AddWithValue("parent_column_name", relationship.View1ColumnName);
        relationshipCommand.Parameters.AddWithValue("child_column_name", relationship.View2ColumnName);
        relationshipCommand.Parameters.AddWithValue("description", "View1 ile View2 arasindaki kolon eslestirmesi.");
        await relationshipCommand.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();

    return new SalesViewsResponse(
        TenantName: session.TenantName,
        View1Name: view1Name,
        View2Name: view2Name,
        Relationships: relationships,
        BusinessDomain: businessDomain,
        View1ColumnCount: view1Columns.Length,
        View2ColumnCount: view2Columns.Length
    );
}

static string NormalizeOracleIdentifier(string value) => value.Trim().ToUpperInvariant();

static string[] NormalizeColumnList(string[]? columns)
{
    return (columns ?? [])
        .SelectMany(SplitColumnInput)
        .Select(NormalizeColumnNameFromInput)
        .Where(column => !string.IsNullOrWhiteSpace(column))
        .Distinct()
        .ToArray();
}

static IEnumerable<string> SplitColumnInput(string value)
{
    var current = new StringBuilder();
    var parenthesisDepth = 0;
    var inDoubleQuote = false;

    foreach (var character in value)
    {
        if (character == '"')
        {
            inDoubleQuote = !inDoubleQuote;
            current.Append(character);
            continue;
        }

        if (!inDoubleQuote)
        {
            if (character == '(')
            {
                parenthesisDepth++;
            }
            else if (character == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }
            else if ((character == ',' || character == '\n' || character == '\r') && parenthesisDepth == 0)
            {
                var item = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(item))
                {
                    yield return item;
                }

                current.Clear();
                continue;
            }
        }

        current.Append(character);
    }

    var finalItem = current.ToString().Trim();
    if (!string.IsNullOrWhiteSpace(finalItem))
    {
        yield return finalItem;
    }
}

static string NormalizeColumnNameFromInput(string value)
{
    var normalized = value.Trim().TrimEnd(',');
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return string.Empty;
    }

    var asIndex = normalized.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
    if (asIndex >= 0)
    {
        normalized = normalized[(asIndex + 4)..].Trim();
    }
    else if (normalized.Contains('(') && normalized.Contains(' '))
    {
        normalized = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
    }

    var dotIndex = normalized.LastIndexOf('.');
    if (dotIndex >= 0)
    {
        normalized = normalized[(dotIndex + 1)..];
    }

    normalized = normalized.Trim().Trim('"').Trim('[', ']').Trim();
    return NormalizeOracleIdentifier(normalized);
}

static async Task<Guid?> GetActiveTenantConnectionIdAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId
)
{
    await using var command = new NpgsqlCommand("""
        select id
        from tenant_db_connections
        where tenant_id = @tenant_id
          and is_active = true
        order by id desc
        limit 1
        """, connection, transaction);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    var value = await command.ExecuteScalarAsync();
    return value is Guid id ? id : null;
}

static async Task<TenantDbHealthResponse> CheckTenantDbHealthAsync(Guid tenantId)
{
    var settings = await LoadActiveTenantDbConnectionAsync(tenantId);
    if (settings is null)
    {
        throw new InvalidOperationException("Active tenant database connection is not configured.");
    }

    if (!string.Equals(settings.Provider, "postgresql", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Unsupported tenant database provider: {settings.Provider}");
    }

    var connectionString = BuildTenantConnectionString(settings);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand("select version()", connection);
    var version = Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;

    return new TenantDbHealthResponse(
        Status: "ok",
        Provider: settings.Provider,
        Host: settings.Host,
        Port: settings.Port,
        DatabaseName: settings.DatabaseName,
        Version: version
    );
}

static async Task<TenantDbConnectionSettings?> LoadActiveTenantDbConnectionAsync(Guid tenantId)
{
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select provider, host, port, database_name, username, password_value, ssl_mode
        from tenant_db_connections
        where tenant_id = @tenant_id
          and is_active = true
        order by id desc
        limit 1
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return null;
    }

    return new TenantDbConnectionSettings(
        Provider: reader.GetString(0),
        Host: reader.GetString(1),
        Port: reader.GetInt32(2),
        DatabaseName: reader.GetString(3),
        Username: reader.GetString(4),
        Password: reader.GetString(5),
        SslMode: reader.GetString(6)
    );
}

static string BuildTenantConnectionString(TenantDbConnectionSettings settings)
{
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = settings.Host,
        Port = settings.Port,
        Database = settings.DatabaseName,
        Username = settings.Username,
        Password = settings.Password,
        SslMode = ParseNpgsqlSslMode(settings.SslMode),
        Timeout = 5,
        CommandTimeout = 5
    };

    return builder.ConnectionString;
}

static SslMode ParseNpgsqlSslMode(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "disable" or "disabled" => SslMode.Disable,
        "prefer" => SslMode.Prefer,
        "require" or "required" => SslMode.Require,
        "verify-ca" or "verifyca" => SslMode.VerifyCA,
        "verify-full" or "verifyfull" => SslMode.VerifyFull,
        _ => SslMode.Require
    };
}

static TenantDbConnectionSettings? LoadTenantDbConnectionFromEnvironment()
{
    var host = ReadEnv("VGANTT_TENANT_DB_HOST", "VGANTT_SEED_TENANT_DB_HOST");
    var databaseName = ReadEnv("VGANTT_TENANT_DB_NAME", "VGANTT_SEED_TENANT_DB_NAME");
    var username = ReadEnv("VGANTT_TENANT_DB_USERNAME", "VGANTT_SEED_TENANT_DB_USERNAME");
    var password = ReadEnv("VGANTT_TENANT_DB_PASSWORD", "VGANTT_SEED_TENANT_DB_PASSWORD");

    if (string.IsNullOrWhiteSpace(host) ||
        string.IsNullOrWhiteSpace(databaseName) ||
        string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    var provider = ReadEnv("VGANTT_TENANT_DB_PROVIDER", "VGANTT_SEED_TENANT_DB_PROVIDER") ?? "postgresql";
    var sslMode = ReadEnv("VGANTT_TENANT_DB_SSL_MODE", "VGANTT_SEED_TENANT_DB_SSL_MODE") ?? "Disable";
    var portValue = ReadEnv("VGANTT_TENANT_DB_PORT", "VGANTT_SEED_TENANT_DB_PORT");
    var port = int.TryParse(portValue, out var configuredPort) ? configuredPort : 5432;

    return new TenantDbConnectionSettings(
        Provider: provider,
        Host: host,
        Port: port,
        DatabaseName: databaseName,
        Username: username,
        Password: password,
        SslMode: sslMode
    );
}

static string? ReadEnv(params string[] names)
{
    foreach (var name in names)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static async Task<AssistantResponse> AnswerAssistantQuestionAsync(
    AuthenticatedUser session,
    string question,
    AssistantHistoryItem[] history
)
{
    var schema = await LoadAssistantSchemaAsync(session.TenantId);
    if (schema.Objects.Length == 0)
    {
        return AskForClarification("Once admin ekraninda sorgulanacak tablo/view ve kolon anlamlarini tanimlamaliyiz.");
    }

    var aiPlan = await BuildOpenAiSqlPlanAsync(schema, question, history);
    if (aiPlan.NeedsClarification)
    {
        return AskForClarification(string.IsNullOrWhiteSpace(aiPlan.ClarificationQuestion)
            ? "Bu sorguyu olusturmak icin bir bilgi daha gerekli. Hangi tablo, kolon veya tarih araligini kullanmaliyim?"
            : aiPlan.ClarificationQuestion);
    }

    var sql = aiPlan.Sql.Trim();
    ValidateAiGeneratedSql(schema, sql);

    AssistantQueryResult result;
    if (string.Equals(schema.Connection.Provider, "postgresql", StringComparison.OrdinalIgnoreCase))
    {
        result = await ExecuteTenantPostgresQueryAsync(schema.Connection, sql);
    }
    else if (string.Equals(schema.Connection.Provider, "oracle", StringComparison.OrdinalIgnoreCase))
    {
        result = await ExecuteTenantOracleQueryAsync(schema.Connection, sql);
    }
    else
    {
        return new AssistantResponse(
            Summary: $"{schema.Connection.Provider} icin AI SQL olusturdu; bu provider icin sorgu calistirma surucusu henuz eklenmedi.",
            Sql: sql,
            Columns: [],
            Rows: []
        );
    }

    return new AssistantResponse(
        Summary: BuildAiSqlSummary(aiPlan, result),
        Sql: sql,
        Columns: result.Columns,
        Rows: result.Rows
    );
}

static AssistantResponse AskForClarification(string message)
{
    return new AssistantResponse(
        Summary: message,
        Sql: string.Empty,
        Columns: [],
        Rows: []
    );
}

static async Task<AiSqlPlan> BuildOpenAiSqlPlanAsync(
    AssistantSchema schema,
    string question,
    AssistantHistoryItem[] history
)
{
    var apiKey = Environment.GetEnvironmentVariable("VGANTT_OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return new AiSqlPlan(
            NeedsClarification: true,
            ClarificationQuestion: "AI SQL modeli aktif degil. Server .env icine VGANTT_OPENAI_API_KEY eklenmeli.",
            Sql: string.Empty,
            Explanation: "OpenAI API key is missing.",
            Confidence: 0
        );
    }

    var model = Environment.GetEnvironmentVariable("VGANTT_AI_MODEL") ?? "gpt-5-mini";
    var endpoint = Environment.GetEnvironmentVariable("VGANTT_OPENAI_RESPONSES_URL") ?? "https://api.openai.com/v1/responses";
    var requestBody = new Dictionary<string, object?>
    {
        ["model"] = model,
        ["instructions"] = BuildAiSqlSystemPrompt(schema.Connection.Provider),
        ["input"] = BuildAiSqlUserPrompt(schema, question, history),
        ["max_output_tokens"] = 1600,
        ["text"] = new Dictionary<string, object?>
        {
            ["format"] = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["name"] = "vgantt_sql_plan",
                ["strict"] = true,
                ["schema"] = BuildAiSqlPlanJsonSchema()
            }
        }
    };

    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(35)
    };
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    request.Content = new StringContent(JsonSerializer.Serialize(requestBody, AppJson.Options), Encoding.UTF8, "application/json");

    using var response = await httpClient.SendAsync(request);
    var responseBody = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"AI model error: {(int)response.StatusCode} {responseBody}");
    }

    using var document = JsonDocument.Parse(responseBody);
    var outputText = ExtractOpenAiOutputText(document.RootElement);
    if (string.IsNullOrWhiteSpace(outputText))
    {
        throw new InvalidOperationException("AI model did not return SQL plan text.");
    }

    var plan = JsonSerializer.Deserialize<AiSqlPlan>(outputText, AppJson.Options)
        ?? throw new InvalidOperationException("AI SQL plan could not be parsed.");
    return plan;
}

static string BuildAiSqlSystemPrompt(string provider)
{
    return $"""
        You are the SQL generation layer for Vgantt ERP AI.
        You must generate exactly one read-only SELECT query for the given database provider: {provider}.
        Return only JSON that matches the provided schema.

        Rules:
        - SQL must be generated by you from the provided schema and user question.
        - You are responsible for choosing the required joins and aggregate expressions.
        - Use only tables/views and columns listed in the schema.
        - Prefer the relationship map for joins. If no mapped relation exists, you may infer a join only when both columns are listed and the relationship is obvious from names/meanings, such as parent.id = child.parent_id or order.id = order_line.order_id.
        - Never invent table names, column names, unsafe functions, or unclear joins.
        - Generate only SELECT. No INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE, MERGE, CALL, EXECUTE, GRANT, REVOKE.
        - Do not use SELECT *.
        - Do not include semicolons or SQL comments.
        - If the question is ambiguous, set needsClarification=true and ask one short Turkish clarification question.
        - Use the conversation context for follow-up questions. Turkish references such as "yukaridaki", "az onceki", "bu", "bunun", "ayni", "bu sefer", "ona gore" usually refer to the previous user question, previous SQL, selected tables, joins, filters, date range, or metric.
        - For follow-up questions, preserve previous table choices, joins, filters, and date ranges unless the new question clearly changes them.
        - If a follow-up cannot be resolved from the conversation context, ask a clarification question instead of guessing.
        - If a date is mentioned as today/bugun, use the provided current date.
        - If user asks "kac/adet/sayisi/count", use count(*) unless a distinct business metric is clearly required.
        - If user asks "toplam/tutar/ciro/ne kadar/sum", use SUM over the best numeric amount column.
        - If user asks "ortalama/average/avg", use AVG over the best numeric measure column.
        - If user asks "maksimum/en yuksek/max", use MAX over the best matching measure column, or GROUP BY + ORDER BY desc + limit when asking for the entity with the highest value.
        - If user asks "minimum/en dusuk/min", use MIN over the best matching measure column, or GROUP BY + ORDER BY asc + limit when asking for the entity with the lowest value.
        - If user asks "en cok/en az/top N", create GROUP BY, aggregate, ORDER BY, and limit clauses as needed.
        - If a requested metric lives on a line/detail table, join the header/detail tables using the relationship map or a clear inferred key.
        - Use Turkish, business-friendly aliases for aggregate columns, such as "toplam_tutar", "ortalama_tutar", "maksimum_tutar", "siparis_adedi".
        - For PostgreSQL quote identifiers with double quotes and use date 'YYYY-MM-DD'.
        - For Oracle quote identifiers with double quotes and use date 'YYYY-MM-DD'.
        - For SQL Server quote identifiers with brackets and use cast('YYYY-MM-DD' as date).
        - For MySQL quote identifiers with backticks and use date literals as 'YYYY-MM-DD'.
        - For non-aggregate list queries, limit to at most 100 rows using the provider's syntax.
        - explanation must be Turkish and concise.
        """;
}

static string BuildConversationContextText(AssistantHistoryItem[] history)
{
    if (history.Length == 0)
    {
        return string.Empty;
    }

    var builder = new StringBuilder();
    foreach (var item in history.TakeLast(12))
    {
        var role = string.IsNullOrWhiteSpace(item.Role) ? "unknown" : item.Role.Trim().ToLowerInvariant();
        var text = TruncateForAiPrompt(item.Text, 900);
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine($"- {role}: {text}");
        }

        if (!string.IsNullOrWhiteSpace(item.Sql))
        {
            builder.AppendLine($"  sql: {TruncateForAiPrompt(item.Sql, 1200)}");
        }

        if (item.Columns is { Length: > 0 })
        {
            builder.AppendLine($"  columns: {string.Join(", ", item.Columns.Take(30))}");
        }

        if (item.Rows is { Length: > 0 })
        {
            var rowTexts = item.Rows
                .Take(3)
                .Select(row => $"[{string.Join(", ", row.Take(12).Select(value => TruncateForAiPrompt(value, 80)))}]");
            builder.AppendLine($"  sample_rows: {string.Join("; ", rowTexts)}");
        }
    }

    return builder.ToString().Trim();
}

static string TruncateForAiPrompt(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
    return normalized.Length <= maxLength
        ? normalized
        : normalized[..maxLength] + "...";
}

static string BuildAiSqlUserPrompt(
    AssistantSchema schema,
    string question,
    AssistantHistoryItem[] history
)
{
    var today = GetConfiguredToday();
    var conversationText = BuildConversationContextText(history);
    var searchQuestion = string.IsNullOrWhiteSpace(conversationText)
        ? question
        : $"{conversationText}\n{question}";
    var selectedObjects = SelectObjectsForAiPrompt(schema, searchQuestion).ToList();
    var selectedObjectIds = selectedObjects.Select(queryObject => queryObject.ObjectId).ToHashSet();
    var connectedRelations = schema.Relations
        .Where(relation => selectedObjectIds.Contains(relation.SourceObjectId) || selectedObjectIds.Contains(relation.TargetObjectId))
        .Take(120)
        .ToArray();

    foreach (var relation in connectedRelations)
    {
        AddRelatedObjectForAiPrompt(schema, selectedObjects, selectedObjectIds, relation.SourceObjectId);
        AddRelatedObjectForAiPrompt(schema, selectedObjects, selectedObjectIds, relation.TargetObjectId);
    }

    var objects = selectedObjects
        .OrderBy(queryObject => queryObject.BusinessDomain)
        .ThenBy(queryObject => queryObject.ObjectName)
        .ToArray();
    var includedObjectIds = objects.Select(queryObject => queryObject.ObjectId).ToHashSet();
    var relations = schema.Relations
        .Where(relation => includedObjectIds.Contains(relation.SourceObjectId) && includedObjectIds.Contains(relation.TargetObjectId))
        .Take(120)
        .ToArray();

    var prompt = new StringBuilder();
    prompt.AppendLine($"Current date: {today:yyyy-MM-dd}");
    prompt.AppendLine($"Database provider: {schema.Connection.Provider}");
    if (!string.IsNullOrWhiteSpace(conversationText))
    {
        prompt.AppendLine();
        prompt.AppendLine("Conversation context:");
        prompt.AppendLine(conversationText);
    }

    prompt.AppendLine($"User question: {question}");
    prompt.AppendLine();
    prompt.AppendLine("Schema objects:");

    foreach (var queryObject in objects)
    {
        prompt.AppendLine($"- object: {queryObject.ObjectName}");
        prompt.AppendLine($"  display: {queryObject.DisplayName}");
        prompt.AppendLine($"  type: {queryObject.ObjectType}");
        prompt.AppendLine($"  domain: {queryObject.BusinessDomain}");
        if (!string.IsNullOrWhiteSpace(queryObject.Description))
        {
            prompt.AppendLine($"  description: {queryObject.Description}");
        }

        prompt.AppendLine("  columns:");
        foreach (var column in queryObject.Columns.Take(80))
        {
            var flags = new List<string>();
            if (column.IsFilterable)
            {
                flags.Add("filterable");
            }

            if (column.IsGroupable)
            {
                flags.Add("groupable");
            }

            if (column.IsSummable)
            {
                flags.Add("summable");
            }

            prompt.Append("    - ");
            prompt.Append(column.ColumnName);
            prompt.Append(" | type=");
            prompt.Append(column.DataType);
            prompt.Append(" | display=");
            prompt.Append(column.DisplayName);
            if (!string.IsNullOrWhiteSpace(column.SemanticMeaning))
            {
                prompt.Append(" | meaning=");
                prompt.Append(column.SemanticMeaning);
            }

            if (flags.Count > 0)
            {
                prompt.Append(" | ");
                prompt.Append(string.Join(",", flags));
            }

            prompt.AppendLine();
        }
    }

    prompt.AppendLine();
    prompt.AppendLine("Relationship map:");
    foreach (var relation in relations)
    {
        foreach (var column in relation.Columns)
        {
            prompt.AppendLine($"- {relation.SourceObjectName}.{column.SourceColumnName} = {relation.TargetObjectName}.{column.TargetColumnName} ({relation.RelationName})");
        }
    }

    return prompt.ToString();
}

static void AddRelatedObjectForAiPrompt(
    AssistantSchema schema,
    List<QueryableObject> objects,
    HashSet<Guid> objectIds,
    Guid objectId
)
{
    if (objectIds.Contains(objectId) || objects.Count >= 80)
    {
        return;
    }

    var queryObject = schema.Objects.FirstOrDefault(candidate => candidate.ObjectId == objectId);
    if (queryObject is null)
    {
        return;
    }

    objects.Add(queryObject);
    objectIds.Add(objectId);
}

static QueryableObject[] SelectObjectsForAiPrompt(AssistantSchema schema, string question)
{
    if (schema.Objects.Length <= 60)
    {
        return schema.Objects;
    }

    var normalizedQuestion = NormalizeSearchText(question);
    return schema.Objects
        .Select(queryObject => new
        {
            Object = queryObject,
            Score = ScoreSearchMatch(normalizedQuestion, BuildObjectSearchText(queryObject)) +
                queryObject.Columns.Sum(column => Math.Min(4, ScoreSearchMatch(normalizedQuestion, BuildColumnSearchText(column))))
        })
        .OrderByDescending(match => match.Score)
        .ThenBy(match => match.Object.ObjectName)
        .Take(60)
        .Select(match => match.Object)
        .ToArray();
}

static Dictionary<string, object?> BuildAiSqlPlanJsonSchema()
{
    return new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new Dictionary<string, object?>
        {
            ["needsClarification"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["clarificationQuestion"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["sql"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["explanation"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["confidence"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["minimum"] = 0,
                ["maximum"] = 1
            }
        },
        ["required"] = new[] { "needsClarification", "clarificationQuestion", "sql", "explanation", "confidence" }
    };
}

static string? ExtractOpenAiOutputText(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (element.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
            element.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString();
        }

        foreach (var property in element.EnumerateObject())
        {
            var found = ExtractOpenAiOutputText(property.Value);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }
    }

    if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            var found = ExtractOpenAiOutputText(item);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }
    }

    return null;
}

static void ValidateAiGeneratedSql(AssistantSchema schema, string sql)
{
    if (!IsSafeSelectSql(sql))
    {
        throw new InvalidOperationException("AI only may generate a safe SELECT query.");
    }

    var referencedObjects = ExtractReferencedSqlObjects(sql);
    if (referencedObjects.Length == 0)
    {
        throw new InvalidOperationException("AI generated SQL does not reference a known object.");
    }

    var knownObjects = BuildKnownObjectNameSet(schema.Objects);
    var unknownObjects = referencedObjects
        .Where(name => !knownObjects.Contains(NormalizeSqlObjectName(name)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (unknownObjects.Length > 0)
    {
        throw new InvalidOperationException($"AI generated SQL references unknown object(s): {string.Join(", ", unknownObjects)}");
    }
}

static string[] ExtractReferencedSqlObjects(string sql)
{
    var matches = Regex.Matches(
        sql,
        @"\b(?:from|join)\s+(?<object>(?:""[^""]+""|\[[^\]]+\]|`[^`]+`|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:""[^""]+""|\[[^\]]+\]|`[^`]+`|[A-Za-z_][A-Za-z0-9_]*))?)",
        RegexOptions.IgnoreCase
    );

    return matches
        .Select(match => match.Groups["object"].Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();
}

static HashSet<string> BuildKnownObjectNameSet(QueryableObject[] objects)
{
    var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var queryObject in objects)
    {
        var normalized = NormalizeSqlObjectName(queryObject.ObjectName);
        known.Add(normalized);

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            known.Add(parts[^1]);
        }
    }

    return known;
}

static string NormalizeSqlObjectName(string value)
{
    return Regex.Replace(value, @"[""`\[\]\s]", string.Empty).Trim().ToLowerInvariant();
}

static string BuildAiSqlSummary(AiSqlPlan plan, AssistantQueryResult result)
{
    if (result.Rows.Length == 0)
    {
        return string.IsNullOrWhiteSpace(plan.Explanation)
            ? "Sorgu calisti, sonuc bulunamadi."
            : $"{plan.Explanation} Sonuc bulunamadi.";
    }

    if (result.Columns.Length == 1 && result.Rows[0].Length == 1)
    {
        return string.IsNullOrWhiteSpace(plan.Explanation)
            ? $"Sonuc: {result.Rows[0][0]}."
            : $"{plan.Explanation} Sonuc: {result.Rows[0][0]}.";
    }

    return string.IsNullOrWhiteSpace(plan.Explanation)
        ? $"{result.Rows.Length} satir sonuc geldi."
        : $"{plan.Explanation} {result.Rows.Length} satir sonuc geldi.";
}

static async Task<AssistantSchema> LoadAssistantSchemaAsync(Guid tenantId)
{
    var tenantConnection = await LoadActiveTenantDbConnectionAsync(tenantId)
        ?? throw new InvalidOperationException("Active tenant database connection is not configured.");

    var objectColumns = new Dictionary<Guid, List<QueryableColumn>>();
    var objectMap = new Dictionary<Guid, QueryableObjectBuilder>();
    await using var connection = await OpenRegistryConnectionAsync();
    await using (var objectCommand = new NpgsqlCommand("""
        select
            id,
            object_name,
            object_type,
            business_domain,
            coalesce(display_name_tr, object_name),
            coalesce(description_tr, description, '')
        from tenant_erp_objects
        where tenant_id = @tenant_id
          and is_active = true
          and is_queryable = true
        order by business_domain, object_name
        """, connection))
    {
        objectCommand.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await objectCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var objectId = reader.GetGuid(0);
            objectMap[objectId] = new QueryableObjectBuilder(
                ObjectId: objectId,
                ObjectName: reader.GetString(1),
                ObjectType: reader.GetString(2),
                BusinessDomain: reader.GetString(3),
                DisplayName: reader.GetString(4),
                Description: reader.GetString(5)
            );
            objectColumns[objectId] = [];
        }
    }

    await using (var columnCommand = new NpgsqlCommand("""
        select
            o.id,
            c.column_name,
            coalesce(c.display_name_tr, m.customer_label, c.business_name, c.column_name),
            coalesce(c.semantic_meaning_tr, m.customer_description, c.description, ''),
            coalesce(c.data_type, ''),
            c.is_filterable,
            c.is_groupable,
            c.is_summable
        from tenant_erp_object_columns c
        join tenant_erp_objects o on o.id = c.object_id
        left join tenant_erp_column_meanings m
            on m.column_id = c.id
           and m.language_code = 'tr'
        where o.tenant_id = @tenant_id
          and o.is_active = true
          and o.is_queryable = true
          and c.is_active = true
        order by o.object_name, c.column_name
        """, connection))
    {
        columnCommand.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await columnCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var objectId = reader.GetGuid(0);
            if (!objectColumns.TryGetValue(objectId, out var columns))
            {
                continue;
            }

            columns.Add(new QueryableColumn(
                ColumnName: reader.GetString(1),
                DisplayName: reader.GetString(2),
                SemanticMeaning: reader.GetString(3),
                DataType: reader.GetString(4),
                IsFilterable: reader.GetBoolean(5),
                IsGroupable: reader.GetBoolean(6),
                IsSummable: reader.GetBoolean(7)
            ));
        }
    }

    var relationMap = new Dictionary<Guid, QueryableRelationBuilder>();
    await using (var relationCommand = new NpgsqlCommand("""
        select
            r.id,
            r.relation_name,
            r.source_view_id,
            source_view.object_name,
            r.target_view_id,
            target_view.object_name,
            r.join_type,
            coalesce(r.description_tr, ''),
            rc.source_column_name,
            rc.target_column_name,
            coalesce(rc.ordinal, 1)
        from tenant_erp_relations r
        join tenant_erp_objects source_view on source_view.id = r.source_view_id
        join tenant_erp_objects target_view on target_view.id = r.target_view_id
        left join tenant_erp_relation_columns rc on rc.relation_id = r.id
        where r.tenant_id = @tenant_id
          and r.is_active = true
        order by r.relation_name, rc.ordinal
        """, connection))
    {
        relationCommand.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await relationCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var relationId = reader.GetGuid(0);
            if (!relationMap.TryGetValue(relationId, out var relation))
            {
                relation = new QueryableRelationBuilder(
                    RelationId: relationId,
                    RelationName: reader.GetString(1),
                    SourceObjectId: reader.GetGuid(2),
                    SourceObjectName: reader.GetString(3),
                    TargetObjectId: reader.GetGuid(4),
                    TargetObjectName: reader.GetString(5),
                    JoinType: reader.GetString(6),
                    Description: reader.GetString(7)
                );
                relationMap[relationId] = relation;
            }

            if (!reader.IsDBNull(8) && !reader.IsDBNull(9))
            {
                relation.Columns.Add(new QueryableRelationColumn(
                    SourceColumnName: reader.GetString(8),
                    TargetColumnName: reader.GetString(9),
                    Ordinal: reader.GetInt32(10)
                ));
            }
        }
    }

    var objects = objectMap.Values
        .Select(builder => builder.ToObject(objectColumns.GetValueOrDefault(builder.ObjectId, [])))
        .Where(queryObject => queryObject.Columns.Length > 0)
        .ToArray();
    var relations = relationMap.Values
        .Select(relation => relation.ToRelation())
        .Where(relation => relation.Columns.Length > 0)
        .ToArray();

    if (objects.Length == 0 && string.Equals(tenantConnection.Provider, "postgresql", StringComparison.OrdinalIgnoreCase))
    {
        return await LoadPostgresAssistantSchemaFromTenantDbAsync(tenantConnection);
    }

    if (relations.Length == 0 && string.Equals(tenantConnection.Provider, "postgresql", StringComparison.OrdinalIgnoreCase))
    {
        relations = await LoadPostgresRelationsFromTenantDbAsync(tenantConnection, objects);
    }

    return new AssistantSchema(
        Connection: tenantConnection,
        Objects: objects,
        Relations: relations
    );
}

static async Task<AssistantSchema> LoadPostgresAssistantSchemaFromTenantDbAsync(TenantDbConnectionSettings tenantConnection)
{
    var builders = new Dictionary<string, QueryableObjectBuilder>(StringComparer.OrdinalIgnoreCase);
    var columns = new Dictionary<Guid, List<QueryableColumn>>();
    await using var connection = new NpgsqlConnection(BuildTenantConnectionString(tenantConnection));
    await connection.OpenAsync();

    await using (var objectCommand = new NpgsqlCommand("""
        select table_schema, table_name, table_type
        from information_schema.tables
        where table_schema not in ('pg_catalog', 'information_schema')
          and table_type in ('BASE TABLE', 'VIEW')
        order by table_schema, table_name
        limit 200
        """, connection))
    {
        await using var reader = await objectCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var objectName = BuildPostgresObjectName(schemaName, tableName);
            var objectId = Guid.NewGuid();
            builders[$"{schemaName}.{tableName}"] = new QueryableObjectBuilder(
                ObjectId: objectId,
                ObjectName: objectName,
                ObjectType: string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table",
                BusinessDomain: "discovered",
                DisplayName: tableName,
                Description: "Discovered from PostgreSQL information_schema."
            );
            columns[objectId] = [];
        }
    }

    await using (var columnCommand = new NpgsqlCommand("""
        select table_schema, table_name, column_name, data_type
        from information_schema.columns
        where table_schema not in ('pg_catalog', 'information_schema')
        order by table_schema, table_name, ordinal_position
        """, connection))
    {
        await using var reader = await columnCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!builders.TryGetValue(key, out var builder))
            {
                continue;
            }

            columns[builder.ObjectId].Add(new QueryableColumn(
                ColumnName: reader.GetString(2),
                DisplayName: reader.GetString(2),
                SemanticMeaning: string.Empty,
                DataType: reader.GetString(3),
                IsFilterable: true,
                IsGroupable: true,
                IsSummable: false
            ));
        }
    }

    var objects = builders.Values
        .Select(builder => builder.ToObject(columns.GetValueOrDefault(builder.ObjectId, [])))
        .Where(queryObject => queryObject.Columns.Length > 0)
        .ToArray();
    var relations = await LoadPostgresRelationsFromTenantDbAsync(tenantConnection, objects);

    return new AssistantSchema(tenantConnection, objects, relations);
}

static async Task<QueryableRelation[]> LoadPostgresRelationsFromTenantDbAsync(
    TenantDbConnectionSettings tenantConnection,
    QueryableObject[] objects
)
{
    if (objects.Length == 0)
    {
        return [];
    }

    await using var connection = new NpgsqlConnection(BuildTenantConnectionString(tenantConnection));
    await connection.OpenAsync();
    var relationMap = new Dictionary<string, QueryableRelationBuilder>(StringComparer.OrdinalIgnoreCase);
    await using var command = new NpgsqlCommand("""
        select
            tc.constraint_name,
            child_kcu.table_schema,
            child_kcu.table_name,
            child_kcu.column_name,
            parent_ccu.table_schema,
            parent_ccu.table_name,
            parent_ccu.column_name
        from information_schema.table_constraints tc
        join information_schema.key_column_usage child_kcu
          on child_kcu.constraint_catalog = tc.constraint_catalog
         and child_kcu.constraint_schema = tc.constraint_schema
         and child_kcu.constraint_name = tc.constraint_name
        join information_schema.constraint_column_usage parent_ccu
          on parent_ccu.constraint_catalog = tc.constraint_catalog
         and parent_ccu.constraint_schema = tc.constraint_schema
         and parent_ccu.constraint_name = tc.constraint_name
        where tc.constraint_type = 'FOREIGN KEY'
          and child_kcu.table_schema not in ('pg_catalog', 'information_schema')
        order by tc.constraint_name, child_kcu.ordinal_position
        """, connection);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var relationName = reader.GetString(0);
        var childObject = FindObjectByPhysicalName(objects, reader.GetString(1), reader.GetString(2));
        var parentObject = FindObjectByPhysicalName(objects, reader.GetString(4), reader.GetString(5));
        if (childObject is null || parentObject is null)
        {
            continue;
        }

        var key = $"{parentObject.ObjectId}:{childObject.ObjectId}:{relationName}";
        if (!relationMap.TryGetValue(key, out var relation))
        {
            relation = new QueryableRelationBuilder(
                RelationId: Guid.NewGuid(),
                RelationName: relationName,
                SourceObjectId: parentObject.ObjectId,
                SourceObjectName: parentObject.ObjectName,
                TargetObjectId: childObject.ObjectId,
                TargetObjectName: childObject.ObjectName,
                JoinType: "INNER JOIN",
                Description: "Discovered from PostgreSQL foreign key."
            );
            relationMap[key] = relation;
        }

        relation.Columns.Add(new QueryableRelationColumn(
            SourceColumnName: reader.GetString(6),
            TargetColumnName: reader.GetString(3),
            Ordinal: relation.Columns.Count + 1
        ));
    }

    return relationMap.Values
        .Select(relation => relation.ToRelation())
        .ToArray();
}

static QueryableObject? FindObjectByPhysicalName(QueryableObject[] objects, string schemaName, string tableName)
{
    var fullName = BuildPostgresObjectName(schemaName, tableName);
    return objects.FirstOrDefault(queryObject =>
        string.Equals(queryObject.ObjectName, fullName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(queryObject.ObjectName, tableName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(queryObject.ObjectName, $"{schemaName}.{tableName}", StringComparison.OrdinalIgnoreCase)
    );
}

static string BuildPostgresObjectName(string schemaName, string tableName)
{
    return string.Equals(schemaName, "public", StringComparison.OrdinalIgnoreCase)
        ? tableName
        : $"{schemaName}.{tableName}";
}

#pragma warning disable CS8321
static AssistantPlanResult BuildAssistantQueryPlan(AssistantSchema schema, string question)
{
    var normalizedQuestion = NormalizeSearchText(question);
    var metricKind = DetectAssistantMetricKind(normalizedQuestion);
    var baseObject = SelectBestQueryableObject(schema.Objects, normalizedQuestion);
    if (baseObject is null)
    {
        return AssistantPlanResult.NeedsClarification("Hangi tablo/view uzerinden sorgulayayim? Admin ekraninda musteri tarafindaki tablo adlarini tanimlayabiliriz.");
    }

    var dateRange = DetectDateRange(question);
    var dateSelection = dateRange is null
        ? null
        : SelectBestDateColumn(schema, baseObject, normalizedQuestion);

    if (dateRange is not null && dateSelection is null)
    {
        return AssistantPlanResult.NeedsClarification(
            $"Tarih filtresi icin {baseObject.DisplayName} tarafinda hangi kolon kullanilsin? Ornek musteri kolon adi: Olusturma Tarihi."
        );
    }

    if (dateRange is null && QuestionImpliesMissingDate(normalizedQuestion))
    {
        var candidate = SelectBestDateColumn(schema, baseObject, normalizedQuestion);
        var label = candidate is null
            ? "tarih kolonu"
            : $"{BuildColumnUserLabel(candidate.Column)} ({candidate.Object.DisplayName})";
        return AssistantPlanResult.NeedsClarification($"Hangi tarih araligini kullanayim? Filtre kolonu olarak {label} gorunuyor.");
    }

    var metricColumn = metricKind == AssistantMetricKind.Sum
        ? SelectBestAmountColumn(schema, baseObject, normalizedQuestion)
        : null;
    if (metricKind == AssistantMetricKind.Sum && metricColumn is null)
    {
        return AssistantPlanResult.NeedsClarification(
            $"{baseObject.DisplayName} icin hangi tutar kolonunu toplayayim? Admin ekraninda kolonun musteri tarafindaki adini 'Tutar' veya 'Net Tutar' gibi tanimlayabiliriz."
        );
    }

    if (metricColumn is not null &&
        dateSelection is not null &&
        metricColumn.Object.ObjectId != baseObject.ObjectId &&
        dateSelection.Object.ObjectId != baseObject.ObjectId &&
        metricColumn.Object.ObjectId != dateSelection.Object.ObjectId)
    {
        return AssistantPlanResult.NeedsClarification("Bu soru icin birden fazla iliski gerekiyor. Hangi tablo iliskisini kullanmam gerektigini netlestirebilir misiniz?");
    }

    var dialect = SqlDialect.ForProvider(schema.Connection.Provider);
    var sql = BuildAssistantSql(dialect, baseObject, metricKind, metricColumn, dateSelection, dateRange);
    return AssistantPlanResult.Ready(new AssistantQueryPlan(
        Sql: sql,
        MetricKind: metricKind,
        BaseObject: baseObject,
        MetricColumn: metricColumn,
        DateSelection: dateSelection,
        DateRange: dateRange
    ));
}

static async Task<AssistantQueryResult> ExecuteTenantPostgresQueryAsync(TenantDbConnectionSettings settings, string sql)
{
    if (!IsSafeSelectSql(sql))
    {
        throw new InvalidOperationException("Only SELECT queries can be executed by the assistant.");
    }

    await using var connection = new NpgsqlConnection(BuildTenantConnectionString(settings));
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection)
    {
        CommandTimeout = 30
    };

    await using var reader = await command.ExecuteReaderAsync();
    var columns = Enumerable.Range(0, reader.FieldCount)
        .Select(reader.GetName)
        .ToArray();
    var rows = new List<string[]>();

    while (await reader.ReadAsync() && rows.Count < 100)
    {
        var row = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index += 1)
        {
            row[index] = FormatAssistantValue(reader.IsDBNull(index) ? null : reader.GetValue(index));
        }

        rows.Add(row);
    }

    return new AssistantQueryResult(columns, rows.ToArray());
}

static async Task<AssistantQueryResult> ExecuteTenantOracleQueryAsync(TenantDbConnectionSettings settings, string sql)
{
    if (!IsSafeSelectSql(sql))
    {
        throw new InvalidOperationException("Only SELECT queries can be executed by the assistant.");
    }

    await using var connection = new OracleConnection(BuildTenantOracleConnectionString(settings));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;

    await using var reader = await command.ExecuteReaderAsync();
    var columns = Enumerable.Range(0, reader.FieldCount)
        .Select(reader.GetName)
        .ToArray();
    var rows = new List<string[]>();

    while (await reader.ReadAsync() && rows.Count < 100)
    {
        var row = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index += 1)
        {
            row[index] = FormatAssistantValue(await reader.IsDBNullAsync(index) ? null : reader.GetValue(index));
        }

        rows.Add(row);
    }

    return new AssistantQueryResult(columns, rows.ToArray());
}

static string BuildTenantOracleConnectionString(TenantDbConnectionSettings settings)
{
    var dataSource = settings.DatabaseName.Trim();
    if (!dataSource.StartsWith("(", StringComparison.Ordinal))
    {
        dataSource = dataSource.Contains(':', StringComparison.Ordinal) || dataSource.Contains('/', StringComparison.Ordinal)
            ? dataSource
            : $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={settings.Host})(PORT={settings.Port}))(CONNECT_DATA=(SERVICE_NAME={dataSource})))";
    }

    var builder = new OracleConnectionStringBuilder
    {
        UserID = settings.Username,
        Password = settings.Password,
        DataSource = dataSource,
        ConnectionTimeout = 5
    };

    return builder.ConnectionString;
}

static bool IsSafeSelectSql(string sql)
{
    var trimmed = sql.Trim();
    return (trimmed.StartsWith("select ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("with ", StringComparison.OrdinalIgnoreCase)) &&
        !trimmed.Contains(';') &&
        !trimmed.Contains("--", StringComparison.Ordinal) &&
        !trimmed.Contains("/*", StringComparison.Ordinal) &&
        !Regex.IsMatch(trimmed, @"\b(insert|update|delete|drop|alter|truncate|merge|call|execute|grant|revoke)\b", RegexOptions.IgnoreCase);
}

static string BuildAssistantSql(
    SqlDialect dialect,
    QueryableObject baseObject,
    AssistantMetricKind metricKind,
    MetricColumnSelection? metricColumn,
    DateColumnSelection? dateSelection,
    DateRange? dateRange
)
{
    const string baseAlias = "t0";
    const string relatedAlias = "t1";
    var relatedObject = ResolveRelatedObjectForQuery(baseObject, metricColumn, dateSelection);
    var selectAlias = metricColumn is not null && relatedObject is not null && metricColumn.Object.ObjectId == relatedObject.ObjectId
        ? relatedAlias
        : baseAlias;
    var selectClause = BuildAssistantSelectClause(dialect, baseObject, selectAlias, metricKind, metricColumn);
    var sql = new StringBuilder();
    sql.Append("select ");
    sql.Append(selectClause);
    sql.AppendLine();
    sql.Append("from ");
    sql.Append(dialect.QuoteObjectName(baseObject.ObjectName));
    sql.Append(' ');
    sql.Append(dialect.TableAliasSeparator);
    sql.Append(baseAlias);

    if (relatedObject is not null)
    {
        var relation = metricColumn is not null && metricColumn.Object.ObjectId == relatedObject.ObjectId
            ? metricColumn.Relation
            : dateSelection?.Relation;
        if (relation is null)
        {
            throw new InvalidOperationException("Related object does not have a relation.");
        }

        sql.AppendLine();
        sql.Append(BuildAssistantJoinClause(dialect, baseObject, baseAlias, relatedObject, relation, relatedAlias));
    }

    if (dateSelection is not null && dateRange is not null)
    {
        var dateAlias = relatedObject is not null && dateSelection.Object.ObjectId == relatedObject.ObjectId
            ? relatedAlias
            : baseAlias;
        sql.AppendLine();
        sql.Append("where ");
        sql.Append(dialect.QuoteQualified(dateAlias, dateSelection.Column.ColumnName));
        sql.Append(" >= ");
        sql.Append(dialect.DateLiteral(dateRange.Start));
        sql.Append(" and ");
        sql.Append(dialect.QuoteQualified(dateAlias, dateSelection.Column.ColumnName));
        sql.Append(" < ");
        sql.Append(dialect.DateLiteral(dateRange.EndExclusive));
    }

    if (metricKind == AssistantMetricKind.List)
    {
        sql.AppendLine();
        sql.Append(dialect.LimitClause(20));
    }

    return sql.ToString();
}

static string BuildAssistantSelectClause(
    SqlDialect dialect,
    QueryableObject baseObject,
    string selectAlias,
    AssistantMetricKind metricKind,
    MetricColumnSelection? metricColumn
)
{
    if (metricKind == AssistantMetricKind.Count)
    {
        return $"count(*) as {dialect.QuoteIdentifier(BuildCountAlias(baseObject))}";
    }

    if (metricKind == AssistantMetricKind.Sum && metricColumn is not null)
    {
        return $"coalesce(sum({dialect.QuoteQualified(selectAlias, metricColumn.Column.ColumnName)}), 0) as {dialect.QuoteIdentifier(BuildSumAlias(metricColumn.Column))}";
    }

    var selectedColumns = baseObject.Columns
        .Where(column => !LooksSensitive(column))
        .Take(8)
        .ToArray();

    if (selectedColumns.Length == 0)
    {
        selectedColumns = baseObject.Columns.Take(8).ToArray();
    }

    return string.Join(", ", selectedColumns.Select(column =>
        $"{dialect.QuoteQualified(selectAlias, column.ColumnName)} as {dialect.QuoteIdentifier(BuildColumnUserLabel(column))}"
    ));
}

static QueryableObject? ResolveRelatedObjectForQuery(
    QueryableObject baseObject,
    MetricColumnSelection? metricColumn,
    DateColumnSelection? dateSelection
)
{
    if (metricColumn is not null && metricColumn.Object.ObjectId != baseObject.ObjectId)
    {
        return metricColumn.Object;
    }

    if (dateSelection is not null && dateSelection.Object.ObjectId != baseObject.ObjectId)
    {
        return dateSelection.Object;
    }

    return null;
}

static string BuildAssistantJoinClause(
    SqlDialect dialect,
    QueryableObject baseObject,
    string baseAlias,
    QueryableObject relatedObject,
    QueryableRelation relation,
    string relatedAlias
)
{
    var conditions = relation.Columns.Select(column =>
    {
        if (relation.SourceObjectId == baseObject.ObjectId && relation.TargetObjectId == relatedObject.ObjectId)
        {
            return $"{dialect.QuoteQualified(baseAlias, column.SourceColumnName)} = {dialect.QuoteQualified(relatedAlias, column.TargetColumnName)}";
        }

        return $"{dialect.QuoteQualified(relatedAlias, column.SourceColumnName)} = {dialect.QuoteQualified(baseAlias, column.TargetColumnName)}";
    });

    return $"inner join {dialect.QuoteObjectName(relatedObject.ObjectName)} {dialect.TableAliasSeparator}{relatedAlias} on {string.Join(" and ", conditions)}";
}

static AssistantMetricKind DetectAssistantMetricKind(string normalizedQuestion)
{
    if (ContainsAny(normalizedQuestion, "kac", "adet", "sayisi", "sayi", "count"))
    {
        return AssistantMetricKind.Count;
    }

    if (ContainsAny(normalizedQuestion, "tutar", "toplam", "ciro", "bedel", "amount", "total", "ne kadar"))
    {
        return AssistantMetricKind.Sum;
    }

    return AssistantMetricKind.List;
}

static QueryableObject? SelectBestQueryableObject(QueryableObject[] objects, string normalizedQuestion)
{
    var wantsOrder = ContainsAny(normalizedQuestion, "siparis", "order");
    var wantsLine = ContainsAny(normalizedQuestion, "satir", "kalem", "line", "urun");
    var wantsSales = ContainsAny(normalizedQuestion, "satis", "sales");

    return objects
        .Select(queryObject =>
        {
            var searchable = BuildObjectSearchText(queryObject);
            var score = ScoreSearchMatch(normalizedQuestion, searchable);
            if (wantsOrder && ContainsAny(searchable, "siparis", "order"))
            {
                score += 12;
            }

            if (wantsSales && ContainsAny(searchable, "satis", "sales", "order"))
            {
                score += 4;
            }

            if (wantsLine && ContainsAny(searchable, "line", "satir", "kalem", "detail", "detay"))
            {
                score += 10;
            }

            if (!wantsLine && ContainsAny(searchable, "line", "satir", "kalem", "detail", "detay"))
            {
                score -= 4;
            }

            return new { Object = queryObject, Score = score };
        })
        .Where(match => match.Score > 0)
        .OrderByDescending(match => match.Score)
        .Select(match => match.Object)
        .FirstOrDefault();
}

static DateColumnSelection? SelectBestDateColumn(AssistantSchema schema, QueryableObject baseObject, string normalizedQuestion)
{
    var candidates = new List<DateColumnSelection>();
    candidates.AddRange(baseObject.Columns
        .Where(IsDateLikeColumn)
        .Select(column => new DateColumnSelection(baseObject, column, null)));

    foreach (var relation in schema.Relations.Where(relation =>
        relation.SourceObjectId == baseObject.ObjectId || relation.TargetObjectId == baseObject.ObjectId))
    {
        var relatedObjectId = relation.SourceObjectId == baseObject.ObjectId
            ? relation.TargetObjectId
            : relation.SourceObjectId;
        var relatedObject = schema.Objects.FirstOrDefault(queryObject => queryObject.ObjectId == relatedObjectId);
        if (relatedObject is null)
        {
            continue;
        }

        candidates.AddRange(relatedObject.Columns
            .Where(IsDateLikeColumn)
            .Select(column => new DateColumnSelection(relatedObject, column, relation)));
    }

    return candidates
        .Select(candidate => new { Candidate = candidate, Score = ScoreDateColumn(candidate.Column, normalizedQuestion) + (candidate.Object.ObjectId == baseObject.ObjectId ? 2 : 0) })
        .Where(match => match.Score > 0)
        .OrderByDescending(match => match.Score)
        .Select(match => match.Candidate)
        .FirstOrDefault();
}

static MetricColumnSelection? SelectBestAmountColumn(AssistantSchema schema, QueryableObject baseObject, string normalizedQuestion)
{
    var candidates = new List<MetricColumnSelection>();
    candidates.AddRange(baseObject.Columns
        .Select(column => new MetricColumnSelection(baseObject, column, null)));

    foreach (var relation in schema.Relations.Where(relation =>
        relation.SourceObjectId == baseObject.ObjectId || relation.TargetObjectId == baseObject.ObjectId))
    {
        var relatedObjectId = relation.SourceObjectId == baseObject.ObjectId
            ? relation.TargetObjectId
            : relation.SourceObjectId;
        var relatedObject = schema.Objects.FirstOrDefault(queryObject => queryObject.ObjectId == relatedObjectId);
        if (relatedObject is null)
        {
            continue;
        }

        candidates.AddRange(relatedObject.Columns
            .Select(column => new MetricColumnSelection(relatedObject, column, relation)));
    }

    return candidates
        .Select(candidate => new { Candidate = candidate, Score = ScoreAmountColumn(candidate.Column, normalizedQuestion) + (candidate.Object.ObjectId == baseObject.ObjectId ? 2 : 0) })
        .Where(match => match.Score > 0)
        .OrderByDescending(match => match.Score)
        .Select(match => match.Candidate)
        .FirstOrDefault();
}

static bool IsDateLikeColumn(QueryableColumn column)
{
    var searchable = BuildColumnSearchText(column);
    return ContainsAny(searchable, "date", "time", "tarih", "created", "create", "olustur", "acilis", "opened") ||
        ContainsAny(NormalizeSearchText(column.DataType), "date", "time", "timestamp");
}

static int ScoreDateColumn(QueryableColumn column, string normalizedQuestion)
{
    var searchable = BuildColumnSearchText(column);
    var score = 0;
    if (ContainsAny(searchable, "date", "time", "tarih"))
    {
        score += 4;
    }

    if (ContainsAny(NormalizeSearchText(column.DataType), "date", "time", "timestamp"))
    {
        score += 3;
    }

    if (ContainsAny(normalizedQuestion, "acilan", "acilmis", "olusturulan", "olusturma", "kayit") &&
        ContainsAny(searchable, "created", "create", "olustur", "acilis", "opened", "kayit"))
    {
        score += 12;
    }

    if (ContainsAny(normalizedQuestion, "siparis tarihi", "order date") &&
        ContainsAny(searchable, "siparis", "order"))
    {
        score += 7;
    }

    score += ScoreSearchMatch(normalizedQuestion, searchable);
    return score;
}

static int ScoreAmountColumn(QueryableColumn column, string normalizedQuestion)
{
    var searchable = BuildColumnSearchText(column);
    var score = column.IsSummable ? 5 : 0;
    if (ContainsAny(searchable, "tutar", "amount", "total", "net", "brut", "gross", "price", "bedel", "ciro"))
    {
        score += 10;
    }

    if (ContainsAny(NormalizeSearchText(column.DataType), "numeric", "decimal", "money", "double", "real", "int"))
    {
        score += 2;
    }

    score += ScoreSearchMatch(normalizedQuestion, searchable);
    return score;
}

static bool QuestionImpliesMissingDate(string normalizedQuestion)
{
    return ContainsAny(normalizedQuestion, "acilan", "acilmis", "olusturulan", "olusturma", "kayit tarihi", "hangi gun", "hangi tarih");
}

static DateRange? DetectDateRange(string question)
{
    var normalizedQuestion = NormalizeSearchText(question);
    var today = GetConfiguredToday();
    if (ContainsAny(normalizedQuestion, "bugun", "today"))
    {
        return new DateRange(today, today.AddDays(1), "bugun");
    }

    if (ContainsAny(normalizedQuestion, "dun", "yesterday"))
    {
        var yesterday = today.AddDays(-1);
        return new DateRange(yesterday, today, "dun");
    }

    if (ContainsAny(normalizedQuestion, "bu ay", "this month"))
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return new DateRange(monthStart, monthStart.AddMonths(1), "bu ay");
    }

    if (ContainsAny(normalizedQuestion, "bu hafta", "this week"))
    {
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysFromMonday);
        return new DateRange(weekStart, weekStart.AddDays(7), "bu hafta");
    }

    var explicitDate = ParseExplicitDate(question);
    return explicitDate is null
        ? null
        : new DateRange(explicitDate.Value, explicitDate.Value.AddDays(1), explicitDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

static DateOnly GetConfiguredToday()
{
    var timeZoneName = Environment.GetEnvironmentVariable("VGANTT_TIME_ZONE") ?? "Europe/Istanbul";
    TimeZoneInfo timeZone;
    try
    {
        timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneName);
    }
    catch (TimeZoneNotFoundException)
    {
        timeZone = TimeZoneInfo.Local;
    }
    catch (InvalidTimeZoneException)
    {
        timeZone = TimeZoneInfo.Local;
    }

    return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime);
}

static DateOnly? ParseExplicitDate(string question)
{
    var match = Regex.Match(question, @"\b(?<year>\d{4})-(?<month>\d{1,2})-(?<day>\d{1,2})\b");
    if (match.Success &&
        int.TryParse(match.Groups["year"].Value, out var year) &&
        int.TryParse(match.Groups["month"].Value, out var month) &&
        int.TryParse(match.Groups["day"].Value, out var day))
    {
        return new DateOnly(year, month, day);
    }

    match = Regex.Match(question, @"\b(?<day>\d{1,2})[./](?<month>\d{1,2})[./](?<year>\d{4})\b");
    if (match.Success &&
        int.TryParse(match.Groups["year"].Value, out year) &&
        int.TryParse(match.Groups["month"].Value, out month) &&
        int.TryParse(match.Groups["day"].Value, out day))
    {
        return new DateOnly(year, month, day);
    }

    return null;
}

static string BuildAssistantSummary(AssistantQueryPlan plan, AssistantQueryResult result)
{
    var objectLabel = string.IsNullOrWhiteSpace(plan.BaseObject.DisplayName)
        ? plan.BaseObject.ObjectName
        : plan.BaseObject.DisplayName;
    var dateLabel = plan.DateRange is null ? string.Empty : $"{plan.DateRange.Label} icin ";
    var firstValue = result.Rows.Length > 0 && result.Rows[0].Length > 0 ? result.Rows[0][0] : "0";

    return plan.MetricKind switch
    {
        AssistantMetricKind.Count => $"{dateLabel}{objectLabel} adedi: {firstValue}.",
        AssistantMetricKind.Sum => $"{dateLabel}{objectLabel} toplami: {firstValue}.",
        _ => $"{objectLabel} icin {result.Rows.Length} satir sonuc geldi."
    };
}
#pragma warning restore CS8321

static string FormatAssistantValue(object? value)
{
    return value switch
    {
        null => string.Empty,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal decimalValue => decimalValue.ToString("0.##", CultureInfo.InvariantCulture),
        double doubleValue => doubleValue.ToString("0.##", CultureInfo.InvariantCulture),
        float floatValue => floatValue.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}

static string BuildObjectSearchText(QueryableObject queryObject)
{
    return NormalizeSearchText($"{queryObject.ObjectName} {queryObject.DisplayName} {queryObject.Description} {queryObject.BusinessDomain}");
}

static string BuildColumnSearchText(QueryableColumn column)
{
    return NormalizeSearchText($"{column.ColumnName} {column.DisplayName} {column.SemanticMeaning} {column.DataType}");
}

static string BuildColumnUserLabel(QueryableColumn column)
{
    if (!string.IsNullOrWhiteSpace(column.DisplayName))
    {
        return column.DisplayName;
    }

    return column.ColumnName;
}

static string BuildCountAlias(QueryableObject queryObject)
{
    var label = NormalizeSearchText(queryObject.DisplayName);
    return ContainsAny(label, "siparis", "order") ? "siparis_adedi" : "adet";
}

static string BuildSumAlias(QueryableColumn column)
{
    var label = NormalizeSearchText(BuildColumnUserLabel(column)).Replace(' ', '_');
    return string.IsNullOrWhiteSpace(label) ? "toplam" : $"toplam_{label}";
}

static bool LooksSensitive(QueryableColumn column)
{
    return ContainsAny(BuildColumnSearchText(column), "password", "sifre", "token", "secret", "hash");
}

static int ScoreSearchMatch(string normalizedQuestion, string searchable)
{
    var score = 0;
    foreach (var token in normalizedQuestion.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length > 2))
    {
        if (searchable.Contains(token, StringComparison.Ordinal))
        {
            score += token.Length > 4 ? 2 : 1;
        }
    }

    return score;
}

static bool ContainsAny(string text, params string[] values)
{
    return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

static string NormalizeSearchText(string value)
{
    var lower = value
        .Replace('İ', 'i')
        .Replace('I', 'i')
        .Replace('ı', 'i')
        .Replace('Ş', 's')
        .Replace('ş', 's')
        .Replace('Ğ', 'g')
        .Replace('ğ', 'g')
        .Replace('Ü', 'u')
        .Replace('ü', 'u')
        .Replace('Ö', 'o')
        .Replace('ö', 'o')
        .Replace('Ç', 'c')
        .Replace('ç', 'c')
        .ToLowerInvariant();
    var builder = new StringBuilder();
    foreach (var character in lower.Normalize(NormalizationForm.FormD))
    {
        if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
        {
            continue;
        }

        builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
    }

    return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
}

static async Task<Guid> UpsertErpObjectAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId,
    Guid? connectionId,
    string objectName,
    string businessDomain,
    string description
)
{
    await using var command = new NpgsqlCommand("""
        insert into tenant_erp_objects (
            tenant_id,
            connection_id,
            object_name,
            object_type,
            business_domain,
            display_name_tr,
            description,
            description_tr,
            is_queryable,
            is_active,
            updated_at
        )
        values (
            @tenant_id,
            @connection_id,
            @object_name,
            'view',
            @business_domain,
            @object_name,
            @description,
            @description,
            true,
            true,
            now()
        )
        on conflict (tenant_id, object_name) do update
        set connection_id = excluded.connection_id,
            object_type = excluded.object_type,
            business_domain = excluded.business_domain,
            display_name_tr = coalesce(tenant_erp_objects.display_name_tr, excluded.display_name_tr),
            description = excluded.description,
            description_tr = coalesce(tenant_erp_objects.description_tr, excluded.description_tr),
            is_queryable = true,
            is_active = true,
            updated_at = now()
        returning id
        """, connection, transaction);

    command.Parameters.AddWithValue("tenant_id", tenantId);
    command.Parameters.AddWithValue("connection_id", (object?)connectionId ?? DBNull.Value);
    command.Parameters.AddWithValue("object_name", objectName);
    command.Parameters.AddWithValue("business_domain", businessDomain);
    command.Parameters.AddWithValue("description", description);

    return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("ERP object could not be saved."));
}

static async Task UpsertErpColumnsAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid objectId,
    string[] columns
)
{
    if (columns.Length == 0)
    {
        return;
    }

    await using (var deactivateCommand = new NpgsqlCommand("""
        update tenant_erp_object_columns
        set is_active = false,
            updated_at = now()
        where object_id = @object_id
        """, connection, transaction))
    {
        deactivateCommand.Parameters.AddWithValue("object_id", objectId);
        await deactivateCommand.ExecuteNonQueryAsync();
    }

    foreach (var column in columns)
    {
        await using var command = new NpgsqlCommand("""
            insert into tenant_erp_object_columns (
                object_id,
                column_name,
                business_name,
                display_name_tr,
                is_sensitive,
                is_filterable,
                is_groupable,
                is_summable,
                is_active,
                updated_at
            )
            values (
                @object_id,
                @column_name,
                @business_name,
                @business_name,
                false,
                true,
                true,
                false,
                true,
                now()
            )
            on conflict (object_id, column_name) do update
            set business_name = excluded.business_name,
                display_name_tr = coalesce(tenant_erp_object_columns.display_name_tr, excluded.display_name_tr),
                is_active = true,
                updated_at = now()
            """, connection, transaction);

        command.Parameters.AddWithValue("object_id", objectId);
        command.Parameters.AddWithValue("column_name", column);
        command.Parameters.AddWithValue("business_name", column);
        await command.ExecuteNonQueryAsync();
    }
}

static async Task<ColumnMeaningView[]> LoadColumnMeaningViewsAsync(Guid tenantId)
{
    var views = new Dictionary<string, List<ColumnMeaningItem>>(StringComparer.OrdinalIgnoreCase);
    await using var connection = await OpenRegistryConnectionAsync();
    await using var command = new NpgsqlCommand("""
        select
            o.object_name,
            c.column_name,
            coalesce(c.display_name_tr, m.customer_label, ''),
            coalesce(c.semantic_meaning_tr, m.customer_description, c.description, '')
        from tenant_erp_objects o
        join tenant_erp_object_columns c on c.object_id = o.id
        left join tenant_erp_column_meanings m
            on m.column_id = c.id
           and m.language_code = 'tr'
        where o.tenant_id = @tenant_id
          and o.is_active = true
          and c.is_active = true
        order by o.object_name, c.column_name
        """, connection);

    command.Parameters.AddWithValue("tenant_id", tenantId);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var viewName = reader.GetString(0);
        if (!views.TryGetValue(viewName, out var columns))
        {
            columns = [];
            views[viewName] = columns;
        }

        columns.Add(new ColumnMeaningItem(
            ColumnName: reader.GetString(1),
            CustomerLabel: reader.GetString(2),
            CustomerDescription: reader.GetString(3)
        ));
    }

    return views
        .Select(view => new ColumnMeaningView(view.Key, view.Value.ToArray()))
        .ToArray();
}

static async Task<SaveColumnMeaningsResponse> SaveColumnMeaningsAsync(
    AuthenticatedUser session,
    SaveColumnMeaningsRequest request
)
{
    var viewName = NormalizeOracleIdentifier(request.ViewName);
    var meanings = (request.Meanings ?? [])
        .Where(meaning => !string.IsNullOrWhiteSpace(meaning.ColumnName))
        .Select(meaning => meaning with
        {
            ColumnName = NormalizeColumnNameFromInput(meaning.ColumnName),
            CustomerLabel = meaning.CustomerLabel?.Trim() ?? string.Empty,
            CustomerDescription = meaning.CustomerDescription?.Trim()
        })
        .Where(meaning =>
            !string.IsNullOrWhiteSpace(meaning.ColumnName) &&
            (!string.IsNullOrWhiteSpace(meaning.CustomerLabel) || !string.IsNullOrWhiteSpace(meaning.CustomerDescription))
        )
        .ToArray();

    await using var connection = await OpenRegistryConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var savedCount = 0;
    foreach (var meaning in meanings)
    {
        await using var command = new NpgsqlCommand("""
            insert into tenant_erp_column_meanings (
                column_id,
                customer_label,
                customer_description,
                language_code,
                updated_at
            )
            select
                c.id,
                @customer_label,
                @customer_description,
                'tr',
                now()
            from tenant_erp_objects o
            join tenant_erp_object_columns c on c.object_id = o.id
            where o.tenant_id = @tenant_id
              and o.object_name = @view_name
              and c.column_name = @column_name
            on conflict (column_id, language_code) do update
            set customer_label = excluded.customer_label,
                customer_description = excluded.customer_description,
                updated_at = now()
            """, connection, transaction);

        command.Parameters.AddWithValue("tenant_id", session.TenantId);
        command.Parameters.AddWithValue("view_name", viewName);
        command.Parameters.AddWithValue("column_name", meaning.ColumnName);
        command.Parameters.AddWithValue("customer_label", meaning.CustomerLabel ?? string.Empty);
        command.Parameters.AddWithValue("customer_description", (object?)meaning.CustomerDescription ?? DBNull.Value);
        savedCount += await command.ExecuteNonQueryAsync();

        await using var columnCommand = new NpgsqlCommand("""
            update tenant_erp_object_columns c
            set display_name_tr = @customer_label,
                semantic_meaning_tr = @customer_description,
                updated_at = now()
            from tenant_erp_objects o
            where c.object_id = o.id
              and o.tenant_id = @tenant_id
              and o.object_name = @view_name
              and c.column_name = @column_name
            """, connection, transaction);

        columnCommand.Parameters.AddWithValue("tenant_id", session.TenantId);
        columnCommand.Parameters.AddWithValue("view_name", viewName);
        columnCommand.Parameters.AddWithValue("column_name", meaning.ColumnName);
        columnCommand.Parameters.AddWithValue("customer_label", meaning.CustomerLabel ?? string.Empty);
        columnCommand.Parameters.AddWithValue("customer_description", (object?)meaning.CustomerDescription ?? DBNull.Value);
        await columnCommand.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
    return new SaveColumnMeaningsResponse(viewName, savedCount);
}

sealed record HttpRequestData(
    string Method,
    string Path,
    string Body,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query
)
{
    public string? BearerToken
    {
        get
        {
            if (!Headers.TryGetValue("authorization", out var authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return authorization["Bearer ".Length..].Trim();
        }
    }

    public string? Cookie(string name)
    {
        if (!Headers.TryGetValue("cookie", out var cookieHeader))
        {
            return null;
        }

        foreach (var cookie in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = cookie.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var cookieName = cookie[..separatorIndex].Trim();
            if (!string.Equals(cookieName, name, StringComparison.Ordinal))
            {
                continue;
            }

            return Uri.UnescapeDataString(cookie[(separatorIndex + 1)..].Trim());
        }

        return null;
    }

    public string? QueryValue(string name)
    {
        return Query.TryGetValue(name, out var value)
            ? value
            : null;
    }

    public T? ReadJson<T>() => string.IsNullOrWhiteSpace(Body)
        ? default
        : JsonSerializer.Deserialize<T>(Body, AppJson.Options);

    public static async Task<HttpRequestData> ReadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        var headerEnd = -1;
        var contentLength = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            var bytes = memory.ToArray();
            headerEnd = FindHeaderEnd(bytes);

            if (headerEnd >= 0)
            {
                var headers = Encoding.UTF8.GetString(bytes, 0, headerEnd);
                contentLength = ParseContentLength(headers);
                var bodyBytesRead = bytes.Length - headerEnd - 4;
                if (bodyBytesRead >= contentLength)
                {
                    break;
                }
            }
        }

        var data = memory.ToArray();
        if (headerEnd < 0)
        {
            throw new InvalidOperationException("Invalid HTTP request.");
        }

        var headerText = Encoding.UTF8.GetString(data, 0, headerEnd);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = headerLines.FirstOrDefault()
            ?? throw new InvalidOperationException("Invalid HTTP request line.");
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Invalid HTTP request line.");
        }

        var bodyStart = headerEnd + 4;
        var body = contentLength > 0
            ? Encoding.UTF8.GetString(data, bodyStart, Math.Min(contentLength, data.Length - bodyStart))
            : string.Empty;

        var target = parts[1];
        var queryStart = target.IndexOf('?');
        var path = queryStart >= 0 ? target[..queryStart] : target;
        var queryString = queryStart >= 0 ? target[(queryStart + 1)..] : string.Empty;

        return new HttpRequestData(parts[0], path, body, ParseHeaders(headerLines), ParseQuery(queryString));
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var length))
            {
                return length;
            }
        }

        return 0;
    }

    private static Dictionary<string, string> ParseHeaders(string[] headerLines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerLines.Skip(1))
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            headers[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        return headers;
    }

    private static Dictionary<string, string> ParseQuery(string queryString)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return query;
        }

        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var key = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            query[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return query;
    }
}

sealed record HttpResponseData(
    HttpStatusCode StatusCode,
    string Body,
    string ContentType,
    IReadOnlyDictionary<string, string> ExtraHeaders
)
{
    public static HttpResponseData Json(
        HttpStatusCode statusCode,
        object payload,
        IReadOnlyDictionary<string, string>? extraHeaders = null
    )
    {
        var body = statusCode == HttpStatusCode.NoContent
            ? string.Empty
            : JsonSerializer.Serialize(payload, AppJson.Options);
        return new HttpResponseData(statusCode, body, "application/json; charset=utf-8", extraHeaders ?? new Dictionary<string, string>());
    }

    public static HttpResponseData Html(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseData(statusCode, body, "text/html; charset=utf-8", new Dictionary<string, string>());
    }

    public static HttpResponseData Redirect(
        string location,
        IReadOnlyDictionary<string, string>? extraHeaders = null
    )
    {
        var headers = new Dictionary<string, string>(extraHeaders ?? new Dictionary<string, string>())
        {
            ["Location"] = location
        };

        return new HttpResponseData(HttpStatusCode.SeeOther, string.Empty, "text/plain; charset=utf-8", headers);
    }

    public async Task WriteAsync(NetworkStream stream)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(Body);
        var statusNumber = (int)StatusCode;
        var statusText = StatusCode.ToString();
        var headerLines = new List<string>
        {
            $"HTTP/1.1 {statusNumber} {statusText}",
            $"Content-Type: {ContentType}",
            $"Content-Length: {bodyBytes.Length}",
            "Access-Control-Allow-Origin: *",
            "Access-Control-Allow-Headers: authorization,content-type",
            "Access-Control-Allow-Methods: GET,POST,OPTIONS",
            "Connection: close"
        };

        foreach (var header in ExtraHeaders)
        {
            headerLines.Add($"{header.Key}: {header.Value}");
        }

        headerLines.Add("");
        headerLines.Add("");

        var headers = string.Join("\r\n", headerLines);

        await stream.WriteAsync(Encoding.UTF8.GetBytes(headers));
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }
    }
}

static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );

        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expectedKey = Convert.FromBase64String(parts[3]);
        var actualKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedKey.Length
        );

        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}

static class TokenHasher
{
    public static string Hash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

static class JwtTokenService
{
    public static string Create(AuthenticatedUser user)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = user.UserId,
            ["tenant_id"] = user.TenantId,
            ["role"] = user.Role,
            ["name"] = user.DisplayName,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()
        }));
        var unsignedToken = $"{header}.{payload}";
        var signature = Base64UrlEncode(Sign(unsignedToken));
        return $"{unsignedToken}.{signature}";
    }

    public static bool TryValidate(string token, out Guid userId, out Guid tenantId)
    {
        userId = Guid.Empty;
        tenantId = Guid.Empty;

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var unsignedToken = $"{parts[0]}.{parts[1]}";
        var expectedSignature = Base64UrlEncode(Sign(unsignedToken));
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedSignature),
            Encoding.UTF8.GetBytes(parts[2])
        ))
        {
            return false;
        }

        Dictionary<string, JsonElement>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(parts[1]));
        }
        catch
        {
            return false;
        }

        if (payload is null ||
            !payload.TryGetValue("sub", out var subValue) ||
            !payload.TryGetValue("tenant_id", out var tenantValue) ||
            !payload.TryGetValue("exp", out var expValue) ||
            !Guid.TryParse(subValue.ToString(), out userId) ||
            !Guid.TryParse(tenantValue.ToString(), out tenantId) ||
            !long.TryParse(expValue.ToString(), out var exp) ||
            exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            userId = Guid.Empty;
            tenantId = Guid.Empty;
            return false;
        }

        return true;
    }

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret()));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Secret()
    {
        return Environment.GetEnvironmentVariable("VGANTT_JWT_SECRET")
            ?? "vgantt-ai-local-development-secret-change-me";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}

sealed record AuthenticatedUser(
    Guid UserId,
    Guid TenantId,
    string DisplayName,
    string TenantName,
    string TenantCode,
    string Role
);

sealed record LoginRequest(
    string Username,
    string Password,
    string? DeviceId = null,
    string? DeviceName = null,
    string? Platform = null,
    string? AppVersion = null,
    string? PushToken = null
);

sealed record LoginResponse(string Token, string TenantName, string UserDisplayName, string Role);

sealed record AssistantRequest(
    string Question,
    AssistantHistoryItem[]? History = null
);

sealed record AssistantHistoryItem(
    string Role,
    string Text,
    string? Sql = null,
    string[]? Columns = null,
    string[][]? Rows = null
);

sealed record AssistantResponse(
    string Summary,
    string Sql,
    string[] Columns,
    string[][] Rows
);

sealed record AiSqlPlan(
    bool NeedsClarification,
    string ClarificationQuestion,
    string Sql,
    string Explanation,
    double Confidence
);

enum AssistantMetricKind
{
    Count,
    Sum,
    List
}

sealed record AssistantSchema(
    TenantDbConnectionSettings Connection,
    QueryableObject[] Objects,
    QueryableRelation[] Relations
);

sealed record QueryableObject(
    Guid ObjectId,
    string ObjectName,
    string ObjectType,
    string BusinessDomain,
    string DisplayName,
    string Description,
    QueryableColumn[] Columns
);

sealed record QueryableColumn(
    string ColumnName,
    string DisplayName,
    string SemanticMeaning,
    string DataType,
    bool IsFilterable,
    bool IsGroupable,
    bool IsSummable
);

sealed record QueryableRelation(
    Guid RelationId,
    string RelationName,
    Guid SourceObjectId,
    string SourceObjectName,
    Guid TargetObjectId,
    string TargetObjectName,
    string JoinType,
    string Description,
    QueryableRelationColumn[] Columns
);

sealed record QueryableRelationColumn(
    string SourceColumnName,
    string TargetColumnName,
    int Ordinal
);

sealed record QueryableObjectBuilder(
    Guid ObjectId,
    string ObjectName,
    string ObjectType,
    string BusinessDomain,
    string DisplayName,
    string Description
)
{
    public QueryableObject ToObject(List<QueryableColumn> columns)
    {
        return new QueryableObject(
            ObjectId,
            ObjectName,
            ObjectType,
            BusinessDomain,
            DisplayName,
            Description,
            columns.ToArray()
        );
    }
}

sealed record QueryableRelationBuilder(
    Guid RelationId,
    string RelationName,
    Guid SourceObjectId,
    string SourceObjectName,
    Guid TargetObjectId,
    string TargetObjectName,
    string JoinType,
    string Description
)
{
    public List<QueryableRelationColumn> Columns { get; } = [];

    public QueryableRelation ToRelation()
    {
        return new QueryableRelation(
            RelationId,
            RelationName,
            SourceObjectId,
            SourceObjectName,
            TargetObjectId,
            TargetObjectName,
            JoinType,
            Description,
            Columns.OrderBy(column => column.Ordinal).ToArray()
        );
    }
}

sealed record DateColumnSelection(
    QueryableObject Object,
    QueryableColumn Column,
    QueryableRelation? Relation
);

sealed record MetricColumnSelection(
    QueryableObject Object,
    QueryableColumn Column,
    QueryableRelation? Relation
);

sealed record DateRange(
    DateOnly Start,
    DateOnly EndExclusive,
    string Label
);

sealed record AssistantQueryPlan(
    string Sql,
    AssistantMetricKind MetricKind,
    QueryableObject BaseObject,
    MetricColumnSelection? MetricColumn,
    DateColumnSelection? DateSelection,
    DateRange? DateRange
);

sealed record AssistantQueryResult(
    string[] Columns,
    string[][] Rows
);

sealed record AssistantPlanResult(
    AssistantQueryPlan? Plan,
    string? Clarification
)
{
    public static AssistantPlanResult Ready(AssistantQueryPlan plan)
    {
        return new AssistantPlanResult(plan, null);
    }

    public static AssistantPlanResult NeedsClarification(string clarification)
    {
        return new AssistantPlanResult(null, clarification);
    }
}

sealed record SqlDialect(
    string Provider,
    string IdentifierOpen,
    string IdentifierClose,
    string TableAliasSeparator
)
{
    public static SqlDialect ForProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "mysql" => new SqlDialect(provider, "`", "`", ""),
            "sqlserver" => new SqlDialect(provider, "[", "]", "as "),
            "oracle" => new SqlDialect(provider, "\"", "\"", ""),
            _ => new SqlDialect(provider, "\"", "\"", "as ")
        };
    }

    public string QuoteObjectName(string objectName)
    {
        return string.Join(".", objectName.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(QuoteIdentifier));
    }

    public string QuoteQualified(string alias, string columnName)
    {
        return $"{alias}.{QuoteIdentifier(columnName)}";
    }

    public string QuoteIdentifier(string value)
    {
        var escaped = IdentifierClose == "]"
            ? value.Replace("]", "]]")
            : value.Replace(IdentifierClose, IdentifierClose + IdentifierClose);
        return $"{IdentifierOpen}{escaped}{IdentifierClose}";
    }

    public string DateLiteral(DateOnly date)
    {
        var value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Provider.Trim().ToLowerInvariant() switch
        {
            "sqlserver" => $"cast('{value}' as date)",
            _ => $"date '{value}'"
        };
    }

    public string LimitClause(int count)
    {
        return Provider.Trim().ToLowerInvariant() switch
        {
            "oracle" => $"fetch first {count} rows only",
            "sqlserver" => string.Empty,
            _ => $"limit {count}"
        };
    }
}

sealed record TenantDbConnectionSettings(
    string Provider,
    string Host,
    int Port,
    string DatabaseName,
    string Username,
    string Password,
    string SslMode
);

sealed record TenantDbHealthResponse(
    string Status,
    string Provider,
    string Host,
    int Port,
    string DatabaseName,
    string Version
);

sealed record AdminDashboardResponse(long ViewCount, long ColumnCount, long RelationCount);

sealed record SaveAdminViewRequest(
    Guid? ViewId,
    string ViewName,
    string? DisplayNameTr = null,
    string? DescriptionTr = null,
    bool IsActive = true
);

sealed record AdminViewResponse(
    Guid ViewId,
    string ViewName,
    string DisplayNameTr,
    string DescriptionTr,
    bool IsActive
);

sealed record SaveAdminViewColumnsRequest(
    Guid ViewId,
    AdminViewColumnInput[]? Columns
);

sealed record NormalizeAdminViewColumnsRequest(
    Guid ViewId,
    string? RawInput = null,
    AdminViewColumnInput[]? Columns = null
);

sealed record AdminViewColumnInput(
    string ColumnName,
    string? DisplayNameTr = null,
    string? DataType = null,
    string? SemanticMeaningTr = null,
    bool IsFilterable = true,
    bool IsGroupable = true,
    bool IsSummable = false,
    bool IsActive = true
);

sealed record AdminViewColumnResponse(
    Guid ColumnId,
    Guid ViewId,
    string ColumnName,
    string DisplayNameTr,
    string DataType,
    string SemanticMeaningTr,
    bool IsFilterable,
    bool IsGroupable,
    bool IsSummable,
    bool IsActive
);

sealed record SaveAdminViewColumnsResponse(Guid ViewId, int SavedCount);

sealed record NormalizeAdminViewColumnsResponse(
    string Message,
    AdminViewColumnInput[] Columns
);

sealed record NormalizedColumnsOutput(AdminViewColumnInput[] Columns);

sealed record SaveAdminRelationRequest(
    Guid? RelationId,
    string RelationName,
    Guid SourceViewId,
    Guid TargetViewId,
    string? JoinType = "INNER JOIN",
    string? DescriptionTr = null,
    bool IsActive = true,
    SaveAdminRelationColumnRequest[]? Columns = null
);

sealed record SaveAdminRelationColumnRequest(
    string SourceColumnName,
    string TargetColumnName,
    int Ordinal = 1
);

sealed record AdminRelationColumnResponse(
    Guid RelationColumnId,
    string SourceColumnName,
    string TargetColumnName,
    int Ordinal
);

sealed record AdminRelationResponse(
    Guid RelationId,
    string RelationName,
    Guid SourceViewId,
    string SourceViewName,
    Guid TargetViewId,
    string TargetViewName,
    string JoinType,
    string DescriptionTr,
    bool IsActive,
    AdminRelationColumnResponse[] Columns
);

sealed record AdminRelationResponseBuilder(
    Guid RelationId,
    string RelationName,
    Guid SourceViewId,
    string SourceViewName,
    Guid TargetViewId,
    string TargetViewName,
    string JoinType,
    string DescriptionTr,
    bool IsActive
)
{
    public List<AdminRelationColumnResponse> Columns { get; } = [];

    public AdminRelationResponse ToResponse() => new(
        RelationId,
        RelationName,
        SourceViewId,
        SourceViewName,
        TargetViewId,
        TargetViewName,
        JoinType,
        DescriptionTr,
        IsActive,
        Columns.ToArray()
    );
}

sealed record SaveSalesViewsRequest(
    string? View1Name = null,
    string? View2Name = null,
    string[]? View1Columns = null,
    string[]? View2Columns = null,
    ViewRelationshipRequest[]? Relationships = null,
    string? OrderViewName = null,
    string? OrderLineViewName = null,
    string? JoinColumnName = null,
    string? BusinessDomain = null
)
{
    public string EffectiveView1Name => View1Name ?? OrderViewName ?? string.Empty;

    public string EffectiveView2Name => View2Name ?? OrderLineViewName ?? string.Empty;

    public IReadOnlyList<ViewRelationshipRequest> EffectiveRelationships =>
        Relationships is { Length: > 0 }
            ? Relationships
            : string.IsNullOrWhiteSpace(JoinColumnName)
                ? []
                : [new ViewRelationshipRequest(JoinColumnName, JoinColumnName)];
}

sealed record ViewRelationshipRequest(string View1ColumnName, string View2ColumnName);

sealed record SalesViewsResponse(
    string TenantName,
    string View1Name,
    string View2Name,
    ViewRelationshipRequest[] Relationships,
    string BusinessDomain,
    int View1ColumnCount,
    int View2ColumnCount
);

sealed record ColumnMeaningView(string ViewName, ColumnMeaningItem[] Columns);

sealed record ColumnMeaningItem(
    string ColumnName,
    string CustomerLabel,
    string? CustomerDescription
);

sealed record SaveColumnMeaningsRequest(
    string ViewName,
    ColumnMeaningInput[]? Meanings
);

sealed record ColumnMeaningInput(
    string ColumnName,
    string? CustomerLabel = null,
    string? CustomerDescription = null
);

sealed record SaveColumnMeaningsResponse(string ViewName, int SavedCount);
