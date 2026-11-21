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
        System.Threading.Timer atuRelogio, nuvemRelogio; bool atualizando;

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
                Controle.Parar(); relogio.Stop(); relogioSom.Stop(); relogioRastreio.Stop(); joystick?.Dispose(); try { rastreio?.Dispose(); } catch { } saida.Dispose(); som.Dispose(); mesaSom.Dispose(); mesa.Dispose();
                // (0.1.12) leva para a nuvem o que mudou nesta sessão (até 8 s; sem internet, vai na próxima vez)
                if (Nuvem.Ligada) try { System.Threading.Tasks.Task.Run(() => Nuvem.Sincronizar(false)).Wait(8000); } catch { }
                Registro.Info("fechou");
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
                MapearMomentos();
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

                relogio.Tick += (s, e) => { Enviar(new { tipo = "numeros", qps = Math.Round(mesa.QpsReal, 1), ms = Math.Round(mesa.MsQuadro, 1), atrasos = mesa.Atrasos, faltas = som.Faltas, descartes = som.Descartes }); Enviar(saida.Estado()); };
                relogio.Start();
                roteiro = new Roteiro(mesa, PrepararRastreio, () => rastreio);
                roteiro.Mudou += () => { if (rotPendente) return; rotPendente = true; Na(() => { rotPendente = false; EnviarRoteiro(null); }); };
                relogioRastreio.Tick += (s, e) => { EnviarRastreio(false); try { roteiro.Tick(); EsperaAutomatica(); GravacaoAutomatica(); GravacaoDoMomento(); } catch (Exception ex) { Registro.Erro("roteiro", ex); } };
                relogioRastreio.Start();
                // (0.1.5) botões (Stream Deck / Stream Dock): porta só deste computador
                Controle.NaTelaFn = f => { var tcs = new System.Threading.Tasks.TaskCompletionSource<object>(); Na(() => { try { tcs.TrySetResult(f()); } catch (Exception ex) { Registro.Erro("botão", ex); tcs.TrySetResult("!erro"); } }); return tcs.Task; };
                Controle.Executar = Botao;
                Controle.Estado = EstadoBotoes;
                Controle.Iniciar();
                // (0.1.11) atalhos dos botões feitos com o Cast em outra pasta (pen drive, zip) passam a abrir este; nunca numa cópia de teste
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CAST_DADOS")))
                    _ = System.Threading.Tasks.Task.Run(() => { try { Controle.ReapontarAtalhos(PastaBotoes); } catch (Exception ex) { Registro.Erro("atalhos dos botões", ex); } });
                // (0.1.11) versão nova no GitHub: confere 20 s depois de abrir e a cada 6 horas (só avisa; quem atualiza é o operador)
                atuRelogio = new System.Threading.Timer(_ => { _ = Atualizacao.Verificar().ContinueWith(t => { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) Na(() => Enviar(t.Result)); }); }, null, 20000, 6 * 3600 * 1000);
                // (0.1.12) nuvem: o que mudou aqui vai para a nuvem a cada 3 minutos (o que chega de lá, ao abrir ou no ☁ Atualizar)
                nuvemRelogio = new System.Threading.Timer(_ => { if (Nuvem.Ligada && !Nuvem.Ocupada) _ = Nuvem.Sincronizar(false); }, null, 60000, 180000);
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

        // (0.1.12) nuvem
        void NuvemConectar(Func<System.Threading.Tasks.Task<string>> como)
        {
            Enviar(new { tipo = "nuvemMsg", msg = "Conectando…" });
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { var email = await como(); Na(() => { Enviar(new { tipo = "nuvemMsg", msg = "Conectado: " + email + ". Sincronizando…" }); Enviar(Nuvem.Info()); NuvemAgora(true); }); }
                catch (Exception e) { Registro.Erro("nuvem: conectar", e); Na(() => { Enviar(new { tipo = "nuvemMsg", msg = "Não conectou: " + e.Message, erro = true }); Enviar(Nuvem.Info()); }); }
            });
        }
        void NuvemAgora(bool baixar)
        {
            if (!Nuvem.Ligada) { Enviar(Nuvem.Info()); return; }
            Enviar(new { tipo = "nuvemMsg", msg = baixar ? "Conferindo a nuvem…" : "Enviando…" });
            _ = Nuvem.Sincronizar(baixar).ContinueWith(t => Na(() =>
            {
                var r = t.Result;
                var msg = r.Erro != null ? "Não sincronizou: " + r.Erro
                    : r.Recebidos > 0 ? $"☁ {r.Recebidos} arquivo(s) vieram da nuvem" + (r.Enviados > 0 ? $" · {r.Enviados} enviado(s) daqui" : "") + ". Reinicie o Cast para usar."
                    : r.Enviados > 0 ? $"☁ {r.Enviados} arquivo(s) enviado(s) para a nuvem." : "☁ Já estava tudo em dia.";
                Enviar(new { tipo = "nuvemMsg", msg, erro = r.Erro != null, reiniciar = Nuvem.AguardandoReinicio });
                Enviar(Nuvem.Info());
            }));
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
            var c = Capa.Cfg; var agora = DateTime.Now;
            // (0.1.12) cada uma diz se está marcada para transmitir e se é a escolhida (a da capa)
            var prep = Miguel.Preparadas(c, agora); var esc = c.SeguirMiguel ? Miguel.Escolhida(c, prep) : null;
            var marcadas = c.MiguelPreparadas ?? new System.Collections.Generic.List<string>();
            var t = Miguel.Transmitidas(c, agora).Take(12).Select(x => new
            {
                quando = x.Quando.ToString("yyyy-MM-ddTHH:mm"), quandoTxt = Miguel.TextoProxima(x, true), nome = x.Texto, roteiro = x.TemRoteiro,
                chave = Miguel.Chave(x), preparada = marcadas.Contains(Miguel.Chave(x)), escolhida = x == esc
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
            // (0.1.18) o nivelador no máximo por mais de 1,5 min de som: a mesa manda baixo demais
            if (som.Cfg.Nivelar && som.Nivel2.SomBaixo)
                return $"o som está chegando baixo da mesa (o nivelador está no máximo, +{som.Nivel2.MaxSubirDb:0} dB): suba o volume da saída (Aux) que vai para este computador";
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
                // (0.1.17) opções da capa e o nome da paróquia que vem do MiguelAngelus
                modelos = Capa.Modelos.Select(x => new { id = x.id, nome = x.nome }), fundos = Capa.Fundos.Select(x => new { id = x.id, nome = x.nome }),
                letras = Capa.Letras.Select(x => new { id = x.Key, nome = x.Value.Nome, fonte = x.Value.Tit, estilo = x.Value.TitEstilo == System.Drawing.FontStyle.Italic ? "italic" : "normal" }),
                temaDia = Temas.DoDia(c.Quando), paroquiaMiguel = Miguel.Paroquia ?? "",
                arquivo = "capa_" + c.Quando.ToString("yyyy-MM-dd_HHmm") + ".png"
            };
            Enviar(info);
            System.Threading.Tasks.Task.Run(() =>
            {
                string img = null; System.Collections.Generic.Dictionary<string, double[]> caixas = null;
                try { img = Capa.JpegBase64(modo, c, out caixas); } catch (Exception e) { Registro.Erro("prévia da capa", e); }
                if (n != capaPedido || img == null) return;
                // (0.1.19) caixas: onde ficou cada item, para arrastar e mudar o tamanho na prévia
                try { BeginInvoke(new Action(() => { if (n == capaPedido) Enviar(new { tipo = "capaImg", modo, img, caixas }); })); } catch { }
            });
        }

        // ---------- início ----------
        // (0.1.15) gravação à parte dos momentos marcados (a Homilia vem marcada): começa quando o momento vai ao ar e
        // para 4 s depois que vem outro (para não cortar a última frase). Só com a live ou a gravação ligadas.
        int parteMomento = -1; DateTime pararParteEm = DateTime.MaxValue;
        void GravacaoDoMomento()
        {
            int i = roteiro.Agora;
            var m = i >= 0 && i < roteiro.Lista.Count ? roteiro.Lista[i] : null;
            bool quer = m != null && m.GravaAParte && saida.GravarMomentos && (saida.AoVivo || saida.Gravando);
            // (0.1.18) seguindo o MiguelAngelus: o telão já está em outro momento há 45 s (ex.: Creio) e ninguém apertou
            // Transição → a homilia acabou; o arquivo à parte fecha (não grava o resto da Missa nele)
            bool telaoSaiu = quer && roteiro.Seguir && roteiro.TelaoMomento >= 0 && roteiro.TelaoMomento != i && (DateTime.Now - roteiro.TelaoDesde).TotalSeconds > 45;
            if (telaoSaiu && parteMomento == i && saida.ParteNome != null && pararParteEm == DateTime.MaxValue)
                roteiro.AvisoExtra($"🎙 O telão já passou de \"{m.Nome}\": o arquivo à parte foi fechado. Aperte Transição para o próximo momento.");
            if (telaoSaiu) quer = false;
            if (quer)
            {
                pararParteEm = DateTime.MaxValue;
                if (parteMomento != i || saida.ParteNome == null)
                {
                    if (parteMomento == i && saida.ParteNome == null && DateTime.Now < tentouParte.AddSeconds(10)) return;   // não insiste a cada tique
                    parteMomento = i; tentouParte = DateTime.Now;
                    // (0.1.18) a live/gravação começou com o momento já no ar há mais de 1 min: o arquivo diz que começou no meio
                    bool noMeio = (DateTime.Now - roteiro.AgoraDesde).TotalSeconds > 60;
                    var nomeArq = noMeio ? m.Nome + " (começou no meio)" : m.Nome;
                    var e = saida.GravarParte(nomeArq);
                    roteiro.AvisoExtra(e == null ? $"🎙 Gravando \"{m.Nome}\" à parte (Momentos, na pasta de gravações)" + (noMeio ? " — começou no meio: o começo ficou de fora." : ".") : $"🎙 Não deu para gravar \"{m.Nome}\" à parte: {e}.");
                }
            }
            else if (saida.ParteNome != null)
            {
                if (pararParteEm == DateTime.MaxValue) pararParteEm = DateTime.Now.AddSeconds(4);
                else if (DateTime.Now >= pararParteEm) { var nome = saida.ParteNome; pararParteEm = DateTime.MaxValue; parteMomento = -1; saida.GravarParte(null); roteiro.AvisoExtra($"🎙 \"{nome}\" gravado à parte."); }
            }
            else parteMomento = -1;
            VersaoEmPe();
        }
        DateTime tentouParte;

        // (0.1.15) etapa 2: depois da Missa (live e gravação desligadas), a versão em pé dos momentos gravados à parte.
        // Se a live ou a gravação começar, para na hora e continua depois.
        DateTime proxEmPe;
        void VersaoEmPe()
        {
            if (saida.AoVivo || saida.Gravando)
            {
                if (Vertical.Rodando) { Vertical.Parar(); Registro.Info("versão em pé: interrompida (live ou gravação ligada)"); }
                if (Cortes.Rodando) { Cortes.Parar(); Registro.Info("transcrição/corte: interrompido (live ou gravação ligada)"); }
                // (0.1.16) a internet é da transmissão: o envio para a nuvem para e continua depois
                if (Nuvem.Homilias.Rodando) { Nuvem.Homilias.Parar(); Registro.Info("nuvem das homilias: pausada (live ou gravação ligada)"); }
                return;
            }
            if (Vertical.Rodando || Cortes.Rodando || Nuvem.Homilias.Rodando || DateTime.Now < proxEmPe) return;
            proxEmPe = DateTime.Now.AddSeconds(15);
            if (saida.Cfg.FazerEmPe)
            {
                var arq = Vertical.Pendente(saida.Cfg.PastaGravacao, saida.ArquivoParte);
                if (arq != null) { Vertical.Iniciar(arq, CodPosMissa(), () => Na(() => { Enviar(saida.Estado()); EnviarCortesLista(); })); return; }
                // (0.1.15) etapa 3: depois da versão em pé, a transcrição e as sugestões de corte (se o modelo de fala foi baixado)
                var emPe = Cortes.TemModelo ? Cortes.Pendente(saida.Cfg.PastaGravacao) : null;
                if (emPe != null) { Cortes.Iniciar(emPe, () => Na(() => { Enviar(saida.Estado()); EnviarCortesLista(); })); return; }
            }
            // (0.1.16) etapa 4: homilias e cortes para a nuvem (a cada 2 min no máximo; o resto do tempo, só olha a pasta)
            if (Nuvem.Conectado && DateTime.Now >= proxNuvemHom && (Nuvem.Homilias.Pol.EnviarHomilias || Nuvem.Homilias.Pol.EnviarCortes))
            {
                proxNuvemHom = DateTime.Now.AddMinutes(2);
                Nuvem.Homilias.Trabalhar(PastaMomentos, CodPosMissa(), f => saida.Cfg.FazerEmPe && Cortes.Aguardando(f), () => Na(() => { Enviar(saida.Estado()); EnviarNuvemHomilias(); if (Nuvem.Homilias.MateriaisNovos) { Nuvem.Homilias.MateriaisNovos = false; EnviarCortesLista(); } }));
            }
        }
        DateTime proxNuvemHom;
        // (0.1.16) a parte "☁ Na nuvem" da aba Cortes (o que está lá, espaço, política)
        void EnviarNuvemHomilias()
        {
            var r = Nuvem.Homilias.Ultimo; var p = Nuvem.Homilias.Pol;
            bool Local(string nome, bool corte) { try { return File.Exists(corte ? Path.Combine(PastaMomentos, "Cortes", nome) : Path.Combine(PastaMomentos, nome)); } catch { return false; } }
            Enviar(new
            {
                tipo = "nuvemHomilias", conectado = Nuvem.Conectado, email = Nuvem.Homilias.Email, lido = r?.Lido.ToString("dd/MM HH:mm"), erro = r?.Erro, url = r?.Url,
                limiteConta = r?.Limite, usoConta = r?.Uso, usoApp = r?.UsoApp ?? 0, estado = Nuvem.Homilias.Estado, rodando = Nuvem.Homilias.Rodando,
                politica = p,
                homilias = r?.Hom.Where(a => a.Nome.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).Select(a => new
                {
                    nome = a.Nome, tamanho = a.Tamanho, quando = a.Quando.ToString("dd/MM/yyyy HH:mm"), local = Local(a.Nome, false),
                    texto = r.Hom.Any(t => t.Nome.Equals(Path.GetFileNameWithoutExtension(a.Nome) + ".cortes.json", StringComparison.OrdinalIgnoreCase))
                }),
                cortes = r?.Cor.Select(a => new { nome = a.Nome, tamanho = a.Tamanho, quando = a.Quando.ToString("dd/MM/yyyy HH:mm"), local = Local(a.Nome, true) })
            });
        }
        void LerNuvemHomilias() => System.Threading.Tasks.Task.Run(async () => { await Nuvem.Homilias.Ler(); Na(EnviarNuvemHomilias); });
        // (0.1.15) a aba ✂ Cortes toca os vídeos da pasta Momentos por https://momentos.local/ (só leitura, só esta pasta)
        string momentosMapeado;
        void MapearMomentos()
        {
            try
            {
                var cw = web?.CoreWebView2; if (cw == null) return;
                Directory.CreateDirectory(PastaMomentos);
                if (momentosMapeado == PastaMomentos) return;
                if (momentosMapeado != null) cw.ClearVirtualHostNameToFolderMapping("momentos.local");
                cw.SetVirtualHostNameToFolderMapping("momentos.local", PastaMomentos, CoreWebView2HostResourceAccessKind.Allow);
                momentosMapeado = PastaMomentos;
            }
            catch (Exception ex) { Registro.Erro("mapear Momentos", ex); }
        }
        string CodPosMissa() => saida.Cfg.Codificador != "auto" ? saida.Cfg.Codificador : saida.Cfg.CodificadorDetectado ?? "libopenh264";
        string PastaMomentos => Path.Combine(saida.Cfg.PastaGravacao, "Momentos");
        // arquivo pedido pela tela: só nomes de dentro da pasta Momentos
        string NaMomentos(string nome)
        {
            if (string.IsNullOrWhiteSpace(nome) || nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var f = Path.Combine(PastaMomentos, nome); return File.Exists(f) ? f : null;
        }
        void EnviarCortesLista()
        {
            var l = new System.Collections.Generic.List<object>();
            try
            {
                if (Directory.Exists(PastaMomentos))
                    foreach (var f in Directory.GetFiles(PastaMomentos, "*" + Vertical.Sufixo + ".mp4").OrderByDescending(File.GetLastWriteTime))
                    {
                        var t = Cortes.Ler(f);
                        l.Add(new { arquivo = Path.GetFileName(f), nome = Path.GetFileNameWithoutExtension(f).Replace(Vertical.Sufixo, ""), quando = File.GetLastWriteTime(f).ToString("dd/MM HH:mm"), transcrito = t != null, candidatos = t?.Candidatos.Count ?? 0 });
                    }
            }
            catch (Exception ex) { Registro.Erro("lista de cortes", ex); }
            var cortes = new System.Collections.Generic.List<string>();
            try { var pc = Path.Combine(PastaMomentos, "Cortes"); if (Directory.Exists(pc)) cortes = Directory.GetFiles(pc, "*.mp4").Where(x => !x.EndsWith(".parcial.mp4")).OrderByDescending(File.GetLastWriteTime).Select(Path.GetFileName).ToList(); } catch { }
            var yt = YouTube.Shorts;
            Enviar(new { tipo = "cortesLista", videos = l, cortes = cortes.Select(c => new { nome = c, yt = yt.TryGetValue(c, out var id) ? id : null }), ytConectado = YouTube.Conectado, ytEnviando = YouTube.Enviando, musicas = Cortes.Musicas(PastaMomentos), vinhetas = Cortes.Vinhetas(PastaMomentos), temModelo = Cortes.TemModelo, estado = Cortes.Estado, rodando = Cortes.Rodando || Vertical.Rodando });
        }

        DateTime esperaFeita;   // a celebração em que a tela de espera automática já entrou (não repete)
        // (0.1.19) backlog 9.2: gravação da Missa inteira que liga sozinha (Ajustes › Transmissão; desligada de fábrica).
        //  "camera":  quando uma câmera entra no Programa, de 15 min antes até 1h30 depois do horário da celebração;
        //  "horario": GravarAntesMin minutos antes do horário da celebração (até 30 min depois, se o Cast abrir atrasado).
        // O horário é o da capa (Capa.Cfg.Quando: a celebração da agenda do MiguelAngelus, ou o posto à mão na capa): fora
        // dessa janela nunca liga, e cada celebração liga uma vez só (se o operador parar, não volta). Nunca liga a live.
        // Para: com a live, como a opção 1 (Saida.Transmitir); sem live, quando o Encerramento fica 1 min no Programa
        // ou 3 h depois do horário.
        // (a capa passa para a próxima celebração 1h30 depois do horário: o limite usa o horário guardado ao ligar)
        string gravAutoFeita = ""; DateTime gravAutoTique, gravAutoQuando = DateTime.MaxValue, encerramentoDesde = DateTime.MaxValue; bool gravAutoTeveLive;
        void GravacaoAutomatica()
        {
            var agora = DateTime.Now;
            if ((agora - gravAutoTique).TotalMilliseconds < 1000) return;
            gravAutoTique = agora;
            var c = Capa.Cfg; var quando = c.Quando;
            if (saida.GravandoSozinho)
            {
                if (saida.AoVivo) { gravAutoTeveLive = true; encerramentoDesde = DateTime.MaxValue; return; }   // para com a live
                if (gravAutoTeveLive) return;
                var prog = mesa.ProgramaAtual;
                bool fim = prog != null && mesa.Retrato.TryGetValue(prog, out var cn) && cn.fonte == "capa:fim";
                if (!fim) encerramentoDesde = DateTime.MaxValue;
                else if (encerramentoDesde == DateTime.MaxValue) encerramentoDesde = agora;
                string porque = fim && (agora - encerramentoDesde).TotalSeconds >= 60 ? "o Encerramento ficou 1 min no ar"
                    : gravAutoQuando != DateTime.MaxValue && agora > gravAutoQuando.AddHours(3) ? "passaram 3 h do horário da celebração" : null;
                if (porque != null) { saida.Gravar(false); encerramentoDesde = DateTime.MaxValue; Registro.Info("gravação automática parada: " + porque); roteiro.AvisoExtra("⏺ Gravação da Missa parada sozinha: " + porque + "."); }
                return;
            }
            gravAutoTeveLive = false; encerramentoDesde = DateTime.MaxValue;
            var modo = saida.GravarAuto;
            var chave = ChaveCelebracao(quando);
            // gravando à mão perto da celebração: ela já está gravada (parar à mão depois não faz ligar de novo)
            if (saida.Gravando) { if (agora >= quando.AddMinutes(-60) && agora <= quando.AddMinutes(150)) gravAutoFeita = chave; return; }
            if (modo != "camera" && modo != "horario") return;
            if (gravAutoFeita == chave) return;
            // depois do horário só liga por pouco tempo (o Cast reaberto no meio da Missa): 30 min pelo horário, 1h30 pela câmera
            if (agora > quando.AddMinutes(modo == "horario" ? 30 : 90)) return;
            string motivo;
            if (modo == "horario")
            {
                if (agora < quando.AddMinutes(-Math.Clamp(saida.GravarAntesMin, 0, 60))) return;
                motivo = $"horário da celebração ({quando:HH\\hmm}, {saida.GravarAntesMin} min antes)";
            }
            else
            {
                if (agora < quando.AddMinutes(-15)) return;
                var cam = CameraNoPrograma(); if (cam == null) return;
                motivo = $"a câmera \"{cam}\" entrou no Programa (celebração das {quando:HH\\hmm})";
            }
            gravAutoFeita = chave;
            var e = saida.GravarSozinho(motivo);
            gravAutoQuando = e == null ? quando : DateTime.MaxValue;
            roteiro.AvisoExtra(e == null ? "⏺ Gravação da Missa ligada sozinha: " + motivo + "." : "⏺ Não deu para ligar a gravação sozinha: " + e);
        }
        static string ChaveCelebracao(DateTime q) => q.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // nome da cena do Programa se ela mostra uma câmera: a câmera PTZ da cena, uma câmera de Ajustes › Câmeras pela imagem
        // NDI, ou qualquer NDI que não seja do MiguelAngelus (telão/letras); capa, vídeo, imagem, cor e telas não contam
        string CameraNoPrograma()
        {
            var prog = mesa.ProgramaAtual;
            if (prog == null || !mesa.Retrato.TryGetValue(prog, out var cn)) return null;
            var cena = mesa.Cfg.Cenas.FirstOrDefault(x => x.Id == prog);
            if (cena != null && !string.IsNullOrEmpty(cena.Camera) && Cameras.Achar(cena.Camera) != null) return cn.nome;
            if (cn.fonte == null || !cn.fonte.StartsWith("ndi:")) return null;
            var ndi = cn.fonte.Substring(4);
            if (Cameras.Lista.Any(x => x.Fonte == ndi)) return cn.nome;
            return ndi.IndexOf("MiguelAngelus", StringComparison.OrdinalIgnoreCase) < 0 ? cn.nome : null;
        }

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
                    else if (!string.IsNullOrEmpty(fonte)) { partes.Add("falta o IP em Ajustes › Câmeras (não se move)"); if (est == "ok") est = "aviso"; }
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
                // (0.1.12) marcar quais celebrações este Cast vai transmitir e escolher, no Início, a que vai ao ar
                case "miguelPreparar":
                case "miguelEscolher":
                    {
                        var chave = Txt("chave") ?? "";
                        var c = JsonSerializer.Deserialize<CapaCfg>(JsonSerializer.Serialize(Capa.Cfg));
                        bool escolher = m.GetProperty("tipo").GetString() == "miguelEscolher", ocupado = saida.AoVivo || roteiro.Agora >= 0;
                        var antes = Miguel.Escolhida(c, Miguel.Preparadas(c, DateTime.Now));
                        var chaveAntes = antes == null ? "" : Miguel.Chave(antes);
                        if (ocupado && chaveAntes != "" && (escolher ? chave != chaveAntes : chave == chaveAntes && !Sim("sim")))
                        { Aviso("Há uma celebração em andamento: troque a que vai ao ar depois de encerrar."); EnviarMiguel(); break; }
                        // limpa as que já passaram
                        var limite = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
                        c.MiguelPreparadas = (c.MiguelPreparadas ?? new()).Where(k => string.CompareOrdinal(k, limite) > 0).Distinct().ToList();
                        if (escolher)
                        {
                            c.MiguelEscolhida = chave;
                            if (chave != "" && !c.MiguelPreparadas.Contains(chave) && c.MiguelPreparadas.Count > 0) c.MiguelPreparadas.Add(chave);
                        }
                        else if (Sim("sim")) { if (!c.MiguelPreparadas.Contains(chave)) c.MiguelPreparadas.Add(chave); }
                        else { c.MiguelPreparadas.Remove(chave); if (c.MiguelEscolhida == chave) c.MiguelEscolhida = ""; }
                        // no ar ou com o roteiro aberto: marcar outras não tira a atual do ar
                        if (ocupado && chaveAntes != "") { c.MiguelEscolhida = chaveAntes; if (c.MiguelPreparadas.Count > 0 && !c.MiguelPreparadas.Contains(chaveAntes)) c.MiguelPreparadas.Add(chaveAntes); }
                        c.MiguelPreparadas.Sort(StringComparer.Ordinal);
                        Capa.Trocar(c, true); AplicarAgenda(); EnviarMiguel(); EnviarInicio(); if (!ytOcupado) EnviarYt(); break;
                    }
                case "sobrePedir":
                    Enviar(new { tipo = "sobre", versao = Program.Versao, dados = Program.PastaDados, programa = Program.Pasta, runtime = Environment.Version.ToString() });
                    if (Atualizacao.Ultima != null) Enviar(Atualizacao.Ultima);
                    break;
                // ---------- (0.1.11) atualização automática ----------
                case "atualizacaoVerificar":
                    _ = Atualizacao.Verificar().ContinueWith(t => { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) Na(() => Enviar(t.Result)); });
                    break;
                case "atualizacaoInstalar":
                    {
                        if (atualizando) break;
                        if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "atualizacaoMsg", msg = "Pare a transmissão e a gravação antes de atualizar.", erro = true }); break; }
                        var url = Txt("url");
                        atualizando = true;
                        Enviar(new { tipo = "atualizacaoMsg", msg = "Baixando a versão nova…" });
                        long ultimo = 0;
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                var baixado = await Atualizacao.BaixarEConferir(url, (f, t) =>
                                {
                                    var agora = Environment.TickCount64; if (agora - ultimo < 300 && f < t) return; ultimo = agora;
                                    Na(() => Enviar(new { tipo = "atualizacaoMsg", msg = t > 0 ? $"Baixando… {f * 100 / t}% ({f / 1048576} de {t / 1048576} MB)" : $"Baixando… {f / 1048576} MB" }));
                                });
                                Registro.Info("atualização baixada e conferida: " + baixado);
                                Na(() =>
                                {
                                    if (Atualizacao.Teste) { atualizando = false; Enviar(new { tipo = "atualizacaoMsg", msg = "Teste: o instalador baixou e a assinatura conferiu (não instalado)." }); return; }
                                    Enviar(new { tipo = "atualizacaoMsg", msg = "Instalando… o Cast fecha e abre de novo sozinho." });
                                    Atualizacao.Instalar(baixado);
                                    var t = new Timer { Interval = 1500 }; t.Tick += (s2, e2) => { t.Stop(); Close(); }; t.Start();
                                });
                            }
                            catch (Exception ex)
                            {
                                Registro.Erro("atualizar", ex);
                                Na(() => { atualizando = false; Enviar(new { tipo = "atualizacaoMsg", msg = "Não atualizou: " + ex.Message, erro = true }); });
                            }
                        });
                        break;
                    }
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
                    }
                // ---------- (0.1.12) nuvem: a mesma conta Google e a mesma pasta do MiguelAngelus ----------
                case "nuvemPedir": Enviar(Nuvem.Info()); break;
                case "nuvemLigar": Nuvem.Ligar(Sim("sim")); Enviar(Nuvem.Info()); if (Sim("sim")) NuvemAgora(true); break;
                case "nuvemUsarMiguel": NuvemConectar(Nuvem.UsarContaDoMiguel); break;
                case "nuvemConectar": NuvemConectar(Nuvem.Conectar); break;
                case "nuvemCliente":
                    {
                        using var dlg = new OpenFileDialog { Title = "ID do cliente OAuth do Google (o mesmo JSON do MiguelAngelus)", Filter = "JSON do Google|*.json" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        try { Nuvem.DefinirCliente(File.ReadAllText(dlg.FileName)); Enviar(new { tipo = "nuvemMsg", msg = "Arquivo do cliente carregado. Agora clique em Conectar." }); }
                        catch (Exception e) { Enviar(new { tipo = "nuvemMsg", msg = "Não deu: " + e.Message, erro = true }); }
                        Enviar(Nuvem.Info()); break;
                    }
                case "nuvemDesconectar": Nuvem.Desconectar(); Enviar(Nuvem.Info()); Enviar(new { tipo = "nuvemMsg", msg = "Desconectado. Os ajustes deste computador continuam aqui; os da nuvem também." }); break;
                case "nuvemAgora": NuvemAgora(true); break;
                case "nuvemReiniciar":
                    if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "nuvemMsg", msg = "Pare a transmissão e a gravação antes de reiniciar.", erro = true }); break; }
                    Registro.Info("nuvem: reiniciando para usar o que veio da nuvem");
                    Application.Restart(); Environment.Exit(0); break;                // ---------- joysticks ----------
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
                        // (0.1.11) e as páginas oficiais para baixar o programa do Stream Dock e do Stream Deck
                        if (u == "https://pascom-sjo.github.io/" || u == "mailto:contato.miguelangelus@gmail.com" || u.StartsWith("https://www.youtube.com/@SaoJos")
                            || u == "https://mirabox.net/pages/download" || u == "https://www.elgato.com/s/downloads"
                            // (0.1.16) a página do Google Cloud para ativar a API do YouTube (vem na mensagem de erro)
                            || System.Text.RegularExpressions.Regex.IsMatch(u, @"^https://console\.(developers|cloud)\.google\.com/[A-Za-z0-9/._?=&%-]*$"))
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
                case "capaSugestoes":
                    {
                        // (0.1.17) 6 combinações que combinam, desenhadas pequenas com os textos de verdade
                        int semente = m.TryGetProperty("semente", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt32() : Environment.TickCount;
                        var baseCfg = Capa.Cfg;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                var itens = Capa.Sugerir(baseCfg, 6, semente).Select(e => new { estilo = e, img = Capa.JpegBase64("capa", Capa.ComEstilo(baseCfg, e), 480, 270) }).ToList();
                                BeginInvoke(new Action(() => Enviar(new { tipo = "capaSug", itens })));
                            }
                            catch (Exception e) { Registro.Erro("sugestões da capa", e); }
                        });
                        break;
                    }
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
                case "gravarMomentos": saida.GravarMomentos = Sim("on"); break;
                case "gravarComLive": saida.GravarComLive = Sim("on"); break;
                // (0.1.19) nunca / com a live / 1ª câmera no Programa / horário (com os minutos de antecedência)
                case "gravarAuto":
                    if (m.TryGetProperty("antes", out var vAntes) && vAntes.ValueKind == JsonValueKind.Number) saida.GravarAntesMin = (int)Math.Round(vAntes.GetDouble());
                    if (Txt("modo") != null) saida.GravarAuto = Txt("modo");
                    break;
                case "fazerEmPe": saida.Cfg.FazerEmPe = Sim("on"); saida.Salvar(); if (!saida.Cfg.FazerEmPe) Vertical.Parar(); Enviar(saida.Estado()); break;
                // (0.1.15) versão em pé de qualquer vídeo gravado (ex.: a Missa baixada do YouTube Studio), com trecho opcional
                case "emPeArquivo":
                    {
                        if (saida.AoVivo || saida.Gravando) { Aviso("A versão em pé roda com a live e a gravação desligadas (o computador é da transmissão)."); break; }
                        if (Vertical.Rodando) { Aviso("Já está fazendo uma versão em pé; espere terminar."); break; }
                        double ini = m.TryGetProperty("ini", out var vi) && vi.ValueKind == JsonValueKind.Number ? vi.GetDouble() : -1;
                        double fim = m.TryGetProperty("fim", out var vf) && vf.ValueKind == JsonValueKind.Number ? vf.GetDouble() : -1;
                        if (fim > 0 && ini > 0 && fim <= ini) { Aviso("O fim do trecho precisa ser depois do início."); break; }
                        using var od = new OpenFileDialog { Title = "Vídeo para a versão em pé", Filter = "Vídeos|*.mp4;*.mkv;*.mov;*.webm;*.ts;*.flv|Todos|*.*" };
                        if (od.ShowDialog(this) != DialogResult.OK) break;
                        var pm = Path.Combine(saida.Cfg.PastaGravacao, "Momentos"); Directory.CreateDirectory(pm);
                        string Hm(double s) => s < 0 ? "" : $"{(int)(s / 3600)}h{(int)(s % 3600 / 60):00}m{(int)(s % 60):00}";
                        var trecho = ini > 0 || fim > 0 ? $" {(ini > 0 ? Hm(ini) : "início")}-{(fim > 0 ? Hm(fim) : "fim")}" : "";
                        var dest = Path.Combine(pm, Path.GetFileNameWithoutExtension(od.FileName) + trecho + Vertical.Sufixo + ".mp4");
                        var cod = saida.Cfg.Codificador != "auto" ? saida.Cfg.Codificador : saida.Cfg.CodificadorDetectado ?? "libopenh264";
                        int atrasoV = m.TryGetProperty("atraso", out var va) && va.ValueKind == JsonValueKind.Number ? Math.Clamp((int)va.GetDouble(), -5000, 5000) : 0;
                        Vertical.Iniciar(od.FileName, cod, () => Na(() => Enviar(saida.Estado())), ini, fim, dest, atrasoV);
                        Enviar(saida.Estado());
                        break;
                    }
                // (0.1.15) etapa 3: aba ✂ Cortes
                case "cortesLista":
                    MapearMomentos(); EnviarCortesLista(); EnviarNuvemHomilias();
                    if (Nuvem.Conectado && !Nuvem.Homilias.Rodando && (Nuvem.Homilias.Ultimo == null || (DateTime.Now - Nuvem.Homilias.Ultimo.Lido).TotalMinutes > 5)) LerNuvemHomilias();
                    break;
                case "cortesAbrir":
                    {
                        var f = NaMomentos(Txt("arquivo")); var t = f == null ? null : Cortes.Ler(f);
                        Enviar(new { tipo = "cortesTexto", arquivo = Txt("arquivo"), falas = t?.Falas, candidatos = t?.Candidatos, quando = t?.Quando });
                        break;
                    }
                case "corteFala":
                    {
                        var f = NaMomentos(Txt("arquivo")); var t = f == null ? null : Cortes.Ler(f); int i = Num("i", -1);
                        if (t != null && i >= 0 && i < t.Falas.Count && Txt("texto") is string novoTexto) { t.Falas[i].T = novoTexto.Trim(); Cortes.Gravar(f, t); }
                        break;
                    }
                case "corteGerar":
                    {
                        var f = NaMomentos(Txt("arquivo"));
                        if (f == null) { Enviar(new { tipo = "cortesMsg", texto = "Vídeo não encontrado na pasta Momentos." }); break; }
                        if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "cortesMsg", texto = "Os cortes são feitos com a live e a gravação desligadas." }); break; }
                        if (Vertical.Rodando || Cortes.Rodando) { Enviar(new { tipo = "cortesMsg", texto = "Espere terminar: " + Vertical.Estado }); break; }
                        double ini = m.TryGetProperty("ini", out var gi) ? gi.GetDouble() : 0, fim = m.TryGetProperty("fim", out var gf) ? gf.GetDouble() : 0;
                        var mus = Txt("musica"); string musArq = null;
                        if (!string.IsNullOrWhiteSpace(mus) && mus.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) { var mf = Path.Combine(Cortes.PastaMusicas(PastaMomentos), mus); if (File.Exists(mf)) musArq = mf; }
                        string Vinheta(string campo) { var v = Txt(campo); if (string.IsNullOrWhiteSpace(v) || v.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null; var vf = Path.Combine(Cortes.PastaVinhetas(PastaMomentos), v); return File.Exists(vf) ? vf : null; }
                        Cortes.Gerar(f, ini, fim, CodPosMissa(), (saidaArq, erro) => Na(() => { EnviarCortesLista(); if (saidaArq != null) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + saidaArq + "\""); }), () => Na(() => { Enviar(saida.Estado()); EnviarCortesLista(); }), musArq, Num("volume", 15), Vinheta("abertura"), Vinheta("encerramento"));
                        break;
                    }
                case "cortesModelo":
                    Cortes.BaixarModelo(() => Na(() => { Enviar(saida.Estado()); EnviarCortesLista(); }));
                    EnviarCortesLista();
                    break;
                case "cortesMusicasPasta":
                    try { var pmu = Cortes.PastaMusicas(PastaMomentos); Directory.CreateDirectory(pmu); System.Diagnostics.Process.Start("explorer.exe", "\"" + pmu + "\""); } catch (Exception ex) { Registro.Erro("abrir músicas", ex); }
                    break;
                case "cortesVinhetasPasta":
                    try { var pv = Cortes.PastaVinhetas(PastaMomentos); Directory.CreateDirectory(pv); System.Diagnostics.Process.Start("explorer.exe", "\"" + pv + "\""); } catch (Exception ex) { Registro.Erro("abrir vinhetas", ex); }
                    break;
                // (0.1.16) parte 4: o corte como Short PRIVADO no YouTube (a equipe confere e publica no Studio)
                case "corteYouTube":
                    {
                        var n = Txt("nome"); string arq = null;
                        if (!string.IsNullOrWhiteSpace(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) { var f = Path.Combine(PastaMomentos, "Cortes", n); if (File.Exists(f)) arq = f; }
                        if (arq == null) { Enviar(new { tipo = "cortesMsg", texto = "Corte não encontrado na pasta Cortes." }); break; }
                        if (!YouTube.Conectado) { Enviar(new { tipo = "cortesMsg", texto = "Conecte a conta do YouTube em Ajustes › Transmissão › Conta do YouTube." }); break; }
                        if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "cortesMsg", texto = "O envio para o YouTube espera a live e a gravação desligarem." }); break; }
                        if (YouTube.Enviando) { Enviar(new { tipo = "cortesMsg", texto = "Já está enviando um corte; espere terminar." }); break; }
                        var titulo = (Txt("titulo") ?? "").Trim(); var desc = Txt("descricao") ?? "";
                        if (titulo == "") { Enviar(new { tipo = "cortesMsg", texto = "Escreva um título." }); break; }
                        var codYt = CodPosMissa();
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            string msg;
                            try
                            {
                                // o vídeo vai com a capa no 1º segundo (o feed de Shorts mostra o primeiro quadro)
                                var envio = arq;
                                if (File.Exists(CapaCorte.Arquivo(arq)))
                                {
                                    Na(() => Enviar(new { tipo = "cortesMsg", texto = "▶ pondo a capa no 1º segundo do vídeo…" }));
                                    try { envio = CapaCorte.ComCapa(arq, codYt); } catch (Exception ex) { Registro.Erro("corte com capa", ex); envio = arq; }
                                }
                                var id = await YouTube.EnviarCorte(envio, titulo, desc, p => Na(() => Enviar(new { tipo = "cortesMsg", texto = $"▶ enviando para o YouTube… {p}%" })));
                                msg = "✔ No YouTube como PRIVADO. Confira no Studio e publique quando o padre aprovar.";
                                // a capa vai como miniatura (canal precisa ser verificado; nos Shorts o YouTube pode mostrar um quadro do vídeo no feed)
                                var capa = CapaCorte.Arquivo(arq);
                                if (File.Exists(capa))
                                    try { await YouTube.EnviarMiniatura(id, File.ReadAllBytes(capa)); msg += " A capa foi junto."; }
                                    catch (Exception ex) { msg += " A capa não foi: " + ex.Message; Registro.Erro("YouTube: capa do corte", ex); }
                                Na(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://studio.youtube.com/video/" + id + "/edit") { UseShellExecute = true }));
                            }
                            catch (Exception ex) { msg = "Não deu para enviar ao YouTube: " + ex.Message; Registro.Erro("YouTube: enviar corte", ex); }
                            Na(() => { Enviar(new { tipo = "cortesMsg", texto = msg }); EnviarCortesLista(); });
                        });
                        Enviar(new { tipo = "cortesMsg", texto = "▶ preparando o envio para o YouTube…" });
                        EnviarCortesLista();
                        break;
                    }
                // (0.1.16) capa do corte (brasão, frase, data, fundo do encerramento): sai sozinha ao abrir "Publicar…"
                case "corteCapa":
                    {
                        var n = Txt("nome"); string arq = null;
                        if (!string.IsNullOrWhiteSpace(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) { var f = Path.Combine(PastaMomentos, "Cortes", n); if (File.Exists(f)) arq = f; }
                        if (arq == null) break;
                        var pm = PastaMomentos;
                        var v = Txt("encerramento"); string enc = null;
                        if (!string.IsNullOrWhiteSpace(v) && v.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) { var vf = Path.Combine(Cortes.PastaVinhetas(pm), v); if (File.Exists(vf)) enc = vf; }
                        string fraseIn = Txt("frase"), dataIn = Txt("data") ?? "";   // lidos aqui: a mensagem não vive até a tarefa
                        bool padrao = fraseIn == null;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                var frase = padrao ? CapaCorte.FrasePadrao(arq, pm) : fraseIn;
                                var data = padrao ? CapaCorte.DataTexto(CapaCorte.DataPadrao(arq, pm)) : dataIn;
                                var capa = CapaCorte.Arquivo(arq);
                                if (!padrao || !File.Exists(capa)) CapaCorte.Gerar(arq, frase, data, enc);
                                var url = "https://momentos.local/Cortes/" + Uri.EscapeDataString(Path.GetFileName(capa)) + "?v=" + File.GetLastWriteTime(capa).Ticks;
                                Na(() => Enviar(new { tipo = "corteCapa", nome = n, frase = padrao ? frase : null, data = padrao ? data : null, url }));
                            }
                            catch (Exception ex) { Registro.Erro("capa do corte", ex); Na(() => Enviar(new { tipo = "cortesMsg", texto = "Não deu para fazer a capa: " + ex.Message })); }
                        });
                        break;
                    }
                case "corteYouTubeAbrir":
                    {
                        var n = Txt("nome");
                        if (n != null && YouTube.Shorts.TryGetValue(n, out var id) && System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{6,20}$"))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://studio.youtube.com/video/" + id + "/edit") { UseShellExecute = true });
                        break;
                    }
                // (0.1.16) parte 5: publicar à mão no Instagram/Facebook (Meta Business Suite) ou no YouTube Studio:
                // copia a legenda, abre a página e mostra o arquivo na pasta (é só arrastar)
                case "cortePublicar":
                    {
                        var n = Txt("nome"); string arq = null;
                        if (!string.IsNullOrWhiteSpace(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) { var f = Path.Combine(PastaMomentos, "Cortes", n); if (File.Exists(f)) arq = f; }
                        if (arq == null) { Enviar(new { tipo = "cortesMsg", texto = "Corte não encontrado na pasta Cortes." }); break; }
                        var legenda = Txt("legenda") ?? "";
                        try { if (legenda.Trim() != "") Clipboard.SetText(legenda); } catch (Exception ex) { Registro.Erro("copiar legenda", ex); }
                        var url = Txt("onde") == "youtube" ? "https://studio.youtube.com/" : "https://business.facebook.com/latest/reels_composer";
                        var codPub = CodPosMissa();
                        var copiou = legenda.Trim() != "" ? "Legenda copiada (Ctrl+V). " : "";
                        if (File.Exists(CapaCorte.Arquivo(arq))) Enviar(new { tipo = "cortesMsg", texto = "Pondo a capa no 1º segundo do vídeo…" });
                        // o vídeo com a capa no 1º segundo (refeito só se a capa ou o corte mudaram)
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            var envio = arq;
                            try { envio = CapaCorte.ComCapa(arq, codPub); } catch (Exception ex) { Registro.Erro("corte com capa", ex); }
                            Na(() =>
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + envio + "\"");
                                }
                                catch (Exception ex) { Registro.Erro("publicar corte", ex); }
                                Enviar(new { tipo = "cortesMsg", texto = copiou + "Arraste o vídeo da pasta que abriu para a página" + (envio != arq ? " (já com a capa no 1º segundo)." : ".") });
                            });
                        });
                        break;
                    }
                // (0.1.16) ☁ homilias e cortes na nuvem
                case "nuvemHomLer":
                    if (!Nuvem.Conectado) { EnviarNuvemHomilias(); break; }
                    LerNuvemHomilias(); break;
                case "nuvemHomAbrir":
                    {
                        var u = Nuvem.Homilias.Ultimo?.Url;
                        if (u != null && u.StartsWith("https://drive.google.com/")) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                        else Enviar(new { tipo = "cortesMsg", texto = "Clique em ↻ Atualizar primeiro (a pasta ainda não foi lida)." });
                        break;
                    }
                case "nuvemHomBaixar":
                    {
                        var n = Txt("nome");
                        if (string.IsNullOrWhiteSpace(n) || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) break;
                        Nuvem.Homilias.Baixar(n, Sim("corte"), PastaMomentos, () => Na(() => { Enviar(saida.Estado()); EnviarNuvemHomilias(); EnviarCortesLista(); }));
                        EnviarNuvemHomilias();
                        break;
                    }
                case "nuvemHomLiberar":
                    Nuvem.Homilias.LiberarEspaco(() => Na(() => { Enviar(saida.Estado()); EnviarNuvemHomilias(); }));
                    break;
                case "nuvemHomEnviarAgora":
                    if (saida.AoVivo || saida.Gravando) { Enviar(new { tipo = "cortesMsg", texto = "O envio para a nuvem espera a live e a gravação desligarem." }); break; }
                    proxNuvemHom = DateTime.MinValue; proxEmPe = DateTime.MinValue;
                    break;
                case "nuvemHomPolitica":
                    {
                        var p = new Nuvem.Homilias.Politica
                        {
                            EnviarHomilias = Sim("enviarHomilias"), EnviarCortes = Sim("enviarCortes"),
                            LimiteGb = m.TryGetProperty("limiteGb", out var lg) && lg.ValueKind == JsonValueKind.Number ? lg.GetDouble() : 20,
                            DiasHomilias = Num("diasHomilias", 30), DiasCortes = Num("diasCortes", 90)
                        };
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            var erro = await Nuvem.Homilias.SalvarPolitica(p);
                            Na(() => { Enviar(new { tipo = "cortesMsg", texto = erro == null ? "✔ Política salva na nuvem (vale para todos os computadores conectados)." : "Política salva só neste computador: " + erro }); EnviarNuvemHomilias(); });
                        });
                        break;
                    }
                case "cortesPasta":
                    try { var pc = Path.Combine(PastaMomentos, "Cortes"); Directory.CreateDirectory(pc); System.Diagnostics.Process.Start("explorer.exe", "\"" + pc + "\""); } catch (Exception ex) { Registro.Erro("abrir Cortes", ex); }
                    break;
                case "abrirMomentos":
                    try { var pm = Path.Combine(saida.Cfg.PastaGravacao, "Momentos"); Directory.CreateDirectory(pm); System.Diagnostics.Process.Start("explorer.exe", "\"" + pm + "\""); } catch (Exception ex) { Registro.Erro("abrir Momentos", ex); }
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
                        var id = Txt("id"); var c = Cameras.Achar(Txt("camera")); var fim = Sim("fim");
                        ViscaRede.LerPosicao(c).ContinueWith(t => Na(() =>
                        {
                            var (p, erro) = t.Result;
                            if (p != null) mesa.GravarPtz(id, p, fim);
                            Enviar(new { tipo = "camCenaMsg", id, texto = p != null ? (fim ? $"Ponto final salvo ({p.Texto()}). Quando a cena entrar no ar, a câmera anda até aqui." : $"Posição salva na cena ({p.Texto()}). A câmera não guardou nada.") : "Não deu para salvar: " + erro + ".", erro = p == null });
                        }));
                        break;
                    }
                case "camCenaEsquecer": mesa.GravarPtz(Txt("id"), null, Sim("fim")); break;
                case "camCenaIr":
                    {
                        var fim = Sim("fim");
                        var e = roteiro.IrPosicaoDaCena(Txt("id"), fim);
                        Enviar(new { tipo = "camCenaMsg", id = Txt("id"), texto = e == null ? (fim ? "Câmera indo para o ponto final." : "Câmera indo para a posição salva na cena.") : "Não deu: " + e + ".", erro = e != null });
                        break;
                    }
                // (0.1.14) movimento programado da cena
                case "camCenaMov": mesa.DefinirMovimento(Txt("id"), Num("vel", 2), Sim("vaiVolta")); break;
                case "camCenaTestar":
                    {
                        var e = roteiro.IniciarMovimento(Txt("id"), true);
                        Enviar(new { tipo = "camCenaMsg", id = Txt("id"), texto = e == null ? "Testando: a câmera vai ao ponto inicial e anda até o ponto final (■ Parar interrompe)." : "Não deu: " + e + ".", erro = e != null });
                        break;
                    }
                case "camCenaParar":
                    {
                        MovimentoPtz.PararCena(Txt("id"));
                        var cam = Cameras.Achar(mesa.CenaPara(Txt("id"))?.Camera);
                        if (cam != null) { _ = ViscaRede.Enviar(cam, new byte[] { 0x81, 0x01, 0x06, 0x01, 0x01, 0x01, 0x03, 0x03, 0xFF }); _ = ViscaRede.Enviar(cam, new byte[] { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF }); }
                        Enviar(new { tipo = "camCenaMsg", id = Txt("id"), texto = "Movimento parado.", erro = false });
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
