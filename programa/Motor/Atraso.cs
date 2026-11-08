// Atraso do som (48 kHz estéreo intercalado), para alinhar com a imagem: câmeras por rede (NDI) e o
// processamento do vídeo chegam alguns décimos de segundo depois do som da mesa.
// Ao mudar o valor com o som passando, abaixa 5 ms, pula e sobe 5 ms (sem estalo).
using System;

namespace MiguelAngelusCast.Motor
{
    public sealed class Atraso
    {
        public const int MaxMs = 2000;
        const int Taxa = 48000, Rampa = Taxa * 5 / 1000;
        readonly float[] buf = new float[(Taxa * MaxMs / 1000 + Rampa * 2) * 2];
        int escrita;                    // em quadros
        int atual, alvo;                // atraso em quadros
        int fase;                       // 0 = normal; >0 descendo; <0 subindo
        public int Ms { get => alvo * 1000 / Taxa; set => alvo = Math.Clamp(value, 0, MaxMs) * Taxa / 1000; }

        public void Zerar() { Array.Clear(buf); escrita = 0; atual = alvo; fase = 0; }

        public void Processar(float[] s, int n)
        {
            int quadros = buf.Length / 2;
            if (alvo == 0 && atual == 0 && fase == 0) { Escrever(s, n); return; }
            for (int i = 0; i + 1 < n; i += 2)
            {
                buf[escrita * 2] = s[i]; buf[escrita * 2 + 1] = s[i + 1];
                int ler = (escrita - atual + quadros) % quadros;
                float g = 1f;
                if (fase == 0 && alvo != atual) fase = Rampa;
                if (fase > 0) { g = fase / (float)Rampa; if (--fase == 0) { atual = alvo; fase = -Rampa; } }
                else if (fase < 0) { g = 1f + fase / (float)Rampa; fase++; }
                s[i] = buf[ler * 2] * g; s[i + 1] = buf[ler * 2 + 1] * g;
                escrita = (escrita + 1) % quadros;
            }
        }

        // sem atraso: só guarda (para poder ligar o atraso depois sem buraco)
        void Escrever(float[] s, int n)
        {
            int quadros = buf.Length / 2;
            for (int i = 0; i + 1 < n; i += 2) { buf[escrita * 2] = s[i]; buf[escrita * 2 + 1] = s[i + 1]; escrita = (escrita + 1) % quadros; }
        }
    }
}
