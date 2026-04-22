using System.Collections.Generic;

namespace ChatCasino.UI;

public sealed class PlayerSlotViewModel
{
    public string PlayerName { get; set; } = string.Empty;
    public int Bank { get; set; }
    public int BetAmount { get; set; }
    public bool IsAfk { get; set; }
    public bool IsKicked { get; set; }
    public bool IsDealer { get; set; }
    public bool IsActiveTurn { get; set; }
    public int ActiveHandIndex { get; set; } = -1;
    public string ResultText { get; set; } = string.Empty;
    public List<string> Cards { get; set; } = new();
    public List<List<string>> HandGroups { get; set; } = new();
    public List<string> HandResultTexts { get; set; } = new();
}

public interface ICasinoViewModel
{
    string GameTitle { get; }
    string GameStatus { get; }
    List<PlayerSlotViewModel> Seats { get; }
    IReadOnlyList<string> GetActionButtons();
}
