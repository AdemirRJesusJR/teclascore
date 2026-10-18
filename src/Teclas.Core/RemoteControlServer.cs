using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Teclas.Core;

/// <summary>
/// Turns a phone on the same Wi-Fi into a page-turning remote for the presentation. A tiny HTTP server answers only three
/// paths, all guarded by a random six-digit code: the remote page, the commands next/prev/nextsong/prevsong, the
/// current state (song and page, shown on the phone) and the image of the current page (for a tablet that shows the score). Anything else, or a wrong code, is refused. It runs only while the
/// user keeps the option on.
/// </summary>
public sealed record RemoteState(string Song, int Index, int Count, int Page, int Pages);

public sealed class RemoteControlServer : IDisposable
{
    public static readonly IReadOnlySet<string> Commands = new HashSet<string> { "next", "prev", "nextsong", "prevsong" };
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly Action<string> onCommand;
    private volatile RemoteState? state;
    private volatile string? pageImage;
    /// <summary>PNG of the current page, shown on a tablet or phone; set from the UI thread.</summary>
    public string? PageImage { get => pageImage; set => pageImage = value; }
    public string Code { get; }
    /// <summary>What the phone shows; set from the UI thread, read on the network thread.</summary>
    public RemoteState? State { get => state; set => state = value; }
    public int Port { get; }

    public RemoteControlServer(Action<string> onCommand, int port = 0, string? code = null)
    {
        this.onCommand = onCommand;
        Code = code ?? RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    /// <summary>Addresses to type on the phone (IPv4 of the active network adapters, Wi-Fi and Ethernet first).</summary>
    public IReadOnlyList<string> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 ? 0 : n.NetworkInterfaceType is NetworkInterfaceType.Ethernet ? 1 : 2)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Select(a => $"http://{a}:{Port}/?k={Code}").Distinct().ToArray();

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                var stream = client.GetStream();
                var buffer = new byte[2048]; int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                string request = Encoding.ASCII.GetString(buffer, 0, read);
                string line = request.Split("\r\n")[0];
                var parts = line.Split(' ');
                var (status, type, payload) = parts.Length >= 2 && parts[0] == "GET" ? Respond(parts[1]) : ("405 Method Not Allowed", "text/plain", []);
                string charset = type.StartsWith("image/", StringComparison.Ordinal) ? "" : "; charset=utf-8";
                string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}{charset}\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
            catch (Exception error) when (error is IOException or SocketException or TimeoutException or ObjectDisposedException) { }
        }
    }

    /// <summary>Like Answer, plus the image of the current page (/page.png, same code), as bytes.</summary>
    public (string Status, string Type, byte[] Body) Respond(string target)
    {
        if (target.Split('?')[0] == "/page.png")
        {
            var (status, type, body) = Answer(target.Replace("/page.png", "/state"));
            if (!status.StartsWith("200")) return (status, type, Encoding.UTF8.GetBytes(body));
            string? file = pageImage;
            if (file is null || !File.Exists(file) || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return ("404 Not Found", "text/plain", []);
            try { return ("200 OK", "image/png", File.ReadAllBytes(file)); }
            catch (IOException) { return ("503 Service Unavailable", "text/plain", []); }
        }
        var answer = Answer(target);
        return (answer.Status, answer.Type, Encoding.UTF8.GetBytes(answer.Body));
    }

    /// <summary>Routes one request target; public for tests.</summary>
    public (string Status, string Type, string Body) Answer(string target)
    {
        string path = target.Split('?')[0];
        var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1])) : [];
        if (!query.TryGetValue("k", out string? key) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(Code)))
            return ("403 Forbidden", "text/plain", "Código inválido.");
        if (path == "/") return ("200 OK", "text/html", Page());
        if (path == "/state")
        {
            var current = state;
            return ("200 OK", "application/json", current is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new
            { song = current.Song, index = current.Index, count = current.Count, page = current.Page, pages = current.Pages }));
        }
        if (path == "/cmd" && query.TryGetValue("a", out string? action) && Commands.Contains(action))
        {
            onCommand(action);
            return ("200 OK", "text/plain", "ok");
        }
        return ("404 Not Found", "text/plain", "");
    }

    private string Page() => $$"""
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>TeclaScore · controle</title><style>
        body{font-family:system-ui,sans-serif;margin:0;background:#1d2228;color:#f2f4f7;display:flex;flex-direction:column;height:100vh}
        h1{font-size:18px;margin:14px 16px 4px}#now{margin:0 16px;font-size:15px;opacity:1}#song{font-weight:600;font-size:20px;margin:0 16px}
        .grid{flex:1;display:grid;grid-template-columns:1fr 1fr;gap:12px;padding:12px}
        #score{display:none;flex:1;min-height:0;background:#fff;align-items:center;justify-content:center}#score img{max-width:100%;max-height:100%}
        body.view #score{display:flex}body.view .grid{flex:0 0 auto;grid-template-columns:repeat(4,1fr)}body.view button{font-size:18px;padding:12px 0}body.view button.main{grid-column:auto;font-size:18px}
        body.view h1,body.view p{display:none}#view{margin:0 16px 8px;background:#2f3640;color:#f2f4f7;border:0;border-radius:10px;font-size:15px;padding:8px}
        button{font-size:28px;border:0;border-radius:18px;background:#2f3640;color:#f2f4f7}button.main{background:#f2bd62;color:#191c22;grid-column:span 2;font-size:40px}button.wide{grid-column:span 2}body.view button.wide{grid-column:auto}
        p{margin:0 16px 14px;font-size:13px;opacity:.7}</style></head><body><h1>TeclaScore · apresentação</h1>
        <div id="song">…</div><p id="now"></p><button id="view" onclick="toggle()">Ver a partitura aqui</button>
        <div id="score" onclick="tap(event)"><img id="pg" alt="Página atual"></div><div class="grid">
        <button class="main" onclick="send('next')">Página ›</button><button class="wide" onclick="send('prev')">‹ Página</button>
        <button onclick="send('prevsong')">‹ Música</button><button onclick="send('nextsong')">Música ›</button></div>
        <p id="s">Toque para virar as páginas no computador.</p>
        <script>
        var last='';
        function toggle(){document.body.classList.toggle('view');document.getElementById('view').textContent=document.body.classList.contains('view')?'Só os botões':'Ver a partitura aqui';last='';show()}
        function tap(e){var r=e.currentTarget.getBoundingClientRect();send(e.clientX-r.left>r.width/2?'next':'prev')}
        function show(){fetch('/state?k={{Code}}').then(r=>r.json()).then(s=>{if(!s.song)return;
          var key=s.index+'-'+s.page;if(document.body.classList.contains('view')&&key!==last){last=key;document.getElementById('pg').src='/page.png?k={{Code}}&v='+key+'-'+Date.now()}
          document.getElementById('song').textContent=s.song;
          document.getElementById('now').textContent='Música '+s.index+' de '+s.count+(s.pages>0?' · página '+s.page+' de '+s.pages:'');}).catch(()=>{})}
        function send(a){fetch('/cmd?a='+a+'&k={{Code}}').then(r=>{document.getElementById('s').textContent=r.ok?'Enviado':'Recusado';setTimeout(show,250)}).catch(()=>document.getElementById('s').textContent='Sem conexão')}
        show();setInterval(show,2000);
        </script>
        </body></html>
        """;

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); } catch (SocketException) { }
        stop.Dispose();
    }
}
