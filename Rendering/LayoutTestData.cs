using DiscordBot.Models;

namespace DiscordBot.Rendering;

public static class LayoutTestData
{
    public static IReadOnlyList<MatchCardData> CreateGroupCards()
    {
        return
        [
            CreateCard("ExtremelyLongSummonerName#EUW123", "Senna", 1),
            CreateCard("AnotherVeryLongRiotIdentifier#DKK", "Velkoz", 1),
            CreateCard("NameThatDefinitelyShouldNotOverflow#TEST", "Smolder", 1)
        ];
    }

    private static MatchCardData CreateCard(string playerName, string championName, int placement)
    {
        return new MatchCardData(
            playerName,
            championName,
            string.Empty,
            [
                new ItemAsset(1, "Gargoyle Stoneplate", string.Empty),
                new ItemAsset(2, "Demon King's Crown", string.Empty),
                new ItemAsset(3, "Radiant Virtue", string.Empty),
                new ItemAsset(4, "Cloak of Starry Night", string.Empty),
                new ItemAsset(5, "Flesheater", string.Empty),
                new ItemAsset(6, "Moonstone Renewer", string.Empty)
            ],
            [
                new AugmentAsset(1, "Gain a Prismatic Stat", string.Empty, 4),
                new AugmentAsset(2, "Augmented Power", string.Empty, 2),
                new AugmentAsset(3, "Dematerialize", string.Empty, 0),
                new AugmentAsset(4, "Replace Augment", string.Empty, 1),
                new AugmentAsset(5, "Scopiest Weapons", string.Empty, 2),
                new AugmentAsset(6, "Tank Engine", string.Empty, 0)
            ],
            "LAYOUT_TEST_MATCH",
            placement,
            new MatchStats(123456, 98765, 54321, 111111));
    }
}
