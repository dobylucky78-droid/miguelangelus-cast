// Gravar e transmitir. O programa da mesa é codificado UMA vez (FFmpeg, em MPEG-TS) e repassado a um
// "retransmissor" por destino (YouTube, Facebook, Instagram, gravação). Se um destino cai, só ele reconecta.
// As chaves ficam no Cofre (criptografadas) e nunca aparecem no registro.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class DestinoCfg
    {
        public string Id { get; set; }
        public string Nome { get; set; }
        public string Tipo { get; set; }          // youtube | facebook | instagram | rtmp
        public string Servidor { get; set; }
        public bool Ligado { get; set; } = true;
    }

    public sealed class SaidaCfg
    {
        public int Kbps { get; set; } = 4500;
        public int KbpsAudio { get; set; } = 160;
        public string Codificador { get; set; } = "auto";
        public string CodificadorDetectado { get; set; }
        public string PastaGravacao { get; set; }
        public List<DestinoCfg> Destinos { get; set; } = new List<DestinoCfg>();
    }

    public static class Servidores
    {
        public static string Padrao(string tipo) => tipo switch
        {
            "youtube" => "rtmp://a.rtmp.youtube.com/live2",
            "facebook" => "rtmps://live-api-s.facebook.com:443/rtmp/",
            _ => ""
        };
    }

    public sealed class Transmissor : IDisposable
    {
        static readonly string arquivoCfg = Path.Combine(Program.PastaDados, "saida.json");
        public static string Ffmpeg => Path.Combine(Program.Pasta, "ffmpeg", "ffmpeg.exe");
        public SaidaCfg Cfg { get; private set; }
        readonly object trava = new object();

        int L, A, Qps;
        Process cod; Thread escritor, leitor, erros;
        volatile bool codRodando;
        byte[] yuvPronto, yuvTrab; volatile bool temQuadro;
        readonly Dictionary<string, Retransmissor> rets = new Dictionary<string, Retransmissor>();
        public bool AoVivo { get; private set; }
        public bool Gravando { get; private set; }
        Stopwatch relAr, relGrav; string arquivoGravacao;
        public string TempoAr => relAr == null ? "" : relAr.Elapsed.ToString(@"hh\:mm\:ss");      // (0.1.6) teclas do Stream Dock
        public string TempoGrav => relGrav == null ? "" : relGrav.Elapsed.ToString(@"hh\:mm\:ss");
        public long QuadrosEnviados, QuadrosPerdidos; public string CodUsado = "", ErroCod = "";
        public event Action Mudou;
        public Som Som;                                  // de onde vem o áudio (sem ele: silêncio)
        NamedPipeServerStream canoAudio; Thread escritorAudio; static int nCano;
        long audioPular;                                 // quadros de áudio a pular junto com o vídeo (mantém a sincronia)

        public Transmissor()
        {
            try { if (File.Exists(arquivoCfg)) Cfg = JsonSerializer.Deserialize<SaidaCfg>(File.ReadAllText(arquivoCfg)); } catch (Exception e) { Registro.Erro("ler saida.json", e); }
            Cfg ??= new SaidaCfg();
            if (Cfg.Destinos.Count == 0)
            {
                Cfg.Destinos.Add(new DestinoCfg { Id = "youtube", Nome = "YouTube", Tipo = "youtube", Servidor = Servidores.Padrao("youtube") });
                Cfg.Destinos.Add(new DestinoCfg { Id = "facebook", Nome = "Facebook", Tipo = "facebook", Servidor = Servidores.Padrao("facebook") });
                Cfg.Destinos.Add(new DestinoCfg { Id = "instagram", Nome = "Instagram", Tipo = "instagram", Servidor = "", Ligado = false });
            }
            Cfg.PastaGravacao ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MiguelAngelus Cast");
        }
        public void Salvar() { try { File.WriteAllText(arquivoCfg, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { Registro.Erro("gravar saida.json", e); } }

        public void Configurar(int largura, int altura, int qps) { L = largura; A = altura; Qps = qps; }

        // ---------- chaves: tudo o que vai para o registro passa por aqui ----------
        public string Mascarar(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            foreach (var d in Cfg.Destinos) { var k = Cofre.Ler("chave:" + d.Id); if (!string.IsNullOrEmpty(k) && k.Length >= 4) t = t.Replace(k, "••••"); }
            return Regex.Replace(t, @"(rtmps?://[^\s'""]+/)[^\s/'""]{8,}", "$1••••");
        }

        // ---------- ligar e desligar ----------
        public string Gravar(bool ligar)
        {
            lock (trava)
            {
                if (ligar == Gravando) return null;
                if (ligar)
                {
                    var erro = GarantirCodificador(); if (erro != null) return erro;
                    Directory.CreateDirectory(Cfg.PastaGravacao);
                    arquivoGravacao = Path.Combine(Cfg.PastaGravacao, "Cast " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4");
                    var r = new Retransmissor(this, "gravacao", "Gravação", "-c copy -bsf:a aac_adtstoasc -f mp4 -movflags +frag_keyframe+empty_moov+default_base_moof \"" + arquivoGravacao + "\"", false);
                    rets["gravacao"] = r; r.Iniciar();
                    Gravando = true; relGrav = Stopwatch.StartNew();
                    Registro.Info("gravando em " + arquivoGravacao);
                }
                else
                {
                    if (rets.TryGetValue("gravacao", out var r)) { r.Parar(); rets.Remove("gravacao"); }
                    Gravando = false; relGrav = null; Registro.Info("gravação parada");
                    PararCodificadorSeOcioso();
                }
            }
            Mudou?.Invoke(); return null;
        }

        public string Transmitir(bool ligar)
        {
            lock (trava)
            {
                if (ligar == AoVivo) return null;
                if (ligar)
                {
                    var ativos = Cfg.Destinos.Where(d => d.Ligado).ToList();
                    if (ativos.Count == 0) return "Nenhum destino ligado. Ligue YouTube, Facebook ou Instagram em Ajustes › Transmissão.";
                    var semChave = ativos.Where(d => !Cofre.Tem("chave:" + d.Id) || string.IsNullOrWhiteSpace(d.Servidor)).Select(d => d.Nome).ToList();
                    if (semChave.Count > 0) return "Falta o servidor ou a chave de: " + string.Join(", ", semChave) + " (Ajustes › Transmissão).";
                    var erro = GarantirCodificador(); if (erro != null) return erro;
                    foreach (var d in ativos)
                    {
                        var url = d.Servidor.TrimEnd('/') + "/" + Cofre.Ler("chave:" + d.Id);
                        var r = new Retransmissor(this, d.Id, d.Nome, "-c copy -bsf:a aac_adtstoasc -f flv -flvflags no_duration_filesize \"" + url + "\"", true);
                        rets[d.Id] = r; r.Iniciar();
                    }
                    AoVivo = true; relAr = Stopwatch.StartNew();
                    Registro.Info("transmissão iniciada: " + string.Join(", ", ativos.Select(d => d.Nome)));
                }
                else
                {
                    foreach (var k in rets.Keys.Where(k => k != "gravacao").ToList()) { rets[k].Parar(); rets.Remove(k); }
                    AoVivo = false; relAr = null; Registro.Info("transmissão encerrada");
                    PararCodificadorSeOcioso();
                }
            }
            Mudou?.Invoke(); return null;
        }

        // ---------- codificador ----------
        string GarantirCodificador()
        {
            if (codRodando) return null;
            if (!File.Exists(Ffmpeg)) return "FFmpeg não encontrado na pasta do programa (ffmpeg\\ffmpeg.exe).";
            var c = EscolherCodificador();
            if (c == null) return "Nenhum codificador de vídeo funcionou neste computador.";
            CodUsado = c; ErroCod = ""; ultimaLinhaCod = null; QuadrosEnviados = QuadrosPerdidos = 0;
            int tam = L * A * 3 / 2;
            yuvPronto = new byte[tam]; yuvTrab = new byte[tam]; temQuadro = false;
            var nomeCano = $"castaudio_{Environment.ProcessId}_{Interlocked.Increment(ref nCano)}";
            canoAudio = new NamedPipeServerStream(nomeCano, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 16);
            Interlocked.Exchange(ref audioPular, 0);
            var args = $"-hide_banner -loglevel warning -stats_period 1 -stats " +
                       $"-f rawvideo -pix_fmt yuv420p -video_size {L}x{A} -framerate {Qps} -i pipe:0 " +
                       $"-f f32le -ar {Som.Taxa} -ac 2 -i \\\\.\\pipe\\{nomeCano} " +
                       $"-map 0:v -map 1:a {ArgsVideo(c, Cfg.Kbps)} -g {Qps * 2} -keyint_min {Qps * 2} " +
                       $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv " +
                       $"-c:a aac -b:a {Cfg.KbpsAudio}k -ar 48000 -bsf:v h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1:video_full_range_flag=0,dump_extra -f mpegts -flush_packets 1 pipe:1";
            cod = new Process { StartInfo = new ProcessStartInfo(Ffmpeg, args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            cod.Start();
            try { cod.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            codRodando = true;
            Registro.Info($"codificador: {c} {Cfg.Kbps} kbps {L}x{A} {Qps} q/s");
            relogioCod = Stopwatch.StartNew();
            escritor = new Thread(Escrever) { IsBackground = true, Name = "cod escrita", Priority = ThreadPriority.AboveNormal }; escritor.Start();
            escritorAudio = new Thread(EscreverAudio) { IsBackground = true, Name = "cod audio", Priority = ThreadPriority.AboveNormal }; escritorAudio.Start();
            leitor = new Thread(Distribuir) { IsBackground = true, Name = "cod leitura" }; leitor.Start();
            erros = new Thread(() => LerErros(cod.StandardError)) { IsBackground = true, Name = "cod erros" }; erros.Start();
            // se o codificador não aguenta nem abrir, avisa agora (e não finge que está gravando)
            if (cod.WaitForExit(700))
            {
                codRodando = false; Thread.Sleep(100); try { canoAudio.Dispose(); } catch { }
                return "O codificador não abriu: " + (ultimaLinhaCod ?? "erro desconhecido") + " (detalhes no registro)";
            }
            return null;
        }

        static string ArgsVideo(string c, int k) => c switch
        {
            "h264_mf" => $"-c:v h264_mf -rate_control cbr -scenario live_streaming -b:v {k}k",
            "libopenh264" => $"-c:v libopenh264 -rc_mode bitrate -b:v {k}k -allow_skip_frames 0",
            "h264_qsv" => $"-c:v h264_qsv -preset veryfast -look_ahead 0 -b:v {k}k -maxrate {k}k",
            "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -tune ll -rc cbr -b:v {k}k",
            "h264_amf" => $"-c:v h264_amf -usage lowlatency -rc cbr -b:v {k}k",
            _ => $"-c:v {c} -b:v {k}k"
        };

        // "auto": a placa de vídeo primeiro (Intel, NVIDIA, AMD), depois o do Windows, depois o OpenH264. Testa uma vez e guarda.
        string EscolherCodificador()
        {
            if (Cfg.Codificador != "auto") return Cfg.Codificador;
            if (!string.IsNullOrEmpty(Cfg.CodificadorDetectado) && Testar(Cfg.CodificadorDetectado)) return Cfg.CodificadorDetectado;
            foreach (var c in new[] { "h264_qsv", "h264_nvenc", "h264_amf", "h264_mf", "libopenh264" })
                if (Testar(c)) { Cfg.CodificadorDetectado = c; Salvar(); Registro.Info("codificador escolhido: " + c); return c; }
            return null;
        }
        public static bool Testar(string c)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Ffmpeg, $"-hide_banner -loglevel error -f lavfi -i color=size=640x360:rate=30 -t 0.5 {ArgsVideo(c, 1000)} -f null -") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                p.StandardError.ReadToEnd();
                return p.WaitForExit(8000) && p.ExitCode == 0;
            }
            catch { return false; }
        }

        void PararCodificadorSeOcioso()
        {
            if (AoVivo || Gravando || !codRodando) return;
            codRodando = false;
            try { cod.StandardInput.Close(); } catch { }
            try { canoAudio?.Dispose(); } catch { }
            Task.Run(() => { try { if (!cod.WaitForExit(4000)) cod.Kill(); } catch { } });
            Registro.Info($"codificador parado ({QuadrosEnviados} quadros, {QuadrosPerdidos} perdidos)");
        }

        // chamado pela mesa a cada quadro: converte para YUV 4:2:0 (só quando há saída ligada)
        public void Receber(Imagem pg)
        {
            if (!codRodando || pg.L != L || pg.A != A) return;
            Desenho.ParaYuv420(pg, yuvTrab);
            lock (trava) { (yuvPronto, yuvTrab) = (yuvTrab, yuvPronto); temQuadro = true; }
        }

        // entrega ao FFmpeg no ritmo exato (repete o último quadro se a mesa atrasar: o tempo do vídeo não escorrega)
        void Escrever()
        {
            var rel = relogioCod; double intervalo = 1000.0 / Qps; long n = 0;
            var saida = cod.StandardInput.BaseStream; var buf = new byte[yuvPronto.Length];
            while (codRodando)
            {
                double alvo = n * intervalo, agora = rel.Elapsed.TotalMilliseconds;
                if (agora < alvo) { Thread.Sleep(Math.Max(0, (int)(alvo - agora) - 1)); continue; }
                if (agora - alvo > intervalo * 4) { long pular = (long)((agora - alvo) / intervalo) - 1; n += pular; Interlocked.Add(ref audioPular, pular * Som.Taxa / Qps); if (agora > 3000) QuadrosPerdidos += pular; }   // nos 3 s iniciais o codificador ainda está abrindo
                if (temQuadro) lock (trava) Buffer.BlockCopy(yuvPronto, 0, buf, 0, buf.Length);
                try { saida.Write(buf, 0, buf.Length); QuadrosEnviados++; }
                catch (Exception e) { if (codRodando) { ErroCod = "o codificador parou"; Registro.Erro("escrever no codificador", e); } break; }
                n++;
            }
        }

        Stopwatch relogioCod; volatile string ultimaLinhaCod;

        // áudio no mesmo relógio do vídeo: blocos de 10 ms; se a entrada atrasa, completa com silêncio
        void EscreverAudio()
        {
            const int bloco = Som.Taxa / 100;
            var amostras = new float[bloco * 2]; var bytes = new byte[bloco * 8];
            try { if (!canoAudio.WaitForConnectionAsync().Wait(15000)) { Registro.Info("o codificador não abriu o áudio"); return; } }
            catch (Exception e) { if (codRodando) Registro.Erro("cano de áudio", e); return; }
            Som?.Esvaziar();
            long n = 0;
            while (codRodando)
            {
                long pular = Interlocked.Exchange(ref audioPular, 0);
                if (pular > 0) n += pular / bloco;
                double alvo = n * 10.0, agora = relogioCod.Elapsed.TotalMilliseconds;
                if (agora < alvo) { Thread.Sleep(Math.Max(1, (int)(alvo - agora))); continue; }
                if (Som != null) Som.Ler(amostras, bloco); else Array.Clear(amostras, 0, amostras.Length);
                Buffer.BlockCopy(amostras, 0, bytes, 0, bytes.Length);
                try { canoAudio.Write(bytes, 0, bytes.Length); }
                catch (Exception e) { if (codRodando) Registro.Erro("escrever áudio", e); break; }
                n++;
            }
        }

        // lê o MPEG-TS do codificador e entrega a cada retransmissor
        void Distribuir()
        {
            var ent = cod.StandardOutput.BaseStream; var buf = new byte[188 * 350];
            while (true)
            {
                int n;
                try { n = ent.Read(buf, 0, buf.Length); } catch { break; }
                if (n <= 0) break;
                Retransmissor[] lista; lock (trava) lista = rets.Values.ToArray();
                foreach (var r in lista) r.Enviar(buf, n);
            }
            if (codRodando) { ErroCod = "o codificador fechou sozinho"; Registro.Info("codificador fechou sozinho"); Mudou?.Invoke(); }
        }

        void LerErros(StreamReader r)
        {
            string linha;
            while ((linha = r.ReadLine()) != null)
                if (!linha.StartsWith("frame=") && linha.Trim().Length > 0) { ultimaLinhaCod = Mascarar(linha); Registro.Info("ffmpeg: " + ultimaLinhaCod); }
        }

        public object Estado()
        {
            lock (trava)
            {
                return new
                {
                    tipo = "transmissao",
                    aoVivo = AoVivo, gravando = Gravando,
                    tempoAr = relAr == null ? "" : relAr.Elapsed.ToString(@"hh\:mm\:ss"),
                    tempoGrav = relGrav == null ? "" : relGrav.Elapsed.ToString(@"hh\:mm\:ss"),
                    codificador = CodUsado, kbps = Cfg.Kbps, perdidos = QuadrosPerdidos, erro = ErroCod,
                    arquivo = Gravando ? Path.GetFileName(arquivoGravacao) : "",
                    destinos = Cfg.Destinos.Select(d => new
                    {
                        id = d.Id, nome = d.Nome, tipo = d.Tipo, ligado = d.Ligado,
                        estado = rets.TryGetValue(d.Id, out var r) ? r.Estado : (d.Ligado ? "pronto" : "desligado"),
                        kbps = rets.TryGetValue(d.Id, out var r2) ? r2.Kbps : 0,
                        quedas = rets.TryGetValue(d.Id, out var r3) ? r3.Quedas : 0
                    }),
                    gravacao = rets.TryGetValue("gravacao", out var g) ? g.Estado : ""
                };
            }
        }

        public void Dispose()
        {
            Transmitir(false); Gravar(false);
        }

        // ---------- um destino ----------
        sealed class Retransmissor
        {
            readonly Transmissor t; readonly string id, nome, argsSaida; readonly bool rede;
            Process p; readonly BlockingCollection<byte[]> fila = new BlockingCollection<byte[]>(600);
            volatile bool ativo; public string Estado = "conectando"; public int Kbps, Quedas;
            long bytes, atrasoEm = -10000; readonly Stopwatch relKbps = Stopwatch.StartNew();

            public Retransmissor(Transmissor t, string id, string nome, string argsSaida, bool rede) { this.t = t; this.id = id; this.nome = nome; this.argsSaida = argsSaida; this.rede = rede; }

            public void Iniciar() { ativo = true; new Thread(Laco) { IsBackground = true, Name = "destino " + nome }.Start(); }
            public void Parar() { ativo = false; fila.CompleteAdding(); try { p?.StandardInput.Close(); } catch { } Estado = "parado"; }

            public void Enviar(byte[] buf, int n)
            {
                if (!ativo || fila.IsAddingCompleted) return;
                var c = new byte[n]; Buffer.BlockCopy(buf, 0, c, 0, n);
                if (!fila.TryAdd(c)) { Estado = "atrasado: a internet não está dando conta"; atrasoEm = Environment.TickCount64; while (fila.TryTake(out _)) { } }   // joga fora o atraso e segue
            }

            void Laco()
            {
                while (ativo)
                {
                    try
                    {
                        Estado = "conectando"; bytes = 0; relKbps.Restart();
                        p = new Process { StartInfo = new ProcessStartInfo(Ffmpeg, "-hide_banner -loglevel warning -stats_period 2 -stats -f mpegts -i pipe:0 " + argsSaida) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true } };
                        p.Start();
                        var pp = p;
                        new Thread(() => LerErros(pp.StandardError)) { IsBackground = true }.Start();
                        var ent = p.StandardInput.BaseStream;
                        while (ativo && !p.HasExited)
                        {
                            if (!fila.TryTake(out var c, 500)) continue;
                            ent.Write(c, 0, c.Length);
                            bytes += c.Length;
                            if (relKbps.ElapsedMilliseconds >= 2000)
                            {
                                // se o retransmissor está consumindo, a conexão está aberta (o cano trava enquanto ele conecta)
                                Kbps = (int)(bytes * 8 / relKbps.ElapsedMilliseconds); bytes = 0; relKbps.Restart();
                                if (Kbps > 0 && Environment.TickCount64 - atrasoEm > 5000) Estado = rede ? "no ar" : "gravando";
                            }
                        }
                    }
                    catch (Exception e) { if (ativo) Registro.Info($"destino {nome}: {t.Mascarar(e.Message)}"); }
                    if (!ativo) break;
                    Quedas++; Kbps = 0;
                    Estado = rede ? "caiu: reconectando em 3 s" : "parou: reiniciando";
                    Registro.Info($"destino {nome} caiu (queda {Quedas})");
                    t.Mudou?.Invoke();
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    for (int i = 0; i < 30 && ativo; i++) Thread.Sleep(100);
                    while (fila.TryTake(out _)) { }
                }
                try { if (p != null && !p.WaitForExit(5000)) p.Kill(); } catch { }
            }

            void LerErros(StreamReader r)
            {
                string linha;
                while ((linha = r.ReadLine()) != null)
                {
                    if (linha.StartsWith("frame=") || linha.StartsWith("size=")) continue;
                    if (linha.Trim().Length > 0) Registro.Info($"destino {nome}: {t.Mascarar(linha)}");
                }
            }
        }
    }
}
