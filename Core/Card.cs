using System;

namespace PokerAssistant.Core;

public enum Suit
{
    Spades = 0,   // ♠
    Hearts = 1,   // ♥
    Diamonds = 2, // ♦
    Clubs = 3     // ♣
}

public enum Rank
{
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Jack = 11,
    Queen = 12,
    King = 13,
    Ace = 14
}

public readonly record struct Card(Rank Rank, Suit Suit) : IComparable<Card>
{
    public char RankChar => Rank switch
    {
        Rank.Ten => 'T',
        Rank.Jack => 'J',
        Rank.Queen => 'Q',
        Rank.King => 'K',
        Rank.Ace => 'A',
        _ => (char)('0' + (int)Rank)
    };

    public string RankDisplay => Rank switch
    {
        Rank.Ten => "10",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Ace => "A",
        _ => ((int)Rank).ToString()
    };

    public char SuitChar => Suit switch
    {
        Suit.Spades => 's',
        Suit.Hearts => 'h',
        Suit.Diamonds => 'd',
        Suit.Clubs => 'c',
        _ => '?'
    };

    public string SuitSymbol => Suit switch
    {
        Suit.Spades => "♠",
        Suit.Hearts => "♥",
        Suit.Diamonds => "♦",
        Suit.Clubs => "♣",
        _ => "?"
    };

    public bool IsRed => Suit == Suit.Hearts || Suit == Suit.Diamonds;

    public string SuitColorHex => Suit switch
    {
        Suit.Spades => "#475569",   // 회색 / Slate Gray (스페이드)
        Suit.Hearts => "#DC2626",   // 빨간색 / Vibrant Red (하트)
        Suit.Diamonds => "#2563EB", // 파란색 / Vibrant Blue (다이아)
        Suit.Clubs => "#16A34A",    // 초록색 / Vibrant Green (클로버)
        _ => "#CBD5E0"
    };

    public string ClassicSuitColorHex => SuitColorHex;

    public override string ToString() => $"{RankChar}{SuitChar}";
    public string DisplayText => $"{RankDisplay}{SuitSymbol}";

    public int CompareTo(Card other) => Rank.CompareTo(other.Rank);

    public static Card? TryParse(string str)
    {
        if (string.IsNullOrWhiteSpace(str) || str.Length < 2) return null;
        str = str.Trim();

        Rank? rank = str[0] switch
        {
            '2' => Rank.Two,
            '3' => Rank.Three,
            '4' => Rank.Four,
            '5' => Rank.Five,
            '6' => Rank.Six,
            '7' => Rank.Seven,
            '8' => Rank.Eight,
            '9' => Rank.Nine,
            'T' or 't' => Rank.Ten,
            'J' or 'j' => Rank.Jack,
            'Q' or 'q' => Rank.Queen,
            'K' or 'k' => Rank.King,
            'A' or 'a' => Rank.Ace,
            _ => null
        };

        if (rank == null && str.Length >= 3 && str.StartsWith("10"))
        {
            rank = Rank.Ten;
            str = str.Substring(1); // align with suit char at end
        }

        if (rank == null) return null;

        char suitChar = str[^1];
        Suit? suit = char.ToLowerInvariant(suitChar) switch
        {
            's' or '♠' => Suit.Spades,
            'h' or '♥' => Suit.Hearts,
            'd' or '♦' => Suit.Diamonds,
            'c' or '♣' => Suit.Clubs,
            _ => null
        };

        if (suit == null) return null;
        return new Card(rank.Value, suit.Value);
    }
}
