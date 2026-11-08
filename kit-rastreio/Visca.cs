// Controle da câmera PTZ por VISCA cru sobre TCP (a câmera da Matriz responde na porta 5678)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

sealed class Visca : IDisposable
{
    TcpClient cli; NetworkStream fluxo; Thread leitor; readonly object trava = new object();
    readonly Action<string> evento;
    public volatile int Zoom = -1;          // posição do zoom, se a câmera responder
    public volatile string UltimaPosicao;   // resposta crua da posição pan/tilt
    volatile string perguntando = "";
    public bool Conectado => cli != null && cli.Connected;

    public Visca(Action<string> evento) { this.evento = evento; }

    public bool Conectar(string ip, int porta)
    {
        Desconectar();
        try
        {
            var c = new TcpClient();
            var ar = c.BeginConnect(ip, porta, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(2500)) { c.Close(); evento($"VISCA: {ip}:{porta} não respondeu em 2,5 s"); return false; }
            c.EndConnect(ar); c.NoDelay = true; c.LingerState = new LingerOption(true, 0);   // fechar na hora, sem esperar a rede
            cli = c; fluxo = c.GetStream();
            leitor = new Thread(Ler) { IsBackground = true, Name = "visca" }; leitor.Start();
            evento($"VISCA: conectado em {ip}:{porta}");
            return true;
        }
        catch (Exception e) { evento($"VISCA: falhou ao conectar em {ip}:{porta}: {e.Message}"); return false; }
    }

    void Ler()
    {
        var buf = new byte[256]; var msg = new List<byte>();
        try
        {
            while (cli != null)
            {
                int n = fluxo.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    msg.Add(buf[i]);
                    if (buf[i] != 0xFF) continue;
                    var m = msg.ToArray(); msg.Clear();
                    // 90 4y FF (recebido) e 90 5y FF (feito) são rotina: não registra
                    if (m.Length == 3 && m[0] == 0x90 && (m[1] & 0xF0) is 0x40 or 0x50) continue;
                    string hex = string.Join(" ", m.Select(b => b.ToString("X2")));
                    if (m.Length == 7 && m[0] == 0x90 && m[1] == 0x50 && perguntando == "zoom")
                    { Zoom = (m[2] & 15) << 12 | (m[3] & 15) << 8 | (m[4] & 15) << 4 | (m[5] & 15); perguntando = ""; continue; }
                    if (m[0] == 0x90 && m[1] == 0x50 && perguntando == "posicao") { UltimaPosicao = hex; perguntando = ""; continue; }
                    evento("VISCA resposta: " + hex + (perguntando != "" ? " (pergunta: " + perguntando + ")" : ""));
                }
            }
        }
        catch { }
        if (cli != null) evento("VISCA: conexão caiu");
    }

    bool Enviar(params byte[] b)
    {
        lock (trava)
        {
            if (fluxo == null) return false;
            try { fluxo.Write(b, 0, b.Length); return true; }
            catch (Exception e) { evento("VISCA: erro ao enviar: " + e.Message); Desconectar(); return false; }
        }
    }

    // pan: 1 esquerda, 2 direita, 3 parado; tilt: 1 cima, 2 baixo, 3 parado; velocidades pan 1-24, tilt 1-20
    public bool Mover(int velPan, int velTilt, int dirPan, int dirTilt) =>
        Enviar(0x81, 0x01, 0x06, 0x01, (byte)Math.Min(24, Math.Max(1, velPan)), (byte)Math.Min(20, Math.Max(1, velTilt)), (byte)dirPan, (byte)dirTilt, 0xFF);

    public bool Parar() => Mover(1, 1, 3, 3);
    public void PerguntarZoom() { perguntando = "zoom"; Enviar(0x81, 0x09, 0x04, 0x47, 0xFF); }
    public void PerguntarPosicao() { perguntando = "posicao"; Enviar(0x81, 0x09, 0x06, 0x12, 0xFF); }

    public void Desconectar()
    {
        var c = cli; cli = null;
        if (c != null) { try { fluxo?.Write(new byte[] { 0x81, 0x01, 0x06, 0x01, 1, 1, 3, 3, 0xFF }, 0, 9); } catch { } try { c.Close(); } catch { } }
        fluxo = null;
    }

    public void Dispose() => Desconectar();
}
