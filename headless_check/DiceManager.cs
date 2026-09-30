// DiceManager.cs — Изолированная логика бросков. Никаких зависимостей от UI/Unity:
// детерминированность через seed => реплеи и серверная валидация.
using System;
using System.Collections.Generic;
using LCN.Cards;

namespace LCN.Core
{
    /// <summary>Результат одного броска дела.</summary>
    public struct RollResult
    {
        public int DiceRolled;
        public int Successes;
        public int Difficulty;
        public List<int> RawValues;
        public bool JobSucceeded => Successes >= Math.Max(1, Difficulty);
        public RollOutcome Outcome;
    }

    /// <summary>
    /// Кубики La Cosa Nostra: 6-гранные, успех = выпавшая грань >= порога (по умолчанию 5).
    /// Сложность дела уменьшает число кубиков; модификаторы влияния (Henchman/Saboteur)
    /// применяются к силе ДО броска.
    /// </summary>
    public class DiceManager
    {
        public const int DieFaces = 6;
        public const int SuccessThreshold = 5;   // 5 или 6 = успех (настраивается конфибом баланса)

        private readonly Random _rng;

        public DiceManager(int? seed = null)
        {
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>Главный API: сколько успехов дал бросок силы против сложности.</summary>
        public int RollDice(int strength, int difficulty) => PerformRoll(strength, difficulty).Successes;

        public RollResult PerformRoll(int strength, int difficulty)
        {
            int dice = Math.Max(0, strength - Math.Max(0, difficulty));
            var raw = new List<int>(dice);
            int successes = 0;
            for (int i = 0; i < dice; i++)
            {
                int v = _rng.Next(1, DieFaces + 1);
                raw.Add(v);
                if (v >= SuccessThreshold) successes++;
            }

            var outcome = successes == 0 && dice > 0 ? RollOutcome.CriticalFailure
                        : successes >= Math.Max(1, difficulty) ? RollOutcome.Success
                        : RollOutcome.Failure;

            return new RollResult
            {
                DiceRolled = dice,
                Successes = successes,
                Difficulty = difficulty,
                RawValues = raw,
                Outcome = outcome
            };
        }

        /// <summary>Эффективная сила гангстера = Strength + модификаторы карт влияния на нём − Saboteur'ы цели.</summary>
        public static int ComputeEffectiveStrength(GangsterCard gangster, CardInstance instance,
                                                   IEnumerable<InfluenceCard> incomingSabotage)
        {
            int s = gangster.Strength;
            foreach (var inf in instance.AttachedInfluences)
                if (inf.Effect is InfluenceType.Henchman or InfluenceType.TommyGun or InfluenceType.MachinePistol)
                    s += inf.ModifierValue;
            foreach (var sab in incomingSabotage ?? Array.Empty<InfluenceCard>())
                if (sab.Effect == InfluenceType.Saboteur) s -= sab.ModifierValue;
            return Math.Max(0, s);
        }
    }
}
