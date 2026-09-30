// GameManager.cs — Машина состояний раунда + передача хода + ExecuteJob + реакции.
// Unity-обёртка (MonoBehaviour) минимальна: вся логика статично-вызываема из headless-тестов.
using System;
using System.Collections.Generic;
using System.Linq;
using LCN.Cards;

namespace LCN.Core
{
    /// <summary>События для UI/AI-подписки (C# events, не UnityEvents — чтобы модель была переносимой).
    /// Методы-обёртки Raise* позволяют поднимать события извне GameEvents.</summary>
    public static class GameEvents
    {
        public static event Action<GamePhase> PhaseChanged;
        public static event Action<CardInstance> JobRevealed;                 // старт анимации переворота
        public static event Action<RollResult> DiceRolled;
        public static event Action<PlayerController, string> ReactionWindowOpened; // цель + описание угрозы
        public static event Action<string> LogMessage;

        public static void RaisePhaseChanged(GamePhase p) => PhaseChanged?.Invoke(p);
        public static void RaiseJobRevealed(CardInstance c) => JobRevealed?.Invoke(c);
        public static void RaiseDiceRolled(RollResult r) => DiceRolled?.Invoke(r);
        public static void RaiseReactionWindow(PlayerController victim, string threat) => ReactionWindowOpened?.Invoke(victim, threat);
        public static void RaiseLog(string msg) => LogMessage?.Invoke(msg);
    }

    public class GameManager
    {
        public GameState State { get; private set; }
        public DiceManager Dice { get; private set; }
        public MarketManager Market { get; private set; }

        private Dictionary<JobDeckRound, Deck<JobCard>> _jobDecks;
        private Deck<InfluenceCard> _influenceDeck;

        // Очередь действий текущей фазы Action: (владелец действия, само действие)
        private readonly Queue<(PlayerController Owner, AssignedAction Action)> _actionQueue = new();
        // Окно реакции: активное действие приостановлено, ждём решения владельцев реакций
        public AssignedAction PendingReactionAction { get; private set; }

        public bool RecruitExpansionEnabled { get; set; } = true;

        public void NewGame(List<PlayerController> players, int? diceSeed = null, int gameSeed = 0)
        {
            Dice = new DiceManager(diceSeed ?? gameSeed + 99);
            State = GameState.Current = new GameState
            {
                Players = players,
                Round = 1,
                ExpansionRecruitEnabled = RecruitExpansionEnabled,
                BusinessDeck = new Deck<BusinessCard>(CardDatabase.AllBusinesses, gameSeed + 2),
                GangsterDeck = new Deck<GangsterCard>(CardDatabase.AllGangsters, gameSeed + 3)
            };
            _jobDecks = new()
            {
                [JobDeckRound.RoundI]   = new(CardDatabase.AllJobs.Where(j => j.DeckRound == JobDeckRound.RoundI),   gameSeed + 10),
                [JobDeckRound.RoundII]  = new(CardDatabase.AllJobs.Where(j => j.DeckRound == JobDeckRound.RoundII),  gameSeed + 11),
                [JobDeckRound.RoundIII] = new(CardDatabase.AllJobs.Where(j => j.DeckRound == JobDeckRound.RoundIII), gameSeed + 12),
                [JobDeckRound.RoundIV]  = new(CardDatabase.AllJobs.Where(j => j.DeckRound == JobDeckRound.RoundIV),  gameSeed + 13),
            };
            _influenceDeck = new Deck<InfluenceCard>(CardDatabase.AllInfluences, gameSeed + 4);

            Market = new MarketManager(State);
            Market.InitialFill();

            // Стартовые гангстеры: по одному каждому (баланс правится в JSON-конфиге найма)
            foreach (var p in players)
            {
                var g = State.GangsterDeck.Draw();
                if (g != null) { g.State = CardViewState.FaceUp; p.Gangsters.Add(g); }
            }

            State.FirstPlayerId = players[new Random(gameSeed).Next(players.Count)].Id;
            Transition(GamePhase.DrawCards);
        }

        /// <summary>Ядро машины состояний. Возвращает false, если переход запрещён.</summary>
        public bool Transition(GamePhase next)
        {
            var allowed = State.Phase switch
            {
                GamePhase.Setup       => next == GamePhase.DrawCards,
                GamePhase.DrawCards   => next == (RecruitExpansionEnabled ? GamePhase.Recruit : GamePhase.Planning),
                GamePhase.Recruit     => next == GamePhase.Planning,
                GamePhase.Planning    => next == GamePhase.Action,
                GamePhase.Action      => next == GamePhase.Payout,
                GamePhase.Payout      => next == GamePhase.RoundEnd,
                GamePhase.RoundEnd    => next is GamePhase.DrawCards or GamePhase.GameOver,
                GamePhase.GameOver    => false
            };
            if (!allowed) return false;

            State.Phase = next;
            GameEvents.RaisePhaseChanged(next);
            OnEnterPhase(next);
            return true;
        }

        private void OnEnterPhase(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.DrawCards:
                    RoundDealer.DealRound(State, _jobDecks, _influenceDeck);
                    break;

                case GamePhase.Recruit:
                    // Найм: каждый игрок может купить гангстера с рынка за RecruitCost.
                    // Решение принимает AIController/человек через RecruitGangster().
                    break;

                case GamePhase.Planning:
                    foreach (var p in State.Players) p.PlannedActions.Clear();
                    break;

                case GamePhase.Action:
                    _actionQueue.Clear();
                    foreach (var p in State.InTurnOrder())
                        foreach (var a in p.PlannedActions) _actionQueue.Enqueue((p, a));
                    RunNextAction();
                    break;

                case GamePhase.Payout:
                    RunPayout();
                    break;

                case GamePhase.RoundEnd:
                    EndRound();
                    break;
            }
        }

        // ================= ACTION PHASE =================

        private void RunNextAction()
        {
            if (_actionQueue.Count == 0) { Transition(GamePhase.Payout); return; }
            var (owner, action) = _actionQueue.Dequeue();
            ExecuteJob(owner, action);
        }

        public string CurrentActorId { get; private set; }

        /// <summary>Полный пайплайн выполнения дела: reveal → бросок → эффекты → реакции.</summary>
        public JobExecutionResult ExecuteJob(PlayerController player, AssignedAction action)
        {
            var result = new JobExecutionResult { Player = player, Action = action };
            if (player == null || action.Job == null) return result;

            CurrentActorId = player.Id;
            var job = action.Job.AsJob;

            // 1) Реакции оппонентов ДО раскрытия (Trap/Robbery из руки).
            //    Клиент оппонентов получает только "угроза идёт", но не id карты дела.
            var reactions = CollectReactions(player, job);
            foreach (var r in reactions)
            {
                if (r.CancelAction)
                {
                    result.ReactedOut = true;
                    player.Cash -= r.CashPenaltyToActive;
                    action.Job.State = CardViewState.FaceDown; // карта уходит рубашкой вверх в сброс
                    return result;
                }
            }

            // 2) Раскрытие карты (UI запускает анимацию flip по событию)
            action.Job.State = CardViewState.FaceUp;
            GameEvents.RaiseJobRevealed(action.Job);

            // 3) Проверка пререквизитов. Без них дело считается проваленным автоматически
            //    (или это блеф — наказание: гангстер ранен при атаке без условий в оригинале упрощённо).
            if (!State.CheckPrerequisites(job, player))
            {
                result.PrereqFailed = true;
                return result;
            }

            // 4) Бросок кубиков
            var gangsterInst = player.Gangsters.FirstOrDefault(g => g.UniqueId == action.GangsterInstanceId);
            var gc = gangsterInst?.Data as GangsterCard;
            int strength = gc != null ? PlayerController.EffectiveStrength(gc, gangsterInst) : 0;
            strength += job.DiceBonus;
            var roll = Dice.PerformRoll(strength, job.Difficulty);
            GameEvents.RaiseDiceRolled(roll);
            result.Roll = roll;

            // 5) Эффекты
            if (roll.JobSucceeded)
            {
                if (job.Type == JobType.Profitable)
                {
                    player.Cash += job.CashReward;
                    result.CashGained = job.CashReward;
                }
                else if (job.Type == JobType.Attack && job.Attack != null)
                {
                    ApplyAttackEffect(player, job, action, roll.Successes, result);
                }
            }
            else if (job.Type == JobType.Attack)
            {
                // Провал атаки: штраф — гангстер исполнителя может быть ранен (rule variant, конфиг)
            }

            // 6) Карта дела сбрасывается рубашкой вверх
            action.Job.State = CardViewState.FaceDown;
            player.Hand.Jobs.Remove(action.Job);
            return result;
        }

        private List<ReactionEffect> CollectReactions(PlayerController actor, JobCard job)
        {
            var list = new List<ReactionEffect>();
            foreach (var foe in State.Players.Where(p => p.Id != actor.Id))
            {
                foreach (var jc in foe.Hand.Jobs.Select(c => c.AsJob).Where(j => j != null && j.Type == JobType.Reaction && j.Reaction != null))
                {
                    bool wantsPlay = foe.IsHuman
                        ? AskHumanReaction(foe, actor, job)          // UI-колбэк (в headless = false)
                        : BotWantsReaction(foe, actor, job, jc);      // эвристика AI
                    if (wantsPlay)
                    {
                        foe.Hand.Jobs.RemoveAll(c => c.Data.Id == jc.Id); // сброс одной карты реакции
                        list.Add(jc.Reaction);
                    }
                }
            }
            return list.OrderByDescending(r => r.TriggerWindowPriority).ToList();
        }

        public Func<PlayerController, PlayerController, JobCard, bool> AskHumanReaction;
        public Func<PlayerController, PlayerController, JobCard, JobCard, bool> BotWantsReaction;

        private void ApplyAttackEffect(PlayerController attacker, JobCard job, AssignedAction action,
                                       int successes, JobExecutionResult result)
        {
            var target = State.GetPlayer(action.TargetPlayerId);
            if (target == null || job.Attack == null) return;

            if (successes >= job.Attack.SuccessesToKill)
            {
                if (job.Attack.CashStealOnSuccess > 0)
                {
                    int steal = Math.Min(job.Attack.CashStealOnSuccess, target.Cash);
                    target.Cash -= steal; attacker.Cash += steal;
                    result.CashStolen = steal;
                }
                if (job.Attack.DeactivateTargetBusiness && action.TargetBusinessUid != null)
                {
                    var biz = State.FindBusinessByUid(action.TargetBusinessUid);
                    if (biz != null)
                    {
                        biz.IsDeactivated = true;               // серый фильтр в Deactivated-состоянии
                        // все маркеры сделок на этом бизнесе сгорают
                        foreach (var tok in biz.TokensOnIt) tok.Status = DealStatus.Expired;
                        biz.TokensOnIt.Clear();
                    }
                }
                // Убийство гангстера цели: первый живой уходит в «ранен» (IsAlive=false, остаётся на столе)
                var victimG = target.Gangsters.FirstOrDefault(g => (g.Data as GangsterCard)?.IsAlive == true);
                if (victimG != null) ((GangsterCard)victimG.Data).IsAlive = false;
                result.GangsterKilled = victimG;
            }
        }

        // ================= PAYOUT / MONOPOLIES =================

        public void RunPayout()
        {
            foreach (var p in State.Players)
            {
                int income = p.TotalIncome();
                p.Cash += income;
                State.Log.Add($"payout:{p.Id}:{income}");
            }
            CheckMonopolies();
        }

        /// <summary>Монополия: >=2 бизнесмена категории И больше, чем у всех остальных вместе взятых → +$5000.</summary>
        public void CheckMonopolies(int bonus = 5000)
        {
            var categories = Enum.GetValues(typeof(MonopolyCategory)).Cast<MonopolyCategory>()
                                .Where(c => c != MonopolyCategory.None);
            foreach (var cat in categories)
            {
                var counts = State.Players.ToDictionary(
                    p => p,
                    p => p.ActiveBusinesses.Count(b => b.AsBusiness != null
                                                    && b.AsBusiness.Kind == BusinessKind.Businessman
                                                    && b.AsBusiness.Category == cat));
                var top = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
                if (top.Key == null || top.Value < 2) continue;

                int othersSum = counts.Where(kv => kv.Key != top.Key).Sum(kv => kv.Value);
                if (top.Value > othersSum)
                {
                    top.Key.Cash += bonus;
                    State.Log.Add($"monopoly:{cat}:{top.Key.Id}:+{bonus}");
                }
            }
        }

        // ================= ROUND END / GAME OVER =================

        private void EndRound()
        {
            // Маркеры сделок живут до конца раунда и снимаются
            foreach (var p in State.Players)
            {
                foreach (var t in p.DealTokens)
                {
                    var biz = State.FindBusinessByUid(t.BusinessId);
                    biz?.TokensOnIt.RemoveAll(x => x.TokenId == t.TokenId);
                }
                p.DealTokens.Clear();
            }
            // Сброс неиспользованных рук (классика: рука не переносится между раундами)
            foreach (var p in State.Players) { p.Hand.Jobs.Clear(); p.Hand.Influences.Clear(); }

            // Передача жетона первого игрока по кругу
            int idx = State.Players.FindIndex(p => p.Id == State.FirstPlayerId);
            State.FirstPlayerId = State.Players[(idx + 1) % State.Players.Count].Id;

            if (State.Round >= 4) FinalScoring();
            else { State.Round++; Transition(GamePhase.DrawCards); }
        }

        /// <summary>Финальный подсчёт: доход ×2, бонус сильнейшему $15k, бонус за убитых врагов.</summary>
        public void FinalScoring(int strongestBonus = 15000, int killMultiplier = 5000)
        {
            // 1) Двойной доход последнего раунда уже начислен в RunPayout — добавляем ещё один доход
            foreach (var p in State.Players) p.Cash += p.TotalIncome();

            // 2) Бонус самому сильному (живые гангстеры + модификаторы)
            var strongest = State.Players.OrderByDescending(p =>
                p.Gangsters.Sum(g => g.IsDeactivated ? 0 : PlayerController.EffectiveStrength((GangsterCard)g.Data, g))
            ).First();
            strongest.Cash += strongestBonus;

            // 3) Бонус силы за УБИТЫХ гангстеров противников
            foreach (var p in State.Players)
            {
                int deadStrength = State.Players.Where(o => o.Id != p.Id)
                    .SelectMany(o => o.Gangsters)
                    .Where(g => (g.Data as GangsterCard)?.IsAlive == false)
                    .Sum(g => ((GangsterCard)g.Data).Strength);
                p.Cash += deadStrength * killMultiplier; // множитель из JSON-конфига баланса
            }

            Transition(GamePhase.GameOver);
        }

        public IEnumerable<PlayerController> Leaderboard => State.Players.OrderByDescending(p => p.Cash);
    }

    public class JobExecutionResult
    {
        public PlayerController Player;
        public AssignedAction Action;
        public RollResult Roll;
        public bool PrereqFailed;
        public bool ReactedOut;
        public int CashGained;
        public int CashStolen;
        public CardInstance GangsterKilled;
    }
}
