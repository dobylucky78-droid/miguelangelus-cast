// (0.1.9) Página da internet como fonte (backlog 1.3): placar, letreiro, página da paróquia…
// Spec:  "web:<largura>x<altura>@<zoom>|<endereço>"   ex.: "web:1920x1080@100|https://exemplo.org/placar"
// Um navegador (WebView2, o mesmo motor da tela do Cast) numa janela escondida fora da tela, sem barras,
// sem som e sem barras de rolagem; a imagem é tirada como a de uma janela (FonteCaptura), a 15 q/s.
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MiguelAngelusCast.Motor
{
    public sealed class FonteNavegador : Fonte
    {
        public readonly int L = 1920, A = 1080, Zoom = 100; public readonly string Url = "";
        readonly FonteCaptura cap;
        Form janela; volatile IntPtr hwnd; volatile string estado = "abrindo o navegador";
        readonly Thread linha;

        public FonteNavegador(string spec)
        {
            Spec = spec;
            var resto = spec.Substring(4);
            int barra = resto.IndexOf('|');
            if (barra >= 0)
            {
                var t = resto.Substring(0, barra); Url = resto.Substring(barra + 1);
                var z = t.Split('@'); var d = z[0].Split('x');
                if (d.Length == 2 && int.TryParse(d[0], out var l) && int.TryParse(d[1], out var a)) { L = Math.Clamp(l, 64, 3840); A = Math.Clamp(a, 64, 2160); }
                if (z.Length > 1 && int.TryParse(z[1], out var zz)) Zoom = Math.Clamp(zz, 25, 500);
            }
            else Url = resto;
            if (!Url.Contains("://") && !Url.StartsWith("about:")) Url = "https://" + Url;
            cap = new FonteCaptura("web:" + Url, () => hwnd);
            linha = new Thread(Rodar) { IsBackground = true, Name = "navegador" };
            linha.SetApartmentState(ApartmentState.STA);
            linha.Start();
        }

        void Rodar()
        {
            try
            {
                janela = new JanelaEscondida
                {
                    FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000 + 100, -32000 + 100), AutoScaleMode = AutoScaleMode.None, Text = "MiguelAngelus Cast - página",
                };
                janela.ClientSize = new Size(L, A);
                var wv = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
                janela.Controls.Add(wv);
                janela.Load += async (_, __) =>
                {
                    try
                    {
                        // pasta própria: não mistura com a tela do Cast; sem "pausar janela escondida" (senão a página para de desenhar)
                        var op = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required");
                        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.PastaDados, "Navegador"), op);
                        await wv.EnsureCoreWebView2Async(env);
                        var c = wv.CoreWebView2;
                        c.IsMuted = true;
                        c.Settings.AreDefaultContextMenusEnabled = false; c.Settings.AreDevToolsEnabled = false;
                        c.Settings.IsStatusBarEnabled = false; c.Settings.IsZoomControlEnabled = false;
                        c.NewWindowRequested += (s, e) => e.Handled = true;          // nada de janelas novas
                        await c.AddScriptToExecuteOnDocumentCreatedAsync(
                            "addEventListener('DOMContentLoaded',()=>{const s=document.createElement('style');s.textContent='::-webkit-scrollbar{display:none!important}html,body{scrollbar-width:none!important}';document.documentElement.appendChild(s)});");
                        try { wv.ZoomFactor = Zoom / 100.0; } catch { }
                        c.NavigationCompleted += (s, e) =>
                        {
                            estado = e.IsSuccess ? null : "a página não abriu (" + e.WebErrorStatus + ")";
                            if (e.IsSuccess) Registro.Info("navegador: abriu " + Url); else Registro.Info("navegador: não abriu " + Url + " — " + e.WebErrorStatus);
                        };
                        estado = "abrindo a página";
                        c.Navigate(Url);
                        hwnd = janela.Handle;
                    }
                    catch (Exception e) { estado = "navegador não abriu"; Registro.Erro("navegador " + Url, e); }
                };
                Application.Run(janela);
            }
            catch (Exception e) { estado = "navegador não abriu"; Registro.Erro("navegador " + Url, e); }
        }

        public override Imagem Atual()
        {
            var img = cap.Atual();
            Seq = cap.Seq;
            Estado = estado ?? cap.Estado;
            return img;
        }

        public override void Dispose()
        {
            cap.Dispose(); hwnd = IntPtr.Zero;
            try { var j = janela; if (j != null && j.IsHandleCreated) j.BeginInvoke(new Action(() => j.Close())); } catch { }
        }

        // não aparece no Alt+Tab nem na barra de tarefas e nunca rouba o foco da tela do Cast
        sealed class JanelaEscondida : Form
        {
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var p = base.CreateParams; p.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */; return p; }
            }
        }
    }
}
