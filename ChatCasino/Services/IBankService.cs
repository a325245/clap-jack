using ChatCasino.Models;

namespace ChatCasino.Services;

public interface IBankService
{
    TransactionResult VerifyFunds(Player player, int amount);
    TransactionResult Deduct(Player player, int amount, string reason);
    TransactionResult Credit(Player player, int amount, string reason);
    TransactionResult Award(Player player, int amount, string reason);
}
