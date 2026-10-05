using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerAssistant.Core;

public class Deck
{
    private static readonly Card[] All52Cards;

    static Deck()
    {
        var cards = new List<Card>(52);
        foreach (Suit suit in Enum.GetValues<Suit>())
        {
            foreach (Rank rank in Enum.GetValues<Rank>())
            {
                cards.Add(new Card(rank, suit));
            }
        }
        All52Cards = cards.ToArray();
    }

    public static IReadOnlyList<Card> FullDeck => All52Cards;

    private readonly List<Card> _cards;
    private readonly Random _rng;

    public Deck(Random? rng = null)
    {
        _rng = rng ?? new Random();
        _cards = new List<Card>(All52Cards);
        Shuffle();
    }

    public Deck(IEnumerable<Card> excludeCards, Random? rng = null)
    {
        _rng = rng ?? new Random();
        var excludeSet = new HashSet<Card>(excludeCards);
        _cards = All52Cards.Where(c => !excludeSet.Contains(c)).ToList();
        Shuffle();
    }

    public void Shuffle()
    {
        int n = _cards.Count;
        while (n > 1)
        {
            n--;
            int k = _rng.Next(n + 1);
            (_cards[k], _cards[n]) = (_cards[n], _cards[k]);
        }
    }

    public Card Draw()
    {
        if (_cards.Count == 0) throw new InvalidOperationException("Deck is empty");
        int lastIndex = _cards.Count - 1;
        var card = _cards[lastIndex];
        _cards.RemoveAt(lastIndex);
        return card;
    }

    public int RemainingCount => _cards.Count;
}
