// Janela principal: a tela é HTML (pasta web\) num WebView2, como no MiguelAngelus.
// A tela manda comandos em JSON; o programa responde com o estado. Os monitores (preview/programa)
// vão por um buffer compartilhado, sem copiar imagem pela mensagem.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MiguelAngelusCast.Motor;
using MiguelAngelusCast.Rastreio;

namespace MiguelAngelusCast
{
    sealed class Principal : Form
    {
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 20, 32) };
        readonly Mesa mesa = new Mesa();
        readonly Transmissor saida = new Transmissor();
        readonly Som som = new Som();
        readonly MesaUi mesaSom = new MesaUi();
        volatile bool somCfgPendente;
        readonly Timer relogioSom = new Timer { Interval = 100 };
        CoreWebView2SharedBuffer monitores;
        readonly Timer relogio = new Timer { Interval = 1000 };
        volatile bool quadroPendente;
        Rastreador rastreio;                                   // criado só quando a tela do rastreio é aberta
        readonly Timer relogioRastreio = new Timer { Interval = 200 };
        long rtImagemSeq; bool rtVisivel;
        Roteiro roteiro; volatile bool rotPendente;

        public Principal()
        {
            Text = "MiguelAngelus Cast " + Program.Versao;
            BackColor = Color.FromArgb(15, 20, 32);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(1440, 900);
            MinimumSize = new Size(1024, 700);
            WindowState = FormWindowState.Maximized;
            Controls.Add(web);
            Load += async (s, e) => await Iniciar();
            FormClosing += (s, e) =>
            {
                if ((saida.AoVivo || saida.Gravando) && MessageBox.Show(this, (saida.AoVivo ? "A transmissão está no ar" : "A gravação está ligada") + ".\n\nEncerrar e fechar o Cast?", "MiguelAngelus Cast", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                { e.Cancel = true; return; }
                Controle.Parar(); relogio.Stop(); relogioSom.Stop(); relogioRastreio.Stop(); joystick?.Dispose(); try { rastreio?.Dispose(); } catch { } saida.Dispose(); som.Dispose(); mesaSom.Dispose(); mesa.Dispose(); Registro.Info("fechou");
            };
        }

        async System.Threading.Tasks.Task Iniciar()
        {
            // (0.1.1) o NDI começa já, à parte (leva vários segundos); quando fica pronto, procura o MiguelAngelus de novo pela rede
            Ndi.IniciarAoFundo().ContinueWith(t => { if (t.Result && Miguel.Erro != "") _ = Miguel.Agora(); });
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.PastaDados, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
                var cw = web.CoreWebView2;
                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.IsZoomControlEnabled = false;
                cw.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("CAST_DEVTOOLS") == "1";
                cw.SetVirtualHostNameToFolderMapping("cast.local", Path.Combine(Program.Pasta, "web"), CoreWebView2HostResourceAccessKind.Allow);
                cw.WebMessageReceived += (s, e) => { try { Mensagem(e.WebMessageAsJson); } catch (Exception ex) { Registro.Erro("mensagem da tela", ex); } };

                monitores = env.CreateSharedBuffer((ulong)mesa.MonitoresTamanho);
                mesa.EstadoMudou += json => Na(() => cw.PostWebMessageAsJson(json));
                mesa.MonitoresProntos += n =>
                {
                    if (quadroPendente) return;               // a tela ainda não desenhou o anterior: pula
                    quadroPendente = true;
                    Na(() => { quadroPendente = false; cw.PostWebMessageAsJson("{\"tipo\":\"quadro\",\"n\":" + n + "}"); });
                };
                saida.Configurar(mesa.Cfg.Largura, mesa.Cfg.Altura, mesa.Cfg.Qps);
                mesa.QuadroDeSaida += saida.Receber;
                saida.Som = som; som.Aplicar();
                mesaSom.Mudou += () => { if (somCfgPendente) return; somCfgPendente = true; Na(() => { somCfgPendente = false; EnviarSomCfg(false); }); };
                mesaSom.Conectar(som.Cfg.MesaIp);
                relogioSom.Tick += (s, e) =>
                {
                    var (pe, pd, re, rd) = som.Nivel();
                    Enviar(new { tipo = "nivel", pe = Math.Round(pe, 1), pd = Math.Round(pd, 1), re = Math.Round(re, 1), rd = Math.Round(rd, 1), mudo = som.Mudo, estado = som.Estado, nome = NomeSom(), avisoMesa = AvisoMesa(),
                        niv = som.Cfg.Nivelar, nivGanho = Math.Round(som.Nivel2.GanhoDb, 1), lufs = Math.Round(som.Nivel2.LufsSaida, 1), lim = Math.Round(som.Nivel2.ReducaoLimitadorDb, 1), atraso = som.Cfg.AtrasoMs, abaixado = som.Abaixado, cenaSom = som.SomDaCena, cenaMuda = som.CenaMuda });
                };
                relogioSom.Start();
                saida.Mudou += () => Na(() => Enviar(saida.Estado()));
                saida.Mudou += () => Na(VerYtSaida);   // (0.1.0) sinal em teste no YouTube / encerrar a live
                // (0.1.7) som de cada cena: o volume de cada fonte acompanha a cena do ar; entradas extras abertas ao fundo
                mesa.SomMudou += (f, ms) => som.DefinirCena(f, ms);
                mesa.EntradasDeSomMudaram += () => System.Threading.Tasks.Task.Run(() => { try { som.AtualizarExtras(mesa.EntradasDeSom()); } catch (Exception ex) { Registro.Erro("entradas extras", ex); } });
                mesa.Iniciar(monitores.Buffer);
                mesa.AvisarSom();
                _ = System.Threading.Tasks.Task.Run(() => { try { som.AtualizarExtras(mesa.EntradasDeSom()); } catch (Exception ex) { Registro.Erro("entradas extras", ex); } });

                relogio.Tick += (s, e) => { Enviar(new { tipo = "numeros", qps = Math.Round(mesa.QpsReal, 1), ms = Math.Round(mesa.MsQuadro, 1), atrasos = mesa.Atrasos }); Enviar(saida.Estado()); };
                relogio.Start();
                roteiro = new Roteiro(mesa, PrepararRastreio, () => rastreio);
                roteiro.Mudou += () => { if (rotPendente) return; rotPendente = true; Na(() => { rotPendente = false; EnviarRoteiro(null); }); };
                relogioRastreio.Tick += (s, e) => { EnviarRastreio(false); try { roteiro.Tick(); EsperaAutomatica(); } catch (Exception ex) { Registro.Erro("roteiro", ex); } };
                relogioRastreio.Start();
                // (0.1.5) botões (Stream Deck / Stream Dock): porta só deste computador
                Controle.NaTelaFn = f => { var tcs = new System.Threading.Tasks.TaskCompletionSource<object>(); Na(() => { try { tcs.TrySetResult(f()); } catch (Exception ex) { Registro.Erro("botão", ex); tcs.TrySetResult("!erro"); } }); return tcs.Task; };
                Controle.Executar = Botao;
                Controle.Estado = EstadoBotoes;
                Controle.Iniciar();
                // (0.0.5) agenda do MiguelAngelus: a capa e o Início acompanham as próximas celebrações
                Miguel.Mudou += () => Na(() => { AplicarAgenda(); EnviarMiguel(); if (!ytOcupado) EnviarYt(); });
                Miguel.Iniciar();
                // (0.0.8) lives do canal: relê a cada 5 min (custa 2 de 10.000 unidades da cota diária)
                ytRelogio = new System.Threading.Timer(_ => Na(() => { if (YouTube.Conectado && !ytOcupado) YtTrabalho(null, async () => { await YouTube.Atualizar(); return null; }); }), null, 4000, 300000);
                cw.Navigate("https://cast.local/index.html");
            }
            catch (Exception e)
            {
                Registro.Erro("iniciar a tela", e);
                MessageBox.Show(this, "Não foi possível abrir a tela do Cast.\n\nVerifique se o \"Microsoft Edge WebView2\" está instalado.\n\n" + e.Message, "MiguelAngelus Cast", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void Na(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        void Enviar(object o) { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o)); }

        // ---------- YouTube (0.0.8) ----------
        static string TituloLive(CapaCfg c) => $"{c.Titulo} · {Capa.CelebracaoUsada(c)} · {c.Quando:dd/MM} {Capa.Hora(c.Quando)}";
        static string DescricaoLive(CapaCfg c)
        {
            var l = new System.Collections.Generic.List<string> { Capa.CelebracaoUsada(c), Capa.DataLonga(c.Quando) + ", " + Capa.Hora(c.Quando) };
            if (!string.IsNullOrWhiteSpace(c.Celebrante)) l.Add("Celebrante: " + c.Celebrante.Trim());
            if (!string.IsNullOrWhiteSpace(Miguel.Paroquia)) l.Add(Miguel.Paroquia);
            return string.Join("\n", l);
        }
        bool ytOcupado; System.Threading.Timer ytRelogio;
        // (0.1.0) Iniciar transmissão → o sinal chega ao YouTube em TESTE; "Entrar ao vivo" põe no ar; Encerrar termina a live
        bool ytArAntes; string ytFase = "", ytFaseId; volatile bool ytParar;
        void YtFase(string f) => Na(() => { ytFase = f; EnviarYt(); });
        void YtFundo(Func<System.Threading.Tasks.Task<string>> f)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                string msg;
                try { msg = await f(); }
                catch (Exception e) { msg = "YouTube: " + e.Message; Registro.Info("YouTube: " + e.Message); }
                try { await YouTube.Atualizar(); } catch { }
                Na(() => EnviarYt(msg));
            });
        }
        void VerYtSaida()
        {
            var ar = saida.AoVivo; if (ar == ytArAntes) return; ytArAntes = ar;
            var dd = saida.Cfg.Destinos.FirstOrDefault(x => x.Tipo == "youtube");
            if (!YouTube.Conectado || dd == null || !dd.Ligado) return;
            if (ar)
            {
                var l = YouTube.DaHora(Capa.Cfg.Quando);
                if (l == null || !l.DoCast) { ytFase = ""; EnviarYt(); return; }
                ytParar = false; ytFaseId = l.Id; ytFase = "esperando o sinal…"; EnviarYt();
                Registro.Info("YouTube: sinal para a live " + l.Id);
                YtFundo(() => YouTube.PorEmTeste(l.Id, YtFase, () => ytParar));
            }
            else
            {
                ytParar = true; var id = ytFaseId; ytFaseId = null; ytFase = ""; EnviarYt();
                if (id != null) YtFundo(async () => await YouTube.Encerrar(id) ? "Live encerrada no YouTube." : null);
            }
        }
        void YtTrabalho(string inicio, Func<System.Threading.Tasks.Task<string>> f)
        {
            if (ytOcupado) return;
            ytOcupado = true; EnviarYt(inicio);
            System.Threading.Tasks.Task.Run(async () =>
            {
                string msg;
                try { msg = await f(); }
                catch (Exception e) { msg = "YouTube: " + e.Message; YouTube.Erro = e.Message; Registro.Info("YouTube: " + e.Message); }
                Na(() => { ytOcupado = false; EnviarYt(msg); });
            });
        }
        void EnviarYt(string msg = null)
        {
            var c = Capa.Cfg; var daHora = YouTube.DaHora(c.Quando);
            object L(LiveYt x) => x == null ? null : new { id = x.Id, titulo = x.Titulo, inicio = x.Inicio, doCast = x.DoCast, autoInicio = x.AutoInicio, quandoTxt = x.Inicio == default ? "" : Capa.DataLonga(x.Inicio) + ", " + Capa.Hora(x.Inicio), privacidade = x.Privacidade, estado = x.Estado, link = x.Link, studio = x.Studio, miniatura = x.Miniatura };
            Enviar(new
            {
                tipo = "yt", msg, ocupado = ytOcupado, fase = ytFase, aoVivoCast = saida.AoVivo, temCliente = YouTube.TemCliente, conectado = YouTube.Conectado, canal = YouTube.Canal, erro = YouTube.Erro,
                lives = YouTube.Lives.Select(L).ToList(), daHora = L(daHora), tituloPadrao = TituloLive(c), descricaoPadrao = DescricaoLive(c),
                privacidade = c.YtPrivacidade, descricao = c.YtDescricao, quandoTxt = Capa.DataLonga(c.Quando) + ", " + Capa.Hora(c.Quando)
            });
        }

        // (0.0.5) agenda do MiguelAngelus → capa (data, hora, "Próxima Missa"); ao mudar, o Início e a capa se refazem
        void AplicarAgenda()
        {
            var n = Miguel.Aplicar(Capa.Cfg);
            if (n == null) return;
            Capa.Trocar(n, true);
            Registro.Info("capa pela agenda do MiguelAngelus: " + n.Quando.ToString("dd/MM HH:mm") + (n.Proxima != "" ? " · próxima " + n.Proxima : ""));
            EnviarInicio();
        }
        void EnviarMiguel()
        {
            var c = Capa.Cfg;
            var t = Miguel.Transmitidas(c, DateTime.Now).Take(8).Select(x => new
            {
                quando = x.Quando.ToString("yyyy-MM-ddTHH:mm"), quandoTxt = Miguel.TextoProxima(x, true), nome = x.Texto, roteiro = x.TemRoteiro
            }).ToList();
            Enviar(new
            {
                tipo = "miguel", erro = Miguel.Erro, origem = Miguel.Origem, paroquia = Miguel.Paroquia, total = Miguel.Lista.Count,
                lido = Miguel.Lido == default ? "" : Miguel.Lido.ToString("HH:mm"), locais = Miguel.Locais.Select(l => new { l.nome, l.sigla }).ToList(),
                seguir = c.SeguirMiguel, local = c.MiguelLocal, soMissas = c.MiguelSoMissas, folheto = c.MiguelFolheto, marcas = c.MiguelMarcas, proximas = t
            });
        }

        void Aviso(string erro) { if (erro != null) Enviar(new { tipo = "aviso", texto = erro }); Enviar(saida.Estado()); }

        // ---------- (0.1.5) botões: Stream Deck, Stream Dock, atalhos ----------
        // Resposta começando com "!" = não fez. Parar gravação e iniciar/encerrar transmissão pedem o botão DUAS vezes (3 s).
        string btArmado; DateTime btArmadoAte;
        bool Confirmou(string cmd)
        {
            if (btArmado == cmd && DateTime.Now < btArmadoAte) { btArmado = null; return true; }
            btArmado = cmd; btArmadoAte = DateTime.Now.AddSeconds(3); return false;
        }
        void Botao(string cmd, string texto) => Enviar(new { tipo = "botao", cmd, texto });

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        // Tecla "Abrir programa" do Stream Dock: traz o Cast ou o MiguelAngelus para a frente (abre o MiguelAngelus se estiver fechado)
        string Abrir(string programa)
        {
            if (programa == "miguel" || programa == "miguelangelus")
            {
                var p = System.Diagnostics.Process.GetProcessesByName("MiguelAngelus").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
                if (p != null)
                {
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    return "MiguelAngelus na frente";
                }
                var exe = new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MiguelAngelus", "MiguelAngelus.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MiguelAngelus", "MiguelAngelus.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MiguelAngelus", "MiguelAngelus.exe")
                }.FirstOrDefault(File.Exists);
                if (exe == null) return "!MiguelAngelus não instalado neste computador";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); return "Abrindo o MiguelAngelus"; }
                catch (Exception e) { Registro.Erro("abrir MiguelAngelus", e); return "!não consegui abrir o MiguelAngelus"; }
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); TopMost = true; TopMost = false; Activate(); SetForegroundWindow(Handle);
            return "Cast na frente";
        }

        string Botao(string cmd, System.Collections.Generic.Dictionary<string, string> q)
        {
            string P(string k) => q.TryGetValue(k, out var v) ? v : null;
            bool Liga(string k) => P(k) is string v && (v == "1" || v == "sim" || v == "true" || v == "ligar");
            string r;
            switch (cmd)
            {
                case "transicao":
                case "corte":
                    {
                        var pv = mesa.Cfg.Preview;
                        if (pv == null || pv == mesa.Cfg.Programa) { r = "!o Preview já está no ar"; break; }
                        if (cmd == "corte") mesa.Corte(); else mesa.Transicao();
                        r = (cmd == "corte" ? "Corte: " : "Transição: ") + (mesa.AcharCena(pv)?.Nome ?? pv); break;
                    }
                case "preview":
                case "programa":
                case "direto":
                    {
                        var c = mesa.AcharCena(P("cena") ?? P("n") ?? P("nome"));
                        if (c == null) { r = "!cena não encontrada (use cena=1, 2… ou o nome)"; break; }
                        if (cmd == "preview") { mesa.PorNoPreview(c.Id); r = "Preview: " + c.Nome; }
                        else { mesa.Direto(c.Id); r = "No ar: " + c.Nome; }
                        break;
                    }
                case "capa":
                    {
                        var modo = (P("modo") ?? "espera").ToLowerInvariant();
                        modo = modo == "abertura" ? "capa" : modo == "encerramento" ? "fim" : modo;
                        if (modo != "espera" && modo != "capa" && modo != "fim") { r = "!modo: espera, abertura ou encerramento"; break; }
                        var id = "capa-" + modo;
                        var nome = modo == "espera" ? "Tela de espera" : modo == "capa" ? "Abertura" : "Encerramento";
                        var achada = mesa.Cfg.Cenas.ToList().FirstOrDefault(c => c.Fonte == "capa:" + modo);
                        if (achada != null) id = achada.Id; else mesa.NovaCena(nome, "capa:" + modo, false, id);
                        if (Liga("direto")) { mesa.Direto(id); r = "No ar: " + nome; } else { mesa.PorNoPreview(id); r = "Preview: " + nome; }
                        break;
                    }
                case "proximo": roteiro.Proximo(); r = "Roteiro: próximo momento"; break;
                case "anterior": roteiro.Anterior(); r = "Roteiro: momento anterior"; break;
                case "mudo":
                    som.Mudo = P("ligado") != null ? Liga("ligado") : !som.Mudo;
                    Registro.Info(som.Mudo ? "som mudo (botão)" : "som de volta (botão)");
                    r = som.Mudo ? "Som MUDO" : "Som de volta"; break;
                case "gravar":
                    if (!saida.Gravando) { var e = saida.Gravar(true); Aviso(e); r = e == null ? "Gravando" : "!" + e; }
                    else if (Confirmou("gravar")) { Aviso(saida.Gravar(false)); r = "Gravação parada"; }
                    else r = "!Aperte de novo para PARAR a gravação";
                    break;
                case "transmitir":
                    {
                        bool ligar = !saida.AoVivo;
                        if (!Confirmou("transmitir")) { r = "!Aperte de novo para " + (ligar ? "INICIAR" : "ENCERRAR") + " a transmissão"; break; }
                        var e = saida.Transmitir(ligar); Aviso(e);
                        r = e != null ? "!" + e : ligar ? "Transmissão iniciada" : "Transmissão encerrada"; break;
                    }
                case "parar":
                    if (rastreio == null) { r = "!a câmera que segue não está aberta"; break; }
                    rastreio.PararCamera(); EnviarRastreio(false); r = "Câmera que segue: PARADA"; break;
                case "ptz" when P("cena") != null:   // (0.1.10) a câmera vai para a posição salva na cena (a cena não muda)
                    {
                        var c = mesa.AcharCena(P("cena"));
                        if (c == null) { r = "!cena não encontrada (use cena=1, 2… ou o nome)"; break; }
                        var e = roteiro.IrPosicaoDaCena(c.Id);
                        r = e == null ? "Câmera → posição da cena " + c.Nome : "!" + e; break;
                    }
                case "ptz":
                    {
                        var lista = Cameras.Lista.Where(c => !string.IsNullOrWhiteSpace(c.Ip)).ToList();
                        var qc = P("camera") ?? "1";
                        var cam = int.TryParse(qc, out var ci) ? (ci >= 1 && ci <= lista.Count ? lista[ci - 1] : null)
                            : lista.FirstOrDefault(c => c.Id == qc) ?? lista.FirstOrDefault(c => c.Nome.Contains(qc, StringComparison.OrdinalIgnoreCase));
                        if (cam == null) { r = "!câmera não encontrada (Ajustes › Câmeras)"; break; }
                        var qp = P("posicao") ?? P("pos") ?? "";
                        var pos = int.TryParse(qp, out var pn) ? cam.Posicoes.FirstOrDefault(p => p.N == pn) ?? new Posicao { N = pn, Nome = "Posição " + pn }
                            : cam.Posicoes.FirstOrDefault(p => string.Equals(p.Nome?.Trim(), qp.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (pos == null || pos.N < 0 || pos.N > 127) { r = "!posição não encontrada"; break; }
                        ViscaRede.Enviar(cam, new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x02, (byte)pos.N, 0xFF })
                            .ContinueWith(t => { if (t.Result != null) Na(() => Enviar(new { tipo = "camMsg", texto = cam.Nome + ": " + t.Result })); });
                        r = cam.Nome + " → " + pos.Nome; break;
                    }
                case "teste": r = "Botão funcionando ✓"; break;
                case "abrir": r = Abrir((P("programa") ?? "cast").ToLowerInvariant()); break;   // tecla com o ícone do programa
                default: r = "!comando desconhecido: " + cmd; break;
            }
            Botao(cmd, r);
            return r;
        }

        static string PastaBotoes => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MiguelAngelus Cast - botões");

        void EnviarBotoes(string msg) => Enviar(new
        {
            tipo = "botoes", porta = Controle.Porta, chave = Controle.Chave, erro = Controle.Erro, msg, pasta = PastaBotoes,
            exe = Environment.ProcessPath, plugin = Controle.PluginSituacao(),
            exemplo = Controle.Porta > 0 ? Controle.Url("transicao") : ""
        });

        // as 15 teclas sugeridas (3 fileiras de 5), na ordem do aparelho; o resto vai para a subpasta "Mais"
        System.Collections.Generic.List<Controle.Tecla> TeclasPadrao()
        {
            Color Az = Color.FromArgb(0x2e, 0x6f, 0xd8), Vd = Color.FromArgb(0x1f, 0x9d, 0x55), Vm = Color.FromArgb(0xd0, 0x2b, 0x2b),
                  Dr = Color.FromArgb(0xc9, 0x9a, 0x2e), Rx = Color.FromArgb(0x7a, 0x4c, 0xc2), Cz = Color.FromArgb(0x5a, 0x66, 0x78);
            var l = new System.Collections.Generic.List<Controle.Tecla>();
            void T(string arq, string rot, string cmd, string extra, Color cor) => l.Add(new Controle.Tecla { Arquivo = arq, Rotulo = rot, Cmd = cmd, Extra = extra, Cor = cor });
            var cs = mesa.Cfg.Cenas.ToList().Where(c => !(c.Fonte ?? "").StartsWith("capa:")).ToList();
            string NomeCena(int i) => i < cs.Count ? cs[i].Nome : "Cena " + (i + 1);
            int NumCena(int i) => i < cs.Count ? mesa.Cfg.Cenas.ToList().IndexOf(cs[i]) + 1 : i + 1;
            string Curto(string s) => s.Length > 22 ? s.Substring(0, 21) + "…" : s;
            // fileira 1: quatro cenas no Preview + TRANSIÇÃO
            for (int i = 0; i < 4; i++) T($"{i + 1:00} Cena {i + 1}", Curto(NomeCena(i)), "preview", "cena=" + NumCena(i), Az);
            T("05 TRANSIÇÃO", "TRANSIÇÃO", "transicao", null, Vd);
            // fileira 2: telas da capa + roteiro
            T("06 Tela de espera", "Tela de\nespera", "capa", "modo=espera", Dr);
            T("07 Abertura", "Abertura", "capa", "modo=abertura", Dr);
            T("08 Encerramento", "Encerra-\nmento", "capa", "modo=encerramento", Dr);
            T("09 Momento anterior", "◀ Momento", "anterior", null, Rx);
            T("10 Próximo momento", "Momento ▶", "proximo", null, Rx);
            // fileira 3
            T("11 CORTE", "CORTE", "corte", null, Vd);
            T("12 Mudo", "MUDO", "mudo", null, Cz);
            T("13 Gravar", "GRAVAR\n(2× para)", "gravar", null, Vm);
            T("14 PARAR câmera que segue", "PARAR\ncâmera", "parar", null, Vm);
            T("15 Transmitir (2x)", "AO VIVO\n(aperte 2×)", "transmitir", null, Vm);
            // Mais: todas as cenas no Preview e direto no ar, e as posições das câmeras PTZ
            var todas = mesa.Cfg.Cenas.ToList();
            string Limpo(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim();
            for (int i = 0; i < todas.Count; i++)
            {
                T($"Mais\\Cenas no Preview\\{i + 1:00} {Limpo(todas[i].Nome)}", Curto(todas[i].Nome), "preview", "cena=" + (i + 1), Az);
                T($"Mais\\Cenas direto no ar\\{i + 1:00} {Limpo(todas[i].Nome)}", Curto(todas[i].Nome) + "\nNO AR", "programa", "cena=" + (i + 1), Vm);
            }
            var cams = Cameras.Lista.Where(c => !string.IsNullOrWhiteSpace(c.Ip)).ToList();
            for (int k = 0; k < cams.Count; k++)
                foreach (var p in cams[k].Posicoes)
                    T($"Mais\\Posições PTZ\\{Limpo(cams[k].Nome)} - {p.N:00} {Limpo(p.Nome)}", Curto(cams[k].Nome) + "\n" + Curto(p.Nome), "ptz", $"camera={k + 1} posicao={p.N}", Cz);
            // (0.1.10) posições salvas nas cenas: só a câmera vai (a cena não muda)
            for (int i = 0; i < todas.Count; i++)
                if (todas[i].Ptz != null)
                    T($"Mais\\Posições das cenas\\{i + 1:00} {Limpo(todas[i].Nome)}", Curto(todas[i].Nome) + "\n📷 posição", "ptz", "cena=" + (i + 1), Cz);
            T("Mais\\Testar botão", "Teste ✓", "teste", null, Cz);
            return l;
        }

        object EstadoBotoes()
        {
            var cs = mesa.Cfg.Cenas.ToList();
            var ml = roteiro?.Lista ?? new System.Collections.Generic.List<Momento>(); int ag = roteiro?.Agora ?? -1;
            string Mom(int i) => i >= 0 && i < ml.Count ? ml[i].Nome : "";
            return new
            {
                versao = Program.Versao,
                programa = mesa.Cfg.Programa, preview = mesa.Cfg.Preview, efeito = mesa.Cfg.Efeito,
                aoVivo = saida.AoVivo, gravando = saida.Gravando, mudo = som.Mudo,
                tempoAr = saida.TempoAr, tempoGrav = saida.TempoGrav,
                seguindo = rastreio != null,
                armado = btArmado != null && DateTime.Now < btArmadoAte ? btArmado : "",
                momento = new { agora = Mom(ag), anterior = Mom(ag - 1), proximo = Mom(ag + 1), n = ag + 1, total = ml.Count },
                cenas = cs.Select((c, i) => new { n = i + 1, id = c.Id, nome = c.Nome, fonte = c.Fonte, programa = c.Id == mesa.Cfg.Programa, preview = c.Id == mesa.Cfg.Preview }).ToList(),
                cameras = Cameras.Lista.Where(c => !string.IsNullOrWhiteSpace(c.Ip)).Select((c, i) => new { n = i + 1, nome = c.Nome, posicoes = c.Posicoes.Select(p => new { n = p.N, nome = p.Nome }).ToList() }).ToList()
            };
        }

        void EnviarSaidaCfg() => Enviar(new
        {
            tipo = "saidaCfg", kbps = saida.Cfg.Kbps, codificador = saida.Cfg.Codificador, detectado = saida.Cfg.CodificadorDetectado,
            pasta = saida.Cfg.PastaGravacao, temFfmpeg = File.Exists(Transmissor.Ffmpeg),
            destinos = saida.Cfg.Destinos.Select(x => new { id = x.Id, nome = x.Nome, tipo = x.Tipo, servidor = x.Servidor, ligado = x.Ligado, chave = Cofre.Mascara("chave:" + x.Id) })
        });

        System.Collections.Generic.List<DispositivoSom> dispositivosSom;
        void EnviarSomCfg(bool relistar)
        {
            if (relistar || dispositivosSom == null) dispositivosSom = Som.Dispositivos();
            Enviar(new
            {
                tipo = "somCfg", dispositivos = dispositivosSom, perfis = Perfis.Lista,
                dispositivo = som.Cfg.Dispositivo, canais = som.Cfg.Canais, ganho = som.Cfg.GanhoDb, perfil = som.Cfg.Perfil,
                nivelar = som.Cfg.Nivelar, alvo = som.Cfg.AlvoLufs, atraso = som.Cfg.AtrasoMs,
                abaixar = som.Cfg.AbaixarComVideo, abaixarDb = som.Cfg.AbaixarDb, volumeVideo = som.Cfg.VolumeVideoDb,
                canaisEntrada = som.CanaisDispositivo,
                rotulos = Enumerable.Range(1, 64).Select(n => Perfis.Rotulo(som.Cfg.Perfil, n + DeslocSom(), mesaSom)),
                mesaIp = som.Cfg.MesaIp, mesaEstado = mesaSom.Estado, mesaModelo = mesaSom.Conectada ? (mesaSom.Modelo + " " + mesaSom.Firmware).Trim() + (mesaSom.Cena != "" ? " · cena: " + mesaSom.Cena : "") : "",
                estado = som.Estado
            });
        }
        int DeslocSom() => som.Cfg.Dispositivo.StartsWith("asio:") ? 0 : Perfis.Deslocamento(dispositivosSom?.FirstOrDefault(d => d.id == som.Cfg.Dispositivo)?.nome ?? som.NomeAtual);
        // a aux da transmissão está muda ou com o fader no mínimo na mesa?
        string AvisoMesa()
        {
            if (string.IsNullOrEmpty(som.Cfg.Dispositivo)) return null;
            foreach (var n in som.Cfg.Canais)
            {
                int aux = Perfis.AuxDaUsb(som.Cfg.Perfil, n + DeslocSom());
                var a = aux > 0 ? mesaSom.AvisoAux(aux) : null;
                if (a != null) return a;
            }
            return null;
        }
        string NomeSom()
        {
            if (string.IsNullOrEmpty(som.Cfg.Dispositivo)) return "sem som";
            var c = som.Cfg.Canais;
            return som.NomeAtual + " · " + string.Join(" + ", c.Select(n => Perfis.Rotulo(som.Cfg.Perfil, n + DeslocSom(), mesaSom))) + (c.Length == 1 ? " (mono)" : "");
        }

        // roteiro: o rastreio nesta câmera, com o movimento ligado (devolve o erro, se houver)
        string PrepararRastreio(CameraCfg c)
        {
            if (string.IsNullOrEmpty(c.Fonte) || string.IsNullOrWhiteSpace(c.Ip)) return "a câmera \"" + c.Nome + "\" precisa da imagem (NDI) e do IP em Ajustes › Câmeras";
            var r = AbrirRastreio(); if (r == null) return "o rastreio não abriu";
            if (r.FonteConectada != c.Fonte || r.Cfg.Ip != c.Ip || r.Cfg.Porta != c.Porta)
            {
                if (r.Real) r.LigarReal(false);
                r.Ajustar(r.Cfg.Pan, r.Cfg.Tilt, r.Cfg.Zona, r.Cfg.Inverter, c.Ip, c.Porta);
                var e = r.Conectar(c.Fonte, r.Cfg.Leve); if (e != null) return e;
            }
            if (!r.Real) { var e = r.LigarReal(true); if (e != null) return "não conectou na câmera para mover"; }
            return null;
        }

        static readonly JsonSerializerOptions jsonSemCaixa = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        void EnviarRoteiro(string msg)
        {
            if (roteiro == null) return;
            Enviar(new
            {
                tipo = "roteiro", perfis = roteiro.Perfis.Select(p => new { id = p.Id, nome = p.Nome, modelo = p.Modelo, n = p.Momentos.Count }),
                perfil = roteiro.Perfil.Id, lista = roteiro.Lista, alterado = roteiro.Alterado, agora = roteiro.Agora, aviso = roteiro.Aviso, msg,
                // (0.1.9) seguir o telão do MiguelAngelus: situação e como cada item de lá cai num momento daqui
                seguir = roteiro.Seguir, antes = roteiro.Antes, preparado = roteiro.Preparado, seguirTexto = roteiro.Seguir ? roteiro.SeguirTexto : "",
                miguel = roteiro.Seguir && Miguel.AoVivo is AoVivoMiguel av ? new
                {
                    roteiro = av.Roteiro, vivo = av.VivoIdx,
                    itens = av.Itens.Select(it => new { txt = AoVivoMiguel.Juntar(it), m = roteiro.Mapear(it) })
                } : null,
            });
        }

        // capa da live: manda os ajustes e a prévia (desenhada fora da linha da tela)
        // rede: placas, câmeras e (se pedir) a internet até os servidores das plataformas
        void EnviarRede(bool internet)
        {
            var cams = Cameras.Lista.Select(c => (c.Nome, c.Ip, c.Porta, c.Protocolo, c.Fonte)).ToList();
            var mesaIp = som.Cfg.MesaIp;
            System.Threading.Tasks.Task.Run(() =>
            {
                var placas = new System.Collections.Generic.List<object>(); var redes = new System.Collections.Generic.List<(System.Net.IPAddress ip, System.Net.IPAddress masc)>();
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var ips = ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
                    if (ips.Count == 0) continue;
                    foreach (var a in ips) redes.Add((a.Address, a.IPv4Mask));
                    var tipoPlaca = ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? "Cabo (Ethernet)" : ni.NetworkInterfaceType.ToString();
                    placas.Add(new { nome = ni.Name, desc = ni.Description, tipo = tipoPlaca, ips = string.Join(", ", ips.Select(a => a.Address + "/" + a.PrefixLength)), mbps = ni.Speed > 0 ? ni.Speed / 1_000_000 : 0 });
                }
                bool MesmaRede(string ip)
                {
                    if (!System.Net.IPAddress.TryParse(ip ?? "", out var x)) return false;
                    if (System.Net.IPAddress.IsLoopback(x)) return true;
                    var b = x.GetAddressBytes();
                    return redes.Any(r => { if (r.masc == null) return false; var i = r.ip.GetAddressBytes(); var m = r.masc.GetAddressBytes(); for (int k = 0; k < 4; k++) if ((i[k] & m[k]) != (b[k] & m[k])) return false; return true; });
                }
                long Ping(string host, int porta)
                {
                    try { var sw = System.Diagnostics.Stopwatch.StartNew(); using var tc = new System.Net.Sockets.TcpClient(); return tc.ConnectAsync(host, porta).Wait(2500) && tc.Connected ? sw.ElapsedMilliseconds : -1; } catch { return -1; }
                }
                var equip = new System.Collections.Generic.List<object>();
                foreach (var (nome, ip, porta, proto, fonte) in cams)
                    if (!string.IsNullOrWhiteSpace(ip)) equip.Add(new { nome = "📷 " + nome, ip, mesma = MesmaRede(ip), ms = proto == "tcp" ? Ping(ip, porta > 0 ? porta : 5678) : -2 });
                if (!string.IsNullOrWhiteSpace(mesaIp)) equip.Add(new { nome = "🎚 Mesa de som", ip = mesaIp, mesma = MesmaRede(mesaIp), ms = Ping(mesaIp, 80) });
                var ndi = Ndi.Fontes().Select(f => f.nome).OrderBy(x => x).ToList();
                object net = null;
                if (internet) net = new[] { ("YouTube", "a.rtmp.youtube.com", 1935), ("Facebook", "live-api-s.facebook.com", 443) }
                    .Select(s => new { nome = s.Item1, ms = Ping(s.Item2, s.Item3) }).ToList();
                try { BeginInvoke(new Action(() => Enviar(new { tipo = "rede", placas, equip, ndi, net }))); } catch { }
            });
        }
        LeitorJoystick joystick;
        int capaPedido;
        void EnviarCapa(string modo, string msg = null)
        {
            var c = Capa.Cfg; int n = ++capaPedido;
            var cenas = mesa.Retrato.ContainsKey("capa-espera") && mesa.Retrato.ContainsKey("capa-capa") && mesa.Retrato.ContainsKey("capa-fim");
            var info = new
            {
                tipo = "capa", cfg = c, modo, msg, cenas,
                corAuto = Capa.CorAuto(c), corUsada = Capa.CorUsada(c), celebracaoAuto = Liturgia.Info(c.Quando).nome,
                paletas = Capa.Paletas.ToDictionary(p => p.Key, p => new { nome = p.Value.Nome, amostra = ColorTranslator.ToHtml(p.Value.Amostra) }),
                arquivo = "capa_" + c.Quando.ToString("yyyy-MM-dd_HHmm") + ".png"
            };
            Enviar(info);
            System.Threading.Tasks.Task.Run(() =>
            {
                string img = null; try { img = Capa.JpegBase64(modo, c); } catch (Exception e) { Registro.Erro("prévia da capa", e); }
                if (n != capaPedido || img == null) return;
                try { BeginInvoke(new Action(() => { if (n == capaPedido) Enviar(new { tipo = "capaImg", modo, img }); })); } catch { }
            });
        }

        // ---------- início ----------
        DateTime esperaFeita;   // a celebração em que a tela de espera automática já entrou (não repete)
        void EsperaAutomatica()
        {
            var c = Capa.Cfg; if (!c.EsperaAuto) return;
            var agora = DateTime.Now; var hora = c.Quando.AddMinutes(-Math.Clamp(c.EsperaMin, 1, 120));
            if (agora < hora || agora >= c.Quando || esperaFeita == c.Quando || roteiro.Agora >= 0) return;
            esperaFeita = c.Quando;
            if (!mesa.Retrato.ContainsKey("capa-espera")) mesa.NovaCena("Tela de espera", "capa:espera", false, "capa-espera");
            mesa.Direto("capa-espera");
            Registro.Info("tela de espera automática no ar (" + c.EsperaMin + " min antes)");
        }

        int inicioPedido;
        void EnviarInicio(string msg = null)
        {
            var c = Capa.Cfg; var lit = Liturgia.Info(c.Quando); int n = ++inicioPedido;
            var destinos = saida.Cfg.Destinos.Select(d => new { id = d.Id, nome = d.Nome, tipo = d.Tipo, ligado = d.Ligado, chave = Cofre.Tem("chave:" + d.Id) }).ToList();
            var cams = Cameras.Lista.Select(x => (x.Nome, x.Fonte, x.Ip, x.Porta, x.Protocolo)).ToList();
            var retrato = mesa.Retrato; var lista = roteiro.Lista.ToList(); var perfil = roteiro.Perfil;
            string somEstado = som.Estado, somNome = som.NomeAtual, pastaGrav = saida.Cfg.PastaGravacao;
            Enviar(new
            {
                tipo = "inicio", msg, celebracao = Capa.CelebracaoUsada(c), cor = Capa.CorUsada(c), amostra = ColorTranslator.ToHtml(Capa.Paletas[Capa.CorUsada(c)].Amostra),
                quando = c.Quando, dataLonga = Capa.DataLonga(c.Quando), hora = Capa.Hora(c.Quando), celebrante = c.Celebrante, titulo = c.Titulo,
                esperaAuto = c.EsperaAuto, esperaMin = c.EsperaMin, horaEspera = Capa.Hora(c.Quando.AddMinutes(-c.EsperaMin)),
                destinos, gravando = saida.Gravando, aoVivo = saida.AoVivo, perfil = perfil?.Id, iniciado = roteiro.Agora >= 0
            });
            // a lista "antes de ir ao ar" testa rede e câmeras: fora da linha da tela
            System.Threading.Tasks.Task.Run(() =>
            {
                var ck = new System.Collections.Generic.List<object>();
                void Item(string nome, string estado, string det) => ck.Add(new { nome, estado, det });   // estado: ok | aviso | erro
                var ndi = Ndi.Fontes().Select(f => f.nome).ToHashSet();
                if (cams.Count == 0) Item("Câmeras", "aviso", "nenhuma câmera em Ajustes › Câmeras");
                foreach (var (nome, fonte, ip, porta, proto) in cams)
                {
                    var partes = new System.Collections.Generic.List<string>(); var est = "ok";
                    if (!string.IsNullOrEmpty(fonte)) { if (ndi.Contains(fonte)) partes.Add("imagem ok"); else { partes.Add("imagem (NDI) não encontrada"); est = "erro"; } }
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        if (proto == "tcp")
                        {
                            bool ok = false; try { using var tc = new System.Net.Sockets.TcpClient(); ok = tc.ConnectAsync(ip, porta > 0 ? porta : 5678).Wait(1200) && tc.Connected; } catch { }
                            if (ok) partes.Add("controle responde"); else { partes.Add("controle não responde (" + ip + ")"); est = "erro"; }
                        }
                        else partes.Add("controle por UDP (" + ip + ")");
                    }
                    if (partes.Count == 0) { partes.Add("sem imagem nem controle configurados"); est = "aviso"; }
                    Item(nome, est, string.Join(" · ", partes));
                }
                Item("Som", somEstado == "ok" ? "ok" : "erro", somEstado == "ok" ? somNome : somEstado);
                var ativos = destinos.Where(d => d.ligado).ToList();
                if (ativos.Count == 0) Item("Plataformas", "aviso", "nenhuma ligada: só grava");
                foreach (var d in ativos) Item(d.nome, d.chave ? "ok" : "erro", d.chave ? "chave guardada" : "falta a chave da transmissão");
                bool net = false; try { using var tc = new System.Net.Sockets.TcpClient(); net = tc.ConnectAsync("a.rtmp.youtube.com", 1935).Wait(2500) && tc.Connected; } catch { }
                Item("Internet", net ? "ok" : "erro", net ? "o servidor do YouTube responde" : "não alcançou o servidor do YouTube");
                try
                {
                    var pasta = string.IsNullOrWhiteSpace(pastaGrav) ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : pastaGrav;
                    var livre = new DriveInfo(Path.GetPathRoot(pasta)).AvailableFreeSpace / 1e9;
                    Item("Espaço para gravar", livre > 20 ? "ok" : livre > 5 ? "aviso" : "erro", livre.ToString("0") + " GB livres");
                }
                catch { }
                Item("Programa de transmissão (FFmpeg)", File.Exists(Transmissor.Ffmpeg) ? "ok" : "erro", File.Exists(Transmissor.Ffmpeg) ? "instalado" : "faltando na pasta do Cast");
                var semCena = lista.Count(x => x.Tipo != "video" && (string.IsNullOrEmpty(x.Cena) || !retrato.ContainsKey(x.Cena)));
                var semVideo = lista.Count(x => x.Tipo == "video" && !File.Exists(x.Video));
                Item("Roteiro", semCena + semVideo == 0 ? "ok" : "aviso", (perfil?.Nome ?? "") + " · " + lista.Count + " momentos" + (semCena > 0 ? " · " + semCena + " sem cena" : "") + (semVideo > 0 ? " · " + semVideo + " vídeo(s) não encontrados" : ""));
                bool cenasCapa = retrato.ContainsKey("capa-espera") && retrato.ContainsKey("capa-capa") && retrato.ContainsKey("capa-fim");
                Item("Capa e telas", cenasCapa ? "ok" : "aviso", cenasCapa ? "Tela de espera, Abertura e Encerramento prontas" : "crie as cenas em Ajustes › Capa da live");
                if (n != inicioPedido) return;
                try { BeginInvoke(new Action(() => { if (n == inicioPedido) Enviar(new { tipo = "inicioCk", itens = ck }); })); } catch { }
            });
        }

        void EnviarCameras(bool comFontes)
        {
            var fontes = comFontes ? Ndi.Fontes().Select(f => f.nome).OrderBy(x => x).ToList() : null;
            Enviar(new { tipo = "cameras", lista = Cameras.Lista, fontes });
        }

        // ---------- rastreio ----------
        Rastreador AbrirRastreio()
        {
            if (rastreio == null)
            {
                try { rastreio = new Rastreador(); }
                catch (Exception e) { Registro.Erro("abrir rastreio", e); Enviar(new { tipo = "rastreio", erro = "O rastreio não abriu: " + e.Message }); return null; }
            }
            return rastreio;
        }
        void EnviarRastreio(bool cfg)
        {
            var r = rastreio; if (r == null) return;
            string img = rtVisivel ? r.ImagemNova(ref rtImagemSeq) : null;
            Enviar(new
            {
                tipo = "rastreio", aberto = true, real = r.Real, seguir = r.Seguir, analisar = r.Analisar, cadastrado = r.Cadastrado, coletando = r.Coletando,
                l1 = r.Linha1, l2 = r.Linha2, l3 = r.Linha3, evento = r.UltimoEvento, fonte = r.FonteConectada, fontes = r.NomesFontes,
                erro = r.ErroModelos ?? r.ErroNdi, img,
                cfg = cfg ? r.Cfg : null
            });
        }

        void Mensagem(string json)
        {
            using var d = JsonDocument.Parse(json);
            var m = d.RootElement;
            string Txt(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Num(string k, int padrao) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : padrao;
            bool Sim(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            switch (Txt("tipo"))
            {
                case "pronto":
                    web.CoreWebView2.PostSharedBufferToScript(monitores, CoreWebView2SharedBufferAccess.ReadOnly,
                        JsonSerializer.Serialize(new { tipo = "monitores", l = Mesa.ML, a = Mesa.MA, miniL = Mesa.MiniL, miniA = Mesa.MiniA, miniMax = Mesa.MiniMax }));
                    Enviar(new { tipo = "versao", versao = Program.Versao });
                    mesa.PedirEstado();
                    Enviar(saida.Estado()); EnviarSaidaCfg(); EnviarSomCfg(true);
                    break;
                case "pedirSom": EnviarSomCfg(true); break;
                // ---------- roteiro ----------
                case "rotPedir": EnviarRoteiro(null); break;
                case "rotEscolher": roteiro.Escolher(Txt("id")); break;
                case "rotEditar":
                    if (m.TryGetProperty("lista", out var rl) && rl.ValueKind == JsonValueKind.Array)
                        roteiro.Editar(JsonSerializer.Deserialize<System.Collections.Generic.List<Momento>>(rl.GetRawText(), jsonSemCaixa));
                    break;
                case "rotSalvar": EnviarRoteiro(roteiro.Salvar()); break;
                case "rotNovo": EnviarRoteiro(roteiro.SalvarComoNovo(Txt("nome"))); break;
                case "rotRenomear": EnviarRoteiro(roteiro.Renomear(Txt("nome"))); break;
                case "rotApagar": EnviarRoteiro(roteiro.Apagar()); break;
                case "rotExportar":
                    using (var dlg = new SaveFileDialog { Title = "Exportar perfil", Filter = "Perfil do Cast|*.json", FileName = roteiro.Perfil.Nome + ".json" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) try { EnviarRoteiro(roteiro.Exportar(dlg.FileName)); } catch (Exception ex) { EnviarRoteiro("Não exportou: " + ex.Message); }
                    break;
                case "rotImportar":
                    using (var dlg = new OpenFileDialog { Title = "Importar perfil", Filter = "Perfil do Cast|*.json" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) try { EnviarRoteiro(roteiro.Importar(dlg.FileName)); } catch (Exception ex) { EnviarRoteiro("Não importou: " + ex.Message); }
                    break;
                case "rotIniciar": roteiro.Iniciar(); break;
                case "rotProximo": roteiro.Proximo(); break;
                case "rotAnterior": roteiro.Anterior(); break;
                case "rotIr": roteiro.Ir(Num("i", -1)); break;
                case "rotSeguir": roteiro.DefinirSeguir(Sim("on")); break;              // (0.1.9)
                case "miniaturas": mesa.DefinirMiniaturas(Sim("on")); break;            // (0.1.9) backlog 2.1
                case "rotAntes": roteiro.DefinirAntes(Num("n", 2)); break;
                case "rotPreparar": roteiro.Preparar(Num("i", -1)); break;
                case "rotVideo":
                    using (var dlg = new OpenFileDialog { Title = "Vídeo do roteiro", Filter = "Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) Enviar(new { tipo = "rotVideoEscolhido", i = Num("i", -1), caminho = dlg.FileName, noFim = Sim("noFim") });
                    break;
                // ---------- câmeras e PTZ ----------
                // ---------- rede, backup e sobre ----------
                case "redePedir": EnviarRede(Sim("internet")); EnviarMiguel(); break;
                case "miguelPedir": EnviarMiguel(); _ = Miguel.Agora(); break;
                case "miguelCfg":
                    {
                        var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                        // só o que veio na mensagem muda
                        if (m.TryGetProperty("seguir", out _)) c.SeguirMiguel = Sim("seguir");
                        if (m.TryGetProperty("local", out _)) c.MiguelLocal = Txt("local") ?? "";
                        if (m.TryGetProperty("soMissas", out _)) c.MiguelSoMissas = Sim("soMissas");
                        if (m.TryGetProperty("folheto", out _)) c.MiguelFolheto = Sim("folheto");
                        if (m.TryGetProperty("marcas", out _)) c.MiguelMarcas = Sim("marcas");
                        Capa.Trocar(c, true); AplicarAgenda(); EnviarMiguel(); EnviarInicio(); break;
                    }
                case "sobrePedir":
                    Enviar(new { tipo = "sobre", versao = Program.Versao, dados = Program.PastaDados, programa = Program.Pasta, runtime = Environment.Version.ToString() }); break;
                case "abrirPastaDados":
                    {
                        var qual = Txt("qual"); var pasta = qual == "registro" ? Path.Combine(Program.PastaDados, "registro") : Program.PastaDados;
                        if (Directory.Exists(pasta)) System.Diagnostics.Process.Start("explorer.exe", "\"" + pasta + "\"");
                        break;
                    }
                case "backupExportar":
                    {
                        using var dlg = new SaveFileDialog { Title = "Salvar o backup dos ajustes", Filter = "Backup do Cast|*.zip", FileName = "backup-cast-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        try { var n = Backup.Exportar(dlg.FileName); Enviar(new { tipo = "backupMsg", msg = "Backup salvo: " + Path.GetFileName(dlg.FileName) + " (" + n + " arquivos)." }); }
                        catch (Exception e) { Registro.Erro("backup", e); Enviar(new { tipo = "backupMsg", msg = "Não consegui salvar o backup: " + e.Message }); }
                        break;
                    }
                case "backupImportar":
                    {
                        using var dlg = new OpenFileDialog { Title = "Restaurar um backup do Cast", Filter = "Backup do Cast|*.zip" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        if (MessageBox.Show(this, "Restaurar os ajustes deste backup?\n\nCenas, câmeras, roteiros, capa, som, transmissão e joysticks deste computador serão trocados pelos do backup. Os atuais ficam guardados numa cópia na pasta de dados.\n\nO Cast fecha e abre de novo.", "MiguelAngelus Cast", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) break;
                        if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "backupMsg", msg = "Pare a transmissão e a gravação antes de restaurar." }); break; }
                        try
                        {
                            var n = Backup.Importar(dlg.FileName);
                            Registro.Info("backup restaurado: " + n + " arquivos");
                            Application.Restart(); Environment.Exit(0);
                        }
                        catch (Exception e) { Registro.Erro("restaurar backup", e); Enviar(new { tipo = "backupMsg", msg = "Não consegui restaurar: " + e.Message }); }
                        break;
                    }                // ---------- joysticks ----------
                case "joyLigar":
                    joystick ??= new LeitorJoystick(Enviar);
                    Enviar(new { tipo = "joyCfg", dados = LeitorJoystick.Carregar() }); break;
                case "joyGuardar": if (m.TryGetProperty("dados", out var jd)) LeitorJoystick.Gravar(jd); break;                // ---------- início ----------
                case "inicioPedir": EnviarInicio(); break;
                case "capaMini":
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        string img = null; try { img = Capa.JpegBase64("capa", Capa.Cfg, 384, 216); } catch (Exception e) { Registro.Erro("miniatura da capa", e); }
                        if (img != null) try { BeginInvoke(new Action(() => Enviar(new { tipo = "capaImg", modo = "mini", img }))); } catch { }
                    });
                    break;
                case "inicioEspera":
                    {
                        var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                        c.EsperaAuto = Sim("ligado"); c.EsperaMin = Math.Clamp(Num("min", c.EsperaMin), 1, 120);
                        Capa.Trocar(c, true); EnviarInicio(); break;
                    }
                case "inicioPreparar":
                    {
                        mesa.NovaCena("Tela de espera", "capa:espera", false, "capa-espera");
                        mesa.NovaCena("Abertura", "capa:capa", false, "capa-capa");
                        mesa.NovaCena("Encerramento", "capa:fim", false, "capa-fim");
                        roteiro.Iniciar();                     // leva a câmera do primeiro momento para a posição
                        mesa.PorNoPreview("capa-espera");
                        var c = Capa.Cfg;
                        EnviarInicio("Pronto: roteiro \"" + roteiro.Perfil.Nome + "\" aberto, câmera indo para o primeiro momento e a Tela de espera no preview." +
                            (c.EsperaAuto ? " Ela entra no ar sozinha às " + Capa.Hora(c.Quando.AddMinutes(-c.EsperaMin)) + "." : " Aperte TRANSIÇÃO para pôr no ar."));
                        break;
                    }
                // ---------- YouTube (0.0.8) ----------
                case "ytPedir": EnviarYt(); if (YouTube.Conectado) YtTrabalho(null, async () => { await YouTube.Atualizar(); return null; }); break;
                case "ytCliente":
                    {
                        using var dlg = new OpenFileDialog { Title = "ID do cliente OAuth (o JSON baixado do Google Cloud)", Filter = "JSON do Google|*.json" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        try { YouTube.DefinirCliente(File.ReadAllText(dlg.FileName)); EnviarYt("ID do cliente guardado (criptografado). Agora clique em Conectar."); }
                        catch (Exception e) { EnviarYt("Não serviu: " + e.Message); }
                        break;
                    }
                case "ytConectar":
                    // (0.0.9) o login espera o navegador sem travar as outras consultas ao YouTube
                    EnviarYt("Abrindo o navegador para entrar na conta do canal… (se fechar o navegador sem entrar, a conexão atual continua valendo)");
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        string msg;
                        try { await YouTube.Conectar(); await YouTube.Atualizar(); msg = "Conectado ao canal " + YouTube.Canal + "."; }
                        catch (OperationCanceledException) { return; }   // substituído por um clique novo em Conectar
                        catch (Exception e) { msg = "YouTube: " + e.Message; Registro.Info("YouTube: " + e.Message); }
                        Na(() => EnviarYt(msg));
                    });
                    break;
                case "ytDesconectar": YtTrabalho(null, async () => { await YouTube.Desconectar(); return "Desconectado. O acesso foi retirado da conta Google."; }); break;
                case "ytCfg":
                    {
                        var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                        var pv = Txt("privacidade"); if (pv is "public" or "unlisted" or "private") c.YtPrivacidade = pv;
                        if (Txt("descricao") != null) c.YtDescricao = Txt("descricao");
                        Capa.Trocar(c, true); break;
                    }
                case "ytCriar":
                    {
                        var c = Capa.Cfg; var titulo = Txt("titulo"); if (string.IsNullOrWhiteSpace(titulo)) titulo = TituloLive(c);
                        var desc = string.IsNullOrWhiteSpace(c.YtDescricao) ? DescricaoLive(c) : c.YtDescricao;
                        var quando = c.Quando; var priv = c.YtPrivacidade;
                        byte[] jpg = null; try { jpg = Capa.Jpeg("capa", 1280, 720, c); } catch (Exception e) { Registro.Erro("miniatura do YouTube", e); }
                        YtTrabalho("Criando a live no YouTube…", async () =>
                        {
                            var (live, servidor, chave, aviso) = await YouTube.Criar(titulo, desc, quando, priv, jpg);
                            Na(() =>
                            {   // o destino YouTube passa a usar a chave do Cast, ligada a esta live
                                var dd = saida.Cfg.Destinos.FirstOrDefault(x => x.Tipo == "youtube");
                                if (dd != null && !string.IsNullOrEmpty(chave)) { dd.Servidor = servidor; dd.Ligado = true; Cofre.Gravar("chave:" + dd.Id, chave); saida.Salvar(); EnviarSaidaCfg(); EnviarInicio(); }
                            });
                            return aviso ?? "Live criada: \"" + live.Titulo + "\", com a capa de miniatura. Entra no ar sozinha quando você iniciar a transmissão.";
                        });
                        break;
                    }
                case "ytMiniatura":
                    {
                        var id = Txt("id"); if (string.IsNullOrEmpty(id)) break;
                        byte[] jpg; try { jpg = Capa.Jpeg("capa", 1280, 720); } catch (Exception e) { EnviarYt("Não consegui desenhar a capa: " + e.Message); break; }
                        YtTrabalho("Enviando a capa…", async () => { await YouTube.EnviarMiniatura(id, jpg); await YouTube.Atualizar(); return "Miniatura trocada pela capa atual."; });
                        break;
                    }
                case "ytAoVivo":
                    {
                        var id = Txt("id"); if (string.IsNullOrEmpty(id)) break;
                        Registro.Info("YouTube: entrar ao vivo " + id);
                        YtFundo(async () => { await YouTube.EntrarAoVivo(id, YtFase); return "No ar no YouTube."; });
                        break;
                    }
                case "abrirLink":      // (0.1.1) links do Sobre: só os da paróquia e o e-mail de contato
                    {
                        var u = Txt("url") ?? "";
                        if (u == "https://pascom-sjo.github.io/" || u == "mailto:contato.miguelangelus@gmail.com" || u.StartsWith("https://www.youtube.com/@SaoJos"))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                        break;
                    }
                case "ytAbrir":
                    {
                        var u = Txt("url") ?? "";
                        if (u.StartsWith("https://www.youtube.com/") || u.StartsWith("https://studio.youtube.com/") || u.StartsWith("https://console.cloud.google.com/"))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                        break;
                    }
                // ---------- capa da live ----------
                case "capaPedir": EnviarCapa(Txt("modo") ?? "capa"); break;
                case "capaMudar":
                    if (m.TryGetProperty("cfg", out var cc) && cc.ValueKind == JsonValueKind.Object)
                    {
                        var nova = JsonSerializer.Deserialize<CapaCfg>(cc.GetRawText(), jsonSemCaixa);
                        var velha = Capa.Cfg;   // os caminhos das imagens só mudam pelos botões
                        nova.Folheto = velha.Folheto; nova.Foto = velha.Foto; nova.Marca1 = velha.Marca1; nova.Marca2 = velha.Marca2;
                        Capa.Trocar(nova, true);
                    }
                    EnviarCapa(Txt("modo") ?? "capa"); break;
                case "capaImagem":
                    {
                        var qual = Txt("qual");
                        using var dlg = new OpenFileDialog { Title = qual == "folheto" ? "Ilustração da semana" : qual == "foto" ? "Foto da paróquia" : "Brasão ou logo (PNG com fundo transparente fica melhor)", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        string msg = null;
                        try
                        {
                            using (var teste = new System.Drawing.Bitmap(dlg.FileName)) { }
                            var dest = Capa.GuardarImagem(dlg.FileName, qual);
                            var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                            if (qual == "folheto") { c.Folheto = dest; c.Imagem = "folheto"; }
                            else if (qual == "foto") { c.Foto = dest; c.Imagem = "foto"; }
                            else if (qual == "marca1") { c.Marca1 = dest; c.UsarMarca1 = true; }
                            else if (qual == "marca2") { c.Marca2 = dest; c.UsarMarca2 = true; }
                            Capa.Trocar(c, true);
                        }
                        catch (Exception e) { msg = "Não consegui abrir essa imagem (" + e.Message + "). WebP pode não abrir: use PNG ou JPG."; }
                        EnviarCapa(Txt("modo") ?? "capa", msg); break;
                    }
                case "capaTirar":
                    {
                        var qual = Txt("qual"); var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                        if (qual == "folheto") c.Folheto = ""; else if (qual == "foto") c.Foto = ""; else if (qual == "marca1") c.Marca1 = ""; else if (qual == "marca2") c.Marca2 = "";
                        Capa.Trocar(c, true); EnviarCapa(Txt("modo") ?? "capa"); break;
                    }
                case "capaExportar":
                    {
                        var modo = Txt("modo") ?? "capa";
                        using var dlg = new SaveFileDialog { Title = "Exportar a capa", Filter = "PNG 1920×1080 (Facebook, telão)|*.png|JPEG 1280×720 (miniatura do YouTube, até 2 MB)|*.jpg", FileName = "capa_" + Capa.Cfg.Quando.ToString("yyyy-MM-dd_HHmm") + (modo == "capa" ? "" : "_" + modo) };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        var jpg = dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
                        try { File.WriteAllBytes(dlg.FileName, jpg ? Capa.Jpeg(modo) : Capa.Png(modo)); EnviarCapa(modo, "Salvo: " + Path.GetFileName(dlg.FileName) + (jpg ? " (1280×720)." : " (1920×1080).")); }
                        catch (Exception e) { EnviarCapa(modo, "Não consegui salvar: " + e.Message); }
                        break;
                    }
                case "capaCenas":
                    mesa.NovaCena("Tela de espera", "capa:espera", false, "capa-espera");
                    mesa.NovaCena("Abertura", "capa:capa", false, "capa-capa");
                    mesa.NovaCena("Encerramento", "capa:fim", false, "capa-fim");
                    EnviarCapa(Txt("modo") ?? "capa", "Cenas Tela de espera, Abertura e Encerramento na lista de cenas do Ao vivo."); break;
                case "camPedir": EnviarCameras(true); break;
                case "camNova":
                    {
                        var c = new CameraCfg { Nome = "Câmera " + (Cameras.Lista.Count + 1) };
                        Cameras.Lista.Add(c); Cameras.Salvar(); Enviar(new { tipo = "camSel", id = c.Id }); EnviarCameras(true); break;
                    }
                case "camSalvar":
                    {
                        var c = Cameras.Achar(Txt("id")); if (c == null) break;
                        c.Nome = (Txt("nome") ?? c.Nome).Trim(); c.Fonte = Txt("fonte") ?? ""; c.Ip = (Txt("ip") ?? "").Trim();
                        c.Protocolo = Txt("protocolo") is "tcp" or "udp" or "visca-ip" ? Txt("protocolo") : "tcp";
                        c.Porta = Math.Clamp(Num("porta", Cameras.PortaPadrao(c.Protocolo)), 1, 65535);
                        Cameras.Salvar(); Registro.Info($"câmera salva: {c.Nome} ({c.Ip}:{c.Porta} {c.Protocolo})"); EnviarCameras(false); break;
                    }
                case "camApagar": Cameras.Lista.RemoveAll(c => c.Id == Txt("id")); Cameras.Salvar(); EnviarCameras(false); break;
                case "ptz":
                    {
                        var c = Cameras.Achar(Txt("id"));
                        if (c == null || !m.TryGetProperty("bytes", out var bs) || bs.ValueKind != JsonValueKind.Array) break;
                        var cmd = bs.EnumerateArray().Select(x => (byte)Math.Clamp(x.GetInt32(), 0, 255)).Take(32).ToArray();
                        if (cmd.Length < 3 || cmd[0] != 0x81 || cmd[^1] != 0xFF) break;          // só comando VISCA para a câmera 1
                        ViscaRede.Enviar(c, cmd).ContinueWith(t => { if (t.Result != null) Na(() => Enviar(new { tipo = "camMsg", texto = c.Nome + ": " + t.Result })); });
                        break;
                    }
                case "camPosGuardar":
                    {
                        var c = Cameras.Achar(Txt("id")); var nome = (Txt("nome") ?? "").Trim(); if (c == null || nome == "") break;
                        int n = Cameras.ProximaLivre(c);
                        if (n < 0) { Enviar(new { tipo = "camMsg", texto = "A câmera não tem mais números livres para posições." }); break; }
                        ViscaRede.Enviar(c, new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x01, (byte)n, 0xFF }).ContinueWith(t => Na(() =>
                        {
                            if (t.Result != null) { Enviar(new { tipo = "camMsg", texto = "Não gravou: " + t.Result }); return; }
                            c.Posicoes.Add(new Posicao { N = n, Nome = nome }); Cameras.Salvar(); Registro.Info($"posição gravada: {c.Nome} nº {n} \"{nome}\"");
                            Enviar(new { tipo = "camMsg", texto = $"📍 Posição \"{nome}\" gravada na câmera (nº {n})." }); EnviarCameras(false);
                        }));
                        break;
                    }
                case "camPosTrazer":
                    {
                        var c = Cameras.Achar(Txt("id")); if (c == null || !m.TryGetProperty("nums", out var ns)) break;
                        var usados = c.Posicoes.Select(p => p.N).ToHashSet();
                        var novas = ns.EnumerateArray().Select(x => x.GetInt32()).Where(n => n >= 0 && n <= 127 && !usados.Contains(n)).Distinct().OrderBy(n => n).ToList();
                        c.Posicoes.AddRange(novas.Select(n => new Posicao { N = n, Nome = "Posição " + n })); Cameras.Salvar();
                        Enviar(new { tipo = "camMsg", texto = novas.Count == 0 ? "Essas posições já estão na lista." : $"📍 {novas.Count} posição(ões) da câmera na lista. Passe o mouse e clique em ✎ para dar nome." });
                        EnviarCameras(false); break;
                    }
                case "camPosRenomear":
                    { var p = Cameras.Achar(Txt("id"))?.Posicoes.FirstOrDefault(x => x.N == Num("n", -1)); if (p != null && !string.IsNullOrWhiteSpace(Txt("nome"))) { p.Nome = Txt("nome").Trim(); Cameras.Salvar(); } EnviarCameras(false); break; }
                case "camPosTirar":
                    { var c = Cameras.Achar(Txt("id")); c?.Posicoes.RemoveAll(x => x.N == Num("n", -1)); Cameras.Salvar(); EnviarCameras(false); break; }
                case "rtVisivel":
                    rtVisivel = Sim("sim");
                    if (rastreio != null) rastreio.QuerImagem = rtVisivel;
                    break;
                case "rtAbrir":
                    { var r = AbrirRastreio(); if (r != null) { r.QuerImagem = rtVisivel; r.Atualizar(); EnviarRastreio(true); } break; }
                case "rtAtualizar": rastreio?.Atualizar(); EnviarRastreio(false); break;
                case "rtConectar":
                    {
                        var r = AbrirRastreio(); if (r == null) break;
                        var err = r.Conectar(Txt("fonte") ?? "", !m.TryGetProperty("leve", out var lv) || lv.ValueKind != JsonValueKind.False);
                        if (err != null) MessageBox.Show(this, err, "Rastreio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        EnviarRastreio(true); break;
                    }
                case "rtUsarCamera":
                    {
                        var c = Cameras.Achar(Txt("id")); var r = AbrirRastreio(); if (c == null || r == null) break;
                        if (c.Protocolo != "tcp" && !string.IsNullOrWhiteSpace(c.Ip))
                            MessageBox.Show(this, "O rastreio move a câmera por VISCA TCP (como a F2.DP, porta 5678). Esta câmera está como \"" + c.Protocolo + "\": confira a conexão em Ajustes › Câmeras.", "Rastreio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        if (r.Real) r.LigarReal(false);   // trocar de câmera desliga o movimento (liga de novo à mão)
                        r.Ajustar(r.Cfg.Pan, r.Cfg.Tilt, r.Cfg.Zona, r.Cfg.Inverter, c.Ip, c.Porta);
                        string err = string.IsNullOrEmpty(c.Fonte) ? "Esta câmera não tem a imagem (NDI) escolhida em Ajustes › Câmeras." : r.Conectar(c.Fonte, !m.TryGetProperty("leve", out var lv2) || lv2.ValueKind != JsonValueKind.False);
                        if (err != null) MessageBox.Show(this, err, "Rastreio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        EnviarRastreio(true); break;
                    }
                case "rtCadastrar":
                    if (rastreio == null) break;
                    if (MessageBox.Show(this, "A pessoa que vai ser cadastrada AUTORIZOU usar o rosto dela para a câmera seguir?\n\nO rosto fica só na memória do Cast: some ao fechar ou em \"Apagar cadastro\". Nada de imagem ou rosto é gravado na telemetria.",
                        "Autorização", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) rastreio.Cadastrar();
                    break;
                case "rtApagar": rastreio?.Apagar(); break;
                case "rtPessoas": Enviar(new { tipo = "rtPessoas", lista = Pessoas.Resumo(), cadastrado = rastreio?.CadastroAtual != null, emUso = rastreio?.NomeCadastro }); break;
                case "rtGuardarPessoa":
                    {
                        var nome = (Txt("nome") ?? "").Trim(); string msg;
                        if (rastreio?.CadastroAtual == null) msg = "Cadastre o rosto primeiro (botão Cadastrar, com a pessoa olhando para a câmera).";
                        else if (nome == "") msg = "Escreva o nome da pessoa.";
                        else if (MessageBox.Show(this, "Leia para a pessoa (ou mostre a tela) e confirme só se ela concordar:\n\n" + Pessoas.Termo(nome) + "\n\n" + nome + " concordou?",
                                 "Termo de consentimento", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) msg = "Não guardado (sem a concordância da pessoa).";
                        else { msg = Pessoas.Guardar(nome, Txt("funcao"), rastreio.CadastroAtual) ?? "Guardado neste computador, criptografado. Nas próximas Missas, é só escolher o nome e \"Usar\"."; rastreio.UsarCadastro(rastreio.CadastroAtual, nome); }
                        Enviar(new { tipo = "rtPessoas", lista = Pessoas.Resumo(), cadastrado = rastreio?.CadastroAtual != null, emUso = rastreio?.NomeCadastro, msg });
                        break;
                    }
                case "rtUsarPessoa":
                    {
                        var pe = Pessoas.Achar(Txt("id")); string msg = null;
                        if (pe == null) msg = "Cadastro não encontrado.";
                        else if (rastreio == null) msg = "Abra a câmera do rastreio primeiro.";
                        else { rastreio.UsarCadastro(pe.Assinatura, pe.Nome); msg = "Seguindo o rosto de " + pe.Nome + " (ligue \"Seguir\")."; }
                        Enviar(new { tipo = "rtPessoas", lista = Pessoas.Resumo(), cadastrado = rastreio?.CadastroAtual != null, emUso = rastreio?.NomeCadastro, msg });
                        break;
                    }
                case "rtApagarPessoa":
                    {
                        var pe = Pessoas.Achar(Txt("id"));
                        if (pe != null && MessageBox.Show(this, "Apagar de vez o cadastro de rosto de " + pe.Nome + " deste computador?", "Apagar cadastro", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        {
                            Pessoas.Apagar(pe.Id);
                            if (rastreio?.NomeCadastro == pe.Nome) rastreio.Apagar();
                        }
                        Enviar(new { tipo = "rtPessoas", lista = Pessoas.Resumo(), cadastrado = rastreio?.CadastroAtual != null, emUso = rastreio?.NomeCadastro });
                        break;
                    }
                case "rtToque": if (rastreio != null && m.TryGetProperty("x", out var tx) && m.TryGetProperty("y", out var ty)) rastreio.Tocar((float)tx.GetDouble(), (float)ty.GetDouble()); break;
                case "rtSeguir": rastreio?.MudarSeguir(Sim("sim")); break;
                case "rtAnalisar": rastreio?.MudarAnalisar(Sim("sim")); break;
                case "rtAjustes":
                    rastreio?.Ajustar(Num("pan", 8), Num("tilt", 6), Num("zona", 14), Sim("inverter"), Txt("ip"), Num("porta", 5678));
                    EnviarRastreio(true); break;
                case "rtReal":
                    {
                        if (rastreio == null) break;
                        var err = rastreio.LigarReal(Sim("sim"));
                        if (err != null) MessageBox.Show(this, err, "Rastreio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        EnviarRastreio(false); break;
                    }
                case "rtParar": rastreio?.PararCamera(); EnviarRastreio(false); break;
                case "rtTeste":
                    {
                        var err = rastreio?.TesteResposta();
                        if (err != null) MessageBox.Show(this, err, "Rastreio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                case "rtPasta": if (rastreio != null) System.Diagnostics.Process.Start("explorer.exe", "\"" + rastreio.PastaTelemetria + "\""); break;
                case "rtPacote":
                    if (rastreio == null) break;
                    try { var z = rastreio.Pacote(); System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + z + "\""); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Rastreio"); }
                    break;
                case "salvarSom":
                    {
                        if (Txt("dispositivo") != null) som.Cfg.Dispositivo = Txt("dispositivo");
                        if (m.TryGetProperty("canais", out var cs) && cs.ValueKind == JsonValueKind.Array)
                        { var l = cs.EnumerateArray().Select(x => x.GetInt32()).Where(x => x >= 1 && x <= 64).Take(2).ToArray(); if (l.Length > 0) som.Cfg.Canais = l; }
                        if (m.TryGetProperty("ganho", out var g) && g.ValueKind == JsonValueKind.Number) som.Cfg.GanhoDb = Math.Clamp(g.GetDouble(), -30, 30);
                        if (Txt("perfil") != null) som.Cfg.Perfil = Txt("perfil");
                        if (Txt("mesaIp") != null) { som.Cfg.MesaIp = Txt("mesaIp").Trim(); mesaSom.Conectar(som.Cfg.MesaIp); }
                        som.Salvar();
                        if (m.TryGetProperty("nivelar", out var nv) && (nv.ValueKind == JsonValueKind.True || nv.ValueKind == JsonValueKind.False)) som.Cfg.Nivelar = nv.GetBoolean();
                        if (m.TryGetProperty("alvo", out var av) && av.ValueKind == JsonValueKind.Number) som.Cfg.AlvoLufs = Math.Clamp(av.GetDouble(), -30, -10);
                        if (m.TryGetProperty("atraso", out var at) && at.ValueKind == JsonValueKind.Number) som.Cfg.AtrasoMs = (int)Math.Round(at.GetDouble());
                        if (m.TryGetProperty("abaixar", out var ab) && (ab.ValueKind == JsonValueKind.True || ab.ValueKind == JsonValueKind.False)) som.Cfg.AbaixarComVideo = ab.GetBoolean();
                        if (m.TryGetProperty("abaixarDb", out var abd) && abd.ValueKind == JsonValueKind.Number) som.Cfg.AbaixarDb = Math.Clamp(abd.GetDouble(), -90, 0);
                        if (m.TryGetProperty("volumeVideo", out var vv) && vv.ValueKind == JsonValueKind.Number) som.Cfg.VolumeVideoDb = Math.Clamp(vv.GetDouble(), -30, 12);
                        som.AplicarNivelador();
                        if (!Sim("soGanho")) som.Aplicar();
                        EnviarSomCfg(false);
                        break;
                    }
                case "mudo": som.Mudo = Sim("ligado"); Registro.Info(som.Mudo ? "som mudo" : "som de volta"); break;
                case "gravar": Aviso(saida.Gravar(Sim("ligar"))); break;
                case "transmitir": Aviso(saida.Transmitir(Sim("ligar"))); break;
                case "salvarDestino":
                    {
                        var dd = saida.Cfg.Destinos.FirstOrDefault(x => x.Id == Txt("id"));
                        if (dd == null) break;
                        if (Txt("servidor") != null) dd.Servidor = Txt("servidor").Trim();
                        if (m.TryGetProperty("ligado", out var lg)) dd.Ligado = lg.ValueKind == JsonValueKind.True;
                        var ch = Txt("chave");
                        if (!string.IsNullOrWhiteSpace(ch)) Cofre.Gravar("chave:" + dd.Id, ch.Trim());
                        if (Sim("apagarChave")) Cofre.Gravar("chave:" + dd.Id, null);
                        saida.Salvar(); EnviarSaidaCfg(); Enviar(saida.Estado());
                        break;
                    }
                case "salvarSaida":
                    saida.Cfg.Kbps = Math.Clamp(Num("kbps", saida.Cfg.Kbps), 1000, 12000);
                    if (Txt("codificador") != null) saida.Cfg.Codificador = Txt("codificador");
                    saida.Salvar(); EnviarSaidaCfg();
                    break;
                case "escolherPasta":
                    using (var fb = new FolderBrowserDialog { Description = "Pasta das gravações", SelectedPath = saida.Cfg.PastaGravacao, UseDescriptionForTitle = true })
                        if (fb.ShowDialog(this) == DialogResult.OK) { saida.Cfg.PastaGravacao = fb.SelectedPath; saida.Salvar(); EnviarSaidaCfg(); }
                    break;
                case "abrirPasta":
                    try { Directory.CreateDirectory(saida.Cfg.PastaGravacao); System.Diagnostics.Process.Start("explorer.exe", "\"" + saida.Cfg.PastaGravacao + "\""); } catch (Exception ex) { Registro.Erro("abrir pasta", ex); }
                    break;
                case "testarCodificadores":
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        var ok = new[] { "h264_qsv", "h264_nvenc", "h264_amf", "h264_mf", "libopenh264" }.Where(Transmissor.Testar).ToList();
                        Na(() => Enviar(new { tipo = "codificadores", lista = ok }));
                    });
                    break;
                case "botoesPedir": EnviarBotoes(null); break;
                case "botoesTestar": Botao(Txt("cmd") ?? "teste", new System.Collections.Generic.Dictionary<string, string>()); break;
                case "botoesCriar":
                    try
                    {
                        Controle.CriarAtalhos(PastaBotoes, TeclasPadrao());
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + PastaBotoes + "\"");
                        EnviarBotoes("Atalhos e figuras criados em " + PastaBotoes);
                    }
                    catch (Exception ex) { Registro.Erro("atalhos dos botões", ex); EnviarBotoes("Não deu para criar os atalhos: " + ex.Message); }
                    break;
                case "pluginInstalar":
                    try { EnviarBotoes(Controle.AtualizarPlugins(true)); }
                    catch (Exception ex) { Registro.Erro("instalar plugin", ex); EnviarBotoes("Não deu para instalar o plugin: " + ex.Message); }
                    break;
                case "botoesPasta":
                    if (Directory.Exists(PastaBotoes)) System.Diagnostics.Process.Start("explorer.exe", "\"" + PastaBotoes + "\""); else EnviarBotoes("Crie os atalhos primeiro.");
                    break;
                case "preview": mesa.PorNoPreview(Txt("id")); break;
                case "transicao": mesa.Transicao(); break;
                case "direto": mesa.Direto(Txt("id")); break;
                case "efeito": mesa.Efeito(Txt("efeito"), Num("ms", 500)); break;
                case "novaCena": mesa.NovaCena(Txt("nome"), Txt("fonte"), Sim("preencher")); break;
                case "apagarCena": mesa.ApagarCena(Txt("id")); break;
                case "duplicarCena": mesa.Duplicar(Txt("id")); break;
                // (0.1.10) câmera PTZ da cena: posição salva na cena (coordenadas) e rastreio a partir dela
                case "camCena": mesa.DefinirCamera(Txt("id"), Txt("camera"), Txt("rastreio"), Txt("pessoa")); break;
                case "camCenaSalvar":
                    {
                        var id = Txt("id"); var c = Cameras.Achar(Txt("camera"));
                        ViscaRede.LerPosicao(c).ContinueWith(t => Na(() =>
                        {
                            var (p, erro) = t.Result;
                            if (p != null) mesa.GravarPtz(id, p);
                            Enviar(new { tipo = "camCenaMsg", id, texto = p != null ? $"Posição salva na cena ({p.Texto()}). A câmera não guardou nada." : "Não deu para salvar: " + erro + ".", erro = p == null });
                        }));
                        break;
                    }
                case "camCenaEsquecer": mesa.GravarPtz(Txt("id"), null); break;
                case "camCenaIr":
                    {
                        var e = roteiro.IrPosicaoDaCena(Txt("id"));
                        Enviar(new { tipo = "camCenaMsg", id = Txt("id"), texto = e == null ? "Câmera indo para a posição salva na cena." : "Não deu: " + e + ".", erro = e != null });
                        break;
                    }
                case "somCena":
                    {
                        System.Collections.Generic.List<SomFonte> fs = null;     // null = padrão
                        if (m.TryGetProperty("fontes", out var fj) && fj.ValueKind == JsonValueKind.Array)
                            fs = JsonSerializer.Deserialize<System.Collections.Generic.List<SomFonte>>(fj.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        mesa.DefinirSom(Txt("id"), fs);
                        break;
                    }
                case "capturas":       // (0.1.8) monitores e janelas abertas, para a cena "Tela ou janela"
                    Enviar(new { tipo = "capturas", monitores = FonteCaptura.Monitores(), janelas = FonteCaptura.ListaJanelas() });
                    break;
                case "somEntradas":
                    Enviar(new { tipo = "somEntradas", lista = Som.Dispositivos().Where(d => d.id.StartsWith("wasapi:") || d.id.StartsWith("loop:")).ToList(), abertas = som.EstadoExtras() });
                    break;
                case "renomearCena": mesa.Renomear(Txt("id"), Txt("nome")); break;
                case "mover": mesa.Mover(Txt("id"), Num("delta", 0)); break;
                case "moverPara": mesa.MoverPara(Txt("id"), Num("pos", 0)); break;
                case "camadas":
                case "sobrepor":
                    {
                        var opc = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var ks = m.TryGetProperty("camadas", out var kj) && kj.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<System.Collections.Generic.List<Camada>>(kj.GetRawText(), opc) : new System.Collections.Generic.List<Camada>();
                        if (Txt("tipo") == "sobrepor") { mesa.DefinirSobrepor(ks, Sim("gravar")); break; }
                        float[] ret = m.TryGetProperty("ret", out var rj) && rj.ValueKind == JsonValueKind.Array ? rj.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray() : null;
                        mesa.DefinirCamadas(Txt("id"), Txt("nome"), ret, ks, Sim("semSobrepor"), Sim("gravar"));
                        break;
                    }
                case "escolherImagemCamada":
                    using (var dlg = new OpenFileDialog { Title = "Imagem para pôr por cima (PNG com fundo transparente fica melhor)", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp" })
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            try { Enviar(new { tipo = "camadaImagem", caminho = Capa.GuardarImagem(dlg.FileName, "camada-" + Path.GetFileNameWithoutExtension(dlg.FileName)), nome = Path.GetFileNameWithoutExtension(dlg.FileName) }); }
                            catch (Exception ex) { Aviso("Não copiou a imagem: " + ex.Message); }
                    break;
                case "fontesNdi":
                    var lista = Ndi.Fontes().Select(f => f.nome).OrderBy(x => x).ToList();
                    Enviar(new { tipo = "fontesNdi", lista, erro = lista.Count == 0 ? Ndi.Erro : null });
                    break;
                case "escolherVideo":
                    using (var dlg = new OpenFileDialog { Title = "Escolher vídeo", Filter = "Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) Enviar(new { tipo = "videoEscolhido", caminho = dlg.FileName });
                    break;
                case "escolherImagem":
                    using (var dlg = new OpenFileDialog { Title = "Escolher imagem", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) Enviar(new { tipo = "imagemEscolhida", caminho = dlg.FileName });
                    break;
            }
        }
    }
}
