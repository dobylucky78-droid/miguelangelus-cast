// Backup dos ajustes do Cast (Ajustes › Backup e sobre): um .zip com os arquivos de ajustes da pasta de dados.
// Ficam DE FORA, de propósito:
//  - chaves.dat: as chaves de transmissão (criptografadas só para este computador e esta conta; não abririam em outro)
//  - rostos / cadastros do rastreio: biometria fica só neste computador
//  - a ilustração do folheto (capa\folheto-*): costuma ter direitos autorais
//  - registro, telemetria e WebView2: só servem a este computador
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MiguelAngelusCast
{
    static class Backup
    {
        static readonly string[] ajustes = { "mesa.json", "saida.json", "som.json", "cameras.json", "roteiros.json", "capa.json", "rastreio.json", "joystick.json" };

        public static int Exportar(string destino)
        {
            int n = 0;
            if (File.Exists(destino)) File.Delete(destino);
            using var zip = ZipFile.Open(destino, ZipArchiveMode.Create);
            foreach (var a in ajustes)
            {
                var f = Path.Combine(Program.PastaDados, a);
                if (File.Exists(f)) { zip.CreateEntryFromFile(f, a); n++; }
            }
            var capa = Path.Combine(Program.PastaDados, "capa");
            if (Directory.Exists(capa))
                foreach (var f in Directory.GetFiles(capa).Where(f => !Path.GetFileName(f).StartsWith("folheto", StringComparison.OrdinalIgnoreCase)))
                { zip.CreateEntryFromFile(f, "capa/" + Path.GetFileName(f)); n++; }
            var leia = zip.CreateEntry("LEIA-ME.txt");
            using (var w = new StreamWriter(leia.Open()))
                w.Write("Backup dos ajustes do MiguelAngelus Cast " + Program.Versao + " (" + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ").\r\n\r\n" +
                        "Não vão no backup: as chaves de transmissão (digite de novo no outro computador), os cadastros de rosto do rastreio,\r\n" +
                        "a ilustração do folheto da capa (direitos autorais) e os registros.\r\n" +
                        "Para restaurar: Ajustes › Backup e sobre › Restaurar um backup.\r\n");
            return n;
        }

        public static int Importar(string origem)
        {
            using var zip = ZipFile.OpenRead(origem);
            var validos = zip.Entries.Where(e => ajustes.Contains(e.FullName) || (e.FullName.StartsWith("capa/") && e.Name != "" && !e.Name.Contains("..")
                && new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))).ToList();
            if (!validos.Any(e => ajustes.Contains(e.FullName))) throw new InvalidDataException("este arquivo não é um backup do Cast");
            // guarda os ajustes atuais antes de trocar
            var antes = Path.Combine(Program.PastaDados, "antes-de-restaurar-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".zip");
            Exportar(antes);
            int n = 0;
            foreach (var e in validos)
            {
                var destino = e.FullName.StartsWith("capa/") ? Path.Combine(Program.PastaDados, "capa", e.Name) : Path.Combine(Program.PastaDados, e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destino));
                e.ExtractToFile(destino, true); n++;
            }
            return n;
        }
    }
}
