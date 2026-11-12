// Rastreio da câmera PTZ dentro do Cast: a MESMA lógica do kit de testes aprovado em 07/11/2026 às 21h24
// (kit-rastreio\Janela.cs, sem a mudança das 21h27 "começar só pelo erro real", que não foi testada).
// Vê a câmera por NDI (recepção leve, separada da cena), acha e reconhece o rosto cadastrado e move a câmera
// por VISCA (ou simula). Telemetria só com números; o cadastro do rosto fica só na memória e some ao fechar.
// A diferença para o kit é só a tela: em vez da janela WinForms, o estado vai para a tela do Cast.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MiguelAngelusCast.Rastreio
{
    public sealed class RastreioCfg
    {
        public string Fonte { get; set; } = "";
        public string Ip { get; set; } = "192.168.100.190";
        public int Porta { get; set; } = 5678;
        public int Pan { get; set; } = 8;
        public int Tilt { get; set; } = 6;
        public int Zona { get; set; } = 14;
        public bool Inverter { get; set; }
        public bool Leve { get; set; } = true;
    }

    public sealed class Rastreador : IDisposable
    {
        public static bool Usado;                         // se foi aberto, o Cast encerra o processo na hora ao fechar (como o kit)
        static readonly string arquivoCfg = Path.Combine(Program.PastaDados, "rastreio.json");
        public RastreioCfg Cfg { get; }

        readonly Telemetria tel = new Telemetria();
        readonly Visca visca;
        Receptor receptor; Rostos rostos; Thread proc; volatile bool rodando = true;
        readonly Timer vigia, rotina;
        public string ErroModelos { get; private set; }
        public string ErroNdi { get; private set; }

        // ajustes lidos pela análise
        volatile bool real, seguir = true, analisar = true, inverter;
        volatile int maxPan = 8, maxTilt = 6, zonaPct = 14;
        public bool Real => real; public bool Seguir => seguir; public bool Analisar => analisar;

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
        const float Previsao = 0.30f;                 // segundos à frente (atraso medido câmera+NDI ~0,2-0,26 s)
        int ladoX, ladoY;                             // de que lado do centro o rosto estava

        // para a tela
        public volatile string Linha1 = "", Linha2 = "", Linha3 = "", UltimoEvento = "";
        public volatile bool QuerImagem;              // a tela do rastreio está aberta
        string imagem; long imagemSeq; double imagemEm;
        readonly object travaImg = new object();
        public string FonteConectada { get; private set; }
        List<KeyValuePair<string, NdiRx.Fonte>> fontes = new List<KeyValuePair<string, NdiRx.Fonte>>();
        public string[] NomesFontes { get; private set; } = new string[0];
        public bool Cadastrado => cadastro != null;
        public int Coletando => coletar;

        static bool BomParaConferir(Rosto r)
        {
            if (r.W < 20 || r.Nota < 0.8f) return false;
            float olhoX = (r.Pontos[0] + r.Pontos[2]) / 2, dist = Math.Abs(r.Pontos[2] - r.Pontos[0]);
            return dist > 1 && Math.Abs(r.Pontos[4] - olhoX) / dist < 0.35f;
        }

        public Rastreador()
        {
            Usado = true;
            Cfg = Carregar();
            maxPan = Cfg.Pan; maxTilt = Cfg.Tilt; zonaPct = Cfg.Zona; inverter = Cfg.Inverter;
            visca = new Visca(t => { tel.Evento(t); UltimoEvento = t; });
            ErroNdi = NdiRx.Iniciar();
            tel.Evento(ErroNdi ?? "NDI iniciado");
            Linha1 = ErroNdi ?? "Procurando câmeras NDI na rede…";
            try { rostos = new Rostos(Path.Combine(Program.Pasta, "modelos"), 2); }
            catch (Exception e) { ErroModelos = "Não carregou os modelos de rosto: " + e.Message; tel.Evento("ERRO modelos: " + e); Registro.Erro("rastreio: modelos", e); }
            tel.Evento($"Ajustes: pan máx {maxPan}, tilt máx {maxTilt}, zona morta {zonaPct}%, invertida {inverter}");
            rotina = new Timer(_ => { try { Rotina(); } catch (Exception e) { Registro.Erro("rastreio: rotina", e); } }, null, 300, 1000);
            vigia = new Timer(_ => Vigiar(), null, 250, 250);
            proc = new Thread(Processar) { IsBackground = true, Name = "rastreio" }; proc.Start();
            Registro.Info("rastreio aberto");
        }

        static RastreioCfg Carregar()
        {
            try { if (File.Exists(arquivoCfg)) return JsonSerializer.Deserialize<RastreioCfg>(File.ReadAllText(arquivoCfg)) ?? new RastreioCfg(); }
            catch (Exception e) { Registro.Erro("ler rastreio.json", e); }
            return new RastreioCfg();
        }
        void SalvarCfg()
        {
            try { File.WriteAllText(arquivoCfg, JsonSerializer.Serialize(Cfg, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar rastreio.json", e); }
        }

        // ---------- comandos da tela ----------
        public void Atualizar()
        {
            var l = NdiRx.Fontes();
            lock (travaImg) { fontes = l; NomesFontes = l.Select(f => f.Key).ToArray(); }
        }

        public string Conectar(string nome, bool leve)
        {
            Atualizar();
            int i; lock (travaImg) i = fontes.FindIndex(f => f.Key == nome);
            if (i < 0) return "Câmera NDI não encontrada: " + nome;
            var r = receptor; receptor = null; r?.Dispose();
            receptor = new Receptor(fontes[i].Value, leve);
            conectouEm = DateTime.Now; trocouParaCompleto = !leve; vx = float.NaN; largura = 0;
            FonteConectada = nome; Cfg.Fonte = nome; Cfg.Leve = leve; SalvarCfg();
            tel.Evento($"Vídeo conectado: \"{nome}\" ({(leve ? "leve" : "completo")})");
            return null;
        }

        // roteiro: "achar" = seguir o maior rosto que aparecer (sem cadastro); "soltar" = largar o alvo atual
        volatile bool pedirMaior, pedirSoltar; bool semConferir;   // alvo do "achar" não é conferido com o cadastro
        public void AcharMaior() { pedirMaior = true; tel.Evento("Roteiro: achar (seguir o maior rosto)"); }
        public void SoltarAlvo() { pedirSoltar = true; pedirMaior = false; tel.Evento("Roteiro: soltar o alvo"); }
        public string Estado => estado;
        public bool Movendo => real ? movendo : movX || movY;      // (0.1.9) o roteiro espera parar no centro (achar com nome)

        public void Cadastrar() { coletados.Clear(); coletar = 12; NomeCadastro = null; tel.Evento("Cadastro iniciado (pessoa autorizou)"); }
        // cadastro guardado (Pessoas.cs): volta a ser o alvo reconhecido, sem cadastrar de novo
        volatile float[] pedirUsar;
        public string NomeCadastro { get; private set; }
        public float[] CadastroAtual => cadastro;
        public void UsarCadastro(float[] v, string nome) { pedirUsar = v; NomeCadastro = nome; tel.Evento("Cadastro guardado em uso"); }
        public void Apagar() { pedirApagar = true; NomeCadastro = null; tel.Evento("Cadastro apagado"); }
        public void Tocar(float x01, float y01) { if (largura > 0) clique = new PointF(x01 * largura, y01 * altura); }
        public void MudarSeguir(bool v) { seguir = v; tel.Evento("Seguir: " + seguir); }
        public void MudarAnalisar(bool v) { analisar = v; tel.Evento("Analisar: " + analisar); }
        public void Ajustar(int pan, int tilt, int zona, bool inv, string ip, int porta)
        {
            maxPan = Math.Clamp(pan, 1, 24); maxTilt = Math.Clamp(tilt, 1, 20); zonaPct = Math.Clamp(zona, 2, 40);
            if (inv != inverter) tel.Evento("Câmera invertida: " + inv);
            inverter = inv;
            Cfg.Pan = maxPan; Cfg.Tilt = maxTilt; Cfg.Zona = zonaPct; Cfg.Inverter = inv;
            if (!string.IsNullOrWhiteSpace(ip)) Cfg.Ip = ip.Trim();
            if (porta > 0 && porta < 65536) Cfg.Porta = porta;
            SalvarCfg();
            tel.Evento($"Ajustes: pan máx {maxPan}, tilt máx {maxTilt}, zona morta {zonaPct}%");
        }

        // devolve o erro (ou null se ligou)
        public string LigarReal(bool ligar)
        {
            if (ligar)
            {
                if (!visca.Conectar(Cfg.Ip, Cfg.Porta)) return "Não conectou na câmera (veja a telemetria). Confira IP e porta, e se outro programa não está ocupando a conexão.";
                real = true; tel.Evento("Câmera de verdade: LIGADO"); Registro.Info("rastreio: câmera de verdade ligada");
            }
            else { real = false; visca.Parar(); visca.Desconectar(); tel.Evento("Câmera de verdade: desligado"); Registro.Info("rastreio: câmera de verdade desligada"); }
            return null;
        }

        public void PararCamera() { real = false; try { visca.Parar(); visca.Desconectar(); } catch { } tel.Evento("PARAR CÂMERA apertado"); Registro.Info("rastreio: PARAR CÂMERA"); }

        public string Pacote() => tel.Empacotar();
        public string PastaTelemetria => tel.Pasta;

        // a cada segundo: lista de fontes, e se o vídeo leve não chegar, troca para o completo
        void Rotina()
        {
            if (receptor == null)
            {
                Atualizar();
                // reconecta sozinho na última câmera usada, quando ela aparecer
                if (!string.IsNullOrEmpty(Cfg.Fonte) && NomesFontes.Contains(Cfg.Fonte)) Conectar(Cfg.Fonte, Cfg.Leve);
                return;
            }
            if (!trocouParaCompleto && largura == 0 && (DateTime.Now - conectouEm).TotalSeconds > 4)
            {
                tel.Evento("Vídeo leve não chegou em 4 s: trocando para o completo");
                Conectar(FonteConectada, false);
            }
            tel.Salvar();
        }

        // segurança: se a análise parar de rodar com a câmera andando, manda parar
        void Vigiar()
        {
            if (real && movendo && !emTeste && tel.Agora - analisadoEm > 0.6) { visca.Parar(); movendo = false; ultimoCmd = new[] { 0, 0, 3, 3 }; tel.Evento("Vigia: análise parada há mais de 0,6 s, câmera parada"); }
        }

        // ---------- teste de resposta: mede quanto tempo a câmera leva para começar a mexer e parar ----------
        public string TesteResposta()
        {
            if (!real) return "Ligue \"Mover a câmera de verdade\" antes. A câmera vai girar um pouco para a direita e voltar.";
            if (emTeste) return null;
            int v = maxPan;
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
            return null;
        }

        // ---------- análise (igual ao kit) ----------
        void Processar()
        {
            byte[] img = new byte[0]; int ultimo = 0;
            var sw = new Stopwatch(); var processo = Process.GetCurrentProcess();
            var cpuAntes = processo.TotalProcessorTime; double tCpu = 0, cpuProg = double.NaN, cpuTot = double.NaN, ndiQps = 0;
            double proximo = 0, ultimoT = 0, perguntaEm = 0; int quadros = 0; double somaDet = 0;
            CpuTotal.Medir();
            while (rodando)
            {
                try
                {
                    var r = receptor; double t0 = tel.Agora;
                    if (t0 - tCpu >= 1)
                    {
                        processo.Refresh(); var c = processo.TotalProcessorTime;
                        cpuProg = Math.Round((c - cpuAntes).TotalSeconds / (t0 - tCpu) / Environment.ProcessorCount * 100, 1);
                        cpuTot = CpuTotal.Medir(); cpuAntes = c;
                        if (r != null) { ndiQps = Math.Round(r.Recebidos / (t0 - tCpu), 1); r.Recebidos = 0; }
                        tCpu = t0;
                        if (!analisar && r != null) { tel.Quadro(largura, altura, null, null, null, null, null, null, null, "so recebendo", null, null, null, null, null, null, real, cpuProg, cpuTot, ndiQps, null, visca.Zoom); Info($"SÓ RECEBENDO | NDI {ndiQps}/s | CPU do Cast {cpuProg}% | CPU total {cpuTot}%", "", ""); }
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
                    if (pedirUsar != null) { cadastro = pedirUsar; pedirUsar = null; coletados.Clear(); coletar = 0; alvo = null; }
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
                    if (pedirSoltar) { pedirSoltar = false; semConferir = false; alvo = null; estado = "sem alvo"; }
                    if (pedirMaior && lista.Count > 0)
                    {
                        pedirMaior = false; semConferir = true; alvo = lista.OrderByDescending(f => f.W * f.H).First(); falhas = 0; vistoEm = DateTime.Now;
                        estado = "seguindo (achar)"; tel.Evento("Achar: alvo = maior rosto");
                    }
                    if (clique.HasValue)
                    {
                        var c = clique.Value; clique = null;
                        var tocado = lista.FirstOrDefault(f => c.X >= f.X && c.X <= f.X + f.W && c.Y >= f.Y && c.Y <= f.Y + f.H);
                        if (tocado != null) { semConferir = false; alvo = tocado; falhas = 0; vistoEm = DateTime.Now; estado = "seguindo (tocado)"; tel.Evento("Alvo escolhido por toque"); }
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
                            if (cadastro != null && !semConferir && BomParaConferir(m) && (DateTime.Now - verificadoEm).TotalSeconds >= (falhas > 0 ? 0.3 : 1.5))
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
                        else if ((DateTime.Now - vistoEm).TotalSeconds > 2.5) { semConferir = false; alvo = null; estado = "perdeu: procurando"; tel.Evento("Perdeu o alvo (2,5 s sem ver)"); }
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

                    if (QuerImagem && t0 - imagemEm >= 0.2) { imagemEm = t0; Desenhar(img, w, h, lista); }
                    Info($"{w}x{h} | NDI {ndiQps}/s | achar rostos {detMs:0} ms{(recMs > 0 ? $" | reconhecer {recMs:0} ms" : "")} | CPU do Cast {cpuProg}% · total {cpuTot}% | zoom {(visca.Zoom >= 0 ? visca.Zoom.ToString() : "?")}",
                        coletar > 0 ? $"CADASTRANDO: olhe para a câmera… ({coletar})" : estado + (cadastro != null ? (NomeCadastro != null ? "   [rosto de " + NomeCadastro + ", guardado neste computador]" : "   [rosto só na memória]") : ""),
                        (real ? "CÂMERA DE VERDADE: " : "simulação: ") + CmdTexto(cmd));
                }
                catch (Exception e) { Registro.Erro("rastreio: análise", e); tel.Evento("ERRO análise: " + e.Message); Thread.Sleep(200); }
            }
        }

        void Info(string a, string b, string c) { Linha1 = a; Linha2 = b; Linha3 = c; }

        static string CmdTexto(int[] c) => c[0] == 0 && c[1] == 0 ? "parada" :
            $"pan {(c[2] == 2 ? "direita" : c[2] == 1 ? "esquerda" : "-")} {c[0]}, tilt {(c[3] == 1 ? "cima" : c[3] == 2 ? "baixo" : "-")} {c[1]}";

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
                movX = Math.Abs(px) > (movX ? zona * 0.4f : zona); movY = Math.Abs(py) > (movY ? zona * 0.4f : zona);
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

        // imagem para a tela do rastreio: quadro com os rostos marcados (JPEG em base64; nunca vai para arquivo)
        void Desenhar(byte[] img, int w, int h, List<Rosto> lista)
        {
            float esc = Math.Min(640f / w, 640f / h); int sw = (int)(w * esc), sh = (int)(h * esc);
            using var bq = Amostrar(img, w, h, 0, 0, w, h, sw, sh);
            float cw = w * 0.5f, ch = h * 0.5f;
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
            using var ms = new MemoryStream();
            var jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var pars = new EncoderParameters(1); pars.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
            bq.Save(ms, jpg, pars);
            lock (travaImg) { imagem = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()); imagemSeq++; }
        }

        // devolve a imagem nova (ou null se não mudou desde "seq")
        public string ImagemNova(ref long seq)
        {
            lock (travaImg) { if (imagemSeq == seq) return null; seq = imagemSeq; return imagem; }
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

        // Fechar: para a câmera e grava a telemetria (NDI e modelos o Windows libera quando o processo acaba)
        public void Dispose()
        {
            rodando = false; vigia?.Dispose(); rotina?.Dispose();
            try { visca.Parar(); } catch { }
            proc?.Join(1500);
            try { visca.Dispose(); } catch { }
            cadastro = null;
            try { tel.Dispose(); } catch { }
            Registro.Info("rastreio fechado");
        }
    }
}
