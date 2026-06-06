using System.Net;
using System.Web;

namespace PocketReader.Helpers;

public class OAuthHelper
{
    private const int CallbackPort = 8080;
    private const string CallbackUrl = "http://localhost:8080/callback";
    private string _authorizationCode;
    private TaskCompletionSource<string> _tcs;

    public string GetAuthorizationUrl(string clientId)
    {
        // Raindrop authorize endpoint is on raindrop.io (NOT app.raindrop.io).
        return $"https://raindrop.io/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(CallbackUrl)}&response_type=code";
    }

    public async Task<string> ListenForCallbackAsync()
    {
        _tcs = new TaskCompletionSource<string>();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");

        try
        {
            listener.Start();
            var contextTask = listener.GetContextAsync();
            var result = await Task.WhenAny(contextTask, _tcs.Task);

            if (result == _tcs.Task)
            {
                return await _tcs.Task;
            }

            var context = await contextTask;
            var query = HttpUtility.ParseQueryString(context.Request.Url.Query);
            _authorizationCode = query["code"];
            var error = query["error"];

            if (!string.IsNullOrEmpty(_authorizationCode))
            {
                SendResponseToClient(context, "Authorization successful! You can close this window.");
                return _authorizationCode;
            }
            else
            {
                SendResponseToClient(context, $"Authorization failed: {error}");
                throw new Exception($"OAuth error: {error}");
            }
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private void SendResponseToClient(HttpListenerContext context, string message)
    {
        using var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        var buffer = System.Text.Encoding.UTF8.GetBytes($@"
<!DOCTYPE html>
<html>
<head><title>PocketReader OAuth</title></head>
<body style='font-family:sans-serif;text-align:center;padding:50px;'>
    <h2>{message}</h2>
    <p>You can now close this window and return to PocketReader.</p>
</body>
</html>");
        response.OutputStream.Write(buffer, 0, buffer.Length);
    }

    public async Task<(string AccessToken, string RefreshToken)> ExchangeCodeForTokenAsync(
        string code, string clientId, string clientSecret)
    {
        using var client = new HttpClient();
        // Token endpoint is on raindrop.io and expects a JSON body (application/json).
        var tokenUrl = "https://raindrop.io/oauth/access_token";

        var payload = new
        {
            client_id = clientId,
            client_secret = clientSecret,
            code = code,
            redirect_uri = CallbackUrl,
            grant_type = "authorization_code"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(tokenUrl, content);
        var jsonResponse = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed ({(int)response.StatusCode}): {jsonResponse}");

        var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonResponse);
        var root = jsonDoc.RootElement;

        if (!root.TryGetProperty("access_token", out var at) || at.ValueKind == System.Text.Json.JsonValueKind.Null)
            throw new Exception($"No access_token in response: {jsonResponse}");

        var accessToken = at.GetString();
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        return (accessToken, refreshToken);
    }
}
