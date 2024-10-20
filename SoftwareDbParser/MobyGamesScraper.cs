using HtmlAgilityPack;
using System.Text.RegularExpressions;
using FuzzySharp;
using Newtonsoft.Json;
using ROMsLinksParser;
using CsvHelper.Configuration;
using CsvHelper;
using System.Globalization;
using Newtonsoft.Json.Linq;
using System.Text;
using static Crayon.Output;
using System.Web;

public class MobyGamesScraper
{
    public List<MobyGamesDbItem> GamesDb = new();
    string _csvFilePath = "MobyGamesDb.csv";
    int _confidenceThreshold = 70;

    public MobyGamesScraper(string csvFilePath, int confidenceThreshold = 70)
    {
        _csvFilePath = csvFilePath;
        _confidenceThreshold = confidenceThreshold;
        LoadFromCsv();
    }

    public async Task AddMobyIdsToRedumpGames(List<RedumpGamesDbItem> gamesList)
    {
        foreach(var redumpGame in gamesList)
        {
            if(redumpGame.MobyID != 0) continue;
            //if(redumpGame.Category != "Games") continue;

            Console.WriteLine(Bold($"Scraping MobyGames for {redumpGame.GameTitle}"));
            var platforms = new List<string>() { "dos", "windows", "win3x", "macintosh" };
            (var mobyGame, int confidence) = ParseAndMatchGame(redumpGame, platforms);
            if(mobyGame == null)
            {
                (mobyGame, confidence) = await SearchMobyGamesFromTopBar(redumpGame, platforms);
                //(mobyGame, confidence) = await ParseAndMatchGameFromBasicSearch(redumpGame, new() { "dos", "windows" });
            }
            if(mobyGame != null)
            {
                redumpGame.MobyID = mobyGame.Id;
                redumpGame.MobyIDConfidence = confidence;
            }
        }

        SaveToCsv();
    }

    public async Task<(MobyGamesDbItem, int)> SearchMobyGamesFromTopBar(RedumpGamesDbItem redumpGame, List<string> platforms, string currentName = null)
    {
        if(currentName == null) currentName = redumpGame.GameTitle;

        if(TryGetExactMatchFromDb(currentName, out var game))
        {
            return (game, 100);
        }
        game = new();
        var queryData = new { q = $"/g {currentName}", p = false };
        var requestBody = new StringContent(JsonConvert.SerializeObject(queryData), Encoding.UTF8, "application/json");
        string searchUrl = "https://www.mobygames.com/search/auto/";
        HttpClient httpClient = new HttpClient();
        try
        {
            var response = await httpClient.PostAsync(searchUrl, requestBody);
            response.EnsureSuccessStatusCode(); // Throw an exception if the response is not successful
            var responseContent = await response.Content.ReadAsStringAsync();

            // Parse the JSON response
            JObject jsonResponse = JObject.Parse(responseContent);
            var results = jsonResponse["data"]["results"];
            var gameInfoList = new List<GameInfo>();
            foreach(var result in results)
            {
                string mainName = result["name"]?.ToString();
                string highlight = result["highlight"]?.ToString();
                var altNames = CleanHtmlTags(highlight);
                string gameUrl = result["url"]?.ToString();
                var gameId = ExtractGameIdFromUrl(gameUrl);
                gameInfoList.Add(new() { Title = mainName, AkaNames = altNames.ToList(), game_id = gameId.Value });
            }

            var bestMatch = FindBestMatch(gameInfoList, currentName, _confidenceThreshold);
            if(bestMatch != null)
            {
                ScrapeAndExtractGameMetadataFromGamePage(bestMatch);
                return AddBestMatch(redumpGame.GameTitle, game, bestMatch);
            }
            else
            {
                if(!string.IsNullOrEmpty(redumpGame.AlternativeGameTitle) && currentName != redumpGame.AlternativeGameTitle)
                {
                    Console.WriteLine(Dim(" Trying alternative name."));
                    currentName = redumpGame.AlternativeGameTitle;
                    return await SearchMobyGamesFromTopBar(redumpGame, platforms, currentName);
                }
                Console.WriteLine(Dim().Yellow("    No suitable match found."));
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine(Dim().Red($"    Error during MobyGames search: {ex.Message}"));
        }

        return (null, 0);
    }

    // Helper method to remove HTML tags from a string and split it into separate names
    private string[] CleanHtmlTags(string htmlString)
    {
        if(string.IsNullOrEmpty(htmlString)) return Array.Empty<string>();

        // Remove HTML tags (like <em>) using Regex
        string cleanString = Regex.Replace(htmlString, "<.*?>", string.Empty);

        // Split alternative names by commas
        return cleanString.Split(new[] { ", " }, StringSplitOptions.None);
    }

    public async Task<(MobyGamesDbItem, int)> ParseAndMatchGameFromBasicSearch(RedumpGamesDbItem redumpGame, List<string> platforms, string currentName = null, bool ignoreCache = false)
    {
        if(currentName == null) currentName = redumpGame.GameTitle;

        if(TryGetExactMatchFromDb(currentName, out var game))
        {
            return (game, 100);
        }

        game = new();

        var gamesList = await ScrapeGamesFromBasicSearch($"https://www.mobygames.com/search/?q={currentName}&type=game&page=0");

        var bestMatch = FindBestMatch(gamesList, currentName, _confidenceThreshold);

        if(bestMatch != null)
        {
            ScrapeAndExtractGameMetadataFromGamePage(bestMatch);
            return AddBestMatch(redumpGame.GameTitle, game, bestMatch);
        }
        else
        {
            if(!string.IsNullOrEmpty(redumpGame.AlternativeGameTitle) && currentName != redumpGame.AlternativeGameTitle)
            {
                Console.WriteLine(Dim(" Trying alternative name."));
                currentName = redumpGame.AlternativeGameTitle;
                return await ParseAndMatchGameFromBasicSearch(redumpGame, platforms, currentName, ignoreCache);
            }
            Console.WriteLine(Dim().Red(" No suitable match found."));
        }

        return (null, 0);
    }

    static void ScrapeAndExtractGameMetadataFromGamePage(GameInfo gameInfo)
    {
        string baseUrl = "https://www.mobygames.com/game/";
        string gameUrl = $"{baseUrl}{gameInfo.game_id}/";
        string cacheDir = "cache";
        string cachedFile = Path.Combine(cacheDir, $"{gameInfo.game_id}.html");

        // Ensure cache directory exists
        if(!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        // Check if cached file exists
        string html;
        if(File.Exists(cachedFile))
        {
            Console.WriteLine(Dim($"    Using cached file for game ID: {gameInfo.game_id}"));
            html = File.ReadAllText(cachedFile);
        }
        else
        {
            Console.WriteLine(Dim($"    Fetching game page from URL: {gameUrl}"));
            using(HttpClient client = new HttpClient())
            {
                html = client.GetStringAsync(gameUrl).Result;
            }
            File.WriteAllText(cachedFile, html);
        }

        // Parse the HTML to extract metadata
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Extract MobyScore
        var mobyScoreNode = doc.DocumentNode.SelectSingleNode("//div[@class='mobyscore']");
        if(!double.TryParse(mobyScoreNode?.InnerText.Trim(), out var mobyScore)) mobyScore = 0;

        // Extract ranking info
        var rankingNode = doc.DocumentNode.SelectSingleNode("//small[@class='text-muted']");
        string rankingText = rankingNode?.InnerText.Trim() ?? "N/A";

        // Extract Critics Score
        var criticsNode = doc.DocumentNode.SelectSingleNode("//dt[contains(text(),'Critics')]/following-sibling::dd[1]");
        if(!double.TryParse(criticsNode?.InnerText.Trim(), out var criticsScore)) criticsScore = 0;

        // Extract Players Score
        var playersNode = doc.DocumentNode.SelectSingleNode("//dt[contains(text(),'Players')]/following-sibling::dd[1]/span[@class='stars']");
        if(!double.TryParse(playersNode?.GetAttributeValue("data-tooltip", "N/A"), out var playersScore)) playersScore = 0;

        // Extract Review Ranking for DOS
        var reviewRankingNode = doc.DocumentNode.SelectSingleNode("//span[contains(@data-tooltip,'ranked DOS games')]");
        string dosRanking = reviewRankingNode?.GetAttributeValue("data-tooltip", "N/A");

        // Extract Collected By (number of players who collected the game)
        var collectedByNode = doc.DocumentNode.SelectSingleNode("//dt[contains(text(),'Collected By')]/following-sibling::dd[1]");
        string collectedBy = collectedByNode?.InnerText.Trim() ?? "N/A";

        // Output the extracted information
        //Console.WriteLine($"    Moby Score: {mobyScore}");
        //Console.WriteLine($"    Ranking: {rankingText}");
        //Console.WriteLine($"    Critics Score: {criticsScore}");
        //Console.WriteLine($"    Players Score: {playersScore}");
        //Console.WriteLine($"    DOS Ranking: {dosRanking}");
        //Console.WriteLine($"    Collected By: {collectedBy}");
        gameInfo.moby_score = mobyScore;
        if(gameInfo.Platforms.Count == 0) gameInfo.Platforms = ExtractPlatformsFromGamePage(doc).Select(p => new Platform() { name = p }).ToList();
        if(gameInfo.Platforms.Count > 0)
        {
            gameInfo.Platforms[0].critic_score = criticsScore;
            gameInfo.Platforms[0].user_score = playersScore;
        }
    }

    public static HashSet<string> ExtractPlatformsFromGamePage(HtmlDocument htmlContent)
    {
        var platforms = new HashSet<string>();

        // Extract platforms from "Released on" section
        var releasedOnNode = htmlContent.DocumentNode.SelectSingleNode("//dl[@class='metadata']//dd[preceding-sibling::dt[text()='Released']]");
        if(releasedOnNode != null)
        {
            var platformNode = releasedOnNode.SelectSingleNode(".//a[contains(@href, '/platform/')]");
            if(platformNode != null)
            {
                string platform = platformNode.InnerText.Trim();
                platforms.Add(platform);
            }
        }

        // Extract platforms from "Releases" section
        var releasesNode = htmlContent.DocumentNode.SelectSingleNode("//ul[@id='platformLinks']");
        if(releasesNode != null)
        {
            var platformLinks = releasesNode.SelectNodes(".//li//a[contains(@href, '/platform/')]");
            if(platformLinks != null)
            {
                foreach(var platformLink in platformLinks)
                {
                    string platform = platformLink.InnerText.Trim();
                    platforms.Add(platform);
                }
            }
        }

        return platforms;
    }

    private static readonly Regex GameIdRegex = new Regex(@"/game/(\d+)/", RegexOptions.Compiled);
    private static GameInfo ExtractGameInfoFromBasicSearchTable(HtmlNode rowNode)
    {
        var gameInfo = new GameInfo
        {
            AkaNames = new List<string>(),
            Platforms = new()
        };

        // Extract game ID and title
        var titleNode = rowNode.SelectSingleNode(".//td/b/a");
        if(titleNode != null)
        {
            var href = titleNode.GetAttributeValue("href", "");
            var match = GameIdRegex.Match(href);
            if(match.Success && int.TryParse(match.Groups[1].Value, out int gameId))
            {
                gameInfo.game_id = gameId;
            }
            gameInfo.Title = titleNode.InnerText.Trim();
        }

        // Extract AKA names
        var akaNode = rowNode.SelectSingleNode(".//small[contains(text(), 'AKA:')]");
        if(akaNode != null)
        {
            var akaText = akaNode.InnerText.Replace("AKA:", "").Trim();
            gameInfo.AkaNames = akaText.Split(',').Select(s => s.Trim()).ToList();
        }

        // Extract platforms and years
        var platformNodes = rowNode.SelectNodes(".//small[not(contains(@class, 'text-muted'))]");
        if(platformNodes != null)
        {
            foreach(var node in platformNodes)
            {
                var platformText = node.InnerText.Trim();
                Regex regex = new Regex(@"^(.*?)\s*\((\d{4})\)$");

                Match match = regex.Match(platformText);
                if(match.Success)
                {
                    string platform = match.Groups[1].Value;
                    string year = match.Groups[2].Value;
                    //Console.WriteLine($"    Platform: {platform}, Year: {year}");
                    if(!string.IsNullOrEmpty(platform) && !string.IsNullOrEmpty(year))
                    {
                        gameInfo.Platforms.Add(new Platform() { name = platform, ReleaseYear = int.Parse(year) });
                    }
                }
                
            }
        }

        return gameInfo;
    }

    public static async Task<List<GameInfo>> ScrapeGamesFromBasicSearch(string url)
    {
        using var client = new HttpClient();
        var html = await client.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var gameInfoList = new List<GameInfo>();

        var tableRows = doc.DocumentNode.SelectNodes("//table[@class='table mb']/tr");
        if(tableRows != null)
        {
            foreach(var row in tableRows)
            {
                var gameInfo = ExtractGameInfoFromBasicSearchTable(row);
                if(gameInfo.Title != null)  // Ensure we only add valid entries
                {
                    gameInfoList.Add(gameInfo);
                }
            }
        }

        return gameInfoList;
    }

    // Scrape the game search page from MobyGames
    static async Task<string> GetPageHtml(string url)
    {
        using HttpClient client = new HttpClient();
        try
        {
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch(HttpRequestException e)
        {
            Console.WriteLine(Dim().Red($"    Error fetching page: {e.Message}"));
            return null;
        }
    }

    private int? ExtractGameIdFromUrl(string url)
    {
        if(string.IsNullOrEmpty(url)) return null;

        // Extract the numeric ID from the URL (e.g., /game/1705/...)
        var match = Regex.Match(url, @"/game/(\d+)/");
        if(match.Success)
        {
            if(int.TryParse(match.Groups[1].Value, out int gameId))
            {
                return gameId;
            }
        }

        return null;
    }

    bool TryGetExactMatchFromDb(string gameName, out MobyGamesDbItem game)
    {
        // Check the moby db for an exact match:
        // TODO: This needs to check platform
        game = GamesDb.FirstOrDefault(g => g.GameTitle.Equals(gameName, StringComparison.OrdinalIgnoreCase));
        if(game == null)
        {
            // Moby db match not found, check for the local cache of similar matched names:
            if(_tempBestMatchCache.TryGetValue(gameName, out var mobyId))
            {
                // TODO: This needs to check platform
                game = GamesDb.FirstOrDefault(g => g.Id == mobyId);
            }
        }
        return game != null;
    }

    // Parse the HTML page and find the closest game title match using fuzzy matching
    public (MobyGamesDbItem, int) ParseAndMatchGame(RedumpGamesDbItem redumpGame, List<string> platforms, string currentName = null, bool ignoreCache = false)
    {
        if(currentName == null) currentName = redumpGame.GameTitle;

        if(TryGetExactMatchFromDb(currentName, out var game))
        {
            Console.WriteLine(Bold().Green($"    Exact match found!"));
            return (game, 100);
        }

        game = new();

        string cacheFilePath = $"cache/{SanitizeFileName(currentName)}.json";

        // Check if we already have cached data for this game
        string jsonResponse = LoadFromCache(cacheFilePath);
        if(ignoreCache || jsonResponse == null)
        {
            string apiUrl = $"https://www.mobygames.com/game/";
            foreach(string platform in platforms)
            {
                apiUrl += $"platform:{platform}/";
            }
            apiUrl += $"include_dlc:true/perpage:50/title:{HttpUtility.UrlEncode(currentName)}/sort:date/page:1/?format=json";

            jsonResponse = FetchJsonData(apiUrl);
            SaveToCache(cacheFilePath, jsonResponse);
        }

        // Parse and process the JSON response
        if(!string.IsNullOrEmpty(jsonResponse))
        {
            var gameResults = ParseGameResults(jsonResponse);
            var bestMatch = FindBestMatch(gameResults, currentName, _confidenceThreshold);

            if(bestMatch != null)
            {
                return AddBestMatch(redumpGame.GameTitle, game, bestMatch);
            }
            else
            {
                if(!string.IsNullOrEmpty(redumpGame.AlternativeGameTitle) && currentName != redumpGame.AlternativeGameTitle)
                {
                    Console.WriteLine(Dim(" Trying alternative name."));
                    currentName = redumpGame.AlternativeGameTitle;
                    return ParseAndMatchGame(redumpGame, platforms, currentName, ignoreCache);
                }
                Console.WriteLine(Dim().Yellow("    No suitable match found."));
            }
        }
        else
        {
            Console.WriteLine(Dim().Yellow("   Got no matching results for this search type."));
        }
        return (null, 0);
    }

    Dictionary<string, int> _tempBestMatchCache = new();
    private (MobyGamesDbItem, int) AddBestMatch(string gameName, MobyGamesDbItem game, GameInfo bestMatch)
    {
        Console.WriteLine(Bold().Green($"    Best match found: {bestMatch.Title}"));

        _tempBestMatchCache[gameName] = bestMatch.game_id;

        game.GameTitle = bestMatch.Title;
        game.Id = bestMatch.game_id;
        if(bestMatch.Platforms.Count > 0)
        {
            game.Platforms = string.Join(",", bestMatch.Platforms.Select(p => p.name));
            game.CriticScore = bestMatch.Platforms[0].critic_score ?? 0;
            game.CriticVotes = bestMatch.Platforms[0].critic_votes ?? 0;
            game.UserScore = bestMatch.Platforms[0].user_score ?? 0;
            game.UserVotes = bestMatch.Platforms[0].user_votes ?? 0;
            game.MobyScore = bestMatch.moby_score ?? 0;
        }
        game.LastUpdatedUTC = DateTime.Now.ToUniversalTime().ToString();
        GamesDb.Add(game);
        return (game, bestMatch.FuzzConfidence);
    }

    static string FetchJsonData(string url)
    {
        using(HttpClient client = new HttpClient())
        {
            try
            {
                return client.GetStringAsync(url).Result;
            }
            catch(Exception ex)
            {
                Console.WriteLine(Dim().Red($"    Error fetching JSON data: {ex.Message}"));
                return null;
            }
        }
    }

    static void SaveToCache(string filePath, string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        File.WriteAllText(filePath, data);
    }

    static string LoadFromCache(string filePath)
    {
        if(File.Exists(filePath))
        {
            Console.WriteLine(Dim($"    Got MobyGames cache hit for {filePath}"));
            return File.ReadAllText(filePath);
        }
        return null;
    }

    public void SaveToCsv()
    {
        using(var writer = new StreamWriter(_csvFilePath))
        using(var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(GamesDb);
        }

        Console.WriteLine($"Data saved to {_csvFilePath}!");
    }

    public void LoadFromCsv()
    {
        if(!File.Exists(_csvFilePath)) return;

        using(var reader = new StreamReader(_csvFilePath))
        using(var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            GamesDb = csv.GetRecords<MobyGamesDbItem>().ToList();
        }
    }

    static List<GameInfo> ParseGameResults(string json)
    {
        var parsedData = JsonConvert.DeserializeObject<MobyGamesResponse>(json);
        if(parsedData?.Data?.Games != null)
        {
            var list = new List<GameInfo>();
            foreach(var game in parsedData?.Data?.Games)
            {
                list.Add(new()
                {
                    Title = game.Title,
                    game_id = game.game_id,
                    moby_score = game.moby_score,
                    Platforms = game.Platforms
                });
            }
            return list;

        }
        return null;
    }

    static GameInfo FindBestMatch(List<GameInfo> games, string searchQuery, int threshold)
    {
        GameInfo bestMatch = null;
        int bestConfidence = 0;

        foreach(var game in games)
        {
            var namesToTry = new List<string>() { game.Title };
            if(game.AkaNames != null && game.AkaNames.Count > 0) namesToTry.AddRange(game.AkaNames);
            foreach(var name in namesToTry)
            {
                int confidence = Fuzz.WeightedRatio(name.ToLower(), searchQuery.ToLower());

                Console.WriteLine(Dim($"    Comparing '{name.ToLower()}' with '{searchQuery.ToLower()}', Confidence: {confidence}"));

                if(confidence > threshold && confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestMatch = game;
                    bestMatch.FuzzConfidence = confidence;
                }
            }
            
        }

        return bestMatch;
    }

    public class GameInfo
    {
        public string Title { get; set; }
        public List<string> AkaNames { get; set; }
        public int FuzzConfidence { get; internal set; }
        public int game_id { get; set; }
        public double? moby_score { get; set; }
        public List<Platform> Platforms { get; set; } = new();
    }

    public class MobyGamesDbItem
    {
        public int Id { get; set; }
        public string GameTitle { get; set; }
        public string Platforms { get; set; }
        public double MobyScore { get; set; }
        public double CriticScore { get; set; }
        public int CriticVotes { get; set; }
        public double UserScore { get; set; }
        public int UserVotes { get; set; }
        public string LastUpdatedUTC { get; set; }
    }

    class MobyGamesResponse
    {
        public string ApiVersion { get; set; }
        public MobyGamesData Data { get; set; }
    }

    class MobyGamesData
    {
        public List<GameResult> Games { get; set; }
    }

    public class Platform
    {
        public double? critic_score { get; set; }
        public int? critic_votes { get; set; }
        public int id { get; set; }
        public double? moby_score { get; set; }
        public string name { get; set; }
        public double? user_score { get; set; }
        public int? user_votes { get; set; }
        public int ReleaseYear { get; set; }
    }

    class GameResult
    {
        public string Title { get; set; }
        public int game_id { get; set; }
        public double? moby_score { get; set; }
        public List<Platform> Platforms { get; set; }
        public int FuzzConfidence { get; set; }
    }

    static string ExtractGameId(string url)
    {
        // Regex to match the game ID between "/game/" and the next "/"
        var match = Regex.Match(url, @"\/game\/(\d+)\/");
        return match.Success ? match.Groups[1].Value : null;
    }

    // Sanitize file names to make them valid for caching
    static string SanitizeFileName(string name)
    {
        return Regex.Replace(name, @"[^a-zA-Z0-9_\-\.]", "_");
    }

    
}
