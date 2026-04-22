using ChatCasino.Models;

namespace ChatCasino.Services;

public interface IDeckService
{
    DeckShoe<Card> GetStandardDeck(int deckCount, bool shuffled);
    DeckShoe<UltimaDeckCard> GetUltimaDeck();
}
