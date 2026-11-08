// MiguelAngelus Cast 0.1 — kit de testes
// Vê a câmera PTZ por NDI, acha e reconhece o rosto cadastrado e move a câmera por VISCA (ou simula),
// gravando tudo em números na pasta "telemetria". Nunca grava imagem nem rosto: o cadastro fica só na memória.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

class Janela : Form
{
    // linha 1: vídeo e rosto
    readonly ComboBox cbFontes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly CheckBox cLeve = new CheckBox { Text = "Vídeo leve", AutoSize = true, Checked = true };
    readonly Button bAtualizar = new Button { Text = "Atualizar lista", AutoSize = true };
    readonly Button bConectar = new Button { Text = "Conectar vídeo", AutoSize = true };
    readonly Button bCadastrar = new Button { Text = "Cadastrar rosto", AutoSize = true };
    readonly Button bApagar = new Button { Text = "Apagar cadastro", AutoSize = true };
    readonly CheckBox cSeguir = new CheckBox { Text = "Seguir", AutoSize = true, Checked = true };
    readonly CheckBox cAnalisar = new CheckBox { Text = "Analisar", AutoSize = true, Checked = true };
    // linha 2: câmera PTZ
    readonly TextBox tIp = new TextBox { Text = "192.168.100.190", Width = 110 };
    readonly NumericUpDown nPorta = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 5678, Width = 65 };
    readonly CheckBox cReal = new CheckBox { Text = "Mover a câmera de verdade", AutoSize = true, ForeColor = Color.DarkRed };
    readonly NumericUpDown nPan = new NumericUpDown { Minimum = 1, Maximum = 24, Value = 8, Width = 45 };
    readonly NumericUpDown nTilt = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 6, Width = 45 };
    readonly NumericUpDown nZona = new NumericUpDown { Minimum = 2, Maximum = 40, Value = 14, Width = 45 };
    readonly CheckBox cInverter = new CheckBox { Text = "Câmera de cabeça para baixo", AutoSize = true };
    readonly Button bTeste = new Button { Text = "Teste de resposta", AutoSize = true };
    readonly Button bParar = new Button { Text = "PARAR CÂMERA", AutoSize = true, BackColor = Color.Firebrick, ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    readonly Button bPasta = new Button { Text = "Abrir telemetria", AutoSize = true };
    readonly Button bPacote = new Button { Text = "Gerar pacote (.zip)", AutoSize = true };

    readonly PictureBox pbQuadro = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Dock = DockStyle.Fill };
    readonly PictureBox pbCamera = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Dock = DockStyle.Fill };
    readonly Label lInfo = new Label { AutoSize = false, Height = 76, Dock = DockStyle.Bottom, Font = new Font("Consolas", 10f), Padding = new Padding(8, 4, 8, 4) };

    readonly Telemetria tel = new Telemetria();
    readonly Visca visca;
    Receptor receptor; Rostos rostos; Thread proc; volatile bool rodando = true;
    System.Threading.Timer vigia;

    // ajustes lidos pela análise (copiados da tela)
    volatile bool real, seguir = true, analisar = true, inverter;
    volatile int maxPan = 8, maxTilt = 6, zonaPct = 14;

    // estado da análise
    float[] cadastro; int coletar; readonly List<float[]> coletados = new List<float[]>();
    Rosto alvo; DateTime vistoEm, verificadoEm; string estado = "sem alvo";
    volatile bool pedirApagar; PointF? clique; volatile int largura, altura;
    float vx = float.NaN, vy; bool movX, movY;
    int[] ultimoCmd = { 0, 0, 3, 3 }; double cmdEm; volatile bool movendo; double analisadoEm;
    volatile bool emTeste; float[] cinzaAntes;
    DateTime conectouEm; bool trocouParaCompleto;
    const float Limiar = 0.40f;
    int falhas;                                   // confirmações ruins seguidas (só larga com 3)
    float exAnt = float.NaN, eyAnt, dEx, dEy;     // quanto o erro está mudando (prevê para onde a pessoa vai)
    const float Previsao = 0.30f;
    int ladoX, ladoY;                             // de que lado do centro o rosto estava                 // segundos à frente (atraso medido câmera+NDI ~0,2-0,26 s)

    // rosto bom para conferir: grande o bastante, nítido e de frente (nariz entre os olhos)
    static bool BomParaConferir(Rosto r)
    {
        if (r.W < 20 || r.Nota < 0.8f) return false;
        float olhoX = (r.Pontos[0] + r.Pontos[2]) / 2, dist = Math.Abs(r.Pontos[2] - r.Pontos[0]);
        return dist > 1 && Math.Abs(r.Pontos[4] - olhoX) / dist < 0.35f;
    }

    public Janela()
    {
        Text = "MiguelAngelus Cast 0.1 — kit de testes (rastreio PTZ + telemetria)"; Width = 1400; Height = 720; StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        visca = new Visca(t => { tel.Evento(t); Status(t); });
        var topo = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(6, 4, 6, 0), WrapContents = true };
        Label L(string s) => new Label { Text = s, AutoSize = true, Padding = new Padding(4, 7, 0, 0) };
        topo.Controls.AddRange(new Control[] { L("Câmera NDI:"), cbFontes, cLeve, bAtualizar, bConectar, L("  |"), bCadastrar, bApagar, cSeguir, cAnalisar });
        topo.SetFlowBreak(cAnalisar, true);
        topo.Controls.AddRange(new Control[] { L("PTZ IP:"), tIp, L("porta"), nPorta, cReal, L("vel. pan máx"), nPan, L("tilt máx"), nTilt, L("zona morta %"), nZona, cInverter, bTeste, bParar, bPasta, bPacote });
        var meio = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        meio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); meio.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        meio.Controls.Add(pbQuadro, 0, 0); meio.Controls.Add(pbCamera, 1, 0);
        Controls.Add(meio); Controls.Add(topo); Controls.Add(lInfo);

        var erro = NdiRx.Iniciar();
        tel.Evento(erro ?? "NDI iniciado");
        lInfo.Text = erro ?? "Procurando câmeras NDI na rede…";
        try { rostos = new Rostos(Program.PastaModelos, 2); } catch (Exception e) { MessageBox.Show("Não carregou os modelos de rosto:\n" + e.Message); tel.Evento("ERRO modelos: " + e); }

        bAtualizar.Click += (s, e) => Atualizar();
        bConectar.Click += (s, e) => Conectar(cLeve.Checked);
        bCadastrar.Click += (s, e) =>
        {
            if (MessageBox.Show("A pessoa que vai ser cadastrada AUTORIZOU usar o rosto dela neste teste?\n\nO rosto fica só na memória deste programa: some ao fechar ou em \"Apagar cadastro\". Nada de imagem ou rosto é gravado na telemetria.",
                "Autorização", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            coletados.Clear(); coletar = 12; tel.Evento("Cadastro iniciado (pessoa autorizou)");
        };
        bApagar.Click += (s, e) => { pedirApagar = true; tel.Evento("Cadastro apagado"); };
        pbQuadro.MouseClick += (s, e) => clique = ParaQuadro(e.Location);
        cSeguir.CheckedChanged += (s, e) => { seguir = cSeguir.Checked; tel.Evento("Seguir: " + seguir); };
        cAnalisar.CheckedChanged += (s, e) => { analisar = cAnalisar.Checked; tel.Evento("Analisar: " + analisar); };
        cInverter.CheckedChanged += (s, e) => { inverter = cInverter.Checked; tel.Evento("Câmera invertida: " + inverter); };
        EventHandler aj = (s, e) => { maxPan = (int)nPan.Value; maxTilt = (int)nTilt.Value; zonaPct = (int)nZona.Value; tel.Evento($"Ajustes: pan máx {maxPan}, tilt máx {maxTilt}, zona morta {zonaPct}%"); };
        nPan.ValueChanged += aj; nTilt.ValueChanged += aj; nZona.ValueChanged += aj;
        cReal.CheckedChanged += (s, e) => LigarReal();
        bParar.Click += (s, e) => { cReal.Checked = false; visca.Parar(); tel.Evento("PARAR CÂMERA apertado"); };
        bTeste.Click += (s, e) => TesteResposta();
        bPasta.Click += (s, e) => Process.Start("explorer.exe", "\"" + tel.Pasta + "\"");
        bPacote.Click += (s, e) => { try { var z = tel.Empacotar(); Process.Start("explorer.exe", "/select,\"" + z + "\""); } catch (Exception x) { MessageBox.Show(x.Message); } };

        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (s, e) => Rotina(); t.Start();
        vigia = new System.Threading.Timer(_ => Vigiar(), null, 250, 250);
        proc = new Thread(Processar) { IsBackground = true, Name = "analise" }; proc.Start();
    }

    void Status(string s) { try { BeginInvoke((Action)(() => Text = "MiguelAngelus Cast 0.1 — " + s)); } catch { } }

    List<KeyValuePair<string, NdiRx.Fonte>> fontes = new List<KeyValuePair<string, NdiRx.Fonte>>();
    void Atualizar()
    {
        fontes = NdiRx.Fontes();
        var sel = cbFontes.SelectedItem as string;
        cbFontes.Items.Clear(); foreach (var f in fontes) cbFontes.Items.Add(f.Key);
        if (sel != null && cbFontes.Items.Contains(sel)) cbFontes.SelectedItem = sel; else if (cbFontes.Items.Count > 0) cbFontes.SelectedIndex = 0;
    }

    void Conectar(bool leve)
    {
        if (cbFontes.SelectedIndex < 0) return;
        receptor?.Dispose();
        receptor = new Receptor(fontes[cbFontes.SelectedIndex].Value, leve);
        conectouEm = DateTime.Now; trocouParaCompleto = !leve; vx = float.NaN;
        tel.Evento($"Vídeo conectado: \"{cbFontes.SelectedItem}\" ({(leve ? "leve" : "completo")})");
    }

    // a cada segundo: lista de fontes, e se o vídeo leve não chegar, troca para o completo
    void Rotina()
    {
        if (receptor == null) { Atualizar(); return; }
        if (!trocouParaCompleto && largura == 0 && (DateTime.Now - conectouEm).TotalSeconds > 4)
        {
            tel.Evento("Vídeo leve não chegou em 4 s: trocando para o completo");
            Conectar(false);
        }
        tel.Salvar();
    }

    void LigarReal()
    {
        if (cReal.Checked)
        {
            if (!visca.Conectar(tIp.Text.Trim(), (int)nPorta.Value)) { cReal.Checked = false; MessageBox.Show("Não conectou na câmera (veja a telemetria). Confira IP e porta, e se outro programa não está ocupando a conexão."); return; }
            real = true; tel.Evento("Câmera de verdade: LIGADO");
        }
        else { real = false; visca.Parar(); visca.Desconectar(); tel.Evento("Câmera de verdade: desligado"); }
    }

    // segurança: se a análise parar de rodar com a câmera andando, manda parar
    void Vigiar()
    {
        if (real && movendo && !emTeste && tel.Agora - analisadoEm > 0.6) { visca.Parar(); movendo = false; ultimoCmd = new[] { 0, 0, 3, 3 }; tel.Evento("Vigia: análise parada há mais de 0,6 s, câmera parada"); }
    }

    PointF? ParaQuadro(Point p)
    {
        if (largura == 0) return null;
        float esc = Math.Min((float)pbQuadro.Width / largura, (float)pbQuadro.Height / altura);
        float ox = (pbQuadro.Width - largura * esc) / 2, oy = (pbQuadro.Height - altura * esc) / 2;
        return new PointF((p.X - ox) / esc, (p.Y - oy) / esc);
    }

    // ---------- teste de resposta: mede quanto tempo a câmera leva para começar a mexer e parar ----------
    void TesteResposta()
    {
        if (!real) { MessageBox.Show("Ligue \"Mover a câmera de verdade\" antes. A câmera vai girar um pouco para a direita e voltar."); return; }
        if (emTeste) return;
        int v = (int)nPan.Value;
        new Thread(() =>
        {
            emTeste = true; tel.Evento($"TESTE DE RESPOSTA: início (vel {v})");
            try
            {
                Thread.Sleep(1500);
                foreach (var dir in new[] { 2, 1 })
                {
                    tel.Evento($"TESTE cmd: pan {(dir == 2 ? "direita" : "esquerda")} vel {v}"); visca.Mover(v, 1, dir, 3);
                    Thread.Sleep(700);
                    tel.Evento("TESTE cmd: parar"); visca.Parar();
                    Thread.Sleep(2500);
                }
                visca.PerguntarZoom(); Thread.Sleep(300);
                tel.Evento("TESTE DE RESPOSTA: fim | zoom " + visca.Zoom);
            }
            finally { emTeste = false; }
        }) { IsBackground = true }.Start();
    }

    // ---------- análise ----------
    void Processar()
    {
        byte[] img = new byte[0]; int ultimo = 0;
        var sw = new Stopwatch(); var processo = Process.GetCurrentProcess();
        var cpuAntes = processo.TotalProcessorTime; double tCpu = 0, cpuProg = double.NaN, cpuTot = double.NaN, ndiQps = 0;
        double proximo = 0, ultimoT = 0, perguntaEm = 0; int quadros = 0; double somaDet = 0;
        CpuTotal.Medir();
        while (rodando)
        {
            var r = receptor; double t0 = tel.Agora;
            if (t0 - tCpu >= 1)
            {
                processo.Refresh(); var c = processo.TotalProcessorTime;
                cpuProg = Math.Round((c - cpuAntes).TotalSeconds / (t0 - tCpu) / Environment.ProcessorCount * 100, 1);
                cpuTot = CpuTotal.Medir(); cpuAntes = c;
                if (r != null) { ndiQps = Math.Round(r.Recebidos / (t0 - tCpu), 1); r.Recebidos = 0; }
                tCpu = t0;
                if (!analisar && r != null) { tel.Quadro(largura, altura, null, null, null, null, null, null, null, "so recebendo", null, null, null, null, null, null, real, cpuProg, cpuTot, ndiQps, null, visca.Zoom); Info($"SÓ RECEBENDO | NDI {ndiQps}/s | CPU programa {cpuProg}% | CPU total {cpuTot}%", ""); }
            }
            if (real && !emTeste && t0 - perguntaEm > 2) { perguntaEm = t0; visca.PerguntarZoom(); }
            bool rapido = emTeste;   // no teste: todos os quadros, só medindo movimento
            if (r == null || (!analisar && !rapido) || (!rapido && t0 < proximo)) { Thread.Sleep(4); continue; }
            r.Querer = true;
            if (!r.Pegar(ref img, out int w, out int h, ref ultimo)) { Thread.Sleep(2); continue; }
            proximo = t0 + 0.1; largura = w; altura = h; analisadoEm = t0;
            double dt = ultimoT == 0 ? 0 : Math.Min(0.25, t0 - ultimoT); ultimoT = t0;
            float mov = Movimento(img, w, h);

            if (rapido || rostos == null)
            {
                tel.Quadro(w, h, null, null, null, null, null, null, null, rapido ? "teste de resposta" : "sem modelos", null, null, null, null, null, null, real, cpuProg, cpuTot, ndiQps, mov, visca.Zoom);
                continue;
            }

            sw.Restart();
            var lista = rostos.Detectar(img, w, h);
            double detMs = sw.Elapsed.TotalMilliseconds, recMs = 0; somaDet += detMs; quadros++;

            if (pedirApagar) { cadastro = null; coletados.Clear(); coletar = 0; alvo = null; pedirApagar = false; }
            if (coletar > 0 && lista.Count > 0)
            {
                var maior = lista.OrderByDescending(f => f.W * f.H).First();
                sw.Restart(); coletados.Add(rostos.Assinar(img, w, h, maior)); recMs += sw.Elapsed.TotalMilliseconds;
                if (--coletar == 0)
                {
                    var m = new float[128];
                    foreach (var v in coletados) for (int i = 0; i < 128; i++) m[i] += v[i];
                    cadastro = Rostos.Normalizar(m); coletados.Clear(); alvo = null; tel.Evento("Cadastro concluído (só na memória)");
                }
            }
            if (clique.HasValue)
            {
                var c = clique.Value; clique = null;
                var tocado = lista.FirstOrDefault(f => c.X >= f.X && c.X <= f.X + f.W && c.Y >= f.Y && c.Y <= f.Y + f.H);
                if (tocado != null) { alvo = tocado; falhas = 0; vistoEm = DateTime.Now; estado = "seguindo (tocado)"; tel.Evento("Alvo escolhido por toque"); }
            }
            if (alvo != null)
            {
                var a = alvo;
                var m = lista.OrderByDescending(f => Rostos.Iou(f, a)).FirstOrDefault();
                if (m != null && (Rostos.Iou(m, a) > 0.2f || Dist(m, a) < a.W * 0.8f))
                {
                    m.Parecido = a.Parecido;
                    // confere a cada 1,5 s, só com rosto bom; depois de uma leitura ruim confere logo de novo.
                    // Larga só com 3 leituras ruins seguidas (rosto virado ou borrado não derruba mais).
                    if (cadastro != null && BomParaConferir(m) && (DateTime.Now - verificadoEm).TotalSeconds >= (falhas > 0 ? 0.3 : 1.5))
                    {
                        verificadoEm = DateTime.Now;
                        sw.Restart(); float p = Rostos.Cosseno(cadastro, rostos.Assinar(img, w, h, m)); recMs += sw.Elapsed.TotalMilliseconds;
                        m.Parecido = p;
                        if (p < Limiar - 0.08f)
                        {
                            falhas++;
                            if (falhas >= 3) { tel.Evento($"Confirmação falhou 3x ({p:0.00}): era outra pessoa"); falhas = 0; m = null; estado = "era outra pessoa: procurando"; }
                            else tel.Evento($"Confirmação ruim {falhas}/3 ({p:0.00}): continua seguindo");
                        }
                        else { if (falhas > 0) tel.Evento($"Confirmação voltou ({p:0.00})"); falhas = 0; }
                    }
                    if (m != null) { alvo = m; vistoEm = DateTime.Now; estado = cadastro != null ? "seguindo (reconhecido)" : "seguindo (tocado)"; }
                    else alvo = null;
                }
                else if ((DateTime.Now - vistoEm).TotalSeconds > 2.5) { alvo = null; estado = "perdeu: procurando"; tel.Evento("Perdeu o alvo (2,5 s sem ver)"); }
                else estado = "rosto sumiu: câmera parada, esperando";
            }
            if (alvo == null && cadastro != null && coletar == 0)
            {
                Rosto melhor = null;
                foreach (var f in lista.OrderByDescending(f => f.W * f.H).Take(4))
                {
                    if (f.W < 20) continue;
                    sw.Restart(); f.Parecido = Rostos.Cosseno(cadastro, rostos.Assinar(img, w, h, f)); recMs += sw.Elapsed.TotalMilliseconds;
                    if (f.Parecido >= Limiar && (melhor == null || f.Parecido > melhor.Parecido)) melhor = f;
                }
                if (melhor != null) { alvo = melhor; falhas = 0; vistoEm = verificadoEm = DateTime.Now; estado = "seguindo (reconhecido)"; tel.Evento($"Achou o rosto cadastrado ({melhor.Parecido:0.00})"); }
                else if (!estado.StartsWith("perdeu") && !estado.StartsWith("era")) estado = "procurando o rosto cadastrado";
            }
            if (alvo == null && cadastro == null && coletar == 0 && !estado.StartsWith("perdeu")) estado = "sem alvo (cadastre ou toque num rosto)";

            var visto = alvo != null && (DateTime.Now - vistoEm).TotalSeconds < 0.3 ? alvo : null;
            var (ex, ey, cmd) = Comandar(w, h, (float)dt, visto);

            tel.Quadro(w, h, Math.Round(detMs, 1), recMs > 0 ? Math.Round(recMs, 1) : (double?)null, lista.Count,
                alvo != null ? alvo.Cx / w : (float?)null, alvo != null ? alvo.Cy / h : (float?)null, alvo != null ? alvo.W / w : (float?)null,
                alvo != null ? alvo.Parecido : (float?)null, estado, ex, ey, cmd[0], cmd[2], cmd[1], cmd[3], real, cpuProg, cpuTot, ndiQps, mov, visca.Zoom);

            Desenhar(img, w, h, lista);
            Info($"{w}x{h} | NDI {ndiQps}/s | achar rostos {detMs:0} ms{(recMs > 0 ? $" | reconhecer {recMs:0} ms" : "")} | CPU programa {cpuProg}% · total {cpuTot}% | zoom {(visca.Zoom >= 0 ? visca.Zoom.ToString() : "?")}",
                (coletar > 0 ? $"CADASTRANDO: olhe para a câmera… ({coletar})" : estado + (cadastro != null ? "   [rosto só na memória]" : "")) + "\n" +
                (real ? "CÂMERA DE VERDADE: " : "simulação: ") + CmdTexto(cmd));
        }
    }

    void Info(string a, string b) { var s = a + "\n" + b; try { BeginInvoke((Action)(() => lInfo.Text = s)); } catch { } }

    static string CmdTexto(int[] c) => c[0] == 0 && c[1] == 0 ? "parada" :
        $"pan {(c[2] == 2 ? "direita" : c[2] == 1 ? "esquerda" : "-")} {c[0]}, tilt {(c[3] == 1 ? "cima" : c[3] == 2 ? "baixo" : "-")} {c[1]}   (VISCA 81 01 06 01 {Math.Max(1, c[0]):X2} {Math.Max(1, c[1]):X2} {c[2]:X2} {c[3]:X2} FF)";

    static float Dist(Rosto a, Rosto b) => (float)Math.Sqrt((a.Cx - b.Cx) * (a.Cx - b.Cx) + (a.Cy - b.Cy) * (a.Cy - b.Cy));

    // movimento da imagem entre quadros (mede quando a câmera realmente começa/para de mexer)
    unsafe float Movimento(byte[] img, int w, int h)
    {
        const int GW = 64, GH = 36; var g = new float[GW * GH];
        fixed (byte* s = img)
            for (int y = 0; y < GH; y++) for (int x = 0; x < GW; x++)
            { byte* p = s + ((y * h / GH) * w + x * w / GW) * 4; g[y * GW + x] = (p[0] + p[1] * 2 + p[2]) / 4f; }
        float d = float.NaN;
        if (cinzaAntes != null) { double soma = 0; for (int i = 0; i < g.Length; i++) soma += Math.Abs(g[i] - cinzaAntes[i]); d = (float)(soma / g.Length); }
        cinzaAntes = g; return d;
    }

    // decide a velocidade; com a câmera de verdade o erro é medido no quadro inteiro, na simulação num recorte de 50%
    (float, float, int[]) Comandar(int w, int h, float dt, Rosto f)
    {
        float cw = real ? w : w * 0.5f, ch = real ? h : h * 0.5f;
        if (float.IsNaN(vx)) { vx = w / 2f; vy = h / 2f; }
        float cx = real ? w / 2f : vx, cy = real ? h / 2f : vy;
        int vp = 0, vt = 0, dx = 3, dy = 3; float ex = float.NaN, ey = float.NaN;
        if (f != null && seguir)
        {
            ex = (f.Cx - cx) / (cw / 2); ey = (f.Cy - (cy - ch * 0.12f)) / (ch / 2);   // rosto um pouco acima do centro
            // previsão: soma ao erro o quanto ele está crescendo (anda junto com quem anda; freia antes de passar do ponto)
            if (!float.IsNaN(exAnt) && dt > 0.02f)
            { dEx = 0.6f * dEx + 0.4f * (ex - exAnt) / dt; dEy = 0.6f * dEy + 0.4f * (ey - eyAnt) / dt; }
            exAnt = ex; eyAnt = ey;
            float px = real ? ex + Previsao * dEx : ex, py = real ? ey + Previsao * dEy : ey;
            // a previsão serve para acelerar ou frear, nunca para inverter: se "passaria do centro", só para
            if (Math.Sign(px) != Math.Sign(ex)) px = 0; if (Math.Sign(py) != Math.Sign(ey)) py = 0;
            float zona = zonaPct / 100f;
            // passou do centro: para de vez (só volta a andar quando sair da zona morta inteira; acaba com o vai e volta)
            if (movX && Math.Sign(ex) != ladoX) movX = false; if (movY && Math.Sign(ey) != ladoY) movY = false;
            ladoX = Math.Sign(ex); ladoY = Math.Sign(ey);
            // começar a andar: pelo erro real (a previsão sozinha não tira a câmera do lugar); continuar: pela previsão
            movX = movX ? Math.Abs(px) > zona * 0.4f : Math.Abs(ex) > zona && Math.Abs(px) > zona;
            movY = movY ? Math.Abs(py) > zona * 0.4f : Math.Abs(ey) > zona && Math.Abs(py) > zona;
            // com o zoom aberto a mesma velocidade mexe pouco a imagem: acelera até 2,5x (zoom 0) e volta a 1x no zoom todo fechado
            int zm = visca.Zoom; float k = zm >= 0 ? 1f + 1.5f * (1f - Math.Min(1f, zm / 16384f)) : 1.5f;
            if (movX) { float t = Math.Min(1, (Math.Abs(px) - zona * 0.4f) / (1 - zona * 0.4f)); vp = Math.Min(24, Math.Max(1, (int)Math.Round(maxPan * k * t))); dx = px > 0 ? 2 : 1; }
            if (movY) { float t = Math.Min(1, (Math.Abs(py) - zona * 0.4f) / (1 - zona * 0.4f)); vt = Math.Min(20, Math.Max(1, (int)Math.Round(maxTilt * k * t))); dy = py > 0 ? 2 : 1; }
            if (inverter) { if (dx != 3) dx = 3 - dx; if (dy != 3) dy = 3 - dy; }
            // inverter o sentido de uma vez dá tranco: primeiro para (um ciclo), depois vai
            if (real && movendo && vp > 0 && ultimoCmd[0] > 0 && ultimoCmd[2] != 3 && dx != 3 && dx != ultimoCmd[2]) { vp = 0; dx = 3; }
            if (real && movendo && vt > 0 && ultimoCmd[1] > 0 && ultimoCmd[3] != 3 && dy != 3 && dy != ultimoCmd[3]) { vt = 0; dy = 3; }
        }
        else { movX = movY = false; exAnt = float.NaN; dEx = dEy = 0; }
        var cmd = new[] { vp, vt, dx, dy };
        if (real)
        {
            bool mudou = !cmd.SequenceEqual(ultimoCmd);
            if (mudou || (movendo && tel.Agora - cmdEm > 1)) { visca.Mover(vp, vt, dx, dy); cmdEm = tel.Agora; ultimoCmd = cmd; movendo = vp > 0 || vt > 0; }
        }
        else
        {   // câmera simulada (recorte)
            int sx = inverter ? -1 : 1;
            vx += sx * (dx == 2 ? 1 : dx == 1 ? -1 : 0) * vp / 24f * 0.6f * w * dt;
            vy += sx * (dy == 2 ? 1 : dy == 1 ? -1 : 0) * vt / 20f * 0.6f * h * dt;
            vx = Math.Min(w - cw / 2, Math.Max(cw / 2, vx)); vy = Math.Min(h - ch / 2, Math.Max(ch / 2, vy));
        }
        return (ex, ey, cmd);
    }

    unsafe void Desenhar(byte[] img, int w, int h, List<Rosto> lista)
    {
        float esc = Math.Min(640f / w, 640f / h); int sw = (int)(w * esc), sh = (int)(h * esc);
        var bq = Amostrar(img, w, h, 0, 0, w, h, sw, sh);
        float cw = w * 0.5f, ch = h * 0.5f;
        var bc = real ? null : Amostrar(img, w, h, (int)(vx - cw / 2), (int)(vy - ch / 2), (int)cw, (int)ch, sw, sh);
        using (var g = Graphics.FromImage(bq))
        using (var fonte = new Font("Segoe UI", 9f, FontStyle.Bold))
        {
            foreach (var f in lista)
            {
                bool eh = alvo != null && Math.Abs(f.Cx - alvo.Cx) < 1 && Math.Abs(f.Cy - alvo.Cy) < 1;
                var cor = eh ? Color.Lime : Color.Silver;
                using (var p = new Pen(cor, eh ? 3 : 1.5f)) g.DrawRectangle(p, f.X * esc, f.Y * esc, f.W * esc, f.H * esc);
                var rot = (eh ? "ALVO " : "") + (float.IsNaN(f.Parecido) ? "" : f.Parecido.ToString("0.00"));
                if (rot.Trim() != "") using (var b = new SolidBrush(cor)) g.DrawString(rot.Trim(), fonte, b, f.X * esc, f.Y * esc - 16);
            }
            float z = zonaPct / 100f;
            if (real)
                using (var p = new Pen(Color.Gold, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    g.DrawRectangle(p, sw / 2f - z * sw / 2, sh / 2f - sh * 0.12f - z * sh / 2, z * sw, z * sh);
            else
                using (var p = new Pen(Color.Gold, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                    g.DrawRectangle(p, (vx - cw / 2) * esc, (vy - ch / 2) * esc, cw * esc, ch * esc);
        }
        try { BeginInvoke((Action)(() => { var a = pbQuadro.Image; pbQuadro.Image = bq; a?.Dispose(); var c = pbCamera.Image; pbCamera.Image = bc; c?.Dispose(); })); }
        catch { bq.Dispose(); bc?.Dispose(); }
    }

    static unsafe Bitmap Amostrar(byte[] img, int w, int h, int x0, int y0, int rw, int rh, int ow, int oh)
    {
        var b = new Bitmap(ow, oh, PixelFormat.Format32bppRgb);
        var d = b.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        fixed (byte* s = img)
            for (int y = 0; y < oh; y++)
            {
                int sy = Math.Min(h - 1, Math.Max(0, y0 + (int)((long)y * rh / oh)));
                uint* dl = (uint*)((byte*)d.Scan0 + y * d.Stride); uint* sl = (uint*)(s + sy * w * 4);
                for (int x = 0; x < ow; x++) dl[x] = sl[Math.Min(w - 1, Math.Max(0, x0 + (int)((long)x * rw / ow)))];
            }
        b.UnlockBits(d); return b;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar: para a câmera, grava a telemetria e encerra o processo na hora.
        // (Desmontar NDI e ONNX enquanto ainda trabalham deixava o programa preso ao fechar, na igreja e em casa.)
        rodando = false; vigia?.Dispose();
        try { visca.Parar(); } catch { }
        proc?.Join(1500);
        try { visca.Dispose(); } catch { }
        cadastro = null;
        try { tel.Dispose(); } catch { }
        base.OnFormClosing(e);
        Process.GetCurrentProcess().Kill();   // o Windows libera NDI, modelos e memória (o rosto some junto)
    }
}
