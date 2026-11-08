// Perfis de mesa: como cada mesa apresenta as fontes que chegam ao computador pela USB.
// O Windows só numera (1, 2, 3…); o perfil traduz para o jeito da mesa. Quando a mesa está na rede,
// os nomes dados nela (ex.: "AUX 6 · Transmissão") entram por cima (próxima etapa).
// Cada tabela só entra depois de conferida no manual ou na própria mesa: nada de numeração chutada.
using System.Collections.Generic;

namespace MiguelAngelusCast.Motor
{
    public static class Perfis
    {
        public sealed class Perfil
        {
            public string id { get; set; }
            public string nome { get; set; }
            public string reconhecer { get; set; }   // trecho do nome do dispositivo no Windows
            public string aviso { get; set; }
        }

        public static readonly List<Perfil> Lista = new List<Perfil>
        {
            new Perfil { id = "generico", nome = "Genérico (Entrada 1, 2, 3…)" },
            // Ui24R: mapa fixo da USB (não tem roteamento para configurar): 1-2 Master L/R, 3-10 Aux 1-8, 11-32 canais 1-22.
            // No Windows (driver comum) aparece como "Soundcraft MADI-USB Combo card", em pares: "Mic/Line In 07/08" = USB 7 e 8.
            new Perfil { id = "ui24r", nome = "Soundcraft Ui24R", reconhecer = "Soundcraft",
                aviso = "Na Ui24R a USB é fixa: 1-2 Master, 3-10 Aux 1 a 8, 11-32 canais 1 a 22. Atenção: cada par do Windows junta duas aux (ex.: 07/08 = Aux 5 + Aux 6); para uma aux só, escolha o canal dela em mono." },
        };

        // nome de cada entrada (1-based) para a tela
        // com a mesa conectada pela rede, entra o nome dado nela: "USB 8 · Aux 6 · LIVE"
        public static string Rotulo(string perfil, int n, MesaUi mesa = null)
        {
            if (perfil != "ui24r") return "Entrada " + n;
            string Nome(string s) => string.IsNullOrWhiteSpace(s) ? "" : " · " + s.Trim();
            if (n <= 2) return $"USB {n} · Master {(n == 1 ? "L" : "R")}";
            if (n <= 10) return $"USB {n} · Aux {n - 2}" + Nome(mesa?.NomeAux(n - 2));
            if (n <= 32) return $"USB {n} · Canal {n - 10}" + Nome(mesa?.NomeCanal(n - 10));
            return "USB " + n;
        }

        // número da aux (1-8) que chega nesse número USB, ou 0
        public static int AuxDaUsb(string perfil, int usb) => perfil == "ui24r" && usb >= 3 && usb <= 10 ? usb - 2 : 0;

        // o driver comum do Windows divide a USB em pares com o número no nome ("Mic/Line In 07/08"):
        // devolve quantos canais pular para a entrada 1 do dispositivo virar o número USB certo
        public static int Deslocamento(string nomeDispositivo)
        {
            var m = System.Text.RegularExpressions.Regex.Match(nomeDispositivo ?? "", @"\b(\d{1,2})\s*/\s*(\d{1,2})\b");
            return m.Success && int.TryParse(m.Groups[1].Value, out int a) && a >= 1 ? a - 1 : 0;
        }
    }
}
