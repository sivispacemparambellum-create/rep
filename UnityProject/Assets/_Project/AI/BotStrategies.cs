// IBotStrategy.cs + три архетипа ботов.
// Каждая стратегия получает GameState и возвращает BotAction — чистые функции, легко тестировать.
using System;
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;

namespace LCN.AI
{
    using LCN.Core;

    /// <summary>Действие, выбранное ботом на свой ход планирования.</summary>
    public class BotAction
    {
        public BotActionType Type;
        public string GangsterInstanceId;
        public CardInstance Job;
        public string TargetPlayerId;
        public string TargetBusinessUid;
        public CardInstance BusinessToBuy;
        public InfluenceCard InfluenceToPlay;
        public DealOffer DealProposal;      // для OfferDeal
        public bool IsBluff;
    }

    public interface IBotStrategy
    {
        BotArchetype Archetype { get; }

        /// <summary>Главный вход: что делает бот этим свободным гангстером.</summary>
        BotAction DecideNextMove(GameState state, PlayerController me, CardInstance freeGangster);

        /// <summary>Оценка выгодности покупки бизнеса с рынка.</summary>
        float ScoreBusinessPurchase(GameState state, PlayerController me, BusinessCard biz);

        /// <summary>Решение о реакции (Trap/Robbery) в чужом действии.</summary>
        bool ShouldPlayReaction(GameState state, PlayerController me,
                                PlayerController actor, JobCard incomingJob, JobCard reactionCard);

        /// <summary>Использование мгновенной карты влияния (Snitch/Saboteur/Henchman).</summary>
        BotAction DecideInfluencePlay(GameState state, PlayerController me);
    }

    // ================= ОБЩИЕ ЭВРИСТИКИ =================
    internal static class BotHelpers
    {
        public static IEnumerable<CardInstance> FreeGangsters(PlayerController p) =>
            p.Gangsters.Where(g => !g.IsDeactivated && (g.Data as GangsterCard)?.IsAlive == true
                && p.PlannedActions.All(a => a.GangsterInstanceId != g.UniqueId));

        /// <summary>Дела, которые бот может выполнить прямо сейчас (пререквизиты закрыты своими бизнесами).</summary>
        public static List<(CardInstance jobInst, JobCard job, int value)> ExecutableJobs(
            GameState state, PlayerController me)
        {
            return me.Hand.Jobs
                .Select(c => (c, c.AsJob))
                .Where(x => x.Item2 != null && state.CheckPrerequisites(x.Item2, me))
                .Select(x => (x.c, x.Item2, EvaluateJobValue(state, me, x.Item2)))
                .OrderByDescending(t => t.Item3)
                .ToList();
        }

        public static int EvaluateJobValue(GameState state, PlayerController me, JobCard job)
        {
            if (job.Type == JobType.Profitable)
                return job.CashReward - 1000 * job.Difficulty;
            if (job.Type == JobType.Attack)
            {
                var target = WeakestRichFoe(state, me);
                if (target == null) return 0;
                int damage = job.Attack?.CashStealOnSuccess ?? 4000;
                return damage / 2 + (job.Attack?.SuccessesToKill > 0 ? 3000 : 0);
            }
            return 500; // реакция — держим в резерве
        }

        public static PlayerController WeakestRichFoe(GameState state, PlayerController me) =>
            state.Players.Where(p => p.Id != me.Id)
                         .OrderByDescending(p => p.Cash)
                         .ThenBy(p => p.AliveGangsters.Sum(g => g.Strength))
                         .FirstOrDefault();

        public static int SuccessChanceEstimate(GameState state, DiceManager dm,
                                                PlayerController me, CardInstance gangster, JobCard job)
        {
            var gc = (GangsterCard)gangster.Data;
            int strength = PlayerController.EffectiveStrength(gc, gangster) + job.DiceBonus;
            int dice = Math.Max(0, strength - job.Difficulty);
            // P(dice>=1) при p=1/3 за кубик
            double p = 1 - Math.Pow(2.0 / 3.0, Math.Max(1, dice));
            return (int)(p * 100);
        }
    }

    // ================= А. AGGRESSIVE BOT =================
    public class AggressiveBot : IBotStrategy
    {
        public BotArchetype Archetype => BotArchetype.Aggressive;

        public BotAction DecideNextMove(GameState state, PlayerController me, CardInstance freeGangster)
        {
            // Приоритет 1: атаки из руки (даже без условий — часть пойдёт «в блеф», давя на оппонентов)
            var attack = me.Hand.Jobs.Select(c => (c, j: c.AsJob))
                       .Where(x => x.j.Type == JobType.Attack)
                       .OrderByDescending(x => x.j.Attack?.CashStealOnSuccess ?? 0)
                       .FirstOrDefault();
            if (attack.c != null)
            {
                var victim = BotHelpers.WeakestRichFoe(state, me);
                return new BotAction
                {
                    Type = BotActionType.AssignJob,
                    GangsterInstanceId = freeGangster.UniqueId,
                    Job = attack.c,
                    TargetPlayerId = victim?.Id,
                    IsBluff = !state.CheckPrerequisites(attack.j, me)
                };
            }

            // Приоритет 2: прибыльные дела, если выполнимы
            var jobs = BotHelpers.ExecutableJobs(state, me).Where(x => x.job.Type == JobType.Profitable).ToList();
            if (jobs.Count > 0)
                return new BotAction
                {
                    Type = BotActionType.AssignJob,
                    GangsterInstanceId = freeGangster.UniqueId,
                    Job = jobs[0].jobInst
                };

            // Приоритет 3: покупка сильного гангстера/доходного бизнеса ради будущей агрессии
            var buy = state.MarketSlots
                        .Select(s => (s, score: ScoreBusinessPurchase(state, me, s.AsBusiness)))
                        .OrderByDescending(x => x.score).FirstOrDefault();
            if (buy.s != null && buy.score > 0 && me.Cash >= buy.s.AsBusiness.Price)
                return new BotAction { Type = BotActionType.BuyBusiness, BusinessToBuy = buy.s };

            // Иначе — случайное дело для блефа (симуляция давления в живом столе)
            var rnd = me.Hand.Jobs.FirstOrDefault();
            return rnd != null
                ? new BotAction { Type = BotActionType.AssignJob, GangsterInstanceId = freeGangster.UniqueId, Job = rnd, IsBluff = true }
                : new BotAction { Type = BotActionType.Skip };
        }

        public float ScoreBusinessPurchase(GameState state, PlayerController me, BusinessCard biz)
        {
            // Агрессору бизнес нужен в основном как щит/цель; ценит дешёвые компании
            return biz.Income * 0.6f - biz.Price * 0.3f;
        }

        public bool ShouldPlayReaction(GameState state, PlayerController me, PlayerController actor,
                                       JobCard incomingJob, JobCard reactionCard)
        {
            // Спасает себя от критического урона ИЛИ грабит богача-активиста
            bool critical = incomingJob.Type == JobType.Attack
                            && (incomingJob.Attack?.CashStealOnSuccess ?? 0) > me.Cash * 0.3f;
            bool profitable = reactionCard.Reaction?.CashPenaltyToActive > 5000 && actor.Cash > 15000;
            return critical || profitable;
        }

        public BotAction DecideInfluencePlay(GameState state, PlayerController me)
        {
            // Saboteur на самого опасного гангстера лидера рынка; Distraction бережёт до своей атаки
            var sab = me.Hand.Influences.Select(c => c.Data as InfluenceCard)
                      .FirstOrDefault(i => i != null && i.Effect == InfluenceType.Saboteur);
            if (sab != null)
            {
                var threat = state.Players.Where(p => p.Id != me.Id)
                             .OrderByDescending(p => p.AliveGangsters.Sum(g => g.Strength)).FirstOrDefault();
                var gInst = threat?.Gangsters.FirstOrDefault(g => (g.Data as GangsterCard)?.IsAlive == true);
                if (gInst != null)
                    return new BotAction { Type = BotActionType.PlayInfluence, InfluenceToPlay = sab,
                                           TargetPlayerId = threat.Id, TargetBusinessUid = gInst.UniqueId };
            }
            return null;
        }
    }

    // ================= Б. ECONOMIST BOT =================
    public class EconomistBot : IBotStrategy
    {
        public BotArchetype Archetype => BotArchetype.Economist;

        public BotAction DecideNextMove(GameState state, PlayerController me, CardInstance freeGangster)
        {
            // Приоритет 1: монополии — докупить бизнесмена категории, где уже есть >=1
            var catCounts = me.ActiveBusinesses
                .Where(b => b.AsBusiness.Kind == BusinessKind.Businessman)
                .GroupBy(b => b.AsBusiness.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            var monopolyTarget = state.MarketSlots
                .Select(s => (s, biz: s.AsBusiness))
                .Where(x => x.biz != null && x.biz.Kind == BusinessKind.Businessman
                            && catCounts.TryGetValue(x.biz.Category, out int owned) && owned >= 1
                            && me.Cash >= x.biz.Price)
                .OrderByDescending(x => ScoreBusinessPurchase(state, me, x.biz))
                .FirstOrDefault();

            if (monopolyTarget.s != null)
                return new BotAction { Type = BotActionType.BuyBusiness, BusinessToBuy = monopolyTarget.s };

            // Приоритет 2: самые доходные дела (без конфликтов — атаки игнорируем)
            var jobs = BotHelpers.ExecutableJobs(state, me)
                        .Where(x => x.job.Type == JobType.Profitable).ToList();
            if (jobs.Count > 0)
                return new BotAction { Type = BotActionType.AssignJob,
                                       GangsterInstanceId = freeGangster.UniqueId, Job = jobs[0].jobInst };

            // Приоритет 3: лучший бизнес по ROI (доход/цена), включая businessmen "на вырост"
            var best = state.MarketSlots
                .Select(s => (s, score: ScoreBusinessPurchase(state, me, s.AsBusiness)))
                .Where(x => x.score > 0 && me.Cash >= x.s.AsBusiness.Price)
                .OrderByDescending(x => x.score).FirstOrDefault();
            if (best.s != null)
                return new BotAction { Type = BotActionType.BuyBusiness, BusinessToBuy = best.s };

            return new BotAction { Type = BotActionType.Skip }; // копит деньги — экономист терпелив
        }

        public float ScoreBusinessPurchase(GameState state, PlayerController me, BusinessCard biz)
        {
            int remaining = Math.Max(1, 4 - state.Round + 1);
            float roi = (biz.Income * remaining) / (float)Math.Max(1, biz.Price);
            // Бонус за уже начатую категорию (стратегия монополии)
            bool sameCat = me.ActiveBusinesses.Any(b => b.AsBusiness.Category == biz.Category
                                                    && b.AsBusiness.Kind == BusinessKind.Businessman);
            return roi * (sameCat ? 1.8f : 1f) + (biz.Kind == BusinessKind.Businessman ? 0.5f : 0f);
        }

        public bool ShouldPlayReaction(GameState state, PlayerController me, PlayerController actor,
                                       JobCard incomingJob, JobCard reactionCard)
        {
            // Избегает конфликтов: реагирует только прямой защитой своего бизнеса/денег
            return incomingJob.Type == JobType.Attack
                   && (incomingJob.Attack?.CashStealOnSuccess ?? 0) > 8000;
        }

        public BotAction DecideInfluencePlay(GameState state, PlayerController me)
        {
            // Henchman всегда на самого слабого кубика — выравниваем шансы дешёвым способом
            var h = me.Hand.Influences.Select(c => c.Data as InfluenceCard)
                    .FirstOrDefault(i => i != null && i.Effect == InfluenceType.Henchman);
            if (h != null)
            {
                var weakest = me.Gangsters.OrderBy(g => ((GangsterCard)g.Data).Strength).FirstOrDefault();
                if (weakest != null)
                    return new BotAction { Type = BotActionType.PlayInfluence, InfluenceToPlay = h,
                                           TargetBusinessUid = weakest.UniqueId };
            }
            return null;
        }
    }

    // ================= В. BALANCED BOT (Дипломат) =================
    public class BalancedBot : IBotStrategy
    {
        public BotArchetype Archetype => BotArchetype.Balanced;

        public BotAction DecideNextMove(GameState state, PlayerController me, CardInstance freeGangster)
        {
            // 1. Дело с выполненными условиями — берём самое дорогое
            var ready = BotHelpers.ExecutableJobs(state, me);
            if (ready.Count > 0 && ready[0].value > 2000)
                return new BotAction { Type = BotActionType.AssignJob,
                                       GangsterInstanceId = freeGangster.UniqueId, Job = ready[0].jobInst };

            // 2. Нет условий? Ищем чужой бизнес под сделку (взаимовыгодную!) через DealSystem
            //    (AIController вызывает это отдельно — здесь помечаем цель)
            var dealTarget = FindMutuallyBeneficialBusiness(state, me);
            if (dealTarget != null)
                return new BotAction { Type = BotActionType.OfferDeal, TargetBusinessUid = dealTarget };

            // 3. Spy/Snitch для оценки рисков перед крупными ходами
            var spy = me.Hand.Influences.Select(c => c.Data as InfluenceCard)
                    .FirstOrDefault(i => i != null && (i.Effect == InfluenceType.Snitch || i.Effect == InfluenceType.Spy));
            if (spy != null && state.Round <= 3)
                return new BotAction { Type = BotActionType.PlayInfluence, InfluenceToPlay = spy };

            // 4. Защита: покупка Bodyguard-совместимых активов или бизнеса с доходом
            var safe = state.MarketSlots
                .Select(s => (s, biz: s.AsBusiness))
                .Where(x => x.biz != null && me.Cash >= x.biz.Price)
                .OrderByDescending(x => x.biz.Income).FirstOrDefault();
            if (safe.s != null && me.Cash > safe.biz.Price * 2) // финансовая подушка
                return new BotAction { Type = BotActionType.BuyBusiness, BusinessToBuy = safe.s };

            return new BotAction { Type = BotActionType.Skip };
        }

        private string FindMutuallyBeneficialBusiness(GameState state, PlayerController me)
        {
            // Бизнес нужной категории у того, кому сам могу «вернуть услугу» той же категорией
            var neededCats = me.Hand.Jobs.Select(c => c.AsJob)
                .Where(j => j != null && j.Type != JobType.Reaction)
                .SelectMany(j => j.Prerequisites.Select(p => p.Category)).Distinct();

            foreach (var cat in neededCats)
            {
                var candidate = state.Players.Where(p => p.Id != me.Id)
                    .SelectMany(p => p.ActiveBusinesses.Where(b => b.AsBusiness.Category == cat && !b.AsBusiness.CannotBeDealt)
                        .Select(b => (p, b)))
                    .OrderBy(x => x.p.Cash) // бедные согласнее
                    .FirstOrDefault();
                if (candidate.b != null) return candidate.b.UniqueId;
            }
            return null;
        }

        public float ScoreBusinessPurchase(GameState state, PlayerController me, BusinessCard biz)
        {
            int remaining = Math.Max(1, 4 - state.Round + 1);
            float incomeScore = biz.Income * remaining / (float)Math.Max(1, biz.Price);
            // Дипломат ценит «разменные» категории — те, что часто требуются в сделках
            float tradeUtility = biz.Category is MonopolyCategory.Credit or MonopolyCategory.Construction ? 0.4f : 0f;
            return incomeScore + tradeUtility;
        }

        public bool ShouldPlayReaction(GameState state, PlayerController me, PlayerController actor,
                                       JobCard incomingJob, JobCard reactionCard)
        {
            // Средний порог: реагирует при реальном ущербе, но не тратит реакцию «в пустую»
            int myLoss = incomingJob.Attack?.CashStealOnSuccess ?? 0;
            int gain = reactionCard.Reaction?.CashPenaltyToActive ?? 0;
            return myLoss > 6000 || gain > myLoss && actor.Cash > gain;
        }

        public BotAction DecideInfluencePlay(GameState state, PlayerController me)
        {
            // Snitch на игрока-лидера: скрытно оценить его руку дел → снизить риск будущих сделок
            var snitch = me.Hand.Influences.Select(c => c.Data as InfluenceCard)
                       .FirstOrDefault(i => i != null && i.Effect == InfluenceType.Snitch);
            if (snitch != null)
            {
                var leader = state.Players.Where(p => p.Id != me.Id)
                             .OrderByDescending(p => p.TotalIncome()).FirstOrDefault();
                if (leader != null && state.Round >= 2)
                    return new BotAction { Type = BotActionType.PlayInfluence, InfluenceToPlay = snitch,
                                           TargetPlayerId = leader.Id };
            }
            return null;
        }
    }
}
