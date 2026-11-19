// (0.1.15) Etapa 3 dos cortes da homilia (backlog 7.3): transcrição NO COMPUTADOR (Whisper, MIT, pelo Whisper.net, MIT),
// sugestão de trechos para Reels e o corte com legenda grande.
// Nada sai do computador: o áudio, o texto e os cortes ficam na pasta Momentos.
//  - Transcrever: o FFmpeg tira o som (16 kHz mono) da versão em pé; o Whisper devolve as falas com o tempo de cada uma.
//  - Candidatos: trechos de 30 a 90 s que começam e terminam em frase completa, com nota por regras simples
//    (chamado no começo, perguntas, palavras de peso, conclusão, ênfase na voz; perde nota se começa "pendurado" no que
//    veio antes, ou com pausas longas). Quem escolhe de verdade é a equipe.
//  - Gerar: corta a versão em pé no trecho e queima a legenda (ASS, Segoe UI grande com contorno) no vídeo.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace MiguelAngelusCast.Motor
{
    public sealed class Fala { public double I { get; set; } public double F { get; set; } public string T { get; set; } = ""; }
    public sealed class Candidato
    {
        public double I { get; set; } public double F { get; set; } public double Nota { get; set; }
        public string Texto { get; set; } = ""; public string Motivo { get; set; } = "";
    }
    public sealed class Transcricao
    {
        public string Video { get; set; } = "";          // nome do arquivo (em pé), na mesma pasta
        public string Quando { get; set; } = "";
        public string Modelo { get; set; } = "";
        public List<Fala> Falas { get; set; } = new List<Fala>();
        public List<Candidato> Candidatos { get; set; } = new List<Candidato>();
        public double[] Energia { get; set; } = Array.Empty<double>();   // volume (dB) a cada segundo, para a "ênfase"
    }

    public static class Cortes
    {
        public const string NomeModelo = "ggml-medium-q5_0.bin";
        public const string UrlModelo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium-q5_0.bin";
        public static string CaminhoModelo => Path.Combine(Program.PastaDados, "modelos", NomeModelo);
        public static bool TemModelo => File.Exists(CaminhoModelo);
        // o andamento aparece no mesmo lugar da versão em pé (Ajustes › Transmissão e embaixo do Ao vivo)
        static volatile string estado = "";
        public static string Estado { get => estado; set { estado = value; Vertical.Estado = value; } }
        public static bool Rodando => tarefa != null && !tarefa.IsCompleted;
        static Task tarefa; static CancellationTokenSource cts;
        static readonly HashSet<string> falhou = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // (0.1.15) músicas de fundo dos cortes: a pasta "Músicas de fundo" dentro de Momentos (a tela toca por momentos.local)
        /// <summary>Volume percebido (LUFS integrado) do som do trecho; -16 se não der para medir (fica sem ganho).</summary>
        static double MedirLufs(string video, double ini, double fim)
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var a = $"-hide_banner -nostats -ss {ini.ToString("0.###", inv)} -to {fim.ToString("0.###", inv)} -i \"{video}\" -vn -af ebur128=framelog=quiet -f null -";
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, a) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
                p.Start(); var s = p.StandardError.ReadToEnd(); p.WaitForExit();
                var m = System.Text.RegularExpressions.Regex.Matches(s, @"I:\s+(-?[0-9.]+) LUFS");
                if (m.Count > 0 && double.TryParse(m[m.Count - 1].Groups[1].Value, NumberStyles.Float, inv, out var v) && v > -70) return v;
            }
            catch { }
            return -16;
        }
        public static string PastaVinhetas(string pastaMomentos) => Path.Combine(pastaMomentos, "Vinhetas");
        public static readonly HashSet<string> ExtVinhetas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".webm", ".m4v" };
        public static readonly HashSet<string> ExtMusicas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".wav", ".ogg", ".aac", ".flac", ".opus" };
        public static List<string> Vinhetas(string pastaMomentos)
        {
            try
            {
                var p = PastaVinhetas(pastaMomentos); Directory.CreateDirectory(p);
                return Directory.GetFiles(p).Where(f => ExtVinhetas.Contains(Path.GetExtension(f)) && !f.EndsWith(".parcial.mp4")).Select(Path.GetFileName).OrderBy(x => x).ToList();
            }
            catch { return new List<string>(); }
        }
        /// <summary>Duração de um vídeo em segundos (lida do FFmpeg); 0 se não der.</summary>
        internal static double Duracao(string arq)
        {
            try
            {
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, $"-hide_banner -i \"{arq}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
                p.Start(); var s = p.StandardError.ReadToEnd(); p.WaitForExit();
                var m = Regex.Match(s, @"Duration: (\d+):(\d+):(\d+(\.\d+)?)");
                if (m.Success) return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            catch { }
            return 0;
        }
        public static string PastaMusicas(string pastaMomentos) => Path.Combine(pastaMomentos, "Músicas de fundo");
        public static List<string> Musicas(string pastaMomentos)
        {
            try
            {
                var p = PastaMusicas(pastaMomentos); Directory.CreateDirectory(p);
                return Directory.GetFiles(p).Where(f => ExtMusicas.Contains(Path.GetExtension(f))).Select(Path.GetFileName).OrderBy(x => x).ToList();
            }
            catch { return new List<string>(); }
        }
        public static string ArquivoTexto(string video) => Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + ".cortes.json");
        static readonly JsonSerializerOptions js = new JsonSerializerOptions { WriteIndented = true };

        public static Transcricao Ler(string video)
        {
            try { var a = ArquivoTexto(video); return File.Exists(a) ? JsonSerializer.Deserialize<Transcricao>(File.ReadAllText(a)) : null; }
            catch (Exception e) { Registro.Erro("ler transcrição", e); return null; }
        }
        public static void Gravar(string video, Transcricao t) => File.WriteAllText(ArquivoTexto(video), JsonSerializer.Serialize(t, js));

        // a próxima versão em pé (dos últimos 7 dias) ainda sem transcrição
        public static string Pendente(string pastaGravacao)
        {
            try
            {
                var pasta = Path.Combine(pastaGravacao ?? "", "Momentos");
                if (!Directory.Exists(pasta)) return null;
                return Directory.GetFiles(pasta, "*" + Vertical.Sufixo + ".mp4")
                    .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddDays(-7) && !falhou.Contains(f) && !File.Exists(ArquivoTexto(f)))
                    .OrderBy(File.GetLastWriteTime).FirstOrDefault();
            }
            catch { return null; }
        }

        // (0.1.16) essa versão em pé ainda vai ser transcrita aqui? (a nuvem espera, para mandar o vídeo junto com o texto)
        public static bool Aguardando(string video) =>
            TemModelo && !falhou.Contains(video) && !File.Exists(ArquivoTexto(video)) && File.GetLastWriteTime(video) > DateTime.Now.AddDays(-7);

        // ---------- baixar o modelo (só quando a pessoa pede) ----------
        public static void BaixarModelo(Action mudou)
        {
            if (Rodando) return;
            cts = new CancellationTokenSource(); var ct = cts.Token;
            tarefa = Task.Run(async () =>
            {
                var destino = CaminhoModelo; var parcial = destino + ".parcial";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destino));
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromHours(2) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("MiguelAngelusCast");
                    using var r = await http.GetAsync(UrlModelo, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                    r.EnsureSuccessStatusCode();
                    long total = r.Content.Headers.ContentLength ?? 0, lido = 0; var ult = DateTime.MinValue;
                    using (var ent = await r.Content.ReadAsStreamAsync(ct))
                    using (var sai = File.Create(parcial))
                    {
                        var buf = new byte[1 << 16]; int n;
                        while ((n = await ent.ReadAsync(buf, ct)) > 0)
                        {
                            await sai.WriteAsync(buf.AsMemory(0, n), ct); lido += n;
                            if ((DateTime.Now - ult).TotalSeconds > 1) { ult = DateTime.Now; Estado = $"baixando o modelo de fala… {lido / 1048576} de {total / 1048576} MB"; mudou?.Invoke(); }
                        }
                    }
                    if (total > 0 && lido != total) throw new Exception("o download veio incompleto");
                    File.Move(parcial, destino, true);
                    Estado = "✔ modelo de fala pronto"; Registro.Info("modelo de fala baixado: " + destino);
                }
                catch (OperationCanceledException) { try { File.Delete(parcial); } catch { } Estado = "download do modelo interrompido"; }
                catch (Exception e) { try { File.Delete(parcial); } catch { } Estado = "não deu para baixar o modelo: " + e.Message; Registro.Erro("baixar modelo de fala", e); }
                mudou?.Invoke();
            });
        }

        // ---------- transcrever ----------
        public static void Iniciar(string video, Action mudou)
        {
            if (Rodando || !TemModelo) return;
            cts = new CancellationTokenSource(); var ct = cts.Token;
            tarefa = Task.Run(async () =>
            {
                try { await Transcrever(video, ct, mudou); }
                catch (OperationCanceledException) { Estado = "transcrição interrompida (continua depois da Missa)"; }
                catch (Exception e) { falhou.Add(video); Estado = "não deu para transcrever " + Path.GetFileName(video) + ": " + e.Message; Registro.Erro("transcrição", e); }
                mudou?.Invoke();
            });
        }
        public static void Parar() { try { cts?.Cancel(); } catch { } }

        static async Task Transcrever(string video, CancellationToken ct, Action mudou)
        {
            var nome = Path.GetFileNameWithoutExtension(video);
            Estado = $"transcrevendo \"{nome}\": lendo o som…"; mudou?.Invoke();
            Registro.Info("transcrição: começando " + video);
            var sw = Stopwatch.StartNew();
            // som 16 kHz mono em float
            var amostras = new List<float>(16000 * 60 * 15);
            using (var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, $"-hide_banner -loglevel error -i \"{video}\" -vn -ac 1 -ar 16000 -f f32le pipe:1") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } })
            {
                p.Start(); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var s = p.StandardOutput.BaseStream; var buf = new byte[1 << 16]; int n, sobra = 0; var resto = new byte[4];
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int i = 0;
                    if (sobra > 0) { while (sobra < 4 && i < n) resto[sobra++] = buf[i++]; if (sobra == 4) { amostras.Add(BitConverter.ToSingle(resto, 0)); sobra = 0; } }
                    for (; i + 4 <= n; i += 4) amostras.Add(BitConverter.ToSingle(buf, i));
                    while (i < n) resto[sobra++] = buf[i++];
                }
                p.WaitForExit(5000);
            }
            if (amostras.Count < 16000) throw new Exception("o vídeo não tem som");
            var som = amostras.ToArray(); amostras = null;
            // volume a cada segundo (dB)
            var energia = new double[som.Length / 16000];
            for (int k = 0; k < energia.Length; k++) { double q = 0; for (int j = k * 16000; j < (k + 1) * 16000; j++) q += som[j] * som[j]; energia[k] = 10 * Math.Log10(q / 16000 + 1e-10); }

            var dur = som.Length / 16000.0;
            Estado = $"transcrevendo \"{nome}\": 0%"; mudou?.Invoke();
            var falas = new List<Fala>();
            int threads = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);
            using (var fab = WhisperFactory.FromPath(CaminhoModelo))
            await using (var proc = fab.CreateBuilder().WithLanguage("pt").WithThreads(threads)
                .WithPrompt("Homilia da Santa Missa. Irmãos e irmãs, o Evangelho de hoje nos fala de Jesus Cristo, de Deus Pai e do Espírito Santo.")
                .WithProgressHandler(pc => { Estado = $"transcrevendo \"{nome}\": {pc}%"; mudou?.Invoke(); })
                .Build())
            {
                await foreach (var seg in proc.ProcessAsync(som, ct))
                {
                    var t = (seg.Text ?? "").Trim();
                    if (t == "" || t.StartsWith("[") || t.StartsWith("(")) continue;   // [Música], (risos)…
                    falas.Add(new Fala { I = Math.Round(seg.Start.TotalSeconds, 2), F = Math.Round(seg.End.TotalSeconds, 2), T = t });
                }
            }
            if (falas.Count == 0) throw new Exception("não reconheceu fala no vídeo");
            var tr = new Transcricao { Video = Path.GetFileName(video), Quando = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), Modelo = NomeModelo, Falas = falas, Energia = energia.Select(e => Math.Round(e, 1)).ToArray() };
            tr.Candidatos = Sugerir(tr);
            Gravar(video, tr);
            Registro.Info($"transcrição pronta: {falas.Count} falas, {tr.Candidatos.Count} candidatos, {sw.Elapsed.TotalSeconds:0} s para {dur:0} s de vídeo");
            Estado = $"✔ \"{nome}\" transcrito: {tr.Candidatos.Count} sugestões de corte (aba ✂ Cortes)";
        }

        // ---------- candidatos ----------
        sealed class Frase { public double I, F; public string T; }
        static string Sem(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }
        static readonly string[] abertura = { "irmaos", "meus irmaos", "minhas irmas", "queridos", "hoje", "jesus", "deus", "o senhor", "quem ", "quantas vezes", "voce", "nos ", "a morte", "a fe", "o amor", "precisamos", "nao tenha", "olhe", "pensem", "imagine" };
        static readonly string[] pendurado = { "e ", "mas ", "entao", "porque ", "pois ", "isso ", "isto ", "esse ", "essa ", "esses ", "essas ", "ele ", "ela ", "eles ", "elas ", "la ", "ai ", "ou seja", "como eu", "por exemplo", "tambem ", "inclusive" };
        static readonly string[] peso = { "precisamos", "devemos", "deus quer", "deus nos", "nao tenham medo", "nao tenha medo", "amor", "esperanca", " fe ", "vida eterna", "ressurreicao", "perdao", "misericordia", "coracao", "eternidade", "salvacao", "santidade", "ceu", "oracao", "familia" };
        static readonly string[] conclusao = { "por isso", "portanto", "que deus", "que o senhor", "amem", "assim seja", "fiquem com deus", "e isso que", "o que importa" };

        public static List<Candidato> Sugerir(Transcricao t)
        {
            // frases completas: junta as falas até um ponto final, interrogação ou exclamação
            var frases = new List<Frase>(); Frase cur = null;
            foreach (var f in t.Falas)
            {
                if (cur == null) cur = new Frase { I = f.I, F = f.F, T = f.T };
                else { cur.F = f.F; cur.T += " " + f.T; }
                if (Regex.IsMatch(f.T, @"[.!?…]\s*$")) { frases.Add(cur); cur = null; }
            }
            if (cur != null) frases.Add(cur);
            var en = t.Energia ?? Array.Empty<double>();
            double mediana = en.Length > 0 ? en.Where(e => e > -60).OrderBy(e => e).ElementAtOrDefault(en.Count(e => e > -60) / 2) : 0;

            var todos = new List<Candidato>();
            for (int i = 0; i < frases.Count; i++)
                for (int j = i; j < frases.Count; j++)
                {
                    double d = frases[j].F - frases[i].I;
                    if (d > 90) break;
                    if (d < 30) continue;
                    var texto = string.Join(" ", frases.Skip(i).Take(j - i + 1).Select(x => x.T));
                    var ini = Sem(frases[i].T).TrimStart(' ', '-', '"', '“');
                    var tudo = " " + Sem(texto) + " ";
                    double nota = 0; var mot = new List<string>();
                    if (abertura.Any(a => ini.StartsWith(a))) { nota += 2; mot.Add("começa chamando a atenção"); }
                    if (pendurado.Any(a => ini.StartsWith(a))) { nota -= 3; }
                    if (tudo.Contains("como eu disse") || tudo.Contains("como eu falei") || tudo.Contains("que eu falei")) nota -= 2;
                    int perg = Math.Min(3, texto.Count(c => c == '?'));
                    if (perg > 0) { nota += perg; mot.Add(perg == 1 ? "tem uma pergunta" : $"{perg} perguntas"); }
                    int pes = peso.Count(p => tudo.Contains(p)); if (pes > 0) { nota += Math.Min(3, pes * 0.6); if (pes >= 2) mot.Add("palavras de peso"); }
                    var fimT = " " + Sem(frases[j].T) + " ";
                    if (conclusao.Any(c => fimT.Contains(c)) || frases[j].T.TrimEnd().EndsWith("!")) { nota += 1.5; mot.Add("termina numa conclusão"); }
                    // pausas longas no meio quebram o ritmo
                    int pausas = 0; for (int k = i; k < j; k++) if (frases[k + 1].I - frases[k].F > 2.5) pausas++;
                    nota -= pausas;
                    // ênfase: volume médio do trecho acima do normal da homilia
                    if (en.Length > 0)
                    {
                        var parte = en.Skip((int)frases[i].I).Take(Math.Max(1, (int)d)).Where(e => e > -60).ToList();
                        if (parte.Count > 0) { var ac = parte.Average() - mediana; if (ac > 1) { nota += Math.Min(2, ac / 2); mot.Add("fala com ênfase"); } }
                    }
                    if (d >= 40 && d <= 70) nota += 1;
                    todos.Add(new Candidato { I = frases[i].I, F = frases[j].F, Nota = Math.Round(nota, 1), Texto = texto, Motivo = string.Join(", ", mot) });
                }
            // os 5 melhores, sem trechos que se sobrepõem
            var esc = new List<Candidato>();
            foreach (var c in todos.OrderByDescending(c => c.Nota))
            {
                if (esc.Any(e => Math.Min(e.F, c.F) - Math.Max(e.I, c.I) > 0)) continue;
                esc.Add(c); if (esc.Count == 5) break;
            }
            return esc.OrderBy(c => c.I).ToList();
        }

        // ---------- gerar o corte com legenda ----------
        public static void Gerar(string video, double ini, double fim, string cod, Action<string, string> pronto, Action mudou, string musica = null, int volumePct = 15, string abertura = null, string encerramento = null)
        {
            if (Rodando) { pronto?.Invoke(null, "espere terminar o que está rodando (" + Estado + ")"); return; }
            cts = new CancellationTokenSource(); var ct = cts.Token;
            tarefa = Task.Run(() =>
            {
                string saida = null, erro = null;
                try { saida = FazerCorte(video, ini, fim, cod, ct, mudou, musica, volumePct, abertura, encerramento); }
                catch (OperationCanceledException) { erro = "interrompido"; }
                catch (Exception e) { erro = e.Message; Registro.Erro("gerar corte", e); }
                Estado = erro == null ? "✔ corte pronto: " + Path.GetFileName(saida) : "não deu para gerar o corte: " + erro;
                mudou?.Invoke(); pronto?.Invoke(saida, erro);
            });
        }

        static string Tempo(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}"; }
        static string MinSeg(double s) => $"{(int)(s / 60)}m{(int)(s % 60):00}";

        static string FazerCorte(string video, double ini, double fim, string cod, CancellationToken ct, Action mudou, string musica = null, int volumePct = 15, string abertura = null, string encerramento = null)
        {
            if (fim - ini < 3) throw new Exception("trecho curto demais");
            var tr = Ler(video);
            var nome = Path.GetFileNameWithoutExtension(video).Replace(Vertical.Sufixo, "");
            var pasta = Path.Combine(Path.GetDirectoryName(video), "Cortes"); Directory.CreateDirectory(pasta);
            var destino = Path.Combine(pasta, $"{nome} - corte {MinSeg(ini)}-{MinSeg(fim)}.mp4");
            Estado = $"gerando o corte {MinSeg(ini)}–{MinSeg(fim)}…"; mudou?.Invoke();
            // legenda: blocos de até ~5 palavras, no tempo de cada fala (dividido pelo tamanho das palavras)
            var ass = new StringBuilder();
            ass.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1080\nPlayResY: 1920\nWrapStyle: 0\n\n[V4+ Styles]\n");
            ass.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            ass.Append("Style: Leg,Segoe UI,80,&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,6,2,2,90,90,430,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
            foreach (var f in (tr?.Falas ?? new List<Fala>()).Where(f => f.F > ini && f.I < fim))
            {
                var pal = f.T.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pal.Length == 0) continue;
                var blocos = new List<string>(); var b = new StringBuilder();
                foreach (var w in pal)
                {
                    if (b.Length > 0 && (b.Length + w.Length > 26 || Regex.IsMatch(b.ToString(), @"[.!?,;:]$"))) { blocos.Add(b.ToString()); b.Clear(); }
                    if (b.Length > 0) b.Append(' '); b.Append(w);
                }
                if (b.Length > 0) blocos.Add(b.ToString());
                double total = blocos.Sum(x => x.Length), t0 = f.I;
                foreach (var bl in blocos)
                {
                    double t1 = t0 + (f.F - f.I) * bl.Length / total;
                    double a = Math.Max(t0, ini) - ini, z = Math.Min(t1, fim) - ini;
                    if (z - a > 0.15) ass.Append($"Dialogue: 0,{Tempo(a)},{Tempo(z)},Leg,,0,0,0,,{bl.Replace("{", "(").Replace("}", ")")}\n");
                    t0 = t1;
                }
            }
            var tmp = Path.Combine(Path.GetTempPath(), "cast-corte-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "leg.ass"), ass.ToString(), new UTF8Encoding(false));
            var inv = CultureInfo.InvariantCulture; var parcial = destino + ".parcial.mp4";
            try
            {
                var fontes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts").Replace("\\", "/").Replace(":", "\\\\:");
                var d = fim - ini;
                string fd(double x) => x.ToString("0.##", inv);
                var vf = $"subtitles=leg.ass:fontsdir={fontes},fade=t=in:st=0:d=0.3,fade=t=out:st={fd(d - 0.4)}:d=0.4";
                // (0.1.15) volume da voz no padrão das redes (~ -16 LUFS): mede o trecho e sobe por igual, com limitador (som da igreja costuma vir baixo)
                double ganho = Math.Clamp(-16 - MedirLufs(video, ini, fim), -10, 30);
                var afVoz = $"volume={fd(ganho)}dB,alimiter=limit=0.89:level=false,afade=t=in:st=0:d=0.3,afade=t=out:st={fd(d - 0.4)}:d=0.4";
                // (0.1.15) vinhetas de abertura/encerramento (qualquer vídeo da pasta Vinhetas; horizontal entra inteiro sobre fundo desfocado)
                bool temMus = !string.IsNullOrEmpty(musica) && File.Exists(musica);
                double A = abertura != null && File.Exists(abertura) ? Math.Min(15, Duracao(abertura)) : 0;
                double E = encerramento != null && File.Exists(encerramento) ? Math.Min(15, Duracao(encerramento)) : 0;
                double T = A + d + E;
                var ins = new StringBuilder($"-ss {ini.ToString("0.###", inv)} -to {fim.ToString("0.###", inv)} -i \"{video}\" ");
                int n = 1, iMus = -1, iA = -1, iE = -1;
                if (temMus) { ins.Append($"-stream_loop -1 -i \"{musica}\" "); iMus = n++; }
                if (A > 0) { ins.Append($"-t {fd(A)} -i \"{abertura}\" "); iA = n++; }
                if (E > 0) { ins.Append($"-t {fd(E)} -i \"{encerramento}\" "); iE = n++; }
                const string norm = "fps=30,format=yuv420p,setsar=1";
                string Fit(int i, string o) =>
                    $"[{i}:v]split=2[{o}f][{o}b];[{o}b]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,scale=270:480,gblur=sigma=12,scale=1080:1920,colorchannelmixer=rr=0.8:gg=0.8:bb=0.8[{o}bb];" +
                    $"[{o}f]scale=1080:1920:force_original_aspect_ratio=decrease[{o}ff];[{o}bb][{o}ff]overlay=(W-w)/2:(H-h)/2,{norm}[{o}];";
                var fc = new StringBuilder($"[0:v]scale=1080:1920,{vf},{norm}[vc];");
                string partes = "";
                if (iA >= 0) { fc.Append(Fit(iA, "va")); partes += "[va]"; }
                partes += "[vc]";
                if (iE >= 0) { fc.Append(Fit(iE, "ve")); partes += "[ve]"; }
                fc.Append(iA >= 0 || iE >= 0 ? $"{partes}concat=n={(iA >= 0 ? 1 : 0) + 1 + (iE >= 0 ? 1 : 0)}:v=1:a=0[v];" : "[vc]null[v];");
                // voz: entra depois da abertura e fica com silêncio até o fim
                fc.Append($"[0:a]{afVoz},aformat=sample_rates=48000:channel_layouts=stereo");
                if (A > 0) fc.Append($",adelay={(int)(A * 1000)}:all=1");
                fc.Append($",apad=whole_dur={fd(T)},asetpts=N/SR/TB");
                if (temMus)
                {
                    // música de fundo: mais alta nas vinhetas, baixinha sob a voz (e abaixa sozinha quando há voz); repete se for curta
                    double vol = Math.Clamp(volumePct, 2, 60) / 100.0, alto = Math.Max(vol, 0.5);
                    string k1 = A > 0 ? $"clip(({fd(A + 1)}-t)/1.5\\,0\\,1)" : "0", k2 = E > 0 ? $"clip((t-{fd(A + d - 1)})/1.5\\,0\\,1)" : "0";
                    string volExpr = A > 0 || E > 0 ? $"volume={vol.ToString("0.###", inv)}+{(alto - vol).ToString("0.###", inv)}*max({k1}\\,{k2}):eval=frame" : $"volume={vol.ToString("0.###", inv)}";
                    fc.Append(",asplit=2[voz][chave];");
                    fc.Append($"[{iMus}:a]loudnorm=I=-16:TP=-2,aformat=sample_rates=48000:channel_layouts=stereo,asetpts=N/SR/TB,{volExpr},afade=t=in:st=0:d={(A > 0 ? "0.3" : "1.5")},afade=t=out:st={fd(Math.Max(0, T - 2))}:d=2[mus];");
                    fc.Append("[mus][chave]sidechaincompress=threshold=0.02:ratio=4:attack=30:release=600[musb];[voz][musb]amix=inputs=2:duration=first:normalize=0,asetpts=N/SR/TB[a]");
                }
                else fc.Append("[a]");   // (asetpts acima: com adelay/apad o FFmpeg às vezes marcava o início do som num tempo absurdo e o celular não tocava)
                var args = $"-hide_banner -loglevel error -y {ins}-filter_complex \"{fc}\" -map [v] -map [a] -t {fd(T)} " +
                           $"{Transmissor.ArgsVideo(cod, 8000)} -c:a aac -b:a 160k -movflags +faststart \"{parcial}\"";
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, args) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = tmp } };
                p.Start(); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var err = p.StandardError.ReadToEndAsync();
                while (!p.WaitForExit(500)) if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } ct.ThrowIfCancellationRequested(); }
                if (p.ExitCode != 0 || !File.Exists(parcial)) { Registro.Info("ffmpeg (corte): " + err.Result); throw new Exception("o FFmpeg não conseguiu gravar (detalhes no registro)"); }
                File.Move(parcial, destino, true);
            }
            finally { try { File.Delete(parcial); } catch { } try { Directory.Delete(tmp, true); } catch { } }
            Registro.Info("corte pronto: " + destino);
            return destino;
        }
    }
}
