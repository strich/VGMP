using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using static Crayon.Output;

namespace ROMsLinksParser
{
    public class RedumpGamesDbItem
    {
        public int ID { get; set; }
        public string GameTitle { get; set; }
        public string AlternativeGameTitle { get; set; }
        public string DiscID { get; set; }
        public string Platform { get; set; }
        public string Region { get; set; }
        public string Language { get; set; }
        public string Version { get; set; }
        public string FileCRC { get; set; }
        public int MobyID { get; set; }
        public int MobyIDConfidence { get; set; }
        public int MobyIDVerifiedCount { get; set; }
        public string SourceDbLastUpdatedUTC { get; set; }
        public string LastUpdatedUTC { get; set; }
        public string FileName { get; set; }
        public long FileSizeBytes { get; set; }
        public string Category { get; set; }
    }

    public class RedumpScraper
    {
        private const string BaseUrl = "http://redump.org/discs/system/pc/?page=";
        public readonly ConcurrentBag<RedumpGamesDbItem> GamesList = new();
        private readonly HashSet<int> _existingGameIDs = new();
        private readonly string _csvFilePath = "RedumpGamesDb.csv";
        private readonly SemaphoreSlim rateLimiter;

        public RedumpScraper(string csvFilePath, int maxConcurrentRequests = 5)
        {
            _csvFilePath = csvFilePath;
            rateLimiter = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
            LoadFromCsv();
        }

        public async Task ScrapeGameDataAsync(int gameCountLimit, bool ignoreSkip = false)
        {
            int gamesScraped = 0;
            int page = 1;

            LoadFromCsv();

            try
            {
                while(gamesScraped < gameCountLimit)
                {
                    string url = $"{BaseUrl}{page}";
                    Console.WriteLine(Bold($"Scraping page {page}"));

                    var gameLinks = await GetGameLinksAsync(url);

                    if(gameLinks == null || gameLinks.Count == 0)
                    {
                        Console.WriteLine("No more games to scrape.");
                        break;
                    }

                    var tasks = new List<Task>();

                    foreach(var link in gameLinks)
                    {
                        if(gamesScraped >= gameCountLimit) break;

                        string gameUrl = $"http://redump.org{link.GetAttributeValue("href", "")}";
                        var idMatch = Regex.Match(gameUrl, @"\/disc\/(\d+)\/");

                        if(idMatch.Success)
                        {
                            int gameID = int.Parse(idMatch.Groups[1].Value);

                            if(!ignoreSkip && _existingGameIDs.Contains(gameID))
                            {
                                Console.WriteLine(Dim($"Game ID {gameID} already scraped, skipping."));
                                continue;
                            }

                            tasks.Add(ScrapeGameDataAsync(gameUrl));
                            _existingGameIDs.Add(gameID);
                            gamesScraped++;
                        }
                    }

                    await Task.WhenAll(tasks);
                    page++;
                }
            } catch(Exception ex)
            { 
                Console.WriteLine(ex.ToString());
            }

            SaveToCsv();

            Console.WriteLine($"Scraping complete. Total games scraped: {gamesScraped}");
        }

        private async Task<HtmlNodeCollection> GetGameLinksAsync(string url)
        {
            await rateLimiter.WaitAsync();
            try
            {
                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(url);
                return doc.DocumentNode.SelectNodes("//a[contains(@href, '/disc/')]");
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        private async Task ScrapeGameDataAsync(string gameUrl)
        {
            await rateLimiter.WaitAsync();
            try
            {
                Console.WriteLine($"Scraping game data from {gameUrl}");

                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(gameUrl);

                bool GotPageError() => doc.Text.Contains("<h1>Internal Server Error</h1>", StringComparison.OrdinalIgnoreCase);
                if(GotPageError())
                {
                    // Try again:
                    doc = await web.LoadFromWebAsync(gameUrl);
                    if(GotPageError())
                    {
                        Console.WriteLine($"    ERROR: Failed to scrape game data from {gameUrl}");
                        return;
                    }
                }

                var gameData = new RedumpGamesDbItem();

                ExtractGameID(gameUrl, gameData);
                ExtractGameTitleAndDiscID(doc, gameData);
                ExtractGameInfo(doc, gameData);
                ExtractTrackData(doc, gameData);
                ExtractAlternativeTitle(doc, gameData);
                await ExtractGameName(gameData);

                gameData.LastUpdatedUTC = DateTime.UtcNow.ToString("o");

                GamesList.Add(gameData);
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        private async Task ExtractGameName(RedumpGamesDbItem gameData)
        {
            var url = $"http://redump.org/disc/{gameData.ID}/sfv/";
            try
            {
                using HttpClient client = new HttpClient();
                HttpResponseMessage response = await client.GetAsync(url);

                if(response.IsSuccessStatusCode)
                {
                    var sfvString = await response.Content.ReadAsStringAsync();
                    Regex regex = new Regex(@"(.+)(\.(?:iso|cue)) (.+)");

                    MatchCollection matches = regex.Matches(sfvString);

                    foreach(Match match in matches)
                    {
                        if(match.Success)
                        {
                            string fileName = match.Groups[1].Value.Trim();
                            string fileExt = match.Groups[2].Value.Trim();
                            string fileCRC = match.Groups[3].Value.Trim();

                            gameData.FileName = fileName;
                            gameData.FileCRC = fileCRC;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"    Failed to retrieve data. Status code: {response.StatusCode}");
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"    Exception occurred: {ex.Message}");
            }
        }

        private void ExtractGameID(string gameUrl, RedumpGamesDbItem gameData)
        {
            var idMatch = Regex.Match(gameUrl, @"\/disc\/(\d+)\/");
            if (idMatch.Success)
            {
                gameData.ID = int.Parse(idMatch.Groups[1].Value);
            }
        }

        private void ExtractGameTitleAndDiscID(HtmlDocument doc, RedumpGamesDbItem gameData)
        {
            var gameTitleNode = doc.DocumentNode.SelectSingleNode("//h1");
            if (gameTitleNode != null)
            {
                string titleWithDisc = gameTitleNode.InnerText.Trim();
                var splitTitle = titleWithDisc.Split("(Disc");
                gameData.GameTitle = WebUtility.HtmlDecode(splitTitle[0].Trim());
                if (splitTitle.Length > 1)
                {
                    gameData.DiscID = splitTitle[1].Replace(")", "").Trim();
                }
            }
        }

        private void ExtractGameInfo(HtmlDocument doc, RedumpGamesDbItem gameData)
        {
            var tableRows = doc.DocumentNode.SelectNodes("//table[@class='gameinfo']//tr");

            foreach (var row in tableRows)
            {
                var thNode = row.SelectSingleNode("th");
                var tdNode = row.SelectSingleNode("td");

                if (thNode != null && tdNode != null)
                {
                    string th = thNode.InnerText.Trim();
                    string td = tdNode.InnerText.Trim();

                    switch (th)
                    {
                        case "System":
                            gameData.Platform = td;
                            break;
                        case "Region":
                            var regionImg = tdNode.SelectSingleNode(".//img");
                            if (regionImg != null)
                            {
                                gameData.Region = regionImg.GetAttributeValue("title", string.Empty).Trim();
                            }
                            break;
                        case "Languages":
                            var languageImg = tdNode.SelectSingleNode(".//img");
                            if (languageImg != null)
                            {
                                gameData.Language = languageImg.GetAttributeValue("title", string.Empty).Trim();
                            }
                            break;
                        case "Version":
                            gameData.Version = td;
                            break;
                        case "Last modified":
                            gameData.SourceDbLastUpdatedUTC = td;
                            break;
                        case "Added":
                            if (string.IsNullOrEmpty(gameData.SourceDbLastUpdatedUTC))
                                gameData.SourceDbLastUpdatedUTC = td;
                            break;
                        case "Category":
                            gameData.Category = td;
                            break;
                    }
                }
            }
        }

        long ParseTableAndGetTotalSize(HtmlNode htmlDoc)
        {
            // Find all rows in the table
            //var rows = htmlDoc.SelectNodes("//tr");

            //long totalSize = 0;
            //bool totalRowExists = false;

            //foreach(var row in rows)
            //{
            //    var cells = row.SelectNodes("td");
            //    if(cells == null || cells.Count < 6)
            //        continue;

            //    // Check for "Total" row
            //    if(cells[1].InnerText.Contains("Total", StringComparison.OrdinalIgnoreCase))
            //    {
            //        // Parse total size if it exists
            //        totalRowExists = true;
            //        if(long.TryParse(cells[5].InnerText.Trim(), out long total))
            //        {
            //            totalSize = total;
            //        }
            //        break;
            //    }

            //    // Otherwise, accumulate track sizes
            //    if(long.TryParse(cells[5].InnerText.Trim(), out long size))
            //    {
            //        totalSize += size;
            //    }
            //}

            // Find all rows in the table
            var rows = htmlDoc.SelectNodes("tr");

            if(rows == null || rows.Count == 0)
            {
                Console.WriteLine(" No rows found.");
                return 0;
            }

            // Find the header row and locate the "Size" column
            var headerCells = rows[1].SelectNodes("th");
            if(headerCells == null)
            {
                Console.WriteLine(" No header found.");
                return 0;
            }

            int sizeColumnIndex = -1;
            for(int i = 0; i < headerCells.Count; i++)
            {
                if(headerCells[i].InnerText.Trim().Equals("Size", StringComparison.OrdinalIgnoreCase))
                {
                    sizeColumnIndex = i;
                    break;
                }
            }

            if(sizeColumnIndex == -1)
            {
                Console.WriteLine(" Size column not found.");
                return 0;
            }

            long totalSize = 0;
            bool totalRowExists = false;

            // Start iterating from the third row as the first two are header rows
            for(int i = 2; i < rows.Count; i++)
            {
                var cells = rows[i].SelectNodes("td");
                if(cells == null || cells.Count <= sizeColumnIndex)
                    continue;

                // Check for "Total" row
                if(cells[1].InnerText.Contains("Total", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse total size if it exists
                    totalRowExists = true;
                    if(long.TryParse(cells[sizeColumnIndex].InnerText.Trim(), out long total))
                    {
                        totalSize = total;
                    }
                    break;
                }

                // Otherwise, accumulate track sizes
                if(long.TryParse(cells[sizeColumnIndex].InnerText.Trim(), out long size))
                {
                    totalSize += size;
                }
            }

            return totalSize;
        }

        private void ExtractTrackData(HtmlDocument doc, RedumpGamesDbItem gameData)
        {
            var trackTable = doc.DocumentNode.SelectSingleNode("//table[@class='tracks']");
            var totalSize = ParseTableAndGetTotalSize(trackTable);
            gameData.FileSizeBytes = totalSize;
            //var trackTableHeaders = doc.DocumentNode.SelectNodes("//table[@class='tracks']//tr[2]/th");
            //var trackDataRows = doc.DocumentNode.SelectNodes("//table[@class='tracks']//tr[position() > 2]");

            //if (trackTableHeaders != null && trackDataRows != null)
            //{
            //    Dictionary<string, int> columnMap = new Dictionary<string, int>();

            //    for (int i = 0; i < trackTableHeaders.Count; i++)
            //    {
            //        string headerText = trackTableHeaders[i].InnerText.Trim().ToLower();
            //        if (!string.IsNullOrEmpty(headerText))
            //        {
            //            columnMap[headerText] = i;
            //        }
            //    }

            //    foreach (var trackRow in trackDataRows)
            //    {
            //        var cells = trackRow.SelectNodes("td");

            //        if (cells != null && cells.Count > 0)
            //        {
            //            if (columnMap.TryGetValue("crc-32", out int crcIndex))
            //            {
            //                gameData.Track1CRC = cells[crcIndex].InnerText.Trim();
            //            }
            //            if (columnMap.TryGetValue("md5", out int md5Index))
            //            {
            //                gameData.Track1MD5 = cells[md5Index].InnerText.Trim();
            //            }
            //            if (columnMap.TryGetValue("sha-1", out int shaIndex))
            //            {
            //                gameData.Track1SHA = cells[shaIndex].InnerText.Trim();
            //            }
            //            break;
            //        }
            //    }
            //}
        }

        private void ExtractAlternativeTitle(HtmlDocument doc, RedumpGamesDbItem gameData)
        {
            var node = doc.DocumentNode.SelectSingleNode("//table[@class='gamecomments']//b[contains(text(), 'Alternative Foreign Title')]/parent::*");
            if (node != null)
            {
                var innerHtml = node.InnerHtml;
                var titleTag = "<b>Alternative Foreign Title</b>: ";
                var startIndex = innerHtml.IndexOf(titleTag);

                if (startIndex != -1)
                {
                    startIndex += titleTag.Length;
                    var endIndex = innerHtml.IndexOf("<br>", startIndex);
                    if (endIndex != -1)
                    {
                        gameData.AlternativeGameTitle = innerHtml.Substring(startIndex, endIndex - startIndex);
                    }
                }
            }
        }

        public void SaveToCsv()
        {
            var sortedList = GamesList.ToImmutableSortedSet(new IdComparer());
            using (var writer = new StreamWriter(_csvFilePath))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                csv.WriteRecords(sortedList);
            }

            Console.WriteLine($"Data saved to {_csvFilePath}!");
        }

        class IdComparer : IComparer<RedumpGamesDbItem>
        {
            public int Compare(RedumpGamesDbItem x, RedumpGamesDbItem y) => x.ID.CompareTo(y.ID);
        }

        public void LoadFromCsv()
        {
            if (!File.Exists(_csvFilePath)) return;

            GamesList.Clear();
            _existingGameIDs.Clear();

            using (var reader = new StreamReader(_csvFilePath))
            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                var records = csv.GetRecords<RedumpGamesDbItem>().ToList();
                foreach (var record in records)
                {
                    GamesList.Add(record);
                    _existingGameIDs.Add(record.ID);
                }
            }
        }
    }
}
