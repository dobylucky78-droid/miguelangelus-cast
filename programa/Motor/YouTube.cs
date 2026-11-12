// Conta do YouTube (0.0.8): cria a live agendada com a capa como miniatura, direto pela API do YouTube (Data API v3).
//  - Login: navegador do sistema + retorno em 127.0.0.1 (OAuth para apps de computador, com PKCE), igual ao Google Drive do MiguelAngelus.
//  - O ID do cliente vem do JSON baixado do Google Cloud ("App para computador"); pode ser o mesmo projeto do MiguelAngelus,
//    com a "YouTube Data API v3" ativada.
//  - Cliente, segredo do cliente e acesso (refresh token) ficam criptografados pelo Windows (DPAPI) em youtube.dat,
//    só neste computador e nesta conta; nunca vão para backup, registro ou tela.
//  - A live é ligada a uma chave própria do Cast ("MiguelAngelus Cast", reutilizável), que vai sozinha para o destino YouTube.
//    Início e fim automáticos: entra no ar quando o Cast começa a mandar o vídeo e termina quando ele para.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class LiveYt
    {
        public string Id = "", Titulo = "", Privacidade = "", Estado = "", Miniatura = "";
        public DateTime Inicio;   // hora local (a agendada)
        public bool DoCast;       // ligada à chave "MiguelAngelus Cast" (as do OBS/Studio usam outra chave)
        public bool AutoInicio;   // criada na 0.0.8: entra no ar sozinha quando o sinal chega
        public string Link => "https://www.youtube.com/watch?v=" + Id;
        public string Studio => "https://studio.youtube.com/video/" + Id + "/livestreaming";
    }

    public static class YouTube
    {
        const string Escopo = "https://www.googleapis.com/auth/youtube";
        const string Api = "https://www.googleapis.com/youtube/v3/";
        const string NomeChave = "MiguelAngelus Cast";
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        static readonly string arquivo = Path.Combine(Program.PastaDados, "youtube.dat");
        static readonly object trava = new object();

        static string clienteId, clienteSegredo, refreshToken, accessToken;
        static DateTime expira;
        public static string Canal { get; private set; } = "";
        public static string Erro { get; set; } = "";
        public static List<LiveYt> Lives { get; private set; } = new List<LiveYt>();
        public static DateTime Lido { get; private set; }

        public static bool TemCliente { get { Carregar(); return !string.IsNullOrEmpty(clienteId); } }
        public static bool Conectado { get { Carregar(); return !string.IsNullOrEmpty(refreshToken); } }

        // ---------- credenciais (DPAPI) ----------
        static bool carregado;
        static void Carregar()
        {
            lock (trava)
            {
                if (carregado) return; carregado = true;
                try
                {
                    if (!File.Exists(arquivo)) return;
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(Cofre.Proteger(File.ReadAllBytes(arquivo), false)));
                    d.TryGetValue("cliente", out clienteId); d.TryGetValue("segredo", out clienteSegredo); d.TryGetValue("refresh", out refreshToken);
                    d.TryGetValue("canal", out var c); Canal = c ?? "";
                }
                catch (Exception e) { Registro.Info("youtube.dat ilegível (outro computador ou outra conta?): " + e.Message); }
            }
        }
        static void Guardar()
        {
            lock (trava)
            {
                var d = new Dictionary<string, string> { ["cliente"] = clienteId ?? "", ["segredo"] = clienteSegredo ?? "", ["refresh"] = refreshToken ?? "", ["canal"] = Canal ?? "" };
                File.WriteAllBytes(arquivo, Cofre.Proteger(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)), true));
            }
        }

        // JSON do Google Cloud: {"installed": {"client_id": ..., "client_secret": ...}}
        public static void DefinirCliente(string textoJson)
        {
            Carregar();
            var raiz = JsonNode.Parse(textoJson);
            var inst = raiz?["installed"] ?? raiz?["web"] ?? raiz;
            var id = (string)inst?["client_id"];
            var seg = (string)inst?["client_secret"];
            if (string.IsNullOrEmpty(id) || !id.EndsWith(".apps.googleusercontent.com")) throw new InvalidOperationException("este arquivo não é um ID do cliente OAuth do Google");
            if (id != clienteId) { refreshToken = null; accessToken = null; Canal = ""; }
            clienteId = id; clienteSegredo = seg;
            Guardar();
        }

        public static async Task Desconectar()
        {
            Carregar();
            var rt = refreshToken;
            refreshToken = null; accessToken = null; Canal = ""; Lives = new List<LiveYt>(); Erro = ""; idChave = null;
            Guardar();
            // tira a permissão também do lado do Google (se falhar, o usuário pode tirar em myaccount.google.com/permissions)
            if (!string.IsNullOrEmpty(rt))
                try { await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = rt })); } catch { }
        }

        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static async Task Conectar()
        {
            Carregar();
            if (string.IsNullOrEmpty(clienteId)) throw new InvalidOperationException("carregue primeiro o arquivo do ID do cliente (JSON do Google Cloud)");
            // (0.0.9) um clique novo em Conectar desiste do login anterior que ficou esperando no navegador
            var cancelar = new System.Threading.CancellationTokenSource();
            System.Threading.Interlocked.Exchange(ref loginPendente, cancelar)?.Cancel();
            var ouvinte = new TcpListener(IPAddress.Loopback, 0);
            ouvinte.Start();
            try
            {
                var porta = ((IPEndPoint)ouvinte.LocalEndpoint).Port;
                var retorno = $"http://127.0.0.1:{porta}/";
                var verificador = B64Url(RandomNumberGenerator.GetBytes(32));
                var desafio = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));
                var estado = Guid.NewGuid().ToString("N");
                var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
                          "&client_id=" + Uri.EscapeDataString(clienteId) + "&redirect_uri=" + Uri.EscapeDataString(retorno) +
                          "&scope=" + Uri.EscapeDataString(Escopo) + "&code_challenge=" + desafio + "&code_challenge_method=S256&state=" + estado;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                var aceitar = ouvinte.AcceptTcpClientAsync();
                var espera = Task.Delay(TimeSpan.FromMinutes(5), cancelar.Token);
                if (await Task.WhenAny(aceitar, espera) != aceitar)
                {
                    if (cancelar.IsCancellationRequested) throw new OperationCanceledException();
                    throw new TimeoutException("o login no navegador não foi concluído (a conexão anterior continua valendo)");
                }
                string codigo = null, erro = null;
                using (var cli = aceitar.Result)
                using (var fluxo = cli.GetStream())
                {
                    var buf = new byte[8192]; var n = await fluxo.ReadAsync(buf, 0, buf.Length);
                    var linha = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0];   // GET /?state=..&code=.. HTTP/1.1
                    var consulta = linha.Split(' ').ElementAtOrDefault(1) ?? "";
                    var q = consulta.Contains('?') ? consulta[(consulta.IndexOf('?') + 1)..].Split('&')
                        .Select(p => p.Split('=')).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1]) : "") : new Dictionary<string, string>();
                    q.TryGetValue("code", out codigo); q.TryGetValue("error", out erro);
                    if (!q.TryGetValue("state", out var est) || est != estado) { codigo = null; erro ??= "resposta inválida"; }
                    var html = codigo != null
                        ? "<h2 style='font-family:Segoe UI'>&#10004; MiguelAngelus Cast conectado ao YouTube.</h2><p style='font-family:Segoe UI'>Pode fechar esta aba e voltar ao Cast.</p>"
                        : "<h2 style='font-family:Segoe UI'>N&atilde;o foi poss&iacute;vel conectar.</h2><p style='font-family:Segoe UI'>" + WebUtility.HtmlEncode(erro ?? "") + "</p>";
                    var corpo = Encoding.UTF8.GetBytes(html);
                    var cab = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {corpo.Length}\r\nConnection: close\r\n\r\n");
                    await fluxo.WriteAsync(cab); await fluxo.WriteAsync(corpo);
                }
                if (codigo == null) throw new InvalidOperationException("o Google recusou: " + erro);
                var tok = await Token(new Dictionary<string, string>
                {
                    ["code"] = codigo, ["client_id"] = clienteId, ["client_secret"] = clienteSegredo ?? "",
                    ["redirect_uri"] = retorno, ["grant_type"] = "authorization_code", ["code_verifier"] = verificador
                });
                refreshToken = (string)tok["refresh_token"];
                if (string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("o Google não devolveu o acesso permanente (tente de novo)");
                Guardar();
                await LerCanal();
                Registro.Info("YouTube conectado: " + Canal);
            }
            finally { ouvinte.Stop(); System.Threading.Interlocked.CompareExchange(ref loginPendente, null, cancelar); }
        }
        static System.Threading.CancellationTokenSource loginPendente;
        public static bool Entrando => loginPendente != null;

        static async Task<JsonNode> Token(Dictionary<string, string> campos)
        {
            var r = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(campos));
            var texto = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
            {
                if (texto.Contains("invalid_grant")) { refreshToken = null; Guardar(); throw new InvalidOperationException("o acesso ao YouTube expirou: clique em Conectar de novo"); }
                string desc = null; try { desc = (string)JsonNode.Parse(texto)?["error_description"]; } catch { }
                throw new InvalidOperationException("Google: " + (desc ?? texto));
            }
            var d = JsonNode.Parse(texto);
            accessToken = (string)d["access_token"];
            expira = DateTime.UtcNow.AddSeconds((double)d["expires_in"] - 60);
            return d;
        }

        static async Task<string> Acesso()
        {
            Carregar();
            if (string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("YouTube não conectado (Ajustes › Transmissão › Conta do YouTube)");
            if (accessToken == null || DateTime.UtcNow >= expira)
                await Token(new Dictionary<string, string> { ["client_id"] = clienteId, ["client_secret"] = clienteSegredo ?? "", ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" });
            return accessToken;
        }

        static async Task<JsonNode> Chamar(HttpMethod metodo, string url, object corpo = null, byte[] bin = null, string tipoBin = null)
        {
            for (int tentativa = 0; ; tentativa++)
            {
                var req = new HttpRequestMessage(metodo, url.StartsWith("http") ? url : Api + url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Acesso());
                if (corpo != null) req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
                if (bin != null) { req.Content = new ByteArrayContent(bin); req.Content.Headers.ContentType = new MediaTypeHeaderValue(tipoBin); }
                var r = await http.SendAsync(req);
                var texto = await r.Content.ReadAsStringAsync();
                if (r.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto);
                if (((int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.Unauthorized) && tentativa < 2)
                {
                    if (r.StatusCode == HttpStatusCode.Unauthorized) accessToken = null;
                    await Task.Delay(800 * (tentativa + 1));
                    continue;
                }
                throw new InvalidOperationException(Explicar(texto, (int)r.StatusCode));
            }
        }

        // erros do YouTube em português, para os casos que a gente conhece
        static string Explicar(string texto, int status)
        {
            string motivo = "", msg = "";
            try { var e = JsonNode.Parse(texto)?["error"]; msg = (string)e?["message"] ?? ""; motivo = (string)e?["errors"]?[0]?["reason"] ?? ""; } catch { }
            return motivo switch
            {
                "liveStreamingNotEnabled" => "a transmissão ao vivo não está ativada neste canal (YouTube Studio › Transmitir ao vivo)",
                "insufficientPermissions" or "forbidden" when msg.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) => "o canal não pode enviar miniaturas personalizadas (precisa estar verificado)",
                "insufficientPermissions" => "faltou permissão: desconecte e conecte de novo, marcando o acesso ao YouTube",
                "quotaExceeded" => "a cota diária da API do YouTube acabou; amanhã volta",
                "accessNotConfigured" or "SERVICE_DISABLED" => "a YouTube Data API v3 não está ativada no projeto do Google Cloud",
                "invalidScheduledStartTime" => "a data e a hora da capa já passaram",
                "mediaBodyTooLarge" => "a miniatura passou de 2 MB",
                _ => $"YouTube ({status}){(motivo != "" ? " " + motivo : "")}: {(msg != "" ? msg : texto)}"
            };
        }

        static async Task LerCanal()
        {
            var d = await Chamar(HttpMethod.Get, "channels?part=snippet&mine=true");
            Canal = (string)d?["items"]?[0]?["snippet"]?["title"] ?? "(conta sem canal)";
            Guardar();
        }

        // Próximas lives do canal (as criadas pelo Cast, pelo OBS ou pelo Studio)
        public static async Task Atualizar()
        {
            if (!Conectado) return;
            if (Canal == "") await LerCanal();
            var l = new List<LiveYt>();
            var minha = await IdChaveCast();
            foreach (var st in new[] { "active", "upcoming" })
            {
                var d = await Chamar(HttpMethod.Get, "liveBroadcasts?part=snippet,status,contentDetails&broadcastType=all&maxResults=25&broadcastStatus=" + st);
                foreach (var it in d?["items"]?.AsArray() ?? new JsonArray())
                {
                    var sn = it["snippet"];
                    DateTime.TryParse((string)sn?["scheduledStartTime"] ?? (string)sn?["actualStartTime"], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ini);
                    var cd = it["contentDetails"];
                    l.Add(new LiveYt
                    {
                        Id = (string)it["id"], Titulo = (string)sn?["title"] ?? "", Inicio = ini == default ? default : ini.ToLocalTime(),
                        Privacidade = (string)it["status"]?["privacyStatus"] ?? "", Estado = (string)it["status"]?["lifeCycleStatus"] ?? st,
                        Miniatura = (string)(sn?["thumbnails"]?["medium"]?["url"] ?? sn?["thumbnails"]?["default"]?["url"]) ?? "",
                        DoCast = minha != null && (string)cd?["boundStreamId"] == minha,
                        AutoInicio = (bool?)cd?["enableAutoStart"] == true
                    });
                }
            }
            Lives = l.OrderBy(x => x.Inicio).ToList();
            Lido = DateTime.Now; Erro = "";
        }

        // A live que é desta celebração: começa até 30 min antes ou depois da hora da capa
        public static LiveYt DaHora(DateTime quando) => Lives.Where(x => Math.Abs((x.Inicio - quando).TotalMinutes) <= 30)
            .OrderBy(x => Math.Abs((x.Inicio - quando).TotalMinutes)).FirstOrDefault();

        static string Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        // Cria a live agendada, manda a miniatura e liga a chave do Cast. Devolve a live e (servidor, chave) para o destino YouTube.
        public static async Task<(LiveYt live, string servidor, string chave, string aviso)> Criar(string titulo, string descricao, DateTime quando, string privacidade, byte[] miniaturaJpeg)
        {
            titulo = Limpar(titulo, 100); descricao = Limpar(descricao, 5000);
            if (titulo == "") throw new InvalidOperationException("falta o título");
            if (quando < DateTime.Now.AddMinutes(1)) quando = DateTime.Now.AddMinutes(2);
            var b = await Chamar(HttpMethod.Post, "liveBroadcasts?part=snippet,status,contentDetails", new
            {
                snippet = new { title = titulo, description = descricao, scheduledStartTime = Utc(quando) },
                status = new { privacyStatus = privacidade, selfDeclaredMadeForKids = false },
                // (0.1.0) igual ao OBS: o sinal chega primeiro em TESTE (prévia só no Studio) e entra no ar pelo botão "Entrar ao vivo"
                contentDetails = new { enableAutoStart = false, enableAutoStop = true, enableDvr = true, monitorStream = new { enableMonitorStream = true, broadcastStreamDelayMs = 0 } }
            });
            var live = new LiveYt { Id = (string)b["id"], Titulo = titulo, Inicio = quando, Privacidade = privacidade, Estado = "created", DoCast = true };
            Registro.Info("live criada no YouTube: " + live.Id + " · " + titulo);
            string aviso = null;
            if (miniaturaJpeg != null)
                try { await EnviarMiniatura(live.Id, miniaturaJpeg); }
                catch (Exception e) { aviso = "A live foi criada, mas a miniatura não foi: " + e.Message; Registro.Info("miniatura: " + e.Message); }
            var (sid, servidor, chave) = await ChaveDoCast();
            await Chamar(HttpMethod.Post, $"liveBroadcasts/bind?id={live.Id}&part=id,contentDetails&streamId={sid}");
            Lives = Lives.Where(x => x.Id != live.Id).Append(live).OrderBy(x => x.Inicio).ToList();
            return (live, servidor, chave, aviso);
        }

        public static Task EnviarMiniatura(string id, byte[] jpeg)
        {
            if (jpeg.Length > 2_000_000) throw new InvalidOperationException("a miniatura passou de 2 MB");
            return Chamar(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?uploadType=media&videoId=" + Uri.EscapeDataString(id), bin: jpeg, tipoBin: "image/jpeg");
        }

        // A chave reutilizável "MiguelAngelus Cast" (cria na primeira vez). Não mexe nas chaves que o OBS usa.
        static async Task<(string id, string servidor, string chave)> ChaveDoCast()
        {
            var d = await Chamar(HttpMethod.Get, "liveStreams?part=id,snippet,cdn,contentDetails&mine=true&maxResults=50");
            var s = d?["items"]?.AsArray().FirstOrDefault(x => (string)x?["snippet"]?["title"] == NomeChave && (bool?)x?["contentDetails"]?["isReusable"] != false);
            s ??= await Chamar(HttpMethod.Post, "liveStreams?part=id,snippet,cdn,contentDetails", new
            {
                snippet = new { title = NomeChave, description = "Chave usada pelo MiguelAngelus Cast (criada por ele)." },
                cdn = new { ingestionType = "rtmp", resolution = "variable", frameRate = "variable" },
                contentDetails = new { isReusable = true }
            });
            var ing = s["cdn"]?["ingestionInfo"];
            idChave = (string)s["id"];
            return ((string)s["id"],(string)ing?["ingestionAddress"] ?? "rtmp://a.rtmp.youtube.com/live2", (string)ing?["streamName"] ?? "");
        }

        // id da chave do Cast, sem criar (null = ainda não existe)
        static string idChave;
        static async Task<string> IdChaveCast()
        {
            if (idChave != null) return idChave;
            var d = await Chamar(HttpMethod.Get, "liveStreams?part=id,snippet&mine=true&maxResults=50");
            idChave = (string)d?["items"]?.AsArray().FirstOrDefault(x => (string)x?["snippet"]?["title"] == NomeChave)?["id"];
            return idChave;
        }

        // ---------- (0.1.0) sinal em teste, entrar ao vivo, encerrar ----------
        static async Task<string> Ciclo(string id)
        {
            var d = await Chamar(HttpMethod.Get, "liveBroadcasts?part=status&id=" + Uri.EscapeDataString(id));
            var st = (string)d?["items"]?[0]?["status"]?["lifeCycleStatus"] ?? "";
            var l = Lives.FirstOrDefault(x => x.Id == id); if (l != null && st != "") l.Estado = st;
            return st;
        }
        static async Task<string> Sinal()
        {
            var id = await IdChaveCast(); if (id == null) return "";
            var d = await Chamar(HttpMethod.Get, "liveStreams?part=status&id=" + id);
            return (string)d?["items"]?[0]?["status"]?["streamStatus"] ?? "";
        }
        static Task Transicao(string id, string alvo) =>
            Chamar(HttpMethod.Post, $"liveBroadcasts/transition?broadcastStatus={alvo}&id={Uri.EscapeDataString(id)}&part=status");
        static async Task<bool> Esperar(Func<Task<bool>> ok, int segundos, Func<bool> parar = null)
        {
            for (var t = DateTime.Now.AddSeconds(segundos); DateTime.Now < t;)
            {
                if (parar?.Invoke() == true) return false;
                if (await ok()) return true;
                await Task.Delay(3000);
            }
            return false;
        }

        // O Cast começou a mandar: espera o sinal chegar e põe a live em TESTE (prévia no Studio, ninguém de fora vê).
        public static async Task<string> PorEmTeste(string id, Action<string> fase, Func<bool> desistir)
        {
            fase("esperando o sinal chegar ao YouTube…");
            if (!await Esperar(async () => await Sinal() == "active", 90, desistir))
                return desistir() ? null : "O sinal não chegou ao YouTube em 90 s (confira a internet e a chave).";
            var st = await Ciclo(id);
            if (st is "live" or "liveStarting") { fase("no ar"); return null; }      // live antiga (0.0.8), com início automático
            if (st is "created" or "ready")
            {
                fase("ligando a prévia…");
                await Transicao(id, "testing");
                await Esperar(async () => await Ciclo(id) == "testing", 60, desistir);
            }
            fase((await Ciclo(id)) == "testing" ? "em teste" : "sinal chegando");
            return null;
        }

        public static async Task EntrarAoVivo(string id, Action<string> fase)
        {
            var st = await Ciclo(id);
            if (st is "created" or "ready")
            {
                if (await Sinal() != "active") throw new InvalidOperationException("o sinal ainda não chegou ao YouTube: inicie a transmissão no Cast primeiro");
                fase("ligando a prévia…");
                await Transicao(id, "testing");
                if (!await Esperar(async () => await Ciclo(id) == "testing", 60)) throw new InvalidOperationException("o YouTube não passou a live para teste");
            }
            else if (st is "live" or "liveStarting") { fase("no ar"); return; }
            else if (st != "testing") throw new InvalidOperationException("a live está \"" + st + "\" e não pode entrar no ar");
            fase("entrando no ar…");
            await Transicao(id, "live");
            await Esperar(async () => await Ciclo(id) == "live", 60);
            fase("no ar");
        }

        // Termina a live no YouTube (só se estiver no ar; em teste, fica para usar de novo)
        public static async Task<bool> Encerrar(string id)
        {
            var st = await Ciclo(id);
            if (st is not ("live" or "liveStarting")) return false;
            await Transicao(id, "complete");
            return true;
        }

        static string Limpar(string s, int max)
        {
            s = (s ?? "").Replace("<", "‹").Replace(">", "›").Trim();   // o YouTube recusa < e > no título e na descrição
            return s.Length > max ? s[..max].Trim() : s;
        }
    }
}
