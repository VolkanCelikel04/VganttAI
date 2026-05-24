using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

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

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();

Console.WriteLine($"Vgantt ERP AI API listening on http://127.0.0.1:{port}");

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

        return HttpResponseData.Json(HttpStatusCode.OK, new LoginResponse(
            Token: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            TenantName: authenticatedUser.TenantName,
            UserDisplayName: authenticatedUser.DisplayName
        ));
    }

    if (request.Method == "POST" && request.Path == "/assistant/ask")
    {
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
        select u.display_name, u.password_hash, t.name as tenant_name
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

    var displayName = reader.GetString(0);
    var passwordHash = reader.GetString(1);
    var tenantName = reader.GetString(2);

    return PasswordHasher.Verify(password, passwordHash)
        ? new AuthenticatedUser(displayName, tenantName)
        : null;
}

sealed record HttpRequestData(string Method, string Path, string Body)
{
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
        var requestLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
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

        return new HttpRequestData(parts[0], parts[1].Split('?')[0], body);
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
}

sealed record HttpResponseData(HttpStatusCode StatusCode, string Body)
{
    public static HttpResponseData Json(HttpStatusCode statusCode, object payload)
    {
        var body = statusCode == HttpStatusCode.NoContent
            ? string.Empty
            : JsonSerializer.Serialize(payload, AppJson.Options);
        return new HttpResponseData(statusCode, body);
    }

    public async Task WriteAsync(NetworkStream stream)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(Body);
        var statusNumber = (int)StatusCode;
        var statusText = StatusCode.ToString();
        var headers = string.Join("\r\n", [
            $"HTTP/1.1 {statusNumber} {statusText}",
            "Content-Type: application/json; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Access-Control-Allow-Origin: *",
            "Access-Control-Allow-Headers: authorization,content-type",
            "Access-Control-Allow-Methods: GET,POST,OPTIONS",
            "Connection: close",
            "",
            ""
        ]);

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

sealed record AuthenticatedUser(string DisplayName, string TenantName);

sealed record LoginRequest(string Username, string Password);

sealed record LoginResponse(string Token, string TenantName, string UserDisplayName);

sealed record AssistantRequest(string Question);

sealed record AssistantResponse(
    string Summary,
    string Sql,
    string[] Columns,
    string[][] Rows
);
