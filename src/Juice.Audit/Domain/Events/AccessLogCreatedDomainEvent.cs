
namespace Juice.Audit.Domain.Events
{
    public record AccessLogCreatedDomainEvent : MessageBase, INotification
    {
        public Guid RecordId { get; private set; }

        public AccessLogCreatedDomainEvent(Guid recordId)
        {
            RecordId = recordId;
        }
    }
}
