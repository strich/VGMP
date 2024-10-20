using HtmlAgilityPack;
using ROMsLinksParser;
using System.Text.RegularExpressions;

class Program
{
    static async Task Main(string[] args)
    {
        var dataFilePath = "../../../../_data/";
        var scraper = new RedumpScraper($"{dataFilePath}RedumpGamesDb.csv", 10);
        await scraper.ScrapeGameDataAsync(5000, false);
        //return;

        //var redumpDatParser = new RedumpDatParser("IBM - PC compatible - Datfile (49532) (2024-10-17 04-38-49).dat");
        //foreach(var item in scraper.GamesList)
        //{
        //    if(string.IsNullOrEmpty(item.FileName))
        //    {
        //        var datItem = redumpDatParser.SearchByMD5(item.Track1MD5);
        //        if(datItem != null)
        //        {
        //            item.FileName = datItem.Name;
        //            item.FileSizeBytes = datItem.GetTotalSize();
        //        }
        //    }
        //}
        //scraper.SaveToCsv();

        //Console.WriteLine($"Games with Moby IDs: {scraper.GamesList.Where(g => g.MobyID != 0).Count()} of {scraper.GamesList.Count}.");

        var mgs = new MobyGamesScraper($"{dataFilePath}MobyGamesDb.csv", 85);
        //await MobyGamesScraper.Start(new[] { "The 7th Guest", "Command & Conquer", "DOOM" });
        await mgs.AddMobyIdsToRedumpGames(scraper.GamesList.ToList());
        scraper.SaveToCsv();
        Console.WriteLine($"Games with Moby IDs: {scraper.GamesList.Where(g => g.MobyID != 0).Count()} of {scraper.GamesList.Count}.");

        
        return;

        //string baseUrl = "https://myrient.erista.me/files/No-Intro/Nintendo%20-%20Nintendo%20Entertainment%20System%20(Headerless)/";
        string baseUrl = "https://myrient.erista.me/files/Redump/IBM%20-%20PC%20compatible/";
        if(!baseUrl.EndsWith("/"))
        {
            baseUrl += "/";
        }

        // Download the HTML content directly
        string htmlContent = await DownloadHtmlAsync(baseUrl);

        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if(links == null)
        {
            Console.WriteLine("No links found in the document.");
            return;
        }

        // Dictionary to hold games and their versions, regions, and sizes
        var gameDictionary = new Dictionary<string, List<GameVersion>>();

        foreach(var link in links)
        {
            string href = link.Attributes["href"]?.Value;
            string fullHref = baseUrl + href;
            if(!href.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip non-zip files
            }

            string title = link.Attributes["title"]?.Value ?? href;
            string size = link.ParentNode.NextSibling.InnerText.Trim(); // Assuming file size is in the next sibling

            var match = Regex.Match(title, @"(.+?)\s*(\(.+\))?.zip", RegexOptions.IgnoreCase);
            if(match.Success)
            {
                string gameName = match.Groups[1].Value;
                string info = match.Groups[2].Value ?? ""; // Capture the region and disc info

                // Separate disc version from region info if exists
                var discMatch = Regex.Match(info, @"\(Disc (\d+)\)", RegexOptions.IgnoreCase);
                string discInfo = discMatch.Success ? discMatch.Value : ""; // Get disc version if available

                // Parse region information
                string regionInfo = GetRegion(info);

                // Create a new GameVersion object
                var gameVersion = new GameVersion
                {
                    FullUrl = fullHref,
                    Region = regionInfo,
                    DiscNumber = discInfo,
                    Size = size
                };

                // Add the game and its version to the dictionary
                if(!gameDictionary.ContainsKey(gameName))
                {
                    gameDictionary[gameName] = new List<GameVersion> { gameVersion };
                }
                else
                {
                    gameDictionary[gameName].Add(gameVersion);
                }
            }
        }

        List<string> fullUrls = new List<string>();

        foreach(var game in gameDictionary)
        {
            foreach(var version in game.Value)
            {
                fullUrls.Add(version.FullUrl);
            }
        }

        File.WriteAllLines("output.txt", fullUrls);

        // Now calculate total size for all games and filtered ones
        double totalSizeMB = fullUrls.Sum(url => ConvertToMB(gameDictionary.SelectMany(g => g.Value).First(v => v.FullUrl == url).Size));

        // Filter only USA, Europe, and World games
        var filteredVersions = gameDictionary
            .SelectMany(g => g.Value)
            .Where(v => v.Region.Contains("USA") || v.Region.Contains("Europe") || v.Region.Contains("World"))
            .ToList();

        double filteredSizeMB = filteredVersions.Sum(v => ConvertToMB(v.Size));

        // Display total size
        DisplayTotalSize(totalSizeMB, "Total Download Size (All Games)");
        DisplayTotalSize(filteredSizeMB, "Total Download Size (USA, Europe, World Games)");

        // Summary of unique games and their versions
        //Console.WriteLine("\nSummary of Unique Games:");
        //foreach(var game in gameDictionary)
        //{
        //    Console.WriteLine($"Game: {game.Key}");
        //    foreach(var version in game.Value)
        //    {
        //        Console.WriteLine($"  - Region: {version.Region}, Disc: {version.DiscNumber}, Size: {version.Size}");
        //    }
        //}
    }

    // Class to store game version details
    class GameVersion
    {
        public string FullUrl { get; set; }
        public string Region { get; set; }
        public string DiscNumber { get; set; }
        public string Size { get; set; }
    }

    // Method to convert size strings like "5.5 GiB" to MB
    static double ConvertToMB(string size)
    {
        var match = Regex.Match(size, @"([\d.]+)\s*(\w+)");
        if(match.Success)
        {
            double fileSize = double.Parse(match.Groups[1].Value);
            string unit = match.Groups[2].Value.ToLower();

            return unit switch
            {
                "kib" => fileSize / 1024,    // KiB to MiB
                "mib" => fileSize,           // MiB is already in the right unit
                "gib" => fileSize * 1024,    // GiB to MiB
                "tib" => fileSize * 1024 * 1024, // TiB to MiB
                _ => 0                       // Return 0 for unknown units
            };
        }
        return 0;
    }

    // Method to display the total size in MB, GB, and TB
    static void DisplayTotalSize(double sizeInMB, string label)
    {
        string totalSizeMBFormatted = $"{sizeInMB:n2}";  // MB with commas
        string totalSizeGBFormatted = $"{sizeInMB / 1024:n2}";  // Convert to GB
        string totalSizeTBFormatted = $"{sizeInMB / (1024 * 1024):n2}";  // Convert to TB

        Console.WriteLine($"\n{label}:");
        Console.WriteLine($"  {totalSizeMBFormatted} MB");
        Console.WriteLine($"  {totalSizeGBFormatted} GB");
        Console.WriteLine($"  {totalSizeTBFormatted} TB");
    }

    static async Task<string> DownloadHtmlAsync(string url)
    {
        using HttpClient client = new HttpClient();
        return await client.GetStringAsync(url);
    }

    static string GetRegion(string info)
    {
        if(info.Contains("(USA)")) return "USA";
        if(info.Contains("(Europe)")) return "Europe";
        if(info.Contains("(Japan)")) return "Japan";
        if(info.Contains("(Australia)")) return "Australia";
        if(info.Contains("(World)")) return "World";
        return "Other";
    }
}