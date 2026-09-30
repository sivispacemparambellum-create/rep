// GameState.cs — Полное состояние партии. Чистая модель: не знает ни о Unity, ни о AI.
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;

namespace LCN.Core
{
    /// <summary>Рука игрока (закрытая информация).</summary>
    public class HandCollection
    {
        public List<CardInstance> Jobs = new();
        public List<CardInstance> Influences = new();
    }

    /// <summary>Назначенное на планирование действие гангстера.</summary>
    public class AssignedAction
    {
        public string GangsterInstanceId;
        public CardInstance Job;              // null => атака "вслепую" назначением силы
        public string TargetPlayerId;         // для атак/грабежей
        public string TargetBusinessUid;      // для trap/саботажа бизнеса
        public bool IsBluff;                  // бот назначил дело без условий ради блефа
    }

    /// <summary>Базовый PlayerController: общая модель для человека и бота.</summary>
    public class PlayerController
    {
        public string Id;
        public string DisplayName;
        public bool IsHuman;
        public BotArchetype? BotType;         // null для человека

        // === Зоны игрока ===
        public int Cash = 20000;              // стартовый капитал (баланс в JSON-конфиге)
        public HandCollection Hand = new();
        public List<CardInstance> Gangsters = new();   // зона гангстеров
        public List<CardInstance> Businesses = new();  // зона бизнесов
        public List<DealToken> DealTokens = new();     // мои маркеры сделок (лежат на чужих бизнесах)
        public List<AssignedAction> PlannedActions = new();
        public int Rank = 1;                  // ранг: 3=босс, 2=сокольник... убитые снижают ранг

        // === Производные величины ===
        public IEnumerable<CardInstance> ActiveBusinesses => Businesses.Where(b => !b.IsDeactivated);
        public IEnumerable<GangsterCard> AliveGangsters =>
            Gangsters.Select(g => g.Data as GangsterCard).Where(g => g != null && g.IsAlive);

        public int TotalIncome()
        {
            int income = ActiveBusinesses.Sum(b => b.AsBusiness?.Income ?? 0);
            // доход через принятые сделки: чужие бизнесы, где мой токен Accepted
            foreach (var t in DealTokens.Where(t => t.Status == DealStatus.Accepted))
            {
                var biz = FindBusinessByUid(t.BusinessId);
                if (biz != null && !biz.IsDeactivated) income += biz.AsBusiness.Income;
            }
            return income;
        }

        private CardInstance FindBusinessByUid(string uid)
        {
            // поиск по всем игрокам выполняется через GameState — см. extension ниже
            return GameStateExtensions.FindBusinessByUid(GameState.Current, uid);
        }

        /// <summary>Суммарная сила кубиков гангстера с учётом модификаторов влияния.</summary>
        public static int EffectiveStrength(GangsterCard g, CardInstance instance)
        {
            int s = g.Strength;
            if (instance != null)
                foreach (var inf in instance.AttachedInfluences)
                    if (inf.Effect == InfluenceType.Henchman || inf.Effect == InfluenceType.TommyGun)
                        s += inf.ModifierValue;
            return Mathf.Max(0, s);
        }
    }

    /// <summary>Верхний уровень состояния.</summary>
    public class GameState
    {
        public static GameState Current;      // активная партия (для headless/UI-привязки)

        public int Round = 1;                 // 1..4
        public GamePhase Phase = GamePhase.Setup;
        public List<PlayerController> Players = new();
        public string FirstPlayerId;          // жетон первого игрока ходит по кругу
        public bool ExpansionRecruitEnabled;  // дополнение: фаза Recruit

        // Рынок (MarketManager держит ссылки сюда)
        public List<CardInstance> MarketSlots = new();      // ровно 4 слота
        public Deck<BusinessCard> BusinessDeck;
        public Deck<GangsterCard> GangsterDeck;

        // Журнал для UI-реплея и античит-верификации
        public List<string> Log = new();

        public PlayerController FirstPlayer => Players.First(p => p.Id == FirstPlayerId);
        public IEnumerable<PlayerController> InTurnOrder()
        {
            int start = Players.FindIndex(p => p.Id == FirstPlayerId);
            for (int i = 0; i < Players.Count; i++) yield return Players[(start + i) % Players.Count];
        }
        public PlayerController GetPlayer(string id) => Players.FirstOrDefault(p => p.Id == id);
    }

    public static class GameStateExtensions
    {
        public static CardInstance FindBusinessByUid(this GameState gs, string uid) =>
            gs.Players.SelectMany(p => p.Businesses).FirstOrDefault(b => b.UniqueId == uid);

        public static PlayerController FindOwnerOfBusiness(this GameState gs, string uid) =>
            gs.Players.FirstOrDefault(p => p.Businesses.Any(b => b.UniqueId == uid));

        /// <summary>Проверка пререквизитов дела: свои бизнесы ИЛИ принятые маркеры сделок.
        /// Особые навыки (CannotBeDealt: Юрист/Политик) сделкой НЕ закрываются.</summary>
        public static bool CheckPrerequisites(this GameState gs, JobCard job, PlayerController player)
        {
            if (job.Prerequisites == null || job.Prerequisites.Count == 0) return true;

            foreach (var req in job.Prerequisites)
            {
                int available = 0;

                // 1) Свои активные бизнесы нужной категории / конкретного id
                available += player.ActiveBusinesses.Count(b =>
                    req.SpecificBusinessId != null
                        ? b.Data.Id == req.SpecificBusinessId
                        : b.AsBusiness != null && b.AsBusiness.Category == req.Category && !b.IsDeactivated);

                // 2) Принятые сделки на чужих бизнесах
                var acceptedTokens = player.DealTokens.Where(t => t.Status == DealStatus.Accepted);
                foreach (var t in acceptedTokens)
                {
                    var biz = gs.FindBusinessByUid(t.BusinessId);
                    if (biz == null || biz.IsDeactivated) continue;
                    if (biz.AsBusiness != null && biz.AsBusiness.CannotBeDealt) continue; // защита special skills
                    bool match = req.SpecificBusinessId != null
                        ? biz.Data.Id == req.SpecificBusinessId
                        : biz.AsBusiness.Category == req.Category;
                    if (match) available++;
                }

                if (available < req.Count) return false;
            }
            return true;
        }
    }
}

// Минимальный Mathf-шим для headless-сборки (в Unity используется UnityEngine.Mathf).
#if !UNITY_2021_3_OR_NEWER
namespace LCN.Core { internal static class Mathf { public static int Max(int a, int b) => a > b ? a : b; } }
#else
namespace LCN.Core
{
    internal static class Mathf
    {
        public static int Max(int a, int b) => UnityEngine.Mathf.Max(a, b);
    }
}
#endif
