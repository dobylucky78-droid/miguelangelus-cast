// Cadastros de rosto guardados (Câmera que segue › Cadastros guardados): o celebrante não precisa cadastrar toda Missa.
// Regras (biometria):
//  - só com o consentimento da própria pessoa, registrado com data e o texto do termo que ela aceitou;
//  - guarda só a assinatura do rosto (128 números), nunca imagem; outros rostos nunca são guardados;
//  - criptografado pelo Windows (DPAPI): só abre neste computador, nesta conta; não vai para backup, nuvem nem repositório;
//  - "Apagar" remove na hora, de vez.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MiguelAngelusCast.Rastreio
{
    public sealed class Pessoa
    {
        public string Id { get; set; } = "p" + DateTime.Now.Ticks.ToString("x");
        public string Nome { get; set; } = "";
        public string Funcao { get; set; } = "";              // ex.: pároco, vigário, diácono
        public float[] Assinatura { get; set; } = Array.Empty<float>();
        public DateTime Consentiu { get; set; }
        public string Termo { get; set; } = "";
    }

    public static class Pessoas
    {
        public const string TermoVersao = "1";
        public static string Termo(string nome) =>
            $"Eu, {nome}, autorizo a Pascom desta paróquia a guardar neste computador a assinatura do meu rosto (uma lista de números, " +
            "sem foto) para que a câmera da transmissão me acompanhe nas celebrações. Sei que ela fica criptografada só neste computador, " +
            "não vai para a internet nem para backups, não é usada para outra finalidade e pode ser apagada a qualquer momento, quando eu pedir.";

        static readonly string arquivo = Path.Combine(Program.PastaDados, "rostos.dat");
        static readonly object trava = new object();
        static List<Pessoa> lista;

        static List<Pessoa> Lista()
        {
            if (lista != null) return lista;
            lista = new List<Pessoa>();
            try { if (File.Exists(arquivo)) lista = JsonSerializer.Deserialize<List<Pessoa>>(Encoding.UTF8.GetString(Cofre.Proteger(File.ReadAllBytes(arquivo), false))) ?? lista; }
            catch (Exception e) { Registro.Info("cadastros de rosto ilegíveis (outro computador ou outra conta?): " + e.Message); }
            return lista;
        }
        static void Gravar()
        {
            if (lista.Count == 0) { try { File.Delete(arquivo); } catch { } return; }
            File.WriteAllBytes(arquivo, Cofre.Proteger(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lista)), true));
        }

        public static object Resumo() { lock (trava) return Lista().Select(p => new { id = p.Id, nome = p.Nome, funcao = p.Funcao, desde = p.Consentiu.ToString("dd/MM/yyyy") }).ToList(); }
        public static Pessoa Achar(string id) { lock (trava) return Lista().FirstOrDefault(p => p.Id == id); }

        public static string Guardar(string nome, string funcao, float[] assinatura)
        {
            nome = (nome ?? "").Trim(); if (nome == "") return "Escreva o nome da pessoa.";
            if (assinatura == null || assinatura.Length != 128) return "Cadastre o rosto primeiro (botão Cadastrar, com a pessoa olhando para a câmera).";
            lock (trava)
            {
                var l = Lista(); var p = l.FirstOrDefault(x => string.Equals(x.Nome, nome, StringComparison.OrdinalIgnoreCase)) ?? new Pessoa();
                p.Nome = nome; p.Funcao = (funcao ?? "").Trim(); p.Assinatura = (float[])assinatura.Clone(); p.Consentiu = DateTime.Now; p.Termo = "v" + TermoVersao + ": " + Termo(nome);
                if (!l.Contains(p)) l.Add(p);
                Gravar();
            }
            Registro.Info("cadastro de rosto guardado com consentimento");
            return null;
        }
        public static void Apagar(string id)
        {
            lock (trava) { var l = Lista(); var p = l.FirstOrDefault(x => x.Id == id); if (p == null) return; l.Remove(p); Gravar(); Registro.Info("cadastro de rosto apagado"); }
        }
    }
}
