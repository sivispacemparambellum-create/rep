// AIController.cs — Дирижёр ботов: вызывает IBotStrategy, валидирует действия и применяет их к GameManager.
// Ключевой метод: DecideNextMove(GameState) -> BotAction (AssignJob / BuyBusiness / PlayInfluence / OfferDeal).
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;
using LCN.Core;

namespace LCN.AI
{
    public class AIController
    {
        private readonly Dictionary<string, IBotStrategy> _strategies = new(); // playerId -> стратегия
        private readonly GameManager _gm;
        private readonly DealSystem _deals;

        public AIController(GameManager gm, DealSystem deals)
        {
            _gm = gm;
            _deals = deals;

            // Подключаем AI-хуки в GameManager (реакции ботов решаются здесь, не в UI)
            gm.BotWantsReaction = (owner, actor, incoming, reaction) =>
                _strategies.TryGetValue(owner.Id, out var s) &&
                s.ShouldPlayReaction(gm.State, owner, actor, incoming, reaction);
        }

        public void RegisterBot(PlayerController bot, BotArchetype archetype)
        {
            bot.BotType = archetype;
            _strategies[bot.Id] = archetype switch
            {
                BotArchetype.Aggressive => new AggressiveBot(),
                BotArchetype.Economist  => new EconomistBot(),
                _                       => new BalancedBot()
            };
        }

        /// <summary>Публичный API из ТЗ: возвращает следующее действие бота по состоянию игры.</summary>
        public BotAction DecideNextMove(GameState state)
        {
            // По умолчанию: первый бот с первым свободным гангстером (используется юнит-тестами/UI-стримом)
            foreach (var p in state.InTurnOrder())
            {
                if (p.IsHuman || !_strategies.ContainsKey(p.Id)) continue;
                var free = BotHelpersPublic.FirstFreeGangster(state, p);
                if (free == null) continue;
                return DecideNextMove(state, p, free);
            }
            return new BotAction { Type = BotActionType.Skip };
        }

        public BotAction DecideNextMove(GameState state, PlayerController me, CardInstance gangster)
        {
            var strategy = _strategies[me.Id];

            // 0. Сначала — карты влияния мгновенного действия (Snitch/Spy/Henchman до назначения дел)
            var influence = strategy.DecideInfluencePlay(state, me);
            if (influence != null && ValidateInfluence(state, me, influence)) return influence;

            // 1. Основное решение стратегии
            var action = strategy.DecideNextMove(state, me, gangster);

            // 2. Если стратегия просит сделку — реально генерируем DealOffer через DealSystem
            if (action.Type == BotActionType.OfferDeal && action.TargetBusinessUid != null)
            {
                action.DealProposal = _deals.CreateOffer(me.Id, action.TargetBusinessUid);
                if (action.DealProposal == null || me.Cash < action.DealProposal.ProposedPrice)
                    action = FallbackToPurchaseOrSkip(state, me, gangster, strategy);
            }

            return Validate(state, me, action) ? action : FallbackToPurchaseOrSkip(state, me, gangster, strategy);
        }

        private BotAction FallbackToPurchaseOrSkip(GameState state, PlayerController me,
            CardInstance gangster, IBotStrategy strategy)
        {
            // Логика планирования из ТЗ: нет условий → купить нужный бизнес; рынок пуст/дорого → блеф-дело
            var affordable = state.MarketSlots
                .Where(s => s.AsBusiness != null && me.Cash >= s.AsBusiness.Price)
                .OrderBy(s => s.AsBusiness.Price)
                .FirstOrDefault();
            if (affordable != null)
                return new BotAction { Type = BotActionType.BuyBusiness, BusinessToBuy = affordable };

            var bluffJob = me.Hand.Jobs.FirstOrDefault(c => c.AsJob?.Type != JobType.Reaction);
            if (bluffJob != null)
                return new BotAction { Type = BotActionType.AssignJob,
                                       GangsterInstanceId = gangster.UniqueId, Job = bluffJob, IsBluff = true };
            return new BotAction { Type = BotActionType.Skip };
        }

        // ============ ВАЛИДАЦИЯ (античит логики: бот не может сделать невозможное) ============

        private bool Validate(GameState state, PlayerController me, BotAction a) => a.Type switch
        {
            BotActionType.AssignJob => a.Job != null && me.Hand.Jobs.Contains(a.Job)
                                       && state.Players.Any(p => p.Id == me.Id)
                                       && (a.GangsterInstanceId != null),
            BotActionType.BuyBusiness => a.BusinessToBuy != null
                                         && state.MarketSlots.Contains(a.BusinessToBuy)
                                         && me.Cash >= a.BusinessToBuy.AsBusiness.Price,
            BotActionType.PlayInfluence => ValidateInfluence(state, me, a),
            BotActionType.OfferDeal => a.DealProposal != null,
            BotActionType.Skip => true,
            _ => false
        };

        private bool ValidateInfluence(GameState state, PlayerController me, BotAction a)
        {
            if (a.InfluenceToPlay == null) return false;
            return me.Hand.Influences.Any(c => c.Data == a.InfluenceToPlay);
        }

        // ============ ПРИМЕНЕНИЕ ДЕЙСТВИЙ (вызывается GameManager в Planning-фазе) ============

        /// <summary>Ход всех ботов в фазе Planning: заполняют PlannedActions, покупают, предлагают сделки.</summary>
        public void RunPlanningPhaseForAllBots()
        {
            var state = _gm.State;
            foreach (var bot in state.Players.Where(p => !p.IsHuman && _strategies.ContainsKey(p.Id)))
            {
                int guard = 0; // страховка от зацикливания
                while (guard++ < 8)
                {
                    var free = BotHelpersPublic.FirstFreeGangster(state, bot);
                    var action = DecideNextMove(state, bot, free ?? bot.Gangsters.FirstOrDefault());
                    if (action.Type == BotActionType.Skip || (free == null && action.Type == BotActionType.AssignJob))
                        break;

                    Apply(state, bot, action, free);
                    if (action.Type is BotActionType.AssignJob or BotActionType.OfferDeal) { /* гангстер занят/сделка есть */ }
                    if (action.Type == BotActionType.AssignJob && free == null) break;
                }
            }
        }

        private void Apply(GameState state, PlayerController bot, BotAction a, CardInstance gangster)
        {
            switch (a.Type)
            {
                case BotActionType.AssignJob:
                    bot.PlannedActions.Add(new AssignedAction
                    {
                        GangsterInstanceId = gangster?.UniqueId,
                        Job = a.Job,
                        TargetPlayerId = a.TargetPlayerId,
                        TargetBusinessUid = a.TargetBusinessUid,
                        IsBluff = a.IsBluff
                    });
                    break;

                case BotActionType.BuyBusiness:
                    if (_gm.Market.BuyBusiness(bot, a.BusinessToBuy, out _))
                        state.Log.Add($"ai.buy:{bot.Id}:{a.BusinessToBuy.Data.Id}");
                    break;

                case BotActionType.PlayInfluence:
                    PlayInfluence(state, bot, a);
                    break;

                case BotActionType.OfferDeal:
                    if (a.DealProposal != null)
                    {
                        var owner = state.GetPlayer(a.DealProposal.TargetPlayerId);
                        if (!owner.IsHuman) _deals.BotEvaluateAndRespond(owner, a.DealProposal);
                        else HumanBotDealRequested?.Invoke(a.DealProposal); // UI покажет авто-цену игроку
                    }
                    break;
            }
        }

        /// <summary>Событие для UI: бот предлагает человеку сделку на его бизнес.</summary>
        public System.Action<DealOffer> HumanBotDealRequested;

        private void PlayInfluence(GameState state, PlayerController bot, BotAction a)
        {
            var inst = bot.Hand.Influences.FirstOrDefault(c => c.Data == a.InfluenceToPlay);
            if (inst == null) return;
            bot.Hand.Influences.Remove(inst);

            var inf = a.InfluenceToPlay;
            if (inf.AttachToGangster && a.TargetBusinessUid != null)
            {
                // Привязка к гангстеру (TargetBusinessUid переиспользуется как uid гангстера для целей AI)
                var target = state.Players.SelectMany(p => p.Gangsters)
                             .FirstOrDefault(g => g.UniqueId == a.TargetBusinessUid);
                if (target != null) target.AttachedInfluences.Add(inf);
            }
            else if (inf.IsInstant && inf.Effect is InfluenceType.Snitch or InfluenceType.Spy)
            {
                // Разведка: бот «подглядывает» — храним знание в локальном memory стратегии
                _scoutingMemory[bot.Id] = PeekHiddenJobs(state, a.TargetPlayerId);
            }
            state.Log.Add($"ai.influence:{bot.Id}:{inf.Effect}");
        }

        private readonly Dictionary<string, List<JobCard>> _scoutingMemory = new();
        private static List<JobCard> PeekHiddenJobs(GameState state, string targetId) =>
            state.GetPlayer(targetId)?.Hand.Jobs.Select(c => c.AsJob).Where(j => j != null).ToList() ?? new();

        /// <summary>Дипломат использует собранные разведданные при оценке риска сделки.</summary>
        public List<JobCard> GetScoutingReport(string botId) =>
            _scoutingMemory.TryGetValue(botId, out var v) ? v : new();
    }

    /// <summary>Публичная обёртка внутренних хелперов стратегий.</summary>
    public static class BotHelpersPublic
    {
        public static CardInstance FirstFreeGangster(GameState state, PlayerController p)
        {
            return p.Gangsters.FirstOrDefault(g =>
                !g.IsDeactivated && (g.Data as GangsterCard)?.IsAlive == true &&
                !p.PlannedActions.Any(a => a.GangsterInstanceId == g.UniqueId));
        }
    }
}
