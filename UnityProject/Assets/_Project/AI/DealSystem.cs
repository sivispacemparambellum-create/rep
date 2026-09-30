// DealSystem.cs — Автоматические переговоры с ботами + ручные сделки игрока.
// Формулы: MaxWillingToPay = ExpectedProfit * GreedFactor (инициатор)
//          MinAcceptablePrice = BasePrice + RiskFactor * PotentialLoss (владелец)
using System;
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;

namespace LCN.Core
{
    /// <summary>Предложение сделки на конкретный бизнес.</summary>
    public class DealOffer
    {
        public string OfferId = Guid.NewGuid().ToString("N");
        public string InitiatorId;      // кто просит использовать бизнес
        public string TargetPlayerId;   // владелец бизнеса
        public string BusinessUid;      // CardInstance.UniqueId бизнеса
        public int ProposedPrice;
        public int JobCashReward;       // сколько заработает инициатор, если сделка состоится
        public DateTime CreatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Тюнинг переговоров — целиком из JSON-конфига баланса (DealBalance).</summary>
    [Serializable]
    public class DealBalance
    {
        public float GreedFactorAggressive = 0.5f;   // агрессор платит меньше охотно
        public float GreedFactorEconomist = 0.8f;
        public float GreedFactorBalanced = 1.0f;
        public float RiskFactor = 0.35f;             // насколько владелец боится репутационных/тактических потерь
        public float AggressiveAcceptThreshold = 0.70f; // «цена < 70% прибыли → отказ»
        public float EconomistAcceptThreshold = 0.30f;  // «цена > 30% прибыли → принять»
        public int RoundsPerGame = 4;
    }

    public class DealSystem
    {
        private readonly GameState _state;
        private readonly DealBalance _balance;

        public DealSystem(GameState state, DealBalance balance = null)
        {
            _state = state;
            _balance = balance ?? new DealBalance();
        }

        // ---------- ИНИЦИАЦИЯ (боту нужен чужой бизнес) ----------

        public DealOffer CreateOffer(string initiatorId, string businessUid)
        {
            var initiator = _state.GetPlayer(initiatorId);
            var bizInst = _state.FindBusinessByUid(businessUid);
            var biz = bizInst?.AsBusiness;
            if (biz == null || biz.CannotBeDealt) return null;

            // Ожидаемая прибыль: доход бизнеса за оставшиеся раунды + средняя выгода дел категории
            int remainingRounds = Math.Max(1, _balance.RoundsPerGame - _state.Round + 1);
            int expectedProfit = biz.Income * remainingRounds + EstimateJobBonus(biz.Category);

            float greed = initiator.BotType switch
            {
                BotArchetype.Aggressive => _balance.GreedFactorAggressive,
                BotArchetype.Economist => _balance.GreedFactorEconomist,
                _ => _balance.GreedFactorBalanced
            };

            return new DealOffer
            {
                InitiatorId = initiatorId,
                TargetPlayerId = _state.FindOwnerOfBusiness(businessUid).Id,
                BusinessUid = businessUid,
                ProposedPrice = (int)(expectedProfit * greed / 1000) * 1000, // округление до $1000 — «человечная» цена
                JobCashReward = expectedProfit
            };
        }

        private int EstimateJobBonus(MonopolyCategory cat) => cat switch
        {
            MonopolyCategory.Drugs => 8000,
            MonopolyCategory.Prostitution => 6000,
            MonopolyCategory.Credit => 5000,
            _ => 3000
        };

        // ---------- ОЦЕНКА ВЛАДЕЛЬЦЕМ ----------

        /// <summary>Минимальная приемлемая цена для владельца бизнеса (используется и ботами, и UI-подсказкой «авто-цена»).</summary>
        public int ComputeMinAcceptablePrice(PlayerController owner, DealOffer offer)
        {
            var biz = _state.FindBusinessByUid(offer.BusinessUid)?.AsBusiness;
            if (biz == null) return int.MaxValue;

            int basePrice = biz.Price;                                   // альтернатива: продать на рынке? (цена входа)
            int potentialLoss = PotentialDealRisk(owner, biz);           // усиление врага через его дела
            return basePrice + (int)(_balance.RiskFactor * potentialLoss);
        }

        /// <summary>Насколько опасно дать именно этому просящему доступ к бизнесу:
        /// сколько денег он может заработать делами этой категории против меня.</summary>
        private int PotentialDealRisk(PlayerController owner, BusinessCard biz)
        {
            // Эвристика: доход всех дел категории у инициатора (оцениваем по открытой информации — числу гангстеров)
            int attackerPower = _state.Players.FirstOrDefault(p => p.Id == owner.Id) != null
                ? owner.AliveGangsters.Sum(g => g.Strength) : 1;
            return biz.Income * 2 + attackerPower * 1000;
        }

        /// <summary>Ответ бота-владельца на предложение. Возвращает принятый токен или null (отказ).</summary>
        public DealToken BotEvaluateAndRespond(PlayerController botOwner, DealOffer offer)
        {
            var biz = _state.FindBusinessByUid(offer.BusinessUid)?.AsBusiness;
            if (biz == null || biz.CannotBeDealt) return Reject(offer);

            int minPrice = ComputeMinAcceptablePrice(botOwner, offer);
            float profitShare = offer.JobCashReward > 0 ? (float)offer.ProposedPrice / offer.JobCashReward : 0f;

            bool accept = botOwner.BotType switch
            {
                // Агрессор: отдаёт только если цена >= 70% от прибыли просителя (жадный шантаж)
                BotArchetype.Aggressive => offer.ProposedPrice >= minPrice && profitShare >= _balance.AggressiveAcceptThreshold,
                // Экономист: рад любому потоку денег — порог низкий, но ниже себестоимости не уходит
                BotArchetype.Economist => offer.ProposedPrice >= minPrice && profitShare >= _balance.EconomistAcceptThreshold,
                // Дипломат: взаимовыгодность = честная цена + нет прямой угрозы мне
                BotArchetype.Balanced => offer.ProposedPrice >= minPrice && !IsThreateningBot(botOwner, offer.InitiatorId),
                _ => offer.ProposedPrice >= minPrice
            };

            return accept ? PlaceDealMarker(offer) : Reject(offer);
        }

        private bool IsThreateningBot(PlayerController me, string otherId)
        {
            var other = _state.GetPlayer(otherId);
            return other != null && other.AliveGangsters.Sum(g => g.Strength) >
                                  me.AliveGangsters.Sum(g => g.Strength) + 1;
        }

        // ---------- РАЗМЕЩЕНИЕ МАРКЕРА ----------

        /// <summary>Ставит DealToken на бизнес. Один токен одного владельца на один бизнес.</summary>
        public DealToken PlaceDealMarker(DealOffer offer)
        {
            var biz = _state.FindBusinessByUid(offer.BusinessUid);
            var initiator = _state.GetPlayer(offer.InitiatorId);

            var existing = biz.TokensOnIt.FirstOrDefault(t => t.OwnerId == offer.InitiatorId);
            if (existing != null) { existing.Status = DealStatus.Accepted; existing.AgreedPrice = offer.ProposedPrice; return existing; }

            var token = new DealToken
            {
                TokenId = Guid.NewGuid().ToString("N"),
                OwnerId = offer.InitiatorId,
                BusinessId = biz.UniqueId,
                TargetPlayerId = offer.TargetPlayerId,
                AgreedPrice = offer.ProposedPrice,
                Status = DealStatus.Accepted
            };
            biz.TokensOnIt.Add(token);
            initiator.DealTokens.Add(token);
            initiator.Cash -= offer.ProposedPrice;          // оплата за доступ
            _state.GetPlayer(offer.TargetPlayerId).Cash += offer.ProposedPrice;
            _state.Log.Add($"deal.accept:{offer.InitiatorId}->{offer.TargetPlayerId}:{biz.Data.Id}:${offer.ProposedPrice}");
            return token;
        }

        public DealToken Reject(DealOffer offer)
        {
            _state.Log.Add($"deal.reject:{offer.InitiatorId}:{offer.BusinessUid}");
            return null;
        }

        // ---------- РУЧНАЯ СДЕЛКА ЧЕЛОВЕКА ----------

        /// <summary>UI вызывает, когда игрок кликает на бизнес бота: показывает авто-цену.</summary>
        public int GetAutoPriceForHuman(string humanId, string businessUid)
        {
            var offer = CreateOffer(humanId, businessUid);
            if (offer == null) return -1;
            var owner = _state.GetPlayer(offer.TargetPlayerId);
            // Авто-цена = максимум из того, что предложил бы сам бот-инициатор, и минималки владельца
            return Math.Max(offer.ProposedPrice, ComputeMinAcceptablePrice(owner, offer));
        }

        /// <summary>Игрок соглашается на авто-цену → маркер ставится сразу (бот гарантированно принимает свою же цену).</summary>
        public DealToken HumanAcceptAutoDeal(string humanId, string businessUid)
        {
            int price = GetAutoPriceForHuman(humanId, businessUid);
            if (price < 0) return null;
            var human = _state.GetPlayer(humanId);
            if (human.Cash < price) return null;
            var offer = CreateOffer(humanId, businessUid);
            offer.ProposedPrice = price;
            return PlaceDealMarker(offer);
        }
    }
}
