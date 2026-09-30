// CardDatabase.cs — Загрузка JSON-базы карт + колоды/раздача.
// Требует Unity: using UnityEngine; using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using LCN.Core;

namespace LCN.Cards
{
    /// <summary>DTO-корень JSON-базы (см. Data/card_database.json).</summary>
    [Serializable]
    public class CardDatabaseRoot
    {
        public int Version;
        public List<JobCard> Jobs;
        public List<InfluenceCard> Influences;
        public List<BusinessCard> Businesses;
        public List<GangsterCard> Gangsters;
    }

    /// <summary>Игровая (живая) обёртка карты в руках/на столе.</summary>
    public class CardInstance
    {
        public CardData Data;
        public string UniqueId;               // генерируется при раздаче, не совпадает с Id базы
        public CardViewState State = CardViewState.FaceDown;
        public bool IsDeactivated;            // бизнес потерян/выключен (серый фильтр в UI)
        public List<DealToken> TokensOnIt = new();   // маркеры сделок на бизнесе
        public List<InfluenceCard> AttachedInfluences = new(); // Henchman/MachinePistol поверх

        public JobCard AsJob => Data as JobCard;
        public BusinessCard AsBusiness => Data as BusinessCard;
    }

    /// <summary>Общий доступ к базе карт (синглтон загрузчика).</summary>
    public static class CardDatabase
    {
        private static CardDatabaseRoot _root;
        public static IReadOnlyList<JobCard> AllJobs => _root.Jobs;
        public static IReadOnlyList<InfluenceCard> AllInfluences => _root.Influences;
        public static IReadOnlyList<BusinessCard> AllBusinesses => _root.Businesses;
        public static IReadOnlyList<GangsterCard> AllGangsters => _root.Gangsters;

        public static void LoadFromJson(string json)
        {
            // System.Text.Json: поддерживает List<T> на верхнем уровне, case-insensitive поля.
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            _root = System.Text.Json.JsonSerializer.Deserialize<CardDatabaseRoot>(json, options);
            ValidateCounts(_root);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private static void ValidateCounts(CardDatabaseRoot r)
        {
            if (r.Jobs.Count != 90) UnityEngine.Debug.LogWarning($"[CardDB] Expected 90 jobs, got {r.Jobs.Count}");
            if (r.Influences.Count != 35) UnityEngine.Debug.LogWarning($"[CardDB] Expected 35 influences, got {r.Influences.Count}");
            if (r.Businesses.Count != 34) UnityEngine.Debug.LogWarning($"[CardDB] Expected 34 businesses, got {r.Businesses.Count}");
        }

        public static JobCard GetJob(string id) => _root.Jobs.First(j => j.Id == id);
        public static BusinessCard GetBusiness(string id) => _root.Businesses.FirstOrDefault(b => b.Id == id);
    }

    /// <summary>Колода с тасовкой Fisher–Yates на системном RNG + seed для реплеев тестов.</summary>
    public class Deck<T> where T : CardData
    {
        private readonly Stack<CardInstance> _cards = new();
        private readonly Random _rng;

        public int Count => _cards.Count;
        public bool IsEmpty => _cards.Count == 0;

        public Deck(IEnumerable<T> cards, int? seed = null)
        {
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();
            var list = cards.Select(c => new CardInstance { Data = c, UniqueId = Guid.NewGuid().ToString("N") }).ToList();
            Shuffle(list);
            foreach (var c in list) _cards.Push(c);
        }

        private void Shuffle(List<CardInstance> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public CardInstance Draw() => _cards.Count == 0 ? null : _cards.Pop();
        public List<CardInstance> DrawMany(int n)
        {
            var res = new List<CardInstance>();
            for (int i = 0; i < n; i++) { var c = Draw(); if (c != null) res.Add(c); }
            return res;
        }
        public void Discard(CardInstance c) { /* сброс обратно не тасуется */ }
    }

    /// <summary>Раздача раунда: рубашка зависит от колоды (безопасность — клиент видит только BackKey).</summary>
    public static class RoundDealer
    {
        // Классика La Cosa Nostra: R1: 2 jobs+2 inf, R2: 3+3, R3: 4+3, R4: 5+2 (баланс правится в JSON-конфиге)
        public static (int jobs, int influences) DrawPlan(int round) => round switch
        {
            1 => (2, 2), 2 => (3, 3), 3 => (4, 3), _ => (5, 2)
        };

        public static void DealRound(GameState state, Dictionary<JobDeckRound, Deck<JobCard>> jobDecks,
                                     Deck<InfluenceCard> influenceDeck)
        {
            var (nj, ni) = DrawPlan(state.Round);
            foreach (var p in state.Players)
            {
                var deck = jobDecks[(JobDeckRound)(state.Round - 1)];
                p.Hand.Jobs.AddRange(deck.DrawMany(nj));
                p.Hand.Influences.AddRange(influenceDeck.DrawMany(ni));
            }
        }
    }
}

// Серверная часть не тянет UnityEngine — в standalone-хосте эти два using подменяются
// own JsonUtility/Debug shim'ом (см. README, секция "Headless mode").
#if !UNITY_EDITOR && !UNITY_2021_3_OR_NEWER
namespace UnityEngine
{
    public static class JsonUtility { public static T FromJson<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json); }
    public static class Debug { public static void LogWarning(object m) => Console.WriteLine(m); }
}
#endif
