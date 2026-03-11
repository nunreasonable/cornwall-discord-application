using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CornwallUtilities.config
{
    internal class JSONReader
    {
        public string? token { get; private set; }
        public string? prefix { get; private set; }
        public string? spreadsheetCsvUrl { get; private set; }
        public string? spreadsheetRosterCsvUrl { get; private set; }
        public string? defaultGameLink { get; private set; }

        public async Task ReadJSON()
        {
            using var sr = new StreamReader("config.json");
            var json = await sr.ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<JSONStructure>(json);

            token = data?.token;
            prefix = data?.prefix;
            spreadsheetCsvUrl = data?.spreadsheetCsvUrl;
            spreadsheetRosterCsvUrl = data?.spreadsheetRosterCsvUrl;
            defaultGameLink = data?.defaultGameLink;
        }
    }

    internal sealed class JSONStructure
    {
        public string? token { get; set; }
        public string? prefix { get; set; }
        public string? spreadsheetCsvUrl { get; set; }
        public string? spreadsheetRosterCsvUrl { get; set; }
        public string? defaultGameLink { get; set; }
    }
}
