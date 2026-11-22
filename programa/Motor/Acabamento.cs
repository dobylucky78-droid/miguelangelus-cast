// (0.1.22) Acabamento do som dentro do Cast (camada 2 do item 16 do backlog), antes do nivelador:
//  1) Corte de graves: passa-altas de 80 Hz, 24 dB/oitava (vento, pancada no púlpito, ronco, "pum" do microfone).
//  2) Equalização automática LENTA: mede, só com som de verdade, quanto grave (80–250 Hz), "embolado" (250–500 Hz) e
//     agudo (2–8 kHz) o som tem em relação ao médio (500–2000 Hz), numa média de ~30 s, e corrige aos poucos (0,1 dB/s)
//     o que passar da faixa normal. Referência = média das gravações das Missas de 22/11/2026 (grave −1, embolado −1,
//     agudo −9 dB em relação ao médio); folga de ±2,5 dB sem mexer; no máximo ±4 dB (agudo sobe no máximo +3, para não
//     subir o chiado; o embolado só é cortado).
//  3) Compressor suave de voz: 2,5:1, joelho de 6 dB, ataque 10 ms, soltura 200 ms. O limiar fica 6 dB acima do volume
//     médio do som (sobe em ~1 s, desce em ~4 s) (só segura os picos: a palavra mais forte, o padre chegando perto do microfone),
//     então funciona igual com a mesa mandando alto ou baixo e não briga com o nivelador.
// Filtros: fórmulas clássicas de biquad (Robert Bristow-Johnson, "Audio EQ Cookbook").
using System;

namespace MiguelAngelusCast.Motor
{
    public sealed class Acabamento
    {
        public bool Ligado = false;
        public bool CortarGraves = true, EqAuto = true, Comprimir = true;
        const double Taxa = 48000;

        // ---------- biquad ----------
        sealed class Bq
        {
            public double b0, b1, b2, a1, a2; readonly double[] z = new double[4];   // estado [canal*2 + 0/1]
            public double P(double x, int c) { int k = c * 2; double y = b0 * x + z[k]; z[k] = b1 * x - a1 * y + z[k + 1]; z[k + 1] = b2 * x - a2 * y; return y; }
            public void Zerar() => Array.Clear(z);
            void Set(double B0, double B1, double B2, double A0, double A1, double A2) { b0 = B0 / A0; b1 = B1 / A0; b2 = B2 / A0; a1 = A1 / A0; a2 = A2 / A0; }
            public Bq PassaAltas(double f, double q) { double w = 2 * Math.PI * f / Taxa, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q); Set((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al); return this; }
            public Bq PassaFaixa(double f, double q) { double w = 2 * Math.PI * f / Taxa, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q); Set(al, 0, -al, 1 + al, -2 * cs, 1 - al); return this; }
            public Bq Pico(double f, double q, double db) { double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / Taxa, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q); Set(1 + al * A, -2 * cs, 1 - al * A, 1 + al / A, -2 * cs, 1 - al / A); return this; }
            public Bq PrateleiraGrave(double f, double db) { double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / Taxa, cs = Math.Cos(w), al = Math.Sin(w) / 2 * Math.Sqrt(2), r = 2 * Math.Sqrt(A) * al; Set(A * ((A + 1) - (A - 1) * cs + r), 2 * A * ((A - 1) - (A + 1) * cs), A * ((A + 1) - (A - 1) * cs - r), (A + 1) + (A - 1) * cs + r, -2 * ((A - 1) + (A + 1) * cs), (A + 1) + (A - 1) * cs - r); return this; }
            public Bq PrateleiraAguda(double f, double db) { double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / Taxa, cs = Math.Cos(w), al = Math.Sin(w) / 2 * Math.Sqrt(2), r = 2 * Math.Sqrt(A) * al; Set(A * ((A + 1) + (A - 1) * cs + r), -2 * A * ((A - 1) + (A + 1) * cs), A * ((A + 1) + (A - 1) * cs - r), (A + 1) - (A - 1) * cs + r, 2 * ((A - 1) - (A + 1) * cs), (A + 1) - (A - 1) * cs - r); return this; }
        }

        // 1) passa-altas 80 Hz de 4ª ordem (Butterworth = dois biquads com Q 0,541 e 1,307)
        readonly Bq hp1 = new Bq().PassaAltas(80, 0.5412), hp2 = new Bq().PassaAltas(80, 1.3066);
        // 2) equalização: prateleira de graves 180 Hz, pico 350 Hz (embolado), prateleira de agudos 4 kHz
        readonly Bq eqG = new Bq().PrateleiraGrave(180, 0), eqE = new Bq().Pico(350, 1.2, 0), eqA = new Bq().PrateleiraAguda(4000, 0);
        // análise (no som que entra, depois do corte de graves): 4 faixas, mono
        readonly Bq[] an = { new Bq().PassaFaixa(140, 0.7), new Bq().PassaFaixa(350, 1.4), new Bq().PassaFaixa(1000, 0.7), new Bq().PassaFaixa(4500, 0.8) };
        readonly double[] somaBanda = new double[4], mediaBanda = new double[4];
        double somaBloco; int amostrasBloco, blocosMedidos;
        const int Bloco = 4800;   // 100 ms

        public double EqGraveDb { get; private set; }
        public double EqEmboladoDb { get; private set; }
        public double EqAgudoDb { get; private set; }
        double alvoG, alvoE, alvoA;
        public double ReducaoCompDb { get; private set; }
        // medida atual (dB em relação ao médio), para mostrar e registrar
        public (double grave, double embolado, double agudo) Medida => (Rel(0), Rel(1), Rel(3));
        double Rel(int i) => mediaBanda[2] <= 1e-15 || mediaBanda[i] <= 1e-15 ? 0 : 10 * Math.Log10(mediaBanda[i] / mediaBanda[2]);

        // referência e limites
        public const double RefGrave = -1, RefEmbolado = -1, RefAgudo = -9, Folga = 2.5;

        // compressor
        double envComp, mediaLonga; float ganhoComp = 1f;
        readonly double atqComp = 1 - Math.Exp(-1.0 / (Taxa * 0.010)), solComp = 1 - Math.Exp(-1.0 / (Taxa * 0.200));
        readonly double mediaSobe = 1 - Math.Exp(-1.0 / (Taxa * 1.0)), mediaDesce = 1 - Math.Exp(-1.0 / (Taxa * 4.0));
        const double AcimaDaMedia = 6, Razao = 2.5, Joelho = 6;

        public void Zerar()
        {
            hp1.Zerar(); hp2.Zerar(); eqG.Zerar(); eqE.Zerar(); eqA.Zerar(); foreach (var b in an) b.Zerar();
            Array.Clear(somaBanda); Array.Clear(mediaBanda); somaBloco = 0; amostrasBloco = blocosMedidos = 0; envComp = 0; mediaLonga = 0; ganhoComp = 1f;
        }

        public void Processar(float[] s, int n)
        {
            if (!Ligado) { ReducaoCompDb = 0; return; }
            int quadros = n / 2;
            double minComp = 0;
            for (int i = 0; i < quadros; i++)
            {
                double l = s[i * 2], r = s[i * 2 + 1];
                if (CortarGraves) { l = hp2.P(hp1.P(l, 0), 0); r = hp2.P(hp1.P(r, 1), 1); }

                // análise (mono)
                if (EqAuto)
                {
                    double m = (l + r) * 0.5;
                    for (int b = 0; b < 4; b++) { double y = an[b].P(m, 0); somaBanda[b] += y * y; }
                    somaBloco += m * m;
                    if (++amostrasBloco == Bloco) FecharBloco();
                }
                if (EqAuto || EqGraveDb != 0 || EqEmboladoDb != 0 || EqAgudoDb != 0)
                { l = eqA.P(eqE.P(eqG.P(l, 0), 0), 0); r = eqA.P(eqE.P(eqG.P(r, 1), 1), 1); }

                if (Comprimir)
                {
                    double x2 = Math.Max(l * l, r * r);
                    envComp += (x2 - envComp) * (x2 > envComp ? atqComp : solComp);
                    double xm = (l * l + r * r) * 0.5;
                    if (xm > 1e-6 || mediaLonga > 1e-6) mediaLonga += (xm - mediaLonga) * (xm > mediaLonga ? mediaSobe : mediaDesce);   // no silêncio (< −60 dBFS) a média não cai
                    double limiar = (mediaLonga <= 1e-12 ? -60 : 10 * Math.Log10(mediaLonga)) + AcimaDaMedia;
                    double db = envComp <= 1e-12 ? -120 : 10 * Math.Log10(envComp), over = db - limiar, red = 0;
                    if (over > Joelho / 2) red = over * (1 - 1 / Razao);
                    else if (over > -Joelho / 2) { double t = over + Joelho / 2; red = (1 - 1 / Razao) * t * t / (2 * Joelho); }
                    float g = (float)Math.Pow(10, -red / 20);
                    ganhoComp += (g - ganhoComp) * 0.02f;
                    l *= ganhoComp; r *= ganhoComp;
                    if (-red < minComp) minComp = -red;
                }
                s[i * 2] = (float)l; s[i * 2 + 1] = (float)r;
            }
            ReducaoCompDb = minComp;
        }

        void FecharBloco()
        {
            double rmsDb = 10 * Math.Log10(somaBloco / Bloco + 1e-20);
            // só blocos com som de verdade (fala, música): pausas e chiado não entram na conta
            if (rmsDb > -50)
            {
                double a = blocosMedidos < 300 ? 1.0 / (blocosMedidos + 1) : 1.0 / 300;   // ~30 s de som
                for (int b = 0; b < 4; b++) mediaBanda[b] += (somaBanda[b] / Bloco - mediaBanda[b]) * a;
                blocosMedidos++;
                if (blocosMedidos >= 150)   // 15 s de som antes de começar a corrigir
                {
                    alvoG = Corrigir(Rel(0), RefGrave, -4, 4);
                    alvoE = Corrigir(Rel(1), RefEmbolado, -4, 0);
                    alvoA = Corrigir(Rel(3), RefAgudo, -4, 3);
                }
            }
            Array.Clear(somaBanda); somaBloco = 0; amostrasBloco = 0;
            // anda 0,01 dB por bloco (0,1 dB/s) até o alvo
            bool mudou = false;
            double Andar(double atual, double alvo) { double d = Math.Clamp(alvo - atual, -0.01, 0.01); if (Math.Abs(d) > 1e-9) mudou = true; return atual + d; }
            EqGraveDb = Andar(EqGraveDb, alvoG); EqEmboladoDb = Andar(EqEmboladoDb, alvoE); EqAgudoDb = Andar(EqAgudoDb, alvoA);
            if (mudou) { eqG.PrateleiraGrave(180, EqGraveDb); eqE.Pico(350, 1.2, EqEmboladoDb); eqA.PrateleiraAguda(4000, EqAgudoDb); }
        }

        // corrige só o que passa da folga, e só 70 % disso (nunca "corrige demais")
        static double Corrigir(double medido, double referencia, double min, double max)
        {
            double desvio = medido - referencia;
            if (Math.Abs(desvio) <= Folga) return 0;
            double excesso = desvio - Math.Sign(desvio) * Folga;
            return Math.Clamp(-0.7 * excesso, min, max);
        }
    }
}
