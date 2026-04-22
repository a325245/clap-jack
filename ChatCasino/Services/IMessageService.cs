namespace ChatCasino.Services;

public interface IMessageService
{
    void QueuePartyMessage(string message);
    void QueueTell(string playerName, string server, string message);
    void QueueAdminEcho(string message);
    void ProcessTick();
    void ClearAllQueues();
}
