using System;
using System.IO;
using System.Windows.Forms;

namespace MiguelAngelusCast
{
    static class Program
    {
        public static readonly string Pasta = AppDomain.CurrentDomain.BaseDirectory;
        // dados deste computador (cenas, ajustes, registro): fora da pasta do programa e fora da nuvem
        public static readonly string PastaDados = Environment.GetEnvironmentVariable("CAST_DADOS") is string d && d != "" ? d   // só para testes
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiguelAngelusCast");
        public static readonly string Versao = typeof(Program).Assembly.GetName().Version.ToString(3);

        [STAThread]
        static int Main(string[] args)
        {
            // (0.1.5) botão do Stream Deck/Stream Dock: manda o comando para o Cast aberto e sai, sem abrir janela
            if (args.Length > 0 && (args[0] == "/botao" || args[0] == "--botao")) return Motor.Controle.Botao(args);
            Directory.CreateDirectory(PastaDados);
            Registro.Iniciar();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Registro.Erro("não tratado", e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => Registro.Erro("tela", e.Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Principal());
            // com o rastreio aberto, encerra na hora (desmontar NDI e modelos de rosto prendia o kit ao fechar)
            if (Rastreio.Rastreador.Usado) Environment.Exit(0);
            return 0;
        }
    }

    static class Registro
    {
        static readonly object trava = new object();
        static string arquivo;
        public static void Iniciar()
        {
            var pasta = Path.Combine(Program.PastaDados, "registro");
            Directory.CreateDirectory(pasta);
            arquivo = Path.Combine(pasta, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
            Info("MiguelAngelus Cast " + Program.Versao + " abriu");
        }
        public static void Info(string t)
        {
            try { lock (trava) File.AppendAllText(arquivo, DateTime.Now.ToString("HH:mm:ss.fff ") + t + Environment.NewLine); } catch { }
        }
        public static void Erro(string onde, Exception e) => Info("ERRO (" + onde + "): " + e);
    }
}
