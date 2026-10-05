using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PokerAssistant.Core;

namespace PokerAssistant.UI;

public partial class CardPickerWindow : Window
{
    private readonly HashSet<Card> _selectedCards = new();
    private readonly int _maxSelect;
    private readonly Action<List<Card>> _onSelected;

    public CardPickerWindow(IEnumerable<Card> initialCards, int maxSelect, Action<List<Card>> onSelected)
    {
        InitializeComponent();
        _maxSelect = maxSelect;
        _onSelected = onSelected;

        foreach (var c in initialCards)
        {
            _selectedCards.Add(c);
        }

        BuildCardButtons();
        UpdateSummary();
    }

    private void BuildCardButtons()
    {
        CardGrid.Children.Clear();

        Suit[] suits = { Suit.Spades, Suit.Hearts, Suit.Diamonds, Suit.Clubs };
        Rank[] ranks = {
            Rank.Ace, Rank.King, Rank.Queen, Rank.Jack, Rank.Ten,
            Rank.Nine, Rank.Eight, Rank.Seven, Rank.Six, Rank.Five,
            Rank.Four, Rank.Three, Rank.Two
        };

        foreach (var suit in suits)
        {
            foreach (var rank in ranks)
            {
                var card = new Card(rank, suit);
                bool isSelected = _selectedCards.Contains(card);

                var cardColor = (Brush)new BrushConverter().ConvertFromString(card.SuitColorHex)!;
                var btn = new Button
                {
                    Content = card.DisplayText,
                    Margin = new Thickness(2),
                    Height = 44,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = cardColor,
                    BorderBrush = isSelected ? Brushes.Yellow : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    Tag = card,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                btn.Click += (s, e) =>
                {
                    if (_selectedCards.Contains(card))
                    {
                        _selectedCards.Remove(card);
                        btn.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                        btn.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        if (_selectedCards.Count >= _maxSelect)
                        {
                            MessageBox.Show($"최대 {_maxSelect}장까지만 선택할 수 있습니다.", "선택 초과", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        _selectedCards.Add(card);
                        btn.BorderBrush = Brushes.Yellow;
                        btn.BorderThickness = new Thickness(3);
                    }
                    UpdateSummary();
                };

                CardGrid.Children.Add(btn);
            }
        }
    }

    private void UpdateSummary()
    {
        string listStr = string.Join(" ", _selectedCards.Select(c => c.DisplayText));
        SelectedSummaryText.Text = $"선택된 카드 ({_selectedCards.Count}/{_maxSelect}장): {listStr}";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _onSelected?.Invoke(_selectedCards.ToList());
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
