using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiveSlides.App;

// Piccolo server HTTP per il controllo remoto (Bitfocus Companion / Stream Deck).
// Usa TcpListener invece di HttpListener: ascolta in rete senza permessi di amministratore
// (stessa scelta fatta in LiveCount - vedi quel progetto per il precedente).
//
//   GET /api/start        apre l'uscita e parte dalla prima slide
//   GET /api/stop         chiude l'uscita
//   GET /api/next         slide successiva
//   GET /api/prev         slide precedente
//   GET /api/first        prima slide della scaletta
//   GET /api/last         ultima slide della scaletta
//   GET /api/goto?deck=2&slide=5   slide 5 della presentazione 2 (numeri da 1)
//   GET /api/tappo        mostra/nasconde l'immagine tappo
//   GET /api/status       stato in JSON
public class RemoteApi : IDisposable
{
    // Riceve percorso e parametri, restituisce il JSON di risposta (o null se il comando non esiste).
    private readonly Func<string, Dictionary<string, string>, string?> _handler;
    private TcpListener? _listener;
    private Thread? _thread;
    private volatile bool _stop;

    public int Port { get; private set; }
    public string? Error { get; private set; }

    public RemoteApi(Func<string, Dictionary<string, string>, string?> handler)
    {
        _handler = handler;
    }

    public bool Start(int port)
    {
        Stop();
        Port = port;
        Error = null;
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            Error = $"Porta {port} non disponibile: {ex.Message}";
            return false;
        }
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "LiveSlides Remote API" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _stop = true;
        if (_listener is not null) { try { _listener.Stop(); } catch { /* already stopped */ } _listener = null; }
        _thread = null;
    }

    private void Loop()
    {
        var listener = _listener;
        while (!_stop && listener is not null)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine)) return;
                // Le intestazioni non servono: si leggono solo per svuotare la richiesta.
                while (!string.IsNullOrEmpty(reader.ReadLine())) { }

                var parts = requestLine.Split(' ');
                var target = parts.Length > 1 ? parts[1] : "/";
                string path = target, query = "";
                var q = target.IndexOf('?');
                if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
                path = Uri.UnescapeDataString(path).TrimEnd('/').ToLowerInvariant();

                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in query.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var k = eq >= 0 ? pair[..eq] : pair;
                    var v = eq >= 0 ? pair[(eq + 1)..] : "";
                    args[Unescape(k)] = Unescape(v);
                }

                string? json = null;
                if (path.StartsWith("/api")) json = _handler(path[4..].TrimStart('/'), args);
                if (json is null) Write(stream, 404, "{\"ok\":false,\"error\":\"comando sconosciuto\"}");
                else Write(stream, 200, json);
            }
            catch { /* client disconnesso o richiesta malformata: nessuna risposta */ }
        }
    }

    private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static void Write(Stream stream, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   "Access-Control-Allow-Origin: *\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n" +
                   $"Content-Length: {body.Length}\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        stream.Write(h, 0, h.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>Indirizzi IPv4 di rete locale di questo PC, per configurare Companion da un altro computer.</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                        list.Add(a.Address.ToString());
            }
        }
        catch { /* best effort: nessun indirizzo trovato non è fatale */ }
        return list;
    }

    public static string JsonString(string? s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public void Dispose() => Stop();
}
