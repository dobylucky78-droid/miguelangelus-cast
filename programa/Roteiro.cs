// Roteiro da celebração: perfis (listas de momentos) e o "Próximo momento" do Ao vivo.
// Cada momento põe uma cena no programa e, conforme o tipo, mexe na câmera:
//   sem    = só a cena (tela de espera, capa, telão…)
//   fixo   = leva a câmera a uma posição pronta e põe a cena
//   achar  = vai à posição e centraliza quem estiver lá (o maior rosto, sem cadastro; ex.: ambão móvel)
//   seguir = vai à posição e a câmera segue o rosto cadastrado (o celebrante)
//   video  = toca um vídeo do computador (com som; a mesa abaixa sozinha) e, se marcado, segue sozinho ao acabar
// Para não mostrar a câmera andando, a posição do PRÓXIMO momento é chamada antes, quando aquela câmera não está no ar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MiguelAngelusCast.Motor;
using MiguelAngelusCast.Rastreio;

namespace MiguelAngelusCast
{
    public sealed class Momento
    {
        public string Nome { get; set; } = "Momento";
        public string Tipo { get; set; } = "sem";          // sem | fixo | achar | seguir | video
        public string Cena { get; set; } = "";             // id da cena (Ao vivo)
        public string Camera { get; set; } = "";           // id da câmera (Ajustes › Câmeras)
        public int Posicao { get; set; } = -1;             // nº da posição pronta na câmera
        public string Video { get; set; } = "";            // arquivo, no tipo vídeo
        public bool Segue { get; set; } = true;            // vídeo: ao acabar, segue sozinho
        // (0.1.9) outras cenas/câmeras para o mesmo momento: o Cast usa a primeira cuja câmera NÃO está no ar
        public List<Opcao> Outras { get; set; } = new List<Opcao>();
        // (0.1.9) como este momento se chama no MiguelAngelus (separado por vírgulas); vazio = pelo nome do momento
        public string Miguel { get; set; } = "";
        // (0.1.9) seguir/achar: id da pessoa cadastrada (Câmera que segue › Cadastros guardados); vazio = como antes
        public string Pessoa { get; set; } = "";
    }

    public sealed class Opcao
    {
        public string Cena { get; set; } = "";
        public string Camera { get; set; } = "";
        public int Posicao { get; set; } = -1;
        public string Pessoa { get; set; } = "";
    }

    public sealed class PerfilRoteiro
    {
        public string Id { get; set; } = "p" + DateTime.Now.Ticks.ToString("x");
        public string Nome { get; set; } = "Perfil";
        public bool Modelo { get; set; }                   // modelo do Cast: não muda (use "Salvar como novo")
        public List<Momento> Momentos { get; set; } = new List<Momento>();
    }

    public sealed class Roteiro
    {
        static readonly string arquivo = Path.Combine(Program.PastaDados, "roteiros.json");
        sealed class Dados
        {
            public List<PerfilRoteiro> Perfis { get; set; } = new List<PerfilRoteiro>(); public string Atual { get; set; }
            public bool Seguir { get; set; }                 // (0.1.9) acompanhar o telão do MiguelAngelus
            public int Antes { get; set; } = 2;              // (0.1.9) preparar o próximo momento faltando N slides
        }

        readonly Mesa mesa;
        readonly Func<CameraCfg, string> prepararRastreio;   // abre/conecta o rastreio nesta câmera e liga o movimento (devolve erro)
        readonly Func<Rastreador> rastreio;                  // o rastreio, se estiver aberto
        Dados d;

        public List<Momento> Lista { get; private set; } = new List<Momento>();
        public bool Alterado { get; private set; }
        public int Agora { get; private set; } = -1;
        public string Aviso { get; private set; } = "";
        public event Action Mudou;
        public PerfilRoteiro Perfil => d.Perfis.FirstOrDefault(p => p.Id == d.Atual) ?? d.Perfis[0];
        public IReadOnlyList<PerfilRoteiro> Perfis => d.Perfis;

        DateTime acharDesde = DateTime.MinValue; bool acharFeito; bool fimVideoTratado;

        public Roteiro(Mesa mesa, Func<CameraCfg, string> prepararRastreio, Func<Rastreador> rastreio)
        {
            this.mesa = mesa; this.prepararRastreio = prepararRastreio; this.rastreio = rastreio;
            try { if (File.Exists(arquivo)) d = JsonSerializer.Deserialize<Dados>(File.ReadAllText(arquivo)); }
            catch (Exception e) { Registro.Erro("ler roteiros.json", e); }
            d ??= new Dados();
            if (!d.Perfis.Any(p => p.Modelo)) d.Perfis.Insert(0, ModeloMissa());
            Lista = Copia(Perfil.Momentos);
            Miguel.Seguir = d.Seguir;
        }

        // modelo do Cast: os momentos da Missa, sem cenas nem câmeras (cada paróquia escolhe as suas)
        static PerfilRoteiro ModeloMissa()
        {
            var p = new PerfilRoteiro { Id = "modelo-missa", Nome = "Missa (modelo do Cast)", Modelo = true };
            foreach (var (nome, tipo) in new[] {
                ("Tela de espera", "sem"), ("Entrada", "fixo"), ("Saudação", "fixo"), ("Ato penitencial", "fixo"), ("Glória", "fixo"),
                ("Oração do dia", "fixo"), ("1ª Leitura", "fixo"), ("Salmo", "fixo"), ("2ª Leitura", "fixo"), ("Evangelho", "fixo"),
                ("Homilia", "seguir"), ("Profissão de fé", "fixo"), ("Preces", "achar"), ("Ofertório", "fixo"), ("Oração eucarística", "fixo"),
                ("Consagração", "fixo"), ("Pai-nosso", "fixo"), ("Comunhão", "fixo"), ("Avisos", "achar"), ("Bênção final", "fixo"), ("Encerramento", "sem") })
                p.Momentos.Add(new Momento { Nome = nome, Tipo = tipo });
            return p;
        }

        static List<Momento> Copia(List<Momento> l) => JsonSerializer.Deserialize<List<Momento>>(JsonSerializer.Serialize(l));

        void Gravar()
        {
            try { File.WriteAllText(arquivo, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Registro.Erro("gravar roteiros.json", e); }
        }
        void Avisar() => Mudou?.Invoke();

        // ---------- perfis ----------
        public void Escolher(string id)
        {
            if (!d.Perfis.Any(p => p.Id == id)) return;
            d.Atual = id; Lista = Copia(Perfil.Momentos); Alterado = false; Agora = -1; Aviso = ""; Gravar(); Avisar();
        }
        public void Editar(List<Momento> nova) { Lista = nova ?? new List<Momento>(); Alterado = true; if (Agora >= Lista.Count) Agora = Lista.Count - 1; Avisar(); }
        public string Salvar()
        {
            if (Perfil.Modelo) return "Os modelos do Cast não mudam: use \"Salvar como novo\".";
            Perfil.Momentos = Copia(Lista); Alterado = false; Gravar(); Avisar(); return $"Perfil \"{Perfil.Nome}\" salvo.";
        }
        public string SalvarComoNovo(string nome)
        {
            nome = (nome ?? "").Trim();
            if (nome == "") return "Dê um nome ao perfil.";
            if (d.Perfis.Any(p => p.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase))) return "Já existe um perfil com esse nome.";
            var p = new PerfilRoteiro { Nome = nome, Momentos = Copia(Lista) };
            d.Perfis.Add(p); d.Atual = p.Id; Alterado = false; Gravar(); Avisar(); return $"Perfil \"{nome}\" criado.";
        }
        public string Renomear(string nome)
        {
            nome = (nome ?? "").Trim();
            if (Perfil.Modelo) return "Os modelos do Cast não mudam de nome.";
            if (nome == "" || d.Perfis.Any(p => p != Perfil && p.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase))) return "Nome vazio ou repetido.";
            Perfil.Nome = nome; Gravar(); Avisar(); return "Nome trocado.";
        }
        public string Apagar()
        {
            if (Perfil.Modelo) return "Os modelos do Cast não podem ser apagados.";
            var nome = Perfil.Nome; d.Perfis.Remove(Perfil); d.Atual = d.Perfis[0].Id; Lista = Copia(Perfil.Momentos); Alterado = false; Agora = -1; Gravar(); Avisar();
            return $"Perfil \"{nome}\" apagado.";
        }
        public string Exportar(string caminho) { File.WriteAllText(caminho, JsonSerializer.Serialize(new PerfilRoteiro { Nome = Perfil.Nome, Momentos = Copia(Lista) }, new JsonSerializerOptions { WriteIndented = true })); return "Perfil exportado."; }
        public string Importar(string caminho)
        {
            var p = JsonSerializer.Deserialize<PerfilRoteiro>(File.ReadAllText(caminho));
            if (p?.Momentos == null) return "Arquivo não é um perfil do Cast.";
            p.Id = "p" + DateTime.Now.Ticks.ToString("x"); p.Modelo = false;
            var nome = p.Nome; int k = 2; while (d.Perfis.Any(x => x.Nome.Equals(p.Nome, StringComparison.OrdinalIgnoreCase))) p.Nome = $"{nome} ({k++})";
            d.Perfis.Add(p); d.Atual = p.Id; Lista = Copia(p.Momentos); Alterado = false; Agora = -1; Gravar(); Avisar();
            return $"Perfil \"{p.Nome}\" importado.";
        }

        // ---------- ao vivo ----------
        public void Iniciar() { Agora = -1; LimparPreparo(); ultimoAlvo = -1; camAgora = null; Aviso = "Celebração iniciada: aperte \"Próximo momento\" para o primeiro."; Registro.Info("roteiro: iniciar \"" + Perfil.Nome + "\""); PreposicionarProximo(); Avisar(); }
        public void Proximo() { if (Agora < Lista.Count - 1) Ir(Agora + 1); }
        public void Anterior() { if (Agora > 0) Ir(Agora - 1); }

        // ---------- (0.1.9) várias cenas por momento: a que usa uma câmera livre ----------
        // Pos = preset da câmera (escolhido no momento); Coord = posição salva na cena; Tipo = o tipo que vale (ver Montar)
        sealed class Escolha { public string Cena; public CameraCfg Cam; public int Pos = -1; public PtzCena Coord; public string Pessoa; public string Tipo; }
        CameraCfg camAgora;               // câmera do momento no ar (a escolhida)
        string tipoAgora = "sem";         // o tipo que valeu no momento do ar
        IEnumerable<Escolha> Opcoes(Momento m)
        {
            yield return Montar(m, m.Cena, m.Camera, m.Posicao, m.Pessoa);
            foreach (var o in m.Outras ?? new List<Opcao>()) yield return Montar(m, o.Cena, o.Camera, o.Posicao, o.Pessoa);
        }
        // (0.1.10) o momento manda no que ele escolheu; o que ficou em branco vem da cena:
        //   câmera/posição em branco → a câmera e a posição salvas na cena
        //   "fixo" sem câmera no momento → o rastreio da cena (fica parada, achar ou seguir)
        //   pessoa em branco → a pessoa da cena
        Escolha Montar(Momento m, string cena, string camera, int pos, string pessoa)
        {
            var e = new Escolha { Cena = cena, Cam = Cameras.Achar(camera), Pos = pos, Pessoa = pessoa, Tipo = m.Tipo };
            if (m.Tipo is "sem" or "video") return e;
            var cc = mesa.CenaPara(cena); var camCena = Cameras.Achar(cc?.Camera);
            if (camCena == null) return e;
            bool momentoEscolheu = e.Cam != null && e.Pos >= 0;
            if (!momentoEscolheu && (e.Cam == null || e.Cam.Id == camCena.Id))
            {
                e.Cam = camCena; e.Pos = -1; e.Coord = cc.Ptz != null && cc.Ptz.Camera == camCena.Id ? cc.Ptz : null;
                if (m.Tipo == "fixo" && string.IsNullOrEmpty(camera) && cc.Rastreio is "achar" or "seguir") e.Tipo = cc.Rastreio;
            }
            if (string.IsNullOrEmpty(e.Pessoa) && e.Cam?.Id == camCena.Id) e.Pessoa = cc.Pessoa;
            return e;
        }
        static bool TemPos(Escolha e) => e?.Cam != null && (e.Pos >= 0 || e.Coord != null);

        // (0.1.9) pessoa escolhida no momento: o roteiro põe o rosto dela em uso só durante o momento; o rosto que você
        // escolheu à mão na aba "Câmera que segue" volta quando vier um seguir/achar sem pessoa
        float[] manualRosto; string manualNome, nomePosto; bool pessoaDoRoteiro;
        // devolve (nome da pessoa, erro); sem pessoa: (null, null)
        (string nome, string erro) PorPessoa(Rastreador r, string pessoaId)
        {
            // trocaram o rosto à mão na aba depois que o roteiro pôs o dele: o de agora passa a ser o "manual"
            if (pessoaDoRoteiro && r.NomeCadastro != nomePosto) pessoaDoRoteiro = false;
            if (string.IsNullOrEmpty(pessoaId))
            {
                if (pessoaDoRoteiro) { pessoaDoRoteiro = false; if (manualRosto != null) r.UsarCadastro(manualRosto, manualNome); else r.Apagar(); }
                return (null, null);
            }
            var pe = MiguelAngelusCast.Rastreio.Pessoas.Achar(pessoaId);
            if (pe == null || pe.Assinatura == null || pe.Assinatura.Length != 128) return (null, "a pessoa escolhida no Roteiro não está mais nos cadastros guardados (aba Câmera que segue)");
            if (!pessoaDoRoteiro) { manualRosto = r.CadastroAtual; manualNome = r.NomeCadastro; pessoaDoRoteiro = true; }
            if (r.NomeCadastro != pe.Nome || r.CadastroAtual == null) r.UsarCadastro(pe.Assinatura, pe.Nome);
            nomePosto = pe.Nome;
            return (pe.Nome, null);
        }
        string acharNome;                 // achar com pessoa: quem está sendo procurado (null = o maior rosto)
        bool acharVisto, acharLigado; DateTime acharVistoEm;
        // a câmera está no ar? (a imagem da cena do Programa — ou da que está entrando — é ela, ou é a câmera do momento atual)
        bool NoAr(CameraCfg c)
        {
            if (c == null) return false;
            if (camAgora != null && camAgora.Id == c.Id && Agora >= 0 && tipoAgora is not ("sem" or "video")) return true;
            if (string.IsNullOrEmpty(c.Fonte)) return false;
            foreach (var cena in new[] { mesa.ProgramaAtual, mesa.ParaAtual })
                if (cena != null && mesa.Retrato.TryGetValue(cena, out var cn) && cn.fonte == "ndi:" + c.Fonte) return true;
            return false;
        }
        Escolha Escolher(Momento m)
        {
            var todas = Opcoes(m).ToList();
            var validas = todas.Where(o => !string.IsNullOrEmpty(o.Cena) && mesa.Retrato.ContainsKey(o.Cena)).ToList();
            if (validas.Count == 0) return todas[0];
            if (validas[0].Tipo == "sem") return validas[0];
            return validas.FirstOrDefault(o => o.Cam == null || !NoAr(o.Cam)) ?? validas[0];
        }

        // ---------- (0.1.9) preparar no Preview (você aperta Transição) ----------
        public int Preparado { get; private set; } = -1;
        Escolha prep; bool prepPosFeita; string prepProgAntes; int prepPedidos;
        void LimparPreparo() { Preparado = -1; prep = null; }
        public void Preparar(int i)
        {
            if (i < 0 || i >= Lista.Count) return;
            var m = Lista[i];
            if (m.Tipo == "video")
            {
                LimparPreparo();
                Aviso = $"Próximo: o vídeo \"{m.Nome}\" — aperte \"Próximo momento\" na hora de tocar."; Avisar(); return;
            }
            var e = Escolher(m);
            if (string.IsNullOrEmpty(e.Cena) || !mesa.Retrato.ContainsKey(e.Cena)) { LimparPreparo(); Aviso = $"Próximo: \"{m.Nome}\" — este momento não tem cena escolhida (Roteiro)."; Avisar(); return; }
            Preparado = i; prep = e; prepPosFeita = false; prepProgAntes = mesa.ProgramaAtual; prepPedidos = mesa.PedidosTroca;
            mesa.PorNoPreview(e.Cena);
            bool mover = TemPos(e) && e.Tipo != "sem";
            if (mover && !NoAr(e.Cam)) { IrPosicao(e); prepPosFeita = true; }
            Registro.Info($"roteiro: preparado {i + 1} \"{m.Nome}\" (cena {mesa.Retrato[e.Cena].nome}{(mover ? ", " + e.Cam.Nome + (prepPosFeita ? " já indo" : " na troca") : "")})");
            Aviso = $"Preparado no Preview: \"{m.Nome}\"" +
                (mover ? (prepPosFeita ? $" — {e.Cam.Nome} indo para {NomePos(e)}" : $" — {e.Cam.Nome} está no ar: vai para {NomePos(e)} na troca") : "") +
                ". Aperte Transição na hora.";
            Avisar();
        }

        // ---------- (0.1.9) seguir o telão do MiguelAngelus ----------
        public bool Seguir => d.Seguir;
        public int Antes => d.Antes;
        public void DefinirSeguir(bool on) { d.Seguir = on; Miguel.Seguir = on; ultimoAlvo = -1; seqVisto = -1; Gravar(); Avisar(); }
        public void DefinirAntes(int n) { d.Antes = Math.Clamp(n, 1, 5); ultimoAlvo = -1; seqVisto = -1; Gravar(); Avisar(); }
        long seqVisto = -1; int ultimoAlvo = -1;
        public string SeguirTexto { get; private set; } = "";

        static readonly Dictionary<string, string[]> sinonimos = new Dictionary<string, string[]>
        {
            ["oracao do dia"] = new[] { "oracao do dia", "coleta" },
            ["primeira leitura"] = new[] { "primeira leitura" },
            ["segunda leitura"] = new[] { "segunda leitura" },
            ["salmo"] = new[] { "salmo" },
            ["profissao de fe"] = new[] { "profissao de fe", "creio", "simbolo" },
            ["preces"] = new[] { "preces", "oracao dos fieis", "oracao universal" },
            ["ofertorio"] = new[] { "ofertorio", "oferendas", "apresentacao das oferendas" },
            ["oracao eucaristica"] = new[] { "oracao eucaristica", "prefacio", "santo" },
            ["comunhao"] = new[] { "comunhao" },
            ["bencao final"] = new[] { "bencao final", "bencao", "ritos finais" },
            ["encerramento"] = new[] { "encerramento", "canto final", "final" },
            ["ato penitencial"] = new[] { "ato penitencial", "senhor tende piedade", "kyrie" },
            ["pai nosso"] = new[] { "pai nosso", "oracao do senhor", "rito da comunhao" },   // o Rito da Comunhão começa no Pai-Nosso
        };
        public static string Normalizar(string s)
        {
            s = (s ?? "").ToLowerInvariant().Replace("1ª", "primeira ").Replace("2ª", "segunda ").Replace("1a ", "primeira ").Replace("2a ", "segunda ");
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }
            return " " + string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
        }
        IEnumerable<string> Chaves(Momento m)
        {
            if (!string.IsNullOrWhiteSpace(m.Miguel)) return m.Miguel.Split(',', ';').Select(x => Normalizar(x).Trim()).Where(x => x != "");
            var n = Normalizar(m.Nome).Trim();
            return sinonimos.TryGetValue(n, out var l) ? l : new[] { n };
        }
        // o momento do Cast que corresponde a um item do MiguelAngelus (-1 = nenhum: comentário, aviso… o momento não muda)
        // o rótulo manda (ex.: canto "Entrada"); só se ele não disser nada, vale o título (ex.: "Missa · Glória")
        public int Mapear((string rot, string tit, string tipo) it)
        {
            foreach (var texto in new[] { Normalizar(it.rot), Normalizar(it.tit) })
            {
                int melhor = -1, tam = 0;
                for (int k = 0; k < Lista.Count; k++)
                    foreach (var c in Chaves(Lista[k]))
                        if (c.Length > tam && texto.Contains(" " + c + " ")) { melhor = k; tam = c.Length; }
                if (melhor >= 0) return melhor;
            }
            return -1;
        }

        void SeguirMiguel()
        {
            var a = Miguel.AoVivo;
            if (a == null) { var t = Miguel.ErroAoVivo != "" ? "⚠ " + Miguel.ErroAoVivo : "procurando o MiguelAngelus…"; if (t != SeguirTexto) { SeguirTexto = t; Avisar(); } return; }
            if (Miguel.SeqAoVivo == seqVisto) return;
            seqVisto = Miguel.SeqAoVivo;
            if (a.VivoIdx < 0 || a.VivoIdx >= a.Itens.Count) { SeguirTexto = "MiguelAngelus: nada no telão" + (a.Roteiro != "" ? " · " + a.Roteiro : ""); Avisar(); return; }
            int mAtual = Mapear(a.Itens[a.VivoIdx]);
            int alvo = -1;
            if (mAtual >= 0 && mAtual != Agora) alvo = mAtual;                                // o telão já está num momento novo
            else if (a.Total > 0 && a.Idx >= a.Total - d.Antes)                              // faltam poucos slides: o próximo
            {
                int base_ = mAtual >= 0 ? mAtual : Agora;
                for (int j = a.VivoIdx + 1; j < a.Itens.Count; j++) { var mj = Mapear(a.Itens[j]); if (mj >= 0 && mj != base_) { alvo = mj; break; } }
            }
            SeguirTexto = $"MiguelAngelus: {a.Texto}" + (a.Total > 0 ? $" · slide {a.Idx + 1} de {a.Total}" : "") +
                (mAtual >= 0 ? $" → momento \"{Lista[mAtual].Nome}\"" : " (sem momento ligado)");
            if (alvo >= 0 && alvo != ultimoAlvo && alvo != Agora) { ultimoAlvo = alvo; Preparar(alvo); }
            else Avisar();
        }

        public void Ir(int i) => Ativar(i, null, false);
        void Ativar(int i, Escolha pronta, bool posFeita)
        {
            if (i < 0 || i >= Lista.Count) return;
            var m = Lista[i]; Agora = i; fimVideoTratado = false; acharFeito = false; acharVisto = false; acharAtivo = false; ligarSeguirEm = DateTime.MinValue;
            LimparPreparo();
            var esc = pronta ?? (m.Tipo == "video" ? null : Escolher(m));
            var cam = esc?.Cam; var tipo = esc?.Tipo ?? m.Tipo;
            tipoAgora = tipo;
            Registro.Info($"roteiro: momento {i + 1} \"{m.Nome}\" ({m.Tipo}{(tipo != m.Tipo ? " → " + tipo + " pela cena" : "")})" + (pronta != null ? " — entrou pela Transição" : ""));
            camAgora = tipo is "sem" or "video" ? null : cam;
            var r = rastreio();
            // fora do seguir/achar, a câmera não segue ninguém (fica parada onde a posição a deixar)
            if (tipo != "seguir" && tipo != "achar" && r != null && r.Real) { r.MudarSeguir(false); r.SoltarAlvo(); }

            string cena = esc?.Cena ?? m.Cena, aviso;
            if (m.Tipo == "video")
            {
                cena = CenaDoVideo(m.Video);
                aviso = string.IsNullOrWhiteSpace(m.Video) ? "Vídeo sem arquivo: escolha no Roteiro." : !File.Exists(m.Video) ? "Arquivo do vídeo não encontrado: " + m.Video : $"Tocando \"{Path.GetFileName(m.Video)}\" (o som do vídeo vai para a transmissão; a mesa abaixa sozinha).";
            }
            else
            {
                // (0.1.9) preparada com a câmera livre: ela já foi; com a câmera que estava no ar: vai agora, na troca
                bool moveAgora = TemPos(esc) && tipo != "sem" && !posFeita;
                if (moveAgora) IrPosicao(esc);
                aviso = tipo switch
                {
                    "fixo" => TemPos(esc) ? $"{cam.Nome} na {NomePos(esc)}: \"{m.Nome}\" no ar." : $"\"{m.Nome}\" no ar.",
                    _ => $"\"{m.Nome}\" no ar."
                };
                if ((tipo == "seguir" || tipo == "achar") && cam != null) aviso = ComecarRastreio(tipo, cam, esc?.Pessoa, m.Nome, moveAgora);
                else if (tipo == "seguir" || tipo == "achar") aviso = "Este momento não tem câmera escolhida (Roteiro nem na cena).";
            }
            if (!string.IsNullOrEmpty(cena) && mesa.Retrato.ContainsKey(cena)) { if (pronta == null && mesa.ProgramaAtual != cena) { mesa.Direto(cena); cenaDoRoteiro = cena; } else if (pronta != null) cenaDoRoteiro = cena; }
            else if (m.Tipo != "video") aviso = "Este momento não tem cena escolhida (Roteiro).";
            movidaNoPreview = null;
            Aviso = aviso;
            ultimoAlvo = i;
            PreposicionarProximo();
            Avisar();
        }

        // ---------- (0.1.10) rastreio a partir da posição: do momento ou da cena ----------
        bool acharAtivo; string acharRotulo = ""; DateTime ligarSeguirEm = DateTime.MinValue;
        // liga o achar/seguir nesta câmera e devolve o aviso; moveu = a câmera acabou de ser mandada para a posição
        // (aí o seguir só liga depois de 2 s, para não brigar com o caminho até lá)
        string ComecarRastreio(string tipo, CameraCfg cam, string pessoa, string rotulo, bool moveu)
        {
            acharAtivo = false; acharFeito = false; acharVisto = false; acharNome = null; acharRotulo = rotulo; ligarSeguirEm = DateTime.MinValue;
            var err = prepararRastreio(cam);
            if (err != null) return "Não deu para seguir: " + err;
            var r = rastreio();
            var (nome, erroPessoa) = PorPessoa(r, pessoa);
            if (erroPessoa != null)
            {
                if (tipo == "achar") { r.MudarSeguir(false); return "Achar: " + erroPessoa + "."; }
                r.MudarSeguir(true); return "Seguir: " + erroPessoa + ".";
            }
            if (tipo == "achar")
            {
                acharAtivo = true; r.MudarSeguir(true); r.SoltarAlvo(); acharDesde = DateTime.Now; acharNome = nome;
                if (nome != null) { r.MudarSeguir(false); acharLigado = false; return $"Câmera foi à posição; procurando {nome} para centralizar…"; }   // só mexe depois que chegar à posição
                return "Câmera foi à posição; procurando quem está lá para centralizar…";
            }
            if (moveu) { r.MudarSeguir(false); ligarSeguirEm = DateTime.Now.AddSeconds(2); } else r.MudarSeguir(true);
            if (nome != null) return $"Câmera seguindo {nome}" + (moveu ? " (começa ao chegar à posição)." : ".");
            if (!r.Cadastrado) return "Seguir: nenhum rosto cadastrado — na aba \"Câmera que segue\", cadastre (com autorização) ou clique no rosto.";
            return "Câmera seguindo o celebrante.";
        }

        void IrPosicao(Escolha e)
        {
            if (e?.Cam == null) return;
            if (e.Pos >= 0) ViscaRede.Enviar(e.Cam, new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x02, (byte)(e.Pos & 0x7F), 0xFF });
            else if (e.Coord != null) _ = ViscaRede.IrPara(e.Cam, e.Coord);
        }
        string NomePos(Escolha e) => e.Pos >= 0 ? "posição \"" + (e.Cam.Posicoes.FirstOrDefault(p => p.N == e.Pos)?.Nome ?? ("nº " + e.Pos)) + "\""
            : "posição salva na cena \"" + (mesa.CenaPara(e.Cena)?.Nome ?? "?") + "\"";

        // ---------- (0.1.10) cenas escolhidas à mão (tela, Stream Deck, celular) ----------
        // No Preview: a câmera da cena já vai para a posição salva, se não estiver no ar.
        // No ar: se ainda não foi, vai agora; e liga o rastreio da cena (achar/seguir) ou, se a cena manda ficar parada,
        // tira o rastreio dessa câmera. As cenas que o roteiro põe ele mesmo cuidam (o momento vale mais).
        bool cenasVistas; string vistoPreview, vistoPrograma, saiuDoAr, cenaDoRoteiro, movidaNoPreview;
        void CenasManuais()
        {
            var pg = mesa.ParaAtual ?? mesa.ProgramaAtual; var pv = mesa.PreviewAtual;
            if (!cenasVistas) { cenasVistas = true; vistoPreview = pv; vistoPrograma = pg; return; }   // ao abrir o programa, nada se mexe
            if (pg != vistoPrograma)
            {
                saiuDoAr = vistoPrograma; vistoPrograma = pg;
                // posta pelo roteiro, ou a preparada por ele entrando pela Transição: o momento cuida da câmera
                bool doRoteiro = pg != null && (pg == cenaDoRoteiro || (prep != null && prep.Cena == pg)); cenaDoRoteiro = null;
                if (!doRoteiro && pg != null) CenaNoAr(pg);
            }
            if (pv != vistoPreview)
            {
                vistoPreview = pv;
                // a que acabou de sair do ar volta para o Preview sozinha (mesa de corte): a câmera não se mexe por isso
                if (pv == null || pv == pg || pv == saiuDoAr || (prep != null && prep.Cena == pv)) { if (pv == saiuDoAr) saiuDoAr = null; return; }
                var cc = mesa.CenaPara(pv); var cam = Cameras.Achar(cc?.Camera);
                if (cc?.Ptz == null || cam == null || cc.Ptz.Camera != cam.Id || NoAr(cam)) return;
                PararRastreioSeFor(cam);
                _ = ViscaRede.IrPara(cam, cc.Ptz); movidaNoPreview = pv;
                Registro.Info($"cena no Preview: {cc.Nome} — {cam.Nome} indo para a posição salva");
            }
        }
        void PararRastreioSeFor(CameraCfg cam)
        {
            var r = rastreio();
            if (r != null && r.Real && r.FonteConectada == cam.Fonte && r.Cfg.Ip == cam.Ip) { r.MudarSeguir(false); r.SoltarAlvo(); acharAtivo = false; ligarSeguirEm = DateTime.MinValue; }
        }
        void CenaNoAr(string id)
        {
            var cc = mesa.CenaPara(id); var cam = Cameras.Achar(cc?.Camera);
            if (cc == null || cam == null) return;                    // cena sem câmera PTZ: o rastreio e as câmeras continuam como estão
            camAgora = null;
            bool temPos = cc.Ptz != null && cc.Ptz.Camera == cam.Id, mover = temPos && movidaNoPreview != id;
            movidaNoPreview = null;
            PararRastreioSeFor(cam);
            if (mover) _ = ViscaRede.IrPara(cam, cc.Ptz);
            string aviso = $"Cena \"{cc.Nome}\" no ar" + (mover ? $": {cam.Nome} indo para a posição salva na cena." : temPos ? $": {cam.Nome} na posição salva na cena." : ".");
            if (cc.Rastreio is "achar" or "seguir") aviso = $"Cena \"{cc.Nome}\": " + ComecarRastreio(cc.Rastreio, cam, cc.Pessoa, cc.Nome, mover);
            Registro.Info($"cena no ar (à mão): {cc.Nome}" + (mover ? " — câmera para a posição salva" : "") + (cc.Rastreio != "" ? " — " + cc.Rastreio : ""));
            Aviso = aviso; Avisar();
        }
        // botão "ir para a posição da cena" (painel da cena e Stream Deck): só a câmera, a cena não muda
        public string IrPosicaoDaCena(string id)
        {
            var cc = mesa.CenaPara(id); var cam = Cameras.Achar(cc?.Camera);
            if (cc == null) return "cena não encontrada";
            if (cam == null || cc.Ptz == null || cc.Ptz.Camera != cam.Id) return $"a cena \"{cc.Nome}\" não tem posição de câmera salva";
            PararRastreioSeFor(cam);
            _ = ViscaRede.IrPara(cam, cc.Ptz);
            return null;
        }

        // a câmera do próximo momento já vai para a posição, se não for a que está no ar agora
        void PreposicionarProximo()
        {
            // o próximo momento COM câmera (pula vídeos e "sem câmera": durante um vídeo dá tempo de arrumar a câmera)
            int j = Agora + 1;
            while (j < Lista.Count && Lista[j].Tipo is "sem" or "video") j++;
            if (j >= Lista.Count) return;
            // (0.1.9) das cenas do momento, a de câmera livre; a câmera no ar nunca se mexe aqui (apareceria)
            var e = Escolher(Lista[j]);
            if (!TemPos(e) || e.Tipo == "sem" || NoAr(e.Cam)) return;
            var atual = Agora >= 0 ? Lista[Agora] : null;
            var programa = atual == null || atual.Tipo == "video" ? mesa.ProgramaAtual : null;
            if (programa != null && !string.IsNullOrEmpty(e.Cam.Fonte) && mesa.Retrato.TryGetValue(programa, out var cn) && cn.fonte == "ndi:" + e.Cam.Fonte) return;
            IrPosicao(e);
        }

        // cena de um vídeo do roteiro: criada sozinha na primeira vez (fica na lista de cenas como "Vídeo: nome")
        string CenaDoVideo(string caminho)
        {
            if (string.IsNullOrWhiteSpace(caminho)) return null;
            var spec = "video:" + caminho;
            var achada = mesa.Retrato.FirstOrDefault(k => k.Value.fonte == spec).Key;
            if (achada != null) return achada;
            var id = "v" + (uint)spec.GetHashCode();
            mesa.NovaCena("Vídeo: " + Path.GetFileNameWithoutExtension(caminho), spec, false, id);
            mesa.Direto(id);   // a mesa cria e já põe no ar (na mesma fila)
            return null;       // já mandado ao ar
        }

        // a cada 200 ms (linha da tela)
        public void Tick()
        {
            // (0.1.9) a cena preparada entrou no ar (você apertou Transição ou Corte): o momento começa
            if (prep != null && Preparado >= 0 && Preparado < Lista.Count && mesa.ProgramaAtual == prep.Cena &&
                (prepProgAntes != prep.Cena || mesa.PedidosTroca != prepPedidos))
            { Ativar(Preparado, prep, prepPosFeita); return; }
            if (d.Seguir) SeguirMiguel();
            CenasManuais();
            // (0.1.10) seguir depois que a câmera chegou à posição
            if (ligarSeguirEm != DateTime.MinValue && DateTime.Now >= ligarSeguirEm) { ligarSeguirEm = DateTime.MinValue; var rs = rastreio(); if (rs != null && rs.Real) rs.MudarSeguir(true); }
            if (acharAtivo && !acharFeito)
            {
                var r = rastreio(); double s = (DateTime.Now - acharDesde).TotalSeconds;
                if (acharNome == null)
                {
                    if (r != null && s > 1.5 && s < 2.0) r.AcharMaior();               // a câmera chegou à posição: escolhe quem está lá
                    if (r != null && s > 7) { r.MudarSeguir(false); acharFeito = true; Aviso = $"\"{acharRotulo}\" no ar (câmera centralizada e parada)."; Avisar(); }
                }
                else if (r != null && s > 1.5)
                {
                    if (!acharLigado) { acharLigado = true; r.MudarSeguir(true); return; }
                    // (0.1.9) achar com nome: o rastreio procura o rosto cadastrado dessa pessoa e centraliza.
                    // Achou e já está parada no centro: para. Não achou em 7 s: para onde a busca terminou.
                    bool achou = r.Estado.StartsWith("seguindo (reconhecido)");
                    if (achou && !acharVisto) { acharVisto = true; acharVistoEm = DateTime.Now; }
                    bool centrada = achou && !r.Movendo && (DateTime.Now - acharVistoEm).TotalSeconds > 1.2;
                    if (centrada || (!acharVisto && s > 7) || s > 10)
                    {
                        r.MudarSeguir(false); acharFeito = true;
                        Aviso = acharVisto ? $"\"{acharRotulo}\" no ar ({acharNome} centralizado, câmera parada)."
                                           : $"\"{acharRotulo}\" no ar — {acharNome} não apareceu em 7 s; a câmera ficou parada na posição.";
                        Avisar();
                    }
                }
            }
            if (Agora < 0 || Agora >= Lista.Count) return;
            var m = Lista[Agora];
            if (m.Tipo == "video" && !fimVideoTratado)
            {
                var spec = "video:" + m.Video;
                var cena = mesa.Retrato.FirstOrDefault(k => k.Value.fonte == spec);
                if (cena.Key != null && cena.Value.estado == "terminou")
                {
                    fimVideoTratado = true;
                    if (m.Segue && Agora < Lista.Count - 1) Ir(Agora + 1);
                    else { Aviso = "O vídeo acabou e ficou parado no último quadro. Aperte \"Próximo momento\"."; Avisar(); }
                }
            }
        }
    }
}
