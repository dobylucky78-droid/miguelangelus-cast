// (0.1.16) Backlog 7.4: homilias e cortes na nuvem, para cortar em casa.
// Mesma conta Google e mesma pasta "MiguelAngelus" da sincronização; aqui numa pasta à parte:
//   MiguelAngelus / Cast Momentos / Homilias   → a versão em pé (reduzida para ~2,5 Mbps) + a transcrição (.cortes.json)
//   MiguelAngelus / Cast Momentos / Cortes     → os cortes prontos
// (não fica dentro de "MiguelAngelus/Cast", porque lá cada arquivo é um ajuste sincronizado)
// Envio "retomável" do Google (pedaços de 8 MB): a internet caiu no meio, continua de onde parou.
// Só envia com a live e a gravação desligadas (quem chama é o Principal, depois da versão em pé e da transcrição).
// A política (quanto guardar e por quanto tempo) fica gravada na própria pasta "Cast Momentos" (appProperties),
// então vale igual para todos os computadores conectados à conta. Apagar = mandar para a lixeira do Drive.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiguelAngelusCast.Motor;

namespace MiguelAngelusCast
{
    static partial class Nuvem
    {
        public static class Homilias
        {
            public sealed class Politica
            {
                public bool EnviarHomilias { get; set; } = true;
                public bool EnviarCortes { get; set; } = true;
                public double LimiteGb { get; set; } = 20;
                public int DiasHomilias { get; set; } = 30;     // 0 = nunca apaga
                public int DiasCortes { get; set; } = 90;
            }
            public sealed record Arq(string Id, string Nome, long Tamanho, DateTime Quando);
            public sealed class Retrato
            {
                public long? Limite, Uso;                       // da conta inteira (Drive + Gmail + Fotos)
                public long UsoApp;                             // só Homilias + Cortes
                public List<Arq> Hom = new List<Arq>(), Cor = new List<Arq>();
                public string Erro, Url;
                public DateTime Lido;
            }

            static readonly HttpClient longo = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            static readonly SemaphoreSlim travaPastas = new SemaphoreSlim(1, 1);
            static string idMom, idHom, idCor;
            public static volatile string Estado = "";
            public static Retrato Ultimo;
            public static Politica Pol = LerPolLocal();
            public static string Email => est.Email;
            public static bool Rodando => tarefa != null && !tarefa.IsCompleted;
            static Task tarefa; static CancellationTokenSource cts;
            static DateTime proxLimpeza;
            public static void Parar() { try { cts?.Cancel(); } catch { } }
            static long LimiteBytes => (long)(Math.Max(1, Pol.LimiteGb) * 1024 * 1024 * 1024);

            // ---------- política (cópia local + a da pasta na nuvem, que vale para todos) ----------
            static string ArqPol => Path.Combine(Program.PastaDados, "nuvem-homilias.json");
            static Politica LerPolLocal()
            {
                try { if (File.Exists(ArqPol)) return JsonSerializer.Deserialize<Politica>(File.ReadAllText(ArqPol)) ?? new Politica(); } catch { }
                return new Politica();
            }
            static void GravarPolLocal() { try { File.WriteAllText(ArqPol, JsonSerializer.Serialize(Pol, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
            static async Task LerPolNuvem()
            {
                using var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, Api + "files/" + idMom + "?fields=appProperties"));
                if (!d.RootElement.TryGetProperty("appProperties", out var ap) || !ap.TryGetProperty("politica", out var pj)) return;
                try { var p = JsonSerializer.Deserialize<Politica>(pj.GetString()); if (p != null) { Pol = p; GravarPolLocal(); } } catch { }
            }
            public static async Task<string> SalvarPolitica(Politica p)
            {
                p.LimiteGb = Math.Clamp(p.LimiteGb, 1, 2000);
                p.DiasHomilias = Math.Clamp(p.DiasHomilias, 0, 3650); p.DiasCortes = Math.Clamp(p.DiasCortes, 0, 3650);
                Pol = p; GravarPolLocal();
                try
                {
                    await Pastas();
                    var corpo = JsonSerializer.Serialize(new { appProperties = new { politica = JsonSerializer.Serialize(p) } });
                    (await Enviar(() => new HttpRequestMessage(HttpMethod.Patch, Api + "files/" + idMom + "?fields=id") { Content = new StringContent(corpo, Encoding.UTF8, "application/json") })).Dispose();
                    return null;
                }
                catch (Exception e) { Registro.Erro("nuvem: salvar a política", e); return e.Message; }
            }

            // ---------- o que já foi enviado (para não mandar de novo o que a política apagou) ----------
            static string ArqEnviados => Path.Combine(Program.PastaDados, "nuvem-homilias-enviados.json");
            static readonly Dictionary<string, string> enviados = LerEnviados();
            static Dictionary<string, string> LerEnviados()
            {
                try { if (File.Exists(ArqEnviados)) return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ArqEnviados)), StringComparer.OrdinalIgnoreCase); } catch { }
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            static void Marcar(string chave, string valor)
            {
                lock (enviados) { enviados[chave] = valor; try { File.WriteAllText(ArqEnviados, JsonSerializer.Serialize(enviados)); } catch { } }
            }
            static string Marca(string chave) { lock (enviados) return enviados.TryGetValue(chave, out var v) ? v : null; }
            static string MarcaTexto(string f) => File.GetLastWriteTimeUtc(f).Ticks.ToString();

            // ---------- pastas, lista, espaço ----------
            static async Task Pastas()
            {
                if (idCor != null) return;
                await travaPastas.WaitAsync();
                try
                {
                    if (idCor != null) return;
                    var raiz = await AcharOuCriarPasta("MiguelAngelus", "root");
                    var m = await AcharOuCriarPasta("Cast Momentos", raiz);
                    idHom = await AcharOuCriarPasta("Homilias", m);
                    idCor = await AcharOuCriarPasta("Cortes", m);
                    idMom = m;
                }
                finally { travaPastas.Release(); }
            }
            static async Task<List<Arq>> ListarArqs(string pid)
            {
                var r = new List<Arq>(); string pagina = null;
                do
                {
                    var url = Api + "files?pageSize=1000&fields=nextPageToken,files(id,name,size,modifiedTime)&q=" +
                              Uri.EscapeDataString($"'{pid}' in parents and trashed=false") + (pagina != null ? "&pageToken=" + Uri.EscapeDataString(pagina) : "");
                    using var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, url));
                    foreach (var f in d.RootElement.GetProperty("files").EnumerateArray())
                        r.Add(new Arq(f.GetProperty("id").GetString(), f.GetProperty("name").GetString(),
                            f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : 0,
                            DateTimeOffset.Parse(f.GetProperty("modifiedTime").GetString(), CultureInfo.InvariantCulture).LocalDateTime));
                    pagina = d.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
                } while (pagina != null);
                return r.OrderByDescending(a => a.Quando).ToList();
            }
            /// <summary>Lê da nuvem: espaço da conta, o que o Cast guardou e a política. Não lança erro (fica em Erro).</summary>
            public static async Task<Retrato> Ler()
            {
                var r = new Retrato();
                try
                {
                    await Pastas();
                    await LerPolNuvem();
                    using (var d = await Json(() => new HttpRequestMessage(HttpMethod.Get, Api + "about?fields=storageQuota(limit,usage)")))
                    {
                        var q = d.RootElement.GetProperty("storageQuota");
                        if (q.TryGetProperty("limit", out var l) && long.TryParse(l.GetString(), out var ln)) r.Limite = ln;
                        if (q.TryGetProperty("usage", out var u) && long.TryParse(u.GetString(), out var un)) r.Uso = un;
                    }
                    r.Hom = await ListarArqs(idHom); r.Cor = await ListarArqs(idCor);
                    r.UsoApp = r.Hom.Sum(a => a.Tamanho) + r.Cor.Sum(a => a.Tamanho);
                    // authuser: abre com a conta da paróquia (o navegador costuma estar na conta pessoal → "Acesso negado")
                    r.Url = "https://drive.google.com/drive/folders/" + idMom + (string.IsNullOrEmpty(est.Email) ? "" : "?authuser=" + Uri.EscapeDataString(est.Email));
                }
                catch (Exception e) { r.Erro = e.Message; Registro.Erro("nuvem das homilias", e); }
                r.Lido = DateTime.Now; Ultimo = r; return r;
            }

            // ---------- enviar (retomável) e baixar ----------
            static string Rfc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            static long? FimDoRange(HttpResponseMessage r)
            {
                // "Range: bytes=0-1234" (o .NET pode guardar como cabeçalho não validado)
                foreach (var h in r.Headers.NonValidated)
                    if (h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = h.Value.ToString(); var i = v.LastIndexOf('-');
                        if (i > 0 && long.TryParse(v[(i + 1)..].Trim(), out var fim)) return fim;
                    }
                return null;
            }
            static async Task<string> SubirArquivo(string pasta, string nome, string caminho, string tipo, string idExistente, CancellationToken ct, Action<int> pct)
            {
                long total = new FileInfo(caminho).Length;
                var quando = Rfc(File.GetLastWriteTime(caminho));
                var meta = idExistente == null
                    ? JsonSerializer.Serialize(new { name = nome, parents = new[] { pasta }, modifiedTime = quando })
                    : JsonSerializer.Serialize(new { modifiedTime = quando });
                var url = ApiUpload + (idExistente == null ? "files" : "files/" + idExistente) + "?uploadType=resumable&fields=id";
                for (int sessaoNova = 0; ; sessaoNova++)
                {
                    Uri sessao;
                    using (var r0 = await Enviar(() =>
                    {
                        var q = new HttpRequestMessage(idExistente == null ? HttpMethod.Post : HttpMethod.Patch, url) { Content = new StringContent(meta, Encoding.UTF8, "application/json") };
                        q.Headers.Add("X-Upload-Content-Type", tipo); q.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
                        return q;
                    })) sessao = r0.Headers.Location;
                    if (sessao == null) throw new InvalidOperationException("o Google Drive não abriu o envio");
                    const int Bloco = 8 * 1024 * 1024;
                    var buf = new byte[Bloco];
                    using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long enviado = 0; int falhas = 0; bool recomecar = false;
                    while (!recomecar)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Position = enviado;
                        int n = 0; while (n < Bloco) { int k = await fs.ReadAsync(buf.AsMemory(n, Bloco - n), ct); if (k <= 0) break; n += k; }
                        HttpResponseMessage resp = null;
                        try
                        {
                            var req = new HttpRequestMessage(HttpMethod.Put, sessao) { Content = new ByteArrayContent(buf, 0, n) };
                            req.Content.Headers.ContentRange = n > 0 ? new ContentRangeHeaderValue(enviado, enviado + n - 1, total) : new ContentRangeHeaderValue(total);
                            resp = await longo.SendAsync(req, ct);
                        }
                        catch (HttpRequestException) when (falhas < 6) { }
                        catch (IOException) when (falhas < 6) { }
                        if (resp != null && (int)resp.StatusCode == 308)
                        {
                            enviado = FimDoRange(resp) is long f ? f + 1 : enviado + n; falhas = 0;
                            pct?.Invoke(total > 0 ? (int)(enviado * 100 / total) : 0);
                            resp.Dispose(); continue;
                        }
                        if (resp != null && resp.IsSuccessStatusCode)
                        {
                            using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); resp.Dispose();
                            return d.RootElement.GetProperty("id").GetString();
                        }
                        if (resp != null && (int)resp.StatusCode < 500 && (int)resp.StatusCode != 429)
                            throw new InvalidOperationException($"Google Drive ({(int)resp.StatusCode}): " + await resp.Content.ReadAsStringAsync(ct));
                        // caiu a internet ou o Google falhou: espera e pergunta até onde chegou
                        resp?.Dispose();
                        if (++falhas > 6) throw new InvalidOperationException("a internet caiu várias vezes durante o envio");
                        Estado = $"☁ sem conexão; tentando de novo ({falhas})…";
                        await Task.Delay(5000 * falhas, ct);
                        try
                        {
                            var q = new HttpRequestMessage(HttpMethod.Put, sessao) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                            q.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);
                            using var rq = await longo.SendAsync(q, ct);
                            if ((int)rq.StatusCode == 308) enviado = FimDoRange(rq) is long f ? f + 1 : 0;   // sem "Range" = nada chegou
                            else if (rq.IsSuccessStatusCode) { using var d = JsonDocument.Parse(await rq.Content.ReadAsStringAsync(ct)); return d.RootElement.GetProperty("id").GetString(); }
                            else if ((int)rq.StatusCode == 404 || (int)rq.StatusCode == 410) recomecar = true;   // a sessão expirou
                        }
                        catch (HttpRequestException) { }
                    }
                    if (sessaoNova >= 2) throw new InvalidOperationException("o envio não conseguiu continuar");
                }
            }
            static async Task BaixarArquivo(string id, string destino, long total, CancellationToken ct, Action<int> pct)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, Api + "files/" + id + "?alt=media");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Acesso());
                using var r = await longo.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Google Drive ({(int)r.StatusCode}): " + await r.Content.ReadAsStringAsync(ct));
                Directory.CreateDirectory(Path.GetDirectoryName(destino));
                var parcial = destino + ".baixando";
                try
                {
                    using (var fs = File.Create(parcial))
                    using (var s = await r.Content.ReadAsStreamAsync(ct))
                    {
                        var buf = new byte[1 << 20]; long lidos = 0; int k, ultimo = -1;
                        while ((k = await s.ReadAsync(buf, ct)) > 0)
                        {
                            await fs.WriteAsync(buf.AsMemory(0, k), ct); lidos += k;
                            var p = total > 0 ? (int)(lidos * 100 / total) : 0; if (p != ultimo) { ultimo = p; pct?.Invoke(p); }
                        }
                    }
                    File.Move(parcial, destino, true);
                }
                catch { try { File.Delete(parcial); } catch { } throw; }
            }
            static async Task Lixeira(string id) =>
                (await Enviar(() => new HttpRequestMessage(HttpMethod.Patch, Api + "files/" + id + "?fields=id") { Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json") })).Dispose();

            // ---------- política de idade e espaço ----------
            static string Base(string nome) => nome.EndsWith(".cortes.json", StringComparison.OrdinalIgnoreCase) ? nome[..^".cortes.json".Length] : Path.GetFileNameWithoutExtension(nome);
            /// <summary>Manda para a lixeira o que passou da idade. Devolve quantos itens.</summary>
            static async Task<int> LimparPorIdade(Retrato r)
            {
                int n = 0;
                if (Pol.DiasHomilias > 0)
                {
                    var limite = DateTime.Now.AddDays(-Pol.DiasHomilias);
                    foreach (var g in r.Hom.GroupBy(a => Base(a.Nome), StringComparer.OrdinalIgnoreCase))
                    {
                        var video = g.FirstOrDefault(a => a.Nome.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
                        var data = video?.Quando ?? g.Max(a => a.Quando);
                        if (data >= limite) continue;
                        foreach (var a in g) { await Lixeira(a.Id); n++; Registro.Info("nuvem: homilia antiga para a lixeira: " + a.Nome); }
                    }
                }
                if (Pol.DiasCortes > 0)
                {
                    var limite = DateTime.Now.AddDays(-Pol.DiasCortes);
                    foreach (var a in r.Cor.Where(a => a.Quando < limite)) { await Lixeira(a.Id); n++; Registro.Info("nuvem: corte antigo para a lixeira: " + a.Nome); }
                }
                return n;
            }
            /// <summary>"Liberar espaço": manda para a lixeira as homilias mais antigas (e depois os cortes) até ficar em 80% do limite.</summary>
            public static void LiberarEspaco(Action mudou)
            {
                if (Rodando) return;
                cts = new CancellationTokenSource();
                tarefa = Task.Run(async () =>
                {
                    try
                    {
                        Estado = "☁ liberando espaço…"; mudou?.Invoke();
                        var r = await Ler(); if (r.Erro != null) throw new Exception(r.Erro);
                        long uso = r.UsoApp, alvo = (long)(LimiteBytes * 0.8); int n = 0;
                        var grupos = r.Hom.GroupBy(a => Base(a.Nome), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Min(a => a.Quando))
                            .Select(g => g.ToList()).Concat(r.Cor.OrderBy(a => a.Quando).Select(a => new List<Arq> { a }));
                        foreach (var g in grupos)
                        {
                            if (uso <= alvo) break;
                            foreach (var a in g) { await Lixeira(a.Id); uso -= a.Tamanho; n++; }
                        }
                        await Ler();
                        Estado = n == 0 ? "☁ nada para apagar" : $"☁ {n} arquivo(s) foram para a lixeira do Drive (somem de vez em 30 dias)";
                    }
                    catch (Exception e) { Estado = "☁ não deu para liberar espaço: " + e.Message; Registro.Erro("nuvem: liberar espaço", e); }
                    mudou?.Invoke();
                });
            }

            // ---------- o trabalho de fundo: envia o que falta (um de cada vez) ----------
            /// <param name="esperar">true = essa homilia ainda vai ser transcrita aqui (manda depois, junto com o texto)</param>
            public static void Trabalhar(string pastaMomentos, string cod, Func<string, bool> esperar, Action mudou)
            {
                if (Rodando || !Conectado) return;
                cts = new CancellationTokenSource(); var ct = cts.Token;
                tarefa = Task.Run(async () =>
                {
                    try { await Fazer(pastaMomentos, cod, esperar, mudou, ct); }
                    catch (OperationCanceledException) { Estado = "☁ envio para a nuvem pausado (continua com a live e a gravação desligadas)"; }
                    catch (Exception e) { Estado = "☁ não deu para enviar para a nuvem: " + e.Message; Registro.Erro("nuvem das homilias", e); }
                    mudou?.Invoke();
                });
            }
            static async Task Fazer(string pastaMomentos, string cod, Func<string, bool> esperar, Action mudou, CancellationToken ct)
            {
                var r = Ultimo;
                if (r == null || r.Erro != null || (DateTime.Now - r.Lido).TotalMinutes > 30) { r = await Ler(); if (r.Erro != null) throw new Exception(r.Erro); }
                if (DateTime.Now >= proxLimpeza)
                {
                    proxLimpeza = DateTime.Now.AddHours(6);
                    if (await LimparPorIdade(r) > 0) r = await Ler();
                }
                var naNuvemH = new HashSet<string>(r.Hom.Select(a => a.Nome), StringComparer.OrdinalIgnoreCase);
                var naNuvemC = new HashSet<string>(r.Cor.Select(a => a.Nome), StringComparer.OrdinalIgnoreCase);
                long uso = r.UsoApp; bool fez = false;
                bool Cabe(long tam) => uso + tam <= LimiteBytes;
                void Cheia() => Estado = $"☁ a nuvem do Cast chegou ao limite ({Gb(uso)} de {Pol.LimiteGb:0.#} GB): use \"Liberar espaço\" ou aumente o limite";

                // 1) homilias em pé (+ transcrição)
                if (Pol.EnviarHomilias && Directory.Exists(pastaMomentos))
                {
                    var corte = Pol.DiasHomilias > 0 ? DateTime.Now.AddDays(-Pol.DiasHomilias) : DateTime.MinValue;
                    foreach (var f in Directory.GetFiles(pastaMomentos, "*" + Vertical.Sufixo + ".mp4").Where(f => File.GetLastWriteTime(f) > corte).OrderBy(File.GetLastWriteTime))
                    {
                        ct.ThrowIfCancellationRequested();
                        var nome = Path.GetFileName(f); var txt = Cortes.ArquivoTexto(f); var nomeTxt = Path.GetFileName(txt);
                        if (Marca(nome) == null && !naNuvemH.Contains(nome))
                        {
                            if (esperar(f)) continue;
                            var red = Path.Combine(Path.GetTempPath(), "cast-nuvem-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
                            try
                            {
                                Reduzir(f, red, cod, ct, p => { Estado = $"☁ preparando \"{Base(nome)}\" para a nuvem… {p}%"; mudou?.Invoke(); });
                                File.SetLastWriteTime(red, File.GetLastWriteTime(f));
                                var tam = new FileInfo(red).Length;
                                if (!Cabe(tam)) { Cheia(); return; }
                                await SubirArquivo(idHom, nome, red, "video/mp4", null, ct, p => { Estado = $"☁ enviando \"{Base(nome)}\"… {p}%"; mudou?.Invoke(); });
                                uso += tam; Marcar(nome, "ok"); naNuvemH.Add(nome); fez = true;
                                Registro.Info($"nuvem: homilia enviada {nome} ({Gb(tam)} GB)");
                            }
                            finally { try { File.Delete(red); } catch { } }
                        }
                        else if (Marca(nome) == null) Marcar(nome, "ok");   // já estava lá (outro computador mandou)
                        // a transcrição (manda de novo quando alguém corrige uma frase)
                        if (File.Exists(txt) && naNuvemH.Contains(nome) && Marca(nomeTxt) != MarcaTexto(txt))
                        {
                            var existente = r.Hom.FirstOrDefault(a => a.Nome.Equals(nomeTxt, StringComparison.OrdinalIgnoreCase))?.Id;
                            await SubirArquivo(idHom, nomeTxt, txt, "application/json", existente, ct, null);
                            Marcar(nomeTxt, MarcaTexto(txt)); fez = true;
                        }
                    }
                }
                // 2) cortes prontos
                var pc = Path.Combine(pastaMomentos, "Cortes");
                if (Pol.EnviarCortes && Directory.Exists(pc))
                {
                    var corte = Pol.DiasCortes > 0 ? DateTime.Now.AddDays(-Pol.DiasCortes) : DateTime.MinValue;
                    foreach (var f in Directory.GetFiles(pc, "*.mp4").Where(f => !f.EndsWith(".parcial.mp4") && File.GetLastWriteTime(f) > corte && File.GetLastWriteTime(f) < DateTime.Now.AddSeconds(-10)).OrderBy(File.GetLastWriteTime))
                    {
                        ct.ThrowIfCancellationRequested();
                        var nome = Path.GetFileName(f);
                        if (Marca("corte:" + nome) != null) continue;
                        if (naNuvemC.Contains(nome)) { Marcar("corte:" + nome, "ok"); continue; }
                        var tam = new FileInfo(f).Length;
                        if (!Cabe(tam)) { Cheia(); return; }
                        await SubirArquivo(idCor, nome, f, "video/mp4", null, ct, p => { Estado = $"☁ enviando o corte \"{Path.GetFileNameWithoutExtension(nome)}\"… {p}%"; mudou?.Invoke(); });
                        uso += tam; Marcar("corte:" + nome, "ok"); naNuvemC.Add(nome); fez = true;
                        Registro.Info("nuvem: corte enviado " + nome);
                    }
                }
                if (fez) { await Ler(); Estado = "☁ nuvem em dia"; }
            }
            static string Gb(long b) => (b / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));

            // a versão em pé, mais leve (~2,5 Mbps: ~200 MB para 12 min), só para a nuvem
            static void Reduzir(string origem, string destino, string cod, CancellationToken ct, Action<int> pct)
            {
                var dur = Cortes.Duracao(origem);
                var args = $"-hide_banner -loglevel error -stats_period 2 -stats -y -i \"{origem}\" -map 0:v:0 -map 0:a:0? {Transmissor.ArgsVideo(cod, 2500)} -c:a aac -b:a 128k -movflags +faststart \"{destino}\"";
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, args) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
                p.Start(); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                string linha;
                while ((linha = p.StandardError.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } p.WaitForExit(3000); ct.ThrowIfCancellationRequested(); }
                    var m = System.Text.RegularExpressions.Regex.Match(linha, @"time=(\d+):(\d+):(\d+)");
                    if (m.Success && dur > 0) pct?.Invoke(Math.Min(99, (int)((int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value)) * 100 / dur)));
                    else if (!linha.StartsWith("frame=") && linha.Trim() != "") Registro.Info("ffmpeg (nuvem): " + linha);
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(destino)) throw new Exception("o FFmpeg não conseguiu preparar o vídeo (detalhes no registro)");
            }

            // ---------- baixar em casa ----------
            public static void Baixar(string nome, bool corte, string pastaMomentos, Action mudou)
            {
                if (Rodando) { Estado = "☁ espere o envio/recebimento atual terminar"; mudou?.Invoke(); return; }
                cts = new CancellationTokenSource(); var ct = cts.Token;
                tarefa = Task.Run(async () =>
                {
                    try
                    {
                        var r = Ultimo ?? await Ler();
                        var lista = corte ? r.Cor : r.Hom;
                        var a = lista.FirstOrDefault(x => x.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("não está mais na nuvem");
                        var destino = corte ? Path.Combine(pastaMomentos, "Cortes", a.Nome) : Path.Combine(pastaMomentos, a.Nome);
                        await BaixarArquivo(a.Id, destino, a.Tamanho, ct, p => { Estado = $"☁ baixando \"{Path.GetFileNameWithoutExtension(a.Nome)}\"… {p}%"; mudou?.Invoke(); });
                        File.SetLastWriteTime(destino, a.Quando);
                        if (corte) Marcar("corte:" + a.Nome, "ok");
                        else
                        {
                            Marcar(a.Nome, "ok");
                            var nt = Base(a.Nome) + ".cortes.json";
                            var t = r.Hom.FirstOrDefault(x => x.Nome.Equals(nt, StringComparison.OrdinalIgnoreCase));
                            if (t != null)
                            {
                                var dt = Path.Combine(pastaMomentos, nt);
                                await BaixarArquivo(t.Id, dt, t.Tamanho, ct, null);
                                Marcar(nt, MarcaTexto(dt));
                            }
                        }
                        Estado = $"✔ baixado: {Path.GetFileNameWithoutExtension(a.Nome)}";
                    }
                    catch (OperationCanceledException) { Estado = "☁ download interrompido"; }
                    catch (Exception e) { Estado = "☁ não deu para baixar: " + e.Message; Registro.Erro("nuvem: baixar", e); }
                    mudou?.Invoke();
                });
            }
        }
    }
}
