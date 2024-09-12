// <auto-generated>
//     Generated by the protocol buffer compiler.  DO NOT EDIT!
//     source: Identify.proto
// </auto-generated>
#pragma warning disable 1591, 0612, 3021, 8981
#region Designer generated code

using pb = global::Google.Protobuf;
using pbc = global::Google.Protobuf.Collections;
using pbr = global::Google.Protobuf.Reflection;
using scg = global::System.Collections.Generic;
namespace Nethermind.Libp2p.Protocols.Identify.Dto {

  /// <summary>Holder for reflection information generated from Identify.proto</summary>
  public static partial class IdentifyReflection {

    #region Descriptor
    /// <summary>File descriptor for Identify.proto</summary>
    public static pbr::FileDescriptor Descriptor {
      get { return descriptor; }
    }
    private static pbr::FileDescriptor descriptor;

    static IdentifyReflection() {
      byte[] descriptorData = global::System.Convert.FromBase64String(
          string.Concat(
            "Cg5JZGVudGlmeS5wcm90byKKAQoISWRlbnRpZnkSFwoPcHJvdG9jb2xWZXJz",
            "aW9uGAUgASgJEhQKDGFnZW50VmVyc2lvbhgGIAEoCRIRCglwdWJsaWNLZXkY",
            "ASABKAwSEwoLbGlzdGVuQWRkcnMYAiADKAwSFAoMb2JzZXJ2ZWRBZGRyGAQg",
            "ASgMEhEKCXByb3RvY29scxgDIAMoCUIrqgIoTmV0aGVybWluZC5MaWJwMnAu",
            "UHJvdG9jb2xzLklkZW50aWZ5LkR0bw=="));
      descriptor = pbr::FileDescriptor.FromGeneratedCode(descriptorData,
          new pbr::FileDescriptor[] { },
          new pbr::GeneratedClrTypeInfo(null, null, new pbr::GeneratedClrTypeInfo[] {
            new pbr::GeneratedClrTypeInfo(typeof(global::Nethermind.Libp2p.Protocols.Identify.Dto.Identify), global::Nethermind.Libp2p.Protocols.Identify.Dto.Identify.Parser, new[]{ "ProtocolVersion", "AgentVersion", "PublicKey", "ListenAddrs", "ObservedAddr", "Protocols" }, null, null, null, null)
          }));
    }
    #endregion

  }
  #region Messages
  [global::System.Diagnostics.DebuggerDisplayAttribute("{ToString(),nq}")]
  public sealed partial class Identify : pb::IMessage<Identify>
  #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      , pb::IBufferMessage
  #endif
  {
    private static readonly pb::MessageParser<Identify> _parser = new pb::MessageParser<Identify>(() => new Identify());
    private pb::UnknownFieldSet _unknownFields;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pb::MessageParser<Identify> Parser { get { return _parser; } }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pbr::MessageDescriptor Descriptor {
      get { return global::Nethermind.Libp2p.Protocols.Identify.Dto.IdentifyReflection.Descriptor.MessageTypes[0]; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    pbr::MessageDescriptor pb::IMessage.Descriptor {
      get { return Descriptor; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public Identify() {
      OnConstruction();
    }

    partial void OnConstruction();

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public Identify(Identify other) : this() {
      protocolVersion_ = other.protocolVersion_;
      agentVersion_ = other.agentVersion_;
      publicKey_ = other.publicKey_;
      listenAddrs_ = other.listenAddrs_.Clone();
      observedAddr_ = other.observedAddr_;
      protocols_ = other.protocols_.Clone();
      _unknownFields = pb::UnknownFieldSet.Clone(other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public Identify Clone() {
      return new Identify(this);
    }

    /// <summary>Field number for the "protocolVersion" field.</summary>
    public const int ProtocolVersionFieldNumber = 5;
    private readonly static string ProtocolVersionDefaultValue = "";

    private string protocolVersion_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string ProtocolVersion {
      get { return protocolVersion_ ?? ProtocolVersionDefaultValue; }
      set {
        protocolVersion_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }
    /// <summary>Gets whether the "protocolVersion" field is set</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool HasProtocolVersion {
      get { return protocolVersion_ != null; }
    }
    /// <summary>Clears the value of the "protocolVersion" field</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void ClearProtocolVersion() {
      protocolVersion_ = null;
    }

    /// <summary>Field number for the "agentVersion" field.</summary>
    public const int AgentVersionFieldNumber = 6;
    private readonly static string AgentVersionDefaultValue = "";

    private string agentVersion_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string AgentVersion {
      get { return agentVersion_ ?? AgentVersionDefaultValue; }
      set {
        agentVersion_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }
    /// <summary>Gets whether the "agentVersion" field is set</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool HasAgentVersion {
      get { return agentVersion_ != null; }
    }
    /// <summary>Clears the value of the "agentVersion" field</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void ClearAgentVersion() {
      agentVersion_ = null;
    }

    /// <summary>Field number for the "publicKey" field.</summary>
    public const int PublicKeyFieldNumber = 1;
    private readonly static pb::ByteString PublicKeyDefaultValue = pb::ByteString.Empty;

    private pb::ByteString publicKey_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pb::ByteString PublicKey {
      get { return publicKey_ ?? PublicKeyDefaultValue; }
      set {
        publicKey_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }
    /// <summary>Gets whether the "publicKey" field is set</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool HasPublicKey {
      get { return publicKey_ != null; }
    }
    /// <summary>Clears the value of the "publicKey" field</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void ClearPublicKey() {
      publicKey_ = null;
    }

    /// <summary>Field number for the "listenAddrs" field.</summary>
    public const int ListenAddrsFieldNumber = 2;
    private static readonly pb::FieldCodec<pb::ByteString> _repeated_listenAddrs_codec
        = pb::FieldCodec.ForBytes(18);
    private readonly pbc::RepeatedField<pb::ByteString> listenAddrs_ = new pbc::RepeatedField<pb::ByteString>();
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pbc::RepeatedField<pb::ByteString> ListenAddrs {
      get { return listenAddrs_; }
    }

    /// <summary>Field number for the "observedAddr" field.</summary>
    public const int ObservedAddrFieldNumber = 4;
    private readonly static pb::ByteString ObservedAddrDefaultValue = pb::ByteString.Empty;

    private pb::ByteString observedAddr_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pb::ByteString ObservedAddr {
      get { return observedAddr_ ?? ObservedAddrDefaultValue; }
      set {
        observedAddr_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }
    /// <summary>Gets whether the "observedAddr" field is set</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool HasObservedAddr {
      get { return observedAddr_ != null; }
    }
    /// <summary>Clears the value of the "observedAddr" field</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void ClearObservedAddr() {
      observedAddr_ = null;
    }

    /// <summary>Field number for the "protocols" field.</summary>
    public const int ProtocolsFieldNumber = 3;
    private static readonly pb::FieldCodec<string> _repeated_protocols_codec
        = pb::FieldCodec.ForString(26);
    private readonly pbc::RepeatedField<string> protocols_ = new pbc::RepeatedField<string>();
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pbc::RepeatedField<string> Protocols {
      get { return protocols_; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override bool Equals(object other) {
      return Equals(other as Identify);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool Equals(Identify other) {
      if (ReferenceEquals(other, null)) {
        return false;
      }
      if (ReferenceEquals(other, this)) {
        return true;
      }
      if (ProtocolVersion != other.ProtocolVersion) return false;
      if (AgentVersion != other.AgentVersion) return false;
      if (PublicKey != other.PublicKey) return false;
      if(!listenAddrs_.Equals(other.listenAddrs_)) return false;
      if (ObservedAddr != other.ObservedAddr) return false;
      if(!protocols_.Equals(other.protocols_)) return false;
      return Equals(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override int GetHashCode() {
      int hash = 1;
      if (HasProtocolVersion) hash ^= ProtocolVersion.GetHashCode();
      if (HasAgentVersion) hash ^= AgentVersion.GetHashCode();
      if (HasPublicKey) hash ^= PublicKey.GetHashCode();
      hash ^= listenAddrs_.GetHashCode();
      if (HasObservedAddr) hash ^= ObservedAddr.GetHashCode();
      hash ^= protocols_.GetHashCode();
      if (_unknownFields != null) {
        hash ^= _unknownFields.GetHashCode();
      }
      return hash;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override string ToString() {
      return pb::JsonFormatter.ToDiagnosticString(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void WriteTo(pb::CodedOutputStream output) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      output.WriteRawMessage(this);
    #else
      if (HasPublicKey) {
        output.WriteRawTag(10);
        output.WriteBytes(PublicKey);
      }
      listenAddrs_.WriteTo(output, _repeated_listenAddrs_codec);
      protocols_.WriteTo(output, _repeated_protocols_codec);
      if (HasObservedAddr) {
        output.WriteRawTag(34);
        output.WriteBytes(ObservedAddr);
      }
      if (HasProtocolVersion) {
        output.WriteRawTag(42);
        output.WriteString(ProtocolVersion);
      }
      if (HasAgentVersion) {
        output.WriteRawTag(50);
        output.WriteString(AgentVersion);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(output);
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalWriteTo(ref pb::WriteContext output) {
      if (HasPublicKey) {
        output.WriteRawTag(10);
        output.WriteBytes(PublicKey);
      }
      listenAddrs_.WriteTo(ref output, _repeated_listenAddrs_codec);
      protocols_.WriteTo(ref output, _repeated_protocols_codec);
      if (HasObservedAddr) {
        output.WriteRawTag(34);
        output.WriteBytes(ObservedAddr);
      }
      if (HasProtocolVersion) {
        output.WriteRawTag(42);
        output.WriteString(ProtocolVersion);
      }
      if (HasAgentVersion) {
        output.WriteRawTag(50);
        output.WriteString(AgentVersion);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(ref output);
      }
    }
    #endif

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public int CalculateSize() {
      int size = 0;
      if (HasProtocolVersion) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(ProtocolVersion);
      }
      if (HasAgentVersion) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(AgentVersion);
      }
      if (HasPublicKey) {
        size += 1 + pb::CodedOutputStream.ComputeBytesSize(PublicKey);
      }
      size += listenAddrs_.CalculateSize(_repeated_listenAddrs_codec);
      if (HasObservedAddr) {
        size += 1 + pb::CodedOutputStream.ComputeBytesSize(ObservedAddr);
      }
      size += protocols_.CalculateSize(_repeated_protocols_codec);
      if (_unknownFields != null) {
        size += _unknownFields.CalculateSize();
      }
      return size;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(Identify other) {
      if (other == null) {
        return;
      }
      if (other.HasProtocolVersion) {
        ProtocolVersion = other.ProtocolVersion;
      }
      if (other.HasAgentVersion) {
        AgentVersion = other.AgentVersion;
      }
      if (other.HasPublicKey) {
        PublicKey = other.PublicKey;
      }
      listenAddrs_.Add(other.listenAddrs_);
      if (other.HasObservedAddr) {
        ObservedAddr = other.ObservedAddr;
      }
      protocols_.Add(other.protocols_);
      _unknownFields = pb::UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(pb::CodedInputStream input) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      input.ReadRawMessage(this);
    #else
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
      if ((tag & 7) == 4) {
        // Abort on any end group tag.
        return;
      }
      switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
            break;
          case 10: {
            PublicKey = input.ReadBytes();
            break;
          }
          case 18: {
            listenAddrs_.AddEntriesFrom(input, _repeated_listenAddrs_codec);
            break;
          }
          case 26: {
            protocols_.AddEntriesFrom(input, _repeated_protocols_codec);
            break;
          }
          case 34: {
            ObservedAddr = input.ReadBytes();
            break;
          }
          case 42: {
            ProtocolVersion = input.ReadString();
            break;
          }
          case 50: {
            AgentVersion = input.ReadString();
            break;
          }
        }
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalMergeFrom(ref pb::ParseContext input) {
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
      if ((tag & 7) == 4) {
        // Abort on any end group tag.
        return;
      }
      switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, ref input);
            break;
          case 10: {
            PublicKey = input.ReadBytes();
            break;
          }
          case 18: {
            listenAddrs_.AddEntriesFrom(ref input, _repeated_listenAddrs_codec);
            break;
          }
          case 26: {
            protocols_.AddEntriesFrom(ref input, _repeated_protocols_codec);
            break;
          }
          case 34: {
            ObservedAddr = input.ReadBytes();
            break;
          }
          case 42: {
            ProtocolVersion = input.ReadString();
            break;
          }
          case 50: {
            AgentVersion = input.ReadString();
            break;
          }
        }
      }
    }
    #endif

  }

  #endregion

}

#endregion Designer generated code
