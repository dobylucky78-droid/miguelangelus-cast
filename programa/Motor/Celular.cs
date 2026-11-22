// (0.1.20) Controle do Cast pelo celular: um servidor pequeno na rede local (Wi-Fi da igreja), com pareamento próprio.
//  GET  /                  página do controle (web\celular.html)
//  POST /api/parear        {codigo, nome} → {token}   (código de 4 números que aparece em Ajustes › MiguelAngelus e celulares)
//  GET  /api/estado?t=     o que está no Programa e no Preview, o momento do roteiro, live e gravação
//  POST /api/cmd?t=        {cmd}: proximo, anterior, transicao (só esses, por enquanto)
// Vem DESLIGADO. Só celulares pareados mandam comandos; os pareamentos ficam em celular.json (fora do backup e da nuvem).
// 5 códigos errados: código novo e 1 minuto sem aceitar pareamento. Cada código serve para um celular só.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public static class Celular
    {
        public const int PortaPadrao = 8941;
        // os comandos que o celular pode mandar (o resto dos botões do Cast fica só no computador, por enquanto)
        static readonly HashSet<string> permitidos = new HashSet<string> { "proximo", "anterior", "transicao" };
        static readonly string arquivo = Path.Combine(Program.PastaDados, "celular.json");

        public sealed class Aparelho { public string Token { get; set; } = ""; public string Nome { get; set; } = "Celular"; public long Desde { get; set; } public long Visto { get; set; } }
        sealed class Cfg { public bool Ligado { get; set; } public int Porta { get; set; } = PortaPadrao; public List<Aparelho> Aparelhos { get; set; } = new List<Aparelho>(); }

        static Cfg cfg = new Cfg();
        static readonly object trava = new object();
        static TcpListener ouvinte;
        static int erros;
        static DateTime travadoAte = DateTime.MinValue;

        public static bool Ligado => cfg.Ligado;
        public static int Porta { get; private set; }
        public static string Codigo { get; private set; } = "";
        public static string Erro { get; private set; } = "";
        public static event Action Mudou;          // pareou, tirou, ligou/desligou: a tela de Ajustes se atualiza

        // o mesmo jeito dos botões do Stream Deck: a tela roda o comando e devolve o texto ("!" = não fez)
        public static Func<string, Dictionary<string, string>, string> Executar;
        public static Func<object> Estado;

        public static void Iniciar()
        {
            try { if (File.Exists(arquivo)) cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(arquivo)) ?? new Cfg(); } catch { cfg = new Cfg(); }
            cfg.Aparelhos ??= new List<Aparelho>();
            if (cfg.Ligado) Abrir();
        }

        static void Salvar()
        {
            try { lock (trava) File.WriteAllText(arquivo, JsonSerializer.Serialize(cfg)); }
            catch (Exception e) { Registro.Erro("celular.json", e); }
        }

        public static void Ligar(bool sim)
        {
            cfg.Ligado = sim; Salvar();
            if (sim) Abrir(); else Fechar();
            Mudou?.Invoke();
        }

        static void Abrir()
        {
            if (ouvinte != null) return;
            Erro = "";
            NovoCodigo();
            for (int p = cfg.Porta; p < cfg.Porta + 10; p++)
            {
                // testes numa cópia (CAST_CELULAR_LOCAL=1): só no próprio computador, sem o aviso do Firewall
                var ip = Environment.GetEnvironmentVariable("CAST_CELULAR_LOCAL") == "1" ? IPAddress.Loopback : IPAddress.Any;
                try { ouvinte = new TcpListener(ip, p); ouvinte.Start(); Porta = p; break; }
                catch (SocketException) { ouvinte = null; }
            }
            if (ouvinte == null) { Erro = "porta " + cfg.Porta + " ocupada"; Registro.Info("celular: " + Erro); return; }
            if (cfg.Porta != Porta) { cfg.Porta = Porta; Salvar(); }
            Registro.Info("controle pelo celular ligado na porta " + Porta);
            var o = ouvinte;
            new Thread(() => Laco(o)) { IsBackground = true, Name = "celular" }.Start();
        }

        static void Fechar()
        {
            try { ouvinte?.Stop(); } catch { }
            ouvinte = null; Porta = 0; Codigo = "";
            Registro.Info("controle pelo celular desligado");
        }
        public static void Parar() { try { ouvinte?.Stop(); } catch { } }

        static void NovoCodigo() { Codigo = RandomNumberGenerator.GetInt32(0, 10000).ToString("0000"); Interlocked.Exchange(ref erros, 0); }

        public static List<Aparelho> Aparelhos() { lock (trava) return cfg.Aparelhos.Select(a => new Aparelho { Token = a.Token.Substring(0, 8), Nome = a.Nome, Desde = a.Desde, Visto = a.Visto }).ToList(); }
        public static void Tirar(string inicio)
        {
            lock (trava) cfg.Aparelhos.RemoveAll(a => a.Token.StartsWith(inicio ?? "\0", StringComparison.Ordinal));
            Salvar(); Mudou?.Invoke();
        }

        // endereços deste computador na rede (para o QR code e para digitar no celular)
        public static List<string> Enderecos()
        {
            var l = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var d = ni.Description.ToLowerInvariant();
                    if (d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vpn") || d.Contains("tap-")) continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address) && !u.Address.ToString().StartsWith("169.254."))
                            l.Add(u.Address.ToString());
                }
            }
            catch { }
            // a rede da igreja costuma ser 192.168.x.x: vem primeiro
            return l.Distinct().OrderBy(a => a.StartsWith("192.168.") ? 0 : a.StartsWith("10.") ? 1 : 2).ToList();
        }

        static void Laco(TcpListener o)
        {
            while (ouvinte == o)
            {
                TcpClient cli;
                try { cli = o.AcceptTcpClient(); } catch { return; }
                _ = Task.Run(() => Atender(cli));
            }
        }

        static async Task Atender(TcpClient cli)
        {
            using (cli)
            {
                try
                {
                    cli.ReceiveTimeout = 5000; cli.SendTimeout = 5000;
                    var s = cli.GetStream();
                    var (metodo, alvo, corpo) = Ler(s);
                    if (metodo == null) return;
                    int iq = alvo.IndexOf('?');
                    var caminho = Uri.UnescapeDataString(iq < 0 ? alvo : alvo.Substring(0, iq));
                    var q = iq < 0 ? "" : alvo.Substring(iq + 1);
                    string token = null;
                    foreach (var par in q.Split('&')) if (par.StartsWith("t=")) token = Uri.UnescapeDataString(par.Substring(2));

                    if (metodo == "GET" && (caminho == "/" || caminho == "/index.html"))
                    {
                        var p = Path.Combine(Program.Pasta, "web", "celular.html");
                        Responder(s, 200, "text/html; charset=utf-8", File.Exists(p) ? File.ReadAllBytes(p) : Encoding.UTF8.GetBytes("Página do celular não encontrada."));
                        return;
                    }
                    // o ícone do Cast (aba do navegador e atalho na tela inicial do celular)
                    if (metodo == "GET" && arquivos.TryGetValue(caminho, out var arq))
                    {
                        if (arq.tipo.StartsWith("application/manifest")) { Responder(s, 200, arq.tipo, Encoding.UTF8.GetBytes(Manifesto)); return; }
                        var p = Path.Combine(Program.Pasta, "web", "img", arq.nome);
                        if (File.Exists(p)) Responder(s, 200, arq.tipo, File.ReadAllBytes(p)); else Responder(s, 404, "text/plain", Encoding.UTF8.GetBytes("-"));
                        return;
                    }
                    if (caminho == "/api/parear" && metodo == "POST")
                    {
                        if (DateTime.UtcNow < travadoAte) { Json(s, 429, new { ok = false, motivo = "espera" }); return; }
                        string cod = "", nome = "";
                        try { using var d = JsonDocument.Parse(corpo); cod = Str(d.RootElement, "codigo").Trim(); nome = Str(d.RootElement, "nome").Trim(); } catch { }
                        if (cod.Length != 4 || cod != Codigo)
                        {
                            if (Interlocked.Increment(ref erros) >= 5) { NovoCodigo(); travadoAte = DateTime.UtcNow.AddMinutes(1); Mudou?.Invoke(); }
                            Json(s, 403, new { ok = false }); return;
                        }
                        if (nome.Length > 40) nome = nome.Substring(0, 40);
                        var novo = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                        var agora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        lock (trava) cfg.Aparelhos.Add(new Aparelho { Token = novo, Nome = nome == "" ? "Celular" : nome, Desde = agora, Visto = agora });
                        Salvar();
                        NovoCodigo();              // cada código serve para um celular só
                        Registro.Info("celular pareado: " + (nome == "" ? "Celular" : nome));
                        Mudou?.Invoke();
                        Json(s, 200, new { ok = true, token = novo });
                        return;
                    }
                    Aparelho ap = null;
                    if (!string.IsNullOrEmpty(token)) lock (trava) ap = cfg.Aparelhos.FirstOrDefault(a => a.Token == token);
                    if (caminho.StartsWith("/api/") && ap == null) { Json(s, 401, new { ok = false, motivo = "pareamento" }); return; }
                    if (ap != null)
                    {
                        var antes = ap.Visto; ap.Visto = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (ap.Visto - antes > 60000) Mudou?.Invoke();       // "visto agora" na tela de Ajustes, sem inundar
                    }
                    if (caminho == "/api/estado" && metodo == "GET")
                    {
                        var est = await NaTela(() => Estado?.Invoke());
                        Responder(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = est != null, nome = ap.Nome, cast = est })));
                        return;
                    }
                    if (caminho == "/api/cmd" && metodo == "POST")
                    {
                        string cmd = "";
                        try { using var d = JsonDocument.Parse(corpo); cmd = Str(d.RootElement, "cmd").Trim().ToLowerInvariant(); } catch { }
                        if (!permitidos.Contains(cmd)) { Json(s, 400, new { ok = false, msg = "comando não existe" }); return; }
                        var q2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["origem"] = "celular", ["aparelho"] = ap.Nome };
                        var resp = await NaTela(() => Executar?.Invoke(cmd, q2) ?? "!Cast ainda abrindo") as string;
                        bool ok = resp != null && !resp.StartsWith("!");
                        Json(s, 200, new { ok, msg = (resp ?? "o Cast não respondeu").TrimStart('!') });
                        return;
                    }
                    Responder(s, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("-"));
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (Exception e) { Registro.Erro("celular", e); }
            }
        }

        static readonly Dictionary<string, (string nome, string tipo)> arquivos = new Dictionary<string, (string, string)>
        {
            ["/icone.svg"] = ("icone.svg", "image/svg+xml"), ["/favicon.ico"] = ("icone-192.png", "image/png"),
            ["/icone-192.png"] = ("icone-192.png", "image/png"), ["/icone-512.png"] = ("icone-512.png", "image/png"),
            ["/manifest.webmanifest"] = ("", "application/manifest+json"),
        };
        const string Manifesto = "{\"name\":\"MiguelAngelus Cast\",\"short_name\":\"Cast\",\"start_url\":\"/\",\"display\":\"standalone\"," +
            "\"background_color\":\"#0f1420\",\"theme_color\":\"#0f1420\",\"icons\":[{\"src\":\"/icone-192.png\",\"sizes\":\"192x192\",\"type\":\"image/png\"}," +
            "{\"src\":\"/icone-512.png\",\"sizes\":\"512x512\",\"type\":\"image/png\"},{\"src\":\"/icone.svg\",\"sizes\":\"any\",\"type\":\"image/svg+xml\"}]}";

        static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static async Task<object> NaTela(Func<object> f)
        {
            if (Controle.NaTelaFn == null) return null;
            var t = Controle.NaTelaFn(f);
            var fim = await Task.WhenAny(t, Task.Delay(8000));
            return fim == t ? t.Result : null;
        }

        static (string, string, string) Ler(Stream s)
        {
            var buf = new byte[16384]; int n = 0, fim = -1;
            while (fim < 0)
            {
                if (n == buf.Length) return (null, null, null);
                int r = s.Read(buf, n, buf.Length - n); if (r <= 0) return (null, null, null);
                n += r;
                for (int i = Math.Max(0, n - r - 3); i + 3 < n; i++) if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) { fim = i; break; }
            }
            var linhas = Encoding.ASCII.GetString(buf, 0, fim).Split("\r\n");
            var partes = linhas[0].Split(' ');
            if (partes.Length < 2) return (null, null, null);
            int tam = 0;
            foreach (var l in linhas.Skip(1))
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out tam);
            if (tam < 0 || tam > 4096) return (null, null, null);
            var corpo = new byte[tam];
            int ja = Math.Min(tam, n - fim - 4);
            Array.Copy(buf, fim + 4, corpo, 0, ja);
            while (ja < tam) { int r = s.Read(corpo, ja, tam - ja); if (r <= 0) break; ja += r; }
            return (partes[0], partes[1], Encoding.UTF8.GetString(corpo, 0, ja));
        }

        static void Json(Stream s, int cod, object o) => Responder(s, cod, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));
        static void Responder(Stream s, int cod, string tipo, byte[] b)
        {
            var txt = cod == 200 ? "OK" : cod == 400 ? "Bad Request" : cod == 401 ? "Unauthorized" : cod == 403 ? "Forbidden" : cod == 404 ? "Not Found" : cod == 429 ? "Too Many Requests" : "Error";
            var h = $"HTTP/1.1 {cod} {txt}\r\nContent-Type: {tipo}\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            s.Write(Encoding.ASCII.GetBytes(h)); s.Write(b); s.Flush();
        }
    }
}
