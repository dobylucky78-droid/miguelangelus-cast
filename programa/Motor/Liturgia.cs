// Calendário litúrgico (rito romano, com as transferências usadas no Brasil: Epifania e Ascensão no domingo).
// Dá o nome da celebração e a cor para a capa da live. Só nomes e cores: nenhum texto litúrgico.
// Simplificado de propósito: tempos, domingos, solenidades e festas principais. O usuário sempre pode escrever à mão.
// Missa de sábado à tarde (a partir das 16h) já é a do domingo.
using System;
using System.Collections.Generic;

namespace MiguelAngelusCast.Motor
{
    public static class Liturgia
    {
        public static DateTime Pascoa(int a)
        {
            int b = a % 19, c = a / 100, d = a % 100, e = c / 4, f = c % 4, g = (c + 8) / 25, h = (c - g + 1) / 3;
            int i = (19 * b + c - e - h + 15) % 30, k = d / 4, l = d % 4, m = (32 + 2 * f + 2 * k - i - l) % 7, n = (b + 11 * m + 22 * i) / 451;
            int mes = (i + m - 7 * n + 114) / 31, dia = (i + m - 7 * n + 114) % 31 + 1;
            return new DateTime(a, mes, dia);
        }
        static DateTime Advento1(int a) { var d = new DateTime(a, 11, 27); while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1); return d; }
        static DateTime Epifania(int a) { var d = new DateTime(a, 1, 2); while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1); return d; }
        static DateTime Batismo(int a) { var e = Epifania(a); return e.Day >= 7 ? e.AddDays(1) : e.AddDays(7); }
        static DateTime SagradaFamilia(int a)
        {
            for (var d = new DateTime(a, 12, 26); d.Day <= 31; d = d.AddDays(1)) if (d.DayOfWeek == DayOfWeek.Sunday) return d;
            return new DateTime(a, 12, 30);
        }

        static readonly string[] dias = { "Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado" };

        static readonly Dictionary<(int, int), (string, string)> fixas = new Dictionary<(int, int), (string, string)>
        {
            [(1, 1)] = ("Santa Maria, Mãe de Deus", "branco"),
            [(2, 2)] = ("Apresentação do Senhor", "branco"),
            [(3, 19)] = ("São José, Esposo de Maria", "branco"),
            [(3, 25)] = ("Anunciação do Senhor", "branco"),
            [(5, 1)] = ("São José Operário", "branco"),
            [(6, 24)] = ("Natividade de São João Batista", "branco"),
            [(6, 29)] = ("São Pedro e São Paulo, Apóstolos", "vermelho"),
            [(8, 6)] = ("Transfiguração do Senhor", "branco"),
            [(8, 15)] = ("Assunção de Nossa Senhora", "branco"),
            [(9, 14)] = ("Exaltação da Santa Cruz", "vermelho"),
            [(10, 12)] = ("Nossa Senhora da Conceição Aparecida", "branco"),
            [(11, 1)] = ("Todos os Santos", "branco"),
            [(11, 2)] = ("Comemoração de todos os fiéis defuntos", "roxo"),
            [(12, 8)] = ("Imaculada Conceição de Nossa Senhora", "branco"),
            [(12, 25)] = ("Natal do Senhor", "branco"),
        };

        /// <summary>Nome da celebração e cor (verde, roxo, branco, vermelho, rosa) para a missa nesse dia e hora.</summary>
        public static (string nome, string cor) Info(DateTime quando)
        {
            var d = quando.Date;
            if (d.DayOfWeek == DayOfWeek.Saturday && quando.Hour >= 16 && !(d.Month == 12 && d.Day == 24)) d = d.AddDays(1);   // missa da vigília do domingo
            int a = d.Year;
            var pas = Pascoa(a); var cinzas = pas.AddDays(-46); var pent = pas.AddDays(49);
            var adv = Advento1(a); var rei = adv.AddDays(-7);
            bool dom = d.DayOfWeek == DayOfWeek.Sunday;
            string Ord(int n, bool f) => n + (f ? "ª" : "º");

            // dias próprios que vencem tudo
            if (d == cinzas) return ("Quarta-feira de Cinzas", "roxo");
            if (d == pas.AddDays(-7)) return ("Domingo de Ramos da Paixão do Senhor", "vermelho");
            if (d == pas.AddDays(-3)) return ("Quinta-feira Santa · Ceia do Senhor", "branco");
            if (d == pas.AddDays(-2)) return ("Sexta-feira Santa · Paixão do Senhor", "vermelho");
            if (d == pas.AddDays(-1)) return ("Vigília Pascal", "branco");
            if (d == pas) return ("Domingo de Páscoa da Ressurreição do Senhor", "branco");
            if (d == pas.AddDays(42)) return ("Ascensão do Senhor", "branco");
            if (d == pent) return ("Domingo de Pentecostes", "vermelho");
            if (d == pent.AddDays(7)) return ("Santíssima Trindade", "branco");
            if (d == pent.AddDays(11)) return ("Santíssimo Corpo e Sangue de Cristo", "branco");
            if (d == pent.AddDays(19)) return ("Sagrado Coração de Jesus", "branco");
            if (d == rei) return ("Nosso Senhor Jesus Cristo, Rei do Universo", "branco");
            if (d == Epifania(a)) return ("Epifania do Senhor", "branco");
            if (d == Batismo(a)) return ("Batismo do Senhor", "branco");
            if (d == SagradaFamilia(a)) return ("Sagrada Família de Jesus, Maria e José", "branco");

            // tempos fortes: o domingo vence as festas fixas
            bool advento = d >= adv && d < new DateTime(a, 12, 25);
            bool quaresma = d > cinzas && d < pas.AddDays(-7);
            bool pascal = d > pas && d < pent;
            if (fixas.TryGetValue((d.Month, d.Day), out var fx) && !(dom && (advento || quaresma || pascal))) return fx;

            if (advento)
            {
                int n = (int)((d - adv).TotalDays / 7) + 1;
                if (dom) return (Ord(n, false) + " Domingo do Advento", n == 3 ? "rosa" : "roxo");
                return (dias[(int)d.DayOfWeek] + " da " + Ord(n, true) + " Semana do Advento", "roxo");
            }
            if (d >= new DateTime(a, 12, 25) || d <= Batismo(a)) return (dom ? "Domingo do Tempo do Natal" : dias[(int)d.DayOfWeek] + " do Tempo do Natal", "branco");
            if (d > cinzas && d < pas)
            {
                var q1 = pas.AddDays(-42);
                if (d < q1) return (dias[(int)d.DayOfWeek] + " depois das Cinzas", "roxo");
                int n = (int)((d - q1).TotalDays / 7) + 1;
                if (n >= 6) return (dias[(int)d.DayOfWeek] + " da Semana Santa", "roxo");
                if (dom) return (Ord(n, false) + " Domingo da Quaresma", n == 4 ? "rosa" : "roxo");
                return (dias[(int)d.DayOfWeek] + " da " + Ord(n, true) + " Semana da Quaresma", "roxo");
            }
            if (pascal)
            {
                int n = (int)((d - pas).TotalDays / 7) + 1;
                if (dom) return (Ord(n, false) + " Domingo da Páscoa", "branco");
                return (dias[(int)d.DayOfWeek] + (n == 1 ? " da Oitava da Páscoa" : " da " + Ord(n, true) + " Semana da Páscoa"), "branco");
            }
            // Tempo Comum
            int sem;
            if (d < cinzas)
            {
                var b = Batismo(a); var dom2 = b.DayOfWeek == DayOfWeek.Sunday ? b.AddDays(7) : b.AddDays(6);
                sem = d < dom2 ? 1 : 2 + (int)((d - dom2).TotalDays / 7);
            }
            else
            {
                var domDe = d.AddDays(-(int)d.DayOfWeek);
                sem = 34 - (int)((rei - domDe).TotalDays / 7);
            }
            if (dom) return (Ord(sem, false) + " Domingo do Tempo Comum", "verde");
            return (dias[(int)d.DayOfWeek] + " da " + Ord(sem, true) + " Semana do Tempo Comum", "verde");
        }
    }
}
