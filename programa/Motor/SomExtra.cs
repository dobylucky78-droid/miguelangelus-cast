// (0.1.7) Entradas de som extras, usadas pelas cenas (backlog 1.2): outra entrada do Windows (microfone USB, outra
// placa) ou o "som do computador" (loopback de uma saída). Cada uma vira 48 kHz estéreo num anel próprio, e o Som
// mistura com o volume que a cena do ar pede. Ficam abertas enquanto alguma cena usa (troca de cena sem esperar).
using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MiguelAngelusCast.Motor
{
    public sealed class EntradaExtra : IDisposable
    {
        public string Id { get; }
        public string Nome { get; private set; } = "";
        public string Estado { get; private set; } = "abrindo";
        readonly object trava = new object();
        readonly float[] anel = new float[Som.Taxa * 2 * 2];
        int ini, qtd;
        IWaveIn cap; NAudio.Dsp.WdlResampler conv; int taxa;
        float[] tmp = new float[0], st = new float[0], saida = new float[0];
        public float Pico;   // desde a última leitura do medidor

        public EntradaExtra(string id)
        {
            Id = id;
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDevice(id.Substring(id.IndexOf(':') + 1));
                Nome = dev.FriendlyName;
                cap = id.StartsWith("loop:") ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, 20);
                var f = cap.WaveFormat; taxa = f.SampleRate;
                if (taxa != Som.Taxa) { conv = new NAudio.Dsp.WdlResampler(); conv.SetMode(true, 2, false); conv.SetFilterParms(); conv.SetFeedMode(true); conv.SetRates(taxa, Som.Taxa); }
                cap.DataAvailable += (s, e) => { try { Receber(e.Buffer, e.BytesRecorded, f); } catch (Exception ex) { Registro.Erro("entrada extra " + Nome, ex); } };
                cap.RecordingStopped += (s, e) => { if (e.Exception != null) { Estado = "parou: " + e.Exception.Message; Registro.Erro("entrada extra " + Nome, e.Exception); } };
                cap.StartRecording();
                Estado = "ok";
                Registro.Info($"som extra aberto: {Nome} ({f.Channels} canais, {taxa} Hz)");
            }
            catch (Exception e) { Estado = "não abriu: " + e.Message; Registro.Erro("abrir entrada extra " + id, e); Dispose(); }
        }

        void Receber(byte[] b, int n, WaveFormat f)
        {
            int ch = f.Channels, bps = f.BitsPerSample / 8; if (ch < 1 || bps < 2) return;
            int quadros = n / (bps * ch);
            if (tmp.Length < quadros * ch) tmp = new float[quadros * ch];
            bool flut = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && bps == 4);
            for (int i = 0; i < quadros * ch; i++)
            {
                int o = i * bps;
                tmp[i] = flut && bps == 4 ? BitConverter.ToSingle(b, o) : bps == 2 ? BitConverter.ToInt16(b, o) / 32768f
                       : bps == 3 ? ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f : BitConverter.ToInt32(b, o) / 2147483648f;
            }
            // mono vai para os dois lados; mais de 2 canais: os dois primeiros
            if (st.Length < quadros * 2) st = new float[quadros * 2];
            for (int i = 0; i < quadros; i++) { st[i * 2] = tmp[i * ch]; st[i * 2 + 1] = tmp[i * ch + (ch > 1 ? 1 : 0)]; }
            if (conv != null)
            {
                int pedir = conv.ResamplePrepare(quadros, 2, out var inbuf, out int off);
                Array.Copy(st, 0, inbuf, off, Math.Min(pedir, quadros) * 2);
                int max = quadros * Som.Taxa / taxa + 64;
                if (saida.Length < max * 2) saida = new float[max * 2];
                int saiu = conv.ResampleOut(saida, 0, quadros, max, 2);
                Guardar(saida, saiu * 2);
            }
            else Guardar(st, quadros * 2);
        }

        void Guardar(float[] s, int n)
        {
            lock (trava)
                for (int i = 0; i < n; i++)
                {
                    var v = s[i]; var a = Math.Abs(v); if (a > Pico) Pico = a;
                    anel[(ini + qtd) % anel.Length] = v; if (qtd < anel.Length) qtd++; else ini = (ini + 1) % anel.Length;
                }
        }

        // entrega "quadros" estéreo em dst (silêncio se faltar; joga fora o excesso se o relógio da placa correr)
        public void Ler(float[] dst, int quadros)
        {
            int n = quadros * 2;
            lock (trava)
            {
                int max = Som.Taxa * 2 / 5;
                if (qtd > max) { int jogar = qtd - Som.Taxa * 2 / 20; jogar -= jogar % 2; ini = (ini + jogar) % anel.Length; qtd -= jogar; }
                int tem = Math.Min(n, qtd); tem -= tem % 2;
                for (int i = 0; i < tem; i++) dst[i] = anel[(ini + i) % anel.Length];
                ini = (ini + tem) % anel.Length; qtd -= tem;
                if (tem < n) Array.Clear(dst, tem, n - tem);
            }
        }

        public void Dispose()
        {
            try { cap?.StopRecording(); } catch { }
            try { (cap as IDisposable)?.Dispose(); } catch { }
            cap = null;
        }
    }

    // um volume que anda até o alvo em N amostras (sem estalo)
    sealed class Rampa
    {
        public float Atual = 1, Alvo = 1; float passo;
        public void Ir(float alvo, int amostras)
        {
            Alvo = alvo;
            passo = amostras <= 1 ? float.MaxValue : Math.Abs(alvo - Atual) / amostras;
            if (passo == 0) passo = float.MaxValue;
        }
        public float Proximo()
        {
            if (Atual != Alvo) { if (Math.Abs(Alvo - Atual) <= passo) Atual = Alvo; else Atual += Math.Sign(Alvo - Atual) * passo; }
            return Atual;
        }
        public bool Parada => Atual == Alvo;
    }
}
