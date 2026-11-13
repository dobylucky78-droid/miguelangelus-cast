// Capa da live (Ajustes › Capa da live): a mesma arte vira a miniatura da live (PNG 1920×1080) e três cenas:
//  "capa:espera"  Tela de espera, com contagem regressiva até a hora da celebração
//  "capa:capa"    Abertura
//  "capa:fim"     Encerramento
// Três modelos do protótipo (arco, imagem inteira, central). Cor litúrgica e nome da celebração vêm sozinhos do
// calendário (Liturgia.cs), a não ser que o usuário escolha à mão.
// A ilustração do folheto costuma ter direitos autorais: fica só na pasta de dados deste computador, nunca no programa.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiguelAngelusCast.Motor
{
    public sealed class CapaCfg
    {
        public string Modelo { get; set; } = "arco";          // arco | sangrada | central
        public string Cor { get; set; } = "";                 // "" = automática pelo calendário
        public string Titulo { get; set; } = "Santa Missa";
        public string Chamada { get; set; } = "ao vivo, diretamente da nossa paróquia";
        public string Extra { get; set; } = "";
        public string Celebrante { get; set; } = "";
        public string Imagem { get; set; } = "simbolo";        // folheto | foto | simbolo
        public string Folheto { get; set; } = "";             // ilustração da semana (só neste computador)
        public string Foto { get; set; } = "";                // foto do altar ou da fachada
        public string Marca1 { get; set; } = "";              // brasão da paróquia
        public string Marca2 { get; set; } = "";              // logo da Pascom
        public bool UsarMarca1 { get; set; } = true;
        public bool UsarMarca2 { get; set; } = true;
        public DateTime Quando { get; set; } = ProximoDomingo();   // próxima celebração
        public string Celebracao { get; set; } = "";          // "" = automática
        public string Proxima { get; set; } = "";             // "Domingo, 07h00" no Encerramento
        public bool EsperaAuto { get; set; }                  // põe a Tela de espera no ar sozinho, X minutos antes
        public int EsperaMin { get; set; } = 15;
        // (0.0.5) agenda do MiguelAngelus: data/hora e "Próxima Missa" vêm de lá (Motor\Miguel.cs)
        public bool SeguirMiguel { get; set; } = true;
        public string MiguelLocal { get; set; } = "";          // sigla da capela que este Cast transmite ("" = todas)
        public bool MiguelSoMissas { get; set; } = true;       // Terço, Adoração… não viram "próxima transmissão"
        public bool MiguelFolheto { get; set; } = true;        // (0.0.6) ilustração do folheto vem do roteiro do MiguelAngelus
        public bool MiguelMarcas { get; set; } = true;         // (0.0.7) brasão e logo vêm dos cantos do telão do MiguelAngelus
        // (0.1.12) celebrações marcadas para transmitir ("2026-11-15T07:00|SJO"; nenhuma = todas) e a escolhida no Início ("" = a primeira)
        public List<string> MiguelPreparadas { get; set; } = new();
        public string MiguelEscolhida { get; set; } = "";
        public string YtPrivacidade { get; set; } = "public";   // (0.0.8) live criada pelo Cast: public | unlisted | private
        public string YtDescricao { get; set; } = "";

        static DateTime ProximoDomingo()
        {
            var d = DateTime.Today.AddDays(1); while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
            return d.AddHours(7);
        }
    }

    public static class Capa
    {
        static readonly string arquivo = Path.Combine(Program.PastaDados, "capa.json");
        public static readonly string Pasta = Path.Combine(Program.PastaDados, "capa");
        static CapaCfg cfg = Carregar();
        public static CapaCfg Cfg => cfg;
        public static int Versao { get; private set; } = 1;

        static CapaCfg Carregar()
        {
            try
            {
                if (File.Exists(arquivo))
                {
                    var c = JsonSerializer.Deserialize<CapaCfg>(File.ReadAllText(arquivo)) ?? new CapaCfg();
                    // backup vindo de outro computador: a pasta de dados lá era outra; as imagens estão na pasta "capa" daqui
                    string Achar(string p) => string.IsNullOrEmpty(p) || File.Exists(p) ? p : File.Exists(Path.Combine(Pasta, Path.GetFileName(p))) ? Path.Combine(Pasta, Path.GetFileName(p)) : "";
                    c.Folheto = Achar(c.Folheto); c.Foto = Achar(c.Foto); c.Marca1 = Achar(c.Marca1); c.Marca2 = Achar(c.Marca2);
                    while (c.Quando < DateTime.Now.AddHours(-3)) c.Quando = c.Quando.AddDays(7);   // já passou: a mesma hora na semana seguinte
                    return c;
                }
            }
            catch (Exception e) { Registro.Erro("ler capa.json", e); }
            return new CapaCfg();
        }
        public static void Trocar(CapaCfg nova, bool gravar)
        {
            cfg = nova; Versao++;
            if (!gravar) return;
            try { File.WriteAllText(arquivo, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar capa.json", e); }
        }
        // copia a imagem escolhida para a pasta de dados (o original pode sumir do pen drive)
        public static string GuardarImagem(string origem, string nome)
        {
            Directory.CreateDirectory(Pasta);
            var destino = Path.Combine(Pasta, nome + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(origem).ToLowerInvariant());
            File.Copy(origem, destino, true);
            return destino;
        }

        // ---------- cores ----------
        public sealed class Paleta { public string Nome; public Color F0, F1, F2, Amostra, Tinta, Acento, I1, I2; public float P1; }
        static Color Hex(string s) => ColorTranslator.FromHtml(s);
        public static readonly Dictionary<string, Paleta> Paletas = new Dictionary<string, Paleta>
        {
            ["verde"] = new Paleta { Nome = "Verde", F0 = Hex("#2a7a56"), F1 = Hex("#16503a"), F2 = Hex("#0b2d20"), P1 = .45f, Amostra = Hex("#1f6a4a"), Tinta = Hex("#f6f1e3"), Acento = Hex("#e3c27a"), I1 = Hex("#3d8a66"), I2 = Hex("#1a5038") },
            ["roxo"] = new Paleta { Nome = "Roxo", F0 = Hex("#64449a"), F1 = Hex("#3b2263"), F2 = Hex("#1d0f33"), P1 = .45f, Amostra = Hex("#4a2c78"), Tinta = Hex("#f4effa"), Acento = Hex("#e0c27e"), I1 = Hex("#7656a8"), I2 = Hex("#3a2262") },
            ["branco"] = new Paleta { Nome = "Branco", F0 = Hex("#fffdf7"), F1 = Hex("#f4ecd6"), F2 = Hex("#e3d3a8"), P1 = .5f, Amostra = Hex("#f3ead2"), Tinta = Hex("#3a2e16"), Acento = Hex("#a07a28"), I1 = Hex("#c9b074"), I2 = Hex("#8d6c2a") },
            ["vermelho"] = new Paleta { Nome = "Vermelho", F0 = Hex("#ad2a34"), F1 = Hex("#761822"), F2 = Hex("#3c0a10"), P1 = .45f, Amostra = Hex("#8e1f2a"), Tinta = Hex("#fbf0ee"), Acento = Hex("#efc77c"), I1 = Hex("#c04a52"), I2 = Hex("#6e1620") },
            ["rosa"] = new Paleta { Nome = "Rosa", F0 = Hex("#dc9db4"), F1 = Hex("#ad6884"), F2 = Hex("#6c3450"), P1 = .5f, Amostra = Hex("#c27a96"), Tinta = Hex("#fff6f8"), Acento = Hex("#fbe2a8"), I1 = Hex("#e2a9bd"), I2 = Hex("#8e4a68") },
        };
        public static string CorAuto(CapaCfg c) => Liturgia.Info(c.Quando).cor;
        public static string CorUsada(CapaCfg c) => Paletas.ContainsKey(c.Cor ?? "") ? c.Cor : CorAuto(c);
        public static string CelebracaoUsada(CapaCfg c) => string.IsNullOrWhiteSpace(c.Celebracao) ? Liturgia.Info(c.Quando).nome : c.Celebracao.Trim();

        static readonly CultureInfo br = new CultureInfo("pt-BR");
        public static string DataLonga(DateTime d)
        {
            var s = d.ToString("dddd, d 'de' MMMM 'de' yyyy", br);
            return char.ToUpper(s[0]) + s.Substring(1);
        }
        public static string Hora(DateTime d) => d.ToString("HH'h'mm");

        // ---------- desenho ----------
        static readonly object trava = new object();
        static readonly Dictionary<string, (DateTime quando, Bitmap bmp)> imagens = new Dictionary<string, (DateTime, Bitmap)>();
        static Bitmap Img(string caminho)
        {
            if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return null;
            var t = File.GetLastWriteTimeUtc(caminho);
            if (imagens.TryGetValue(caminho, out var x) && x.quando == t) return x.bmp;
            try
            {
                Bitmap b; using (var tmp = new Bitmap(caminho)) b = new Bitmap(tmp);   // cópia: não deixa o arquivo preso
                if (x.bmp != null) x.bmp.Dispose();
                imagens[caminho] = (t, b); return b;
            }
            catch (Exception e) { Registro.Erro("capa: imagem " + caminho, e); return null; }
        }

        sealed class Lay { public float iL, iT, iW, iH, tL, tT, tW, tit; public bool centro, esticar, chamada, fade; public string forma; public float zoom = 1, oX = .5f, oY = .5f, posY = .5f; }
        static Lay Layout(string m) => m switch
        {
            "sangrada" => new Lay { iL = .44f, iT = 0, iW = .56f, iH = 1, tL = .067f, tT = .11f, tW = .48f, tit = 10.8f, fade = true, forma = "reta", posY = .2f },
            "central" => new Lay { iL = .44f, iT = .06f, iW = .12f, iH = -1, tL = 0, tT = .305f, tW = 1, tit = 6.8f, centro = true, forma = "circulo", zoom = 1.9f, oX = .33f, oY = .14f, posY = 0 },
            _ => new Lay { iL = .568f, iT = .085f, iW = .344f, iH = .83f, tL = .067f, tT = .09f, tW = .47f, tit = 9.6f, esticar = true, chamada = true, forma = "arco", posY = .25f },
        };

        /// <summary>Desenha a capa. modo: capa | espera | fim.</summary>
        public static Bitmap Desenhar(string modo, int W = 1920, int H = 1080, CapaCfg c = null, DateTime? agora = null)
        {
            c ??= cfg;
            lock (trava)
            {
                var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float u = W / 100f, px = W / 960f;
                var P = Paletas[CorUsada(c)]; bool claro = CorUsada(c) == "branco";
                var L = Layout(c.Modelo);
                Color suave = claro ? Color.FromArgb(173, 58, 46, 22) : Color.FromArgb(184, 255, 255, 255);
                Color caixa = claro ? Color.FromArgb(31, 160, 122, 40) : Color.FromArgb(56, 0, 0, 0);
                Color fio = claro ? Color.FromArgb(89, 160, 122, 40) : Color.FromArgb(36, 255, 255, 255);
                Color tintaAcento = claro ? Hex("#fffdf7") : Hex("#1d1405");

                // fundo: gradiente radial (120% 100% em 15% 0%) e a textura de riscos finos
                g.Clear(P.F2);
                using (var cam = new GraphicsPath())
                {
                    cam.AddEllipse(.15f * W - 1.2f * W, -H, 2.4f * W, 2f * H);
                    using var pb = new PathGradientBrush(cam) { CenterPoint = new PointF(.15f * W, 0), CenterColor = P.F0 };
                    pb.InterpolationColors = new ColorBlend { Colors = new[] { P.F2, P.F1, P.F0 }, Positions = new[] { 0f, 1 - P.P1, 1f } };
                    g.FillPath(pb, cam);
                }
                using (var risco = new Pen(Color.FromArgb(4, 255, 255, 255), 2 * px))
                {
                    var st = g.Save(); g.TranslateTransform(W / 2f, H / 2f); g.RotateTransform(25);
                    float R = W + H; for (float x = -R; x < R; x += 9 * px) g.DrawLine(risco, x, -R, x, R);
                    g.Restore(st);
                }

                // imagem (ilustração do folheto, foto ou o símbolo)
                float iw = L.iW * W, ih = L.iH < 0 ? iw : L.iH * H, ix = L.iL * W, iy = L.iT * H;
                var caixaImg = new RectangleF(ix, iy, iw, ih);
                using (var forma = Forma(L.forma, caixaImg, u))
                {
                    if (L.forma == "arco" || L.forma == "circulo")
                    {
                        float off = L.forma == "arco" ? u : .5f * u;
                        using var fora = Forma(L.forma, RectangleF.Inflate(caixaImg, off, off), u, off);
                        using var pen = new Pen(P.Acento, (L.forma == "arco" ? 1.5f : 2f) * px);
                        g.DrawPath(pen, fora);
                    }
                    var st = g.Save(); g.SetClip(forma);
                    using (var lg = new LinearGradientBrush(RectangleF.Inflate(caixaImg, 1, 1), P.I1, P.I2, 70f)) g.FillRectangle(lg, caixaImg);
                    var foto = c.Imagem == "folheto" ? Img(c.Folheto) : c.Imagem == "foto" ? Img(c.Foto) : null;
                    if (foto != null) Cobrir(g, foto, caixaImg, L);
                    else Simbolo(g, caixaImg, P.Acento, c.Imagem == "simbolo" ? null : "Escolha a imagem nos ajustes", u);
                    g.Restore(st);
                }
                if (L.fade)
                {
                    var r = new RectangleF(ix, 0, W - ix, H);
                    using var fb = new LinearGradientBrush(new PointF(ix - 1, 0), new PointF(ix + .45f * r.Width + 2, 0), P.F1, Color.FromArgb(0, P.F1)) { WrapMode = WrapMode.TileFlipX };
                    g.FillRectangle(fb, ix, 0, .45f * r.Width + 1, H);
                }

                // textos
                bool fim = modo == "fim", espera = modo == "espera";
                var now = agora ?? DateTime.Now;
                string eyebrow = fim ? "Obrigado por participar" : espera ? "Em instantes, ao vivo" : "Transmissão ao vivo";
                float x0 = L.tL * W, tw = L.tW * W, y = L.tT * H;
                if (L.centro) { x0 = .08f * W; tw = .84f * W; }
                using var fEb = new Font("Segoe UI Semibold", 1.15f * u, GraphicsUnit.Pixel);
                using var bAc = new SolidBrush(P.Acento); using var bTi = new SolidBrush(P.Tinta); using var bSu = new SolidBrush(suave);
                y += Espacado(g, eyebrow.ToUpperInvariant(), fEb, bAc, x0, y, tw, L.centro, .32f) + 1.3f * u;

                float tam = L.tit * u; var titulo = string.IsNullOrWhiteSpace(c.Titulo) ? "Santa Missa" : c.Titulo.Trim();
                Font fTit = new Font("Georgia", tam, FontStyle.Italic, GraphicsUnit.Pixel);
                while (g.MeasureString(titulo, fTit, PointF.Empty, StringFormat.GenericTypographic).Width > tw && tam > L.tit * u * .55f)
                { fTit.Dispose(); tam *= .94f; fTit = new Font("Georgia", tam, FontStyle.Italic, GraphicsUnit.Pixel); }
                y += Bloco(g, titulo, fTit, bTi, x0, y - .08f * tam, tw, L.centro, .95f) - .08f * tam;
                fTit.Dispose();

                // divisor ── ✠ ──
                y += 1.5f * u;
                using (var fCruz = new Font("Segoe UI Symbol", 1.5f * u, GraphicsUnit.Pixel))
                using (var pDiv = new Pen(Color.FromArgb(178, P.Acento), Math.Max(1, px)))
                {
                    float cw = g.MeasureString("✠", fCruz, PointF.Empty, StringFormat.GenericTypographic).Width, wl = 8 * u, gap = u;
                    float total = wl * 2 + gap * 2 + cw, dx = L.centro ? x0 + (tw - total) / 2 : x0, my = y + .9f * u;
                    g.DrawLine(pDiv, dx, my, dx + wl, my);
                    g.DrawString("✠", fCruz, bAc, dx + wl + gap, y, StringFormat.GenericTypographic);
                    g.DrawLine(pDiv, dx + wl + gap * 2 + cw, my, dx + total, my);
                }
                y += 1.8f * u + 1.3f * u;

                using (var fCel = new Font("Segoe UI Semibold", 2.7f * u, GraphicsUnit.Pixel)) y += Bloco(g, CelebracaoUsada(c), fCel, bTi, x0, y, tw, L.centro, 1.15f);
                if (!string.IsNullOrWhiteSpace(c.Extra))
                    using (var fEx = new Font("Georgia", 2.2f * u, FontStyle.Italic, GraphicsUnit.Pixel)) y += .6f * u + Bloco(g, c.Extra.Trim(), fEx, bAc, x0, y + .6f * u, tw, L.centro, 1.2f);
                string chamada = fim && string.IsNullOrWhiteSpace(c.Proxima) ? "Deus abençoe a sua semana" : L.chamada ? c.Chamada : null;
                if (!string.IsNullOrWhiteSpace(chamada))
                    using (var fCh = new Font("Segoe UI", 1.35f * u, GraphicsUnit.Pixel)) y += 1.2f * u + Bloco(g, chamada.Trim(), fCh, bSu, x0, y + 1.2f * u, tw, L.centro, 1.4f);

                // caixa com a data e a hora
                if (!(fim && string.IsNullOrWhiteSpace(c.Proxima)))
                {
                    y += 2.6f * u;
                    string rot = fim ? "PRÓXIMA MISSA" : "AO VIVO", data = fim ? c.Proxima.Trim() : DataLonga(c.Quando), hora = fim ? "" : Hora(c.Quando);
                    string cont = null;
                    if (espera)
                    {
                        var falta = c.Quando - now;
                        cont = falta.TotalSeconds <= 0 ? "JÁ VAI COMEÇAR"
                             : falta.TotalDays >= 1 ? (falta.Days == 1 ? "FALTA 1 DIA" : "FALTAM " + falta.Days + " DIAS") + (falta.Hours > 0 ? " E " + falta.Hours + "H" : "")
                             : falta.TotalHours >= 1 ? "FALTAM " + (int)falta.TotalHours + "H" + falta.Minutes.ToString("00")
                             : "FALTAM " + falta.Minutes + ":" + falta.Seconds.ToString("00");
                    }
                    var sf = StringFormat.GenericTypographic;
                    using var fRot = new Font("Segoe UI", 1.15f * u, GraphicsUnit.Pixel);
                    using var fHora = new Font("Segoe UI", 2.7f * u, FontStyle.Bold, GraphicsUnit.Pixel); using var fCont = new Font("Segoe UI Semibold", 1.05f * u, GraphicsUnit.Pixel);
                    float wHora = hora == "" ? 0 : g.MeasureString(hora, fHora, PointF.Empty, sf).Width, wCont = cont == null ? 0 : LarguraEspacada(g, cont, fCont, .1f);
                    float wDir = hora == "" ? 0 : Math.Max(wHora, wCont) + 3.4f * u;
                    // a data diminui (até 70%) para caber ao lado da hora, sem passar por baixo dela
                    float tData = 1.9f * u, wRot = LarguraEspacada(g, rot, fRot, .14f);
                    var fData = new Font("Segoe UI Semibold", tData, GraphicsUnit.Pixel);
                    float wData = g.MeasureString(data, fData, PointF.Empty, sf).Width;
                    while (L.esticar && Math.Max(wRot, wData) + 3.4f * u + wDir > tw && tData > 1.9f * u * .7f)
                    { fData.Dispose(); tData *= .95f; fData = new Font("Segoe UI Semibold", tData, GraphicsUnit.Pixel); wData = g.MeasureString(data, fData, PointF.Empty, sf).Width; }
                    float wEsq = Math.Max(wRot, wData) + 3.4f * u, hEsq = 2.5f * u + 1.15f * u * 1.25f + .3f * u + tData * 1.25f;
                    // hora + contagem: a hora (2,7u) e a contagem logo abaixo, com folga para não encostar
                    float hh = 2.7f * u * 1.2f + (cont != null ? .35f * u + 1.05f * u * 1.2f : 0);
                    float wBox = L.esticar ? Math.Max(tw, wEsq + wDir) : wEsq + wDir, hBox = Math.Max(hEsq, hh + 2.2f * u), bx = L.centro ? x0 + (tw - wBox) / 2 : x0;
                    using (var cx = Arred(new RectangleF(bx, y, wBox, hBox), 1.25f * u))
                    {
                        var st = g.Save(); g.SetClip(cx);
                        using (var bc = new SolidBrush(caixa)) g.FillRectangle(bc, bx, y, wBox, hBox);
                        if (wDir > 0) using (var ba = new SolidBrush(P.Acento)) g.FillRectangle(ba, bx + wBox - wDir, y, wDir, hBox);
                        g.Restore(st);
                    }
                    float ty = y + (hBox - hEsq) / 2;
                    Espacado(g, rot, fRot, bSu, bx + 1.7f * u, ty + 1.25f * u, 0, false, .14f);
                    g.DrawString(data, fData, bTi, bx + 1.7f * u, ty + 1.25f * u + 1.15f * u * 1.25f + .3f * u, sf);
                    fData.Dispose();
                    if (wDir > 0)
                    {
                        using var bTa = new SolidBrush(tintaAcento);
                        float hx = bx + wBox - wDir, hy = y + (hBox - hh) / 2;
                        g.DrawString(hora, fHora, bTa, hx + (wDir - wHora) / 2, hy, sf);
                        if (cont != null) Espacado(g, cont, fCont, bTa, hx + (wDir - wCont) / 2, hy + 2.7f * u * 1.2f + .35f * u, 0, false, .1f);
                    }
                    y += hBox;
                }

                // marcas e celebrante
                var marcas = new List<Bitmap>();
                if (c.UsarMarca1 && Img(c.Marca1) is Bitmap m1) marcas.Add(m1);
                if (c.UsarMarca2 && Img(c.Marca2) is Bitmap m2) marcas.Add(m2);
                bool temCeleb = !string.IsNullOrWhiteSpace(c.Celebrante);
                if (marcas.Count > 0 || temCeleb)
                {
                    y += 2.6f * u;
                    float lh = 4.8f * u, wPlaca = 0;
                    foreach (var m in marcas) wPlaca += lh * m.Width / m.Height;
                    if (marcas.Count > 0) wPlaca += 2 * u + (marcas.Count - 1) * u;
                    using var fCe = new Font("Segoe UI", 1.35f * u, GraphicsUnit.Pixel); using var fCeB = new Font("Segoe UI Semibold", 1.35f * u, GraphicsUnit.Pixel);
                    float wCel = temCeleb ? Math.Max(g.MeasureString("Celebrante", fCe).Width, g.MeasureString(c.Celebrante.Trim(), fCeB).Width) : 0;
                    float total = wPlaca + (marcas.Count > 0 && temCeleb ? 1.5f * u : 0) + wCel, hx = L.centro ? x0 + (tw - total) / 2 : x0, hPl = lh + 1.2f * u;
                    if (marcas.Count > 0)
                    {
                        using (var pl = Arred(new RectangleF(hx, y, wPlaca, hPl), 1.25f * u)) using (var bw = new SolidBrush(Color.FromArgb(240, 255, 255, 255))) g.FillPath(bw, pl);
                        float mx = hx + u;
                        foreach (var m in marcas) { float mw = lh * m.Width / m.Height; g.DrawImage(m, mx, y + .6f * u, mw, lh); mx += mw + u; }
                        hx += wPlaca + 1.5f * u;
                    }
                    if (temCeleb)
                    {
                        float cy = y + (marcas.Count > 0 ? hPl : 3.6f * u) / 2 - 1.35f * u * 1.3f;
                        g.DrawString("Celebrante", fCe, bSu, hx, cy);
                        g.DrawString(c.Celebrante.Trim(), fCeB, bTi, hx, cy + 1.35f * u * 1.3f);
                    }
                }

                // fio da moldura por cima de tudo
                using (var mold = Arred(RectangleF.Inflate(new RectangleF(0, 0, W, H), -1.45f * u, -1.45f * u), .6f * u))
                using (var pf = new Pen(fio, Math.Max(1, px))) g.DrawPath(pf, mold);
                return bmp;
            }
        }

        public static byte[] Png(string modo, int W = 1920, int H = 1080)
        {
            using var b = Desenhar(modo, W, H); using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray();
        }
        public static string JpegBase64(string modo, CapaCfg c, int W = 960, int H = 540) => Convert.ToBase64String(Jpeg(modo, W, H, c));
        // (0.0.8) miniatura do YouTube: JPEG 1280×720 (o limite de lá é 2 MB; fica em ~200–400 KB)
        public static byte[] Jpeg(string modo, int W = 1280, int H = 720, CapaCfg c = null, long qualidade = 90)
        {
            using var b = Desenhar(modo, W, H, c); using var ms = new MemoryStream();
            var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qualidade);
            b.Save(ms, enc, ps); return ms.ToArray();
        }

        // ---------- ajudantes de desenho ----------
        static GraphicsPath Arred(RectangleF r, float raio)
        {
            var p = new GraphicsPath(); float d = Math.Min(raio * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        static GraphicsPath Forma(string forma, RectangleF r, float u, float extra = 0)
        {
            if (forma == "circulo") { var p = new GraphicsPath(); p.AddEllipse(r); return p; }
            if (forma != "arco") { var p = new GraphicsPath(); p.AddRectangle(r); return p; }
            var a = new GraphicsPath(); float top = Math.Min(17 * u + extra, r.Width / 2) * 2, bot = (u + extra) * 2;
            a.AddArc(r.X, r.Y, top, top, 180, 90); a.AddArc(r.Right - top, r.Y, top, top, 270, 90);
            a.AddArc(r.Right - bot, r.Bottom - bot, bot, bot, 0, 90); a.AddArc(r.X, r.Bottom - bot, bot, bot, 90, 90); a.CloseFigure();
            return a;
        }
        // object-fit: cover, com a posição vertical do modelo e o zoom do medalhão
        static void Cobrir(Graphics g, Bitmap img, RectangleF r, Lay L)
        {
            float esc = Math.Max(r.Width / img.Width, r.Height / img.Height), w = img.Width * esc, h = img.Height * esc;
            float x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) * L.posY;
            if (L.zoom != 1)
            {
                float ox = r.X + r.Width * L.oX, oy = r.Y + r.Height * L.oY;
                x = ox + (x - ox) * L.zoom; y = oy + (y - oy) * L.zoom; w *= L.zoom; h *= L.zoom;
            }
            g.DrawImage(img, x, y, w, h);
        }
        // desenho próprio do Cast: raios, hóstia com cruz e cálice (o mesmo do protótipo)
        static void Simbolo(Graphics g, RectangleF r, Color cor, string aviso, float u)
        {
            float esc = Math.Min(r.Width * .66f / 220, r.Height * .66f / 300), w = 220 * esc, h = 300 * esc;
            var st = g.Save(); g.TranslateTransform(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2); g.ScaleTransform(esc, esc);
            using (var raio = new Pen(Color.FromArgb(140, cor), 1.2f))
                foreach (var (x2, y2) in new[] { (110, 0), (30, 20), (190, 20), (0, 80), (220, 80), (0, 150), (220, 150) }) g.DrawLine(raio, 110, 110, x2, y2);
            using (var hb = new SolidBrush(Hex("#fbf6e8"))) g.FillEllipse(hb, 64, 64, 92, 92);
            using (var p3 = new Pen(cor, 3)) g.DrawEllipse(p3, 64, 64, 92, 92);
            using (var p4 = new Pen(cor, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) { g.DrawLine(p4, 110, 84, 110, 136); g.DrawLine(p4, 88, 104, 132, 104); }
            using (var cb = new SolidBrush(Color.FromArgb(230, cor)))
            using (var calice = new GraphicsPath())
            {
                calice.AddLine(62, 190, 158, 190); calice.AddBezier(158, 190, 154, 201, 152, 207, 148, 218);
                calice.AddBezier(148, 218, 142, 234, 126, 244, 110, 244); calice.AddBezier(110, 244, 94, 244, 78, 234, 72, 218); calice.CloseFigure();
                g.FillPath(cb, calice); g.FillRectangle(cb, 104, 244, 12, 30);
                using var base0 = Arred(new RectangleF(78, 272, 64, 9), 4); g.FillPath(cb, base0);
            }
            g.Restore(st);
            if (aviso != null)
                using (var f = new Font("Segoe UI", 1.2f * u, GraphicsUnit.Pixel)) using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.DrawString(aviso, f, b, new RectangleF(r.X, r.Bottom - 4 * u, r.Width, 3 * u), new StringFormat { Alignment = StringAlignment.Center });
        }
        static float LarguraEspacada(Graphics g, string s, Font f, float em)
        {
            float w = 0; foreach (var ch in s) w += g.MeasureString(ch.ToString(), f, PointF.Empty, StringFormat.GenericTypographic).Width + (ch == ' ' ? f.Size * .28f : 0) + em * f.Size;
            return w - em * f.Size;
        }
        // texto de uma linha com espaço entre as letras; devolve a altura usada
        static float Espacado(Graphics g, string s, Font f, Brush b, float x, float y, float largura, bool centro, float em)
        {
            if (centro) x += (largura - LarguraEspacada(g, s, f, em)) / 2;
            foreach (var ch in s)
            {
                g.DrawString(ch.ToString(), f, b, x, y, StringFormat.GenericTypographic);
                x += g.MeasureString(ch.ToString(), f, PointF.Empty, StringFormat.GenericTypographic).Width + (ch == ' ' ? f.Size * .28f : 0) + em * f.Size;
            }
            return f.Size * 1.25f;
        }
        // parágrafo com quebra de linha dentro da largura; devolve a altura usada
        static float Bloco(Graphics g, string s, Font f, Brush b, float x, float y, float largura, bool centro, float alturaLinha)
        {
            var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = centro ? StringAlignment.Center : StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
            var linhas = new List<string>(); var atual = "";
            foreach (var p in s.Split(' '))
            {
                var t = atual == "" ? p : atual + " " + p;
                if (atual != "" && g.MeasureString(t, f, PointF.Empty, sf).Width > largura) { linhas.Add(atual); atual = p; } else atual = t;
            }
            if (atual != "") linhas.Add(atual);
            if (linhas.Count > 3) { linhas = linhas.GetRange(0, 3); linhas[2] += "…"; }
            float lh = f.Size * alturaLinha;
            for (int i = 0; i < linhas.Count; i++)
                g.DrawString(linhas[i], f, b, new RectangleF(x, y + i * lh, largura, lh * 1.4f), sf);
            return lh * linhas.Count;
        }
    }

    // cena com a capa: redesenha quando os ajustes mudam e, na tela de espera, a cada segundo (contagem)
    public sealed class FonteCapa : Fonte
    {
        readonly string modo; Imagem img = new Imagem(16, 9); string feito = ""; int ocupado;
        readonly int L, A;
        public FonteCapa(string modo, int l = 1920, int a = 1080) { this.modo = modo; L = l; A = a; Spec = "capa:" + modo; Estado = "preparando"; }
        public override Imagem Atual()
        {
            var chave = Capa.Versao + (modo == "espera" ? "|" + DateTime.Now.ToString("HHmmss") : "");
            if (chave != feito && Interlocked.CompareExchange(ref ocupado, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        using var b = Capa.Desenhar(modo, L, A);
                        var nova = new Imagem(); FonteImagem.Carregar(b, nova);
                        img = nova; feito = chave; Seq++; Estado = "ok";
                    }
                    catch (Exception e) { Estado = "erro ao desenhar a capa"; Registro.Erro("capa " + modo, e); feito = chave; }
                    finally { Volatile.Write(ref ocupado, 0); }
                });
            }
            return img;
        }
    }
}
