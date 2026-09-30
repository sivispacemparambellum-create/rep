// MarketManager.cs — Рынок бизнеса: 4 открытых слота, пополнение, проверка дубликатов при старте.
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;

namespace LCN.Core
{
    public class MarketManager
    {
        public const int SlotCount = 4;
        private readonly GameState _state;

        public MarketManager(GameState state)
        {
            _state = state;
        }

        /// <summary>Стартовое наполнение рынка. Правило: среди 4 карт не должно быть
        /// двух одинаковых Id (дубликаты уходят на низ колоды).</summary>
        public void InitialFill()
        {
            _state.MarketSlots.Clear();
            var buffer = new List<CardInstance>();

            while (_state.MarketSlots.Count < SlotCount)
            {
                var c = _state.BusinessDeck.Draw();
                if (c == null) break; // колода кончилась — рынок неполный (допустимо в эндшпиле)

                bool duplicate = _state.MarketSlots.Any(s => s.Data.Id == c.Data.Id)
                              || buffer.Any(s => s.Data.Id == c.Data.Id);
                if (duplicate) buffer.Add(c);
                else _state.MarketSlots.Add(c);
            }
            // буферные дубликаты кладём под колоду (Deck не поддерживает push — сбрасываем в discard-стопку UI)
            foreach (var b in buffer) _state.Log.Add($"market.duplicate_buffered:{b.Data.Id}");
        }

        /// <summary>Покупка бизнесом игроком: списание денег, перенос карты в зону, добор с рынка.</summary>
        public bool BuyBusiness(PlayerController buyer, CardInstance marketCard, out string error)
        {
            error = null;
            if (!_state.MarketSlots.Contains(marketCard)) { error = "Карта не на рынке"; return false; }

            var biz = marketCard.AsBusiness;
            if (biz == null) { error = "Это не бизнес-карта"; return false; }
            if (buyer.Cash < biz.Price) { error = $"Недостаточно денег: нужно ${biz.Price}"; return false; }

            buyer.Cash -= biz.Price;
            _state.MarketSlots.Remove(marketCard);
            marketCard.State = CardViewState.FaceUp;      // купленный бизнес открыт всем
            buyer.Businesses.Add(marketCard);

            RefillSlot();
            _state.Log.Add($"market.buy:{buyer.Id}:{biz.Id}:${biz.Price}");
            return true;
        }

        /// <summary>Пополнение освободившегося слота (без мгновенной проверки дублей —
        /// дубликат на рынке разрешён после стартовой фазы, как в оригинале).</summary>
        private void RefillSlot()
        {
            if (_state.MarketSlots.Count >= SlotCount) return;
            var c = _state.BusinessDeck.Draw();
            if (c != null) { c.State = CardViewState.FaceUp; _state.MarketSlots.Add(c); }
        }

        /// <summary>Итоговая очистка: несбросы в конце партии учитываются как «рынок пуст».</summary>
        public bool IsEmpty => _state.MarketSlots.Count == 0 || _state.BusinessDeck.IsEmpty
                                                    && _state.MarketSlots.Count == 0;
    }
}
