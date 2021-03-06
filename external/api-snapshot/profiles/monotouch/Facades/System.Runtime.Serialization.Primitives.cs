// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

[assembly:System.Reflection.AssemblyVersionAttribute("4.2.1.0")]
[assembly:System.Diagnostics.DebuggableAttribute((System.Diagnostics.DebuggableAttribute.DebuggingModes)(2))]
[assembly:System.Reflection.AssemblyCompanyAttribute("Mono development team")]
[assembly:System.Reflection.AssemblyCopyrightAttribute("(c) Various Mono authors")]
[assembly:System.Reflection.AssemblyDefaultAliasAttribute("System.Runtime.Serialization.Primitives")]
[assembly:System.Reflection.AssemblyDescriptionAttribute("System.Runtime.Serialization.Primitives")]
[assembly:System.Reflection.AssemblyFileVersionAttribute("4.0.0.0")]
[assembly:System.Reflection.AssemblyInformationalVersionAttribute("4.0.0.0")]
[assembly:System.Reflection.AssemblyProductAttribute("Mono Common Language Infrastructure")]
[assembly:System.Reflection.AssemblyTitleAttribute("System.Runtime.Serialization.Primitives")]
[assembly:System.Runtime.CompilerServices.CompilationRelaxationsAttribute(8)]
[assembly:System.Runtime.CompilerServices.RuntimeCompatibilityAttribute(WrapNonExceptionThrows=true)]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.CollectionDataContractAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.ContractNamespaceAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.DataContractAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.DataMemberAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.EnumMemberAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.ExportOptions))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.ExtensionDataObject))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.IExtensibleDataObject))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.InvalidDataContractException))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.KnownTypeAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.OnDeserializedAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.OnDeserializingAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.OnSerializedAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.OnSerializingAttribute))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.SerializationException))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.StreamingContext))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.StreamingContextStates))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.XmlSerializableServices))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.XPathQueryGenerator))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Runtime.Serialization.XsdDataContractExporter))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IFragmentCapableXmlDictionaryWriter))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IStreamProvider))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IXmlBinaryReaderInitializer))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IXmlBinaryWriterInitializer))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IXmlTextReaderInitializer))]
[assembly:System.Runtime.CompilerServices.TypeForwardedToAttribute(typeof(System.Xml.IXmlTextWriterInitializer))]
namespace System.Runtime.Serialization
{
    public partial interface ISerializationSurrogateProvider
    {
        object GetDeserializedObject(object obj, System.Type targetType);
        object GetObjectToSerialize(object obj, System.Type targetType);
        System.Type GetSurrogateType(System.Type type);
    }
}
