using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerAssistant.Core;

public enum HandType
{
    HighCard = 0,
    OnePair = 1,
    TwoPair = 2,
    ThreeOfAKind = 3,
    Straight = 4,
    Flush = 5,
    FullHouse = 6,
    FourOfAKind = 7,
    StraightFlush = 8,
    RoyalFlush = 9
}

public readonly record struct HandScore(HandType HandType, int Score, string Description) : IComparable<HandScore>
{
    public int CompareTo(HandScore other) => Score.CompareTo(other.Score);

    public static bool operator >(HandScore a, HandScore b) => a.Score > b.Score;
    public static bool operator <(HandScore a, HandScore b) => a.Score < b.Score;
    public static bool operator >=(HandScore a, HandScore b) => a.Score >= b.Score;
    public static bool operator <=(HandScore a, HandScore b) => a.Score <= b.Score;
}

public static class HandEvaluator
{
    // Combinations of 5 from 7 cards (21 combos)
    private static readonly int[][] Combos7Choose5 = new int[][]
    {
        new[] {0,1,2,3,4}, new[] {0,1,2,3,5}, new[] {0,1,2,3,6}, new[] {0,1,2,4,5},
        new[] {0,1,2,4,6}, new[] {0,1,2,5,6}, new[] {0,1,3,4,5}, new[] {0,1,3,4,6},
        new[] {0,1,3,5,6}, new[] {0,1,4,5,6}, new[] {0,2,3,4,5}, new[] {0,2,3,4,6},
        new[] {0,2,3,5,6}, new[] {0,2,4,5,6}, new[] {0,3,4,5,6}, new[] {1,2,3,4,5},
        new[] {1,2,3,4,6}, new[] {1,2,3,5,6}, new[] {1,2,4,5,6}, new[] {1,3,4,5,6},
        new[] {2,3,4,5,6}
    };

    // Combinations of 5 from 6 cards (6 combos)
    private static readonly int[][] Combos6Choose5 = new int[][]
    {
        new[] {0,1,2,3,4}, new[] {0,1,2,3,5}, new[] {0,1,2,4,5},
        new[] {0,1,3,4,5}, new[] {0,2,3,4,5}, new[] {1,2,3,4,5}
    };

    public static HandScore Evaluate(IReadOnlyList<Card> cards)
    {
        if (cards.Count < 5)
        {
            return EvaluatePartial(cards);
        }
        if (cards.Count == 5)
        {
            return Evaluate5(cards[0], cards[1], cards[2], cards[3], cards[4]);
        }
        if (cards.Count == 6)
        {
            HandScore best = default;
            var five = new Card[5];
            foreach (var combo in Combos6Choose5)
            {
                five[0] = cards[combo[0]];
                five[1] = cards[combo[1]];
                five[2] = cards[combo[2]];
                five[3] = cards[combo[3]];
                five[4] = cards[combo[4]];
                var score = Evaluate5(five[0], five[1], five[2], five[3], five[4]);
                if (score > best) best = score;
            }
            return best;
        }
        if (cards.Count == 7)
        {
            HandScore best = default;
            foreach (var combo in Combos7Choose5)
            {
                var score = Evaluate5(
                    cards[combo[0]],
                    cards[combo[1]],
                    cards[combo[2]],
                    cards[combo[3]],
                    cards[combo[4]]
                );
                if (score > best) best = score;
            }
            return best;
        }

        // For more than 7 (rare fallback), take best 7
        return Evaluate(cards.Take(7).ToArray());
    }

    private static HandScore EvaluatePartial(IReadOnlyList<Card> cards)
    {
        if (cards.Count == 2)
        {
            if (cards[0].Rank == cards[1].Rank)
            {
                int score = (int)HandType.OnePair * 1_000_000 + (int)cards[0].Rank * 1000;
                return new HandScore(HandType.OnePair, score, $"포켓 페어 ({cards[0].RankDisplay} Pair)");
            }
            int high = Math.Max((int)cards[0].Rank, (int)cards[1].Rank);
            int low = Math.Min((int)cards[0].Rank, (int)cards[1].Rank);
            bool suited = cards[0].Suit == cards[1].Suit;
            string sText = suited ? "수티드" : "오프수티드";
            return new HandScore(HandType.HighCard, (int)HandType.HighCard * 1_000_000 + high * 100 + low,
                $"{cards[0].RankDisplay}{cards[1].RankDisplay} {sText}");
        }
        return new HandScore(HandType.HighCard, 0, "진행 중");
    }

    public static HandScore Evaluate5(Card c1, Card c2, Card c3, Card c4, Card c5)
    {
        Span<Card> cards = stackalloc Card[5] { c1, c2, c3, c4, c5 };
        // Sort descending by rank
        for (int i = 0; i < 4; i++)
        {
            for (int j = i + 1; j < 5; j++)
            {
                if (cards[j].Rank > cards[i].Rank)
                {
                    (cards[i], cards[j]) = (cards[j], cards[i]);
                }
            }
        }

        bool isFlush = cards[0].Suit == cards[1].Suit &&
                       cards[1].Suit == cards[2].Suit &&
                       cards[2].Suit == cards[3].Suit &&
                       cards[3].Suit == cards[4].Suit;

        bool isStraight = false;
        int straightHigh = 0;

        // Check normal straight
        if (cards[0].Rank - 1 == cards[1].Rank &&
            cards[1].Rank - 1 == cards[2].Rank &&
            cards[2].Rank - 1 == cards[3].Rank &&
            cards[3].Rank - 1 == cards[4].Rank)
        {
            isStraight = true;
            straightHigh = (int)cards[0].Rank;
        }
        // Ace-low straight: A-5-4-3-2
        else if (cards[0].Rank == Rank.Ace &&
                 cards[1].Rank == Rank.Five &&
                 cards[2].Rank == Rank.Four &&
                 cards[3].Rank == Rank.Three &&
                 cards[4].Rank == Rank.Two)
        {
            isStraight = true;
            straightHigh = 5; // Five-high straight
        }

        if (isFlush && isStraight)
        {
            if (straightHigh == 14)
            {
                return new HandScore(HandType.RoyalFlush, 9_000_000 + straightHigh, "로얄 스트레이트 플러시 (Royal Flush)");
            }
            return new HandScore(HandType.StraightFlush, 8_000_000 + straightHigh, $"스트레이트 플러시 ({straightHigh} 탑)");
        }

        // Rank count frequencies
        Span<int> rankCounts = stackalloc int[15];
        for (int i = 0; i < 5; i++)
        {
            rankCounts[(int)cards[i].Rank]++;
        }

        int fourRank = 0, threeRank = 0, pair1 = 0, pair2 = 0;
        for (int r = 14; r >= 2; r--)
        {
            if (rankCounts[r] == 4) fourRank = r;
            else if (rankCounts[r] == 3) threeRank = r;
            else if (rankCounts[r] == 2)
            {
                if (pair1 == 0) pair1 = r;
                else if (pair2 == 0) pair2 = r;
            }
        }

        if (fourRank > 0)
        {
            int kicker = 0;
            for (int i = 0; i < 5; i++)
                if ((int)cards[i].Rank != fourRank) { kicker = (int)cards[i].Rank; break; }
            int score = 7_000_000 + fourRank * 100 + kicker;
            return new HandScore(HandType.FourOfAKind, score, $"포카드 ({RankName(fourRank)} Four of a Kind)");
        }

        if (threeRank > 0 && pair1 > 0)
        {
            int score = 6_000_000 + threeRank * 100 + pair1;
            return new HandScore(HandType.FullHouse, score, $"풀하우스 ({RankName(threeRank)}s full of {RankName(pair1)}s)");
        }

        if (isFlush)
        {
            int score = 5_000_000 +
                        (int)cards[0].Rank * 10_000 +
                        (int)cards[1].Rank * 1_000 +
                        (int)cards[2].Rank * 100 +
                        (int)cards[3].Rank * 10 +
                        (int)cards[4].Rank;
            return new HandScore(HandType.Flush, score, $"플러시 ({RankName((int)cards[0].Rank)} 하이)");
        }

        if (isStraight)
        {
            int score = 4_000_000 + straightHigh;
            return new HandScore(HandType.Straight, score, $"스트레이트 ({RankName(straightHigh)} 탑)");
        }

        if (threeRank > 0)
        {
            int k1 = 0, k2 = 0;
            for (int i = 0; i < 5; i++)
            {
                if ((int)cards[i].Rank != threeRank)
                {
                    if (k1 == 0) k1 = (int)cards[i].Rank;
                    else if (k2 == 0) k2 = (int)cards[i].Rank;
                }
            }
            int score = 3_000_000 + threeRank * 1_000 + k1 * 10 + k2;
            return new HandScore(HandType.ThreeOfAKind, score, $"트리플 ({RankName(threeRank)} Set/Trips)");
        }

        if (pair1 > 0 && pair2 > 0)
        {
            int kicker = 0;
            for (int i = 0; i < 5; i++)
            {
                if ((int)cards[i].Rank != pair1 && (int)cards[i].Rank != pair2)
                {
                    kicker = (int)cards[i].Rank;
                    break;
                }
            }
            int score = 2_000_000 + pair1 * 1_000 + pair2 * 50 + kicker;
            return new HandScore(HandType.TwoPair, score, $"투 페어 ({RankName(pair1)} & {RankName(pair2)})");
        }

        if (pair1 > 0)
        {
            int k1 = 0, k2 = 0, k3 = 0;
            for (int i = 0; i < 5; i++)
            {
                if ((int)cards[i].Rank != pair1)
                {
                    if (k1 == 0) k1 = (int)cards[i].Rank;
                    else if (k2 == 0) k2 = (int)cards[i].Rank;
                    else if (k3 == 0) k3 = (int)cards[i].Rank;
                }
            }
            int score = 1_000_000 + pair1 * 10_000 + k1 * 100 + k2 * 10 + k3;
            return new HandScore(HandType.OnePair, score, $"원 페어 ({RankName(pair1)} Pair)");
        }

        // High card
        int hcScore = (int)cards[0].Rank * 10_000 +
                      (int)cards[1].Rank * 1_000 +
                      (int)cards[2].Rank * 100 +
                      (int)cards[3].Rank * 10 +
                      (int)cards[4].Rank;
        return new HandScore(HandType.HighCard, hcScore, $"하이 카드 ({RankName((int)cards[0].Rank)} High)");
    }

    private static string RankName(int rank) => rank switch
    {
        14 => "A",
        13 => "K",
        12 => "Q",
        11 => "J",
        10 => "10",
        _ => rank.ToString()
    };
}
