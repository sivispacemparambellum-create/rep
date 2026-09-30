// GameEnums.cs — Единый словарь перечислений La Cosa Nostra (Digital Edition)
namespace LCN.Core
{
    /// <summary>Фазы главного цикла раунда (машина состояний GameManager).</summary>
    public enum GamePhase
    {
        Setup,          // Инициализация партии
        DrawCards,      // Раздача карт раунда (Jobs + Influence)
        Recruit,        // Наём гангстеров (активна только если включено дополнение)
        Planning,       // Выбор действий / переговоры (Deal Phase)
        Action,         // Поочерёдное выполнение назначенных действий + реакции
        Payout,         // Выплаты доходов, проверка монополий
        RoundEnd,       // Передача жетона первого игрока, очистка маркеров сделок
        GameOver        // После 4-го раунда: двойной доход + бонусы
    }

    /// <summary>Тип карты дела.</summary>
    public enum JobType { Profitable, Attack, Reaction }

    /// <summary>Раунд-колода дел (влияет на рубашку карты в UI и на безопасность данных).</summary>
    public enum JobDeckRound { RoundI, RoundII, RoundIII, RoundIV }

    /// <summary>Категории влияния для расчёта монополий.</summary>
    public enum MonopolyCategory { None, Drugs, Prostitution, Credit, Construction, Transport, Entertainment }

    /// <summary>Подтип бизнес-карты.</summary>
    public enum BusinessKind { Company, Businessman }

    /// <summary>Мгновенные/отложенные карты влияния.</summary>
    public enum InfluenceType
    {
        None,
        Henchman,       // +1 кубик гангстеру
        MachinePistol,  // +1 успех атаки / защита от убийцы
        TommyGun,       // +2 к силе гангстера
        Distraction,    // Защита от любого нападения (сбрасывается)
        Saboteur,       // -1 кубик жертве (или провал без броска при 0 кубиков)
        Snitch,         // Подглядывание: скрыто посмотреть карту дела жертвы
        Spy,            // Разведка руки/планов оппонента (BalanceBot инструмент)
        Bodyguard       // Спасает гангстера от убийства
    }

    /// <summary>Визуальное состояние карты в CardView.</summary>
    public enum CardViewState { FaceUp, FaceDown, Deactivated }

    /// <summary>Юридический статус сделки по конкретному бизнесу.</summary>
    public enum DealStatus { None, Offered, Accepted, Rejected, Expired }

    /// <summary>Вид действия, которое AIController возвращает в Planning-фазе.</summary>
    public enum BotActionType { AssignJob, BuyBusiness, PlayInfluence, OfferDeal, Skip, AttackWithGangster }

    /// <summary>Архетипы ботов.</summary>
    public enum BotArchetype { Aggressive, Economist, Balanced }

    /// <summary>Тип результата броска DiceManager (для отладки/аналитики).</summary>
    public enum RollOutcome { CriticalSuccess, Success, Failure, CriticalFailure }
}
