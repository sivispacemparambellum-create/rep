// CardData.cs — Serializable-модели карт. Загружаются из JSON (Data/card_database.json),
// поэтому баланс настраивается БЕЗ перекомпиляции кода.
using System.Collections.Generic;

namespace LCN.Cards
{
    using LCN.Core;

    /// <summary>Базовая карточная сущность. Id соответствует ключу в JSON-базе.</summary>
    [System.Serializable]
    public abstract class CardData
    {
        public string Id;
        public string Name;
        public string Description;
        public SpriteRef FrontSprite;   // ссылка на спрайт (имя/адрес в Addressables)
    }

    /// <summary>Ссылка на спрайт без прямой зависимости от UnityEngine.Object (для серверной части).</summary>
    [System.Serializable]
    public class SpriteRef
    {
        public string Key;         // например "jobs/job_014"
        public string BackKey;     // рубашка: "back_jobs_round2" и т.п. — клиент видит ТОЛЬКО это для скрытых карт
    }

    /// <summary>Карта дела (Job). 90 штук в базе, распределены по 4 раундным колодам.</summary>
    [System.Serializable]
    public class JobCard : CardData
    {
        public JobType Type;                    // Profitable / Attack / Reaction
        public JobDeckRound DeckRound;          // определяет рубашку FaceDown
        public int Difficulty = 1;              // модификатор сложности броска (-difficulty к кубикам)
        public int CashReward;                  // доход при успехе ($)
        public int DiceBonus;                   // доп. кубики за специализацию гангстера
        public bool RequiresGangsterAlive = true;

        /// <summary>Пререквизиты: список категорий бизнесов, которые должны быть у исполнителя
        /// (свои ИЛИ доступные через принятый маркер сделки).</summary>
        public List<PrerequisiteEntry> Prerequisites = new();

        /// <summary>Эффект атаки: сколько успехов нужно, чтобы убить/ранить цель.</summary>
        public AttackEffect Attack;

        /// <summary>Реакция (Trap/Robbery): срабатывает в чужом действии.</summary>
        public ReactionEffect Reaction;

        public float Value => CashReward > 0 ? CashReward : 5000f; // для оценки ботами
    }

    [System.Serializable]
    public class PrerequisiteEntry
    {
        public MonopolyCategory Category;   // требуемая категория бизнеса
        public int Count = 1;               // сколько штук нужно
        public bool DealAllowed = true;     // можно ли закрыть сделкой (Legal/Politician = false!)
        public string SpecificBusinessId;   // опционально: конкретный бизнес вместо категории
    }

    [System.Serializable]
    public class AttackEffect
    {
        public int SuccessesToKill = 1;       // успехов для убийства гангстера цели
        public int CashStealOnSuccess;        // грабёж: сколько денег забирается у цели
        public bool DeactivateTargetBusiness; // trap: цель теряет бизнес
        public string TargetBusinessCategory; // какая именно бизнес-линия атакуется
    }

    [System.Serializable]
    public class ReactionEffect
    {
        public int TriggerWindowPriority = 0; // выше = раньше получает окно реакции
        public int CashPenaltyToActive;       // штраф активному игроку
        public bool CancelAction;             // полностью отменяет действие
        public int CounterAttackDamage;       // ответный урон
    }

    /// <summary>Карта влияния (35 в базе). Модифицирует гангстеров/бизнесы или мгновенна.</summary>
    [System.Serializable]
    public class InfluenceCard : CardData
    {
        public InfluenceType Effect;
        public int ModifierValue;             // напр. +1 кубик (Henchman) или -1 (Saboteur)
        public bool AttachToGangster;         // остаётся на гангстере (видима в Modifier Layer)
        public bool AttachToBusiness;         // напр. MachinePistol на бизнесе
        public bool IsInstant;                // мгновенный эффект (Snitch/Spy) — сбрасывается сразу
        public int OneTimeCash;               // денежный эффект (если есть)
    }

    /// <summary>Карта бизнеса (34 в базе): компания или бизнесмен.</summary>
    [System.Serializable]
    public class BusinessCard : CardData
    {
        public BusinessKind Kind;
        public MonopolyCategory Category;     // Drugs / Prostitution / Credit / ...
        public int Income;                    // ежегодный доход ($ за раунд)
        public int Price;                     // цена покупки на рынке ($)
        public int MaxDealIncome;             // сколько раз/максимум дохода может дать через сделки
        public bool CannotBeDealt;            // Юрист, Политик: особые навыки НЕ подлежат сделкам
        public bool BlocksMonopolyCount;      // считается ли для монополии (бизнесмены — да)
    }

    /// <summary>Карта гангстера. Strength = количество кубиков базовой силы.</summary>
    [System.Serializable]
    public class GangsterCard : CardData
    {
        public int Strength;                  // базовое число кубиков (обычно 1..3)
        public string Specialization;         // "Thug", "Hitman", "Driver" — даёт DiceBonus своим делам
        public int RecruitCost;               // стоимость найма (дополнение)
        public bool IsAlive = true;           // ранен/убит меняет только ранг, карта не уходит из игры
    }

    /// <summary>Маркер сделки: лежит НА чужом бизнесе, принадлежит OwnerId.</summary>
    [System.Serializable]
    public class DealToken
    {
        public string TokenId;
        public string OwnerId;          // кто поставил маркер (инициатор сделки)
        public string BusinessId;       // на каком бизнесе лежит
        public string TargetPlayerId;   // владелец бизнеса
        public int AgreedPrice;         // зафиксированная авто-цена бота / договорённая цена игрока
        public DealStatus Status;
    }
}
