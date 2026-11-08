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
        sealed class Dados { public List<PerfilRoteiro> Perfis { get; set; } = new List<PerfilRoteiro>(); public string Atual { get; set; } }

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
        public void Iniciar() { Agora = -1; Aviso = "Celebração iniciada: aperte \"Próximo momento\" para o primeiro."; Registro.Info("roteiro: iniciar \"" + Perfil.Nome + "\""); PreposicionarProximo(); Avisar(); }
        public void Proximo() { if (Agora < Lista.Count - 1) Ir(Agora + 1); }
        public void Anterior() { if (Agora > 0) Ir(Agora - 1); }

        public void Ir(int i)
        {
            if (i < 0 || i >= Lista.Count) return;
            var m = Lista[i]; Agora = i; fimVideoTratado = false; acharFeito = false;
            Registro.Info($"roteiro: momento {i + 1} \"{m.Nome}\" ({m.Tipo})");
            var cam = Cameras.Achar(m.Camera);
            var r = rastreio();
            // fora do seguir/achar, a câmera não segue ninguém (fica parada onde a posição a deixar)
            if (m.Tipo != "seguir" && m.Tipo != "achar" && r != null && r.Real) { r.MudarSeguir(false); r.SoltarAlvo(); }

            string cena = m.Cena, aviso;
            if (m.Tipo == "video")
            {
                cena = CenaDoVideo(m.Video);
                aviso = string.IsNullOrWhiteSpace(m.Video) ? "Vídeo sem arquivo: escolha no Roteiro." : !File.Exists(m.Video) ? "Arquivo do vídeo não encontrado: " + m.Video : $"Tocando \"{Path.GetFileName(m.Video)}\" (o som do vídeo vai para a transmissão; a mesa abaixa sozinha).";
            }
            else
            {
                if (cam != null && m.Posicao >= 0 && m.Tipo != "sem") IrPosicao(cam, m.Posicao);
                aviso = m.Tipo switch
                {
                    "fixo" => cam != null && m.Posicao >= 0 ? $"Câmera na posição \"{NomePos(cam, m.Posicao)}\": \"{m.Nome}\" no ar." : $"\"{m.Nome}\" no ar.",
                    "achar" => "Câmera foi à posição; procurando quem está lá para centralizar…",
                    "seguir" => "Câmera seguindo o celebrante.",
                    _ => $"\"{m.Nome}\" no ar."
                };
                if ((m.Tipo == "seguir" || m.Tipo == "achar") && cam != null)
                {
                    var err = prepararRastreio(cam);
                    if (err != null) aviso = "Não deu para seguir: " + err;
                    else
                    {
                        r = rastreio();
                        r.MudarSeguir(true);
                        if (m.Tipo == "achar") { r.SoltarAlvo(); acharDesde = DateTime.Now; }
                        else if (!r.Cadastrado) aviso = "Seguir: nenhum rosto cadastrado — na aba \"Câmera que segue\", cadastre (com autorização) ou clique no rosto.";
                    }
                }
                else if (m.Tipo == "seguir" || m.Tipo == "achar") aviso = "Este momento não tem câmera escolhida (Roteiro).";
            }
            if (!string.IsNullOrEmpty(cena) && mesa.Retrato.ContainsKey(cena)) { if (mesa.ProgramaAtual != cena) mesa.Direto(cena); }
            else if (m.Tipo != "video") aviso = "Este momento não tem cena escolhida (Roteiro).";
            Aviso = aviso;
            PreposicionarProximo();
            Avisar();
        }

        void IrPosicao(CameraCfg c, int n) => ViscaRede.Enviar(c, new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x02, (byte)(n & 0x7F), 0xFF });
        static string NomePos(CameraCfg c, int n) => c.Posicoes.FirstOrDefault(p => p.N == n)?.Nome ?? ("nº " + n);

        // a câmera do próximo momento já vai para a posição, se não for a que está no ar agora
        void PreposicionarProximo()
        {
            // o próximo momento COM câmera (pula vídeos e "sem câmera": durante um vídeo dá tempo de arrumar a câmera)
            int j = Agora + 1;
            while (j < Lista.Count && Lista[j].Tipo is "sem" or "video") j++;
            if (j >= Lista.Count) return;
            var prox = Lista[j];
            if (prox.Posicao < 0) return;
            var cam = Cameras.Achar(prox.Camera); if (cam == null) return;
            var atual = Agora >= 0 ? Lista[Agora] : null;
            if (atual != null && atual.Camera == prox.Camera && atual.Tipo is not ("sem" or "video")) return;   // mesma câmera no ar: mexer apareceria
            var programa = atual == null || atual.Tipo == "video" ? mesa.ProgramaAtual : atual.Cena;
            if (programa != null && !string.IsNullOrEmpty(cam.Fonte) && mesa.Retrato.TryGetValue(programa, out var cn) && cn.fonte == "ndi:" + cam.Fonte) return;
            IrPosicao(cam, prox.Posicao);
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
            if (Agora < 0 || Agora >= Lista.Count) return;
            var m = Lista[Agora];
            if (m.Tipo == "achar" && !acharFeito && acharDesde != DateTime.MinValue)
            {
                var r = rastreio(); double s = (DateTime.Now - acharDesde).TotalSeconds;
                if (r != null && s > 1.5 && s < 2.0) r.AcharMaior();                   // a câmera chegou à posição: escolhe quem está lá
                if (r != null && s > 7) { r.MudarSeguir(false); acharFeito = true; Aviso = $"\"{m.Nome}\" no ar (câmera centralizada e parada)."; Avisar(); }
            }
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
