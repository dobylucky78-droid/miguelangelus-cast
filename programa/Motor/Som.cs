// Som da transmissão: captura uma entrada (mesa pela interface USB, via ASIO ou o driver comum do Windows,
// ou o "som do computador"), escolhe os canais, aplica ganho e mudo, mede o nível e entrega 48 kHz estéreo
// para o codificador. Os nomes das entradas seguem o perfil da mesa (Perfis.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MiguelAngelusCast.Motor
{
    public sealed class SomCfg
    {
        public string Dispositivo { get; set; } = "";    // wasapi:<id> | loop:<id> | asio:<nome> | "" (sem som)
        public int[] Canais { get; set; } = { 1, 2 };     // 1 canal = mono (vai para os dois lados); 2 = estéreo
        public double GanhoDb { get; set; } = 0;
        public string Perfil { get; set; } = "generico";
        public string MesaIp { get; set; } = "";
        public bool Nivelar { get; set; } = true;          // nivelador automático (o limitador fica sempre ligado)
        public double AlvoLufs { get; set; } = -16;
        public int AtrasoMs { get; set; } = 0;              // atraso do som para alinhar com a imagem
        public bool AbaixarComVideo { get; set; } = true;   // vídeo tocando com som: abaixa o som da mesa
        public double AbaixarDb { get; set; } = -20;        // quanto abaixa (−90 = some)
        public double VolumeVideoDb { get; set; } = 0;      // volume do som dos vídeos
    }

    public sealed class DispositivoSom
    {
        public string id { get; set; }
        public string nome { get; set; }
        public string grupo { get; set; }
        public int canais { get; set; }
    }

    public sealed class Som : IDisposable
    {
        public const int Taxa = 48000;
        static readonly string arquivoCfg = Path.Combine(Program.PastaDados, "som.json");
        public SomCfg Cfg { get; private set; }
        public bool Mudo { get; set; }
        public string Estado { get; private set; } = "sem som";
        public string NomeAtual { get; private set; } = "";
        public int CanaisDispositivo { get; private set; }

        readonly object trava = new object();
        readonly float[] anel = new float[Taxa * 2 * 2];   // 2 s estéreo intercalado
        int ini, qtd;                                       // em amostras (L+R = 2)
        float picoE, picoD; double somaE, somaD; int nMedidas;
        IWaveIn captura; AsioOut asio;
        NAudio.Dsp.WdlResampler conv; int taxaEntrada;
        float[] tmp = new float[0], tmpSaida = new float[0];
        public readonly Nivelador Nivel2 = new Nivelador();
        readonly Atraso atraso = new Atraso();

        public Som()
        {
            try { if (File.Exists(arquivoCfg)) Cfg = JsonSerializer.Deserialize<SomCfg>(File.ReadAllText(arquivoCfg)); } catch (Exception e) { Registro.Erro("ler som.json", e); }
            Cfg ??= new SomCfg();
            AplicarNivelador();
        }
        public void Salvar() { try { File.WriteAllText(arquivoCfg, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { Registro.Erro("gravar som.json", e); } }

        public void AplicarNivelador()
        {
            Nivel2.Ligado = Cfg.Nivelar; Nivel2.AlvoLufs = Math.Clamp(Cfg.AlvoLufs, -30, -10);
            Cfg.AtrasoMs = Math.Clamp(Cfg.AtrasoMs, 0, Atraso.MaxMs); atraso.Ms = Cfg.AtrasoMs;
        }

        // ---------- lista de entradas ----------
        public static List<DispositivoSom> Dispositivos()
        {
            var l = new List<DispositivoSom>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    l.Add(new DispositivoSom { id = "wasapi:" + d.ID, nome = d.FriendlyName, grupo = "Entradas do Windows", canais = Canais(d) });
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    l.Add(new DispositivoSom { id = "loop:" + d.ID, nome = "Som do computador: " + d.FriendlyName, grupo = "Som do computador (o que toca na saída)", canais = Canais(d) });
            }
            catch (Exception e) { Registro.Erro("listar entradas", e); }
            try
            {
                foreach (var n in AsioOut.GetDriverNames())
                {
                    int c = 0;
                    try { using var a = new AsioOut(n); c = a.DriverInputChannelCount; } catch { }
                    l.Add(new DispositivoSom { id = "asio:" + n, nome = n + " (ASIO)", grupo = "ASIO (recomendado para mesas USB)", canais = c });
                }
            }
            catch (Exception e) { Registro.Erro("listar ASIO", e); }
            return l;
        }
        static int Canais(MMDevice d) { try { return d.AudioClient.MixFormat.Channels; } catch { return 2; } }

        // ---------- ligar a entrada escolhida ----------
        public void Aplicar()
        {
            Parar();
            var id = Cfg.Dispositivo ?? "";
            if (id == "") { Estado = "sem som"; NomeAtual = ""; return; }
            NomeAtual = id.StartsWith("asio:") ? id.Substring(5) : "";
            try
            {
                if (id.StartsWith("asio:")) IniciarAsio(id.Substring(5));
                else
                {
                    using var en = new MMDeviceEnumerator();
                    var dev = en.GetDevice(id.Substring(id.IndexOf(':') + 1));
                    NomeAtual = dev.FriendlyName;
                    var cap = id.StartsWith("loop:") ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, 20);
                    CanaisDispositivo = cap.WaveFormat.Channels;
                    PrepararConversao(cap.WaveFormat.SampleRate);
                    var fmt = cap.WaveFormat;
                    cap.DataAvailable += (s, e) => Receber(e.Buffer, e.BytesRecorded, fmt);
                    cap.RecordingStopped += (s, e) => { if (e.Exception != null) { Estado = "a entrada parou: " + e.Exception.Message; Registro.Erro("entrada de som", e.Exception); } };
                    captura = cap; cap.StartRecording();
                }
                Estado = "ok";
                Registro.Info($"som: {NomeAtual} canais {string.Join("+", Cfg.Canais)} ({CanaisDispositivo} na entrada, {taxaEntrada} Hz{(asio != null ? ", ASIO" : "")})");
            }
            catch (Exception e)
            {
                // 0x88890004 = AUDCLNT_E_DEVICE_INVALIDATED; 0x80070490 = elemento não encontrado
                var msg = e.Message.Contains("88890004") || e.Message.Contains("80070490") || e is ArgumentException
                    ? "a entrada não está conectada (a mesa está desligada ou o cabo USB saiu?) — ligue e clique em ↻"
                    : e.Message;
                Estado = "não abriu: " + msg; Registro.Erro("abrir entrada de som", e); Parar();
            }
        }

        void IniciarAsio(string nome)
        {
            asio = new AsioOut(nome);
            NomeAtual = nome; CanaisDispositivo = asio.DriverInputChannelCount;
            int c0 = Cfg.Canais.Min(), c1 = Cfg.Canais.Max();
            if (c1 > CanaisDispositivo) throw new Exception($"a entrada {c1} não existe (o driver tem {CanaisDispositivo})");
            asio.InputChannelOffset = c0 - 1;
            int n = c1 - c0 + 1;
            // 48 kHz (o da Ui24R e o da transmissão); se o driver não aceitar, tenta outras e converte
            int taxa = 0; Exception ultimo = null;
            foreach (var tx in new[] { Taxa, 44100, 96000 })
            {
                try { if (!asio.IsSampleRateSupported(tx)) continue; asio.InitRecordAndPlayback(null, n, tx); taxa = tx; break; }
                catch (Exception e) { ultimo = e; asio.Dispose(); asio = new AsioOut(nome) { InputChannelOffset = c0 - 1 }; }
            }
            if (taxa == 0)
                throw new Exception(ultimo != null && ultimo.Message.Contains("NoClock")
                    ? "o driver ASIO está sem relógio (a mesa/interface está ligada e conectada? se for o Voicemeeter, ele precisa estar aberto)"
                    : "o driver ASIO não aceitou 48 kHz nem 44,1 kHz");
            PrepararConversao(taxa);
            var buf = new float[0];
            asio.AudioAvailable += (s, e) =>
            {
                int amostras = e.SamplesPerBuffer * n;
                if (buf.Length < amostras) buf = new float[amostras];
                e.GetAsInterleavedSamples(buf);
                // aqui o "canal 1" do bloco é o c0
                Misturar(buf, e.SamplesPerBuffer, n, Cfg.Canais.Select(c => c - c0).ToArray());
            };
            asio.Play();
        }

        void PrepararConversao(int taxa)
        {
            taxaEntrada = taxa; conv = null;
            if (taxa != Taxa)
            {
                conv = new NAudio.Dsp.WdlResampler();
                conv.SetMode(true, 2, false); conv.SetFilterParms(); conv.SetFeedMode(true); conv.SetRates(taxa, Taxa);
            }
            lock (trava) { ini = qtd = 0; }
            Nivel2.Zerar(); atraso.Zerar();
        }

        // WASAPI: bytes no formato do Windows -> float
        void Receber(byte[] b, int n, WaveFormat f)
        {
            int ch = f.Channels, bps = f.BitsPerSample / 8, quadros = n / (bps * ch);
            if (tmp.Length < quadros * ch) tmp = new float[quadros * ch];
            bool flutuante = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && bps == 4);   // modo compartilhado do Windows = float 32
            for (int i = 0; i < quadros * ch; i++)
            {
                int o = i * bps;
                tmp[i] = flutuante && bps == 4 ? BitConverter.ToSingle(b, o)
                       : bps == 2 ? BitConverter.ToInt16(b, o) / 32768f
                       : bps == 3 ? ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f
                       : bps == 4 ? BitConverter.ToInt32(b, o) / 2147483648f : 0f;
            }
            Misturar(tmp, quadros, ch, Cfg.Canais.Select(c => c - 1).ToArray());
        }

        // escolhe os canais e o ganho; converte para 48 kHz; nivela, limita, mede, mudo e guarda no anel
        void Misturar(float[] src, int quadros, int ch, int[] sel)
        {
            int a = Math.Clamp(sel[0], 0, ch - 1), b = sel.Length > 1 ? Math.Clamp(sel[1], 0, ch - 1) : a;
            float g = (float)Math.Pow(10, Cfg.GanhoDb / 20);
            var st = new float[quadros * 2];
            for (int i = 0; i < quadros; i++) { st[i * 2] = src[i * ch + a] * g; st[i * 2 + 1] = src[i * ch + b] * g; }

            if (conv != null)
            {
                int pedir = conv.ResamplePrepare(quadros, 2, out var inbuf, out int off);
                Array.Copy(st, 0, inbuf, off, Math.Min(pedir, quadros) * 2);
                int max = quadros * Taxa / taxaEntrada + 64;
                if (tmpSaida.Length < max * 2) tmpSaida = new float[max * 2];
                int saiu = conv.ResampleOut(tmpSaida, 0, quadros, max, 2);
                Depois(tmpSaida, saiu * 2);
            }
            else Depois(st, st.Length);
        }

        // já em 48 kHz: nivelador + limitador, medidor (mostra o que vai para a transmissão), mudo
        void Depois(float[] s, int n)
        {
            Nivel2.Processar(s, n);
            float pe = 0, pd = 0; double se = 0, sd = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                float l = s[i], r = s[i + 1], al = Math.Abs(l), ar = Math.Abs(r);
                if (al > pe) pe = al; if (ar > pd) pd = ar; se += l * l; sd += r * r;
            }
            lock (trava) { if (pe > picoE) picoE = pe; if (pd > picoD) picoD = pd; somaE += se; somaD += sd; nMedidas += n / 2; }
            atraso.Processar(s, n);              // o medidor mostra o som na hora; a transmissão recebe atrasado
            if (Mudo) Array.Clear(s, 0, n);      // o mudo vale na hora (depois do atraso)
            Guardar(s, n);
        }

        void Guardar(float[] s, int n)
        {
            lock (trava)
            {
                for (int i = 0; i < n; i++) { anel[(ini + qtd) % anel.Length] = s[i]; if (qtd < anel.Length) qtd++; else ini = (ini + 1) % anel.Length; }
            }
        }

        // ---------- para o codificador ----------
        public void Esvaziar() { lock (trava) { ini = qtd = 0; } }

        // o som da mesa está abaixado por causa de um vídeo?
        public bool Abaixado => Cfg.AbaixarComVideo && VideoComSom();
        static bool VideoComSom() { foreach (var v in FonteVideo.Ativas.Keys) if (v.TocandoComSom) return true; return false; }
        float abaixo = 1f; float[] somVideo = new float[0];

        // entrega exatamente "quadros" (completa com silêncio se faltar; joga fora o excesso se acumular)
        // + som dos vídeos tocando, com o som da mesa abaixado enquanto isso
        public void Ler(float[] dst, int quadros)
        {
            LerMesa(dst, quadros);
            int n = quadros * 2;
            bool video = VideoComSom();
            float alvo = video && Cfg.AbaixarComVideo ? (float)Math.Pow(10, Math.Clamp(Cfg.AbaixarDb, -90, 0) / 20) : 1f;
            // (0.1.7) volume da mesa pedido pela cena do ar, andando junto com a transição
            bool mexeMesa = !rMesa.Parada || rMesa.Atual != 1f;
            if (alvo != 1f || abaixo != 1f || mexeMesa)
            {
                // abaixar com vídeo: desce em ~0,3 s, volta em ~0,8 s (sem degrau)
                float k = alvo < abaixo ? 1f / (Taxa * 0.3f / 4) : 1f / (Taxa * 0.8f / 4);
                for (int i = 0; i < n; i += 2)
                {
                    abaixo += (alvo - abaixo) * k; if (Math.Abs(abaixo - alvo) < 1e-4f) abaixo = alvo;
                    float g = abaixo * rMesa.Proximo();
                    dst[i] *= g; dst[i + 1] *= g;
                }
            }
            bool somou = false;
            if (video)
            {
                if (somVideo.Length < n) somVideo = new float[n];
                Array.Clear(somVideo, 0, n);
                float g = (float)Math.Pow(10, Math.Clamp(Cfg.VolumeVideoDb, -30, 12) / 20);
                foreach (var v in FonteVideo.Ativas.Keys) v.SomarSom(somVideo, quadros, g);
                if (!Mudo) { for (int i = 0; i < n; i += 2) { float gv = rVideo.Proximo(); dst[i] += somVideo[i] * gv; dst[i + 1] += somVideo[i + 1] * gv; } somou = true; }
            }
            // entradas extras da cena (som do computador, microfone USB…)
            EntradaExtra[] lista; lock (travaExtras) lista = extras.Values.ToArray();
            foreach (var e in lista)
            {
                Rampa r; lock (travaExtras) if (!rExtras.TryGetValue(e.Id, out r)) continue;
                if (somExtra.Length < n) somExtra = new float[n];
                e.Ler(somExtra, quadros);
                if (Mudo || (r.Parada && r.Atual == 0f)) { for (int i = 0; i < n; i += 2) r.Proximo(); continue; }
                for (int i = 0; i < n; i += 2) { float g = r.Proximo(); dst[i] += somExtra[i] * g; dst[i + 1] += somExtra[i + 1] * g; }
                somou = true;
            }
            if (somou)
                // soma; acima de −1 dB entra um teto suave (o vídeo já vem masterizado, a mesa já passou pelo limitador)
                for (int i = 0; i < n; i++)
                {
                    float s = dst[i], a = Math.Abs(s);
                    if (a > 0.85f) dst[i] = Math.Sign(s) * (0.85f + 0.1f * MathF.Tanh((a - 0.85f) / 0.1f));
                }
        }

        // ---------- (0.1.7) som de cada cena ----------
        readonly object travaExtras = new object();
        readonly Dictionary<string, EntradaExtra> extras = new Dictionary<string, EntradaExtra>();
        readonly Dictionary<string, Rampa> rExtras = new Dictionary<string, Rampa>();
        readonly Rampa rMesa = new Rampa(), rVideo = new Rampa();
        float[] somExtra = new float[0];
        public string SomDaCena { get; private set; } = "padrão (mesa + vídeos)";
        public bool CenaMuda { get; private set; }

        static float Lin(double db) => (float)Math.Pow(10, Math.Clamp(db, -60, 12) / 20);

        // a cena do ar mudou (ou a configuração dela): leva cada volume ao alvo em "ms"
        public void DefinirCena(List<SomFonte> fontes, int ms)
        {
            int amostras = Math.Max(1, ms * Taxa / 1000);
            float mesa = 1, video = 1; var alvos = new Dictionary<string, float>();
            if (fontes != null)
            {
                mesa = 0; video = 0;
                foreach (var f in fontes.Where(f => f.Ligada))
                {
                    var g = Lin(f.VolumeDb);
                    if (f.Tipo == "mesa") mesa = Math.Max(mesa, g);
                    else if (f.Tipo == "video") video = Math.Max(video, g);
                    else if (f.Tipo == "entrada" && !string.IsNullOrEmpty(f.Dispositivo)) alvos[f.Dispositivo] = Math.Max(alvos.TryGetValue(f.Dispositivo, out var x) ? x : 0, g);
                }
            }
            lock (travaExtras)
            {
                rMesa.Ir(mesa, amostras); rVideo.Ir(video, amostras);
                foreach (var id in alvos.Keys) if (!rExtras.ContainsKey(id)) rExtras[id] = new Rampa { Atual = 0, Alvo = 0 };
                foreach (var kv in rExtras) kv.Value.Ir(alvos.TryGetValue(kv.Key, out var g) ? g : 0, amostras);
            }
            CenaMuda = mesa == 0 && video == 0 && alvos.Values.All(v => v == 0);
            SomDaCena = fontes == null ? "padrão (mesa + vídeos)"
                : CenaMuda ? "esta cena está SEM SOM (transmissão muda)"
                : string.Join(" + ", fontes.Where(f => f.Ligada).Select(f => f.Tipo == "mesa" ? "mesa" : f.Tipo == "video" ? "vídeos" : (string.IsNullOrEmpty(f.Nome) ? "outra entrada" : f.Nome)));
        }

        // abre as entradas que alguma cena usa e fecha as que ninguém usa mais
        public void AtualizarExtras(IEnumerable<string> ids)
        {
            var quero = new HashSet<string>(ids.Where(i => !string.IsNullOrEmpty(i) && (i.StartsWith("wasapi:") || i.StartsWith("loop:"))));
            List<EntradaExtra> fechar = new List<EntradaExtra>(); List<string> abrir;
            lock (travaExtras)
            {
                foreach (var id in extras.Keys.Where(k => !quero.Contains(k)).ToList()) { fechar.Add(extras[id]); extras.Remove(id); }
                abrir = quero.Where(q => !extras.ContainsKey(q)).ToList();
            }
            foreach (var e in fechar) e.Dispose();
            foreach (var id in abrir)
            {
                var e = new EntradaExtra(id);
                lock (travaExtras) { extras[id] = e; if (!rExtras.ContainsKey(id)) rExtras[id] = new Rampa { Atual = 0, Alvo = 0 }; }
            }
        }
        public object EstadoExtras() { lock (travaExtras) return extras.Values.Select(e => new { id = e.Id, nome = e.Nome, estado = e.Estado }).ToList(); }

        void LerMesa(float[] dst, int quadros)
        {
            int n = quadros * 2;
            lock (trava)
            {
                int max = Taxa * 2 / 5;                                // mais de 200 ms guardados: o relógio da placa corre na frente
                if (qtd > max) { int jogar = qtd - Taxa * 2 / 20; jogar -= jogar % 2; ini = (ini + jogar) % anel.Length; qtd -= jogar; }
                int tem = Math.Min(n, qtd); tem -= tem % 2;
                for (int i = 0; i < tem; i++) dst[i] = anel[(ini + i) % anel.Length];
                ini = (ini + tem) % anel.Length; qtd -= tem;
                if (tem < n) Array.Clear(dst, tem, n - tem);
            }
        }

        // nível desde a última leitura (dBFS): pico e média
        public (double pe, double pd, double re, double rd) Nivel()
        {
            lock (trava)
            {
                static double Db(double v) => v <= 1e-6 ? -120 : 20 * Math.Log10(v);
                var r = (Db(picoE), Db(picoD), Db(nMedidas > 0 ? Math.Sqrt(somaE / nMedidas) : 0), Db(nMedidas > 0 ? Math.Sqrt(somaD / nMedidas) : 0));
                picoE = picoD = 0; somaE = somaD = 0; nMedidas = 0;
                return r;
            }
        }

        void Parar()
        {
            try { captura?.StopRecording(); } catch { }
            try { (captura as IDisposable)?.Dispose(); } catch { }
            captura = null;
            try { asio?.Stop(); asio?.Dispose(); } catch { }
            asio = null; CanaisDispositivo = 0;
        }

        public void Dispose()
        {
            Parar();
            lock (travaExtras) { foreach (var e in extras.Values) e.Dispose(); extras.Clear(); }
        }
    }
}
