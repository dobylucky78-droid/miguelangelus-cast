// Agenda do MiguelAngelus (0.0.5): o Cast lê as próximas celebrações do MiguelAngelus da rede e deixa a capa da live
// e o Início em dia sozinhos (data, hora e o "Próxima Missa" do encerramento).
//  - Procura no próprio computador (127.0.0.1) e nos computadores que mandam a faixa NDI "MiguelAngelus".
//  - Endereço: http://<computador>:8930/api/cast/agenda (servidor do controle pelo celular; precisa estar ligado lá).
//  - Só leitura. Sem o MiguelAngelus, nada muda: a capa continua com o que foi digitado à mão.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class Celebracao
    {
        public DateTime Quando;
        public string Tipo = "", Texto = "", LocalNome = "", LocalSigla = "";
        public string Capa = "";            // id da ilustração do folheto no MiguelAngelus ("" = roteiro sem ilustração)
        public bool TemRoteiro;
        public bool EhMissa => Tipo == "Missa" || Tipo == "Crisma" || Tipo == "Primeira Eucaristia";
    }

    // (0.1.9) o que está no telão do MiguelAngelus agora (1.8.5: /api/cast/aovivo)
    public sealed class AoVivoMiguel
    {
        public string Roteiro = "", RoteiroId = "";
        public List<(string rot, string tit, string tipo)> Itens = new List<(string, string, string)>();
        public int VivoIdx = -1, Idx = -1, Total;
        public bool Preto, Altar;
        public string Texto => VivoIdx >= 0 && VivoIdx < Itens.Count ? Juntar(Itens[VivoIdx]) : "";
        public static string Juntar((string rot, string tit, string tipo) it) => string.IsNullOrEmpty(it.tit) ? it.rot : it.rot + " · " + it.tit;
    }

    public static class Miguel
    {
        // 8930; testes: CAST_PORTA_MIGUEL=outra (para uma cópia do MiguelAngelus)
        public static readonly int Porta = int.TryParse(Environment.GetEnvironmentVariable("CAST_PORTA_MIGUEL"), out var p) && p > 1024 ? p : 8930;
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(2.5) };
        static Timer timer;
        static int ocupado;

        public static List<Celebracao> Lista { get; private set; } = new List<Celebracao>();
        public static List<(string nome, string sigla)> Locais { get; private set; } = new List<(string, string)>();
        public static string Origem { get; private set; } = "";        // computador onde achou o MiguelAngelus
        public static string Paroquia { get; private set; } = "";
        public static string Brasao { get; private set; } = "";   // ids das imagens (0.0.7)
        public static string Logo { get; private set; } = "";
        public static DateTime Lido { get; private set; }
        public static string Erro { get; private set; } = "procurando…";
        public static event Action Mudou;

        // (0.1.1) a primeira busca logo no começo: a tela de abertura espera por ela
        public static void Iniciar() { timer ??= new Timer(_ => _ = Atualizar(), null, 300, 60000); linhaVivo ??= new Thread(Escutar) { IsBackground = true, Name = "telão do MiguelAngelus" }; if (!linhaVivo.IsAlive) linhaVivo.Start(); }

        // ---------- (0.1.9) seguir o telão: item e slide no ar, só com o "Seguir o MiguelAngelus" ligado ----------
        // Uma conexão aberta com o MiguelAngelus (/api/cast/eventos, 1.8.5): ele AVISA a cada troca de item ou slide —
        // o Cast não fica perguntando. Caiu (MiguelAngelus fechou, rede…): tenta de novo a cada 3 s.
        public static volatile bool Seguir;
        public static AoVivoMiguel AoVivo { get; private set; }
        public static long SeqAoVivo { get; private set; }          // muda a cada novidade
        public static string ErroAoVivo { get; private set; } = "";
        static Thread linhaVivo;
        static readonly HttpClient httpVivo = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        static void Escutar()
        {
            while (true)
            {
                if (!Seguir) { if (AoVivo != null) { AoVivo = null; SeqAoVivo++; } ErroAoVivo = ""; Thread.Sleep(500); continue; }
                bool conectou = false;
                var hosts = !string.IsNullOrEmpty(Origem) ? new[] { Origem }.Concat(Lugares().Where(h => h != Origem)).ToArray() : Lugares().ToArray();
                foreach (var host in hosts)
                {
                    try
                    {
                        using var cancelar = new CancellationTokenSource();
                        var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{Porta}/api/cast/eventos");
                        using var abrir = new CancellationTokenSource(3000);
                        using var resp = httpVivo.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, abrir.Token).GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType != "text/event-stream") continue;
                        using var leitor = new System.IO.StreamReader(resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), System.Text.Encoding.UTF8);
                        conectou = true; Origem = host; ErroAoVivo = "";
                        Registro.Info("seguindo o telão do MiguelAngelus em " + host);
                        // sem nada por 40 s (o MiguelAngelus manda um "pulso" a cada 15 s): a conexão morreu
                        var vigia = new Timer(_ => { try { cancelar.Cancel(); resp.Dispose(); } catch { } }, null, 40000, Timeout.Infinite);
                        try
                        {
                            string linha;
                            while (Seguir && (linha = leitor.ReadLine()) != null)
                            {
                                vigia.Change(40000, Timeout.Infinite);
                                if (!linha.StartsWith("data: ")) continue;
                                var a = LerVivo(linha.Substring(6));
                                if (a != null) { AoVivo = a; SeqAoVivo++; }
                            }
                        }
                        finally { vigia.Dispose(); }
                        if (!Seguir) break;
                        ErroAoVivo = "a conexão com o MiguelAngelus caiu — tentando de novo…"; SeqAoVivo++;
                        break;
                    }
                    catch { if (conectou) { ErroAoVivo = "a conexão com o MiguelAngelus caiu — tentando de novo…"; SeqAoVivo++; break; } }
                }
                if (!conectou)
                {
                    ErroAoVivo = string.IsNullOrEmpty(Origem) ? "MiguelAngelus não encontrado (aberto, com o Controle pelo celular ligado?)"
                        : "o MiguelAngelus em " + Origem + " não respondeu (precisa da versão 1.8.5 ou mais nova, com o Controle pelo celular ligado)";
                    if (AoVivo != null) { AoVivo = null; }
                    SeqAoVivo++;
                }
                for (int k = 0; k < 6 && Seguir; k++) Thread.Sleep(500);
            }
        }
        static AoVivoMiguel LerVivo(string texto)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var r = doc.RootElement;
                if (!r.TryGetProperty("itens", out var its)) return null;
                string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                int N(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
                bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                var a = new AoVivoMiguel { Roteiro = S(r, "roteiro"), RoteiroId = S(r, "roteiroId"), VivoIdx = N("vivoIdx"), Idx = N("idx"), Total = Math.Max(0, N("total")), Preto = B("preto"), Altar = B("altar") };
                foreach (var it in its.EnumerateArray()) a.Itens.Add((S(it, "rot"), S(it, "tit"), S(it, "tipo")));
                return a;
            }
            catch { return null; }
        }
        public static Task Agora() => Atualizar();

        // Computadores a tentar: este e os que mandam "XXX (MiguelAngelus …)" por NDI
        static IEnumerable<string> Lugares()
        {
            var l = new List<string> { "127.0.0.1" };
            try
            {
                foreach (var (nome, _) in Ndi.Fontes())
                {
                    var i = nome.IndexOf(" (", StringComparison.Ordinal);
                    if (i > 0 && nome.IndexOf("miguelangelus", StringComparison.OrdinalIgnoreCase) > i && nome.IndexOf("cast", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        var host = nome.Substring(0, i).Trim();
                        if (!l.Contains(host, StringComparer.OrdinalIgnoreCase)) l.Add(host);
                    }
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(Origem) && l.Remove(Origem)) l.Insert(0, Origem);   // o que funcionou da última vez primeiro
            return l;
        }

        static async Task Atualizar()
        {
            if (Interlocked.Exchange(ref ocupado, 1) == 1) return;
            try
            {
                foreach (var host in Lugares())
                {
                    string texto;
                    try { texto = await http.GetStringAsync($"http://{host}:{Porta}/api/cast/agenda"); }
                    catch { continue; }
                    if (Ler(texto)) { Origem = host; Erro = ""; Lido = DateTime.Now; await BaixarCapas(host); Mudou?.Invoke(); return; }
                }
                var tinha = Erro;
                Erro = "MiguelAngelus não encontrado (aberto, com o Controle pelo celular ligado?)";
                if (tinha != Erro) Mudou?.Invoke();
            }
            catch (Exception e) { Registro.Erro("agenda do MiguelAngelus", e); }
            finally { Interlocked.Exchange(ref ocupado, 0); }
        }

        // Ilustrações do folheto (0.0.6): ficam na pasta da capa (só neste computador; o backup não leva o folheto)
        // ids com "p" na frente: brasão e logo (PNG); os outros: ilustração (JPEG)
        public static string ArquivoCapa(string id) => System.IO.Path.Combine(Motor.Capa.Pasta,
            id.StartsWith("p") ? "marca-miguel-" + id + ".png" : "folheto-miguel-" + id + ".jpg");
        static async Task BaixarCapas(string host)
        {
            foreach (var id in Lista.Where(x => x.Capa != "").Select(x => x.Capa).Concat(new[] { Brasao, Logo }).Where(x => x != "").Distinct())
            {
                var arq = ArquivoCapa(id);
                if (System.IO.File.Exists(arq) || id.Any(ch => !char.IsLetterOrDigit(ch))) continue;
                try
                {
                    var b = await http.GetByteArrayAsync($"http://{host}:{Porta}/api/cast/capa?id={id}");
                    System.IO.Directory.CreateDirectory(Motor.Capa.Pasta);
                    System.IO.File.WriteAllBytes(arq, b);
                    Registro.Info("ilustração do folheto recebida do MiguelAngelus: " + id);
                }
                catch { }   // ainda não chegou ao servidor: tenta de novo no próximo minuto
            }
        }

        static bool Ler(string texto)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var r = doc.RootElement;
                if (!r.TryGetProperty("lista", out var lista)) return false;
                string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                var nova = new List<Celebracao>();
                foreach (var c in lista.EnumerateArray())
                {
                    if (!DateTime.TryParseExact(S(c, "data") + " " + S(c, "hora"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var q)) continue;
                    var loc = c.TryGetProperty("local", out var lo) ? lo : default;
                    nova.Add(new Celebracao
                    {
                        Quando = q, Tipo = S(c, "tipo"), Texto = S(c, "texto"), LocalNome = S(loc, "nome"), LocalSigla = S(loc, "sigla"), Capa = S(c, "capa"),
                        TemRoteiro = c.TryGetProperty("temRoteiro", out var tr) && tr.ValueKind == JsonValueKind.True
                    });
                }
                var locais = new List<(string, string)>();
                if (r.TryGetProperty("locais", out var ls)) foreach (var l in ls.EnumerateArray()) locais.Add((S(l, "nome"), S(l, "sigla")));
                Lista = nova.OrderBy(x => x.Quando).ToList();
                Locais = locais;
                Paroquia = S(r, "paroquia");
                var mar = r.TryGetProperty("marcas", out var mm) ? mm : default;
                Brasao = S(mar, "brasao"); Logo = S(mar, "logo");
                return true;
            }
            catch { return false; }
        }

        // As celebrações que este Cast transmite (filtro dos Ajustes da capa), a partir de "agora" (a que começou há
        // menos de 1h30 ainda conta: é a que está no ar)
        public static List<Celebracao> Transmitidas(CapaCfg c, DateTime agora) => Lista
            .Where(x => x.Quando > agora.AddMinutes(-90))
            .Where(x => !c.MiguelSoMissas || x.EhMissa)
            .Where(x => string.IsNullOrEmpty(c.MiguelLocal) || string.Equals(x.LocalSigla, c.MiguelLocal, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(x.LocalNome, c.MiguelLocal, StringComparison.OrdinalIgnoreCase))
            .ToList();

        static readonly CultureInfo pt = new CultureInfo("pt-BR");
        // "Domingo, 07h00" (+ capela, se o Cast transmite de várias)
        public static string TextoProxima(Celebracao x, bool comLocal)
        {
            var dia = (x.Quando.Date - DateTime.Today).Days switch
            {
                0 => "Hoje", 1 => "Amanhã",
                _ => pt.TextInfo.ToTitleCase(x.Quando.ToString("dddd", pt)).Replace("-Feira", "-feira")
            };
            if ((x.Quando.Date - DateTime.Today).Days >= 7) dia += x.Quando.ToString(" dd/MM", pt);
            var s = dia + ", " + x.Quando.ToString("HH'h'mm");
            if (comLocal && !string.IsNullOrEmpty(x.LocalSigla)) s += " · " + x.LocalSigla;
            return s;
        }

        // Aplica a agenda na capa: data/hora da próxima celebração transmitida e o texto da seguinte (encerramento).
        // Devolve a capa nova, ou null se nada mudou / sem agenda / desligado.
        public static CapaCfg Aplicar(CapaCfg c)
        {
            if (!c.SeguirMiguel || Lista.Count == 0) return null;
            var t = Transmitidas(c, DateTime.Now);
            if (t.Count == 0) return null;
            var comLocal = string.IsNullOrEmpty(c.MiguelLocal) && t.Select(x => x.LocalSigla).Distinct().Count() > 1;
            var quando = t[0].Quando;
            var proxima = t.Count > 1 ? TextoProxima(t[1], comLocal) : "";
            // ilustração do folheto da celebração (se o roteiro dela tem e já chegou); sem ela, fica a que estava
            var folheto = c.Folheto;
            var imagem = c.Imagem;
            if (c.MiguelFolheto && t[0].Capa != "" && System.IO.File.Exists(ArquivoCapa(t[0].Capa))) { folheto = ArquivoCapa(t[0].Capa); imagem = "folheto"; }
            // brasão e logo da paróquia (cantos de cima do telão do MiguelAngelus)
            string m1 = c.Marca1, m2 = c.Marca2;
            if (c.MiguelMarcas)
            {
                if (Brasao != "" && System.IO.File.Exists(ArquivoCapa(Brasao))) m1 = ArquivoCapa(Brasao);
                if (Logo != "" && System.IO.File.Exists(ArquivoCapa(Logo))) m2 = ArquivoCapa(Logo);
            }
            if (c.Quando == quando && c.Proxima == proxima && c.Folheto == folheto && c.Imagem == imagem && c.Marca1 == m1 && c.Marca2 == m2) return null;
            var n = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(c));
            n.Quando = quando; n.Proxima = proxima; n.Folheto = folheto; n.Imagem = imagem; n.Marca1 = m1; n.Marca2 = m2;
            return n;
        }
    }
}
