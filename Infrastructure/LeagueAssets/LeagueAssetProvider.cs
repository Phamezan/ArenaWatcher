using System.Net.Http.Json;
using System.Text.Json;
using DiscordBot.Models;

namespace DiscordBot.Infrastructure.LeagueAssets;

public sealed class LeagueAssetProvider(HttpClient httpClient) : ILeagueAssetProvider
{
    private const string DataDragonBaseUrl = "https://ddragon.leagueoflegends.com";
    private const string CommunityDragonBaseUrl = "https://raw.communitydragon.org/latest";
    private const string CommunityDragonGameDataBaseUrl = $"{CommunityDragonBaseUrl}/plugins/rcp-be-lol-game-data/global/default";

    private string? _dataDragonVersion;
    private Dictionary<int, ItemAsset>? _items;
    private Dictionary<string, string>? _championIconByName;
    private Dictionary<int, AugmentAsset>? _augments;
    private readonly HashSet<int> _loggedUnknownAugmentIds = [];

    public async Task<MatchCardData> BuildCardDataAsync(MatchSummary summary, CancellationToken cancellationToken)
    {
        var version = await GetDataDragonVersionAsync(cancellationToken);
        var championIcons = await GetChampionIconMapAsync(version, cancellationToken);
        var items = await GetItemsAsync(version, cancellationToken);
        var augments = await GetAugmentsAsync(cancellationToken);

        var championIconUrl = championIcons.TryGetValue(summary.ChampionName, out var iconUrl)
            ? iconUrl
            : $"{DataDragonBaseUrl}/cdn/{version}/img/champion/{summary.ChampionName}.png";

        return new MatchCardData(
            summary.PlayerName,
            summary.ChampionName,
            championIconUrl,
            summary.ItemIds.Select(id => items.GetValueOrDefault(id) ?? UnknownItem(version, id)).ToArray(),
            summary.AugmentIds.Select(id => ResolveAugment(augments, id)).ToArray(),
            summary.MatchId,
            summary.Placement,
            summary.Stats);
    }

    private async Task<string> GetDataDragonVersionAsync(CancellationToken cancellationToken)
    {
        if (_dataDragonVersion is not null)
        {
            return _dataDragonVersion;
        }

        var versions = await httpClient.GetFromJsonAsync<List<string>>(
            $"{DataDragonBaseUrl}/api/versions.json",
            cancellationToken);

        _dataDragonVersion = versions?.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not resolve the latest Data Dragon version.");

        return _dataDragonVersion;
    }

    private async Task<Dictionary<int, ItemAsset>> GetItemsAsync(string version, CancellationToken cancellationToken)
    {
        if (_items is not null)
        {
            return _items;
        }

        using var document = await GetJsonDocumentAsync(
            $"{DataDragonBaseUrl}/cdn/{version}/data/en_US/item.json",
            cancellationToken);

        var items = new Dictionary<int, ItemAsset>();
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            _items = items;
            return _items;
        }

        foreach (var item in data.EnumerateObject())
        {
            if (!int.TryParse(item.Name, out var itemId))
            {
                continue;
            }

            var name = ReadString(item.Value, "name", $"Item {itemId}");
            items[itemId] = new ItemAsset(
                itemId,
                StripHtml(name),
                $"{DataDragonBaseUrl}/cdn/{version}/img/item/{itemId}.png");
        }

        _items = items;
        return _items;
    }

    private async Task<Dictionary<string, string>> GetChampionIconMapAsync(string version, CancellationToken cancellationToken)
    {
        if (_championIconByName is not null)
        {
            return _championIconByName;
        }

        using var document = await GetJsonDocumentAsync(
            $"{DataDragonBaseUrl}/cdn/{version}/data/en_US/champion.json",
            cancellationToken);

        var champions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            _championIconByName = champions;
            return _championIconByName;
        }

        foreach (var champion in data.EnumerateObject())
        {
            var id = ReadString(champion.Value, "id", champion.Name);
            var name = ReadString(champion.Value, "name", id);
            var full = champion.Value.TryGetProperty("image", out var image)
                ? ReadString(image, "full", $"{id}.png")
                : $"{id}.png";
            var url = $"{DataDragonBaseUrl}/cdn/{version}/img/champion/{full}";

            champions[id] = url;
            champions[name] = url;
            champions[champion.Name] = url;
        }

        _championIconByName = champions;
        return _championIconByName;
    }

    private async Task<Dictionary<int, AugmentAsset>> GetAugmentsAsync(CancellationToken cancellationToken)
    {
        if (_augments is not null)
        {
            return _augments;
        }

        using var document = await GetJsonDocumentAsync(
            $"{CommunityDragonBaseUrl}/cdragon/arena/en_us.json",
            cancellationToken);

        var augments = new Dictionary<int, AugmentAsset>();
        if (!document.RootElement.TryGetProperty("augments", out var augmentData)
            || augmentData.ValueKind != JsonValueKind.Array)
        {
            _augments = augments;
            return _augments;
        }

        foreach (var augment in augmentData.EnumerateArray())
        {
            var id = ReadInt(augment, "id");
            if (id <= 0)
            {
                continue;
            }

            var iconPath = ReadString(augment, "iconSmall", ReadString(augment, "iconLarge"));
            augments[id] = new AugmentAsset(
                id,
                StripHtml(ReadString(augment, "name", $"Augment {id}")),
                $"{CommunityDragonBaseUrl}/game/{iconPath}",
                ReadInt(augment, "rarity"));
        }

        await AddClientAugmentsAsync(augments, cancellationToken);

        _augments = augments;
        return _augments;
    }

    private async Task AddClientAugmentsAsync(
        Dictionary<int, AugmentAsset> augments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(
                $"{CommunityDragonBaseUrl}/plugins/rcp-be-lol-game-data/global/en_gb/v1/cherry-augments.json",
                cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var augment in document.RootElement.EnumerateArray())
            {
                var id = ReadInt(augment, "id");
                if (id <= 0)
                {
                    continue;
                }

                var name = ReadAugmentName(augment, id);
                if (augments.ContainsKey(id) || name.Equals($"Augment {id}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                augments[id] = new AugmentAsset(
                    id,
                    name,
                    BuildClientAssetUrl(ReadAugmentIconPath(augment)),
                    ReadRarity(augment));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Could not load client Arena augments: {ex.Message}");
        }
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        await using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static ItemAsset UnknownItem(string version, int id)
    {
        return new ItemAsset(id, $"Item {id}", $"{DataDragonBaseUrl}/cdn/{version}/img/item/{id}.png");
    }

    private static AugmentAsset UnknownAugment(int id)
    {
        return new AugmentAsset(id, $"Unknown Augment {id}", string.Empty, 0);
    }

    private AugmentAsset ResolveAugment(IReadOnlyDictionary<int, AugmentAsset> augments, int id)
    {
        if (augments.TryGetValue(id, out var augment))
        {
            return augment;
        }

        var normalizedId = NormalizeAugmentId(id);
        if (normalizedId != id && augments.TryGetValue(normalizedId, out var normalizedAugment))
        {
            return normalizedAugment;
        }

        if (_loggedUnknownAugmentIds.Add(id))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Unknown Arena augment id {id}; static data may need another update.");
        }

        return UnknownAugment(id);
    }

    private static int NormalizeAugmentId(int id)
    {
        return id >= 1000 ? id - 1000 : id;
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "")
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static int ReadRarity(JsonElement augment)
    {
        if (!augment.TryGetProperty("rarity", out var rarity))
        {
            return 0;
        }

        if (rarity.ValueKind == JsonValueKind.Number && rarity.TryGetInt32(out var numericRarity))
        {
            return numericRarity;
        }

        if (rarity.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        var rarityText = rarity.GetString() ?? string.Empty;
        if (rarityText.Contains("prismatic", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (rarityText.Contains("gold", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static string ReadAugmentName(JsonElement augment, int id)
    {
        var name = ReadString(
            augment,
            "name",
            ReadString(
                augment,
                "nameTRA",
                ReadString(augment, "displayName", $"Augment {id}")));

        return StripHtml(name);
    }

    private static string ReadAugmentIconPath(JsonElement augment)
    {
        return ReadString(
            augment,
            "iconPath",
            ReadString(
                augment,
                "augmentSmallIconPath",
                ReadString(augment, "icon")));
    }

    private static string BuildClientAssetUrl(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return string.Empty;
        }

        const string assetPrefix = "/lol-game-data/assets/";
        if (iconPath.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{CommunityDragonGameDataBaseUrl}/{iconPath[assetPrefix.Length..].ToLowerInvariant()}";
        }

        if (iconPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return iconPath;
        }

        return $"{CommunityDragonGameDataBaseUrl}/{iconPath.TrimStart('/').ToLowerInvariant()}";
    }

    private static string StripHtml(string value)
    {
        return value
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase);
    }
}
