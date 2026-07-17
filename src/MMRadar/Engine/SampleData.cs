using System;
using System.Collections.Generic;
using MMRadar.Wallii;

namespace MMRadar.Engine
{
    /// <summary>Fake lobby used by the "Preview with sample data" menu action and the harness.</summary>
    public static class SampleData
    {
        public static List<PlayerSummary> Lobby()
        {
            return new List<PlayerSummary>
            {
                new PlayerSummary { LobbyName = "beterbabbit", DisplayName = "beterbabbit", OnLeaderboard = true, PlayerId = 2827, Region = "NA", Rating = 18892, Rank = 2, DayAvg = 2.42, WeekAvg = 1.92, GamesToday = 13, GamesWeek = 48, IsLive = true, TwitchChannel = "beterbabbit" },
                new PlayerSummary { LobbyName = "стоякбездела", DisplayName = "стоякбездела", OnLeaderboard = true, PlayerId = 3324, Region = "EU", Rating = 18715, Rank = 3, DayAvg = 2.97, WeekAvg = 3.05, GamesToday = 15, GamesWeek = 41 },
                new PlayerSummary { LobbyName = "sevel", DisplayName = "sevel", OnLeaderboard = true, PlayerId = 3185, Region = "EU", Rating = 17220, Rank = 9, DayAvg = 3.38, WeekAvg = 2.40, GamesToday = 4, GamesWeek = 21, IsLive = true, TwitchChannel = "sevel07" },
                new PlayerSummary { LobbyName = "YourHero", DisplayName = "YourHero", OnLeaderboard = true, PlayerId = 9001, Region = "EU", Rating = 12480, Rank = 214, DayAvg = 4.1, WeekAvg = 3.8, GamesToday = 6, GamesWeek = 33, IsLocalPlayer = true },
                new PlayerSummary { LobbyName = "kuositovélez", DisplayName = "kuositovélez", OnLeaderboard = true, PlayerId = 9002, Region = "EU", Rating = 12314, Rank = 146, DayAvg = 4.55, WeekAvg = 4.6, GamesToday = 9, GamesWeek = 52 },
                new PlayerSummary { LobbyName = "ironq", DisplayName = "ironq", OnLeaderboard = true, PlayerId = 9003, Region = "EU", Rating = 11074, Rank = 323, DayAvg = 5.2, WeekAvg = 4.9, GamesToday = 3, GamesWeek = 18 },
                new PlayerSummary { LobbyName = "RandomLegend", OnLeaderboard = false },
                new PlayerSummary { LobbyName = "МишаТаверна", OnLeaderboard = false },
            };
        }

        public static PlayerDetails Details(PlayerSummary summary)
        {
            var rng = new Random(summary.PlayerId);
            var rating = summary.Rating;
            var history = new List<int>();
            var games = new List<GameRecord>();
            var t = DateTimeOffset.UtcNow;
            for (var i = 0; i < 10; i++)
            {
                var delta = rng.Next(-70, 75);
                games.Add(new GameRecord
                {
                    At = t,
                    DeltaMmr = delta,
                    EndingMmr = rating,
                    Placement = PlacementEstimator.EstimatePlacement(rating - delta, rating),
                });
                history.Insert(0, rating);
                rating -= delta;
                t = t.AddMinutes(-rng.Next(25, 90));
            }
            history.Insert(0, rating);
            return new PlayerDetails
            {
                Summary = summary,
                RecentGames = games,
                RecentAvg = PlacementEstimator.Average(games),
                RatingHistory = history,
            };
        }
    }
}
