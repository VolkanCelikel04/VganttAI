using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

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

        return HttpResponseData.Json(HttpStatusCode.OK, new AssistantResponse(
            Summary: "Backend calisiyor. AI ve tenant sorgu katmani bir sonraki adimda baglanacak.",
            Sql: "select now() as server_time",
            Columns: ["server_time"],
            Rows: [[DateTimeOffset.UtcNow.ToString("O")]]
        ));
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
    var dashboardActive = activePage == "dashboard" ? "active" : "";
    var viewsActive = activePage == "views" ? "active" : "";
    var columnsActive = activePage == "columns" ? "active" : "";
    var relationsActive = activePage == "relations" ? "active" : "";

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
    body { margin: 0; background: #f6f7f9; }
    .app-shell { min-height: 100vh; display: grid; grid-template-columns: 240px minmax(0, 1fr); }
    .sidebar { background: #17242b; color: #f9fafb; padding: 18px 14px; }
    .brand { display: grid; gap: 2px; padding: 8px 8px 18px; border-bottom: 1px solid rgba(255,255,255,.14); }
    .brand strong { font-size: 18px; }
    .brand span { color: #b8c4cc; font-size: 12px; }
    .nav-section { margin-top: 18px; }
    .nav-title { color: #b8c4cc; font-size: 12px; font-weight: 700; margin: 0 8px 8px; text-transform: uppercase; }
    .nav-item { display: flex; align-items: center; color: #fff; border-radius: 6px; padding: 10px 12px; font-weight: 700; text-decoration: none; margin-bottom: 6px; }
    .nav-item.active { background: rgba(255,255,255,.12); }
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
    .grid-2 { display: grid; grid-template-columns: 360px minmax(0, 1fr); gap: 16px; align-items: start; }
    .stats { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
    .stat { border: 1px solid var(--line); border-radius: 8px; padding: 16px; background: #fff; }
    .stat strong { display: block; font-size: 28px; margin-bottom: 4px; }
    form { display: grid; gap: 12px; }
    label { display: grid; gap: 6px; font-size: 13px; font-weight: 600; color: #344054; }
    input, textarea, select { border: 1px solid #c8d0d9; border-radius: 6px; font: inherit; padding: 9px 10px; min-width: 0; }
    textarea { min-height: 84px; resize: vertical; }
    .check-row { display: flex; gap: 16px; flex-wrap: wrap; }
    .check-row label { display: flex; align-items: center; gap: 6px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { border-bottom: 1px solid var(--line); padding: 8px; text-align: left; vertical-align: top; }
    th { color: #344054; font-size: 13px; background: #f8fafc; }
    td.mono { font-family: Consolas, monospace; font-size: 13px; }
    .table-wrap { overflow: auto; border: 1px solid var(--line); border-radius: 8px; }
    .toolbar { display: flex; align-items: end; justify-content: space-between; gap: 12px; margin-bottom: 12px; }
    .button-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    button.primary, button.secondary, button.icon { border-radius: 6px; font: inherit; font-weight: 700; padding: 9px 12px; cursor: pointer; }
    button.primary { border: 0; background: var(--primary); color: #fff; }
    button.primary:hover { background: var(--primary-dark); }
    button.secondary, button.icon { border: 1px solid var(--line); background: #fff; color: #1f2933; }
    button:disabled { opacity: .65; cursor: wait; }
    .message { min-height: 22px; font-size: 14px; font-weight: 600; }
    .message.ok { color: var(--success); }
    .message.error { color: var(--danger); }
    .relation-columns { display: grid; gap: 8px; }
    .relation-column-row { display: grid; grid-template-columns: 70px minmax(0, 1fr) 28px minmax(0, 1fr) 40px; gap: 8px; align-items: end; }
    .equals { align-self: center; text-align: center; color: var(--muted); font-weight: 700; }

    @media (max-width: 900px) {
      .app-shell, .grid-2, .stats, .relation-column-row { grid-template-columns: 1fr; }
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
        <a class="nav-item {{dashboardActive}}" href="/admin/dashboard">Dashboard</a>
        <a class="nav-item {{viewsActive}}" href="/admin/settings/views">Views</a>
        <a class="nav-item {{columnsActive}}" href="/admin/settings/columns">View Columns</a>
        <a class="nav-item {{relationsActive}}" href="/admin/settings/relations">View Relations</a>
      </nav>
    </aside>
    <div class="content">
      <header class="topbar">
        <div>
          <h1 id="pageTitle">Vgantt AI Admin</h1>
          <p id="pageSubtitle">ERP view yapisini tanimlayin.</p>
        </div>
        <div class="user-meta">
          <span>{{displayName}} / {{role}}</span>
          <button id="logoutButton" class="logout" type="button">Cikis yap</button>
        </div>
      </header>
      <main>
        <section id="dashboardPage" class="section hidden">
          <h2>Dashboard</h2>
          <div class="stats">
            <div class="stat"><strong id="viewCount">0</strong><span>View</span></div>
            <div class="stat"><strong id="columnCount">0</strong><span>Kolon</span></div>
            <div class="stat"><strong id="relationCount">0</strong><span>Iliski</span></div>
          </div>
        </section>

        <section id="viewsPage" class="grid-2 hidden">
          <div class="section">
            <h2>View Tanimi</h2>
            <form id="viewForm">
              <input id="viewId" type="hidden">
              <label>view_name<input id="viewName" placeholder="vgantt_ai_customer_order" required></label>
              <label>display_name_tr<input id="viewDisplayNameTr" placeholder="Musteri Siparisleri"></label>
              <label>description_tr<textarea id="viewDescriptionTr" placeholder="Satis siparis baslik bilgileri"></textarea></label>
              <div class="check-row"><label><input id="viewIsActive" type="checkbox" checked> is_active</label></div>
              <div class="button-row">
                <button class="primary" type="submit">Kaydet</button>
                <button id="newViewButton" class="secondary" type="button">Yeni</button>
              </div>
              <div id="viewMessage" class="message" role="status"></div>
            </form>
          </div>
          <div class="section">
            <div class="toolbar">
              <h2>Views</h2>
              <button id="refreshViewsButton" class="secondary" type="button">Yenile</button>
            </div>
            <div class="table-wrap">
              <table>
                <thead><tr><th>view_id</th><th>view_name</th><th>display_name_tr</th><th>is_active</th></tr></thead>
                <tbody id="viewsBody"></tbody>
              </table>
            </div>
          </div>
        </section>

        <section id="columnsPage" class="section hidden">
          <div class="toolbar">
            <div>
              <h2>View Columns</h2>
              <p>Her view icin AI'nin anlayacagi kolon sozlugunu yazin.</p>
            </div>
            <label>View<select id="columnsViewSelect"></select></label>
          </div>
          <div class="button-row">
            <button id="addColumnButton" class="secondary" type="button">Kolon ekle</button>
            <button id="saveColumnsButton" class="primary" type="button">Kaydet</button>
          </div>
          <div class="table-wrap" style="margin-top:12px">
            <table>
              <thead>
                <tr>
                  <th>column_name</th><th>display_name_tr</th><th>data_type</th><th>semantic_meaning_tr</th>
                  <th>filter</th><th>group</th><th>sum</th><th>active</th><th></th>
                </tr>
              </thead>
              <tbody id="columnsBody"></tbody>
            </table>
          </div>
          <div id="columnsMessage" class="message" role="status"></div>
        </section>

        <section id="relationsPage" class="grid-2 hidden">
          <div class="section">
            <h2>Relation Tanimi</h2>
            <form id="relationForm">
              <input id="relationId" type="hidden">
              <label>relation_name<input id="relationName" placeholder="customer_order_to_lines" required></label>
              <label>source_view_id<select id="sourceViewSelect"></select></label>
              <label>target_view_id<select id="targetViewSelect"></select></label>
              <label>join_type
                <select id="joinType">
                  <option>INNER JOIN</option>
                  <option>LEFT JOIN</option>
                  <option>RIGHT JOIN</option>
                  <option>FULL JOIN</option>
                </select>
              </label>
              <label>description_tr<textarea id="relationDescriptionTr" placeholder="Siparis basligi ile siparis satirlari order_no uzerinden eslesir."></textarea></label>
              <div class="check-row"><label><input id="relationIsActive" type="checkbox" checked> is_active</label></div>
              <h2>Relation Columns</h2>
              <div id="relationColumns" class="relation-columns"></div>
              <div class="button-row">
                <button id="addRelationColumnButton" class="secondary" type="button">Eslesme ekle</button>
                <button class="primary" type="submit">Kaydet</button>
                <button id="newRelationButton" class="secondary" type="button">Yeni</button>
              </div>
              <div id="relationMessage" class="message" role="status"></div>
            </form>
          </div>
          <div class="section">
            <div class="toolbar">
              <h2>View Relations</h2>
              <button id="refreshRelationsButton" class="secondary" type="button">Yenile</button>
            </div>
            <div class="table-wrap">
              <table>
                <thead><tr><th>relation_name</th><th>source</th><th>target</th><th>join</th><th>columns</th><th>active</th></tr></thead>
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
    const pageTitles = {
      dashboard: ['Dashboard', 'Admin panel ozeti.'],
      views: ['Views', 'ERP view tanimlarini yonetin.'],
      columns: ['View Columns', 'Kolon anlamlarini ve AI semantigini yonetin.'],
      relations: ['View Relations', 'View join/eslesme tanimlarini yonetin.']
    };
    let views = [];
    let relations = [];

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

    function showPage(page) {
      for (const id of ['dashboardPage', 'viewsPage', 'columnsPage', 'relationsPage']) {
        document.getElementById(id).classList.add('hidden');
      }
      document.getElementById(`${page}Page`).classList.remove('hidden');
      document.getElementById('pageTitle').textContent = pageTitles[page][0];
      document.getElementById('pageSubtitle').textContent = pageTitles[page][1];
    }

    function value(id) {
      return document.getElementById(id).value.trim();
    }

    function checked(id) {
      return document.getElementById(id).checked;
    }

    function optionText(id) {
      const select = document.getElementById(id);
      return select.options[select.selectedIndex]?.textContent || '';
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

    async function loadDashboard() {
      const dashboard = await api('/admin/api/dashboard');
      document.getElementById('viewCount').textContent = dashboard.viewCount;
      document.getElementById('columnCount').textContent = dashboard.columnCount;
      document.getElementById('relationCount').textContent = dashboard.relationCount;
    }

    async function loadViews() {
      views = await api('/admin/api/views');
      renderViews();
      fillViewSelects();
    }

    function renderViews() {
      document.getElementById('viewsBody').innerHTML = views.map(view => `
        <tr data-view-id="${view.viewId}">
          <td class="mono">${shortId(view.viewId)}</td>
          <td class="mono">${escapeHtml(view.viewName)}</td>
          <td>${escapeHtml(view.displayNameTr)}</td>
          <td>${view.isActive ? 'true' : 'false'}</td>
        </tr>
      `).join('');

      for (const row of document.querySelectorAll('#viewsBody tr')) {
        row.addEventListener('click', () => editView(views.find(view => view.viewId === row.dataset.viewId)));
      }
    }

    function editView(view) {
      if (!view) return;
      document.getElementById('viewId').value = view.viewId;
      document.getElementById('viewName').value = view.viewName;
      document.getElementById('viewDisplayNameTr').value = view.displayNameTr || '';
      document.getElementById('viewDescriptionTr').value = view.descriptionTr || '';
      document.getElementById('viewIsActive').checked = view.isActive;
    }

    function clearViewForm() {
      document.getElementById('viewId').value = '';
      document.getElementById('viewName').value = '';
      document.getElementById('viewDisplayNameTr').value = '';
      document.getElementById('viewDescriptionTr').value = '';
      document.getElementById('viewIsActive').checked = true;
      setMessage('viewMessage', '', '');
    }

    async function saveView(event) {
      event.preventDefault();
      setMessage('viewMessage', '', 'Kaydediliyor...');
      const payload = {
        viewId: value('viewId') || null,
        viewName: value('viewName'),
        displayNameTr: value('viewDisplayNameTr'),
        descriptionTr: value('viewDescriptionTr'),
        isActive: checked('viewIsActive')
      };
      const saved = await api('/admin/api/views', { method: 'POST', body: JSON.stringify(payload) });
      setMessage('viewMessage', 'ok', `${saved.viewName} kaydedildi.`);
      await loadViews();
      editView(saved);
    }

    function fillViewSelects() {
      const options = views.map(view => `<option value="${view.viewId}">${escapeHtml(view.viewName)}</option>`).join('');
      for (const id of ['columnsViewSelect', 'sourceViewSelect', 'targetViewSelect']) {
        const select = document.getElementById(id);
        if (select) select.innerHTML = options;
      }
    }

    async function loadColumnsForSelectedView() {
      const viewId = value('columnsViewSelect');
      if (!viewId) return;
      const columns = await api(`/admin/api/view-columns?viewId=${encodeURIComponent(viewId)}`);
      renderColumns(columns);
    }

    function renderColumns(columns) {
      const body = document.getElementById('columnsBody');
      body.innerHTML = '';
      for (const column of columns) {
        addColumnRow(column);
      }
    }

    function addColumnRow(column = {}) {
      const row = document.createElement('tr');
      row.innerHTML = `
        <td><input data-field="columnName" value="${escapeHtml(column.columnName || '')}" placeholder="order_no"></td>
        <td><input data-field="displayNameTr" value="${escapeHtml(column.displayNameTr || '')}" placeholder="Siparis No"></td>
        <td><input data-field="dataType" value="${escapeHtml(column.dataType || '')}" placeholder="text"></td>
        <td><textarea data-field="semanticMeaningTr" placeholder="Musteri siparis numarasi">${escapeHtml(column.semanticMeaningTr || '')}</textarea></td>
        <td><input data-field="isFilterable" type="checkbox" ${column.isFilterable ?? true ? 'checked' : ''}></td>
        <td><input data-field="isGroupable" type="checkbox" ${column.isGroupable ?? true ? 'checked' : ''}></td>
        <td><input data-field="isSummable" type="checkbox" ${column.isSummable ? 'checked' : ''}></td>
        <td><input data-field="isActive" type="checkbox" ${column.isActive ?? true ? 'checked' : ''}></td>
        <td><button class="icon" type="button">X</button></td>
      `;
      row.querySelector('button').addEventListener('click', () => row.remove());
      document.getElementById('columnsBody').appendChild(row);
    }

    async function saveColumns() {
      const viewId = value('columnsViewSelect');
      const columns = [...document.querySelectorAll('#columnsBody tr')]
        .map(row => ({
          columnName: row.querySelector('[data-field="columnName"]').value.trim(),
          displayNameTr: row.querySelector('[data-field="displayNameTr"]').value.trim(),
          dataType: row.querySelector('[data-field="dataType"]').value.trim(),
          semanticMeaningTr: row.querySelector('[data-field="semanticMeaningTr"]').value.trim(),
          isFilterable: row.querySelector('[data-field="isFilterable"]').checked,
          isGroupable: row.querySelector('[data-field="isGroupable"]').checked,
          isSummable: row.querySelector('[data-field="isSummable"]').checked,
          isActive: row.querySelector('[data-field="isActive"]').checked
        }))
        .filter(column => column.columnName);

      setMessage('columnsMessage', '', 'Kaydediliyor...');
      const saved = await api('/admin/api/view-columns', {
        method: 'POST',
        body: JSON.stringify({ viewId, columns })
      });
      setMessage('columnsMessage', 'ok', `${saved.savedCount} kolon kaydedildi.`);
      await loadColumnsForSelectedView();
    }

    async function loadRelations() {
      relations = await api('/admin/api/relations');
      renderRelations();
    }

    function renderRelations() {
      document.getElementById('relationsBody').innerHTML = relations.map(relation => `
        <tr data-relation-id="${relation.relationId}">
          <td>${escapeHtml(relation.relationName)}</td>
          <td class="mono">${escapeHtml(relation.sourceViewName)}</td>
          <td class="mono">${escapeHtml(relation.targetViewName)}</td>
          <td>${escapeHtml(relation.joinType)}</td>
          <td>${relation.columns.length}</td>
          <td>${relation.isActive ? 'true' : 'false'}</td>
        </tr>
      `).join('');

      for (const row of document.querySelectorAll('#relationsBody tr')) {
        row.addEventListener('click', () => editRelation(relations.find(relation => relation.relationId === row.dataset.relationId)));
      }
    }

    function addRelationColumnRow(column = {}) {
      const row = document.createElement('div');
      row.className = 'relation-column-row';
      row.innerHTML = `
        <label>ordinal<input data-field="ordinal" type="number" min="1" value="${column.ordinal || document.querySelectorAll('.relation-column-row').length + 1}"></label>
        <label>source_column_name<input data-field="sourceColumnName" value="${escapeHtml(column.sourceColumnName || '')}" placeholder="order_no"></label>
        <span class="equals">=</span>
        <label>target_column_name<input data-field="targetColumnName" value="${escapeHtml(column.targetColumnName || '')}" placeholder="order_no"></label>
        <button class="icon" type="button">X</button>
      `;
      row.querySelector('button').addEventListener('click', () => {
        if (document.querySelectorAll('.relation-column-row').length > 1) row.remove();
      });
      document.getElementById('relationColumns').appendChild(row);
    }

    function clearRelationForm() {
      document.getElementById('relationId').value = '';
      document.getElementById('relationName').value = '';
      document.getElementById('joinType').value = 'INNER JOIN';
      document.getElementById('relationDescriptionTr').value = '';
      document.getElementById('relationIsActive').checked = true;
      document.getElementById('relationColumns').innerHTML = '';
      addRelationColumnRow({ sourceColumnName: 'order_no', targetColumnName: 'order_no', ordinal: 1 });
      setMessage('relationMessage', '', '');
    }

    function editRelation(relation) {
      if (!relation) return;
      document.getElementById('relationId').value = relation.relationId;
      document.getElementById('relationName').value = relation.relationName;
      document.getElementById('sourceViewSelect').value = relation.sourceViewId;
      document.getElementById('targetViewSelect').value = relation.targetViewId;
      document.getElementById('joinType').value = relation.joinType;
      document.getElementById('relationDescriptionTr').value = relation.descriptionTr || '';
      document.getElementById('relationIsActive').checked = relation.isActive;
      document.getElementById('relationColumns').innerHTML = '';
      for (const column of relation.columns) addRelationColumnRow(column);
    }

    async function saveRelation(event) {
      event.preventDefault();
      const columns = [...document.querySelectorAll('.relation-column-row')]
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
          columns
        })
      });
      setMessage('relationMessage', 'ok', `${saved.relationName} kaydedildi.`);
      await loadRelations();
      editRelation(saved);
    }

    document.getElementById('logoutButton').addEventListener('click', () => {
      localStorage.removeItem('vgantt_admin_token');
      window.location.href = '/admin/logout';
    });
    document.getElementById('viewForm').addEventListener('submit', saveView);
    document.getElementById('newViewButton').addEventListener('click', clearViewForm);
    document.getElementById('refreshViewsButton').addEventListener('click', loadViews);
    document.getElementById('columnsViewSelect').addEventListener('change', loadColumnsForSelectedView);
    document.getElementById('addColumnButton').addEventListener('click', () => addColumnRow());
    document.getElementById('saveColumnsButton').addEventListener('click', saveColumns);
    document.getElementById('relationForm').addEventListener('submit', saveRelation);
    document.getElementById('newRelationButton').addEventListener('click', clearRelationForm);
    document.getElementById('addRelationColumnButton').addEventListener('click', () => addRelationColumnRow());
    document.getElementById('refreshRelationsButton').addEventListener('click', loadRelations);

    async function boot() {
      showPage(activePage);
      await loadViews();
      if (activePage === 'dashboard') await loadDashboard();
      if (activePage === 'columns') await loadColumnsForSelectedView();
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

    await transaction.CommitAsync();
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

sealed record AssistantRequest(string Question);

sealed record AssistantResponse(
    string Summary,
    string Sql,
    string[] Columns,
    string[][] Rows
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
