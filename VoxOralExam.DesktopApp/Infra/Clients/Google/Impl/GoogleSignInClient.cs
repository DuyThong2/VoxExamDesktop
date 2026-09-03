using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.Google.Impl;

/// <summary>
/// Google sign-in for a desktop app: authorization code + PKCE, over a loopback redirect.
///
/// <para><b>Why the system browser and not WebView2</b>, which this project already references.
/// Google's own policy for OAuth blocks embedded webviews, and they enforce it by user agent -- a
/// flow built on WebView2 works right up until they decide it does not, and then it fails for every
/// user at once with nothing on our side having changed. The system browser is also the only way the
/// student can see the real accounts.google.com address bar and padlock, which is the entire basis
/// on which they are being asked to type a password. RFC 8252 says the same thing at greater length.</para>
///
/// <para><b>Why PKCE, and why a client secret as well.</b> A desktop app cannot keep a secret --
/// anything shipped in the .exe is readable by anyone holding it -- so the secret proves nothing on
/// its own. PKCE is what actually secures this: the verifier is generated per attempt and never
/// leaves the process, so an attacker who intercepts the authorization code still cannot redeem it.
/// Google nonetheless issues Desktop app clients a secret and its token endpoint rejects the
/// exchange without one (the parameter is only truly optional for Android/iOS/Chrome client types),
/// so <see cref="AppSettings.GoogleClientSecret"/> is sent too. Treat it as a required config value
/// rather than as a credential.</para>
///
/// <para><b>Loopback, not a custom URI scheme.</b> A custom scheme is a machine-wide registration any
/// other program can claim; loopback needs no registration, cannot be hijacked by another user's
/// session, and is what Google recommends for desktop. Port 0 lets the OS pick a free one, so two
/// copies of the app never collide -- Google explicitly allows any port on 127.0.0.1 for Desktop
/// clients, so this needs no per-port registration in the Cloud console.</para>
/// </summary>
public sealed class GoogleSignInClient : IGoogleSignInClient
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// openid is what makes this an OIDC request at all -- without it Google returns an access token
    /// and no id_token, and the whole exchange has nothing to send to vox. email is the claim the
    /// backend looks the user up by; profile supplies name/picture, which are optional there.
    /// </summary>
    private const string Scope = "openid email profile";

    /// <summary>
    /// How long to wait for the person to finish in the browser before giving up and freeing the port.
    ///
    /// <para>Generous on purpose: this covers typing a password, a 2FA prompt on a phone that has to
    /// be found first, and a consent screen. Timing out early on a student who is doing exactly what
    /// they were asked strands them at a login screen with no explanation. The listener is disposed
    /// on the way out either way, so an abandoned attempt costs nothing but the wait.</para>
    /// </summary>
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;

    public GoogleSignInClient(AppSettings settings, HttpClient httpClient)
    {
        _settings = settings;
        _httpClient = httpClient;
    }

    public async Task<string?> AcquireIdTokenAsync(CancellationToken cancellationToken = default)
    {
        var clientId = _settings.GoogleClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "GOOGLE_CLIENT_ID chưa được cấu hình. Đặt nó trong .env bằng client ID loại " +
                "'Desktop app' lấy từ Google Cloud Console.");
        }

        var codeVerifier = CreateCodeVerifier();
        var state = CreateRandomToken();

        using var listener = new HttpListener();
        var redirectUri = StartListener(listener);

        var authorizationUrl = BuildAuthorizationUrl(clientId, redirectUri, codeVerifier, state);
        OpenInBrowser(authorizationUrl);

        var code = await WaitForAuthorizationCodeAsync(listener, state, cancellationToken);
        if (code is null)
        {
            return null;
        }

        return await ExchangeCodeForIdTokenAsync(clientId, code, codeVerifier, redirectUri, cancellationToken);
    }

    /// <summary>
    /// Binds a loopback listener on a free port and returns the redirect URI that was actually taken.
    ///
    /// <para>Tries 127.0.0.1 first because that is what Google recommends for loopback redirects --
    /// "localhost" depends on name resolution, and on a machine where it resolves to ::1 first a
    /// listener bound only to IPv4 would never see the browser's callback.</para>
    ///
    /// <para>Falls back to "localhost" on <see cref="HttpListenerException"/>. Windows gates
    /// HttpListener prefixes behind URL ACLs, and while loopback is normally exempt, a machine with
    /// tightened http.sys reservations refuses the literal IP with "Access is denied" while still
    /// allowing the localhost prefix. Both forms are registered loopback redirect URIs as far as
    /// Google is concerned, so the fallback costs nothing -- and without it, sign-in would be dead on
    /// exactly the locked-down school machines this app is built to run on, with an error message
    /// pointing at nothing.</para>
    /// </summary>
    private static string StartListener(HttpListener listener)
    {
        // HttpListener has no "give me the port you got" API, so the port is claimed with a plain
        // socket first and then handed over. The socket is closed before the listener binds; the
        // window between them is a theoretical race with another process on this machine, which is
        // the same race every implementation of this flow accepts.
        var port = ClaimFreePort();

        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            var candidate = $"http://{host}:{port}/";
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(candidate);
                listener.Start();
                return candidate;
            }
            catch (HttpListenerException e)
            {
                LocalFileLogger.Error("login", "google_listener_bind_failed", e, new { prefix = candidate });
            }
        }

        throw new InvalidOperationException(
            "Không mở được cổng cục bộ để nhận phản hồi đăng nhập Google. " +
            "Hãy thử đăng nhập bằng mật khẩu.");
    }

    private static int ClaimFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string BuildAuthorizationUrl(string clientId, string redirectUri, string codeVerifier, string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = clientId;
        query["redirect_uri"] = redirectUri;
        query["response_type"] = "code";
        query["scope"] = Scope;
        query["code_challenge"] = CreateCodeChallenge(codeVerifier);
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        // Without this Google silently reuses the session already signed in to the browser, which on
        // a shared school machine hands the next student the previous student's account.
        query["prompt"] = "select_account";

        return $"{AuthorizationEndpoint}?{query}";
    }

    /// <summary>
    /// UseShellExecute is required: without it .NET tries to exec the URL as a program and throws.
    /// </summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            LocalFileLogger.Error("login", "google_browser_open_failed", e);
            throw new InvalidOperationException(
                "Không mở được trình duyệt để đăng nhập Google. Hãy thử đăng nhập bằng mật khẩu.", e);
        }
    }

    /// <summary>
    /// Waits for Google to redirect the browser back here, and validates the state parameter.
    ///
    /// <para>The state check is not ceremony. Without it, anything able to reach this loopback port
    /// could feed the app an authorization code obtained under a different account, and the app would
    /// exchange it and sign the student in as someone else -- with no visible sign anything was
    /// wrong. It is compared in constant time because the value is a secret for as long as this
    /// attempt lasts.</para>
    /// </summary>
    private async Task<string?> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowserTimeout);

        HttpListenerContext context;
        try
        {
            // GetContextAsync ignores cancellation tokens, so the wait is raced against one and the
            // listener is stopped to unblock it. Without this an abandoned login pins a thread and a
            // port until the process exits.
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
            if (completed != contextTask)
            {
                LocalFileLogger.Info("login", "google_browser_timeout");
                return null;
            }
            context = await contextTask;
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
            return null;
        }

        var query = context.Request.QueryString;
        var error = query["error"];
        var code = query["code"];
        var state = query["state"];

        // Answer the browser BEFORE deciding what to do: whatever happened, the person is staring at
        // a loading tab and deserves to be told to go back to the app.
        await WriteBrowserResponseAsync(context, error is null && code is not null);

        if (error is not null)
        {
            // access_denied is the user pressing Cancel -- a decision, not a fault.
            LocalFileLogger.Info("login", "google_consent_declined", new { error });
            return null;
        }

        if (!FixedTimeEquals(state, expectedState))
        {
            LocalFileLogger.Info("login", "google_state_mismatch");
            return null;
        }

        return code;
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerContext context, bool success)
    {
        var message = success
            ? "Đăng nhập thành công. Bạn có thể đóng tab này và quay lại ứng dụng VOX."
            : "Đăng nhập chưa hoàn tất. Bạn có thể đóng tab này và thử lại trong ứng dụng VOX.";

        var html = Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\">" +
            $"<title>VOX</title></head><body style=\"font-family:Segoe UI,sans-serif;padding:40px\">" +
            $"<p>{message}</p></body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = html.Length;
        await context.Response.OutputStream.WriteAsync(html);
        context.Response.Close();
    }

    private async Task<string> ExchangeCodeForIdTokenAsync(
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };

        // Sent despite PKCE. Google's token endpoint treats client_secret as optional only for
        // Android/iOS/Chrome client types -- a Desktop app client is issued one and is rejected
        // without it. Omitting it fails at THIS step, after the browser has already succeeded, which
        // makes it look like a bug in the exchange rather than a missing setting.
        //
        // Still conditional: a client type that genuinely has no secret must not send an empty one,
        // because Google reads a present-but-blank client_secret as a wrong secret.
        if (!string.IsNullOrWhiteSpace(_settings.GoogleClientSecret))
        {
            parameters["client_secret"] = _settings.GoogleClientSecret;
        }

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Google's reason goes in the MESSAGE, not just a log line. Everything here fails after
            // the browser succeeded, so "Google từ chối" on its own tells the person nothing and
            // tells whoever reads the log even less -- every distinct misconfiguration produces that
            // one identical sentence. invalid_client, redirect_uri_mismatch and invalid_grant each
            // need a completely different fix, and Google already names which one it is.
            throw new InvalidOperationException(DescribeTokenError(response.StatusCode, body));
        }

        var token = JsonSerializer.Deserialize<GoogleTokenResponse>(body, JsonOptions);
        if (string.IsNullOrWhiteSpace(token?.IdToken))
        {
            // Almost always a missing openid scope: Google answers 200 with an access token only.
            throw new InvalidOperationException(
                "Google không trả về id_token (thường do thiếu scope 'openid'). Vui lòng thử lại.");
        }

        return token.IdToken;
    }

    /// <summary>
    /// 32 random bytes, base64url with no padding -- inside RFC 7636's 43..128 character range.
    /// </summary>
    private static string CreateCodeVerifier() => CreateRandomToken();

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string CreateRandomToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string? left, string right) =>
        left is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// Turns Google's error payload into a sentence that names the actual fix.
    ///
    /// <para>The three that actually happen, and what each one means here:</para>
    /// <list type="bullet">
    /// <item><c>invalid_client</c> -- GOOGLE_CLIENT_SECRET missing or wrong for this client id;</item>
    /// <item><c>redirect_uri_mismatch</c> -- the client is a Web application type, which requires
    /// every loopback address to be pre-registered. Desktop app clients accept any 127.0.0.1 port;</item>
    /// <item><c>invalid_grant</c> -- the code was already redeemed or expired; just sign in again.</item>
    /// </list>
    /// </summary>
    private static string DescribeTokenError(System.Net.HttpStatusCode status, string body)
    {
        string? error = null;
        string? description = null;
        try
        {
            var payload = JsonSerializer.Deserialize<GoogleErrorResponse>(body, JsonOptions);
            error = payload?.Error;
            description = payload?.ErrorDescription;
        }
        catch (JsonException)
        {
            // Not JSON (a proxy error page, say). The raw body below is then the only clue there is.
        }

        var hint = error switch
        {
            "invalid_client" =>
                " Kiểm tra GOOGLE_CLIENT_SECRET trong .env: client loại 'Desktop app' bắt buộc phải có.",
            "redirect_uri_mismatch" =>
                " Client này nhiều khả năng là loại 'Web application'. Hãy tạo client loại 'Desktop app'.",
            "invalid_grant" => " Mã đăng nhập đã hết hạn hoặc đã dùng. Hãy thử đăng nhập lại.",
            _ => string.Empty
        };

        var detail = error is null
            ? (string.IsNullOrWhiteSpace(body) ? "(không có nội dung)" : body)
            : $"{error}{(description is null ? "" : $" - {description}")}";

        return $"Google từ chối yêu cầu đăng nhập ({(int)status}): {detail}.{hint}";
    }

    /// <summary>
    /// The attribute is required and PropertyNameCaseInsensitive does not cover it: that option
    /// ignores CASE, not the underscore, so "id_token" would never bind to IdToken and this would
    /// deserialize to null on a perfectly good response.
    /// </summary>
    private sealed record GoogleTokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("id_token")] string? IdToken);

    private sealed record GoogleErrorResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("error_description")] string? ErrorDescription);
}
