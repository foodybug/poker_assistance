using System;
using System.Collections.Generic;
using PokerAssistant.Core;

namespace PokerAssistant.Engine;

public enum ActionType
{
    Fold,
    Check,
    Call,
    RaiseSmall,  // 30% Pot / 쿼터
    RaiseMedium, // 50% Pot / 하프
    RaiseLarge,  // 75%~100% Pot / 풀, 따당
    AllIn        // 올인
}

public enum Position
{
    UTG,
    MP,
    CO,
    BTN,
    SB,
    BB
}

public record StrategyAdvice(
    ActionType RecommendedAction,
    string ActionNameKr,
    string ActionColorHex,
    double PotOdds,
    double Equity,
    double ExpectedValue,
    string SummaryReason,
    string TacticalDetails,
    string BetSizingRecommendation
);

public static class GtoStrategyEngine
{
    public static StrategyAdvice EvaluateAction(
        IReadOnlyList<Card> heroCards,
        IReadOnlyList<Card> boardCards,
        SimulationResult sim,
        double potSize = 1000,
        double callAmount = 200,
        Position position = Position.BTN,
        double heroStack = 5000)
    {
        double potOdds = 0;
        if (callAmount > 0 && potSize + callAmount > 0)
        {
            potOdds = Math.Round((callAmount / (potSize + callAmount)) * 100.0, 1);
        }

        double totalPotIfCall = potSize + callAmount * 2;
        int evDecimals = potSize < 1000 ? 1 : 0;
        double ev = Math.Round((sim.Equity / 100.0 * totalPotIfCall) - callAmount, evDecimals);

        // Preflop logic
        if (boardCards.Count == 0)
        {
            return EvaluatePreflop(heroCards, position, potSize, callAmount, sim.Equity, potOdds, ev);
        }

        // Postflop logic
        return EvaluatePostflop(heroCards, boardCards, sim, potSize, callAmount, heroStack, potOdds, ev);
    }

    private static StrategyAdvice EvaluatePreflop(
        IReadOnlyList<Card> heroCards,
        Position position,
        double potSize,
        double callAmount,
        double equity,
        double potOdds,
        double ev)
    {
        if (heroCards.Count < 2)
        {
            return new StrategyAdvice(
                ActionType.Fold, "다이 (FOLD)", "#E53E3E", potOdds, equity, ev,
                "카드 미인식", "핸드를 감지하지 못했습니다.", "0"
            );
        }

        var c1 = heroCards[0];
        var c2 = heroCards[1];
        int high = Math.Max((int)c1.Rank, (int)c2.Rank);
        int low = Math.Min((int)c1.Rank, (int)c2.Rank);
        bool isPair = c1.Rank == c2.Rank;
        bool isSuited = c1.Suit == c2.Suit;

        // Tier 1: Monsters (AA, KK, QQ, AKs)
        if (isPair && high >= 12 || (high == 14 && low == 13 && isSuited))
        {
            return new StrategyAdvice(
                ActionType.RaiseLarge, "강력 레이즈 / 3-Bet", "#805AD5", potOdds, equity, ev,
                "최상위 프리미엄 핸드 (Tier 1)",
                $"프리미엄 핸드로 어떤 포지션에서도 레이즈 및 3-베팅 가치가 매우 높습니다. 팟을 선점하고 밸류를 구축하세요.",
                "팟의 60%~100% (하프/풀 또는 3-Bet)"
            );
        }

        // Tier 2: Strong hands (JJ, TT, 99, AQs, AJs, KQs, AKo, AQo)
        if ((isPair && high >= 9) || 
            (high == 14 && low >= 11 && isSuited) || 
            (high == 13 && low == 12 && isSuited) ||
            (high == 14 && low >= 12 && !isSuited))
        {
            if (callAmount > potSize * 0.8) // Facing huge 3-bet
            {
                return new StrategyAdvice(
                    ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, equity, ev,
                    "강한 핸드 vs 상대 거대 베팅",
                    "상대의 강한 공격을 상대로 적정 팟 오즈가 성립하므로 플랍을 확인합니다.",
                    "콜"
                );
            }
            return new StrategyAdvice(
                ActionType.RaiseMedium, "오픈 레이즈 (RAISE)", "#805AD5", potOdds, equity, ev,
                "상위 프리플랍 레인지 (Tier 2)",
                $"{position} 포지션에서 주도권을 잡기 위해 표준 오픈 레이즈(하프)를 권장합니다.",
                "2.5BB ~ 3BB (하프 레이즈)"
            );
        }

        // Tier 3: Mid Pairs (88-66), Suited Broadways (KJs, QJs, JTs), Suited Aces (A2s-A9s)
        if ((isPair && high >= 6) || 
            (isSuited && high >= 11 && low >= 10) || 
            (isSuited && high == 14))
        {
            if (callAmount == 0 || callAmount <= potSize * 0.25)
            {
                var action = (position == Position.BTN || position == Position.CO) ? ActionType.RaiseSmall : ActionType.Call;
                string actionName = action == ActionType.RaiseSmall ? "스틸 레이즈 (STEAL)" : "콜 / 림프 (CALL)";
                return new StrategyAdvice(
                    action, actionName, action == ActionType.RaiseSmall ? "#805AD5" : "#3182CE",
                    potOdds, equity, ev,
                    "플레이어블 투기적 핸드 (Tier 3)",
                    $"{position} 레이트 포지션의 이점을 활용하여 셋마이닝(Set-mining) 및 플러시 드로우를 노립니다.",
                    action == ActionType.RaiseSmall ? "쿼터~하프 레이즈" : "체크 / 콜"
                );
            }
            if (callAmount <= potSize * 0.4 && equity >= potOdds)
            {
                return new StrategyAdvice(
                    ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, equity, ev,
                    "오즈에 맞는 참여",
                    "낮은 베팅액 대비 잠재적 기대값(Implied Odds)이 충분하여 콜이 타당합니다.",
                    "콜"
                );
            }
        }

        // Weak/Marginal hands
        if (callAmount == 0)
        {
            return new StrategyAdvice(
                ActionType.Check, "체크 (CHECK)", "#718096", potOdds, equity, ev,
                "무료 플랍 확인",
                "추가 지출 없이 무료로 플랍 카드를 확인합니다.",
                "체크"
            );
        }

        if (equity < potOdds || ev < 0)
        {
            return new StrategyAdvice(
                ActionType.Fold, "다이 (FOLD)", "#E53E3E", potOdds, equity, ev,
                "기대값 마이너스 (-EV) 핸드",
                $"승률({equity}%)이 요구 팟오즈({potOdds}%)에 미치지 못합니다. 장기적 손실을 줄이기 위해 폴드하세요.",
                "폴드"
            );
        }

        return new StrategyAdvice(
            ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, equity, ev,
            "한계 콜 (Marginal Call)",
            "팟 오즈와 에퀴티가 비슷하여 방어 목적으로 콜을 고려할 수 있습니다.",
            "콜"
        );
    }

    private static StrategyAdvice EvaluatePostflop(
        IReadOnlyList<Card> heroCards,
        IReadOnlyList<Card> boardCards,
        SimulationResult sim,
        double potSize,
        double callAmount,
        double heroStack,
        double potOdds,
        double ev)
    {
        var allKnown = heroCards.Concat(boardCards).ToList();
        var heroHand = HandEvaluator.Evaluate(allKnown);

        // Monster hands: Flush, Full House, Quads, Straight, or high Trips
        bool isMonster = heroHand.HandType >= HandType.Straight;
        bool isStrongMade = heroHand.HandType == HandType.ThreeOfAKind || heroHand.HandType == HandType.TwoPair;
        bool isTopPair = heroHand.HandType == HandType.OnePair && heroCards.Any(c => (int)c.Rank >= (int)boardCards.Max(b => b.Rank));
        bool isStrongDraw = sim.Outs >= 8; // Open-ended straight draw or flush draw

        // Check if facing a bet
        bool facingBet = callAmount > 0;

        // 1. Monster Hands (Huge Equity > 75%)
        if (isMonster || sim.Equity >= 75)
        {
            if (heroStack <= potSize * 1.5 || sim.Equity >= 90)
            {
                return new StrategyAdvice(
                    ActionType.AllIn, "올인 (ALL-IN)", "#D69E2E", potOdds, sim.Equity, ev,
                    $"절대적 우위 ({heroHand.Description})",
                    $"압도적인 승률({sim.Equity}%)을 보유한 몬스터 핸드입니다. 상대의 콜을 유도하거나 팟 전체를 흡수하세요.",
                    "올인 또는 풀 베팅"
                );
            }
            return new StrategyAdvice(
                ActionType.RaiseLarge, "밸류 레이즈 (RAISE)", "#805AD5", potOdds, sim.Equity, ev,
                $"강력한 밸류 핸드 ({heroHand.Description})",
                $"승률 {sim.Equity}%로 최상의 상황입니다. 상대의 핸드로부터 최대 가치를 이끌어내기 위해 50%~75% 레이즈를 추천합니다.",
                "하프 ~ 75% 팟 레이즈"
            );
        }

        // 2. Strong Made Hands / Two Pair / Top Pair Good Kicker (Equity 55% ~ 75%)
        if (isStrongMade || isTopPair || sim.Equity >= 55)
        {
            if (!facingBet)
            {
                return new StrategyAdvice(
                    ActionType.RaiseMedium, "베팅 (BET 50%)", "#805AD5", potOdds, sim.Equity, ev,
                    $"주도권 밸류 베팅 ({heroHand.Description})",
                    $"에퀴티({sim.Equity}%) 우위를 바탕으로 드로우 핸드들에게 비싼 가격을 치르게 하기 위해 하프 베팅합니다.",
                    "팟의 40% ~ 55% (하프)"
                );
            }
            else
            {
                // If bet is huge vs top pair
                if (callAmount > potSize * 0.8 && !isStrongMade)
                {
                    return new StrategyAdvice(
                        ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, sim.Equity, ev,
                        $"탑페어 캐처 콜",
                        "상대의 강한 압박에 레이즈하기에는 리스크가 크므로 콜로 쇼다운까지 안전하게 확인합니다.",
                        "콜"
                    );
                }
                return new StrategyAdvice(
                    ActionType.RaiseSmall, "레이즈 / 콜 (RAISE)", "#805AD5", potOdds, sim.Equity, ev,
                    $"유리한 에퀴티 ({sim.Equity}%)",
                    $"상대 베팅에 대해 아직 우위를 점하고 있습니다. 리레이즈로 상대의 블러프를 차단하거나 콜합니다.",
                    "콜 또는 쿼터 레이즈"
                );
            }
        }

        // 3. Drawing Hands (Flush Draw, Straight Draw, Outs >= 8)
        if (isStrongDraw)
        {
            if (!facingBet)
            {
                return new StrategyAdvice(
                    ActionType.RaiseSmall, "세미 블러프 (SEMI-BLUFF)", "#805AD5", potOdds, sim.Equity, ev,
                    $"강력한 드로우 ({sim.Outs} Outs)",
                    $"{sim.Outs}개의 아웃츠를 가진 발전 가능성 높은 핸드입니다. 폴드 에퀴티를 얻기 위해 세미 블러프로 주도권을 잡습니다.",
                    "팟의 33% ~ 50% (쿼터/하프)"
                );
            }
            else
            {
                if (sim.Equity >= potOdds || ev > 0)
                {
                    return new StrategyAdvice(
                        ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, sim.Equity, ev,
                        $"오즈 적합 콜 (+EV)",
                        $"팟 오즈({potOdds}%) 대비 에퀴티({sim.Equity}%)와 {sim.Outs}개의 아웃츠가 뒷받침되므로 수익성 있는 콜입니다.",
                        "콜"
                    );
                }
            }
        }

        // 4. Weak / Marginal Hands
        if (!facingBet)
        {
            return new StrategyAdvice(
                ActionType.Check, "체크 (CHECK)", "#718096", potOdds, sim.Equity, ev,
                "팟 컨트롤 (Pot Control)",
                "약한 메이드 핸드 또는 쇼다운 가치가 부족하므로 체크하여 무료로 다음 스트리트를 봅니다.",
                "체크"
            );
        }

        if (sim.Equity < potOdds || ev < 0)
        {
            return new StrategyAdvice(
                ActionType.Fold, "다이 (FOLD)", "#E53E3E", potOdds, sim.Equity, ev,
                "역전 불리 (-EV 폴드 권장)",
                $"요구 팟 오즈 {potOdds}%에 비해 승률({sim.Equity}%)이 너무 낮고 아웃츠({sim.Outs}개)가 부족합니다. 미련 없이 다이하세요.",
                "폴드"
            );
        }

        return new StrategyAdvice(
            ActionType.Call, "콜 (CALL)", "#3182CE", potOdds, sim.Equity, ev,
            "한계 콜 (Marginal Call)",
            "팟 오즈와 근접하여 최소 방어 목적으로 1회 콜을 고려합니다.",
            "콜"
        );
    }
}
