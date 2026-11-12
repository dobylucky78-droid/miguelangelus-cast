// (0.1.4) Fundo como camada: cor sólida ou com efeito, nas cores litúrgicas (as mesmas da capa da live) ou numa cor
// escolhida. Spec: "fundo:<cor>|<efeito>"
//   cor    = auto (a cor litúrgica de hoje, pelo calendário) | verde | roxo | branco | vermelho | rosa | #rrggbb
//   efeito = lisa | degrade | riscos | filete | vinheta | luz (luz que passeia devagar: o único animado)
//            | tema (o tema do dia, pelo calendário) | tema-advento | tema-natal | … (ver Temas.cs)
// Os parados são desenhados uma vez no tamanho da saída; a luz é desenhada menor (640×360, 15 q/s) e ampliada,
// para não pesar no computador da transmissão.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;

namespace MiguelAngelusCast.Motor
{
    public sealed class FonteFundo : Fonte
    {
        readonly string cor, efeito; readonly int L, A;
        readonly Imagem img = new Imagem(16, 9);
        string corUsada, temaUsado; int velasUsadas; DateTime proxConferir;
        readonly Stopwatch relogio = Stopwatch.StartNew(); long ultimoQuadro = -1;

        public FonteFundo(string spec, int l, int a)
        {
            Spec = "fundo:" + spec; Estado = "ok";
            var p = (spec ?? "").Split('|');
            cor = p.Length > 0 && p[0] != "" ? p[0] : "auto";
            efeito = p.Length > 1 && p[1] != "" ? p[1] : "degrade";
            L = efeito == "luz" ? 480 : l; A = efeito == "luz" ? 270 : a;
        }

        // cores: as paletas da capa; uma cor qualquer vira paleta (mais clara, ela, mais escura)
        static Capa.Paleta Paleta(string cor)
        {
            if (Capa.Paletas.TryGetValue(cor, out var p)) return p;
            Color c; try { c = ColorTranslator.FromHtml(cor.StartsWith("#") ? cor : "#" + cor); } catch { c = Color.FromArgb(0x5a, 0x15, 0x20); }
            Color Mis(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
            bool claro = c.GetBrightness() > .7f;
            return new Capa.Paleta { F0 = Mis(c, Color.White, .22), F1 = c, F2 = Mis(c, Color.Black, .55), P1 = .45f, Acento = claro ? Color.FromArgb(0xa0, 0x7a, 0x28) : Color.FromArgb(0xe3, 0xc2, 0x7a) };
        }

        public override Imagem Atual()
        {
            if (DateTime.Now >= proxConferir)
            {
                proxConferir = DateTime.Now.AddMinutes(10);
                var nova = cor == "auto" ? Liturgia.Info(DateTime.Now).cor : cor;   // vira o dia, vira a cor
                var tema = efeito == "tema" ? Temas.DoDia(DateTime.Now) : efeito.StartsWith("tema-") ? efeito.Substring(5) : null;
                int velas = Temas.VelasAdvento(DateTime.Now);
                if (nova != corUsada || tema != temaUsado || velas != velasUsadas) { corUsada = nova; temaUsado = tema; velasUsadas = velas; ultimoQuadro = -1; }
            }
            long q = efeito == "luz" ? relogio.ElapsedMilliseconds / 66 : 0;      // a luz anda a 15 q/s; os outros nunca mudam
            if (q != ultimoQuadro)
            {
                ultimoQuadro = q;
                try { if (efeito == "luz") DesenharLuz(Paleta(corUsada), relogio.Elapsed.TotalSeconds); else Desenhar(Paleta(corUsada), relogio.Elapsed.TotalSeconds); Seq++; }
                catch (Exception e) { Estado = "erro no fundo"; Registro.Erro("fundo " + Spec, e); }
            }
            return img;
        }

        void Desenhar(Capa.Paleta P, double t)
        {
            using var bmp = new Bitmap(L, A, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (efeito == "lisa") g.Clear(P.F1);
                else if (efeito == "vinheta")
                {
                    // a cor no meio e as bordas escurecendo
                    g.Clear(P.F2);
                    using var cam = new GraphicsPath(); cam.AddEllipse(-.3f * L, -.4f * A, 1.6f * L, 1.8f * A);
                    using var pb = new PathGradientBrush(cam) { CenterColor = P.F1, SurroundColors = new[] { P.F2 } };
                    pb.Blend = new Blend { Factors = new[] { 0f, .65f, 1f }, Positions = new[] { 0f, .4f, 1f } };
                    g.FillPath(pb, cam);
                }
                else if (temaUsado != null)
                {
                    // (0.1.9) imagens de fundo temáticas (backlog 1.8): degradê da cor + desenho do tema, feitos aqui
                    Degrade(g, P);
                    var st = g.Save(); g.ScaleTransform(L / 1920f, A / 1080f);
                    Temas.Desenhar(g, temaUsado, P, velasUsadas);
                    g.Restore(st);
                }
                else
                {
                    Degrade(g, P);
                    if (efeito == "riscos" || efeito == "filete") Riscos(g);
                    if (efeito == "filete")
                    {
                        float u = L / 100f;
                        using var pen = new Pen(Color.FromArgb(150, P.Acento), Math.Max(1, L / 960f * 1.5f));
                        g.DrawRectangle(pen, 1.45f * u, 1.45f * u, L - 2.9f * u, A - 2.9f * u);
                    }
                }
            }
            FonteImagem.Carregar(bmp, img);
            img.TemAlfa = false;
        }

        // luz suave: o degradê e a mancha de luz são desenhados uma vez; a cada quadro só a luz muda de lugar
        Bitmap luzFundo, luzMancha, luzQuadro; string luzCor;
        void DesenharLuz(Capa.Paleta P, double t)
        {
            if (luzFundo == null || luzCor != corUsada)
            {
                luzFundo?.Dispose(); luzMancha?.Dispose(); luzQuadro?.Dispose();
                luzCor = corUsada;
                luzFundo = new Bitmap(L, A, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(luzFundo)) { g.SmoothingMode = SmoothingMode.AntiAlias; Degrade(g, P); }
                int mw = (int)(1.5f * L), mh = (int)(1.1f * L);
                luzMancha = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(luzMancha))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    using var cam = new GraphicsPath(); cam.AddEllipse(0, 0, mw, mh);
                    using var pb = new PathGradientBrush(cam) { CenterColor = Color.FromArgb(120, P.F0), SurroundColors = new[] { Color.FromArgb(0, P.F0) } };
                    g.FillPath(pb, cam);
                }
                luzQuadro = new Bitmap(L, A, PixelFormat.Format32bppArgb);
            }
            // passeia devagar (volta inteira em ~40 s), sem nada chamar atenção
            double a = t * 2 * Math.PI / 40;
            float cx = (float)(L * (.5 + .32 * Math.Sin(a))), cy = (float)(A * (.45 + .28 * Math.Sin(2 * a + .8)));
            using (var g = Graphics.FromImage(luzQuadro))
            {
                g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(luzFundo, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver; g.DrawImageUnscaled(luzMancha, (int)(cx - luzMancha.Width / 2f), (int)(cy - luzMancha.Height / 2f));
            }
            FonteImagem.Carregar(luzQuadro, img);
            img.TemAlfa = false;
        }
        public override void Dispose() { luzFundo?.Dispose(); luzMancha?.Dispose(); luzQuadro?.Dispose(); }

        // o mesmo fundo da capa da live: gradiente radial a partir do alto, à esquerda
        void Degrade(Graphics g, Capa.Paleta P)
        {
            g.Clear(P.F2);
            using var cam = new GraphicsPath();
            cam.AddEllipse(.15f * L - 1.2f * L, -A, 2.4f * L, 2f * A);
            using var pb = new PathGradientBrush(cam) { CenterPoint = new PointF(.15f * L, 0), CenterColor = P.F0 };
            pb.InterpolationColors = new ColorBlend { Colors = new[] { P.F2, P.F1, P.F0 }, Positions = new[] { 0f, 1 - P.P1, 1f } };
            g.FillPath(pb, cam);
        }
        void Riscos(Graphics g)
        {
            float px = L / 960f;
            using var risco = new Pen(Color.FromArgb(5, 255, 255, 255), 2 * px);
            var st = g.Save(); g.TranslateTransform(L / 2f, A / 2f); g.RotateTransform(25);
            float R = L + A; for (float x = -R; x < R; x += 9 * px) g.DrawLine(risco, x, -R, x, R);
            g.Restore(st);
        }
    }
}
