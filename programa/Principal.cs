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
                relogio.Stop(); relogioSom.Stop(); relogioRastreio.Stop(); joystick?.Dispose(); try { rastreio?.Dispose(); } catch { } saida.Dispose(); som.Dispose(); mesaSom.Dispose(); mesa.Dispose(); Registro.Info("fechou");
            };
        }

        async System.Threading.Tasks.Task Iniciar()
        {
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
                        niv = som.Cfg.Nivelar, nivGanho = Math.Round(som.Nivel2.GanhoDb, 1), lufs = Math.Round(som.Nivel2.LufsSaida, 1), lim = Math.Round(som.Nivel2.ReducaoLimitadorDb, 1), atraso = som.Cfg.AtrasoMs, abaixado = som.Abaixado });
                };
                relogioSom.Start();
                saida.Mudou += () => Na(() => Enviar(saida.Estado()));
                mesa.Iniciar(monitores.Buffer);

                relogio.Tick += (s, e) => { Enviar(new { tipo = "numeros", qps = Math.Round(mesa.QpsReal, 1), ms = Math.Round(mesa.MsQuadro, 1), atrasos = mesa.Atrasos }); Enviar(saida.Estado()); };
                relogio.Start();
                roteiro = new Roteiro(mesa, PrepararRastreio, () => rastreio);
                roteiro.Mudou += () => { if (rotPendente) return; rotPendente = true; Na(() => { rotPendente = false; EnviarRoteiro(null); }); };
                relogioRastreio.Tick += (s, e) => { EnviarRastreio(false); try { roteiro.Tick(); EsperaAutomatica(); } catch (Exception ex) { Registro.Erro("roteiro", ex); } };
                relogioRastreio.Start();
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

        void Aviso(string erro) { if (erro != null) Enviar(new { tipo = "aviso", texto = erro }); Enviar(saida.Estado()); }

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
                perfil = roteiro.Perfil.Id, lista = roteiro.Lista, alterado = roteiro.Alterado, agora = roteiro.Agora, aviso = roteiro.Aviso, msg
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
                        JsonSerializer.Serialize(new { tipo = "monitores", l = Mesa.ML, a = Mesa.MA }));
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
                case "rotVideo":
                    using (var dlg = new OpenFileDialog { Title = "Vídeo do roteiro", Filter = "Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts" })
                        if (dlg.ShowDialog(this) == DialogResult.OK) Enviar(new { tipo = "rotVideoEscolhido", i = Num("i", -1), caminho = dlg.FileName, noFim = Sim("noFim") });
                    break;
                // ---------- câmeras e PTZ ----------
                // ---------- rede, backup e sobre ----------
                case "redePedir": EnviarRede(Sim("internet")); break;
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
                        using var dlg = new SaveFileDialog { Title = "Exportar a capa", Filter = "PNG|*.png", FileName = "capa_" + Capa.Cfg.Quando.ToString("yyyy-MM-dd_HHmm") + (modo == "capa" ? "" : "_" + modo) + ".png" };
                        if (dlg.ShowDialog(this) != DialogResult.OK) break;
                        try { File.WriteAllBytes(dlg.FileName, Capa.Png(modo)); EnviarCapa(modo, "Salvo: " + Path.GetFileName(dlg.FileName) + " (1920×1080)."); }
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
                case "preview": mesa.PorNoPreview(Txt("id")); break;
                case "transicao": mesa.Transicao(); break;
                case "direto": mesa.Direto(Txt("id")); break;
                case "efeito": mesa.Efeito(Txt("efeito"), Num("ms", 500)); break;
                case "novaCena": mesa.NovaCena(Txt("nome"), Txt("fonte"), Sim("preencher")); break;
                case "apagarCena": mesa.ApagarCena(Txt("id")); break;
                case "mover": mesa.Mover(Txt("id"), Num("delta", 0)); break;
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
