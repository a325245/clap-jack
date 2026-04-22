using System.Collections.Generic;

namespace ChatCasino.UI;

public abstract class BaseViewModel : ICasinoViewModel
{
    public string GameTitle { get; set; } = string.Empty;
    public string GameStatus { get; set; } = string.Empty;
    public List<PlayerSlotViewModel> Seats { get; set; } = new();

    protected readonly List<string> Actions = new();

    public virtual IReadOnlyList<string> GetActionButtons() => Actions;
}
