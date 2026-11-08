// Nivelador + limitador do som da transmissão (48 kHz estéreo intercalado).
// 1) Mede o volume como o YouTube/Facebook medem (LUFS, ITU-R BS.1770: filtro K + blocos de 400 ms).
// 2) Nivelador: sobe ou desce o ganho devagar até o alvo (padrão −16 LUFS). No silêncio (abaixo do portão)
//    o ganho fica parado, para não "puxar" o chiado da sala nas pausas.
// 3) Limitador com antecipação de 5 ms: nada passa de −1 dBFS (sem estalo, sem som rachado).
using System;

namespace MiguelAngelusCast.Motor
{
    public sealed class Nivelador
    {
        public bool Ligado = true;
        public double AlvoLufs = -16;
        public double MaxSubirDb = 24, MaxDescerDb = 12;
        const double PortaoLufs = -50;           // abaixo disso (antes do ganho) é pausa/silêncio
        const float Teto = 0.891f;               // −1 dBFS
        const int Taxa = 48000, Antecipa = Taxa * 5 / 1000;

        // filtro K (BS.1770) em 48 kHz: prateleira de agudos + passa-altas
        static readonly double[] P = { 1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585 };
        static readonly double[] H = { 1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621 };
        readonly double[] zp = new double[4], zh = new double[4];   // estado [canal*2 + 0/1]

        // blocos de 100 ms -> janela de 400 ms (momentâneo) e 3 s (curto)
        const int Bloco = Taxa / 10;
        readonly double[] blocos = new double[30]; int nBlocos, iBloco, amostrasBloco; double somaBloco;

        double ganhoDb, alvoGanhoDb;                // ganho do nivelador (aplicado com rampa)
        float ganhoAtual = 1f, ganhoSuave = 1f;
        const float Suaviza = 1f / 480;           // ~10 ms para o ganho chegar ao novo valor

        // limitador
        readonly float[] atraso = new float[Antecipa * 2]; int iAtraso;
        float env = 1f, segura = 1f; int contaSegura;
        readonly float ataque = (float)(1 - Math.Exp(-1.0 / (Antecipa / 4.0)));
        readonly float soltura = (float)(1 - Math.Exp(-1.0 / (Taxa * 0.15)));

        public double GanhoDb => Ligado ? ganhoDb : 0;
        public double LufsEntrada { get; private set; } = -120;   // curto (3 s), antes do nivelador
        public double LufsSaida => LufsEntrada <= -100 ? -120 : LufsEntrada + GanhoDb;
        public double ReducaoLimitadorDb { get; private set; }

        public void Zerar() { Array.Clear(zp); Array.Clear(zh); Array.Clear(blocos); nBlocos = iBloco = amostrasBloco = 0; somaBloco = 0; Array.Clear(atraso); env = segura = 1f; }

        static double Biquad(double x, double[] c, double[] z, int k)
        {
            double y = c[0] * x + z[k];
            z[k] = c[1] * x - c[3] * y + z[k + 1];
            z[k + 1] = c[2] * x - c[4] * y;
            return y;
        }

        static double Lufs(double energiaMedia) => energiaMedia <= 1e-12 ? -120 : -0.691 + 10 * Math.Log10(energiaMedia);

        double MediaBlocos(int n)
        {
            n = Math.Min(n, nBlocos); if (n == 0) return 0;
            double s = 0; for (int i = 1; i <= n; i++) s += blocos[(iBloco - i + blocos.Length) % blocos.Length];
            return s / n;
        }

        public void Processar(float[] s, int n)
        {
            int quadros = n / 2;
            float minimoReducao = 1f;
            for (int i = 0; i < quadros; i++)
            {
                float l = s[i * 2], r = s[i * 2 + 1];

                // medição (antes do ganho do nivelador)
                double kl = Biquad(Biquad(l, P, zp, 0), H, zh, 0), kr = Biquad(Biquad(r, P, zp, 2), H, zh, 2);
                somaBloco += kl * kl + kr * kr;
                if (++amostrasBloco == Bloco) FecharBloco();

                // ganho do nivelador com rampa suave (sem degrau audível)
                ganhoSuave += ((Ligado ? ganhoAtual : 1f) - ganhoSuave) * Suaviza;
                float g = ganhoSuave;
                l *= g; r *= g;

                // limitador com antecipação: decide olhando a amostra que entra, aplica na que sai 5 ms depois
                float pico = Math.Max(Math.Abs(l), Math.Abs(r));
                float precisa = pico > Teto ? Teto / pico : 1f;
                if (precisa <= segura) { segura = precisa; contaSegura = Antecipa; }
                else if (--contaSegura <= 0) segura = precisa;
                env += (segura - env) * (segura < env ? ataque : soltura);

                float ol = atraso[iAtraso * 2], or = atraso[iAtraso * 2 + 1];
                atraso[iAtraso * 2] = l; atraso[iAtraso * 2 + 1] = r;
                iAtraso = (iAtraso + 1) % Antecipa;
                ol *= env; or *= env;
                // segurança final: nunca passa do teto
                s[i * 2] = Math.Clamp(ol, -Teto, Teto); s[i * 2 + 1] = Math.Clamp(or, -Teto, Teto);
                if (env < minimoReducao) minimoReducao = env;
            }
            ReducaoLimitadorDb = minimoReducao >= 0.9999f ? 0 : 20 * Math.Log10(minimoReducao);
        }

        void FecharBloco()
        {
            blocos[iBloco] = somaBloco / Bloco; iBloco = (iBloco + 1) % blocos.Length;
            if (nBlocos < blocos.Length) nBlocos++;
            somaBloco = 0; amostrasBloco = 0;

            double momentaneo = Lufs(MediaBlocos(4));
            LufsEntrada = Lufs(MediaBlocos(30));
            // só reajusta quando há som de verdade (fala, música) — nas pausas o ganho fica onde está
            if (momentaneo > PortaoLufs && LufsEntrada > PortaoLufs)
                alvoGanhoDb = Math.Clamp(AlvoLufs - LufsEntrada, -MaxDescerDb, MaxSubirDb);
            // velocidade: sobe devagar (2 dB/s), desce mais rápido (6 dB/s) — a cada bloco de 100 ms
            double passo = alvoGanhoDb > ganhoDb ? 0.2 : 0.6;
            // se o momentâneo já estoura muito o alvo, desce ainda mais rápido
            if (momentaneo + ganhoDb > AlvoLufs + 8) passo = 1.5;
            ganhoDb += Math.Clamp(alvoGanhoDb - ganhoDb, -passo, passo);
            ganhoAtual = (float)Math.Pow(10, ganhoDb / 20);
        }
    }
}
