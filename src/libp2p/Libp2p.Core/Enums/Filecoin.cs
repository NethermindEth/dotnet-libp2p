namespace Libp2p.Core.Enums;
public enum Filecoin
{
    // Filecoin piece or sector data commitment merkle node/root (CommP & CommD)
    FilCommitmentUnsealed = 0xf101,
    // Filecoin sector data commitment merkle node/root - sealed and replicated (CommR)
    FilCommitmentSealed = 0xf102,
    Unknown,
}
