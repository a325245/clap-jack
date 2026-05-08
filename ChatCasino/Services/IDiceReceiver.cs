namespace ChatCasino.Services;

/// <summary>Implemented by game modules that support dice-driven card randomization.</summary>
public interface IDiceReceiver
{
    /// <summary>Called when an in-game /dice result is received matching the expected max value.</summary>
    void ReceiveDice(int value);
}
