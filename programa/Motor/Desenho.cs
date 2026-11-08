// Operações de imagem do motor: ajustar uma fonte ao quadro (caber/preencher), misturar (esmaecer),
// virar página e reduzir para os monitores da tela. Tudo em BGRA, em paralelo por linhas.
using System;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    static unsafe class Desenho
    {
        // BGRA -> YUV 4:2:0 (I420), BT.709 faixa limitada (16-235), o que as plataformas esperam. Cor = média de 2x2.
        public static void ParaYuv420(Imagem src, byte[] dst)
        {
            int W = src.L, H = src.A; int tamY = W * H, tamC = tamY / 4;
            fixed (byte* ps0 = src.Px) fixed (byte* pd0 = dst)
            {
                byte* ps = ps0, pY = pd0, pU = pd0 + tamY, pV = pd0 + tamY + tamC;
                Parallel.For(0, H / 2, j =>
                {
                    byte* l0 = ps + (long)(2 * j) * W * 4, l1 = l0 + W * 4;
                    byte* y0 = pY + (long)(2 * j) * W, y1 = y0 + W;
                    byte* u = pU + (long)j * (W / 2), v = pV + (long)j * (W / 2);
                    for (int x = 0; x < W; x += 2)
                    {
                        int b0 = l0[x * 4], g0 = l0[x * 4 + 1], r0 = l0[x * 4 + 2];
                        int b1 = l0[x * 4 + 4], g1 = l0[x * 4 + 5], r1 = l0[x * 4 + 6];
                        int b2 = l1[x * 4], g2 = l1[x * 4 + 1], r2 = l1[x * 4 + 2];
                        int b3 = l1[x * 4 + 4], g3 = l1[x * 4 + 5], r3 = l1[x * 4 + 6];
                        y0[x] = (byte)(16 + ((47 * r0 + 157 * g0 + 16 * b0 + 128) >> 8));
                        y0[x + 1] = (byte)(16 + ((47 * r1 + 157 * g1 + 16 * b1 + 128) >> 8));
                        y1[x] = (byte)(16 + ((47 * r2 + 157 * g2 + 16 * b2 + 128) >> 8));
                        y1[x + 1] = (byte)(16 + ((47 * r3 + 157 * g3 + 16 * b3 + 128) >> 8));
                        int r = r0 + r1 + r2 + r3, g = g0 + g1 + g2 + g3, b = b0 + b1 + b2 + b3;   // soma de 4
                        u[x >> 1] = (byte)(128 + ((-26 * r - 87 * g + 113 * b + 512) >> 10));
                        v[x >> 1] = (byte)(128 + ((113 * r - 103 * g - 10 * b + 512) >> 10));
                    }
                });
            }
        }

        // Desenha "src" dentro de "dst" (já do tamanho do quadro de saída), bilinear.
        // caber = mostra a imagem inteira (faixas pretas se a proporção for outra); preencher = cobre tudo (corta as sobras).
        public static void Ajustar(Imagem src, Imagem dst, bool preencher)
        {
            int SW = src.L, SH = src.A, DW = dst.L, DH = dst.A;
            if (SW == DW && SH == DH) { Buffer.BlockCopy(src.Px, 0, dst.Px, 0, dst.Px.Length); return; }
            double s = preencher ? Math.Max((double)DW / SW, (double)DH / SH) : Math.Min((double)DW / SW, (double)DH / SH);
            double dw = SW * s, dh = SH * s, ox = (DW - dw) / 2, oy = (DH - dh) / 2;
            int x0 = Math.Max(0, (int)Math.Round(ox)), x1 = Math.Min(DW, (int)Math.Round(ox + dw));
            int y0 = Math.Max(0, (int)Math.Round(oy)), y1 = Math.Min(DH, (int)Math.Round(oy + dh));
            if (x0 > 0 || y0 > 0 || x1 < DW || y1 < DH) Preto(dst);

            var ix = new int[DW]; var ix1 = new int[DW]; var fx = new int[DW];
            for (int x = x0; x < x1; x++)
            {
                double sx = (x - ox + 0.5) / s - 0.5;
                int i = (int)Math.Floor(sx); int f = (int)((sx - i) * 256);
                if (i < 0) { i = 0; f = 0; }
                if (i >= SW - 1) { i = SW - 1; f = 0; }
                ix[x] = i; ix1[x] = Math.Min(i + 1, SW - 1); fx[x] = f;
            }
            fixed (byte* ps0 = src.Px) fixed (byte* pd0 = dst.Px)
            {
                uint* ps = (uint*)ps0, pd = (uint*)pd0;
                Parallel.For(y0, y1, y =>
                {
                    double sy = (y - oy + 0.5) / s - 0.5;
                    int j = (int)Math.Floor(sy); int fy = (int)((sy - j) * 256);
                    if (j < 0) { j = 0; fy = 0; }
                    if (j >= SH - 1) { j = SH - 1; fy = 0; }
                    uint* l0 = ps + (long)j * SW, l1 = ps + (long)Math.Min(j + 1, SH - 1) * SW, o = pd + (long)y * DW;
                    for (int x = x0; x < x1; x++)
                    {
                        int f = fx[x];
                        uint a = Mistura(l0[ix[x]], l0[ix1[x]], f), b = Mistura(l1[ix[x]], l1[ix1[x]], f);
                        o[x] = Mistura(a, b, fy);
                    }
                });
            }
        }

        // a + (b − a)·k/256, dois canais de cada vez
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static uint Mistura(uint a, uint b, int k)
        {
            if (k == 0) return a;
            uint ik = (uint)(256 - k), uk = (uint)k;
            uint rb = (((a & 0x00FF00FF) * ik + (b & 0x00FF00FF) * uk) >> 8) & 0x00FF00FF;
            uint ag = ((((a >> 8) & 0x00FF00FF) * ik + ((b >> 8) & 0x00FF00FF) * uk)) & 0xFF00FF00;
            return rb | ag;
        }

        public static void Preto(Imagem d)
        {
            fixed (byte* p0 = d.Px) { uint* p = (uint*)p0; long n = (long)d.L * d.A; for (long i = 0; i < n; i++) p[i] = 0xFF000000; }
        }

        // Esmaecer: saída = de·(1−t) + para·t
        public static void Esmaecer(Imagem de, Imagem para, Imagem saida, double t)
        {
            int k = Math.Clamp((int)(t * 256), 0, 256), L = saida.L;
            fixed (byte* a0 = de.Px) fixed (byte* b0 = para.Px) fixed (byte* o0 = saida.Px)
            {
                uint* a = (uint*)a0, b = (uint*)b0, o = (uint*)o0;
                Parallel.For(0, saida.A, y =>
                {
                    long i0 = (long)y * L;
                    for (long i = i0; i < i0 + L; i++) o[i] = Mistura(a[i], b[i], k);
                });
            }
        }

        // Virar página: a cena que sai gira em torno da borda esquerda (vai estreitando e escurecendo)
        // e revela a nova embaixo, com uma sombra junto da dobra
        public static void VirarPagina(Imagem de, Imagem para, Imagem saida, double t)
        {
            int L = saida.L, A = saida.A;
            double suave = 0.5 - 0.5 * Math.Cos(Math.PI * t);                 // começa e termina devagar
            int largVelha = Math.Max(0, (int)(L * Math.Cos(suave * Math.PI / 2)));
            int escuro = (int)(suave * 150), sombra = Math.Max(1, L / 24);
            int fimSombra = Math.Min(L, largVelha + sombra);
            // por coluna, uma vez por quadro: de onde vem o pixel da página velha e quanto escurece a sombra
            var col = new int[Math.Max(1, largVelha)];
            for (int x = 0; x < largVelha; x++) col[x] = (int)((long)x * L / largVelha);
            var somb = new int[sombra];
            for (int d = 0; d < sombra; d++) somb[d] = (int)(110 * (1 - (double)d / sombra) * (1 - suave));
            fixed (byte* a0 = de.Px) fixed (byte* b0 = para.Px) fixed (byte* o0 = saida.Px)
            {
                uint* a = (uint*)a0, b = (uint*)b0, o = (uint*)o0;
                Parallel.For(0, A, y =>
                {
                    uint* la = a + (long)y * L, lb = b + (long)y * L, lo = o + (long)y * L;
                    for (int x = 0; x < largVelha; x++) lo[x] = Mistura(la[col[x]], 0xFF000000, escuro);
                    for (int x = largVelha; x < fimSombra; x++) lo[x] = Mistura(lb[x], 0xFF000000, somb[x - largVelha]);
                    if (fimSombra < L) Buffer.MemoryCopy(lb + fimSombra, lo + fimSombra, (L - fimSombra) * 4L, (L - fimSombra) * 4L);
                });
            }
        }

        // Reduz para o monitor da tela (RGBA, para o canvas do HTML), pegando o pixel do meio de cada bloco
        public static void Monitor(Imagem src, byte* destino, int ML, int MA)
        {
            int L = src.L, A = src.A;
            fixed (byte* s0 = src.Px)
            {
                uint* s = (uint*)s0; uint* d = (uint*)destino;
                for (int y = 0; y < MA; y++)
                {
                    uint* ls = s + (long)((y * A + A / 2) / MA) * L, ld = d + (long)y * ML;
                    for (int x = 0; x < ML; x++)
                    {
                        uint c = ls[(x * L + L / 2) / ML];
                        ld[x] = (c & 0xFF00FF00) | ((c >> 16) & 0xFF) | ((c & 0xFF) << 16);   // BGRA → RGBA
                    }
                }
            }
        }
    }
}
