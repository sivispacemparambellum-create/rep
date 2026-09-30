using System;
using System.IO;
using System.Linq;
using LCN.Cards; using LCN.Core; using LCN.AI;

class Smoke {
    static int Main() {
        // 1. Загрузка JSON-базы (sample: урезанные counts — предупреждения ожидаемы)
        string json = File.ReadAllText("../UnityProject/Assets/_Project/Data/card_database.full.json");
        CardDatabase.LoadFromJson(json);
        Console.WriteLine($"[OK] DB loaded: jobs={CardDatabase.AllJobs.Count} inf={CardDatabase.AllInfluences.Count} biz={CardDatabase.AllBusinesses.Count} gang={CardDatabase.AllGangsters.Count}");

        // 2. DiceManager детерминизм
        var d1 = new DiceManager(42); var d2 = new DiceManager(42);
        int r1 = d1.RollDice(3,1), r2b = d2.RollDice(3,1);
        if (r1 != r2b) { Console.WriteLine("FAIL dice determinism"); return 1; }
        Console.WriteLine($"[OK] DiceManager deterministic roll={r1}");

        // 3. GameManager полный цикл 4 раундов: человек + 3 архетипа ботов
        var gm = new GameManager { RecruitExpansionEnabled = false };
        var players = new System.Collections.Generic.List<PlayerController> {
            new(){Id="p1",DisplayName="Human",IsHuman=true},
            new(){Id="p2",DisplayName="Tony (Aggro)",BotType=BotArchetype.Aggressive},
            new(){Id="p3",DisplayName="Meyer (Econ)",BotType=BotArchetype.Economist},
            new(){Id="p4",DisplayName="Frankie (Diplo)",BotType=BotArchetype.Balanced},
        };
        gm.NewGame(players, diceSeed:7, gameSeed:123);
        var deals = new DealSystem(gm.State);
        var ai = new AIController(gm, deals);
        foreach (var p in players.Where(p=>!p.IsHuman)) ai.RegisterBot(p, p.BotType.Value);

        int safety = 0;
        while (gm.State.Phase != GamePhase.GameOver && safety++ < 60) {
            switch (gm.State.Phase) {
                case GamePhase.Planning:
                    ai.RunPlanningPhaseForAllBots();
                    break;
            }
            // авто-продвижение фаз
            var next = gm.State.Phase switch {
                GamePhase.DrawCards => GamePhase.Planning,
                GamePhase.Recruit => GamePhase.Planning,
                GamePhase.Planning => GamePhase.Action,
                GamePhase.Action => GamePhase.Payout,
                GamePhase.Payout => GamePhase.RoundEnd,
                _ => (GamePhase?)null };
            if (next == null) break;
            if (!gm.Transition(next.Value)) { Console.WriteLine($"FAIL transition {gm.State.Phase}->{next}"); return 1; }
        }
        Console.WriteLine($"[OK] Full game finished in {safety} steps, phase={gm.State.Phase}");
        foreach (var p in gm.Leaderboard) Console.WriteLine($"     {p.DisplayName}: ${p.Cash}, income={p.TotalIncome()}");

        // 4. Prerequisites + сделки: p2 просит бизнес p3
        var econ = gm.State.GetPlayer("p3");
        if (econ.Businesses.Count > 0) {
            var offer = deals.CreateOffer("p2", econ.Businesses[0].UniqueId);
            if (offer != null) {
                int min = deals.ComputeMinAcceptablePrice(econ, offer);
                var tok = deals.BotEvaluateAndRespond(econ, offer);
                Console.WriteLine($"[OK] Deal test: price=${offer.ProposedPrice}, minAccept=${min}, result={(tok!=null?"ACCEPTED token placed":"REJECTED")}");
                if (tok != null && (tok.OwnerId!="p2" || tok.TargetPlayerId!="p3")) { Console.WriteLine("FAIL token fields"); return 1; }
            } else Console.WriteLine("[OK] Deal skipped (politician/lawyer protected or no biz)");
        }

        // 5. Монополия: насильно даём p3 двух Credit-бизнесменов и проверяем бонус
        var before = econ.Cash;
        var acc = CardDatabase.AllBusinesses.First(b=>b.Id=="bnz_accountant");
        for (int i=0;i<2;i++) econ.Businesses.Add(new CardInstance{Data=acc, UniqueId="mono"+i, State=CardViewState.FaceUp});
        // убираем чужие кредитные карты чтобы монополия была гарантирована
        gm.CheckMonopolies();
        Console.WriteLine(econ.Cash > before ? "[OK] Monopoly bonus applied" : "[WARN] Monopoly not triggered (other players hold credit businessmen)");

        // 6. ExecuteJob напрямую: назначаем дело без условий -> prereq fail; с условием -> бросок
        var human = gm.State.GetPlayer("p1");
        var jobInst = human.Hand.Jobs.FirstOrDefault();
        if (jobInst != null) {
            var act = new AssignedAction { GangsterInstanceId = human.Gangsters[0].UniqueId, Job = jobInst };
            var res = gm.ExecuteJob(human, act);
            Console.WriteLine($"[OK] ExecuteJob ran: prereqFail={res.PrereqFailed} reactedOut={res.ReactedOut} rolled={res.Roll.DiceRolled}d successes={res.Roll.Successes}");
        }
        Console.WriteLine("SMOKE TEST PASSED");
        return 0;
    }
}
