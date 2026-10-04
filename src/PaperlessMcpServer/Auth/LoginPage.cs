using System.Text;
using System.Text.Encodings.Web;

namespace PaperlessMcpServer.Auth;

/// <summary>Renders the sign-in / consent page shown during the OAuth authorization flow.</summary>
public static class LoginPage
{
    public sealed record Model(
        string SealedRequest,
        string ClientName,
        string RedirectHost,
        Uri? PreconfiguredUrl,
        string? Error = null,
        string? PrefillUrl = null,
        string? PrefillUsername = null,
        bool TokenModeOpen = false);

    public static string Render(Model model)
    {
        static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

        var body = new StringBuilder();
        body.Append($"""
            <main class="card">
              <div class="brand">
                <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 2h8l6 6v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2zm7 1.5V9h5.5L13 3.5zM8 13h8v1.5H8V13zm0 3.5h8V18H8v-1.5z"/></svg>
                <span>Paperless MCP</span>
              </div>
              <h1>Connect to Paperless</h1>
              <p class="lead"><strong>{E(model.ClientName)}</strong> wants to access your documents.
                After you sign in you will be returned to <code>{E(model.RedirectHost)}</code>.</p>
            """);

        if (model.Error is not null)
        {
            body.Append($"""<div class="error" role="alert">{E(model.Error)}</div>""");
        }

        if (model.PreconfiguredUrl is { } instance)
        {
            body.Append($"""
                <p class="instance">Paperless instance: <code>{E(instance.AbsoluteUri.TrimEnd('/'))}</code></p>
                <form method="post" action="authorize" autocomplete="on">
                  <input type="hidden" name="request" value="{E(model.SealedRequest)}">
                  <input type="hidden" name="mode" value="password">
                  <label for="username">Username</label>
                  <input id="username" name="username" autocomplete="username" required autofocus value="{E(model.PrefillUsername)}">
                  <label for="password">Password</label>
                  <input id="password" name="password" type="password" autocomplete="current-password" required>
                  <div class="actions">
                    <button type="submit" name="action" value="deny" class="secondary" formnovalidate>Deny</button>
                    <button type="submit" name="action" value="approve">Sign in &amp; allow</button>
                  </div>
                </form>
                <details{(model.TokenModeOpen ? " open" : "")}>
                  <summary>Sign in with an API token instead</summary>
                  <form method="post" action="authorize">
                    <input type="hidden" name="request" value="{E(model.SealedRequest)}">
                    <input type="hidden" name="mode" value="token">
                    <label for="token-alt">API token</label>
                    <input id="token-alt" name="token" type="password" autocomplete="off" required spellcheck="false">
                    <p class="hint">Useful if your account uses two-factor authentication. Find it in Paperless under <em>My Profile → API Auth Token</em>.</p>
                    <div class="actions">
                      <button type="submit" name="action" value="approve">Allow</button>
                    </div>
                  </form>
                </details>
                """);
        }
        else
        {
            body.Append($"""
                <form method="post" action="authorize" autocomplete="on">
                  <input type="hidden" name="request" value="{E(model.SealedRequest)}">
                  <input type="hidden" name="mode" value="token">
                  <label for="url">Paperless URL</label>
                  <input id="url" name="url" type="url" inputmode="url" placeholder="https://paperless.example.com" required autofocus value="{E(model.PrefillUrl)}">
                  <label for="token">API token</label>
                  <input id="token" name="token" type="password" autocomplete="off" required spellcheck="false">
                  <p class="hint">Find your token in Paperless under <em>My Profile → API Auth Token</em>. It is stored only inside the encrypted access token issued to {E(model.ClientName)}.</p>
                  <div class="actions">
                    <button type="submit" name="action" value="deny" class="secondary" formnovalidate>Deny</button>
                    <button type="submit" name="action" value="approve">Connect &amp; allow</button>
                  </div>
                </form>
                """);
        }

        body.Append("""
            <p class="foot">Only allow access for applications you trust. The application can read, change and delete documents your Paperless user has access to.</p>
            </main>
            """);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>Connect to Paperless</title>
            <style>
            :root { --bg:#f3f5f3; --card:#fff; --text:#1d2420; --muted:#5d6a62; --border:#d7ddd9; --accent:#17541f; --accent-text:#fff; --error-bg:#fdecec; --error:#9b1c1c; --input:#fff; }
            @media (prefers-color-scheme: dark) { :root { --bg:#121614; --card:#1b211e; --text:#e6ebe8; --muted:#9aa7a0; --border:#2d3631; --accent:#3f9a4b; --accent-text:#0b120d; --error-bg:#3a1a1a; --error:#f3b0b0; --input:#141917; } }
            * { box-sizing: border-box; }
            body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; padding:24px 16px; background:var(--bg); color:var(--text); font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
            .card { width:100%; max-width:420px; background:var(--card); border:1px solid var(--border); border-radius:14px; padding:28px; box-shadow:0 8px 30px rgba(0,0,0,.06); }
            .brand { display:flex; align-items:center; gap:8px; color:var(--accent); font-weight:600; font-size:14px; }
            .brand svg { width:22px; height:22px; fill:currentColor; }
            h1 { font-size:22px; margin:14px 0 6px; }
            .lead, .instance { color:var(--muted); margin:0 0 16px; }
            code { font-size:13px; background:rgba(127,127,127,.12); padding:1px 5px; border-radius:5px; overflow-wrap:anywhere; }
            label { display:block; font-weight:600; font-size:13px; margin:14px 0 6px; }
            input[type=url], input[type=password], input:not([type]) { width:100%; padding:10px 12px; border:1px solid var(--border); border-radius:8px; background:var(--input); color:var(--text); font:inherit; }
            input:focus { outline:2px solid var(--accent); outline-offset:1px; border-color:transparent; }
            .hint { color:var(--muted); font-size:13px; margin:8px 0 0; }
            .actions { display:flex; gap:10px; justify-content:flex-end; margin-top:20px; }
            button { font:inherit; font-weight:600; padding:9px 16px; border-radius:8px; border:1px solid var(--accent); background:var(--accent); color:var(--accent-text); cursor:pointer; }
            button.secondary { background:transparent; color:var(--text); border-color:var(--border); }
            .error { background:var(--error-bg); color:var(--error); border-radius:8px; padding:10px 12px; margin-bottom:8px; }
            details { margin-top:18px; border-top:1px solid var(--border); padding-top:12px; }
            summary { cursor:pointer; color:var(--muted); font-size:14px; }
            .foot { color:var(--muted); font-size:12px; margin:22px 0 0; }
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    public static string RenderError(string title, string message)
    {
        static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}}</title>
            <style>
            :root { --bg:#f3f5f3; --card:#fff; --text:#1d2420; --muted:#5d6a62; --border:#d7ddd9; }
            @media (prefers-color-scheme: dark) { :root { --bg:#121614; --card:#1b211e; --text:#e6ebe8; --muted:#9aa7a0; --border:#2d3631; } }
            body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; padding:24px 16px; background:var(--bg); color:var(--text); font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; }
            .card { max-width:420px; background:var(--card); border:1px solid var(--border); border-radius:14px; padding:28px; }
            h1 { font-size:20px; margin:0 0 8px; } p { color:var(--muted); margin:0; }
            </style>
            </head>
            <body><main class="card"><h1>{{E(title)}}</h1><p>{{E(message)}}</p></main></body>
            </html>
            """;
    }
}
