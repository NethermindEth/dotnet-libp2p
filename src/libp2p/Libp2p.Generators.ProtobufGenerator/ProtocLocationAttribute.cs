namespace Libp2p.Generators.ProtobufGenerator;

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class ProtocLocationAttribute : Attribute
{
    public ProtocLocationAttribute(string protocLocation)
    {
        ProtocLocation = protocLocation;
    }

    public string ProtocLocation { get; }
}
