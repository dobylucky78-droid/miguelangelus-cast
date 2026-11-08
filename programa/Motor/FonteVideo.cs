// Vídeo do computador (MP4, MOV…): imagem para a cena e som para a transmissão.
// Decodifica com o FFmpeg (LGPL, processo separado) em dois processos: imagem (BGRA já no tamanho da saída)
// e som (48 kHz estéreo). Fica pronto no 1º quadro; toca do começo quando a cena entra no programa
// e volta ao começo quando sai. No fim, segura o último quadro.
// O relógio do vídeo manda: imagem e som andam pelo tempo decorrido (se a mesa atrasar um quadro, ele pula;
// se ninguém estiver lendo o som — sem gravar nem transmitir — o som é descartado no mesmo ritmo).
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace MiguelAngelusCast.Motor
{
    public sealed class FonteVideo : Fonte
    {
        public static readonly ConcurrentDictionary<FonteVideo, byte> Ativas = new ConcurrentDictionary<FonteVideo, byte>();
        const int Taxa = 48000;

        readonly string caminho; readonly int L, A, qps;
        readonly Imagem img;
        Process pv, pa; int geracao;
        BlockingCollection<byte[]> quadros; readonly ConcurrentBag<byte[]> sobras = new ConcurrentBag<byte[]>();
        volatile bool fimVideo;

        // som: anel de 2 s (estéreo intercalado)
        readonly object travaSom = new object();
        readonly float[] anel = new float[Taxa * 2 * 2]; int ini, qtd;
        long consumidos;                // quadros de som já entregues/descartados desde o play
        volatile bool temSom;

        Stopwatch relogio; long mostrados; bool noAr, tocou, primeiro;
        double duracao;                 // s (do FFmpeg)

        public bool Tocando => relogio != null && !terminou;
        public bool TocandoComSom => Tocando && temSom;
        bool terminou;

        public FonteVideo(string caminho, int l, int a, int qps)
        {
            this.caminho = caminho; L = l; A = a; this.qps = qps;
            Spec = "video:" + caminho; img = new Imagem(l, a); Desenho.Preto(img);
            Ativas[this] = 0;
            Preparar();
        }

        // abre os dois processos parados no começo (o FFmpeg espera enquanto não lemos)
        void Preparar()
        {
            Parar();
            int g = ++geracao;
            fimVideo = false; terminou = false; relogio = null; mostrados = 0; primeiro = false; tocou = false;
            lock (travaSom) { ini = qtd = 0; consumidos = 0; }
            if (!File.Exists(caminho)) { Estado = "arquivo não encontrado"; return; }
            if (!File.Exists(Transmissor.Ffmpeg)) { Estado = "FFmpeg não encontrado"; return; }
            Estado = "abrindo";
            try
            {
                var vf = $"fps={qps},scale={L}:{A}:force_original_aspect_ratio=decrease,pad={L}:{A}:(ow-iw)/2:(oh-ih)/2,format=bgra";
                pv = Iniciar($"-hide_banner -nostats -i \"{caminho}\" -map 0:v:0 -vf \"{vf}\" -f rawvideo -");
                pv.ErrorDataReceived += (s, e) =>
                {
                    var m = e.Data == null ? null : Regex.Match(e.Data, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (m != null && m.Success && duracao == 0)
                        duracao = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                };
                pv.BeginErrorReadLine();
                quadros = new BlockingCollection<byte[]>(3);
                var q = quadros; var proc = pv;
                new Thread(() => LerImagem(proc, q, g)) { IsBackground = true, Name = "video-img" }.Start();

                pa = Iniciar($"-hide_banner -nostats -loglevel error -i \"{caminho}\" -map 0:a:0? -vn -ac 2 -ar {Taxa} -f f32le -");
                pa.ErrorDataReceived += (s, e) => { };
                pa.BeginErrorReadLine();
                var procA = pa;
                new Thread(() => LerSomDoArquivo(procA, g)) { IsBackground = true, Name = "video-som" }.Start();
            }
            catch (Exception e) { Estado = "não abriu o vídeo"; Registro.Erro("vídeo " + caminho, e); }
        }

        static Process Iniciar(string args)
        {
            var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            return p;
        }

        void LerImagem(Process p, BlockingCollection<byte[]> q, int g)
        {
            int tam = L * A * 4;
            try
            {
                var s = p.StandardOutput.BaseStream;
                while (g == geracao)
                {
                    if (!sobras.TryTake(out var buf) || buf.Length != tam) buf = new byte[tam];
                    int lido = 0;
                    while (lido < tam) { int r = s.Read(buf, lido, tam - lido); if (r <= 0) break; lido += r; }
                    if (lido < tam) break;
                    q.Add(buf);
                }
            }
            catch { }
            if (g == geracao) fimVideo = true;
        }

        void LerSomDoArquivo(Process p, int g)
        {
            var b = new byte[Taxa / 50 * 8];   // 20 ms
            var f = new float[b.Length / 4];
            int resto = 0;
            try
            {
                var s = p.StandardOutput.BaseStream;
                while (g == geracao)
                {
                    int r = s.Read(b, resto, b.Length - resto);
                    if (r <= 0) break;
                    temSom = true;
                    int n = (resto + r) / 8 * 8;      // só quadros inteiros (2 canais × 4 bytes)
                    Buffer.BlockCopy(b, 0, f, 0, n);
                    resto = resto + r - n; if (resto > 0) Buffer.BlockCopy(b, n, b, 0, resto);
                    // espera ter espaço no anel (o FFmpeg fica parado enquanto isso)
                    while (g == geracao)
                    {
                        lock (travaSom)
                        {
                            if (anel.Length - qtd >= n / 4)
                            {
                                for (int i = 0; i < n / 4; i++) anel[(ini + qtd + i) % anel.Length] = f[i];
                                qtd += n / 4; break;
                            }
                        }
                        Thread.Sleep(5);
                    }
                }
            }
            catch { }
        }

        // chamado pela mesa a cada quadro: a cena deste vídeo está no ar?
        public void NoAr(bool sim)
        {
            if (sim == noAr) return;
            noAr = sim;
            if (sim)
            {
                if (tocou) Preparar();     // já tocou antes: volta ao começo
                tocou = true; relogio = Stopwatch.StartNew(); mostrados = Math.Max(mostrados, 1);
                Registro.Info("vídeo tocando: " + Path.GetFileName(caminho));
            }
            else if (tocou) Preparar();
        }

        public override Imagem Atual()
        {
            if (quadros == null) return img;
            // 1º quadro (pronto para o preview)
            if (!primeiro && quadros.TryTake(out var b0)) { Mostrar(b0); primeiro = true; mostrados = 1; if (relogio == null) Estado = "pronto" + Tempo(0); }
            if (relogio == null || terminou) return img;

            double t = relogio.Elapsed.TotalSeconds;
            long devidos = (long)(t * qps) + 1;
            byte[] ultimo = null;
            while (mostrados < devidos && quadros.TryTake(out var b)) { if (ultimo != null) sobras.Add(ultimo); ultimo = b; mostrados++; }
            if (ultimo != null) Mostrar(ultimo);
            else if (fimVideo && quadros.Count == 0 && mostrados < devidos) { terminou = true; Estado = "terminou"; Registro.Info("vídeo terminou: " + Path.GetFileName(caminho)); return img; }

            // som no mesmo relógio: se ninguém leu (sem gravar/transmitir), descarta
            long alvo = (long)(t * Taxa);
            lock (travaSom)
            {
                if (consumidos < alvo - Taxa * 6 / 100)
                {
                    int jogar = (int)Math.Min(qtd / 2, alvo - consumidos);
                    ini = (ini + jogar * 2) % anel.Length; qtd -= jogar * 2; consumidos += jogar;
                }
            }
            var est = "tocando" + Tempo(t);
            if (est != Estado) Estado = est;
            return img;
        }

        string Tempo(double t)
        {
            static string F(double s) { var ts = TimeSpan.FromSeconds(Math.Max(0, s)); return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"); }
            return duracao > 0 ? (relogio == null ? " · " + F(duracao) : " " + F(t) + " / " + F(duracao)) : (relogio == null ? "" : " " + F(t));
        }

        void Mostrar(byte[] b) { Buffer.BlockCopy(b, 0, img.Px, 0, Math.Min(b.Length, img.Px.Length)); Seq++; sobras.Add(b); }

        // som do vídeo para a saída (soma em dst com o ganho dado); devolve se havia som
        public void SomarSom(float[] dst, int quadrosPedidos, float ganho)
        {
            if (!TocandoComSom) return;
            lock (travaSom)
            {
                int n = Math.Min(quadrosPedidos, qtd / 2);
                for (int i = 0; i < n * 2; i++) dst[i] += anel[(ini + i) % anel.Length] * ganho;
                ini = (ini + n * 2) % anel.Length; qtd -= n * 2; consumidos += n;
            }
        }

        void Parar()
        {
            geracao++;
            try { quadros?.CompleteAdding(); } catch { }
            while (quadros != null && quadros.TryTake(out var b)) sobras.Add(b);
            foreach (var p in new[] { pv, pa })
            {
                if (p == null) continue;
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            pv = pa = null; temSom = false;
        }

        public override void Dispose() { Ativas.TryRemove(this, out _); Parar(); }
    }
}
