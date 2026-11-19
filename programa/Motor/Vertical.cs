// (0.1.15) Etapa 2 dos cortes da homilia (backlog 7.2): versão EM PÉ (9:16, 1080×1920) dos momentos gravados à parte,
// recortada no padre e acompanhando o rosto dele, para Reels e Stories.
// Roda DEPOIS da Missa (com a live e a gravação desligadas), com prioridade baixa:
//  1) o FFmpeg entrega 4 quadros por segundo, pequenos (640×360), e o detector de rostos (YuNet) acha o rosto do padre
//     (o maior no começo; depois, o mais perto de onde ele estava: não pula para outra pessoa);
//  2) a "câmera virtual" só anda quando o rosto sai do meio do quadro em pé, e anda devagar (como um operador);
//  3) o FFmpeg recorta quadro a quadro (sendcmd + crop) e grava "<nome> (em pé).mp4" ao lado do original.
// Nada sai do computador. A assembleia não aparece: o quadro em pé fica só no padre.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiguelAngelusCast.Rastreio;

namespace MiguelAngelusCast.Motor
{
    public static class Vertical
    {
        public static volatile string Estado = "";          // aparece em Ajustes › Transmissão e embaixo do Ao vivo
        public static bool Rodando => tarefa != null && !tarefa.IsCompleted;
        static Task tarefa; static CancellationTokenSource cts;
        // vídeos que não deram (ex.: nenhum rosto): guardados em arquivo, para não tentar de novo a cada abertura do Cast
static string ArqFalhas => Path.Combine(Program.PastaDados, "em-pe-falhas.txt");
static readonly HashSet<string> falhou = LerFalhas();
static HashSet<string> LerFalhas()
{
    var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try { if (File.Exists(ArqFalhas)) foreach (var l in File.ReadAllLines(ArqFalhas)) if (l.Trim() != "") h.Add(l.Trim()); } catch { }
    return h;
}
static void Falhou(string arq) { lock (falhou) { falhou.Add(arq); try { File.WriteAllLines(ArqFalhas, falhou); } catch { } } }
        public const string Sufixo = " (em pé)";
        const int Amostras = 4;                              // quadros analisados por segundo

        // o próximo momento gravado sem a versão em pé (dos últimos 7 dias)
        public static string Pendente(string pastaGravacao, string gravandoAgora)
        {
            try
            {
                var pasta = Path.Combine(pastaGravacao ?? "", "Momentos");
                if (!Directory.Exists(pasta)) return null;
                return Directory.GetFiles(pasta, "*.mp4")
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(Sufixo) && !f.EndsWith(".parcial.mp4"))
                    .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddDays(-7) && File.GetLastWriteTime(f) < DateTime.Now.AddSeconds(-20))
                    .Where(f => !string.Equals(f, gravandoAgora, StringComparison.OrdinalIgnoreCase) && !falhou.Contains(f))
                    .Where(f => !File.Exists(Destino(f)))
                    .OrderBy(File.GetLastWriteTime).FirstOrDefault();
            }
            catch { return null; }
        }
        public static string Destino(string f) => Path.Combine(Path.GetDirectoryName(f), Path.GetFileNameWithoutExtension(f) + Sufixo + ".mp4");

        public static void Iniciar(string arquivo, string codificador, Action mudou, double ini = -1, double fim = -1, string destino = null, int atrasoMs = 0)
        {
            if (Rodando) return;
            cts = new CancellationTokenSource(); var ct = cts.Token;
            tarefa = Task.Run(() =>
            {
                try { Fazer(arquivo, codificador, ct, mudou, ini, fim, destino, atrasoMs); }
                catch (OperationCanceledException) { Estado = "versão em pé interrompida (continua depois da Missa)"; }
                catch (Exception e) { Falhou(arquivo); Estado = "não deu para fazer a versão em pé de " + Path.GetFileName(arquivo) + ": " + e.Message; Registro.Erro("versão em pé", e); }
                mudou?.Invoke();
            });
        }
        // a live ou a gravação começou: para na hora (o computador é da transmissão)
        public static void Parar() { try { cts?.Cancel(); } catch { } }

        static void Fazer(string arq, string cod, CancellationToken ct, Action mudou, double ini = -1, double fim = -1, string destinoPedido = null, int atrasoMs = 0)
        {
            // (0.1.15) trecho de um vídeo qualquer (ex.: a Missa baixada do YouTube Studio): -ss/-to antes do -i
            var inv0 = CultureInfo.InvariantCulture;
            var corte = (ini > 0 ? $"-ss {ini.ToString("0.###", inv0)} " : "") + (fim > 0 ? $"-to {fim.ToString("0.###", inv0)} " : "");
            var nome = Path.GetFileNameWithoutExtension(arq);
            Estado = $"versão em pé de \"{nome}\": procurando o rosto…"; mudou?.Invoke();
            Registro.Info("versão em pé: começando " + arq);
            // ---------- 1) onde está o rosto, 4 vezes por segundo ----------
            const int W = 640, H = 360;
            var xs = new List<float>();   // centro do rosto (0..1 da largura) por amostra; NaN = sem rosto
            using (var rostos = new Rostos(Path.Combine(Program.Pasta, "modelos"), 1))
            using (var p = Abrir($"-hide_banner -loglevel error {corte}-i \"{arq}\" -an -vf setpts=PTS-STARTPTS,fps={Amostras},scale={W}:{H} -f rawvideo -pix_fmt bgra pipe:1", true))
            {
                var buf = new byte[W * H * 4]; var s = p.StandardOutput.BaseStream;
                float ant = float.NaN; var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } ct.ThrowIfCancellationRequested(); }
                    int lidos = 0;
                    while (lidos < buf.Length) { int n = s.Read(buf, lidos, buf.Length - lidos); if (n <= 0) break; lidos += n; }
                    if (lidos < buf.Length) break;
                    var l = rostos.Detectar(buf, W, H, 0.6f);
                    Rosto escolhido = null;
                    if (l.Count > 0)
                        escolhido = float.IsNaN(ant)
                            ? l.OrderByDescending(r => r.W * r.H).First()
                            // continuidade: o mais perto de onde estava, a menos que esteja longe demais (aí o maior)
                            : l.OrderBy(r => Math.Abs(r.Cx / W - ant) - r.W / W).First();
                    float x = escolhido == null ? float.NaN : escolhido.Cx / W;
                    if (!float.IsNaN(x) && !float.IsNaN(ant) && Math.Abs(x - ant) > 0.35f && l.Count > 1) x = float.NaN;   // salto grande com outra gente no quadro: ignora
                    xs.Add(x); if (!float.IsNaN(x)) ant = x;
                    if (xs.Count % (Amostras * 30) == 0) { Estado = $"versão em pé de \"{nome}\": procurando o rosto… {xs.Count / Amostras / 60}:{xs.Count / Amostras % 60:00} analisados"; mudou?.Invoke(); }
                }
                p.WaitForExit(5000);
                Registro.Info($"versão em pé: {xs.Count} amostras em {sw.Elapsed.TotalSeconds:0} s, rosto em {xs.Count(v => !float.IsNaN(v))}");
            }
            if (xs.Count == 0) throw new Exception("o vídeo não abriu");
            if (xs.All(float.IsNaN)) throw new Exception("nenhum rosto encontrado no vídeo");

            // ---------- 2) a câmera virtual: anda só quando o rosto sai do meio, e devagar ----------
            int L = 1920, A = 1080;   // a gravação do Cast é sempre na resolução da saída; o recorte usa a proporção
            (L, A) = Tamanho(arq) ?? (L, A);
            int cw = (int)Math.Round(A * 9.0 / 16 / 2) * 2;     // largura do recorte (1080 → 608)
            float primeiro = xs.First(v => !float.IsNaN(v));
            double c = primeiro * L, alvo = c, folga = cw * 0.18;
            var centros = new double[xs.Count];
            for (int i = 0; i < xs.Count; i++)
            {
                if (!float.IsNaN(xs[i])) { double f = xs[i] * L; if (Math.Abs(f - c) > folga) alvo = f; else if (Math.Abs(f - alvo) > folga) alvo = f; }
                c += (alvo - c) * 0.18;                           // ~1,5 s para chegar
                if (Math.Abs(alvo - c) < 2) alvo = c;
                centros[i] = Math.Clamp(c, cw / 2.0, L - cw / 2.0);
            }
            // média móvel de 1 s: tira os tremidos
            var suave = new double[centros.Length];
            for (int i = 0; i < centros.Length; i++)
            {
                int a = Math.Max(0, i - 2), b = Math.Min(centros.Length - 1, i + 2);
                double soma = 0; for (int k = a; k <= b; k++) soma += centros[k]; suave[i] = soma / (b - a + 1);
            }

            // ---------- 3) recorta e grava ----------
            var pasta = Path.GetDirectoryName(destinoPedido ?? arq);
            var cmds = Path.Combine(Path.GetTempPath(), "cast-empe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            var sb = new StringBuilder(); var inv = CultureInfo.InvariantCulture;
            const int qps = 30;
            int totalQ = (int)Math.Ceiling(suave.Length * (double)qps / Amostras);
            for (int q = 0; q < totalQ; q++)
            {
                double pos = q * (double)Amostras / qps; int i0 = Math.Min((int)pos, suave.Length - 1), i1 = Math.Min(i0 + 1, suave.Length - 1);
                double cx = suave[i0] + (suave[i1] - suave[i0]) * (pos - i0);
                int x = (int)Math.Round(cx - cw / 2.0); x = Math.Clamp(x, 0, L - cw);
                sb.Append((q / (double)qps).ToString("0.000", inv)).Append(" crop x ").Append(x).Append(";\n");
            }
            File.WriteAllText(cmds, sb.ToString());
            var destino = destinoPedido ?? Destino(arq); var parcial = Path.Combine(pasta, Path.GetFileNameWithoutExtension(destino) + ".parcial.mp4");
            int x0 = (int)Math.Clamp(Math.Round(suave[0] - cw / 2.0), 0, L - cw);
            var dur = suave.Length / (double)Amostras;
            Estado = $"versão em pé de \"{nome}\": gravando…"; mudou?.Invoke();
            try
            {
                var nomeCmds = Path.GetFileName(cmds);
                var args = $"-hide_banner -loglevel error -stats_period 2 -stats -y {corte}-i \"{arq}\" " +
                           $"-vf \"setpts=PTS-STARTPTS,sendcmd=f={nomeCmds},crop=w={cw}:h={A}:x={x0}:y=0,scale=1080:1920:flags=lanczos,setsar=1,format=yuv420p\" " +
                           $"-af {FiltroSom(atrasoMs)} {Transmissor.ArgsVideo(cod, 8000)} -r {qps} -shortest -c:a aac -b:a 160k -movflags +faststart \"{parcial}\"";
                using var p = Abrir(args, false, Path.GetTempPath());
                string linha;
                while ((linha = p.StandardError.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } p.WaitForExit(3000); try { File.Delete(parcial); } catch { } ct.ThrowIfCancellationRequested(); }
                    var m = System.Text.RegularExpressions.Regex.Match(linha, @"time=(\d+):(\d+):(\d+)");
                    if (m.Success && dur > 0)
                    {
                        var t = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
                        Estado = $"versão em pé de \"{nome}\": gravando… {Math.Min(99, (int)(t * 100 / dur))}%"; mudou?.Invoke();
                    }
                    else if (!linha.StartsWith("frame=") && linha.Trim() != "") Registro.Info("ffmpeg (em pé): " + linha);
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(parcial)) { try { File.Delete(parcial); } catch { } throw new Exception("o FFmpeg não conseguiu gravar (detalhes no registro)"); }
                File.Move(parcial, destino, true);
            }
            finally { try { File.Delete(cmds); } catch { } }
            Estado = $"✔ versão em pé pronta: {Path.GetFileName(destino)}";
            Registro.Info("versão em pé pronta: " + destino);
        }

        // (0.1.15) acerto do som de vídeos de fora (OBS/YouTube): + atrasa a voz (a imagem vem depois), − adianta
        static string FiltroSom(int ms)
        {
            if (ms > 0) return $"\"asetpts=PTS-STARTPTS,adelay={ms}:all=1\"";
            if (ms < 0) return $"\"atrim=start={(-ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS\"";
            return "asetpts=PTS-STARTPTS";
        }

        static Process Abrir(string args, bool saida, string pasta = null)
        {
            var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, args) { UseShellExecute = false, RedirectStandardOutput = saida, RedirectStandardError = !saida, CreateNoWindow = true, WorkingDirectory = pasta ?? "" } };
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            return p;
        }

        // largura × altura do vídeo (pela primeira linha "Stream ... Video: ..., 1920x1080")
        static (int, int)? Tamanho(string arq)
        {
            try
            {
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, $"-hide_banner -i \"{arq}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
                p.Start(); var t = p.StandardError.ReadToEnd(); p.WaitForExit(5000);
                var m = System.Text.RegularExpressions.Regex.Match(t, @"Video:.*?(\d{3,5})x(\d{3,5})");
                return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
            }
            catch { return null; }
        }
    }
}
