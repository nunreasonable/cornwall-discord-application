using System.Collections.Generic;
using System.Text;

namespace CornwallUtilities.Services
{
    /// <summary>
    /// Parser de CSV compartilhado (planilhas do Google exportadas como CSV).
    /// </summary>
    internal static class CsvUtil
    {
        /// <summary>
        /// Le o CSV inteiro em registros.
        ///
        /// Precisa varrer o texto todo em vez de quebrar por linha antes: um campo
        /// entre aspas pode conter QUEBRA DE LINHA, e a planilha do regimento tem
        /// exatamente isso (ex.: o campo "sr.miojao\n"). Dividindo por linha, o
        /// registro era partido ao meio e o jogador perdia cargo, batalhas e K/D/A.
        ///
        /// Linhas em branco sao descartadas, como no comportamento anterior.
        /// </summary>
        public static List<List<string>> ParseCsv(string? text)
        {
            var records = new List<List<string>>();
            if (string.IsNullOrEmpty(text))
                return records;

            var record = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            void EndField()
            {
                record.Add(field.ToString());
                field.Clear();
            }

            void EndRecord()
            {
                EndField();

                // Ignora linhas totalmente vazias.
                if (record.Count > 1 || record[0].Length > 0)
                    records.Add(new List<string>(record));

                record.Clear();
            }

            for (var i = 0; i < text!.Length; i++)
            {
                var ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // Aspas duplicadas dentro do campo representam uma aspa literal.
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                            continue;
                        }

                        inQuotes = false;
                        continue;
                    }

                    field.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        continue;
                    case ',':
                        EndField();
                        continue;
                    case '\r':
                        // Trata \r\n e \r soltos como um unico fim de registro.
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                            i++;
                        EndRecord();
                        continue;
                    case '\n':
                        EndRecord();
                        continue;
                    default:
                        field.Append(ch);
                        continue;
                }
            }

            // Ultimo registro, quando o arquivo nao termina com quebra de linha.
            if (field.Length > 0 || record.Count > 0)
                EndRecord();

            return records;
        }

        /// <summary>
        /// Le uma unica linha de CSV. Use ParseCsv quando tiver o texto completo -
        /// esta sobrecarga nao consegue lidar com quebras de linha dentro de aspas.
        /// </summary>
        public static List<string> ParseCsvLine(string line)
        {
            var records = ParseCsv(line);
            return records.Count > 0 ? records[0] : new List<string>();
        }
    }
}
