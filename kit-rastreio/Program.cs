using System;
using System.IO;
using System.Windows.Forms;

static class Program
{
    public static readonly string Pasta = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string PastaModelos = Path.Combine(Pasta, "modelos");

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Janela());
    }
}
