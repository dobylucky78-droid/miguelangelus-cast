// (0.1.9) Imagens de fundo temáticas (backlog 1.8). Tudo desenhado aqui, em traço dourado discreto sobre o degradê da
// cor (nenhuma imagem de terceiros). O desenho fica no terço da direita, para não brigar com o que vai na frente.
// As coordenadas são de 1920×1080; quem chama já pôs a escala do tamanho da saída.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;

namespace MiguelAngelusCast.Motor
{
    public static class Temas
    {
        public static readonly string[] Lista = { "advento", "natal", "quaresma", "pascoa", "pentecostes", "maria", "jose", "eucaristia", "comum" };

        /// <summary>O tema do dia, pelo nome da celebração do calendário.</summary>
        public static string DoDia(DateTime quando)
        {
            var n = Liturgia.Info(quando).nome.ToLowerInvariant();
            if (n.Contains("advento")) return "advento";
            if (n.Contains("pentecostes")) return "pentecostes";
            if (n.Contains("corpo e sangue") || n.Contains("ceia do senhor")) return "eucaristia";
            if (n.Contains("josé")) return "jose";
            if (n.Contains("nossa senhora") || n.Contains("maria") || n.Contains("anunciação")) return "maria";
            if (n.Contains("natal") || n.Contains("epifania") || n.Contains("batismo do senhor")) return "natal";
            if (n.Contains("quaresma") || n.Contains("cinzas") || n.Contains("semana santa") || n.Contains("paixão")) return "quaresma";
            if (n.Contains("páscoa") || n.Contains("pascal") || n.Contains("ascensão")) return "pascoa";
            return "comum";
        }

        /// <summary>Quantas velas da coroa ficam acesas: a semana do Advento; fora do Advento, as quatro.</summary>
        public static int VelasAdvento(DateTime quando)
        {
            var m = Regex.Match(Liturgia.Info(quando).nome, @"(\d)[ºª] (Domingo do|Semana do) Advento");
            return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 1, 4) : 4;
        }

        // ---------- cores do traço ----------
        static Color ouro; static Color luz; static Color sombra;
        static Color O(int a) => Color.FromArgb(a, ouro);
        static Color Lz(int a) => Color.FromArgb(a, luz);
        static Color Sb(int a) => Color.FromArgb(a, sombra);

        public static void Desenhar(Graphics g, string tema, Capa.Paleta P, int velas)
        {
            bool claro = P.F1.GetBrightness() > .7f;
            ouro = P.Acento; luz = claro ? P.Acento : Color.FromArgb(255, 250, 236); sombra = P.F2;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            switch (tema)
            {
                case "advento": Advento(g, velas); break;
                case "natal": Natal(g); break;
                case "quaresma": Quaresma(g); break;
                case "pascoa": Pascoa(g); break;
                case "pentecostes": Pentecostes(g); break;
                case "maria": Maria(g); break;
                case "jose": Jose(g); break;
                case "eucaristia": Eucaristia(g); break;
                default: Comum(g); break;
            }
        }

        // ---------- peças ----------
        static void Brilho(Graphics g, float cx, float cy, float r, Color c, int alfa)
        {
            using var cam = new GraphicsPath(); cam.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
            using var pb = new PathGradientBrush(cam) { CenterColor = Color.FromArgb(alfa, c), SurroundColors = new[] { Color.FromArgb(0, c) } };
            g.FillPath(pb, cam);
        }
        static GraphicsPath Estrela(float cx, float cy, float R, float r, int pontas)
        {
            var pts = new PointF[pontas * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / pontas; float rr = i % 2 == 0 ? R : r;
                pts[i] = new PointF(cx + (float)(rr * Math.Cos(a)), cy + (float)(rr * Math.Sin(a)));
            }
            var p = new GraphicsPath(); p.AddPolygon(pts); return p;
        }
        static GraphicsPath Chama(float x, float y, float meia, float alt)   // gota: ponta em cima, base em (x, y)
        {
            var p = new GraphicsPath();
            p.AddBezier(x, y - alt, x + meia * .9f, y - alt * .45f, x + meia * 1.1f, y, x, y);
            p.AddBezier(x, y, x - meia * 1.1f, y, x - meia * .9f, y - alt * .45f, x, y - alt);
            p.CloseFigure(); return p;
        }
        static void Folha(Graphics g, Brush b, float x, float y, float c, float l, float grausRot)
        {
            var st = g.Save(); g.TranslateTransform(x, y); g.RotateTransform(grausRot);
            g.FillEllipse(b, -c / 2, -l / 2, c, l); g.Restore(st);
        }
        static void Raios(Graphics g, float cx, float cy, int n, float r0, float r1, float de, float ate, Color c)
        {
            using var b = new SolidBrush(c);
            float passo = (ate - de) / n;
            for (int i = 0; i < n; i += 2)
            {
                double a0 = (de + i * passo) * Math.PI / 180, a1 = (de + (i + 1) * passo) * Math.PI / 180;
                g.FillPolygon(b, new[] {
                    new PointF(cx + (float)(r0 * Math.Cos(a0)), cy + (float)(r0 * Math.Sin(a0))),
                    new PointF(cx + (float)(r1 * Math.Cos(a0)), cy + (float)(r1 * Math.Sin(a0))),
                    new PointF(cx + (float)(r1 * Math.Cos(a1)), cy + (float)(r1 * Math.Sin(a1))),
                    new PointF(cx + (float)(r0 * Math.Cos(a1)), cy + (float)(r0 * Math.Sin(a1))) });
            }
        }
        static void Colina(Graphics g, float y0, float pico, float x0 = 1050)
        {
            using var p = new GraphicsPath();
            p.AddBezier(x0, 1080, x0 + 250, y0, 1560 - 200, pico, 1560, pico);
            p.AddBezier(1560, pico, 1760, pico, 1880, y0 + 30, 1960, y0 + 60);
            p.AddLine(1960, y0 + 60, 1960, 1080); p.CloseFigure();
            using var b = new SolidBrush(Sb(150)); g.FillPath(b, p);
            using var pen = new Pen(O(110), 2.5f); g.DrawBezier(pen, x0, 1080, x0 + 250, y0, 1560 - 200, pico, 1560, pico); g.DrawBezier(pen, 1560, pico, 1760, pico, 1880, y0 + 30, 1960, y0 + 60);
        }
        static void Cruz(Graphics g, float cx, float topo, float baixo, float braco, float travessa, float grossura, int alfaFundo, int alfaTraco)
        {
            using var p = new GraphicsPath();
            float h = grossura / 2;
            p.AddPolygon(new[] {
                new PointF(cx - h, topo), new PointF(cx + h, topo), new PointF(cx + h, travessa - h), new PointF(cx + braco, travessa - h),
                new PointF(cx + braco, travessa + h), new PointF(cx + h, travessa + h), new PointF(cx + h, baixo), new PointF(cx - h, baixo),
                new PointF(cx - h, travessa + h), new PointF(cx - braco, travessa + h), new PointF(cx - braco, travessa - h), new PointF(cx - h, travessa - h) });
            using var b = new SolidBrush(O(alfaFundo)); g.FillPath(b, p);
            using var pen = new Pen(O(alfaTraco), 3f) { LineJoin = LineJoin.Round }; g.DrawPath(pen, p);
        }

        // ---------- temas ----------
        static void Advento(Graphics g, int acesas)
        {
            float cx = 1540, cy = 790, rx = 240, ry = 72;
            Brilho(g, cx, cy - 150, 420, luz, 55);
            var rnd = new Random(3);
            void Coroa(bool frente)
            {
                using var b = new SolidBrush(O(frente ? 120 : 75));
                for (int k = 0; k < 110; k++)
                {
                    double t = k * 2 * Math.PI / 110 + rnd.NextDouble() * .05; float s = (float)Math.Sin(t);
                    if (s >= 0 != frente) continue;
                    float x = cx + rx * (float)Math.Cos(t) + (float)(rnd.NextDouble() - .5) * 26, y = cy + ry * s + (float)(rnd.NextDouble() - .5) * 18;
                    Folha(g, b, x, y, 16, 52, (float)(t * 180 / Math.PI) + (float)(rnd.NextDouble() - .5) * 70);
                }
            }
            Coroa(false);
            float[] xs = { -165, -60, 60, 165 }, dy = { 12, -34, -34, 12 };
            for (int i = 0; i < 4; i++)
            {
                float x = cx + xs[i], baseY = cy + dy[i], alt = 230, w = 38;
                var corVela = i == 2 ? Color.FromArgb(150, 0xdc, 0x9d, 0xb4) : Lz(85);
                using (var b = new SolidBrush(corVela)) g.FillRectangle(b, x - w / 2, baseY - alt, w, alt);
                using (var pen = new Pen(O(170), 2.5f)) { g.DrawRectangle(pen, x - w / 2, baseY - alt, w, alt); g.DrawLine(pen, x, baseY - alt, x, baseY - alt - 14); }
                if (i < acesas)
                {
                    Brilho(g, x, baseY - alt - 50, 90, Color.FromArgb(255, 240, 190), 110);
                    using var ch = Chama(x, baseY - alt - 12, 14, 62);
                    using var bc = new SolidBrush(Color.FromArgb(235, 255, 236, 180)); g.FillPath(bc, ch);
                    using var ci = Chama(x, baseY - alt - 14, 6, 30);
                    using var bi = new SolidBrush(Color.FromArgb(240, 255, 252, 235)); g.FillPath(bi, ci);
                }
            }
            Coroa(true);
        }

        static void Natal(Graphics g)
        {
            var rnd = new Random(11);
            for (int i = 0; i < 80; i++)
            {
                float x = 860 + (float)rnd.NextDouble() * 1040, y = 20 + (float)rnd.NextDouble() * 620, r = 2 + (float)rnd.NextDouble() * 4;
                using var p = Estrela(x, y, r * 2, r * .7f, 4);
                using var b = new SolidBrush(Lz(60 + rnd.Next(120))); g.FillPath(b, p);
            }
            float sx = 1560, sy = 250;
            Brilho(g, sx, sy, 300, luz, 90);
            using (var b = new SolidBrush(Lz(225)))
            {
                var pts = new[] {
                    new PointF(sx, sy - 130), new PointF(sx + 16, sy - 16), new PointF(sx + 150, sy), new PointF(sx + 16, sy + 16),
                    new PointF(sx, sy + 330), new PointF(sx - 16, sy + 16), new PointF(sx - 150, sy), new PointF(sx - 16, sy - 16) };
                g.FillPolygon(b, pts);
                using var d = Estrela(sx, sy, 62, 10, 4); var st = g.Save(); g.TranslateTransform(sx, sy); g.RotateTransform(45); g.TranslateTransform(-sx, -sy); g.FillPath(b, d); g.Restore(st);
            }
            // a gruta: colina com a entrada iluminada
            Colina(g, 860, 820);
            using var gruta = new GraphicsPath(); gruta.AddArc(1490, 880, 140, 160, 180, 180); gruta.AddLine(1630, 960, 1630, 1080); gruta.AddLine(1630, 1080, 1490, 1080); gruta.CloseFigure();
            using var bg = new SolidBrush(Lz(70)); g.FillPath(bg, gruta);
            using var pg = new Pen(O(150), 3); g.DrawPath(pg, gruta);
        }

        static void Quaresma(Graphics g)
        {
            Brilho(g, 1560, 420, 360, luz, 35);
            Cruz(g, 1560, 150, 900, 135, 340, 30, 35, 170);
            // coroa de espinhos no cruzamento
            using (var pen = new Pen(O(160), 2.2f))
            {
                g.DrawEllipse(pen, 1560 - 74, 340 - 22, 148, 44);
                var rnd = new Random(5);
                for (int k = 0; k < 46; k++)
                {
                    double t = k * 2 * Math.PI / 46; float x = 1560 + 74 * (float)Math.Cos(t), y = 340 + 22 * (float)Math.Sin(t);
                    float a = (float)(t + (rnd.NextDouble() - .5) * 1.6);
                    g.DrawLine(pen, x - 12 * (float)Math.Cos(a), y - 12 * (float)Math.Sin(a) * .6f, x + 12 * (float)Math.Cos(a), y + 12 * (float)Math.Sin(a) * .6f);
                }
            }
            // o deserto: dunas
            float[][] dunas = { new float[] { 900, 960, 60 }, new float[] { 980, 1010, 90 }, new float[] { 1040, 1050, 130 } };
            foreach (var d in dunas)
            {
                using var p = new GraphicsPath();
                p.AddBezier(d[0], 1080, d[0] + 300, d[1] - 120, 1500, d[1] - 40, 1960, d[1] - 90);
                p.AddLine(1960, d[1] - 90, 1960, 1080); p.CloseFigure();
                using var b = new SolidBrush(Sb((int)d[2])); g.FillPath(b, p);
                using var pen = new Pen(O(90), 2); g.DrawBezier(pen, d[0], 1080, d[0] + 300, d[1] - 120, 1500, d[1] - 40, 1960, d[1] - 90);
            }
        }

        static void Pascoa(Graphics g)
        {
            Raios(g, 1560, 980, 36, 120, 950, 180, 360, Lz(16));
            Brilho(g, 1560, 900, 420, luz, 110);
            Colina(g, 900, 830);
            Cruz(g, 1560, 360, 850, 125, 470, 28, 60, 190);
            // o pano branco sobre a travessa
            using var pano = new GraphicsPath();
            pano.AddBezier(1440, 462, 1500, 520, 1620, 520, 1680, 462);
            pano.AddBezier(1680, 462, 1690, 560, 1660, 640, 1640, 700);
            pano.AddBezier(1640, 700, 1610, 600, 1600, 540, 1560, 520);
            pano.AddBezier(1560, 520, 1520, 540, 1510, 600, 1480, 700);
            pano.AddBezier(1480, 700, 1460, 640, 1430, 560, 1440, 462);
            pano.CloseFigure();
            using var b = new SolidBrush(Color.FromArgb(200, 255, 253, 246)); g.FillPath(b, pano);
            using var pen = new Pen(O(150), 2); g.DrawPath(pen, pano);
        }

        static void Pentecostes(Graphics g)
        {
            Raios(g, 1540, -60, 30, 60, 1000, 45, 135, Lz(13));
            Brilho(g, 1540, 470, 380, Color.FromArgb(255, 236, 190), 80);
            for (int i = 0; i < 7; i++)        // os sete dons: chamas em arco
            {
                double a = (205 + i * 130 / 6.0) * Math.PI / 180;
                float x = 1540 + 300 * (float)Math.Cos(a), y = 560 + 300 * (float)Math.Sin(a);
                Brilho(g, x, y - 40, 80, Color.FromArgb(255, 220, 150), 90);
                using var ch = Chama(x, y, 26, 110); using var b = new SolidBrush(Color.FromArgb(220, 0xf6, 0xb2, 0x5a)); g.FillPath(b, ch);
                using var ci = Chama(x, y - 4, 12, 56); using var bi = new SolidBrush(Color.FromArgb(235, 255, 244, 210)); g.FillPath(bi, ci);
            }
            // a pomba, em traço simples, no meio do arco
            using var pomba = new GraphicsPath();
            float px = 1540, py = 560;
            pomba.AddBezier(px - 120, py - 30, px - 60, py - 110, px - 10, py - 60, px, py - 10);      // asa esquerda
            pomba.AddBezier(px, py - 10, px + 10, py - 60, px + 60, py - 110, px + 120, py - 30);      // asa direita
            pomba.AddBezier(px + 120, py - 30, px + 70, py - 20, px + 30, py, px + 14, py + 20);
            pomba.AddBezier(px + 14, py + 20, px + 30, py + 60, px + 20, py + 90, px, py + 110);      // cauda
            pomba.AddBezier(px, py + 110, px - 20, py + 90, px - 30, py + 60, px - 14, py + 20);
            pomba.AddBezier(px - 14, py + 20, px - 30, py, px - 70, py - 20, px - 120, py - 30);
            pomba.CloseFigure();
            using var bp = new SolidBrush(Lz(200)); g.FillPath(bp, pomba);
            using var pen = new Pen(O(170), 2); g.DrawPath(pen, pomba);
            using var cab = new SolidBrush(Lz(220)); g.FillEllipse(cab, px - 15, py - 42, 30, 30);
        }

        static void Maria(Graphics g)
        {
            float cx = 1540, cy = 470;
            Brilho(g, cx, cy, 340, luz, 70);
            using (var b = new SolidBrush(O(210)))
                for (int i = 0; i < 12; i++)      // as doze estrelas
                {
                    double a = (-90 + i * 30) * Math.PI / 180;
                    using var p = Estrela(cx + 270 * (float)Math.Cos(a), cy + 270 * (float)Math.Sin(a), 24, 10, 5); g.FillPath(b, p);
                }
            using (var f = new Font("Georgia", 300, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(O(190)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("M", f, b, new RectangleF(cx - 250, cy - 170, 500, 360), sf);
            Cruz(g, cx, cy - 215, cy - 105, 30, cy - 180, 12, 160, 200);
            // lírio estilizado embaixo
            using var pen = new Pen(O(140), 3);
            g.DrawBezier(pen, cx - 220, cy + 310, cx - 80, cy + 260, cx + 80, cy + 260, cx + 220, cy + 310);
        }

        static void Lirio(Graphics g, float x, float y, float tam, float rot)
        {
            var st = g.Save(); g.TranslateTransform(x, y); g.RotateTransform(rot);
            using var b = new SolidBrush(Lz(185)); using var pen = new Pen(O(170), 2);
            for (int k = 0; k < 5; k++)        // pétalas em trombeta, com a ponta virada para fora
            {
                float ang = -56 + k * 28; var s2 = g.Save(); g.RotateTransform(ang);
                float vira = ang < 0 ? -1 : ang > 0 ? 1 : 0;
                using var p = new GraphicsPath(); p.AddBezier(0, 0, -tam * .2f, -tam * .4f, -tam * .16f, -tam * .85f, vira * tam * .22f, -tam);
                p.AddBezier(vira * tam * .22f, -tam, tam * .16f, -tam * .8f, tam * .2f, -tam * .4f, 0, 0); p.CloseFigure();
                g.FillPath(b, p); g.DrawPath(pen, p); g.Restore(s2);
            }
            using var bo = new SolidBrush(O(220));
            for (int k = 0; k < 5; k++) { var s3 = g.Save(); g.RotateTransform(-40 + k * 20); g.DrawLine(pen, 0, 0, 0, -tam * .6f); g.FillEllipse(bo, -5, -tam * .6f - 8, 10, 14); g.Restore(s3); }
            g.Restore(st);
        }
        static void Jose(Graphics g)
        {
            Brilho(g, 1560, 420, 380, luz, 60);
            using (var pen = new Pen(O(170), 5))
            {
                g.DrawBezier(pen, 1600, 1080, 1610, 850, 1560, 650, 1565, 360);
                g.DrawBezier(pen, 1585, 640, 1540, 560, 1500, 520, 1480, 470);
                g.DrawBezier(pen, 1590, 680, 1640, 600, 1670, 560, 1680, 500);
            }
            using (var b = new SolidBrush(O(120)))
            {
                Folha(g, b, 1625, 880, 18, 120, 25); Folha(g, b, 1575, 820, 18, 120, -30);
                Folha(g, b, 1615, 760, 16, 100, 30); Folha(g, b, 1570, 720, 16, 90, -28);
            }
            Lirio(g, 1565, 360, 120, 0); Lirio(g, 1480, 470, 100, -35); Lirio(g, 1680, 500, 100, 35);
            // o esquadro do carpinteiro
            using var pe = new Pen(O(130), 4) { LineJoin = LineJoin.Miter };
            g.DrawLines(pe, new[] { new PointF(1240, 790), new PointF(1240, 1000), new PointF(1420, 1000), new PointF(1420, 970), new PointF(1270, 970), new PointF(1270, 790), new PointF(1240, 790) });
            for (int k = 1; k < 6; k++) g.DrawLine(pe, 1270 + k * 25, 970, 1270 + k * 25, 985);
        }

        static void Eucaristia(Graphics g)
        {
            float cx = 1560;
            Raios(g, cx, 390, 48, 135, 300, 0, 360, Lz(55));
            Brilho(g, cx, 390, 300, luz, 90);
            using (var b = new SolidBrush(Color.FromArgb(235, 255, 252, 242))) g.FillEllipse(b, cx - 115, 275, 230, 230);
            using (var pen = new Pen(O(170), 3))
            {
                g.DrawEllipse(pen, cx - 115, 275, 230, 230);
                g.DrawEllipse(pen, cx - 95, 295, 190, 190);
                g.DrawLine(pen, cx, 325, cx, 455); g.DrawLine(pen, cx - 50, 370, cx + 50, 370);
            }
            // o cálice
            using var calice = new GraphicsPath();
            calice.AddLine(cx - 120, 560, cx + 120, 560);
            calice.AddBezier(cx + 120, 560, cx + 120, 680, cx + 60, 730, cx + 18, 745);
            calice.AddLine(cx + 18, 745, cx + 18, 880);
            calice.AddBezier(cx + 18, 880, cx + 60, 900, cx + 120, 915, cx + 125, 945);
            calice.AddLine(cx + 125, 945, cx - 125, 945);
            calice.AddBezier(cx - 125, 945, cx - 120, 915, cx - 60, 900, cx - 18, 880);
            calice.AddLine(cx - 18, 880, cx - 18, 745);
            calice.AddBezier(cx - 18, 745, cx - 60, 730, cx - 120, 680, cx - 120, 560);
            calice.CloseFigure();
            using var bc = new SolidBrush(O(150)); g.FillPath(bc, calice);
            using var pc = new Pen(O(230), 3); g.DrawPath(pc, calice);
            g.FillEllipse(bc, cx - 34, 795, 68, 30); g.DrawEllipse(pc, cx - 34, 795, 68, 30);
        }

        static void Trigo(Graphics g, PointF a, PointF c1, PointF c2, PointF ponta)
        {
            using var pen = new Pen(O(160), 4); g.DrawBezier(pen, a, c1, c2, ponta);
            using var b = new SolidBrush(O(170)); using var fino = new Pen(O(120), 1.5f);
            PointF Em(float t)
            {
                float u = 1 - t;
                return new PointF(u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * ponta.X,
                                  u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * ponta.Y);
            }
            for (int k = 0; k < 11; k++)
            {
                float t = .62f + k * .035f; var p = Em(t); var q = Em(t + .01f);
                float ang = (float)(Math.Atan2(q.Y - p.Y, q.X - p.X) * 180 / Math.PI) + 90;
                float tam = 46 - k * 2.4f;
                foreach (var lado in new[] { -28f, 28f })
                {
                    Folha(g, b, p.X, p.Y, tam * .42f, tam, ang + lado);
                    double ar = (ang + lado * 1.4 - 90) * Math.PI / 180;
                    g.DrawLine(fino, p.X, p.Y, p.X + (float)Math.Cos(ar) * tam * 1.6f, p.Y + (float)Math.Sin(ar) * tam * 1.6f);
                }
            }
        }
        static void Comum(Graphics g)
        {
            Brilho(g, 1560, 520, 380, luz, 45);
            Trigo(g, new PointF(1600, 1090), new PointF(1580, 800), new PointF(1520, 560), new PointF(1470, 350));
            Trigo(g, new PointF(1610, 1090), new PointF(1610, 780), new PointF(1600, 520), new PointF(1610, 300));
            Trigo(g, new PointF(1620, 1090), new PointF(1650, 800), new PointF(1700, 580), new PointF(1760, 390));
            // um cacho de uva ao lado
            using var b = new SolidBrush(O(115)); using var pen = new Pen(O(170), 2);
            float ux = 1330, uy = 700; int[] linhas = { 4, 3, 3, 2, 1 };
            for (int l = 0; l < linhas.Length; l++)
                for (int k = 0; k < linhas[l]; k++)
                {
                    float x = ux + (k - (linhas[l] - 1) / 2f) * 46, y = uy + l * 40;
                    g.FillEllipse(b, x - 22, y - 22, 44, 44); g.DrawEllipse(pen, x - 22, y - 22, 44, 44);
                }
            g.DrawBezier(pen, ux, uy - 22, ux + 5, uy - 60, ux + 30, uy - 80, ux + 60, uy - 90);
            Folha(g, b, ux + 70, uy - 60, 40, 90, 60);
        }
    }
}
