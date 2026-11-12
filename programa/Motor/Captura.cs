// (0.1.8) Tela ou janela do computador como fonte (backlog 1.1): o monitor inteiro ou uma janela (PowerPoint, navegador…).
// Spec:  "tela:<n>"                       monitor n (1 = o principal, na ordem do Windows)
//        "janela:<processo>|<título>"     a janela daquele programa cujo título contém o texto (acha de novo se fechar e abrir)
// Captura do próprio Windows (BitBlt / PrintWindow), sem programa extra, numa linha à parte a 15 q/s; só trabalha
// enquanto a cena está sendo mostrada (Preview, Programa ou camada no ar), para não pesar à toa.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MiguelAngelusCast.Motor
{
    public sealed class FonteCaptura : Fonte
    {
        readonly string alvo; readonly bool janela;
        readonly object trava = new object();
        Imagem pronta = new Imagem(16, 9), livre = new Imagem(16, 9);
        readonly Imagem saida = new Imagem(16, 9);
        long seqPronta, seqEntregue; volatile bool parar; long ultimoPedido;
        readonly Thread linha;
        IntPtr hwnd; DateTime proxBusca;
        readonly Func<IntPtr> fixa;      // (0.1.9) janela já conhecida (o navegador escondido do próprio Cast)

        internal FonteCaptura(string spec, Func<IntPtr> fixa) : this(spec) { this.fixa = fixa; }
        public FonteCaptura(string spec)
        {
            Spec = spec; janela = !spec.StartsWith("tela:");
            alvo = spec.Substring(spec.IndexOf(':') + 1);
            Estado = "abrindo";
            linha = new Thread(Laco) { IsBackground = true, Name = "captura " + alvo, Priority = ThreadPriority.BelowNormal };
            linha.Start();
        }

        public override Imagem Atual()
        {
            Interlocked.Exchange(ref ultimoPedido, Environment.TickCount64);
            lock (trava)
            {
                if (seqPronta != seqEntregue)
                {
                    saida.Garantir(pronta.L, pronta.A);
                    Buffer.BlockCopy(pronta.Px, 0, saida.Px, 0, pronta.Px.Length);
                    seqEntregue = seqPronta; Seq++;
                }
            }
            return saida;
        }

        void Laco()
        {
            var rel = Stopwatch.StartNew(); long prox = 0;
            while (!parar)
            {
                // ninguém pediu a imagem há mais de 1,5 s: a cena não está à vista, descansa
                if (Environment.TickCount64 - Interlocked.Read(ref ultimoPedido) > 1500) { Thread.Sleep(200); continue; }
                long agora = rel.ElapsedMilliseconds;
                if (agora < prox) { Thread.Sleep((int)Math.Min(prox - agora, 50)); continue; }
                prox = agora + 66;     // 15 q/s
                try
                {
                    bool ok = janela ? CapturarJanela(livre) : CapturarTela(livre);
                    if (ok) lock (trava) { (pronta, livre) = (livre, pronta); seqPronta++; }
                }
                catch (Exception e) { Estado = "erro na captura"; Registro.Erro("captura " + Spec, e); Thread.Sleep(1000); }
            }
        }

        // ---------- monitor ----------
        bool CapturarTela(Imagem img)
        {
            var telas = Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
            int n = int.TryParse(alvo, out var k) ? k : 1;
            if (n < 1 || n > telas.Length) { Estado = "monitor " + n + " não encontrado"; Thread.Sleep(500); return false; }
            var b = telas[n - 1].Bounds;
            var tela = GetDC(IntPtr.Zero);
            try { Copiar(tela, b.X, b.Y, b.Width, b.Height, img, null); }
            finally { ReleaseDC(IntPtr.Zero, tela); }
            Estado = "ok";
            return true;
        }

        // ---------- janela ----------
        bool CapturarJanela(Imagem img)
        {
            if (fixa != null)
            {
                hwnd = fixa();
                if (hwnd == IntPtr.Zero) { Estado = "abrindo a página"; return false; }
            }
            else if (hwnd != IntPtr.Zero && !IsWindow(hwnd)) hwnd = IntPtr.Zero;
            // procura de novo a cada 3 s (ou na hora, se fechou): ex. o PowerPoint abre a apresentação numa janela nova
            if (fixa == null && (hwnd == IntPtr.Zero || DateTime.Now >= proxBusca))
            {
                if (hwnd == IntPtr.Zero && DateTime.Now < proxBusca) return false;
                proxBusca = DateTime.Now.AddSeconds(hwnd == IntPtr.Zero ? 1 : 3);
                var p = alvo.Split('|'); string proc = p[0], tit = p.Length > 1 ? p[1] : "";
                var achada = Janelas().Where(j => string.Equals(j.processo, proc, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(j => tit != "" && j.titulo.Contains(tit, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(j => j.area).Select(j => j.hwnd).FirstOrDefault();
                if (achada != hwnd && achada != IntPtr.Zero) Registro.Info("captura: janela de " + proc);
                if (achada != IntPtr.Zero) hwnd = achada;
                if (hwnd == IntPtr.Zero) { Estado = "janela fechada (abra o " + proc + ")"; return false; }
            }
            if (IsIconic(hwnd)) { Estado = "a janela está minimizada"; return false; }
            GetClientRect(hwnd, out var r);
            int l = r.Right - r.Left, a = r.Bottom - r.Top;
            if (l < 8 || a < 8) { Estado = "janela pequena demais"; return false; }
            var dc = GetDC(hwnd);
            try { Copiar(dc, 0, 0, l, a, img, hwnd); }
            finally { ReleaseDC(hwnd, dc); }
            Estado = "ok";
            return true;
        }

        // desenha num bitmap do Windows (DIB) e copia para a Imagem; PrintWindow pega a janela mesmo coberta por outras
        static void Copiar(IntPtr origem, int x, int y, int l, int a, Imagem img, IntPtr? janela)
        {
            var mem = CreateCompatibleDC(origem);
            var bi = new BITMAPINFO { biSize = 40, biWidth = l, biHeight = -a, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(origem, ref bi, 0, out var bits, IntPtr.Zero, 0);
            var velho = SelectObject(mem, dib);
            try
            {
                bool ok = janela.HasValue ? PrintWindow(janela.Value, mem, 3 /* só a área de dentro + conteúdo acelerado */) : false;
                if (!ok) BitBlt(mem, 0, 0, l, a, origem, x, y, 0x00CC0020 /* SRCCOPY */);
                GdiFlush();
                img.Garantir(l, a);
                Marshal.Copy(bits, img.Px, 0, l * a * 4);
                for (int i = 3; i < img.Px.Length; i += 4) img.Px[i] = 255;     // o Windows deixa o alfa zerado
                img.TemAlfa = false;
            }
            finally { SelectObject(mem, velho); DeleteObject(dib); DeleteDC(mem); }
        }

        public override void Dispose() { parar = true; }

        // ---------- listas para a tela "+ Cena" ----------
        public static List<object> Monitores() => Screen.AllScreens.OrderByDescending(s => s.Primary).Select((s, i) =>
            (object)new { spec = "tela:" + (i + 1), nome = $"Monitor {i + 1}{(s.Primary ? " (principal)" : "")} · {s.Bounds.Width}×{s.Bounds.Height}" }).ToList();

        public static List<object> ListaJanelas() => Janelas()
            .Where(j => !string.Equals(j.processo, "MiguelAngelusCast", StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.processo).Select(j => (object)new { spec = "janela:" + j.processo + "|" + j.titulo, nome = $"{j.titulo}  ({j.processo})" }).ToList();

        static List<(IntPtr hwnd, string titulo, string processo, long area)> Janelas()
        {
            var l = new List<(IntPtr, string, string, long)>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                        // WS_EX_TOOLWINDOW
                if (DwmGetWindowAttribute(h, 14, out int oculta, 4) == 0 && oculta != 0) return true;   // DWMWA_CLOAKED (apps escondidos)
                int n = GetWindowTextLength(h); if (n == 0) return true;
                var sb = new StringBuilder(n + 1); GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                string proc; try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
                GetClientRect(h, out var r);
                l.Add((h, sb.ToString(), proc, (long)(r.Right - r.Left) * (r.Bottom - r.Top)));
                return true;
            }, IntPtr.Zero);
            return l;
        }

        // ---------- Windows ----------
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; public int cores; }
        delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int tam);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint uso, out IntPtr bits, IntPtr sec, uint off);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int l, int a, IntPtr o, int ox, int oy, uint rop);
    }
}
