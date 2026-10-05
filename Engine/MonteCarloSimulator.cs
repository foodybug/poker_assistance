using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PokerAssistant.Core;

namespace PokerAssistant.Engine;

public record SimulationResult(
    double WinRate,
    double TieRate,
    double LossRate,
    double Equity,
    int Outs,
    string HeroCurrentHand,
    string BoardTexture,
    int TotalSimulations
);

public static class MonteCarloSimulator
{
    public static SimulationResult Run(
        IReadOnlyList<Card> heroCards,
        IReadOnlyList<Card> boardCards,
        int opponentCount = 1,
        int simulations = 5000)
    {
        if (heroCards.Count < 2)
        {
            return new SimulationResult(0, 0, 100, 0, 0, "핸드 없음", "대기", 0);
        }

        var allKnown = heroCards.Concat(boardCards).ToList();
        var currentHeroScore = HandEvaluator.Evaluate(allKnown);
        string boardTexture = AnalyzeBoardTexture(boardCards);

        // If river (board has 5 cards), calculate exact or high-count simulation against opponent range
        int cardsNeededForBoard = 5 - boardCards.Count;
        int cardsNeededPerOpponent = 2;
        int totalCardsNeeded = cardsNeededForBoard + opponentCount * cardsNeededPerOpponent;

        int wins = 0;
        int ties = 0;
        int losses = 0;

        // Partition work across threads
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        int simsPerThread = simulations / processorCount;

        object lockObj = new();

        Parallel.For(0, processorCount, _ =>
        {
            var localRng = new Random(Guid.NewGuid().GetHashCode());
            int localWins = 0;
            int localTies = 0;
            int localLosses = 0;

            var deck = new Deck(allKnown, localRng);
            var remainingCards = new List<Card>(deck.RemainingCount);

            for (int i = 0; i < simsPerThread; i++)
            {
                // Fast shuffle of remaining cards
                var simDeck = new Deck(allKnown, localRng);

                // Draw remaining board cards
                var simBoard = new List<Card>(5);
                simBoard.AddRange(boardCards);
                for (int b = 0; b < cardsNeededForBoard; b++)
                {
                    simBoard.Add(simDeck.Draw());
                }

                // Hero final 7 cards
                var heroFull = new List<Card>(7);
                heroFull.AddRange(heroCards);
                heroFull.AddRange(simBoard);
                var heroScore = HandEvaluator.Evaluate(heroFull);

                // Evaluate opponents
                bool heroWon = true;
                bool isTie = false;

                for (int opp = 0; opp < opponentCount; opp++)
                {
                    var opp1 = simDeck.Draw();
                    var opp2 = simDeck.Draw();
                    var oppFull = new List<Card>(7) { opp1, opp2 };
                    oppFull.AddRange(simBoard);

                    var oppScore = HandEvaluator.Evaluate(oppFull);

                    if (oppScore > heroScore)
                    {
                        heroWon = false;
                        isTie = false;
                        break;
                    }
                    else if (oppScore == heroScore)
                    {
                        isTie = true;
                    }
                }

                if (heroWon && !isTie) localWins++;
                else if (heroWon && isTie) localTies++;
                else localLosses++;
            }

            lock (lockObj)
            {
                wins += localWins;
                ties += localTies;
                losses += localLosses;
            }
        });

        int totalSims = wins + ties + losses;
        if (totalSims == 0) totalSims = 1;

        double winPct = (double)wins / totalSims * 100.0;
        double tiePct = (double)ties / totalSims * 100.0;
        double lossPct = (double)losses / totalSims * 100.0;
        double equity = winPct + (tiePct / (opponentCount + 1));

        int outs = CalculateOuts(heroCards, boardCards);

        return new SimulationResult(
            Math.Round(winPct, 1),
            Math.Round(tiePct, 1),
            Math.Round(lossPct, 1),
            Math.Round(equity, 1),
            outs,
            currentHeroScore.Description,
            boardTexture,
            totalSims
        );
    }

    public static int CalculateOuts(IReadOnlyList<Card> heroCards, IReadOnlyList<Card> boardCards)
    {
        if (boardCards.Count < 3 || boardCards.Count >= 5) return 0;

        var allKnown = heroCards.Concat(boardCards).ToHashSet();
        var currentScore = HandEvaluator.Evaluate(heroCards.Concat(boardCards).ToList());

        int outs = 0;
        foreach (var candidate in Deck.FullDeck)
        {
            if (allKnown.Contains(candidate)) continue;

            var testBoard = boardCards.Concat(new[] { candidate }).ToList();
            var testScore = HandEvaluator.Evaluate(heroCards.Concat(testBoard).ToList());

            // If the single card strictly improves the hand category (e.g. from high card to pair, pair to trips, draw to flush/straight)
            if (testScore.HandType > currentScore.HandType || 
                (testScore.HandType == currentScore.HandType && testScore.Score > currentScore.Score + 10_000))
            {
                outs++;
            }
        }
        return outs;
    }

    public static string AnalyzeBoardTexture(IReadOnlyList<Card> board)
    {
        if (board.Count < 3) return "프리플랍 (Preflop)";

        // Suit counts
        var suitCounts = board.GroupBy(c => c.Suit).Select(g => g.Count()).OrderByDescending(c => c).First();
        bool isMonotone = suitCounts >= 3;
        bool isTwoTone = suitCounts == 2;
        bool isRainbow = suitCounts == 1;

        // Paired board
        bool isPaired = board.GroupBy(c => c.Rank).Any(g => g.Count() >= 2);

        if (isMonotone) return "모노톤 (Monotone - 플러시 위험 높음)";
        if (isPaired) return "페어 보드 (Paired Board - 풀하우스/트립스 유의)";
        if (isTwoTone) return "투 톤 (Two-Tone - 플러시 드로우 가능)";
        return "레인보우 (Rainbow - 드라이 보드)";
    }
}
