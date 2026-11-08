// Fontes de imagem das cenas: câmera NDI, barras de teste, cor sólida, imagem e vídeo do computador (FonteVideo.cs).
// Toda fonte entrega uma Imagem BGRA e um número que muda quando a imagem muda (para não redesenhar à toa).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace MiguelAngelusCast.Motor
{
    public sealed class Imagem
    {
        public int L, A;
        public byte[] Px = Array.Empty<byte>();   // BGRA, 4 bytes por pixel, sem folga no fim da linha
        public Imagem() { }
        public Imagem(int l, int a) { Garantir(l, a); }
        public void Garantir(int l, int a)
        {
            if (L == l && A == a && Px.Length == l * a * 4) return;
            L = l; A = a; Px = new byte[l * a * 4];
        }
    }

    public abstract class Fonte : IDisposable
    {
        public string Spec { get; protected set; }      // "ndi:NOME", "barras", "cor:#102030", "imagem:C:\..."
        public long Seq { get; protected set; }         // muda quando a imagem muda
        public string Estado { get; protected set; } = "";
        public abstract Imagem Atual();                  // chamada só pela linha do motor
        public virtual void Dispose() { }

        public static Fonte Criar(string spec)
        {
            spec ??= "";
            if (spec.StartsWith("ndi:")) return new FonteNdi(spec.Substring(4));
            if (spec.StartsWith("cor:")) return new FonteCor(spec.Substring(4));
            if (spec.StartsWith("imagem:")) return new FonteImagem(spec.Substring(7));
            if (spec.StartsWith("video:")) return new FonteVideo(spec.Substring(6), 1920, 1080, 30);
            if (spec.StartsWith("capa:")) return new FonteCapa(spec.Substring(5));
            return new FonteBarras();
        }
    }

    // Câmera (ou o telão do MiguelAngelus) pela rede, por NDI. Procura a fonte pelo nome até ela aparecer.
    public sealed class FonteNdi : Fonte
    {
        readonly string nome; Ndi.Receptor rx; long ultimo; DateTime proxBusca;
        readonly Imagem img = new Imagem(16, 9);
        public FonteNdi(string nome) { this.nome = nome; Spec = "ndi:" + nome; Estado = "procurando"; }

        public override Imagem Atual()
        {
            if (rx == null && DateTime.Now >= proxBusca)
            {
                proxBusca = DateTime.Now.AddSeconds(2);
                var f = Ndi.Fontes().FirstOrDefault(x => x.nome == nome);
                if (f.nome != null) { rx = new Ndi.Receptor(f.origem, "MiguelAngelus Cast"); Estado = "conectando"; Registro.Info("NDI: recebendo " + nome); }
                else Estado = Ndi.Erro ?? "procurando";
            }
            if (rx != null && rx.Pegar(img, ref ultimo)) { Seq++; Estado = "ok"; }
            return img;
        }
        public override void Dispose() { rx?.Dispose(); rx = null; }
    }

    // Barras coloridas com um quadradinho andando e o relógio: mostra se o vídeo está fluido, sem precisar de câmera
    // (já no tamanho da saída: não precisa ampliar; a cada quadro só apaga e redesenha o quadradinho)
    public sealed class FonteBarras : Fonte
    {
        readonly Imagem img;
        static readonly uint[] cores = { 0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0 };
        int quadro, bxAnt = -1;
        public FonteBarras(int l = 1920, int a = 1080)
        {
            Spec = "barras"; Estado = "ok"; img = new Imagem(l, a);
            for (int y = 0; y < a; y++) for (int x = 0; x < l; x++) Pintar(x, y, Fundo(x, y));
        }
        uint Fundo(int x, int y) => y < img.A * 3 / 4 ? cores[x * 7 / img.L] : (x * 8 / img.L % 2 == 0 ? 0xFF101010u : 0xFF2A2A2Au);
        unsafe void Pintar(int x, int y, uint c) { fixed (byte* p = img.Px) ((uint*)p)[y * img.L + x] = c; }
        public override unsafe Imagem Atual()
        {
            quadro++; Seq++;
            int L = img.L, A = img.A, lado = A / 18;
            int t = quadro % 120, bx = t * (L - lado) / 119, by = A * 3 / 4 + (A / 4 - lado) / 2;   // 1 volta a cada 4 s a 30 q/s
            fixed (byte* p0 = img.Px)
            {
                uint* p = (uint*)p0;
                if (bxAnt >= 0) for (int y = by; y < by + lado; y++) for (int x = bxAnt; x < bxAnt + lado; x++) p[y * L + x] = Fundo(x, y);
                for (int y = by; y < by + lado; y++) for (int x = bx; x < bx + lado; x++) p[y * L + x] = 0xFFFFFFFF;
            }
            bxAnt = bx;
            return img;
        }
    }

    public sealed class FonteCor : Fonte
    {
        readonly Imagem img = new Imagem(16, 9);
        public FonteCor(string hex)
        {
            Spec = "cor:" + hex; Estado = "ok"; Seq = 1;
            var c = ColorTranslator.FromHtml(hex.StartsWith("#") ? hex : "#" + hex);
            for (int i = 0; i < img.Px.Length; i += 4) { img.Px[i] = c.B; img.Px[i + 1] = c.G; img.Px[i + 2] = c.R; img.Px[i + 3] = 255; }
        }
        public override Imagem Atual() => img;
    }

    // Imagem do computador (PNG/JPG): capa, cartela do Pix, logo...
    public sealed class FonteImagem : Fonte
    {
        readonly Imagem img = new Imagem(16, 9);
        public FonteImagem(string caminho)
        {
            Spec = "imagem:" + caminho; Seq = 1;
            try
            {
                using var bmp = new Bitmap(caminho);
                Carregar(bmp, img);
                Estado = "ok";
            }
            catch (Exception e) { Estado = "não abriu a imagem"; Registro.Erro("imagem " + caminho, e); }
        }
        public static void Carregar(Bitmap bmp, Imagem img)
        {
            img.Garantir(bmp.Width, bmp.Height);
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < d.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, img.Px, y * img.L * 4, img.L * 4); }
            finally { bmp.UnlockBits(d); }
        }
        public override Imagem Atual() => img;
    }
}
