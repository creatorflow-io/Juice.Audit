using Juice.Domain;

namespace Juice.Audit.Domain.AccessLogAggregate
{
    public class ServerInfo : ValueObject
    {
        public ServerInfo() { }
        public ServerInfo(string machineName, string osVersion, string? softwareVersion, string appName)
        {
            Machine = Validatable.TrimExceededLength(machineName, LengthConstants.NameLength) ?? "";
            OS = Validatable.TrimExceededLength(osVersion, LengthConstants.NameLength) ?? "";
            AppVer = Validatable.TrimExceededLength(softwareVersion, LengthConstants.NameLength);
            App = Validatable.TrimExceededLength(appName, LengthConstants.NameLength) ?? "";
        }
        public string Machine { get; private set; }
        public string OS { get; private set; }
        public string? AppVer { get; private set; }
        public string App { get; private set; }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Machine;
        }
    }
}
