// (0.1.12) Sincronização dos ajustes do Cast pelo Google Drive — a mesma conta e a mesma pasta "MiguelAngelus" do
// MiguelAngelus, numa subpasta "Cast". Login: navegador do sistema + retorno em 127.0.0.1 (OAuth com PKCE),
// escopo drive.file (só enxerga o que o MiguelAngelus/Cast criaram). O acesso fica no cofre (chaves.dat, DPAPI).
// Neste computador, se o MiguelAngelus já está conectado, o Cast aproveita o acesso dele (mesmo usuário do Windows).
//
// O que vai para a nuvem: cenas (mesa.json), câmeras, roteiros, capa, rastreio (ajustes, não os rostos), joystick e as
// imagens da capa escolhidas por você. NÃO vão: chaves de transmissão e do YouTube, rostos cadastrados, a ilustração do
// folheto e o brasão/logo vindos do MiguelAngelus (direitos autorais / já chegam por ele), som e saída (placa de som,
// pasta de gravação e codificador são deste computador), registro e telemetria.
//
// Regra: o mais novo vence; a versão trocada fica guardada em "nuvem-anteriores". Na primeira vez num computador, o
// que já está na nuvem vence (para um Cast recém-instalado não apagar as cenas de quem já usa).
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
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast
{
    static class Nuvem
    {
        const string Escopo = "https://www.googleapis.com/auth/drive.file";
        const string Api = "https://www.googleapis.com/drive/v3/";
        const string ApiUpload = "https://www.googleapis.com/upload/drive/v3/";
        const string TipoPasta = "application/vnd.google-apps.folder";
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        static readonly string arqEstado = Path.Combine(Program.PastaDados, "nuvem.json");
        static readonly string pastaAnteriores = Path.Combine(Program.PastaDados, "nuvem-anteriores");
        public static readonly string[] Ajustes = { "mesa.json", "cameras.json", "roteiros.json", "capa.json", "rastreio.json", "joystick.json" };
        static readonly string[] imagens = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

        static string clienteId, clienteSegredo, refreshToken, accessToken;
        static DateTime expira;
        static string pastaId;                                         // MiguelAngelus/Cast
        static readonly SemaphoreSlim trava = new SemaphoreSlim(1, 1);

        sealed class Item { public string Hash { get; set; } = ""; public long Remoto { get; set; } }
        sealed class Estado
        {
            public bool Ligada { get; set; }
            public string Email { get; set; } = "";
            public string Ultima { get; set; } = "";                    // "dd/MM HH:mm"
            public string Resumo { get; set; } = "";
            public Dictionary<string, Item> Itens { get; set; } = new();
        }
        static Estado est = LerEstado();
        static Estado LerEstado()
        {
            try { if (File.Exists(arqEstado)) return JsonSerializer.Deserialize<Estado>(File.ReadAllText(arqEstado)) ?? new Estado(); } catch { }
            return new Estado();
        }
        static void GravarEstado() { try { File.WriteAllText(arqEstado, JsonSerializer.Serialize(est, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { Registro.Erro("gravar nuvem.json", e); } }

        public static bool Conectado { get { CarregarCred(); return !string.IsNullOrEmpty(refreshToken); } }
        public static bool TemCliente { get { CarregarCred(); return !string.IsNullOrEmpty(clienteId); } }
        public static bool Ligada => est.Ligada && Conectado;
        public static bool Ocupada => trava.CurrentCount == 0;
        public static bool Aberto;                 // a tela já carregou cenas e câmeras (depois do AoAbrir)
        public static bool AguardandoReinicio;

        // ---------- credenciais (cofre do Cast) ----------
        static bool credLida;
        static void CarregarCred()
        {
            if (credLida) return; credLida = true;
            try
            {
                var t = Cofre.Ler("google"); if (string.IsNullOrEmpty(t)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(t);
                d.TryGetValue("cliente", out clienteId); d.TryGetValue("segredo", out clienteSegredo); d.TryGetValue("refresh", out refreshToken);
            }
            catch { }
        }
        static void GuardarCred() => Cofre.Gravar("google", JsonSerializer.Serialize(new Dictionary<string, string> { ["cliente"] = clienteId ?? "", ["segredo"] = clienteSegredo ?? "", ["refresh"] = refreshToken ?? "" }));

        // O MiguelAngelus deste computador guarda o acesso em %LOCALAPPDATA%\MiguelAngelus\google.dat (DPAPI do mesmo usuário)
        static string ArqMiguel => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiguelAngelus", "google.dat");
        static Dictionary<string, string> CredMiguel()
        {
            try
            {
                if (!File.Exists(ArqMiguel)) return null;
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(Cofre.Proteger(File.ReadAllBytes(ArqMiguel), false)));
                return d != null && d.TryGetValue("refresh", out var r) && !string.IsNullOrEmpty(r) ? d : null;
            }
            catch { return null; }
        }
        public static bool MiguelConectado => CredMiguel() != null;

        public static async Task<string> UsarContaDoMiguel()
        {
            var d = CredMiguel() ?? throw new InvalidOperationException("o MiguelAngelus deste computador não está conectado ao Google (Ajustes → Sincronização)");
            d.TryGetValue("cliente", out clienteId); d.TryGetValue("segredo", out clienteSegredo); d.TryGetValue("refresh", out refreshToken);
            accessToken = null; pastaId = null; credLida = true;
            GuardarCred();
            return await ConfirmarConta();
        }

        // JSON baixado do Google Cloud ("App para computador") — o mesmo do MiguelAngelus
        public static void DefinirCliente(string textoJson)
        {
            using var doc = JsonDocument.Parse(textoJson);
            var r = doc.RootElement;
            var inst = r.TryGetProperty("installed", out var i) ? i : r.TryGetProperty("web", out var w) ? w : r;
            var id = inst.TryGetProperty("client_id", out var ci) ? ci.GetString() : null;
            var seg = inst.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
            if (string.IsNullOrEmpty(id) || !id.EndsWith(".apps.googleusercontent.com")) throw new InvalidOperationException("este arquivo não é um ID do cliente OAuth do Google");
            CarregarCred();
            if (id != clienteId) { refreshToken = null; accessToken = null; }
            clienteId = id; clienteSegredo = seg;
            GuardarCred();
        }

        public static void Desconectar()
        {
            CarregarCred();
            refreshToken = null; accessToken = null; pastaId = null;
            GuardarCred();
            est.Ligada = false; est.Email = ""; GravarEstado();
        }

        static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static async Task<string> Conectar()
        {
            CarregarCred();
            if (string.IsNullOrEmpty(clienteId)) throw new InvalidOperationException("carregue primeiro o arquivo do ID do cliente (o mesmo JSON do MiguelAngelus)");
            var ouvinte = new TcpListener(IPAddress.Loopback, 0);
            ouvinte.Start();
            try
            {
                var porta = ((IPEndPoint)ouvinte.LocalEndpoint).Port;
                var retorno = $"http://127.0.0.1:{porta}/";
                var verificador = Base64Url(RandomNumberGenerator.GetBytes(32));
                var desafio = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));
                var estado = Guid.NewGuid().ToString("N");
                var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
                          "&client_id=" + Uri.EscapeDataString(clienteId) + "&redirect_uri=" + Uri.EscapeDataString(retorno) +
                          "&scope=" + Uri.EscapeDataString(Escopo) + "&code_challenge=" + desafio + "&code_challenge_method=S256&state=" + estado;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                var aceitar = ouvinte.AcceptTcpClientAsync();
                if (await Task.WhenAny(aceitar, Task.Delay(TimeSpan.FromMinutes(5))) != aceitar) throw new TimeoutException("o login no navegador não foi concluído");
                string codigo, erro;
                using (var cli = aceitar.Result)
                using (var fluxo = cli.GetStream())
                {
                    var buf = new byte[8192]; var n = await fluxo.ReadAsync(buf, 0, buf.Length);
                    var linha = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0];
                    var consulta = linha.Split(' ').ElementAtOrDefault(1) ?? "";
                    var q = consulta.Contains('?') ? consulta[(consulta.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('='))
                        .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1]) : "") : new Dictionary<string, string>();
                    q.TryGetValue("code", out codigo); q.TryGetValue("error", out erro);
                    if (!q.TryGetValue("state", out var es) || es != estado) { codigo = null; erro ??= "resposta inválida"; }
                    var html = codigo != null
                        ? "<h2 style='font-family:Segoe UI'>&#10004; MiguelAngelus Cast conectado ao Google Drive.</h2><p style='font-family:Segoe UI'>Pode fechar esta aba e voltar ao Cast.</p>"
                        : "<h2 style='font-family:Segoe UI'>N&atilde;o foi poss&iacute;vel conectar.</h2><p style='font-family:Segoe UI'>" + WebUtility.HtmlEncode(erro ?? "") + "</p>";
                    var corpo = Encoding.UTF8.GetBytes(html);
                    var cab = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {corpo.Length}\r\nConnection: close\r\n\r\n");
                    await fluxo.WriteAsync(cab); await fluxo.WriteAsync(corpo);
                }
                if (codigo == null) throw new InvalidOperationException("o Google recusou: " + erro);
                using var tok = await Token(new Dictionary<string, string> {
                    ["code"] = codigo, ["client_id"] = clienteId, ["client_secret"] = clienteSegredo ?? "",
                    ["redirect_uri"] = retorno, ["grant_type"] = "authorization_code", ["code_verifier"] = verificador });
                if (tok.RootElement.TryGetProperty("refresh_token", out var rt)) refreshToken = rt.GetString();
                if (string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("o Google não devolveu o acesso permanente (tente de novo)");
                pastaId = null;
                GuardarCred();
            }
            finally { ouvinte.Stop(); }
            return await ConfirmarConta();
        }

        static async Task<string> ConfirmarConta()
        {
            using var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, Api + "about?fields=user(emailAddress)"));
            var email = d.RootElement.GetProperty("user").GetProperty("emailAddress").GetString() ?? "";
            est.Email = email; est.Ligada = true; GravarEstado();
            Registro.Info("nuvem: conectado ao Google Drive");
            return email;
        }

        static async Task<JsonDocument> Token(Dictionary<string, string> campos)
        {
            var r = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(campos));
            var texto = await r.Content.ReadAsStringAsync();
            var d = JsonDocument.Parse(texto);
            if (!r.IsSuccessStatusCode)
            {
                d.Dispose();
                if (texto.Contains("invalid_grant")) { refreshToken = null; GuardarCred(); throw new InvalidOperationException("o acesso ao Google expirou: conecte de novo"); }
                throw new InvalidOperationException("Google: " + texto);
            }
            accessToken = d.RootElement.GetProperty("access_token").GetString();
            expira = DateTime.UtcNow.AddSeconds(d.RootElement.GetProperty("expires_in").GetDouble() - 60);
            return d;
        }

        static async Task<string> Acesso()
        {
            CarregarCred();
            if (string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("não conectado ao Google");
            if (accessToken == null || DateTime.UtcNow >= expira)
                (await Token(new Dictionary<string, string> { ["client_id"] = clienteId, ["client_secret"] = clienteSegredo ?? "", ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" })).Dispose();
            return accessToken;
        }

        static async Task<HttpResponseMessage> Enviar(Func<HttpRequestMessage> criar)
        {
            for (int tentativa = 0; ; tentativa++)
            {
                var req = criar();
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Acesso());
                var r = await http.SendAsync(req);
                if (r.IsSuccessStatusCode) return r;
                if (((int)r.StatusCode == 429 || (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.Unauthorized) && tentativa < 3)
                {
                    if (r.StatusCode == HttpStatusCode.Unauthorized) accessToken = null;
                    await Task.Delay(800 * (tentativa + 1));
                    continue;
                }
                throw new InvalidOperationException($"Google Drive ({(int)r.StatusCode}): " + await r.Content.ReadAsStringAsync());
            }
        }
        static async Task<JsonDocument> Json(Func<HttpRequestMessage> criar) => JsonDocument.Parse(await (await Enviar(criar)).Content.ReadAsStringAsync());
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        static async Task<string> AcharOuCriarPasta(string nome, string pai)
        {
            var q = $"name='{Q(nome)}' and mimeType='{TipoPasta}' and trashed=false and '{pai}' in parents";
            using (var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, Api + "files?fields=files(id)&q=" + Uri.EscapeDataString(q))))
            {
                var l = d.RootElement.GetProperty("files");
                if (l.GetArrayLength() > 0) return l[0].GetProperty("id").GetString();
            }
            using var c = await Json(() => new HttpRequestMessage(HttpMethod.Post, Api + "files?fields=id")
            { Content = new StringContent(JsonSerializer.Serialize(new { name = nome, mimeType = TipoPasta, parents = new[] { pai } }), Encoding.UTF8, "application/json") });
            return c.RootElement.GetProperty("id").GetString();
        }
        static async Task<string> Pasta()
        {
            if (pastaId != null) return pastaId;
            var raiz = await AcharOuCriarPasta("MiguelAngelus", "root");      // a mesma do MiguelAngelus
            return pastaId = await AcharOuCriarPasta("Cast", raiz);
        }
        static long Ms(string rfc3339) => DateTimeOffset.Parse(rfc3339, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        sealed record Remoto(string Id, long Mtime);
        static async Task<Dictionary<string, Remoto>> Listar()
        {
            var pid = await Pasta();
            var r = new Dictionary<string, Remoto>(StringComparer.OrdinalIgnoreCase);
            string pagina = null;
            do
            {
                var url = Api + "files?pageSize=1000&fields=nextPageToken,files(id,name,modifiedTime)&q=" +
                          Uri.EscapeDataString($"'{pid}' in parents and trashed=false") + (pagina != null ? "&pageToken=" + pagina : "");
                using var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, url));
                foreach (var f in d.RootElement.GetProperty("files").EnumerateArray())
                    r[f.GetProperty("name").GetString()] = new Remoto(f.GetProperty("id").GetString(), Ms(f.GetProperty("modifiedTime").GetString()));
                pagina = d.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            } while (pagina != null);
            return r;
        }
        static async Task<byte[]> Baixar(string id) => await (await Enviar(() => new HttpRequestMessage(HttpMethod.Get, Api + "files/" + id + "?alt=media"))).Content.ReadAsByteArrayAsync();
        static async Task<long> Subir(string nome, string id, byte[] dados)
        {
            var tipo = nome.EndsWith(".json") ? "application/json" : nome.EndsWith(".png") ? "image/png" : "application/octet-stream";
            JsonDocument d;
            if (id != null)
                d = await Json(() => new HttpRequestMessage(HttpMethod.Patch, ApiUpload + "files/" + id + "?uploadType=media&fields=id,modifiedTime")
                { Content = new ByteArrayContent(dados) { Headers = { ContentType = new MediaTypeHeaderValue(tipo) } } });
            else
            {
                var pid = await Pasta();
                d = await Json(() =>
                {
                    var multi = new MultipartContent("related");
                    multi.Add(new StringContent(JsonSerializer.Serialize(new { name = nome, parents = new[] { pid } }), Encoding.UTF8, "application/json"));
                    multi.Add(new ByteArrayContent(dados) { Headers = { ContentType = new MediaTypeHeaderValue(tipo) } });
                    return new HttpRequestMessage(HttpMethod.Post, ApiUpload + "files?uploadType=multipart&fields=id,modifiedTime") { Content = multi };
                });
            }
            using (d) return Ms(d.RootElement.GetProperty("modifiedTime").GetString());
        }

        // ---------- arquivos locais que vão para a nuvem ----------
        // nome na nuvem → caminho aqui ("capa--foto.png" = capa\foto.png)
        static Dictionary<string, string> Locais()
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Ajustes) r[a] = Path.Combine(Program.PastaDados, a);
            var capa = Path.Combine(Program.PastaDados, "capa");
            if (Directory.Exists(capa))
                foreach (var f in Directory.GetFiles(capa))
                {
                    var n = Path.GetFileName(f);
                    if (n.StartsWith("folheto", StringComparison.OrdinalIgnoreCase) || n.StartsWith("marca-miguel", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!imagens.Contains(Path.GetExtension(n).ToLowerInvariant()) || new FileInfo(f).Length > 15_000_000) continue;
                    r["capa--" + n] = f;
                }
            return r;
        }
        static string CaminhoDe(string nomeNuvem)
        {
            if (Ajustes.Contains(nomeNuvem, StringComparer.OrdinalIgnoreCase)) return Path.Combine(Program.PastaDados, nomeNuvem);
            if (!nomeNuvem.StartsWith("capa--")) return null;
            var n = nomeNuvem[6..];
            if (n == "" || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.Contains("..") || n.StartsWith("folheto", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("marca-miguel", StringComparison.OrdinalIgnoreCase) || !imagens.Contains(Path.GetExtension(n).ToLowerInvariant())) return null;
            return Path.Combine(Program.PastaDados, "capa", n);
        }
        static byte[] LerLocal(string f)
        {
            if (!File.Exists(f)) return null;
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray();
        }
        static string Hash(byte[] b) => b == null ? "" : Convert.ToHexString(SHA256.HashData(b));
        static void GuardarAnterior(string f)
        {
            try
            {
                if (!File.Exists(f)) return;
                Directory.CreateDirectory(pastaAnteriores);
                File.Copy(f, Path.Combine(pastaAnteriores, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Path.GetFileName(f)), true);
                foreach (var velho in new DirectoryInfo(pastaAnteriores).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).Skip(60)) velho.Delete();
            }
            catch (Exception e) { Registro.Erro("nuvem: guardar a versão anterior", e); }
        }

        public sealed record Resultado(int Recebidos, int Enviados, string Erro);

        // baixar=false: só envia o que mudou aqui (com o Cast aberto, o que chega da nuvem espera o próximo "Atualizar"/abrir)
        public static async Task<Resultado> Sincronizar(bool baixar)
        {
            if (!Ligada) return new Resultado(0, 0, null);
            // chegou coisa com o Cast aberto e ainda não reiniciou: não manda nada (mandaria as cenas velhas da memória)
            if (AguardandoReinicio) return new Resultado(0, 0, null);
            // outra sincronização em andamento: espera ela acabar (até 90 s) em vez de dizer "em dia" sem ter olhado
            if (!await trava.WaitAsync(TimeSpan.FromSeconds(90))) return new Resultado(0, 0, "outra sincronização ainda está em andamento");
            int rec = 0, env = 0;
            try
            {
                var remotos = await Listar();
                var locais = Locais();
                var nomes = new HashSet<string>(locais.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var n in remotos.Keys) if (CaminhoDe(n) != null) nomes.Add(n);
                foreach (var nome in nomes)
                {
                    var f = locais.TryGetValue(nome, out var lf) ? lf : CaminhoDe(nome);
                    var dados = LerLocal(f); var h = Hash(dados);
                    est.Itens.TryGetValue(nome, out var it);
                    remotos.TryGetValue(nome, out var rem);
                    bool localMudou = dados != null && (it == null || it.Hash != h);
                    bool remotoMudou = rem != null && (it == null || it.Remoto != rem.Mtime);
                    bool descer;
                    if (remotoMudou && !localMudou) descer = true;
                    else if (localMudou && !remotoMudou) descer = false;
                    else if (localMudou && remotoMudou)
                        // primeira vez neste computador: a nuvem vence; depois, o mais novo
                        descer = it == null || rem.Mtime > new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds();
                    else continue;
                    if (descer)
                    {
                        if (!baixar) continue;
                        var novo = await Baixar(rem.Id);
                        if (Hash(novo) != h)
                        {
                            GuardarAnterior(f);
                            Directory.CreateDirectory(Path.GetDirectoryName(f));
                            File.WriteAllBytes(f, novo); rec++;
                            Registro.Info("nuvem: recebido " + nome);
                        }
                        est.Itens[nome] = new Item { Hash = Hash(novo), Remoto = rem.Mtime };
                    }
                    else
                    {
                        var mt = await Subir(nome, rem?.Id, dados); env++;
                        est.Itens[nome] = new Item { Hash = h, Remoto = mt };
                        Registro.Info("nuvem: enviado " + nome);
                    }
                }
                if (rec > 0 && Aberto) AguardandoReinicio = true;
                if (baixar || env > 0 || est.Ultima == "") { est.Ultima = DateTime.Now.ToString("dd/MM HH:mm"); est.Resumo = ""; }
                GravarEstado();
                return new Resultado(rec, env, null);
            }
            catch (Exception e)
            {
                Registro.Erro("nuvem", e);
                est.Resumo = "erro: " + e.Message; GravarEstado();
                return new Resultado(rec, env, e.Message);
            }
            finally { trava.Release(); }
        }

        // Ao abrir: antes de carregar cenas e câmeras, traz o que outro computador mudou (até ~12 s; sem internet, segue)
        public static void AoAbrir()
        {
            if (!Ligada) return;
            try
            {
                var t = Task.Run(() => Sincronizar(true));
                if (t.Wait(TimeSpan.FromSeconds(12)) && t.Result.Recebidos > 0) Registro.Info("nuvem: " + t.Result.Recebidos + " arquivo(s) vieram da nuvem ao abrir");
            }
            catch (Exception e) { Registro.Erro("nuvem ao abrir", e); }
        }

        public static object Info() => new
        {
            tipo = "nuvem", conectado = Conectado, ligada = Ligada, temCliente = TemCliente, miguel = MiguelConectado,
            email = est.Email, ultima = est.Ultima, resumo = est.Resumo, ocupada = Ocupada, reiniciar = AguardandoReinicio
        };
        public static void Ligar(bool sim) { est.Ligada = sim; GravarEstado(); }
    }
}
