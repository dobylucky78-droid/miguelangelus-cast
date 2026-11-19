// (0.1.16) Capa do corte (Short / Reels), 1080×1920 em JPEG, gerada sozinha:
//  - fundo: um quadro do vídeo de encerramento escolhido (as luzes desfocadas; desfocado forte, o texto dele some);
//    sem vinheta, um quadro do próprio corte, desfocado;
//  - em cima o brasão da paróquia, no meio a frase principal (entre aspas), embaixo a data da homilia e o logo da Pascom
//    (brasão e logo = os mesmos da capa da live, que vêm do MiguelAngelus).
// Fica ao lado do corte: "<corte>.capa.jpg" (vai como miniatura para o YouTube e serve de capa no Instagram).
// Na hora de publicar, o vídeo vai com a capa no 1º segundo ("Cortes\Com capa\<corte>.mp4").
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MiguelAngelusCast.Motor
{
    public static class CapaCorte
    {
        const int W = 1080, H = 1920;
        static readonly CultureInfo pt = new CultureInfo("pt-BR");
        public static string Arquivo(string corte) => Path.Combine(Path.GetDirectoryName(corte), Path.GetFileNameWithoutExtension(corte) + ".capa.jpg");

        // ---------- o que vem preenchido ----------
        /// <summary>Nome da homilia ("Homilia Finados 02-11") a partir do nome do corte.</summary>
        public static string Base(string corte) => Regex.Replace(Path.GetFileNameWithoutExtension(corte), @"\s*-\s*corte\s+[\dmh]+-[\dmh]+$", "", RegexOptions.IgnoreCase).Trim();
        static (double ini, double fim)? Trecho(string corte)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(corte), @"corte\s+(?:(\d+)h)?(\d+)m(\d+)-(?:(\d+)h)?(\d+)m(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double S(int h, int mi, int s) => (m.Groups[h].Success ? int.Parse(m.Groups[h].Value) * 3600 : 0) + int.Parse(m.Groups[mi].Value) * 60 + int.Parse(m.Groups[s].Value);
            return (S(1, 2, 3), S(4, 5, 6));
        }
        /// <summary>Uma frase do trecho para a capa (a primeira completa de tamanho bom); senão o nome da homilia.</summary>
        public static string FrasePadrao(string corte, string pastaMomentos)
        {
            try
            {
                var tr = Trecho(corte); var emPe = Path.Combine(pastaMomentos, Base(corte) + Vertical.Sufixo + ".mp4");
                var t = tr != null ? Cortes.Ler(emPe) : null;
                if (t != null)
                {
                    var texto = string.Join(" ", t.Falas.Where(f => f.I >= tr.Value.ini - 0.6 && f.F <= tr.Value.fim + 0.6).Select(f => f.T.Trim()));
                    var frases = Regex.Split(texto, @"(?<=[.!?])\s+").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    var boa = frases.FirstOrDefault(x => x.Length >= 25 && x.Length <= 110 && !x.EndsWith("?")) ?? frases.FirstOrDefault(x => x.Length >= 25 && x.Length <= 110);
                    if (boa != null) return boa.TrimEnd('.');
                    // sem frase curta: as primeiras orações (até a vírgula) que dão ~50 a 90 letras
                    var partes = texto.Split(',');
                    var acc = "";
                    foreach (var p in partes) { var n = acc == "" ? p.Trim() : acc + "," + p; if (n.Length > 90 && acc.Length >= 25) break; acc = n; if (acc.Length >= 50) break; }
                    acc = acc.Trim().TrimEnd('.', ',');
                    if (acc.Length > 0 && acc.Length <= 110) return acc;
                    if (texto.Length > 0) return texto.Length > 90 ? texto[..texto.LastIndexOf(' ', 90)] + "…" : texto;
                }
            }
            catch { }
            return Base(corte);
        }
        /// <summary>Data da homilia: a do nome do arquivo gravado ("Homilia 2026-11-15 17h27", "… 02-11"); senão a do vídeo original.</summary>
        public static DateTime DataPadrao(string corte, string pastaMomentos)
        {
            var b = Base(corte);
            var m = Regex.Match(b, @"(\d{4})-(\d{2})-(\d{2})");
            if (m.Success && DateTime.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            var orig = Path.Combine(pastaMomentos, b + ".mp4");
            var quando = File.Exists(orig) ? File.GetLastWriteTime(orig) : File.GetLastWriteTime(corte);
            m = Regex.Match(b, @"(?<!\d)(\d{2})[-/](\d{2})(?!\d)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var dia) && int.TryParse(m.Groups[2].Value, out var mes) && mes is >= 1 and <= 12 && dia >= 1 && dia <= DateTime.DaysInMonth(quando.Year, mes))
            {
                var c = new DateTime(quando.Year, mes, dia);
                return c > quando.Date ? c.AddYears(-1) : c;
            }
            return quando.Date;
        }
        public static string DataTexto(DateTime d) => d.ToString("d 'de' MMMM 'de' yyyy", pt);

        // ---------- desenhar ----------
        public static string Gerar(string corte, string frase, string data, string encerramento)
        {
            var bg = Path.Combine(Path.GetTempPath(), "cast-capa-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            try
            {
                // fundo: 9:16 do centro, desfocado forte (o texto da vinheta vira luz) e escurecido
                string fonte = encerramento != null && File.Exists(encerramento) ? encerramento : corte;
                double t = encerramento != null && File.Exists(encerramento) ? 1.2 : 2;
                var a = $"-hide_banner -loglevel error -y -ss {t.ToString(CultureInfo.InvariantCulture)} -i \"{fonte}\" -frames:v 1 " +
                        $"-vf \"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},gblur=sigma=75,colorchannelmixer=rr=0.8:gg=0.8:bb=0.8\" \"{bg}\"";
                using (var p = Process.Start(new ProcessStartInfo(Transmissor.Ffmpeg, a) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
                { p.StandardError.ReadToEnd(); p.WaitForExit(20000); }

                using var img = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.CompositingQuality = CompositingQuality.HighQuality;
                    if (File.Exists(bg)) using (var f = Image.FromFile(bg)) g.DrawImage(f, 0, 0, W, H);
                    else using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(10, 40, 60), Color.FromArgb(5, 15, 30), 90f)) g.FillRectangle(b, 0, 0, W, H);
                    // véu escuro em cima e embaixo (o texto aparece em qualquer fundo)
                    using (var v = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), 90f)) g.FillRectangle(v, 0, 0, W, H);

                    // brasão em cima, logo da Pascom embaixo (sem as sobras transparentes do PNG)
                    var marca1 = Capa.Cfg.Marca1; var marca2 = Capa.Cfg.Marca2;
                    int y = 150;
                    using (var m = Recortado(marca1)) if (m != null) { var r = Caber(m, 360, 320); g.DrawImage(m, (W - r.Width) / 2, y, r.Width, r.Height); y += r.Height; } else y += 160;
                    int yLogo = H;
                    using (var m = Recortado(marca2))
                        if (m != null) { var r = Caber(m, 330, 200); yLogo = H - 110 - r.Height; g.DrawImage(m, (W - r.Width) / 2, yLogo, r.Width, r.Height); }
                    int yd = yLogo - 190;   // fio dourado; a data logo abaixo dele

                    // a frase, entre aspas, o maior tamanho que couber entre o brasão e a data
                    var txt = "“" + (frase ?? "").Trim().Trim('"', '“', '”') + "”";
                    var caixa = new RectangleF(90, y + 60, W - 180, yd - 60 - (y + 60));
                    // quebra própria, só nos espaços (o Windows quebraria "sexta-|feira" no hífen)
                    using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
                    var palavras = txt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Font fonteFrase = null; var linhas = new System.Collections.Generic.List<string>(); float alt = 0;
                    for (float tam = 104; tam >= 40; tam -= 4)
                    {
                        fonteFrase?.Dispose(); fonteFrase = new Font("Georgia", tam, FontStyle.Regular, GraphicsUnit.Pixel);
                        linhas.Clear(); var atual = ""; bool largo = false;
                        foreach (var p in palavras)
                        {
                            var tenta = atual == "" ? p : atual + " " + p;
                            if (g.MeasureString(tenta, fonteFrase, int.MaxValue, fmt).Width <= caixa.Width) { atual = tenta; continue; }
                            if (atual != "") linhas.Add(atual);
                            atual = p;
                            if (g.MeasureString(p, fonteFrase, int.MaxValue, fmt).Width > caixa.Width) largo = true;
                        }
                        if (atual != "") linhas.Add(atual);
                        alt = fonteFrase.GetHeight(g) * 1.12f;
                        if (!largo && linhas.Count * alt <= caixa.Height) break;
                    }
                    using (fonteFrase)
                    using (var sombra = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        float yl = caixa.Y + (caixa.Height - linhas.Count * alt) / 2;
                        foreach (var l in linhas)
                        {
                            for (int k = 1; k <= 4; k++) g.DrawString(l, fonteFrase, sombra, new RectangleF(caixa.X + k, yl + k, caixa.Width, alt), fmt);
                            g.DrawString(l, fonteFrase, Brushes.White, new RectangleF(caixa.X, yl, caixa.Width, alt), fmt);
                            yl += alt;
                        }
                    }

                    // fio dourado + data
                    using (var ouro = new Pen(Color.FromArgb(212, 169, 58), 4)) g.DrawLine(ouro, W / 2 - 90, yd, W / 2 + 90, yd);
                    using (var fd = new Font("Segoe UI Semibold", 46, GraphicsUnit.Pixel))
                    using (var cor = new SolidBrush(Color.FromArgb(240, 228, 196)))
                        g.DrawString("Homilia · " + (data ?? "").Trim(), fd, cor, new RectangleF(60, yd + 30, W - 120, 70), new StringFormat { Alignment = StringAlignment.Center });
                }
                var destino = Arquivo(corte);
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                using var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                img.Save(destino, enc, ps);
                return destino;
            }
            finally { try { File.Delete(bg); } catch { } }
        }
        // ---------- o corte com a capa no 1º segundo ----------
        // O feed de Shorts/Reels costuma mostrar o primeiro quadro: com a capa ali, ela aparece sempre.
        // Fica em "Cortes\Com capa\<corte>.mp4" (fora da lista e da nuvem); refaz só se a capa ou o corte mudaram.
        public static string ArquivoComCapa(string corte) => Path.Combine(Path.GetDirectoryName(corte), "Com capa", Path.GetFileName(corte));
        public static string ComCapa(string corte, string cod)
        {
            var capa = Arquivo(corte);
            if (!File.Exists(capa)) return corte;
            var destino = ArquivoComCapa(corte);
            if (File.Exists(destino) && File.GetLastWriteTime(destino) > File.GetLastWriteTime(capa) && File.GetLastWriteTime(destino) > File.GetLastWriteTime(corte)) return destino;
            Directory.CreateDirectory(Path.GetDirectoryName(destino));
            var parcial = destino + ".parcial.mp4";
            const string norm = "scale=1080:1920,fps=30,format=yuv420p,setsar=1";
            const string aud = "aformat=sample_rates=48000:channel_layouts=stereo";
            var a = $"-hide_banner -loglevel error -y -loop 1 -framerate 30 -t 1 -i \"{capa}\" -f lavfi -t 1 -i anullsrc=r=48000:cl=stereo -i \"{corte}\" " +
                    $"-filter_complex \"[0:v]{norm}[c];[1:a]{aud}[s];[2:v]{norm}[v2];[2:a]{aud}[a2];[c][s][v2][a2]concat=n=2:v=1:a=1[v][a]\" -map [v] -map [a] " +
                    $"{Transmissor.ArgsVideo(cod, 8000)} -c:a aac -b:a 160k -movflags +faststart \"{parcial}\"";
            try
            {
                using var p = new Process { StartInfo = new ProcessStartInfo(Transmissor.Ffmpeg, a) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
                p.Start(); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(); } catch { } throw new Exception("o FFmpeg demorou demais"); }
                if (p.ExitCode != 0 || !File.Exists(parcial)) { Registro.Info("ffmpeg (corte com capa): " + err.Result); throw new Exception("o FFmpeg não conseguiu juntar a capa (detalhes no registro)"); }
                File.Move(parcial, destino, true);
            }
            finally { try { File.Delete(parcial); } catch { } }
            return destino;
        }

        // a imagem sem as bordas transparentes (null se não existe)
        static Bitmap Recortado(string arq)
        {
            if (string.IsNullOrEmpty(arq) || !File.Exists(arq)) return null;
            using var b = new Bitmap(arq);
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[d.Stride * d.Height]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                for (int yy = 0; yy < d.Height; yy++)
                    for (int xx = 0; xx < d.Width; xx++)
                        if (px[yy * d.Stride + xx * 4 + 3] > 16) { if (xx < x0) x0 = xx; if (xx > x1) x1 = xx; if (yy < y0) y0 = yy; if (yy > y1) y1 = yy; }
            }
            finally { b.UnlockBits(d); }
            if (x1 < 0) return null;
            return b.Clone(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1), PixelFormat.Format32bppArgb);
        }
        static Size Caber(Image m, int maxL, int maxA)
        {
            var f = Math.Min(maxL / (double)m.Width, maxA / (double)m.Height);
            return new Size((int)(m.Width * f), (int)(m.Height * f));
        }
    }
}
