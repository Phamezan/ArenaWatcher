using DiscordBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiscordBot.Rendering;

public sealed class MatchCardRenderer(HttpClient httpClient) : IMatchCardRenderer
{
    private static readonly Color Background = Color.ParseHex("#15171f");
    private static readonly Color Panel = Color.ParseHex("#202332");
    private static readonly Color Text = Color.ParseHex("#f4f6fb");
    private static readonly Color Muted = Color.ParseHex("#aeb5c4");
    private static readonly Color Accent = Color.ParseHex("#5865f2");
    private static readonly Color DamageColor = Color.ParseHex("#ff6b6b");
    private static readonly Color TakenColor = Color.ParseHex("#f7b955");
    private static readonly Color HealingColor = Color.ParseHex("#62d68f");
    private static readonly Color MitigatedColor = Color.ParseHex("#74b9ff");

    public async Task<byte[]> RenderAsync(MatchCardData cardData, CancellationToken cancellationToken)
    {
        using var image = new Image<Rgba32>(1000, 620, Background);
        var family = ResolveFontFamily();

        var titleFont = family.CreateFont(36, FontStyle.Bold);
        var championFont = family.CreateFont(34, FontStyle.Bold);
        var sectionFont = family.CreateFont(23, FontStyle.Bold);
        var bodyFont = family.CreateFont(18);
        var smallFont = family.CreateFont(14);
        var footerFont = family.CreateFont(13);

        image.Mutate(ctx =>
        {
            ctx.Fill(Panel, new RectangleF(24, 24, 952, 572));
            ctx.Fill(Accent, new RectangleF(24, 24, 8, 572));
            ctx.DrawText("Arena Result", titleFont, Text, new PointF(56, 48));
            ctx.DrawText(cardData.PlayerName, bodyFont, Muted, new PointF(58, 94));
            ctx.DrawText($"Match {cardData.MatchId}", footerFont, Muted, new PointF(58, 566));
        });

        using var championIcon = await LoadIconAsync(cardData.ChampionIconUrl, 112, cancellationToken);
        image.Mutate(ctx =>
        {
            ctx.DrawImage(championIcon, new Point(58, 136), 1f);
            ctx.DrawText(cardData.ChampionName, championFont, Text, new PointF(194, 148));
            ctx.DrawText(cardData.Placement is null ? "Placement unknown" : $"Placement #{cardData.Placement}", bodyFont, Muted, new PointF(198, 196));
        });

        DrawStats(image, cardData.Stats, bodyFont, smallFont);

        image.Mutate(ctx =>
        {
            ctx.DrawText("Augments", sectionFont, Text, new PointF(58, 294));
            ctx.DrawText("Items", sectionFont, Text, new PointF(608, 294));
        });

        await DrawAugmentsAsync(image, cardData.Augments, bodyFont, cancellationToken);
        await DrawItemsAsync(image, cardData.Items, smallFont, cancellationToken);

        await using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream, cancellationToken);
        return stream.ToArray();
    }

    public async Task<byte[]> RenderGroupAsync(IReadOnlyList<MatchCardData> cards, CancellationToken cancellationToken)
    {
        if (cards.Count == 0)
        {
            throw new ArgumentException("At least one card is required.", nameof(cards));
        }

        using var image = new Image<Rgba32>(1200, 760, Background);
        var family = ResolveFontFamily();
        var titleFont = family.CreateFont(34, FontStyle.Bold);
        var sectionFont = family.CreateFont(20, FontStyle.Bold);
        var bodyFont = family.CreateFont(17);
        var nameFont = family.CreateFont(15, FontStyle.Bold);
        var smallFont = family.CreateFont(13);
        var footerFont = family.CreateFont(13);

        image.Mutate(ctx =>
        {
            ctx.Fill(Panel, new RectangleF(24, 24, 1152, 712));
            ctx.Fill(Accent, new RectangleF(24, 24, 8, 712));
            ctx.DrawText("Arena Group Result", titleFont, Text, new PointF(56, 48));
            ctx.DrawText(cards[0].Placement is null ? "Placement unknown" : $"Placement #{cards[0].Placement}", bodyFont, Muted, new PointF(58, 94));
            ctx.DrawText($"Match {cards[0].MatchId}", footerFont, Muted, new PointF(58, 704));
        });

        for (var index = 0; index < Math.Min(cards.Count, 4); index++)
        {
            await DrawGroupPlayerAsync(image, cards[index], index, nameFont, bodyFont, smallFont, cancellationToken);
        }

        await using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream, cancellationToken);
        return stream.ToArray();
    }

    private static void DrawStats(Image<Rgba32> image, MatchStats stats, Font valueFont, Font labelFont)
    {
        var statItems = new[]
        {
            ("Damage", stats.DamageDealtToChampions, DamageColor),
            ("Taken", stats.DamageTaken, TakenColor),
            ("Healed", stats.HealingDone, HealingColor),
            ("Mitigated", stats.DamageMitigated, MitigatedColor)
        };

        const int startX = 608;
        const int startY = 142;
        const int statWidth = 148;
        const int statHeight = 54;

        image.Mutate(ctx =>
        {
            for (var index = 0; index < statItems.Length; index++)
            {
                var x = startX + (index % 2) * (statWidth + 14);
                var y = startY + (index / 2) * (statHeight + 14);

                ctx.Fill(Color.ParseHex("#2a2e3e"), new RectangleF(x, y, statWidth, statHeight));
                ctx.Fill(statItems[index].Item3, new RectangleF(x, y, 4, statHeight));
                ctx.DrawText(FormatNumber(statItems[index].Item2), valueFont, statItems[index].Item3, new PointF(x + 12, y + 8));
                ctx.DrawText(statItems[index].Item1, labelFont, Muted, new PointF(x + 12, y + 32));
            }
        });
    }

    private async Task DrawGroupPlayerAsync(
        Image<Rgba32> image,
        MatchCardData card,
        int index,
        Font nameFont,
        Font bodyFont,
        Font smallFont,
        CancellationToken cancellationToken)
    {
        var y = 136 + index * 136;
        using var championIcon = await LoadIconAsync(card.ChampionIconUrl, 88, cancellationToken);

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("#252938"), new RectangleF(58, y - 12, 1084, 118));
            ctx.DrawImage(championIcon, new Point(76, y), 1f);
            ctx.DrawText(FitText(card.PlayerName, 22), nameFont, Text, new PointF(184, y + 4));
            ctx.DrawText(card.ChampionName, bodyFont, Muted, new PointF(186, y + 34));
        });

        DrawCompactStats(image, card.Stats, bodyFont, smallFont, 386, y);
        await DrawCompactAugmentsAsync(image, card.Augments, smallFont, 588, y, cancellationToken);
        await DrawCompactItemsAsync(image, card.Items, 990, y, cancellationToken);
    }

    private static void DrawCompactStats(Image<Rgba32> image, MatchStats stats, Font valueFont, Font labelFont, int x, int y)
    {
        var statItems = new[]
        {
            ("DMG", stats.DamageDealtToChampions, DamageColor),
            ("TAKEN", stats.DamageTaken, TakenColor),
            ("HEAL", stats.HealingDone, HealingColor),
            ("MIT", stats.DamageMitigated, MitigatedColor)
        };

        image.Mutate(ctx =>
        {
            for (var index = 0; index < statItems.Length; index++)
            {
                var statX = x + (index % 2) * 108;
                var statY = y + (index / 2) * 50;
                ctx.DrawText(FormatNumber(statItems[index].Item2), valueFont, statItems[index].Item3, new PointF(statX, statY));
                ctx.DrawText(statItems[index].Item1, labelFont, Muted, new PointF(statX, statY + 24));
            }
        });
    }

    private async Task DrawCompactAugmentsAsync(
        Image<Rgba32> image,
        IReadOnlyList<AugmentAsset> augments,
        Font font,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        const int iconSize = 24;

        for (var index = 0; index < Math.Min(augments.Count, 6); index++)
        {
            var augment = augments[index];
            var rowX = x + (index / 3) * 210;
            var rowY = y + (index % 3) * 34;
            using var icon = await LoadIconAsync(augment.IconUrl, iconSize, cancellationToken);

            image.Mutate(ctx =>
            {
                ctx.DrawImage(icon, new Point(rowX, rowY), 1f);
                ctx.DrawText(FitText(augment.Name, 22), font, GetAugmentColor(augment.Rarity), new PointF(rowX + 32, rowY + 4));
            });
        }
    }

    private async Task DrawCompactItemsAsync(
        Image<Rgba32> image,
        IReadOnlyList<ItemAsset> items,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        const int iconSize = 36;

        for (var index = 0; index < Math.Min(items.Count, 6); index++)
        {
            var item = items[index];
            var itemX = x + (index % 3) * 46;
            var itemY = y + (index / 3) * 46;
            using var icon = await LoadIconAsync(item.IconUrl, iconSize, cancellationToken);
            image.Mutate(ctx => ctx.DrawImage(icon, new Point(itemX, itemY), 1f));
        }
    }

    private async Task DrawAugmentsAsync(
        Image<Rgba32> image,
        IReadOnlyList<AugmentAsset> augments,
        Font font,
        CancellationToken cancellationToken)
    {
        const int startX = 58;
        const int startY = 338;
        const int rowHeight = 34;
        const int iconSize = 28;

        for (var index = 0; index < Math.Min(augments.Count, 6); index++)
        {
            var augment = augments[index];
            var y = startY + index * rowHeight;
            var x = startX;
            using var icon = await LoadIconAsync(augment.IconUrl, iconSize, cancellationToken);

            image.Mutate(ctx =>
            {
                ctx.DrawImage(icon, new Point(x, y), 1f);
                ctx.DrawText(FitText(augment.Name, 36), font, GetAugmentColor(augment.Rarity), new PointF(x + 42, y + 3));
            });
        }
    }

    private async Task DrawItemsAsync(
        Image<Rgba32> image,
        IReadOnlyList<ItemAsset> items,
        Font font,
        CancellationToken cancellationToken)
    {
        const int startX = 608;
        const int startY = 338;
        const int iconSize = 48;
        const int tileWidth = 58;

        for (var index = 0; index < Math.Min(items.Count, 7); index++)
        {
            var item = items[index];
            var x = startX + index * tileWidth;
            var y = startY;
            using var icon = await LoadIconAsync(item.IconUrl, iconSize, cancellationToken);

            image.Mutate(ctx =>
            {
                ctx.DrawImage(icon, new Point(x + 2, y), 1f);
            });
        }
    }

    private async Task<Image<Rgba32>> LoadIconAsync(string url, int size, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return CreatePlaceholder(size);
        }

        try
        {
            var bytes = await httpClient.GetByteArrayAsync(url, cancellationToken);
            var image = Image.Load<Rgba32>(bytes);
            image.Mutate(ctx => ctx.Resize(size, size));
            return image;
        }
        catch
        {
            return CreatePlaceholder(size);
        }
    }

    private static Image<Rgba32> CreatePlaceholder(int size)
    {
        var image = new Image<Rgba32>(size, size, Color.ParseHex("#2f3447"));
        image.Mutate(ctx => ctx.Draw(Color.ParseHex("#51586f"), 2, new RectangleF(1, 1, size - 2, size - 2)));
        return image;
    }

    private static string FitText(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : $"{value[..Math.Max(0, maxLength - 1)]}.";
    }

    private static string FormatNumber(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString();
    }

    private static Color GetAugmentColor(int rarity)
    {
        return rarity switch
        {
            0 => Color.ParseHex("#c9d3df"),
            1 => Color.ParseHex("#ffd166"),
            2 or 4 => Color.ParseHex("#f2a7ff"),
            _ => Text
        };
    }

    private static FontFamily ResolveFontFamily()
    {
        var fonts = SystemFonts.Collection;
        var family = fonts.Families.FirstOrDefault(f => f.Name.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(family.Name) ? fonts.Families.First() : family;
    }
}
