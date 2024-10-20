using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

public class RedumpDatParser
{
    private readonly List<Game> games = new List<Game>();

    public RedumpDatParser(string filePath)
    {
        LoadDatFile(filePath);
    }

    // Load the DAT file and parse the game data
    private void LoadDatFile(string filePath)
    {
        XElement root = XElement.Load(filePath);
        var gameElements = root.Elements("game");

        foreach(var gameElement in gameElements)
        {
            string gameName = gameElement.Attribute("name")?.Value;
            var roms = gameElement.Elements("rom")
                .Select(romElement => new Rom
                {
                    Name = romElement.Attribute("name")?.Value,
                    Size = romElement.Attribute("size")?.Value,
                    CRC = romElement.Attribute("crc")?.Value,
                    MD5 = romElement.Attribute("md5")?.Value,
                    SHA1 = romElement.Attribute("sha1")?.Value
                })
                .ToList();

            var game = new Game
            {
                Name = gameName,
                Category = gameElement.Element("category")?.Value,
                Description = gameElement.Element("description")?.Value,
                Roms = roms
            };

            games.Add(game);
        }
    }

    public Game SearchByMD5(string hash)
    {
        return games.FirstOrDefault(game => game.Roms.Any(rom =>
            string.Equals(rom.MD5, hash, StringComparison.OrdinalIgnoreCase)));
    }

    // Return the list of all games loaded from the DAT file
    public List<Game> GetAllGames()
    {
        return games;
    }
}

// Class to represent a game
public class Game
{
    public string Name { get; set; }
    public string Category { get; set; }
    public string Description { get; set; }
    public List<Rom> Roms { get; set; }

    // Method to calculate the total size of the game's ROMs
    public long GetTotalSize()
    {
        // Sum the sizes of all ROMs, converting from string to long
        return Roms.Sum(rom => rom.GetSizeInBytes());
    }

    public override string ToString()
    {
        string romsInfo = string.Join("\n", Roms.Select(rom => rom.ToString()));
        return $"Game: {Name}\nCategory: {Category}\nDescription: {Description}\nRoms:\n{romsInfo}";
    }
}

// Class to represent a ROM file
public class Rom
{
    public string Name { get; set; }
    public string Size { get; set; }
    public string CRC { get; set; }
    public string MD5 { get; set; }
    public string SHA1 { get; set; }

    // Convert the size string to long
    public long GetSizeInBytes()
    {
        return long.TryParse(Size, out long sizeInBytes) ? sizeInBytes : 0;
    }

    public override string ToString()
    {
        return $"- {Name} (Size: {Size}, CRC: {CRC}, MD5: {MD5}, SHA1: {SHA1})";
    }
}
